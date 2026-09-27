// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;
using System.Net;
using System.Net.Sockets;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T184_queue_push_reaches_both_members_and_delete_does_not_decline_formed_party()
    {
        string map = Path.GetTempFileName();
        var old = Environment.GetEnvironmentVariable(MatchQueueManager.MinMembersVariable);
        MatchWiring.Reset();
        try
        {
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, null);
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(listener.LocalEndPoint!);
            using var peer = listener.Accept();
            peer.ReceiveTimeout = 2000;
            using var session = new GameSession(client,
                new PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketDispatcher>.Instance),
                OpcodeTable.LoadFromFile(map, "376012"), new DefinitionRegistry(QuietLog()), 376012, 100, QuietLog())
                { PlayerId = 4742 };
            using var ev = T161bEvents((2154, "Dungeon", 9739));
            var delivered = new List<byte[]>();
            MatchWiring.PartyOf = _ => new[] { new MatchQueueManager.Queuer(4742, 4742, 0, 70, 1),
                new MatchQueueManager.Queuer(20000, 20000, 2, 70, 1) };
            MatchWiring.Deliver = (q, f) => { Hex.True(q.PlayerId == 20000, "other member delivery"); delivered.Add(f); };
            MatchWiring.OnMatchAdd(session, Convert.FromHexString(
                "37005FCE01000E0002001F0000000E0000000B2600000000000000000000001F002B0086120000010000002B0000008612000001000000").AsMemory(4));
            Hex.True(delivered.Count == 3 && BitConverter.ToUInt16(delivered[1], 2) == 0xC730
                && BitConverter.ToUInt16(delivered[2], 2) == 0x87AC, "member gets application notification, pool, then queued state");
            Hex.Eq(delivered[0], Convert.FromHexString("12000EF30600400032003100370033000000"),
                "cap_2man client2:819/client1:1230; Arb076:15123-15136 sends @2173 to every local member");
            var expected = delivered.SelectMany(f => f).ToArray();
            var actual = new byte[expected.Length];
            int count = 0;
            while (count < actual.Length)
            {
                int n = peer.Receive(actual, count, actual.Length - count, SocketFlags.None);
                Hex.True(n > 0, "leader connection stayed open"); count += n;
            }
            Hex.Eq(actual, expected, "leader and member receive the same complete pool/state frames");
            session.PlayerId = 20000;
            MatchWiring.OnMatchProgress(session, ReadOnlyMemory<byte>.Empty);
            var memberProgress = new byte[36];
            count = 0;
            while (count < memberProgress.Length)
            {
                int n = peer.Receive(memberProgress, count, memberProgress.Length - count, SocketFlags.None);
                Hex.True(n > 0, "member progress arrived"); count += n;
            }
            Hex.Eq(memberProgress, MatchQueueManager.BuildMatchProgress(9739, 0, 0, 0, 2, 0),
                "Arb077:9059-9116: member resolves the party application and sees both DPS");
            var now = DateTimeOffset.UnixEpoch.AddDays(1);
            T161Form(now);
            session.PlayerId = 9;
            session.SelectedCharacter = new TeraSharp.Arbiter.Game.FakeCharacter { Id = 9 };
            MatchWiring.OnMatchDel(session, ReadOnlyMemory<byte>.Empty);
            Hex.True(MatchWiring.PendingFor(9, now) != null && MatchWiring.PendingFor(10, now) != null,
                "World:583220 / Arb062:6233: queue deletion is not a post-FIN decline");
        }
        finally { MatchWiring.Reset(); Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, old); File.Delete(map); }
    }

    [Test] public static void T184_event_reset_uses_only_destinations_in_the_matching_sheets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TeraSharp-T184-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ep = Path.Combine(dir, "EventMatching.xml"), dp = Path.Combine(dir, "DungeonMatching.xml"), bp = Path.Combine(dir, "BattleFieldData.xml");
        try
        {
            File.WriteAllText(dp, "<DungeonMatching><Dungeon id='9739'/></DungeonMatching>");
            File.WriteAllText(bp, "<BattleFieldData><BattleField id='118'/></BattleFieldData>");
            File.WriteAllText(ep, "<EventMatching><Event id='2154' type='Dungeon'><Action type='matching'/><Target id='9739'/><Target id='9999'/></Event><Event id='5001' type='BattleField'><Action type='matching'/><Target id='118'/><Target id='9999'/></Event></EventMatching>");
            var rows = DatasheetLoader.ReadEventMatchingTargets(dir)!;
            Hex.True(rows.Count == 2 && rows[9739].Dungeon.SequenceEqual(new[] { 2154 })
                && rows[118].BattleField.SequenceEqual(new[] { 5001 }), "Arb079:7274-7326: stale event targets do not create matching destinations");
        }
        finally { File.Delete(ep); File.Delete(dp); File.Delete(bp); Directory.Delete(dir); }
    }

    [Test] public static void T184_queued_pool_matches_member_capture_and_multi_destination_links()
    {
        // classic_live2 9335: member-side capture proves the whole ordered two-person pool.
        const string pair = "420030C701000C00000000000C000000020020000A000000010000000000000020003100F00A0000E90D0000000200000031000000F00A0000945F00000000000000";
        var players = new[] { new MatchQueueManager.PoolPlayer(2800, 24468, 0, 0),
                              new MatchQueueManager.PoolPlayer(2800, 3561, 0, 2) };
        Hex.Eq(MatchQueueManager.BuildAddInterPartyMatchPool(10, players, 1), Convert.FromHexString(pair),
            "PDId order, type=1, marker=0, healer=2 and tank=0");
        Hex.Eq(MatchQueueManager.BuildAddInterPartyMatchPool(9739,
            new[] { new MatchQueueManager.PoolPlayer(2800, 4742, 0, 1) }),
            Convert.FromHexString("310030C701000C00000000000C000000010020000B260000000000000000000020000000F00A0000861200000001000000"), "classic_live3 8662");
        var multi = MatchQueueManager.BuildAddInterPartyMatchPool(new[] { 9739, 9781 }, players, remainSec: 30);
        Hex.True(BitConverter.ToInt32(multi, 8) == 30 && BitConverter.ToUInt16(multi, 4) == 2,
            "Arb076:14950: RemainSec is a scalar, not a second array descriptor");
        int second = BitConverter.ToUInt16(multi, 14);
        Hex.True(second == 66 && BitConverter.ToInt32(multi, second + 8) == 9781
            && BitConverter.ToUInt16(multi, second + 2) == 0, "second matching row follows the first nested member list");
    }

    [Test] public static void T184_fixed_role_subsets_keep_premades_atomic()
    {
        MatchQueueManager.Reset();
        var old = Environment.GetEnvironmentVariable(MatchQueueManager.MinMembersVariable);
        try
        {
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, null);
            var first = new MatchQueueManager.Queuer(1, 1, 2, 70, 1);
            var e = MatchQueueManager.Add(1, new[] { 9739 }, new[] { first }, DateTimeOffset.UnixEpoch);
            var premade = new[] { new MatchQueueManager.Queuer(2, 2, 1, 70, 0),
                new MatchQueueManager.Queuer(3, 3, 6, 70, 2),
                new MatchQueueManager.Queuer(4, 4, 2, 70, 1),
                new MatchQueueManager.Queuer(5, 5, 4, 70, 1),
                new MatchQueueManager.Queuer(6, 6, 5, 70, 1) };
            MatchQueueManager.Add(2, new[] { 9739 }, premade, DateTimeOffset.UnixEpoch.AddSeconds(1));
            var progress = MatchQueueManager.ProgressFrame(e, 9739)!;
            Hex.True(BitConverter.ToInt32(progress, 24) == 0 && BitConverter.ToInt32(progress, 28) == 1
                && BitConverter.ToInt32(progress, 32) == 0, "requester's partial group cannot borrow part of a premade");
            var group = MatchQueueManager.TryForm(9739, DateTimeOffset.UnixEpoch.AddSeconds(2));
            Hex.True(group != null && group.Entries.Count == 1 && group.Members.Select(q => q.PlayerId)
                .SequenceEqual(new uint[] { 2, 3, 4, 5, 6 }) && e.State == MatchQueueManager.MatchState.Waiting,
                "MatchServer:217054-217975: skip the solo to select the complete atomic premade");
        }
        finally { MatchQueueManager.Reset(); Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, old); }
    }

    // classic_live3.log record 8678, copied from the reassembled capture (no ID normalization).
    const string T184_8678 = "2400E48B01000800080000000B2600000000000000000000000000000100000000000000";
    // classic_live3.log record 10346, copied from the reassembled capture (no ID normalization).
    const string T184_10346 = "2400E48B01000800080000000B2600000000000000000000010000000000000000000000";
    // classic_live3.log record 10580, copied from the reassembled capture (no ID normalization).
    const string T184_10580 = "DA02AC875A000A0000010A0012007808000012001A00086801001A0022007908000022002A00096801002A0032007B08000032003A000B6801003A0042007A08000042004A000A6801004A00520035350C0052005A0039350C005A00620038350C0062006A0005350C006A007200155C0C0072007A0006350C007A008200165C0C0082008A000A350C008A0092004F04000092009A0009350C009A00A200195C0C00A200AA0001350C00AA00B200115C0C00B200BA0002350C00BA00C200125C0C00C200CA0004350C00CA00D200145C0C00D200DA0007350C00DA00E200175C0C00E200EA0008350C00EA00F200185C0C00F200FA0049350C00FA00020148350C0002010A0136350C000A0112011D350C0012011A016A0800001A012201F767010022012A0134350C002A01320133350C0032013A0132350C003A01420137350C0042014A0114350C004A01520113350C0052015A016D0800005A016201FA67010062016A0103350C006A017201135C0C0072017A01780800007A0182010868010082018A01790800008A0192010968010092019A017B0800009A01A2010B680100A201AA017A080000AA01B2010A680100B201BA0135350C00BA01C20139350C00C201CA0138350C00CA01D20105350C00D201DA01155C0C00DA01E20106350C00E201EA01165C0C00EA01F2010A350C00F201FA014F040000FA01020209350C0002020A02195C0C000A02120201350C0012021A02115C0C001A02220202350C0022022A02125C0C002A02320204350C0032023A02145C0C003A02420207350C0042024A02175C0C004A02520208350C0052025A02185C0C005A02620249350C0062026A0248350C006A02720236350C0072027A021D350C007A0282026A08000082028A02F76701008A02920234350C0092029A0233350C009A02A20232350C00A202AA0237350C00AA02B20214350C00B202BA0213350C00BA02C2026D080000C202CA02FA670100CA02D20203350C00D2020000135C0C00";
    // classic_live3.log record 10581, copied from the reassembled capture (no ID normalization).
    const string T184_10581 = "0A05AC87A0000A0000000A0012009908000012001A007E1F0D001A0022007F1F0D0022002A00801F0D002A003200811F0D0032003A00821F0D003A004200831F0D0042004A00841F0D004A005200851F0D0052005A00861F0D005A006200871F0D0062006A00881F0D006A007200891F0D0072007A008A1F0D007A0082008B1F0D0082008A008C1F0D008A0092008D1F0D0092009A008E1F0D009A00A2008F1F0D00A200AA00901F0D00AA00B200911F0D00B200BA00921F0D00BA00C200931F0D00C200CA00941F0D00CA00D200951F0D00D200DA00961F0D00DA00E200971F0D00E200EA00981F0D00EA00F200991F0D00F200FA009A1F0D00FA0002019B1F0D0002010A019C1F0D000A0112019D1F0D0012011A019E1F0D001A0122019F1F0D0022012A01A01F0D002A0132012968010032013A012B6801003A0142019B08000042014A019F0800004A0152012F68010052015A019E0800005A0162019C08000062016A012C6801006A0172013A350C0072017A0166230D007A01820167230D0082018A0168230D008A01920169230D0092019A016A230D009A01A2016B230D00A201AA016C230D00AA01B2016D230D00B201BA016E230D00BA01C2016F230D00C201CA0170230D00CA01D20171230D00D201DA0172230D00DA01E20173230D00E201EA0174230D00EA01F20175230D00F201FA0176230D00FA01020277230D0002020A0278230D000A02120279230D0012021A027A230D001A0222027B230D0022022A027C230D002A0232027D230D0032023A027E230D003A0242027F230D0042024A0280230D004A02520281230D0052025A0282230D005A02620283230D0062026A0284230D006A02720285230D0072027A0286230D007A02820287230D0082028A0288230D008A0292029908000092029A027E1F0D009A02A2027F1F0D00A202AA02801F0D00AA02B202811F0D00B202BA02821F0D00BA02C202831F0D00C202CA02841F0D00CA02D202851F0D00D202DA02861F0D00DA02E202871F0D00E202EA02881F0D00EA02F202891F0D00F202FA028A1F0D00FA0202038B1F0D0002030A038C1F0D000A0312038D1F0D0012031A038E1F0D001A0322038F1F0D0022032A03901F0D002A033203911F0D0032033A03921F0D003A034203931F0D0042034A03941F0D004A035203951F0D0052035A03961F0D005A036203971F0D0062036A03981F0D006A037203991F0D0072037A039A1F0D007A0382039B1F0D0082038A039C1F0D008A0392039D1F0D0092039A039E1F0D009A03A2039F1F0D00A203AA03A01F0D00AA03B20329680100B203BA032B680100BA03C2039B080000C203CA039F080000CA03D2032F680100D203DA039E080000DA03E2039C080000E203EA032C680100EA03F2033A350C00F203FA0366230D00FA03020467230D0002040A0468230D000A04120469230D0012041A046A230D001A0422046B230D0022042A046C230D002A0432046D230D0032043A046E230D003A0442046F230D0042044A0470230D004A04520471230D0052045A0472230D005A04620473230D0062046A0474230D006A04720475230D0072047A0476230D007A04820477230D0082048A0478230D008A04920479230D0092049A047A230D009A04A2047B230D00A204AA047C230D00AA04B2047D230D00B204BA047E230D00BA04C2047F230D00C204CA0480230D00CA04D20481230D00D204DA0482230D00DA04E20483230D00E204EA0484230D00EA04F20485230D00F204FA0486230D00FA04020587230D000205000088230D00";
    // classic_live3.log record 10585, copied from the reassembled capture (no ID normalization).
    const string T184_10585 = "100070640B2600000000000000000000";
    // classic_live3.log record 10587, copied from the reassembled capture (no ID normalization).
    const string T184_10587 = "E801C7741E00080008001800F00A0000861200000000000018002800F00A00009B1C02000200000028003800F00A0000328801000100000038004800F00A0000580E02000100000048005800F00A00000BCC01000100000058006800FFFFFFFF00000000FFFFFFFF68007800FFFFFFFF00000000FFFFFFFF78008800FFFFFFFF00000000FFFFFFFF88009800FFFFFFFF00000000FFFFFFFF9800A800FFFFFFFF00000000FFFFFFFFA800B800FFFFFFFF00000000FFFFFFFFB800C800FFFFFFFF00000000FFFFFFFFC800D800FFFFFFFF00000000FFFFFFFFD800E800FFFFFFFF00000000FFFFFFFFE800F800FFFFFFFF00000000FFFFFFFFF8000801FFFFFFFF00000000FFFFFFFF08011801FFFFFFFF00000000FFFFFFFF18012801FFFFFFFF00000000FFFFFFFF28013801FFFFFFFF00000000FFFFFFFF38014801FFFFFFFF00000000FFFFFFFF48015801FFFFFFFF00000000FFFFFFFF58016801FFFFFFFF00000000FFFFFFFF68017801FFFFFFFF00000000FFFFFFFF78018801FFFFFFFF00000000FFFFFFFF88019801FFFFFFFF00000000FFFFFFFF9801A801FFFFFFFF00000000FFFFFFFFA801B801FFFFFFFF00000000FFFFFFFFB801C801FFFFFFFF00000000FFFFFFFFC801D801FFFFFFFF00000000FFFFFFFFD8010000FFFFFFFF00000000FFFFFFFF";

    [Test] public static void T184_progress_is_tank_dealer_healer_not_three_party_sizes()
    {
        MatchQueueManager.Reset();
        try
        {
            var q = new MatchQueueManager.Queuer(4742, 4742, 0, 70, 1);
            var e = MatchQueueManager.Add(4742, new[] { 9739 }, new[] { q }, DateTimeOffset.UnixEpoch);
            Hex.Eq(MatchQueueManager.ProgressFrame(e, -9999)!, Convert.FromHexString(T184_8678), "real DPS queue 8678");
            e = MatchQueueManager.Add(4742, new[] { 9739 }, new[] { q with { Chosen = 0 } }, DateTimeOffset.UnixEpoch);
            Hex.Eq(MatchQueueManager.ProgressFrame(e, 9739)!, Convert.FromHexString(T184_10346), "same player, tank queue 10346");
            Hex.True(MatchQueueManager.ProgressFrame(null, -9999) == null, "no application: no fabricated progress row (Arb077:8848)");
        }
        finally { MatchQueueManager.Reset(); }
    }

    [Test] public static void T184_match_found_resets_all_destinations_in_both_free_modes()
    {
        // Capture-derived EventMatching inputs, not a claim that the local XML equals retail.
        // Half of each captured list is repeated by free=0/free=1 expansion in Arb079:7274.
        int[] dungeon = new[] { 2168,92168,2169,92169,2171,92171,2170,92170,800053,800057,800056,800005,810005,800006,810006,800010,1103,800009,810009,800001,810001,800002,810002,800004,810004,800007,810007,800008,810008,800073,800072,800054,800029,2154,92151,800052,800051,800050,800055,800020,800019,2157,92154,800003,810003 };
        int[] battle = new[] { 2201,860030,860031,860032,860033,860034,860035,860036,860037,860038,860039,860040,860041,860042,860043,860044,860045,860046,860047,860048,860049,860050,860051,860052,860053,860054,860055,860056,860057,860058,860059,860060,860061,860062,860063,860064,92201,92203,2203,2207,92207,2206,2204,92204,800058,861030,861031,861032,861033,861034,861035,861036,861037,861038,861039,861040,861041,861042,861043,861044,861045,861046,861047,861048,861049,861050,861051,861052,861053,861054,861055,861056,861057,861058,861059,861060,861061,861062,861063,861064 };
        using var ev = T161bEvents(dungeon.Select(id => (id, "Dungeon", 9739))
            .Concat(battle.Select(id => (id, "BattleField", 118))).ToArray());
        var f = MatchWiring.MatchFoundFrames(9739);
        Hex.Eq(f[0], Convert.FromHexString(T184_10580), "all 90 dungeon event IDs, including duplicate pass");
        Hex.Eq(f[1], Convert.FromHexString(T184_10581), "all 160 battlefield event IDs");
        Hex.Eq(f[2], Convert.FromHexString(T184_10585), "FIN still follows both resets");
    }

    [Test] public static void T184_retail_roster_and_physical_slot_holes()
    {
        var pm = NewPartyManager();
        var roles = new Dictionary<int, int>();
        uint ticket = 100;
        foreach (var (id, role) in T138dCaptureParty)
        {
            pm.Register(P(ticket++, id, "member" + id));
            roles[id] = (int)role;
        }
        var actions = pm.FormMatchedParty(T138dMembers(T138dCaptureParty), false, 9739);
        Hex.True(actions.Rejected == null, "retail five-member roster forms");
        foreach (var packet in T138dSysFrames(actions))
            Hex.Eq(packet.Frame, Convert.FromHexString(T184_10587), "same complete roster for every member");
        var party = pm.FindByMember(4742)!;
        party.Slots[1] = null;
        var expected = Convert.FromHexString(T184_10587);
        BitConverter.GetBytes(-1).CopyTo(expected, 28);
        BitConverter.GetBytes(0).CopyTo(expected, 32);
        BitConverter.GetBytes(-1).CopyTo(expected, 36);
        Hex.Eq(pm.SysPartyInfoFor(4742, roles)!, expected, "Arb079:3223: retain the physical hole; do not shift slots 2..4");
    }

    [Test] public static void T184_role17_admission_is_not_completion_and_two_member_rule_forms()
    {
        const string role17 = "<MatchingRoleTemplate><Role id='17' changeRoleId='24'><RoleData totalUser='5' totalUserQa='3' tankerMin='1' tankerMax='1' dealerMin='1' dealerMax='3' healerMin='1' healerMax='1' minMatchingMember='2'/></Role></MatchingRoleTemplate>";
        const string dungeon = "<DungeonMatching><Dungeon id='9781' matchingRoleId='17'/></DungeonMatching>";
        var prod = DungeonMatchRules.Parse(XDocument.Parse(role17), XDocument.Parse(dungeon))[9781];
        var qa = DungeonMatchRules.Parse(XDocument.Parse(role17), XDocument.Parse(dungeon), qa: true)[9781];
        Hex.True(prod.Total == 5 && prod.MinMatchingMember == 2 && prod.Templates().Single().Size == 5 && qa.Total == 3,
            "minMatchingMember=2 does not replace totalUser=5 or totalUserQa=3");
        Hex.True(!prod.AcceptsApplicant(1) && prod.AcceptsApplicant(2) && !prod.AcceptsApplicant(6),
            "World:3035963-3035982: min/max admission is checked per applying party");
        string dir = Path.Combine(Path.GetTempPath(), "TeraSharp-T184-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string rpath = Path.Combine(dir, "MatchingRoleTemplate.xml"), dpath = Path.Combine(dir, "DungeonMatching.xml");
        var old = Environment.GetEnvironmentVariable(MatchQueueManager.MinMembersVariable);
        try
        {
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, null);
            File.WriteAllText(rpath, role17); File.WriteAllText(dpath, dungeon);
            DungeonMatchRules.Entry.Load(dir);
            MatchQueueManager.Reset();
            var q1 = new MatchQueueManager.Queuer(9, 9, 2, 70, 1);
            var q2 = new MatchQueueManager.Queuer(10, 10, 5, 70, 1);
            MatchQueueManager.Add(9, new[] { 9781 }, new[] { q1, q2 }, DateTimeOffset.UnixEpoch);
            Hex.True(MatchQueueManager.TryForm(9781, DateTimeOffset.UnixEpoch) == null, "two DPS do not fill role 17");
            File.WriteAllText(rpath, "<MatchingRoleTemplate><Role id='17'><RoleData totalUser='2' tankerMin='0' tankerMax='0' dealerMin='2' dealerMax='2' healerMin='0' healerMax='0'/></Role></MatchingRoleTemplate>");
            DungeonMatchRules.Entry.Load(dir);
            var group = MatchQueueManager.TryForm(9781, DateTimeOffset.UnixEpoch);
            Hex.True(group != null && group.Members.Count == 2 && group.Roles.All(r => r == MatchRole.Dps), "explicit two-member rule forms without the override");
            var pm = NewPartyManager();
            pm.Register(P(5, 9, "one", cls: 2)); pm.Register(P(6, 10, "two", cls: 5));
            var actions = pm.FormMatchedParty(T138dMembers((9, MatchRole.Dps), (10, MatchRole.Dps)), false, 9781,
                matchFrames: new[] { MatchQueueManager.BuildFinInterPartyMatch(9781) });
            var roster = PartyPackets.BuildSysPartyInfo(new[] { new PartyPackets.SysPartySlot(2800, 9, 1), new PartyPackets.SysPartySlot(2800, 10, 1) });
            Hex.True(T138dSysFrames(actions).Count == 2, "both members receive the derived two-member roster");
            foreach (var packet in T138dSysFrames(actions)) Hex.Eq(packet.Frame, roster, "same slot order and roles at both recipients");
        }
        finally
        {
            MatchQueueManager.Reset(); DungeonMatchRules.Entry.UseBuiltIn();
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, old);
            File.Delete(rpath); File.Delete(dpath); Directory.Delete(dir);
        }
    }
}
