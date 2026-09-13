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
        0x293E, // SDB_UPDATE_GET_EXTRA_REWARD - see the note below
    };

    // NOTE: 0x2958 and 0x293E carry SDB_ names, so by the naming convention they would expect a
    // DBS_ reply. Neither appears as a W->A frame anywhere in arb_world.log or lobby_tap.log, so
    // listing them here is a no-op on both captures and cannot lose a mapping. They are here
    // because CLAUDE.md section 3 records them as one-way periodics observed live. If one ever
    // shows up in a capture as a genuine per-user request, it needs a REAL handler in
    // DbProxyHandlers (echoing the live DLM id), not a replay entry - remove it from this set then.

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
