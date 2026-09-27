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
    [Test] public static void T201_native_UI_commands_emit_their_client_packets_and_preserve_state()
    {
        string path = Path.Combine(Path.GetTempPath(), "t201-ui-" + Guid.NewGuid() + ".db"), map = Path.GetTempFileName();
        using var env = new T185Environment(null);
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
                long account = store.GetOrCreateAccount("t201-ui").Id;
                int id = store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "ui", WorldBlob = new byte[512] });
                var s = client.Session; s.Account.Name = "t201-ui"; s.PlayerId = (uint)id; s.CurrentWorldId = 13;
                s.SelectedCharacter = new FakeCharacter { Id = (uint)id };
                void Execute(string text) => QaUiCommands.TryExecute(s, store, GmCommandParser.Parse(text)!, QuietLog());
                // Decompile-derived expected packets, not invented capture pins. Each writer is cited in QaUiCommands.
                var cases = new (string Command, string Hex)[] {
                    ("init_awesomium", "0400E5F5"),
                    ("ps_com_window ignored", "0400F57C"),
                    ("ps_vote_window", "04000B73"),
                    ("ps_guard_window", "0400788D"),
                    ("ps_reg_window", "12000EF30600400031003300390031000000"),
                    ("open_awesomium x", "0A00577D060078000000"),
                    ("set_awesomium_web_url x", "0A0058C5060078000000"),
                    ("set_awesomium_debug_mode off", "0500AEA100"),
                    ("set_awesomium_debug_mode OFF", "0500AEA101"),
                    ("versionInfoHide", "0D0067B70B0000000000000000"),
                    ("string x y", "1400975C0A0000000000400078003A0079000000"),
                    ("change_voice 259", "0800BB5C03010000"),
                };
                foreach (var test in cases) { Execute(test.Command); Hex.Eq(client.Frame(), Convert.FromHexString(test.Hex), test.Command); }
                foreach (string alias in new[] { "cc", "clientCommand" })
                {
                    Execute(alias + " go a b");
                    Hex.Eq(client.Frame(), Convert.FromHexString("2400D9C6020010000A0067006F00000010001A001600610000001A000000200062000000"), "native linked argument nodes: " + alias);
                    byte[] echo = client.Frame();
                    Hex.True(BitConverter.ToUInt16(echo, 2) == 0x994C
                        && System.Text.Encoding.Unicode.GetString(echo, 6, echo.Length - 8) == "clientCommand = go a b ", "native trailing-space echo");
                }
                Execute("observer_mode ON"); Hex.True(store.GetQaObserverType(id) == 3, "observer enum persists without fabricated packet");
                Execute("observer_mode invalid"); Hex.True(store.GetQaObserverType(id) == 3, "invalid observer mode does nothing");
                Execute("change_deco_ui 17");
                byte[] deco = Convert.FromHexString("0F0000008815210000001100000001");
                Hex.Eq(main.Frame(), deco, "native all-World contents update, main"); Hex.Eq(owner.Frame(), deco, "native all-World contents update, dungeon");
                Hex.Eq(QaUiCommands.BuildDecoUi(store), Convert.FromHexString("0800B35711000000"), "next lobby uses persisted selection");
                Hex.True(client.Available == 0, "observer/contents changes have no extra client push");
                Hex.True(s.SelectedCharacter.Appearance[1] == 3, "voice customization masks to one byte");
                store.SaveWorldBlob(id, Enumerable.Repeat((byte)0x55, 512).ToArray());
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var character = store.GetCharacter(1)!;
                Hex.True(character.Appearance[1] == 3 && character.WorldBlob![289] == 3 && character.WorldBlob[288] == 0x55
                    && character.WorldBlob[290] == 0x55, "reopen overlays only the SQL-authoritative voice byte after stale World save");
                Hex.True(store.GetQaObserverType(1) == 3 && store.GetQaDecoUi() == 17, "native persisted states survive restart");
                store.SetQaObserverType(1, -1); store.SetQaDecoUi(23);
                Hex.True(store.GetQaObserverType(1) == -1 && store.GetQaDecoUi() == 23, "off and replacement selection replace old state");
            }
            foreach (string name in QaUiCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised
                    && GmCommandHandlers.Classify(true, 5, GmCommandParser.Parse(name)) == GmDispatch.Local, "central operator gate: " + name);
        }
        finally
        {
            T185Environment.SetWorld(null); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(map);
        }
    }
}
