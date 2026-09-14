using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Arbiter-owned social systems: friends, friend groups, memos, the block list and whisper.
/// Guilds and parties are NOT handled here (they drive World via AS_DO_*_PARTY).
///
/// <para>T30 made all of it real, from rows, against the decompile. The three login lists are
/// byte-exact against cap_newchar_client.log frames 304/305/306 (plus 307) for a brand-new
/// character; every rule below is quoted to its Handler_C_* in Arb_part_*.c -
/// status/FRIENDS.md has the full write-up.</para>
///
/// <para>The real Arbiter keeps friends in SQL (dbo.spAddFriendOnList and friends), NOT in the
/// DB-proxy protocol, so nothing here goes near WorldServer. The one exception is the pair of
/// AS_ pushes that tell World about the block list; those are cross-session and stubbed -
/// status/MULTIPLAYER-DESIGN.md.</para>
/// </summary>
public sealed class SocialHandlers
{
    private readonly ILogger _log;
    public SocialHandlers(ILogger log) => _log = log;

    // ---- Limits and constants, all from the decompile (status/FRIENDS.md section 3) ----

    /// <summary>User+0x623c, set to 100 in User::Init (Arb_part_027.c:13955).</summary>
    public const int MaxFriends = 100;
    /// <summary>User+0x6238, set to 0x78 in User::Init; QA command clamps 1..120.</summary>
    public const int MaxBlocks = 120;
    /// <summary>Handler_C_ADD_FRIEND / C_CHANGE_FRIEND_MEMO: wcslen must be &lt; 21.</summary>
    public const int MaxFriendMemo = 20;
    /// <summary>User::ChangeBlockedUserMemoWithLock truncates with wcsncpy_s(.., 0x29, ..).</summary>
    public const int MaxBlockMemo = 40;
    /// <summary>Handler_C_ADD_FRIEND_GROUP / _EDIT_: wcslen must be &lt; 41.</summary>
    public const int MaxGroupName = 40;
    /// <summary>User::UpdateFriendGroup drops anything outside 2..10 (8 &lt; index - 2U).</summary>
    public const int MinGroupIndex = 2, MaxGroupIndex = 10;
    /// <summary>The implicit ungrouped bucket. Never listed in S_FRIEND_GROUP_LIST; every new
    /// friend gets it (TryToAddFriend writes 1 at UserFriendInfo+0x100) and DeleteFriendGroup
    /// moves orphans back to it.</summary>
    public const int UngroupedGroupId = 1;
    /// <summary>The group User::ProvideSampleFriendGroup seeds once per character.</summary>
    public const int SampleGroupIndex = 2;
    /// <summary>StrFriendDataSheet id 100 as the TW server had it - the seeded group name.
    /// Captured bytes: 7D 59 CB 53 in cap_newchar_client.log frame 305.</summary>
    public const string SampleGroupNameTw = "好友";
    /// <summary>StrFriendDataSheet id 200 as the TW server had it - the default profile message.
    /// Captured bytes: CA 4E 29 59 ... in frame 306.</summary>
    public const string DefaultProfileMessageTw = "今天也是愉快的一天!";
    /// <summary>The same two for every other language. T45: the capture's strings were being
    /// seeded onto every character on a European server.</summary>
    public const string SampleGroupNameEn = "Friends";

    /// <summary>Back-compat for callers that predate the per-language table (T45). Resolves
    /// through <see cref="SampleGroupNameFor"/> with the default language.</summary>
    public static string SampleGroupName => SampleGroupNameFor(Auth.LoginLanguage.Default);
    /// <summary>Back-compat, as above.</summary>
    public static string DefaultProfileMessage => DefaultProfileMessageFor(Auth.LoginLanguage.Default);

    /// <summary>
    /// The seeded friend-group name, by client language — T45.
    ///
    /// <para>The Chinese strings below are what <c>cap_newchar_client.log</c> frames 305/306
    /// contain, and until T45 every character on this server got them regardless of language,
    /// because they were hard-coded from that capture. They are <c>StrFriendDataSheet</c> ids 100
    /// and 200, which the real Arbiter reads from its own installed string sheet; the capture came
    /// off a Taiwanese server while its client reported <c>language = 6</c> (EUR), so the packet
    /// field never distinguished them. We have no string sheet, so this table keys off the field
    /// and everything unlisted gets English. status/CLIENT-REJECTS.md section 8.</para>
    /// </summary>
    public static string SampleGroupNameFor(uint language) => language switch
    {
        Auth.LoginLanguage.Twn => SampleGroupNameTw,
        _ => SampleGroupNameEn,
    };

    /// <summary>The default profile message, by client language. Empty everywhere but TW: a
    /// greeting nobody wrote is worse than no greeting.</summary>
    public static string DefaultProfileMessageFor(uint language) => language switch
    {
        Auth.LoginLanguage.Twn => DefaultProfileMessageTw,
        _ => "",
    };


    // System-message ids. The format is proven by the capture: @id then \v-separated
    // parameter/value pairs (cap_newchar_client.log frame 692: @2977\vquestTemplateId\v59901).
    private const int SmtTargetNotFound      = 0x1AE;  // 430  FindUserWithLock failed
    private const int SmtAlreadyFriend       = 0x1B6;  // 438
    private const int SmtMyListFull          = 0x1B8;  // 440
    private const int SmtTargetListFull      = 0xDAC;  // 3500
    private const int SmtCannotAddSelf       = 0x1B9;  // 441  self or same account
    private const int SmtTargetBlockedMe     = 0x1B3;  // 435  [UserName]
    private const int SmtIBlockedTarget      = 0x531;  // 1329 [UserName]
    private const int SmtRequestSent         = 0xD7A;  // 3450 [UserName] -> requester
    private const int SmtRequestReceived     = 0xD7B;  // 3451 [UserName] -> target
    private const int SmtAcceptedToRequester = 0x1B0;  // 432  [UserName]
    private const int SmtAcceptedToAccepter  = 0x1B1;  // 433
    private const int SmtFriendDeleted       = 0x1B4;  // 436  [UserName] -> deleter only
    private const int SmtRequestDeclined     = 0xD7C;  // 3452 [RecvName][ReqName] -> both
    private const int SmtRequestCancelled    = 0xD7D;  // 3453 [ReqName][RecvName] -> both
    private const int SmtBlockTargetMissing  = 0x529;  // 1321
    private const int SmtCannotBlock         = 0x530;  // 1328 self / same account / admin
    private const int SmtBlockListFull       = 0x528;  // 1320
    private const int SmtAlreadyBlocked      = 0x52A;  // 1322

    /// <summary>Friend relation, as stored at UserFriendInfo+0xE8 and sent as S_FRIEND_LIST.type.</summary>
    public const int FriendTypeMutual = 0, FriendTypeOutgoing = 1, FriendTypeIncoming = 2;

    // ---- Session registry for whisper routing and cross-session pushes ----

    /// <summary>Name -> session lookup. Case-insensitive.</summary>
    internal static readonly ConcurrentDictionary<string, GameSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    internal static void RegisterSession(string characterName, GameSession session)
        => Sessions[characterName] = session;

    internal static void UnregisterSession(string characterName)
        => Sessions.TryRemove(characterName, out _);

    /// <summary>The online session playing a given character, or null.</summary>
    internal static GameSession? SessionForCharacter(int characterId)
    {
        foreach (var s in Sessions.Values)
            if (s.SelectedCharacter != null && (int)s.SelectedCharacter.Id == characterId) return s;
        return null;
    }

    /// <summary>Builds an S_SYSTEM_MESSAGE payload: <c>@id</c> then \v-separated key/value pairs.</summary>
    public static string Smt(int id, params string[] keysAndValues)
    {
        var sb = new System.Text.StringBuilder("@").Append(id);
        for (int i = 0; i + 1 < keysAndValues.Length; i += 2)
            sb.Append('\v').Append(keysAndValues[i]).Append('\v').Append(keysAndValues[i + 1]);
        return sb.ToString();
    }

    private static void SendSmt(GameSession s, int id, params string[] keysAndValues)
        => s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = Smt(id, keysAndValues) });

    // =====================================================================
    // The three login lists, rebuilt from rows
    // =====================================================================

    /// <summary>
    /// S_FRIEND_LIST (0xA111) from the friends table, in the order
    /// User::SendFriendListNoLock walks its vector: insertion order.
    /// <para>Byte-exact for a brand-new character against cap_newchar_client.log frame 306
    /// (empty list + the seeded profile message).</para>
    /// </summary>
    public static void SendFriendList(GameSession s)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return;
        V100Definitions.EnsureRegistered(s.Definitions);
        var store = Program.Store;
        var fields = store == null
            ? new Dictionary<string, object> { ["personalNote"] = "", ["friends"] = new List<object>() }
            : BuildFriendListFields(store, (int)chr.Id, id => SessionForCharacter(id) != null);
        s.SendByDef("S_FRIEND_LIST", fields);
    }

    /// <summary>
    /// The S_FRIEND_LIST field set for one character, straight from rows. Pure: the only thing
    /// it needs from the live server is which friends are online, which decides lastOnline.
    /// </summary>
    public static Dictionary<string, object> BuildFriendListFields(
        CharacterStore store, int characterId, Func<int, bool>? isOnline = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        int me = characterId;
        var friends = new List<object>();
        foreach (var row in store.GetFriendRows(me))
        {
            var f = store.GetCharacter(row.FriendId);
            if (f == null) continue;
            bool online = isOnline != null && isOnline(row.FriendId);
            friends.Add(new Dictionary<string, object>
            {
                ["playerId"] = (uint)f.Id,
                ["group"] = row.GroupId,
                ["level"] = f.Level,
                ["race"] = f.Race,
                ["class"] = f.Class,
                ["gender"] = f.Gender,
                ["worldId"] = 0,
                ["guardId"] = 0,
                ["sectionId"] = 0,
                ["summonable"] = false,
                // The real Arbiter sends seconds-since-logout for offline friends and 0 for
                // online ones (it diffs two timestamps); we have last_logout on the row.
                ["lastOnline"] = online ? 0L : SecondsSince(f.LastLogout),
                ["type"] = (uint)row.Type,
                ["bonds"] = 0,
                ["name"] = f.Name,
                ["myNote"] = row.Memo,
                ["theirNote"] = store.GetFriendRow(row.FriendId, me)?.Memo ?? "",
            });
        }

        return new Dictionary<string, object>
        {
            ["personalNote"] = store.GetProfileMessage(me),
            ["friends"] = friends,
        };
    }

    /// <summary>
    /// S_FRIEND_GROUP_LIST (0xCD00) from friend_groups. Group 1 (ungrouped) is never listed -
    /// User::SendFriendGroupListNoLock walks only the group map, and group 1 exists solely as a
    /// value on friend rows.
    /// <para>Byte-exact against cap_newchar_client.log frame 305 once the sample group is seeded.</para>
    /// </summary>
    public static void SendFriendGroupList(GameSession s)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return;
        var store = Program.Store;
        // T45: the seeded strings follow the client's language instead of the TW capture's.
        uint language = Auth.LoginLanguage.For(s.Account?.Name);

        if (store == null)
        {
            s.SendByDef("S_FRIEND_GROUP_LIST", new Dictionary<string, object>
            {
                ["groups"] = new List<object>
                {
                    new Dictionary<string, object>
                        { ["index"] = SampleGroupIndex, ["name"] = SampleGroupNameFor(language) },
                },
            });
            return;
        }
        ProvideSampleGroup(store, (int)chr.Id, language);
        s.SendByDef("S_FRIEND_GROUP_LIST", BuildFriendGroupListFields(store, (int)chr.Id));
    }

    /// <summary>The S_FRIEND_GROUP_LIST field set for one character, from friend_groups.</summary>
    public static Dictionary<string, object> BuildFriendGroupListFields(CharacterStore store, int characterId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var groups = new List<object>();
        foreach (var (index, name) in store.GetFriendGroups(characterId))
            groups.Add(new Dictionary<string, object> { ["index"] = index, ["name"] = name });
        return new Dictionary<string, object> { ["groups"] = groups };
    }

    /// <summary>
    /// S_USER_BLOCK_LIST (0x774B) from the blocks table.
    /// <para>Byte-exact against cap_newchar_client.log frame 304 (empty = 00 00 00 00).</para>
    /// TODO(multiplayer): the real Arbiter also pushes the id vector to World (opcode 0x1474)
    /// when the list is non-empty - status/MULTIPLAYER-DESIGN.md.
    /// </summary>
    public static void SendBlockList(GameSession s)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return;
        var store = Program.Store;

        var fields = store == null
            ? new Dictionary<string, object> { ["blockList"] = new List<object>() }
            : BuildBlockListFields(store, (int)chr.Id);
        s.SendByDef("S_USER_BLOCK_LIST", fields);
    }

    /// <summary>The S_USER_BLOCK_LIST field set for one character, from the blocks table.</summary>
    public static Dictionary<string, object> BuildBlockListFields(CharacterStore store, int characterId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var blocks = new List<object>();
        foreach (var row in store.GetBlockRows(characterId))
        {
            var b = store.GetCharacter(row.BlockedId);
            if (b == null) continue;
            blocks.Add(new Dictionary<string, object>
            {
                ["id"] = (uint)b.Id,
                ["level"] = b.Level,
                ["class"] = b.Class,
                ["name"] = b.Name,
                ["myNote"] = row.Memo,
            });
        }
        return new Dictionary<string, object> { ["blockList"] = blocks };
    }

    /// <summary>
    /// S_UPDATE_FRIEND_INFO (0x5CA5) - the live half of the friend panel. The real Arbiter sends
    /// it at login right after S_FRIEND_LIST (cap_newchar_client.log frame 307) and again on
    /// every C_UPDATE_FRIEND_INFO, and it lists ONLY friends who are online
    /// (User::SendUpdateFriendListInfo looks each one up with UserManager::FindUser and skips
    /// misses), so for a single-player server it is normally empty.
    /// </summary>
    public static void SendUpdateFriendInfo(GameSession s)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return;
        V100Definitions.EnsureRegistered(s.Definitions);
        var store = Program.Store;

        var friends = new List<object>();
        if (store != null)
        {
            foreach (var row in store.GetFriendRows((int)chr.Id))
            {
                var online = SessionForCharacter(row.FriendId);
                var f = online?.SelectedCharacter;
                if (f == null) continue;
                friends.Add(new Dictionary<string, object>
                {
                    ["playerId"] = (uint)f.Id,
                    ["level"] = f.Level,
                    ["race"] = f.Race,
                    ["class"] = f.Class,
                    ["gender"] = f.Gender,
                    ["status"] = 0,
                    ["worldId"] = f.WorldId,
                    ["guardId"] = f.GuardId,
                    ["sectionId"] = f.SectionId,
                    ["updated"] = true,
                    ["isWorldEventTarget"] = false,
                    ["summonable"] = false,
                    ["lastOnline"] = 0L,
                    ["name"] = f.Name,
                });
            }
        }

        s.SendByDef("S_UPDATE_FRIEND_INFO", new Dictionary<string, object> { ["friends"] = friends });
    }

    /// <summary>
    /// Seed the sample group and the default profile message the first time a character reaches
    /// the friend panel - User::ProvideSampleFriendGroup, which the real Arbiter runs once per
    /// character in EnterWorldEnd and guards with dbo.spIsProvideSampleFriendGroup.
    /// </summary>
    public static void ProvideSampleGroup(CharacterStore store, int characterId)
        => ProvideSampleGroup(store, characterId, Auth.LoginLanguage.Default);

    /// <inheritdoc cref="ProvideSampleGroup(CharacterStore, int)"/>
    /// <param name="language">The client language from C_LOGIN_ARBITER; picks the strings.</param>
    public static void ProvideSampleGroup(CharacterStore store, int characterId, uint language)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.TryProvideSampleFriendGroup(characterId)) return;
        store.UpsertFriendGroup(characterId, SampleGroupIndex, SampleGroupNameFor(language));
        string greeting = DefaultProfileMessageFor(language);
        if (greeting.Length > 0 && string.IsNullOrEmpty(store.GetProfileMessage(characterId)))
            store.SetProfileMessage(characterId, greeting);
    }

    /// <summary>
    /// S_CHANGE_FRIEND_STATE (0xE887) to every online MUTUAL friend - the login/logout ping.
    /// User::ChangeFriendStateWithLock only notifies rows whose type is 0.
    /// </summary>
    public static void NotifyFriendsOfState(GameSession s, uint state)
    {
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return;
        foreach (var row in store.GetFriendRows((int)chr.Id))
        {
            if (row.Type != FriendTypeMutual) continue;
            var peer = SessionForCharacter(row.FriendId);
            peer?.SendByDef("S_CHANGE_FRIEND_STATE", new Dictionary<string, object>
            {
                ["playerId"] = (uint)chr.Id,
                ["state"] = state,
            });
        }
    }

    private static long SecondsSince(DateTime when)
    {
        if (when == default) return 0;
        long secs = (long)(DateTime.UtcNow - when).TotalSeconds;
        return secs < 0 ? 0 : secs;
    }

    // =====================================================================
    // The rules, as pure functions over rows - every one of them quoted to
    // User::CanAddFriendNoLock / User::CanBlockUserNoLock in the decompile.
    // =====================================================================

    /// <summary>Why a friend request was refused, in the order CanAddFriendNoLock tests.</summary>
    public enum AddFriendResult
    {
        Ok,
        TargetNotFound,   // SMT 430
        CannotAddSelf,    // SMT 441 - self, or another character on my own account
        IBlockedTarget,   // SMT 1329
        TargetBlockedMe,  // SMT 435
        AlreadyFriend,    // SMT 438 - includes a request already pending either way
        MyListFull,       // SMT 440
        TargetListFull,   // SMT 3500
    }

    /// <summary>Why a block was refused (User::CanBlockUserNoLock).</summary>
    public enum BlockResult
    {
        Ok,
        TargetNotFound,   // SMT 430 (the handler's own lookup) / 1321 inside CanBlockUser
        CannotBlock,      // SMT 1328 - self, same account, or an admin
        AlreadyBlocked,   // SMT 1322
        ListFull,         // SMT 1320
    }

    /// <summary>
    /// Can <paramref name="me"/> send a friend request to <paramref name="targetId"/>?
    /// Order matters: the real Arbiter tests self/account, then blocks, then duplicates, then
    /// the two list-full limits, and answers with the FIRST failure.
    /// </summary>
    public static AddFriendResult CanAddFriend(CharacterStore store, int me, int targetId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var mine = store.GetCharacter(me);
        var target = store.GetCharacter(targetId);
        if (target == null) return AddFriendResult.TargetNotFound;
        if (targetId == me || (mine != null && target.AccountId == mine.AccountId))
            return AddFriendResult.CannotAddSelf;
        if (store.GetBlocks(me).Contains(targetId)) return AddFriendResult.IBlockedTarget;
        if (store.GetBlocks(targetId).Contains(me)) return AddFriendResult.TargetBlockedMe;
        if (store.GetFriendRow(me, targetId) != null) return AddFriendResult.AlreadyFriend;
        if (store.GetFriendRows(me).Count >= MaxFriends) return AddFriendResult.MyListFull;
        if (store.GetFriendRows(targetId).Count >= MaxFriends) return AddFriendResult.TargetListFull;
        return AddFriendResult.Ok;
    }

    /// <summary>Can <paramref name="me"/> block <paramref name="targetId"/>?</summary>
    public static BlockResult CanBlockUser(CharacterStore store, int me, int targetId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var mine = store.GetCharacter(me);
        var target = store.GetCharacter(targetId);
        if (target == null) return BlockResult.TargetNotFound;
        if (targetId == me || (mine != null && target.AccountId == mine.AccountId))
            return BlockResult.CannotBlock;
        var blocks = store.GetBlocks(me);
        if (blocks.Contains(targetId)) return BlockResult.AlreadyBlocked;
        if (blocks.Count >= MaxBlocks) return BlockResult.ListFull;
        return BlockResult.Ok;
    }

    /// <summary>
    /// Write the pending pair: my row type 1 (outgoing), theirs type 2 (incoming), the
    /// requester's greeting stored as the memo on BOTH sides, both ungrouped.
    /// </summary>
    public static void WriteFriendRequest(CharacterStore store, int me, int targetId, string memo)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.UpsertFriend(me, targetId, FriendTypeOutgoing, memo);
        store.UpsertFriend(targetId, me, FriendTypeIncoming, memo);
    }

    /// <summary>
    /// Turn a received request into a friendship: both rows become type 0 and both memos are
    /// cleared (the Arbiter re-adds with an empty memo, and AddToFriendListNoLock overwrites).
    /// Groups are left alone. False when there is no incoming request to accept - the Arbiter
    /// answers that case with silence.
    /// </summary>
    public static bool AcceptFriendRequest(CharacterStore store, int me, int requesterId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var row = store.GetFriendRow(me, requesterId);
        if (row == null || row.Type != FriendTypeIncoming) return false;
        var theirs = store.GetFriendRow(requesterId, me);
        store.UpsertFriend(me, requesterId, FriendTypeMutual, "", row.GroupId);
        store.UpsertFriend(requesterId, me, FriendTypeMutual, "", theirs?.GroupId ?? UngroupedGroupId);
        return true;
    }

    /// <summary>
    /// Remove both directions (one spDeleteFriendOnList call does both) and return what the
    /// relation WAS, which is what selects the system message. Null when there was no row.
    /// </summary>
    public static int? DeleteFriendPair(CharacterStore store, int me, int targetId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var row = store.GetFriendRow(me, targetId);
        if (row == null) return null;
        store.RemoveFriend(me, targetId);
        store.RemoveFriend(targetId, me);
        return row.Type;
    }

    // =====================================================================
    // C_ADD_FRIEND - the REQUEST half of the two-step flow
    // =====================================================================

    /// <summary>
    /// Handler_C_ADD_FRIEND (Arb_part_040.c:16904). Body: string name, string message (the
    /// greeting, stored as the memo on BOTH sides).
    ///
    /// <para>This does NOT create a friendship: it writes my row as type 1 (outgoing) and the
    /// target's as type 2 (incoming), exactly as User::TryToAddFriend does, and both sides get a
    /// refreshed S_FRIEND_LIST in which the pending entry shows up. The client renders the type-2
    /// entry as the invitation; accepting sends C_ACCEPT_FRIEND.</para>
    ///
    /// <para>DIVERGENCE, deliberate: the real Arbiter requires the target to be ONLINE
    /// (UserManager::FindUserWithLock) and answers SMT 430 otherwise. We look the name up in the
    /// store instead, so a friend request to an offline character works - a single-box test server
    /// could not otherwise exercise any of this. Everything else follows the decompile.</para>
    /// </summary>
    public bool OnAddFriend(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_ADD_FRIEND", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        string targetName = Str(f, "name");
        string memo = Str(f, "message");
        if (targetName.Length == 0) return true;
        // Handler_C_ADD_FRIEND rejects a memo of 21+ wchars with no reply at all.
        if (memo.Length > MaxFriendMemo) return true;

        var target = store.GetCharacterByName(targetName);
        if (target == null)
        {
            SendSmt(s, SmtTargetNotFound);
            _log.LogInformation("C_ADD_FRIEND: {Target} not found (from {Name})", targetName, chr.Name);
            return true;
        }

        int me = (int)chr.Id;
        var verdict = CanAddFriend(store, me, target.Id);
        if (verdict != AddFriendResult.Ok)
        {
            switch (verdict)
            {
                case AddFriendResult.CannotAddSelf:   SendSmt(s, SmtCannotAddSelf); break;
                case AddFriendResult.IBlockedTarget:  SendSmt(s, SmtIBlockedTarget, "UserName", target.Name); break;
                case AddFriendResult.TargetBlockedMe: SendSmt(s, SmtTargetBlockedMe, "UserName", target.Name); break;
                case AddFriendResult.AlreadyFriend:   SendSmt(s, SmtAlreadyFriend); break;
                case AddFriendResult.MyListFull:      SendSmt(s, SmtMyListFull); break;
                case AddFriendResult.TargetListFull:  SendSmt(s, SmtTargetListFull); break;
                default:                              SendSmt(s, SmtTargetNotFound); break;
            }
            _log.LogInformation("C_ADD_FRIEND: {Name} -> {Target} refused ({Why})",
                chr.Name, target.Name, verdict);
            return true;
        }

        WriteFriendRequest(store, me, target.Id, memo);
        _log.LogInformation("C_ADD_FRIEND: {Name} -> {Target} (request)", chr.Name, target.Name);

        SendSmt(s, SmtRequestSent, "UserName", target.Name);
        SendFriendList(s);
        SendUpdateFriendInfo(s);

        var peer = SessionForCharacter(target.Id);
        if (peer != null)
        {
            SendSmt(peer, SmtRequestReceived, "UserName", chr.Name);
            SendFriendList(peer);
            SendUpdateFriendInfo(peer);
        }
        return true;
    }

    // =====================================================================
    // C_ACCEPT_FRIEND
    // =====================================================================

    /// <summary>
    /// Handler_C_ACCEPT_FRIEND (Arb_part_040.c:16774). Body: string name (the requester).
    /// Flips both rows to type 0 and CLEARS both memos - the real Arbiter re-runs TryToAddFriend
    /// with an empty memo and AddToFriendListNoLock overwrites the stored one.
    /// A C_ACCEPT_FRIEND with no matching incoming request is dropped silently
    /// (User::CanAddFriendNoLock returns 0 and sends nothing).
    /// </summary>
    public bool OnAcceptFriend(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_ACCEPT_FRIEND", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        string requesterName = Str(f, "name");
        if (requesterName.Length == 0) return true;
        var requester = store.GetCharacterByName(requesterName);
        if (requester == null) { SendSmt(s, SmtTargetNotFound); return true; }

        int me = (int)chr.Id;
        if (!AcceptFriendRequest(store, me, requester.Id)) return true;   // silent, as the Arbiter
        _log.LogInformation("C_ACCEPT_FRIEND: {Name} accepted {Requester}", chr.Name, requester.Name);

        SendSmt(s, SmtAcceptedToAccepter);
        SendFriendList(s);
        SendUpdateFriendInfo(s);

        var peer = SessionForCharacter(requester.Id);
        if (peer != null)
        {
            SendSmt(peer, SmtAcceptedToRequester, "UserName", chr.Name);
            SendFriendList(peer);
            SendUpdateFriendInfo(peer);
        }
        return true;
    }

    // =====================================================================
    // C_DELETE_FRIEND - also declines and cancels requests
    // =====================================================================

    /// <summary>
    /// Handler_C_DELETE_FRIEND (Arb_part_041.c:1357) -> User::TryToDeleteFriend. One SQL call
    /// removes BOTH directions; each side gets its own S_DELETE_FRIEND (just the playerId) and
    /// the system message depends on what the relation was:
    /// type 2 = declining an invitation (3452, both), type 1 = cancelling mine (3453, both),
    /// type 0 = a real unfriend (436, deleter only).
    /// </summary>
    public bool OnDeleteFriend(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_DELETE_FRIEND", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        string targetName = Str(f, "name");
        if (targetName.Length == 0) return true;
        var target = store.GetCharacterByName(targetName);
        if (target == null) return true;                 // silent, as the Arbiter

        int me = (int)chr.Id;
        int? wasType = DeleteFriendPair(store, me, target.Id);
        if (wasType == null) return true;                // silent
        _log.LogInformation("C_DELETE_FRIEND: {Name} x {Target} (was type {Type})",
            chr.Name, target.Name, wasType);

        var peer = SessionForCharacter(target.Id);
        s.SendByDef("S_DELETE_FRIEND", new Dictionary<string, object> { ["id"] = (uint)target.Id });
        peer?.SendByDef("S_DELETE_FRIEND", new Dictionary<string, object> { ["id"] = (uint)me });

        switch (wasType)
        {
            case FriendTypeIncoming:   // I am declining their request
                SendSmt(s, SmtRequestDeclined, "RecvName", chr.Name, "ReqName", target.Name);
                if (peer != null) SendSmt(peer, SmtRequestDeclined, "RecvName", chr.Name, "ReqName", target.Name);
                break;
            case FriendTypeOutgoing:   // I am cancelling mine
                SendSmt(s, SmtRequestCancelled, "ReqName", chr.Name, "RecvName", target.Name);
                if (peer != null) SendSmt(peer, SmtRequestCancelled, "ReqName", chr.Name, "RecvName", target.Name);
                break;
            default:                   // a real friend: only the deleter is told
                SendSmt(s, SmtFriendDeleted, "UserName", target.Name);
                break;
        }
        return true;
    }

    // =====================================================================
    // Friend groups
    // =====================================================================

    /// <summary>
    /// Handler_C_ADD_FRIEND_GROUP (Arb_part_040.c:16982). Body: uint32 id, string name,
    /// array&lt;uint32&gt; friends. Creates or renames the group, then moves every listed friend
    /// into it. The real Arbiter sends NO reply - the client already updated its own panel.
    /// </summary>
    public bool OnAddFriendGroup(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_ADD_FRIEND_GROUP", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        int id = I32(f, "id");
        string name = Str(f, "name");
        if (name.Length > MaxGroupName) return true;     // handler bails, nothing stored

        int me = (int)chr.Id;
        UpdateGroup(store, me, id, name);
        foreach (var raw in List(f, "friends"))
            store.SetFriendGroup(me, Convert.ToInt32(raw), id);
        _log.LogInformation("C_ADD_FRIEND_GROUP: {Name} group {Id}", chr.Name, id);
        return true;
    }

    /// <summary>
    /// Handler_C_EDIT_FRIEND_GROUP (Arb_part_041.c:2114). Same as ADD except each array element
    /// carries its OWN target group: { uint32 playerId, uint32 id }. Also no reply.
    /// </summary>
    public bool OnEditFriendGroup(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_EDIT_FRIEND_GROUP", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        int id = I32(f, "id");
        string name = Str(f, "name");
        if (name.Length > MaxGroupName) return true;

        int me = (int)chr.Id;
        UpdateGroup(store, me, id, name);
        foreach (var raw in List(f, "friends"))
        {
            if (raw is not IReadOnlyDictionary<string, object> e) continue;
            store.SetFriendGroup(me, Convert.ToInt32(e["playerId"]), Convert.ToInt32(e["id"]));
        }
        _log.LogInformation("C_EDIT_FRIEND_GROUP: {Name} group {Id}", chr.Name, id);
        return true;
    }

    /// <summary>
    /// Handler_C_DELETE_FRIEND_GROUP (Arb_part_041.c:1404) -> User::DeleteFriendGroup, which
    /// moves every member back to group 1 before dropping the group. No reply.
    /// </summary>
    public bool OnDeleteFriendGroup(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_DELETE_FRIEND_GROUP", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        store.DeleteFriendGroup((int)chr.Id, I32(f, "id"));
        return true;
    }

    /// <summary>
    /// User::UpdateFriendGroup: only indices 2..10 exist. Anything else is dropped silently -
    /// which is why the friends in the same packet still move (the Arbiter has the same gap,
    /// AddFriendToGroup validates nothing).
    /// </summary>
    private static void UpdateGroup(CharacterStore store, int characterId, int index, string name)
    {
        if (index < MinGroupIndex || index > MaxGroupIndex) return;
        store.UpsertFriendGroup(characterId, index, name);
    }

    // =====================================================================
    // Memos
    // =====================================================================

    /// <summary>
    /// Handler_C_CHANGE_FRIEND_MEMO (Arb_part_040.c:19601). Body: int32 friendDbId, string
    /// newMemo (&gt; 20 wchars is rejected by the handler). S_RESULT_CHANGE_FRIEND_MEMO is sent
    /// in BOTH outcomes, and it carries the memo read back from storage - so a rejected change
    /// echoes the old one and the client reverts.
    /// </summary>
    public bool OnChangeFriendMemo(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_CHANGE_FRIEND_MEMO", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        int friendId = I32(f, "friendDbId");
        string memo = Str(f, "newMemo");
        int me = (int)chr.Id;
        if (memo.Length <= MaxFriendMemo && store.GetFriendRow(me, friendId) != null)
            store.SetFriendMemo(me, friendId, memo);

        s.SendByDef("S_RESULT_CHANGE_FRIEND_MEMO", new Dictionary<string, object>
        {
            ["friendDbId"] = friendId,
            ["newMemo"] = store.GetFriendRow(me, friendId)?.Memo ?? "",
        });
        return true;
    }

    /// <summary>
    /// Handler_C_EDIT_BLOCKED_USER_MEMO (Arb_part_041.c:2065). Body: uint32 id, string memo.
    /// No length check in the handler - storage truncates to 40 wchars. No reply.
    /// </summary>
    public bool OnEditBlockedUserMemo(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_EDIT_BLOCKED_USER_MEMO", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        int id = I32(f, "id");
        if (id == (int)chr.Id) return true;              // the Arbiter logs a Proxy User warning
        string memo = Str(f, "memo");
        if (memo.Length > MaxBlockMemo) memo = memo[..MaxBlockMemo];
        store.SetBlockMemo((int)chr.Id, id, memo);
        return true;
    }

    // =====================================================================
    // C_UPDATE_FRIEND_INFO - a pure refresh poll
    // =====================================================================

    /// <summary>
    /// Handler_C_UPDATE_FRIEND_INFO (Arb_part_041.c:13669) reads NOTHING out of the body and
    /// touches no storage: it answers S_INVITE_CODE_EXPIRE_TIME and S_UPDATE_FRIEND_INFO.
    /// We skip the invite-code half (no invite-code system here).
    /// </summary>
    public bool OnUpdateFriendInfo(GameSession s, ReadOnlyMemory<byte> body)
    {
        SendUpdateFriendInfo(s);
        return true;
    }

    // =====================================================================
    // Block list
    // =====================================================================

    /// <summary>
    /// Handler_C_BLOCK_USER (Arb_part_040.c:19304) -> User::CanBlockUserNoLock. Blocking also
    /// deletes any friendship (TryToDeleteFriend) and answers S_ADD_BLOCKED_USER with an EMPTY
    /// memo - the Arbiter writes L"" there unconditionally.
    /// TODO(multiplayer): also push AS_ADD_BLOCKED_USER (0x1475) to World -
    /// status/MULTIPLAYER-DESIGN.md.
    /// </summary>
    public bool OnBlockUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_BLOCK_USER", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        string targetName = Str(f, "name");
        if (targetName.Length == 0) return true;
        var target = store.GetCharacterByName(targetName);
        if (target == null) { SendSmt(s, SmtTargetNotFound); return true; }

        int me = (int)chr.Id;
        var verdict = CanBlockUser(store, me, target.Id);
        if (verdict != BlockResult.Ok)
        {
            switch (verdict)
            {
                case BlockResult.CannotBlock:    SendSmt(s, SmtCannotBlock); break;
                case BlockResult.AlreadyBlocked: SendSmt(s, SmtAlreadyBlocked); break;
                case BlockResult.ListFull:       SendSmt(s, SmtBlockListFull); break;
                default:                         SendSmt(s, SmtBlockTargetMissing); break;
            }
            return true;
        }

        store.AddBlock(me, target.Id);

        // Blocking breaks the friendship in both directions, and both sides are told.
        if (DeleteFriendPair(store, me, target.Id) != null)
        {
            s.SendByDef("S_DELETE_FRIEND", new Dictionary<string, object> { ["id"] = (uint)target.Id });
            SessionForCharacter(target.Id)?.SendByDef("S_DELETE_FRIEND",
                new Dictionary<string, object> { ["id"] = (uint)me });
        }

        _log.LogInformation("C_BLOCK_USER: {Name} -> {Target}", chr.Name, target.Name);
        s.SendByDef("S_ADD_BLOCKED_USER", new Dictionary<string, object>
        {
            ["id"] = (uint)target.Id,
            ["level"] = target.Level,
            ["class"] = target.Class,
            ["name"] = target.Name,
            ["myNote"] = "",
        });
        return true;
    }

    /// <summary>
    /// Handler_C_REMOVE_BLOCKED_USER (Arb_part_041.c:6174). Answers S_REMOVE_BLOCKED_USER with
    /// just the id; an unknown name is dropped silently (no system message, unlike C_BLOCK_USER).
    /// TODO(multiplayer): also push AS_REMOVE_BLOCKED_USER (0x1476) to World.
    /// </summary>
    public bool OnRemoveBlockedUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_REMOVE_BLOCKED_USER", body);
        if (f == null) return true;
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        string targetName = Str(f, "name");
        if (targetName.Length == 0) return true;
        var target = store.GetCharacterByName(targetName);
        if (target == null) return true;

        int me = (int)chr.Id;
        if (!store.GetBlocks(me).Contains(target.Id)) return true;
        store.RemoveBlock(me, target.Id);
        _log.LogInformation("C_REMOVE_BLOCKED_USER: {Name} x {Target}", chr.Name, target.Name);
        s.SendByDef("S_REMOVE_BLOCKED_USER", new Dictionary<string, object> { ["id"] = (uint)target.Id });
        return true;
    }

    // =====================================================================
    // Whisper (unchanged behaviour, now block-aware through the same rows)
    // =====================================================================

    public bool OnWhisper(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_WHISPER", body);
        if (f == null) return true;

        string targetName = Str(f, "target");
        string message = Str(f, "message");
        var chr = s.SelectedCharacter;
        if (chr == null) return true;

        string senderName = chr.Name;

        if (string.Equals(senderName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            SendSmt(s, 0x6F);        // 111: cannot whisper yourself
            return true;
        }

        if (!Sessions.TryGetValue(targetName, out var targetSession))
        {
            SendSmt(s, 0x33F);       // 831: user not found / offline
            _log.LogInformation("Whisper from {Name} to {Target}: offline", senderName, targetName);
            return true;
        }

        var store = Program.Store;
        if (store != null)
        {
            var targetChr = targetSession.SelectedCharacter;
            if (targetChr != null)
            {
                int me = (int)chr.Id, them = (int)targetChr.Id;
                if (store.GetBlocks(me).Contains(them) || store.GetBlocks(them).Contains(me))
                {
                    SendSmt(s, 0x71);   // 113: restricted user
                    return true;
                }
            }
        }

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

    // ---- Field helpers: a def field that is absent must never throw in a handler ----

    private static string Str(IReadOnlyDictionary<string, object> f, string name)
        => f.TryGetValue(name, out var v) ? v?.ToString() ?? "" : "";

    private static int I32(IReadOnlyDictionary<string, object> f, string name)
        => f.TryGetValue(name, out var v) && v != null ? Convert.ToInt32(v) : 0;

    private static List<object> List(IReadOnlyDictionary<string, object> f, string name)
        => f.TryGetValue(name, out var v) && v is List<object> l ? l : new List<object>();
}
