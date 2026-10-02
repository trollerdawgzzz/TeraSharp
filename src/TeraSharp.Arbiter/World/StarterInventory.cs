// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>One item in a class's starting kit, already placed.</summary>
/// <param name="TemplateId">itemTemplateId from CreateCharData.xml.</param>
/// <param name="Amount">amount from CreateCharData.xml (1 for gear, 20 for the potions).</param>
/// <param name="Pocket">Item record +28: 0 = bag, 14 = worn.</param>
/// <param name="Slot">Item record +36: bag position, or the INVTYPE for a worn item.</param>
public readonly record struct StarterItem(int TemplateId, int Amount, int Pocket, int Slot);

/// <summary>
/// The per-class starting inventory, and the code that renders it as a
/// <c>DBS_USER_LOAD_INVENTORY</c> (0x27A4) payload.
///
/// <para><b>Where the table comes from.</b> The real Arbiter builds a new character's items in
/// <c>Handler_C_CREATE_USER</c> -> <c>CreateUserCallback</c> ->
/// <c>DatasheetManager::GetCreateCharData(classId, CreateCharData&amp;)</c> ->
/// <c>AccountManager::CreateUser_FillInitData</c> -> <c>AccountManager::ExecCreateInitItems</c>,
/// which writes them straight to its own DB (that is why the six items are already in the very
/// first 0x27A4 of cap_newchar.log, with no 0x2768 before it). The list it reads is the
/// <c>CreateCharData</c> datasheet, on disk at
/// <c>D:\v100\TERA_SERVER.100\Executable\Datasheet\CreateCharData.xml</c>:</para>
/// <code>
///   &lt;Char class="glaiver" createdLevel="1"&gt;
///     &lt;InitItem itemTemplateId="59053" initWear="true"  amount="1"  /&gt;
///     ...
///     &lt;InitItem itemTemplateId="6550"  initWear="false" amount="20" /&gt;
/// </code>
/// <para>It is keyed by <b>class alone</b> — there is no race or gender dimension, so a "starter
/// kit per (race, class)" is really per class. <see cref="ByClass"/> below is that file,
/// transcribed; the class ids are the order of the if-chain in <c>DataSheetReadClass</c>.</para>
///
/// <para><b>Where the placement comes from.</b> <c>ExecCreateInitItems</c> wears an item when
/// <c>initWear</c> is set AND the item has an equipment part, which it turns into an inventory
/// slot with <c>GetInvenTypeFromEquipPart(EquipmentPart)</c>. The part comes from
/// <c>ItemTemplate.xml</c>'s <c>combatItemType</c> (EQUIP_WEAPON / EQUIP_ARMOR_BODY / _ARM /
/// _LEG for everything in this table) and the INVTYPE numbering is spelled out in the comment
/// block at the top of <c>ItemEquipRestriction.xml</c>:
/// <c>NON_EQUIP 0, WEAPON 1, HEAD 2, BODY 3, HANDS 4, FEET 5, …</c> — which is exactly what the
/// capture shows (59053 at slot 1, 15004/5/6 at 3/4/5). Non-equippable items go to the bag at
/// consecutive slots from 0.</para>
///
/// <para><b>Marked as inferred</b>, because the capture only pins the six-item case:
/// <list type="bullet">
/// <item>Pocket <see cref="EquippedPocket"/> = 14 for worn items is taken from the captured
/// records, not derived. It is NOT an INVTYPE (14 is STYLE_HAIR there); the field is something
/// else that happens to be 14 on all four worn records and 0 on both bag records.</item>
/// <item>Bag items get slots 0, 1, 2 … in datasheet order. The capture only ever shows two bag
/// items (6550 at slot 0, 6560 at slot 1, in datasheet order), so slots 2+ — which only
/// <c>soulless</c> uses — are an extension of that pattern.</item>
/// <item>The 536-byte records for bag slots 2+ are cloned from the captured bag record, since
/// there is no captured record at those positions. See <see cref="BaseRecordFor"/>.</item>
/// </list></para>
/// </summary>
public static class StarterInventory
{
    /// <summary>Item record +28 for a worn item (from the capture; see the class remarks).</summary>
    public const int EquippedPocket = 14;
    /// <summary>Item record +28 for an item in the bag.</summary>
    public const int BagPocket = 0;

    // --- 536-byte record fields we patch (status/INVENTORY-DESIGN.md section 2) ---
    public const int RecordIdOffset = 0;
    public const int RecordTemplateIdOffset = 8;
    public const int RecordAmountOffset = 24;
    public const int RecordPocketOffset = 28;
    public const int RecordSlotOffset = 36;

    /// <summary>
    /// First item DB id in a starter kit. The capture allocated 7..12 for "Test", and
    /// <see cref="Persistence.CharacterStore.FirstItemId"/> (1000) deliberately starts above
    /// them. Starter ids are deterministic rather than drawn from that counter: they have to be
    /// the same on every login for the same character, and with no items table behind them a
    /// fresh id per load would hand World a different id for the same item each time.
    /// </summary>
    /// <para><b>T105:</b> these are the ids of the PAYLOAD only. They are the same for every
    /// character, so they must never reach the items table as they are - seeding renumbers them
    /// from the item-id counter (<c>DbProxyHandlers.SeedStarterRows</c>). Before that, seeding a
    /// second character re-owned the first one's six rows and emptied its bag.</para>
    public const int FirstStarterItemId = 7;

    /// <summary>
    /// Class ids, in the order of the if-chain in the Arbiter's <c>DataSheetReadClass</c> —
    /// which is also what <c>CharacterHandlers.ComputeTemplateId</c> assumes (race 4, gender 1,
    /// class 12 -> templateId 11013 -> the capture's Elin female glaiver).
    /// </summary>
    public static readonly string[] ClassNames =
    {
        "warrior", "lancer", "slayer", "berserker", "sorcerer", "archer", "priest",
        "elementalist", "soulless", "engineer", "fighter", "assassin", "glaiver",
    };

    /// <summary>
    /// CreateCharData.xml, one row per class, in datasheet order — which is the order ids are
    /// allocated in. The <c>hero</c> row in the file is deliberately absent: "hero" is not a name
    /// <c>DataSheetReadClass</c> knows, so the real Arbiter cannot parse that row either.
    /// <para>T159: read from the sheet (<see cref="DatasheetLoader.StarterKits"/>); the table
    /// below is the sheet as transcribed, used only when the sheet is missing.</para>
    /// </summary>
    private static StarterItem[][] ByClass => DatasheetLoader.StarterKits.Value;

    /// <summary>
    /// The transcribed CreateCharData.xml - the built-in for <see cref="ByClass"/>.
    ///
    /// <para>T228: item 200999 is the level scroll, and the shipped sheet gave it only to soulless -
    /// the one class with <c>createdLevel=50</c>, which gets five. No other class had one, in the
    /// sheet or here, which is why no new character of any other class has ever received it: nothing
    /// was being dropped. Every other class now carries one, at bag slot 2 (slots 0 and 1 are the
    /// two potion stacks). tools/add-starter-scroll.py puts the same row in the sheet, which
    /// overrides this table whenever it loads.</para>
    /// </summary>
    public static readonly StarterItem[][] BuiltInKits =
    {
        new StarterItem[] { new(10001, 1, 14, 1), new(15004, 1, 14, 3), new(15005, 1, 14, 4), new(15006, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // warrior
        new StarterItem[] { new(10002, 1, 14, 1), new(15001, 1, 14, 3), new(15002, 1, 14, 4), new(15003, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // lancer
        new StarterItem[] { new(10003, 1, 14, 1), new(15004, 1, 14, 3), new(15005, 1, 14, 4), new(15006, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // slayer
        new StarterItem[] { new(10004, 1, 14, 1), new(15001, 1, 14, 3), new(15002, 1, 14, 4), new(15003, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // berserker
        new StarterItem[] { new(10005, 1, 14, 1), new(15007, 1, 14, 3), new(15008, 1, 14, 4), new(15009, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // sorcerer
        new StarterItem[] { new(10006, 1, 14, 1), new(15004, 1, 14, 3), new(15005, 1, 14, 4), new(15006, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // archer
        new StarterItem[] { new(10007, 1, 14, 1), new(15007, 1, 14, 3), new(15008, 1, 14, 4), new(15009, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // priest
        new StarterItem[] { new(10008, 1, 14, 1), new(15007, 1, 14, 3), new(15008, 1, 14, 4), new(15009, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // elementalist
        new StarterItem[] { new(80396, 1, 14, 1), new(80397, 1, 14, 3), new(80398, 1, 14, 4), new(80399, 1, 14, 5), new(6551, 20, 0, 0), new(6561, 20, 0, 1), new(362, 10, 0, 2), new(391, 3, 0, 3), new(200999, 5, 0, 4) },   // soulless
        new StarterItem[] { new(55005, 1, 14, 1), new(15001, 1, 14, 3), new(15002, 1, 14, 4), new(15003, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // engineer
        new StarterItem[] { new(82005, 1, 14, 1), new(15001, 1, 14, 3), new(15002, 1, 14, 4), new(15003, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // fighter
        new StarterItem[] { new(58171, 1, 14, 1), new(15007, 1, 14, 3), new(15008, 1, 14, 4), new(15009, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // assassin
        new StarterItem[] { new(59053, 1, 14, 1), new(15004, 1, 14, 3), new(15005, 1, 14, 4), new(15006, 1, 14, 5), new(6550, 20, 0, 0), new(6560, 20, 0, 1), new(200999, 1, 0, 2) },   // glaiver
    };

    /// <summary>
    /// T162. CreateCharData.xml's <c>createdLevel</c> per class id, transcribed - the built-in for
    /// <see cref="DatasheetLoader.CreatedLevels"/>. Every row says 1 except soulless (50).
    /// </summary>
    public static readonly int[] BuiltInCreatedLevels = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };  // T221/T231: Reaper starts at 1

    /// <summary>The level a new character of this class is made at: the sheet's createdLevel, 1
    /// for a class id outside the 13.</summary>
    public static int CreatedLevelFor(int classId)
    {
        var levels = DatasheetLoader.CreatedLevels.Value;
        return classId >= 0 && classId < levels.Length ? levels[classId] : 1;
    }

    /// <summary>INVTYPE slots of the four worn starter items, as ItemEquipRestriction.xml numbers
    /// them and as the capture places them: WEAPON 1, BODY 3, HANDS 4, FEET 5 (HEAD 2 is unused -
    /// no class starts with a helmet). The same four <see cref="DatasheetLoader.WornSlots"/> uses.
    /// </summary>
    public const int WeaponSlot = 1, BodySlot = 3, HandSlot = 4, FeetSlot = 5;

    /// <summary>
    /// T232. The class's four worn starter items, as the <c>characters</c> row's look columns want
    /// them: <c>weapon, body, hand, feet</c>.
    ///
    /// <para>Those four columns are what the lobby list and <c>InventoryHandlers.SendInventory</c>
    /// read, and nothing had ever written them - <c>BuildRecord</c> left them at the column default
    /// of 0, so every character this server created said it was wearing item 0 while its actual
    /// inventory (the kit) said otherwise. The kit is the only honest source: it is the same
    /// <c>CreateCharData.xml</c> rows World is handed in <c>DBS_USER_LOAD_INVENTORY</c>, so the row
    /// and the inventory now agree by construction rather than by coincidence.</para>
    ///
    /// <para>Read off <see cref="EquippedPocket"/> and the INVTYPE slot, not off position in the
    /// kit: a sheet row with <c>initWear="true"</c> past the fourth goes to the bag
    /// (<see cref="DatasheetLoader.ReadStarterKits"/>), and soulless carries nine items. A class id
    /// outside the 13, or a kit missing a slot, yields 0 for that slot - which is what the column
    /// held before, so a class we cannot place is no worse off.</para>
    /// </summary>
    public static (int Weapon, int Body, int Hand, int Feet) WornLook(int classId)
        => WornLookOf(ForClass(classId));

    /// <summary>T232c: the same, over a kit the caller already holds - so the sheet reader can check
    /// a row it has just parsed without going back through <see cref="ForClass"/>.</summary>
    public static (int Weapon, int Body, int Hand, int Feet) WornLookOf(IReadOnlyList<StarterItem>? kit)
    {
        int weapon = 0, body = 0, hand = 0, feet = 0;
        if (kit != null)
        {
            foreach (var it in kit)
            {
                if (it.Pocket != EquippedPocket) continue;
                switch (it.Slot)
                {
                    case WeaponSlot: weapon = it.TemplateId; break;
                    case BodySlot:   body = it.TemplateId;   break;
                    case HandSlot:   hand = it.TemplateId;   break;
                    case FeetSlot:   feet = it.TemplateId;   break;
                }
            }
        }
        return (weapon, body, hand, feet);
    }

    /// <summary>The kit for a class, or null when the class id is not one of the 13.</summary>
    public static IReadOnlyList<StarterItem>? ForClass(int classId)
        => classId >= 0 && classId < ByClass.Length ? ByClass[classId] : null;

    /// <summary>
    /// Render a class's starting inventory as a 0x27A4 payload:
    /// <c>[u32 listOff=19][u32 count*536][u32 reqId][u8 0]</c> then the item records.
    /// Returns null for an unknown class, so the caller can fall back.
    ///
    /// <para>Ids are assigned in <b>datasheet order</b> and the records are emitted sorted by
    /// <b>(pocket, slot)</b> — the two orders differ, and both are forced by the capture: the
    /// glaiver kit lists 59053 first and gets id 7, while the 0x27A4 payload starts with the
    /// potions (ids 11 and 12) because they sit at pocket 0.</para>
    /// </summary>
    /// <param name="capturedPayload">data/starter_inventory.bin — the 3229-byte captured 0x27A4
    /// payload, used for its six item records (see <see cref="BaseRecordFor"/>).</param>
    public static byte[]? Build(byte[] capturedPayload, int classId, int playerId, uint reqId)
    {
        ArgumentNullException.ThrowIfNull(capturedPayload);
        var kit = ForClass(classId);
        if (kit == null) return null;

        // Ids follow the datasheet order the real Arbiter created the rows in.
        var ids = new int[kit.Count];
        for (int i = 0; i < kit.Count; i++) ids[i] = FirstStarterItemId + i;

        // The wire order is by position, not creation order.
        var order = new int[kit.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => kit[a].Pocket != kit[b].Pocket
            ? kit[a].Pocket.CompareTo(kit[b].Pocket)
            : kit[a].Slot.CompareTo(kit[b].Slot));

        int size = DbProxyHandlers.StarterInventoryItemSize;
        int header = DbProxyHandlers.StarterInventoryItemStart;   // 13
        var payload = new byte[header + kit.Count * size];
        BitConverter.GetBytes(19u).CopyTo(payload, 0);                       // list offset (frame-relative)
        BitConverter.GetBytes((uint)(kit.Count * size)).CopyTo(payload, 4);  // list length
        BitConverter.GetBytes(reqId).CopyTo(payload, 8);
        payload[12] = 0;                                                     // flag: 0 in the capture

        for (int n = 0; n < order.Length; n++)
        {
            var item = kit[order[n]];
            int at = header + n * size;
            BaseRecordFor(capturedPayload, item.Pocket, item.Slot).CopyTo(payload.AsSpan(at));
            BitConverter.GetBytes(ids[order[n]]).CopyTo(payload, at + RecordIdOffset);
            BitConverter.GetBytes(item.TemplateId).CopyTo(payload, at + RecordTemplateIdOffset);
            BitConverter.GetBytes(playerId).CopyTo(payload, at + DbProxyHandlers.StarterInventoryOwnerOffset);
            BitConverter.GetBytes(item.Amount).CopyTo(payload, at + RecordAmountOffset);
            BitConverter.GetBytes(item.Pocket).CopyTo(payload, at + RecordPocketOffset);
            BitConverter.GetBytes(item.Slot).CopyTo(payload, at + RecordSlotOffset);
        }
        return payload;
    }

    /// <summary>
    /// T209: the same payload with every 536-byte record BUILT rather than copied out of the
    /// capture. <see cref="WarehouseHandlers.BuildItemRecord"/> writes the named fields and
    /// leaves the rest zero, which is what <c>BagItems</c> already serves for any row whose
    /// stored record was lost - so this path is not new code, only newly reachable at creation.
    ///
    /// <para>It is behind <c>economy.synthItemRecords</c> because the copied record is the one
    /// live-verified to get past <c>SA_ENTER_WORLD_FAILED</c>: about 400 of the 536 bytes are
    /// zero either way, but the remainder is uninitialised Arbiter heap and no capture proves
    /// World ignores all of it on the CREATION path. Default off; turn it on to run with no
    /// <c>starter_inventory.bin</c> at all.</para>
    /// </summary>
    public static byte[]? BuildSynthetic(int classId, int playerId, uint reqId)
    {
        var kit = ForClass(classId);
        if (kit == null) return null;

        var order = new int[kit.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => kit[a].Pocket != kit[b].Pocket
            ? kit[a].Pocket.CompareTo(kit[b].Pocket)
            : kit[a].Slot.CompareTo(kit[b].Slot));

        int size = DbProxyHandlers.StarterInventoryItemSize;
        int header = DbProxyHandlers.StarterInventoryItemStart;
        var payload = new byte[header + kit.Count * size];
        BitConverter.GetBytes(19u).CopyTo(payload, 0);
        BitConverter.GetBytes((uint)(kit.Count * size)).CopyTo(payload, 4);
        BitConverter.GetBytes(reqId).CopyTo(payload, 8);
        payload[12] = 0;

        for (int n = 0; n < order.Length; n++)
        {
            var item = kit[order[n]];
            int at = header + n * size;
            WarehouseHandlers.BuildItemRecord(FirstStarterItemId + order[n], item.TemplateId,
                playerId, item.Amount, item.Pocket, item.Slot).CopyTo(payload.AsSpan(at));
        }
        return payload;
    }

    /// <summary>
    /// The 536-byte record to start from for a given position.
    ///
    /// <para>We cannot synthesise one: about 40 of the 536 bytes are named, ~400 are zero, and
    /// the rest is uninitialised Arbiter heap (the capture's records carry recognisable
    /// fragments of its own SQL — status/INVENTORY-DESIGN.md section 2). So every record starts
    /// as one the real server actually sent and only the six named fields are patched.</para>
    ///
    /// <para>Exact match on (pocket, slot) first — that is what makes the glaiver kit come back
    /// byte-identical to the capture. Otherwise the first record from the same pocket, which is
    /// <b>inferred</b>: it only happens for bag slots 2+, i.e. soulless.</para>
    /// </summary>
    public static ReadOnlySpan<byte> BaseRecordFor(byte[] capturedPayload, int pocket, int slot)
    {
        int size = DbProxyHandlers.StarterInventoryItemSize;
        int start = DbProxyHandlers.StarterInventoryItemStart;
        int count = (capturedPayload.Length - start) / size;

        int samePocket = -1;
        for (int i = 0; i < count; i++)
        {
            int at = start + i * size;
            int p = BitConverter.ToInt32(capturedPayload, at + RecordPocketOffset);
            if (p != pocket) continue;
            if (BitConverter.ToInt32(capturedPayload, at + RecordSlotOffset) == slot)
                return capturedPayload.AsSpan(at, size);
            if (samePocket < 0) samePocket = at;
        }
        if (samePocket >= 0) return capturedPayload.AsSpan(samePocket, size);
        return capturedPayload.AsSpan(start, size);
    }
}
