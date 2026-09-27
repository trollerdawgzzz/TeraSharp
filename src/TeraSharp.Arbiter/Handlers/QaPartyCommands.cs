// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201: native Arbiter QA party commands. Authorization is performed by
/// GmCommandHandlers before this dispatcher. World-owned apm_* and
/// match_battle_field retain their existing per-character 2829 forward.</summary>
public static class QaPartyCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "party", "raid", "reg_party", "unreg_party", "show_party", "apply_party", "change_pr", "world_of_party_match", "sim_match_progress", "show_cand" };
    private static int nextDummy = int.MaxValue;

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (!session.InWorld || session.SelectedCharacter == null) return true;
        PartyWiring.Register(session);
        int id = (int)session.PlayerId;
        var args = line.Args;
        switch (line.Name.ToLowerInvariant())
        {
            case "show_cand": session.Send(BuildCandidates(id, store)); break;
            case "sim_match_progress":
                if (args.Count == 3 && int.TryParse(args[0], out int tank) && int.TryParse(args[1], out int dps) && int.TryParse(args[2], out int healer))
                    QaMatchSimulation.Set(tank, dps, healer);
                else QaMatchSimulation.Clear();
                break;
            case "party": CreateParty(session, store, args, false, log); break;
            case "raid": CreateParty(session, store, args, true, log); break;
            case "reg_party": // Arb044:407-438: exactly one PR-text argument, raid=false.
                if (args.Count == 1) Board(PartyMatchManager.OnRegister(id, session.SelectedCharacter.Name,
                    new(false, args[0]))); break;
            case "unreg_party": Board(PartyMatchManager.OnQaUnregister(id)); break;
            case "change_pr": // Arb040:7829: the first argument, not a rebuilt command.
                if (args.Count > 0) Board(PartyMatchManager.OnChangePr(id, args[0])); break;
            case "show_party": // Arb044:5734-5786: page,minLevel,maxLevel,PR; sort3/order0.
                if (args.Count == 4 && short.TryParse(args[0], out short page) && page >= 0
                    && short.TryParse(args[1], out short min) && short.TryParse(args[2], out short max))
                    Board(PartyMatchManager.OnRequestInfo(id, new(page, min, max, 3, 0, args[3])));
                break;
            case "apply_party": // Arb040:5906 -> Arb071:9008, target is UserDbId.
                if (args.Count == 1 && int.TryParse(args[0], out int target))
                {
                    if (PartyMatchManager.Find(target) == null)
                        session.SendByDef("S_SYSTEM_MESSAGE", PartyMatchManager.BuildSmtFields(1003));
                    else PartyWiring.Dispatcher(session, log).Dispatch(PartyWiring.Manager.OnClientPacket(
                        (uint)id, PartyPackets.C_APPLY_PARTY, BitConverter.GetBytes(target)), "qa-apply-party");
                }
                break;
            case "world_of_party_match":
                // Native Arb044:8180-8283 constructs and destroys local test objects.
                // There is no observable state change or packet in this retail build.
                break;
        }
        return true;
        void Board(ArbiterActions actions) => PartyMatchManager.Dispatcher(session, log).Dispatch(actions, "qa-party-match");
    }

    internal static byte[] BuildCandidates(int owner, CharacterStore? store)
    {
        // Arb072:739-922: count/first/PR-text ref, then30B linked candidate records.
        var listing = PartyMatchManager.Find(owner);
        var candidates = listing == null ? Array.Empty<PartyManager.PartyPlayer>() : PartyWiring.Manager.CandidatesFor(owner).ToArray();
        byte[] pr = System.Text.Encoding.Unicode.GetBytes((listing?.Message ?? "") + "\0");
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write((ushort)0); w.Write((ushort)0xF12D); w.Write((ushort)candidates.Length);
        w.Write((ushort)(candidates.Length == 0 ? 0 : 10 + pr.Length)); w.Write((ushort)10); w.Write(pr);
        for (int i = 0; i < candidates.Length; i++)
        {
            var p = candidates[i]; int at = (int)stream.Position;
            byte[] name = System.Text.Encoding.Unicode.GetBytes(p.Name + "\0");
            var character = store?.GetCharacter(p.UserDbId);
            w.Write((ushort)at); w.Write((ushort)(i + 1 == candidates.Length ? 0 : at + 30 + name.Length));
            w.Write((ushort)(at + 30)); w.Write(p.UserDbId);
            w.Write((ushort)p.Class); w.Write((ushort)p.Race); w.Write((ushort)p.Gender); w.Write((ushort)p.Level);
            // User+3B88/3B8C/3B90 = Map/Guard/Section (Arb028:7515; C_VISIT_NEW_SECTION041:14472).
            w.Write(character?.LastWorld ?? 0); w.Write(character?.LastGuard ?? 0); w.Write(character?.LastSection ?? 0); w.Write(name);
        }
        byte[] packet = stream.ToArray(); BitConverter.GetBytes(checked((ushort)packet.Length)).CopyTo(packet, 0); return packet;
    }

    private static void CreateParty(GameSession session, CharacterStore? store, IReadOnlyList<string> args,
        bool raid, ILogger log)
    {
        var manager = PartyWiring.Manager;
        string caller = session.SelectedCharacter!.Name;
        // Native /raid N creates N named bots (Arb040:12894-12989). /party N
        // is the user-requested T200 extension; retail treats that N as one name.
        if (args.Count == 1 && int.TryParse(args[0], out int count))
        {
            var party = manager.FindByMember((int)session.PlayerId);
            int capacity = party?.MaxMembers ?? (raid ? PartyPackets.MaxRaidMembers : PartyPackets.MaxPartyMembers);
            if (count < 1 || count > capacity - (party?.Count ?? 1) || party?.IsSys == true)
            { GmCommandHandlers.SendCustom(session, $"Invalid member count parameter [{args[0]}]"); return; }
            var names = new List<string>();
            for (int suffix = 0; names.Count < count; suffix++)
            {
                string name = caller[..Math.Min(caller.Length, 25)] + suffix;
                if (!manager.TryGetPlayerByName(name, out _) && store?.NameExists(name) != true) names.Add(name);
            }
            foreach (string name in names) Join(caller, name);
            return;
        }
        if (!raid && args.Count is 1 or 2)
            Join(args.Count == 1 ? caller : args[0], args[^1]);

        void Join(string inviterName, string inviteeName)
        {
            if (string.IsNullOrWhiteSpace(inviterName) || string.IsNullOrWhiteSpace(inviteeName)
                || inviterName.Length > 36 || inviteeName.Length > 36) return;
            if (string.Equals(inviterName, inviteeName, StringComparison.OrdinalIgnoreCase))
            { GmCommandHandlers.SendCustom(session, $"Cannot invite oneself invitor[{inviterName}] or invitee[{inviteeName}]!"); return; }
            var inviter = Resolve(inviterName); var invitee = Resolve(inviteeName);
            var actions = manager.JoinForQa(inviter, invitee, raid);
            PartyWiring.Dispatcher(session, log).Dispatch(actions, "qa-party");
            if (actions.Rejected == null)
                GmCommandHandlers.SendCustom(session, $"Create Party with [{inviterName}, {inviteeName}]!");
            else
            {
                foreach (var player in new[] { inviter, invitee })
                    if (player.QaDummy && manager.FindByMember(player.UserDbId) == null) manager.Unregister(player.Ticket);
            }
        }
        PartyManager.PartyPlayer Resolve(string name)
        {
            if (manager.TryGetPlayerByName(name, out var player)) return player;
            int id;
            do id = Interlocked.Decrement(ref nextDummy);
            while (manager.TryGetTicket(id, out _) || store?.GetCharacter(id) != null);
            // cap_bg1:11887/11971/12077/12167; the native QA user has no World object/session.
            player = new((uint)id, id, name, 1, 0, 0, 1, 0, Online: false, QaDummy: true);
            manager.Register(player);
            GmCommandHandlers.SendCustom(session, $"Create a new invitor[{name}]!");
            return player;
        }
    }
}
