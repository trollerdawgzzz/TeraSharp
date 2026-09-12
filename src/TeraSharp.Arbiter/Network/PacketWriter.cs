using System.Buffers.Binary;
using System.Text;

namespace TeraSharp.Arbiter.Network;

/// <summary>
/// Builds an outgoing server packet: a growable body you append fields to, then
/// <see cref="ToPacket"/> prepends the 4-byte header (ushort size, ushort opcode)
/// and returns the framed bytes ready to encrypt+send.
///
/// Little-endian throughout, matching the client. Offset-referenced strings
/// (TERA's layout) aren't needed for the simple early packets; when we reach
/// packets with strings/arrays we'll extend this with the offset-table logic.
/// </summary>
public sealed class PacketWriter
{
    private byte[] _buf;
    private int _len;

    public PacketWriter(int initialCapacity = 64)
    {
        _buf = new byte[Math.Max(16, initialCapacity)];
        _len = 0;
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int n = _buf.Length * 2;
        while (n < _len + extra) n *= 2;
        Array.Resize(ref _buf, n);
    }

    public PacketWriter WriteByte(byte v)
    {
        Ensure(1);
        _buf[_len++] = v;
        return this;
    }

    public PacketWriter WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    public PacketWriter WriteInt16(short v)
    {
        Ensure(2);
        BinaryPrimitives.WriteInt16LittleEndian(_buf.AsSpan(_len), v);
        _len += 2;
        return this;
    }

    public PacketWriter WriteUInt16(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len), v);
        _len += 2;
        return this;
    }

    public PacketWriter WriteInt32(int v)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
        return this;
    }

    public PacketWriter WriteUInt32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
        return this;
    }

    public PacketWriter WriteInt64(long v)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_len), v);
        _len += 8;
        return this;
    }

    public PacketWriter WriteSingle(float v)
    {
        Ensure(4);
        BitConverter.TryWriteBytes(_buf.AsSpan(_len), v);
        _len += 4;
        return this;
    }

    public PacketWriter WriteBytes(ReadOnlySpan<byte> v)
    {
        Ensure(v.Length);
        v.CopyTo(_buf.AsSpan(_len));
        _len += v.Length;
        return this;
    }

    /// <summary>
    /// Finalise: prepend the 4-byte header (total size, opcode) and return the
    /// complete framed packet. Total size includes the header itself.
    /// </summary>
    public byte[] ToPacket(ushort opcode)
    {
        int total = _len + 4;
        if (total > ushort.MaxValue)
            throw new InvalidOperationException($"packet too large: {total} bytes");

        var packet = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0), (ushort)total);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        Array.Copy(_buf, 0, packet, 4, _len);
        return packet;
    }
}
