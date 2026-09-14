namespace TeraSharp.Arbiter.World;

/// <summary>
/// One addressee of an <c>SA_BYPASS_TO_CLIENT</c> frame: 16 bytes of the UserList.
///
/// <para><paramref name="Ticket"/> is the ClientSession-table index
/// <c>PacketBypassManager::BypassStart</c> handed out, and is what TeraSharp routes on.
/// <paramref name="Sequence"/> is the per-Ticket ordering counter, stored shifted left 19 bits.
/// <paramref name="Unread"/> is a field the Arbiter never reads (it is non-zero on the wire -
/// 95 and 178 in the two captured samples - so it is not padding; it is simply not ours).</para>
/// </summary>
public readonly record struct TunnelRecipient(uint PlanetId, uint Unread, uint Ticket, uint Sequence);

/// <summary>A parsed <c>SA_BYPASS_TO_CLIENT</c>: who it is for, and the client packet itself.</summary>
public sealed record BypassToClient(IReadOnlyList<TunnelRecipient> Recipients, byte[] ClientPacket);

/// <summary>
/// The two tunnel frames, as pure functions over bytes - the half of
/// status/MULTIPLAYER-DESIGN.md section 6 that is not in a human-owned file. WorldBridge keeps
/// the sockets, the reorder buffers and the session map; this file knows the layouts.
///
/// <para><b>W-&gt;A, SA_BYPASS_TO_CLIENT (0x13F7).</b> Payload:</para>
/// <code>
/// [0]  u32 userListOffset   frame-relative, always 22  -> payload 16
/// [4]  u32 userListBytes    16 * recipients
/// [8]  u32 packetOffset     frame-relative, 22 + 16N   -> payload 16 + 16N
/// [12] u32 packetLength
/// [16] UserList[N], 16 B each:
///        [+0]  u32 planetId       2800 in every captured frame
///        [+4]  u32 (never read by the Arbiter)
///        [+8]  u32 ticket
///        [+12] u32 sequence &lt;&lt; 19
/// [16+16N] the client packet, itself [u16 len][u16 opcode][body]
/// </code>
///
/// <para><b>A-&gt;W, AS_BYPASS_FROM_CLIENT (0x13F6).</b> Payload:</para>
/// <code>
/// [0]  u32 packetOffset     frame-relative, always 30  -> payload 24
/// [4]  u32 packetLength
/// [8]  u64 worldClient      User+0x4038, the value SA_ENTER_WORLD gave us (== gameId here)
/// [16] u64 sendTick
/// [24] the client packet
/// </code>
///
/// <para><b>Verified mechanically, not by eye.</b> Every 0x13F7 and 0x13F6 in
/// <c>cap_newchar.log</c> and <c>arb_world_2026-09-13T11-33-30-680Z.log</c> was reframed by the
/// u32 length and checked: 4169 x 0x13F7 (userListOffset 22, userListBytes 16, packetOffset-6 ==
/// 16+userListBytes, planetId 2800, sequence low 19 bits always zero) and 606 x 0x13F6
/// (packetOffset 30, 24+packetLength == payload length). Zero exceptions. Six of them are in
/// <c>data/cap_t38.bin</c> for the tests.</para>
///
/// <para>Every captured frame has exactly ONE recipient, because no capture has two players in
/// it. The N-recipient shape is the Arbiter's own (the dumper at Arb_part_016.c:8636 prints a
/// list, and the handler at Arb_part_062.c:3736 refcounts one packet by recipient count), which
/// is why the two-recipient tests here are synthetic and say so.</para>
/// </summary>
public static class TunnelFrames
{
    /// <summary>The [u32 length][u16 opcode] every Arbiter-World frame starts with. Offsets
    /// inside a payload that point at the frame include it.</summary>
    public const int FrameHeaderSize = 6;

    /// <summary>Fixed part of an SA_BYPASS_TO_CLIENT payload, before the UserList.</summary>
    public const int BypassToClientHeaderSize = 16;

    /// <summary>One UserList entry.</summary>
    public const int UserListEntrySize = 16;

    /// <summary>Fixed part of an AS_BYPASS_FROM_CLIENT payload, before the client packet.</summary>
    public const int BypassToWorldHeaderSize = 24;

    /// <summary>Sequence numbers live in the top 13 bits of the entry's last u32.</summary>
    public const int SequenceShift = 19;

    /// <summary>Sanity cap. The real limit is however many players can see each other.</summary>
    public const int MaxRecipients = 64;

    /// <summary>PlanetId in every captured frame; an entry for another planet is not ours.</summary>
    public const uint DefaultPlanetId = 2800;

    /// <summary>
    /// The real Arbiter refuses to tunnel a client packet of 0x1F41 bytes or more and kicks the
    /// session instead (Handler_CA_Default, Arb_part_040.c:16700).
    /// </summary>
    public const int MaxTunnelledClientPacket = 0x1F41;

    // Field offsets, so callers never spell a magic number.
    public const int UserListOffsetField = 0;
    public const int UserListBytesField = 4;
    public const int PacketOffsetField = 8;
    public const int PacketLengthField = 12;
    public const int EntryPlanetIdOffset = 0;
    public const int EntryUnreadOffset = 4;
    public const int EntryTicketOffset = 8;
    public const int EntrySequenceOffset = 12;

    // ---- W->A ----

    /// <summary>
    /// Parse an <c>SA_BYPASS_TO_CLIENT</c> payload. Returns null for anything malformed - a
    /// short payload, a recipient count outside 1..<see cref="MaxRecipients"/>, or a
    /// packet offset/length that does not fit - so the caller logs and drops rather than
    /// throwing on a hostile or truncated frame.
    ///
    /// <para>The client packet is a fresh array. Callers that deliver to more than one session
    /// must clone it again per session: GameSession.Send encrypts in place with a stateful
    /// cipher, so two sessions sharing one buffer corrupt each other's stream.</para>
    ///
    /// <para><see cref="TunnelRecipient.PlanetId"/> is parsed, never filtered: TeraSharp serves
    /// one planet (2800, the value in every captured frame) and dropping is the caller's policy,
    /// not the parser's. A multi-planet deployment would ignore entries that are not its own.</para>
    /// </summary>
    public static BypassToClient? ParseBypassToClient(byte[]? payload)
    {
        if (payload == null || payload.Length < BypassToClientHeaderSize) return null;

        int userListBytes = BitConverter.ToInt32(payload, UserListBytesField);
        if (userListBytes <= 0 || userListBytes % UserListEntrySize != 0) return null;
        int count = userListBytes / UserListEntrySize;
        if (count > MaxRecipients) return null;

        int listStart = BitConverter.ToInt32(payload, UserListOffsetField) - FrameHeaderSize;
        if (listStart != BypassToClientHeaderSize) return null;      // 22 - 6 in every frame
        if (listStart + userListBytes > payload.Length) return null;

        int packetStart = BitConverter.ToInt32(payload, PacketOffsetField) - FrameHeaderSize;
        int packetLength = BitConverter.ToInt32(payload, PacketLengthField);
        if (packetStart != listStart + userListBytes) return null;   // the invariant the capture proves
        if (packetLength < 0 || packetLength > payload.Length - packetStart) return null;   // no int overflow (T54 H1)

        var recipients = new TunnelRecipient[count];
        for (int i = 0; i < count; i++)
        {
            int b = listStart + i * UserListEntrySize;
            recipients[i] = new TunnelRecipient(
                BitConverter.ToUInt32(payload, b + EntryPlanetIdOffset),
                BitConverter.ToUInt32(payload, b + EntryUnreadOffset),
                BitConverter.ToUInt32(payload, b + EntryTicketOffset),
                BitConverter.ToUInt32(payload, b + EntrySequenceOffset) >> SequenceShift);
        }

        var clientPacket = new byte[packetLength];
        Array.Copy(payload, packetStart, clientPacket, 0, packetLength);
        return new BypassToClient(recipients, clientPacket);
    }

    /// <summary>
    /// Build an <c>SA_BYPASS_TO_CLIENT</c> payload. World writes these, not us - this exists so
    /// the tests can round-trip the captured frames and build the two-recipient shape no capture
    /// has yet.
    /// </summary>
    public static byte[] BuildBypassToClient(byte[] clientPacket, params TunnelRecipient[] recipients)
    {
        ArgumentNullException.ThrowIfNull(clientPacket);
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Length == 0 || recipients.Length > MaxRecipients)
            throw new ArgumentOutOfRangeException(nameof(recipients),
                $"1..{MaxRecipients} recipients, got {recipients.Length}");

        int userListBytes = recipients.Length * UserListEntrySize;
        int listStart = BypassToClientHeaderSize;
        int packetStart = listStart + userListBytes;
        var payload = new byte[packetStart + clientPacket.Length];

        BitConverter.GetBytes(listStart + FrameHeaderSize).CopyTo(payload, UserListOffsetField);
        BitConverter.GetBytes(userListBytes).CopyTo(payload, UserListBytesField);
        BitConverter.GetBytes(packetStart + FrameHeaderSize).CopyTo(payload, PacketOffsetField);
        BitConverter.GetBytes(clientPacket.Length).CopyTo(payload, PacketLengthField);

        for (int i = 0; i < recipients.Length; i++)
        {
            int b = listStart + i * UserListEntrySize;
            var r = recipients[i];
            BitConverter.GetBytes(r.PlanetId).CopyTo(payload, b + EntryPlanetIdOffset);
            BitConverter.GetBytes(r.Unread).CopyTo(payload, b + EntryUnreadOffset);
            BitConverter.GetBytes(r.Ticket).CopyTo(payload, b + EntryTicketOffset);
            BitConverter.GetBytes(r.Sequence << SequenceShift).CopyTo(payload, b + EntrySequenceOffset);
        }
        clientPacket.CopyTo(payload, packetStart);
        return payload;
    }

    /// <summary>A recipient with the planet every captured frame carries.</summary>
    public static TunnelRecipient To(uint ticket, uint sequence = 0, uint unread = 0)
        => new(DefaultPlanetId, unread, ticket, sequence);

    // ---- A->W ----

    /// <summary>
    /// Build an <c>AS_BYPASS_FROM_CLIENT</c> payload: what TeraSharp sends for every client
    /// packet it has no handler for.
    ///
    /// <para><b>There is no ticket in this direction.</b> The frame addresses the user by
    /// <paramref name="worldClient"/> - the u64 the dumper calls <c>WorldClient</c>
    /// (Arb_part_011.c:6532), taken from <c>User+0x4038</c>, which is the value
    /// <c>SA_ENTER_WORLD</c> handed us. It holds the same number as GameId in this build. The
    /// Ticket only exists in the W-&gt;A direction, where it indexes the ClientSession table.</para>
    /// </summary>
    public static byte[] BuildBypassToWorld(ulong worldClient, byte[] clientPacket, ulong sendTick)
    {
        ArgumentNullException.ThrowIfNull(clientPacket);
        var payload = new byte[BypassToWorldHeaderSize + clientPacket.Length];
        BitConverter.GetBytes(BypassToWorldHeaderSize + FrameHeaderSize).CopyTo(payload, 0);
        BitConverter.GetBytes(clientPacket.Length).CopyTo(payload, 4);
        BitConverter.GetBytes(worldClient).CopyTo(payload, 8);
        BitConverter.GetBytes(sendTick).CopyTo(payload, 16);
        clientPacket.CopyTo(payload, BypassToWorldHeaderSize);
        return payload;
    }

    /// <summary>Whether the real Arbiter would tunnel this packet at all, or kick instead.</summary>
    public static bool IsTunnellable(byte[]? clientPacket)
        => clientPacket != null && clientPacket.Length > 0 && clientPacket.Length < MaxTunnelledClientPacket;

    /// <summary>The inverse of <see cref="BuildBypassToWorld"/>, for the tests and for reading a capture.</summary>
    public static bool TryParseBypassToWorld(
        byte[]? payload, out ulong worldClient, out ulong sendTick, out byte[] clientPacket)
    {
        worldClient = 0; sendTick = 0; clientPacket = Array.Empty<byte>();
        if (payload == null || payload.Length < BypassToWorldHeaderSize) return false;
        int start = BitConverter.ToInt32(payload, 0) - FrameHeaderSize;
        int length = BitConverter.ToInt32(payload, 4);
        if (start != BypassToWorldHeaderSize || length < 0 || start + length > payload.Length) return false;
        worldClient = BitConverter.ToUInt64(payload, 8);
        sendTick = BitConverter.ToUInt64(payload, 16);
        clientPacket = new byte[length];
        Array.Copy(payload, start, clientPacket, 0, length);
        return true;
    }
}

/// <summary>
/// Hands out the per-session Ticket that <c>SA_BYPASS_TO_CLIENT</c> routes on.
///
/// <para>Mirrors <c>PacketBypassManager::BypassStart</c> (FUN_1405afaf0, Arb_part_048.c:9296):
/// a monotonic cursor over a <c>maxUsers * 4</c> slot table, linear-probing past anything still
/// live. Two consequences the tests pin: a live Ticket is never handed out twice, and a freed
/// Ticket is not reused immediately - the cursor has moved on - so a relog does not collide with
/// the frames still in flight for the session that just left.</para>
///
/// <para>The real Arbiter's cursor starts at 0 (captures: cap_newchar 0; lobby_tap 0,1; the
/// 09-13 relog 0,1,2). TeraSharp starts at 5 because that is the value the single-session path
/// has always sent in AS_ENTER_WORLD[80] and World accepts it; only uniqueness matters.</para>
/// </summary>
public sealed class TicketAllocator
{
    /// <summary>What TeraSharp's pinned <c>AllocateTunnelKey</c> returned before T38.</summary>
    public const uint DefaultFirstTicket = 5;

    /// <summary>maxUsers * 4 in the real Arbiter; the cursor wraps here.</summary>
    public const int DefaultTableSize = 4096;

    private readonly object _gate = new();
    private readonly HashSet<uint> _live = new();
    private readonly int _tableSize;
    private readonly uint _first;
    private long _cursor;

    public TicketAllocator(uint firstTicket = DefaultFirstTicket, int tableSize = DefaultTableSize)
    {
        if (tableSize <= 0) throw new ArgumentOutOfRangeException(nameof(tableSize));
        if (firstTicket >= tableSize) throw new ArgumentOutOfRangeException(nameof(firstTicket));
        _tableSize = tableSize;
        _first = firstTicket;
        _cursor = firstTicket;
    }

    /// <summary>How many tickets are checked out.</summary>
    public int LiveCount { get { lock (_gate) return _live.Count; } }

    /// <summary>Is this ticket checked out right now?</summary>
    public bool IsLive(uint ticket) { lock (_gate) return _live.Contains(ticket); }

    /// <summary>
    /// Take the next free ticket. Throws when every slot is live, which cannot happen below
    /// <see cref="DefaultTableSize"/> concurrent players.
    /// </summary>
    public uint Allocate()
    {
        lock (_gate)
        {
            if (_live.Count >= _tableSize)
                throw new InvalidOperationException($"all {_tableSize} tunnel tickets are in use");
            for (int probe = 0; probe < _tableSize; probe++)
            {
                uint candidate = (uint)(_cursor % _tableSize);
                _cursor++;
                if (_live.Add(candidate)) return candidate;
            }
            throw new InvalidOperationException("no free tunnel ticket found");   // unreachable
        }
    }

    /// <summary>
    /// Give a ticket back, on SA_LEAVE_WORLD. The cursor is deliberately NOT rewound, so the
    /// number is not reissued until it wraps - late frames for the session that left cannot be
    /// delivered to whoever logs in next.
    /// </summary>
    public bool Free(uint ticket) { lock (_gate) return _live.Remove(ticket); }

    /// <summary>Forget every ticket - World restarted, so its session table is gone too.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _live.Clear();
            _cursor = _first;
        }
    }
}
