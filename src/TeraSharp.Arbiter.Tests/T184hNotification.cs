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
    [Test] public static void T184h_battleground_and_refused_application_do_not_send_dungeon_notification()
    {
        // Positive bytes and order are pinned by T184f's two cap_2man applications
        // (client2:819 and client1:1230); T184_queue_push covers every party member.
        // Native Arb076:14928-14944 / 15123-15136 only selects @2173 for type0.
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
                OpcodeTable.LoadFromFile(map, "376012"), new DefinitionRegistry(QuietLog()), 376012, 100, QuietLog())
                { PlayerId = 1 };
            T157TestSheet();
            using var events = T161bEvents((5001, "BattleField", 10));
            MatchWiring.PartyOf = _ => new[] { new MatchQueueManager.Queuer(1, 1, 2, 70, 1) };
            MatchWiring.SendWorld = (_, _, _) => { };

            // cap_2man client2:818 request with only dungeon9781 replaced by offered BG10.
            var request = Convert.FromHexString("37005FCE01000E0002001F0000000E000000352600000000000000000000001F002B0001000000010000002B0000000100000001000000");
            BitConverter.GetBytes(10).CopyTo(request, 18);
            MatchWiring.OnClientPacket(session, MatchQueueManager.C_MATCH_ADD, request.AsMemory(4));
            Hex.True(MatchQueueManager.FindForPlayer(1)?.Wants(10) == true, "battleground application was accepted");
            var pool = ReadFrame();
            Hex.True(BitConverter.ToUInt16(pool, 2) == 0xC730, "accepted BG starts with pool, without dungeon @2173");
            Hex.True(BitConverter.ToUInt16(ReadFrame(), 2) == 0x87AC, "BG queued state follows immediately");

            MatchWiring.OnClientPacket(session, MatchQueueManager.C_MATCH_ADD, ReadOnlyMemory<byte>.Empty);
            Hex.Eq(ReadFrame(), Convert.FromHexString("0C0056D7F1D8FFFF02000000"),
                "empty/refused application sends only cancellation, without dungeon admission notification");
            Hex.True(peer.Available == 0, "no trailing notification after BG or refusal");

            byte[] ReadFrame()
            {
                var header = new byte[4];
                Receive(header, 0);
                var frame = new byte[BitConverter.ToUInt16(header, 0)];
                header.CopyTo(frame, 0);
                Receive(frame, 4);
                return frame;
            }
            void Receive(byte[] bytes, int offset)
            {
                while (offset < bytes.Length)
                {
                    int count = peer.Receive(bytes, offset, bytes.Length - offset, SocketFlags.None);
                    Hex.True(count > 0, "application response remains connected"); offset += count;
                }
            }
        }
        finally { MatchWiring.Reset(); BattleFieldSheet.ResetForTest(); File.Delete(map); }
    }
}
