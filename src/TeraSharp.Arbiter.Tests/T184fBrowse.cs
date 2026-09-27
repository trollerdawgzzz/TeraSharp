// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T184f_nonempty_browse_statistics_match_real_world_request()
    {
        // cap_2man.log record1289 offset0, 2007B AS1644, UserDbId1. Actual bytes, no normalisation.
        int[] ids = { 3023, 3026, 3027, 3030, 3036, 3102, 3103, 3126, 3201, 3202, 3203, 9025, 9026, 9044, 9047, 9053, 9071, 9072, 9073, 9075, 9076, 9087, 9088, 9089, 9093, 9094, 9126, 9713, 9714, 9727, 9735, 9739, 9780, 9781, 9794, 9809, 9920, 9979, 9982 };
        var roles = new[] { new MatchBrowsePackets.RoleStatus(1, 1),
            new MatchBrowsePackets.RoleStatus(2, 1), new MatchBrowsePackets.RoleStatus(4, 1) };
        var rows = ids.Select(id => new MatchBrowsePackets.MatchingStatus(id, 3, roles)).ToArray();
        var payload = MatchBrowsePackets.BuildExtendedList(1, rows);
        Hex.Eq(payload, Convert.FromHexString("D7070000441627000000120000000100000012000000450000000300000027000000CF0B00000327000000310000000101310000003B00000002013B0000000000000004014500000078000000030000005A000000D20B0000035A000000640000000101640000006E00000002016E00000000000000040178000000AB000000030000008D000000D30B0000038D00000097000000010197000000A10000000201A1000000000000000401AB000000DE00000003000000C0000000D60B000003C0000000CA0000000101CA000000D40000000201D4000000000000000401DE0000001101000003000000F3000000DC0B000003F3000000FD0000000101FD00000007010000020107010000000000000401110100004401000003000000260100001E0C00000326010000300100000101300100003A01000002013A010000000000000401440100007701000003000000590100001F0C00000359010000630100000101630100006D01000002016D01000000000000040177010000AA010000030000008C010000360C0000038C01000096010000010196010000A00100000201A0010000000000000401AA010000DD01000003000000BF010000810C000003BF010000C90100000101C9010000D30100000201D3010000000000000401DD0100001002000003000000F2010000820C000003F2010000FC0100000101FC0100000602000002010602000000000000040110020000430200000300000025020000830C000003250200002F02000001012F0200003902000002013902000000000000040143020000760200000300000058020000412300000358020000620200000101620200006C02000002016C02000000000000040176020000A9020000030000008B02000042230000038B020000950200000101950200009F02000002019F020000000000000401A9020000DC02000003000000BE0200005423000003BE020000C80200000101C8020000D20200000201D2020000000000000401DC0200000F03000003000000F10200005723000003F1020000FB0200000101FB020000050300000201050300000000000004010F0300004203000003000000240300005D23000003240300002E03000001012E03000038030000020138030000000000000401420300007503000003000000570300006F2300000357030000610300000101610300006B03000002016B03000000000000040175030000A8030000030000008A03000070230000038A030000940300000101940300009E03000002019E030000000000000401A8030000DB03000003000000BD0300007123000003BD030000C70300000101C7030000D10300000201D1030000000000000401DB0300000E04000003000000F00300007323000003F0030000FA0300000101FA030000040400000201040400000000000004010E0400004104000003000000230400007423000003230400002D04000001012D04000037040000020137040000000000000401410400007404000003000000560400007F2300000356040000600400000101600400006A04000002016A04000000000000040174040000A70400000300000089040000802300000389040000930400000101930400009D04000002019D040000000000000401A7040000DA04000003000000BC0400008123000003BC040000C60400000101C6040000D00400000201D0040000000000000401DA0400000D05000003000000EF0400008523000003EF040000F90400000101F9040000030500000201030500000000000004010D0500004005000003000000220500008623000003220500002C05000001012C0500003605000002013605000000000000040140050000730500000300000055050000A623000003550500005F05000001015F0500006905000002016905000000000000040173050000A60500000300000088050000F12500000388050000920500000101920500009C05000002019C050000000000000401A6050000D905000003000000BB050000F225000003BB050000C50500000101C5050000CF0500000201CF050000000000000401D90500000C06000003000000EE050000FF25000003EE050000F80500000101F8050000020600000201020600000000000004010C0600003F06000003000000210600000726000003210600002B06000001012B060000350600000201350600000000000004013F0600007206000003000000540600000B26000003540600005E06000001015E0600006806000002016806000000000000040172060000A50600000300000087060000342600000387060000910600000101910600009B06000002019B060000000000000401A5060000D806000003000000BA0600003526000003BA060000C40600000101C4060000CE0600000201CE060000000000000401D80600000B07000003000000ED0600004226000003ED060000F70600000101F7060000010700000201010700000000000004010B0700003E07000003000000200700005126000003200700002A07000001012A070000340700000201340700000000000004013E070000710700000300000053070000C026000003530700005D07000001015D0700006707000002016707000000000000040171070000A40700000300000086070000FB2600000386070000900700000101900700009A07000002019A070000000000000401A40700000000000003000000B9070000FE26000003B9070000C30700000101C3070000CD0700000201CD070000000000000401").AsSpan(6).ToArray(),
            "all39 real dungeon statistics rows and117 role-status elements, with frame-relative links");
        Hex.Eq(MatchBrowsePackets.BuildExtendedList(1, Array.Empty<MatchBrowsePackets.MatchingStatus>()),
            PartyPackets.BuildAsViewInterPartyMatchList(1), "absence of statistics retains the proven empty request");
        // Actual 51-byte linked row at full-frame offset1701, including its three role nodes.
        // Record6338 user1003 has status1; record6475 user1 returns to3. No normalization.
        rows = ids.Select(id => new MatchBrowsePackets.MatchingStatus(id, (byte)(id == 9781 ? 1 : 3), roles)).ToArray();
        Hex.Eq(MatchBrowsePackets.BuildExtendedList(1003, rows).AsSpan(1701 - 6, 51).ToArray(),
            Convert.FromHexString("A5060000D806000003000000BA0600003526000001BA060000C40600000101C4060000CE0600000201CE060000000000000401"),
            "tap6338 measured status1 row");
        Hex.Eq(payload.AsSpan(1701 - 6, 51).ToArray(),
            Convert.FromHexString("A5060000D806000003000000BA0600003526000003BA060000C40600000101C4060000CE0600000201CE060000000000000401"),
            "tap6475 measured status3 row");
    }
    [Test] public static void T184f_browse_status_uses_sampled_applications_and_completed_waits()
    {
        // Decompile-derived runtime test, not invented capture timestamps. Native counts
        // pending applications once per sampled bucket, but completed waits in UTC seconds.
        var settings = MatchBrowseStatistics.Parse(
            System.Xml.Linq.XDocument.Parse("<MatchingRoleTemplate><Role id='1'><RoleData totalUser='2' tankerMin='0' tankerMax='2' dealerMin='0' dealerMax='2' healerMin='0' healerMax='2'/></Role></MatchingRoleTemplate>"),
            System.Xml.Linq.XDocument.Parse("<DungeonMatching><MatchingTimeDisplay checkTime1='20' checkTime2='180'/><MatchingStateDisplay checkNum1Max='1' checkNum2Max='6' checkNum3Max='14'/><Dungeon id='9739' matchingRoleId='1'/><Dungeon id='9781' matchingRoleId='1'/></DungeonMatching>"));
        var engine = new MatchBrowseStatistics.Engine(settings);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        MatchQueueManager.Entry Application(uint id, int wait) => new()
        {
            LeaderPlayerId = id, InstanceIds = new[] { 9739, 9781 }, QueuedAt = now.AddSeconds(-wait),
            Members = new[] { new MatchQueueManager.Queuer(id, (int)id, 2, 70, 1) },
        };
        var applications = new[] { Application(1003, 6), Application(1, 27) };
        MatchBrowsePackets.MatchingStatus For(int id) => engine.Rows.Single(r => r.MatchingId == id);
        Hex.True(For(9781).MatchTimeStatus == 3 && For(9781).Roles.All(r => r.Status == 1), "native no-history defaults, as captured in tap1289");
        engine.Publish(now, applications);
        Hex.True(For(9781).MatchTimeStatus == 3 && For(9781).Roles.Single(r => r.RoleMask == 2).Status == 2,
            "sampled waiting applications affect time and actual selected DPS demand");
        engine.Completed(9781, applications, now);
        engine.Publish(now, Array.Empty<MatchQueueManager.Entry>());
        Hex.True(For(9781).MatchTimeStatus == 1 && For(9781).Roles.All(r => r.Status == 1),
            "immediate completion publishes16.5-second average and removes current demand within the same sample second");
        Hex.True(For(9739).MatchTimeStatus == 3, "completion affects the selected destination only");
        engine.Publish(now.AddMilliseconds(500), Enumerable.Range(10, 13).Select(id => Application((uint)id, 0)).ToArray());
        Hex.True(For(9781).MatchTimeStatus == 1 && For(9781).Roles.Single(r => r.RoleMask == 2).Status == 3,
            "a same-second publication updates current roles without adding invented pending-history samples");
        engine.Publish(now.AddSeconds(22), new[] { Application(2, 0) });
        Hex.True(For(9781).MatchTimeStatus == 3, "a newly sampled application changes completed-only status1 back to3");
        engine.Publish(now.AddSeconds(700), Array.Empty<MatchQueueManager.Entry>());
        Hex.True(For(9781).MatchTimeStatus == 1 && For(9739).MatchTimeStatus == 3,
            "rotation clears pending history, retains native completion totals, and empty pools return default3");
    }

    [Test] public static void T184f_browse_request_reads_live_sheet_statistics_and_role_groups()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t184f-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldMinimum = Environment.GetEnvironmentVariable(MatchQueueManager.MinMembersVariable);
        MatchWiring.Reset();
        try
        {
            File.WriteAllText(Path.Combine(dir, "MatchingRoleTemplate.xml"),
                "<MatchingRoleTemplate><Role id='1'><RoleData totalUser='5' healerMin='1' healerMax='1'/></Role></MatchingRoleTemplate>");
            File.WriteAllText(Path.Combine(dir, "DungeonMatching.xml"),
                "<DungeonMatching><MatchingTimeDisplay checkTime1='20' checkTime2='180'/><MatchingStateDisplay checkNum1Max='1' checkNum2Max='6' checkNum3Max='14'/><Dungeon id='9781' matchingRoleId='1'/></DungeonMatching>");
            MatchBrowseStatistics.Entry.Load(dir);
            DungeonMatchRules.Entry.Load(dir);
            var rules = MatchBrowseStatistics.Entry.Value.Pools[9781];
            Hex.True(rules.RoleGroups.SequenceEqual(new byte[] { 3, 4 }),
                "unconstrained tank/DPS share native role mask3; constrained healer is mask4");
            var (op, payload) = TeraSharp.Arbiter.Handlers.LeaderboardPackets.WorldListRequest(true, 1);
            Hex.True(op == 0x1644 && payload.Length == 53, "live browse callsite includes its configured pool and roles");
            Hex.Eq(payload, MatchBrowsePackets.BuildExtendedList(1, new[]
            {
                new MatchBrowsePackets.MatchingStatus(9781, 3, new[]
                {
                    new MatchBrowsePackets.RoleStatus(3, 1), new MatchBrowsePackets.RoleStatus(4, 1),
                }),
            }), "World receives the native no-history status for configured groups");
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, null);
            var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
            MatchWiring.Clock = () => now;
            MatchWiring.FormParty = (group, _) => group.Members.Select(q => q.CharacterId).ToArray();
            for (int i = 1; i <= 5; i++)
            {
                int cls = i == 1 ? 1 : i == 5 ? 6 : 2;
                int role = i == 1 ? 0 : i == 5 ? 2 : 1;
                MatchQueueManager.Add((uint)i, new[] { 9781 },
                    new[] { new MatchQueueManager.Queuer((uint)i, i, cls, 70, role) }, now.AddSeconds(-5 - i));
            }
            MatchBrowseStatistics.Publish(now);
            Hex.True(MatchWiring.TryFormAndFinish(9781, now) != null, "configured five-member party forms");
            var (_, afterFormation) = TeraSharp.Arbiter.Handlers.LeaderboardPackets.WorldListRequest(true, 1);
            Hex.True(afterFormation[32] == 1,
                "immediate browser request sees completed wait statistics without waiting for the periodic publisher");
        }
        finally
        {
            MatchBrowseStatistics.Entry.UseBuiltIn(); DungeonMatchRules.Entry.UseBuiltIn(); MatchWiring.Reset();
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, oldMinimum);
            File.Delete(Path.Combine(dir, "MatchingRoleTemplate.xml"));
            File.Delete(Path.Combine(dir, "DungeonMatching.xml"));
            Directory.Delete(dir);
        }
    }
}
