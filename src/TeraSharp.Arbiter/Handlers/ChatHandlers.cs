using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Known TERA chat channel IDs (from decompiled client/Arbiter).
/// The Arbiter owns all chat routing; World never sees C_CHAT.
/// </summary>
public enum ChatChannel : uint
{
    Say       = 0,   // proximity (nearby players)
    Party     = 1,   // party members only
    Guild     = 2,   // guild members only
    Area      = 3,   // zone-wide broadcast
    Trade     = 4,   // global trade channel
    Team      = 5,   // battleground team
    Grp       = 6,   // group
    Raid      = 11,  // raid party
    Megaphone = 12,  // global shout (costs megaphone item)
    Emote     = 21,  // emote (proximity)
    Global    = 22,  // global channel
    Private   = 25,  // custom private channel
    Lfg       = 27,  // looking-for-group (global)
    System    = 213, // system/global-raid
}

/// <summary>
/// Chat: C_CHAT -> S_CHAT. Routes messages by channel:
///   - Global channels (Area, Trade, Megaphone, Global, LFG): broadcast to all sessions.
///   - Membership channels (Party, Guild, Raid): echo to sender only (no membership tracking yet).
///   - Proximity channels (Say, Emote): broadcast to all sessions (no spatial query yet).
///   - Messages starting with "!" are server commands (sender only).
/// </summary>
public sealed class ChatHandlers
{
    private readonly ILogger _log;
    public ChatHandlers(ILogger log) => _log = log;

    /// <summary>Channels that broadcast to every connected session.</summary>
    internal static bool IsBroadcastChannel(uint channel) => channel switch
    {
        (uint)ChatChannel.Say       => true,  // proximity → broadcast (no spatial yet)
        (uint)ChatChannel.Area      => true,  // zone-wide
        (uint)ChatChannel.Trade     => true,  // global
        (uint)ChatChannel.Megaphone => true,  // global shout
        (uint)ChatChannel.Global    => true,  // global
        (uint)ChatChannel.Emote     => true,  // proximity → broadcast (no spatial yet)
        (uint)ChatChannel.Lfg       => true,  // global
        _ => false,
    };

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

        if (IsBroadcastChannel(channel))
        {
            BroadcastChat(s, channel, message);
        }
        else
        {
            // Party/Guild/Raid/Private: echo to sender (no membership tracking yet)
            SendChat(s, channel, s.GameId, name, message);
        }
        return true;
    }

    /// <summary>
    /// Broadcast a chat message to all connected sessions, respecting block lists.
    /// The sender always receives their own message.
    /// </summary>
    internal static void BroadcastChat(GameSession sender, uint channel, string message)
    {
        var chr = sender.SelectedCharacter;
        if (chr == null) return;
        string senderName = chr.Name;
        ulong senderGameId = sender.GameId;
        var store = Program.Store;

        // Send to sender first (always)
        SendChat(sender, channel, senderGameId, senderName, message);

        // Broadcast to all other sessions
        foreach (var kvp in SocialHandlers.Sessions)
        {
            var target = kvp.Value;
            if (target == sender) continue;

            var targetChr = target.SelectedCharacter;
            if (targetChr == null) continue;

            // Check block list bidirectionally
            if (store != null)
            {
                var senderBlocks = store.GetBlocks((int)chr.Id);
                if (senderBlocks.Contains((int)targetChr.Id)) continue;
                var targetBlocks = store.GetBlocks((int)targetChr.Id);
                if (targetBlocks.Contains((int)chr.Id)) continue;
            }

            SendChat(target, channel, senderGameId, senderName, message);
        }
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

    internal static string StripFont(string msg)
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
