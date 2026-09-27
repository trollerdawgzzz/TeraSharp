// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201 per-user QA social state. Authorization belongs to the central command handler.</summary>
public static class QaSocialCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] {
        "add_friend", "delete_friend", "friendlist_max", "blocklist_max", "pchannel_max",
        "add_friendship", "add_friendship_party", "show_friendship_info", "set_chat_ban",
        "clear_friend_request", "delete_friend_limit_off",
    }, StringComparer.OrdinalIgnoreCase);
    private sealed class State
    {
        public int Friends = SocialHandlers.MaxFriends, Blocks = SocialHandlers.MaxBlocks;
        public bool ShowFriendship;
        public int? HighestGage;
        public long BanShownUntil;
        public readonly Dictionary<int, int> FriendRequests = new();
    }
    private static readonly ConditionalWeakTable<GameSession, State> States = new();
    public static int FriendLimit(int id) => StateOf(id)?.Friends ?? SocialHandlers.MaxFriends;
    public static int BlockLimit(int id) => StateOf(id)?.Blocks ?? SocialHandlers.MaxBlocks;
    private static State? StateOf(int id) => SocialHandlers.SessionForCharacter(id) is { } s ? States.GetOrCreateValue(s) : null;
    internal static void Forget(GameSession s) => States.Remove(s);
    private static int _friendRequestLimit = 3; // DatasheetManager ctor, Arb084:608–609, +0x7A21F8.
    internal static bool TakeFriendRequest(GameSession s, int target)
    {
        var requests = States.GetOrCreateValue(s).FriendRequests;
        lock (requests)
        {
            if (!requests.TryGetValue(target, out int count)) { requests[target] = 1; return true; }
            count = requests[target] = unchecked(count + 1);
            return count < Volatile.Read(ref _friendRequestLimit); // Arb028:6808–6812: third attempt rejects.
        }
    }
    internal static void ResetLimitsForTests() => _friendRequestLimit = 3;

    internal static void OnWorldEntryComplete(GameSession? session, CharacterStore? store)
    {
        if (session == null || store == null) return;
        int highest = store.MaxFriendshipGage((int)session.PlayerId);
        States.GetOrCreateValue(session).HighestGage = highest;
        // User::EnterWorldEnd, Arb028:15135. Restore learned gauge after World has the user.
        if (highest != 0) ArbiterClientHandlers.SendToWorld(session, 0x15B1,
            SocialHandlers.BuildAsUserPair((int)session.PlayerId, highest));
    }

    public static bool TryExecute(GameSession s, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        int me = (int)(s.SelectedCharacter?.Id ?? s.PlayerId); var state = States.GetOrCreateValue(s);
        switch (line.Name.ToLowerInvariant())
        {
            case "clear_friend_request": // User::ClearFriendRequest, Arb028:9045, no SQL or packets.
                lock (state.FriendRequests) state.FriendRequests.Clear();
                break;
            case "delete_friend_limit_off": // Arb044:3598: process-wide limit, not a persisted setting.
                Volatile.Write(ref _friendRequestLimit, int.MaxValue);
                break;
            case "friendlist_max": // Arb044:4100; native range1..100 despite old Korean help saying80.
                if (line.Args.Count != 1) GmCommandHandlers.SendCustom(s, "변경할 친구목록의 max값을 입력하세요 (1~80)");
                else if (QaGeneralCommands.NativeInt(line.Arg(0)) is int f && f is >= 1 and <= 100) state.Friends = f;
                break;
            case "blocklist_max":
                if (line.Args.Count == 1 && QaGeneralCommands.NativeInt(line.Arg(0)) is int b && b is >= 1 and <= 120) state.Blocks = b;
                break;
            case "pchannel_max":
                if (line.Args.Count == 1 && QaGeneralCommands.NativeInt(line.Arg(0)) is int c && c is >= 1 and <= 99) ChatManager.MaxMembersPerChannel = c;
                break;
            case "show_friendship_info":
                if (line.Args.Count == 1 && line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase)) state.ShowFriendship = true;
                else if (line.Args.Count == 1 && line.Arg(0).Equals("off", StringComparison.OrdinalIgnoreCase)) state.ShowFriendship = false;
                break;
            case "add_friend":
            case "delete_friend":
                bool add = line.Name.Equals("add_friend", StringComparison.OrdinalIgnoreCase);
                if (store != null && (add ? line.Args.Count == 1 : line.Args.Count > 0)
                    && Program.World?.InWorldSessions().FirstOrDefault(x => x.SelectedCharacter?.Name.Equals(line.Arg(0), StringComparison.OrdinalIgnoreCase) == true) is { } target)
                {
                    bool success = SocialHandlers.QaFriend(s, target, store, add, log);
                    if (!success) GmCommandHandlers.SendCustom(s, $"User[{s.SelectedCharacter?.Name}]에 친구[{target.SelectedCharacter?.Name}] {(add ? "추가" : "삭제")} 실패");
                }
                break;
            case "add_friendship": // Arb040:3427: current-user direction only; target must resolve online.
                if (store != null && line.Args.Count == 2 && Program.World?.InWorldSessions().FirstOrDefault(
                    x => x.SelectedCharacter?.Name.Equals(line.Arg(0), StringComparison.OrdinalIgnoreCase) == true) is { } friend)
                    SetGage(s, store, (int)friend.PlayerId, QaGeneralCommands.NativeInt(line.Arg(1)));
                break;
            case "add_friendship_party": // Arb030:9204: only caller's existing, online friends in current party.
                if (store != null && line.Args.Count == 1)
                {
                    PartyWiring.SyncRoster();
                    if (PartyWiring.Manager.FindByMember(me) is { } party)
                        foreach (var member in party.Members())
                            if (member.UserDbId != me && Program.World?.SessionForPlayerId(member.UserDbId) is { InWorld: true })
                                SetGage(s, store, member.UserDbId, QaGeneralCommands.NativeInt(line.Arg(0)));
                }
                break;
            case "set_chat_ban": // Arb044:3301 -> Arb001:5090: minutes, not seconds. Account SQL restriction.
                if (store != null && line.Args.Count == 1 && QaGeneralCommands.NativeInt(line.Arg(0)) is int minutes && minutes > 0)
                    ApplyChatBan(s, store, minutes, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                break;
        }
        return true;
    }

    private static void SetGage(GameSession s, CharacterStore store, int friend, int requested)
    {
        int me = (int)s.PlayerId;
        if (store.GetFriendRow(me, friend)?.Type != SocialHandlers.FriendTypeMutual) return;
        State state = States.GetOrCreateValue(s); state.HighestGage ??= store.MaxFriendshipGage(me);
        if (!store.SetFriendshipGage(me, friend, requested, out int value)) return;
        if (value > state.HighestGage)
        {
            state.HighestGage = value;
            ArbiterClientHandlers.SendToWorld(s, 0x15B1, SocialHandlers.BuildAsUserPair(me, value)); // Arb031:7064.
        }
        s.Send(Packet(0xFCF6, SocialHandlers.BuildAsUserPair(friend, value))); // Arb030:10995.
        if (state.ShowFriendship && QaSocialSheet.Entry.Value.TryGetValue(1003, out string? label))
            s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = SocialHandlers.Smt(3510, "type", label, "point", requested.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
    }

    internal static long ApplyChatBan(GameSession s, CharacterStore store, int minutes, long nowUnix)
    {
        long until = nowUnix + unchecked(minutes * 60);
        long account = store.AccountOf((int)s.PlayerId); store.SetQaChatBan(account, until);
        foreach (var user in (Program.World?.InWorldSessions().AsEnumerable() ?? Array.Empty<GameSession>()).Append(s).Distinct().Where(x => store.AccountOf((int)x.PlayerId) == account))
        {
            // Arb054:1528 filters kind3 chat bans out of the World list; the current restriction
            // model has no kind2 rows, so the native alarm is the existing empty2830 snapshot.
            ArbiterClientHandlers.SendToWorld(user, 0x2830, DbProxyHandlers.BuildDbsUserRestriction(user.GameId));
            user.Send(Packet(0x6DDF, BitConverter.GetBytes(until)));
            States.GetOrCreateValue(user).BanShownUntil = until;
        }
        return until;
    }

    internal static void RestoreChatBan(GameSession s, CharacterStore? store, long? nowUnix = null)
    {
        if (store == null || s.SelectedCharacter == null) return;
        long account = store.AccountOf((int)s.PlayerId), until = store.GetQaChatBan(account);
        long now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (until <= now) { if (until != 0) store.ClearExpiredQaChatBan(account, now); return; }
        State state = States.GetOrCreateValue(s);
        if (state.BanShownUntil == until) return;
        // Native login Account initialization can call StartChatBan again (Arb058:19022).
        s.Send(Packet(0x6DDF, BitConverter.GetBytes(until))); state.BanShownUntil = until;
    }

    private static long _lastTickUnix;
    public static void Tick(CharacterStore? store, WorldBridge world, DateTimeOffset now)
    {
        long unix = now.ToUnixTimeSeconds();
        if (store == null || Interlocked.Exchange(ref _lastTickUnix, unix) == unix) return;
        ExpireChatBans(store, world.InWorldSessions(), unix);
    }

    internal static void ExpireChatBans(CharacterStore store, IEnumerable<GameSession> sessions, long nowUnix)
    {
        foreach (var group in sessions.GroupBy(s => store.AccountOf((int)s.PlayerId)))
        {
            if (!store.ClearExpiredQaChatBan(group.Key, nowUnix)) continue;
            foreach (var user in group)
            {
                // Account::ChatBanCheckTick -> DeleteAccountRestriction, Arb061:12982–13072.
                user.Send(Packet(0xEA91, Array.Empty<byte>())); States.GetOrCreateValue(user).BanShownUntil = 0;
            }
        }
    }

    public static bool IsChatBanned(CharacterStore? store, int characterId, long? nowUnix = null)
        => store != null && store.GetQaChatBan(store.AccountOf(characterId)) > (nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static byte[] Packet(ushort opcode, byte[] payload)
    {
        var p = new byte[4 + payload.Length]; BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(opcode).CopyTo(p, 2); payload.CopyTo(p, 4); return p;
    }
}
