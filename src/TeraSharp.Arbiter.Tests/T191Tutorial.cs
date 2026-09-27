// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T191_tutorial_load_matches_retail_relogs_including_tip1()
    {
        string? path = FindRepoFile(Path.Combine("data", "t191", "frames.json"));
        if (path is null) { Skip.Because("T191 tutorial frames.json missing"); return; }
        using var data = JsonDocument.Parse(File.ReadAllText(path));
        using var store = StoreWithTwoCharacters();
        // Reverse insertion proves native ascending map order, not accidental insertion order.
        foreach (int tip in new[] { 41, 39, 35, 33, 2, 1 }) store.AddTutorialTip(1, tip);
        foreach (int tip in new[] { 41, 39, 35, 33, 31, 2, 1 }) store.AddTutorialTip(2, tip);
        foreach (var pair in new[] {
            ("cap_2man_b", 17468, 17469, 1), ("cap_2man_b", 17490, 17491, 2),
            ("cap_final2b", 34422, 34423, 1), ("cap_final2b", 33787, 33788, 2) })
        {
            byte[] Frame(int record) => Convert.FromHexString(data.RootElement.GetProperty(pair.Item1)
                .EnumerateArray().Single(f => f.GetProperty("source_record").GetInt32() == record)
                .GetProperty("hex").GetString()!);
            byte[] request = Frame(pair.Item2)[6..], expected = Frame(pair.Item3);
            // Capture owner1003 maps to local fixture owner2; no reply byte needs normalization.
            BitConverter.GetBytes(pair.Item4).CopyTo(request, 4);
            var (op, body) = RunHandler1(DbProxyHandlers.SDB_TUTORIAL_SIMPLE_TIP, request, store);
            Hex.True(op == 0x2873, "tutorial load opcode");
            Hex.Eq(body, expected[6..], $"{pair.Item1} {pair.Item2}->{pair.Item3} entire reply payload");
            Hex.True(body.Length + 6 == expected.Length && BitConverter.ToInt32(body, 13) == 1 &&
                BitConverter.ToInt32(body, 17) == 1, "tip1 popup count survives retail relog");
        }
    }

    [Test] public static void T191_tutorial_popup_counts_migrate_persist_and_reject_missing_owner()
    {
        string path = Path.Combine(Path.GetTempPath(), "t191-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Legacy T22 table has no popup_count. Its existing tip must migrate to count1.
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = @"CREATE TABLE tutorial_tips (
owner_id INTEGER NOT NULL, tip_id INTEGER NOT NULL,
created_at TEXT NOT NULL DEFAULT (datetime('now')), PRIMARY KEY(owner_id, tip_id));
INSERT INTO tutorial_tips(owner_id, tip_id) VALUES(1, 1);";
                cmd.ExecuteNonQuery();
            }
            byte[] Request(int owner, int tip, int? delta = null)
            {
                var p = new byte[delta.HasValue ? 16 : 12];
                BitConverter.GetBytes(123u).CopyTo(p, 0);
                BitConverter.GetBytes(owner).CopyTo(p, 4);
                BitConverter.GetBytes(tip).CopyTo(p, 8);
                if (delta.HasValue) BitConverter.GetBytes(delta.Value).CopyTo(p, 12);
                return p;
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var account = store.GetOrCreateAccount("t191");
                int owner = store.CreateCharacter(new CharacterRecord { AccountId = account.Id, Name = "T191" });
                Hex.True(owner == 1 && store.GetTutorialTipCounts(1).Single().PopupCount == 1,
                    "legacy row migrates to one popup");
                var (addOp, add) = RunHandler1(0x286e, Request(owner, 1), store);
                Hex.True(addOp == 0x286f, "ADD reply opcode");
                Hex.Eq(add, Convert.FromHexString("7B00000001"), "native ADD reqId/ok layout");
                var (repeatOp, repeat) = RunHandler1(0x2870, Request(owner, 1, 3), store);
                Hex.True(repeatOp == 0x2871, "DONT_REPEAT reply opcode");
                Hex.Eq(repeat, Convert.FromHexString("7B00000001"), "native DONT_REPEAT reqId/ok layout");
                var (_, missing) = RunHandler1(0x2870, Request(99999, 1, 9), store);
                Hex.Eq(missing, Convert.FromHexString("7B00000000"), "unknown owner refused");
                var (_, malformed) = RunHandler1(0x2870, new byte[4], store);
                Hex.Eq(malformed, new byte[5], "truncated write refused without mutation");
            }
            using (var reopened = new CharacterStore(path, QuietLog()))
            {
                Hex.True(reopened.GetTutorialTipCounts(1).Single() == (1, 5),
                    "ADD+1 and DONT_REPEAT+3 persist over reopen");
                var (_, body) = RunHandler1(0x2872, Convert.FromHexString("7B00000001000000"), reopened);
                Hex.Eq(body, Convert.FromHexString("13000000080000007B000000010100000005000000"),
                    "next login serves stored count5, including captured-character id1");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
