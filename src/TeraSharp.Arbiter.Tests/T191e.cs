// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T191e - the WASD guide survives T191d because the byte is honest, not because it is missing.
//
// cap_wasd2_client is caludesucks (id 9, operator) logging in twice on the T219c build, after
// /api/reset-client-settings. Every check the brief listed passes:
//
//   S_ADMIN_GM_SKILL   sent, in its slot, on BOTH entries: 80 -> 81 and 1557 -> 1558, immediately
//                      before the tunnelled S_LOAD_TOPO. T191d works.
//   the enabled byte   00 - and correct: IsGmInvisible follows World's SDB_USER_VAPORIZED and
//                      World has this GM visible.
//   tip checks         the client sends no C_SIMPLE_TIP_REPEAT_CHECK at all (retail's
//                      cap_final_gm_client2 has 22), so there is nothing to answer.
//   user settings      login 1 is served 8 B (the reset), the client saves 1063 B then 1133 B;
//                      login 2 is served 1133 B, body byte-identical to that last save, and
//                      nowhere near the 9000 B ceiling. T191c works.
//
// One byte differs from retail, the last one in the frame:
//
//     retail  cap_final_gm_client2  99    09 00 BE 64 00 00 00 00 01
//     ours    cap_wasd2_client      80    09 00 BE 64 00 00 00 00 00
//                                                              ^^ payload+4, frame offset 8
//
// Retail's 01 is not a different opinion about the same state - it IS a different state. T152
// recorded that World vaporizes a GM at spawn at adminLevel 1 (cap_final 495: SDB_USER_VAPORIZED
// 01 00 00 00 01, then 496 carries the 01 to the client) and never at the 5 we send (cap_bag,
// cap_skills2). Retail's GM entered the world vaporized, which is also why the tool's first
// toggle at 542 turns invisibility OFF rather than on.
//
// So the fix is not to write 01 into a frame World disagrees with - that is exactly the T152
// desync - but to ask World for the same toggle the panel asks for, once per world entry, at
// S_SPAWN_ME. World answers with SDB_USER_VAPORIZED and its own tunnelled S_ADMIN_GM_SKILL 01:
// the exchange the operator performs by hand today, which is what clears the prompt.
// =============================================================================================
public static partial class Tests
{
    /// <summary>cap_wasd2_client 1818 - the frame the spawn-time request is anchored on.</summary>
    static readonly byte[] T191eSpawnMe =
        Hex.B("1C 00 65 83 05 00 F0 0A 00 80 00 00 7C 4A 89 C4 C4 75 E0 45 00 C0 07 45 7C D8 01 00");

    /// <summary>
    /// The byte, named. Our capture's enter-world frame differs from retail's in exactly one
    /// position, and that position is the enabled flag the builder writes.
    /// </summary>
    [Test] public static void T191e_the_two_captures_differ_in_one_byte_and_it_is_the_enabled_flag()
    {
        var ours = Hex.B("09 00 BE 64 00 00 00 00 00");     // cap_wasd2_client 80 and 1557
        var retail = Hex.B("09 00 BE 64 00 00 00 00 01");   // cap_final_gm_client2 99 and 2445
        Hex.True(ours.Length == retail.Length && ours.Length == 9, "both are the 9-byte frame");
        var differ = Enumerable.Range(0, 9).Where(i => ours[i] != retail[i]).ToArray();
        Hex.True(differ.Length == 1 && differ[0] == 8,
            $"exactly one byte differs and it is frame offset 8 (payload+4), got [{string.Join(",", differ)}]");
        Hex.Eq(ArbiterClientHandlers.BuildAdminGmSkill(ArbiterClientHandlers.GmSkillInvisible, on: false), ours,
            "ours is what the builder writes for a visible GM");
        Hex.Eq(ArbiterClientHandlers.BuildAdminGmSkill(ArbiterClientHandlers.GmSkillInvisible, on: true), retail,
            "retail's is the same builder with the vaporized state");
    }

    /// <summary>
    /// The one-shot: the spawn-time request is taken once per world entry, separately from the
    /// S_LOAD_TOPO push - that one is already spent by the time S_SPAWN_ME arrives.
    /// </summary>
    [Test] public static void T191e_the_spawn_vaporize_is_its_own_once_per_entry_shot()
    {
        const int player = 9109;
        try
        {
            ArbiterClientHandlers.ResetGmSkillPush(player);
            Hex.True(ArbiterClientHandlers.TryTakeGmSkillPush(player), "the topo push is armed");
            Hex.True(ArbiterClientHandlers.TryTakeGmSpawnVaporize(player),
                "and the spawn request is a separate shot, not spent by the topo push");
            Hex.True(!ArbiterClientHandlers.TryTakeGmSpawnVaporize(player), "taken once per entry");

            ArbiterClientHandlers.ResetGmSkillPush(player);
            Hex.True(ArbiterClientHandlers.TryTakeGmSpawnVaporize(player), "a relog re-arms it");
            Hex.True(ArbiterClientHandlers.ForgetPlayer(player), "and the admin reset reports it too");
        }
        finally { ArbiterClientHandlers.ResetGmSkillPush(player); }
    }

    /// <summary>
    /// A GM World has already vaporized is left alone - the request would toggle them back to
    /// visible, which is the bug this replaces, not the fix.
    /// </summary>
    [Test] public static void T191e_an_already_vaporized_gm_is_not_asked_again()
    {
        var definitions = new DefinitionRegistry(QuietLog());
        var opcodes = OpcodeTable.LoadFromFile(T191dOpcodeMap(), "376012");
        string? previousGm = Environment.GetEnvironmentVariable(GmAccounts.EnvVariable);
        try
        {
            using var client = new T185Client(definitions, opcodes, QuietLog());
            client.Session.PlayerId = 9;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 9, Name = "caludesucks" };
            client.Session.Account.Name = "t191e-operator";
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, client.Session.Account.Name);
            TerasConfig.ResetForTests();

            ArbiterClientHandlers.ResetGmSkillPush(9);
            ArbiterClientHandlers.SetGmInvisible(9, true);
            Hex.True(ArbiterClientHandlers.RequestSpawnVaporize(client.Session)
                     == ArbiterClientHandlers.SpawnVaporizeResult.AlreadyInvisible,
                "vaporized already: no request, and the shot stays armed");
            Hex.True(ArbiterClientHandlers.TryTakeGmSpawnVaporize(9), "the shot really was left armed");

            // Visible, no World link: the send fails, and T191e-b does NOT burn the one-shot on it.
            ArbiterClientHandlers.ResetGmSkillPush(9);
            ArbiterClientHandlers.SetGmInvisible(9, false);
            Hex.True(ArbiterClientHandlers.RequestSpawnVaporize(client.Session)
                     == ArbiterClientHandlers.SpawnVaporizeResult.NoWorldLink,
                "no World link means no request - the standalone path answers locally as before");
            Hex.True(!ArbiterClientHandlers.SpawnVaporizeAsked(9),
                "and a failed send leaves the entry's attempt unspent");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, previousGm);
            TerasConfig.ResetForTests();
            ArbiterClientHandlers.ResetGmSkillPush(9);
        }
    }

    /// <summary>Records every line, at any level - the T191e-b spawn line is Information.</summary>
    private sealed class T191eLog : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,
            Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> fmt) => Lines.Add(fmt(state, ex));
    }

    /// <summary>
    /// T191e-b. The live path, end to end: the delegate WorldBridge.RegisterPlayer installs is
    /// DeliverTunnelled, so a tunnelled S_SPAWN_ME for an operator must reach the spawn block,
    /// send the hold release AND report the vaporize attempt. The live run logged nothing at all,
    /// which the old silent call could not tell apart from "sent, and World ignored it".
    /// </summary>
    [Test] public static void T191e_b_a_tunnelled_spawn_me_reaches_the_operator_block_and_says_so()
    {
        var definitions = new DefinitionRegistry(QuietLog());
        var opcodes = OpcodeTable.LoadFromFile(T191dOpcodeMap(), "376012");
        string? previousGm = Environment.GetEnvironmentVariable(GmAccounts.EnvVariable);
        var log = new T191eLog();
        try
        {
            using var client = new T185Client(definitions, opcodes, log);
            client.Session.PlayerId = 9;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 9, Name = "caludesucks" };
            client.Session.Account.Name = "t191e-b-operator";
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, client.Session.Account.Name);
            TerasConfig.ResetForTests();
            Hex.True(ArbiterClientHandlers.OperatorGetsGmSkillPush(
                GmCommandHandlers.LevelOf(client.Session, null)), "the session is an operator");

            ArbiterClientHandlers.ResetGmSkillPush(9);
            ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])T191eSpawnMe.Clone());

            Hex.Eq(client.Frame(), T191eSpawnMe, "the spawn frame goes out first, unchanged");
            var hold = client.Frame();
            Hex.True(hold.Length == 5 && hold[2] == 0x0E && hold[3] == 0xA3,
                $"then S_ADMIN_HOLD_CHARACTER - proof the operator block ran, got {Hex.S(hold)}");

            var line = log.Lines.FirstOrDefault(l => l.Contains("S_SPAWN_ME for operator", StringComparison.Ordinal));
            Hex.True(line != null, "the spawn block logs one line per operator spawn");
            Hex.True(line!.Contains("spawn vaporize armed", StringComparison.Ordinal),
                $"T191e-c: the spawn anchor arms the request, it does not send it, got: {line}");
            Hex.True(ArbiterClientHandlers.SpawnVaporizePendingFor(9), "and the request is now pending");
            Hex.True(!ArbiterClientHandlers.SpawnVaporizeAsked(9), "with no attempt spent yet");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, previousGm);
            TerasConfig.ResetForTests();
            ArbiterClientHandlers.ResetGmSkillPush(9);
        }
    }

    /// <summary>
    /// T191e-c. The pump: armed at S_SPAWN_ME, retried on later tunnelled frames, bounded, and it
    /// stops the moment World's SDB_USER_VAPORIZED sets IsGmInvisible.
    /// </summary>
    [Test] public static void T191e_c_the_pending_request_is_retried_until_world_answers()
    {
        var definitions = new DefinitionRegistry(QuietLog());
        var opcodes = OpcodeTable.LoadFromFile(T191dOpcodeMap(), "376012");
        var log = new T191eLog();
        try
        {
            using var client = new T185Client(definitions, opcodes, log);
            client.Session.PlayerId = 9;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 9, Name = "caludesucks" };
            ArbiterClientHandlers.ResetGmSkillPush(9);

            var t0 = DateTimeOffset.UnixEpoch;
            Hex.True(ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t0)
                     == ArbiterClientHandlers.SpawnVaporizePump.Idle,
                "nothing pending before the spawn anchor arms it");

            ArbiterClientHandlers.ArmSpawnVaporize(9);
            Hex.True(ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t0)
                     == ArbiterClientHandlers.SpawnVaporizePump.Tried, "the first frame after spawn tries");
            Hex.True(ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t0)
                     == ArbiterClientHandlers.SpawnVaporizePump.Waiting,
                "and the next frame in the same millisecond waits out the gap");

            var t = t0;
            for (int i = 2; i <= ArbiterClientHandlers.SpawnVaporizeMaxTries; i++)
            {
                t = t.Add(ArbiterClientHandlers.SpawnVaporizeRetryGap);
                Hex.True(ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t)
                         == ArbiterClientHandlers.SpawnVaporizePump.Tried, $"attempt {i} goes out");
            }
            t = t.Add(ArbiterClientHandlers.SpawnVaporizeRetryGap);
            Hex.True(ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t)
                     == ArbiterClientHandlers.SpawnVaporizePump.GaveUp,
                $"and it gives up after {ArbiterClientHandlers.SpawnVaporizeMaxTries} - it does not ask forever");
            Hex.True(!ArbiterClientHandlers.SpawnVaporizePendingFor(9), "nothing pending once it gave up");
            Hex.True(log.Lines.Any(l => l.Contains("did not answer", StringComparison.Ordinal)),
                "and it says so at Warning");

            // World answers: the next pump confirms and clears, whatever attempts are left.
            ArbiterClientHandlers.ArmSpawnVaporize(9);
            ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t);
            ArbiterClientHandlers.SetGmInvisible(9, true);        // SDB_USER_VAPORIZED 01
            Hex.True(ArbiterClientHandlers.PumpSpawnVaporize(client.Session, t)
                     == ArbiterClientHandlers.SpawnVaporizePump.Confirmed, "World answered: done");
            Hex.True(!ArbiterClientHandlers.SpawnVaporizePendingFor(9), "and nothing is pending");
            Hex.True(log.Lines.Any(l => l.Contains("World confirmed after", StringComparison.Ordinal)),
                "the confirmation is logged too");

            // A GM World already has vaporized is never armed - asking would turn them visible.
            ArbiterClientHandlers.ResetGmSkillPush(9);
            ArbiterClientHandlers.SetGmInvisible(9, true);
            ArbiterClientHandlers.ArmSpawnVaporize(9);
            Hex.True(!ArbiterClientHandlers.SpawnVaporizePendingFor(9), "already invisible: nothing armed");
        }
        finally { ArbiterClientHandlers.ResetGmSkillPush(9); }
    }

    /// <summary>
    /// S_SPAWN_ME stays the anchor, and an ordinary character is untouched: the tunnelled frame
    /// goes out unchanged and no spawn request is taken.
    /// </summary>
    [Test] public static void T191e_spawn_me_is_the_anchor_and_a_player_is_untouched()
    {
        var definitions = new DefinitionRegistry(QuietLog());
        var opcodes = OpcodeTable.LoadFromFile(T191dOpcodeMap(), "376012");
        string? previousGm = Environment.GetEnvironmentVariable(GmAccounts.EnvVariable);
        try
        {
            using var client = new T185Client(definitions, opcodes, QuietLog());
            client.Session.PlayerId = 9;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 9, Name = "caludesucks" };
            client.Session.Account.Name = "t191e-player";
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, "");
            TerasConfig.ResetForTests();

            ArbiterClientHandlers.ResetGmSkillPush(9);
            ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])T191eSpawnMe.Clone());
            Hex.Eq(client.Frame(), T191eSpawnMe, "the spawn frame reaches the client unchanged");
            Hex.True(client.Available == 0, "a non-operator gets nothing else");
            Hex.True(ArbiterClientHandlers.TryTakeGmSpawnVaporize(9),
                "and no spawn request was taken for them");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, previousGm);
            TerasConfig.ResetForTests();
            ArbiterClientHandlers.ResetGmSkillPush(9);
        }
    }
}
