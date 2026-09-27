// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T184f_captured_solo_applicants_notify_client_and_world_state()
    {
        // Actual cap_2man bytes: client2 818->819/820/821 then client1 1229->1230/1231/1232.
        // Tap 1463 and 1838 are the 11-byte World pushes (strip their six-byte frame header).
        var captures = new[]
        {
            (Id: 1, Request: "37005FCE01000E0002001F0000000E000000352600000000000000000000001F002B0001000000010000002B0000000100000001000000",
             Pool: "310030C701000C00000000000C0000000100200035260000000000000000000020000000F00A0000010000000001000000",
             World: "0B000000CD150100000001"),
            (Id: 1003, Request: "37005FCE01000E0002001F0000000E000000352600000000000000000000001F002B00EB030000010000002B000000EB03000001000000",
             Pool: "310030C701000C00000000000C0000000100200035260000000000000000000020000000F00A0000EB0300000001000000",
             World: "0B000000CD15EB03000001"),
        };
        const string queued = "1A00AC8702000A0001010A0012005E08000012000000EB670100";
        string map = Path.GetTempFileName();
        MatchWiring.Reset();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(listener.LocalEndPoint!);
            using var peer = listener.Accept(); peer.ReceiveTimeout = 2000;
            using var session = new GameSession(client,
                new PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketDispatcher>.Instance),
                OpcodeTable.LoadFromFile(map, "376012"), new DefinitionRegistry(QuietLog()), 376012, 100, QuietLog());
            using var ev = T161bEvents((2142, "Dungeon", 9781), (92139, "Dungeon", 9781));
            var world = new List<(int Character, ushort Opcode, byte[] Payload)>();
            MatchWiring.PartyOf = s => new[] { new MatchQueueManager.Queuer(s.PlayerId, (int)s.PlayerId, 2, 70, 1) };
            MatchWiring.FormParty = (g, _) => g.Members.Select(q => q.CharacterId).ToArray();
            MatchWiring.SendWorld = (q, op, p) => world.Add((q.CharacterId, op, p));
            foreach (var cap in captures)
            {
                session.PlayerId = (uint)cap.Id;
                MatchWiring.OnMatchAdd(session, Convert.FromHexString(cap.Request).AsMemory(4));
                var expected = Convert.FromHexString("12000EF30600400032003100370033000000" + cap.Pool + queued);
                var received = new byte[expected.Length];
                int count = 0;
                while (count < received.Length)
                {
                    int got = peer.Receive(received, count, received.Length - count, SocketFlags.None);
                    Hex.True(got > 0, "matching connection remains open"); count += got;
                }
                Hex.Eq(received, expected, $"captured application message, pool and queued state for character {cap.Id}");
                var push = world.Last();
                Hex.True(push.Character == cap.Id && push.Opcode == 0x15CD, "send queued state for this member");
                Hex.Eq(push.Payload, Convert.FromHexString(cap.World).AsSpan(6).ToArray(), "captured UserDbId + IsMatching=1");
            }
            Hex.True(world.Count == 2, "both separately queued members update World once");

            // Cancellation is decompile-derived (no C_MATCH_DEL in cap_2man):
            // Arb076:15693-15700 -> Arb077:6149-6152 clears every removed application's member.
            MatchQueueManager.Reset(); world.Clear();
            MatchQueueManager.Add(1003, new[] { 9781 }, new[] {
                new MatchQueueManager.Queuer(1003, 1003, 2, 70, 1),
                new MatchQueueManager.Queuer(1, 1, 2, 70, 1) }, DateTimeOffset.UnixEpoch);
            session.PlayerId = 1;
            MatchWiring.OnMatchDel(session, ReadOnlyMemory<byte>.Empty);
            Hex.True(world.Count == 2 && world.All(p => p.Opcode == 0x15CD),
                "nonleader cancellation clears the whole application's World state");
            Hex.Eq(world[0].Payload, Convert.FromHexString("EB03000000"), "false layout pinned by cap_2man tap1924");
            Hex.Eq(world[1].Payload, Convert.FromHexString("0100000000"), "false layout pinned by cap_2man tap1928+0");
            MatchWiring.OnMatchDel(session, ReadOnlyMemory<byte>.Empty);
            Hex.True(world.Count == 2, "deleting an absent application does not add a World transition");
        }
        finally { MatchWiring.Reset(); File.Delete(map); }
    }

    [Test] public static void T184f_pending_relog_enter_and_party_withdrawal_do_not_repeat_completion()
    {
        // cap_2man_client2: FIN4356, lobby4506, selection4745, topo-fin5010; no repeated FIN.
        // client1 enters at4502; client2 later uses the NPC entry path, reaching topo9781 at5556.
        var now = DateTimeOffset.UnixEpoch.AddDays(200);
        using var sheet = new T184hDungeonSheet(9781, total: 2);
        MatchWiring.Reset();
        try
        {
            MatchWiring.Clock = () => now;
            var sent = new List<byte[]>();
            MatchWiring.Deliver = (_, frame) => sent.Add(frame);
            MatchWiring.FormParty = (g, _) => g.Members.Select(q => q.CharacterId).ToArray();
            foreach (int id in new[] { 1003, 1 })
                MatchQueueManager.Add((uint)id, new[] { 9781 },
                    new[] { new MatchQueueManager.Queuer((uint)id, id, 2, 70, 1) }, now);
            Hex.True(MatchWiring.TryFormAndFinish(9781, now) != null, "two-DPS fixture forms");
            Hex.Eq(MatchQueueManager.BuildFinInterPartyMatch(9781),
                Convert.FromHexString("10007064352600000000000000000000"), "cap_2man_client1 1285 / client2 914");
            Hex.True(!MatchWiring.OnLeftWorld(1, disconnected: false), "lobby leave preserves the standing entry");
            Hex.True(MatchWiring.TakeReoffer(0x80000AF00003, 1, now.AddSeconds(30)).Count == 0
                && MatchWiring.TakeReoffer(0x80000AF00004, 1, now.AddSeconds(40)).Count == 0,
                "new world entry never fabricates another match completion");
            Hex.True(!MatchWiring.OnDungeonEntered(1003, 7005), "another continent does not consume entry");
            Hex.True(MatchWiring.OnDungeonEntered(1003, 9781), "successful dungeon entry consumes only this member");
            Hex.True(MatchWiring.PendingFor(1003, now) == null && MatchWiring.PendingFor(1, now) != null,
                "member inside cannot get a second offer; waiting partner remains claimable");
            Hex.True(MatchWiring.ReofferFrames(1003, now).Count == 0, "no popup replay inside the dungeon");
            Hex.True(MatchWiring.OnLeftParty(1003) && MatchWiring.PendingCount == 0,
                "leaving the matched party clears its waiting member even when the leaver entered already");
            Hex.True(sent.Count == 0, "PartyManager owns withdrawal packets; pending cleanup adds none");
        }
        finally { MatchWiring.Reset(); }
    }
    [Test] public static void T184f_formation_preserves_item_level_and_original_application_size()
    {
        // Actual item-level IEEE754 bits in cap_2man's AS_CREATE members. Runtime input is
        // the saved SA_EQUIP_ITEM_LEVEL observation, never these fixture constants.
        var solo = new MatchQueueManager.Queuer(1003, 1003, 2, 70, 1,
            BitConverter.Int32BitsToSingle(unchecked((int)0x43EFA6C8)));
        var premadeA = new MatchQueueManager.Queuer(1, 1, 2, 70, 1,
            BitConverter.Int32BitsToSingle(unchecked((int)0x449ABACA)));
        var premadeB = new MatchQueueManager.Queuer(2, 2, 6, 70, 2, 470);
        var entries = new[]
        {
            new MatchQueueManager.Entry { Members = new[] { solo }, InstanceIds = new[] { 9781 } },
            new MatchQueueManager.Entry { Members = new[] { premadeA, premadeB }, InstanceIds = new[] { 9781 } },
        };
        var applicants = MatchQueueManager.WithChoices(new[] { solo, premadeA, premadeB },
            new[] { (1003, 1), (1, 1), (2, 2) });
        var group = new MatchQueueManager.FormedGroup(9781, entries, applicants, applicants,
            Array.Empty<MatchQueueManager.Queuer>(), new[] { MatchRole.Dps, MatchRole.Dps, MatchRole.Healer });
        var members = MatchWiring.MatchedMembersFor(group);
        Hex.True(members[0].IsSoloMatching && !members[1].IsSoloMatching && !members[2].IsSoloMatching,
            "solo is based on original application, while the completed roster contains three members");
        Hex.True(BitConverter.SingleToInt32Bits(members[0].TrueItemLevel) == unchecked((int)0x43EFA6C8)
            && BitConverter.SingleToInt32Bits(members[1].TrueItemLevel) == unchecked((int)0x449ABACA),
            "role selection and party formation preserve observed item-level bits");
    }
}
