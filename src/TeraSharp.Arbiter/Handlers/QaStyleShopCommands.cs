// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>Arb040:5187 QA wrapper over ProductEventManager's persistent mark products.</summary>
public static class QaStyleShopCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "addstyleshop" };
    // ItemTemplate+0x310 enum, not item category. Arb009:13306, Arb004:1983; binary table proof in data/t201/native-proof.
    private static readonly HashSet<string> ItemTypes = new(StringComparer.OrdinalIgnoreCase)
        { "EQUIP_UNDERWEAR", "SKILLBOOK", "EQUIP_STYLE_ACCESSORY", "EQUIP_STYLE_WEAPON", "EQUIP_STYLE_BODY", "EQUIP_STYLE_BACK" };
    private sealed class Runtime
    {
        internal readonly object Gate = new(); internal readonly Dictionary<int, (int Display, int Mark)> Visible = new();
        internal bool Loaded; internal int LastId; internal long NextTick;
    }
    private static readonly ConditionalWeakTable<CharacterStore, Runtime> States = new();
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || session.SelectedCharacter == null || line.Args.Count == 0) return true;
        int itemId = QaGeneralCommands.NativeInt(line.Args[0]);
        if (!QaItemSheet.Entry.Value.Items.TryGetValue(itemId, out var item)
            || !item.Attributes.TryGetValue("combatItemType", out string? type) || !ItemTypes.Contains(type)) return true;
        long now = Clock().ToUnixTimeSeconds(), start = now, end = now + 86400, preview = now;
        int price = 10; string label = "test by QA";
        // Native chooses the optional form ONLY when there are exactly six arguments.
        if (line.Args.Count == 6)
        {
            if (!QaDungeonEvents.TryParseDate(line.Args[1], out start) || !QaDungeonEvents.TryParseDate(line.Args[2], out end)
                || !QaDungeonEvents.TryParseDate(line.Args[3], out preview)) return true;
            label = line.Args[4]; price = QaGeneralCommands.NativeInt(line.Args[5]);
        }
        if (start > end || label.Length >= 16) return true; // saleType2 permits zero/negative price; preview need not precede sale.
        var state = States.GetOrCreateValue(store);
        lock (state.Gate)
        {
            Load(store, state, now);
            if (store.GetStyleProducts().Any(r => r.Item == itemId)) return true;
            // Native loads max(eventId), then increments RAM +0xC4. SQL eventId is NOT an identity.
            var row = new CharacterStore.StyleProduct(++state.LastId, itemId, start, end, 0, start, end, 0, preview, label, price, 2);
            if (!store.AddStyleProduct(row)) return true;
            state.Visible[row.Id] = (0, 0); // Insert does not send an update; the 30-second manager timer does.
            GmCommandHandlers.SendCustom(session, $"StyleShopItem [tid:{itemId}] Inserted");
        }
        return true;
    }
    private static void Load(CharacterStore store, Runtime state, long now)
    {
        if (state.Loaded) return; state.Loaded = true; state.NextTick = now + 30;
        foreach (var row in store.GetStyleProducts())
        {
            if (row.SaleEnd < now) { store.DeleteStyleProduct(row.Id); continue; } // LoadManager's spDeleteOldProductMarkEvent.
            state.LastId = Math.Max(state.LastId, row.Id);
            // DBLoadProductMark restores current display/mark before reconnect (Arb082:1788-1807).
            int display = now >= row.SaleEnd ? 0 : now >= row.SaleStart ? 2 : now >= row.PreviewStart ? 1 : 0;
            state.Visible[row.Id] = (display, Mark(row, now));
        }
    }
    internal static int Display(CharacterStore.StyleProduct row, long now)
        // IsEmptyDate tests the entire1970-01-01 day (Arb000:18859). Native writers intentionally do not make this check.
        => now >= row.SaleEnd ? 3 : now >= row.SaleStart ? 2 : (row.PreviewStart < 0 || row.PreviewStart >= 86400) && now >= row.PreviewStart ? 1 : 0;
    private static int Mark(CharacterStore.StyleProduct row, long now) => row.MarkStart <= now && now <= row.MarkEnd ? row.MarkType : 0;
    public static void Tick(CharacterStore? store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (store == null) return; var state = States.GetOrCreateValue(store); long epoch = now.ToUnixTimeSeconds();
        lock (state.Gate)
        {
            Load(store, state, epoch); if (epoch < state.NextTick) return; state.NextTick = epoch + 30; // Arb083:1444 StartManager.
            foreach (var row in store.GetStyleProducts())
            {
                int display = Display(row, epoch), mark = Mark(row, epoch); state.Visible.TryGetValue(row.Id, out var old);
                if (display == 3)
                {
                    if (store.DeleteStyleProduct(row.Id))
                    {
                        state.Visible.Remove(row.Id);
                        // Native delete clears every used field except id/item; its unused saleType is uninitialized. Send zero.
                        Send(bridge, new(row.Id, row.Item, 0, 0, 0, 0, 0, 0, 0, "", 0, 0), true, 0, epoch);
                    }
                    continue;
                }
                if (old.Display != display) Send(bridge, row, display == 0, display, epoch);
                if (old.Mark != mark && display != 0) Send(bridge, row, false, display, epoch);
                state.Visible[row.Id] = (display, mark);
            }
        }
    }
    private static void Send(WorldBridge? bridge, CharacterStore.StyleProduct row, bool remove, int display, long now)
    {
        if (bridge == null) return;
        byte[] world = BuildUpdate(row, remove, now); // Arb081:19526 -> Arb045:18750 broadcasts to every ready World.
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, 0x15B9, world);
        byte[] client = remove ? new byte[] { 8, 0, 0xB1, 0x84, 0, 0, 0, 0 } : BuildClient(row, display, now);
        if (remove) BitConverter.GetBytes(row.Item).CopyTo(client, 4);
        // QA accepts only the six style item types, so native category1/2 (ContentsOnOff22/23) cannot occur here.
        foreach (var user in bridge.InWorldSessions()) user.Send(client);
    }
    public static void ReplayWorld(CharacterStore? store, WorldLink link, DateTimeOffset now)
    {
        if (store == null) return; var state = States.GetOrCreateValue(store); long epoch = now.ToUnixTimeSeconds();
        lock (state.Gate)
        {
            Load(store, state, epoch); var rows = store.GetStyleProducts().Where(r => state.Visible.TryGetValue(r.Id, out var s) && s.Display is 1 or 2).ToArray();
            var chunk = new List<CharacterStore.StyleProduct>(); int length = 14;
            foreach (var row in rows)
            {
                int added = 45 + (row.Label.Length + 1) * 2;
                // Native's count limit1000 plus the transport's65535-byte frame bound.
                if (chunk.Count == 1000 || length + added > ushort.MaxValue) { link.SendFrame(0x15B8, BuildSnapshot(chunk, epoch)); chunk.Clear(); length = 14; }
                chunk.Add(row); length += added;
            }
            if (chunk.Count != 0) link.SendFrame(0x15B8, BuildSnapshot(chunk, epoch));
        }
    }
    internal static byte[] BuildUpdate(CharacterStore.StyleProduct row, bool remove, long now)
    {
        byte[] label = Encoding.Unicode.GetBytes(row.Label + '\0'), p = new byte[38 + label.Length];
        Put(p, 0, 44); p[4] = remove ? (byte)1 : (byte)0; Put(p, 5, row.Id); Put(p, 9, row.Item); Put(p, 13, Mark(row, now));
        BitConverter.GetBytes(row.SaleStart).CopyTo(p, 17); Put(p, 25, row.Discount); Put(p, 29, row.Price);
        p[33] = row.PreviewStart <= now && now < row.SaleStart ? (byte)1 : (byte)0; Put(p, 34, row.SaleType); label.CopyTo(p, 38); return p;
    }
    internal static byte[] BuildClient(CharacterStore.StyleProduct row, int display, long now)
    {
        byte[] label = Encoding.Unicode.GetBytes((display == 1 ? row.Label : "") + '\0'), p = new byte[35 + label.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0); BitConverter.GetBytes((ushort)0xAB44).CopyTo(p, 2); BitConverter.GetBytes((ushort)35).CopyTo(p, 4);
        Put(p, 6, row.Item); Put(p, 10, Mark(row, now)); BitConverter.GetBytes(row.SaleStart).CopyTo(p, 14); Put(p, 22, row.Price);
        p[26] = display == 1 ? (byte)1 : (byte)0; Put(p, 27, display == 2 ? row.Discount : 0); Put(p, 31, row.SaleType); label.CopyTo(p, 35); return p;
    }
    internal static byte[] BuildSnapshot(IReadOnlyList<CharacterStore.StyleProduct> rows, long now)
    {
        byte[] p = new byte[8 + rows.Sum(r => 45 + (r.Label.Length + 1) * 2)]; Put(p, 0, rows.Count); Put(p, 4, rows.Count == 0 ? 0 : 14);
        int at = 8;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; byte[] label = Encoding.Unicode.GetBytes(row.Label + '\0'); int next = at + 45 + label.Length;
            Put(p, at, at + 6); Put(p, at + 4, i + 1 < rows.Count ? next + 6 : 0); Put(p, at + 8, at + 51);
            Put(p, at + 12, row.Id); Put(p, at + 16, row.Item); Put(p, at + 20, Mark(row, now)); BitConverter.GetBytes(row.SaleStart).CopyTo(p, at + 24);
            Put(p, at + 32, row.Discount); Put(p, at + 36, row.Price); p[at + 40] = row.PreviewStart <= now && now < row.SaleStart ? (byte)1 : (byte)0;
            Put(p, at + 41, row.SaleType); label.CopyTo(p, at + 45); at = next;
        }
        return p;
    }
    private static void Put(byte[] p, int at, int value) => BitConverter.GetBytes(value).CopyTo(p, at);
    internal static void ResetForTests(CharacterStore store) { States.Remove(store); Clock = () => DateTimeOffset.UtcNow; }
}
