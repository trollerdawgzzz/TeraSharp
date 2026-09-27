// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaPurchaseCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] { "reset_buymenu", "reset_buymenu_limit" }, StringComparer.OrdinalIgnoreCase);
    public static readonly SheetValue<IReadOnlySet<int>> Menus = new("BuyMenuData*.xml", "native QA purchase-limit reset validation", new HashSet<int>(), dir =>
    {
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "BuyMenuData*.xml") : Array.Empty<string>();
        return files.Length == 0 ? null : files.SelectMany(file => XDocument.Load(file).Descendants("BuyMenu"))
            .Select(row => (int?)row.Attribute("id") ?? 0).Where(id => id != 0).ToHashSet();
    }, x => x.Count);
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (line.Args.Count != 1 || !session.InWorld) return true;
        int menu = QaGeneralCommands.NativeInt(line.Arg(0));
        if (Menus.Value.Contains(menu)) store?.ResetPurchaseLimits(menu);
        // Native wrapper broadcasts even if the SQL helper rejected an unknown menu.
        var payload = PurchaseLimitReset.Build(menu); payload[4] = line.Name.Equals("reset_buymenu", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
        Broadcast(Program.World, 0x1631, payload); return true;
    }
    public static bool Handle(CharacterStore? store, WorldBridge bridge, WorldLink link, ushort opcode, byte[] payload)
    {
        if (opcode == 0x2983)
        {
            if (payload.Length < 32) return true;
            int player = BitConverter.ToInt32(payload, 4);
            if (store?.GetCharacter(player) != null)
                store.AddPurchaseLimit(BitConverter.ToInt64(payload, 8), BitConverter.ToInt32(payload, 16),
                    BitConverter.ToInt32(payload, 20), BitConverter.ToInt32(payload, 24), BitConverter.ToInt32(payload, 28));
            link.SendFrame(0x2984, payload[..4].Concat(new byte[] { 1 }).ToArray()); return true;
        }
        if (opcode == 0x2981)
        {
            if (payload.Length < 8) return true;
            long account = store?.AccountOf(BitConverter.ToInt32(payload, 4)) ?? 0;
            var rows = account != 0 ? store!.GetPurchaseLimits(account) : Array.Empty<CharacterStore.PurchaseLimit>();
            link.SendFrame(0x2982, BuildList(rows, BitConverter.ToUInt32(payload, 0))); return true;
        }
        if (opcode == 0x162F)
        {
            if (payload.Length < 16 || store == null) return true;
            int total = store.AddPurchaseLimit(0, BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4),
                BitConverter.ToInt32(payload, 8), BitConverter.ToInt32(payload, 12));
            if (total > 0)
            {
                var update = payload[..16]; BitConverter.GetBytes(total).CopyTo(update, 12); Broadcast(bridge, 0x1630, update);
            }
            return true;
        }
        return false;
    }
    public static void Replay(CharacterStore? store, WorldLink link)
    {
        if (store == null) return;
        var rows = store.GetPurchaseLimits(0);
        if (rows.Count != 0) link.SendFrame(0x162E, BuildList(rows, null));
    }
    internal static byte[] BuildList(IReadOnlyList<CharacterStore.PurchaseLimit> rows, uint? dlm)
    {
        int header = dlm.HasValue ? 13 : 9; var bytes = new byte[header + rows.Count * 24];
        BitConverter.GetBytes(rows.Count).CopyTo(bytes, 0);
        if (rows.Count != 0) BitConverter.GetBytes(header + 6).CopyTo(bytes, 4);
        if (dlm.HasValue) BitConverter.GetBytes(dlm.Value).CopyTo(bytes, 8);
        bytes[header - 1] = 1;
        for (int i = 0; i < rows.Count; i++)
        {
            int offset = header + 24 * i; var row = rows[i];
            BitConverter.GetBytes(offset + 6).CopyTo(bytes, offset);
            if (i + 1 < rows.Count) BitConverter.GetBytes(offset + 30).CopyTo(bytes, offset + 4);
            BitConverter.GetBytes(row.BuyMenu).CopyTo(bytes, offset + 8); BitConverter.GetBytes(row.Menu).CopyTo(bytes, offset + 12);
            BitConverter.GetBytes(row.Item).CopyTo(bytes, offset + 16); BitConverter.GetBytes(row.Count).CopyTo(bytes, offset + 20);
        }
        return bytes;
    }
    private static void Broadcast(WorldBridge? bridge, ushort opcode, byte[] payload)
    {
        if (bridge == null) return;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, opcode, payload);
    }
}
