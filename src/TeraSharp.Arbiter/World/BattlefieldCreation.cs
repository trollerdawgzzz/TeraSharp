// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
                // T208c: World sends this right after a create it did NOT build (cap_bg4 194979,
                // 17:07:44) and not at all for one it did (196198-196201, 17:09:46). The refusal
                // itself is reported from BSA_CREATE_LOG, which names the parties; this only notes
                // that the unique id was consumed while a GM create was still open.
                NoteCreateResult();
                return true; // Arb061:18068, no reply.
            case BSA_CREATE_LOG:
                var request = ParseLog(payload);
                // T208c: World's own verdict on our ABS_CREATE, in the one frame we must answer
                // anyway. A zero party list means it resolved neither handle and built nothing.
                if (request != null) ReportCreateLog(request, WorldRouting.WorldIdOf(link));
                if (request != null && store != null)
                {
                    int id = store.CreateBattlefieldLog(request.BattlefieldId, request.TemplateId, request.BlueParty, request.RedParty);
                    link.SendFrame(ABS_CREATE_LOG, BuildLogReply(request.BattlefieldId, id));
                }
                return true;
            default: return false;
        }
    }

    // =============================================================================================
    // T208c - the GM create, and what happens when World will not build it
    // =============================================================================================
    //
    // cap_bg4 (tap) 194977-194980, 17:07:44 - /@battlefield 38 <leader>, both leaders in 3-man
    // parties, world 10 linked with 18 continents:
    //   A->W  ABS_CREATE        template 38, isGm 1, parties 0xAF0000100000002 / 0xAF0000100000001
    //   W->A  BSA_CREATE_LOG    battlefieldId 0x0AF00002, template 38, party list 0 / 0
    //   W->A  BSA_CREATE_RESULT (empty)
    //   -- and no 0x1513 offer at all.
    // The same command at 196198-196201, 17:09:46, after both parties were recreated:
    //   W->A  BSA_CREATE_LOG    battlefieldId 0x0AF00003, template 38, parties ...004 / ...003
    //   W->A  0x1513            the offer, 462 B.
    //
    // The two that failed were created at 10:04:27 and 10:04:34; world 10's links came up at
    // 10:07:01. AS_DO_CREATE_PARTY is sent when a party FORMS and nothing replays it to a World
    // that links afterwards, so world 10 had never heard of those two handles and resolved both
    // to zero. That is the precondition, and it is World-side: the frame we send is well formed.
    //
    // What changed here is that none of this is silent any more. Before, CreateForGm sent the
    // frame when it could, said "Enter battleField[id]" either way, and logged nothing; the zero
    // party list came back and was written to the log table as a battlefield with no parties.
    // Now every exit logs at Information with its reason, every refusal answers the GM, and
    // World's own refusal is reported against the command that caused it.

    /// <summary>OURS: one GM create is answerable at a time, for this long.</summary>
    public static readonly TimeSpan GmCreateWindow = TimeSpan.FromSeconds(30);

    private sealed record PendingGm(int TemplateId, long[] Parties, int WorldId,
        Action<string> Say, ILogger Log, DateTimeOffset At);

    private static readonly object gmGate = new();
    private static PendingGm? pendingGm;

    /// <summary>The template of the GM create still waiting for World, or null. Tests and seams.</summary>
    internal static int? PendingGmTemplate
    {
        get { lock (gmGate) return Fresh(DateTimeOffset.UtcNow)?.TemplateId; }
    }

    internal static void ResetGmCreateForTests() { lock (gmGate) pendingGm = null; }

    /// <summary>Arm the wait without a session - the test seam for the refusal path.</summary>
    internal static void ArmGmCreateForTests(int templateId, IReadOnlyList<long> parties,
        int worldId, Action<string> say, ILogger? log = null)
    {
        lock (gmGate)
            pendingGm = new PendingGm(templateId, parties.ToArray(), worldId, say,
                log ?? NullLogger.Instance, DateTimeOffset.UtcNow);
    }

    /// <summary>The armed create if it is still inside the window; expired ones are dropped.</summary>
    private static PendingGm? Fresh(DateTimeOffset now)
    {
        var p = pendingGm;
        if (p == null) return null;
        if (now - p.At <= GmCreateWindow) return p;
        pendingGm = null;
        return null;
    }

    private static string Ids(IEnumerable<long> parties) => string.Join(", ", parties.Select(p => $"0x{p:X}"));

    /// <summary>
    /// T208c. World answered our ABS_CREATE. A zero party list is a refusal: report it against
    /// the command, with the reason, instead of storing a battlefield nobody is in.
    /// </summary>
    private static void ReportCreateLog(LogRequest request, int worldId)
    {
        PendingGm? p;
        lock (gmGate)
        {
            p = Fresh(DateTimeOffset.UtcNow);
            if (p != null && p.TemplateId == request.TemplateId) pendingGm = null;
            else p = null;
        }
        if (p == null) return;

        if (request.BlueParty == 0 && request.RedParty == 0)
        {
            p.Log.LogInformation(
                "battlefield[{Id}]: world {W} resolved NONE of the parties {Parties} - refused. "
                + "A World only learns a party from the AS_DO_CREATE_PARTY sent when the party forms, "
                + "so a party created before that World linked is unknown there (cap_bg4 17:07:44).",
                request.TemplateId, worldId, Ids(p.Parties));
            p.Say($"battleField[{request.TemplateId}] was not created: world {p.WorldId} does not know "
                + "either party (they were formed before it came up). Re-form the parties and retry.");
            return;
        }

        if (request.BlueParty == 0 || request.RedParty == 0)
        {
            long missing = request.BlueParty == 0 ? p.Parties.FirstOrDefault() : p.Parties.Skip(1).FirstOrDefault();
            p.Log.LogInformation(
                "battlefield[{Id}]: world {W} resolved only one of the parties {Parties} (blue 0x{B:X}, red 0x{R:X}) - refused.",
                request.TemplateId, worldId, Ids(p.Parties), request.BlueParty, request.RedParty);
            p.Say($"battleField[{request.TemplateId}] was not created: world {p.WorldId} does not know "
                + $"party 0x{missing:X}. Re-form it and retry.");
            return;
        }

        p.Log.LogInformation(
            "battlefield[{Id}] 0x{Bf:X} created in world {W}: blue 0x{B:X}, red 0x{R:X}",
            request.TemplateId, request.BattlefieldId, worldId, request.BlueParty, request.RedParty);
    }

    /// <summary>T208c. The unique id was consumed while a GM create was still open.</summary>
    private static void NoteCreateResult()
    {
        lock (gmGate)
        {
            var p = Fresh(DateTimeOffset.UtcNow);
            p?.Log.LogInformation("battlefield[{Id}]: BSA_CREATE_RESULT while the GM create was still open "
                + "- World consumed the unique id without building it", p.TemplateId);
        }
    }

    /// <summary>
    /// Arb040:14274-14402: existing distinct parties, command order; the GM dispatcher has already
    /// authorized. T208c: every exit logs its reason at Information and answers the caller, and
    /// "Enter battleField[id]" is said only when the frame actually left.
    /// </summary>
    public static void CreateForGm(WorldBridge bridge, GameSession caller, IReadOnlyList<string> args,
        ILogger? log = null)
    {
        var l = log ?? NullLogger.Instance;
        void Refuse(string why, string say)
        {
            l.LogInformation("battlefield: {Why} - refused ({Cmd})", why,
                args.Count == 0 ? "no arguments" : string.Join(' ', args));
            GmCommandHandlers.SendCustom(caller, say);
        }

        if (args.Count == 0 || !int.TryParse(args[0], out int id))
        {
            Refuse("no battleField id in the command", "Usage: @battlefield <battleFieldId> <other party leader>...");
            return;
        }
        var sheet = BattleFieldSheet.Find(id);
        if (sheet == null) { Refuse($"battleField[{id}] is not in BattleFieldData.xml", $"Cannot find battleField[{id}]"); return; }

        var own = PartyWiring.Manager.FindByMember((int)caller.PlayerId);
        if (own == null)
        {
            Refuse($"caller {caller.SelectedCharacter?.Name} (player {caller.PlayerId}) is in no party",
                $"User[{caller.SelectedCharacter?.Name}] is not a party member");
            return;
        }
        var parties = new List<long> { own.Id };
        foreach (string name in args.Skip(1))
        {
            var other = bridge.InWorldSessions().FirstOrDefault(s => string.Equals(s.SelectedCharacter?.Name, name, StringComparison.OrdinalIgnoreCase));
            if (other == null) { Refuse($"no in-world session named {name}", $"Cannot find user[{name}]"); return; }
            var party = PartyWiring.Manager.FindByMember((int)other.PlayerId);
            if (party == null) { Refuse($"{name} (player {other.PlayerId}) is in no party", $"User[{name}] is not a party member."); return; }
            if (parties.Contains(party.Id)) { Refuse($"{name} is already in party 0x{party.Id:X}", $"User[{name}] is duplicate party other members."); return; }
            parties.Add(party.Id);
        }

        // Arb040:14361 -> Arb046:2668: template's continent -> configured owner, no caller/main fallback.
        if (sheet.ContinentId <= 0)
        {
            Refuse($"battleField[{id}] has no continent in BattleFieldData.xml",
                $"battleField[{id}] has no continent configured - nothing can host it.");
            return;
        }
        if (DungeonRouting.Channels.WorldForContinent(sheet.ContinentId) is not int owner)
        {
            Refuse($"no world owns continent {sheet.ContinentId} for battleField[{id}]",
                $"battleField[{id}] needs continent {sheet.ContinentId}, which no World has claimed.");
            return;
        }
        if (!bridge.HasLinks(owner))
        {
            Refuse($"world {owner} owns continent {sheet.ContinentId} but has no links",
                $"battleField[{id}] belongs to world {owner}, which is not running.");
            return;
        }

        lock (gmGate)
            pendingGm = new PendingGm(id, parties.ToArray(), owner,
                msg => GmCommandHandlers.SendCustom(caller, msg), l, DateTimeOffset.UtcNow);

        bridge.SendFrame(owner, ABS_CREATE, BuildCreate(id, parties, isGm: true));
        l.LogInformation("battlefield[{Id}]: ABS_CREATE to world {W} (continent {C}), parties {Parties}",
            id, owner, sheet.ContinentId, Ids(parties));
        GmCommandHandlers.SendCustom(caller, $"Enter battleField[{id}]");
    }
}
