// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private sealed class T201DispatchHarness : IDisposable
    {
        internal readonly CharacterStore Store = new(":memory:", QuietLog());
        internal readonly T185Client Caller, Friend, Outsider;
        internal readonly T192WorldPeer Main, Instance;
        private readonly T185Environment _environment = new(null);
        private readonly object? _oldStore;
        private readonly string _map = Path.GetTempFileName();
        internal T201DispatchHarness()
        {
            var property = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
            _oldStore = property.GetValue(null); property.SetValue(null, Store);
            File.WriteAllText(_map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244,\"S_SYSTEM_MESSAGE\":62222,\"S_GUILD_QUEST_LIST\":51292}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("C_OP_COMMAND", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE", "ref message\nstring message\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            Main = new(bridge, 1, 0); Instance = new(bridge, 13, 13); T185Environment.SetWorld(bridge);
            var opcodes = OpcodeTable.LoadFromFile(_map, "376012");
            Caller = new(defs, opcodes, QuietLog()); Friend = new(defs, opcodes, QuietLog()); Outsider = new(defs, opcodes, QuietLog());
            int id = 0;
            foreach (var client in new[] { Caller, Friend, Outsider })
            {
                id++; string account = "dispatch" + id, name = "member" + id;
                long accountId = Store.GetOrCreateAccount(account).Id;
                int character = Store.CreateCharacter(new CharacterRecord { AccountId = accountId, Name = name, Level = 70 });
                var session = client.Session; session.Account.LoadFromStore(Store, account);
                session.PlayerId = (uint)character; session.GameId = (ulong)(2017000 + id);
                session.SelectedCharacter = FakeCharacter.FromRecord(Store.GetCharacter(character)!);
                session.CurrentWorldId = id == 1 ? 13 : 0; session.EnterWorld();
            }
        }
        internal void Run(string text, bool op = false)
        {
            var handler = new GmCommandHandlers(QuietLog()); byte[] body = T201Command(text);
            if (op) handler.OnOpCommand(Caller.Session, body); else handler.OnAdminCommand(Caller.Session, body);
        }
        public void Dispose()
        {
            Caller.Dispose(); Friend.Dispose(); Outsider.Dispose(); Instance.Dispose(); Main.Dispose(); Store.Dispose();
            typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!.SetValue(null, _oldStore);
            _environment.Dispose(); File.Delete(_map); PartyWiring.ResetForTests(); GuildWiring.ResetForTests();
        }
    }

    [Test] public static void T201_social_command_wrappers_apply_party_gauge_debug_delete_and_account_ban()
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-social-dispatch-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "StrSheet_Friend.xml"), "<StrSheet_Friend><String id='1003' string='PartyQA'/></StrSheet_Friend>");
        PartyWiring.ResetForTests();
        try
        {
            QaSocialSheet.Entry.Load(directory);
            using var h = new T201DispatchHarness();
            foreach (int friend in new[] { 2, 3 })
            { h.Store.UpsertFriend(1, friend, 0, "QACommand"); h.Store.UpsertFriend(friend, 1, 0, "QACommand"); }
            PartyWiring.SyncRoster();
            PartyWiring.Manager.JoinForQa(new(h.Caller.Session.PlayerId, 1, "member1", 70, 0, 0, 0, h.Caller.Session.GameId),
                new(h.Friend.Session.PlayerId, 2, "member2", 70, 0, 0, 0, h.Friend.Session.GameId), false);
            T181WithOperators(null, () => h.Run("add_friendship_party 123"));
            Hex.True(h.Store.GetFriendshipGage(1, 2) == 0 && h.Caller.Available == 0, "non-operator wrapper cannot mutate a party friend's gauge");
            T181WithOperators("dispatch1", () =>
            {
                h.Run("show_friendship_info on", op: true); h.Run("add_friendship_party 123");
                Hex.Eq(h.Instance.Frame(), Convert.FromHexString("0E000000B115010000007B000000"), "native15B1 highest gauge reaches caller's World13");
                Hex.Eq(h.Caller.Frame(), Convert.FromHexString("0C00F6FC020000007B000000"), "nativeFCF6 assigns the existing party friend's gauge");
                byte[] text = h.Caller.Frame();
                Hex.True(BitConverter.ToUInt16(text, 2) == 0xF30E
                    && Encoding.Unicode.GetString(text[6..]).TrimEnd('\0') == "@3510\vtype\vPartyQA\vpoint\v123",
                    "show_friendship_info consumes StrSheet_Friend1003 in native acquisition notification");
                Hex.True(h.Store.GetFriendshipGage(1, 2) == 123 && h.Store.GetFriendshipGage(2, 1) == 0
                    && h.Store.GetFriendshipGage(1, 3) == 0 && h.Friend.Available == 0 && h.Outsider.Available == 0,
                    "party-only command modifies caller direction, preserving outside-party and reverse relationships");
                h.Run("show_friendship_info off"); h.Run("add_friendship_party 124", op: true);
                h.Instance.Frame(); h.Caller.Frame(); Hex.True(h.Caller.Available == 0, "off suppresses only the debug acquisition message");
                h.Run("delete_friend member2", op: true); h.Caller.Frame();
                Hex.True(h.Store.GetFriendRow(1, 2) == null && h.Store.GetFriendRow(2, 1) == null
                    && h.Store.GetFriendRow(1, 3) != null, "native QA delete removes only the named online friendship in both directions");
                long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); h.Run("set_chat_ban 2");
                long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), until = h.Store.GetQaChatBan(1);
                Hex.True(until >= before + 120 && until <= after + 120 && h.Store.GetQaChatBan(2) == 0,
                    "parsed set_chat_ban applies minutes to only caller account");
                Hex.Eq(h.Instance.Frame(), T201SocialWorld(0x2830, DbProxyHandlers.BuildDbsUserRestriction(h.Caller.Session.GameId)), "restriction snapshot routed to current World");
                Hex.Eq(h.Caller.Frame(), new byte[] { 12, 0, 0xDF, 0x6D }.Concat(BitConverter.GetBytes(until)).ToArray(), "native signed64 S_START_CHAT_BAN expiry");
                Hex.True(h.Main.Available == 0 && h.Friend.Available == 0 && h.Outsider.Available == 0, "no side effects on other account or main World");
            });
        }
        finally { QaSocialSheet.Entry.UseBuiltIn(); PartyWiring.ResetForTests(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_guild_quest_wrappers_refresh_member_specific_board_and_clear_own_guild()
    {
        string directory = T201GuildSheets();
        try
        {
            using var h = new T201DispatchHarness();
            int guild = h.Store.CreateGuild("guild", 1, false), other = h.Store.CreateGuild("other", 3, false);
            h.Store.AddGuildMember(guild, 1, "member1", 0, 0, 0, 70, 1);
            h.Store.AddGuildMember(guild, 2, "member2", 0, 0, 0, 70, 2);
            h.Store.AddGuildMember(other, 3, "member3", 0, 0, 0, 70, 3);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            h.Store.SetGuildQuest(guild, 10001, 1, now, now + 300, 1, 0);
            h.Store.SetGuildQuest(other, 10001, 1, now, now + 300, 3, 0);
            T181WithOperators("dispatch1", () =>
            {
                h.Run("guild_quest_usable on"); byte[] caller = h.Caller.Frame(), friend = h.Friend.Frame();
                Hex.True(BitConverter.ToUInt16(caller, 2) == 0xC85C && caller[80] == 0 && friend[80] == 1,
                    "native member-specific first-season byte, fullframe80; on changes only calling member");
                h.Run("guild_quest_usable off", op: true); caller = h.Caller.Frame(); h.Friend.Frame();
                Hex.True(caller[80] == 1, "native off restores caller's first-season flag");
                h.Run("reset_guild_quest", op: true); byte[] reset = h.Caller.Frame();
                Hex.True(BitConverter.ToUInt16(reset, 2) == 0xC85C && h.Store.GetGuildQuests(guild).Count == 0
                    && h.Store.GetGuildQuests(other).Count == 1, "native reset clears caller guild statuses and returns caller board");
                Hex.True(h.Friend.Available == 0 && h.Outsider.Available == 0 && h.Main.Available == 0 && h.Instance.Available == 0,
                    "reset does not broadcast to other members or Worlds");
            });
        }
        finally { GuildLevelSheet.Entry.UseBuiltIn(); GuildLevelSheet.QuestEntry.UseBuiltIn(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_send_mass_parcel_wrapper_uses_native_500_or_online_recipient_branch()
    {
        using var h = new T201DispatchHarness();
        T181WithOperators(null, () => h.Run("send_mass_parcel"));
        Hex.True(h.Store.GetParcelsFor(1).Count == 0 && h.Caller.Available == 0, "non-operator cannot invoke native mass-test utility");
        T181WithOperators("dispatch1", () =>
        {
            h.Run("send_mass_parcel");
            for (int i = 1; i <= 500; i++)
                Hex.Eq(h.Caller.Frame(), ParcelHandlers.BuildReadRecvStatus((uint)i, 0, false), "one native notification per generated test parcel");
            var parcels = h.Store.GetParcelsFor(1);
            Hex.True(parcels.Count == 500 && parcels.All(x => x.ParcelType == 102 && h.Store.CountParcelItems(x.ParcelId) == 5)
                && h.Store.GetParcelsFor(2).Count == 0, "Arb033 native no-argument branch creates exactly500 test parcels for caller");
            h.Run("send_mass_parcel any-token", op: true);
            h.Caller.Frame(); h.Friend.Frame(); h.Outsider.Frame();
            Hex.True(h.Store.GetParcelsFor(1).Count == 501 && h.Store.GetParcelsFor(2).Count == 1 && h.Store.GetParcelsFor(3).Count == 1,
                "native argument branch sends one parcel to each online user, not another500 to caller");
        });
    }
}
