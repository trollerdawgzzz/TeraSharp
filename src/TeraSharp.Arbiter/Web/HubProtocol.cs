// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Text;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// HubProtocol - T207. The wire format of the TERA platform "hub" socket, as tera-api speaks it.
//
// tera-api's shop delivers a purchase by connecting to HUB_HOST:HUB_PORT (its .env; 11001 on
// this stack, where the retail arb_gw_tw2_log.exe used to answer) and calling three functions:
// CreateServiceItem, CreateBox and BoxNotiUser. Benefits (elite) ride the same socket as
// AddBenefit / RemoveBenefit. TeraSharp answers that socket itself, so arb_gw is not needed and
// tera-api stays stock.
//
// FRAME (tera-api src/lib/hubConnection.js send/recv):
//     [u16 size][u16 msgId][protobuf body]        size counts itself: 2 + 2 + body
// msgId is the hub function: 1 RegisterReq, 2 RegisterAns, 3 SendMessageReq, 4 SendMessageAns,
// 5 RecvMessageReq, 6 PingReq, 7 PingAns, 8 ServerEvent. tera-api SENDS 1, 3 and 7 and expects
// 2, 4, 5, 6 and 8.
//
// A call is SendMessageReq { jobId, serverId, msgBuf }, where msgBuf is itself
// [u16 innerId][protobuf]. The answer comes back as RecvMessageReq with the SAME jobId and its
// own [u16 innerId][protobuf]; tera-api matches on jobId and decodes with the Ans type it asked
// for, so the answer's innerId is never read. SendMessageAns only matters when it reports
// failure (result false), which is how a call is refused.
//
// Field numbers below are read out of tera-api's generated protobuf modules (lib/protobuf/
// hub.js, opArb.js, opUent.js) - the client we have to satisfy. The argument NAMES of the box
// calls are the same ones the retail Arbiter's own box code builds (Arb_part_077.c: boxSN,
// boxItemSNs, receiverUserSN, boxStateCode, endActivationDateTime), which is the cross-check
// that these are the retail shapes and not tera-api inventions.
// =============================================================================================
public static class HubProtocol
{
    public const int MaxFrame = 0xFFFF;

    /// <summary>Hub function ids, from tera-api's hubFunctionMap.</summary>
    public const ushort RegisterReq = 1, RegisterAns = 2, SendMessageReq = 3, SendMessageAns = 4,
        RecvMessageReq = 5, PingReq = 6, PingAns = 7, ServerEvent = 8;

    /// <summary>Inner ids on the OpArb channel (hubFunctions.js comments).</summary>
    public const ushort OpMsg = 1, KickUserReq = 2, KickUserAns = 3, SendMsgReq = 4, SendMsgAns = 5,
        BulkKickReq = 6, BulkKickAns = 7, BoxNotiUserReq = 15, BoxNotiUserAns = 16,
        AddBenefitReq = 38, AddBenefitAns = 39, RemoveBenefitReq = 40, RemoveBenefitAns = 41;

    /// <summary>Inner ids on the OpUent channel, which is addressed to <see cref="UserEntityGusid"/>.</summary>
    public const ushort QueryUserReq = 1, QueryUserAns = 2;

    /// <summary>gusid = category &lt;&lt; 24 | number (tera-api lib/teraPlatformGuid.js).</summary>
    public static uint Gusid(int category, int number) => (uint)((category & 0xFF) << 24 | (number & 0xFFFFFF));

    /// <summary>The number half of a gusid - the success test for an opmsg result code.</summary>
    public static int GusidNumber(uint gusid) => (int)(gusid & 0xFFFFFF);

    public const int CategoryArbiterGw = 0, CategoryWebCsTool = 19, CategoryBoxApi = 16;
    public const uint UserEntityGusid = 0xFF000000;
    /// <summary>(16 &lt;&lt; 24) + 1 - what the box calls are addressed to.</summary>
    public const uint BoxApiGusid = 0x10000001;

    /// <summary>Box-API function numbers, the low half of an opmsg gufid (hubFunctions.js).</summary>
    public const int FnCreateBox = 107, FnGetPageServiceItem = 115, FnGetServiceItem = 116,
        FnCreateServiceItem = 117, FnSetDisableServiceItem = 118;

    // ---------------------------------------------------------------------------- framing

    /// <summary>
    /// One complete frame out of a receive buffer, size prefix included - exactly what
    /// <see cref="Frame"/> produces and what <c>HubServer.Handle</c> takes - or null when fewer
    /// than <c>size</c> bytes have arrived yet. <paramref name="consumed"/> is how much of the
    /// buffer the frame took.
    ///
    /// <para>T207b: this used to strip the size prefix while Handle() read the function id from
    /// byte 0. The two disagreed, so every call read its id out of the size field, matched
    /// nothing and was answered with silence. Frame in, frame out - one shape everywhere.</para>
    /// </summary>
    public static byte[]? ReadFrame(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < 2) return null;
        int size = buffer[0] | buffer[1] << 8;
        if (size < 4 || size > MaxFrame) throw new InvalidDataException("hub frame size " + size);
        if (buffer.Length < size) return null;
        consumed = size;
        return buffer[..size].ToArray();
    }

    /// <summary>The hub function id of a whole frame: the u16 after the size prefix.</summary>
    public static ushort FrameId(ReadOnlySpan<byte> frame)
        => frame.Length < 4 ? (ushort)0 : (ushort)(frame[2] | frame[3] << 8);

    /// <summary>The protobuf body of a whole frame - everything after size and function id.</summary>
    public static byte[] FrameBody(ReadOnlySpan<byte> frame)
        => frame.Length < 4 ? Array.Empty<byte>() : frame[4..].ToArray();

    /// <summary>A frame for the wire: the size prefix, the function id, then the body.</summary>
    public static byte[] Frame(ushort msgId, ReadOnlySpan<byte> body)
    {
        int size = 4 + body.Length;
        if (size > MaxFrame) throw new ArgumentOutOfRangeException(nameof(body));
        var frame = new byte[size];
        frame[0] = (byte)size; frame[1] = (byte)(size >> 8);
        frame[2] = (byte)msgId; frame[3] = (byte)(msgId >> 8);
        body.CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// <summary>The [u16 innerId] an inner message carries in front of its protobuf.</summary>
    public static ushort InnerId(ReadOnlySpan<byte> msgBuf)
        => msgBuf.Length < 2 ? (ushort)0 : (ushort)(msgBuf[0] | msgBuf[1] << 8);

    /// <summary>The protobuf half of an inner message.</summary>
    public static byte[] InnerBody(ReadOnlySpan<byte> msgBuf)
        => msgBuf.Length < 2 ? Array.Empty<byte>() : msgBuf[2..].ToArray();

    /// <summary>An inner message: its id, then its protobuf.</summary>
    public static byte[] Inner(ushort innerId, ReadOnlySpan<byte> body)
    {
        var buf = new byte[2 + body.Length];
        buf[0] = (byte)innerId; buf[1] = (byte)(innerId >> 8);
        body.CopyTo(buf.AsSpan(2));
        return buf;
    }

    // ---------------------------------------------------------------------------- protobuf

    /// <summary>A protobuf field as it arrives: the number, the wire type and the raw value.</summary>
    public readonly record struct Field(int Number, int WireType, ulong Varint, byte[] Bytes);

    /// <summary>
    /// Every field in a protobuf message, in order. Repeated fields simply appear more than
    /// once. Unknown fields are returned rather than skipped, so a caller can log them.
    /// </summary>
    public static List<Field> Parse(ReadOnlySpan<byte> body)
    {
        var fields = new List<Field>();
        int at = 0;
        while (at < body.Length)
        {
            ulong tag = Varint(body, ref at);
            int number = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    fields.Add(new(number, wire, Varint(body, ref at), Array.Empty<byte>()));
                    break;
                case 1:
                    Need(body, at, 8);
                    fields.Add(new(number, wire, BitConverter.ToUInt64(body[at..(at + 8)]), Array.Empty<byte>()));
                    at += 8;
                    break;
                case 5:
                    Need(body, at, 4);
                    fields.Add(new(number, wire, BitConverter.ToUInt32(body[at..(at + 4)]), Array.Empty<byte>()));
                    at += 4;
                    break;
                case 2:
                    int len = (int)Varint(body, ref at);
                    if (len < 0) throw new InvalidDataException("protobuf length");
                    Need(body, at, len);
                    fields.Add(new(number, wire, (ulong)len, body[at..(at + len)].ToArray()));
                    at += len;
                    break;
                default:
                    throw new InvalidDataException("protobuf wire type " + wire);
            }
        }
        return fields;
    }

    private static void Need(ReadOnlySpan<byte> body, int at, int count)
    {
        if (at < 0 || count < 0 || at + count > body.Length) throw new InvalidDataException("protobuf truncated");
    }

    private static ulong Varint(ReadOnlySpan<byte> body, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (at >= body.Length) throw new InvalidDataException("protobuf truncated varint");
            byte b = body[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("protobuf varint too long");
    }

    /// <summary>The first field with this number, or null.</summary>
    public static Field? First(List<Field> fields, int number)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var f in fields) if (f.Number == number) return f;
        return null;
    }

    /// <summary>A scalar field's value, or <paramref name="fallback"/> when it is absent.</summary>
    public static ulong Scalar(List<Field> fields, int number, ulong fallback = 0)
        => First(fields, number) is { } f ? f.Varint : fallback;

    /// <summary>A length-delimited field's bytes, or empty.</summary>
    public static byte[] Bytes(List<Field> fields, int number)
        => First(fields, number) is { } f ? f.Bytes : Array.Empty<byte>();

    /// <summary>A protobuf writer that only has to produce the shapes tera-api decodes.</summary>
    public sealed class Writer
    {
        private readonly List<byte> _bytes = new();

        public Writer Fixed32(int number, uint value)
        {
            Tag(number, 5);
            _bytes.Add((byte)value); _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)(value >> 16)); _bytes.Add((byte)(value >> 24));
            return this;
        }

        public Writer Fixed64(int number, ulong value)
        {
            Tag(number, 1);
            for (int i = 0; i < 8; i++) _bytes.Add((byte)(value >> (i * 8)));
            return this;
        }

        public Writer Varint(int number, ulong value)
        {
            Tag(number, 0);
            Raw(value);
            return this;
        }

        public Writer Bool(int number, bool value) => Varint(number, value ? 1u : 0u);

        public Writer Bytes(int number, ReadOnlySpan<byte> value)
        {
            Tag(number, 2);
            Raw((ulong)value.Length);
            foreach (byte b in value) _bytes.Add(b);
            return this;
        }

        /// <summary>A UTF-8 string field. Every hub argument value is ASCII text on the wire.</summary>
        public Writer Text(int number, string value) => Bytes(number, Encoding.UTF8.GetBytes(value ?? string.Empty));

        public byte[] ToArray() => _bytes.ToArray();

        private void Tag(int number, int wire) => Raw((ulong)(number << 3 | wire));

        private void Raw(ulong value)
        {
            while (value >= 0x80) { _bytes.Add((byte)(value | 0x80)); value >>= 7; }
            _bytes.Add((byte)value);
        }
    }

    // ---------------------------------------------------------------------------- opmsg

    /// <summary>
    /// An opmsg call: the function (<c>gufid</c>, field 5) and its named arguments (field 9,
    /// each an Argument of name = 1 / value = 2, both byte strings holding ASCII text).
    /// </summary>
    public static (uint Gufid, uint SenderGusid, Dictionary<string, string> Arguments) ParseOpMsg(byte[] body)
    {
        var fields = Parse(body);
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            if (f.Number != 9 || f.WireType != 2) continue;
            var inner = Parse(f.Bytes);
            string name = Encoding.UTF8.GetString(Bytes(inner, 1));
            if (name.Length == 0) continue;
            args[name] = Encoding.UTF8.GetString(Bytes(inner, 2));
        }
        return ((uint)Scalar(fields, 5), (uint)Scalar(fields, 1), args);
    }

    /// <summary>
    /// An opmsg answer. <c>resultCode</c> (field 10) is a gusid whose NUMBER half is the result:
    /// tera-api's opMsg() resolves only when that number is 0. A scalar answer (a new box or
    /// service-item id) goes in field 11 as text; a table answer goes in field 12.
    /// </summary>
    public static byte[] BuildOpMsgAns(uint gufid, string? resultScalar = null,
        IReadOnlyList<string>? columns = null, IReadOnlyList<IReadOnlyList<string>>? rows = null,
        int resultNumber = 0)
    {
        var w = new Writer()
            .Fixed32(1, BoxApiGusid)                 // senderGusid: the box API answers
            .Fixed32(2, Gusid(CategoryWebCsTool, 0)) // receiverGusid: back to the caller
            .Varint(3, 2)                            // jobType: RESPONSE
            .Fixed32(5, gufid)
            .Varint(6, 1)                            // execType: EXECUTE
            .Fixed32(10, Gusid(CategoryBoxApi, resultNumber));
        if (resultScalar is not null) w.Text(11, resultScalar);
        if (columns is not null)
        {
            var set = new Writer();
            foreach (string c in columns) set.Text(1, c);
            if (rows is not null)
                foreach (var row in rows)
                {
                    var r = new Writer();
                    foreach (string v in row) r.Text(1, v);
                    set.Bytes(2, r.ToArray());
                }
            set.Fixed32(3, (uint)(rows?.Count ?? 0));
            w.Bytes(12, set.ToArray());
        }
        return w.ToArray();
    }

    // ---------------------------------------------------------------------------- box arguments

    /// <summary>One item in a box: the service item it names and how many of it.</summary>
    public readonly record struct BoxItem(long ServiceItemSn, long Count);

    /// <summary>
    /// <c>boxServiceItemInfo</c>, built by tera-api's convertBoxTagValue:
    /// <c>N,serviceItemSN,externalItemKey,tagCount,[tagSN,hexUtf8(value)]...</c> per item, where
    /// item tag 1 is the stack count. Returns an empty list for an empty or malformed string
    /// rather than guessing a count.
    /// </summary>
    public static List<BoxItem> ParseBoxItems(string? info)
    {
        var items = new List<BoxItem>();
        if (string.IsNullOrWhiteSpace(info)) return items;
        string[] parts = info.Split(',');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)) return items;
        int at = 1;
        for (int i = 0; i < count; i++)
        {
            if (at + 2 >= parts.Length) return items;
            if (!long.TryParse(parts[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sn)) return items;
            if (!int.TryParse(parts[at + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tags)) return items;
            at += 3;
            long amount = 1;
            for (int t = 0; t < tags; t++)
            {
                if (at + 1 >= parts.Length) return items;
                bool isCount = parts[at] == "1";
                string value = FromHex(parts[at + 1]);
                if (isCount && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                    && parsed > 0) amount = parsed;
                at += 2;
            }
            items.Add(new(sn, amount));
        }
        return items;
    }

    /// <summary>
    /// <c>boxTagInfo</c>: <c>N,tagSN,hexUtf8(value)</c> per tag. tera-api's box helper writes
    /// 1 = content, 2 = title, 3 = icon.
    /// </summary>
    public static Dictionary<int, string> ParseBoxTags(string? info)
    {
        var tags = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(info)) return tags;
        string[] parts = info.Split(',');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)) return tags;
        int at = 1;
        for (int i = 0; i < count && at + 1 < parts.Length; i++, at += 2)
            if (int.TryParse(parts[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn))
                tags[sn] = FromHex(parts[at + 1]);
        return tags;
    }

    /// <summary>A tag value: UTF-8 bytes as lower-case hex, or the text itself when it is not hex.</summary>
    public static string FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return string.Empty;
        if (hex.Length % 2 != 0) return hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return hex;
            bytes[i] = b;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>An argument as a long, or <paramref name="fallback"/> when it is absent or empty.</summary>
    public static long Number(IReadOnlyDictionary<string, string> args, string name, long fallback = 0)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.TryGetValue(name, out string? text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value : fallback;
    }

    /// <summary>An argument as text, never null.</summary>
    public static string Text(IReadOnlyDictionary<string, string> args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.TryGetValue(name, out string? text) ? text ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// A hub date argument (<c>YYYY-MM-DD HH:mm:ss</c>, tera-api uses moment().format) as Unix
    /// seconds. 0 when it is empty or unparseable, which the caller reads as "no limit".
    /// </summary>
    public static long Timestamp(IReadOnlyDictionary<string, string> args, string name)
    {
        string text = Text(args, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? new DateTimeOffset(when, TimeSpan.Zero).ToUnixTimeSeconds() : 0;
    }
}
