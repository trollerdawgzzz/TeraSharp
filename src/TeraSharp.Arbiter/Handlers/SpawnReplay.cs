using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Full verbatim replay of a captured login->world sequence. Two phases:
///   Phase 1 (on C_SELECT_USER): S_SELECT_USER .. up to but excluding S_SPAWN_ME
///   Phase 2 (on C_LOAD_TOPO_FIN): S_SPAWN_ME .. end
/// The captured player gameId is rewritten to the session's gameId.
/// </summary>
public static class SpawnReplay
{
    private static readonly List<(string name, byte[] body)> _phase1 = new();
    private static readonly List<(string name, byte[] body)> _phase2 = new();
    private static bool _loaded;

    private static readonly byte[] CapturedGameId = { 0x04, 0x00, 0xF0, 0x0A, 0x00, 0x80, 0x00, 0x00 };

    public static void Load(string path, ILogger log)
    {
        if (_loaded) return;
        if (!File.Exists(path)) { log.LogWarning("Replay file not found: {Path}", path); return; }

        bool inPhase2 = false;
        foreach (var line in File.ReadLines(path))
        {
            int bar = line.IndexOf('|');
            if (bar < 0) continue;
            string name = line[..bar];
            var parts = line[(bar + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            var full = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) full[i] = Convert.ToByte(parts[i], 16);
            var body = new byte[full.Length - 4];
            Array.Copy(full, 4, body, 0, body.Length);

            if (name == "S_SPAWN_ME") inPhase2 = true;
            if (name == "S_CHAT") break;

            (inPhase2 ? _phase2 : _phase1).Add((name, body));
        }
        _loaded = true;
        log.LogInformation("Replay loaded: phase1={P1} packets, phase2={P2} packets", _phase1.Count, _phase2.Count);
    }

    public static void ReplayPhase1(GameSession s, ILogger log) => Send(s, _phase1, log, "phase1");
    public static void ReplayPhase2(GameSession s, ILogger log) => Send(s, _phase2, log, "phase2");

    private static void Send(GameSession s, List<(string name, byte[] body)> packets, ILogger log, string label)
    {
        if (!_loaded) return;
        var ourGameId = BitConverter.GetBytes(s.GameId);
        int sent = 0;

        foreach (var (name, body) in packets)
        {
            var patched = (byte[])body.Clone();
            for (int i = 0; i + 8 <= patched.Length; i++)
            {
                if (patched.AsSpan(i, 8).SequenceEqual(CapturedGameId))
                {
                    ourGameId.CopyTo(patched, i);
                    i += 7;
                }
            }
            s.SendRawBody(name, patched);
            sent++;
        }
        log.LogInformation("Replay {Label}: sent {Sent}", label, sent);
    }
}
