// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// ===================== T209b: the handshake burst, built instead of replayed =====================
//
// T23 shipped the burst as 63 captured payloads in data/handshake_burst.bin. T209b replaces the
// file with Protocol/InterServerDefinitions (50 defs, every field named from the retail Arbiter's
// own dump helpers) plus World/HandshakeBurst (the order and the non-zero values), so the repo
// carries no captured retail bytes for it - T139.
//
// The whole claim is one assertion: the built burst IS the capture, frame for frame, byte for
// byte. data/handshake_burst.bin stays as that test's evidence and can be removed from the repo
// at any time - the test then skips and nothing else notices.
public static partial class Tests
{
    /// <summary>
    /// T209b. All 63 frames, built from defs at the capture's own instant, are the captured
    /// bytes exactly - same opcodes, same order, same payloads, same 1082 bytes on the wire.
    /// </summary>
    [Test] public static void T209b_burst_built_from_defs_is_the_capture_byte_for_byte()
    {
        var burst = LoadHandshakeBurstOrSkip();
        if (burst == null) return;

        var built = HandshakeBurst.Build(DateTimeOffset.FromUnixTimeSeconds(BurstCapture0913Now),
            (ulong)BurstCapture0913Reset);

        Hex.True(built.Count == burst.Count && built.Count == HandshakeBurst.FrameCount,
            $"built {built.Count} frames against {burst.Count} captured, expected {HandshakeBurst.FrameCount}");
        for (int i = 0; i < built.Count; i++)
        {
            Hex.True(built[i].op == burst[i].Op,
                $"frame {i}: built 0x{built[i].op:X4}, captured 0x{burst[i].Op:X4}");
            Hex.Eq(built[i].payload, burst[i].Payload,
                $"frame {i} (0x{burst[i].Op:X4}, capture seq {burst[i].Seq}) must be byte-identical to the capture");
        }
        Hex.True(built.Sum(f => f.payload.Length + 6) == 1082, "the burst is 1082 frame bytes on the wire");
    }

    /// <summary>
    /// T209b. The two u64s that move with the clock still move, and nothing else does: rebuilt at
    /// the 09-12 capture's instant every other frame is byte-identical to the 09-13 build.
    /// </summary>
    [Test] public static void T209b_only_the_two_live_u64s_move_with_the_clock()
    {
        var a = HandshakeBurst.Build(DateTimeOffset.FromUnixTimeSeconds(BurstCapture0913Now),
            (ulong)BurstCapture0913Reset);
        var b = HandshakeBurst.Build(DateTimeOffset.FromUnixTimeSeconds(BurstCapture0912Now),
            (ulong)BurstCapture0912Reset);

        int sync = 0, reset = 0, identical = 0;
        for (int i = 0; i < a.Count; i++)
        {
            var (op, payload) = b[i];
            if (op == DbProxyHandlers.AS_SYNC_DATE_TIME)
            {
                Hex.True(BitConverter.ToUInt64(payload, DbProxyHandlers.SyncDateTimeOffset) == (ulong)BurstCapture0912Now,
                    "0x15BD payload+0 is the live unix time");
                Hex.Eq(payload[8..], a[i].payload[8..], "0x15BD: only that u64 may change");
                sync++;
            }
            else if (op == DbProxyHandlers.AS_SET_DARK_RIFT_DAILY_COMPLETED)
            {
                Hex.True(BitConverter.ToUInt64(payload, DbProxyHandlers.DarkRiftResetTimeOffset) == (ulong)BurstCapture0912Reset,
                    "0x14D1 payload+8 is that day's daily reset");
                Hex.Eq(payload[..8], a[i].payload[..8], "0x14D1: the list header must not change");
                reset++;
            }
            else
            {
                Hex.Eq(payload, a[i].payload, $"0x{op:X4} has no live field and must not move");
                identical++;
            }
        }
        Hex.True(sync == 1 && reset == 1 && identical == HandshakeBurst.FrameCount - 2,
            $"exactly one live 0x15BD and one live 0x14D1, {identical} frames fixed");
    }

    /// <summary>
    /// T209b. Nothing at runtime reads data/handshake_burst.bin any more: the burst builds without
    /// it and --selftest checks the BUILD, so an absent capture file is no longer a bad deploy.
    /// </summary>
    [Test] public static void T209b_burst_and_selftest_need_no_capture_file()
    {
        Hex.True(InterServerDefinitions.Count == 50,
            $"50 inter-server defs cover the burst's 50 distinct opcodes, not {InterServerDefinitions.Count}");
        Hex.True(HandshakeBurst.Build(DateTimeOffset.UtcNow).Count == HandshakeBurst.FrameCount,
            "the burst builds at any instant with no file present");

        var build = SelfTest.CheckHandshakeBurstBuild();
        Hex.True(build.Pass, "--selftest checks that the burst builds: " + build.Detail);

        var missing = SelfTest.CheckHandshakeBurst(
            Path.Combine(Path.GetTempPath(), "no_such_handshake_burst.bin"), required: false);
        Hex.True(missing.Pass, "an absent capture file is not a failed deploy: " + missing.Detail);
    }

    /// <summary>
    /// T209b. The framing mode is the whole difference between a client packet and a tunnel frame:
    /// the same def writes u16 offsets from base 4 for the client and u32 offsets from base 6 for
    /// the World bridge. Both empty-list forms the burst relies on fall straight out of that -
    /// an empty bytes field points at the end of the fixed part with length 0, an empty array is
    /// count 0 / offset 0.
    /// </summary>
    [Test] public static void T209b_framing_mode_is_the_whole_difference()
    {
        var none = new Dictionary<string, object>();

        // bytes DailyCompletedList + int64 LastResetDateTime: fixed part 8+8 inter, 4+8 client.
        var darkRift = InterServerDefinitions.Def("AS_SET_DARK_RIFT_DAILY_COMPLETED");
        Hex.Eq(new DefinitionWriter(PacketFraming.InterServer).Write(darkRift, none),
            Convert.FromHexString("16000000000000000000000000000000"),
            "0x14D1 empty: u32 offset 0x16 = 6 + 16, u32 length 0, then the u64");
        Hex.Eq(new DefinitionWriter(PacketFraming.Client).Write(darkRift, none),
            Convert.FromHexString("100000000000000000000000"),
            "the same def in client framing: u16 offset 0x10 = 4 + 12, u16 length 0");

        // array<int32> CostList: count and offset both zero when the list is empty.
        var cost = InterServerDefinitions.Def("AS_GUILD_WAR_ADMIN_MAINTAINCOST");
        Hex.Eq(new DefinitionWriter(PacketFraming.InterServer).Write(cost, none), new byte[8],
            "an empty inter-server array is eight zero bytes");
        Hex.Eq(new DefinitionWriter(PacketFraming.Client).Write(cost, none), new byte[4],
            "and four in client framing");

        Hex.True(InterServerDefinitions.Opcode("DBS_TBA_UPDATE_ROTATION") == 0x29E2
            && InterServerDefinitions.Name(0x157F) == "AS_CONTENTS_ON_OFF_LIST",
            "the name<->opcode map goes both ways");
    }
}
