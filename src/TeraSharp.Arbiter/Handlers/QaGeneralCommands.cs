// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201 native QA state changes; caller authorization is performed by GmCommandHandlers.</summary>
public static class QaGeneralCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] {
        "check_simple_tip", "clear_simple_tip", "refresh_inven", "clear_inven", "set_go",
        "set_new_member", "set_pcbang", "usage",
    }, StringComparer.OrdinalIgnoreCase);
    private readonly record struct AccountFlags(uint Values, uint Known);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AccountFlags> Flags = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryExecute(GameSession s, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        int id = (int)(s.SelectedCharacter?.Id ?? s.PlayerId);
        switch (line.Name.ToLowerInvariant())
        {
            case "check_simple_tip":
                // Arb040:8250 -> Arb030:15271: native adds exactly one to this tip's popup count.
                if (line.Args.Count > 0) store?.AddTutorialTipCount(id, NativeInt(line.Arg(0)), 1);
                break;
            case "clear_simple_tip": store?.ClearTutorialTips(id); break; // Arb040:8611, no reply.
            case "refresh_inven":
                if (store != null) ArbiterClientHandlers.SendToWorld(s, 0x2808,
                    BuildRefreshInventory(id, store.GetCharacterMoney(id), store.GetInventoryItems(id)));
                break;
            case "clear_inven":
                if (store != null)
                {
                    store.ClearInventory(id);
                    ArbiterClientHandlers.SendToWorld(s, 0x27E5, BuildBool(id, false));
                }
                break;
            case "set_go":
                // Arb044:3613: absent/non-on argument means off; trailing arguments are ignored.
                bool enabled = line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase);
                int level = enabled ? 5 : 0;
                store?.SetCharacterAdminLevel(id, level);
                var p = new byte[8]; BitConverter.GetBytes(id).CopyTo(p, 0); BitConverter.GetBytes(level).CopyTo(p, 4);
                ArbiterClientHandlers.SendToWorld(s, 0x1578, p);
                GmCommandHandlers.SendCustom(s, "set_go : " + (enabled ? "on" : "off"));
                break;
            case "set_new_member":
            case "set_pcbang":
                if (line.Args.Count != 1) break;
                // These native parsers compare literal lowercase 'on'; any other token selects false.
                bool value = line.Arg(0) == "on", pcBang = line.Name.Equals("set_pcbang", StringComparison.OrdinalIgnoreCase);
                uint mask = pcBang ? 8u : 16u;
                bool changed;
                lock (Gate)
                {
                    Flags.TryGetValue(s.Account.Name, out var old);
                    changed = ((old.Values & mask) != 0) != value;
                    Flags[s.Account.Name] = new(value ? old.Values | mask : old.Values & ~mask, old.Known | mask);
                }
                // Arb044:4181/4346: new-member always pushes; PC-bang only pushes a transition.
                if (!pcBang || changed) ArbiterClientHandlers.SendToWorld(s, pcBang ? (ushort)0x14B5 : (ushort)0x14B4, BuildBool(id, value));
                break;
            case "usage": // Arb044:5658-5668 is only the scope tracer: native no-op, no synthetic reply.
                log.LogDebug("usage: native handler is empty in this build");
                break;
        }
        return true;
    }

    /// <summary>Arb029:16441: [ref26][raw byte length][UserDbId][u64 money], then native536-byte ItemData records.</summary>
    public static byte[] BuildRefreshInventory(int id, long money, IReadOnlyList<CharacterStore.ItemRow> rows)
    {
        byte[] records = BagItems.BuildPayload(rows, 0, id)[BagItems.PayloadHeader..];
        var p = new byte[20 + records.Length];
        BitConverter.GetBytes(26).CopyTo(p, 0); BitConverter.GetBytes(records.Length).CopyTo(p, 4);
        BitConverter.GetBytes(id).CopyTo(p, 8); BitConverter.GetBytes(money).CopyTo(p, 12); records.CopyTo(p, 20);
        return p;
    }

    public static byte[] BuildBool(int id, bool value)
    {
        var p = new byte[5]; BitConverter.GetBytes(id).CopyTo(p, 0); p[4] = value ? (byte)1 : (byte)0; return p;
    }

    /// <summary>Native _wtol: signed decimal prefix, invalid=0, clamped to Windows32-bit long.</summary>
    public static int NativeInt(string value)
    {
        value = value.TrimStart(); int end = value.Length > 0 && (value[0] == '-' || value[0] == '+') ? 1 : 0;
        int start = end;
        while (end < value.Length && value[end] is >= '0' and <= '9') end++;
        if (end == start) return 0;
        if (!long.TryParse(value[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
            return value[0] == '-' ? int.MinValue : int.MaxValue;
        return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
    }

    /// <summary>AS_ENTER_WORLD payload92/93 are these same account-runtime flags; preserve unmodified fields.</summary>
    public static void StampEnterWorldFlags(byte[] payload, CharacterStore? store)
    {
        if (payload.Length < 108 || store == null) return;
        string? account = store.GetAccountById(store.AccountOf(BitConverter.ToInt32(payload, 32)))?.Name;
        if (account == null) return;
        lock (Gate)
        {
            if (!Flags.TryGetValue(account, out var flags)) return;
            if ((flags.Known & 8) != 0) payload[92] = (byte)((flags.Values & 8) != 0 ? 1 : 0);
            if ((flags.Known & 16) != 0) payload[93] = (byte)((flags.Values & 16) != 0 ? 1 : 0);
            if ((flags.Known & 0x70000) != 0) payload[107] = (byte)((flags.Values >> 16) & 7);
        }
    }

    /// <summary>Native set_account_res_level ORs three bits into account flags; no database write.</summary>
    public static void SetRestrictionLevel(GameSession s, uint level)
    {
        lock (Gate)
        {
            Flags.TryGetValue(s.Account.Name, out var old);
            Flags[s.Account.Name] = new(old.Values | ((level & 7) << 16), old.Known | 0x70000);
        }
    }

    internal static void ResetForTests() { lock (Gate) Flags.Clear(); }
}
