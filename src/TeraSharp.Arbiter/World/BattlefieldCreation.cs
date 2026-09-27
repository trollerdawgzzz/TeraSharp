// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>T199: forced QA creation, startup ID load and durable log identity. cap_bg1:12388–12403/12533–12536.</summary>
public sealed class BattlefieldCreation
{
    public const ushort BSA_OPEN_INFO = 0x13DF, BSA_CREATE_LOG = 0x13E0, ABS_CREATE_LOG = 0x13E1,
        BSA_CREATE_RESULT = 0x1512, BSA_LOAD_UNIQUE_ID = 0x1514, ABS_LOAD_UNIQUE_ID = 0x1515,
        BSA_CREATE = 0x1517, ABS_CREATE = 0x1518;
    public sealed record OpenInfo(int TemplateId, bool CurrentOpen, bool NextOpen, int RemainSec);
    public sealed record LogRequest(long BattlefieldId, int TemplateId, long BlueParty, long RedParty);
    private readonly object gate = new();
    private readonly Dictionary<int, OpenInfo> open = new();

    public OpenInfo? OpenFor(int templateId) { lock (gate) return open.GetValueOrDefault(templateId); }

    public static IReadOnlyList<OpenInfo>? ParseOpen(byte[] p)
    {
        if (p.Length < 8) return null;
        uint count = BitConverter.ToUInt32(p, 0), next = BitConverter.ToUInt32(p, 4);
        if (count > (p.Length - 8) / 18) return null;
        var result = new List<OpenInfo>(); var seen = new HashSet<uint>();
        for (uint i = 0; i < count; i++)
        {
            if (next < 14 || (ulong)next + 12 > (ulong)p.Length || !seen.Add(next)) return null;
            int at = (int)next - 6;
            if (BitConverter.ToUInt32(p, at) != next) return null;
            result.Add(new(BitConverter.ToInt32(p, at + 8), p[at + 12] != 0, p[at + 13] != 0,
                BitConverter.ToInt32(p, at + 14)));
            next = BitConverter.ToUInt32(p, at + 4);
        }
        return next == 0 ? result : null;
    }

    public static LogRequest? ParseLog(byte[] p)
    {
        if (p.Length < 20) return null;
        uint off = BitConverter.ToUInt32(p, 0), size = BitConverter.ToUInt32(p, 4);
        if (off < 26 || size < 16 || size % 8 != 0 || off - 6 > p.Length || size > p.Length - (off - 6)) return null;
        int at = (int)off - 6;
        return new(BitConverter.ToInt64(p, 8), BitConverter.ToInt32(p, 16),
            BitConverter.ToInt64(p, at), BitConverter.ToInt64(p, at + 8));
    }

    /// <summary>Arb039:18304–18389: offset/byteLength, template, GM flag, ordered i64 party IDs.</summary>
    public static byte[] BuildCreate(int templateId, IReadOnlyList<long> parties, bool isGm)
    {
        var p = new byte[checked(13 + parties.Count * 8)];
        BitConverter.GetBytes(19).CopyTo(p, 0); BitConverter.GetBytes(parties.Count * 8).CopyTo(p, 4);
        BitConverter.GetBytes(templateId).CopyTo(p, 8); p[12] = (byte)(isGm ? 1 : 0);
        for (int i = 0; i < parties.Count; i++) BitConverter.GetBytes(parties[i]).CopyTo(p, 13 + i * 8);
        return p;
    }

    public static byte[] BuildLogReply(long battlefieldId, int logId)
    {
        var p = new byte[12]; BitConverter.GetBytes(battlefieldId).CopyTo(p, 0);
        BitConverter.GetBytes(logId).CopyTo(p, 8); return p;
    }

    public bool TryHandle(WorldLink link, CharacterStore? store, ushort opcode, byte[] payload)
    {
        switch (opcode)
        {
            case BSA_CREATE:
                // Arb061:17830 Handler_BSA_CREATE_BATTLE_FIELD: echo the supplied party handles
                // to the requesting World, isGm=false. This is distinct from tournament13E8.
                if (payload.Length < 12) return true;
                uint listRef = BitConverter.ToUInt32(payload, 0), listBytes = BitConverter.ToUInt32(payload, 4);
                if (listBytes % 8 != 0 || listBytes > 0 && (listRef < 18 || listRef - 6 > payload.Length || listBytes > payload.Length - (listRef - 6))) return true;
                var parties = new List<long>();
                for (uint i = 0; i < listBytes; i += 8) parties.Add(BitConverter.ToInt64(payload, checked((int)(listRef - 6 + i))));
                link.SendFrame(ABS_CREATE, BuildCreate(BitConverter.ToInt32(payload, 8), parties, false));
                return true;
            case BSA_OPEN_INFO:
                var info = ParseOpen(payload);
                if (info != null) lock (gate) foreach (var row in info) open[row.TemplateId] = row;
                return true; // Native Arb061:17664 ignores the sender; this is NOT an owner announcement.
            case BSA_LOAD_UNIQUE_ID:
                if (payload.Length == 0 && store != null)
                    link.SendFrame(ABS_LOAD_UNIQUE_ID, BitConverter.GetBytes(unchecked((int)store.GetCounterValue(CharacterStore.BattlefieldUniqueCounter, 0))));
                return true;
            case BSA_CREATE_RESULT:
                if (payload.Length == 0 && store != null) store.IssueBattlefieldUniqueId();
                return true; // Arb061:18068, no reply.
            case BSA_CREATE_LOG:
                var request = ParseLog(payload);
                if (request != null && store != null)
                {
                    int id = store.CreateBattlefieldLog(request.BattlefieldId, request.TemplateId, request.BlueParty, request.RedParty);
                    link.SendFrame(ABS_CREATE_LOG, BuildLogReply(request.BattlefieldId, id));
                }
                return true;
            default: return false;
        }
    }

    /// <summary>Arb040:14274–14402: existing distinct parties, command order; the GM dispatcher has already authorized.</summary>
    public static void CreateForGm(WorldBridge bridge, GameSession caller, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !int.TryParse(args[0], out int id)) return;
        var sheet = BattleFieldSheet.Find(id);
        if (sheet == null) { GmCommandHandlers.SendCustom(caller, $"Cannot find battleField[{id}]"); return; }
        var own = PartyWiring.Manager.FindByMember((int)caller.PlayerId);
        if (own == null) { GmCommandHandlers.SendCustom(caller, $"User[{caller.SelectedCharacter?.Name}] is not a party member"); return; }
        var parties = new List<long> { own.Id };
        foreach (string name in args.Skip(1))
        {
            var other = bridge.InWorldSessions().FirstOrDefault(s => string.Equals(s.SelectedCharacter?.Name, name, StringComparison.OrdinalIgnoreCase));
            if (other == null) { GmCommandHandlers.SendCustom(caller, $"Cannot find user[{name}]"); return; }
            var party = PartyWiring.Manager.FindByMember((int)other.PlayerId);
            if (party == null) { GmCommandHandlers.SendCustom(caller, $"User[{name}] is not a party member."); return; }
            if (parties.Contains(party.Id)) { GmCommandHandlers.SendCustom(caller, $"User[{name}] is duplicate party other members."); return; }
            parties.Add(party.Id);
        }
        // Arb040:14361 -> Arb046:2668: template's continent -> configured owner, no caller/main fallback.
        int? world = sheet.ContinentId > 0 ? DungeonRouting.Channels.WorldForContinent(sheet.ContinentId) : null;
        if (world is int owner && bridge.HasLinks(owner)) bridge.SendFrame(owner, ABS_CREATE, BuildCreate(id, parties, isGm: true));
        GmCommandHandlers.SendCustom(caller, $"Enter battleField[{id}]");
    }
}
