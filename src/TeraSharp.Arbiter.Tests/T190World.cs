// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T190_party_mirror_and_withdrawal_frames_reach_the_captured_worlds()
    {
        // cap_2man_b raw10299 and11532: control registrations link#26=World0, link#54=World13.
        byte[] main = Convert.FromHexString("1B0000008A1300F00A00000000000018000000FFFFFFFF07BC0500");
        byte[] dungeon = Convert.FromHexString("1B0000008A1300F00A00000D00000004000000FFFFFFFF07BC0500");
        Hex.True(WorldRegistration.Parse(main.AsSpan(6))?.WorldId == 0
            && WorldRegistration.Parse(dungeon.AsSpan(6))?.WorldId == 13,
            "the packet routes are identified by actual link registrations");
        const long partyId = 0x0AF0003000000002;
        // raw12571/12572 are the identical creation frame sent to both Worlds.
        // Its builder layout is separately pinned by T184f; this fixture checks routing,
        // preserving native name-capacity/alignment bytes as well as semantic fields.
        const string creation = "780100009E133800000040010000010000003000F00AF00A0000F00A00000100000002000000000000000135260000000000000000000000F00A0000010000000100F00A00800000460000000C00000003000000010000000100000064006F0062006200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000101879CAF0000000000000000CABA9A4402000000000000000000000000000000000133FD830100000000000000000000F00A0000EB0300000200F00A00800000460000000C0000000400000001000000010000004E0065007700000064006F0062006200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001010000000000000000000000C8A6EF430200000000000000000000000000000000010000000000000000000000000000";
        var frames = new[]
        {
            (Op: (ushort)0x139E, Body: Convert.FromHexString(creation)[6..],
                Full: creation, Worlds: new[] { 0, 13 }),
            // raw19036/19037: both matching flags are cleared on World0 before removal.
            (Op: (ushort)0x15CD, Body: PartyPackets.BuildAsChangeEventMatchingState(1, false),
                Full: "0B000000CD150100000000", Worlds: new[] { 0 }),
            (Op: (ushort)0x15CD, Body: PartyPackets.BuildAsChangeEventMatchingState(1003, false),
                Full: "0B000000CD15EB03000000", Worlds: new[] { 0 }),
            // raw19038/19039: removal to both Worlds; raw19048/19049: dismissal to both.
            (Op: (ushort)0x13A0, Body: PartyPackets.BuildDoRemovePartyMember(partyId, 2800, 1),
                Full: "16000000A013020000003000F00AF00A000001000000", Worlds: new[] { 0, 13 }),
            (Op: (ushort)0x13A1, Body: PartyPackets.BuildDoDismissParty(partyId),
                Full: "0E000000A113020000003000F00A", Worlds: new[] { 0, 13 }),
            // raw19061: withdrawal goes only to the departing user's World13.
            (Op: (ushort)0x13F5, Body: PartyPackets.BuildAsNotifyAboutSysPartyWithdrawal(2800, 1),
                Full: "0E000000F513F00A000001000000", Worlds: new[] { 13 }),
        };
        foreach (var frame in frames)
        {
            byte[] captured = Convert.FromHexString(frame.Full);
            Hex.True(BitConverter.ToUInt32(captured, 0) == captured.Length
                && BitConverter.ToUInt16(captured, 4) == frame.Op, "captured World frame header");
            Hex.Eq(frame.Body, captured[6..], $"cap_2man_b {frame.Op:X4} payload");
            var sent = new List<int>();
            Hex.True(PartyWiring.RouteWorldAction(frame.Op, frame.Body, world => world is 0 or 13,
                player => player == 1 ? 13 : null, (world, op, body) =>
                {
                    Hex.True(op == frame.Op, "routing preserves the captured opcode");
                    Hex.Eq(body, captured[6..], "routing preserves every captured payload byte");
                    sent.Add(world);
                }) && sent.SequenceEqual(frame.Worlds), "captured World destinations");
        }
        Hex.True(!PartyWiring.RouteWorldAction(0x13A0, frames[3].Body, _ => false, _ => 13,
            (_, _, _) => throw new Exception("unlinked World must not receive a broadcast")),
            "a broadcast with no registered World reports that nothing was sent");
    }

    [Test] public static void T190_clear_disarms_withdrawal_and_active_leave_penalty_follows_dismissal()
    {
        // cap_2man_b raw16357: DSA_NOTIFY_ABOUT_DUNGEON_CLEAR for first matched party/9781.
        // raw17290: New leaves that cleared party; raw17293-17296 have remove+dismiss, no13F5.
        // raw19035: dobb leaves second, uncleared party; remove+dismiss precede13F5 at19061.
        byte[] capturedClear = Convert.FromHexString("12000000F013010000003000F00A35260000");
        byte[] clearedLeave = Convert.FromHexString("1A0000009613F00A0000010000003000F00AF00A0000EB030000");
        byte[] activeLeave = Convert.FromHexString("1A0000009613F00A0000020000003000F00AF00A000001000000");
        foreach (bool cleared in new[] { true, false })
        {
            var pm = NewPartyManager();
            pm.Register(P(1, 1003, "New") with { WorldId = 13 });
            pm.Register(P(2, 1, "dobb") with { WorldId = 13 });
            // The two captured AS_CREATEs (12571 and18232) seat dobb first, then New.
            pm.FormMatchedParty(T138dMembers((1, MatchRole.Dps), (1003, MatchRole.Dps)), false, dungeonId: 9781);
            var party = pm.FindByMember(1003)!;
            if (cleared)
            {
                byte[] clearBody = capturedClear[6..];
                // The process-issued party id is state; all other captured fields stay intact.
                BitConverter.GetBytes(party.Id).CopyTo(clearBody, 0);
                var clear = pm.OnWorldFrame(0x13F0, clearBody);
                Hex.True(clear.Rejected == null && clear.IsEmpty && party.DungeonCleared
                    && !party.WithdrawalPenalty, "World owns the clear; Arbiter silently disarms departure penalty");
            }
            byte[] leaveBody = (cleared ? clearedLeave : activeLeave)[6..];
            BitConverter.GetBytes(party.Id).CopyTo(leaveBody, 4);
            var leave = pm.OnWorldFrame(0x1396, leaveBody);
            ushort[] expected = cleared ? new ushort[] { 0x15CD, 0x15CD, 0x13A0, 0x13A1 }
                : new ushort[] { 0x15CD, 0x15CD, 0x13A0, 0x13A1, 0x13F5 };
            Hex.True(leave.Rejected == null && leave.ToWorld.Select(w => w.Opcode).SequenceEqual(expected),
                cleared ? "cleared-party departure has no penalty" : "uncleared-party penalty follows full teardown");
            Hex.Eq(leave.ToWorld[0].Payload, "01 00 00 00 00", "raw17291/19036: first member clear");
            Hex.Eq(leave.ToWorld[1].Payload, "EB 03 00 00 00", "raw17292/19037: second member clear");
            if (!cleared)
                Hex.Eq(leave.ToWorld[^1].Payload, "F0 0A 00 00 01 00 00 00", "raw19061: departing dobb PDId");
        }
    }
}
