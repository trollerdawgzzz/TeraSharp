// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    /// <summary>T208 evidence: the reframed cap_bg1/cap_bg2 tap records behind the battleground exit.</summary>
    private static byte[] T208Frame(int record, ushort opcode, string capture = "cap_bg1")
    {
        var path = FindRepoFile(Path.Combine("data", "t208", "frames.json"))
            ?? throw new FileNotFoundException("tracked T208 frames.json missing");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var frame = document.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            f.GetProperty("n").GetInt32() == record && f.GetProperty("op").GetUInt16() == opcode);
        return Convert.FromHexString(frame.GetProperty("hex").GetString()!);
    }

    private static byte[] T208Wire(ushort opcode, byte[] body)
    {
        var frame = new byte[6 + body.Length];
        BitConverter.GetBytes(frame.Length).CopyTo(frame, 0);
        BitConverter.GetBytes(opcode).CopyTo(frame, 4);
        body.CopyTo(frame, 6);
        return frame;
    }

    /// <summary>
    /// T208 part 1. "Leave battleground" produced 0x13CD -> 0x1392 and then nothing: the BF World
    /// never sent SA_LEAVE_WORLD, so the return transfer never ran (cap_bg2 60456/60457/60481 and
    /// 60514/60515/60521, arbiter-bg2.log 20:04:04-20:04:45). The only control frame retail answers
    /// on the BF link and TeraSharp did not is SA_UPDATE_BATTLE_FIELD_COOL_TIME - a per-user DLM
    /// item, so unanswered it head-blocks UserLeaveWorld exactly like 0x1562 does.
    /// </summary>
    [Test] public static void T208_battlefield_cooltime_update_is_acked_like_retail()
    {
        if (FixtureOrSkip(Path.Combine("data", "t208", "frames.json"), "T208 frames.json") is null) return;
        using var store = StoreWithTwoAccounts();
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        var oldStore = storeProperty.GetValue(null); storeProperty.SetValue(null, store);
        try
        {
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog())
                { DbProxy = new DbProxyHandlers(store, QuietLog()) };
            using var bf = new T192WorldPeer(bridge, 56, 10);
            foreach (var (request, answer) in new[] { (12933, 12940), (12944, 12945) })
            {
                var body = T208Frame(request, DbProxyHandlers.SA_UPDATE_BATTLE_FIELD_COOL_TIME)[6..];
                bridge.HandleFrame(bf.Link, DbProxyHandlers.SA_UPDATE_BATTLE_FIELD_COOL_TIME, body);
                Hex.Eq(bf.Frame(), T208Frame(answer, DbProxyHandlers.AS_UPDATE_BATTLE_FIELD_COOL_TIME),
                    "0x1523 is acked with retail's [ok][dlmId]");
            }
            // The two frames cap_bg2 left unanswered carry the dlm ids World then waited on forever.
            foreach (var (record, dlmId) in new[] { (56491, 0x89u), (56497, 0x9Eu) })
            {
                var body = T208Frame(record, DbProxyHandlers.SA_UPDATE_BATTLE_FIELD_COOL_TIME, "cap_bg2")[6..];
                Hex.True(BitConverter.ToUInt32(body, 16) == dlmId && BitConverter.ToInt32(body, 20) == 38,
                    "reqId at payload16, battlefield38 - the TeraSharp run's own frames");
                bridge.HandleFrame(bf.Link, DbProxyHandlers.SA_UPDATE_BATTLE_FIELD_COOL_TIME, body);
                Hex.Eq(bf.Frame(), T208Wire(DbProxyHandlers.AS_UPDATE_BATTLE_FIELD_COOL_TIME,
                    DbProxyHandlers.BuildOkReqId(body, 16)), "the blocked dlm item is released now");
            }
        }
        finally { storeProperty.SetValue(null, oldStore); }
    }

    /// <summary>
    /// T208 part 2. Entry stamps the battlefield destination into the stored blob (arbiter-bg2.log
    /// 20:01:31 "Saved world blob ... zone115"), so a session that ends inside the battleground
    /// leaves the character routed at the BF World; the relog is refused there
    /// (20:04:50 SA_ENTER_WORLD_FAIL reason3) and the client hangs on the loading screen.
    /// ForgetUser/ForgetWorld now put the character back on13CB's return point instead.
    /// </summary>
    [Test] public static void T208_abandoned_battleground_session_restores_the_town_return_point()
    {
        using var store = StoreWithTwoAccounts();
        DungeonRouting.ResetForTest();
        DungeonRouting.Channels.CatchAllWorldId = 0;
        var inBattlefield = CrossWorldHandoff.StampLocation(new byte[15312],
            new CrossWorldHandoff.Teleport(0, 0, 10, 115, 0x0AF03922, 9368.1f, 99881.3f, 6756.6f, 5, Array.Empty<byte>()), 10);
        store.SaveWorldBlob(1, inBattlefield);
        Hex.True(store.GetCharacter(1)!.Zone == 115, "the BG transfer leaves the character in continent115");
        Hex.True(store.SaveSystemReturn(1, new CharacterStore.SystemReturnPoint(7005, 0, -1109, 7131, 2172)),
            "13CB's town return point is stored on the way in");

        new BattlefieldHandoff().ReleaseEntered(1, store);

        var after = store.GetCharacter(1)!;
        Hex.True(after.Zone == 7005 && (int)after.X == -1109 && (int)after.Y == 7131 && (int)after.Z == 2172,
            "an abandoned BG session ends where a completed return would have put it");
        Hex.True(BitConverter.ToInt32(after.WorldBlob!, 236) == 7005
            && BitConverter.ToUInt32(after.WorldBlob!, 240) == 0
            && BitConverter.ToInt32(after.WorldBlob!, 244) == 0
            && BitConverter.ToInt32(after.WorldBlob!, 304) == 5,
            "the blob routes the next AS_ENTER_WORLD at the main World and keeps the facing");

        // A character with no stored return point is left alone rather than moved to nowhere.
        store.SaveWorldBlob(2, inBattlefield);
        new BattlefieldHandoff().ReleaseEntered(2, store);
        Hex.True(store.GetCharacter(2)!.Zone == 115, "no return point means no invented destination");
    }
}
