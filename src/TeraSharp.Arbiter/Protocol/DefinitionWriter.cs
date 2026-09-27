// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Buffers.Binary;

namespace TeraSharp.Arbiter.Protocol;

/// <summary>How wide a frame header and its ref offsets are. T209b.</summary>
public enum PacketFraming
{
    /// <summary>Client packets: [u16 length][u16 opcode], u16 offsets, packet-relative base 4.</summary>
    Client,

    /// <summary>
    /// Arbiter&lt;-&gt;World frames: [u32 length][u16 opcode], u32 offsets, base 6. The record model
    /// is the same - ref-header block, fixed fields, variable data - which is why the handshake
    /// burst's two empty-list forms fall out of this writer byte for byte: an empty array is
    /// <c>count=0, offset=0</c>, and an empty bytes field is <c>offset=&lt;end of the fixed
    /// part&gt;, length=0</c> - `0e 00 00 00 00 00 00 00` for a frame whose fixed part is the
    /// header alone, `16 00 00 00 00 00 00 00` for 0x14D1, which has a u64 after it.
    /// </summary>
    InterServer,
}

/// <summary>
/// Writes a field dictionary to a packet body. Mirrors the tera-data compiler:
/// per record, emit the ref-header block, then fixed fields, then variable data,
/// backpatching offsets. Offsets are packet-relative - base 4 and two bytes wide for client
/// framing, base 6 and four bytes wide for inter-server framing.
/// </summary>
public sealed class DefinitionWriter
{
    private readonly int _header;
    private readonly int _offsetWidth;
    private readonly List<byte> _buf = new(256);

    public DefinitionWriter(PacketFraming framing = PacketFraming.Client)
    {
        _header = framing == PacketFraming.InterServer ? 6 : 4;
        _offsetWidth = framing == PacketFraming.InterServer ? 4 : 2;
    }

    /// <summary>Which framing this writer was built for.</summary>
    public PacketFraming Framing => _offsetWidth == 4 ? PacketFraming.InterServer : PacketFraming.Client;

    /// <summary>If set, records "field@packetOffset" for each emitted field.</summary>
    public List<string>? Trace;

    public byte[] Write(PacketDef def, IReadOnlyDictionary<string, object> data)
    {
        WriteRecord(def.Fields, data);
        return _buf.ToArray();
    }

    private int PacketOffset => _buf.Count + _header;

    private void T(string name) => Trace?.Add($"{name}@{PacketOffset}");

    private void WriteRecord(List<FieldDef> fields, IReadOnlyDictionary<string, object> data)
    {
        var offsetSlot = new Dictionary<int, int>();

        // Phase 1: ref headers.
        foreach (var f in fields)
        {
            if (f.IsHeaderFor is not FieldKind hk) continue;
            switch (hk)
            {
                case FieldKind.String:
                    T($"HDR.str.{f.Name}");
                    offsetSlot[f.RefId] = _buf.Count;
                    WriteOffset(0);
                    break;
                case FieldKind.Bytes:
                    T($"HDR.bytes.{f.Name}");
                    offsetSlot[f.RefId] = _buf.Count;
                    WriteOffset(0); WriteOffset(0);
                    break;
                case FieldKind.Array:
                    T($"HDR.arr.{f.Name}");
                    WriteOffset(0);
                    offsetSlot[f.RefId] = _buf.Count;
                    WriteOffset(0);
                    break;
            }
        }

        // Phase 2: fixed fields.
        var dataFields = new List<FieldDef>();
        foreach (var f in fields)
        {
            if (f.IsHeaderFor is not null) continue;
            switch (f.Kind)
            {
                case FieldKind.String:
                case FieldKind.Bytes:
                case FieldKind.Array:
                    dataFields.Add(f);
                    break;
                case FieldKind.Object:
                    T($"OBJ.{f.Name}");
                    WriteRecord(f.Children, GetDict(data, f.Name));
                    break;
                default:
                    T(f.Name);
                    WritePrimitive(f.Kind, Get(data, f.Name));
                    break;
            }
        }

        // Phase 3: variable data.
        foreach (var f in dataFields)
        {
            switch (f.Kind)
            {
                case FieldKind.String:
                    PatchOffset(offsetSlot[f.RefId], PacketOffset);
                    T($"DATA.str.{f.Name}");
                    WriteStringData(Get(data, f.Name) as string ?? string.Empty);
                    break;
                case FieldKind.Bytes:
                {
                    var bytes = Get(data, f.Name) as byte[] ?? Array.Empty<byte>();
                    int slot = offsetSlot[f.RefId];
                    PatchOffset(slot, PacketOffset);
                    PatchOffset(slot + _offsetWidth, bytes.Length);
                    T($"DATA.bytes.{f.Name}");
                    _buf.AddRange(bytes);
                    break;
                }
                case FieldKind.Array:
                    T($"DATA.arr.{f.Name}");
                    WriteArray(f, data, offsetSlot[f.RefId]);
                    break;
            }
        }
    }

    private void WriteArray(FieldDef arrayField, IReadOnlyDictionary<string, object> parent, int offsetSlotPos)
    {
        var items = new List<object>();
        if (Get(parent, arrayField.Name) is System.Collections.IEnumerable e)
            foreach (var it in e) items.Add(it);

        PatchOffset(offsetSlotPos - _offsetWidth, items.Count);
        if (items.Count == 0) { PatchOffset(offsetSlotPos, 0); return; }

        PatchOffset(offsetSlotPos, PacketOffset);

        for (int i = 0; i < items.Count; i++)
        {
            T($"ELEM[{i}].here");
            WriteOffset(PacketOffset);
            int nextSlot = _buf.Count;
            WriteOffset(0);

            if (arrayField.ElementKind is FieldKind ek)
                WritePrimitive(ek, items[i]);
            else
                WriteRecord(arrayField.Children, items[i] as IReadOnlyDictionary<string, object> ?? new Dictionary<string, object>());

            PatchOffset(nextSlot, i < items.Count - 1 ? PacketOffset : 0);
        }
    }

    private void WritePrimitive(FieldKind kind, object? value)
    {
        switch (kind)
        {
            case FieldKind.Byte: WriteU8(ToByte(value)); break;
            case FieldKind.Bool: WriteU8((value is bool b && b) ? (byte)1 : (byte)0); break;
            case FieldKind.Int16: WriteU16((ushort)ToLong(value)); break;
            case FieldKind.UInt16: WriteU16((ushort)ToLong(value)); break;
            case FieldKind.Int32: WriteI32((int)ToLong(value)); break;
            case FieldKind.UInt32:
            case FieldKind.SkillId: WriteI32(unchecked((int)ToULong(value))); break;
            case FieldKind.Int64: WriteI64(ToLong(value)); break;
            case FieldKind.UInt64: WriteI64(unchecked((long)ToULong(value))); break;
            case FieldKind.Float: WriteF32(ToFloat(value)); break;
            case FieldKind.Double: WriteI64(BitConverter.DoubleToInt64Bits(ToDouble(value))); break;
            case FieldKind.Angle: WriteU16((ushort)ToLong(value)); break;
            case FieldKind.Vec3:
            case FieldKind.Vec3Fa:
            {
                var v = value as float[] ?? new float[3];
                WriteF32(v.Length > 0 ? v[0] : 0);
                WriteF32(v.Length > 1 ? v[1] : 0);
                WriteF32(v.Length > 2 ? v[2] : 0);
                break;
            }
            case FieldKind.Customize:
            {
                var c = value as byte[] ?? new byte[8];
                for (int i = 0; i < 8; i++) WriteU8(i < c.Length ? c[i] : (byte)0);
                break;
            }
            case FieldKind.Unknown: WriteI32((int)ToLong(value)); break;
            default: throw new InvalidOperationException($"cannot write primitive kind {kind}");
        }
    }

    private static object? Get(IReadOnlyDictionary<string, object> d, string name)
        => d.TryGetValue(name, out var v) ? v : null;
    private static IReadOnlyDictionary<string, object> GetDict(IReadOnlyDictionary<string, object> d, string name)
        => d.TryGetValue(name, out var v) && v is IReadOnlyDictionary<string, object> dd ? dd : new Dictionary<string, object>();

    private static long ToLong(object? v) => v switch
    {
        null => 0, bool b => b ? 1 : 0,
        byte x => x, sbyte x => x, short x => x, ushort x => x,
        int x => x, uint x => x, long x => x, ulong x => (long)x,
        float x => (long)x, double x => (long)x, _ => 0,
    };
    private static ulong ToULong(object? v) => v switch
    {
        null => 0, byte x => x, sbyte x => (ulong)x, short x => (ulong)x, ushort x => x,
        int x => (ulong)x, uint x => x, long x => (ulong)x, ulong x => x,
        float x => (ulong)x, double x => (ulong)x, _ => 0,
    };
    private static byte ToByte(object? v) => (byte)ToLong(v);
    private static float ToFloat(object? v) => v is float f ? f : (float)ToDouble(v);
    private static double ToDouble(object? v) => v switch { null => 0, float x => x, double x => x, _ => ToLong(v) };

    private void WriteU8(byte v) => _buf.Add(v);
    private void WriteU16(ushort v) { _buf.Add((byte)v); _buf.Add((byte)(v >> 8)); }
    private void WriteI32(int v) { _buf.Add((byte)v); _buf.Add((byte)(v >> 8)); _buf.Add((byte)(v >> 16)); _buf.Add((byte)(v >> 24)); }
    private void WriteI64(long v) { for (int i = 0; i < 8; i++) _buf.Add((byte)(v >> (8 * i))); }
    private void WriteF32(float v)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(tmp, v);
        _buf.Add(tmp[0]); _buf.Add(tmp[1]); _buf.Add(tmp[2]); _buf.Add(tmp[3]);
    }
    private void WriteStringData(string s)
    {
        foreach (char ch in s) { _buf.Add((byte)ch); _buf.Add((byte)(ch >> 8)); }
        _buf.Add(0); _buf.Add(0);
    }
    private void PatchU16(int at, ushort v) { _buf[at] = (byte)v; _buf[at + 1] = (byte)(v >> 8); }

    /// <summary>A ref offset/length/count slot: two bytes for client framing, four for inter-server.</summary>
    private void WriteOffset(int v)
    {
        if (_offsetWidth == 2) WriteU16((ushort)v); else WriteI32(v);
    }

    private void PatchOffset(int at, int v)
    {
        if (_offsetWidth == 2) { PatchU16(at, (ushort)v); return; }
        _buf[at] = (byte)v; _buf[at + 1] = (byte)(v >> 8);
        _buf[at + 2] = (byte)(v >> 16); _buf[at + 3] = (byte)(v >> 24);
    }
}
