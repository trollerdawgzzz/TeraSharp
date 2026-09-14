using System.Text;
using TeraSharp.Arbiter.Network; // PacketReadException

namespace TeraSharp.Arbiter.Protocol;

/// <summary>
/// Reads a packet body into a Dictionary&lt;string, object&gt; using a parsed
/// <see cref="PacketDef"/> whose field list already has meta-ref headers inserted
/// by <see cref="DefinitionParser"/>.
///
/// Matches the tera-data compiler exactly:
///   - Ref headers come first (per string/bytes/array, in order):
///       string : offset(u16)
///       bytes  : offset(u16) + count(u16)
///       array  : count(u16)  + offset(u16)
///   - Then the fixed primitive fields.
///   - Then variable data, located by the header offsets.
///   - A string is UTF-16LE terminated by a 0x0000 code unit (the offset points
///     at the first char). An array element begins with here(u16)+next(u16); we
///     follow the offset chain via the header offset then each element's own
///     layout (the element records also carry their own ref headers).
///
/// Offsets are absolute from the packet start (include the 4-byte header). This
/// reader sees the BODY only, so bodyIndex = offset - 4.
/// </summary>
public sealed class DefinitionReader
{
    private const int Header = 4;

    /// <summary>
    /// Total array elements one packet may produce, across every array in it. T48.
    ///
    /// <para><b>Why this exists.</b> An array header is <c>[u16 count][u16 offset]</c>, so a
    /// four-byte body can declare 65535 elements, and each element is a full
    /// <see cref="ReadRecord"/> - a Dictionary allocation, plus a string scan per string field.
    /// Worse, elements are chained by a <c>next</c> offset the packet also supplies, and nothing
    /// stopped that chain pointing at itself. <c>C_CHECK_VERSION</c> is an array packet, it is
    /// the FIRST handler registered, and it is reachable BEFORE authentication: the 16-byte body
    /// <c>FF FF 08 00 08 00 08 00 01 00 00 00 01 00 00 00</c> declared 65535 elements whose
    /// <c>next</c> pointed at themselves and cost ~13 MB of allocation per packet - an 850,000x
    /// amplification, pre-auth, repeatable as fast as a socket can write.</para>
    ///
    /// <para>4096 is far above anything the real protocol sends to us (the largest client array
    /// we answer is a 24-entry private-channel invite list) and far below anything that hurts.</para>
    /// </summary>
    public const int MaxElementsPerPacket = 4096;

    private readonly byte[] _body;
    private int _elementBudget = MaxElementsPerPacket;

    public DefinitionReader(ReadOnlySpan<byte> body) => _body = body.ToArray();

    public Dictionary<string, object> Read(PacketDef def)
    {
        int pos = 0;
        _elementBudget = MaxElementsPerPacket;
        return ReadRecord(def.Fields, ref pos);
    }

    private int ToBody(int packetOffset)
    {
        if (packetOffset == 0) return -1;
        int b = packetOffset - Header;
        if (b < 0 || b > _body.Length) return -1;
        return b;
    }

    private Dictionary<string, object> ReadRecord(List<FieldDef> fields, ref int pos)
    {
        var result = new Dictionary<string, object>(fields.Count);

        // refId -> (offset, count) captured from the header block.
        var refs = new Dictionary<int, (int offset, int count)>();

        foreach (var f in fields)
        {
            if (f.IsHeaderFor is FieldKind hk)
            {
                switch (hk)
                {
                    case FieldKind.String:
                    {
                        int offset = ReadU16(ref pos);
                        refs[f.RefId] = (offset, 0);
                        break;
                    }
                    case FieldKind.Bytes:
                    {
                        int offset = ReadU16(ref pos);
                        int count = ReadU16(ref pos);
                        refs[f.RefId] = (offset, count);
                        break;
                    }
                    case FieldKind.Array:
                    {
                        int count = ReadU16(ref pos);
                        int offset = ReadU16(ref pos);
                        refs[f.RefId] = (offset, count);
                        break;
                    }
                }
                continue;
            }

            switch (f.Kind)
            {
                case FieldKind.String:
                {
                    var (offset, _) = refs.TryGetValue(f.RefId, out var r) ? r : (0, 0);
                    result[f.Name] = ReadStringAt(ToBody(offset));
                    break;
                }
                case FieldKind.Bytes:
                {
                    var (offset, count) = refs.TryGetValue(f.RefId, out var r) ? r : (0, 0);
                    result[f.Name] = ReadBytesAt(ToBody(offset), count);
                    break;
                }
                case FieldKind.Array:
                {
                    var (offset, count) = refs.TryGetValue(f.RefId, out var r) ? r : (0, 0);
                    result[f.Name] = ReadArray(f, count, offset);
                    break;
                }
                case FieldKind.Object:
                    result[f.Name] = ReadRecord(f.Children, ref pos);
                    break;
                default:
                    result[f.Name] = ReadPrimitive(f.Kind, ref pos);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Walk an array's element chain. Three things here are defensive and all three matter (T48,
    /// status/SECURITY-AUDIT.md):
    /// <list type="number">
    /// <item><b>The list is never pre-sized from the count.</b> <c>new List&lt;object&gt;(count)</c>
    /// allocated 512 KB for a four-byte body that declared 65535 and then went nowhere.</item>
    /// <item><b>Every element offset must be new.</b> The chain is packet-supplied; without this
    /// an element whose <c>next</c> points at itself is walked <c>count</c> times.</item>
    /// <item><b>A shared per-packet budget.</b> Nesting multiplies: an array of records that each
    /// contain an array would otherwise be count^depth, and neither of the first two rules bounds
    /// the product on its own.</item>
    /// </list>
    /// Hitting any limit truncates the array and stops - it does not throw, because a short read
    /// is what every other malformed-packet path here does and the handler above copes with an
    /// empty list.
    /// </summary>
    private List<object> ReadArray(FieldDef arrayField, int count, int firstOffset)
    {
        var list = new List<object>();
        if (count <= 0) return list;

        // An element is at least its own here+next header, so the body itself caps the count.
        int maxByBody = _body.Length / 4;
        if (count > maxByBody) count = maxByBody;

        HashSet<int>? seen = null;
        int elemBody = ToBody(firstOffset);
        for (int i = 0; i < count && elemBody >= 0; i++)
        {
            if (_elementBudget <= 0) break;
            _elementBudget--;

            seen ??= new HashSet<int>();
            if (!seen.Add(elemBody)) break;          // the chain looped back

            int pos = elemBody;
            ushort here = ReadU16(ref pos);
            ushort next = ReadU16(ref pos);
            _ = here;

            if (arrayField.ElementKind is FieldKind ek)
                list.Add(ReadPrimitive(ek, ref pos));
            else
                list.Add(ReadRecord(arrayField.Children, ref pos));

            elemBody = ToBody(next);
        }

        return list;
    }

    private object ReadPrimitive(FieldKind kind, ref int pos)
    {
        switch (kind)
        {
            case FieldKind.Byte: return ReadU8(ref pos);
            case FieldKind.Bool: return ReadU8(ref pos) != 0;
            case FieldKind.Int16: return (short)ReadU16(ref pos);
            case FieldKind.UInt16: return ReadU16(ref pos);
            case FieldKind.Int32: return ReadI32(ref pos);
            case FieldKind.UInt32:
            case FieldKind.SkillId: return (uint)ReadI32(ref pos);
            case FieldKind.Int64: return ReadI64(ref pos);
            case FieldKind.UInt64: return (ulong)ReadI64(ref pos);
            case FieldKind.Float: return ReadF32(ref pos);
            case FieldKind.Double: return BitConverter.Int64BitsToDouble(ReadI64(ref pos));
            case FieldKind.Angle: return (short)ReadU16(ref pos);
            case FieldKind.Vec3:
            case FieldKind.Vec3Fa:
            {
                float x = ReadF32(ref pos), y = ReadF32(ref pos), z = ReadF32(ref pos);
                return new float[] { x, y, z };
            }
            case FieldKind.Customize: return ReadBytesAtCursor(ref pos, 8);
            case FieldKind.Unknown: return (uint)ReadI32(ref pos);
            default: throw new PacketReadException($"cannot read primitive of kind {kind}");
        }
    }

    private void Need(int at, int n)
    {
        if (at < 0 || at + n > _body.Length)
            throw new PacketReadException($"read of {n} at {at} exceeds body {_body.Length}");
    }

    private byte ReadU8(ref int pos) { Need(pos, 1); return _body[pos++]; }

    private ushort ReadU16(ref int pos)
    {
        Need(pos, 2);
        ushort v = (ushort)(_body[pos] | (_body[pos + 1] << 8));
        pos += 2; return v;
    }

    private int ReadI32(ref int pos)
    {
        Need(pos, 4);
        int v = _body[pos] | (_body[pos + 1] << 8) | (_body[pos + 2] << 16) | (_body[pos + 3] << 24);
        pos += 4; return v;
    }

    private long ReadI64(ref int pos)
    {
        Need(pos, 8);
        long v = 0;
        for (int i = 0; i < 8; i++) v |= (long)_body[pos + i] << (8 * i);
        pos += 8; return v;
    }

    private float ReadF32(ref int pos)
    {
        Need(pos, 4);
        float v = BitConverter.ToSingle(_body, pos);
        pos += 4; return v;
    }

    private byte[] ReadBytesAtCursor(ref int pos, int n)
    {
        Need(pos, n);
        var b = new byte[n];
        Array.Copy(_body, pos, b, 0, n);
        pos += n; return b;
    }

    private string ReadStringAt(int offset)
    {
        if (offset < 0 || offset >= _body.Length) return string.Empty;
        int end = -1;
        for (int i = offset; i + 1 < _body.Length; i += 2)
            if (_body[i] == 0 && _body[i + 1] == 0) { end = i; break; }
        if (end < 0) return string.Empty;
        return Encoding.Unicode.GetString(_body, offset, end - offset);
    }

    private byte[] ReadBytesAt(int offset, int count)
    {
        if (count == 0 || offset < 0) return Array.Empty<byte>();
        if (offset + count > _body.Length)
            throw new PacketReadException($"bytes at {offset}+{count} exceed body {_body.Length}");
        var b = new byte[count];
        Array.Copy(_body, offset, b, 0, count);
        return b;
    }
}
