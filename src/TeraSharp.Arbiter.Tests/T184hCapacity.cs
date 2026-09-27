// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    /// <summary>Fixture gate. Returns the resolved path, or null after marking the test
    /// skipped, so a tree published without the capture fixtures skips instead of failing.</summary>
    static string? FixtureOrSkip(string relative, string what)
    {
        string? path = FindRepoFile(relative);
        if (path is null) Skip.Because(what + " missing");
        return path;
    }

    // Small explicit sheets replace the retired environment bypass in lifecycle fixtures.
    sealed class T184hDungeonSheet : IDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "TeraSharp-T184h-" + Guid.NewGuid().ToString("N"));
        readonly int dungeon;
        public T184hDungeonSheet(int dungeon, int total, int healers = 0)
        {
            this.dungeon = dungeon;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "DungeonMatching.xml"),
                $"<DungeonMatching><Dungeon id='{dungeon}' matchingRoleId='1'/></DungeonMatching>");
            Set(total, healers);
        }
        public void Set(int total, int healers = 0)
        {
            File.WriteAllText(Path.Combine(directory, "MatchingRoleTemplate.xml"),
                $"<MatchingRoleTemplate><Role id='1'><RoleData totalUser='{total}' tankerMin='0' tankerMax='0' dealerMin='{total - healers}' dealerMax='{total - healers}' healerMin='{healers}' healerMax='{healers}'/></Role></MatchingRoleTemplate>");
            DungeonMatchRules.Entry.Load(directory);
            Hex.True(DungeonMatchRules.For(dungeon)?.Total == total, "fixture loaded through the production sheet reader");
        }
        public void Dispose()
        {
            DungeonMatchRules.Entry.UseBuiltIn();
            File.Delete(Path.Combine(directory, "DungeonMatching.xml"));
            File.Delete(Path.Combine(directory, "MatchingRoleTemplate.xml"));
            Directory.Delete(directory);
        }
    }

    [Test] public static void T184h_sheet_size_governs_completion_roster_world_capacity_and_fixed_SYS()
    {
        if (FixtureOrSkip(Path.Combine("data", "t184f", "streams.json"), "cap_2man T184f streams.json") is null) return;
        var defs = LoadDefinitionsOrSkip(); if (defs == null) return;
        string fixture = FindRepoFile(Path.Combine("data", "t184f", "streams.json"))
            ?? throw new Exception("cap_2man T184f fixture missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
        byte[] Captured(int record) => Convert.FromHexString(doc.RootElement.GetProperty("cap_2man_client1")
            .EnumerateArray().Single(f => f.GetProperty("record").GetInt32() == record).GetProperty("hex").GetString()!);
        using var sheet = new T184hDungeonSheet(9781, total: 5);
        string? oldMinimum = Environment.GetEnvironmentVariable(MatchQueueManager.MinMembersVariable);
        MatchQueueManager.Reset();
        try
        {
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, "2");
            var now = DateTimeOffset.UnixEpoch;
            foreach (int id in new[] { 1003, 1 })
                MatchQueueManager.Add((uint)id, new[] { 9781 },
                    new[] { new MatchQueueManager.Queuer((uint)id, id, 2, 70, 1) }, now);
            // MatchServer:216390-216500 completes at RoleData.totalUser, not an override.
            Hex.True(MatchQueueManager.TryForm(9781, now) == null
                && MatchQueueManager.TryFormDungeon(9781, 2, now) == null,
                "legacy override and explicit helper size cannot form a two-of-five sheet group");
            sheet.Set(total: 2);
            var group = MatchQueueManager.TryForm(9781, now);
            Hex.True(group != null && group.Members.Count == 2, "same applicants fill an explicit two-member rule");
            var pm = NewPartyManager();
            pm.Register(P(1, 1003, "New", cls: 2));
            pm.Register(P(2, 1, "dobb", cls: 2));
            var actions = pm.FormMatchedParty(group!.Members.Select((q, i) =>
                new PartyManager.MatchedMember(q.CharacterId, group.Roles[i])).ToArray(), false, 9781);
            Hex.True(actions.Rejected == null && pm.FindByMember(1003)!.MaxMembers == 2, "party capacity equals sheet total");
            var lists = actions.ToClients.Where(c => c.PacketName == "S_PARTY_MEMBER_LIST").ToArray();
            Hex.True(lists.Length == 3, "one incremental list, then both members' complete list");
            foreach (var list in lists)
            {
                byte[] body = WriteByDef(defs, list.PacketName, (Dictionary<string, object>)list.Fields!);
                // cap_2man client1:1279/1281, client2:910: full-frame byte10 is capacity2.
                Hex.True(body[6] == Captured(1281)[10] && body[6] == 2, "roster advertises capacity2");
            }
            byte[] create = actions.ToWorld.Single(w => w.Opcode == 0x139E).Payload;
            // cap_2man tap1921: full-frame i32@34 =2; payload omits the six-byte header.
            Hex.Eq(create.AsSpan(28, 4).ToArray(), "02 00 00 00", "World receives the same capacity2");
            var sys = T138dSysFrames(actions);
            Hex.True(sys.Count == 2, "both members receive SYS");
            foreach (var packet in sys)
                Hex.Eq(packet.Frame, Captured(1286), "cap_2man client1:1286/client2:915: two occupied, 28 empty slots");
            Hex.True(Captured(1286).Length == 488 && BitConverter.ToUInt16(Captured(1286), 4) == 30,
                "SYS keeps 30 physical slots; it has no party-capacity field");
        }
        finally
        {
            MatchQueueManager.Reset();
            Environment.SetEnvironmentVariable(MatchQueueManager.MinMembersVariable, oldMinimum);
        }
    }
}
