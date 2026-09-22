// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T103. SA_REGISTER (0x138A) and AS_REGISTER (0x138B) - the first thing every Arbiter&lt;-&gt;World
/// socket says, and the only place the WorldId appears on the wire.
///
/// <para>Until now the reply came out of the capture replay table, which meant every link - on
/// every World - was handed world 0's captured answer, BypassIndex and all. That is the first of
/// the eight single-World assumptions in status/MULTIWORLD-DESIGN.md section 4, and it is the one
/// that has to go before a second World can connect at all.</para>
///
/// <para>Layouts are the Arbiter's own PDL dumpers; the behaviour is
/// <c>Handler_SA_REGISTER(class WorldServerSession *, const unsigned char *, int)</c>
/// (Arb_part_062.c). Offsets below are PAYLOAD-relative - WorldLink hands the handler the frame
/// minus its 6-byte <c>[u32 len][u16 op]</c> header, so payload index = frame offset - 6.</para>
/// </summary>
public readonly record struct WorldRegistrationInfo(
    bool IsBypass, int PlanetId, int WorldId, int TotalBypassCount, int BypassIndex,
    int WorldVersion);

public static class WorldRegistration
{
    public const ushort SA_REGISTER = 0x138A;
    public const ushort AS_REGISTER = 0x138B;

    /// <summary>27-byte frame, 21-byte payload. The handler's own guard is <c>param_3 &lt; 0x1b</c>,
    /// and a short one is the "Arbiter &lt;-&gt; World PDL Version Mismatch! Bye :(" path.</summary>
    public const int RequestPayloadSize = 27 - 6;

    /// <summary>23-byte frame, 17-byte payload.</summary>
    public const int ReplyPayloadSize = 23 - 6;

    /// <summary>
    /// The version the real Arbiter compares against, as a literal in the handler
    /// (<c>if (WorldVersion == 0x5bc07)</c>). It is NOT an echo: the whole point of the field is
    /// that a World built against another PDL gets Result 0 and disconnects, and echoing would
    /// hide exactly that. The capture cannot tell the two apart - our World is 0x5BC07 too.
    /// </summary>
    public const int ArbiterVersion = 0x0005BC07;      // 375815

    /// <summary>
    /// <c>if (WorldId &lt; 0x20)</c> in the handler, and the highest id in ServerConfig.xml's
    /// WorldServerList is 31. A higher id still gets a reply - with Result 0.
    /// </summary>
    public const int MaxWorldId = 0x20;

    /// <summary>The control link's BypassIndex. The 25 registrations in the tap are one link with
    /// -1 (IsBypass 0) and 24 with 0..23 (IsBypass 1).</summary>
    public const int ControlBypassIndex = -1;

    /// <summary>
    /// T108. The World a link belongs to before it has registered, and the World everything that
    /// predates multi-World means when it says "the World": id 0, the main
    /// <c>&lt;WorldServer id="0" loadAllContinents="true"&gt;</c>. A single-World server never
    /// leaves this id, which is what keeps every existing test byte-identical.
    /// </summary>
    public const int DefaultWorldId = 0;

    /// <summary>
    /// Parse SA_REGISTER's payload:
    /// <code>
    ///   +0x00 u8  IsBypass          +0x01 i32 PlanetId     +0x05 i32 WorldId
    ///   +0x09 i32 TotalBypassCount  +0x0D i32 BypassIndex  +0x11 i32 WorldVersion
    /// </code>
    /// <para><c>TotalBypassCount</c> is only meaningful on the control link - the real handler
    /// reads +0x09 solely in the <c>IsBypass == 0</c> branch, where it hands the count to the
    /// world registry so it knows how many bypass sockets to expect.</para>
    /// </summary>
    public static WorldRegistrationInfo? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < RequestPayloadSize) return null;
        return new WorldRegistrationInfo(
            IsBypass: payload[0] != 0,
            PlanetId: BitConverter.ToInt32(payload[1..]),
            WorldId: BitConverter.ToInt32(payload[5..]),
            TotalBypassCount: BitConverter.ToInt32(payload[9..]),
            BypassIndex: BitConverter.ToInt32(payload[13..]),
            WorldVersion: BitConverter.ToInt32(payload[17..]));
    }

    /// <summary>
    /// Result 1 when the World's PDL version matches ours and the id is one we could route to.
    /// The handler logs "Unknown WorldServer [id=%d]" for an id that is not in its configured
    /// WorldServerList but carries on regardless, so an unconfigured id is NOT a refusal here -
    /// only the version and the 0x20 ceiling are.
    /// </summary>
    public static int ResultFor(WorldRegistrationInfo info)
        => info.WorldVersion == ArbiterVersion && (uint)info.WorldId < MaxWorldId ? 1 : 0;

    /// <summary>
    /// Build AS_REGISTER's payload:
    /// <code>
    ///   +0x00 u8  IsBypass    +0x01 i32 WorldId   +0x05 i32 BypassIndex
    ///   +0x09 i32 ArbiterVersion                  +0x0D i32 Result
    /// </code>
    /// The first three are echoed from the request - the link is being told which of its own
    /// sockets this is. Verified byte-for-byte against all 25 pairs in
    /// <c><captures>\arb_world_2026-09-13T11-33-30-680Z.log</c> (frames 17/18 .. 25 links).
    /// </summary>
    public static byte[] Build(bool isBypass, int worldId, int bypassIndex, int result,
                               int arbiterVersion = ArbiterVersion)
    {
        var p = new byte[ReplyPayloadSize];
        p[0] = (byte)(isBypass ? 1 : 0);
        BitConverter.GetBytes(worldId).CopyTo(p, 1);
        BitConverter.GetBytes(bypassIndex).CopyTo(p, 5);
        BitConverter.GetBytes(arbiterVersion).CopyTo(p, 9);
        BitConverter.GetBytes(result).CopyTo(p, 13);
        return p;
    }

    /// <summary>The whole answer in one call: parse, decide, build. Null in means a short frame,
    /// which the real Arbiter treats as a version mismatch and we log and drop.</summary>
    public static byte[] Reply(WorldRegistrationInfo info)
        => Build(info.IsBypass, info.WorldId, info.BypassIndex, ResultFor(info));
}

/// <summary>
/// T103. One instance of <typeparamref name="T"/> per WorldId, created on first use.
///
/// <para>Built for the ticket space: a Ticket indexes a World's own bypass slots, so two Worlds
/// hand out the same numbers and a single global allocator would give two players in different
/// Worlds the same tunnel key. Kept generic so nothing here has to know what a TicketAllocator
/// is - WorldBridge supplies the factory.</para>
///
/// <para>Single-World behaviour is unchanged: world 0 gets a fresh instance on its first
/// allocation, so the first ticket is still what it always was.</para>
/// </summary>
public sealed class PerWorld<T>
{
    private readonly Func<T> _factory;
    private readonly Dictionary<int, T> _byWorld = new();
    private readonly object _lock = new();

    public PerWorld(Func<T> factory) => _factory = factory;

    /// <summary>The instance for this World, creating it if this is its first.</summary>
    public T For(int worldId)
    {
        lock (_lock)
        {
            if (!_byWorld.TryGetValue(worldId, out var v))
            {
                v = _factory();
                _byWorld[worldId] = v;
            }
            return v;
        }
    }

    /// <summary>Whether this World has one yet - for logging and for tests.</summary>
    public bool Has(int worldId) { lock (_lock) return _byWorld.ContainsKey(worldId); }

    /// <summary>How many Worlds have one. One in a single-World server.</summary>
    public int Count { get { lock (_lock) return _byWorld.Count; } }

    /// <summary>Drop a World's instance when its last link goes away.</summary>
    public bool Forget(int worldId) { lock (_lock) return _byWorld.Remove(worldId); }
}

/// <summary>
/// T108. The per-World half of what <c>WorldBridge</c> keeps as three process-wide fields today:
/// the startup-handshake flag, the game-id counter it resets, and whether
/// <c>DbProxy.OnWorldReady</c> has already fired.
///
/// <para>Why it has to be per World: <c>IsReady</c> is set by the first <c>0x294F</c> on any
/// link and that same branch zeroes <c>_gameIdSeq</c>. A dungeon World finishing its handshake
/// an hour after the main World would therefore restart the main World's game-id counter under
/// live players and hand the next two logins ids that are already in use
/// (status/MULTIWORLD-DESIGN.md section 4 item 3).</para>
///
/// <para>Single-World behaviour is unchanged to the byte: world 0's runtime is created on first
/// use with <c>IsReady == false</c> and the counter at 0, so the first game id is still
/// <c>0x80000AF00001</c>.</para>
/// </summary>
public sealed class WorldRuntime
{
    /// <summary>The high half of every game id. gameId = this | a per-World counter.</summary>
    public const ulong GameIdBase = 0x80000AF00000UL;

    private int _gameIdSeq;

    /// <summary>True once this World has completed the startup handshake and can take players.</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// The handshake finished. Returns true exactly once per ready transition, which is the
    /// <c>if (op == OpHandshakeDone &amp;&amp; !IsReady)</c> guard WorldBridge has today - so
    /// <c>DbProxy.OnWorldReady</c> still fires once per World, not once per link.
    /// </summary>
    public bool MarkReady()
    {
        if (IsReady) return false;
        IsReady = true;
        Interlocked.Exchange(ref _gameIdSeq, 0);
        return true;
    }

    /// <summary>Every link of this World went away.</summary>
    public void MarkDisconnected() => IsReady = false;

    /// <summary>The next game id for this World.</summary>
    public ulong AllocateGameId() => GameIdBase | (ulong)(uint)Interlocked.Increment(ref _gameIdSeq);

    /// <summary>How many ids this World has handed out. For the status tab and for tests.</summary>
    public int IssuedGameIds => Volatile.Read(ref _gameIdSeq);
}
