using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Arbiter-owned social systems: friends, blocks, whisper.
/// Guilds and parties are NOT handled here (they drive World via AS_DO_*_PARTY).
/// </summary>
public sealed class SocialHandlers
{
    private readonly ILogger _log;
    public SocialHandlers(ILogger log) => _log = log;

    // ---- Session registry for whisper routing ----

    /// <summary>Name → session lookup for whisper routing. Case-insensitive.</summary>
    internal static readonly ConcurrentDictionary<string, GameSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    internal static void RegisterSession(string characterName, GameSession session)
        => Sessions[characterName] = session;

    internal static void UnregisterSession(string characterName)
        => Sessions.TryRemove(characterName, out _);

    // ---- Friends ----

    /// <summary>Send S_FRIEND_LIST from DB. Called at character select time.</summary>
    public static void SendFriendList(GameSession s)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return;
        var store = Program.Store;

        // Build friend entries from DB
        var friendEntries = new List<object>();
        if (store != null)
        {
            var friends = store.GetFriends((int)chr.Id);
            foreach (var (friendId, type) in friends)
            {
                var friendChar = store.GetCharacter(friendId);
                if (friendChar == null) continue;
                friendEntries.Add(new Dictionary<string, object>
                {
                    ["playerId"] = (uint)friendChar.Id,
                    ["group"] = 2, // default group
                    ["level"] = friendChar.Level,
                    ["race"] = friendChar.Race,
                    ["class"] = friendChar.Class,
                    ["gender"] = friendChar.Gender,
                    ["worldId"] = 0,
                    ["guardId"] = 0,
                    ["sectionId"] = 0,
                    ["dungeonGauntletDifficultyId"] = 0,
                    ["summonable"] = false,
                    ["lastOnline"] = 0L,
                    ["type"] = (uint)type,
                    ["bonds"] = 0,
                    ["name"] = friendChar.Name,
                    ["myNote"] = "",
                    ["theirNote"] = "",
                });
            }
        }

        s.SendByDef("S_FRIEND_LIST", new Dictionary<string, object>
        {
            ["personalNote"] = "",
            ["friends"] = friendEntries,
        });
    }

    /// <summary>Send S_USER_BLOCK_LIST from DB.</summary>
    public static void SendBlockList(GameSession s)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return;
        var store = Program.Store;

        var blockEntries = new List<object>();
        if (store != null)
        {
            var blocks = store.GetBlocks((int)chr.Id);
            foreach (int blockedId in blocks)
            {
                var blockedChar = store.GetCharacter(blockedId);
                if (blockedChar == null) continue;
                blockEntries.Add(new Dictionary<string, object>
                {
                    ["id"] = (uint)blockedChar.Id,
                    ["level"] = blockedChar.Level,
                    ["class"] = blockedChar.Class,
                    ["name"] = blockedChar.Name,
                    ["myNote"] = "",
                });
            }
        }

        s.SendByDef("S_USER_BLOCK_LIST", new Dictionary<string, object>
        {
            ["blockList"] = blockEntries,
        });
    }

    /// <summary>Send S_FRIEND_GROUP_LIST with default "好友" group (index=2).</summary>
    public static void SendFriendGroupList(GameSession s)
    {
        s.SendByDef("S_FRIEND_GROUP_LIST", new Dictionary<string, object>
        {
            ["groups"] = new List<object>
            {
                new Dictionary<string, object> { ["index"] = 2, ["name"] = "好友" },
            },
        });
    }

    // ---- C_ADD_FRIEND handler ----

    public bool OnAddFriend(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_ADD_FRIEND", body);
        if (f == null) return true;

        string targetName = f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
        var chr = s.SelectedCharacter;
        if (chr == null || string.IsNullOrEmpty(targetName)) return true;

        var store = Program.Store;
        if (store == null) return true;

        var targetChar = store.GetCharacterByName(targetName);
        if (targetChar == null)
        {
            // SMT 0x1ae = friend not found
            s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object>
                { ["message"] = "@430" }); // 0x1ae = 430
            _log.LogInformation("C_ADD_FRIEND: '{Target}' not found (from {Name})", targetName, chr.Name);
            return true;
        }

        if (targetChar.Id == chr.Id)
        {
            _log.LogInformation("C_ADD_FRIEND: can't add self");
            return true;
        }

        // Add bidirectional friendship
        store.AddFriend((int)chr.Id, targetChar.Id);
        store.AddFriend(targetChar.Id, (int)chr.Id);

        _log.LogInformation("Added friend: {Name} <-> {Target}", chr.Name, targetName);
        SendFriendList(s);
        return true;
    }

    // ---- C_DELETE_FRIEND handler ----

    public bool OnDeleteFriend(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_DELETE_FRIEND", body);
        if (f == null) return true;

        string targetName = f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
        var chr = s.SelectedCharacter;
        if (chr == null || string.IsNullOrEmpty(targetName)) return true;

        var store = Program.Store;
        if (store == null) return true;

        var targetChar = store.GetCharacterByName(targetName);
        if (targetChar == null) return true;

        store.RemoveFriend((int)chr.Id, targetChar.Id);
        store.RemoveFriend(targetChar.Id, (int)chr.Id);

        _log.LogInformation("Removed friend: {Name} x {Target}", chr.Name, targetName);
        SendFriendList(s);
        return true;
    }

    // ---- C_BLOCK_USER handler ----

    public bool OnBlockUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_BLOCK_USER", body);
        if (f == null) return true;

        string targetName = f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
        var chr = s.SelectedCharacter;
        if (chr == null || string.IsNullOrEmpty(targetName)) return true;

        var store = Program.Store;
        if (store == null) return true;

        var targetChar = store.GetCharacterByName(targetName);
        if (targetChar == null) return true;

        store.AddBlock((int)chr.Id, targetChar.Id);
        _log.LogInformation("Blocked: {Name} -> {Target}", chr.Name, targetName);
        SendBlockList(s);
        return true;
    }

    // ---- C_REMOVE_BLOCKED_USER handler ----

    public bool OnRemoveBlockedUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_REMOVE_BLOCKED_USER", body);
        if (f == null) return true;

        string targetName = f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
        var chr = s.SelectedCharacter;
        if (chr == null || string.IsNullOrEmpty(targetName)) return true;

        var store = Program.Store;
        if (store == null) return true;

        var targetChar = store.GetCharacterByName(targetName);
        if (targetChar == null) return true;

        store.RemoveBlock((int)chr.Id, targetChar.Id);
        _log.LogInformation("Unblocked: {Name} x {Target}", chr.Name, targetName);
        SendBlockList(s);
        return true;
    }

    // ---- C_WHISPER handler ----

    public bool OnWhisper(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_WHISPER", body);
        if (f == null) return true;

        string targetName = f.TryGetValue("target", out var t) ? t?.ToString() ?? "" : "";
        string message = f.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
        var chr = s.SelectedCharacter;
        if (chr == null) return true;

        string senderName = chr.Name;

        // Can't whisper self
        if (string.Equals(senderName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            // SMT 0x6f = can't whisper self
            s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object>
                { ["message"] = "@111" }); // 0x6f = 111
            return true;
        }

        // Look up target session
        if (!Sessions.TryGetValue(targetName, out var targetSession))
        {
            // SMT 0x33f = user not found / offline
            s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object>
                { ["message"] = "@831" }); // 0x33f = 831
            _log.LogInformation("Whisper from {Name} to {Target}: offline", senderName, targetName);
            return true;
        }

        // Check block list bidirectionally
        var store = Program.Store;
        if (store != null)
        {
            var senderBlocks = store.GetBlocks((int)chr.Id);
            var targetChr = targetSession.SelectedCharacter;
            if (targetChr != null)
            {
                if (senderBlocks.Contains((int)targetChr.Id))
                {
                    s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object>
                        { ["message"] = "@113" }); // 0x71 = restricted user
                    return true;
                }
                var targetBlocks = store.GetBlocks((int)targetChr.Id);
                if (targetBlocks.Contains((int)chr.Id))
                {
                    s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object>
                        { ["message"] = "@113" });
                    return true;
                }
            }
        }

        // Send S_WHISPER to both sender and recipient (as the real Arbiter does)
        var whisperFields = new Dictionary<string, object>
        {
            ["gameId"] = s.GameId,
            ["isWorldEventTarget"] = false,
            ["gm"] = false,
            ["founder"] = false,
            ["name"] = senderName,
            ["recipient"] = targetName,
            ["message"] = message,
        };

        s.SendByDef("S_WHISPER", whisperFields);
        targetSession.SendByDef("S_WHISPER", whisperFields);

        _log.LogInformation("Whisper: {Sender} -> {Target}", senderName, targetName);
        return true;
    }
}
