// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private sealed class T201UtilityEnvironment : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "t201-utility-" + Guid.NewGuid());
        internal readonly CharacterStore Store; internal readonly WorldBridge Bridge;
        internal readonly T192WorldPeer Main, Dungeon; internal readonly T185Client Caller, Target;
        internal readonly int CallerId, TargetId; internal readonly long AccountId;
        private readonly T185Environment environment = new(null);
        private readonly PropertyInfo property = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        private readonly object? oldStore; private readonly string? oldDirectory;
        internal T201UtilityEnvironment()
        {
            oldStore = property.GetValue(null); oldDirectory = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET");
            Directory.CreateDirectory(DirectoryPath); Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "ReplayMovie.xml"), "<ReplayMovie><MovieGroup><Movie id='80'/><Movie id='1101'/></MovieGroup></ReplayMovie>");
            File.WriteAllText(Path.Combine(DirectoryPath, "AccountTrait.xml"), "<AccountTrait><PackageList><Package id='0'><Property name='expandCharacterSlot' slot='3'/></Package><Package id='1'><Property name='expandCharacterSlot' slot='8'/></Package></PackageList></AccountTrait>");
            QaUtilityCommands.Sheet.Load(DirectoryPath);
            Store = new CharacterStore(Path.Combine(DirectoryPath, "store.db"), QuietLog()); property.SetValue(null, Store);
            AccountId = Store.GetOrCreateAccount("utility-op").Id;
            CallerId = Store.CreateCharacter(new CharacterRecord { AccountId = AccountId, Name = "Utility", Zone = 9781, X = 11, Y = 22, Z = 33 });
            var other = Store.GetOrCreateAccount("utility-target");
            TargetId = Store.CreateCharacter(new CharacterRecord { AccountId = other.Id, Name = "Target", Zone = 7005 });
            string map = Path.Combine(DirectoryPath, "map.json");
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244,\"S_CAN_CREATE_USER\":28786}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("C_OP_COMMAND", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            defs.RegisterFromDef("S_CAN_CREATE_USER", "bool ok\n");
            Bridge = new(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            Main = new(Bridge, 81, 0); Dungeon = new(Bridge, 82, 13);
            Caller = new(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog()); Target = new(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(Bridge); DungeonRouting.ResetForTest(); DungeonRouting.Channels.CatchAllWorldId = 0; DungeonRouting.Channels.MapContinent(9781, 13);
            var s = Caller.Session; s.Account.LoadFromStore(Store, "utility-op"); s.PlayerId = (uint)CallerId; s.GameId = 2016001;
            s.SelectedCharacter = FakeCharacter.FromRecord(Store.GetCharacter(CallerId)!); s.EnterWorld(); s.CurrentWorldId = 13;
        }
        internal void Run(string text, bool op = false)
        {
            var payload = new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray();
            var gm = new GmCommandHandlers(QuietLog());
            if (op) gm.OnOpCommand(Caller.Session, payload); else gm.OnAdminCommand(Caller.Session, payload);
        }
        internal void TargetOnline()
        {
            var s = Target.Session; s.Account.LoadFromStore(Store, "utility-target"); s.PlayerId = (uint)TargetId; s.GameId = 2016002;
            s.SelectedCharacter = FakeCharacter.FromRecord(Store.GetCharacter(TargetId)!); s.EnterWorld(); s.CurrentWorldId = 0;
        }
        public void Dispose()
        {
            Caller.Dispose(); Target.Dispose(); Main.Dispose(); Dungeon.Dispose(); property.SetValue(null, oldStore);
            T185Environment.SetWorld(null); Store.Dispose(); environment.Dispose(); DungeonRouting.ResetForTest();
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDirectory); QaUtilityCommands.Sheet.Load(HandshakeData.DatasheetDirectory());
            QaGuildRankingCommands.Sheet.Load(HandshakeData.DatasheetDirectory()); QaItemSheet.Entry.Load(HandshakeData.DatasheetDirectory());
            QaUtilityCommands.TickCount = () => unchecked((uint)Environment.TickCount); SqliteConnection.ClearAllPools();
            foreach (string file in Directory.GetFiles(DirectoryPath)) File.Delete(file); Directory.Delete(DirectoryPath);
        }
    }

    [Test] public static void T201_utility_commands_gate_operators_and_route_native_notice_debug_vote_help()
    {
        using var e = new T201UtilityEnvironment();
        T181WithOperators(null, () =>
        {
            foreach (string name in QaUtilityCommands.Names) e.Run(name + " Target 1 1");
            Hex.True(e.Caller.Available == 0 && e.Main.Available == 0 && e.Dungeon.Available == 0, "all utility names enforce central operator gate");
            Hex.True(e.Store.GetWatchedMoviesForAccount(e.AccountId).Count == 0 && e.Store.GetNonPkSections().Count == 0, "unauthorized commands do not write");
        });
        T181WithOperators("utility-op", () =>
        {
            e.Run("clear_vote_cool"); var expected = Convert.FromHexString("0A000000281501000000");
            Hex.Eq(e.Main.Frame(), expected, "vote clear allWorld0 Arb028690"); Hex.Eq(e.Dungeon.Frame(), expected, "vote clear allWorld13");
            e.Run("gmevent_notice Hi"); Hex.Eq(e.Dungeon.Frame(), Convert.FromHexString("1200000011160A0000004800690020000000"), "native command retains trailing space and targets callerWorld13");
            Hex.True(e.Main.Available == 0, "GM event notice never falls back to mainWorld");
            QaUtilityCommands.TickCount = () => 123456; e.Run("devdebug ping");
            Hex.Eq(e.Dungeon.Frame(), Convert.FromHexString("0E00000089130100000040E20100"), "nativeAS1389 user and tick");
            e.Run("devdebug draw on"); Hex.Eq(e.Caller.Frame(), Convert.FromHexString("16006F8C0600640072006100770020006F006E000000"), "native S_STEER_DEBUG_COMMAND ref6");
            e.Run("i_want_server_language_and_revision"); Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("server lang and revision [6],[376056]"), "agrees with S_SERVER_BUILD_INFO");
            e.Run("help unlock_all_movies"); var heading = Encoding.Unicode.GetString(e.Caller.Frame()); var row = Encoding.Unicode.GetString(e.Caller.Frame());
            Hex.True(heading.Contains("*****Help search Result***** &#xa;") && row.Contains("unlock_all_movies") && row.Contains(" // ") && row.Contains(" &#xa;"), "native help heading and row formatter");
            // T233: this used to assert a "_helpworld unlock_all_movies" forward to World. _helpworld is
            // not one of the 545 names WorldServer's CommandDistributor registers, so the forward was
            // always answered with "Invalid QA Command" - /@help's World half never worked. World's
            // catalogue is searched locally now, and "unlock_all_movies" is an Arbiter-only name that
            // matches no World row, so the answer ends after the one native row consumed above.
            Hex.True(WorldQaCommandData.Search("unlock_all_movies").Count() == 0, "unlock_all_movies is not a WorldServer command");
            Hex.True(e.Dungeon.Available == 0 && e.Main.Available == 0 && e.Caller.Available == 0, "help answers entirely from local catalogues - no World forward");
            e.Run("devdebug connection"); Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("AuthManager"), "does not fabricate unavailable admission counters");
        });
    }

    [Test] public static void T201_utility_persistent_movies_slots_loading_and_non_pk_have_consumers()
    {
        using var e = new T201UtilityEnvironment(); e.TargetOnline();
        T181WithOperators("utility-op", () =>
        {
            e.Run("unlock_all_movies"); e.Caller.Frame(); e.Run("unlock_all_movies"); e.Caller.Frame();
            Hex.True(e.Store.GetWatchedMoviesForAccount(e.AccountId).SequenceEqual(new[] { 80, 1101 }), "account movie consumer sees every sheet ID exactly once");
            e.Run("change_loading_screen_status 1"); Hex.Eq(e.Caller.Frame(), Convert.FromHexString("050088C501"), "caller loading control");
            Hex.Eq(e.Target.Frame(), Convert.FromHexString("050088C501"), "other online account loading control");
            e.Run("pk_section 91 A 7"); var add = Convert.FromHexString("17000000E014130000005B000000070000000141000000");
            Hex.Eq(e.Main.Frame(), add, "native14E0 safe UTF16 name"); Hex.Eq(e.Dungeon.Frame(), add, "native broadcast scope");
            e.Store.CreateCharacter(new CharacterRecord { AccountId = e.AccountId, Name = "Second" });
            e.Store.CreateCharacter(new CharacterRecord { AccountId = e.AccountId, Name = "Third" });
            e.Run("reset_charsock"); Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("reset!"), "under4 native guard");
            e.Caller.Session.Account.LoadFromStore(e.Store, "utility-op"); new CharacterHandlers(QuietLog()).OnCanCreateUser(e.Caller.Session, ReadOnlyMemory<byte>.Empty);
            // T228: the slot count is max(sheet base, MaxCharactersPerAccount) raised by packages,
            // so defaultPackage0's slot="3" no longer caps this account at three - reading the sheet
            // alone would have SHRUNK every unpackaged account from eight slots to three. Three
            // characters against the floor of eight leaves room, so the answer is now ok=1.
            Hex.Eq(e.Caller.Frame(), Convert.FromHexString("0500727001"), "three existing users do not exhaust the floored capacity");
            using (var reopened = new CharacterStore(Path.Combine(e.DirectoryPath, "store.db"), QuietLog()))
            {
                // T228: reset_charsock pinned character_slots_<account> to the sheet's 3, and an explicit
                // counter wins over the max(sheet, 8) floor in either direction - that is what keeps the
                // command meaningful. So the persisted value read back after restart is 3.
                Hex.True(QaUtilityCommands.LoadingScreenEnabled(reopened) && QaUtilityCommands.CharacterSlots(reopened, e.AccountId) == 3, "login consumes persisted loading and slot controls after restart");
                Hex.True(reopened.GetWatchedMoviesForAccount(e.AccountId).Count == 2 && reopened.GetNonPkSections().Count == 1, "movies and PK row survive restart");
            }
            e.Run("pk_section 91 A 7 on"); e.Main.Frame(); e.Dungeon.Frame(); Hex.True(e.Store.GetNonPkSections().Count == 0, "PK on removes restriction");
            e.Store.CreateCharacter(new CharacterRecord { AccountId = e.AccountId, Name = "Fourth" });
            e.Store.SetCounterValue("character_slots_" + e.AccountId, 8); e.Run("reset_charsock");
            Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("under 4") && QaUtilityCommands.CharacterSlots(e.Store, e.AccountId) == 8, "four characters refuse without changing capacity");
        });
    }

    [Test] public static void T201_escape_is_saved_location_and_sticktogether_uses_distinct_OP_QA_paths()
    {
        using var e = new T201UtilityEnvironment();
        var blob = new byte[308]; BitConverter.GetBytes(11f).CopyTo(blob, 220); BitConverter.GetBytes(22f).CopyTo(blob, 224);
        BitConverter.GetBytes(33f).CopyTo(blob, 228); BitConverter.GetBytes(9781).CopyTo(blob, 236); BitConverter.GetBytes(321u).CopyTo(blob, 240);
        BitConverter.GetBytes(123).CopyTo(blob, 304); e.Store.SaveWorldBlob(e.CallerId, blob);
        T181WithOperators("utility-op", () =>
        {
            e.Run("escape Target"); var escaped = e.Store.GetCharacter(e.TargetId)!;
            Hex.True(escaped.Zone == 1 && BitConverter.SingleToInt32Bits(escaped.X) == 0x47A92D80
                && BitConverter.SingleToInt32Bits(escaped.Y) == unchecked((int)0xC6AEEE00) && BitConverter.SingleToInt32Bits(escaped.Z) == 0x44A20000,
                "native escape fixed location bit patterns");
            Hex.True(e.Main.Available == 0 && e.Dungeon.Available == 0, "native escape persists but emits no immediate teleport");
            e.Run("sticktogether Target"); var moved = e.Store.GetCharacter(e.TargetId)!;
            Hex.True(moved.Zone == 9781 && moved.X == 11 && moved.Y == 22 && moved.Z == 33, "offline target uses caller's last saved position");
            e.TargetOnline(); e.Run("sticktogether Target", op: true);
            Hex.Eq(e.Main.Frame(), Convert.FromHexString("1E000000B113020000003526000041010000000030410000B04100000442"), "OP summon13B1 targets summoned user's currentWorld0");
            Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("Summoned [Target]"), "OP native acknowledgement");
            var transfers = (CrossWorldHandoff)typeof(WorldBridge).GetField("_crossWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(e.Bridge)!;
            var enter = new byte[170]; BitConverter.GetBytes(173u).CopyTo(enter, 8); BitConverter.GetBytes(3u).CopyTo(enter, 12);
            BitConverter.GetBytes(e.Target.Session.GameId).CopyTo(enter, 24); BitConverter.GetBytes(e.TargetId).CopyTo(enter, 32);
            BitConverter.GetBytes(e.Target.Session.GameId).CopyTo(enter, 84); enter[167] = 4; enter[168] = 5; enter[169] = 6;
            transfers.RememberEnter(0, enter); e.Run("sticktogether Target");
            var leave = e.Main.Frame(); Hex.True(BitConverter.ToUInt16(leave, 4) == 0x1392 && BitConverter.ToUInt32(leave, 14) == 2, "QA summon starts type2 source leave");
            Hex.True(e.Dungeon.Available == 0 && e.Caller.Available == 0, "QA native branch has no invented immediate destination or client reply");
            e.Run("sticktogether Target"); Hex.True(e.Main.Available == 0, "duplicate QA transfer refused while pending");
            var pending = transfers.Take(e.Target.Session.GameId, 0)!;
            Hex.True(pending.DestinationWorld == 13 && pending.Move.Channel == 321 && pending.Move.Direction == 123
                && pending.Move.EtcData.SequenceEqual(new byte[] { 4, 5, 6 }), "source context and latest saved caller channel/direction survive genuine handoff");
            enter[8] = 1; Hex.True(CrossWorldHandoff.QaTeleport(enter, e.Target.Session.GameId, 9781, 0, 0, 0, 0, 0) == null, "invalid live context cannot start QA transfer");
        });
    }
}
