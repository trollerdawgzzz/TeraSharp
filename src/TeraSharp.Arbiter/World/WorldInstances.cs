using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// WorldInstances - T108, multi-world step 2 (status/MULTIWORLD-DESIGN.md sections 2 and 7).
//
// Step 1 (T103) gave every link a WorldId. This file is what the Arbiter does with it once more
// than one World is connected: a table of which World owns which dungeon channel, and the two
// hops of the enter-dungeon handshake routed through that table instead of straight back down
// the socket the request arrived on.
//
// Nothing here changes a single-World server. Both tables start empty, WorldRouting's two hooks
// start null, and every routing decision then resolves to "the link it came from" - which is
// literally the call DbProxyHandlers made before this file existed.
// =============================================================================================

/// <summary>
/// The seam between the cowork-owned routing in this file and the human-owned per-World link
/// sets in <c>WorldBridge</c>. Both hooks are null until the T108 WorldBridge patch lands; while
/// they are, every link is world 0 and no cross-World send is possible, which is today's tree.
/// </summary>
public static class WorldRouting
{
    /// <summary>Which World a link registered as (SA_REGISTER payload +5). Null hook = world 0.</summary>
    public static Func<WorldLink, int>? WorldIdOfLink;

    /// <summary>Send a frame on a given World's links. Null hook = no cross-World send.</summary>
    public static Action<int, ushort, byte[]>? SendToWorld;

    /// <summary>
    /// T111. Whether a World has any connected link. Null hook = nothing is live, which is what
    /// keeps a process with no per-World link sets on the default World whatever the config says.
    ///
    /// <para>This is the guard that makes seeding the continent map safe. ServerConfig.xml gives
    /// continent 102 to world 10; on a server where world 10 was never started, routing there
    /// would put AS_ENTER_WORLD on a socket that does not exist and the player would simply
    /// never load. Liveness first, config second.</para>
    /// </summary>
    public static Func<int, bool>? HasLinks;

    /// <summary>True when that World is connected and can be routed to.</summary>
    public static bool IsLive(int worldId) => HasLinks?.Invoke(worldId) ?? false;

    /// <summary>The World a frame arrived from, or <see cref="WorldRegistration.DefaultWorldId"/>.</summary>
    public static int WorldIdOf(WorldLink? link)
        => link == null ? WorldRegistration.DefaultWorldId
                        : WorldIdOfLink?.Invoke(link) ?? WorldRegistration.DefaultWorldId;

    /// <summary>True when the frame was handed to that World. False means "no hook, send it yourself".</summary>
    public static bool TrySend(int worldId, ushort op, byte[] payload)
    {
        var send = SendToWorld;
        if (send == null) return false;
        send(worldId, op, payload);
        return true;
    }

    /// <summary>Tests only - put both hooks back the way a fresh process has them.</summary>
    internal static void ResetForTest() { WorldIdOfLink = null; SendToWorld = null; HasLinks = null; }
}

/// <summary>One announced dungeon instance: a channel of a continent, owned by one World.</summary>
public readonly record struct DungeonChannel(int ContinentId, int ChannelId, int WorldId);

/// <summary>
/// The instance registry - which World hosts which dungeon channel.
///
/// <para>Fed by <c>SA_ADD_DUNGEON_CHANNEL</c> (0x13C5) and emptied by
/// <c>SA_REMOVE_DUNGEON_CHANNEL</c> (0x13C6). Layouts are the Arbiter's own PDL dumpers
/// (FUN_140212600 / FUN_1402201b0, Arb_part_016.c); offsets below are PAYLOAD-relative, so
/// payload index = frame offset - 6:</para>
/// <code>
///   0x13C5  +0 i32 ContinentId   +4 i32 ChannelId   +8 DungeonOwnerInfo (24 B)   min payload 32
///   0x13C6  +0 i32 ContinentId   +4 i32 ChannelId                                min payload 8
/// </code>
/// <para>DungeonOwnerInfo is memcpy'd with its own struct padding - u64 at +0, u8 at +8, three
/// pad bytes, u64 at +12, i32 at +20 - which is why the add frame is 0x26 and not 0x23. Nothing
/// here reads inside it; the owner matters to the World, not to the routing.</para>
///
/// <para><b>Continents come from config too.</b> The real Arbiter's lookup for "who owns this
/// continent" (FUN_14082e730, called from Handler_SA_REQUEST_ENTER_DUNGEON with the requested
/// ContinentId) answers from its copy of <c>ServerConfig.xml/WorldServerList</c>, which lists the
/// continents each World id loads - the channel table is the dynamic half. <see cref="MapContinent"/>
/// is that static half; it is empty until someone loads the config, and an empty map is a
/// single-World server.</para>
/// </summary>
public sealed class DungeonChannels
{
    public const ushort SA_ADD_DUNGEON_CHANNEL = 0x13C5;
    public const ushort SA_REMOVE_DUNGEON_CHANNEL = 0x13C6;

    /// <summary>Frame 0x26 (the dumper guard is <c>0x25 &lt; param_2</c>), so payload 32.</summary>
    public const int AddMinPayload = 0x26 - 6;

    /// <summary>Frame 0x0E (guard <c>0xd &lt; param_2</c>), so payload 8.</summary>
    public const int RemoveMinPayload = 0x0E - 6;

    public const int ContinentIdOffset = 0;
    public const int ChannelIdOffset = 4;
    public const int OwnerInfoOffset = 8;
    public const int OwnerInfoSize = 24;

    private readonly Dictionary<(int Continent, int Channel), int> _channels = new();
    private readonly Dictionary<int, int> _continents = new();
    private readonly object _gate = new();

    /// <summary>Record an announced channel. Null for a frame shorter than the real guard.</summary>
    public DungeonChannel? Add(int worldId, byte[]? payload)
    {
        if (payload == null || payload.Length < AddMinPayload) return null;
        var ch = new DungeonChannel(
            BitConverter.ToInt32(payload, ContinentIdOffset),
            BitConverter.ToInt32(payload, ChannelIdOffset),
            worldId);
        lock (_gate) _channels[(ch.ContinentId, ch.ChannelId)] = worldId;
        return ch;
    }

    /// <summary>Drop an announced channel. False for a short frame or one we never had.</summary>
    public bool Remove(int worldId, byte[]? payload)
    {
        if (payload == null || payload.Length < RemoveMinPayload) return false;
        var key = (BitConverter.ToInt32(payload, ContinentIdOffset),
                   BitConverter.ToInt32(payload, ChannelIdOffset));
        lock (_gate)
            return _channels.TryGetValue(key, out int owner) && owner == worldId && _channels.Remove(key);
    }

    /// <summary>The World hosting one instance, or null.</summary>
    public int? WorldForChannel(int continentId, int channelId)
    {
        lock (_gate)
            return _channels.TryGetValue((continentId, channelId), out int w) ? w : null;
    }

    /// <summary>
    /// The World that hosts this continent: **the configured owner first**, then a World that has
    /// announced a channel on it, else null (= "the one asking", i.e. the catch-all World).
    ///
    /// <para>T111 put config first, mirroring the binary. The Arbiter's own lookup,
    /// <c>WorldSessionManager::GetDataSession(continentId)</c> (Arb_part_046.c:2545), reads the
    /// continent's <c>worldServerInfo</c> list out of PlanetInfo and asserts at
    /// <c>WorldSessionManager.cpp(356)</c> when the count is anything but 1 - so a continent has
    /// exactly one owner and it comes from <c>ServerConfig.xml</c>, never from what a World
    /// happens to have announced. The two can only disagree in a configuration the real server
    /// refuses to run (MULTIWORLD-DESIGN.md section 7.1).</para>
    /// </summary>
    public int? WorldForContinent(int continentId)
    {
        lock (_gate)
        {
            if (_continents.TryGetValue(continentId, out int configured)) return configured;
            foreach (var (key, world) in _channels)
                if (key.Continent == continentId) return world;
            return null;
        }
    }

    /// <summary>The static half: ServerConfig.xml's "World id N loads continent C".</summary>
    public void MapContinent(int continentId, int worldId)
    {
        lock (_gate) _continents[continentId] = worldId;
    }

    /// <summary>How many continents the config named. 0 before the seed, and on a bare tree.</summary>
    public int ConfiguredContinents { get { lock (_gate) return _continents.Count; } }

    /// <summary>
    /// The World that owns every continent nobody claimed - <c>loadAllContinents="true"</c> in
    /// ServerConfig.xml, which is <c>&lt;WorldServer id="0"&gt;</c> on this deployment.
    /// </summary>
    public int CatchAllWorldId { get; set; } = WorldRegistration.DefaultWorldId;

    /// <summary>Every announced channel, for the status tab and for tests.</summary>
    public IReadOnlyList<DungeonChannel> Snapshot()
    {
        lock (_gate)
        {
            var all = new List<DungeonChannel>(_channels.Count);
            foreach (var (key, world) in _channels) all.Add(new DungeonChannel(key.Continent, key.Channel, world));
            return all;
        }
    }

    /// <summary>A World disconnected: its instances are gone. Returns how many were dropped.</summary>
    public int ForgetWorld(int worldId)
    {
        lock (_gate)
        {
            var dead = new List<(int, int)>();
            foreach (var (key, world) in _channels) if (world == worldId) dead.Add(key);
            foreach (var key in dead) _channels.Remove(key);
            return dead.Count;
        }
    }

    public int Count { get { lock (_gate) return _channels.Count; } }

    /// <summary>Drop every channel and every configured continent (World restart, and tests).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _channels.Clear();
            _continents.Clear();
            CatchAllWorldId = WorldRegistration.DefaultWorldId;
        }
    }
}

/// <summary>
/// Which World a user's enter-dungeon request came from, remembered between 0x13BE and 0x13C0.
///
/// <para><c>Handler_SA_RESPONSE_ENTER_DUNGEON</c> (Arb_part_062.c:13707) finds the user from the
/// PDId's HIGH 32 bits and sends 0x13C1 to <i>that user's own</i> World session - the World it
/// left, not the World that answered. With one World the two are the same socket, which is why
/// echoing on the arriving link has always worked; with two they are not.</para>
/// </summary>
public sealed class DungeonTransfers
{
    private readonly Dictionary<ulong, int> _origin = new();
    private readonly object _gate = new();

    public void Remember(ulong pdId, int fromWorldId) { lock (_gate) _origin[pdId] = fromWorldId; }

    /// <summary>The World that asked, removing the record. Null if we never saw the request.</summary>
    public int? Take(ulong pdId)
    {
        lock (_gate)
        {
            if (!_origin.TryGetValue(pdId, out int w)) return null;
            _origin.Remove(pdId);
            return w;
        }
    }

    public int Pending { get { lock (_gate) return _origin.Count; } }

    /// <summary>Drop every in-flight transfer (World restart, and tests).</summary>
    public void Clear() { lock (_gate) _origin.Clear(); }

    /// <summary>A World disconnected: forget every transfer that started there.</summary>
    public int ForgetWorld(int worldId)
    {
        lock (_gate)
        {
            var dead = new List<ulong>();
            foreach (var (pd, world) in _origin) if (world == worldId) dead.Add(pd);
            foreach (var pd in dead) _origin.Remove(pd);
            return dead.Count;
        }
    }
}

/// <summary>Where one built frame has to go: a World id, an opcode and the bytes.</summary>
public readonly record struct WorldSend(int WorldId, ushort Opcode, byte[] Payload);

/// <summary>
/// The enter-dungeon transfer path, routed by <see cref="Channels"/>.
///
/// <para>The bytes are not built here - <see cref="DbProxyHandlers.BuildAsRequestEnterDungeon"/>
/// and <see cref="DbProxyHandlers.BuildAsResponseEnterDungeon"/> own them and are byte-exact
/// against cap_newchar.log seq 2457/2458 and 2464/2465. T108 only decides which World they go
/// to.</para>
/// </summary>
public static class DungeonRouting
{
    /// <summary>The process-wide instance registry. One per Arbiter, like the party table.</summary>
    public static DungeonChannels Channels { get; } = new();

    /// <summary>The in-flight 0x13BE -&gt; 0x13C0 pairs.</summary>
    public static DungeonTransfers Transfers { get; } = new();

    /// <summary>Tests only - empty both tables and unhook WorldRouting.</summary>
    internal static void ResetForTest()
    {
        Channels.Clear();
        Transfers.Clear();
        WorldRouting.ResetForTest();
    }

    /// <summary>
    /// Decide where <c>AS_REQUEST_ENTER_DUNGEON</c> (0x13BF) goes and remember who asked.
    /// The continent is <c>DungeonEnterContext[0]</c> = payload
    /// <see cref="DbProxyHandlers.DungeonCtxDungeonId"/>, which is the value the real handler
    /// passes to its own continent-&gt;World lookup.
    /// </summary>
    public static WorldSend RouteRequest(DungeonChannels channels, DungeonTransfers transfers,
                                         int fromWorldId, byte[] request, byte[] forward)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(transfers);
        int continentId = request.Length >= DbProxyHandlers.DungeonCtxDungeonId + 4
            ? BitConverter.ToInt32(request, DbProxyHandlers.DungeonCtxDungeonId)
            : 0;
        int target = channels.WorldForContinent(continentId) is int owner && WorldRouting.IsLive(owner)
            ? owner
            : fromWorldId;
        if (forward.Length >= 8) transfers.Remember(BitConverter.ToUInt64(forward, 0), fromWorldId);
        return new WorldSend(target, DbProxyHandlers.AS_REQUEST_ENTER_DUNGEON, forward);
    }

    /// <summary>
    /// Decide where <c>AS_RESPONSE_ENTER_DUNGEON</c> (0x13C1) goes: back to the World that sent
    /// the 0x13BE for this PDId, or - if we never saw one - back where the answer came from,
    /// which is what the Arbiter did before there was a second World.
    /// </summary>
    public static WorldSend RouteResponse(DungeonTransfers transfers, int fromWorldId, byte[] forward)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        int target = fromWorldId;
        if (forward.Length >= 8) target = transfers.Take(BitConverter.ToUInt64(forward, 0)) ?? fromWorldId;
        return new WorldSend(target, DbProxyHandlers.AS_RESPONSE_ENTER_DUNGEON, forward);
    }

    // AS_ENTER_WORLD payload offsets. DbProxyHandlers already owns these three - T21 pinned
    // them for the retry builder - so they are aliased, not re-derived: frame 0x36/0x3A/0x56
    // minus the 6-byte header is 48/52/80.

    /// <summary>Payload 48: the continent the player enters.</summary>
    public const int EnterWorldContinentOffset = DbProxyHandlers.EnterWorldContinentIdOffset;

    /// <summary>Payload 52: the instance, or -1 for the open world.</summary>
    public const int EnterWorldChannelInstanceOffset = DbProxyHandlers.EnterWorldChannelInstanceIdOffset;

    /// <summary>The ChannelInstanceId WorldEntry sends for a character who is not in an instance.</summary>
    public const uint OpenWorldChannelInstance = 0xFFFFFFFF;

    /// <summary>
    /// Which World an <c>AS_ENTER_WORLD</c> (0x138E) has to go to, read from the payload
    /// WorldEntry just built. The open world and anything unannounced stay on
    /// <see cref="WorldRegistration.DefaultWorldId"/>, so a single-World server never moves.
    ///
    /// <para>There is no worldId field in AS_ENTER_WORLD - the destination World is chosen by
    /// which socket the Arbiter writes to (status/MULTIWORLD-DESIGN.md section 2), which is why
    /// this has to be resolved here rather than stamped into the frame.</para>
    /// </summary>
    public static int WorldForEnterWorld(byte[]? enterPayload)
    {
        int fallback = Channels.CatchAllWorldId;
        if (enterPayload == null || enterPayload.Length < EnterWorldChannelInstanceOffset + 4)
            return fallback;
        int continentId = BitConverter.ToInt32(enterPayload, EnterWorldContinentOffset);
        uint instance = BitConverter.ToUInt32(enterPayload, EnterWorldChannelInstanceOffset);

        // The configured owner (or an announced channel) only wins if that World is actually
        // connected. ServerConfig.xml hands continent 102 to world 10 whether or not anyone
        // started world 10 - T111. With no per-World link sets nothing is live and this is the
        // catch-all World, which is the single-World tree exactly as it was.
        if (instance != OpenWorldChannelInstance
            && Channels.WorldForChannel(continentId, unchecked((int)instance)) is int owner
            && WorldRouting.IsLive(owner))
            return owner;
        if (Channels.WorldForContinent(continentId) is int host && WorldRouting.IsLive(host))
            return host;
        return fallback;
    }

    /// <summary>Payload 80: the Ticket (our tunnel key).</summary>
    public const int EnterWorldTicketOffset = DbProxyHandlers.EnterWorldTicketOffset;

    /// <summary>
    /// Write the Ticket into an AS_ENTER_WORLD payload that was built without one. The Ticket
    /// indexes the DESTINATION World's bypass slots, so it cannot be allocated until the World
    /// is known, and the World is read out of the payload - hence the two steps. Same trick
    /// <c>DbProxyHandlers.BuildEnterWorldRetryPayload</c> already uses for the retry.
    /// </summary>
    public static bool StampTicket(byte[]? enterPayload, uint ticket)
    {
        if (enterPayload == null || enterPayload.Length < EnterWorldTicketOffset + 4) return false;
        BitConverter.TryWriteBytes(enterPayload.AsSpan(EnterWorldTicketOffset, 4), ticket);
        return true;
    }

    /// <summary>
    /// Send one routed frame. A target that is the arriving link's own World - or a process with
    /// no per-World link sets yet - goes out on <paramref name="link"/>, byte for byte the send
    /// DbProxyHandlers did before T108.
    /// </summary>
    public static void Dispatch(WorldLink? link, int fromWorldId, WorldSend send, ILogger? log)
    {
        if (send.WorldId != fromWorldId && WorldRouting.IsLive(send.WorldId)
            && WorldRouting.TrySend(send.WorldId, send.Opcode, send.Payload))
        {
            log?.LogInformation("0x{Op:X4}: world {From} -> world {To} ({Len} B)",
                send.Opcode, fromWorldId, send.WorldId, send.Payload.Length + 6);
            return;
        }
        if (link == null)
        {
            log?.LogWarning("0x{Op:X4} for world {To} has nowhere to go - no link and no bridge hook",
                send.Opcode, send.WorldId);
            return;
        }
        link.SendFrame(send.Opcode, send.Payload);
    }
}

/// <summary>
/// SA_WORLD_CONTINENT_LIST (0x164D) - the roster a World sends right after SA_REGISTER, saying
/// which continents it loads. T138b: this is the LIVE half of "who owns this continent", and it
/// feeds the same <see cref="DungeonChannels.MapContinent"/> table that
/// <see cref="WorldServerList.SeedDefault"/> seeds from config. Live data wins because it arrives
/// later; config is the fallback for a World that never sends one.
///
/// <para>Layout, pinned against D:\packetlogs\cap_multiworld3.log (T137c). Payload-relative:</para>
/// <code>
///   0   i32 count / 4 i32 unk (22 in every captured frame) / 8 i32 planetId / 12 i32 worldId
///   16  count x 16 B:  i32 minLevel / i32 maxLevel / i32 continentId / i32 unk
/// </code>
/// <para>It consumes the payload EXACTLY on all three captured links - the main World 173 rows in
/// 2784 B, world 13 34 rows in 560 B, world 12 5 rows in 96 B - which is what makes the stride
/// certain rather than plausible. Velik's Sanctuary (9781) is in world 13's roster and in neither
/// of the others; that one fact is the whole of continent routing.</para>
/// </summary>
public static class WorldContinentList
{
    public const ushort SA_WORLD_CONTINENT_LIST = 0x164D;

    /// <summary>Header is four int32; anything shorter is not this frame.</summary>
    public const int MinPayload = 16;

    /// <summary>One row: a continent and the level band it is offered at.</summary>
    public readonly record struct Row(int MinLevel, int MaxLevel, int ContinentId);

    /// <summary>What one World declared.</summary>
    public sealed class Roster
    {
        public int WorldId { get; init; }
        public int PlanetId { get; init; }
        public IReadOnlyList<Row> Rows { get; init; } = Array.Empty<Row>();
    }

    /// <summary>Parse, or null for a short payload or a count that overruns it.</summary>
    public static Roster? Parse(byte[]? payload)
    {
        if (payload == null || payload.Length < MinPayload) return null;
        int count = BitConverter.ToInt32(payload, 0);
        if (count < 0 || MinPayload + count * 16 > payload.Length) return null;
        var rows = new Row[count];
        for (int i = 0; i < count; i++)
        {
            int o = MinPayload + i * 16;
            rows[i] = new Row(BitConverter.ToInt32(payload, o),
                              BitConverter.ToInt32(payload, o + 4),
                              BitConverter.ToInt32(payload, o + 8));
        }
        return new Roster
        {
            WorldId = BitConverter.ToInt32(payload, 12),
            PlanetId = BitConverter.ToInt32(payload, 8),
            Rows = rows,
        };
    }

    /// <summary>
    /// Fold a roster into the continent table. Returns the number of continents claimed, or 0 for
    /// a frame that did not parse. A World that reconnects simply re-claims its continents, which
    /// is what cap_multiworld3 needs: the DungeonServer came back as link #9, then #14, then #16.
    /// </summary>
    public static int Apply(DungeonChannels channels, byte[]? payload, ILogger? log = null)
    {
        if (channels == null) return 0;
        var roster = Parse(payload);
        if (roster == null) return 0;
        foreach (var row in roster.Rows) channels.MapContinent(row.ContinentId, roster.WorldId);
        log?.LogInformation("World {W} claims {N} continents (planet {P})",
            roster.WorldId, roster.Rows.Count, roster.PlanetId);
        return roster.Rows.Count;
    }
}

/// <summary>
/// The cross-World dungeon hand-off, as cap_multiworld3 performs it. T138b.
///
/// <para>Two request/reply pairs that CROSS links - that crossing is the whole mechanism:</para>
/// <code>
///   W->A 0x13BE  on the MAIN link       "this player wants continent 9781"
///   A->W 0x13BF  on the OWNER's link    <- WorldForContinent decides this
///   W->A 0x13C5  on the OWNER's link    the owner registers a channel
///   W->A 0x13C0  on the OWNER's link    ready
///   A->W 0x13C1  on the MAIN link       <- back to whoever asked
///   W->A 0x2711  on the OWNER's link    SDB_USER_ENTERWORLD, carrying the channel
///   ... play ...
///   W->A 0x13C6  on the OWNER's link    channel released
///   W->A 0x2711  on the MAIN link       re-entry, channel 0
/// </code>
/// <para>Both replies are near-copies, which is what makes this implementable without knowing
/// what the 200-odd bytes mean: 0x13BF is 8 fixed bytes plus the whole of 0x13BE from offset 8
/// on (verified on all three captured entries), and 0x13C1 is 0x13C0 byte for byte, 208 B.</para>
/// </summary>
public static class ContinentHandoff
{
    /// <summary>Main World -> Arbiter: a player wants a continent somebody else may own.</summary>
    public const ushort SA_REQUEST_ENTER_CONTINENT = 0x13BE;

    /// <summary>Arbiter -> the OWNING World. The reply that crosses.</summary>
    public const ushort AS_ENTER_CONTINENT = 0x13BF;

    /// <summary>Owning World -> Arbiter: ready.</summary>
    public const ushort SA_CONTINENT_READY = 0x13C0;

    /// <summary>Arbiter -> the link that asked. The second crossing.</summary>
    public const ushort AS_CONTINENT_READY = 0x13C1;

    /// <summary>The 8-byte handle at the head of 0x13BE, and the continent right after it.</summary>
    public const int HandleSize = 8;

    /// <summary>Shortest 0x13BE that still names a continent.</summary>
    public const int MinPayload = 12;

    /// <summary>
    /// What 0x13BF carries where the request carried a session handle: planet 2800, then 1.
    /// Identical in all three captured entries. The 1 is NOT the destination world id (that is 13
    /// in the same capture), so it is copied as the observed constant rather than computed from
    /// something we would be guessing at.
    /// </summary>
    public static readonly byte[] EnterReplyPrefix = { 0xF0, 0x0A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

    /// <summary>The continent a 0x13BE names - it decides which link gets the reply.</summary>
    public static int? ContinentOf(byte[]? request)
        => request == null || request.Length < MinPayload ? null : BitConverter.ToInt32(request, HandleSize);

    /// <summary>
    /// 0x13BE -> 0x13BF. Swap the handle, copy everything else verbatim - destination continent,
    /// spawn coordinates and all. Null for a payload too short to hold the prefix.
    /// </summary>
    public static byte[]? EnterReply(byte[]? request)
    {
        if (request == null || request.Length < HandleSize) return null;
        var reply = new byte[request.Length];
        EnterReplyPrefix.CopyTo(reply, 0);
        Array.Copy(request, HandleSize, reply, HandleSize, request.Length - HandleSize);
        return reply;
    }

    /// <summary>
    /// 0x13C0 -> 0x13C1. The captured pair is 208 B and identical, so this is a pure relay. It is
    /// a named method anyway, because "it is a pure relay" is a fact about this build that a
    /// future capture could contradict.
    /// </summary>
    public static byte[]? ReadyReply(byte[]? ready) => ready == null ? null : (byte[])ready.Clone();

    /// <summary>
    /// The channel number the CLIENT is told in S_CURRENT_CHANNEL, which is the internal channel
    /// PLUS ONE. Four samples settle it: 12 -> 13 in cap_multiworld2, then 13 -> 14, 14 -> 15,
    /// 15 -> 16 across cap_multiworld3's three entries.
    /// </summary>
    public static int ClientChannel(int internalChannel) => internalChannel + 1;

    /// <summary>
    /// The low half of <see cref="DungeonChannels"/>'s packed ChannelId. T138b reconciliation:
    /// 0x13C5's +4 field really is one int32, as the decompile says - but the capture shows it is
    /// PACKED, <c>channel | (planetId &lt;&lt; 16)</c>. cap_multiworld3's first entry is
    /// <c>0D 00 F0 0A</c> = 0x0AF0000D: channel 13, planet 2800. That is the same packing
    /// S_CURRENT_CHANNEL's second int32 uses. DungeonChannels keeps the packed value because it
    /// only ever needs it to be unique; these two helpers are for anything that has to show or
    /// compare the halves.
    /// </summary>
    public static int ChannelOf(int packedChannelId) => packedChannelId & 0xFFFF;

    /// <summary>The high half of the same packed field: the planet id.</summary>
    public static int PlanetOf(int packedChannelId) => (packedChannelId >> 16) & 0xFFFF;
}
