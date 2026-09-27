// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>Native ItemPeriodInfoManager: Arb082:16781, Arb083:2224, SQL20151.</summary>
public static class QaItemPeriodCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] { "add_item_period", "delete_item_period" }, StringComparer.OrdinalIgnoreCase);
    private sealed class Runtime
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<int, int> Stages = new(); // 0 pending, 1 active, 2 ended
        internal long? LastTick;
    }
    private static readonly ConditionalWeakTable<CharacterStore, Runtime> States = new();
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || !session.InWorld || line.Args.Count == 0) return true;
        int template = QaGeneralCommands.NativeInt(line.Arg(0));
        if (line.Name.Equals("add_item_period", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Args.Count < 4 || !QaItemSheet.Entry.Value.Items.TryGetValue(template, out var item)
                || item.Int("periodInMinute") <= 0 || !TryDate(line.Arg(1), out var start)
                || !TryDate(line.Arg(2), out var end) || !TryDate(line.Arg(3), out var expiry) || start >= end || end > expiry) return true;
            var runtime = States.GetOrCreateValue(store);
            lock (runtime.Gate)
            {
                int eventId = store.AddQaItemPeriod(template, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), expiry.ToUnixTimeSeconds());
                if (eventId == 0) return true;
                runtime.Stages[eventId] = 0; // A new QA insertion still waits for the native activation tick.
                GmCommandHandlers.SendCustom(session, $"아이템[{template}] 기간 추가: [{Format(start)}] [{Format(end)}] [{Format(expiry)}]");
            }
        }
        else
        {
            var runtime = States.GetOrCreateValue(store);
            lock (runtime.Gate)
            {
                // Native lookup: active template map, then pending event map, then ended event map.
                long now = Clock().ToUnixTimeSeconds();
                var row = store.GetQaItemPeriods().Where(x => x.Template == template)
                    .OrderBy(x => Stage(runtime, x, now) == 1 ? 0 : Stage(runtime, x, now) == 0 ? 1 : 2)
                    .ThenBy(x => x.Id).FirstOrDefault();
                if (row == null) return true;
                int stage = Stage(runtime, row, now);
                store.DeleteQaItemPeriod(row.Id);
                runtime.Stages.Remove(row.Id);
                if (stage == 1) Broadcast(Program.World, 0x14C4, BitConverter.GetBytes(row.Id));
                GmCommandHandlers.SendCustom(session, $"아이템[{template}] 기간 삭제");
            }
        }
        return true;
    }
    private static string Format(DateTimeOffset date) => date.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static bool TryDate(string text, out DateTimeOffset date)
    {
        date = default;
        if (!DateTime.TryParseExact(text, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = new DateTimeOffset(parsed); return true;
    }
    public static void Tick(CharacterStore? store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (store == null) return;
        var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate)
        {
            long milliseconds = now.ToUnixTimeMilliseconds();
            if (runtime.LastTick is long prior && milliseconds >= prior && milliseconds - prior < 1000) return;
            runtime.LastTick = milliseconds;
            long epoch = now.ToUnixTimeSeconds();
            foreach (var row in store.GetQaItemPeriods())
            {
                int stage = runtime.Stages.GetValueOrDefault(row.Id);
                if (stage == 0 && epoch >= row.Start) { stage = 1; Broadcast(bridge, 0x14C3, BuildUpdate(row)); }
                if (stage == 1 && epoch >= row.End) { stage = 2; Broadcast(bridge, 0x14C4, BitConverter.GetBytes(row.Id)); }
                if (stage == 2 && epoch >= row.Expiry) { store.DeleteQaItemPeriod(row.Id); runtime.Stages.Remove(row.Id); }
                else runtime.Stages[row.Id] = stage;
            }
        }
    }
    public static void Replay(CharacterStore? store, WorldLink link, DateTimeOffset now)
    {
        if (store == null) return;
        // Replay does not advance the shared timer, which must still notify already-connected Worlds.
        long epoch = now.ToUnixTimeSeconds();
        foreach (var row in store.GetQaItemPeriods())
            if (row.Start <= epoch && epoch < row.End) link.SendFrame(0x14C3, BuildUpdate(row));
    }
    internal static byte[] BuildUpdate(CharacterStore.QaItemPeriod row)
        => BitConverter.GetBytes(row.Id).Concat(BitConverter.GetBytes(row.Template)).Concat(BitConverter.GetBytes(row.Expiry)).ToArray();
    // Reconnect restores persisted active rows before the first timer pass. Derive unknown
    // stages without mutating the timer ledger: its broadcast must still reach other Worlds.
    private static int Stage(Runtime runtime, CharacterStore.QaItemPeriod row, long now)
        => runtime.Stages.TryGetValue(row.Id, out int stage) ? stage : now < row.Start ? 0 : now < row.End ? 1 : 2;
    private static void Broadcast(WorldBridge? bridge, ushort opcode, byte[] payload)
    {
        if (bridge == null) return;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, opcode, payload);
    }
    /// <summary>Arb082:5818 GetPeriodEndTime; Arb038 op7/op8 invoke it only for the native 1970/1/1 unset value.</summary>
    public static void ApplyCreatedAtoms(CharacterStore store, byte[] reply, int refOffset)
    {
        if (refOffset < 0 || refOffset + 8 > reply.Length) return;
        long start = (long)BitConverter.ToUInt32(reply, refOffset) - 6;
        long bytes = BitConverter.ToUInt32(reply, refOffset + 4);
        if (start < 0 || bytes <= 0 || bytes % ItemCreate.AtomSize != 0 || start + bytes > reply.Length) return;
        var runtime = States.GetOrCreateValue(store);
        for (long offset = start; offset < start + bytes; offset += ItemCreate.AtomSize)
        {
            int at = (int)offset; uint op = BitConverter.ToUInt32(reply, at + 4);
            if (op is not (7 or 8) || BitConverter.ToUInt16(reply, at + 0x1F0) != 1970
                || BitConverter.ToUInt16(reply, at + 0x1F2) != 1 || BitConverter.ToUInt16(reply, at + 0x1F4) != 1) continue;
            int template = BitConverter.ToInt32(reply, at + 0x18);
            if (!QaItemSheet.Entry.Value.Items.TryGetValue(template, out var item) || item.Int("periodInMinute") <= 0) continue;
            bool byAdmin = item.Flag("periodByWebAdmin"); DateTimeOffset expiry = Clock();
            if (byAdmin)
            {
                lock (runtime.Gate)
                {
                    var active = store.GetQaItemPeriods().FirstOrDefault(x => x.Template == template && Stage(runtime, x, expiry.ToUnixTimeSeconds()) == 1);
                    if (active != null) expiry = DateTimeOffset.FromUnixTimeSeconds(active.Expiry);
                }
            }
            else expiry = expiry.AddMinutes(item.Int("periodInMinute"));
            DbProxyHandlers.WriteArbTimestamp(reply, at + 0x1F0, expiry.UtcDateTime); reply[at + 0x200] = byAdmin ? (byte)1 : (byte)0;
            // Op8 is persisted by ItemCreate immediately after this hook. Op7 stores its expiry here.
            if (op != 7) continue;
            var row = store.GetItem(BitConverter.ToInt32(reply, at + 0x10));
            if (row == null) continue;
            var record = row.Record is { Length: ItemCreate.RecordSize } existing ? (byte[])existing.Clone()
                : WarehouseHandlers.BuildItemRecord(row.ItemDbId, row.TemplateId, (int)row.OwnerDbId, (int)row.Amount, row.InvenType, row.Slot);
            reply.AsSpan(at + 0x1F0, 16).CopyTo(record.AsSpan(0x1D0)); record[0x1E0] = reply[at + 0x200];
            store.UpsertItem(row.ItemDbId, row.OwnerDbId, row.InvenType, row.Slot, row.TemplateId, row.Amount, record);
        }
    }
}
