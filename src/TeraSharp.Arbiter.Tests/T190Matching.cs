// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T190_real_lobby_pending_trace_has_no_completion_replay_or_early_expiry()
    {
        // cap_2man_b: first party forms at tap12571@0, 00:57:37.865Z.
        // Client1 FIN1470 -> lobby1719 -> select1899 -> topo-fin2163; no second FIN.
        // Client2 FIN1110 -> C_ENTER1386. Client1 later enters through NPC/contract,
        // reaching9781 at client2787 / tap14670@1357, 01:00:13.767Z (155.902s).
        var now = DateTimeOffset.Parse("2026-09-26T00:57:37.865Z");
        using var sheet = new T184hDungeonSheet(9781, total: 2);
        string? oldExpiry = Environment.GetEnvironmentVariable(MatchWiring.EntrySecondsVariable);
        MatchWiring.Reset();
        try
        {
            Environment.SetEnvironmentVariable(MatchWiring.EntrySecondsVariable, null);
            MatchWiring.Clock = () => now;
            var frames = new List<(int Character, byte[] Frame)>();
            MatchWiring.Deliver = (member, frame) => frames.Add((member.CharacterId, frame));
            MatchWiring.FormParty = (_, _) => Array.Empty<int>();
            // Actual C_MATCH_ADD client1 record1162 and client2 record994, through the
            // production request readers. Character class/level are server-side state.
            foreach (var request in new[]
            {
                (Id: 1003, Hex: "37005FCE01000E0002001F0000000E000000352600000000000000000000001F002B00EB030000010000002B000000EB03000001000000"),
                (Id: 1, Hex: "37005FCE01000E0002001F0000000E000000352600000000000000000000001F002B0001000000010000002B0000000100000001000000"),
            })
            {
                var body = Convert.FromHexString(request.Hex).AsMemory(4);
                var members = MatchQueueManager.WithChoices(
                    new[] { new MatchQueueManager.Queuer((uint)request.Id, request.Id, 2, 70) },
                    MatchQueueManager.ReadQueueChoices(body));
                MatchQueueManager.Add((uint)request.Id, MatchQueueManager.ReadInstanceIds(body).ToArray(), members, now);
            }
            Hex.True(MatchWiring.TryFormAndFinish(9781, now) != null, "captured two-DPS applications form");
            var completions = frames.Where(f => BitConverter.ToUInt16(f.Frame, 2) == 0x6470).ToArray();
            Hex.True(completions.Length == 2, "one completion per member");
            foreach (var completion in completions)
                Hex.Eq(completion.Frame, Convert.FromHexString("10007064352600000000000000000000"),
                    completion.Character == 1003 ? "client1 record1470" : "client2 record1110");
            frames.Clear();
            Hex.True(MatchWiring.OnDungeonEntered(1, 9781), "client2 admission consumes only its own pending entry");
            Hex.True(!MatchWiring.OnLeftWorld(1003, disconnected: false), "captured lobby return retains client1's entry");
            Hex.True(MatchWiring.TakeReoffer(0x80000AF00003, 1003, now.AddSeconds(40)).Count == 0,
                "reselected character gets no fabricated FIN or initial488-byte SYS");
            var admitted = DateTimeOffset.Parse("2026-09-26T01:00:13.767Z");
            Hex.True(MatchWiring.SweepPending(admitted) == 0 && MatchWiring.PendingFor(1003, admitted) != null,
                "the actual155.902-second pending interval remains valid; this does not prove expiry beyond300s");
            Hex.True(MatchWiring.OnDungeonEntered(1003, 9781) && MatchWiring.PendingCount == 0,
                "NPC-mediated successful admission consumes the same pending entry");
            Hex.True(!MatchWiring.OnLeftParty(1003) && frames.Count == 0,
                "later captured party withdrawal adds no matching-lifecycle cancellation once both entered");
        }
        finally
        {
            MatchWiring.Reset();
            Environment.SetEnvironmentVariable(MatchWiring.EntrySecondsVariable, oldExpiry);
        }
    }
}
