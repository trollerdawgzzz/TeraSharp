// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_existing_commands_dispatch_native_messages_and_preserve_account_admin_policy()
    {
        using var h = new T201AccountHarness();
        long previousCap = GmCommandHandlers.WarehouseGoldMax;
        try
        {
            T181WithOperators("t39", () =>
            {
                h.Run("query_point");
                Hex.Eq(h.Client.Frame(), Convert.FromHexString("2C004C990600430061006E002700740020007200650071007500650073007400200063006F0069006E000000"),
                    "Arb044:262 billing request failure uses native custom text");
                h.Run("create_user");
                Hex.True(Encoding.Unicode.GetString(h.Client.Frame()[6..^2]) == GmCommandHandlers.CreateUserUsage, "native missing-name usage");
                string existing = h.Store.GetCharacter(1)!.Name;
                h.Run("create_user " + existing);
                Hex.True(Encoding.Unicode.GetString(h.Client.Frame()[6..^2]) == GmCommandHandlers.AlreadyExistsMessage(existing),
                    "native duplicate-name refusal does not create another character");
                h.Run("set_admin_level " + existing + " 3");
                Hex.True(h.Store.GetAdminLevel(1) == 3 && Encoding.Unicode.GetString(h.Client.Frame()[6..^2])
                    == "Admin level of [" + existing + "] is now 3\n", "documented TeraSharp account-scope compatibility path remains effective");
                h.Run("warehousegold_max 123"); h.Run("testitem ignored arguments");
                Hex.True(GmCommandHandlers.WarehouseGoldMax == 123 && h.Client.Available == 0
                    && h.Main.Available == 0 && h.Instance.Available == 0, "native setter and empty testitem send no invented confirmation");
            });
            h.Store.SetAdminLevel(1, 0);
            T181WithOperators(null, () =>
            {
                foreach (string command in new[] { "query_point", "create_user", "set_admin_level g1 5", "warehousegold_max 1", "testitem" }) h.Run(command);
            });
            Hex.True(h.Store.GetAdminLevel(1) == 0 && GmCommandHandlers.WarehouseGoldMax == 123
                && h.Client.Available == 0 && h.Main.Available == 0 && h.Instance.Available == 0, "ordinary account is silently refused before each action");
        }
        finally { GmCommandHandlers.WarehouseGoldMax = previousCap; }
    }

    [Test] public static void T201_warehouse_QA_cap_refuses_whole_transaction_without_partial_mutation()
    {
        using var h = new T201AccountHarness();
        long previousCap = GmCommandHandlers.WarehouseGoldMax;
        try
        {
            h.Store.AddWarehouseMoney(1, 1, 80); h.Store.AddCharacterMoney(1, 1_000);
            long gold = h.Store.GetCharacter(1)!.Money;
            h.Store.UpsertItem(111, 1, 0, 0, 7, 1);
            T181WithOperators("t39", () => h.Run("warehousegold_max 100"));
            byte[] request = T42StorePayload(
                (9, 0, 0, 1, 0, 1, 0, 0, -21),
                (14, 111, 7, 1, 0, 1, 1, 0, 1),
                (13, 0, 0, 1, 1, 1, 1, 0, 21));
            var (opcode, reply) = RunHandler1(0x274C, request, h.Store);
            Hex.True(opcode == 0x274D, "native warehouse store reply opcode");
            Hex.Eq(reply, Convert.FromHexString("1F000000080A0000CDAB0000001D0000000000000000000000")
                .Concat(request[WarehouseHandlers.StoreRequestSize..]).ToArray(),
                "Arb038:4403-4478 + Arb048:13470-13537: false/error29, same ordered atoms and no commission");
            Hex.True(h.Store.GetWarehouse(1, 1).Money == 80 && h.Store.GetCharacter(1)!.Money == gold
                && h.Store.GetItem(111)!.InvenType == 0, "later cap rejection cannot debit character gold or move earlier item atom");
            byte[] accepted = T42StorePayload((9, 0, 0, 1, 0, 1, 0, 0, -20), (13, 0, 0, 1, 1, 1, 1, 0, 20));
            var (_, success) = RunHandler1(0x274C, accepted, h.Store);
            Hex.True(success[12] == 1 && BitConverter.ToUInt32(success, 13) == 0 && h.Store.GetWarehouse(1, 1).Money == 100
                && h.Store.GetCharacter(1)!.Money == gold - 20, "exactly at the configured cap succeeds");
            var (_, negative) = RunHandler1(0x274C, T42StorePayload((13, 0, 0, 1, 1, 1, 1, 0, -101)), h.Store);
            Hex.True(negative[12] == 0 && BitConverter.ToUInt32(negative, 13) == 0 && h.Store.GetWarehouse(1, 1).Money == 100,
                "native negative balance fails without assigning over-cap error29");
            T181WithOperators("t39", () => h.Run("warehousegold_max 0"));
            var (_, defaultLimit) = RunHandler1(0x274C, T42StorePayload((13, 0, 0, 1, 1, 1, 1, 0, 99_999_999_999_901L)), h.Store);
            Hex.True(defaultLimit[12] == 0 && BitConverter.ToUInt32(defaultLimit, 13) == 29 && h.Store.GetWarehouse(1, 1).Money == 100,
                "zero restores native100000000000000 rather than removing the cap");
            Hex.True(h.Client.Available == 0 && h.Main.Available == 0 && h.Instance.Available == 0, "QA setting itself remains silent");
        }
        finally { GmCommandHandlers.WarehouseGoldMax = previousCap; }
    }
}
