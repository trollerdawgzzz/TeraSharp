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
