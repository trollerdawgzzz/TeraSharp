// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T217 - /@makeitem creates nothing, and 0x161E is not why.
//
// 0x161E is SA_GIVE_FIELD_EVENT_CLEAR_REWARD (Arb_part_003.c:5812). Its handler
// (FUN_140726070) guards on len < 0x12, reads [u32 offset @6][u32 byteLength @10]
// [u32 fieldEventId @14] plus a vector of 16-byte rows, hands them to a FieldDataSheet holder
// and returns 1 with no packet writer anywhere in it. It is one-way, and it is periodic:
//
//   cap_makeitem          04:15:37 04:22:57 04:30:17 04:37:37 04:44:57   7m20s apart
//   arbiter-makeitem2.log 21:59:57 22:07:16 22:14:37                     7m19s / 7m21s apart
//
// The first two in the failing log arrive BEFORE any character logged in (22:08:12), so it
// cannot be an answer to a GM command. It is not a create and it is not a pre-notice.
//
// The real create pair is unchanged: A->W 0x2829 AS_ADMIN_COMMAND carrying the command string,
// then W->A 0x2768 SDB_ITEM_SINGLE one millisecond later (cap_makeitem 15690 -> 15691, and
// 15151 -> 15152). What went wrong is that the 0x2829 never left: SendToWorld returns false on a
// closed gate and ForwardToWorld discarded it.
// =============================================================================================
public static partial class Tests
{
    /// <summary>cap_makeitem 4587 / 7814 - the whole frame, both payloads the tick alternates.</summary>
    static readonly byte[] T217FieldEventTick =
        Hex.B("12 00 00 00 00 00 00 00 79 93 2C 04");

    /// <summary>
    /// T217. 0x161E is one-way and quiet: it must never be answered and never be logged as a
    /// missing reply, because nothing is missing.
    /// </summary>
    [Test] public static void T217_the_field_event_tick_is_one_way_and_quiet()
    {
        Hex.True(T217FieldEventTick.Length == 12,
            "the captured frame is 18 bytes - 6 of header and these 12, the empty-vector form");
        Hex.True(BitConverter.ToUInt32(T217FieldEventTick, 0) == 0x12,
            "payload+0 is the 0x12 the handler's own length guard tests against");
        Hex.True(WorldReplayTable.OneWayFromWorld.Contains((ushort)0x161E),
            "SA_GIVE_FIELD_EVENT_CLEAR_REWARD is sealed one-way - its handler has no packet writer");
        Hex.True(WorldReplayTable.LogsAtDebug(0x161E),
            "and quiet, because it arrives every 7m20s whether anyone is playing or not");
    }

    /// <summary>
    /// T217. The regression: a GM command that cannot be forwarded used to be accepted in the log
    /// and then dropped. Every closed gate now names itself.
    /// </summary>
    [Test] public static void T217_a_command_that_cannot_reach_world_says_so()
    {
        Hex.True(GmCommandHandlers.ForwardBlockedReason(null) == "no session",
            "no session is named");

        var definitions = new TeraSharp.Arbiter.Protocol.DefinitionRegistry(QuietLog());
        var opcodes = TeraSharp.Arbiter.Protocol.OpcodeTable.LoadFromFile(T217OpcodeMap(), "376012");
        using var client = new T185Client(definitions, opcodes, QuietLog());

        // No World bridge in a test process - the first gate, and the one that is silent in
        // production when the link drops between the command and the send.
        string reason = GmCommandHandlers.ForwardBlockedReason(client.Session);
        Hex.True(reason.Length > 0 && reason != "the World bridge refused the send",
            "a real gate is named rather than the catch-all: " + reason);

        client.Session.SelectedCharacter = new FakeCharacter { Id = 9, Name = "caludesucks" };
        Hex.True(GmCommandHandlers.ForwardBlockedReason(client.Session) != "no character selected",
            "and it moves on once the earlier gate is satisfied");
    }

    /// <summary>
    /// T217b. The frame the forward puts on the wire, against both captures. cap_makeitem3 1295
    /// (the run that "lost" the command) and cap_makeitem 15690 (the run that worked) are the SAME
    /// 52 bytes apart from the player id - 9 against 10 - so nothing was malformed and nothing was
    /// dropped: A-&gt;W#1 05:48:10.871 carried it, and that frame is simply the last one in the tap.
    /// </summary>
    [Test] public static void T217b_the_forwarded_admin_command_is_the_captured_frame()
    {
        // cap_makeitem3 1295, A->W#1 05:48:10.871, 0x2829, 52 B frame = 46 B payload.
        byte[] failing = Hex.B(
            "12 00 00 00 09 00 00 00 01 00 00 00 6D 00 61 00 6B 00 65 00 69 00 74 00 65 00 6D 00 "
            + "20 00 38 00 38 00 33 00 38 00 34 00 20 00 31 00 00 00");
        // cap_makeitem 15690, A->W#1 04:46:18.113 - the one World answered 1 ms later with 0x2768.
        byte[] working = Hex.B(
            "12 00 00 00 0A 00 00 00 01 00 00 00 6D 00 61 00 6B 00 65 00 69 00 74 00 65 00 6D 00 "
            + "20 00 38 00 38 00 33 00 38 00 34 00 20 00 31 00 00 00");

        Hex.Eq(GmCommandHandlers.BuildWorldForward(9, GmCommandHandlers.BypassModeWorld, "makeitem 88384 1"),
            failing, "cap_makeitem3 1295 byte for byte - the frame that supposedly never left");
        Hex.Eq(GmCommandHandlers.BuildWorldForward(10, GmCommandHandlers.BypassModeWorld, "makeitem 88384 1"),
            working, "cap_makeitem 15690 byte for byte - the one World answered");

        Hex.True(failing.Length == working.Length, "same length");
        int differing = 0;
        for (int i = 0; i < failing.Length; i++) if (failing[i] != working[i]) differing++;
        Hex.True(differing == 1 && failing[4] != working[4],
            "and they differ in exactly one byte: the player id at payload+4");
        Hex.True(BitConverter.ToInt32(failing, 0) == 18,
            "payload+0 is 18 - where the command string starts, frame-relative");
    }

    /// <summary>A one-opcode map: the forward path is tested by its gates, not by the name table.</summary>
    static string T217OpcodeMap()
    {
        string path = Path.Combine(Path.GetTempPath(), "t217-opcodes-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"maps\":{\"376012\":{}}}");
        return path;
    }
}
