// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T201Command(string text) => new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray();

    [Test] public static void T201_complete_native_registry_is_operator_gated_without_documentation()
    {
        string? path = FixtureOrSkip(Path.Combine("data", "t201", "native-commands.json"), "T201 native-commands.json");
        if (path is null) return;
        using var rows = JsonDocument.Parse(File.ReadAllText(path));
        var names = rows.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Hex.True(rows.RootElement.GetArrayLength() == 319 && names.Count == 318 && names.SetEquals(NativeQaCommands.Names),
            "compiled registry covers every native registration including PE-resolved aliases raid/cc");
        var oldArbiter = GmCommandCatalog.ArbiterCommands.ToArray(); var oldWorld = GmCommandCatalog.WorldCommands.ToArray();
        try
        {
            GmCommandCatalog.Set(Array.Empty<string>(), Array.Empty<string>());
            foreach (string name in names.Concat(new[] { "BOT_future", "Shutdown_now", "crash_world", "enchantitemsuccess", "apm_ask", "match_battle_field" }))
            {
                var command = GmCommandParser.Parse(name);
                Hex.True(GmCommandHandlers.Classify(false, 5, command) == GmDispatch.NoUser
                    && GmCommandHandlers.Classify(true, 0, command) == GmDispatch.NotAuthorised, "every QA route shares the authorization gate: " + name);
                var kind = GmCommandHandlers.Classify(true, 5, command);
                if (GmCommandHandlers.IsDenied(name)) Hex.True(kind == GmDispatch.Denied, "explicit deny survives empty catalogues: " + name);
                else if (names.Contains(name)) Hex.True(kind is GmDispatch.Local or GmDispatch.NotApplicable,
                    "every native name is implemented or explicitly unavailable; none can be unfinished or fall through to World: " + name);
                else Hex.True(kind == GmDispatch.ForwardToWorld, "native World command retains per-user forwarding: " + name);
            }
        }
        finally { GmCommandCatalog.Set(oldArbiter, oldWorld); }
    }

    [Test] public static void T201_denied_commands_never_forward_and_captured_enchant_follows_current_World()
    {
        using var env = new T185Environment(null);
        string map = Path.GetTempFileName();
        var property = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? prior = property.GetValue(null); property.SetValue(null, null);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("C_OP_COMMAND", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0); using var owner = new T192WorldPeer(bridge, 13, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            var s = client.Session; s.PlayerId = 2; s.SelectedCharacter = new FakeCharacter { Id = 2, Name = "T201" };
            s.Account.Name = "t201-operator"; s.EnterWorld(); s.CurrentWorldId = 13;
            var handler = new GmCommandHandlers(QuietLog());
            T181WithOperators(s.Account.Name, () =>
            {
                foreach (string name in new[] { "BOT_recvmsg", "bot_check", "BOT_new_name", "ps_im_king", "ps_vote_count", "crash", "crash_arbiter", "dbg_break", "shutdown", "shutdown_world", "reset_bf_result", "reset_all_phaselevel" })
                {
                    handler.OnAdminCommand(s, T201Command(name));
                    Hex.True(BitConverter.ToUInt16(client.Frame(), 2) == 0x994C, "operator receives explicit policy refusal");
                    handler.OnOpCommand(s, T201Command(name)); client.Frame();
                    Hex.True(main.Available == 0 && owner.Available == 0, "neither command entrypoint leaks denied input: " + name);
                }
                // cap_final2b_client2:692 supplies the complete C_ADMIN bytes. The AS header/layout
                // is native Arb067:6890-6903; user2 is this live test's identity.
                byte[] captured = Convert.FromHexString("30005CA4060065006E006300680061006E0074006900740065006D007300750063006300650073007300200031000000");
                handler.OnAdminCommand(s, captured[4..]);
                byte[] expected = Convert.FromHexString("3C0000002928120000000200000001000000").Concat(captured[6..]).ToArray();
                Hex.Eq(owner.Frame(), expected, "captured World-owned enchant command follows CurrentWorldId13");
                Hex.True(main.Available == 0, "no main-link fallback");
            });
            T181WithOperators(null, () => { handler.OnAdminCommand(s, T201Command("clear_inven")); handler.OnOpCommand(s, T201Command("ps_im_king")); });
            Hex.True(client.Available == 0 && main.Available == 0 && owner.Available == 0, "ordinary accounts receive no QA effect or information");
            using var store = StoreWithTwoAccounts();
            RunHandler(0x13E8, new byte[24], 0, store);
            Hex.True(DbProxyHandlers.IsHandledRequest(0x13E8) && !WorldReplayTable.OneWayFromWorld.Contains(0x13E8),
                "native local tournament path is explicitly consumed; no duplicate replay seal or invented1518");
        }
        finally { T185Environment.SetWorld(null); property.SetValue(null, prior); File.Delete(map); }
    }

    [Test] public static void T201_general_QA_frames_and_persistent_clears_survive_reopen()
    {
        string path = Path.Combine(Path.GetTempPath(), "t201-general-" + Guid.NewGuid() + ".db"), map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        QaGeneralCommands.ResetForTests();
        ulong? previousGameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(1, out var oldGameId) ? oldGameId : null;
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0); using var owner = new T192WorldPeer(bridge, 13, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            using (var store = new CharacterStore(path, QuietLog()))
            {
                long account = store.GetOrCreateAccount("t201-general").Id;
                int id = store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "first", Money = 777 });
                int other = store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "second" });
                Hex.True(id == 1, "clear regression covers the old captured-player static fallback");
                var s = client.Session; s.Account.Name = "t201-general"; s.PlayerId = (uint)id;
                s.SelectedCharacter = new FakeCharacter { Id = (uint)id }; s.EnterWorld(); s.CurrentWorldId = 13;
                void Execute(string command) => QaGeneralCommands.TryExecute(s, store, GmCommandParser.Parse(command)!, QuietLog());
                store.UpsertItem(100, id, 0, 0, 800, 3, null);
                store.UpsertItem(101, id, 14, 1, 801, 1, null);
                store.UpsertItem(102, id, 1, 0, 802, 9, null);
                store.UpsertItem(103, other, 0, 0, 803, 1, null);
                byte[] records = BagItems.BuildPayload(store.GetInventoryItems(id), 0, id)[13..];
                Execute("refresh_inven");
                var expected = Convert.FromHexString("4A04000008281A00000030040000010000000903000000000000").Concat(records).ToArray();
                Hex.Eq(owner.Frame(), expected, "Arb029:16441: refresh has both536B records, user1 and money777");
                Execute("check_simple_tip 1"); Execute("check_simple_tip 1");
                Hex.True(store.GetTutorialTipCounts(id).Single() == (1, 2), "native check increments the popup count");
                Execute("clear_simple_tip"); Execute("clear_inven");
                Hex.Eq(owner.Frame(), Convert.FromHexString("0B000000E5270100000000"), "Arb028:9086: ClearInven(false)");
                Hex.True(store.GetItem(100) == null && store.GetItem(101) == null && store.GetItem(102) != null
                    && store.GetItem(103) != null && store.GetCharacterMoney(id) == 777, "clear preserves warehouse, other user and money");
                Execute("set_go ON ignored");
                Hex.Eq(owner.Frame(), Convert.FromHexString("0E00000078150100000005000000"), "Arb044:3613 per-character GO5"); client.Frame();
                Hex.True(store.GetCharacterAdminLevel(id) == 5 && store.GetCharacterAdminLevel(other) == null
                    && store.GetAdminLevel(account) == 0, "set_go persists only the calling user");
                Execute("set_pcbang on"); Hex.Eq(owner.Frame(), Convert.FromHexString("0B000000B5140100000001"), "native PC-bang transition");
                Execute("set_pcbang on"); Hex.True(owner.Available == 0, "same PC-bang value does not send again");
                Execute("set_new_member on"); Hex.Eq(owner.Frame(), Convert.FromHexString("0B000000B4140100000001"), "native new-member toggle");
                var entry = Enumerable.Repeat((byte)0x77, 183).ToArray(); BitConverter.GetBytes(id).CopyTo(entry, 32);
                var stamped = (byte[])entry.Clone(); QaGeneralCommands.StampEnterWorldFlags(stamped, store);
                entry[92] = entry[93] = 1; Hex.Eq(stamped, entry, "only known account flags change across later World entry");
                Execute("usage"); Hex.True(owner.Available == 0 && main.Available == 0 && client.Available == 0, "native no-op emits nothing and every live change stayed on World13");
            }
            using var reopened = new CharacterStore(path, QuietLog());
            var tips = RunHandler1(0x2872, Convert.FromHexString("0900000001000000"), reopened);
            Hex.Eq(tips.body, DbProxyHandlers.BuildDbs2873(Array.Empty<int>(), 9), "explicitly cleared tip list cannot resurrect captured-player data");
            var inventory = RunHandler(0x27A2, Convert.FromHexString("0A00000001000000"), 2, reopened);
            Hex.Eq(inventory[1].body, BagItems.BuildPayload(Array.Empty<CharacterStore.ItemRow>(), 10, 1),
                "explicit empty bag survives reopen without a starter fixture or reseeding");
            Hex.True(reopened.GetCharacterAdminLevel(1) == 5, "per-character GO survives reopen");
        }
        finally
        {
            T185Environment.SetWorld(null); QaGeneralCommands.ResetForTests();
            if (previousGameId is ulong old) DbProxyHandlers.GameIdByPlayer[1] = old;
            else DbProxyHandlers.GameIdByPlayer.TryRemove(1, out _);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path); File.Delete(map);
        }
    }
}
