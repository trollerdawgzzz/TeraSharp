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
                if (op is WorldBridge.OpTunnelToClient or WorldBridge.OpHeartbeat14 or WorldBridge.OpHeartbeat6) continue;
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
