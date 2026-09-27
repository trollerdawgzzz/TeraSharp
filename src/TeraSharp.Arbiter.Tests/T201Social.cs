// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_social_QA_mutual_friend_gauge_caps_and_chat_ban_restore_native_state()
    {
        using var env = new T185Environment(null);
        string path = Path.Combine(Path.GetTempPath(), "t201-social-" + Guid.NewGuid() + ".db"), map = Path.GetTempFileName();
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? oldStore = storeProperty.GetValue(null); int oldChannelMax = ChatManager.MaxMembersPerChannel;
        var oldIds = new[] { 1, 2 }.ToDictionary(id => id, id => DbProxyHandlers.GameIdByPlayer.TryGetValue(id, out var value) ? (ulong?)value : null);
        CharacterStore? store = null;
        QaSocialCommands.ResetLimitsForTests();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            store = new CharacterStore(path, QuietLog()); storeProperty.SetValue(null, store);
            long account = store.GetOrCreateAccount("t201-social-a").Id;
            int me = store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "sociala" });
            int them = store.CreateCharacter(new CharacterRecord { AccountId = store.GetOrCreateAccount("t201-social-b").Id, Name = "socialb" });
            int third = store.CreateCharacter(new CharacterRecord { AccountId = store.GetOrCreateAccount("t201-social-c").Id, Name = "socialc" });
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0); using var owner = new T192WorldPeer(bridge, 13, 13);
            var defs = new DefinitionRegistry(QuietLog()); var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            using var a = new T185Client(defs, opcodes, QuietLog()); using var b = new T185Client(defs, opcodes, QuietLog());
            T185Environment.SetWorld(bridge);
            a.Session.PlayerId = (uint)me; a.Session.Account.Name = "t201-social-a";
            a.Session.SelectedCharacter = new FakeCharacter { Id = (uint)me, Name = "sociala" };
            a.Session.GameId = 0x20101; a.Session.TunnelKey = 20101; a.Session.CurrentWorldId = 13; a.Session.EnterWorld();
            b.Session.PlayerId = (uint)them; b.Session.Account.Name = "t201-social-b";
            b.Session.SelectedCharacter = new FakeCharacter { Id = (uint)them, Name = "socialb" };
            b.Session.GameId = 0x20102; b.Session.TunnelKey = 20102; b.Session.EnterWorld();
            void Run(string text) => QaSocialCommands.TryExecute(a.Session, store, GmCommandParser.Parse(text)!, QuietLog());

            T181WithOperators(null, () => Run("add_friend socialb"));
            Hex.Eq(owner.Frame(), Convert.FromHexString("0E00000062280100000001000000"), "native QA mutual friend count follows caller World13");
            Hex.Eq(main.Frame(), Convert.FromHexString("0E00000062280200000001000000"), "target gets its own World0 friend count");
            Hex.True(store.GetFriendRow(me, them) is { Type: 0, Memo: "QACommand" }
                && store.GetFriendRow(them, me) is { Type: 0, Memo: "QACommand" }, "Arb030:7789 type0xAB becomes mutual with native QA memo");
            Run("friendlist_max 1"); Run("blocklist_max 1"); Run("pchannel_max 2");
            Hex.True(SocialHandlers.CanAddFriend(store, me, third) == SocialHandlers.AddFriendResult.MyListFull
                && QaSocialCommands.FriendLimit(them) == 100 && ChatManager.MaxMembersPerChannel == 2,
                "per-user friend cap and global private-channel cap are consumed by real handlers");
            Run("friendlist_max 0"); Hex.True(QaSocialCommands.FriendLimit(me) == 1, "invalid native cap is silent and unchanged");
            Hex.True(QaSocialCommands.TakeFriendRequest(a.Session, third) && QaSocialCommands.TakeFriendRequest(a.Session, third)
                && !QaSocialCommands.TakeFriendRequest(a.Session, third), "Arb084:609 default3 rejects third request");
            Run("clear_friend_request"); Hex.True(QaSocialCommands.TakeFriendRequest(a.Session, third)
                && store.GetFriendRow(me, them) != null, "clear_request clears only native RAM counter, not SQL friends");
            Run("delete_friend_limit_off");
            for (int i = 0; i < 5; i++) Hex.True(QaSocialCommands.TakeFriendRequest(a.Session, third), "native global counter override is effective");

            Run("add_friendship socialb 500");
            Hex.Eq(owner.Frame(), Convert.FromHexString("0E000000B11501000000F4010000"), "Arb031:7064 highest gauge goes to current World");
            Hex.Eq(a.Frame(), Convert.FromHexString("0C00F6FC02000000F4010000"), "Arb030:10995 client friendship SET500");
            Run("add_friendship socialb 400");
            Hex.Eq(a.Frame(), Convert.FromHexString("0C00F6FC0200000090010000"), "QA condition6 assigns400 instead of adding400");
            Hex.True(owner.Available == 0 && store.GetFriendshipGage(them, me) == 0, "only caller direction changes; native high-water World stat does not shrink in session");

            const long now = 4102444800; // Synthetic deterministic future clock, not claimed captured.
            long until = QaSocialCommands.ApplyChatBan(a.Session, store, 5, now);
            Hex.True(until == now + 300, "native QA argument is minutes");
            Hex.Eq(owner.Frame(), T201SocialWorld(0x2830, DbProxyHandlers.BuildDbsUserRestriction(a.Session.GameId)),
                "kind3 ban is omitted from World's kind2 restriction snapshot, Arb054:1528");
            byte[] start = new byte[] { 12, 0, 0xDF, 0x6D }.Concat(BitConverter.GetBytes(until)).ToArray();
            Hex.Eq(a.Frame(), start, "native S_START_CHAT_BAN uses signed64 Unix expiry");
            Hex.True(QaSocialCommands.IsChatBanned(store, me, now) && !QaSocialCommands.IsChatBanned(store, them, now), "ban is account-scoped");

            store.Dispose(); store = new CharacterStore(path, QuietLog()); storeProperty.SetValue(null, store);
            var row = (Dictionary<string, object>)((List<object>)SocialHandlers.BuildFriendListFields(store, me)["friends"])[0];
            Hex.True((int)row["bonds"] == 400 && store.GetQaChatBan(account) == until, "friend gauge and account expiry survive close/reopen");
            QaSocialCommands.Forget(a.Session); QaSocialCommands.RestoreChatBan(a.Session, store, now + 1);
            Hex.Eq(a.Frame(), start, "native login restriction restores the client ban");
            QaSocialCommands.RestoreChatBan(a.Session, store, now + 2); Hex.True(a.Available == 0, "roster refresh does not duplicate start packet");
            QaSocialCommands.OnWorldEntryComplete(a.Session, store);
            Hex.Eq(owner.Frame(), Convert.FromHexString("0E000000B1150100000090010000"), "new World entry restores persisted max gauge after user exists");
            QaSocialCommands.ExpireChatBans(store, new[] { a.Session, b.Session }, until - 1); Hex.True(a.Available == 0, "not expired early");
            QaSocialCommands.ExpireChatBans(store, new[] { a.Session, b.Session }, until);
            Hex.Eq(a.Frame(), Convert.FromHexString("040091EA"), "Arb061:13048 S_END_CHAT_BAN on native expiry");
            Hex.True(store.GetQaChatBan(account) == 0 && !QaSocialCommands.IsChatBanned(store, me, until), "expiry removes persisted account restriction");
            foreach (string name in QaSocialCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised,
                    "ordinary users cannot execute any social QA path: " + name);
            Hex.True(GmCommandHandlers.Classify(true, 5, GmCommandParser.Parse("add_many_friends 10")) == GmDispatch.Denied,
                "persistent synthetic account generation is explicitly denied");
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, oldStore); store?.Dispose();
            ChatManager.MaxMembersPerChannel = oldChannelMax; QaSocialCommands.ResetLimitsForTests();
            foreach (var pair in oldIds) if (pair.Value is ulong value) DbProxyHandlers.GameIdByPlayer[pair.Key] = value; else DbProxyHandlers.GameIdByPlayer.TryRemove(pair.Key, out _);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(map);
        }
    }

    private static byte[] T201SocialWorld(ushort opcode, byte[] payload)
        => BitConverter.GetBytes(payload.Length + 6).Concat(BitConverter.GetBytes(opcode)).Concat(payload).ToArray();
}
