// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaDiagnosticCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] {
        "profile", "reset_profile", "dis", "logout", "reload_datasheet", "refresh_quest_cache",
        "consoleprint_commandlist", "huntingevent_add",
        "add_event_system", "set_bypass", "set_arbiter_worldparam",
    }, StringComparer.OrdinalIgnoreCase);
    private static int _bypassEnabled = 1;
    private static int _leaveSeconds = 5; // Preserve the existing captured/default path until QA explicitly overrides it.
    public static bool BypassEnabled => Volatile.Read(ref _bypassEnabled) != 0;
    public static int LeaveCountdownSeconds => Volatile.Read(ref _leaveSeconds);
    internal static void ResetRuntimeForTests() { _bypassEnabled = 1; _leaveSeconds = 5; }
    public static async Task WaitForLeaveCountdown(int seconds, CancellationToken cancellation)
    {
        // Native signed negatives expire immediately; -1 must not become Task.Delay's infinite wait.
        // Chunk very large native int32 values instead of exceeding Task.Delay's millisecond range.
        long remaining = Math.Max(0, seconds);
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, 3600);
            await Task.Delay(TimeSpan.FromSeconds(chunk), cancellation); remaining -= chunk;
        }
        cancellation.ThrowIfCancellationRequested();
    }

    public static bool TryExecute(GameSession s, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        switch (line.Name.ToLowerInvariant())
        {
            case "set_bypass": // Arb044:3119; actual consumer Handler_SA_BYPASS_TO_CLIENT, Arb062:3672.
                if (line.Args.Count == 1 && line.Arg(0) == "on") Volatile.Write(ref _bypassEnabled, 1);
                else if (line.Args.Count == 1 && line.Arg(0) == "off") Volatile.Write(ref _bypassEnabled, 0);
                break;
            case "set_arbiter_worldparam": // Arb044:5292: only this key, case-insensitive, signed _wtol value.
                if (line.Args.Count >= 2 && line.Arg(0).Equals("clientLeaveWorldWaitSec", StringComparison.OrdinalIgnoreCase))
                    Volatile.Write(ref _leaveSeconds, QaGeneralCommands.NativeInt(line.Arg(1)));
                break;
            case "profile": // Arb043:19902: native instrumentation is C++-specific; the World request still applies.
            case "reset_profile": // Arb044:1510: global World broadcast, not the caller's World alone.
                Broadcast(line.Name.Equals("profile", StringComparison.OrdinalIgnoreCase) ? (ushort)0x1447 : (ushort)0x1448, Array.Empty<byte>());
                log.LogInformation("{Command}: native World profiler request sent; native Arbiter C++ function/packet instrumentation is not present in TeraSharp", line.Name);
                break;
            case "dis": s.Close(); break; // Arb040:14022 -> User::LeaveWorldStart(disconnect), Arb029:3862.
            case "logout": s.ForceLeaveToWorld(LeaveMode.Lobby); break; // Arb043:17034: immediate lobby leave, no countdown request.
            case "reload_datasheet": // Arb044:441: reload Arbiter sheets, then AS_ADMIN_RELOAD_DATASHEET to every World.
                DatasheetLoader.LoadAll(log);
                Broadcast(0x13B3, BitConverter.GetBytes((int)(s.SelectedCharacter?.Id ?? s.PlayerId)));
                break;
            case "refresh_quest_cache": // Arb044:326: reload SQL into User quest cache. Our load reads SQL on every request.
                if (store != null)
                {
                    int id = (int)(s.SelectedCharacter?.Id ?? s.PlayerId);
                    store.GetActiveQuestRecords(id); store.GetCompletedQuestIds(id);
                    GmCommandHandlers.SendCustom(s, "QuestCache refresh completed. next up to type `/@refresh_quest`");
                }
                break;
            case "consoleprint_commandlist": // Arb040:9092: console only, no client push.
                log.LogInformation("Admin Command List: {Commands}", string.Join(", ", NativeQaCommands.Names.OrderBy(x => x, StringComparer.Ordinal)));
                log.LogInformation("World Command List: {Commands}", string.Join(", ", GmCommandCatalog.WorldCommands.OrderBy(x => x, StringComparer.Ordinal)));
                break;
            case "huntingevent_add": // Arb040:4092: this build's handler consists only of scope trace enter/leave.
                log.LogDebug("huntingevent_add: native handler is empty in this build"); break;
            case "add_event_system": // Arb040:3335–3394 -> Arb080:7498–7511: parsed values feed a trace-only empty function.
                if (line.Args.Count == 6) log.LogDebug("add_event_system: native AddEventByQa is empty in this build");
                break;
        }
        return true;
    }

    private static void Broadcast(ushort opcode, byte[] payload)
    {
        if (Program.World is not { } world) return;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++)
            if (world.HasLinks(id)) world.SendFrame(id, opcode, payload);
    }
}
