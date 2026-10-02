// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201: native utility branches. Central command dispatch performs operator authorization.</summary>
public static class QaUtilityCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "escape", "sticktogether", "clear_vote_cool", "pk_section", "help", "devdebug",
      "i_want_server_language_and_revision", "change_loading_screen_status", "unlock_all_movies", "reset_charsock", "gmevent_notice" };
    public sealed record Data(int[] Movies, int? BaseSlots, IReadOnlyDictionary<int, int> PackageSlots);
    public static readonly SheetValue<Data> Sheet = new("ReplayMovie.xml + AccountTrait.xml", "QA movies and character slots",
        new(Array.Empty<int>(), null, new Dictionary<int, int>()), dir =>
        {
            string movies = Path.Combine(dir, "ReplayMovie.xml"), traits = Path.Combine(dir, "AccountTrait.xml");
            if (!File.Exists(movies) && !File.Exists(traits)) return null;
            int[] ids = File.Exists(movies) ? XDocument.Load(movies).Descendants("Movie").Select(r => (int?)r.Attribute("id") ?? 0).Where(i => i > 0).Distinct().Order().ToArray() : Array.Empty<int>();
            var doc = File.Exists(traits) ? XDocument.Load(traits) : null;
            var defaults = doc?.Descendants("Package").FirstOrDefault(p => (int?)p.Attribute("id") == 0);
            int? slots = (int?)defaults?.Elements("Property").FirstOrDefault(p => string.Equals((string?)p.Attribute("name"), "expandCharacterSlot", StringComparison.OrdinalIgnoreCase))?.Attribute("slot");
            // T228: every package's expandCharacterSlot, not just the defaults'. `slot` is the
            // TOTAL the package confers, not an increment: package 0 carries 3, and 34/102/334/434/
            // 435 carry 8, 436 carries 10, 437 carries 12.
            var per = new Dictionary<int, int>();
            foreach (var pkg in doc?.Descendants("Package") ?? Enumerable.Empty<XElement>())
            {
                int? pid = (int?)pkg.Attribute("id");
                int? v = (int?)pkg.Elements("Property").FirstOrDefault(p => string.Equals((string?)p.Attribute("name"), "expandCharacterSlot", StringComparison.OrdinalIgnoreCase))?.Attribute("slot");
                if (pid.HasValue && v.HasValue && v.Value > 0) per[pid.Value] = v.Value;
            }
            return new(ids, slots, per);
        }, d => d.Movies.Length + (d.BaseSlots.HasValue ? 1 : 0));
    internal static Func<uint> TickCount { get; set; } = () => unchecked((uint)Environment.TickCount);
    public static bool LoadingScreenEnabled(CharacterStore? store) => store?.GetCounterValue("loading_screen_enabled", 0) != 0 && store != null;
    /// <summary>
    /// T228. The account's character slots: an operator-set counter when there is one, else the
    /// larger of the sheet's base and <see cref="CharacterHandlers.MaxCharactersPerAccount"/>,
    /// raised to the largest total any of the account's LIVE benefit packages confers.
    ///
    /// <para>This was a flat <see cref="CharacterHandlers.MaxCharactersPerAccount"/> and the sheet
    /// value the loader already parses was never read. AccountTrait.xml package 0 carries
    /// <c>expandCharacterSlot slot="3"</c> - retail's base - so the sheet alone would SHRINK every
    /// unpackaged account from 8 to 3; the floor keeps that from happening to accounts that already
    /// hold more than three characters.</para>
    /// </summary>
    public static int CharacterSlots(CharacterStore? store, long account)
    {
        long? pinned = store?.TryGetCounterValue("character_slots_" + account);
        if (pinned.HasValue) return checked((int)pinned.Value);
        var sheet = Sheet.Value;
        int slots = Math.Max(sheet.BaseSlots ?? CharacterHandlers.MaxCharactersPerAccount,
                             CharacterHandlers.MaxCharactersPerAccount);
        if (store == null || sheet.PackageSlots.Count == 0) return slots;
        var now = DateTimeOffset.UtcNow;
        foreach (var row in store.GetAccountBenefits(account))
            if (World.AccountBenefitExperiment.IsLive(row, now)
                && sheet.PackageSlots.TryGetValue(row.PackageId, out int granted) && granted > slots)
                slots = granted;
        return slots;
    }

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log,
        int commandType = GmCommandHandlers.CommandTypeAdmin)
    {
        if (!Names.Contains(line.Name)) return false;
        if (!session.InWorld) return true;
        switch (line.Name.ToLowerInvariant())
        {
            case "i_want_server_language_and_revision":
                GmCommandHandlers.SendCustom(session, "server lang and revision [6],[376056]"); break;
            case "clear_vote_cool": Broadcast(0x1528, QaDungeonCommands.IntBytes((int)session.PlayerId)); break;
            case "gmevent_notice": if (line.Args.Count > 0) SendGmEventNotice(session, string.Join(" ", line.Args) + " "); break;
            case "change_loading_screen_status":
                if (store != null && line.Args.Count == 1 && int.TryParse(line.Arg(0), out int status))
                {
                    store.SetCounterValue("loading_screen_enabled", status == 0 ? 0 : 1);
                    foreach (var user in Online(session)) user.Send(new byte[] { 5, 0, 0x88, 0xC5, (byte)(status == 0 ? 0 : 1) });
                }
                break;
            case "unlock_all_movies":
                if (store != null)
                {
                    foreach (int movie in Sheet.Value.Movies) store.AddWatchedMovieForAccount((long)session.Account.AccountId, movie);
                    GmCommandHandlers.SendCustom(session, "Unlock all moives.");
                }
                break;
            case "reset_charsock":
                if (store != null)
                {
                    string name = session.Account.Name;
                    if (store.GetCharacters((long)session.Account.AccountId).Count >= 4)
                        GmCommandHandlers.SendCustom(session, $"Cannot reset character socket of '[{name}]'!, character count must under 4.");
                    else if (Sheet.Value.BaseSlots is int slots)
                    {
                        store.SetCounterValue("character_slots_" + session.Account.AccountId, slots);
                        GmCommandHandlers.SendCustom(session, $"Character socket of '[{name}]' reset!");
                    }
                }
                break;
            case "escape":
                if (store?.GetCharacterByName(line.Arg(0)) is { } escaped)
                    store.SetQaLocation(escaped.Id, 1, 0, BitConverter.Int32BitsToSingle(0x47A92D80),
                        BitConverter.Int32BitsToSingle(unchecked((int)0xC6AEEE00)), BitConverter.Int32BitsToSingle(0x44A20000));
                break;
            case "sticktogether": StickTogether(session, store, line.Arg(0), commandType); break;
            case "pk_section":
                if (store != null && line.Args.Count >= 3 && int.TryParse(line.Arg(0), out int continent) && int.TryParse(line.Arg(2), out int section))
                {
                    bool nonPk = !line.Arg(3).Equals("on", StringComparison.OrdinalIgnoreCase) && line.Arg(3) != "1";
                    var row = new CharacterStore.NonPkSection(continent, line.Arg(1), section);
                    if (row.Area.Length > 60 || row.Area.Contains('\0')) break;
                    store.SetNonPkSection(row, nonPk); Broadcast(0x14E0, NonPkPayload(row, nonPk));
                }
                break;
            case "devdebug":
                if (line.Args.Count == 0) break;
                if (line.Arg(0).Equals("ping", StringComparison.OrdinalIgnoreCase))
                    ArbiterClientHandlers.SendToWorld(session, 0x1389, QaDungeonCommands.IntBytes((int)session.PlayerId, unchecked((int)TickCount())));
                else if (line.Arg(0).Equals("connection", StringComparison.OrdinalIgnoreCase) || line.Arg(0).Equals("set_play_limit", StringComparison.OrdinalIgnoreCase))
                    GmCommandHandlers.SendCustom(session, "devdebug: AuthManager admission counters and waiting queue are not implemented\n");
                else session.Send(ClientString(0x8C6F, string.Join(" ", line.Args)));
                break;
            case "help": ShowHelp(session, line.Arg(0)); break;
        }
        return true;
    }

    public static bool SendGmEventNotice(GameSession session, string text)
    {
        // Arb040:15999 appends a space after each argument; panel text is passed unchanged.
        // Native's regional word filter is separate from packet ownership and is not modeled here.
        text = text.Split('\0')[0]; if (text.Length >= 4096) return false;
        var p = new byte[4 + (text.Length + 1) * 2]; BitConverter.GetBytes(10).CopyTo(p, 0);
        Encoding.Unicode.GetBytes(text).CopyTo(p, 4); return ArbiterClientHandlers.SendToWorld(session, 0x1611, p);
    }
    internal static byte[] ClientString(ushort opcode, string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text); var frame = new byte[8 + bytes.Length];
        BitConverter.GetBytes((ushort)frame.Length).CopyTo(frame, 0); BitConverter.GetBytes(opcode).CopyTo(frame, 2);
        BitConverter.GetBytes((ushort)6).CopyTo(frame, 4); bytes.CopyTo(frame, 6); return frame;
    }
    internal static byte[] NonPkPayload(CharacterStore.NonPkSection row, bool nonPk)
    {
        var text = Encoding.Unicode.GetBytes(row.Area); var p = new byte[15 + text.Length];
        BitConverter.GetBytes(19).CopyTo(p, 0); BitConverter.GetBytes(row.Continent).CopyTo(p, 4);
        BitConverter.GetBytes(row.Section).CopyTo(p, 8); p[12] = (byte)(nonPk ? 1 : 0); text.CopyTo(p, 13); return p;
    }
    public static void ReplayNonPk(CharacterStore? store, WorldLink link)
    {
        // Native14E1 copies std::wstring pointers for long names. World2997143/1764862 only adds rows,
        // so14E0 is its equivalent safe UTF16 setter; the baseline empty14E1 never clears state.
        if (store != null) foreach (var row in store.GetNonPkSections()) link.SendFrame(0x14E0, NonPkPayload(row, true));
    }
    private static IEnumerable<GameSession> Online(GameSession caller)
        => (Program.World?.InWorldSessions() ?? new List<GameSession>()).Append(caller).Distinct().Where(s => s.InWorld);
    private static void Broadcast(ushort op, byte[] payload)
    { if (Program.World is { } world) for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (world.HasLinks(id)) world.SendFrame(id, op, payload); }

    private static void StickTogether(GameSession caller, CharacterStore? store, string name, int type)
    {
        if (store?.GetCharacterByName(name) is not { } target || store.GetCharacter((int)caller.PlayerId) is not { } source) return;
        var blob = source.WorldBlob; uint channel = blob is { Length: >= 244 } ? BitConverter.ToUInt32(blob, 240) : 0;
        short direction = blob is { Length: >= 308 } ? unchecked((short)BitConverter.ToInt32(blob, 304)) : (short)0;
        var online = Program.World?.SessionForPlayerId(target.Id);
        if (online?.InWorld == true)
        {
            if (type == GmCommandHandlers.CommandTypeOp)
            {
                var p = new byte[24]; BitConverter.GetBytes(target.Id).CopyTo(p, 0); BitConverter.GetBytes(source.Zone).CopyTo(p, 4);
                BitConverter.GetBytes(channel).CopyTo(p, 8); BitConverter.GetBytes(source.X).CopyTo(p, 12);
                BitConverter.GetBytes(source.Y).CopyTo(p, 16); BitConverter.GetBytes(source.Z).CopyTo(p, 20);
                ArbiterClientHandlers.SendToWorld(online, 0x13B1, p);
            }
            else Program.World?.TryQaTeleport(online, source.Zone, channel, source.X, source.Y, source.Z, direction);
        }
        else store.SetQaLocation(target.Id, source.Zone, channel, source.X, source.Y, source.Z);
        if (type == GmCommandHandlers.CommandTypeOp) GmCommandHandlers.SendCustom(caller, $"Summoned [{target.Name}]");
    }
    /// <summary>How many WorldServer rows one /@help answer will list before it summarises.</summary>
    public const int WorldHelpRowLimit = 40;

    /// <summary>
    /// /@help - the Arbiter's own catalogue, then WorldServer's.
    ///
    /// <para>T233: this used to forward <c>_helpworld &lt;term&gt;</c> to World for the second half.
    /// <c>_helpworld</c> is not one of the 545 names WorldServer's CommandDistributor registers, so
    /// that forward could only ever come back as "Invalid QA Command" - it answered nothing. World's
    /// table is extracted instead (<see cref="WorldQaCommandData"/>) and searched here, which also
    /// means /@help works with World down.</para>
    ///
    /// <para>The World rows carry a [world] tag because they do not run here, and the list is capped:
    /// an empty keyword matches all 545, and one chat line each would flood the client.</para>
    /// </summary>
    private static void ShowHelp(GameSession session, string keyword)
    {
        GmCommandHandlers.SendCustom(session, "*****Help search Result***** &#xa;");
        foreach (var row in QaCommandHelpData.Rows.Where(r => !r.Name.StartsWith('_') &&
            (r.Name.Contains(keyword, StringComparison.Ordinal) || r.Arguments.Contains(keyword, StringComparison.Ordinal) || r.Description.Contains(keyword, StringComparison.Ordinal))))
            GmCommandHandlers.SendCustom(session, $"{row.Name} {row.Arguments} // {row.Description} &#xa;");

        var world = WorldQaCommandData.Search(keyword ?? string.Empty).ToList();
        foreach (var row in world.Take(WorldHelpRowLimit))
            GmCommandHandlers.SendCustom(session, $"{row.Name} {row.Arguments} // {row.Description} [world] &#xa;");
        if (world.Count > WorldHelpRowLimit)
            GmCommandHandlers.SendCustom(session,
                $"... and {world.Count - WorldHelpRowLimit} more WorldServer command(s) - narrow the search &#xa;");
    }
}
