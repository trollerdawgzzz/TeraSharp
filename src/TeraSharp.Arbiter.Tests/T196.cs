// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static JsonDocument? T196Frames()
    {
        string? path = FindRepoFile(Path.Combine("data", "t196", "frames.json"));
        if (path == null) { Skip.Because("data/t196/frames.json absent"); return null; }
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte[] T196Frame(JsonDocument frames, int record, ushort opcode)
        => Convert.FromHexString(frames.RootElement.GetProperty("cap_final2b").EnumerateArray().Single(f =>
            f.GetProperty("source_record").GetInt32() == record && f.GetProperty("op").GetInt32() == opcode)
            .GetProperty("hex").GetString()!);

    [Test] public static void T196_EP_reward_parcel_defined_fields_notify_and_claim_match_retail()
    {
        using var frames = T196Frames(); if (frames == null) return;
        using var retailRows = new T234Switch("0");  // T234 deletes a claimed system parcel; this test pins retail's kept row
        using var store = GuildStore(1);
        using var env = new T185Environment(null);
        string map = Path.GetTempFileName();
        ulong? previousGameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(1, out var old) ? old : null;
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            T185Environment.SetWorld(bridge);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()),
                OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            client.Session.PlayerId = 1; client.Session.GameId = 19601; client.Session.CurrentWorldId = 13;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "g1" };
            bridge.RegisterPlayer(client.Session);

            byte[] accomplish = T196Frame(frames, 6185, 0x2802)[6..];
            var (_, achievementAck) = RunHandler1(0x2802, accomplish, store);
            Hex.Eq(achievementAck, T196Frame(frames, 6186, 0x2803)[6..], "retail accomplishment acknowledgement");
            Hex.True(store.GetParcelsFor(1).Count == 0, "World requests the parcel after the accomplishment; no duplicate grant here");
            var (_, repeatedAck) = RunHandler1(0x2802, accomplish, store);
            Hex.True(BitConverter.ToUInt32(repeatedAck, 4) == 0, "repeat accomplishment is not rewarded again");

            byte[] request = T196Frame(frames, 6189, 0x1479)[6..];
            var (opcode, ack) = RunHandler1(0x1479, request, store);
            Hex.True(opcode == 0x147A, "system parcel acknowledgement");
            Hex.Eq(ack, T196Frame(frames, 6190, 0x147A)[6..], "cap_final2b:6190 entire payload");
            var notification = frames.RootElement.GetProperty("cap_final2_clients/capture_2026-09-22T08-58-07-796Z")
                .EnumerateArray().Single(f => f.GetProperty("n").GetInt32() == 1861).GetProperty("hex").GetString()!;
            Hex.Eq(client.Frame(), Convert.FromHexString(notification), "retail client1861: reward notification reaches the user on World13");
            Hex.True(store.CountParcelItems(1) == 1 && store.GetParcel(1)!.Message.Contains("1903001"), "attachment and localized text persist");

            // Pin only defined fields: the native records also leak stack bytes in unused
            // slot tails. Replaying those would not be a specification of an EP potion.
            byte[] expected = ParcelDbHandlers.Ref(T196Frame(frames, 28068, 0x277C)[6..], 0);
            Hex.True(SystemParcelAttachments.TryRead(request, out var items), "captured linked reward is valid");
            var (writer, title, message) = DbProxyHandlers.ReadSysParcelText(request);
            byte[] built = SystemParcelAttachments.BuildRecord(13, 1,
                ParcelDbHandlers.WStringAt(expected, 0x54, 37), writer, title, message, 0, items);
            Hex.Eq(built[..0xA8], expected[..0xA8], "sender, receiver, parcel id and system type");
            for (int slot = 0; slot < 5; slot++)
            {
                int at = 0xD8 + slot * 0x1B0;
                Hex.Eq(built.AsSpan(at, 0x104).ToArray(), expected.AsSpan(at, 0x104).ToArray(),
                    "retail SendItemInfo defined prefix: slot " + slot);
            }
            Hex.Eq(built.AsSpan(0x950, 8).ToArray(), expected.AsSpan(0x950, 8).ToArray(), "attached money");
            Hex.Eq(built.AsSpan(0x960, 130).ToArray(), expected.AsSpan(0x960, 130).ToArray(), "localized title");
            Hex.True(ParcelDbHandlers.WStringAt(built, 0x9E8, 501) == ParcelDbHandlers.WStringAt(expected, 0x9E8, 501), "localized message");

            // World asks for the full record, then submits its amount change and op37 marker.
            byte[] read = T196Frame(frames, 28067, 0x277B)[6..];
            BitConverter.GetBytes(1).CopyTo(read, ParcelDbHandlers.RecvReqParcelId);
            var (_, readReply) = RunHandler1(0x277B, read, store);
            Hex.Eq(readReply[..25], T196Frame(frames, 28068, 0x277C).AsSpan(6, 25).ToArray(), "full-record reply header");
            Hex.True(ParcelDbHandlers.Ref(readReply, 0).Length == 3544, "claim can read all five attachment slots and message");
            store.UpsertItem(10037, 1, 0, 20, 201100, 1);
            byte[] claim = T196Frame(frames, 28069, 0x277B)[6..];
            BitConverter.GetBytes(1).CopyTo(claim, ParcelDbHandlers.RecvReqParcelId);
            int atomStart = (int)BitConverter.ToUInt32(claim, 0) - 6;
            BitConverter.GetBytes(1).CopyTo(claim, atomStart + DbProxyHandlers.ItemAtomSize + WarehouseHandlers.AtomRecvParcelId);
            var (_, claimed) = RunHandler1(0x277B, claim, store);
            byte[] expectedAtoms = ParcelDbHandlers.Ref(T196Frame(frames, 28070, 0x277C)[6..], 8);
            BitConverter.GetBytes(1).CopyTo(expectedAtoms, DbProxyHandlers.ItemAtomSize + WarehouseHandlers.AtomRecvParcelId);
            Hex.Eq(ParcelDbHandlers.Ref(claimed, 8), expectedAtoms, "retail claim atoms byte-exact, including op37; parcel id normalized");
            Hex.True(store.GetItem(10037)!.Amount == 4 && store.GetParcel(1)!.IsRecved, "EP reset potions are delivered on claim");
            byte[] listed = ParcelDbHandlers.ServedParcelRecord(store, store.GetParcel(1)!);
            Hex.True(BitConverter.ToInt32(listed, 0xA8) == 2, "claimed system reward advertises native status2");
        }
        finally
        {
            File.Delete(map);
            if (previousGameId is ulong value) DbProxyHandlers.GameIdByPlayer[1] = value;
        }
    }

    [Test] public static void T196_reward_attachments_survive_store_restart()
    {
        using var frames = T196Frames(); if (frames == null) return;
        string path = Path.Combine(Path.GetTempPath(), "T196-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                long account = store.GetOrCreateAccount("t196").Id;
                store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "reward", Level = 70 });
                foreach (var f in frames.RootElement.GetProperty("cap_instance1").EnumerateArray()
                    .Where(f => f.GetProperty("op").GetInt32() == 0x1479))
                {
                    byte[] request = Convert.FromHexString(f.GetProperty("hex").GetString()!)[6..];
                    BitConverter.GetBytes(1).CopyTo(request, 24);
                    RunHandler1(0x1479, request, store);
                }
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var rows = store.GetParcelsFor(1);
                Hex.True(rows.Count == 4 && store.GetParcelCounts(1).Unread == 4, "all four EP rewards survive restart");
                int[] amounts = { 1, 1, 2, 3 };
                for (int i = 0; i < rows.Count; i++)
                {
                    byte[] record = ParcelDbHandlers.ServedParcelRecord(store, rows[i], full: true);
                    Hex.True(record.Length == 3544 && BitConverter.ToInt32(record, 0xE0) == 201100
                        && BitConverter.ToInt32(record, 0xE4) == amounts[i]
                        && store.CountParcelItems(rows[i].ParcelId) == 1,
                        "cap_instance1:139184 reward quantity " + amounts[i]);
                }
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Test] public static void T196_bad_system_parcel_refuses_without_creating_partial_mail()
    {
        using var frames = T196Frames(); if (frames == null) return;
        using var store = GuildStore(1);
        byte[] request = T196Frame(frames, 6189, 0x1479)[6..];
        var missingOwner = (byte[])request.Clone(); BitConverter.GetBytes(9999).CopyTo(missingOwner, 24);
        var brokenList = (byte[])request.Clone(); BitConverter.GetBytes(uint.MaxValue).CopyTo(brokenList, 4);
        foreach (byte[] malformed in new[] { missingOwner, brokenList, request[..25] })
        {
            var (op, body) = RunHandler1(0x1479, malformed, store);
            Hex.True(op == 0x147A && BitConverter.ToInt32(body, 4) == 1, "negative ack always completes the request");
        }
        Hex.True(store.GetParcelsFor(1).Count == 0 && store.GetParcelsFor(9999).Count == 0, "no empty reward mail or orphan recipient");
    }
}
