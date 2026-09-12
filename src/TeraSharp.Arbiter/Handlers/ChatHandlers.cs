using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Chat: C_CHAT -> S_CHAT. Echoes the message back to the sender (and later,
/// broadcasts to others). Messages starting with "!" are server commands.
/// </summary>
public sealed class ChatHandlers
{
    private readonly ILogger _log;
    public ChatHandlers(ILogger log) => _log = log;

    public bool OnChat(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_CHAT", body);
        if (f == null) return true;

        uint channel = f.TryGetValue("channel", out var ch) ? Convert.ToUInt32(ch) : 0;
        string message = f.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
        string name = s.SelectedCharacter?.Name ?? "Unknown";

        // TERA wraps chat in <FONT>...</FONT> HTML. Strip it for logging/commands.
        string plain = StripFont(message);
        _log.LogInformation("[CHAT ch={Ch}] {Name}: {Msg}", channel, name, plain);

        // Server commands
        if (plain.StartsWith("!"))
        {
            HandleCommand(s, plain[1..].Trim());
            return true;
        }

        // Echo back to sender (single-player for now; broadcast later)
        SendChat(s, channel, s.GameId, name, message);
        return true;
    }

    private void HandleCommand(GameSession s, string cmd)
    {
        var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (verb)
        {
            case "help":
                SystemMessage(s, "Commands: !help, !pos, !time, !say <msg>");
                break;
            case "pos":
                var c = s.SelectedCharacter;
                SystemMessage(s, c != null ? $"Zone {c.Zone} ({c.X:F0}, {c.Y:F0}, {c.Z:F0})" : "No character");
                break;
            case "time":
                SystemMessage(s, $"Server time: {DateTime.UtcNow:HH:mm:ss} UTC");
                break;
            case "raw":
                // Exact bytes from real capture [416], gameId patched to ours
                var raw = new byte[] { 0x17,0x00,0x1F,0x00,0x00,0x00,0x00,0x00 };
                raw = raw.Concat(BitConverter.GetBytes(s.GameId)).Concat(new byte[] {
                    0x00,0x00,0x00,0x64,0x00,0x6F,0x00,0x62,0x00,0x00,0x00,
                    0x3C,0x00,0x46,0x00,0x4F,0x00,0x4E,0x00,0x54,0x00,0x3E,0x00,
                    0x68,0x00,0x65,0x00,0x6C,0x00,0x6C,0x00,0x6F,0x00,
                    0x3C,0x00,0x2F,0x00,0x46,0x00,0x4F,0x00,0x4E,0x00,0x54,0x00,0x3E,0x00,0x00,0x00 }).ToArray();
                s.SendRawBody("S_CHAT", raw);
                break;
            case "chan":
                foreach (uint ch in new uint[] { 0, 3, 21, 25, 27, 213 })
                    SendChat(s, ch, s.GameId, "TestChar", $"<FONT>channel {ch}</FONT>");
                break;
            case "test":
                s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = "@769" });
                s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = "TeraSharp test message" });
                break;
            case "say":
                SendChat(s, 0, s.GameId, "[Server]", arg);
                break;
            default:
                SystemMessage(s, $"Unknown command: {verb}. Try !help");
                break;
        }
    }

    public static void SendChat(GameSession s, uint channel, ulong gameId, string name, string message)
    {
        s.SendByDef("S_CHAT", new Dictionary<string, object>
        {
            ["channel"] = channel,
            ["gameId"] = gameId,
            ["isWorldEventTarget"] = false,
            ["gm"] = false,
            ["founder"] = false,
            ["name"] = name,
            ["message"] = message,
        });
    }

    public static void SystemMessage(GameSession s, string text)
    {
        // Channel 0 = say. Use a server-side name so it's visually distinct.
        SendChat(s, 0, 0, "[System]", $"<FONT>{text}</FONT>");
    }

    private static string StripFont(string msg)
    {
        // Remove <FONT ...> and </FONT> tags the client wraps messages in.
        var sb = new System.Text.StringBuilder();
        bool inTag = false;
        foreach (char c in msg)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }
}
