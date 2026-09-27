// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T184fQueue_both_solo_pools_queued_and_fin_match_two_player_capture()
    {
        // cap_2man_client1 #1231/#1232/#1285; client2 #820/#821/#914.
        // All three successful attempts have remainSec=0; no synthetic countdown.
        Hex.Eq(MatchQueueManager.BuildAddInterPartyMatchPool(9781,
            new[] { new MatchQueueManager.PoolPlayer(2800, 1003, 0, 1) }),
            Convert.FromHexString("310030C701000C00000000000C0000000100200035260000000000000000000020000000F00A0000EB0300000001000000"), "client1 #1231 C730");
        Hex.Eq(MatchQueueManager.BuildAddInterPartyMatchPool(9781,
            new[] { new MatchQueueManager.PoolPlayer(2800, 1, 0, 1) }),
            Convert.FromHexString("310030C701000C00000000000C0000000100200035260000000000000000000020000000F00A0000010000000001000000"), "client2 #820 C730");
        using var events = T161bEvents((2142, "Dungeon", 9781), (92139, "Dungeon", 9781));
        Hex.Eq(MatchQueueManager.EventMatchingFrames(new[] { 9781 }, true).Single(),
            Convert.FromHexString("1A00AC8702000A0001010A0012005E08000012000000EB670100"), "both members queued 87AC");
        Hex.Eq(MatchQueueManager.BuildFinInterPartyMatch(9781),
            Convert.FromHexString("10007064352600000000000000000000"), "both members FIN");
    }

    [Test] public static void T184fQueue_progress_reports_one_queued_dps_before_formation()
    {
        // cap_2man_client1 #1237 -> #1238 and client2 #826 -> #827.
        // Each request asks wildcard -9999; both replies are tank=0/dealer=1/healer=0.
        MatchQueueManager.Reset();
        try
        {
            foreach (uint player in new uint[] { 1003, 1 })
            {
                MatchQueueManager.Reset();
                var q = new MatchQueueManager.Queuer(player, (int)player, 2, 70, 1);
                var entry = MatchQueueManager.Add(player, new[] { 9781 }, new[] { q }, DateTimeOffset.UnixEpoch);
                Hex.Eq(MatchQueueManager.ProgressFrame(entry, -9999)!,
                    Convert.FromHexString("2400E48B0100080008000000352600000000000000000000000000000100000000000000"), "one chosen DPS application");
            }
        }
        finally { MatchQueueManager.Reset(); }
    }

    [Test] public static void T184fQueue_timeline_events_restore_full_captured_dungeon_reset()
    {
        // Actual target/event associations from Executable/Datasheet/EventMatching.xml.
        // The 66 event IDs occur twice in cap_2man_client1 #1283 and client2 #912.
        // Target3030's 2176/92176 are type=TimeLine; excluding them gave queue4's 1034B.
        int[][] destinations =
        {
            new[] { 3023, 2168, 92168 },
            new[] { 3026, 2169, 92169 },
            new[] { 3027, 2171, 92171 },
            new[] { 3030, 2176, 92176 },
            new[] { 3036, 2180, 92180 },
            new[] { 3102, 2173, 92173 },
            new[] { 3103, 2178, 92178 },
            new[] { 3126, 2170, 92170 },
            new[] { 3201, 2167, 92167 },
            new[] { 3202, 2174, 92174 },
            new[] { 3203, 2179, 92179 },
            new[] { 9025, 1101 },
            new[] { 9026, 1103 },
            new[] { 9044, 2162, 92159 },
            new[] { 9053, 2175, 92175 },
            new[] { 9071, 800005, 810005 },
            new[] { 9072, 800006, 810006 },
            new[] { 9073, 800010, 810010 },
            new[] { 9076, 800009, 810009 },
            new[] { 9087, 800001, 810001 },
            new[] { 9088, 800002, 810002 },
            new[] { 9089, 800004, 810004 },
            new[] { 9093, 800007, 810007 },
            new[] { 9094, 800008, 810008 },
            new[] { 9727, 1102, 91102 },
            new[] { 9735, 2152, 92149 },
            new[] { 9739, 2154, 92151 },
            new[] { 9780, 2140, 92137 },
            new[] { 9781, 2142, 92139 },
            new[] { 9794, 2147, 92144 },
            new[] { 9809, 2101, 92101 },
            new[] { 9920, 2157, 92154 },
            new[] { 9979, 800003, 810003 },
            new[] { 9982, 2161, 92158 },
        };
        using var events = T161bEvents(destinations.SelectMany(row => row.Skip(1)
            .Select(id => (id, row[0] == 3030 ? "TimeLine" : "Dungeon", row[0]))).ToArray());
        var frames = MatchQueueManager.AllEventMatchingFrames();
        Hex.True(frames.Count == 1, "fixture holds dungeon destinations only");
        Hex.Eq(frames[0], Convert.FromHexString(T184fQueueReset1283),
            "all 1066 bytes of cap_2man_client1 #1283 / client2 #912");
    }

    // Unmodified full HEX of cap_2man_client1 #1283 (1066 B).
    const string T184fQueueReset1283 =
            "2A04AC8784000A0000010A0012007808000012001A00086801001A0022007908000022002A00096801002A0032007B08000032003A000B6801003A0042008008" +
            "000042004A00106801004A0052008408000052005A00146801005A0062007D08000062006A000D6801006A0072008208000072007A00126801007A0082007A08" +
            "000082008A000A6801008A0092007708000092009A00076801009A00A2007E080000A200AA000E680100AA00B20083080000B200BA0013680100BA00C2004D04" +
            "0000C200CA004F040000CA00D20072080000D200DA00FF670100DA00E2007F080000E200EA000F680100EA00F20005350C00F200FA00155C0C00FA0002010635" +
            "0C0002010A01165C0C000A0112010A350C0012011A011A5C0C001A01220109350C0022012A01195C0C002A01320101350C0032013A01115C0C003A0142010235" +
            "0C0042014A01125C0C004A01520104350C0052015A01145C0C005A01620107350C0062016A01175C0C006A01720108350C0072017A01185C0C007A0182014E04" +
            "000082018A01DE6301008A0192016808000092019A01F56701009A01A2016A080000A201AA01F7670100AA01B2015C080000B201BA01E9670100BA01C2015E08" +
            "0000C201CA01EB670100CA01D20163080000D201DA01F0670100DA01E20135080000E201EA01C5670100EA01F2016D080000F201FA01FA670100FA0102020335" +
            "0C0002020A02135C0C000A0212027108000012021A02FE6701001A0222027808000022022A02086801002A0232027908000032023A02096801003A0242027B08" +
            "000042024A020B6801004A0252028008000052025A02106801005A0262028408000062026A02146801006A0272027D08000072027A020D6801007A0282028208" +
            "000082028A02126801008A0292027A08000092029A020A6801009A02A20277080000A202AA0207680100AA02B2027E080000B202BA020E680100BA02C2028308" +
            "0000C202CA0213680100CA02D2024D040000D202DA024F040000DA02E20272080000E202EA02FF670100EA02F2027F080000F202FA020F680100FA0202030535" +
            "0C0002030A03155C0C000A03120306350C0012031A03165C0C001A0322030A350C0022032A031A5C0C002A03320309350C0032033A03195C0C003A0342030135" +
            "0C0042034A03115C0C004A03520302350C0052035A03125C0C005A03620304350C0062036A03145C0C006A03720307350C0072037A03175C0C007A0382030835" +
            "0C0082038A03185C0C008A0392034E04000092039A03DE6301009A03A20368080000A203AA03F5670100AA03B2036A080000B203BA03F7670100BA03C2035C08" +
            "0000C203CA03E9670100CA03D2035E080000D203DA03EB670100DA03E20363080000E203EA03F0670100EA03F20335080000F203FA03C5670100FA0302046D08" +
            "000002040A04FA6701000A04120403350C0012041A04135C0C001A0422047108000022040000FE670100";
}
