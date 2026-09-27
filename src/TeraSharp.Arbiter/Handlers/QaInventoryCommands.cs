// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using System.Xml.Linq;
using System.Globalization;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaInventoryCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] {
        "clear_recipe", "reset_ware", "reset_ware_slot", "reset_styleware", "use_style_warehouse", "open_identify",
        "request_shop_url", "masterpiece", "ware_duration", "makeitem_mprate",
    }, StringComparer.OrdinalIgnoreCase);
    private static int _styleWarehouse = -1; // Unknown preserves the captured/native configured startup value.
    private static int _openIdentify;
    private static int _nativeMasterpieceFlag;
    private static int _commissionDuration;
    private static float _masterworkRate = 1;
    public sealed record CommissionDurations(int Account, int Guild);
    public static readonly SheetValue<CommissionDurations> CommissionSheet = new("WorldData.xml (warehouse commission durations)",
        "native QA legacy commission configuration", new(0, 0), directory =>
        {
            string path = Path.Combine(directory, "WorldData.xml");
            if (!File.Exists(path)) return null;
            var root = XDocument.Load(path).Root!;
            return new((int?)root.Element("Warehouse")?.Attribute("accountCommisionDuration") ?? 0,
                (int?)root.Element("Guildwarehouse")?.Attribute("guildCommisionDuration") ?? 0);
        }, _ => 2);
    internal static CommissionDurations CurrentCommissionDurations => Volatile.Read(ref _commissionDuration) is int n && n > 60
        ? new(n, n) : CommissionSheet.Value;
    public static bool? StyleWarehouseOverride => Volatile.Read(ref _styleWarehouse) is int n && n >= 0 ? n != 0 : null;
    public static bool OpenIdentify => Volatile.Read(ref _openIdentify) != 0;
    internal static bool NativeMasterpieceFlag => Volatile.Read(ref _nativeMasterpieceFlag) != 0;
    internal static void ResetForTests() { _styleWarehouse = -1; _openIdentify = 0; _nativeMasterpieceFlag = 0; _commissionDuration = 0; _masterworkRate = 1; }
    public static void ApplyMasterworkRate(Span<byte> atom)
    {
        float requested = Volatile.Read(ref _masterworkRate);
        if (requested == 1 || atom.Length < ItemCreate.AtomSize || atom[0x128] == 0 || atom[0xE8] != 0
            || BitConverter.ToInt32(atom.Slice(0x104, 4)) != 0
            || !QaItemSheet.Entry.Value.Items.TryGetValue(BitConverter.ToInt32(atom.Slice(0x18, 4)), out var item)
            || !item.Attributes.TryGetValue("masterpieceBasicStatRevise", out string? text)) return;
        // Arb009:14042 removes values<=1 before numbering; duplicate rates retain separate indices.
        int index = 0;
        foreach (string value in text.Split(';'))
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rate) || !(rate > 1)) continue;
            if (MathF.Abs(rate - requested) < .001f)
            {
                BitConverter.TryWriteBytes(atom.Slice(0xE0, 4), index); BitConverter.TryWriteBytes(atom.Slice(0xE4, 4), index); return;
            }
            index++;
        }
        // No matching override leaves the existing normal creation path's supplied indices intact.
    }

    public static bool TryExecute(GameSession s, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        int player = (int)(s.SelectedCharacter?.Id ?? s.PlayerId);
        switch (line.Name.ToLowerInvariant())
        {
            case "makeitem_mprate": // Arb043:17137; actual matched override consumer Arb038:1118–1146.
                if (line.Args.Count == 1) Volatile.Write(ref _masterworkRate, line.Arg(0) == "off" ? 1
                    : float.TryParse(line.Arg(0), NumberStyles.Float, CultureInfo.InvariantCulture, out float rate) ? rate : 0);
                break;
            case "ware_duration": // Arb044:8148; native account/guild CommisionPayed both unconditionally return true.
                if (line.Args.Count > 0 && QaGeneralCommands.NativeInt(line.Arg(0)) is int duration && duration > 60)
                    Volatile.Write(ref _commissionDuration, duration);
                break;
            case "masterpiece": // Arb040:14531/14556; full native PE has only these two stores, no flag reader.
                if (line.Args.Count == 1 && line.Arg(0) == "on") Volatile.Write(ref _nativeMasterpieceFlag, 1);
                else if (line.Args.Count == 1 && line.Arg(0) == "off") Volatile.Write(ref _nativeMasterpieceFlag, 0);
                break;
            case "request_shop_url": // Arb065:8485–8574: native no-UserEntityGw branch, matching this runtime.
                s.Send(QaUiCommands.StringPacket(0xC558, "tgwiki/redmine"));
                break;
            case "clear_recipe": // Arb040:8771–8865: only a nonempty recipe vector triggers the World command.
                if (store != null)
                {
                    var recipes = store.GetItemRecipes(player);
                    foreach (var recipe in recipes) store.DeleteItemRecipe(player, recipe.RecipeId);
                    if (recipes.Count != 0) ArbiterClientHandlers.SendToWorld(s, 0x2829,
                        GmCommandHandlers.BuildWorldForward(player, 1, "clear_recipe_world"));
                }
                break;
            case "reset_ware":
            case "reset_ware_slot":
            case "reset_styleware":
                if (store != null)
                {
                    bool style = line.Name.Equals("reset_styleware", StringComparison.OrdinalIgnoreCase);
                    bool clear = !line.Name.Equals("reset_ware_slot", StringComparison.OrdinalIgnoreCase);
                    // Arb044:1742/1817/1862 -> Warehouse::ClearWarehouse, SharedDBCache task5/6.
                    store.ResetQaWarehouse(store.AccountOf(player), style ? 12 : 1, clear);
                    GmCommandHandlers.SendCustom(s, $"{(style ? "Style Warehouse" : "Warehouse")} slot of '[{s.Account.Name}]' reset!");
                }
                break;
            case "use_style_warehouse": // Arb044:7993: omitted means on; first arg otherwise case-insensitive on.
                bool enabled = line.Args.Count == 0 || line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase);
                Volatile.Write(ref _styleWarehouse, enabled ? 1 : 0);
                if (Program.World is { } world)
                    for (int id = 0; id < WorldRegistration.MaxWorldId; id++)
                        if (world.HasLinks(id)) world.SendFrame(id, 0x13B5, new[] { enabled ? (byte)1 : (byte)0 });
                GmCommandHandlers.SendCustom(s, $"useStyleWarehouse: {(enabled ? "true" : "false")}\n");
                break;
            case "open_identify": // Arb040:14580; DO_TS_INSERT_NONSTACKABLE_ITEM, Arb038:958/1162.
                if (line.Args.Count > 0) Volatile.Write(ref _openIdentify, line.Arg(0) == "on" ? 1 : 0);
                break;
        }
        return true;
    }
}
