// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging.Abstractions;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Web;

namespace TeraSharp.Arbiter.Tests;

// ===================== T228 A/C: the lobby slot, and the level scroll =====================
//
// (A) Reported as "the slot limit is not enforced: I can create without limit, then World refuses
// entry with 'delete 1 character before entering world'". The count check was never the problem.
// arbiter-t228.log, one session:
//
//   C_LOGIN_ARBITER: account '1' (id 1) language 6, 3 character(s)
//   C_GET_USER_LIST -> 3 character(s)      ... later 4, then 5
//   C_CAN_CREATE_USER from ... -> ok=True (4/8)
//   C_CREATE_USER ...: created 'gun'  id=17 template=10210 ... slot=10
//   C_CREATE_USER ...: created 'valk' id=18 template=10213 ... slot=11
//
// Four of eight used, and the new characters land at ordinals 10 and 11. NextPosition was
// MAX(position)+1, which never reuses the slot a deleted character freed, so the ordinal space
// drifts away from the count. terasharp.db agrees: account 1's five live characters sit at
// positions 5, 7, 8, 9, 10 while account 2's four sit at 1, 2, 3, 4. World's own slot limit reads
// the ORDINAL, so it refuses a character the Arbiter was happy to create.
//
// So: allocate the lowest free ordinal in 1..slots and refuse on 0 (the count and the ordinal
// space disagree by design - a pending-delete character keeps its slot while the lobby stops
// listing it, the T88 rule), and repair the accounts that already drifted.
//
// The slot count itself was a flat MaxCharactersPerAccount = 8 with the sheet value the loader
// already parses thrown away. AccountTrait.xml package 0 carries expandCharacterSlot slot="3" -
// retail's base - and packages 34/102/334/434/435 carry 8, 436 carries 10, 437 carries 12, where
// `slot` is the TOTAL the package confers. Reading the sheet alone would shrink every unpackaged
// account from 8 to 3, so the base is floored at 8 and packages raise it from there.
//
// (C) Reported as "the level-70 scroll doesn't arrive for ANY new character". Nothing was being
// dropped: CreateCharData.xml's glaiver row asks for six items and the log delivered six
// ("SDB_USER_LOAD_INVENTORY: player 18 -> 6 starter items for class 12 (glaiver)"), and
// BuiltInKits transcribed the same six. Item 200999 was in the soulless row only - the one class
// with createdLevel=50, which gets five. It was never in anyone else's row.
// =========================================================================================

public static partial class Tests
{
    /// <summary>A store whose one account holds characters at exactly these lobby ordinals.</summary>
    private static CharacterStore StoreAtPositions(out long accountId, params int[] positions)
    {
        string path = Path.Combine(Path.GetTempPath(), "t228-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new CharacterStore(path, NullLogger.Instance);
        accountId = store.GetOrCreateAccount("slots").Id;
        for (int i = 0; i < positions.Length; i++)
            store.CreateCharacter(new CharacterRecord
            {
                AccountId = accountId,
                Name = "c" + positions[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                TemplateId = 10101, Level = 1, Position = positions[i],
            });
        return store;
    }

    [Test] public static void T228_the_lobby_slot_is_the_lowest_free_ordinal_not_max_plus_one()
    {
        using var store = StoreAtPositions(out long account, 5, 7, 8, 9, 10);

        Hex.True(store.NextPosition(account, 8) == 1,
            "with 5,7,8,9,10 taken the next slot is 1, not MAX+1 = 11 - the old value World refused");
        Hex.True(store.OccupiedPositions(account).SequenceEqual(new[] { 5, 7, 8, 9, 10 }),
            "and the reported occupancy is the ordinals themselves, not a count");

        using var tight = StoreAtPositions(out long full, 1, 2, 3, 4, 5, 6, 7, 8);
        Hex.True(tight.NextPosition(full, 8) == 0, "1..8 taken on eight slots is full, and 0 says so");
        Hex.True(tight.NextPosition(full, 12) == 9, "twelve slots and the same rows leaves 9 free");
        Hex.True(tight.NextPosition(full, 0) == 0, "and no slots at all is full, not an exception");
    }

    [Test] public static void T228_a_drifted_account_is_renumbered_into_1_to_n_in_lobby_order()
    {
        using var store = StoreAtPositions(out long account, 5, 7, 8, 9, 10);
        var before = store.GetCharacters(account).Select(c => c.Name).ToArray();

        Hex.True(store.CompactPositions(account) == 5, "all five rows move");
        Hex.True(store.OccupiedPositions(account).SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
            "into 1..5, so no ordinal is past the cap any more");
        Hex.True(store.GetCharacters(account).Select(c => c.Name).SequenceEqual(before),
            "and the lobby order is unchanged - the select screen must not reshuffle");
        Hex.True(store.CompactPositions(account) == 0, "running it again moves nothing");
        Hex.True(store.NextPosition(account, 8) == 6, "and the next free slot is now 6, not 1");
    }

    [Test] public static void T228_only_a_drifted_account_is_touched_at_login()
    {
        using var tidy = StoreAtPositions(out long ok, 1, 2, 3);
        Hex.True(tidy.CompactPositionsIfDrifted(ok, 8) == 0,
            "1,2,3 inside eight slots is left alone - login must not rewrite rows it need not");

        using var drifted = StoreAtPositions(out long bad, 5, 7, 8, 9, 10);
        Hex.True(drifted.CompactPositionsIfDrifted(bad, 8) == 5, "10 is past 8, so it repairs");
        Hex.True(drifted.CompactPositionsIfDrifted(bad, 8) == 0, "and is then idempotent");
    }

    [Test] public static void T228_the_slot_count_is_the_sheet_floored_at_eight_and_a_package_raises_it()
    {
        using var store = StoreAtPositions(out long account, 1);
        string dir = Path.Combine(Path.GetTempPath(), "t228-sheet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // the shipped shape: package 0 is the defaults and carries retail's base of 3
        File.WriteAllText(Path.Combine(dir, "AccountTrait.xml"), """
<AccountTraitData>
  <Package id="0"><Property name="expandCharacterSlot" slot="3" /></Package>
  <Package id="437"><Property name="expandCharacterSlot" slot="12" /></Package>
</AccountTraitData>
""");
        try
        {
            QaUtilityCommands.Sheet.Load(dir);
            Hex.True(QaUtilityCommands.Sheet.Value.BaseSlots == 3, "the sheet's base really is 3");
            Hex.True(QaUtilityCommands.CharacterSlots(store, account) == 8,
                "but it is floored at 8, so no existing account shrinks from eight slots to three");

            long future = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            store.GrantAccountBenefit(account, 437, future);
            Hex.True(QaUtilityCommands.CharacterSlots(store, account) == 12,
                "a live package raises it to the total that package confers");

            store.RevokeAccountBenefit(account, 437);
            store.GrantAccountBenefit(account, 437, DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
            Hex.True(QaUtilityCommands.CharacterSlots(store, account) == 8,
                "an EXPIRED package does not - the same IsLive rule SDB_LOAD_ACCOUNT_BENEFIT uses");

            store.SetCounterValue("character_slots_" + account, 20);
            Hex.True(QaUtilityCommands.CharacterSlots(store, account) == 20,
                "and an operator-pinned counter wins over both");
        }
        finally
        {
            QaUtilityCommands.Sheet.UseBuiltIn();
            Directory.Delete(dir, true);
        }
    }

    [Test] public static void T228_the_compact_positions_route_reports_the_ordinals_either_side()
    {
        using var store = StoreAtPositions(out long account, 5, 7, 8, 9, 10);
        var api = NewAdminApi(store);

        var bad = api.Handle("POST", "/api/compact-positions", body: "{}", token: T101Token, sourceIp: "127.0.0.1");
        Hex.True(bad.Status == 400, $"accountId is required: {bad.Body}");

        var missing = api.Handle("POST", "/api/compact-positions", body: "{\"accountId\":999}",
            token: T101Token, sourceIp: "127.0.0.1");
        Hex.True(missing.Status == 404, $"and the account has to exist: {missing.Body}");

        var res = api.Handle("POST", "/api/compact-positions",
            body: "{\"accountId\":" + account.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}",
            token: T101Token, sourceIp: "127.0.0.1");
        Hex.True(res.Status == 200 && res.Body.Contains("\"moved\":5"), $"five rows moved: {res.Body}");
        Hex.True(res.Body.Contains("\"before\":[5,7,8,9,10]") && res.Body.Contains("\"after\":[1,2,3,4,5]"),
            $"and it says which ordinals, either side: {res.Body}");
        Hex.True(store.OccupiedPositions(account).SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "for real");
    }

    [Test] public static void T228_every_class_but_soulless_gets_exactly_one_level_scroll()
    {
        var kits = World.StarterInventory.BuiltInKits;
        Hex.True(kits.Length == 13, $"thirteen classes, got {kits.Length}");
        for (int cls = 0; cls < kits.Length; cls++)
        {
            var scrolls = kits[cls].Where(i => i.TemplateId == 200999).ToArray();
            Hex.True(scrolls.Length == 1, $"class {cls} carries the level scroll exactly once");
            int want = cls == 8 ? 5 : 1;          // 8 is soulless, the class made at level 50
            Hex.True(scrolls[0].Amount == want,
                $"class {cls} gets {want} of them, not {scrolls[0].Amount}");
            var bag = kits[cls].Where(i => i.Pocket == World.StarterInventory.BagPocket)
                               .Select(i => i.Slot).ToArray();
            Hex.True(bag.Distinct().Count() == bag.Length, $"class {cls}'s bag slots stay distinct");
        }
    }
}
