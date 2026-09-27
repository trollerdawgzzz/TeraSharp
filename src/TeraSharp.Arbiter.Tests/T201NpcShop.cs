// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_NPC_shop_QA_writes_native_overlay_and_replays_after_restart()
    {
        using var h = new T201AccountHarness();
        string directory = Path.Combine(Path.GetTempPath(), "t201-npcshop-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var oldRandom = QaNpcShopCommands.RandomInclusive;
        try
        {
            File.WriteAllText(Path.Combine(directory, "BuyMenuList.xml"), "<MenuList><Menu id='100'><ItemList id='101'/></Menu></MenuList>");
            QaNpcShopCommands.Menus.Load(directory); QaNpcShopCommands.RandomInclusive = maximum => maximum == 1_000_000_000 ? 123 : 0;
            T181WithOperators("t39", () => h.Run("store_changeinfo_add random 1 buymenu 100 item 7"));
            byte[] expected = Convert.FromHexString("47000000D815" + "010000000E000000" + "0E00000000000000"
                + "010000006500000007000000" + "7B00000000000000"
                + "00000000" + "00000000" + "0000000000000000" + "00000000" + "00000000" + "00000000" + "01");
            Hex.Eq(h.Main.Frame(), expected, "Arb084:9848 normal store overlay57Brow, every World"); Hex.Eq(h.Instance.Frame(), expected, "current dungeon is not the only recipient");
            Hex.True(System.Text.Encoding.Unicode.GetString(h.Client.Frame()).Contains("일반 아이템 1개 변경 완료"), "native completion text");
            T181WithOperators("t39", () => h.Run("store_changeinfo_arbiter"));
            Hex.True(System.Text.Encoding.Unicode.GetString(h.Client.Frame()).Contains("항목 수: 1개"), "native count reads actual ledger");
            T181WithOperators("t39", () => h.Run("store_changeinfo_arbiter 1"));
            Hex.True(System.Text.Encoding.Unicode.GetString(h.Client.Frame()).Contains("1번째 목록 100: 아이템 7 가격 123 (추가)"), "native1-based detail maps child list back to menu");
            string database = Path.Combine(directory, "shop.db");
            using (var first = new CharacterStore(database, QuietLog())) first.AddNpcShopChange(101, 7, 123);
            using (var reopened = new CharacterStore(database, QuietLog()))
            {
                QaNpcShopCommands.Replay(reopened, h.Main.Link); Hex.Eq(h.Main.Frame(), expected, "World reconnect restores persistent overlay after Arbiter restart");
            }
            foreach (string name in QaNpcShopCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised, "operator refusal: " + name);
        }
        finally
        {
            QaNpcShopCommands.RandomInclusive = oldRandom; QaNpcShopCommands.Menus.UseBuiltIn();
            SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }

    [Test] public static void T201_World_match_battle_field_1517_echoes_handles_on_same_link()
    {
        if (FixtureOrSkip(Path.Combine("data", "t201", "battlefield-create-pairs.json"), "cap_bg1 battlefield-create-pairs.json") is null) return;
        using var environment = new T185Environment(null);
        var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
        using var main = new T192WorldPeer(bridge, 1, 0); using var owner = new T192WorldPeer(bridge, 13, 13);
        string fixture = FindRepoFile(Path.Combine("data", "t201", "battlefield-create-pairs.json"))
            ?? throw new FileNotFoundException("tracked cap_bg1 battlefield-creation fixture missing");
        using var captures = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture));
        var pairs = captures.RootElement.GetProperty("pairs"); Hex.True(pairs.GetArrayLength() == 4, "all four complete real request/reply pairs");
        foreach (var pair in pairs.EnumerateArray())
        {
            byte[] request = Convert.FromHexString(pair.GetProperty("request").GetProperty("hex").GetString()!);
            bridge.HandleFrame(owner.Link, BattlefieldCreation.BSA_CREATE, request[6..]);
            Hex.Eq(owner.Frame(), Convert.FromHexString(pair.GetProperty("reply").GetProperty("hex").GetString()!),
                "cap_bg1 " + pair.GetProperty("request").GetProperty("record").GetInt32() + "→"
                + pair.GetProperty("reply").GetProperty("record").GetInt32() + ": same requesting World and unchanged raw64-bit handles");
        }
        Hex.True(main.Available == 0, "no unconditional main-link reply or invented party resolution");
        foreach (var malformed in new[] { new byte[11], Convert.FromHexString("120000001000000026000000"), Convert.FromHexString("FFFFFFFF1000000026000000"), Convert.FromHexString("12000000010000002600000000") })
            bridge.HandleFrame(owner.Link, BattlefieldCreation.BSA_CREATE, malformed);
        Hex.True(owner.Available == 0 && main.Available == 0, "truncated/overflow/partial handles are rejected without reply");
        bridge.HandleFrame(owner.Link, BattlefieldCreation.BSA_CREATE, Convert.FromHexString("000000000000000026000000"));
        Hex.Eq(owner.Frame(), Convert.FromHexString("13000000181513000000000000002600000000"), "native empty party vector remains an empty1518 with falseflag");
    }
}
