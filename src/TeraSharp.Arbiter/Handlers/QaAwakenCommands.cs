// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>Native temporary awaken overlays (Arb081:16139/16479;082:18553/18994), separate from item atom edits.</summary>
public static class QaAwakenCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "addawakenchange", "addawakenenchant", "deleteawakenchange", "deleteawakenenchant" };
    private sealed class Runtime
    {
        internal readonly object Gate = new(); internal readonly HashSet<(bool Enchant, int Id)> Started = new();
        internal long NextTick;
    }
    private static readonly ConditionalWeakTable<CharacterStore, Runtime> States = new();
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    // Native ContentsOnOff(type11,id0) gates client data, never its World0 mirror.
    internal static Func<bool> ClientDataDisabled { get; set; } = () => false;

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || session.SelectedCharacter == null) return true;
        bool enchant = line.Name.EndsWith("enchant", StringComparison.OrdinalIgnoreCase), add = line.Name.StartsWith("add", StringComparison.OrdinalIgnoreCase);
        int count = add ? enchant ? 6 : 3 : 1; if (line.Args.Count != count) return true;
        int[] args = line.Args.Select(QaGeneralCommands.NativeInt).ToArray();
        string action = (add ? "Add" : "Delete") + "Awaken" + (enchant ? "Enchant" : "Change");
        var state = States.GetOrCreateValue(store); long now = Clock().ToUnixTimeSeconds();
        lock (state.Gate)
        {
            if (add)
            {
                int[] values = enchant ? new[] { args[3], args[4], args[5], args[0], args[1], args[2] } : args;
                bool valid = args[0] is >= 0 and < 1000000 && QaItemSheet.Entry.Value.Items.ContainsKey(args[0]) && args[1] > 0 && args[2] > 0
                    && (!enchant || args[5] is 1 or 3 or 4 or 5);
                var row = valid ? store.AddAwakenEvent(enchant, values, now, now + 86400) : null;
                if (row != null) { Start(row, state, Program.World); GmCommandHandlers.SendCustom(session, $"{action} Success [eventId : {row.Id}]"); }
                else GmCommandHandlers.SendCustom(session, action + " Fail");
            }
            else
            {
                var row = store.GetAwakenEvents(enchant).FirstOrDefault(r => r.Id == args[0]);
                if (row == null || !store.DeleteAwakenEvent(enchant, row.Id)) GmCommandHandlers.SendCustom(session, action + " Fail");
                else
                {
                    if (state.Started.Remove((enchant, row.Id)) || row.Start <= now && now < row.End) End(row, Program.World);
                    GmCommandHandlers.SendCustom(session, $"{action} Success [eventId : {row.Id}]");
                }
            }
        }
        return true;
    }
    private static void Start(CharacterStore.AwakenEvent row, Runtime state, WorldBridge? bridge)
    {
        if (!state.Started.Add((row.Enchant, row.Id))) return;
        if (!ClientDataDisabled()) foreach (var user in bridge?.InWorldSessions() ?? new List<GameSession>()) user.Send(BuildClient(new[] { row }, row.Enchant));
        if (bridge?.HasLinks(0) == true) bridge.SendFrame(0, row.Enchant ? (ushort)0x15D4 : (ushort)0x15D5, BuildWorld(new[] { row }, row.Enchant));
    }
    private static void End(CharacterStore.AwakenEvent row, WorldBridge? bridge)
    {
        ushort opcode = row.Enchant ? (ushort)0x5621 : (ushort)0xE86A;
        if (!ClientDataDisabled()) foreach (var user in bridge?.InWorldSessions() ?? new List<GameSession>())
        {
            byte[] p = new byte[8]; BitConverter.GetBytes((ushort)8).CopyTo(p, 0); BitConverter.GetBytes(opcode).CopyTo(p, 2); BitConverter.GetBytes(row.Id).CopyTo(p, 4); user.Send(p);
        }
        if (bridge?.HasLinks(0) == true) bridge.SendFrame(0, row.Enchant ? (ushort)0x15D6 : (ushort)0x15D7, BitConverter.GetBytes(row.Id));
    }
    public static void Tick(CharacterStore? store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (store == null) return; var state = States.GetOrCreateValue(store); long epoch = now.ToUnixTimeSeconds();
        lock (state.Gate)
        {
            if (epoch < state.NextTick) return; state.NextTick = epoch + 60; // native constructor081:13546 sets60000ms.
            foreach (bool enchant in new[] { true, false })
            {
                foreach (var row in store.GetAwakenEvents(enchant).Where(r => r.Start <= epoch)) Start(row, state, bridge);
            }
            foreach (bool enchant in new[] { true, false })
                foreach (var row in store.GetAwakenEvents(enchant).Where(r => r.End <= epoch))
                {
                    if (state.Started.Remove((enchant, row.Id))) End(row, bridge); store.DeleteAwakenEvent(enchant, row.Id);
                }
        }
    }
    public static void ReplayWorld(CharacterStore? store, WorldLink link, DateTimeOffset now)
    {
        if (store == null || link.WorldId != 0) return;
        foreach (bool enchant in new[] { true, false })
        {
            var rows = Active(store, enchant, now); if (rows.Count > 0) link.SendFrame(enchant ? (ushort)0x15D4 : (ushort)0x15D5, BuildWorld(rows, enchant));
        }
    }
    private static IReadOnlyList<CharacterStore.AwakenEvent> Active(CharacterStore store, bool enchant, DateTimeOffset now)
        => store.GetAwakenEvents(enchant).Where(r => r.Start <= now.ToUnixTimeSeconds() && now.ToUnixTimeSeconds() < r.End).ToArray();
    internal static byte[] BuildClient(IReadOnlyList<CharacterStore.AwakenEvent> rows, bool enchant)
    {
        int element = enchant ? 32 : 20; byte[] frame = new byte[8 + rows.Count * element];
        BitConverter.GetBytes((ushort)frame.Length).CopyTo(frame, 0); BitConverter.GetBytes(enchant ? (ushort)0x9E6F : (ushort)0xD319).CopyTo(frame, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(frame, 4); BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : 8)).CopyTo(frame, 6);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = 8 + i * element; BitConverter.GetBytes((ushort)at).CopyTo(frame, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? at + element : 0)).CopyTo(frame, at + 2);
            BitConverter.GetBytes(rows[i].Id).CopyTo(frame, at + 4);
            for (int j = 0; j < rows[i].Values.Length; j++) BitConverter.GetBytes(rows[i].Values[j]).CopyTo(frame, at + 8 + j * 4);
        }
        return frame;
    }
    internal static byte[] BuildWorld(IReadOnlyList<CharacterStore.AwakenEvent> rows, bool enchant)
    {
        int element = enchant ? 36 : 24; byte[] payload = new byte[8 + rows.Count * element];
        BitConverter.GetBytes(rows.Count).CopyTo(payload, 0); BitConverter.GetBytes(rows.Count == 0 ? 0 : 14).CopyTo(payload, 4);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = 8 + i * element; BitConverter.GetBytes(at + 6).CopyTo(payload, at);
            BitConverter.GetBytes(i + 1 < rows.Count ? at + element + 6 : 0).CopyTo(payload, at + 4); BitConverter.GetBytes(rows[i].Id).CopyTo(payload, at + 8);
            for (int j = 0; j < rows[i].Values.Length; j++) BitConverter.GetBytes(rows[i].Values[j]).CopyTo(payload, at + 12 + j * 4);
        }
        return payload;
    }
    internal static void ResetForTests(CharacterStore store) { States.Remove(store); Clock = () => DateTimeOffset.UtcNow; ClientDataDisabled = () => false; }
}
