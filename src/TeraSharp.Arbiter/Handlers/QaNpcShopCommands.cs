// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaNpcShopCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] { "store_changeinfo_add", "store_changeinfo_arbiter" }, StringComparer.OrdinalIgnoreCase);
    public static readonly SheetValue<IReadOnlyDictionary<int, int[]>> Menus = new("BuyMenuList.xml", "QA NPC-shop list selection", new Dictionary<int, int[]>(), dir =>
    {
        string path = Path.Combine(dir, "BuyMenuList.xml"); if (!File.Exists(path)) return null;
        return XDocument.Load(path).Descendants("Menu").Where(x => (int?)x.Attribute("id") != null)
            .ToDictionary(x => (int)x.Attribute("id")!, x => x.Elements("ItemList").Select(i => (int?)i.Attribute("id") ?? 0).ToArray());
    }, x => x.Count);
    internal static Func<int, int> RandomInclusive { get; set; } = maximum => checked((int)Random.Shared.NextInt64(0, (long)maximum + 1));
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || !session.InWorld) return true;
        if (line.Name.Equals("store_changeinfo_arbiter", StringComparison.OrdinalIgnoreCase))
        {
            var rows = store.GetNpcShopChanges();
            if (line.Args.Count == 0) GmCommandHandlers.SendCustom(session, $"항목 수: {rows.Count}개");
            else
            {
                int index = QaGeneralCommands.NativeInt(line.Arg(0));
                if (index < 1 || index > rows.Count) GmCommandHandlers.SendCustom(session, $"올바르지 않은 인덱스: {index} (가능 범위: 1부터 {rows.Count}까지)");
                else
                {
                    var row = rows[index - 1]; int menu = row.BuyList;
                    // Native ordered traversal keeps the last matching parent menu (Arb044:5391–5421).
                    foreach (var pair in Menus.Value.OrderBy(x => x.Key)) if (pair.Value.Contains(row.BuyList)) menu = pair.Key;
                    GmCommandHandlers.SendCustom(session, $"{index}번째 목록 {menu}: 아이템 {row.Item} 가격 {row.Price} (추가)");
                }
            }
            return true;
        }
        if (line.Args.Count < 5 || !line.Arg(0).Equals("random", StringComparison.OrdinalIgnoreCase)) return true;
        int count = QaGeneralCommands.NativeInt(line.Arg(1)); if (count <= 0) return true;
        bool menuRange = line.Arg(2).Equals("buymenurange", StringComparison.OrdinalIgnoreCase) || line.Arg(2).Equals("range", StringComparison.OrdinalIgnoreCase);
        int cursor = 3; var menus = ReadNumbers(line, ref cursor);
        if (cursor >= line.Args.Count) return true;
        bool itemRange = line.Arg(cursor).Equals("itemrange", StringComparison.OrdinalIgnoreCase) || line.Arg(cursor).Equals("range", StringComparison.OrdinalIgnoreCase);
        cursor++; var items = ReadNumbers(line, ref cursor);
        if (menus.Count == 0 || items.Count == 0 || menuRange && menus.Count == 2 && menus[1] < menus[0]
            || itemRange && items.Count == 2 && items[1] < items[0]) return true;
        for (int i = 0; i < count; i++)
        {
            int menu = Pick(menus, menuRange);
            if (!Menus.Value.TryGetValue(menu, out var lists) || lists.Length == 0)
            {
                GmCommandHandlers.SendCustom(session, $"오류: 올바르지 않은 목록 ID {menu}"); break;
            }
            int list = lists[RandomInclusive(lists.Length - 1)], item = Pick(items, itemRange);
            var row = store.AddNpcShopChange(list, item, RandomInclusive(1_000_000_000));
            var payload = Build(new[] { row });
            if (Program.World is { } bridge)
                for (int world = 0; world < WorldRegistration.MaxWorldId; world++)
                    if (bridge.HasLinks(world)) bridge.SendFrame(world, 0x15D8, payload);
        }
        // Native reports requested amount even if an invalid menu interrupted the loop.
        GmCommandHandlers.SendCustom(session, $"일반 아이템 {count}개 변경 완료"); return true;
    }
    private static List<int> ReadNumbers(GmCommandLine line, ref int cursor)
    {
        var list = new List<int>();
        while (cursor < line.Args.Count)
        {
            int number = QaGeneralCommands.NativeInt(line.Arg(cursor)); if (number == 0) break;
            list.Add(number); cursor++;
        }
        return list;
    }
    private static int Pick(IReadOnlyList<int> values, bool range) => range && values.Count == 2
        ? checked(values[0] + RandomInclusive(checked(values[1] - values[0]))) : values[RandomInclusive(values.Count - 1)];
    public static void Replay(CharacterStore? store, WorldLink link)
    {
        if (store == null) return;
        foreach (var chunk in store.GetNpcShopChanges().Chunk(0x46C)) link.SendFrame(0x15D8, Build(chunk));
    }
    internal static byte[] Build(IReadOnlyList<CharacterStore.NpcShopChange> rows)
    {
        var payload = new byte[8 + 57 * rows.Count]; BitConverter.GetBytes(rows.Count).CopyTo(payload, 0);
        if (rows.Count > 0) BitConverter.GetBytes(14).CopyTo(payload, 4);
        for (int i = 0; i < rows.Count; i++)
        {
            int offset = 8 + 57 * i; var row = rows[i];
            BitConverter.GetBytes(offset + 6).CopyTo(payload, offset);
            if (i + 1 < rows.Count) BitConverter.GetBytes(offset + 63).CopyTo(payload, offset + 4);
            BitConverter.GetBytes(1).CopyTo(payload, offset + 8); // native normal-store type
            BitConverter.GetBytes(row.BuyList).CopyTo(payload, offset + 12); BitConverter.GetBytes(row.Item).CopyTo(payload, offset + 16);
            BitConverter.GetBytes(row.Price).CopyTo(payload, offset + 20);
            payload[offset + 56] = 1; // isAdd; medal/fragment/token/npcGuild/grade fields remain native zero.
        }
        return payload;
    }
}
