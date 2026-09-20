using System.Globalization;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

// =============================================================================================
// GuildHandlers - the Arbiter-owned half of guilds (T39). Research: status/GUILD-DESIGN.md,
// codec: GuildPackets (T36), rows: CharacterStore's guild tables.
//
// Guilds are the mirror image of parties. A party lives only in Arbiter RAM and is never
// persisted; a guild is persisted BY THE ARBITER in SQL, and World holds only a read-only mirror
// the Arbiter pushes at it. So this class is a thin, pure layer over CharacterStore rather than
// a stateful manager: every entry point is a total function of (store contents, input).
//
// The client surface is SPLIT. Nineteen guild C_ packets have an Arbiter handler; the other ten
// belong to WorldServer, which answers the Arbiter with an SA_ frame. Answering one of World's
// in here would double-answer the client - GuildPackets.ArbiterHandlesClientPacket(op) is that
// line, and Handles(op) below is the subset T39 implements.
//
// NOTHING IS WIRED UP. HandlerRegistry, GameSession and WorldBridge are human-owned and
// untouched; the exact registration diff is in status/GUILD-DESIGN.md section 10, and since T41
// it is three lines because World/ActionDispatcher.cs performs the sends. Like PartyManager,
// this returns ACTIONS:
//
//   OnClientPacket(characterId, opcode, body) -> GuildActions { ToClients, ToWorld, Rejected }
//
// Client packets come back as a NAME plus a field dictionary rather than bytes, because
// GameSession.SendByDef already turns exactly that into bytes through the .def codec - and
// because ten of the guild .def files are wrong (GUILD-DESIGN.md section 5.5). ResolveDef()
// below is what makes the corrections real: it prefers GuildPackets.CorrectedDefs and
// GuildPackets.NamedDefs over the shipped file, so a handler emitting S_ADD_GUILD_MEMBER gets
// the 0x38-byte layout the Arbiter's dumper proves rather than the .def's 0x33.
//
// Arbiter -> World frames ARE bytes, built by GuildPackets and byte-exact against the layouts in
// GUILD-DESIGN.md section 4.1. They are emitted but unwired, exactly like PartyManager's.
// =============================================================================================

/// <summary>
/// One packet for one character. Addressed by character db id, not by session ticket, because
/// that is the key the guild tables use - the routing layer resolves db id -> ticket and drops
/// the members who are offline.
/// </summary>
public readonly record struct GuildClientAction(
    int CharacterId, string PacketName, IReadOnlyDictionary<string, object>? Fields, byte[]? RawBody)
    : IArbiterClientAction
{
    public static GuildClientAction Def(int characterId, string name, IReadOnlyDictionary<string, object> fields)
        => new(characterId, name, fields, null);

    /// <summary>A body GuildPackets built directly, for the packets whose .def cannot express
    /// the layout. ActionDispatcher frames it by packet name.</summary>
    public static GuildClientAction Raw(int characterId, string name, byte[] body)
        => new(characterId, name, null, body);

    public bool IsRaw => RawBody != null;

    /// <summary>Guilds address clients by character db id - GameSession.PlayerId - because that
    /// is the key the guild tables use.</summary>
    public Recipient To => Recipient.Player(CharacterId);

    /// <summary>Always null: a guild action's raw form is a BODY, not a framed packet.</summary>
    public byte[]? RawPacket => null;
}

/// <summary>Everything one input produced. Empty is a valid answer - a window-type-1 probe on a
/// guild that exists says nothing at all.</summary>
public sealed class GuildActions : IArbiterActions
{
    public List<GuildClientAction> ToClients { get; } = new();
    public List<WorldAction> ToWorld { get; } = new();

    private readonly List<IArbiterAction> _ordered = new();

    /// <summary>
    /// Every client packet and World frame in the order the handlers produced them - the list
    /// ActionDispatcher walks. The two typed lists above hold the same items and stay because
    /// the guild tests read them; nothing writes to either directly.
    /// </summary>
    public IReadOnlyList<IArbiterAction> Ordered => _ordered;

    /// <summary>The character whose packet caused this, so a rejection has somewhere to go.</summary>
    public Recipient Origin { get; internal set; } = Recipient.None;

    /// <summary>
    /// Why nothing happened, when nothing happened for a reason worth logging. The real Arbiter
    /// answers most of these with S_SYSTEM_MESSAGE - 0x127 "no such guild", 0x114 "not the guild
    /// master", 0xE2B/0xE2C/0xE2E/0xE36 on the invite path. This carries the reason rather than
    /// guessing at message ids we have never seen on the wire; SystemMessage below has the four
    /// the decompile does name.
    /// </summary>
    public string? Rejected { get; set; }

    public bool IsEmpty => ToClients.Count == 0 && ToWorld.Count == 0;

    internal GuildActions Reject(string why) { Rejected = why; return this; }

    internal void Client(GuildClientAction a) { ToClients.Add(a); _ordered.Add(a); }

    internal void World(ushort op, byte[] payload)
    {
        var w = new WorldAction(op, payload);
        ToWorld.Add(w);
        _ordered.Add(w);
    }
}

public sealed class GuildHandlers
{
    private readonly CharacterStore _store;
    private readonly ILogger _log;

    public GuildHandlers(CharacterStore store, ILogger log)
    {
        _store = store;
        _log = log;
    }

    // ---- system message ids the decompile names on these paths ----
    /// <summary>"that guild does not exist" - FUN_1403aa760(user, 0x127, 0).</summary>
    public const int MsgNoSuchGuild = 0x127;
    /// <summary>"you are not the guild master" - the C_SET_GUILD_JOIN_CONDITION guard.</summary>
    public const int MsgNotGuildMaster = 0x114;
    /// <summary>"no such user" on the invite path.</summary>
    public const int MsgNoSuchUser = 0xE2B;
    /// <summary>"you do not have the authority" on the invite path.</summary>
    public const int MsgNoAuthority = 0xE2C;
    /// <summary>T52, from Handler_C_INVITE_USER_TO_GUILD (FUN_1404e1890) and
    /// GuildJoinManager::AddInviteUserToGuildInfo (FUN_140825d20). The key names are the
    /// binary's and the CASE matters - the invite path spells them lowercase
    /// (L"userName", L"guildName") while the leave path spells them capitalised.</summary>
    public const int MsgNotOnWantedList = 0xE2E;   // fromWantedList set, but they are not on it
    public const int MsgInviteSent = 0xE2F;        // -> the inviter,  {userName}
    public const int MsgInviteReceived = 0xE30;    // -> the target,   {guildName}
    public const int MsgAlreadyInAGuild = 0xE36;   // -> the inviter,  {userName}

    /// <summary>
    /// C_REQUEST_GUILD_INFO's windowType switch, from FUN_1404e7e80. 1 is a bare probe; 6 is the
    /// guild-quest panel, which needs the quest tables T39 does not build.
    /// </summary>
    public const int WindowProbe = 1, WindowInfoForMember = 2, WindowInfoForOutsider = 3,
                     WindowMemberList = 5, WindowQuests = 6, WindowApplyList = 0x0B;

    /// <summary>
    /// Seconds a character must wait before joining another guild, from Guild::CanRejoinGuild
    /// (it compares User+0x3c48, the leave time, against a config value). We have never seen
    /// that config, so the default is 0 = no cooldown; set it when the value turns up.
    /// </summary>
    public static int RejoinCooldownSeconds { get; set; }

    /// <summary>The opcodes THIS class answers. A subset of
    /// GuildPackets.ArbiterHandlesClientPacket: C_INVITE_USER_TO_GUILD needs the target's live
    /// session and C_CHANGE_GUILDNAME needs the SDB_ASK_CHANGE_GUILD_NAME round trip, so both
    /// wait for routing.</summary>
    public static bool Handles(ushort opcode) => opcode switch
    {
        GuildPackets.C_REQUEST_GUILD_INFO => true,
        GuildPackets.C_REQUEST_GUILD_MEMBER_LIST => true,
        GuildPackets.C_GET_GUILD_HISTORY => true,
        GuildPackets.C_GUILD_APPLY_LIST => true,
        GuildPackets.C_GUILD_APPLY_LIST_PAGE => true,
        GuildPackets.C_GET_USER_GUILD_LOGO => true,
        GuildPackets.C_UPDATE_GUILD_LOGO => true,
        GuildPackets.C_UPDATE_GUILD_TITLE => true,
        GuildPackets.C_SET_GUILD_JOIN_CONDITION => true,
        GuildPackets.C_REQUEST_COOLTIME_TO_JOIN_GUILD => true,
        GuildPackets.C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD => true,
        GuildPackets.C_CHECK_CHANGE_GUILDNAME => true,
        GuildPackets.C_APPLY_GUILD => true,
        GuildPackets.C_ACCEPT_GUILD_APPLY => true,
        GuildPackets.C_REJECT_INVITE_USER_TO_GUILD => true,
        // T52: the two section 10 left "for routing". The session lookup the first one waited
        // for is WorldBridge.SessionForPlayerId (T47), and the second turned out not to need the
        // SDB_ASK_CHANGE_GUILD_NAME round trip at all - see ChangeGuildName.
        GuildPackets.C_INVITE_USER_TO_GUILD => true,
        GuildPackets.C_CHANGE_GUILDNAME => true,
        C_REQUEST_UPDATE_ANNOUNCE => true,
        C_REQUEST_UPDATE_INTRODUCE => true,
        // T135, the guild-quest board. This predicate - not the dispatch switch - is the
        // declared set: GuildWiring registers from ClientOpcodes and T51 compares the two, so
        // a case without an entry here reads as "registered but unanswered" and every one of
        // those three would have come back to the player as a rejection.
        GuildPackets.C_REQUEST_START_GUILD_QUEST => true,
        GuildPackets.C_REQUEST_FINISH_GUILD_QUEST => true,
        GuildPackets.C_REQUEST_CANCEL_GUILD_QUEST => true,
        _ => false,
    };

    /// <summary>
    /// C_REQUEST_UPDATE_ANNOUNCE (0x9CB1): `[u16 motdOff]` + wstring, min total 6.
    /// NOT in GuildPackets because its name has no GUILD in it, which is why T36's sweep missed
    /// it: Handler_C_REQUEST_UPDATE_ANNOUNCE::_ChangeGuildNoticeCallback::OnSuccess
    /// (Arb_part_040.c:6664) -> Guild::UpdateGuildAnnounce (Arb_part_046.c:14684).
    /// </summary>
    public const ushort C_REQUEST_UPDATE_ANNOUNCE = 0x9CB1;

    /// <summary>
    /// C_REQUEST_UPDATE_INTRODUCE (0xD434): `[u16 messageOff]` + wstring, min total 6.
    /// Handler_C_REQUEST_UPDATE_INTRODUCE::_ChangeGuildIntroduceCallback::OnSuccess
    /// (Arb_part_040.c:6579) -> Guild::UpdateGuildmemberIntroduce (Arb_part_046.c:16088).
    /// This is the MEMBER's own note, not the guild's announce.
    /// </summary>
    public const ushort C_REQUEST_UPDATE_INTRODUCE = 0xD434;

    public GuildActions OnClientPacket(int characterId, ushort opcode, byte[] body)
    {
        var a = new GuildActions { Origin = Recipient.Player(characterId) };
        switch (opcode)
        {
            case GuildPackets.C_REQUEST_GUILD_INFO: return RequestGuildInfo(a, characterId, body);
            case GuildPackets.C_REQUEST_GUILD_MEMBER_LIST: return RequestMemberList(a, characterId);
            case GuildPackets.C_GET_GUILD_HISTORY: return GetGuildHistory(a, characterId, body);
            case GuildPackets.C_GUILD_APPLY_LIST: return SendApplyList(a, characterId, 1);
            case GuildPackets.C_GUILD_APPLY_LIST_PAGE: return ApplyListPage(a, characterId, body);
            case GuildPackets.C_GET_USER_GUILD_LOGO: return GetUserGuildLogo(a, body);
            case GuildPackets.C_UPDATE_GUILD_LOGO: return UpdateGuildLogo(a, characterId, body);
            case GuildPackets.C_UPDATE_GUILD_TITLE: return UpdateGuildTitle(a, characterId, body);
            case GuildPackets.C_SET_GUILD_JOIN_CONDITION: return SetJoinCondition(a, characterId, body);
            case GuildPackets.C_REQUEST_COOLTIME_TO_JOIN_GUILD: return RequestRejoinCooltime(a, characterId);
            case GuildPackets.C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD: return InfoBeforeApply(a, characterId, body);
            case GuildPackets.C_CHECK_CHANGE_GUILDNAME: return CheckChangeGuildName(a, characterId, body);
            case GuildPackets.C_APPLY_GUILD: return ApplyGuild(a, characterId, body);
            case GuildPackets.C_ACCEPT_GUILD_APPLY: return AcceptGuildApply(a, characterId, body);
            case GuildPackets.C_REJECT_INVITE_USER_TO_GUILD: return RejectInvite(a, characterId, body);
            case GuildPackets.C_INVITE_USER_TO_GUILD: return InviteUserToGuild(a, characterId, body);
            case GuildPackets.C_CHANGE_GUILDNAME: return ChangeGuildName(a, characterId, body);
            case C_REQUEST_UPDATE_ANNOUNCE: return UpdateAnnounce(a, characterId, body);
            case C_REQUEST_UPDATE_INTRODUCE: return UpdateIntroduce(a, characterId, body);
            case GuildPackets.C_REQUEST_START_GUILD_QUEST: return StartGuildQuest(a, characterId, body);
            case GuildPackets.C_REQUEST_FINISH_GUILD_QUEST: return FinishGuildQuest(a, characterId, body);
            case GuildPackets.C_REQUEST_CANCEL_GUILD_QUEST: return CancelGuildQuest(a, characterId, body);
            default:
                return a.Reject(GuildPackets.ArbiterHandlesClientPacket(opcode)
                    ? $"0x{opcode:X4} is Arbiter-side but needs routing - T39 does not answer it"
                    : $"0x{opcode:X4} is not an Arbiter-side guild packet");
        }
    }

    // =======================================================================================
    // Guild creation. There is no C_ packet for it: the client asks WorldServer, which sends
    // SDB_CREATE_GUILD2 (0x27D4) down the DB-proxy link, and the Arbiter answers
    // DBS_CREATE_GUILD2 (0x27D5). This is the Arbiter half of that, ready for the DB-proxy
    // handler to call - GUILD-DESIGN.md section 6.
    // =======================================================================================

    /// <summary>
    /// GuildManager::CreateGuildData + GuildCreateWorker::Response. Creates the guild, its two
    /// seed ranks and the chief's member row, logs the creation, and drops every application
    /// and invitation the founder had outstanding. Returns the new guild id, or 0 when the name
    /// is taken or the founder is already in a guild.
    /// </summary>
    public int CreateGuild(GuildActions a, int chiefId, string name,
        string masterGroupName = "Master", string memberGroupName = "Member", bool warAcceptable = false)
    {
        if (a.Origin.Kind == RecipientKind.None) a.Origin = Recipient.Player(chiefId);
        var chief = _store.GetCharacter(chiefId);
        if (chief == null) { a.Reject($"character {chiefId} does not exist"); return 0; }
        if (_store.GetGuildIdOf(chiefId) != 0) { a.Reject("the founder is already in a guild"); return 0; }

        int guildId = _store.CreateGuild(name, chiefId, warAcceptable, masterGroupName, memberGroupName);
        if (guildId == 0) { a.Reject($"guild name '{name}' is taken"); return 0; }

        // The chief goes on rank 1, the officer rank SDB_CREATE_GUILD2 names.
        _store.AddGuildMember(guildId, chiefId, chief.Name, chief.Race, chief.Class, chief.Gender,
            chief.Level, chief.AccountId, guildGroupId: 1);
        _store.DeleteGuildAppliesOfUser(chiefId);
        _store.DeleteGuildInvitesOfUser(chiefId);
        _store.AddGuildLog(guildId, GuildLogCreate, chief.Name, actorDbId: chiefId);

        a.Client(GuildClientAction.Def(chiefId, "S_REQUEST_JOIN_GUILD_NOTICE", Empty));
        EmitMemberAdded(a, guildId, chiefId);
        return guildId;
    }

    /// <summary>GuildUtil::UserJoinToGuild writes action 0x0B for a join; the create entry is the
    /// one before it. Both ids are ours - the real log's action enum is not in the decompile.</summary>
    public const int GuildLogCreate = 0x0A, GuildLogJoin = 0x0B, GuildLogLeave = 0x0C;

    /// <summary>
    /// Three the decompile DOES name - T57. They are the second argument to Guild::AddGuildLog
    /// (FUN_140566090) on the three guild-group paths: 0x15 when a group is added
    /// (Handler_SA_CREATE_GUILD_GROUP::_AddGuildGroupNameCallback::OnSuccess,
    /// Arb_part_072.c:17662), 0x16 when one is removed (Handler_SA_REMOVE_GUILD_GROUP, :14689),
    /// 0x20 when a member moves between groups (Handler_SA_CHANGE_GUILDGROUP, :13524).
    /// </summary>
    public const int GuildLogAddGroup = 0x15, GuildLogRemoveGroup = 0x16, GuildLogChangeMemberGroup = 0x20;

    // =======================================================================================
    // Read-only windows
    // =======================================================================================

    /// <summary>
    /// C_REQUEST_GUILD_INFO (0x5B51): `[i32 guildDbId][i32 windowType]`. FUN_1404e7e80 switches
    /// on the type; a missing guild answers S_EMPTY_GUILD_WINDOW and, for every type but the
    /// bare probe, system message 0x127.
    /// </summary>
    private GuildActions RequestGuildInfo(GuildActions a, int characterId, byte[] body)
    {
        var req = GuildPackets.ParseCRequestGuildInfo(body);
        if (req == null) return a.Reject("C_REQUEST_GUILD_INFO: short body");
        var (guildId, windowType) = req.Value;

        var guild = _store.GetGuild(guildId);
        if (guild == null)
        {
            a.Client(GuildClientAction.Def(characterId, "S_EMPTY_GUILD_WINDOW", Empty));
            return windowType == WindowProbe ? a : a.Reject($"no guild {guildId} (system message 0x{MsgNoSuchGuild:X})");
        }

        switch (windowType)
        {
            case WindowProbe:
                return a;
            case WindowInfoForMember:
                SendGuildInfo(a, characterId, guild);
                return SendHistory(a, characterId, guild.GuildId, 1);
            case WindowInfoForOutsider:
                SendGuildInfo(a, characterId, guild);
                return a;
            case WindowMemberList:
                SendMemberList(a, characterId, guild);
                return a;
            case WindowApplyList:
                return SendApplyListFor(a, characterId, guild, 1);
            case WindowQuests:
                return a.Reject("guild quests are not implemented (window type 6)");
            default:
                return a.Reject($"unknown guild window type {windowType}");
        }
    }

    /// <summary>C_REQUEST_GUILD_MEMBER_LIST (0x6657): empty body, and it uses the CALLER's own
    /// guild - FUN_1404e8ab0 calls User::GetGuild, it does not read an id.</summary>
    private GuildActions RequestMemberList(GuildActions a, int characterId)
    {
        var guild = MyGuild(characterId);
        if (guild == null)
        {
            a.Client(GuildClientAction.Def(characterId, "S_EMPTY_GUILD_WINDOW", Empty));
            return a;
        }
        SendMemberList(a, characterId, guild);
        return a;
    }

    /// <summary>C_GET_GUILD_HISTORY (0xDC60): `[i32 viewPage]`, the caller's own guild.</summary>
    private GuildActions GetGuildHistory(GuildActions a, int characterId, byte[] body)
    {
        int? page = GuildPackets.ParseCGetGuildHistory(body);
        if (page == null) return a.Reject("C_GET_GUILD_HISTORY: short body");
        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject("C_GET_GUILD_HISTORY: caller is in no guild");
        return SendHistory(a, characterId, guild.GuildId, page.Value);   // clamped in SendHistory
    }

    /// <summary>C_GUILD_APPLY_LIST_PAGE (0xDB53): `[i32 pageNumber]`.</summary>
    private GuildActions ApplyListPage(GuildActions a, int characterId, byte[] body)
    {
        int? page = GuildPackets.ParseCGuildApplyListPage(body);
        if (page == null) return a.Reject("C_GUILD_APPLY_LIST_PAGE: short body");
        return SendApplyList(a, characterId, page.Value);
    }

    private GuildActions SendApplyList(GuildActions a, int characterId, int page)
    {
        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject("C_GUILD_APPLY_LIST: caller is in no guild");
        return SendApplyListFor(a, characterId, guild, page);
    }

    /// <summary>C_GET_USER_GUILD_LOGO (0x584B): `[i32 userDbId][i32 guildDbId]`. FUN_1404e11a0
    /// drops the request silently when guildDbId is 0.</summary>
    private GuildActions GetUserGuildLogo(GuildActions a, byte[] body)
    {
        var req = GuildPackets.ParseCGetUserGuildLogo(body);
        if (req == null) return a.Reject("C_GET_USER_GUILD_LOGO: short body");
        var (userDbId, guildId) = req.Value;
        if (guildId == 0) return a.Reject("C_GET_USER_GUILD_LOGO: guildDbId 0 - the real handler drops it");

        var logo = _store.GetGuildLogo(guildId) ?? Array.Empty<byte>();
        a.Client(GuildClientAction.Def(userDbId, "S_GET_USER_GUILD_LOGO", new Dictionary<string, object>
        {
            ["playerId"] = userDbId,
            ["guildId"] = guildId,
            ["logo"] = logo,
        }));
        return a;
    }

    /// <summary>
    /// C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD (0xC046): `[u16 nameOff][i32 guildDbId]`; the name
    /// is only consulted when the id is 0. FUN_1404e8100 also refuses anyone already in a guild.
    /// </summary>
    private GuildActions InfoBeforeApply(GuildActions a, int characterId, byte[] body)
    {
        var req = GuildPackets.ParseCRequestGuildInfoBeforeApply(body);
        if (req == null) return a.Reject("C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD: short body");
        var (guildName, guildId) = req.Value;

        if (_store.GetGuildIdOf(characterId) != 0)
            return a.Reject("C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD: caller is already in a guild");

        var guild = guildId != 0 ? _store.GetGuild(guildId) : _store.GetGuildByName(guildName);
        if (guild == null) return a.Reject($"no such guild (system message 0x{MsgNoSuchGuild:X})");

        a.Client(GuildClientAction.Def(characterId, "S_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD",
            new Dictionary<string, object>
            {
                ["guildName"] = guild.Name,
                // GuildWarDeclareCount comes from the guild-war tables T39 does not build.
                ["guildWarDeclareCount"] = 0,
                ["isGuildWarAcceptable"] = guild.WarAcceptable,
            }));
        return a;
    }

    /// <summary>
    /// C_REQUEST_COOLTIME_TO_JOIN_GUILD (0xC9C4): empty body. FUN_1404e75f0 asks
    /// Guild::CanRejoinGuild about the character's stored leave time and answers
    /// `[u8 onCooltime][i64 endTimestamp]`.
    /// </summary>
    private GuildActions RequestRejoinCooltime(GuildActions a, int characterId)
    {
        long leftAt = _store.GetGuildLeaveTime(characterId);
        long endsAt = leftAt == 0 ? 0 : leftAt + RejoinCooldownSeconds;
        bool onCooltime = endsAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        a.Client(GuildClientAction.Def(characterId, "S_REQUEST_COOLTIME_TO_JOIN_GUILD",
            new Dictionary<string, object>
            {
                ["hasCoolTime"] = onCooltime,
                ["coolTime"] = onCooltime ? endsAt : 0L,
            }));
        return a;
    }

    // =======================================================================================
    // Writes
    // =======================================================================================

    /// <summary>
    /// C_UPDATE_GUILD_LOGO (0x60C1): a BYTES field, not a string - `[u16 off][u16 len]` + the raw
    /// image. Guild::UpdateGuildLogo is chief-only and rejects `8000 &lt; len`. The image itself
    /// never crosses the World link; only the new logo ID does.
    /// </summary>
    private GuildActions UpdateGuildLogo(GuildActions a, int characterId, byte[] body)
    {
        byte[]? logo = GuildPackets.ParseCUpdateGuildLogo(body);
        if (logo == null) return a.Reject("C_UPDATE_GUILD_LOGO: short body or over the 8000-byte cap");
        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject("C_UPDATE_GUILD_LOGO: caller is in no guild");
        if (guild.ChiefDbId != characterId)
            return a.Reject($"C_UPDATE_GUILD_LOGO: chief only (system message 0x{MsgNotGuildMaster:X})");

        int logoId = _store.UpdateGuildLogo(guild.GuildId, logo);
        if (logoId == 0) return a.Reject("C_UPDATE_GUILD_LOGO: the store refused the blob");

        a.World(GuildPackets.AS_UPDATE_GUILD_LOGO,
            GuildPackets.BuildAsGuildString(guild.GuildId, logoId.ToString(CultureInfo.InvariantCulture)));
        return a;
    }

    /// <summary>C_UPDATE_GUILD_TITLE (0x807B): `[u16 titleOff]` + wstring, wchar[15] in GuildData.</summary>
    private GuildActions UpdateGuildTitle(GuildActions a, int characterId, byte[] body)
    {
        string? title = GuildPackets.ParseCSingleString(body);
        if (title == null) return a.Reject("C_UPDATE_GUILD_TITLE: short body");
        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject("C_UPDATE_GUILD_TITLE: caller is in no guild");
        if (guild.ChiefDbId != characterId)
            return a.Reject($"C_UPDATE_GUILD_TITLE: chief only (system message 0x{MsgNotGuildMaster:X})");

        if (!_store.UpdateGuildTitle(guild.GuildId, title)) return a.Reject("C_UPDATE_GUILD_TITLE: no rows");
        a.World(GuildPackets.AS_UPDATE_GUILD_TITLE,
            GuildPackets.BuildAsGuildString(guild.GuildId, Truncate(title, CharacterStore.MaxGuildTitle)));
        return a;
    }

    /// <summary>
    /// C_REQUEST_UPDATE_ANNOUNCE (0x9CB1). Guild::UpdateGuildAnnounce HTML-escapes the text
    /// (&amp; then &lt; then &gt;, in that order), needs GuildAuthority bit 2 (mask 4) rather
    /// than the chief, writes spUpdateGuildAnnounce, truncates into GuildData+0x118 at 200
    /// chars, and then broadcasts. We emit the broadcast to every member; the routing layer
    /// drops the offline ones.
    /// </summary>
    private GuildActions UpdateAnnounce(GuildActions a, int characterId, byte[] body)
    {
        string? raw = GuildPackets.ParseCSingleString(body);
        if (raw == null) return a.Reject("C_REQUEST_UPDATE_ANNOUNCE: short body");
        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject("C_REQUEST_UPDATE_ANNOUNCE: caller is in no guild");
        if (!_store.HasGuildAuthority(guild.GuildId, characterId, CharacterStore.GuildAuthorityAnnounce))
            return a.Reject($"C_REQUEST_UPDATE_ANNOUNCE: no announce authority (0x{MsgNoAuthority:X})");

        string announce = Truncate(EscapeGuildText(raw), CharacterStore.MaxGuildAnnounce);
        if (!_store.UpdateGuildAnnounce(guild.GuildId, announce))
            return a.Reject("C_REQUEST_UPDATE_ANNOUNCE: no rows");

        var fields = new Dictionary<string, object> { ["motd"] = announce };
        foreach (var m in _store.GetGuildMembers(guild.GuildId))
            a.Client(GuildClientAction.Def(m.UserDbId, "S_UPDATE_GUILD_ANNOUNCE", fields));
        return a;
    }

    /// <summary>
    /// The three replacements Guild::UpdateGuildAnnounce runs, in the binary's order - ampersand
    /// first, so an escape it introduces is not escaped again.
    /// </summary>
    public static string EscapeGuildText(string s)
        => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// C_REQUEST_UPDATE_INTRODUCE (0xD434) - the MEMBER's own note, wchar[31].
    /// Guild::UpdateGuildmemberIntroduce only checks that the caller is in the map; it writes
    /// spUpdateUserGuildIntroduce and does NOT broadcast, so this emits no client packet.
    /// </summary>
    private GuildActions UpdateIntroduce(GuildActions a, int characterId, byte[] body)
    {
        string? text = GuildPackets.ParseCSingleString(body);
        if (text == null) return a.Reject("C_REQUEST_UPDATE_INTRODUCE: short body");
        if (_store.GetGuildMember(characterId) == null)
            return a.Reject("C_REQUEST_UPDATE_INTRODUCE: caller is in no guild");
        if (!_store.UpdateGuildMemberIntroduce(characterId, text))
            return a.Reject("C_REQUEST_UPDATE_INTRODUCE: no rows");
        return a;   // the real handler broadcasts nothing
    }

    /// <summary>
    /// C_SET_GUILD_JOIN_CONDITION (0xFFDB): `[u16 introOff][i32 min][i32 max][i32 joinType]
    /// [i32 preference]`, chief only (FUN_1404ed2d0 answers system message 0x114 otherwise).
    /// The "introduction" string is the guild's recruit blurb - GuildData+0x2194, the same field
    /// S_GUILD_INFO calls GuildPromotion. The reply carries only a success bool; the values come
    /// back in the next S_GUILD_INFO.
    /// </summary>
    private GuildActions SetJoinCondition(GuildActions a, int characterId, byte[] body)
    {
        var req = GuildPackets.ParseCSetGuildJoinCondition(body);
        if (req == null) return a.Reject("C_SET_GUILD_JOIN_CONDITION: short body");
        var (introduction, minLevel, maxLevel, joinType, preference) = req.Value;

        var guild = MyGuild(characterId);
        bool ok = guild != null && guild.ChiefDbId == characterId;
        if (ok)
        {
            _store.UpdateGuildJoinCondition(guild!.GuildId, minLevel, maxLevel, joinType, preference);
            _store.UpdateGuildPromotion(guild.GuildId, EscapeGuildText(introduction));
        }
        a.Client(GuildClientAction.Def(characterId, "S_SET_GUILD_JOIN_CONDITION",
            new Dictionary<string, object> { ["success"] = ok }));
        if (!ok) a.Reject($"C_SET_GUILD_JOIN_CONDITION: chief only (system message 0x{MsgNotGuildMaster:X})");
        return a;
    }

    /// <summary>
    /// C_CHECK_CHANGE_GUILDNAME (0xB77C): `[u16 nameOff]`. FUN_1404dce30 is chief-only and always
    /// answers S_CHECK_CHANGE_GUILDNAME, echoing the name back with a usable flag. The .def is
    /// missing that flag - GuildPackets.CorrectedDefs has the 0x07-byte version.
    /// </summary>
    private GuildActions CheckChangeGuildName(GuildActions a, int characterId, byte[] body)
    {
        string? name = GuildPackets.ParseCSingleString(body);
        if (name == null) return a.Reject("C_CHECK_CHANGE_GUILDNAME: short body");
        var guild = MyGuild(characterId);
        bool usable = guild != null && guild.ChiefDbId == characterId
                      && name.Length > 0 && name.Length <= CharacterStore.MaxGuildName
                      && !_store.GuildNameTaken(name);
        a.Client(GuildClientAction.Def(characterId, "S_CHECK_CHANGE_GUILDNAME",
            new Dictionary<string, object> { ["guildName"] = name, ["usable"] = usable }));
        return a;
    }

    // =======================================================================================
    // Applications
    // =======================================================================================

    /// <summary>
    /// C_APPLY_GUILD (0xA0DF): `[u16 guildNameOff][u16 joinMsgOff]`. FUN_1404db730 looks the
    /// guild up BY NAME, refuses an applicant who is already in a guild, writes
    /// spInsertGuildApply, and then broadcasts S_GUILD_APPLY_COUNT - to the members who hold the
    /// invite authority, since they are the ones who can act on it.
    /// </summary>
    private GuildActions ApplyGuild(GuildActions a, int characterId, byte[] body)
    {
        var req = GuildPackets.ParseCApplyGuild(body);
        if (req == null) return a.Reject("C_APPLY_GUILD: short body");
        var (guildName, joinMsg) = req.Value;

        var guild = _store.GetGuildByName(guildName);
        if (guild == null) return a.Reject($"no guild '{guildName}' (system message 0x{MsgNoSuchGuild:X})");
        if (_store.GetGuildIdOf(characterId) != 0)
            return a.Reject("C_APPLY_GUILD: applicant is already in a guild");
        var me = _store.GetCharacter(characterId);
        if (me == null) return a.Reject($"character {characterId} does not exist");
        if (me.Level < guild.JoinMinLevel || me.Level > guild.JoinMaxLevel)
            return a.Reject($"C_APPLY_GUILD: level {me.Level} is outside {guild.JoinMinLevel}..{guild.JoinMaxLevel}");

        _store.InsertGuildApply(guild.GuildId, characterId, EscapeGuildText(joinMsg));
        // cap_final_client3 2939 / 2997: the applicant is told, with the name they typed.
        Smt(a, characterId, SmtGuildApplySent, "GuildName", guildName);
        BroadcastApplyCount(a, guild);
        return a;
    }

    // ===================================================================================
    // T92 - the two system messages the apply flow sends, and the guild-data push.
    // Ground truth: cap_final_client3 2939 and 2997, cap_final_client4 2789, tap 2501.
    // ===================================================================================

    /// <summary>
    /// Sent to the APPLICANT the moment the application lands - cap_final_client3 frame 2939
    /// is <c>@1604\vGuildName\vfdh</c>, and 2997 repeats it for the second application. The
    /// key is the guild NAME the client asked for, not the row s.
    /// </summary>
    public const int SmtGuildApplySent = 1604;

    /// <summary>
    /// Sent to the OFFICER who turned an application down - cap_final_client4 frame 2789 is
    /// <c>@261\vName\vjoinguild</c>, the applicant s name. Only the reject branch sends it;
    /// the accept at 2834 answers with S_ADD_GUILD_MEMBER instead.
    /// </summary>
    public const int SmtGuildApplyRejected = 261;

    // AS_UPDATE_GUILD_DATA (0x144E) lives with the other guild opcodes now: T92b found that
    // GuildPackets.BuildAsGuildData already builds exactly tap 2501's payload - the same two
    // header words - for AS_LOAD_GUILD_DATA (0x144D), one opcode along. GuildWiring pushes it
    // through GuildWiring.BuildGuildDataPush, so the duplicate builder that stood here is gone.

    /// <summary>
    /// C_ACCEPT_GUILD_APPLY (0xDBE4): `[u8 accept][u32 userDbId]` - unaligned, no padding after
    /// the bool. FUN_1404d8590 needs the invite authority, then re-sends the apply list forced to
    /// page 1 whichever way the answer went.
    /// </summary>
    private GuildActions AcceptGuildApply(GuildActions a, int characterId, byte[] body)
    {
        var req = GuildPackets.ParseCAcceptGuildApply(body);
        if (req == null) return a.Reject("C_ACCEPT_GUILD_APPLY: short body");
        var (accept, applicantId) = req.Value;

        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject("C_ACCEPT_GUILD_APPLY: caller is in no guild");
        if (!_store.HasGuildAuthority(guild.GuildId, characterId, CharacterStore.GuildAuthorityInvite))
            return a.Reject($"C_ACCEPT_GUILD_APPLY: no invite authority (0x{MsgNoAuthority:X})");

        bool joined = false;
        if (accept)
        {
            var applicant = _store.GetCharacter(applicantId);
            if (applicant == null)
                return a.Reject($"C_ACCEPT_GUILD_APPLY: applicant {applicantId} does not exist (0x{MsgNoSuchUser:X})");
            if (_store.GetGuildIdOf(applicantId) != 0)
                return a.Reject("C_ACCEPT_GUILD_APPLY: applicant joined another guild first");

            long joinDate = _store.AddGuildMember(guild.GuildId, applicantId, applicant.Name,
                applicant.Race, applicant.Class, applicant.Gender, applicant.Level, applicant.AccountId);
            joined = joinDate != 0;
            if (joined)
            {
                _store.AddGuildLog(guild.GuildId, GuildLogJoin, applicant.Name, actorDbId: applicantId);
                _store.DeleteGuildAppliesOfUser(applicantId);
                _store.DeleteGuildInvitesOfUser(applicantId);
                EmitMemberAdded(a, guild.GuildId, applicantId);
            }
        }
        if (!joined)
        {
            _store.DeleteGuildApply(guild.GuildId, applicantId);
            // cap_final_client4 2789: only the REJECT branch answers, and it answers the
            // officer. An accept that failed for another reason has already been rejected out
            // of this method, so reaching here with accept still set cannot happen.
            if (!accept)
                Smt(a, characterId, SmtGuildApplyRejected, "Name",
                    _store.GetCharacter(applicantId)?.Name ?? string.Empty);
        }

        SendApplyListFor(a, characterId, guild, 1);
        BroadcastApplyCount(a, guild);
        return a;
    }

    // =======================================================================================
    // T52 - the two packets T39 left "for routing" (GUILD-DESIGN.md section 12).
    // =======================================================================================

    /// <summary>
    /// C_INVITE_USER_TO_GUILD (0xEF92): `[u16 nameOff][u32 userDbId][u8 fromWantedList]`, min
    /// total 0x0B. Handler FUN_1404e1890 (Arb_part_041.c) in the binary's own order:
    /// <list type="number">
    /// <item>the caller must be in a guild — else system message 0xE2B;</item>
    /// <item><c>Guild::HaveGuildAuthority(guild, user, invite)</c> — else 0xE2C;</item>
    /// <item>resolve the target: by NAME when <c>userDbId</c> is 0, by db id otherwise — a target
    /// that cannot be resolved is 0xE2B again;</item>
    /// <item>the target must not already be in a guild — else 0xE36 with <c>userName</c>;</item>
    /// <item>if <c>fromWantedList</c>, the target must actually be on the wanted board — else
    /// 0xE2E;</item>
    /// <item>then <c>GuildJoinManager::AddInviteUserToGuildInfo</c>: reject a duplicate invite,
    /// <c>spAddInviteUserToGuild</c>, 0xE2F to the inviter and 0xE30 to the target.</item>
    /// </list>
    ///
    /// <para><b>One deviation, deliberate.</b> The real handler resolves the target through
    /// <c>UserManager</c>, so an OFFLINE character is "no such user". We resolve through the
    /// characters table instead and address the 0xE30 to their db id: the dispatcher drops it
    /// when they are offline, which is what every other cross-session guild push already does,
    /// and the invite row persists so the client's invite list shows it at next login. Requiring
    /// the target online would also make this untestable without a live roster.</para>
    ///
    /// <para>There is no wanted board (GUILD-DESIGN.md section 10, "What is NOT modelled"), so
    /// <c>fromWantedList</c> is answered with 0xE2E rather than silently treated as a plain
    /// invite — refusing is honest, inviting anyway would claim a board we do not have.</para>
    /// </summary>
    private GuildActions InviteUserToGuild(GuildActions a, int characterId, byte[] body)
    {
        var req = GuildPackets.ParseCInviteUserToGuild(body);
        if (req == null) return a.Reject("C_INVITE_USER_TO_GUILD: short body");
        var (name, userDbId, fromWantedList) = req.Value;

        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject($"C_INVITE_USER_TO_GUILD: caller is in no guild (0x{MsgNoSuchUser:X})");
        if (!_store.HasGuildAuthority(guild.GuildId, characterId, CharacterStore.GuildAuthorityInvite))
            return a.Reject($"C_INVITE_USER_TO_GUILD: no invite authority (0x{MsgNoAuthority:X})");

        var target = userDbId != 0 ? _store.GetCharacter(userDbId) : _store.GetCharacterByName(name);
        if (target == null)
            return a.Reject($"C_INVITE_USER_TO_GUILD: no such user '{name}'/{userDbId} (0x{MsgNoSuchUser:X})");
        int targetId = target.Id;
        if (targetId == characterId)
            return a.Reject("C_INVITE_USER_TO_GUILD: cannot invite yourself");

        if (_store.GetGuildIdOf(targetId) != 0)
        {
            Smt(a, characterId, MsgAlreadyInAGuild, "userName", target.Name);
            return a;
        }
        if (fromWantedList)
            return a.Reject($"C_INVITE_USER_TO_GUILD: no wanted board in this build (0x{MsgNotOnWantedList:X})");

        foreach (var existing in _store.GetGuildInvites(targetId))
            if (existing.GuildId == guild.GuildId)
                return a.Reject("C_INVITE_USER_TO_GUILD: already invited (GuildJoinManager::AlreadyInvitedGuild)");

        _store.AddGuildInvite(guild.GuildId, targetId, characterId);
        Smt(a, characterId, MsgInviteSent, "userName", target.Name);
        Smt(a, targetId, MsgInviteReceived, "guildName", guild.Name);
        _log.LogInformation("Guild {G} '{GN}': {A} invited {B}", guild.GuildId, guild.Name, characterId, target.Name);
        return a;
    }

    /// <summary>
    /// C_CHANGE_GUILDNAME (0xFC1C): one `[u16 off]` + wstring, min total 6. Handler FUN_1404dc6a0
    /// (Arb_part_040.c:19716) has exactly one guard before it hands off, and it is worth quoting
    /// because it is not the invite authority: <c>*(int *)(guild + 0xd8) == *(int *)(user +
    /// 0x120)</c> — Guild+0xD8 is GuildData+0x50, i.e. <c>ChiefDbId</c>, so <b>only the guild
    /// master may rename</b>. A non-master is answered with nothing at all; we relay the reason.
    ///
    /// <para><b>What we skip, and why it is safe.</b> The real handler then runs
    /// <c>InputRestrictionHelper::CheckGuildName</c> (banned words, NetModerator) and an
    /// <c>SDB_ASK_CHANGE_GUILD_NAME</c> round trip whose only job is the uniqueness check the
    /// SQL UNIQUE index already performs. We have neither moderator, so the name goes straight to
    /// <see cref="CharacterStore.RenameGuild"/>, which answers false when it is taken — the same
    /// answer, one hop earlier. The reply is <c>S_CHANGE_GUILDNAME</c>
    /// (<c>string guildName, bool usable</c> — GuildPackets.CorrectedDefs; the shipped .def drops
    /// the flag), and World is told with AS_UPDATE_GUILD_NAME (0x1490), the same
    /// `[u32 stringOff][i32 GuildDbId]` + wstring shape AS_UPDATE_GUILD_TITLE uses.</para>
    /// </summary>
    private GuildActions ChangeGuildName(GuildActions a, int characterId, byte[] body)
    {
        string? raw = GuildPackets.ParseCSingleString(body);
        if (raw == null) return a.Reject("C_CHANGE_GUILDNAME: short body");
        var guild = MyGuild(characterId);
        if (guild == null) return a.Reject($"C_CHANGE_GUILDNAME: caller is in no guild (0x{MsgNoSuchGuild:X})");
        if (guild.ChiefDbId != characterId)
            return a.Reject($"C_CHANGE_GUILDNAME: only the guild master may rename (0x{MsgNotGuildMaster:X})");

        string wanted = Truncate(EscapeGuildText(raw), CharacterStore.MaxGuildName);
        bool ok = wanted.Length > 0 && _store.RenameGuild(guild.GuildId, wanted);

        a.Client(GuildClientAction.Def(characterId, "S_CHANGE_GUILDNAME", new Dictionary<string, object>
        {
            ["guildName"] = wanted,
            ["usable"] = ok,
        }));
        if (!ok) return a;

        a.World(GuildPackets.AS_UPDATE_GUILD_NAME, GuildPackets.BuildAsGuildString(guild.GuildId, wanted));
        _log.LogInformation("Guild {G} renamed '{Old}' -> '{New}'", guild.GuildId, guild.Name, wanted);
        return a;
    }

    /// <summary>
    /// One S_SYSTEM_MESSAGE to one character. The Arbiter builds these as
    /// <c>@id\vKey\vValue</c> pairs (SocialHandlers.Smt is the same builder for the friends
    /// paths); the guild paths that use it are quoted to their message id in the callers.
    /// </summary>
    private static void Smt(GuildActions a, int characterId, int id, params string[] keysAndValues)
        => a.Client(GuildClientAction.Def(characterId, "S_SYSTEM_MESSAGE",
            new Dictionary<string, object> { ["message"] = SocialHandlers.Smt(id, keysAndValues) }));

    /// <summary>C_REJECT_INVITE_USER_TO_GUILD (0xDE54): `[i32 guildDbId]` - spDeleteInviteUserToGuild
    /// then S_REQUEST_INVITE_GUILD_TAG with the caller's remaining invitation count.</summary>
    private GuildActions RejectInvite(GuildActions a, int characterId, byte[] body)
    {
        int? guildId = GuildPackets.ParseCRejectInviteUserToGuild(body);
        if (guildId == null) return a.Reject("C_REJECT_INVITE_USER_TO_GUILD: short body");
        _store.DeleteGuildInvite(guildId.Value, characterId);
        a.Client(GuildClientAction.Def(characterId, "S_REQUEST_INVITE_GUILD_TAG",
            new Dictionary<string, object> { ["count"] = _store.GetGuildInvites(characterId).Count }));
        return a;
    }

    // =======================================================================================
    // Builders - one place each S_ packet's fields are assembled
    // =======================================================================================

    // ===================================================================================
    // T98 - the guild-quest board
    // ===================================================================================

    /// <summary>Header scalars neither capture explains. Both frames carry exactly these, and
    /// they do not move between two different guilds, so they are constants until something
    /// makes one of them move.</summary>
    public const int QuestUnk1 = 1, QuestUnk2 = 0, QuestUnk3 = 100, QuestUnk4 = 0,
                     QuestUnk5 = 2, QuestUnk6 = 2, QuestUnk7 = 0, QuestUnk8 = 0;
    /// <summary>The 900 both frames report, which is also the top tier s point total.</summary>
    public const int QuestMaxPoint = 900;
    /// <summary>The trailing byte, 1 in both frames.</summary>
    public const byte QuestFlag = 1;

    /// <summary>
    /// S_GUILD_QUEST_LIST for one guild. The catalogue is sheet data and identical for every
    /// guild; the only things that move are the header (guild id, money, the member who started
    /// the running quest, the points) and the running quest s <c>remainSec</c>.
    ///
    /// <para>cap_final_client3 280 and client4 2084 differ ONLY in the header, the two names and
    /// that one countdown - 7095 against 6823 seconds, ten minutes of capture apart. That is why
    /// <paramref name="nowUnix"/> is a parameter: the number on the wire is <c>ends_at - now</c>,
    /// not a stored value.</para>
    /// </summary>
    public byte[] BuildGuildQuestList(CharacterStore.GuildRow g, long nowUnix)
    {
        var running = _store.GetRunningGuildQuest(g.GuildId);
        string starterName = string.Empty;
        int starterDbId = 0;
        if (running != null)
        {
            starterDbId = running.StarterDbId;
            starterName = _store.GetGuildMember(running.StarterDbId)?.Name ?? string.Empty;
        }

        var rows = new List<GuildPackets.GuildQuestRow>(GuildPackets.GuildQuestCatalogue.Length);
        foreach (var q in GuildPackets.GuildQuestCatalogue)
        {
            int remain = q.RemainSec;
            if (running != null && running.QuestId == q.QuestId)
            {
                long left = running.EndsAt - nowUnix;
                remain = left < 0 ? 0 : (left > int.MaxValue ? int.MaxValue : (int)left);
            }
            rows.Add(q with { RemainSec = remain });
        }

        return GuildPackets.BuildSGuildQuestListBody(
            g.GuildId, starterDbId, QuestUnk1, (int)g.Point, QuestUnk2, QuestUnk3, QuestUnk4,
            g.Money, QuestUnk5, QuestUnk6, QuestUnk7, nowUnix, QuestUnk8, QuestMaxPoint,
            QuestFlag, g.Name, starterName,
            GuildPackets.GuildQuestTiers, rows);
    }

    /// <summary>
    /// Push the board to one member. The capture sends it at exactly two moments: in the
    /// enter-world burst beside S_GUILD_INFO (cap_final_client3 280, client4 2084) and again
    /// right after the roster changes (client3 3020 and 4130, client4 2751).
    /// </summary>
    private void SendGuildQuestList(GuildActions a, int characterId, CharacterStore.GuildRow g)
        => a.Client(GuildClientAction.Raw(characterId, "S_GUILD_QUEST_LIST",
            BuildGuildQuestList(g, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));

    /// <summary>
    /// T135: the same board to the whole guild, built once. Every quest verb ends here -
    /// classic_live2 pushes S_GUILD_QUEST_LIST after each of its three finishes (12223, and
    /// again at 13330 and 64933), because the board's own row changed.
    /// </summary>
    private void BroadcastGuildQuestList(GuildActions a, CharacterStore.GuildRow g)
    {
        var frame = BuildGuildQuestList(g, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var m in _store.GetGuildMembers(g.GuildId))
            a.Client(GuildClientAction.Raw(m.UserDbId, "S_GUILD_QUEST_LIST", frame));
    }

    // =======================================================================================
    // T135. The three guild-quest verbs. status/GUILD-DESIGN.md section 12.
    //
    // WHAT IS CAPTURED AND WHAT IS NOT. classic_live2 and classic_live3 together hold three
    // complete FINISH exchanges and nothing else: no client in either capture ever sent
    // C_REQUEST_START_GUILD_QUEST or C_REQUEST_CANCEL_GUILD_QUEST. So finish is byte-for-byte
    // the live server's, down to the reward, and the other two are their .def plus the one
    // rule every verb shares - the board is re-pushed afterwards. Each is marked below.
    // =======================================================================================

    /// <summary>The guild exp one finished quest pays. classic_live2's three finishes moved the
    /// guild exp 157620 -&gt; 157640 -&gt; 157660, so 20 each.</summary>
    public const long GuildQuestExpReward = 20;
    /// <summary>The funds one finished quest pays: 711 -&gt; 712 -&gt; 713 across the same three.</summary>
    public const long GuildQuestMoneyReward = 1;
    /// <summary>guild_quests.status - the two T98 defined.</summary>
    public const int GuildQuestAvailable = 0, GuildQuestRunning = 1;

    /// <summary>All three verbs carry the same body: a bare i32 quest id (frame 12185 is
    /// <c>11 27 00 00</c>, quest 10001). Null when the body is short.</summary>
    public static int? ParseQuestId(byte[] body)
        => body == null || body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    private static GuildPackets.GuildQuestRow? Catalogue(int questId)
    {
        foreach (var q in GuildPackets.GuildQuestCatalogue) if (q.QuestId == questId) return q;
        return null;
    }

    /// <summary>
    /// C_REQUEST_START_GUILD_QUEST (0x77A9). <b>Not captured</b> - the .def is one i32 and the
    /// reply S_START_GUILD_QUEST IS captured (frame 12254), so the frame we send is real even
    /// though the request that triggers it never was.
    ///
    /// <para>One quest runs at a time: T98's table has a single row at status 1 and
    /// <c>GetRunningGuildQuest</c> is what the board reads. The countdown is the catalogue
    /// row's own <c>RemainSec</c> (43200 = the 720 minutes its totalTime field carries).</para>
    /// </summary>
    private GuildActions StartGuildQuest(GuildActions a, int characterId, byte[] body)
    {
        if (ParseQuestId(body) is not int questId) return a.Reject("C_REQUEST_START_GUILD_QUEST: short body");
        var g = MyGuild(characterId);
        if (g == null) return a.Reject("C_REQUEST_START_GUILD_QUEST: not in a guild");
        var row = Catalogue(questId);
        if (row == null) return a.Reject($"C_REQUEST_START_GUILD_QUEST: {questId} is not on the board");

        var running = _store.GetRunningGuildQuest(g.GuildId);
        if (running != null)
            return a.Reject($"C_REQUEST_START_GUILD_QUEST: quest {running.QuestId} is already running");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _store.SetGuildQuest(g.GuildId, questId, GuildQuestRunning, now, now + row.RemainSec,
            characterId, progress: 0);

        // Frame 12254's string is the GUILD's name - all three of its S_START_GUILD_QUEST
        // frames carry "Candlelight" while three different members were involved.
        var fields = new Dictionary<string, object>
        {
            ["result"] = (byte)1, ["questId"] = questId, ["guildName"] = g.Name,
        };
        foreach (var m in _store.GetGuildMembers(g.GuildId))
            a.Client(GuildClientAction.Def(m.UserDbId, "S_START_GUILD_QUEST", fields));
        BroadcastGuildQuestList(a, g);
        return a;
    }

    /// <summary>
    /// C_REQUEST_FINISH_GUILD_QUEST (0xA332). <b>Fully captured</b>, three times: 12185 -&gt;
    /// 12218 / 12220 / 12221 / 12222 / 12223, and the same shape at 13322 and 64897.
    ///
    /// <para>The order below is the capture's: the reply to the requester first, then the three
    /// economy pushes, then the board. Point is pushed but never changes - 14 through all three
    /// finishes - so it is a refresh and not a reward, and <c>AddGuildQuestReward</c> leaves it
    /// alone.</para>
    /// </summary>
    private GuildActions FinishGuildQuest(GuildActions a, int characterId, byte[] body)
    {
        if (ParseQuestId(body) is not int questId) return a.Reject("C_REQUEST_FINISH_GUILD_QUEST: short body");
        var g = MyGuild(characterId);
        if (g == null) return a.Reject("C_REQUEST_FINISH_GUILD_QUEST: not in a guild");

        var running = _store.GetRunningGuildQuest(g.GuildId);
        if (running == null || running.QuestId != questId)
            return a.Reject($"C_REQUEST_FINISH_GUILD_QUEST: {questId} is not the running quest");

        _store.SetGuildQuest(g.GuildId, questId, GuildQuestAvailable, 0, 0, 0, progress: 0);
        _store.AddGuildQuestReward(g.GuildId, GuildQuestExpReward, GuildQuestMoneyReward);
        var after = _store.GetGuild(g.GuildId) ?? g;

        a.Client(GuildClientAction.Def(characterId, "S_FINISH_GUILD_QUEST",
            new Dictionary<string, object> { ["result"] = (byte)1, ["questId"] = questId }));

        var level = new Dictionary<string, object>
        {
            ["guildDbId"] = after.GuildId, ["newLevel"] = after.Level,
            ["newExp"] = after.Exp, ["isLevelUp"] = (byte)0,
        };
        var point = new Dictionary<string, object>
        {
            ["guildDbId"] = after.GuildId, ["newPoint"] = after.Point,
        };
        var money = new Dictionary<string, object>
        {
            ["guildDbId"] = after.GuildId, ["newMoney"] = after.Money,
        };
        foreach (var m in _store.GetGuildMembers(after.GuildId))
        {
            a.Client(GuildClientAction.Def(m.UserDbId, "S_GUILD_LEVEL_INFO_CHANGED", level));
            a.Client(GuildClientAction.Def(m.UserDbId, "S_GUILD_POINT_INFO_CHANGED", point));
            a.Client(GuildClientAction.Def(m.UserDbId, "S_GUILD_MONEY_INFO_CHANGED", money));
        }
        BroadcastGuildQuestList(a, after);
        return a;
    }

    /// <summary>
    /// C_REQUEST_CANCEL_GUILD_QUEST (0xA6D1). <b>Not captured, and it has no reply of its
    /// own</b>: the 376012 map has no S_CANCEL_GUILD_QUEST, only S_FAIL_GUILD_QUEST, which is
    /// a failure notice rather than an acknowledgement. So the board refresh IS the answer.
    ///
    /// <para>Who may cancel is not in any capture either. Giving it to every member would let
    /// one of them throw away the guild's twelve-hour timer, so it is the starter or the chief
    /// - the narrowest rule that still lets the guild undo a mistake. If a capture ever shows
    /// otherwise, this is the line to change.</para>
    /// </summary>
    private GuildActions CancelGuildQuest(GuildActions a, int characterId, byte[] body)
    {
        if (ParseQuestId(body) is not int questId) return a.Reject("C_REQUEST_CANCEL_GUILD_QUEST: short body");
        var g = MyGuild(characterId);
        if (g == null) return a.Reject("C_REQUEST_CANCEL_GUILD_QUEST: not in a guild");

        var running = _store.GetRunningGuildQuest(g.GuildId);
        if (running == null || running.QuestId != questId)
            return a.Reject($"C_REQUEST_CANCEL_GUILD_QUEST: {questId} is not the running quest");
        if (running.StarterDbId != characterId && g.ChiefDbId != characterId)
            return a.Reject("C_REQUEST_CANCEL_GUILD_QUEST: only the starter or the chief may cancel");

        _store.SetGuildQuest(g.GuildId, questId, GuildQuestAvailable, 0, 0, 0, progress: 0);
        BroadcastGuildQuestList(a, g);
        return a;
    }

    private void SendGuildInfo(GuildActions a, int characterId, CharacterStore.GuildRow g)
    {
        var members = _store.GetGuildMembers(g.GuildId);
        var groups = _store.GetGuildGroups(g.GuildId);
        var chief = _store.GetGuildMember(g.ChiefDbId);
        var mine = _store.GetGuildMember(characterId);
        var myGroup = mine == null ? null : Find(groups, mine.GuildGroupId);

        var ranks = new List<Dictionary<string, object>>(groups.Count);
        foreach (var grp in groups)
            ranks.Add(new Dictionary<string, object>
            {
                ["groupId"] = grp.GuildGroupId,
                ["groupAuthority"] = grp.Authority,
                ["groupName"] = grp.Name,
            });

        a.Client(GuildClientAction.Def(characterId, "S_GUILD_INFO", new Dictionary<string, object>
        {
            ["guildDbId"] = g.GuildId,
            ["guildInfo"] = 0L,
            ["chiefDbId"] = g.ChiefDbId,
            ["guildCreateDate"] = g.CreateDate,
            ["guildLevel"] = g.Level,
            ["guildExp"] = g.Exp,
            ["guildNextExp"] = 0L,
            ["guildMoney"] = g.Money,
            ["recommendationPoint"] = g.RecommendationPoint,
            ["policyPoint"] = 0,
            ["needChangeGuildName"] = false,
            ["battleChip"] = 0,
            ["guildSize"] = GuildSize(g.Level),
            ["playingMemberCount"] = 0,
            ["memberCount"] = members.Count,
            ["accountCount"] = CountAccounts(members),
            ["maxAccountCount"] = MaxAccounts(g),
            ["accountCountCanGuildWar"] = 0,
            ["isGuildWarAcceptable"] = g.WarAcceptable,
            ["guildWarAcceptableToggleTime"] = g.WarToggleTime,
            ["lordBehaviorRank"] = 0,
            ["lordBehaviorPoints"] = 0,
            ["isHaveFloatingCastle"] = false,
            ["floatingCastleCoinAmount"] = 0,
            ["guildPreference"] = g.Preference,
            ["joinMinLevel"] = g.JoinMinLevel,
            ["joinMaxLevel"] = g.JoinMaxLevel,
            ["joinType"] = g.JoinType,
            ["guildWindowType"] = mine != null ? WindowInfoForMember : WindowInfoForOutsider,
            ["isOccupation"] = false,
            ["guildName"] = g.Name,
            ["chiefName"] = chief?.Name ?? "",
            ["guildAnnounce"] = g.Announce,
            ["guildGroupName"] = myGroup?.Name ?? "",
            ["guildPromotion"] = g.Promotion,
            ["guildLogoId"] = g.LogoId.ToString(CultureInfo.InvariantCulture),
            ["guildGroups"] = ranks,
        }));

        // T98b: the board follows S_GUILD_INFO wherever it goes out - cap_final_client3 3017
        // then 3020, and the enter-world burst at client3 280 / client4 2084. MEMBERS only:
        // window type 3 shows an outsider the guild card, and the capture never sends the
        // board to anyone who is not in the guild.
        if (mine != null) SendGuildQuestList(a, characterId, g);
    }

    private void SendMemberList(GuildActions a, int characterId, CharacterStore.GuildRow g)
    {
        var members = _store.GetGuildMembers(g.GuildId);
        var chief = _store.GetGuildMember(g.ChiefDbId);

        var rows = new List<Dictionary<string, object>>(members.Count);
        foreach (var m in members)
            rows.Add(new Dictionary<string, object>
            {
                ["userDbId"] = m.UserDbId,
                ["memberType"] = 0,
                ["worldId"] = m.WorldId,
                ["guardId"] = m.GuardId,
                ["sectionId"] = m.SectionId,
                ["groupId"] = m.GuildGroupId,
                ["userLevel"] = m.UserLevel,
                ["race"] = m.Race,
                ["userClass"] = m.UserClass,
                ["gender"] = m.Gender,
                // The load loop forces State to 2 (offline); the routing layer flips the ones
                // it has a live session for to 0.
                ["state"] = MemberStateOffline,
                ["weeklyContributionPoint"] = m.WeeklyContribution,
                ["totalContributionPoint"] = m.TotalContribution,
                ["lastLogoutTime"] = m.LastLogoutTime,
                ["cityWarCompensationStatus"] = false,
                ["userName"] = m.Name,
                ["userAnnounce"] = m.Introduce,
            });

        a.Client(GuildClientAction.Def(characterId, "S_GUILD_MEMBER_LIST", new Dictionary<string, object>
        {
            ["guildDbId"] = g.GuildId,
            ["chiefDbId"] = g.ChiefDbId,
            ["guildLevel"] = g.Level,
            ["guildExp"] = g.Exp,
            ["guildNextExp"] = 0L,
            ["guildMoney"] = g.Money,
            ["memberCount"] = members.Count,
            ["accountCount"] = CountAccounts(members),
            ["guildSize"] = GuildSize(g.Level),
            ["guildCreateDate"] = g.CreateDate,
            ["ended"] = true,
            ["clearCache"] = true,
            ["showGuildWindow"] = true,
            ["guildName"] = g.Name,
            ["guildMaster"] = chief?.Name ?? "",
            ["members"] = rows,
        }));
    }

    /// <summary>GuildMemberData+0x70. The load loop forces 2; 0 is online.</summary>
    public const int MemberStateOnline = 0, MemberStateOffline = 2;

    private GuildActions SendHistory(GuildActions a, int characterId, int guildId, int page)
    {
        // T48: clamp before the store multiplies it into a SQL OFFSET.
        page = ClampPage(page, _store.CountGuildLogPages(guildId));
        var rows = _store.GetGuildLog(guildId, page);
        var events = new List<Dictionary<string, object>>(rows.Count);
        foreach (var r in rows)
            events.Add(new Dictionary<string, object>
            {
                ["date"] = r.LogTime,
                ["event"] = r.ActionType,
                ["initiator"] = r.ActorName,
                ["description"] = r.Detail.Length > 0 ? r.Detail : r.TargetName,
            });

        a.Client(GuildClientAction.Def(characterId, "S_GUILD_HISTORY", new Dictionary<string, object>
        {
            ["page"] = page,
            ["pages"] = _store.CountGuildLogPages(guildId),
            ["events"] = events,
        }));
        return a;
    }

    /// <summary>
    /// A page number from a packet, clamped to 1..<paramref name="totalPages"/>. T48.
    ///
    /// <para>Every paginated handler must go through this. The rule from
    /// status/ARBITER-SECURITY-NOTES.md bug #2 is that a page index is validated against BOTH
    /// bounds - against the collection's real size, not just against being negative - and the
    /// clamp has to happen <b>before</b> any <c>page * pageSize</c>, because that multiply
    /// overflows in C# silently and turns a positive page into a negative offset.</para>
    /// </summary>
    public static int ClampPage(int page, int totalPages)
    {
        if (totalPages < 1) totalPages = 1;
        if (page < 1) return 1;
        return page > totalPages ? totalPages : page;
    }

    private GuildActions SendApplyListFor(GuildActions a, int characterId, CharacterStore.GuildRow g, int page)
    {
        var all = _store.GetGuildApplies(g.GuildId);
        int pageSize = GuildPackets.ApplyListPageSize;
        int totalPages = all.Count == 0 ? 1 : (all.Count + pageSize - 1) / pageSize;

        // T48. `page` is an i32 straight off the wire and the old code was
        //     for (int i = (page - 1) * pageSize; i < all.Count && i < page * pageSize; i++)
        // with only `if (page < 1) page = 1;` in front of it - the one-sided guard that
        // status/ARBITER-SECURITY-NOTES.md bug #2 describes, plus an overflow that defeats it.
        // page = 165191051 makes (page-1)*13 wrap to -2147483646, which is BOTH less than
        // all.Count and less than page*13 (also wrapped), so the loop runs and indexes all[-2^31].
        // Clamping against totalPages first makes the multiply unreachable for any hostile value.
        page = ClampPage(page, totalPages);

        var apps = new List<Dictionary<string, object>>();
        for (int i = (page - 1) * pageSize; i < all.Count && i < page * pageSize; i++)
        {
            var row = all[i];
            var who = _store.GetCharacter(row.UserDbId);
            apps.Add(new Dictionary<string, object>
            {
                ["userDbId"] = row.UserDbId,
                ["classType"] = who?.Class ?? 0,
                ["userLevel"] = who?.Level ?? 0,
                ["dateTime"] = row.AppliedAt,
                ["userName"] = who?.Name ?? "",
                ["joinMsg"] = row.JoinMsg,
            });
        }

        a.Client(GuildClientAction.Def(characterId, "S_GUILD_APPLY_LIST", new Dictionary<string, object>
        {
            ["inviteAuthority"] = _store.HasGuildAuthority(g.GuildId, characterId, CharacterStore.GuildAuthorityInvite),
            ["curPageNum"] = page,
            ["totalPageCount"] = totalPages,
            ["guildApplyList"] = apps,
        }));
        return a;
    }

    /// <summary>S_GUILD_APPLY_COUNT to every member who can act on an application. The real
    /// Arbiter calls GuildJoinManager::BroadcastGuildApplyCount, which walks Guild+0x68 - the
    /// LOGGED-IN set - so the routing layer drops whoever is offline.</summary>
    private void BroadcastApplyCount(GuildActions a, CharacterStore.GuildRow g)
    {
        int count = _store.GetGuildApplies(g.GuildId).Count;
        var fields = new Dictionary<string, object> { ["count"] = count };
        foreach (var m in _store.GetGuildMembers(g.GuildId))
            if (_store.HasGuildAuthority(g.GuildId, m.UserDbId, CharacterStore.GuildAuthorityInvite))
                a.Client(GuildClientAction.Def(m.UserDbId, "S_GUILD_APPLY_COUNT", fields));
    }

    /// <summary>
    /// Everything a join produces once the row is in: S_ADD_GUILD_MEMBER to the guild, then the
    /// two World frames GuildUtil::UserJoinToGuild sends - AS_ADD_GUILDMEMBER broadcast to every
    /// world, AS_GUILD_JOINED to the joiner's own world session only.
    /// </summary>
    /// <summary>T69: the same fan-out, for the founding members SDB_CREATE_GUILD2 names. The
    /// create path is on the DB-proxy link, not a client packet, so it has no other way in.</summary>
    internal void EmitMemberAddedForWorld(GuildActions a, int guildId, int userDbId)
        => EmitMemberAdded(a, guildId, userDbId);

    private void EmitMemberAdded(GuildActions a, int guildId, int userDbId)
    {
        var m = _store.GetGuildMember(userDbId);
        if (m == null) return;

        var fields = new Dictionary<string, object>
        {
            ["memberDbId"] = m.UserDbId,
            ["name"] = m.Name,
            ["worldId"] = m.WorldId,
            ["guardId"] = m.GuardId,
            ["sectionId"] = m.SectionId,
            ["groupId"] = m.GuildGroupId,
            ["userLevel"] = m.UserLevel,
            ["race"] = m.Race,
            ["userClass"] = m.UserClass,
            ["state"] = MemberStateOnline,
            ["gender"] = m.Gender,
            ["lastLogoutTime"] = m.LastLogoutTime,
            ["isWorldEventTarget"] = false,
            ["cityWarCompensationStatus"] = false,
        };
        foreach (var other in _store.GetGuildMembers(guildId))
            a.Client(GuildClientAction.Def(other.UserDbId, "S_ADD_GUILD_MEMBER", fields));

        a.World(GuildPackets.AS_ADD_GUILDMEMBER, GuildPackets.BuildAsAddGuildMember(
            guildId, m.UserDbId, m.Name, m.WorldId, m.GuardId, m.SectionId, m.UserLevel,
            m.Race, m.UserClass, m.Gender, MemberStateOnline, m.GuildGroupId,
            m.LastLogoutTime, false, m.AccountId, m.GuildJoinDate));
        a.World(GuildPackets.AS_GUILD_JOINED, GuildPackets.BuildAsGuildJoined(m.UserDbId));

        // T98b: last, because that is the order on the wire - cap_final_client3 sends
        // S_ADD_GUILD_MEMBER at 3014 and only reaches the board at 3020. Client4 2751 is the
        // same shape. The joiner alone gets it; the other members keep the board they have.
        var guild = _store.GetGuild(guildId);
        if (guild != null) SendGuildQuestList(a, userDbId, guild);
    }

    // =======================================================================================
    // Helpers
    // =======================================================================================

    private static readonly Dictionary<string, object> Empty = new();

    private CharacterStore.GuildRow? MyGuild(int characterId)
    {
        int id = _store.GetGuildIdOf(characterId);
        return id == 0 ? null : _store.GetGuild(id);
    }

    private static CharacterStore.GuildGroupRow? Find(List<CharacterStore.GuildGroupRow> groups, int id)
    {
        foreach (var g in groups) if (g.GuildGroupId == id) return g;
        return null;
    }

    private static string Truncate(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>
    /// S_GUILD_INFO/S_GUILD_MEMBER_LIST's GuildSize: 0 small, 1 medium, 2 large. The real
    /// Arbiter derives it from the guild level through FUN_140067ca0, a datasheet lookup we do
    /// not have; these thresholds are ours and are the one made-up number in this file.
    /// </summary>
    public static int GuildSize(int guildLevel) => guildLevel >= 5 ? 2 : guildLevel >= 3 ? 1 : 0;

    /// <summary>Distinct accounts among the members - the real Arbiter counts them live rather
    /// than storing a column (GUILD-DESIGN.md section 2.1).</summary>
    private static int CountAccounts(List<CharacterStore.GuildMemberRow> members)
    {
        var seen = new HashSet<long>();
        foreach (var m in members) seen.Add(m.AccountId);
        return seen.Count;
    }

    /// <summary>
    /// MaxAccountCount = GuildConfigDataSheet+0x20 + GuildData+0x238C. We have never seen the
    /// datasheet, so the base is a constant here; the per-guild bonus is real.
    /// </summary>
    public static int BaseMaxAccounts { get; set; } = 30;
    private static int MaxAccounts(CharacterStore.GuildRow g) => BaseMaxAccounts + g.AddAccountLimit;

    // =======================================================================================
    // Def resolution - what makes the .def corrections actually take effect
    // =======================================================================================

    private static readonly Dictionary<string, PacketDef> _overrides = new();
    private static readonly object _overrideLock = new();

    /// <summary>
    /// The def to encode <paramref name="name"/> with: GuildPackets.CorrectedDefs first (the ten
    /// files whose LAYOUT is wrong), then GuildPackets.NamedDefs (two whose layout is right but
    /// whose field names are `unk1`..`unk20`), then the shipped registry. Without this the
    /// handlers above would emit S_ADD_GUILD_MEMBER five bytes short.
    /// </summary>
    public static PacketDef? ResolveDef(DefinitionRegistry? defs, string name)
    {
        lock (_overrideLock)
        {
            if (_overrides.TryGetValue(name, out var cached)) return cached;
            string? text = null;
            if (GuildPackets.CorrectedDefs.TryGetValue(name, out var corrected)) text = corrected;
            else if (GuildPackets.NamedDefs.TryGetValue(name, out var named)) text = named;
            if (text != null)
            {
                var parsed = DefinitionParser.ParseText(name, text);
                _overrides[name] = parsed;
                return parsed;
            }
        }
        return defs?.Get(name);
    }
}
