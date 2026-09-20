using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// ContinentRouting - which linked World owns which continent, and the cross-link dungeon
// hand-off. T138 parts 1 and 2. Research and the frame-by-frame trace: status/MULTIWORLD-DESIGN.md
// section T137c.
//
// Ground truth: D:\packetlogs\cap_multiworld3.log, 19046 frames once the coalesced
// [u32 len][u16 op] stream is split. The player entered Velik's Sanctuary (9781) three times from
// the main World and came back each time. Two links were live: #11 (main, world id 0) and
// #14/#16 (the DungeonServer, world id 13).
//
// THE HAND-OFF IS TWO REQUEST/REPLY PAIRS THAT CROSS:
//
//     W->A  0x13BE  on the MAIN link      "this player wants continent 9781"
//     A->W  0x13BF  on the OWNER's link   <- the routing decision lives here
//     W->A  0x13C5  on the OWNER's link   the owner registers a channel
//     W->A  0x13C0  on the OWNER's link   "ready"
//     A->W  0x13C1  on the MAIN link      <- back to whoever asked
//     W->A  0x2711  on the OWNER's link   SDB_USER_ENTERWORLD, carrying the channel
//     ... play ...
//     W->A  0x13C6  on the OWNER's link   channel released
//     W->A  0x2711  on the MAIN link      re-entry, channel 0
//
// Both replies are near-copies, which is what makes this implementable without knowing what the
// 200-odd bytes mean:
//   * 0x13BF = 8 fixed bytes + the whole of 0x13BE from offset 8 on. Verified on all three
//     entries; the prefix is F0 0A 00 00 01 00 00 00 every time.
//   * 0x13C1 = 0x13C0 byte for byte, 208 B, opcode swapped. A pure relay.
// RELATIONSHIP TO DungeonRouting (World/WorldInstances.cs). That file already owns the POLICY
// and PLUMBING half - DungeonChannels, DungeonTransfers, RouteRequest/RouteResponse/Dispatch,
// WorldForEnterWorld, StampTicket - and status/MULTIWORLD-PATCH.diff calls into it. This file
// is the WIRE half: the byte layouts, pinned to cap_multiworld3, plus the continent -> link
// table built from what the Worlds actually announce. They are meant to meet at
// WorldBridge.HandleFrame, which is NOT wired yet. Before adding a third home for any of
// this, fold one into the other - two routing tables that disagree is the failure mode.
// =============================================================================================
public static class ContinentRouting
{
    /// <summary>SA_REGISTER_WORLD: a World announcing its id on link-up.</summary>
    public const ushort SA_REGISTER_WORLD = 0x294E;

    /// <summary>The continent roster a World sends after registering.</summary>
    public const ushort SA_WORLD_CONTINENT_LIST = 0x164D;

    /// <summary>Main World -> Arbiter: "this player wants that continent".</summary>
    public const ushort SA_REQUEST_ENTER_CONTINENT = 0x13BE;

    /// <summary>Arbiter -> the OWNING World. The reply that crosses links.</summary>
    public const ushort AS_ENTER_CONTINENT = 0x13BF;

    /// <summary>Owning World -> Arbiter: ready.</summary>
    public const ushort SA_CONTINENT_READY = 0x13C0;

    /// <summary>Arbiter -> the link that asked. The second crossing.</summary>
    public const ushort AS_CONTINENT_READY = 0x13C1;

    /// <summary>Owning World -> Arbiter: a dungeon channel now exists.</summary>
    public const ushort SA_ADD_DUNGEON_CHANNEL = 0x13C5;

    /// <summary>Owning World -> Arbiter: that channel is gone.</summary>
    public const ushort SA_DEL_DUNGEON_CHANNEL = 0x13C6;

    /// <summary>
    /// The 8 bytes 0x13BF carries where 0x13BE carried a session handle. Identical in all three
    /// captured entries: planet 2800, then 1. The 1 is NOT the destination world id (that is 13
    /// in the same capture), so it is copied as the constant it is observed to be rather than
    /// computed from something we would be guessing at.
    /// </summary>
    public static readonly byte[] EnterContinentPrefix = { 0xF0, 0x0A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

    /// <summary>The main World's id. Anything it does not own still routes to it (catch-all).</summary>
    public const int MainWorldId = 0;

    /// <summary>One row of a World's continent roster.</summary>
    public readonly record struct ContinentRange(int MinLevel, int MaxLevel, int ContinentId);

    /// <summary>What <see cref="SA_WORLD_CONTINENT_LIST"/> declares.</summary>
    public sealed class WorldRoster
    {
        public int WorldId { get; init; }
        public int PlanetId { get; init; }
        public IReadOnlyList<ContinentRange> Continents { get; init; } = Array.Empty<ContinentRange>();
    }

    /// <summary>A live dungeon channel, as 0x13C5 announced it.</summary>
    public readonly record struct DungeonChannel(int ContinentId, int Channel, int PlanetId, int Link);

    private static readonly object Lock = new();
    private static readonly Dictionary<int, int> WorldIdByLink = new();        // link -> world id
    private static readonly Dictionary<int, int> LinkByContinent = new();      // continent -> link
    private static readonly List<DungeonChannel> Channels = new();

    // ---- parsers, pinned to the capture -------------------------------------------------

    /// <summary>
    /// SA_REGISTER_WORLD. Two shapes in cap_multiworld3: the main World sends 24 bytes
    /// (<c>01 00 00 00 12 00 00 00 00 00 00 00 12 00 00 00 00 00 00 00 01 00 00 00</c>) and a
    /// DungeonServer sends 12 (<c>00 00 00 00 00 00 00 00 | u32 worldId</c>, 12 on link #4 and 13
    /// on #9/#14/#16). Returns the id, or null when the body is neither shape.
    /// </summary>
    public static int? ReadServerId(ReadOnlySpan<byte> body)
    {
        if (body.Length == 12) return BitConverter.ToInt32(body.Slice(8, 4));
        if (body.Length >= 24) return MainWorldId;
        return null;
    }

    /// <summary>
    /// SA_WORLD_CONTINENT_LIST:
    /// <code>
    ///   0   u32 count / 4 u32 unk (22 every time) / 8 u32 planetId / 12 u32 worldId
    ///   16  count x 16 B: u32 minLevel / u32 maxLevel / u32 continentId / u32 unk
    /// </code>
    /// <para>Verified to consume the body exactly on all three links: main 173 rows in 2784 B,
    /// world 13 34 rows in 560 B, world 12 5 rows in 96 B. 9781 is in world 13's roster and in
    /// neither of the others, which is the whole of part 1.</para>
    /// </summary>
    public static WorldRoster? ReadRoster(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16) return null;
        int count = BitConverter.ToInt32(body[..4]);
        int planet = BitConverter.ToInt32(body.Slice(8, 4));
        int world = BitConverter.ToInt32(body.Slice(12, 4));
        if (count < 0 || 16 + count * 16 > body.Length) return null;
        var rows = new ContinentRange[count];
        for (int i = 0; i < count; i++)
        {
            int o = 16 + i * 16;
            rows[i] = new ContinentRange(
                BitConverter.ToInt32(body.Slice(o, 4)),
                BitConverter.ToInt32(body.Slice(o + 4, 4)),
                BitConverter.ToInt32(body.Slice(o + 8, 4)));
        }
        return new WorldRoster { WorldId = world, PlanetId = planet, Continents = rows };
    }

    // ---- the registry ---------------------------------------------------------------------

    /// <summary>Drops every link and channel. Tests, and a full Arbiter restart.</summary>
    public static void Reset()
    {
        lock (Lock) { WorldIdByLink.Clear(); LinkByContinent.Clear(); Channels.Clear(); }
    }

    /// <summary>A World announced itself on a link (0x294E).</summary>
    public static void RegisterLink(int link, int worldId)
    {
        lock (Lock) WorldIdByLink[link] = worldId;
    }

    /// <summary>
    /// A World declared what it owns (0x164D). Later registrations win, which is what a
    /// reconnect needs: cap_multiworld3's DungeonServer came back as #9, then #14, then #16, and
    /// the newest link is the live one.
    /// </summary>
    public static void RegisterRoster(int link, WorldRoster roster)
    {
        if (roster == null) return;
        lock (Lock)
        {
            WorldIdByLink[link] = roster.WorldId;
            foreach (var c in roster.Continents) LinkByContinent[c.ContinentId] = link;
        }
    }

    /// <summary>The world id a link announced, or null.</summary>
    public static int? WorldIdFor(int link)
    {
        lock (Lock) return WorldIdByLink.TryGetValue(link, out int w) ? w : null;
    }

    /// <summary>
    /// The link that owns a continent. **Catch-all is the main World**: a continent nobody
    /// claimed is the main World's, which is what keeps a single-World deployment behaving
    /// exactly as it does today - with one link registered, every continent resolves to it.
    /// Returns null only when no link has registered at all.
    /// </summary>
    public static int? LinkForContinent(int continentId)
    {
        lock (Lock)
        {
            if (LinkByContinent.TryGetValue(continentId, out int link)) return link;
            foreach (var (l, w) in WorldIdByLink) if (w == MainWorldId) return l;
            return null;
        }
    }

    /// <summary>True when the continent is served by a link other than the one asking.</summary>
    public static bool IsCrossWorld(int continentId, int askingLink)
    {
        int? owner = LinkForContinent(continentId);
        return owner != null && owner.Value != askingLink;
    }

    // ---- dungeon channels -------------------------------------------------------------------

    /// <summary>
    /// 0x13C5: <c>i32 continentId / u16 channel / u16 planetId</c> + 24 B tail. cap_multiworld3
    /// registers channel 13, then 14, then 15 for 9781 - the counter increments per entry and is
    /// never reused inside a session.
    /// </summary>
    public static DungeonChannel? ReadAddChannel(ReadOnlySpan<byte> body, int link)
    {
        if (body.Length < 8) return null;
        return new DungeonChannel(
            BitConverter.ToInt32(body[..4]),
            BitConverter.ToUInt16(body.Slice(4, 2)),
            BitConverter.ToUInt16(body.Slice(6, 2)),
            link);
    }

    /// <summary>0x13C6: the same three fields and nothing else, 8 B of body.</summary>
    public static (int ContinentId, int Channel)? ReadDelChannel(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) return null;
        return (BitConverter.ToInt32(body[..4]), BitConverter.ToUInt16(body.Slice(4, 2)));
    }

    /// <summary>Record a channel 0x13C5 announced.</summary>
    public static void AddChannel(DungeonChannel channel)
    {
        lock (Lock)
        {
            Channels.RemoveAll(c => c.ContinentId == channel.ContinentId && c.Channel == channel.Channel);
            Channels.Add(channel);
        }
    }

    /// <summary>Release one 0x13C6 named. True when it was there.</summary>
    public static bool ReleaseChannel(int continentId, int channel)
    {
        lock (Lock) return Channels.RemoveAll(c => c.ContinentId == continentId && c.Channel == channel) > 0;
    }

    /// <summary>The newest live channel for a continent, or null.</summary>
    public static DungeonChannel? NewestChannel(int continentId)
    {
        lock (Lock)
        {
            for (int i = Channels.Count - 1; i >= 0; i--)
                if (Channels[i].ContinentId == continentId) return Channels[i];
            return null;
        }
    }

    /// <summary>Every live channel, for tests and the admin view.</summary>
    public static IReadOnlyList<DungeonChannel> LiveChannels()
    {
        lock (Lock) return new List<DungeonChannel>(Channels);
    }

    /// <summary>
    /// The number the CLIENT is told in S_CURRENT_CHANNEL, which is the internal channel PLUS
    /// ONE. Four samples settle it: 12 -> 13 (cap_multiworld2), then 13 -> 14, 14 -> 15, 15 -> 16
    /// across cap_multiworld3's three entries. T137b guessed "the server id" off the single
    /// sample where 13 happened to be both; it is an off-by-one, not an id.
    /// </summary>
    public static int ClientChannel(int internalChannel) => internalChannel + 1;

    // ---- the two crossing replies -----------------------------------------------------------

    /// <summary>
    /// 0x13BE -> 0x13BF. The first 8 bytes of the request are a session handle; the reply
    /// replaces them with <see cref="EnterContinentPrefix"/> and copies the rest verbatim -
    /// destination continent, spawn coordinates and all. Returns null for a body too short to
    /// hold the prefix.
    /// </summary>
    public static byte[]? BuildEnterContinentBody(ReadOnlySpan<byte> requestBody)
    {
        if (requestBody.Length < 8) return null;
        var reply = new byte[requestBody.Length];
        EnterContinentPrefix.CopyTo(reply, 0);
        requestBody[8..].CopyTo(reply.AsSpan(8));
        return reply;
    }

    /// <summary>
    /// The continent the request names, which is what picks the destination link. It sits
    /// immediately after the 8-byte handle.
    /// </summary>
    public static int? ContinentInRequest(ReadOnlySpan<byte> requestBody)
        => requestBody.Length < 12 ? null : BitConverter.ToInt32(requestBody.Slice(8, 4));

    /// <summary>
    /// 0x13C0 -> 0x13C1. Byte for byte: the captured pair is 208 B and identical, so the reply is
    /// the request with a different opcode. Kept as a named method anyway, because "it is a pure
    /// relay" is a fact about this build that a future capture could contradict.
    /// </summary>
    public static byte[] BuildContinentReadyBody(ReadOnlySpan<byte> readyBody) => readyBody.ToArray();
}
