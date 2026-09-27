// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T203 - server-first achievements were handed to everybody.
///
/// <para>The flag is <c>serverUnique</c> from <c>AchievementList.xml</c>, and the Arbiter never has
/// to read that sheet: World copies the decoded value into the <b>second u32 of each 24-byte
/// 0x2802 record</b> - the field ACHIEVEMENTS.md section 1 called <c>[u32 0]</c>. 0 is an ordinary
/// achievement, 1 is a server first, 2 and 3 are server firsts that the claiming PARTY shares.</para>
///
/// <para>There is nothing to send back on a refusal. <c>0x2803</c>'s success byte is a hard-coded 1
/// in the real Arbiter and the refusal IS the record's absence from the reply list; World walks
/// only the records it was handed, so the client hears nothing either. cap_final2b 3622 -&gt; 3623
/// is the whole specification in one pair.</para>
/// </summary>
public static partial class Tests
{
    static Dictionary<uint, byte[]>? LoadT203CaptureOrSkip() => LoadTsisOrSkip("cap_t203.bin");

    /// <summary>A 0x2802 request: <c>[u32 off][u32 bytes][u32 dlmId][u32 playerId]</c> + records.</summary>
    static byte[] T203Request(uint dlmId, int playerId, params (int Id, uint Unique)[] records)
    {
        int header = DbProxyHandlers.AchievementRequestHeader, size = DbProxyHandlers.AchievementRecordSize;
        var p = new byte[header + records.Length * size];
        BitConverter.GetBytes((uint)(6 + header)).CopyTo(p, 0);
        BitConverter.GetBytes((uint)(records.Length * size)).CopyTo(p, 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        BitConverter.GetBytes(playerId).CopyTo(p, 12);
        for (int i = 0; i < records.Length; i++)
        {
            int at = header + i * size;
            BitConverter.GetBytes(records[i].Id).CopyTo(p, at + DbProxyHandlers.AchievementRecordIdOffset);
            BitConverter.GetBytes(records[i].Unique).CopyTo(p, at + DbProxyHandlers.AchievementRecordServerUniqueOffset);
            foreach (var (value, o) in new[] { (2026, 0), (9, 2), (22, 4), (8, 6), (59, 8), (12, 10) })
                BitConverter.GetBytes((ushort)value).CopyTo(p, at + 8 + o);
        }
        return p;
    }

    /// <summary>
    /// The achievement ids a 0x2802 request or a 0x2803 reply carries, in order. Both start with
    /// <c>[u32 frame offset][u32 byte count]</c>; the reply's header is four bytes shorter, which
    /// is why <c>SliceAchievementRecords</c> (written for requests) cannot read one.
    /// </summary>
    static List<int> T203Ids(byte[] frame)
    {
        var ids = new List<int>();
        if (frame is null || frame.Length < 8) return ids;
        int at = (int)BitConverter.ToUInt32(frame, 0) - 6, len = (int)BitConverter.ToUInt32(frame, 4);
        if (at < 0 || len <= 0 || len % DbProxyHandlers.AchievementRecordSize != 0
            || at > frame.Length || len > frame.Length - at) return ids;
        for (int o = at; o + DbProxyHandlers.AchievementRecordSize <= at + len;
             o += DbProxyHandlers.AchievementRecordSize)
            ids.Add((int)BitConverter.ToUInt32(frame, o + DbProxyHandlers.AchievementRecordIdOffset));
        return ids;
    }

    [Test] public static void T203_the_real_arbiter_refuses_taken_server_firsts_by_omission()
    {
        var cap = LoadT203CaptureOrSkip();
        if (cap == null) return;

        var offered = DbProxyHandlers.SliceAchievementRecords(cap[3622]);
        var granted = T203Ids(cap[3623]);
        Hex.True(offered.Count == 12 && granted.Count == 7,
                 $"twelve offered, seven granted, got {offered.Count} and {granted.Count}");
        Hex.True(cap[3623][12] == 1, "and the reply still says success - there is no refusal reply");

        // The split is exactly serverUnique, with no exceptions in either direction.
        foreach (var rec in offered)
        {
            int id = (int)BitConverter.ToUInt32(rec, DbProxyHandlers.AchievementRecordIdOffset);
            uint unique = BitConverter.ToUInt32(rec, DbProxyHandlers.AchievementRecordServerUniqueOffset);
            Hex.True(granted.Contains(id) == (unique == 0),
                     $"achievement {id} has serverUnique {unique} and was {(granted.Contains(id) ? "granted" : "refused")}");
        }
        Hex.True(!granted.Intersect(new[] { 730, 731, 732, 737, 2103 }).Any(),
                 "the five server firsts 730/731/732/737/2103 are the ones left out");

        // Our handler, with those five already held by another character, reproduces the reply
        // BYTE FOR BYTE - which is possible precisely because the refusal writes nothing.
        using var store = T45Store();
        foreach (int id in new[] { 730, 731, 732, 737, 2103 })
            Hex.True(store.TryClaimServerAchievement(id, 2), $"character 2 claims {id} first");

        var request = (byte[])cap[3622].Clone();
        BitConverter.GetBytes(1).CopyTo(request, 12);        // 1003 -> our character 1
        var (op, body) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT, request, store);
        Hex.True(op == DbProxyHandlers.DBS_ACCOMPLISH_USER_ACHIEVEMENT, $"reply opcode 0x{op:X4}");
        Hex.Eq(body, cap[3623], "DBS_ACCOMPLISH_USER_ACHIEVEMENT (cap_final2b 3622 -> 3623)");

        // And the seven ordinary ones really were stored, not merely echoed.
        Hex.True(store.GetAccomplishedAchievements(1).Count == 7,
                 $"seven rows kept, got {store.GetAccomplishedAchievements(1).Count}");
        Hex.True(store.GetServerAchievements().All(c => c.OwnerId == 2),
                 "and no server first changed hands");
    }

    [Test] public static void T203_only_the_first_claimant_gets_a_server_first()
    {
        using var store = T45Store();

        // Character 1 is first to both: the ordinary achievement and the server first.
        var (_, first) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
            T203Request(1, 1, (54, 0), (730, 1)), store);
        Hex.True(T203Ids(first).SequenceEqual(new[] { 54, 730 }), "the first claimant gets both");

        // Character 2 comes second. The ordinary one is still theirs to earn; the server first is
        // not, and it simply does not appear in the reply.
        var (_, second) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
            T203Request(2, 2, (54, 0), (730, 1)), store);
        Hex.True(T203Ids(second).SequenceEqual(new[] { 54 }),
                 "the second claimant gets the ordinary one only: " + string.Join(",", T203Ids(second)));
        Hex.True(store.GetAccomplishedAchievements(2).Count == 1, "and only that one is stored for them");
        Hex.True(store.GetServerAchievementClaim(730)!.OwnerId == 1, "character 1 still holds 730");

        // The holder re-submitting it is the ordinary duplicate case: the per-character table
        // refuses it, not the claim table, and the reply is the 19-byte empty form.
        var (_, again) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
            T203Request(3, 1, (730, 1)), store);
        Hex.True(again.Length == DbProxyHandlers.AchievementReplyHeader && T203Ids(again).Count == 0,
                 $"the holder gets the empty reply, {again.Length} B");

        // A party-shared server first (serverUnique 2/3) behaves like kind 1 for a stranger with
        // no party - the only case any capture can speak to.
        var (_, stranger) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
            T203Request(4, 1, (900, 3)), store);
        Hex.True(T203Ids(stranger).SequenceEqual(new[] { 900 }), "character 1 claims the party-shared one");
        var (_, outsider) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
            T203Request(5, 2, (900, 3)), store);
        Hex.True(T203Ids(outsider).Count == 0, "and a partyless outsider is refused it too");
    }

    [Test] public static void T203_a_claim_survives_a_restart_and_the_admin_api_shows_and_clears_it()
    {
        string dir = T37TempDir();
        try
        {
            string path = Path.Combine(dir, "t203.db");
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var account = store.GetOrCreateAccount("t203");
                int one = store.CreateCharacter(new CharacterRecord { AccountId = account.Id, Name = "Winner" });
                int two = store.CreateCharacter(new CharacterRecord { AccountId = account.Id, Name = "Runner" });
                Hex.True(one == 1 && two == 2, $"characters {one} and {two}");
                RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT, T203Request(1, one, (730, 1)), store);
                Hex.True(store.GetServerAchievementClaim(730)!.OwnerId == one, "Winner holds 730");
            }

            using (var reopened = new CharacterStore(path, QuietLog()))
            {
                // A restart must not free a server first - that is the whole point of the table.
                var claim = reopened.GetServerAchievementClaim(730);
                Hex.True(claim is not null && claim.OwnerId == 1, "the claim is still Winner's after a restart");
                var (_, refused) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
                    T203Request(2, 2, (730, 1)), reopened);
                Hex.True(T203Ids(refused).Count == 0, "and Runner is still refused it");

                var api = NewAdminApi(reopened);
                var list = api.Handle("GET", "/api/server-achievements", token: T101Token);
                Hex.True(list.Status == 200 && list.Body.Contains("\"achievementId\":730")
                         && list.Body.Contains("\"name\":\"Winner\""),
                         $"the winners list names the holder: {list.Body}");

                var bad = api.Handle("POST", "/api/clear-server-achievement", body: "{}", token: T101Token);
                Hex.True(bad.Status == 400, "clearing needs an id or all");
                var cleared = api.Handle("POST", "/api/clear-server-achievement",
                    body: "{\"id\":730}", token: T101Token);
                Hex.True(cleared.Status == 200 && cleared.Body.Contains("\"cleared\":1"),
                         $"one claim released: {cleared.Body}");

                // Released, so the next claimant wins it.
                var (_, now) = RunHandler1(DbProxyHandlers.SDB_ACCOMPLISH_USER_ACHIEVEMENT,
                    T203Request(3, 2, (730, 1)), reopened);
                Hex.True(T203Ids(now).SequenceEqual(new[] { 730 }), "Runner now gets it");
                Hex.True(reopened.GetServerAchievementClaim(730)!.OwnerId == 2, "and holds the claim");

                var all = api.Handle("POST", "/api/clear-server-achievement",
                    body: "{\"all\":true}", token: T101Token);
                Hex.True(all.Status == 200 && reopened.GetServerAchievements().Count == 0,
                         $"and clear-all empties the table: {all.Body}");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}
