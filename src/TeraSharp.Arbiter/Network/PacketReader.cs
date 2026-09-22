// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;

namespace TeraSharp.Arbiter.Network;

/// <summary>
/// Safe, bounds-checked reader for incoming client packets.
///
/// This is the single most important defensive component in the server. Every
/// crash we found in the original Arbiter (guild-war OOB read, visited-section
/// unbounded index, ranking bad-lookup) came from the C++ handlers trusting
/// client data and reading past buffer/collection bounds. Here, EVERY read is
/// bounds-checked: a malformed, truncated, or oversized packet throws a
/// <see cref="PacketReadException"/> that the dispatcher catches and turns into
/// a dropped packet + a log line - never a process crash.
///
/// TERA packet body layout: a 4-byte header (ushort size, ushort opcode) is
/// stripped before the body reaches a handler, so this reader operates on the
/// body only. Offsets here are body-relative (the decompiled handlers read at
/// +4, +8, ... which is header+body; subtract 4 to get the body offset).
///
/// Strings in TERA are offset-referenced: a ushort "offset" field elsewhere in
/// the packet points to a null-terminated UTF-16LE string later in the body.
/// ReadOffsetString validates that offset stays in-bounds.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _body;
    private int _pos;

    public PacketReader(ReadOnlySpan<byte> body)
    {
        _body = body;
        _pos = 0;
    }

    public int Position => _pos;
    public int Length => _body.Length;
    public int Remaining => _body.Length - _pos;

    private void Need(int bytes)
    {
        if (bytes < 0 || _pos + bytes > _body.Length)
            throw new PacketReadException(
                $"read of {bytes} byte(s) at offset {_pos} exceeds body length {_body.Length}");
    }

    public byte ReadByte()
    {
        Need(1);
        return _body[_pos++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public short ReadInt16()
    {
        Need(2);
        short v = (short)(_body[_pos] | (_body[_pos + 1] << 8));
        _pos += 2;
        return v;
    }

    public ushort ReadUInt16()
    {
        Need(2);
        ushort v = (ushort)(_body[_pos] | (_body[_pos + 1] << 8));
        _pos += 2;
        return v;
    }

    public int ReadInt32()
    {
        Need(4);
        int v = _body[_pos] | (_body[_pos + 1] << 8) | (_body[_pos + 2] << 16) | (_body[_pos + 3] << 24);
        _pos += 4;
        return v;
    }

    public uint ReadUInt32() => (uint)ReadInt32();

    public long ReadInt64()
    {
        Need(8);
        long v = 0;
        for (int i = 0; i < 8; i++)
            v |= (long)_body[_pos + i] << (8 * i);
        _pos += 8;
        return v;
    }

    public ulong ReadUInt64() => (ulong)ReadInt64();

    public float ReadSingle()
    {
        Need(4);
        float v = BitConverter.ToSingle(_body.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    /// <summary>
    /// Read a value at an absolute body offset without moving the cursor.
    /// Useful for the offset-based fields the client protocol uses.
    /// </summary>
    public int ReadInt32At(int offset)
    {
        if (offset < 0 || offset + 4 > _body.Length)
            throw new PacketReadException(
                $"absolute read at offset {offset} exceeds body length {_body.Length}");
        return _body[offset] | (_body[offset + 1] << 8) | (_body[offset + 2] << 16) | (_body[offset + 3] << 24);
    }

    /// <summary>
    /// Read a TERA offset-referenced UTF-16LE string. The current cursor holds a
    /// ushort offset (body-relative) to a null-terminated wide string. A zero
    /// offset or one that runs past the body is treated as empty rather than a
    /// crash - matching the original's defensive intent but doing it safely.
    /// </summary>
    public string ReadOffsetString()
    {
        ushort offset = ReadUInt16();
        if (offset == 0 || offset >= _body.Length)
            return string.Empty;

        // Walk UTF-16LE code units until a null terminator or end of body.
        int i = offset;
        int end = -1;
        while (i + 1 < _body.Length)
        {
            if (_body[i] == 0 && _body[i + 1] == 0)
            {
                end = i;
                break;
            }
            i += 2;
        }
        if (end < 0)
            return string.Empty; // no terminator in-bounds -> treat as empty, don't overread

        return Encoding.Unicode.GetString(_body.Slice(offset, end - offset));
    }
}

/// <summary>Thrown when a packet is malformed/truncated. Caught by the dispatcher.</summary>
public sealed class PacketReadException : Exception
{
    public PacketReadException(string message) : base(message) { }
}
