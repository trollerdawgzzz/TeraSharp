// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Text.RegularExpressions;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Web;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T206 - the single-page admin UI and the endpoints it needed.
//
// The most valuable test here is not any one endpoint: it is
// T206_every_endpoint_the_UI_calls_is_a_route_the_server_answers, which reads the shipped
// JavaScript, pulls every /api/ path out of it and asks the real router about each one. A
// mistyped path in a view module is otherwise a 404 nobody sees until an operator clicks the
// button, and no amount of per-endpoint testing catches it.
// =============================================================================================
public static partial class Tests
{
    /// <summary>The seven screens, as app.js registers them.</summary>
    static readonly string[] T206Screens =
        { "dashboard", "accounts", "characters", "mail", "guilds", "server", "settings" };

    /// <summary>
    /// T206.1 - the UI ships inside the assembly and is served as files, not as one string.
    /// The shell, the stylesheet, the core and one module per screen all have to be there: a
    /// missing module is a blank screen with a console error nobody is watching.
    /// </summary>
    [Test] public static void T206_the_embedded_UI_ships_every_file_the_shell_imports()
    {
        Hex.True(AdminAssets.Available, "the admin UI is embedded in TeraSharp.Arbiter.dll");
        foreach (string name in new[] { "index.html", "app.css", "app.js" })
            Hex.True(AdminAssets.Find(name) != null, name + " is embedded");
        foreach (string screen in T206Screens)
            Hex.True(AdminAssets.Find("view-" + screen + ".js") != null, "view-" + screen + ".js is embedded");

        // Whatever app.js imports has to exist, so adding a screen cannot half-land.
        string app = AdminAssets.Find("app.js")!.Body;
        foreach (Match m in Regex.Matches(app, @"from '\./([A-Za-z0-9.\-]+)'"))
            Hex.True(AdminAssets.Find(m.Groups[1].Value) != null,
                "app.js imports " + m.Groups[1].Value + " and it is embedded");
        foreach (string screen in T206Screens)
            Hex.True(app.Contains("'" + screen + "'"), screen + " is registered in the nav");

        // The content type has to come off the extension, not off a guess.
        Hex.True(AdminAssets.Find("index.html")!.ContentType.StartsWith("text/html")
                 && AdminAssets.Find("app.js")!.ContentType.StartsWith("text/javascript")
                 && AdminAssets.Find("app.css")!.ContentType.StartsWith("text/css"),
            "each asset carries its own content type");

        // T106's page lived in a verbatim string and could hold no double quote. These are real
        // files, so the rule does not apply - and index.html does use double-quoted attributes,
        // which is the point of moving them out.
        Hex.True(AdminAssets.Find("index.html")!.Body.Contains("charset=\"utf-8\""),
            "the shell is a real file, so ordinary HTML quoting is available again");
    }

    /// <summary>
    /// T206.2 - the assets are reachable without a token and the API is not. The page has to be
    /// served token-free (a browser typing the address has nowhere to put a header), so the test
    /// that matters is that nothing behind it leaks the same way.
    /// </summary>
    [Test] public static void T206_assets_need_no_token_and_no_path_escapes_the_resource_list()
    {
        using var store = StoreWithTwoAccounts();
        var api = NewAdminApi(store);

        foreach (string path in new[] { "/", "/index.html", "/app.js", "/app.css", "/view-mail.js" })
        {
            var r = api.Handle("GET", path);
            Hex.True(r.Status == 200 && r.Body.Length > 0, path + " is served without a token");
            Hex.True(r.CacheControl == "no-store" && r.ETag != null && r.ETag.StartsWith("\""),
                path + " carries no-store and an ETag");
        }

        // Every API route still needs one.
        foreach (string path in new[] { "/api/db", "/api/queue", "/api/settings", "/api/guilds" })
            Hex.True(api.Handle("GET", path).Status == 401, path + " still needs a token");

        // Nothing walks out of the flat resource list, and /api/ never falls through to an asset.
        foreach (string path in new[] { "/../app.js", "/views/x.js", "/nope.js", "/app.js/x" })
            Hex.True(api.Handle("GET", path).Status is 401 or 404,
                path + " is not served as an asset");
        Hex.True(api.Handle("GET", "/api/app.js").Status == 401,
            "a path under /api/ is never answered from the resource list");

        // The whole tool off is still 503, page included.
        Hex.True(NewAdminApi(store, token: null).Handle("GET", "/").Status == 503,
            "an unset TERASHARP_ADMIN_TOKEN serves nothing at all, not even the shell");
    }

    /// <summary>
    /// T206.3 - every <c>/api/</c> path in the shipped JavaScript is a route the router answers.
    /// A path the server does not know answers 404; this asks for each one with a good token and
    /// insists on anything but that.
    /// </summary>
    [Test] public static void T206_every_endpoint_the_UI_calls_is_a_route_the_server_answers()
    {
        using var store = StoreWithTwoAccounts();
        var api = NewAdminApi(store);

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var asset in AdminAssets.All.Values)
        {
            if (!asset.Path.EndsWith(".js", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(asset.Body, @"'(/api/[a-z0-9\-]+)'"))
                paths.Add(m.Groups[1].Value);
        }
        Hex.True(paths.Count >= 40, "the UI calls " + paths.Count + " endpoints (expected 40+)");

        // A known route can legitimately answer 404 - "no such character" - so the test is not
        // "did it 404" but "was it the ROUTER's 404". Only the fall-through at the bottom of
        // Handle says "no such endpoint", and that is the one answer a mistyped path produces.
        static bool Known(AdminResponse r)
            => !(r.Status == 404 && r.Body.Contains("no such endpoint"));

        foreach (string path in paths)
        {
            var get = api.Handle("GET", path, token: T101Token);
            var post = api.Handle("POST", path, body: "{}", token: T101Token);
            Hex.True(Known(get) || Known(post), path + " is a route the server knows");
            Hex.True(get.Status != 401 && post.Status != 401, path + " accepted the token");
            Hex.True(get.Status != 501 && post.Status != 501,
                path + " is implemented, not a phase-3 placeholder the UI links to");
        }
    }

    /// <summary>
    /// T206.4 - the Dashboard and Settings reads. The database report has to come off the page
    /// geometry rather than a FileInfo, and the settings dump must never carry a secret.
    /// </summary>
    [Test] public static void T206_dashboard_and_settings_reads_report_real_state_with_secrets_masked()
    {
        using var store = StoreWithTwoAccounts();
        var api = NewAdminApi(store);

        var db = api.Handle("GET", "/api/db", token: T101Token);
        Hex.True(db.Status == 200 && db.Body.Contains("\"tables\":[") && db.Body.Contains("\"name\":\"characters\""),
            "the database report lists the real tables");
        var stats = store.GetDatabaseStats();
        Hex.True(stats.Bytes == stats.PageCount * stats.PageSize && stats.PageSize > 0,
            "the size is page_count times page_size, which is right while a WAL is open");
        Hex.True(stats.Tables.Count > 40, stats.Tables.Count + " tables counted");
        foreach (var t in stats.Tables)
            if (t.Name == "accounts") Hex.True(t.Rows == 2, "the accounts row count is the real one");

        var queue = api.Handle("GET", "/api/queue", token: T101Token);
        Hex.True(queue.Status == 200 && queue.Body.Contains("\"queues\":[") && queue.Body.Contains("\"parties\":"),
            "the match pool reads even when it is empty");

        var settings = api.Handle("GET", "/api/settings", token: T101Token);
        Hex.True(settings.Status == 200 && settings.Body.Contains("\"values\":["),
            "the settings dump answers");
        Hex.True(settings.Body.Contains("TERASHARP_ADMIN_TOKEN") && !settings.Body.Contains(T101Token),
            "the token is named but its value is never in the body");
        Hex.True(AdminApi.IsSecret("TERASHARP_ADMIN_TOKEN") && AdminApi.IsSecret("TERASHARP_API_JWT_SECRET")
                 && !AdminApi.IsSecret("TERASHARP_ADMIN_PORT"),
            "the secret test catches tokens and secrets by name and leaves ports alone");
    }

    /// <summary>
    /// T206.5 - the per-character writes the retail right-hand menu had and this tool did not:
    /// a place to stand, an item taken away, skills cleared, EP set.
    /// </summary>
    [Test] public static void T206_the_new_character_writes_change_the_row_they_claim_to()
    {
        using var store = StoreWithTwoCharacters();
        var api = NewAdminApi(store);

        var moved = api.Handle("POST", "/api/set-position",
            body: "{\"id\":1,\"zone\":9781,\"x\":11.5,\"y\":22.5,\"z\":33.5,\"reason\":\"stuck\"}",
            token: T101Token);
        Hex.True(moved.Status == 200, "set-position answers");
        var after = store.GetCharacter(1)!;
        Hex.True(after.Zone == 9781 && Math.Abs(after.X - 11.5f) < 0.01f
                 && Math.Abs(after.Z - 33.5f) < 0.01f, "the character is where the call put it");

        int itemId = store.NextItemId();
        store.UpsertItem(itemId, 1, 0, 0, 88888, 3);
        var removed = api.Handle("POST", "/api/remove-item",
            body: "{\"itemDbId\":" + itemId + ",\"reason\":\"duped\"}", token: T101Token);
        Hex.True(removed.Status == 200 && store.GetItem(itemId) == null, "remove-item deletes the row");
        Hex.True(api.Handle("POST", "/api/remove-item", body: "{\"itemDbId\":" + itemId + "}",
            token: T101Token).Status == 404, "and says so when it is already gone");

        Hex.True(api.Handle("POST", "/api/reset-skills", body: "{\"id\":1}", token: T101Token).Status == 200,
            "reset-skills answers");

        Hex.True(api.Handle("POST", "/api/set-ep", body: "{\"id\":1,\"level\":7,\"point\":42}",
            token: T101Token).Status == 200, "set-ep answers");
        var ep = store.GetCharacterEp(1);
        Hex.True(ep != null && ep.EpLevel == 7 && ep.EpPoint == 42, "and the EP row carries both numbers");
        Hex.True(api.Handle("POST", "/api/set-ep", body: "{\"id\":1,\"level\":-1,\"point\":0}",
            token: T101Token).Status == 400, "a negative level is refused, not clamped");

        // The item picker the Characters screen and the mail form both use.
        var found = api.Handle("GET", "/api/item-search",
            new Dictionary<string, string> { ["q"] = "nothing-matches-this" }, token: T101Token);
        Hex.True(found.Status == 200 && found.Body.Contains("\"items\":[]"),
            "the item search answers with an empty list rather than failing when no sheet is loaded");
    }

    /// <summary>
    /// T206.6 - mail. One parcel to one character, the attachment list parsed from the flat
    /// string the body scanner can actually read, and a broadcast that counts its receivers.
    /// </summary>
    [Test] public static void T206_system_mail_reaches_one_character_and_every_character()
    {
        using var store = StoreWithTwoCharacters();
        var api = NewAdminApi(store);

        var one = api.Handle("POST", "/api/send-mail",
            body: "{\"id\":1,\"title\":\"make good\",\"message\":\"sorry\",\"money\":500,"
                + "\"items\":\"88888:2,99999\",\"reason\":\"ticket 12\"}", token: T101Token);
        Hex.True(one.Status == 200 && one.Body.Contains("\"sent\":1") && one.Body.Contains("\"receivers\":1"),
            "one parcel to one character");
        var inbox = store.GetParcelsFor(1);
        Hex.True(inbox.Count == 1 && inbox[0].Money == 500 && inbox[0].Title == "make good",
            "the parcel is in the inbox with its money");
        Hex.True(inbox[0].ParcelType == AdminApi.SystemParcelType && inbox[0].SenderName == AdminApi.SystemSender,
            "it is a system parcel from GM, so it has no sent-box entry and no reply");
        var attachments = store.GetParcelItems(inbox[0].ParcelId);
        Hex.True(attachments.Count == 2 && attachments[0].TemplateId == 88888 && attachments[0].Amount == 2
                 && attachments[1].TemplateId == 99999 && attachments[1].Amount == 1,
            "both attachments landed, and a bare template means one of it");
        Hex.True(attachments[0].ItemDbId != attachments[1].ItemDbId
                 && attachments[0].ItemDbId > 0, "each attachment got its own item id");

        // The flat attachment format is parsed strictly: a bad pair is a 400, never a silent drop.
        Hex.True(AdminApi.TryParseAttachments("1:2,3:4", out var good, out _) && good.Count == 2,
            "template:amount pairs parse");
        Hex.True(AdminApi.TryParseAttachments("", out var none, out _) && none.Count == 0,
            "no attachments is valid");
        Hex.True(!AdminApi.TryParseAttachments("abc:1", out _, out string why1) && why1.Length > 0,
            "a non-numeric template is refused with a reason");
        Hex.True(!AdminApi.TryParseAttachments("1:0", out _, out _), "a zero amount is refused");
        Hex.True(!AdminApi.TryParseAttachments("1,2,3,4,5,6", out _, out string why2)
                 && why2.Contains("at most"), "more than the protocol's slots is refused");
        Hex.True(api.Handle("POST", "/api/send-mail", body: "{\"id\":1,\"title\":\"x\",\"items\":\"abc\"}",
            token: T101Token).Status == 400, "and the endpoint returns 400 for it");
        Hex.True(api.Handle("POST", "/api/send-mail", body: "{\"id\":1,\"message\":\"no title\"}",
            token: T101Token).Status == 400, "a missing title is refused");

        var all = api.Handle("POST", "/api/send-mail",
            body: "{\"all\":\"everyone\",\"title\":\"maintenance\",\"reason\":\"downtime\"}", token: T101Token);
        Hex.True(all.Status == 200 && all.Body.Contains("\"sent\":2") && all.Body.Contains("\"receivers\":2"),
            "everyone means every character, and it says how many");
        Hex.True(store.GetParcelsFor(2).Count == 1, "the second character got theirs");

        // Reading them back, with the attachments spelled out.
        var view = api.Handle("GET", "/api/parcels",
            new Dictionary<string, string> { ["id"] = "1" }, token: T101Token);
        Hex.True(view.Status == 200 && view.Body.Contains("\"inbox\":[") && view.Body.Contains("\"templateId\":88888"),
            "the parcel view lists the attachment, not just a count");

        var dropped = api.Handle("POST", "/api/delete-parcel",
            body: "{\"parcelId\":" + inbox[0].ParcelId + ",\"reason\":\"wrong item\"}", token: T101Token);
        Hex.True(dropped.Status == 200 && store.GetParcel(inbox[0].ParcelId) == null
                 && store.GetParcelItems(inbox[0].ParcelId).Count == 0,
            "deleting a parcel takes its attachments with it");
    }

    /// <summary>
    /// T206.7 - guilds. The list, one guild with its roster, and the three writes retail had
    /// (money, level, disband) which the store could only do as deltas before this task.
    /// </summary>
    [Test] public static void T206_guild_screens_list_read_and_edit_a_real_guild()
    {
        using var store = StoreWithTwoCharacters();
        var api = NewAdminApi(store);

        int guildId = store.CreateGuild("Testers", chiefDbId: 1, warAcceptable: true);
        Hex.True(guildId > 0, "a guild to look at");
        store.AddGuildMember(guildId, 1, "t30_1", 4, 12, 1, 11, 1);
        store.AddGuildMember(guildId, 2, "t30_2", 4, 12, 1, 12, 2);

        var list = api.Handle("GET", "/api/guilds",
            new Dictionary<string, string> { ["q"] = "test" }, token: T101Token);
        Hex.True(list.Status == 200 && list.Body.Contains("\"name\":\"Testers\"") && list.Body.Contains("\"members\":2"),
            "the substring search finds it and counts its members");
        Hex.True(api.Handle("GET", "/api/guilds",
            new Dictionary<string, string> { ["q"] = "nothing" }, token: T101Token)
            .Body.Contains("\"shown\":0"), "and a term that matches nothing shows nothing");

        var one = api.Handle("GET", "/api/guild",
            new Dictionary<string, string> { ["name"] = "Testers" }, token: T101Token);
        Hex.True(one.Status == 200 && one.Body.Contains("\"members\":[") && one.Body.Contains("\"t30_2\"")
                 && one.Body.Contains("\"isChief\":true") && one.Body.Contains("\"wars\":["),
            "one guild carries its roster, its chief flag and its war list");
        Hex.True(api.Handle("GET", "/api/guild",
            new Dictionary<string, string> { ["name"] = "nope" }, token: T101Token).Status == 404,
            "and a guild that is not there is a 404");

        var money = api.Handle("POST", "/api/guild-money",
            body: "{\"id\":" + guildId + ",\"money\":12345,\"reason\":\"restore\"}", token: T101Token);
        Hex.True(money.Status == 200 && money.Body.Contains("\"newMoney\":12345")
                 && store.GetGuild(guildId)!.Money == 12345, "money is set, not added");
        Hex.True(api.Handle("POST", "/api/guild-money", body: "{\"id\":" + guildId + ",\"money\":-1}",
            token: T101Token).Status == 400, "negative money is refused");

        Hex.True(api.Handle("POST", "/api/guild-level",
            body: "{\"id\":" + guildId + ",\"level\":5}", token: T101Token).Status == 200
                 && store.GetGuild(guildId)!.Level == 5, "level is set outright");
        Hex.True(api.Handle("POST", "/api/guild-level",
            body: "{\"id\":" + guildId + ",\"level\":" + (AdminApi.MaxGuildLevel + 1) + "}",
            token: T101Token).Status == 400, "past the ceiling is refused");

        var gone = api.Handle("POST", "/api/guild-disband",
            body: "{\"id\":" + guildId + ",\"reason\":\"empty\"}", token: T101Token);
        Hex.True(gone.Status == 200 && store.GetGuild(guildId) == null, "disband removes the guild");
    }

    /// <summary>
    /// T206.8 - the Server screen's two additions, and the audit trail every write now leaves in
    /// BOTH logs. Before this task an operator's edits were only in admin_log, so searching a
    /// player's own history showed nothing an operator had done to them.
    /// </summary>
    [Test] public static void T206_writes_land_in_game_log_and_the_server_screen_additions_answer()
    {
        using var store = StoreWithTwoCharacters();
        var api = NewAdminApi(store);

        Hex.True(store.CountGameLog(category: AdminApi.AdminLogCategory) == 0, "no admin rows yet");
        var set = api.Handle("POST", "/api/set-money",
            body: "{\"id\":1,\"money\":777,\"reason\":\"ticket 99\"}", token: T101Token);
        Hex.True(set.Status == 200, "a write to audit");

        var rows = store.QueryGameLog(characterId: 1, category: AdminApi.AdminLogCategory);
        Hex.True(rows.Count == 0, "set-money carries no character id yet, so it is account-agnostic");
        var all = store.QueryGameLog(category: AdminApi.AdminLogCategory);
        Hex.True(all.Count == 1 && all[0].Action == "set-money" && all[0].Extra.Contains("ticket 99"),
            "the write is in game_log under the admin category with its reason");
        Hex.True(store.GetAdminLog(10).Count == 1, "and still in admin_log, which the tool tails");

        // A write that knows whose character it is keys the row on them, so the player's own
        // history search finds it.
        api.Handle("POST", "/api/set-position", body: "{\"id\":1,\"zone\":5,\"reason\":\"unstick\"}",
            token: T101Token);
        var mine = store.QueryGameLog(characterId: 1, category: AdminApi.AdminLogCategory);
        Hex.True(mine.Count == 1 && mine[0].Action == "set-position",
            "set-position is findable from the character it moved");

        // Restart notices: one now, and one per ladder step inside the window.
        int announced = 0;
        api.Announce = _ => { announced++; return 3; };
        var notice = api.Handle("POST", "/api/restart-notice",
            body: "{\"minutes\":20,\"text\":\"down in {0}\",\"reason\":\"patch\"}", token: T101Token);
        Hex.True(notice.Status == 200 && announced == 1 && notice.Body.Contains("\"sent\":3"),
            "the first notice goes out immediately");
        int expected = 0;
        foreach (int step in AdminApi.RestartNoticeMinutes) if (step < 20) expected++;
        Hex.True(store.GetAnnounces(100).Count == expected,
            expected + " later notices are scheduled, one per ladder step inside the window");
        Hex.True(api.Handle("POST", "/api/restart-notice", body: "{\"minutes\":0}", token: T101Token)
            .Status == 400, "zero minutes is refused");
        Hex.True(api.Handle("POST", "/api/restart-notice",
            body: "{\"minutes\":" + (AdminApi.MaxRestartMinutes + 1) + "}", token: T101Token)
            .Status == 400, "and so is a countdown longer than the ceiling");

        var reload = api.Handle("POST", "/api/reload-datasheets", body: "{\"reason\":\"edited\"}",
            token: T101Token);
        Hex.True(reload.Status == 200 && reload.Body.Contains("\"sheets\":["),
            "the datasheet reload reports every sheet, whether or not a file was found");

        // The login history the Accounts screen shows: the stamps plus the login rows.
        store.AddGameLog(World.GameLogPackets.CategoryUser, AdminApi.LoginAction, 1, 1, 0, 0, 0, 0, 0, "world 1");
        var logins = api.Handle("GET", "/api/account-logins",
            new Dictionary<string, string> { ["id"] = "1" }, token: T101Token);
        Hex.True(logins.Status == 200 && logins.Body.Contains("\"logins\":[") && logins.Body.Contains("world 1")
                 && logins.Body.Contains("\"characters\":["),
            "the login history carries both the stamped pairs and the logged events");
        Hex.True(api.Handle("GET", "/api/account-logins",
            new Dictionary<string, string> { ["name"] = "nobody" }, token: T101Token).Status == 404,
            "and an account that is not there is a 404");
    }

    /// <summary>
    /// T206.9 - achievements, read back from the 24-byte records World stores. T203 established
    /// that the id is the first word and the server-unique flag the second; this decodes those
    /// two and the date and leaves the rest of the record alone.
    /// </summary>
    [Test] public static void T206_the_achievement_view_decodes_the_records_world_stored()
    {
        using var store = StoreWithTwoCharacters();
        var api = NewAdminApi(store);

        byte[] Record(int id, int unique)
        {
            var r = new byte[24];
            BitConverter.GetBytes(id).CopyTo(r, 0);
            BitConverter.GetBytes(unique).CopyTo(r, 4);
            BitConverter.GetBytes((ushort)2026).CopyTo(r, 8);
            BitConverter.GetBytes((ushort)9).CopyTo(r, 10);
            BitConverter.GetBytes((ushort)27).CopyTo(r, 12);
            BitConverter.GetBytes((ushort)13).CopyTo(r, 14);
            BitConverter.GetBytes((ushort)5).CopyTo(r, 16);
            BitConverter.GetBytes((ushort)41).CopyTo(r, 18);
            return r;
        }
        store.AddAccomplishedAchievements(1, new[] { (100, Record(100, 0)), (200, Record(200, 1)) });
        Hex.True(store.TryClaimServerAchievement(200, 1), "and character 1 holds that server first");

        var view = api.Handle("GET", "/api/achievements",
            new Dictionary<string, string> { ["id"] = "1" }, token: T101Token);
        Hex.True(view.Status == 200 && view.Body.Contains("\"count\":2"), "both records are counted");
        Hex.True(view.Body.Contains("\"id\":100") && view.Body.Contains("\"serverUnique\":1"),
            "the id and the server-unique flag are read off the record");
        Hex.True(view.Body.Contains("2026-09-27 13:05:41"), "and the six date words decode");
        Hex.True(view.Body.Contains("\"serverFirsts\":[{\"id\":200"),
            "the claim table shows the server first this character took");
        Hex.True(api.Handle("GET", "/api/achievements",
            new Dictionary<string, string> { ["id"] = "2" }, token: T101Token)
            .Body.Contains("\"count\":0"), "a character with none reports none");
    }
    // ========================================================================= T206b

    /// <summary>
    /// T206b - the UI refused a token the API accepted. Nothing was wrong with the token, the
    /// header or any endpoint: <c>app.js</c> armed the header poll at page load, so while nobody
    /// was signed in it called <c>/api/status</c> with an empty token, earned a real 401, and
    /// treated it as "the token was refused". On an idle login page that wrote the error onto a
    /// form nobody had submitted; and when a poll was in flight at the moment the form WAS
    /// submitted, its 401 landed after the sign-in had succeeded and threw the operator straight
    /// back out. This replays the same request sequence against a real listener, and then pins
    /// the three guards in the shipped module so the defect cannot come back.
    /// </summary>
    [Test] public static void T206b_the_token_the_api_accepts_signs_in_through_the_UI_wrapper()
    {
        const string token = "t206b-live-token";
        string? oldToken = Environment.GetEnvironmentVariable(AdminServer.TokenVariable);
        string? oldPort = Environment.GetEnvironmentVariable(AdminServer.PortVariable);
        int port = T206bFreePort();
        Environment.SetEnvironmentVariable(AdminServer.TokenVariable, token);
        Environment.SetEnvironmentVariable(AdminServer.PortVariable, port.ToString(CultureInfo.InvariantCulture));
        TerasConfig.ResetForTests();
        try
        {
            using var store = StoreWithTwoCharacters();
            var online = new List<AdminOnlineRow> { new(1, "t30_1", 11, 5, "acct1") };
            using var server = AdminServer.TryStart(store, () => online, QuietLog());
            if (server == null)
            {
                // HttpListener needs a URL ACL on Windows unless the process is elevated. That is
                // a property of the machine, not of the code under test, so skip rather than fail.
                Skip.Because("HttpListener would not bind 127.0.0.1:" + port);
                return;
            }
            server.Api.WorldStatus = () => (2, true, online.Count);

            string root = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // The shell is served with no token at all - the browser has nowhere to put a header.
            var shell = http.GetAsync(root + "/").GetAwaiter().GetResult();
            Hex.True((int)shell.StatusCode == 200, "the sign-in page is served without a token");

            // What the poll used to do before anyone signed in: an EMPTY token, which is a real
            // 401. The fix is that app.js never makes this call; the server is right to refuse it.
            Hex.True((int)T206bGet(http, root + "/api/status", string.Empty).StatusCode == 401,
                "an empty X-Admin-Token is refused, which is why the UI must not send one");

            // And the token the API accepts - sent exactly as app.js sends it - on every endpoint
            // the first screen after sign-in touches.
            foreach (string path in new[] { "/api/status", "/api/online", "/api/db", "/api/queue" })
            {
                var reply = T206bGet(http, root + path, token);
                Hex.True((int)reply.StatusCode == 200,
                    path + " answers 200 for the header app.js sends, not 401");
            }

            // Header name and case: HTTP headers are case-insensitive, and the tool must not
            // depend on the exact spelling surviving a proxy or a browser's normalisation.
            foreach (string spelling in new[] { "X-Admin-Token", "x-admin-token", "X-ADMIN-TOKEN" })
                Hex.True((int)T206bGet(http, root + "/api/status", token, spelling).StatusCode == 200,
                    spelling + " is accepted - header names are case-insensitive");

            // A pasted value with stray whitespace was the other suspect, and it is NOT the
            // cause: optional whitespace around a header value is not part of the field value, so
            // the stack strips it and an untrimmed paste reaches the server clean. app.js trims
            // anyway - what it keeps in sessionStorage should be the secret and nothing else - but
            // a trailing space was never what refused a good token.
            Hex.True((int)T206bGet(http, root + "/api/status", " " + token + " ").StatusCode == 200,
                "surrounding whitespace is stripped by HTTP, so it was never the cause");

            // The compare itself is exact, so a genuinely different secret is still refused.
            Hex.True((int)T206bGet(http, root + "/api/status", token.ToUpperInvariant()).StatusCode == 401,
                "a token that differs in case is a different token");
            Hex.True((int)T206bGet(http, root + "/api/status", token + "x").StatusCode == 401,
                "and so is one with a character added");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdminServer.TokenVariable, oldToken);
            Environment.SetEnvironmentVariable(AdminServer.PortVariable, oldPort);
            TerasConfig.ResetForTests();
        }
    }

    /// <summary>
    /// T206b - the three guards that keep a 401 from eating a good sign-in, asserted against the
    /// module that actually ships. These are source assertions on purpose: the failure was a
    /// lifecycle mistake in <c>app.js</c>, and no amount of endpoint testing catches it.
    /// </summary>
    [Test] public static void T206b_the_shipped_app_never_calls_the_api_without_a_token()
    {
        string app = AdminAssets.Find("app.js")?.Body ?? string.Empty;
        Hex.True(app.Length > 0, "app.js ships in the assembly");

        // 1. No poll is armed at load. startHealth/stopHealth own the timer, and they run on
        //    sign-in and sign-out, so nothing calls the API until a token exists.
        Hex.True(app.Contains("function startHealth()") && app.Contains("function stopHealth()")
                 && app.Contains("clearInterval(healthTimer)"),
            "the header poll is started on sign-in and cleared on sign-out");
        int start = app.IndexOf("function start()", StringComparison.Ordinal);
        Hex.True(start > 0 && !app[start..].Contains("setInterval"),
            "start() arms no interval - that is what polled with an empty token");

        // 2. api() refuses to reach the network without a token, so an empty token can never
        //    produce the 401 that used to be read as a refusal.
        Hex.True(app.Contains("if (!token) return { status: 401"),
            "api() answers locally when nobody is signed in");
        Hex.True(!app.Contains("token || ''"),
            "and no longer sends an empty string as the token");

        // 3. A 401 only ends the session that made the call. Every sign-in and sign-out bumps the
        //    generation, so a reply from a superseded session is discarded.
        Hex.True(app.Contains("const issued = generation")
                 && app.Contains("issued === generation")
                 && app.Contains("token !== null"),
            "the 401 handler is guarded by the session generation");
        Hex.True(app.Contains("if (!token) return;"), "health() is silent while signed out");

        // Signing in clears any error the previous attempt left, so a stale reason cannot sit on
        // a form that has just succeeded.
        int signIn = app.IndexOf("function signIn(", StringComparison.Ordinal);
        int signOut = app.IndexOf("export function signOut(", StringComparison.Ordinal);
        Hex.True(signIn > 0 && signOut > signIn
                 && app[signIn..signOut].Contains("error.hidden = true"),
            "signIn clears the login error");
    }

    /// <summary>A port nothing is listening on, so two runs of the suite cannot collide.</summary>
    static int T206bFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>One GET with the token in a header, the way app.js sends it.</summary>
    static System.Net.Http.HttpResponseMessage T206bGet(System.Net.Http.HttpClient http,
        string url, string token, string header = AdminServer.TokenHeader)
    {
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(header, token);
        return http.SendAsync(request).GetAwaiter().GetResult();
    }
}
