// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static string? T193Data(string name)
    {
        var path = FindRepoFile(Path.Combine("data", "t193", name));
        if (path == null) Skip.Because("data/t193/" + name + " absent");
        return path;
    }

    private static byte[] T193Frame(JsonDocument doc, string capture, int record, ushort opcode)
        => Convert.FromHexString(doc.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            f.GetProperty("source_record").GetInt32() == record && f.GetProperty("op").GetInt32() == opcode)
            .GetProperty("hex").GetString()!);

    [Test] public static void T193_level70_loads_retail_points_and_keeps_perk_PRE_separate()
    {
        string? frames = T193Data("frames.json"), sheet = T193Data("EpData.xml"), exp = T193Data("EpExp.xml");
        if (frames == null || sheet == null || exp == null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(frames));
        var epSheet = XDocument.Load(sheet).Root!;
        var expRows = XDocument.Load(exp).Root!.Elements().ToArray();
        using var store = GuildStore(1);
        store.UpdateLevelAndPosition(1, 70, 5, 0, 0, 0);
        var handlers = FreshHandlers(store);
        foreach (var pin in new[] { ("cap_final2b", 518, 519), ("cap_2man", 256, 257) })
        {
            byte[] expected = T193Frame(doc, pin.Item1, pin.Item3, 0x1555);
            var row = new CharacterStore.EpRow(BitConverter.ToInt64(expected, 19),
                BitConverter.ToInt32(expected, 15), BitConverter.ToInt32(expected, 55),
                BitConverter.ToInt32(expected, 27), BitConverter.ToInt32(expected, 31),
                BitConverter.ToInt32(expected, 35), BitConverter.ToInt64(expected, 39));
            store.SetCharacterEpDaily(1, row.ReserveBonus, row.ResetTime);
            store.SetCharacterEp(1, row);
            var (op, actual) = RunHandler1(0x1554, T193Frame(doc, pin.Item1, pin.Item2, 0x1554)[6..], store, handlers);
            Hex.True(op == 0x1555, "EP load opcode");
            Hex.Eq(actual, expected[6..], pin.Item1 + ":" + pin.Item3 + " full payload, including TotalEp");
        }

        // World supplies the opening grant: 266 EP levels/1493660 exp gives 300 points.
        byte[] opening = T193Frame(doc, "cap_final2b", 519, 0x1555);
        int epLevel = BitConverter.ToInt32(opening, 15), points = BitConverter.ToInt32(opening, 55);
        Hex.True(points == (int)epSheet.Attribute("startEp")!
            && BitConverter.ToInt64(opening, 19) == (long)expRows.Single(x => (int)x.Attribute("level")! == epLevel - 1).Attribute("exp")!,
            "captured level70 opening balance equals the sheet's start300, at EP level266");
        Hex.True((int)epSheet.Attribute("maxEp")! == 500 && (int)epSheet.Attribute("openLevel")! == 65,
            "500 is the sheet maximum; character level70 does not mean EP level443");

        // PRE is a previous-login snapshot, not a write to the account's current 302 points.
        var before = store.GetCharacterEp(1);
        RunHandler1(0x27C1, T193Frame(doc, "cap_final2b", 5716, 0x27C1)[6..], store, handlers);
        Hex.True(store.GetCharacterEp(1) == before, "PRE must not downgrade current account progress");
        var (perkOp, perk) = RunHandler1(0x27B9, T193Frame(doc, "cap_final2b", 682, 0x27B9)[6..], store, handlers);
        Hex.True(perkOp == 0x27BA, "EP perk load opcode");
        Hex.Eq(perk, T193Frame(doc, "cap_final2b", 683, 0x27BA)[6..], "retail per-character PRE/perk load");
    }

    [Test] public static void T193_account_progress_migrates_once_and_survives_daily_reset_and_reopen()
    {
        string? frames = T193Data("frames.json");
        if (frames == null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(frames));
        string path = Path.Combine(Path.GetTempPath(), "T193-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var original = new CharacterStore(path, QuietLog()))
            {
                long account = original.GetOrCreateAccount("shared").Id;
                original.CreateCharacter(new CharacterRecord { AccountId = account, Name = "first", Level = 70 });
                original.CreateCharacter(new CharacterRecord { AccountId = account, Name = "second", Level = 70 });
                original.CreateCharacter(new CharacterRecord { AccountId = original.GetOrCreateAccount("other").Id, Name = "other", Level = 70 });
            }
            // Legacy conflicting per-character balances: retain originals and choose one intact row,
            // never sum them. There is no timestamp to establish which old EP write was latest.
            using (var db = new SqliteConnection("Data Source=" + path))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP TABLE account_ep; "
                    + "UPDATE characters SET ep_exp=1000,ep_level=10,ep_point=100,ep_daily_exp=30,ep_reset_time=60 WHERE id=1; "
                    + "UPDATE characters SET ep_exp=2000,ep_level=20,ep_point=90,ep_daily_exp=40,ep_reset_time=70 WHERE id=2;";
                cmd.ExecuteNonQuery();
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                Hex.True(store.GetCharacterEp(1) == new CharacterStore.EpRow(2000, 20, 90, 40, 0, 0, 70)
                    && store.GetCharacterEp(2) == store.GetCharacterEp(1), "migration selects the highest-exp intact row for the account");
                var handlers = FreshHandlers(store);
                var (_, ack) = RunHandler1(0x27B1, T193Frame(doc, "cap_social4", 2929, 0x27B1)[6..], store, handlers);
                Hex.Eq(ack, T193Frame(doc, "cap_social4", 2930, 0x27B2)[6..], "captured opening grant ACK");
                RunHandler1(0x27AF, T193Frame(doc, "cap_social4", 3052, 0x27AF)[6..], store, handlers);
                (_, ack) = RunHandler1(0x27B3, T193Frame(doc, "cap_final2b", 1012, 0x27B3)[6..], store, handlers);
                Hex.Eq(ack, T193Frame(doc, "cap_final2b", 1013, 0x27B4)[6..], "captured daily-limit ACK");
                var ep = store.GetCharacterEp(2)!;
                Hex.True(ep.EpLevel == 266 && ep.EpExp == 1493660 && ep.EpPoint == 300
                    && ep.DailyEpExp == 0 && ep.ReserveBonus == 304230 && ep.DailyLimit == 313223,
                    "all sibling logins see the grant, cleared daily exp and updated limit");
                Hex.True(store.GetCharacterEp(3)!.EpPoint == 0, "another account does not inherit progress");
                store.SetEpPre(1, 266, 300);
                Hex.True(store.GetEpPages(2).PreEpLevel == 0, "PRE remains per-character");
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                Hex.True(store.GetCharacterEp(1)!.EpPoint == 300, "current account balance survives reopen");
                store.AddEpPoint(2, 7);
                Hex.True(store.GetCharacterEp(1)!.EpPoint == 307, "item gain targets the sibling's same account");
                var (_, ack) = RunHandler1(0x27B5, T167Frame(8, (0, 99, 4), (4, 2, 4)), store);
                Hex.Eq(ack, "63 00 00 00 01", "decompile-only reset ACK");
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var ep = store.GetCharacterEp(1)!;
                Hex.True(ep.EpPoint == 0 && ep.EpExp == 0 && ep.DailyLimit == 313223
                    && store.GetCharacterEp(2) == ep, "reset survives reopen; legacy progress is never re-imported");
                Hex.True(store.GetEpPages(1).PreEpTotalPoint == 300, "account reset does not reset character PRE");
            }
            using (var db = new SqliteConnection("Data Source=" + path))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT SUM(ep_exp) FROM characters";
                Hex.True(Convert.ToInt64(cmd.ExecuteScalar()) == 3000, "legacy rows remain intact for recovery");
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Test] public static void T193_EP_writes_refuse_unknown_character_without_creating_account_state()
    {
        using var store = GuildStore(1);
        var before = store.GetCharacterEp(1);
        foreach (var (op, size) in new[] { ((ushort)0x27B1, 36), ((ushort)0x27AF, 20),
            ((ushort)0x27B3, 12), ((ushort)0x27BF, 12), ((ushort)0x27B5, 8) })
        {
            var (_, ack) = RunHandler1(op, T167Frame(size, (0, 77, 4), (4, 999999, 4)), store);
            Hex.Eq(ack, "4D 00 00 00 00", "unknown character refused for " + op.ToString("X4"));
        }
        Hex.True(store.GetCharacterEp(999999) == null && store.GetCharacterEp(1) == before,
            "unknown character never creates or changes an account balance");
    }
}
