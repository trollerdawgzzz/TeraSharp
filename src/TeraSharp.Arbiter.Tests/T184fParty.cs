// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T184f_matched_roster_and_names_match_both_real_members()
    {
        var defs = LoadDefinitionsOrSkip(); if (defs == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "t184f-party-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "DungeonMatching.xml"),
                "<DungeonMatching><Dungeon id='9781' matchingRoleId='1'/></DungeonMatching>");
            File.WriteAllText(Path.Combine(dir, "MatchingRoleTemplate.xml"),
                "<MatchingRoleTemplate><Role id='1'><RoleData totalUser='2' tankerMin='0' tankerMax='0' dealerMin='2' dealerMax='2' healerMin='0' healerMax='0'/></Role></MatchingRoleTemplate>");
            File.WriteAllText(Path.Combine(dir, "WorldData.xml"),
                "<WorldData><PartyLootingOption lootingType='1' exceptionGradeForDistribution='1' appliedToGearOnly='false' onlyAppropriateClassCanLoot='true' exceptionItemDistributionType='0' nonSoulboundItems='1' lootingNotAllowedDuringCombat='false'/></WorldData>");
            File.WriteAllText(Path.Combine(dir, "StrSheet_BattleField.xml"),
                "<StrSheet_BattleField>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
                    $"<String id='{10000001 + i}' string='\u968A\u4F0D{i + 1}'/>")) + "</StrSheet_BattleField>");
            DungeonMatchRules.Entry.Load(dir); DatasheetLoader.PartyLootDefaults.Load(dir); DatasheetLoader.RaidPartyNames.Load(dir);
            var pm = NewPartyManager();
            pm.Register(P(1, 1003, "New") with { Level = 70, Laurel = 0, AwakenGrade = 0, GameId = 0x80000AF00002 });
            pm.Register(P(2, 1, "dobb") with { Level = 70, Race = 3, Laurel = 0, AwakenGrade = 0, GameId = 0x80000AF00001 });
            // Native handles are opaque pointers in these 13CC requests. They must be resolved,
            // not truncated to a user id. Their floats exactly match AS_CREATE below.
            pm.ObserveEquipItemLevel(Convert.FromHexString("20E0A7CB30020000C8A6EF43"), _ => 1003);
            pm.ObserveEquipItemLevel(Convert.FromHexString("2000A6CB30020000CABA9A44"), _ => 1);
            var a = pm.FormMatchedParty(new[]
            {
                new PartyManager.MatchedMember(1003, MatchRole.Dps, pm.TrueItemLevelFor(1003), CountOfDungeonClear: 1, IsSoloMatching: true),
                new PartyManager.MatchedMember(1, MatchRole.Dps, pm.TrueItemLevelFor(1), CountOfDungeonClear: 1, IsSoloMatching: true),
            }, false, 9781);
            Hex.True(a.Rejected == null, a.Rejected ?? "formed");
            var one = Convert.FromHexString("6600C68B01003200010002000000010000002F00F00AF00A0000EB0300000100000001000000000100000000010000000000320000005E00F00A0000EB030000460000000C000000010200F00A00800000000000000000000000000000004E00650077000000");
            var two = Convert.FromHexString("9C00C68B02003200010002000000010000002F00F00AF00A0000EB0300000100000001000000000100000000010000000000320066005E00F00A0000EB030000460000000C000000010200F00A00800000000000000000000000000000004E00650077000000660000009200F00A000001000000460000000C000000010100F00A008000000100000000000000000000000064006F00620062000000");
            var names = Convert.FromHexString("7400BCCC0600080008001A001200000000008A960D4F310000001A002C002400010000008A960D4F320000002C003E003600020000008A960D4F330000003E0050004800030000008A960D4F34000000500062005A00040000008A960D4F35000000620000006C00050000008A960D4F36000000");
            // Only party-id allocation differs from this real run; every remaining byte is pinned.
            BitConverter.GetBytes(pm.FindByMember(1003)!.Id).CopyTo(one, 14);
            BitConverter.GetBytes(pm.FindByMember(1003)!.Id).CopyTo(two, 14);
            var first = a.ToClients.Where(c => c.Ticket == 1).ToArray();
            var second = a.ToClients.Where(c => c.Ticket == 2).ToArray();
            byte[] Body(ClientAction c) => WriteByDef(defs, c.PacketName, (Dictionary<string, object>)c.Fields!);
            Hex.Eq(Body(first[0]), one[4..], "cap_2man_client1:1279, one-member list 102B");
            Hex.Eq(first[1].RawPacket!, names, "cap_2man_client1:1280, six names 116B");
            Hex.Eq(Body(first[2]), two[4..], "cap_2man_client1:1281, final list 156B");
            Hex.Eq(first[3].RawPacket!, names, "cap_2man_client1:1282");
            Hex.Eq(Body(second[0]), two[4..], "cap_2man_client2:910, same final list");
            Hex.Eq(second[1].RawPacket!, names, "cap_2man_client2:911");
            Hex.True(a.ToWorld.Select(w => w.Opcode).SequenceEqual(new ushort[]
                { 0x139E, 0x13AD, 0x13AD, 0x15CD, 0x15CD, 0x15CD, 0x15CD }),
                "cap_2man records1921/1922/1924/1928: create, refreshes, two matching-clear phases");
            var clears = a.ToWorld.Where(w => w.Opcode == 0x15CD).ToArray();
            for (int i = 0; i < clears.Length; i++)
                Hex.Eq(clears[i].Payload, i % 2 == 0 ? "EB 03 00 00 00" : "01 00 00 00 00",
                    "each phase clears New then dobb on World's control link");
            var create = a.ToWorld.Single(w => w.Opcode == 0x139E).Payload;
            Hex.True(BitConverter.ToInt32(create, 0x32 + 0x20) == 1
                && BitConverter.ToInt32(create, 0x32 + 0xA0 + 0x20) == 1,
                "cap_2man1921: matched DPS role enters both World member records, not -1");
            var expectedCreate = Convert.FromHexString(
                "780100009E133800000040010000010000002F00F00AF00A0000F00A0000EB03000002000000000000000135260000000000000000000000F00A0000EB0300000200F00A00800000460000000C0000000400000001000000010000004E00650077000000000000000000000000000000000000000000000000000000000000000000000000000000000000001900170000003400000000000000000000000000EA0709001900000101879CAF0000000000000000C8A6EF4301000000000000000000000000000000000143FC300200000000000000000000F00A0000010000000100F00A00800000460000000C00000003000000010000000100000064006F00620062000000650077000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001010000000000000000000000CABA9A44010000000000000000000000000000000001DDEF000000000000000000000000");
            BitConverter.GetBytes(pm.FindByMember(1003)!.Id).CopyTo(expectedCreate, 14);
            // Native constructor Arb068:14433-14464 initializes every semantic field but
            // wcsncpy_s leaves name capacity beyond NUL untouched; alignment bytes are also
            // untouched. Keep 0x98..9F in the comparison: Arb001:4648 initializes them zero.
            foreach (int start in new[] { 56, 216 })
            {
                int end = start + 0x24;
                while (BitConverter.ToUInt16(expectedCreate, end) != 0) end += 2;
                end += 2;
                Array.Clear(expectedCreate, end, start + 0x6E - end);
                Array.Clear(expectedCreate, start + 0x71, 3);
                Array.Clear(expectedCreate, start + 0x92, 6);
            }
            Hex.Eq(create, expectedCreate[6..],
                "cap_2man1921 AS_CREATE all semantic fields, only partyId and proven unused bytes normalized");
        }
        finally
        {
            DungeonMatchRules.Entry.UseBuiltIn(); DatasheetLoader.PartyLootDefaults.UseBuiltIn(); DatasheetLoader.RaidPartyNames.UseBuiltIn();
            foreach (string file in new[] { "DungeonMatching.xml", "MatchingRoleTemplate.xml", "WorldData.xml", "StrSheet_BattleField.xml" })
                File.Delete(Path.Combine(dir, file));
            Directory.Delete(dir);
        }
    }

    [Test] public static void T184f_item_level_updates_resolve_the_native_handle_without_reply()
    {
        var pm = NewPartyManager(); pm.Register(P(1, 1003, "New"));
        // cap_2man raw695 full frame12000000CC13; raw184 is the other user's value.
        var payload = Convert.FromHexString("20E0A7CB30020000C8A6EF43");
        Hex.True(pm.ObserveEquipItemLevel(payload, h => h == 0x00000230CBA7E020 ? 1003 : null),
            "13CC resolves the opaque Arbiter handle");
        Hex.Eq(BitConverter.GetBytes(pm.TrueItemLevelFor(1003)), "C8 A6 EF 43", "captured float bits retained");
        Hex.True(!pm.ObserveEquipItemLevel(payload[..11], _ => 1003)
            && !pm.ObserveEquipItemLevel(payload, _ => null), "short and unknown handles do not mutate users");
        Hex.True(pm.TrueItemLevelFor(1) == 0, "unknown user has no invented item level");
        pm.Unregister(1);
        Hex.True(pm.TrueItemLevelFor(1003) == 0, "a departed session does not leak stale item level into another login");
    }

    [Test] public static void T184f_only_matched_creation_enables_clear_compensation()
    {
        var pm = NewPartyManager();
        pm.Register(P(1, 1003, "New")); pm.Register(P(2, 1, "dobb"));
        var normal = pm.OnWorldFrame(PartyPackets.SA_JOIN_PARTY, SaJoinPartyPayload(1003, 1));
        Hex.True(normal.ToWorld.Single(w => w.Opcode == 0x139E).Payload[0x24] == 0,
            "ordinary-party constructor default remains false");
        var matched = pm.FormMatchedParty(T138dMembers((1003, MatchRole.Dps), (1, MatchRole.Dps)),
            false, dungeonId: 0, battleFieldId: 118);
        Hex.True(matched.ToWorld.Single(w => w.Opcode == 0x139E).Payload[0x24] == 1,
            "MatchServer257253 writes literal1 even for battlefield caller234353");
    }

    [Test] public static void T184f_reset_broadcast_and_withdrawal_use_native_world_destinations()
    {
        var sent = new List<(int World, ushort Op, byte[] Payload)>();
        void Send(int world, ushort op, byte[] body) => sent.Add((world, op, body));
        byte[] reset = PartyPackets.BuildAsResetAllDungeon(2800, 1003, 42, true, 2);
        Hex.True(PartyWiring.RouteWorldAction(0x13B9, reset, id => id is 0 or 12,
            _ => 12, Send) && sent.Select(x => x.World).SequenceEqual(new[] { 0, 12 }),
            "Arb_part_041:10100-10114: reset reaches each registered World once");
        sent.Clear();
        var penalty = PartyPackets.BuildAsNotifyAboutSysPartyWithdrawal(2800, 1003);
        Hex.True(PartyWiring.RouteWorldAction(0x13F5, penalty, _ => true,
            player => player == 1003 ? 12 : null, Send) && sent.Count == 1 && sent[0].World == 12,
            "Arb_part_082:15914-15925: penalty goes to the departing user's current World");
        Hex.Eq(sent[0].Payload, "F0 0A 00 00 EB 03 00 00", "decompile-marked 13F5 PDId, not captured on World0 tap");
        sent.Clear();
        Hex.True(!PartyWiring.RouteWorldAction(0x13F5, penalty, _ => true, _ => null, Send)
            && sent.Count == 0, "no departing user session means no fabricated destination");
        foreach (bool queued in new[] { false, true })
        {
            sent.Clear();
            var state = PartyPackets.BuildAsChangeEventMatchingState(1003, queued);
            Hex.True(PartyWiring.RouteWorldAction(0x15CD, state, _ => true,
                player => player == 1003 ? 12 : null, Send) && sent.Count == 1 && sent[0].World == 0,
                $"T190 corrects the T184f inference: 15CD queued={queued} explicitly targets World0");
            Hex.Eq(sent[0].Payload, queued ? "EB 03 00 00 01" : "EB 03 00 00 00",
                "15CD user id starts at payload0, not the PDId offset used by13F5");
            sent.Clear();
            Hex.True(!PartyWiring.RouteWorldAction(0x15CD, state, _ => false, _ => 12, Send)
                && sent.Count == 0, "unlinked World0 has no current-World fallback");
        }
    }

    [Test] public static void T184f_reset_request_matches_cap_2man_7888_and_is_arbiter_owned()
    {
        // cap_2man_client1:9822 is C_RESET_ALL_DUNGEON, 04 00 67 58.
        // cap_2man.log TCP record 7888 +0: the complete AS_RESET_ALL_DUNGEON reply.
        var expected = Convert.FromHexString(
            "2A000000B913F00A0000EB030000020000002F00F00A01000000FFFFFFFF000000000000000002000000");
        Hex.Eq(PartyPackets.BuildAsResetAllDungeon(2800, 1003, 0x0AF0002F00000002, true, 2),
            expected[6..], "PDId + DungeonOwnerInfo (including padding) + online count");
        Hex.True(PartyWiring.ClientOpcodes.Contains(("C_RESET_ALL_DUNGEON", (ushort)0x5867))
            && PartyWiring.MinBodyLength(0x5867) == 0 && !PartyWiring.NotModelled.Contains(0x5867),
            "header-only request reaches the party manager");

        var pm = NewPartyManager();
        pm.Register(P(1, 1003, "New")); pm.Register(P(2, 1, "dobb"));
        pm.FormMatchedParty(T138dMembers((1003, MatchRole.Dps), (1, MatchRole.Dps)), false, 9781);
        var a = pm.OnClientPacket(1, 0x5867, Array.Empty<byte>());
        Hex.True(a.ToClients.Count == 0 && a.ToWorld.Count == 1 && a.ToWorld[0].Opcode == 0x13B9,
            "Arbiter requests the reset; World supplies vote/complete/port-out packets");
        // The runtime party id is state. Replace only that field in the independent capture.
        BitConverter.GetBytes(pm.FindByMember(1003)!.Id).CopyTo(expected, 14);
        Hex.Eq(a.ToWorld[0].Payload, expected[6..], "captured request through the party handler");
    }

    [Test] public static void T184f_reset_solo_leader_and_world_majority_match_decompile()
    {
        var solo = NewPartyManager(); solo.Register(P(1, 1003, "New"));
        var a = solo.OnClientPacket(1, 0x5867, Array.Empty<byte>());
        Hex.Eq(a.ToWorld.Single().Payload,
            "F0 0A 00 00 EB 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 F0 0A 00 00 EB 03 00 00 00 00 00 00 00 00 00 00",
            "Arb_part_041:10083-10099 solo owner PDId, no party, online count zero");

        var pm = NewPartyManager();
        pm.Register(P(1, 1003, "New") with { WorldId = 12 });
        pm.Register(P(2, 1, "dobb") with { WorldId = 13 });
        T163NormalParty(pm, 1003, 1);
        var nonLeader = pm.OnClientPacket(2, 0x5867, Array.Empty<byte>());
        Hex.True(nonLeader.ToWorld.Count == 0 && nonLeader.ToClients.Count == 1, "member cannot reset");
        Hex.Eq(nonLeader.ToClients[0].RawPacket!,
            "12 00 0E F3 06 00 40 00 34 00 33 00 39 00 33 00 00 00",
            "Arb_part_041:10070, SMT 0x1129");
        var split = pm.OnClientPacket(1, 0x5867, Array.Empty<byte>());
        Hex.True(split.ToWorld.Count == 0 && split.ToClients.Count == 1, "one of two is not a strict majority");
        Hex.Eq(split.ToClients[0].RawPacket!,
            "12 00 0E F3 06 00 40 00 32 00 36 00 36 00 30 00 00 00",
            "Arb_part_041:10118, SMT 0xA64");
        pm.Register(P(2, 1, "dobb") with { WorldId = 12 });
        Hex.True(pm.OnClientPacket(1, 0x5867, Array.Empty<byte>()).ToWorld.Count == 1,
            "both online members in World12 allow the request");
    }

    [Test] public static void T184f_system_party_leave_resets_both_members_before_leave_and_cancel()
    {
        // cap_2man_client1:11052-11055 and client2:8664-8668. The full lists are
        // data-dependent (1066/554B); this fixture keeps one event of each kind.
        using var events = T161bEvents((2154, "Dungeon", 9781), (5001, "BattleField", 118));
        var pm = NewPartyManager(); pm.Register(P(1, 1003, "New")); pm.Register(P(2, 1, "dobb"));
        pm.FormMatchedParty(T138dMembers((1003, MatchRole.Dps), (1, MatchRole.Dps)), false, 9781);
        var a = pm.OnWorldFrame(PartyPackets.SA_LEAVE_PARTY, SaLeavePartyPayload(pm.FindByMember(1003)!.Id, 1003));
        foreach (uint ticket in new uint[] { 1, 2 })
        {
            var frames = a.ToClients.Where(c => c.Ticket == ticket).ToArray();
            Hex.Eq(frames[0].RawPacket!, "1A 00 AC 87 02 00 0A 00 00 01 0A 00 12 00 6A 08 00 00 12 00 00 00 6A 08 00 00",
                "full dungeon reset precedes the leave");
            Hex.Eq(frames[1].RawPacket!, "1A 00 AC 87 02 00 0A 00 00 00 0A 00 12 00 89 13 00 00 12 00 00 00 89 13 00 00",
                "full battleground reset precedes the leave");
            Hex.True(frames[2].PacketName == (ticket == 1 ? "S_LEAVE_PARTY" : "S_LEAVE_PARTY_MEMBER"),
                "leaver and remaining member receive their respective leave frame");
            Hex.Eq(frames[3].RawPacket!, "0C 00 56 D7 F1 D8 FF FF 02 00 00 00",
                "cap_2man clients1:11055/2:8667, exactly one wildcard cancel after leave");
        }
        Hex.True(pm.FindByMember(1003) == null && pm.FindByMember(1) == null,
            "the remaining singleton is dismissed");
    }
}
