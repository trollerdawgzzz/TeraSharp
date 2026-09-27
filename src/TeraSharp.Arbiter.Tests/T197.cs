// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static JsonDocument? T197Frames()
    {
        var path = FindRepoFile(Path.Combine("data", "t197", "frames.json"));
        if (path == null) { Skip.Because("tracked data/t197/frames.json is absent"); return null; }
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte[] T197Frame(JsonDocument data, string capture, int record)
        => Convert.FromHexString(data.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            (f.TryGetProperty("source_record", out var r) ? r.GetInt32() : f.GetProperty("n").GetInt32()) == record)
            .GetProperty("hex").GetString()!);

    [Test] public static void T197_unlock_apply_reopen_load_matches_retail_without_waiting_for_world_save()
    {
        using var data = T197Frames(); if (data == null) return;
        var expected = T197Frame(data, "cap_2man_b", 180);
        var blob = expected[19..];
        var learn = T197Frame(data, "cap_final2b", 3383);
        var learned = DbProxyHandlers.ReadCrestEntries(learn[6..]).Select(c => c.id).ToHashSet();
        var before = (byte[])blob.Clone();
        Array.Clear(before, CharacterStore.CrestPointBlobOffset, 4); // stale pre-1465 base points
        for (int i = 0; i < CharacterStore.CrestBlobSlots; i++)
        {
            int at = CharacterStore.CrestBlobOffset + i * CharacterStore.CrestBlobStride;
            if (learned.Contains(BitConverter.ToInt32(before, at))) Array.Clear(before, at, 4);
            before[at + 4] = 0;
        }
        string path = Path.Combine(Path.GetTempPath(), "T197-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                store.CreateCharacter(new CharacterRecord { AccountId = store.GetOrCreateAccount("t197").Id,
                    Name = "crest", Level = BitConverter.ToInt32(blob, StarterBlob.LevelOffset),
                    Money = BitConverter.ToInt64(blob, StarterBlob.MoneyOffset), WorldBlob = before });
                var h = FreshHandlers(store); h.PlayerIdForGameId = _ => 1; // only runtime identity differs between these captures
                foreach (var (request, reply) in new[] { (3379, 3380), (3383, 3384), (4746, 4747) })
                {
                    var req = T197Frame(data, "cap_final2b", request);
                    var (op, body) = RunHandler1(BitConverter.ToUInt16(req, 4), req[6..], store, h);
                    Hex.Eq(T180Frame(op, body), T197Frame(data, "cap_final2b", reply), $"retail {request}->{reply}");
                }
                // Simulate a delayed old snapshot arriving after the independently acknowledged SQL writes.
                store.SaveWorldBlob(1, before);
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var req = T197Frame(data, "cap_2man_b", 179);
                var replies = RunHandler(0x2711, req[6..], 2, store);
                Hex.Eq(T180Frame(replies[0].op, replies[0].body), expected,
                    "retail full15331-byte load after unlock/apply, process restart and stale World snapshot");
                Hex.True(store.GetCrestPoints(1) == (60, 0), "real point write persists independently; no guessed point default");
                var retailEnter = T197Frame(data, "cap_2man_b", 177);
                var entry = retailEnter[6..]; FreshHandlers(store).StampEnterWorldCrestPoints(entry);
                Hex.Eq(T180Frame(WorldBridge.OpPlayerEnter, entry), retailEnter,
                    "captured zero extra remains byte-exact after the point reload");

                // 1469 has no pair in these retail captures: native Arb062:5024–5048 / Arb031:1014–1068.
                var single = new byte[17]; BitConverter.GetBytes(197UL).CopyTo(single, 0);
                BitConverter.GetBytes(123u).CopyTo(single, 8); BitConverter.GetBytes(33000).CopyTo(single, 12); single[16] = 1;
                var h = FreshHandlers(store); h.PlayerIdForGameId = g => g == 197 ? 1 : 0;
                var ack = RunHandler1(0x1469, single, store, h);
                Hex.Eq(T180Frame(ack.op, ack.body), Convert.FromHexString("0B0000006A147B00000001"), "native-only one-crest apply ack");
                byte[] changed = (byte[])blob.Clone(); changed[CharacterStore.CrestBlobOffset + 4] = 1;
                byte[] actual = store.GetCharacter(1)!.WorldBlob!; store.StampCrests(1, actual);
                Hex.Eq(actual, changed, "single toggle changes only its useNow byte and preserves all other fields");
                h.PlayerIdForGameId = _ => 0;
                ack = RunHandler1(0x1469, single, store, h);
                Hex.True(ack.body[4] == 0, "unresolved user receives failure, never writes another character");
                var bad = T197Frame(data, "cap_instance1", 1249)[6..];
                BitConverter.GetBytes(uint.MaxValue).CopyTo(bad, 4);
                ack = RunHandler1(0x1467, bad, store, h);
                Hex.True(ack.body[12] == 0, "hostile list offset cannot clear an accepted crest set");
                h.PlayerIdForGameId = _ => 1;
                ack = RunHandler1(0x1467, T197Frame(data, "cap_instance1", 22700)[6..], store, h);
                Hex.Eq(T180Frame(ack.op, ack.body), T197Frame(data, "cap_instance1", 22701), "explicit empty list is a real clear");
                actual = store.GetCharacter(1)!.WorldBlob!; store.StampCrests(1, actual);
                for (int i = 0; i < CharacterStore.CrestBlobSlots; i++)
                    Hex.True(actual[CharacterStore.CrestBlobOffset + i * CharacterStore.CrestBlobStride + 4] == 0, "clear survives reload");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    [Test] public static void T197_native_extra_points_restore_on_every_entry_and_never_decrease()
    {
        using var data = T197Frames(); if (data == null) return;
        var retailEnter = T197Frame(data, "cap_2man_b", 177);
        var blob = T197Frame(data, "cap_2man_b", 180)[19..];
        string path = Path.Combine(Path.GetTempPath(), "T197-points-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                store.CreateCharacter(new CharacterRecord { AccountId = store.GetOrCreateAccount("t197").Id,
                    Name = "points", WorldBlob = blob });
                var unchanged = (byte[])blob.Clone(); store.StampCrests(1, unchanged);
                Hex.Eq(unchanged, blob, "unknown legacy SQL zero preserves saved base points");
                Hex.True(store.GetKnownCrestPoints(1) == null, "new character has no invented points");
                // Nonzero extra values are native-only (Arb031:1188–1213), not captured values.
                var h = FreshHandlers(store); h.PlayerIdForGameId = _ => 1;
                var write = T197Frame(data, "cap_final2b", 3379)[6..];
                BitConverter.GetBytes(7).CopyTo(write, 24);
                RunHandler1(0x1465, write, store, h);
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var h = FreshHandlers(store); h.PlayerIdForGameId = _ => 1;
                var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog()) { DbProxy = h };
                using var owner = new T192WorldPeer(bridge, 13, 13);
                foreach (int extra in new[] { 3, 11 })
                {
                    // Entry and cached type-2/retry payloads all pass through the same send seam.
                    var payload = retailEnter[6..];
                    if (extra == 11) BitConverter.GetBytes(2u).CopyTo(payload, 68);
                    var wanted = (byte[])payload.Clone();
                    BitConverter.GetBytes(extra == 3 ? 7 : 11).CopyTo(wanted, 161);
                    var write = T197Frame(data, "cap_final2b", 3379)[6..];
                    BitConverter.GetBytes(extra).CopyTo(write, 24);
                    RunHandler1(0x1465, write, store, h);
                    bridge.SendFrame(13, WorldBridge.OpPlayerEnter, payload);
                    Hex.Eq(owner.Frame(), T180Frame(WorldBridge.OpPlayerEnter, wanted),
                        "T197-PATCH: restore persisted extra; change no unrelated entry byte");
                }
                Hex.True(store.GetCrestPoints(1) == (60, 11), "extra increases but a smaller write cannot lower it");
                store.SetCrestPoints(1, 0, 0);
                var cleared = (byte[])blob.Clone(); store.StampCrests(1, cleared);
                var wantedBlob = (byte[])blob.Clone(); Array.Clear(wantedBlob, CharacterStore.CrestPointBlobOffset, 4);
                Hex.Eq(cleared, wantedBlob, "an explicit base-point zero is authoritative; no other blob field changes");
                Hex.True(store.GetCrestPoints(1) == (0, 11), "base may reset while earned extra remains");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    [Test] public static void T197_live_topo_fin_does_not_overwrite_world_crest_info()
    {
        using var data = T197Frames(); if (data == null) return;
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        using var store = GuildStore(1);
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        var previousStore = storeProperty.GetValue(null);
        ulong? previousGameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(1, out var old) ? old : null;
        try
        {
            storeProperty.SetValue(null, store);
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"C_LOAD_TOPO_FIN\":24916}}}");
            var defs = new DefinitionRegistry(QuietLog());
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            using var factory = LoggerFactory.Create(_ => { });
            var dispatcher = new PacketDispatcher(factory.CreateLogger<PacketDispatcher>());
            HandlerRegistry.RegisterAll(dispatcher, opcodes, defs, factory);
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var owner = new T192WorldPeer(bridge, 13, 13);
            T185Environment.SetWorld(bridge);
            using var client = new T185Client(defs, opcodes, QuietLog());
            client.Session.PlayerId = 1; client.Session.GameId = 19701; client.Session.CurrentWorldId = 13;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "crest" };
            client.Session.EnterWorld();
            var authoritative = T197Frame(data, "cap_instance1_client2", 12494);
            client.Session.Send(authoritative);
            Hex.Eq(client.Frame(), authoritative, "World's42 learned/19 applied/60 used frame is delivered intact");
            dispatcher.Dispatch(client.Session, T197Frame(data, "cap_instance1_client2", 13037));
            var ops = new List<ushort>();
            while (client.Available > 0) ops.Add(BitConverter.ToUInt16(client.Frame(), 2));
            Hex.True(ops.Count > 0 && !ops.Contains(ArbiterClientHandlers.S_CREST_INFO),
                "T197-PATCH.diff required: topo-fin must not inject captured13039's false all-clear frame");
            var worldOps = new List<ushort>();
            while (owner.Available > 0) worldOps.Add(BitConverter.ToUInt16(owner.Frame(), 4));
            Hex.True(worldOps.Contains(WorldBridge.OpLoadTopoFin), "normal World spawn notification still completes");
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, previousStore);
            if (previousGameId is ulong game) DbProxyHandlers.GameIdByPlayer[1] = game;
            else DbProxyHandlers.GameIdByPlayer.TryRemove(1, out _);
            File.Delete(map);
        }
    }
}
