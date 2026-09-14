using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// ChatManager - whisper and private (user-made) chat channels, the Arbiter-owned half (T43).
// Research and every decompile reference: status/CHAT-DESIGN.md.
//
// Private channels are PARTIES, not guilds: pure Arbiter RAM, gone on restart. That is not an
// assumption - CHAT-DESIGN.md section 3 proves it four ways, including a 167-function walk of
// the channel subsystem's call graph that reaches no SQL bind and no stored procedure, and the
// fact that ChatManager::StartManager does nothing but arm a 1-second timer where a persisted
// collection would be rehydrated. So this class has a store only to look a whisper target up by
// name and to read the block list; channels themselves never touch it.
//
// NOTHING IS WIRED UP. Like PartyManager and GuildHandlers, this answers an input with actions:
//
//   OnClientPacket(characterId, opcode, body) -> ArbiterActions
//
// and World/ActionDispatcher.cs (T41) performs the sends. status/CHAT-DESIGN.md section 7 has
// the registration diff, including the swap for the existing ChatHandlers.OnWhisper.
//
// THE TRAP, and the reason the brief for this task had it wrong: the client does NOT address a
// private channel by its id. C_CHAT's Type field 0x0B..0x12 and C_LEAVE_PRIVATE_CHANNEL's Index
// are the user's own SLOT 0..7 in an eight-entry array (User+0x56F8); the real channel id is
// looked up server-side. S_PRIVATE_CHAT then carries the real id. Getting this backwards yields
// a server that works for one channel and silently crosses wires for two.
// =============================================================================================

/// <summary>
/// A private chat channel. Mirrors PrivateChatChannel (0xB8 bytes) field for field, minus the
/// lock and the refcount: id (+0xB0), master (+0x78), name (+0x80, wchar[9]), password (+0x92,
/// a short), member list (+0xA0) and count (+0xA8), and the empty-since stamp (+0x98).
/// </summary>
public sealed class PrivateChannel
{
    public int Id { get; init; }
    /// <summary>wchar_t[9] in the real object, so eight characters plus the NUL.</summary>
    public string Name { get; set; } = "";
    /// <summary>A short, and ChatManager::CreatePrivateChannel rejects anything outside
    /// 1000..9999 with system message 0x3C2.</summary>
    public ushort Password { get; set; }
    /// <summary>PrivateChatChannel+0x78 is a User*, i.e. the master must be online. We keep the
    /// db id; <see cref="ChatManager"/> decides what an offline master means.</summary>
    public int MasterDbId { get; set; }
    /// <summary>Insertion order, which is the order the real std::list walk delivers in.</summary>
    public List<int> Members { get; } = new();
    /// <summary>PrivateChatChannel+0x98: unix ms when the last member left, 0 while occupied.
    /// ChatManager::CheckEmptyChannel erases the channel 600000 ms later.</summary>
    public long EmptySinceMs { get; set; }

    public bool IsMaster(int userDbId) => MasterDbId == userDbId;
    public bool Has(int userDbId) => Members.Contains(userDbId);
    public int Count => Members.Count;
}

/// <summary>A player as the chat layer needs them. Registered on entering the world, dropped on
/// leaving - "registered" is this manager's definition of online, which is what
/// UserManager::GetCachedUserWithLock means in the real whisper path.</summary>
public readonly record struct ChatPlayer(
    int UserDbId, string Name, ulong GameId, int Level, int Class, bool IsAdmin);

public sealed class ChatManager
{
    private readonly CharacterStore _store;
    private readonly ILogger _log;

    /// <summary>Channels by id, and the id counter. Both are process-local: the real one is a
    /// plain field of ChatManager initialised to 0, so the first id issued is 1.</summary>
    private readonly Dictionary<int, PrivateChannel> _channels = new();
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private int _nextChannelId;

    /// <summary>Registered (online) players by character db id.</summary>
    private readonly Dictionary<int, ChatPlayer> _players = new();

    /// <summary>User+0x56F8: eight channel-id slots per character, -1 = free. The client
    /// addresses a channel by its SLOT, never by the id.</summary>
    private readonly Dictionary<int, int[]> _slots = new();

    public ChatManager(CharacterStore store, ILogger log)
    {
        _store = store;
        _log = log;
    }

    // ---- limits, all from the decompile ----

    /// <summary>User::CanJoinPrivateChannel scans exactly eight slots at User+0x56F8.</summary>
    public const int MaxChannelsPerUser = 8;
    /// <summary>DAT_140e315a8. Its only writer is the QA command
    /// ArbiterQACommandHandler::SetMaxPrivateChatChannelMemberCount, clamped to 1..99; the
    /// compiled-in default lives in .data and is not visible in the decompile, so this value is
    /// ours. Over it, join answers system message 0x3C9.</summary>
    public static int MaxMembersPerChannel { get; set; } = 24;
    /// <summary>`if (8999 &lt; (ushort)(password - 1000U))` - a four-digit number, nothing else.</summary>
    public const ushort MinPassword = 1000, MaxPassword = 9999;
    /// <summary>wcsncpy_s(channel+0x80, 9, name, -1) - eight characters plus the NUL.</summary>
    public const int MaxChannelNameChars = 8;
    /// <summary>ChatManager::CheckEmptyChannel erases a channel this long after it empties.</summary>
    public const long EmptyChannelTimeoutMs = 600000;

    // ---- ChatType, the C_CHAT `Type` field ----

    /// <summary>ChatType 0x0B..0x12 is a private channel, and the value minus 0x0B is the
    /// sender's own SLOT index - not a channel id. ChatManager::ChatMessageHandler:
    /// `iVar6 = User::GetPrivateChannelId(user, type - 0xb);`</summary>
    public const int ChatTypePrivateFirst = 0x0B;
    public const int ChatTypePrivateLast = 0x12;
    /// <summary>CheckChannelLimitLevel rejects `0xDC &lt;= type`, so the space is 0..0xDB.</summary>
    public const int ChatTypeMax = 0xDB;

    public static bool IsPrivateChatType(int type)
        => type >= ChatTypePrivateFirst && type <= ChatTypePrivateLast;
    public static int SlotOfChatType(int type) => type - ChatTypePrivateFirst;

    // ---- system message ids, every one quoted to its line in CHAT-DESIGN.md section 6 ----
    public const int MsgWhisperSelf = 0x6F;            // 111
    public const int MsgWhisperNoSession = 0x71;       // 113
    public const int MsgKickSelf = 0x116;              // 278
    public const int MsgWhisperNoSuchUser = 0x33F;     // 831
    public const int MsgChannelNameTaken = 0x3C0;      // 960
    public const int MsgBadPassword = 0x3C2;           // 962, outside 1000..9999
    public const int MsgNoSuchChannel = 0x3C3;         // 963, join / leave / password
    public const int MsgNoSuchChannelChat = 0x3C4;     // 964, chat
    public const int MsgNotAMember = 0x3C5;            // 965, kick target
    public const int MsgWrongPassword = 0x3C6;         // 966
    public const int MsgTooManyChannels = 0x3C7;       // 967, already in eight
    public const int MsgNotChannelMaster = 0x3C8;      // 968
    public const int MsgChannelFull = 0x3C9;           // 969
    public const int MsgJoined = 0x3CA;                // 970, _ChannelName
    public const int MsgKicked = 0x3CB;                // 971, _ChannelName
    public const int MsgLeft = 0x3CC;                  // 972, _ChannelName
    public const int MsgSlotNotJoined = 0x3D1;         // 977
    public const int MsgPasswordChanged = 0x3D3;       // 979, _ChannelName
    public const int MsgBlockedByTarget = 0x53A;       // 1338, UserName
    public const int MsgBlockingTarget = 0x5B7;        // 1463, UserName

    /// <summary>S_PRIVATE_CHANNEL_NOTICE.SysMsgId - the four channel events.</summary>
    public const int NoticeCreated = 0xDFE, NoticeJoined = 0xDFF, NoticeLeft = 0xE00, NoticeNewMaster = 0xE01;

    // =======================================================================================
    // Registration - "online" for this manager
    // =======================================================================================

    public void Register(in ChatPlayer p)
    {
        _players[p.UserDbId] = p;
        if (!_slots.ContainsKey(p.UserDbId)) _slots[p.UserDbId] = NewSlots();
    }

    /// <summary>
    /// AccountManager::DeleteUser calls User::LeaveAllPrivateChannel unconditionally, so logging
    /// out drops every membership - which is the other half of "channels are RAM only".
    /// </summary>
    public ArbiterActions Unregister(int userDbId)
    {
        var a = new ArbiterActions { Origin = Recipient.Player(userDbId) };
        _players.Remove(userDbId);
        if (_slots.TryGetValue(userDbId, out var slots))
        {
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != -1) LeaveChannel(a, userDbId, i, announce: true);
            _slots.Remove(userDbId);
        }
        return a;
    }

    public bool IsOnline(int userDbId) => _players.ContainsKey(userDbId);
    public PrivateChannel? Channel(int id) => _channels.TryGetValue(id, out var c) ? c : null;
    public PrivateChannel? ChannelByName(string name)
        => name != null && _byName.TryGetValue(name, out int id) ? Channel(id) : null;
    public IReadOnlyDictionary<int, PrivateChannel> Channels => _channels;

    /// <summary>The eight slots for a character, creating the row on first use.</summary>
    public int[] SlotsOf(int userDbId)
    {
        if (!_slots.TryGetValue(userDbId, out var s)) _slots[userDbId] = s = NewSlots();
        return s;
    }

    private static int[] NewSlots()
    {
        var s = new int[MaxChannelsPerUser];
        for (int i = 0; i < s.Length; i++) s[i] = -1;
        return s;
    }

    // =======================================================================================
    // Entry point
    // =======================================================================================

    public ArbiterActions OnClientPacket(int characterId, ushort opcode, byte[] body)
    {
        var a = new ArbiterActions { Origin = Recipient.Player(characterId) };
        switch (opcode)
        {
            case ChatPackets.C_WHISPER: return Whisper(a, characterId, body);
            case ChatPackets.C_CHAT: return Chat(a, characterId, body);
            case ChatPackets.C_CREATE_PRIVATE_CHANNEL: return CreateChannel(a, characterId, body);
            case ChatPackets.C_JOIN_PRIVATE_CHANNEL: return JoinChannel(a, characterId, body);
            case ChatPackets.C_LEAVE_PRIVATE_CHANNEL: return LeaveChannelPacket(a, characterId, body);
            case ChatPackets.C_KICK_CHANNEL_MEMBER: return KickMember(a, characterId, body);
            case ChatPackets.C_CHANGE_CHANNEL_PASSWORD: return ChangePassword(a, characterId, body);
            case ChatPackets.C_REQUEST_PRIVATE_CHANNEL_INFO: return ChannelInfo(a, characterId, body);
            default:
                return a.Reject($"0x{opcode:X4} is not an Arbiter-side chat packet");
        }
    }

    // =======================================================================================
    // Whisper
    // =======================================================================================

    /// <summary>
    /// C_WHISPER (0xE8E5): `[u16 toOff][u16 talkOff]`, min total 8. Handler_C_WHISPER resolves
    /// the target BY NAME through UserManager::GetCachedUserWithLock - the online map - and then
    /// ChatManager::ProcessWhisperMessage runs the block and self checks. On success it sends
    /// S_WHISPER TWICE: once to the receiver and once back to the sender, both carrying the
    /// SENDER as FromName/FromGameId and the receiver as To.
    /// </summary>
    private ArbiterActions Whisper(ArbiterActions a, int senderId, byte[] body)
    {
        var req = ChatPackets.ParseCWhisper(body);
        if (req == null) return a.Reject("C_WHISPER: short body");
        var (to, talk) = req.Value;

        if (!_players.TryGetValue(senderId, out var sender))
            return a.Reject($"character {senderId} is not registered with the chat manager");

        var target = _store.GetCharacterByName(to);
        if (target == null)
            return Sysmsg(a, senderId, MsgWhisperNoSuchUser).Reject($"C_WHISPER: no character '{to}'");
        if (target.Id == senderId)
            return Sysmsg(a, senderId, MsgWhisperSelf).Reject("C_WHISPER: cannot whisper yourself");
        if (!_players.TryGetValue(target.Id, out var receiver))
            return Sysmsg(a, senderId, MsgWhisperNoSuchUser).Reject($"C_WHISPER: '{to}' is offline");

        // User::IsBlockedUser, both directions, each with its own message and a UserName param.
        if (_store.GetBlocks(target.Id).Contains(senderId))
            return Sysmsg(a, senderId, MsgBlockedByTarget, "UserName", target.Name)
                .Reject($"C_WHISPER: {target.Name} has blocked the sender");
        if (_store.GetBlocks(senderId).Contains(target.Id))
            return Sysmsg(a, senderId, MsgBlockingTarget, "UserName", target.Name)
                .Reject($"C_WHISPER: the sender has blocked {target.Name}");

        var fields = ChatPackets.WhisperFields(sender, receiver.Name, talk);
        a.ToPlayer(receiver.UserDbId, "S_WHISPER", fields);   // to the receiver
        a.ToPlayer(senderId, "S_WHISPER", fields);            // and echoed to the sender
        return a;
    }

    // =======================================================================================
    // C_CHAT - only the private-channel types; everything else belongs elsewhere
    // =======================================================================================

    /// <summary>
    /// C_CHAT (0xEB77): `[u16 talkOff][i32 type]`, min total 0x0A. This answers ONLY types
    /// 0x0B..0x12. ChatManager::ChatMessageHandler routes 0/9/0xA/0xD4 to World as
    /// AS_REQUEST_NORMAL_CHAT, 5/0x16 as AS_REQUEST_TEAM_CHAT, 2 to the party, 3/0x17 to the
    /// guild, and 4/0x1B/0xD5 to the server-wide S_CHAT - none of which is this class's job yet.
    /// </summary>
    private ArbiterActions Chat(ArbiterActions a, int senderId, byte[] body)
    {
        var req = ChatPackets.ParseCChat(body);
        if (req == null) return a.Reject("C_CHAT: short body");
        var (talk, type) = req.Value;

        if (type > ChatTypeMax) return a.Reject($"C_CHAT: type {type} is above the 0xDB ceiling");
        if (!IsPrivateChatType(type))
            return a.Reject($"C_CHAT: type {type} is not a private channel - route it elsewhere");

        if (!_players.TryGetValue(senderId, out var sender))
            return a.Reject($"character {senderId} is not registered with the chat manager");

        int slot = SlotOfChatType(type);
        int channelId = SlotsOf(senderId)[slot];
        if (channelId == -1)
            return Sysmsg(a, senderId, MsgSlotNotJoined).Reject($"C_CHAT: slot {slot} is not joined");

        var channel = Channel(channelId);
        if (channel == null)
            return Sysmsg(a, senderId, MsgNoSuchChannelChat).Reject($"C_CHAT: channel {channelId} is gone");

        // PrivateChatChannel::BroadcastChatMessage walks channel+0xA0 and rebuilds the packet per
        // recipient. Note the packet carries the REAL id, while the client sent a slot index.
        var fields = ChatPackets.PrivateChatFields(channel.Id, sender, talk);
        foreach (int member in channel.Members)
            a.ToPlayer(member, "S_PRIVATE_CHAT", fields);
        return a;
    }

    // =======================================================================================
    // Channels
    // =======================================================================================

    /// <summary>
    /// C_CREATE_PRIVATE_CHANNEL (0xB868): `[u16 count][u16 off][u16 nameOff][u16 password]` and
    /// an array of invited UserDbIds, min total 0x0C. ChatManager::CreatePrivateChannel checks,
    /// in this order: the name passes InputRestriction (0x738), the name is free (0x3C0), the
    /// creator has a free slot (0x3C7), the invite count is within the member cap (0x3C9), and
    /// the password is 1000..9999 (0x3C2).
    ///
    /// <para>There is no S_CREATE_PRIVATE_CHANNEL on the wire: that packet has a dumper and a
    /// size validator but NO construction site anywhere in the binary. The creator learns about
    /// it from S_PRIVATE_CHANNEL_NOTICE(0xDFE) followed by S_JOIN_PRIVATE_CHANNEL.</para>
    /// </summary>
    private ArbiterActions CreateChannel(ArbiterActions a, int creatorId, byte[] body)
    {
        var req = ChatPackets.ParseCCreatePrivateChannel(body);
        if (req == null) return a.Reject("C_CREATE_PRIVATE_CHANNEL: short body");
        var (name, password, invited) = req.Value;

        if (name.Length == 0 || name.Length > MaxChannelNameChars)
            return a.Reject($"C_CREATE_PRIVATE_CHANNEL: name must be 1..{MaxChannelNameChars} characters");
        if (_byName.ContainsKey(name))
            return Sysmsg(a, creatorId, MsgChannelNameTaken).Reject($"channel '{name}' already exists");
        if (FreeSlot(creatorId) < 0)
            return Sysmsg(a, creatorId, MsgTooManyChannels).Reject("the creator is already in eight channels");
        if (invited.Count > MaxMembersPerChannel)
            return Sysmsg(a, creatorId, MsgChannelFull).Reject("too many invitees");
        if (password < MinPassword || password > MaxPassword)
            return Sysmsg(a, creatorId, MsgBadPassword).Reject($"password {password} is outside 1000..9999");

        var channel = new PrivateChannel
        {
            Id = ++_nextChannelId,
            Name = name,
            Password = password,
            MasterDbId = creatorId,
        };
        _channels[channel.Id] = channel;
        _byName[channel.Name] = channel.Id;
        _log.LogInformation("private channel {Id} '{Name}' created by character {Chief}",
            channel.Id, channel.Name, creatorId);

        // The invite list is VALIDATED (the cap check above) but not acted on: the real Arbiter
        // sends each invitee a prompt, and the packet that carries it is not identified yet -
        // see CHAT-DESIGN.md section 9. Invitees join by name and password like anyone else
        // until it is. Same for C_EDIT_PRIVATE_CHANNEL, which is why it has no handler here.
        a.ToPlayer(creatorId, "S_PRIVATE_CHANNEL_NOTICE",
            ChatPackets.NoticeFields(channel.Id, NoticeCreated, channel.Name));
        AddMember(a, channel, creatorId);
        return a;
    }

    /// <summary>
    /// C_JOIN_PRIVATE_CHANNEL (0x7E16): `[u16 nameOff][i16 password]`, min total 8. By NAME, not
    /// by id. Order of checks from Handler_C_JOIN_PRIVATE_CHANNEL: free slot (0x3C7), channel
    /// exists (0x3C3), password matches (0x3C6), room (0x3C9).
    /// </summary>
    private ArbiterActions JoinChannel(ArbiterActions a, int userDbId, byte[] body)
    {
        var req = ChatPackets.ParseCJoinPrivateChannel(body);
        if (req == null) return a.Reject("C_JOIN_PRIVATE_CHANNEL: short body");
        var (name, password) = req.Value;

        if (FreeSlot(userDbId) < 0)
            return Sysmsg(a, userDbId, MsgTooManyChannels).Reject("already in eight channels");
        var channel = ChannelByName(name);
        if (channel == null)
            return Sysmsg(a, userDbId, MsgNoSuchChannel).Reject($"no channel '{name}'");
        if (channel.Has(userDbId))
            return a.Reject($"already a member of '{name}'");
        if (channel.Password != password)
            return Sysmsg(a, userDbId, MsgWrongPassword).Reject("wrong password");
        if (channel.Count >= MaxMembersPerChannel)
            return Sysmsg(a, userDbId, MsgChannelFull).Reject("channel is full");

        AddMember(a, channel, userDbId);
        return a;
    }

    /// <summary>
    /// PrivateChatChannel::OnJoin: the joiner gets S_JOIN_PRIVATE_CHANNEL carrying their SLOT,
    /// the real channel id and the member list, and every member (the joiner included) gets
    /// S_PRIVATE_CHANNEL_NOTICE. The channel's empty-since stamp is cleared.
    /// </summary>
    private void AddMember(ArbiterActions a, PrivateChannel channel, int userDbId)
    {
        int slot = FreeSlot(userDbId);
        if (slot < 0) return;
        SlotsOf(userDbId)[slot] = channel.Id;
        channel.Members.Add(userDbId);
        channel.EmptySinceMs = 0;

        a.ToPlayer(userDbId, "S_JOIN_PRIVATE_CHANNEL",
            ChatPackets.JoinFields(slot, channel.Id, channel.Name, channel.Members));

        var notice = ChatPackets.NoticeFields(channel.Id, NoticeJoined, NameOf(userDbId));
        foreach (int member in channel.Members)
            a.ToPlayer(member, "S_PRIVATE_CHANNEL_NOTICE", notice);
    }

    /// <summary>
    /// C_LEAVE_PRIVATE_CHANNEL (0x561E): `[i16 index]`, min total 6. The field is the SLOT, not
    /// a channel id - the .def comment "0-7" is right and the name "index" is the binary's.
    /// </summary>
    private ArbiterActions LeaveChannelPacket(ArbiterActions a, int userDbId, byte[] body)
    {
        int? slot = ChatPackets.ParseCLeavePrivateChannel(body);
        if (slot == null) return a.Reject("C_LEAVE_PRIVATE_CHANNEL: short body");
        if (slot.Value < 0 || slot.Value >= MaxChannelsPerUser)
            return Sysmsg(a, userDbId, MsgSlotNotJoined).Reject($"slot {slot} is out of range");
        if (SlotsOf(userDbId)[slot.Value] == -1)
            return Sysmsg(a, userDbId, MsgSlotNotJoined).Reject($"slot {slot} is not joined");
        if (!LeaveChannel(a, userDbId, slot.Value, announce: true))
            return Sysmsg(a, userDbId, MsgNoSuchChannel).Reject("the channel is gone");
        return a;
    }

    /// <summary>
    /// PrivateChatChannel::OnLeave: the leaver gets S_LEAVE_PRIVATE_CHANNEL, the remaining
    /// members get S_PRIVATE_CHANNEL_NOTICE(0xE00), and an emptied channel is stamped rather
    /// than deleted - ChatManager::CheckEmptyChannel erases it ten minutes later.
    /// </summary>
    private bool LeaveChannel(ArbiterActions a, int userDbId, int slot, bool announce)
    {
        var slots = SlotsOf(userDbId);
        int channelId = slots[slot];
        slots[slot] = -1;

        var channel = Channel(channelId);
        if (channel == null) return false;
        channel.Members.Remove(userDbId);

        if (announce)
            a.ToPlayer(userDbId, "S_LEAVE_PRIVATE_CHANNEL", ChatPackets.LeaveFields(channel.Id));

        if (channel.Count == 0)
        {
            channel.EmptySinceMs = NowMs();
            return true;
        }

        // The master leaving hands the channel to the oldest remaining member. The real Arbiter
        // has a NoticeNewMaster event (0xE01) for exactly this; the promotion rule itself is not
        // visible in the decompile, so "oldest remaining" is ours.
        if (channel.IsMaster(userDbId))
        {
            channel.MasterDbId = channel.Members[0];
            var promoted = ChatPackets.NoticeFields(channel.Id, NoticeNewMaster, NameOf(channel.MasterDbId));
            foreach (int member in channel.Members)
                a.ToPlayer(member, "S_PRIVATE_CHANNEL_NOTICE", promoted);
        }

        var left = ChatPackets.NoticeFields(channel.Id, NoticeLeft, NameOf(userDbId));
        foreach (int member in channel.Members)
            a.ToPlayer(member, "S_PRIVATE_CHANNEL_NOTICE", left);
        return true;
    }

    /// <summary>
    /// C_KICK_CHANNEL_MEMBER (0xA531): `[u16 userNameOff][i16 index]`, min total 8. Note the
    /// order - the .def has it backwards, see CHAT-DESIGN.md section 5.3. Rules: the slot must
    /// be joined (0x3D1), the caller must be the master (0x3C8), you cannot kick yourself
    /// (0x116), and the name must be a member (0x3C5).
    /// </summary>
    private ArbiterActions KickMember(ArbiterActions a, int callerId, byte[] body)
    {
        var req = ChatPackets.ParseCKickChannelMember(body);
        if (req == null) return a.Reject("C_KICK_CHANNEL_MEMBER: short body");
        var (userName, slot) = req.Value;

        if (slot < 0 || slot >= MaxChannelsPerUser || SlotsOf(callerId)[slot] == -1)
            return Sysmsg(a, callerId, MsgSlotNotJoined).Reject($"slot {slot} is not joined");
        var channel = Channel(SlotsOf(callerId)[slot]);
        if (channel == null) return Sysmsg(a, callerId, MsgNoSuchChannel).Reject("the channel is gone");
        if (!channel.IsMaster(callerId))
            return Sysmsg(a, callerId, MsgNotChannelMaster).Reject("only the master may kick");

        var victim = _store.GetCharacterByName(userName);
        if (victim != null && victim.Id == callerId)
            return Sysmsg(a, callerId, MsgKickSelf).Reject("cannot kick yourself");
        if (victim == null || !channel.Has(victim.Id))
            return Sysmsg(a, callerId, MsgNotAMember).Reject($"'{userName}' is not a member");

        int victimSlot = SlotOfChannel(victim.Id, channel.Id);
        if (victimSlot < 0) return a.Reject("the victim's slot is missing - state is inconsistent");

        LeaveChannel(a, victim.Id, victimSlot, announce: true);
        a.ToPlayer(victim.Id, "S_SYSTEM_MESSAGE", ChatPackets.SystemMessageFields(
            MsgKicked, "_ChannelName", channel.Name));
        return a;
    }

    /// <summary>
    /// C_CHANGE_CHANNEL_PASSWORD (0x96E4): `[i16 index][i16 current][i16 new]`, min total 0x0A.
    /// A no-op when old == new; then channel exists (0x3C3), current matches (0x3C6), caller is
    /// the master (0x3C8), and on success system message 0x3D3 with the channel name.
    /// </summary>
    private ArbiterActions ChangePassword(ArbiterActions a, int callerId, byte[] body)
    {
        var req = ChatPackets.ParseCChangeChannelPassword(body);
        if (req == null) return a.Reject("C_CHANGE_CHANNEL_PASSWORD: short body");
        var (slot, current, next) = req.Value;

        if (current == next) return a.Reject("C_CHANGE_CHANNEL_PASSWORD: no change");
        if (slot < 0 || slot >= MaxChannelsPerUser || SlotsOf(callerId)[slot] == -1)
            return Sysmsg(a, callerId, MsgSlotNotJoined).Reject($"slot {slot} is not joined");
        var channel = Channel(SlotsOf(callerId)[slot]);
        if (channel == null) return Sysmsg(a, callerId, MsgNoSuchChannel).Reject("the channel is gone");
        if (channel.Password != current)
            return Sysmsg(a, callerId, MsgWrongPassword).Reject("wrong current password");
        if (!channel.IsMaster(callerId))
            return Sysmsg(a, callerId, MsgNotChannelMaster).Reject("only the master may change the password");
        if (next < MinPassword || next > MaxPassword)
            return Sysmsg(a, callerId, MsgBadPassword).Reject($"password {next} is outside 1000..9999");

        channel.Password = next;
        return Sysmsg(a, callerId, MsgPasswordChanged, "_ChannelName", channel.Name);
    }

    /// <summary>
    /// C_REQUEST_PRIVATE_CHANNEL_INFO (0x73BD): `[i32 channelId]`, min total 8, and the handler
    /// enforces nothing - it always answers. The reply is S_REQUEST_PRIVATE_CHANNEL_INFO
    /// (0x846F); there is no packet called S_PRIVATE_CHANNEL_INFO in this build.
    ///
    /// <para>The client opens the create dialog with channelId = -1, and the reply is then the
    /// defaults: not the master, password 1000, no members. That is what the .def comment on
    /// C_REQUEST_PRIVATE_CHANNEL_INFO describes.</para>
    /// </summary>
    private ArbiterActions ChannelInfo(ArbiterActions a, int callerId, byte[] body)
    {
        int? channelId = ChatPackets.ParseCRequestPrivateChannelInfo(body);
        if (channelId == null) return a.Reject("C_REQUEST_PRIVATE_CHANNEL_INFO: short body");

        var channel = Channel(channelId.Value);
        var names = new List<string>();
        bool isMaster = false;
        ushort password = MinPassword;
        if (channel != null)
        {
            isMaster = channel.IsMaster(callerId);
            password = channel.Password;
            foreach (int member in channel.Members) names.Add(NameOf(member));
        }

        // FriendList is the online-friends picker the create dialog shows. Empty until the chat
        // layer is wired to SocialHandlers; the packet is well-formed either way.
        a.ToPlayer(callerId, "S_REQUEST_PRIVATE_CHANNEL_INFO",
            ChatPackets.ChannelInfoFields(isMaster, password, names));
        return a;
    }

    // =======================================================================================
    // Housekeeping
    // =======================================================================================

    /// <summary>
    /// ChatManager::CheckEmptyChannel, driven by a 1000 ms timer in the real Arbiter: erase a
    /// channel that has been empty for ten minutes. Pure, so the caller supplies the clock.
    /// </summary>
    public int SweepEmptyChannels(long nowMs)
    {
        var doomed = new List<int>();
        foreach (var kv in _channels)
            if (kv.Value.Count == 0 && kv.Value.EmptySinceMs != 0
                && nowMs - kv.Value.EmptySinceMs >= EmptyChannelTimeoutMs)
                doomed.Add(kv.Key);

        foreach (int id in doomed)
        {
            if (_channels.TryGetValue(id, out var c))
            {
                _byName.Remove(c.Name);
                _log.LogInformation("private channel {Id} '{Name}' erased after {Ms} ms empty",
                    id, c.Name, nowMs - c.EmptySinceMs);
            }
            _channels.Remove(id);
        }
        return doomed.Count;
    }

    // =======================================================================================
    // Helpers
    // =======================================================================================

    private int FreeSlot(int userDbId)
    {
        var slots = SlotsOf(userDbId);
        for (int i = 0; i < slots.Length; i++) if (slots[i] == -1) return i;
        return -1;
    }

    private int SlotOfChannel(int userDbId, int channelId)
    {
        var slots = SlotsOf(userDbId);
        for (int i = 0; i < slots.Length; i++) if (slots[i] == channelId) return i;
        return -1;
    }

    private string NameOf(int userDbId)
        => _players.TryGetValue(userDbId, out var p) ? p.Name : (_store.GetCharacter(userDbId)?.Name ?? "");

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private ArbiterActions Sysmsg(ArbiterActions a, int userDbId, int id, params string[] keysAndValues)
    {
        a.ToPlayer(userDbId, "S_SYSTEM_MESSAGE", ChatPackets.SystemMessageFields(id, keysAndValues));
        return a;
    }

    /// <summary>
    /// The def to encode a chat packet with: ChatPackets.CorrectedDefs first, then NamedDefs,
    /// then the shipped registry. Same contract as GuildHandlers.ResolveDef, so the wiring can
    /// chain them: <c>n =&gt; GuildHandlers.ResolveDef(null, n) ?? ChatManager.ResolveDef(null, n)</c>.
    /// </summary>
    public static PacketDef? ResolveDef(DefinitionRegistry? defs, string name)
        => ChatPackets.ResolveDef(defs, name);
}

// =============================================================================================
// ChatPackets - the codec half of T43. Research: status/CHAT-DESIGN.md.
//
// It lives here rather than in DbProxyStaticData.cs (where PartyPackets and GuildPackets are)
// because the whole chat subsystem is one file's worth of work and that file is already 130 KB.
// The contract is the same as GuildPackets': opcode constants, the handlers' own minimum
// lengths, hand-written parsers, field dictionaries for the encoder, and the two .def
// dictionaries ActionDispatcher.ResolveDef consults.
//
// Opcodes are from data.json maps."376012", and all 22 of them match the ids the Arbiter writes
// into the packet - test Chat_opcodes_match_the_decompile.
//
// Offsets in the comments are PACKET-relative (the [u16 len][u16 opcode] header included, so the
// first ref slot is 0x04); the parsers take the BODY, i.e. index = offset - 4.
// =============================================================================================
public static class ChatPackets
{
    // ---- client -> Arbiter ----
    public const ushort C_WHISPER = 0xE8E5;
    public const ushort C_CHAT = 0xEB77;
    public const ushort C_CREATE_PRIVATE_CHANNEL = 0xB868;
    public const ushort C_EDIT_PRIVATE_CHANNEL = 0x7DA0;
    public const ushort C_JOIN_PRIVATE_CHANNEL = 0x7E16;
    public const ushort C_LEAVE_PRIVATE_CHANNEL = 0x561E;
    public const ushort C_KICK_CHANNEL_MEMBER = 0xA531;
    public const ushort C_CHANGE_CHANNEL_PASSWORD = 0x96E4;
    public const ushort C_REQUEST_PRIVATE_CHANNEL_INFO = 0x73BD;
    /// <summary>The misspelling is the protocol's, not ours.</summary>
    public const ushort C_REUQUEST_JOINED_CHANNEL_LIST = 0x7F78;
    /// <summary>No Arbiter handler at all - not found anywhere in the binary.</summary>
    public const ushort C_LIST_CHANNEL = 0x661B;

    // ---- Arbiter -> client ----
    public const ushort S_WHISPER = 0x96F1;
    public const ushort S_CHAT = 0x7D6B;
    public const ushort S_PRIVATE_CHAT = 0xBD2B;
    public const ushort S_PRIVATE_CHANNEL_NOTICE = 0x879A;
    public const ushort S_JOIN_PRIVATE_CHANNEL = 0x81E7;
    public const ushort S_LEAVE_PRIVATE_CHANNEL = 0x67AD;
    /// <summary>The reply to C_REQUEST_PRIVATE_CHANNEL_INFO. There is no packet called
    /// S_PRIVATE_CHANNEL_INFO in this build.</summary>
    public const ushort S_REQUEST_PRIVATE_CHANNEL_INFO = 0x846F;
    public const ushort S_REQUEST_JOINED_CHANNEL_LIST = 0x51EC;
    public const ushort S_CANNOT_USE_CHAT_CHANNEL = 0x6D00;
    public const ushort S_SYSTEM_MESSAGE = 0xF30E;
    /// <summary>Dead in 100.02: a dumper and a size validator exist, but the binary has no
    /// construction site for it. A create is answered with S_PRIVATE_CHANNEL_NOTICE(0xDFE)
    /// followed by S_JOIN_PRIVATE_CHANNEL.</summary>
    public const ushort S_CREATE_PRIVATE_CHANNEL = 0x76E5;

    /// <summary>
    /// Minimum TOTAL client packet length (the 4-byte header included) the real handler enforces
    /// before it touches the body, or 0 for a chat packet the Arbiter does not answer.
    /// </summary>
    public static int MinClientLength(ushort op) => op switch
    {
        C_WHISPER => 0x08,
        C_CHAT => 0x0A,
        C_CREATE_PRIVATE_CHANNEL => 0x0C,
        C_EDIT_PRIVATE_CHANNEL => 0x0C,
        C_JOIN_PRIVATE_CHANNEL => 0x08,
        C_LEAVE_PRIVATE_CHANNEL => 0x06,
        C_KICK_CHANNEL_MEMBER => 0x08,
        C_CHANGE_CHANNEL_PASSWORD => 0x0A,
        C_REQUEST_PRIVATE_CHANNEL_INFO => 0x08,
        C_REUQUEST_JOINED_CHANNEL_LIST => 0x04,
        _ => 0,
    };

    // =======================================================================================
    // client -> Arbiter parsers. Each takes the BODY and returns null rather than throwing on a
    // short one - the real handler answers GET_CLIENT_BUFFER_BUFSIZE_MISMATCH and drops it.
    // =======================================================================================

    /// <summary>C_WHISPER (0xE8E5): `[u16 toOff][u16 talkOff]`, fixed part 0x08.</summary>
    public static (string to, string talk)? ParseCWhisper(byte[] body)
        => body.Length < 4 ? null : (ReadWString(body, 0), ReadWString(body, 2));

    /// <summary>C_CHAT (0xEB77): `[u16 talkOff][i32 type]`, fixed part 0x0A. `type` is the
    /// ChatType; 0x0B..0x12 means a private channel and carries a SLOT, not an id.</summary>
    public static (string talk, int type)? ParseCChat(byte[] body)
        => body.Length < 6 ? null : (ReadWString(body, 0), BitConverter.ToInt32(body, 2));

    /// <summary>C_CREATE_PRIVATE_CHANNEL (0xB868) and C_EDIT_PRIVATE_CHANNEL (0x7DA0) share one
    /// shape: `[u16 userListCount][u16 userListOff][u16 nameOff][u16 password]`, fixed part 0x0C,
    /// then an array of `{i32 UserDbId}` elements.</summary>
    public static (string name, ushort password, List<int> invited)? ParseCCreatePrivateChannel(byte[] body)
    {
        if (body.Length < 8) return null;
        int count = BitConverter.ToUInt16(body, 0);
        int offset = BitConverter.ToUInt16(body, 2);
        string name = ReadWString(body, 4);
        ushort password = BitConverter.ToUInt16(body, 6);

        var invited = new List<int>(count);
        int at = offset - 4;
        for (int i = 0; i < count; i++)
        {
            // element: [u16 self][u16 next][i32 UserDbId]
            if (at < 0 || at + 8 > body.Length) return null;
            invited.Add(BitConverter.ToInt32(body, at + 4));
            int next = BitConverter.ToUInt16(body, at + 2);
            if (next == 0) break;
            at = next - 4;
        }
        return (name, password, invited);
    }

    /// <summary>C_JOIN_PRIVATE_CHANNEL (0x7E16): `[u16 nameOff][i16 password]`, fixed part 0x08.
    /// By NAME - the client has no id to give until it has joined.</summary>
    public static (string name, ushort password)? ParseCJoinPrivateChannel(byte[] body)
        => body.Length < 4 ? null : (ReadWString(body, 0), BitConverter.ToUInt16(body, 2));

    /// <summary>C_LEAVE_PRIVATE_CHANNEL (0x561E): `[i16 index]`, fixed part 0x06. The SLOT 0..7.</summary>
    public static int? ParseCLeavePrivateChannel(byte[] body)
        => body.Length < 2 ? null : BitConverter.ToInt16(body, 0);

    /// <summary>
    /// C_KICK_CHANNEL_MEMBER (0xA531): `[u16 userNameOff][i16 index]`, fixed part 0x08.
    /// The shipped .def declares two strings in the other order and is WRONG - the handler reads
    /// the name ref at packet 0x04 and a raw i16 slot at 0x06.
    /// </summary>
    public static (string userName, int slot)? ParseCKickChannelMember(byte[] body)
        => body.Length < 4 ? null : (ReadWString(body, 0), BitConverter.ToInt16(body, 2));

    /// <summary>C_CHANGE_CHANNEL_PASSWORD (0x96E4): `[i16 index][i16 current][i16 new]`,
    /// fixed part 0x0A.</summary>
    public static (int slot, ushort current, ushort next)? ParseCChangeChannelPassword(byte[] body)
        => body.Length < 6 ? null
            : (BitConverter.ToInt16(body, 0), BitConverter.ToUInt16(body, 2), BitConverter.ToUInt16(body, 4));

    /// <summary>C_REQUEST_PRIVATE_CHANNEL_INFO (0x73BD): `[i32 channelId]`, fixed part 0x08.
    /// The client sends -1 when it opens the create dialog.</summary>
    public static int? ParseCRequestPrivateChannelInfo(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    // =======================================================================================
    // Arbiter -> client field dictionaries. Names match the def ResolveDef hands the encoder.
    // =======================================================================================

    /// <summary>
    /// S_WHISPER (0x96F1), fixed part 0x15. Both copies - the receiver's and the sender's echo -
    /// carry the SENDER as FromName/FromGameId and the receiver as To, so one dictionary serves
    /// both sends.
    /// </summary>
    public static Dictionary<string, object> WhisperFields(in ChatPlayer sender, string toName)
        => new()
        {
            ["fromName"] = sender.Name,
            ["to"] = toName ?? "",
            ["talk"] = "",
            ["fromGameId"] = sender.GameId,
            ["isWorldEventTarget"] = false,
            ["isAdmin"] = sender.IsAdmin,
            ["isExistingUser"] = false,
        };

    /// <summary>The same, with the text. Kept separate so the caller cannot forget it.</summary>
    public static Dictionary<string, object> WhisperFields(in ChatPlayer sender, string toName, string talk)
    {
        var f = WhisperFields(sender, toName);
        f["talk"] = talk ?? "";
        return f;
    }

    /// <summary>S_PRIVATE_CHAT (0xBD2B), fixed part 0x14. Carries the REAL channel id, even
    /// though the client addressed the channel by slot.</summary>
    public static Dictionary<string, object> PrivateChatFields(int channelId, in ChatPlayer sender, string talk)
        => new()
        {
            ["fromName"] = sender.Name,
            ["talk"] = talk ?? "",
            ["channelId"] = channelId,
            ["fromGameId"] = sender.GameId,
        };

    /// <summary>S_JOIN_PRIVATE_CHANNEL (0x81E7), fixed part 0x12: the joiner's SLOT, the real
    /// channel id, the name, and the member list as `{i32 UserDbId}` elements.</summary>
    public static Dictionary<string, object> JoinFields(int slot, int channelId, string name, IEnumerable<int> members)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (int m in members) list.Add(new Dictionary<string, object> { ["userDbId"] = m });
        return new Dictionary<string, object>
        {
            ["index"] = slot,
            ["channelId"] = channelId,
            ["name"] = name ?? "",
            ["userList"] = list,
        };
    }

    /// <summary>S_LEAVE_PRIVATE_CHANNEL (0x67AD), fixed part 0x08.</summary>
    public static Dictionary<string, object> LeaveFields(int channelId)
        => new() { ["channelId"] = channelId };

    /// <summary>S_PRIVATE_CHANNEL_NOTICE (0x879A), fixed part 0x0E. SysMsgId is one of
    /// 0xDFE created / 0xDFF joined / 0xE00 left / 0xE01 new master, and Value is the name the
    /// message interpolates.</summary>
    public static Dictionary<string, object> NoticeFields(int channelId, int sysMsgId, string value)
        => new() { ["channelId"] = channelId, ["sysMsgId"] = sysMsgId, ["value"] = value ?? "" };

    /// <summary>
    /// S_REQUEST_PRIVATE_CHANNEL_INFO (0x846F), fixed part 0x0F. FriendList is the online-friends
    /// picker the create dialog shows; it is left empty here (see ChatManager.ChannelInfo).
    /// </summary>
    public static Dictionary<string, object> ChannelInfoFields(bool isMaster, ushort password,
        IEnumerable<string> memberNames)
    {
        var members = new List<Dictionary<string, object>>();
        foreach (var n in memberNames) members.Add(new Dictionary<string, object> { ["charName"] = n ?? "" });
        return new Dictionary<string, object>
        {
            ["isMaster"] = isMaster,
            ["password"] = password,
            ["memberList"] = members,
            ["friendList"] = new List<Dictionary<string, object>>(),
        };
    }

    /// <summary>
    /// S_SYSTEM_MESSAGE (0xF30E): one wide string, `@id` then \v-separated key/value pairs.
    /// The same encoding SocialHandlers.Smt produces - duplicated rather than referenced so a
    /// World/ file does not have to depend on Handlers/.
    /// </summary>
    public static Dictionary<string, object> SystemMessageFields(int id, params string[] keysAndValues)
        => new() { ["message"] = Smt(id, keysAndValues) };

    public static string Smt(int id, params string[] keysAndValues)
    {
        var sb = new System.Text.StringBuilder("@").Append(id);
        for (int i = 0; i + 1 < keysAndValues.Length; i += 2)
            sb.Append('\v').Append(keysAndValues[i]).Append('\v').Append(keysAndValues[i + 1]);
        return sb.ToString();
    }

    // =======================================================================================
    // .def overrides. Same two-dictionary contract as GuildPackets: CorrectedDefs is where the
    // shipped file is WRONG, NamedDefs is where it is byte-correct but the field names are not
    // the binary's. status/CHAT-DESIGN.md section 5.3 has the evidence for each.
    // =======================================================================================

    /// <summary>The shipped .def gets the bytes wrong. Pinned by Chat_corrected_defs_*.</summary>
    public static IReadOnlyDictionary<string, string> CorrectedDefs { get; } = new Dictionary<string, string>
    {
        // The handler reads a NAME ref at 0x04 and a raw i16 SLOT at 0x06; the .def declares
        // `string index` then `string userName`, i.e. two refs in the other order.
        ["C_KICK_CHANNEL_MEMBER"] = "string userName\nint16  index\n",

        // `array unk` has no element type, so the writer emits an element header and no payload.
        // The real element is a single i32 UserDbId - the dumper calls the array UserList.
        ["S_JOIN_PRIVATE_CHANNEL"] = @"ref userList
ref name

int32 index
int32 channelId

array userList
- int32 userDbId

string name
",
    };

    /// <summary>Byte-correct as shipped; restated with the names the Arbiter's own PDL dumper
    /// uses, so the handlers above are readable. Chat_named_defs_are_byte_identical_to_the_shipped_ones
    /// proves nothing moved.</summary>
    public static IReadOnlyDictionary<string, string> NamedDefs { get; } = new Dictionary<string, string>
    {
        ["S_WHISPER"] = @"ref fromName
ref to
ref talk

uint64 fromGameId
bool  isWorldEventTarget
bool  isAdmin
bool  isExistingUser

string fromName
string to
string talk
",
        ["S_PRIVATE_CHAT"] = @"ref fromName
ref talk

int32 channelId
uint64 fromGameId

string fromName
string talk
",
        ["S_PRIVATE_CHANNEL_NOTICE"] = @"ref value

int32 channelId
int32 sysMsgId

string value
",
        ["S_REQUEST_PRIVATE_CHANNEL_INFO"] = @"ref friendList
ref memberList

bool   isMaster
uint16 password

array memberList
- ref charName
- string charName

array friendList
- ref charName
- uint32 userDbId
- uint32 userClass
- uint32 level
- uint32 groupId
- string charName
",
    };

    private static readonly Dictionary<string, PacketDef> _overrides = new();
    private static readonly object _overrideLock = new();

    /// <summary>CorrectedDefs, then NamedDefs, then the shipped registry - the same contract as
    /// GuildHandlers.ResolveDef, so the wiring can chain the two with <c>??</c>.</summary>
    public static PacketDef? ResolveDef(DefinitionRegistry? defs, string name)
    {
        lock (_overrideLock)
        {
            if (_overrides.TryGetValue(name, out var cached)) return cached;
            string? text = null;
            if (CorrectedDefs.TryGetValue(name, out var corrected)) text = corrected;
            else if (NamedDefs.TryGetValue(name, out var named)) text = named;
            if (text != null)
            {
                var parsed = DefinitionParser.ParseText(name, text);
                _overrides[name] = parsed;
                return parsed;
            }
        }
        return defs?.Get(name);
    }

    // ---------------------------------- private helpers ----------------------------------

    /// <summary>Reads a NUL-terminated UTF-16LE string whose u16 PACKET offset sits at
    /// body[slotIndex]. "" for the 0/out-of-range offsets the real handlers fall back on.</summary>
    private static string ReadWString(byte[] body, int slotIndex)
    {
        if (slotIndex + 2 > body.Length) return string.Empty;
        int at = BitConverter.ToUInt16(body, slotIndex) - 4;
        if (at < 0 || at >= body.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = at; i + 1 < body.Length; i += 2)
        {
            char ch = (char)(body[i] | (body[i + 1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
