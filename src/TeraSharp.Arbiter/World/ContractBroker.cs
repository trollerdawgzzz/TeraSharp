// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Handlers;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// ContractBroker - the two-party interactions the Arbiter brokers (T60).
// Research, layouts and citations: status/CONTRACT-DESIGN.md.
//
// WHY THIS EXISTS. On 2026-09-15 a party invite between two in-world players did nothing. World
// sent SDB_FETCH_THROUGH_ARBITER_CONTRACT (0x2809) and later SDB_SEND_END (0x280E); T47 had
// sealed both in WorldReplayTable.OneWayFromWorld on the reading that 0x2809 "sends NOTHING at
// all". It does: the handler dispatches on ContractType into one of four FetchWork objects and
// THOSE send - DBS_FETCH (0x280A) to the initiator, and a DBS_ASK (0x280B) fan-out to every
// opponent. T47 stopped at the handler. The four seals are lifted and this file answers instead.
//
// Guild creation is ContractType 10 and is gated on being in a party, so the same seal blocked
// guilds as well as parties.
//
// WHAT THIS IS NOT. It is a matchmaker, not a party manager. The real Arbiter never creates a
// party here either: it brokers the contract, the client accepts, and WORLD then creates the
// party and tells us with SA_JOIN_PARTY (0x1395), which is where PartyManager takes over
// (status/PARTY-DESIGN.md section 6.1). That separation is exactly why PartyManager saw nothing.
//
// The human-owned diff after this file:
//
//   World/WorldBridge.cs   HandleFrame's default: arm, after the GuildWiring line -
//       if (ContractBroker.TryHandleWorldFrame(op, payload)) return;   // T60: the four SDB_ contract opcodes
//
//   Handlers/HandlerRegistry.cs  with the other Reg(...) calls -
//       Reg("C_REPLY_THROUGH_ARBITER_CONTRACT", ContractBroker.ReplyMinBodyLength,
//           (s, b) => ContractBroker.OnClientReply(s, b, misc));
//       Reg("C_ADD_TRADE_BAG", ArbiterClientHandlers.AddTradeBagBodySize,
//           (s, b) => ArbiterClientHandlers.OnAddTradeBag(s, b, misc));
//
// Offsets: every layout in the decompile is FRAME-relative and every parser here takes the
// PAYLOAD, so payload index = frame offset - 6. Same convention as DbProxyStaticData's W->A
// parsers and ParcelDbHandlers.
// =============================================================================================

/// <summary>
/// Brokers the <c>THROUGH_ARBITER_CONTRACT</c> family: 0x2809-0x2810 plus the client-side
/// <c>C_REPLY_THROUGH_ARBITER_CONTRACT</c>. Static, because there is one contract table per
/// Arbiter process.
/// </summary>
public static class ContractBroker
{
    // The one place this file reaches outside itself. Fully qualified because the enclosing
    // namespace is TeraSharp.Arbiter.World and "World" is also the property name.
    private static WorldBridge? Bridge => global::TeraSharp.Arbiter.Program.World;

    // =========================================================================================
    // 1. Opcodes, types, sizes
    // =========================================================================================

    /// <summary>W-&gt;A. The initiator's World asks the Arbiter to broker. Min frame 0x22.</summary>
    public const ushort SDB_FETCH_THROUGH_ARBITER_CONTRACT = 0x2809;
    /// <summary>A-&gt;W, to the initiator. The verdict, with the AskList and an ErrorNo.</summary>
    public const ushort DBS_FETCH_THROUGH_ARBITER_CONTRACT = 0x280A;
    /// <summary>A-&gt;W, one frame per opponent. Carries both names.</summary>
    public const ushort DBS_ASK_THROUGH_ARBITER_CONTRACT = 0x280B;
    /// <summary>W-&gt;A. The opponent's World answers CanContract. Min frame 0x1B.</summary>
    public const ushort SDB_ASK_THROUGH_ARBITER_CONTRACT = 0x280C;
    /// <summary>W-&gt;A. Becomes the client packet S_BEGIN_THROUGH_ARBITER_CONTRACT. Min frame 0x26.</summary>
    public const ushort SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT = 0x280D;
    /// <summary>W-&gt;A. Becomes S_END_THROUGH_ARBITER_CONTRACT plus a 0x280F fan-out. Min frame 0x22.</summary>
    public const ushort SDB_SEND_END_THROUGH_ARBITER_CONTRACT = 0x280E;
    /// <summary>A-&gt;W, one per participant. 22 bytes, no refs.</summary>
    public const ushort DBS_SEND_END_THROUGH_ARBITER_CONTRACT = 0x280F;
    /// <summary>A-&gt;W, to the requestor. 26 bytes, no refs.</summary>
    public const ushort DBS_REPLY_THROUGH_ARBITER_CONTRACT = 0x2810;

    /// <summary>C_REPLY_THROUGH_ARBITER_CONTRACT (23420). Accept / reject, from the client.</summary>
    public const ushort C_REPLY_THROUGH_ARBITER_CONTRACT = 0x5B7C;
    /// <summary>S_BEGIN_THROUGH_ARBITER_CONTRACT (32319). No def file - built by hand.</summary>
    public const ushort S_BEGIN_THROUGH_ARBITER_CONTRACT = 0x7E3F;
    /// <summary>S_END_THROUGH_ARBITER_CONTRACT (51159). No def file - built by hand.</summary>
    public const ushort S_END_THROUGH_ARBITER_CONTRACT = 0xC7D7;

    /// <summary>Party invite - "join my party". <c>ContractPartyFetchWork</c>.</summary>
    public const int TypePartyInvite = 4;
    /// <summary>Party apply - "let me join yours". <c>ContractPartyApplyFetchWork</c>.</summary>
    public const int TypePartyApply = 5;
    /// <summary>Guild creation. <c>CreateGuildFetchWork</c>. Brokered since T76.</summary>
    public const int TypeCreateGuild = 10;
    /// <summary>Trade broker open deal. <c>TradeBrokerOpenDealFetchWork</c>. Refused.</summary>
    public const int TypeTradeBrokerOpenDeal = 0x23;

    /// <summary>ErrorNo 0: the contract was brokered.</summary>
    public const int ErrorNone = 0;
    /// <summary>
    /// ErrorNo 2: <c>ContractPartyFetchWork</c>'s initial value, which it keeps when the target
    /// name resolves to nobody or the target is not in world. The only code whose meaning is
    /// certain, and therefore the only refusal this broker sends
    /// (status/CONTRACT-DESIGN.md section 5).
    /// </summary>
    public const int ErrorTargetNotInWorld = 2;

    /// <summary>Fixed part of 0x2809 / 0x280A / 0x280E, from their dumper guards.</summary>
    public const int FetchFrameSize = 0x22;          // 34
    /// <summary>Fixed part of 0x280C.</summary>
    public const int AskFrameSize = 0x1B;            // 27
    /// <summary>Fixed part of 0x280D.</summary>
    public const int SendBeginFrameSize = 0x26;      // 38
    /// <summary>Fixed part of 0x280F.</summary>
    public const int SendEndReplyFrameSize = 0x16;   // 22
    /// <summary>Fixed part of 0x2810.</summary>
    public const int ReplyFrameSize = 0x1A;          // 26
    /// <summary>Fixed part of S_BEGIN_THROUGH_ARBITER_CONTRACT.</summary>
    public const int SBeginPacketSize = 0x16;        // 22
    /// <summary>Fixed part of S_END_THROUGH_ARBITER_CONTRACT.</summary>
    public const int SEndPacketSize = 0x0E;          // 14
    /// <summary>Minimum TOTAL length of C_REPLY_THROUGH_ARBITER_CONTRACT (its handler's guard).</summary>
    public const int ReplyPacketSize = 0x1E;         // 30
    /// <summary>The same as a BODY length, which is what PacketDispatcher compares.</summary>
    public const int ReplyMinBodyLength = ReplyPacketSize - 4;   // 26

    /// <summary>TeraSharp serves one planet; 2800 is the value in every captured frame.</summary>
    public const int PlanetId = 2800;

    /// <summary>A cap on the live contract table, so a hostile or looping World cannot grow it.</summary>
    public const int MaxLiveContracts = 4096;

    /// <summary>
    /// The contract types this broker actually brokers.
    ///
    /// <para><b>T76 added type 10.</b> T60 left guild creation out on the theory that
    /// <c>CreateGuildFetchWork</c> runs a guild-name restriction check the Arbiter side cannot
    /// do. The wire says otherwise: in cap_social2 the whole of what the real Arbiter does for
    /// a guild create is broker it, and every frame it sends is one this file already builds -
    /// tap 1181 is the 0x2809 (type 10, contractId 4, Param = the guild name), 1182 the 0x280B,
    /// 1183 the World answer 0x280C, 1185 the 0x280A with AskList [2], 1186 the 0x280D that becomes the
    /// members S_BEGIN (cap_social2_client 896). No name check crosses the link. Refusing it
    /// was what left the founder with a dead Create button.</para>
    ///
    /// <para>0x23 (trade broker deal) stays out: it needs broker state this class does not
    /// have, and a made-up verdict would tell World a contract was brokered that never was.
    /// status/CONTRACT-DESIGN.md section 7.</para>
    /// </summary>
    public static bool IsBrokeredType(int contractType) =>
        contractType is TypePartyInvite or TypePartyApply or TypeCreateGuild;

    /// <summary>The four opcodes this broker takes off WorldBridge's default arm.</summary>
    public static bool HandlesWorldFrame(ushort op) =>
        op is SDB_FETCH_THROUGH_ARBITER_CONTRACT or SDB_ASK_THROUGH_ARBITER_CONTRACT
           or SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT or SDB_SEND_END_THROUGH_ARBITER_CONTRACT;

    // =========================================================================================
    // 2. The live contract table
    // =========================================================================================

    /// <summary>One brokered contract, from the 0x2809 that started it.</summary>
    public sealed class Contract
    {
        public int Index;
        public int ContractorDbId;
        public int ContractType;
        public int ContractId;
        public List<int> Opponents = new();
        /// <summary>Set by 0x280C. Null until the opponent's World has answered.</summary>
        public bool? CanContract;
        /// <summary>T64: kept so 0x280A can be built when 0x280C arrives, not before.</summary>
        public string ContractorName = string.Empty;
        public string OpponentName = string.Empty;
        /// <summary>
        /// T76: the opponents whose World has answered 0x280C. A party invite has exactly one
        /// opponent, but guild creation asks the whole party, so 0x280A must not go out until
        /// every one of them has answered.
        /// </summary>
        public readonly HashSet<int> Answered = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Contract> Live = new();
    private static int _nextIndex;
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Give the broker the process logger. Called once, like PartyWiring.UsePartyLogger.</summary>
    public static void UseContractLogger(ILogger log) => Log = log ?? Log;

    /// <summary>
    /// The Arbiter's own handle for a live contract; both Worlds quote it back. A per-process
    /// counter from 1 - the real Arbiter's comes from ContractManager and may be reused, which
    /// nothing in the protocol depends on.
    /// </summary>
    public static int NextContractIndex()
    {
        lock (Gate) { if (_nextIndex == int.MaxValue) _nextIndex = 0; return ++_nextIndex; }
    }

    /// <summary>The live contract with this index, or null.</summary>
    public static Contract? Find(int index)
    {
        lock (Gate) return Live.TryGetValue(index, out var c) ? c : null;
    }

    /// <summary>Forget a contract. Called when it ends, and by the tests between cases.</summary>
    public static bool Forget(int index)
    {
        lock (Gate) return Live.Remove(index);
    }

    /// <summary>Drop every live contract. Tests only.</summary>
    public static void Reset()
    {
        lock (Gate) { Live.Clear(); _nextIndex = 0; }
    }

    /// <summary>How many contracts are live. Tests and the log line.</summary>
    public static int LiveCount { get { lock (Gate) return Live.Count; } }

    private static void Remember(Contract c)
    {
        lock (Gate)
        {
            if (Live.Count >= MaxLiveContracts)
            {
                // Oldest index wins the eviction; a contract nobody ever answered is dead weight
                // and the alternative is an unbounded dictionary fed straight off the link.
                int oldest = int.MaxValue;
                foreach (var k in Live.Keys) if (k < oldest) oldest = k;
                Live.Remove(oldest);
            }
            Live[c.Index] = c;
        }
    }

    // =========================================================================================
    // 3. Parsers. Payload = frame minus the 6-byte header, so payload index = frame offset - 6.
    // =========================================================================================

    /// <summary>A frame-relative <c>[u32 offset][u32 count]</c> bytes ref, bounds-checked.</summary>
    public static byte[] Ref(byte[] p, int refAt)
    {
        if (p is null || refAt < 0 || refAt + 8 > p.Length) return Array.Empty<byte>();
        uint frameOff = BitConverter.ToUInt32(p, refAt);
        uint count = BitConverter.ToUInt32(p, refAt + 4);
        if (count == 0 || frameOff < 6) return Array.Empty<byte>();
        long start = (long)frameOff - 6;
        // T50: compare on the safe side. start + count would overflow for a hostile count.
        if (start < 0 || count > (uint)p.Length || start > p.Length - count) return Array.Empty<byte>();
        var outp = new byte[count];
        Buffer.BlockCopy(p, (int)start, outp, 0, (int)count);
        return outp;
    }

    /// <summary>SDB_FETCH_THROUGH_ARBITER_CONTRACT (0x2809).</summary>
    public readonly record struct FetchRequest(
        byte[] Param, byte[] FetchDataList, int ContractorDbId, int ContractType, int ContractId);

    /// <summary>Parse 0x2809, or null when the payload is shorter than the handler's guard.</summary>
    public static FetchRequest? ParseFetch(byte[] payload)
    {
        if (payload is null || payload.Length < FetchFrameSize - 6) return null;
        return new FetchRequest(
            Ref(payload, 0), Ref(payload, 8),
            BitConverter.ToInt32(payload, 16),
            BitConverter.ToInt32(payload, 20),
            BitConverter.ToInt32(payload, 24));
    }

    /// <summary>SDB_ASK_THROUGH_ARBITER_CONTRACT (0x280C). No refs.</summary>
    public readonly record struct AskAnswer(
        int ContractIndex, int ContractorDbId, int ContractType, int ContractId,
        int OpponentDbId, bool CanContract);

    /// <summary>Parse 0x280C.</summary>
    public static AskAnswer? ParseAsk(byte[] payload)
    {
        if (payload is null || payload.Length < AskFrameSize - 6) return null;
        return new AskAnswer(
            BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4),
            BitConverter.ToInt32(payload, 8), BitConverter.ToInt32(payload, 12),
            BitConverter.ToInt32(payload, 16), payload[20] != 0);
    }

    /// <summary>SDB_SEND_BEGIN (0x280D) and SDB_SEND_END (0x280E). END has no ContractIndex.</summary>
    public readonly record struct SendRequest(
        byte[] Param, byte[] AskUserList, int ContractorDbId, int ContractType, int ContractId,
        int ContractIndex);

    /// <summary>Parse 0x280D.</summary>
    public static SendRequest? ParseSendBegin(byte[] payload)
    {
        if (payload is null || payload.Length < SendBeginFrameSize - 6) return null;
        return new SendRequest(
            Ref(payload, 0), Ref(payload, 8),
            BitConverter.ToInt32(payload, 16), BitConverter.ToInt32(payload, 20),
            BitConverter.ToInt32(payload, 24), BitConverter.ToInt32(payload, 28));
    }

    /// <summary>Parse 0x280E. ContractIndex comes back 0 - the frame does not carry one.</summary>
    public static SendRequest? ParseSendEnd(byte[] payload)
    {
        if (payload is null || payload.Length < FetchFrameSize - 6) return null;
        return new SendRequest(
            Ref(payload, 0), Ref(payload, 8),
            BitConverter.ToInt32(payload, 16), BitConverter.ToInt32(payload, 20),
            BitConverter.ToInt32(payload, 24), 0);
    }

    /// <summary>Every u32 in a <c>[u32 offset][u32 count]</c> block, e.g. an AskUserList.</summary>
    public static List<int> ReadIdList(byte[] block)
    {
        var list = new List<int>();
        if (block is null) return list;
        for (int at = 0; at + 4 <= block.Length; at += 4) list.Add(BitConverter.ToInt32(block, at));
        return list;
    }

    // =========================================================================================
    // 4. Builders. Byte-exact against the decompiled writers - see CONTRACT-DESIGN.md section 3.
    // =========================================================================================

    private static byte[] WString(string? s)
    {
        s ??= string.Empty;
        var b = new byte[s.Length * 2 + 2];
        System.Text.Encoding.Unicode.GetBytes(s, 0, s.Length, b, 0);
        return b;   // the two trailing zero bytes are the terminator
    }

    /// <summary>
    /// DBS_FETCH_THROUGH_ARBITER_CONTRACT (0x280A) - the verdict, to the initiator's World.
    ///
    /// <para>The empty form is not all zeros. <c>FetchWork::ResponseFailure</c>
    /// (FUN_1409cde70, Arb_part_085.c:17031) backpatches the AskList OFFSET slot to the frame
    /// length whether or not the list is empty, and leaves the COUNT at 0 - so a refusal carries
    /// offset 34, count 0, and that is what goes on the wire.</para>
    /// </summary>
    public static byte[] BuildDbsFetch(
        int contractorDbId, int contractType, int contractId, int contractIndex, int errorNo,
        IReadOnlyList<int>? askList = null)
    {
        int n = askList?.Count ?? 0;
        var p = new byte[FetchFrameSize - 6 + n * 4];
        BitConverter.GetBytes((uint)FetchFrameSize).CopyTo(p, 0);    // offset, always the fixed size
        BitConverter.GetBytes((uint)(n * 4)).CopyTo(p, 4);           // count, 0 for a refusal
        BitConverter.GetBytes(contractorDbId).CopyTo(p, 8);
        BitConverter.GetBytes(contractType).CopyTo(p, 12);
        BitConverter.GetBytes(contractId).CopyTo(p, 16);
        BitConverter.GetBytes(contractIndex).CopyTo(p, 20);
        BitConverter.GetBytes(errorNo).CopyTo(p, 24);
        for (int i = 0; i < n; i++) BitConverter.GetBytes(askList![i]).CopyTo(p, 28 + i * 4);
        return p;
    }

    /// <summary>
    /// DBS_ASK_THROUGH_ARBITER_CONTRACT (0x280B) - one frame per opponent.
    ///
    /// <para>Both name refs are OFFSET ONLY, four bytes each, and the strings are
    /// null-terminated UTF-16LE written in the writer's order: contractor first, then opponent
    /// (Arb_part_079.c:14312). An inter-server wstr has no count slot - the dumper
    /// <c>FUN_14016be20</c> takes a pointer.</para>
    /// </summary>
    public static byte[] BuildDbsAsk(
        int contractIndex, int contractorDbId, int contractType, int contractId, int opponentDbId,
        string contractorName, string opponentName)
    {
        var c = WString(contractorName);
        var o = WString(opponentName);
        var p = new byte[FetchFrameSize - 6 + c.Length + o.Length];
        BitConverter.GetBytes((uint)FetchFrameSize).CopyTo(p, 0);                  // ContractorName
        BitConverter.GetBytes((uint)(FetchFrameSize + c.Length)).CopyTo(p, 4);     // OpponentName
        BitConverter.GetBytes(contractIndex).CopyTo(p, 8);
        BitConverter.GetBytes(contractorDbId).CopyTo(p, 12);
        BitConverter.GetBytes(contractType).CopyTo(p, 16);
        BitConverter.GetBytes(contractId).CopyTo(p, 20);
        BitConverter.GetBytes(opponentDbId).CopyTo(p, 24);
        c.CopyTo(p, 28);
        o.CopyTo(p, 28 + c.Length);
        return p;
    }

    /// <summary>DBS_SEND_END_THROUGH_ARBITER_CONTRACT (0x280F). 22 bytes, no refs.</summary>
    public static byte[] BuildDbsSendEnd(int contractorDbId, int contractType, int contractId, int opponentDbId)
    {
        var p = new byte[SendEndReplyFrameSize - 6];
        BitConverter.GetBytes(contractorDbId).CopyTo(p, 0);
        BitConverter.GetBytes(contractType).CopyTo(p, 4);
        BitConverter.GetBytes(contractId).CopyTo(p, 8);
        BitConverter.GetBytes(opponentDbId).CopyTo(p, 12);
        return p;
    }

    /// <summary>DBS_REPLY_THROUGH_ARBITER_CONTRACT (0x2810). 26 bytes, no refs.</summary>
    public static byte[] BuildDbsReply(
        int requestorDbId, int requesteeDbId, int contractType, int contractId, int reply)
    {
        var p = new byte[ReplyFrameSize - 6];
        BitConverter.GetBytes(requestorDbId).CopyTo(p, 0);
        BitConverter.GetBytes(requesteeDbId).CopyTo(p, 4);
        BitConverter.GetBytes(contractType).CopyTo(p, 8);
        BitConverter.GetBytes(contractId).CopyTo(p, 12);
        BitConverter.GetBytes(reply).CopyTo(p, 16);
        return p;
    }

    /// <summary>
    /// S_BEGIN_THROUGH_ARBITER_CONTRACT (0x7E3F), a finished packet. 22-byte fixed part from the
    /// dumper FUN_14025aaa0 (Arb_part_018.c:16178); there is no .def file for it.
    /// </summary>
    public static byte[] BuildSBegin(string requestorName, int contractType, int contractId,
                                     int contractIndex, byte[]? param = null)
    {
        var n = WString(requestorName);
        param ??= Array.Empty<byte>();
        int total = SBeginPacketSize + n.Length + param.Length;
        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_BEGIN_THROUGH_ARBITER_CONTRACT).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)SBeginPacketSize).CopyTo(p, 4);                  // name offset
        BitConverter.GetBytes((ushort)(SBeginPacketSize + n.Length)).CopyTo(p, 6);     // Param offset
        BitConverter.GetBytes((ushort)param.Length).CopyTo(p, 8);                      // Param count
        BitConverter.GetBytes(contractType).CopyTo(p, 10);
        BitConverter.GetBytes(contractId).CopyTo(p, 14);
        BitConverter.GetBytes(contractIndex).CopyTo(p, 18);
        n.CopyTo(p, SBeginPacketSize);
        param.CopyTo(p, SBeginPacketSize + n.Length);
        return p;
    }

    /// <summary>
    /// S_END_THROUGH_ARBITER_CONTRACT (0xC7D7), a finished packet. 14-byte fixed part from the
    /// dumper FUN_14027c6d0 (Arb_part_019.c:18782); no .def file either.
    /// </summary>
    public static byte[] BuildSEnd(string requestorName, int contractType, int contractId)
    {
        var n = WString(requestorName);
        int total = SEndPacketSize + n.Length;
        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_END_THROUGH_ARBITER_CONTRACT).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)SEndPacketSize).CopyTo(p, 4);
        BitConverter.GetBytes(contractType).CopyTo(p, 6);
        BitConverter.GetBytes(contractId).CopyTo(p, 10);
        n.CopyTo(p, SEndPacketSize);
        return p;
    }

    // =========================================================================================
    // 5. C_REPLY_THROUGH_ARBITER_CONTRACT - read by absolute offset, NOT by the .def
    // =========================================================================================

    /// <summary>The client's accept / reject.</summary>
    public readonly record struct ClientReply(
        string ContractRequestorName, int ContractType, int ContractId, int ContractIndex, int Reply);

    /// <summary>
    /// Parse C_REPLY_THROUGH_ARBITER_CONTRACT from the FULL packet (header included), using the
    /// offsets Handler_C_REPLY_THROUGH_ARBITER_CONTRACT reads.
    ///
    /// <para><b>C_REPLY_THROUGH_ARBITER_CONTRACT.1.def is wrong</b> and must not be used. It
    /// declares uint32 type / uint64 id / uint32 response / string recipient, which is 22 bytes
    /// and puts every field in the wrong place; the handler's own reads and its 30-byte
    /// GET_CLIENT_BUFFER_BUFSIZE_MISMATCH guard say otherwise. Same class of trap as the
    /// patch-101 S_FRIEND_LIST def and the ten wrong guild defs.</para>
    /// </summary>
    public static ClientReply? ParseClientReply(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < ReplyPacketSize) return null;
        ushort nameOff = BitConverter.ToUInt16(packet[0x0C..]);
        return new ClientReply(
            ReadWString(packet, nameOff),
            BitConverter.ToInt32(packet[0x0E..]),
            BitConverter.ToInt32(packet[0x12..]),
            BitConverter.ToInt32(packet[0x16..]),
            BitConverter.ToInt32(packet[0x1A..]));
    }

    /// <summary>
    /// A null-terminated UTF-16LE block, decoded from its first byte. Separate from
    /// <see cref="ReadWString"/> because there an offset of 0 means "the packet has no string",
    /// which is the codec's convention and the real handler's <c>uVar1 == 0</c> check; here 0 is
    /// simply where the block starts.
    /// </summary>
    public static string DecodeWString(ReadOnlySpan<byte> block)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < block.Length; i += 2)
        {
            ushort c = BitConverter.ToUInt16(block[i..]);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    /// <summary>A null-terminated UTF-16LE string at a packet-relative offset; 0 means absent.</summary>
    public static string ReadWString(ReadOnlySpan<byte> packet, int offset)
    {
        if (offset <= 0 || offset >= packet.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = offset; i + 1 < packet.Length; i += 2)
        {
            ushort c = BitConverter.ToUInt16(packet[i..]);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    // =========================================================================================
    // 6. The live surface
    // =========================================================================================

    /// <summary>
    /// A World frame arrived. Returns true when it was a contract frame and has been dealt with,
    /// so WorldBridge can <c>return</c>; false leaves it to DbProxy and the replay table. Same
    /// shape as PartyWiring.TryHandleWorldFrame.
    /// </summary>
    public static bool TryHandleWorldFrame(ushort opcode, byte[] payload)
    {
        if (!HandlesWorldFrame(opcode)) return false;
        switch (opcode)
        {
            case SDB_FETCH_THROUGH_ARBITER_CONTRACT: OnFetch(payload); return true;
            case SDB_ASK_THROUGH_ARBITER_CONTRACT: OnAsk(payload); return true;
            case SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT: OnSendBegin(payload); return true;
            case SDB_SEND_END_THROUGH_ARBITER_CONTRACT: OnSendEnd(payload); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The target of a party contract, read out of the request's two variable blocks.
    ///
    /// <para><b>T64 settled this from the wire.</b> cap_social.log seq 650 is a real 0x2809 for
    /// a party invite, and it puts the target in <c>Param</c> as a name:
    /// <c>[06] Param offset 34, [0A] Param count 10, [0E] FetchDataList offset 44,
    /// [12] FetchDataList count 0</c>, with Param = <c>74 00 77 00 6F 00 00 00 00 00</c> - the
    /// null-terminated UTF-16LE "two" plus two bytes of padding. <c>FetchDataList is empty</c>,
    /// and it is empty the way this protocol writes an empty list at the end of a frame: offset
    /// = frame length, count 0. That matches <c>ContractPartyFetchWork</c>, which resolves the
    /// target through the by-name lookup <c>FUN_14082dc50</c>.
    ///
    /// <para>So the NAME is tried first and is the only route the capture supports. The db-id
    /// list is kept as a fallback rather than deleted because <c>FetchDataList</c> is a real
    /// field that some other contract type may well fill - but it can no longer shadow a name
    /// that is present, which is what it did before T64.</para>
    /// </summary>
    public static GameSession? ResolveTarget(FetchRequest r, out string how)
    {
        how = "nothing";
        var bridge = Bridge;
        if (bridge == null) return null;
        var hint = ReadTargetHint(r);

        if (hint.Name.Length > 0)
        {
            foreach (var s in bridge.InWorldSessions())
                if (string.Equals(s.SelectedCharacter?.Name, hint.Name, StringComparison.OrdinalIgnoreCase))
                { how = $"Param name '{hint.Name}'"; return s; }
            // T65: inviting the GM character was refused with "this character doesn't exist"
            // while the reverse invite worked, and nothing in the log said which name we
            // compared against. ContractPartyFetchWork (Arb_part_084.c:5749) has no AdminLevel
            // branch at all - its only gate is the by-name lookup FUN_14082dc50 plus
            // "state == 2" (in world) - so a miss here is OUR session table, not a GM rule.
            how = $"Param name '{hint.Name}' (not in world)";
            Log.LogWarning(
                "contract: no in-world character named '{Name}'; in world right now: [{Names}]",
                hint.Name,
                string.Join(", ", bridge.InWorldSessions().Select(x => x.SelectedCharacter?.Name ?? "<none>")));
        }

        foreach (int id in hint.DbIds)
        {
            var s = bridge.SessionForPlayerId(id);
            if (s != null) { how = $"FetchDataList db id {id}"; return s; }
        }
        return null;
    }

    /// <summary>Both candidate readings of a 0x2809's variable blocks, without a bridge.</summary>
    public readonly record struct TargetHint(IReadOnlyList<int> DbIds, string Name);

    /// <summary>
    /// The pure half of <see cref="ResolveTarget"/>: the name <c>Param</c> decodes to, and every
    /// positive db id in <c>FetchDataList</c>. Split out so it can be tested without a live
    /// WorldBridge - <c>T64_the_captured_fetch_names_its_target_in_Param</c> runs it over the
    /// real seq-650 bytes.
    /// </summary>
    public static TargetHint ReadTargetHint(FetchRequest r)
    {
        var ids = new List<int>();
        foreach (int id in ReadIdList(r.FetchDataList)) if (id > 0) ids.Add(id);
        return new TargetHint(ids, DecodeWString(r.Param));
    }

    /// <summary>
    /// Everybody a 0x2809 has to ask. One session for a party contract; for guild creation, the
    /// whole party.
    ///
    /// <para><b>Guild creation must not take the by-name route.</b> Its <c>Param</c> is the GUILD
    /// NAME, not a character name - tap 1181 carries a 138-byte Param whose first characters are
    /// the typed name (test), and the founders own C_REQUEST_CONTRACT (cap_social3_client 2388)
    /// shows the client sending that name and contract type 10 in one packet. The party comes in
    /// FetchDataList instead: 4 bytes at frame offset 0xAC, one db id, and the 0x280B the real
    /// Arbiter answered with named opponent 2 - the other party member. Feeding the guild name to
    /// the name lookup would find nobody and refuse the contract.</para>
    /// </summary>
    public static List<GameSession> ResolveOpponents(FetchRequest r, out string how)
    {
        var found = new List<GameSession>();
        how = "nothing";
        var bridge = Bridge;
        if (bridge == null) return found;

        if (r.ContractType != TypeCreateGuild)
        {
            var one = ResolveTarget(r, out how);
            if (one?.SelectedCharacter != null) found.Add(one);
            return found;
        }

        foreach (int id in ReadIdList(r.FetchDataList))
        {
            if (id <= 0 || id == r.ContractorDbId) continue;
            var s = bridge.SessionForPlayerId(id);
            if (s?.SelectedCharacter != null && !found.Contains(s)) found.Add(s);
        }
        how = found.Count == 0
            ? "FetchDataList named no in-world party member"
            : "FetchDataList party [" + string.Join(", ", found.Select(x => x.SelectedCharacter!.Name)) + "]";
        return found;
    }

    /// <summary>
    /// 0x2809. Broker the contract, or refuse it with ErrorNo 2.
    ///
    /// <para>Type 0x23 (trade broker) is still refused on purpose: it needs state this broker
    /// does not have, and inventing a verdict would tell World a contract was brokered that never
    /// was. Type 10 (guild creation) IS brokered since T76 - see <see cref="IsBrokeredType"/>.
    /// status/CONTRACT-DESIGN.md section 7.</para>
    /// </summary>
    private static void OnFetch(byte[] payload)
    {
        var req = ParseFetch(payload);
        if (req == null)
        {
            Log.LogWarning("SDB_FETCH_THROUGH_ARBITER_CONTRACT: {Len} B payload (want {Want}) - dropped",
                payload?.Length ?? 0, FetchFrameSize - 6);
            return;
        }
        var r = req.Value;
        var bridge = Bridge;
        if (bridge == null) return;

        if (!IsBrokeredType(r.ContractType))
        {
            Log.LogInformation(
                "SDB_FETCH_THROUGH_ARBITER_CONTRACT: type {Type} ({What}) is not brokered yet - refusing contract {Id}",
                r.ContractType, DescribeType(r.ContractType), r.ContractId);
            ArbiterClientHandlers.SendToWorld(r.ContractorDbId, DBS_FETCH_THROUGH_ARBITER_CONTRACT,
                BuildDbsFetch(r.ContractorDbId, r.ContractType, r.ContractId, 0, ErrorTargetNotInWorld));
            return;
        }

        var initiator = bridge.SessionForPlayerId(r.ContractorDbId);
        var opponents = ResolveOpponents(r, out string how);
        if (opponents.Count == 0 || initiator?.SelectedCharacter == null)
        {
            // What the real Arbiter does when the by-name lookup misses or the target's state is
            // not 2: ErrorNo keeps its initial value and ResponseFailure sends an empty AskList.
            Log.LogInformation(
                "SDB_FETCH_THROUGH_ARBITER_CONTRACT: contract {Id} type {Type} from {From} - target not in world ({How}), refusing with ErrorNo {E}",
                r.ContractId, r.ContractType, r.ContractorDbId, how, ErrorTargetNotInWorld);
            ArbiterClientHandlers.SendToWorld(r.ContractorDbId, DBS_FETCH_THROUGH_ARBITER_CONTRACT,
                BuildDbsFetch(r.ContractorDbId, r.ContractType, r.ContractId, 0, ErrorTargetNotInWorld));
            return;
        }

        var contract = new Contract
        {
            Index = NextContractIndex(),
            ContractorDbId = r.ContractorDbId,
            ContractType = r.ContractType,
            ContractId = r.ContractId,
            Opponents = opponents.Select(x => (int)x.SelectedCharacter!.Id).ToList(),
        };
        Remember(contract);

        // T64 - ORDER, from cap_social.log: 0x2809 (650) -> 0x280B (652) -> 0x280C (653) ->
        // 0x280A (655). The opponent's World is asked FIRST and the initiator's World is told
        // nothing until that answer is in. Before T64 we sent 0x280A immediately with
        // ErrorNo 0, which told the initiator the contract was brokered before anybody had
        // agreed to it - and then ignored a CanContract = 0 that arrived afterwards.
        contract.ContractorName = initiator.SelectedCharacter.Name;
        contract.OpponentName = opponents[0].SelectedCharacter!.Name;
        // T76: one 0x280B per opponent, each naming THAT opponent - tap 1182 for the guild
        // create carries New + Test, the founder and the single other party member.
        foreach (var opp in opponents)
            ArbiterClientHandlers.SendToWorld(opp, DBS_ASK_THROUGH_ARBITER_CONTRACT,
                BuildDbsAsk(contract.Index, r.ContractorDbId, r.ContractType, r.ContractId,
                            (int)opp.SelectedCharacter!.Id,
                            contract.ContractorName, opp.SelectedCharacter!.Name));

        Log.LogInformation(
            "Contract {Index}: {Type} from {A} to {B} asked (contractId {Cid}, target via {How})",
            contract.Index, DescribeType(r.ContractType), contract.ContractorName,
            contract.OpponentName, r.ContractId, how);
    }

    /// <summary>
    /// 0x280C. Record the answer and send nothing - Handler_SDB_ASK_THROUGH_ARBITER_CONTRACT
    /// (Arb_part_063.c:810) is 38 lines and has no SendToSession.
    /// </summary>
    private static void OnAsk(byte[] payload)
    {
        var ask = ParseAsk(payload);
        if (ask == null)
        {
            Log.LogWarning("SDB_ASK_THROUGH_ARBITER_CONTRACT: {Len} B payload (want {Want}) - dropped",
                payload?.Length ?? 0, AskFrameSize - 6);
            return;
        }
        var a = ask.Value;
        var c = Find(a.ContractIndex);
        if (c == null)
        {
            Log.LogInformation("SDB_ASK_THROUGH_ARBITER_CONTRACT: no live contract {Index} - ignored", a.ContractIndex);
            return;
        }
        c.CanContract = a.CanContract;
        int answered;
        lock (Gate) { c.Answered.Add(a.OpponentDbId); answered = c.Answered.Count; }
        Log.LogInformation("Contract {Index}: opponent {Who} CanContract = {Can} ({A}/{N} answered)",
            a.ContractIndex, a.OpponentDbId, a.CanContract, answered, c.Opponents.Count);

        // T64: NOW the initiator's World gets its verdict - this is seq 655 in cap_social.log,
        // 0.4 ms after the 0x280C at 653. A refusal keeps the empty-AskList form
        // FetchWork::ResponseFailure writes (offset = frame length, count 0).
        var bridge = Bridge;
        if (bridge == null) return;
        if (a.CanContract)
        {
            // T76: a guild create asks the whole party. Sending 0x280A on the first yes would
            // tell the founders World the contract was brokered before the rest had answered.
            if (answered < c.Opponents.Count)
            {
                Log.LogInformation("Contract {Index}: holding 0x280A until all {N} opponents answer",
                    a.ContractIndex, c.Opponents.Count);
                return;
            }
            ArbiterClientHandlers.SendToWorld(c.ContractorDbId, DBS_FETCH_THROUGH_ARBITER_CONTRACT,
                BuildDbsFetch(c.ContractorDbId, c.ContractType, c.ContractId, c.Index,
                              ErrorNone, c.Opponents));
        }
        else
        {
            ArbiterClientHandlers.SendToWorld(c.ContractorDbId, DBS_FETCH_THROUGH_ARBITER_CONTRACT,
                BuildDbsFetch(c.ContractorDbId, c.ContractType, c.ContractId, 0, ErrorTargetNotInWorld));
            Forget(c.Index);
        }
    }

    /// <summary>0x280D -&gt; the client packet S_BEGIN_THROUGH_ARBITER_CONTRACT, and nothing else.</summary>
    private static void OnSendBegin(byte[] payload)
    {
        var req = ParseSendBegin(payload);
        if (req == null)
        {
            Log.LogWarning("SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT: {Len} B payload (want {Want}) - dropped",
                payload?.Length ?? 0, SendBeginFrameSize - 6);
            return;
        }
        var r = req.Value;
        var bridge = Bridge;
        if (bridge == null) return;

        string requestor = bridge.SessionForPlayerId(r.ContractorDbId)?.SelectedCharacter?.Name ?? string.Empty;
        var packet = BuildSBegin(requestor, r.ContractType, r.ContractId, r.ContractIndex, r.Param);
        int sent = 0;
        foreach (int who in ReadIdList(r.AskUserList))
        {
            var s = bridge.SessionForPlayerId(who);
            if (s == null) continue;
            // GameSession.Send encrypts in place with a stateful cipher, so a second recipient
            // gets a clone - the same rule the tunnel fan-out follows.
            s.Send(sent == 0 ? packet : (byte[])packet.Clone());
            sent++;
        }
        Log.LogInformation("Contract {Index}: S_BEGIN to {N} client(s) for {Type}",
            r.ContractIndex, sent, DescribeType(r.ContractType));
    }

    /// <summary>
    /// 0x280E -&gt; S_END_THROUGH_ARBITER_CONTRACT to each participant, then a 0x280F fan-out,
    /// one frame per participant. Handler_SDB_SEND_END (Arb_part_064.c:2983).
    /// </summary>
    private static void OnSendEnd(byte[] payload)
    {
        var req = ParseSendEnd(payload);
        if (req == null)
        {
            Log.LogWarning("SDB_SEND_END_THROUGH_ARBITER_CONTRACT: {Len} B payload (want {Want}) - dropped",
                payload?.Length ?? 0, FetchFrameSize - 6);
            return;
        }
        var r = req.Value;
        var bridge = Bridge;
        if (bridge == null) return;

        string requestor = bridge.SessionForPlayerId(r.ContractorDbId)?.SelectedCharacter?.Name ?? string.Empty;
        var packet = BuildSEnd(requestor, r.ContractType, r.ContractId);
        int clients = 0, frames = 0;
        foreach (int who in ReadIdList(r.AskUserList))
        {
            var s = bridge.SessionForPlayerId(who);
            if (s != null) { s.Send(clients == 0 ? packet : (byte[])packet.Clone()); clients++; }
            if (ArbiterClientHandlers.SendToWorld(who, DBS_SEND_END_THROUGH_ARBITER_CONTRACT,
                BuildDbsSendEnd(r.ContractorDbId, r.ContractType, r.ContractId, who))) frames++;
        }
        Log.LogInformation("Contract for {Cid} ended: S_END to {C} client(s), {F} x 0x280F",
            r.ContractId, clients, frames);
    }

    /// <summary>
    /// C_REPLY_THROUGH_ARBITER_CONTRACT. The replier's World gets 0x280F (this is over for you);
    /// the requestor's World gets 0x2810 (here is your answer). Directions straight out of
    /// Handler_C_REPLY_THROUGH_ARBITER_CONTRACT - see CONTRACT-DESIGN.md section 4.
    ///
    /// <para>World then does the work: for a party invite it creates the party and tells us with
    /// SA_JOIN_PARTY, which is where PartyManager takes over. This handler never touches a
    /// party.</para>
    /// </summary>
    public static bool OnClientReply(GameSession s, ReadOnlyMemory<byte> body, ILogger? log = null)
    {
        var logger = log ?? Log;
        // PacketDispatcher hands the handler the BODY; the offsets are packet-relative, so
        // rebuild the 4-byte header rather than subtracting 4 from every constant.
        var packet = new byte[body.Length + 4];
        BitConverter.GetBytes((ushort)packet.Length).CopyTo(packet, 0);
        BitConverter.GetBytes(C_REPLY_THROUGH_ARBITER_CONTRACT).CopyTo(packet, 2);
        body.Span.CopyTo(packet.AsSpan(4));

        var parsed = ParseClientReply(packet);
        if (parsed == null)
        {
            logger.LogWarning("C_REPLY_THROUGH_ARBITER_CONTRACT: {Len} B packet (want {Want})",
                packet.Length, ReplyPacketSize);
            return true;
        }
        var r = parsed.Value;

        var chr = s.SelectedCharacter;
        var bridge = Bridge;
        if (chr == null || bridge == null) return true;
        int replierDbId = (int)chr.Id;

        // The other party is named by the packet. The real Arbiter looks it up by name and
        // answers CR_PACKET_FORGED when it misses; ours resolves the live contract first,
        // because the index is ours and is the stronger check.
        var contract = Find(r.ContractIndex);
        int requestorDbId = contract?.ContractorDbId ?? 0;
        if (requestorDbId == 0)
        {
            foreach (var other in bridge.InWorldSessions())
                if (string.Equals(other.SelectedCharacter?.Name, r.ContractRequestorName,
                                  StringComparison.OrdinalIgnoreCase))
                { requestorDbId = (int)other.SelectedCharacter!.Id; break; }
        }
        if (requestorDbId == 0 || requestorDbId == replierDbId)
        {
            logger.LogInformation(
                "C_REPLY_THROUGH_ARBITER_CONTRACT from {Who}: contract {Index} names '{Name}', which resolves to nobody - ignored",
                chr.Name, r.ContractIndex, r.ContractRequestorName);
            return true;
        }

        ArbiterClientHandlers.SendToWorld(s, DBS_SEND_END_THROUGH_ARBITER_CONTRACT,
            BuildDbsSendEnd(requestorDbId, r.ContractType, r.ContractId, replierDbId));
        ArbiterClientHandlers.SendToWorld(requestorDbId, DBS_REPLY_THROUGH_ARBITER_CONTRACT,
            BuildDbsReply(requestorDbId, replierDbId, r.ContractType, r.ContractId, r.Reply));

        Forget(r.ContractIndex);
        logger.LogInformation(
            "Contract {Index}: {Who} replied {Reply} to {Type} from {Requestor} - 0x280F + 0x2810 sent",
            r.ContractIndex, chr.Name, r.Reply, DescribeType(r.ContractType), r.ContractRequestorName);
        return true;
    }

    /// <summary>The contract type, for a log line.</summary>
    public static string DescribeType(int contractType) => contractType switch
    {
        TypePartyInvite => "party invite",
        TypePartyApply => "party apply",
        TypeCreateGuild => "guild creation",
        TypeTradeBrokerOpenDeal => "trade broker deal",
        _ => $"type {contractType}",
    };
}
