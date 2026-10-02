// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Web;

namespace TeraSharp.Arbiter.Tests;

// ===================== T224: the in-game delete has to actually stick =====================
//
// Reported: deleting from the lobby answers S_DELETE_USER ok, and the character is back in the
// next S_GET_USER_LIST - with ServerConfig.xml carrying <DeleteUser expireHour2="0">, i.e.
// "delete it now", and TeraSharp restarted in between. Three separate causes:
//
//   1. OnDeleteUser took the window from the hardcoded CharacterStore.DeleteExpireHours (72) and
//      never read ServerConfig.xml, so expireHour2="0" still parked the row for three days.
//   2. CharacterStore.GetCharacters had no delete_at predicate, so the parked row was listed
//      again on the next login. That is the "reappears" symptom.
//   3. CharacterStore.PurgeExpiredDeletes - the only thing that ever removed a parked row - was
//      called from nothing but a test, and it computes its cutoff from one global hour count, so
//      it could not honour a per-character window anyway.
//
// RETAIL, PINNED. cap_final_client2 frames 11 and 35 bracket a successful
// C_CANCEL_DELETE_USER (frame 32 -> S_CANCEL_DELETE_USER 01 at frame 33). Both S_GET_USER_LIST
// frames are 1183 bytes and differ in exactly six bytes, so a pending-delete character is NOT
// hidden from the list. Element 0 reads:
//
//                     frame 11 (pending)      frame 35 (cancelled)
//   isDeleting        1                       0                      <- the only semantic change
//   deleteTime        1789879815              1789879815             <- stamp survives the cancel
//   deleteRemainSec   259182                  259150                 <- deleteTime - now, per send
//   banRemainSec      -1789620633             -1789620665            <- same 32 s of wall clock
//
//   fixed part: deletionSectionClassifyLevel 5, deleteCharacterExpireHour1 0,
//               deleteCharacterExpireHour2 72
//
// 259200 s is 72 h to the second, and the fixed part is this deployment's own
// <DeleteUser expireHour1="0" expireHour2="72" deletionSectionClassifyLevel="5" /> - so retail
// sends the file's numbers and derives the countdown from an absolute stamp.
//
// Decompile: Handler_C_DELETE_USER Arb_part_079.c:9852 - guard packet >= 8, u32 at frame offset
// 4, refuse against the account's own list, one call whose bool goes into S_DELETE_USER (0xB80B)
// and then a "DELETE_USER" audit row. No hour argument anywhere, which is why the window can
// only come from ServerConfig.xml.
// =========================================================================================

public static partial class Tests
{
    /// <summary>The shipped element, verbatim from D:\v100\TERA_SERVER.100\Executable\ServerConfig.xml.</summary>
    const string T224ServerConfig =
        "<ServerConfig><Server>"
        + "<DeleteUser expireHour1=\"0\" expireHour2=\"72\" deletionSectionClassifyLevel=\"5\" />"
        + "</Server></ServerConfig>";

    /// <summary>
    /// T224 - the policy is ServerConfig.xml's, and a missing or broken file is the default
    /// rather than an error (the same contract WorldServerList.Parse has).
    /// </summary>
    [Test] public static void T224_the_delete_policy_comes_from_ServerConfig_xml()
    {
        var p = CharacterDeletion.Parse(T224ServerConfig);
        Hex.True(p.ExpireHour1 == 0 && p.ExpireHour2 == 72 && p.ClassifyLevel == 5,
            $"the shipped element: {p.ExpireHour1}/{p.ExpireHour2}/{p.ClassifyLevel}");
        Hex.True(p == CharacterDeletion.Policy.Default,
            "which is also the default, so this deployment is unaffected by reading the file");

        var zero = CharacterDeletion.Parse(
            "<ServerConfig><DeleteUser expireHour1=\"0\" expireHour2=\"0\" "
            + "deletionSectionClassifyLevel=\"5\" /></ServerConfig>");
        Hex.True(zero.ExpireHour2 == 0, "the T224 repro's own setting is read as 0");

        Hex.True(CharacterDeletion.Parse(null) == CharacterDeletion.Policy.Default, "null -> default");
        Hex.True(CharacterDeletion.Parse("<ServerConfig>") == CharacterDeletion.Policy.Default,
            "unparseable XML -> default, never a throw");
        Hex.True(CharacterDeletion.Parse("<ServerConfig/>") == CharacterDeletion.Policy.Default,
            "no DeleteUser element -> default");
        Hex.True(CharacterDeletion.Parse(
            "<ServerConfig><DeleteUser expireHour2=\"nonsense\" /></ServerConfig>").ExpireHour2 == 72,
            "an unparseable attribute keeps its default rather than becoming 0 - a typo must not "
            + "silently turn every delete into an immediate one");

        // the classify-level split: below it the throwaway goes at once, at or above it gets the window
        Hex.True(p.WindowHoursFor(1) == 0 && p.WindowHoursFor(4) == 0,
            "level 1 and 4 are below classifyLevel 5 -> expireHour1");
        Hex.True(p.WindowHoursFor(5) == 72 && p.WindowHoursFor(60) == 72,
            "level 5 and up -> expireHour2");
    }

    /// <summary>
    /// T224 - the countdown fields, against cap_final_client2 frame 11 element 0. This is the
    /// layout check: deleteTime is the absolute instant, deleteRemainSec is deleteTime - now.
    /// </summary>
    [Test] public static void T224_the_lobby_countdown_matches_the_capture()
    {
        const long DeleteTime = 1789879815L;   // frame 11, element 0
        const long FrameNow = 1789620633L;    // 0 - banRemainSec in the same element

        var f = CharacterDeletion.LobbyFields(DeleteTime, FrameNow);
        Hex.True(f.IsDeleting && f.DeleteTime == DeleteTime && f.RemainSec == 259182,
            $"frame 11: expected (true, {DeleteTime}, 259182), got {f}");
        Hex.True(CharacterDeletion.LobbyFields(DeleteTime, FrameNow + 32).RemainSec == 259150,
            "and frame 35, 32 s later, reads 259150 - the field is recomputed per send");
        Hex.True(DeleteTime - 259182 == FrameNow && 259200 - 259182 == 18,
            "which puts the delete 18 s before the capture, inside a 72 h window");

        var none = CharacterDeletion.LobbyFields(0, FrameNow);
        Hex.True(!none.IsDeleting && none.DeleteTime == 0 && none.RemainSec == 0,
            "no stamp -> the (false, 0, 0) every other element in that frame carries");
        Hex.True(CharacterDeletion.LobbyFields(FrameNow - 10, FrameNow).RemainSec == 0,
            "a window that has already run out clamps at 0 rather than going negative");
    }

    /// <summary>
    /// T224 - the reported bug. expireHour 0 is an immediate HARD delete, and the row is gone
    /// from GetCharacters for good - which is what "deleted from the lobby, back after a
    /// restart" was failing at.
    /// </summary>
    [Test] public static void T224_a_zero_window_hard_deletes_and_the_lobby_never_lists_it_again()
    {
        using var store = StoreWithTwoAccounts();
        var acct = store.GetOrCreateAccount("acct1");
        store.UpsertItem(9001, 1, 0, 0, 88375, 3);
        var now = DateTimeOffset.UtcNow;
        var policy = new CharacterDeletion.Policy(0, 0, 5);

        Hex.True(store.GetCharacters(acct.Id).Count == 1, "one character to start with");
        var outcome = CharacterDeletion.Delete(store, 1, acct.Id, "t30_1", 11, now, policy,
            out long at);
        Hex.True(outcome == CharacterDeletion.Outcome.Hard && at == 0,
            $"a zero window is a hard delete, not a 72 h park: {outcome}/{at}");
        Hex.True(store.GetCharacter(1) == null, "the row is gone now, not in three days");
        Hex.True(store.GetCharacters(acct.Id).Count == 0,
            "and the lobby list is empty - this is the reported symptom, fixed");
        Hex.True(store.GetDeletedCharacters(10).Count == 0, "nothing is waiting to be restored");

        Hex.True(CharacterDeletion.Delete(store, 1, acct.Id, "t30_1", 11, now, policy, out _)
                 == CharacterDeletion.Outcome.Refused,
            "and deleting it twice is refused rather than reported ok");
        Hex.True(CharacterDeletion.Delete(null, 2, acct.Id, "x", 11, now, policy, out _)
                 == CharacterDeletion.Outcome.Refused,
            "no store is a refusal, not a crash - Program.Store is null in the self-test");
    }

    /// <summary>
    /// T224 - a non-zero window still parks the row, the character STAYS listed (retail does),
    /// and C_CANCEL_DELETE_USER's store call still undoes the whole thing.
    /// </summary>
    [Test] public static void T224_a_scheduled_delete_stays_listed_and_cancel_still_undoes_it()
    {
        using var store = StoreWithTwoAccounts();
        var acct = store.GetOrCreateAccount("acct1");
        store.UpsertItem(9001, 1, 0, 0, 88375, 3);
        // GetCharacters' own purge pass runs against the real clock, so a scheduled stamp has to be
        // really in the future or the test would watch its own row get collected.
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix);
        var policy = CharacterDeletion.Policy.Default;   // 0 / 72 / 5

        var outcome = CharacterDeletion.Delete(store, 1, acct.Id, "t30_1", 11, now, policy,
            out long at);
        Hex.True(outcome == CharacterDeletion.Outcome.Scheduled
                 && at == nowUnix + 72L * 3600L,
            $"level 11 with classifyLevel 5 gets expireHour2: {outcome}/{at}");
        Hex.True(store.GetCharacterDeleteAt(1) == at, "and the stamp is the one it reported");

        var listed = store.GetCharacters(acct.Id);
        Hex.True(listed.Count == 1 && listed[0].Id == 1,
            "still listed: retail's S_GET_USER_LIST is 1183 bytes either side of the cancel");
        var f = CharacterDeletion.LobbyFields(at, nowUnix);
        Hex.True(f.IsDeleting && f.DeleteTime == at && f.RemainSec == 259200,
            $"and it is listed AS DELETING, the full 72 h: {f}");
        Hex.True(store.GetItems(1, 0).Count == 0, "with its items parked in deleted_items");

        Hex.True(store.CancelCharacterDelete(1, acct.Id), "C_CANCEL_DELETE_USER still works");
        Hex.True(store.GetCharacterDeleteAt(1) == 0, "the stamp is cleared");
        Hex.True(!CharacterDeletion.LobbyFields(store.GetCharacterDeleteAt(1), nowUnix).IsDeleting,
            "so the lobby stops reporting isDeleting");
        Hex.True(store.GetItems(1, 0).Count == 1, "and the items came back");
        Hex.True(store.GetCharacters(acct.Id).Count == 1, "character intact");
    }

    /// <summary>
    /// T224 - the classify-level split, and the purge that finally removes a row by its OWN
    /// stamp. PurgeExpiredDeletes could only ever apply one global hour count.
    /// </summary>
    [Test] public static void T224_the_window_is_per_character_and_the_purge_honours_it()
    {
        using var store = StoreWithTwoAccounts();
        var a1 = store.GetOrCreateAccount("acct1");
        var a2 = store.GetOrCreateAccount("acct2");
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // see the note above
        var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix);
        var policy = new CharacterDeletion.Policy(0, 72, 5);   // below 5: now. 5 and up: 72 h.

        store.UpdateLevelAndPosition(1, 3, 5, 1f, 2f, 3f);     // under the classify level
        Hex.True(CharacterDeletion.Delete(store, 1, a1.Id, "low", 3, now, policy, out long atLow)
                 == CharacterDeletion.Outcome.Hard && atLow == 0,
            "a level-3 character is the throwaway expireHour1 is for - it goes at once");
        Hex.True(store.GetCharacter(1) == null && store.GetCharacters(a1.Id).Count == 0,
            "gone, and not listed");

        Hex.True(CharacterDeletion.Delete(store, 2, a2.Id, "high", 12, now, policy, out long atHigh)
                 == CharacterDeletion.Outcome.Scheduled && atHigh == nowUnix + 72L * 3600L,
            "a level-12 one gets the full window from the same policy");

        Hex.True(store.PurgeDueDeletes(atHigh - 1, force: true) == 0, "inside the window nothing goes");
        Hex.True(store.GetCharacter(2) != null, "still restorable");
        Hex.True(store.GetCharacters(a2.Id).Count == 1, "and still listed, counting down");

        Hex.True(store.PurgeDueDeletes(atHigh, force: true) == 1, "at the stamp, exactly one goes");
        Hex.True(store.GetCharacter(2) == null, "the row is finally gone");
        Hex.True(store.GetDeletedCharacters(10).Count == 0, "and the waiting list is empty");
    }

    /// <summary>
    /// T224 - a row whose window ran out while the server was down is excluded from the lobby
    /// list on sight, before any purge has had a chance to run. The reported repro restarted
    /// TeraSharp between the delete and the relogin, which is exactly this path.
    /// </summary>
    [Test] public static void T224_a_row_past_its_window_is_never_listed_even_before_the_purge()
    {
        using var store = StoreWithTwoAccounts();
        var acct = store.GetOrCreateAccount("acct1");
        long past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;

        Hex.True(store.SoftDeleteCharacter(1, acct.Id, "t30_1", past, past - 3600),
            "parked with a stamp that has already come due");
        Hex.True(store.GetCharacters(acct.Id).Count == 0,
            "the lobby list excludes it - the delete_at predicate GetCharacters never had");
        Hex.True(store.GetCharacter(1) == null,
            "and GetCharacters' own purge pass has since removed the row for good");
    }

    /// <summary>
    /// T224 - /api/delete-character goes through the same policy, so the admin tool and the
    /// lobby cannot disagree about what "delete" means. "hard":true keeps the old immediate
    /// behaviour for test characters.
    /// </summary>
    [Test] public static void T224_the_admin_delete_route_uses_the_same_policy()
    {
        using var store = StoreWithTwoAccounts();
        var api = NewAdminApi(store);
        var a1 = store.GetOrCreateAccount("acct1");
        var a2 = store.GetOrCreateAccount("acct2");

        try
        {
            CharacterDeletion.UseForTests(CharacterDeletion.Policy.Default);   // 0 / 72 / 5

            var soft = api.Handle("POST", "/api/delete-character",
                body: "{\"id\":1,\"reason\":\"support ticket\"}", token: T101Token,
                sourceIp: "127.0.0.1");
            Hex.True(soft.Status == 200 && soft.Body.Contains("\"result\":" + AdminApi.ResultOk),
                $"accepted: {soft.Body}");
            Hex.True(store.GetCharacterDeleteAt(1) > 0 && store.GetCharacter(1) != null,
                "and it SCHEDULED rather than hard-deleting - the policy says 72 h for level 11");
            Hex.True(soft.Body.Contains("restore-character"),
                $"and it says so, because restore is now possible: {soft.Body}");

            var hard = api.Handle("POST", "/api/delete-character",
                body: "{\"id\":2,\"reason\":\"test character\",\"hard\":true}", token: T101Token,
                sourceIp: "127.0.0.1");
            Hex.True(hard.Status == 200 && store.GetCharacter(2) == null,
                $"\"hard\":true still drops the row at once: {hard.Body}");
            Hex.True(store.GetCharacters(a2.Id).Count == 0, "and it is not listed");
            Hex.True(store.GetCharacters(a1.Id).Count == 1, "while the scheduled one still is");

            var gone = api.Handle("POST", "/api/delete-character",
                body: "{\"id\":2,\"reason\":\"again\"}", token: T101Token, sourceIp: "127.0.0.1");
            Hex.True(gone.Status == 404, $"and deleting it twice is a 404: {gone.Status}");

            // T224b: "hard":true on a character that is ALREADY parked purges it - the row and the
            // items waiting in deleted_items both go, so the admin tool can always finish a delete
            // the player started.
            Hex.True(store.GetCharacterDeleteAt(1) > 0, "character 1 is still parked from above");
            var finish = api.Handle("POST", "/api/delete-character",
                body: "{\"id\":1,\"reason\":\"finish it\",\"hard\":true}", token: T101Token,
                sourceIp: "127.0.0.1");
            Hex.True(finish.Status == 200 && store.GetCharacter(1) == null,
                $"a parked character can be purged outright: {finish.Body}");
            Hex.True(store.GetCharacters(a1.Id).Count == 0 && store.GetDeletedCharacters(10).Count == 0,
                "not listed, and nothing left waiting");

            // T224b: the flag itself. This was the bug - JsonBool was built on RawValue, whose
            // scanner only walks number characters, so it stopped on the 't' of true, returned null,
            // and the route fell through to the 72 h policy for a caller who asked for an immediate
            // delete. Every spelling the admin UI, a curl and a form post might send:
            Hex.True(AdminApi.JsonBool("{\"hard\":true}", "hard") == true
                     && AdminApi.JsonBool("{\"hard\":false}", "hard") == false
                     && AdminApi.JsonBool("{\"hard\": true }", "hard") == true
                     && AdminApi.JsonBool("{\"hard\":\"true\"}", "hard") == true
                     && AdminApi.JsonBool("{\"hard\":\"false\"}", "hard") == false
                     && AdminApi.JsonBool("{\"hard\":1}", "hard") == true
                     && AdminApi.JsonBool("{\"hard\":0}", "hard") == false
                     && AdminApi.JsonBool("{\"id\":2}", "hard") == null
                     && AdminApi.JsonBool(null, "hard") == null,
                "the flag reads true/false, \"true\"/\"false\" and 1/0, and is null when absent");
            Hex.True(AdminApi.JsonNumber("{\"id\":7,\"hard\":true}", "id") == 7,
                "and reading a bool next to a number did not disturb the number reader");
        }
        finally { CharacterDeletion.UseForTests(null); }
    }
}
