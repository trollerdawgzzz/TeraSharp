using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// Loads a raw Arbiter↔World tap log (TCP chunks), reframes it, and builds a
/// lookup of "World sent opcode X" → "Arbiter replied with these frames".
///
/// DB-proxy responses echo a request id. Its position varies per message, so
/// for each captured (request, response) pair we detect which u32 in the response
/// mirrors which u32 in the request, and patch the live request's value in when
/// replaying. Without this, World rejects stale ids and stalls (e.g. at logout).
/// </summary>
public sealed class WorldReplayTable
{
    /// <summary>One captured response frame plus how to patch it from a live request.</summary>
    public sealed class Response
    {
        public ushort Op { get; init; }
        public byte[] Body { get; init; } = Array.Empty<byte>();
        /// <summary>(requestPayloadOffset, responsePayloadOffset) u32 pairs to copy.</summary>
        public List<(int req, int resp)> IdMap { get; } = new();
    }

    private sealed class Entry
    {
        public byte[] RequestBody = Array.Empty<byte>();
        public List<Response> Responses = new();
    }

    private readonly Dictionary<ushort, Entry> _byRequest = new();
    public byte[]? PlayerEnterBody { get; private set; }
    public byte[]? CharacterDataBody { get; private set; }

    /// <summary>
    /// World opcodes that are one-way pushes or answers to Arbiter-initiated messages, never
    /// requests. A frame in this set must NEVER become a request entry, and must SEAL whatever
    /// request is pending, so a later Arbiter frame cannot be attributed backwards across it.
    ///
    /// Why this matters: a replayed DBS_* attributed to the wrong request goes out carrying the
    /// CAPTURED DLM id. DLMExistManager::Find then misses, the item never calls CompleteMyself,
    /// and every later per-user DB message for that user - including UserLeaveWorld, the only
    /// thing that emits SA_LEAVE_WORLD - waits forever (status/HANDOFF.md section 1).
    /// That is exactly what 0x143F -> 0x290D did.
    ///
    /// Measured against D:\packetlogs\arb_world.log (the file the table is loaded from): this set
    /// removes 8 request entries that had ZERO responses - 0x13AA 0x13CC 0x13FA 0x1436 0x15A8
    /// 0x15B5 0x1626 0x164D, pure "no replay for" noise - and strips three frames that the
    /// heartbeats had let 0x27B3 inherit (0x15FB, 0x1449 and, worst, 0x14FF AS_USER_REQUEST_EXIT).
    /// No legitimate request -> response mapping is lost.
    ///
    /// Names from D:\packetlogs\world_opcodes.txt and the opcode switch in WorldServer.exe.c.
    /// SA_BYPASS_TO_CLIENT (0x13F7) is deliberately NOT here: it is the tunnel, it interleaves
    /// constantly, and sealing on it would break every attribution.
    /// </summary>
    // =========================================================================================
    // T106: the logging filter. NOT the same set as OneWayFromWorld below, and deliberately so.
    //
    // WorldBridge logs one Information line per W->A frame. On a live server that is most of the
    // console, and none of it is news: these seven are periodic heartbeats and harmless pushes
    // that arrive by the hundred. Demoting them to Debug leaves the console readable while the
    // file (T106's ArbiterLogProvider) still has every one of them.
    //
    // 0x1562 is the reason this is a separate set: SA_CLEAR_BATTLE_FIELD_ENTER_COUNT has a real
    // AS_ reply (0x1563) and a real handler, so it must never be sealed as one-way - it only
    // needs to stop shouting. Putting it in OneWayFromWorld would break the battlefield counter.
    //
    // 0x138A, 0x15A8, 0x1436 and 0x164D were already being suppressed by a literal list inline
    // in WorldBridge; they are folded in here so there is one place to look.
    // =========================================================================================

    /// <summary>W-&gt;A opcodes whose arrival is logged at Debug instead of Information.</summary>
    public static readonly IReadOnlySet<ushort> QuietInLog = new HashSet<ushort>
    {
        0x138A, // SA_REGISTER            - the pool heartbeat (0x13AA is its DEL twin)
        0x15A8, // SA_BROADCAST_FLOATING_CASTLE_NAMEPLATE
        0x1436, // SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD
        0x164D, // SA_WORLD_SERVER_STATUS                - periodic
        0x13FA, // SA_DUMMY_PACKET                       - exactly what it says
        0x159A, // SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME
        0x1598, // SA_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME
        0x13CC, // SA_EQUIP_ITEM_LEVEL
        0x1562, // SA_CLEAR_BATTLE_FIELD_ENTER_COUNT     - answered, just noisy
        0x1626, // SA_SEND_USE_OPTIONAL_ITEM
        0x2927, // SDB_CANCEL_NPC_ARENA_BET              - the logout-countdown ticks
    };

    /// <summary>True when this frame's arrival should be logged at Debug rather than Information.</summary>
    public static bool LogsAtDebug(ushort op) => QuietInLog.Contains(op);

    public static readonly IReadOnlySet<ushort> OneWayFromWorld = new HashSet<ushort>
    {
        0x1436, // SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD
        0x15A8, // SA_BROADCAST_FLOATING_CASTLE_NAMEPLATE
        0x159A, // SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME
        0x2958, // SDB_CHANGE_CITY_WAR_STATE   - see the note below
        0x13FA, // SA_DUMMY_PACKET
        0x13CC, // SA_EQUIP_ITEM_LEVEL
        0x1626, // SA_SEND_USE_OPTIONAL_ITEM
        0x1441, // SA_UPDATE_FIELD_POINT_RECEIVED_INDEX
        0x143F, // SA_UPDATE_FIELD_POINT       - World's answer to our 0x143E push
        0x15B5, // SA_REQUEST_SEND_SKILL_SCRIPT_LIST
        0x13AA, // SA_DEL_FROM_INTER_PARTY_MATCH_POOL
        0x13F2, // DSA_DUNGEON_TIMELINE_OPEN_INFO      (periodic heartbeat)
        0x13E5, // BSA_REQUEST_BOUNTY_HUNT_SEASON_INFO (periodic heartbeat)
        0x164D, // SA_WORLD_SERVER_STATUS              (periodic)

        // --- T74: the two audit-log writes. Both are fire-and-forget: Handler_SDB_ITEM_TRADE_LOG
        // (Arb_part_063.c:12031) and Handler_SDB_CASH_ITEM_LOG run to `return 1` with no packet
        // writer in them at all - no FUN_140350eb0, no send - and cap_social2/3 confirm it: three
        // 0x288C arrive and not one A->W frame follows any of them.
        //
        // 0x27DD carries what its dumper calls a DlmId (at frame +0x26), which normally means
        // "answer this or the character wedges". It does not here, because the real Arbiter never
        // answers it either, so World cannot be waiting on it. What sealing them actually buys is
        // the other half: an unsealed request makes the replay table attribute the NEXT A->W frame
        // to it as a response, which is how 0x156F nearly stole 0x273B's reply (see above).
        0x27DD, // SDB_ITEM_TRADE_LOG  - written after a completed player trade (122 B live)
        0x288C, // SDB_CASH_ITEM_LOG

        // --- T77, cap_social4.log. Neither carries a DlmId and neither draws a reply: two
        // SDB_PUBLISH_INVITE_CODE and three SA_DARK_RIFT_EVENT_OPEN arrive and no A->W frame
        // follows any of them. ---
        0x28B8, // SDB_PUBLISH_INVITE_CODE  - `UserDbId@06`, nothing else
        0x14CE, // SA_DARK_RIFT_EVENT_OPEN  - five i32s of event config

        // --- T15: proven one-way in D:\packetlogs\cap_newchar.log (a real ArbiterServer
        // playing for five minutes). Each was checked frame by frame: no A->W frame follows
        // any occurrence, and the Arbiter's handler has no SendToSession at all. ---
        0x2927, // SDB_CANCEL_NPC_ARENA_BET  - 5 occurrences (the logout-countdown ticks),
                //   Handler_SDB_CANCEL_NPC_ARENA_BET (Arb_part_063.c:1315) never replies.
        0x1491, // SA_UPDATE_USER_STATUS     - 12 occurrences
        0x15F9, // SA_REWARD_CITYWAR_KILL_DEATH_COUNT - Handler_SA_REWARD_CITYWAR_KILL_DEATH_COUNT
                //   (Arb_part_062.c:13831) has no SendToSession; the 0x15ED that follows it in
                //   the captures belongs to 0x295C SDB_RESULT_CITY_WAR (T23).
        0x156F, // SA_UPDATE_MAKRER_END      - 6 occurrences. THIS IS THE DANGEROUS ONE: it
                //   arrives BETWEEN S_UPDATE_EXP_LEVEL and its reply (cap_newchar.log seq 2994
                //   is 0x273B then 0x156F then 0x273C). Without this entry 0x156F becomes a
                //   request whose "response" is 0x273C, and the real 0x273B loses it.
        // 0x13B6 SA_UPDATE_DUNGEON_COOLTIME is a real (reply-less) handler since T25 - see
        // DbProxyHandlers.IsHandledRequest; it must not also be sealed here. T108 moved
        // 0x13C5 / 0x13C6 SA_ADD_/REMOVE_DUNGEON_CHANNEL out for the same reason: they feed the
        // instance registry now, and a sealed opcode never reaches a handler. Nothing is lost at
        // load time - 0x13C5 is immediately followed by 0x1499, which is still sealed and seals
        // it in turn, and the frames after 0x13C6 are the A->W spawn trio the loader skips
        // outright. Both still get an entry in _byRequest; it is dead, because TryHandle answers
        // them before WorldBridge ever reaches the replay lookup.
        0x1499, // SA_SAVE_ETC_DATA_FOR_MOVE_WORLD (zone change)

        // --- T47 sealed the four "Arbiter Contract" requests here. T60 UNSEALED them: the
        // seal was based on a wrong reading and it is what made a party invite between two
        // in-world players do nothing at all on 2026-09-15.
        //
        // What T47 got wrong: it said 0x2809 "sends NOTHING at all - no World frame, no client
        // packet". The HANDLER sends nothing - it dispatches on ContractType into one of four
        // FetchWork objects (4 ContractPartyFetchWork, 5 ContractPartyApplyFetchWork,
        // 10 CreateGuildFetchWork, 0x23 TradeBrokerOpenDealFetchWork) and THOSE send:
        // FetchWork::ResponseFailure (FUN_1409cde70) and ::ResponseSuccess both emit
        // DBS_FETCH_THROUGH_ARBITER_CONTRACT 0x280A, and the success path additionally fans
        // DBS_ASK 0x280B out to every opponent. T47 stopped at the handler.
        //
        // The four are now answered by World/ContractBroker.cs, gated out of WorldBridge's
        // default arm the way PartyWiring and GuildWiring are, so they must not be sealed here:
        // a sealed opcode never reaches TryHandleWorldFrame.
        //
        //   0x2809 SDB_FETCH_THROUGH_ARBITER_CONTRACT      -> ContractBroker.OnFetch
        //   0x280C SDB_ASK_THROUGH_ARBITER_CONTRACT        -> ContractBroker.OnAsk
        //   0x280D SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT -> ContractBroker.OnSendBegin
        //   0x280E SDB_SEND_END_THROUGH_ARBITER_CONTRACT   -> ContractBroker.OnSendEnd
        //
        // None of them carries a DlmId, so the replay table could never have head-blocked a user
        // over one - what the seal bought was stopping the table pairing them with whatever A->W
        // frame happened to follow. The gate does that now, and does it earlier.
        // status/CONTRACT-DESIGN.md.

        // --- T42: SDB_MOVE_WAREHOUSE_ITEM. Not "one-way" because we chose not to answer it -
        // the REAL Arbiter does not answer it. Handler_SDB_MOVE_WAREHOUSE_ITEM (FUN_1405b53c0,
        // Arb_part_048.c:13050) is ten lines long: enter the scope tracer, leave it, return 1.
        // No DB work, no SendToSession. Either World never sends 0x2754 in this build or it is a
        // latent hang in the original; either way matching it means sending nothing, and sealing
        // it here is what stops the replay table handing it somebody else's DBS_ reply.
        // status/MAIL-WAREHOUSE.md section 4.1.
        0x2754, // SDB_MOVE_WAREHOUSE_ITEM

        // T62: SDB_ADD_PVP_USER_LOG, seen at every logout (14 B; the guard is param_3 < 0xE).
        // Handler_SDB_ADD_PVP_USER_LOG (Arb_part_062.c:19349) reads the killer and victim db ids
        // at frame 6 and 10, looks both users up and writes a log row - no SendToSession. It was
        // briefed as the cinematic-seen flag; it is not (that is C_WATCHED_MOVIES, Arbiter-owned).
        0x27FE, // SDB_ADD_PVP_USER_LOG
        0x15FA, // one-way push, once per session
    };

    // NOTE: 0x2958 carries an SDB_ name, so by the naming convention it would expect a DBS_
    // reply. It does not appear as a W->A frame in arb_world.log, lobby_tap.log or
    // cap_newchar.log, so listing it here is a no-op on all three captures and cannot lose a
    // mapping. It is here because CLAUDE.md section 3 records it as a one-way periodic
    // observed live. If it ever shows up in a capture as a genuine per-user request it needs a
    // REAL handler in DbProxyHandlers (echoing the live DLM id), not a replay entry - remove it
    // from this set then.
    //
    // 0x293E SDB_UPDATE_GET_EXTRA_REWARD used to be in this set for exactly that reason, and it
    // was WRONG: cap_newchar.log seq 503 -> 504 shows a real request/reply pair, and the missing
    // reply wedged a login live on 2026-09-14. It is a real handler as of T15. Absence from one
    // capture is not evidence that an opcode is one-way; only a decompiled handler with no
    // SendToSession is.
    //
    // 0x162C is NOT here on purpose: cap_newchar.log seq 640 shows the Arbiter answering it with
    // 0x162D, so it is a genuine request.

    public static WorldReplayTable Load(string path, ILogger log)
    {
        var table = new WorldReplayTable();
        if (!File.Exists(path)) { log.LogWarning("World tap log not found: {Path}", path); return table; }

        var frames = new List<(bool fromWorld, ushort op, byte[] body)>();
        var bufW = new List<byte>();
        var bufA = new List<byte>();

        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var hdr = lines[i];
            if (!hdr.StartsWith("[")) continue;
            bool fromWorld = hdr.Contains("[W->A]");
            bool fromArb = hdr.Contains("[A->W]");
            if (!fromWorld && !fromArb) continue;

            var hexParts = lines[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var chunk = new byte[hexParts.Length];
            for (int k = 0; k < hexParts.Length; k++) chunk[k] = Convert.ToByte(hexParts[k], 16);

            var buf = fromWorld ? bufW : bufA;
            buf.AddRange(chunk);
            while (buf.Count >= 6)
            {
                int len = buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24);
                if (len < 6) { log.LogWarning("Bad frame len {Len} at {Line}", len, hdr); buf.Clear(); break; }
                if (buf.Count < len) break;
                ushort op = (ushort)(buf[4] | (buf[5] << 8));
                frames.Add((fromWorld, op, buf.GetRange(6, len - 6).ToArray()));
                buf.RemoveRange(0, len);
            }
        }
        log.LogInformation("World tap: reframed {N} frames", frames.Count);

        // Build request → responses. First occurrence of each request opcode wins.
        Entry? pending = null;
        ushort pendingOp = 0;
        var sealedOps = new HashSet<ushort>();
        int mapped = 0, idPatches = 0;

        foreach (var (fromWorld, op, body) in frames)
        {
            if (fromWorld)
            {
                // The tunnel interleaves constantly and must be skipped WITHOUT sealing.
                if (op == WorldBridge.OpTunnelToClient) continue;
                // One-way pushes and answers to Arbiter-initiated messages are never requests.
                // Seal whatever is pending so a later Arbiter frame cannot be attributed
                // backwards across them, and never give them an entry of their own.
                if (OneWayFromWorld.Contains(op))
                {
                    if (pending != null) sealedOps.Add(pendingOp);
                    pending = null;
                    continue;
                }
                if (pending != null) sealedOps.Add(pendingOp);
                if (table._byRequest.ContainsKey(op)) { pending = null; continue; } // already captured
                pending = new Entry { RequestBody = body };
                pendingOp = op;
                table._byRequest[op] = pending;
            }
            else
            {
                if (op == WorldBridge.OpPlayerEnter) table.PlayerEnterBody ??= body;
                else if (op == WorldBridge.OpCharacterData) table.CharacterDataBody ??= body;
                if (op is WorldBridge.OpTunnelFromClient or WorldBridge.OpPlayerEnter or WorldBridge.OpLoadTopoFin
                    or WorldBridge.OpForceEnterDungeonId or WorldBridge.OpUpdateVisitedSection
                    or WorldBridge.OpPlayerLeave1 or WorldBridge.OpPlayerLeave2) continue;

                if (pending != null && !sealedOps.Contains(pendingOp))
                {
                    var r = new Response { Op = op, Body = body };
                    DetectIdEcho(pending.RequestBody, body, r.IdMap);
                    idPatches += r.IdMap.Count;
                    pending.Responses.Add(r);
                    mapped++;
                }
            }
        }

        log.LogInformation("World replay: {Req} request opcodes, {Resp} response frames, {Ids} id-echo patches, playerEnter={PE}, charData={CD}",
            table._byRequest.Count, mapped, idPatches, table.PlayerEnterBody?.Length ?? 0, table.CharacterDataBody?.Length ?? 0);
        return table;
    }

    /// <summary>
    /// Find u32s that appear in both request and response and look like a request id:
    /// value >= 2, not equal to either frame's length, not an offset into either frame.
    /// </summary>
    private static void DetectIdEcho(byte[] req, byte[] resp, List<(int req, int resp)> map)
    {
        int reqLen = req.Length + 6, respLen = resp.Length + 6;
        bool LooksLikeOffsetOrLen(uint v) => v == reqLen || v == respLen || v == 0 || v == 1 || v > 10_000_000;

        for (int p = 0; p + 4 <= resp.Length; p += 1)
        {
            uint rv = BitConverter.ToUInt32(resp, p);
            if (LooksLikeOffsetOrLen(rv)) continue;
            // Skip values that are plausible offsets (<= frame len) unless they also appear in request at a
            // non-offset-looking position — we require an exact match in the request.
            for (int q = 0; q + 4 <= req.Length; q += 1)
            {
                if (BitConverter.ToUInt32(req, q) == rv)
                {
                    // Prefer aligned matches; accept the first found.
                    map.Add((q, p));
                    break;
                }
            }
            if (map.Count > 0) break; // one id per response is the norm
        }
    }

    /// <summary>Get replay responses for a request, with request ids patched from the live request.</summary>
    public List<(ushort op, byte[] body)> GetResponses(ushort requestOp, byte[]? liveRequest = null)
    {
        var outp = new List<(ushort, byte[])>();
        if (!_byRequest.TryGetValue(requestOp, out var e)) return outp;
        foreach (var r in e.Responses)
        {
            var body = (byte[])r.Body.Clone();
            if (liveRequest != null)
                foreach (var (q, p) in r.IdMap)
                    if (q + 4 <= liveRequest.Length && p + 4 <= body.Length)
                        Array.Copy(liveRequest, q, body, p, 4);
            outp.Add((r.Op, body));
        }
        return outp;
    }

    public List<(ushort op, byte[] body)> GetResponses(ushort requestOp) => GetResponses(requestOp, null);
}
