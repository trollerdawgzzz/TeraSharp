// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// BrokerPackets - the trade-broker codec (T53). Research: status/BROKER-DESIGN.md.
//
// Research plus a codec, nothing else: there is no manager, no store table and no handler layer,
// the same shape PartyPackets had after T28 and GuildPackets after T36.
//
// It lives in its own file rather than in DbProxyStaticData.cs (where PartyPackets and
// GuildPackets are) for the reason ChatPackets did: that file is already 130 KB.
//
// THE ONE THING TO KNOW. Of the 21 C_TRADE_BROKER_* packets in 376012 the Arbiter answers 16 and
// never sees the other five - and those five are exactly the ones that move an item between an
// inventory and the broker. They go to WorldServer, which turns each into an SDB_TRADE_BROKER_*
// on the DB-proxy link. Five of the seven SDB_ requests carry a DlmId, so each is a per-user
// DLMItem: an unanswered one head-blocks that character's DB queue for the life of the World
// process (status/HANDOFF.md section 1). Nothing answers them today and no capture contains one,
// so the replay table cannot cover either - opening the broker on a live TeraSharp wedges the
// character, exactly as opening the mailbox did before T45. BROKER-DESIGN.md section 8.
//
// Offsets in the comments are PACKET-relative for client packets (first ref slot at 0x04, after
// [u16 len][u16 opcode]) and FRAME-relative for inter-server frames (first field at 0x06, after
// [u32 len][u16 opcode]) - matching how the decompile quotes them. The parsers take the BODY /
// PAYLOAD, i.e. header removed.
//
// Every layout is from the packet's own PDL dumper: a function beginning
// `wcscpy_s(name, 0x100, L"<PACKET>")` that then emits one L"FieldName" per field with the
// offset it reads. All 57 of them exist. Two checks make them trustworthy: each dumper's guard
// lands exactly on the end of its last field, and for all seven SDB_ packets the dumper's guard
// equals the handler's guard byte for byte.
// =============================================================================================
public static class BrokerPackets
{
    // ---- client -> Arbiter: the 16 the Arbiter answers itself ----
    public const ushort C_TRADE_BROKER_BOUGHT_ITEM_LIST = 0xABCA;
    public const ushort C_TRADE_BROKER_CLOSE = 0x961B;
    public const ushort C_TRADE_BROKER_DEAL_CONFIRM = 0x9FA3;
    public const ushort C_TRADE_BROKER_DEAL_PRICE_UPDATE = 0xDB7F;
    public const ushort C_TRADE_BROKER_HIGHEST_ITEM_LEVEL = 0xEAFB;
    public const ushort C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW = 0x76B4;
    public const ushort C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE = 0xE53F;
    public const ushort C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT = 0x983A;
    public const ushort C_TRADE_BROKER_INPUT_PRICE = 0x6F34;
    public const ushort C_TRADE_BROKER_REGISTERED_ITEM_LIST = 0xB981;
    public const ushort C_TRADE_BROKER_REJECT_SUGGEST = 0xB33F;
    public const ushort C_TRADE_BROKER_SOLD_ITEM_LIST = 0x92E5;
    public const ushort C_TRADE_BROKER_SUGGEST_DEAL = 0xA788;
    public const ushort C_TRADE_BROKER_WAITING_ITEM_LIST_NEW = 0x8DC7;
    public const ushort C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE = 0x8CFB;
    public const ushort C_TRADE_BROKER_WAITING_ITEM_LIST_SORT = 0x8863;

    // ---- client -> WorldServer: the 5 the Arbiter must NOT answer. They have PDL dumpers but
    //      no Handler_C_* anywhere in ArbiterServer.exe; World owns them because each one moves
    //      an item. Listed so the tunnel and the design doc agree on who owns what. ----
    public const ushort C_TRADE_BROKER_REGISTER_ITEM = 0x7E74;
    public const ushort C_TRADE_BROKER_UNREGISTER_ITEM = 0xB8BA;
    public const ushort C_TRADE_BROKER_CALC_SOLD_ITEM = 0x7516;
    public const ushort C_TRADE_BROKER_CALC_BOUGHT_ITEM = 0x5C10;
    public const ushort C_TRADE_BROKER_BUY_IT_NOW = 0xF66D;

    // ---- Arbiter -> client ----
    public const ushort S_TRADE_BROKER_BOUGHT_ITEM_LIST = 0x53C0;
    public const ushort S_TRADE_BROKER_BUY_IT_NOW = 0xEC54;
    public const ushort S_TRADE_BROKER_CALC_BOUGHT_ITEM = 0xDEC6;
    public const ushort S_TRADE_BROKER_CALC_NOTIFY = 0x6AF1;
    public const ushort S_TRADE_BROKER_CALC_SOLD_ITEM = 0x97ED;
    public const ushort S_TRADE_BROKER_DEAL_INFO_UPDATE = 0xB9E0;
    public const ushort S_TRADE_BROKER_DEAL_SUGGESTED = 0xED80;
    public const ushort S_TRADE_BROKER_HIGHEST_ITEM_LEVEL = 0x7E53;
    public const ushort S_TRADE_BROKER_HISTORY_ITEM_LIST = 0x9372;
    public const ushort S_TRADE_BROKER_INPUT_PRICE = 0x4FE6;
    public const ushort S_TRADE_BROKER_REGISTERED_ITEM_LIST = 0xCDEA;
    public const ushort S_TRADE_BROKER_REQUEST_DEAL_RESULT = 0xD0E6;
    public const ushort S_TRADE_BROKER_SOLD_ITEM_LIST = 0x5587;
    public const ushort S_TRADE_BROKER_SUGGEST_DEAL = 0x7066;
    public const ushort S_TRADE_BROKER_WAITING_ITEM_LIST = 0xFD2C;

    // ---- the DB-proxy half. SDB_ = World -> Arbiter, DBS_ = Arbiter -> World. ----
    public const ushort SDB_TRADE_BROKER_REGISTER_ITEM = 0x2817;
    public const ushort DBS_TRADE_BROKER_REGISTER_ITEM = 0x2818;
    public const ushort SDB_TRADE_BROKER_UNREGISTER_ITEM = 0x2819;
    public const ushort DBS_TRADE_BROKER_UNREGISTER_ITEM = 0x281A;
    public const ushort SDB_TRADE_BROKER_CALC_SOLD_ITEM = 0x281B;
    public const ushort DBS_TRADE_BROKER_CALC_SOLD_ITEM = 0x281C;
    public const ushort SDB_TRADE_BROKER_CALC_BOUGHT_ITEM = 0x281D;
    public const ushort DBS_TRADE_BROKER_CALC_BOUGHT_ITEM = 0x281E;
    public const ushort SDB_TRADE_BROKER_BUY_IT_NOW = 0x281F;
    public const ushort DBS_TRADE_BROKER_BUY_IT_NOW = 0x2820;
    public const ushort SDB_TRADE_BROKER_START_DEAL = 0x2821;
    public const ushort DBS_TRADE_BROKER_START_DEAL = 0x2822;
    /// <summary>The odd one: an Arbiter -&gt; World PUSH with no SDB_ partner, sent when both
    /// sides of a bargain confirm.</summary>
    public const ushort DBS_TRADE_BROKER_ACCEPT_DEAL = 0x2823;
    public const ushort SDB_TRADE_BROKER_CANCEL_DEAL = 0x2824;
    public const ushort DBS_TRADE_BROKER_CANCEL_DEAL = 0x282C;

    // ---- the inter-server six ----
    public const ushort SA_TRADE_BROKER_OPEN = 0x1457;
    public const ushort AS_TRADE_BROKER_CLOSE = 0x1458;
    public const ushort SA_TRADE_BROKER_DEAL_OPEN = 0x1459;
    public const ushort AS_TRADE_BROKER_DEAL_OPEN = 0x145A;
    public const ushort AS_TRADE_BROKER_DEAL_CLOSE = 0x145B;
    public const ushort AS_TRADE_BROKER_ITEM_SOLD = 0x286D;

    /// <summary>A client packet's <c>[u16 length][u16 opcode]</c> header.</summary>
    public const int ClientHeaderSize = 4;
    /// <summary>An inter-server frame's <c>[u32 length][u16 opcode]</c> header.</summary>
    public const int FrameHeaderSize = 6;

    // =========================================================================================
    // Lengths
    // =========================================================================================

    /// <summary>
    /// Minimum TOTAL client packet length for one of the sixteen the Arbiter answers; 0 for the
    /// five World owns and for anything else.
    ///
    /// <para>Taken from the packet's PDL dumper guard where it has one, and 4 (header only) for
    /// the five that carry no fields at all. Where both a dumper guard and a handler guard exist
    /// they agree on all eleven - BROKER-DESIGN.md section 2.1. Three of the field-less five
    /// still guard on 4 in the handler and two do not guard at all; a 4-byte frame is just the
    /// header either way.</para>
    /// </summary>
    public static int MinClientLength(ushort op) => op switch
    {
        C_TRADE_BROKER_BOUGHT_ITEM_LIST => 0x04,
        C_TRADE_BROKER_CLOSE => 0x04,
        C_TRADE_BROKER_DEAL_CONFIRM => 0x0C,
        C_TRADE_BROKER_DEAL_PRICE_UPDATE => 0x10,
        C_TRADE_BROKER_HIGHEST_ITEM_LEVEL => 0x04,
        C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW => 0x6C,
        C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE => 0x08,
        C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT => 0x09,
        C_TRADE_BROKER_INPUT_PRICE => 0x10,
        C_TRADE_BROKER_REGISTERED_ITEM_LIST => 0x04,
        C_TRADE_BROKER_REJECT_SUGGEST => 0x0C,
        C_TRADE_BROKER_SOLD_ITEM_LIST => 0x04,
        C_TRADE_BROKER_SUGGEST_DEAL => 0x10,
        C_TRADE_BROKER_WAITING_ITEM_LIST_NEW => 0x75,
        C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE => 0x08,
        C_TRADE_BROKER_WAITING_ITEM_LIST_SORT => 0x09,
        _ => 0,
    };

    /// <summary>
    /// Minimum BODY length - what <c>PacketDispatcher.Register</c> takes, because it hands the
    /// handler <c>packet[4..]</c>. Every guard in the decompile is a FRAME length. This is the
    /// same -4 that was wrong in the guild and chat wiring diffs
    /// (status/CLIENT-REJECTS.md section 7.2c); doing it once here means nobody does it twice.
    /// </summary>
    public static int MinBodyLength(ushort op)
    {
        int frame = MinClientLength(op);
        return frame <= ClientHeaderSize ? 0 : frame - ClientHeaderSize;
    }

    /// <summary>True if the ARBITER - not WorldServer - owns this broker client packet.</summary>
    public static bool ArbiterHandlesClientPacket(ushort op) => MinClientLength(op) != 0;

    /// <summary>The five World owns. Each becomes an SDB_TRADE_BROKER_* on the DB-proxy link;
    /// answering one here would double-answer the client.</summary>
    public static bool WorldHandlesClientPacket(ushort op) => op switch
    {
        C_TRADE_BROKER_REGISTER_ITEM => true,
        C_TRADE_BROKER_UNREGISTER_ITEM => true,
        C_TRADE_BROKER_CALC_SOLD_ITEM => true,
        C_TRADE_BROKER_CALC_BOUGHT_ITEM => true,
        C_TRADE_BROKER_BUY_IT_NOW => true,
        _ => false,
    };

    /// <summary>
    /// Minimum FRAME length each inter-server broker handler demands, from its dumper guard.
    /// A short frame is not a dropped packet on the real Arbiter - like the party and guild
    /// frames it logs a PDL version mismatch and kills the link - so these have to be exact.
    /// Our parsers return null instead, which is the safe analogue.
    /// </summary>
    public static int MinFrameLength(ushort op) => op switch
    {
        SDB_TRADE_BROKER_REGISTER_ITEM => 0x16,
        SDB_TRADE_BROKER_UNREGISTER_ITEM => 0x1E,
        SDB_TRADE_BROKER_CALC_SOLD_ITEM => 0x22,
        SDB_TRADE_BROKER_CALC_BOUGHT_ITEM => 0x22,
        SDB_TRADE_BROKER_BUY_IT_NOW => 0x27,
        SDB_TRADE_BROKER_START_DEAL => 0x12,
        SDB_TRADE_BROKER_CANCEL_DEAL => 0x0E,
        DBS_TRADE_BROKER_REGISTER_ITEM => 0x13,
        DBS_TRADE_BROKER_UNREGISTER_ITEM => 0x1F,
        DBS_TRADE_BROKER_CALC_SOLD_ITEM => 0x1F,
        DBS_TRADE_BROKER_CALC_BOUGHT_ITEM => 0x1F,
        DBS_TRADE_BROKER_BUY_IT_NOW => 0x1F,
        DBS_TRADE_BROKER_START_DEAL => 0x4C,
        DBS_TRADE_BROKER_ACCEPT_DEAL => 0x1E,
        DBS_TRADE_BROKER_CANCEL_DEAL => 0x0A,
        SA_TRADE_BROKER_OPEN => 0x0E,
        SA_TRADE_BROKER_DEAL_OPEN => 0x13,
        AS_TRADE_BROKER_CLOSE => 0x0A,
        AS_TRADE_BROKER_DEAL_OPEN => 0x12,
        AS_TRADE_BROKER_DEAL_CLOSE => 0x0A,
        AS_TRADE_BROKER_ITEM_SOLD => 0x0B,
        _ => 0,
    };

    /// <summary>
    /// The five SDB_ requests that carry a DlmId, and are therefore per-user DLMItems: leaving
    /// one unanswered head-blocks that character's DB queue for the life of the World process.
    /// <c>SDB_TRADE_BROKER_START_DEAL</c> and <c>_CANCEL_DEAL</c> carry none and are safe to
    /// ignore. This is the set BROKER-DESIGN.md section 8 says to answer first, even with
    /// <c>Success = 0</c>.
    /// </summary>
    public static bool CarriesDlmId(ushort op) => op switch
    {
        SDB_TRADE_BROKER_REGISTER_ITEM => true,
        SDB_TRADE_BROKER_UNREGISTER_ITEM => true,
        SDB_TRADE_BROKER_CALC_SOLD_ITEM => true,
        SDB_TRADE_BROKER_CALC_BOUGHT_ITEM => true,
        SDB_TRADE_BROKER_BUY_IT_NOW => true,
        _ => false,
    };

    /// <summary>The DBS_ reply for an SDB_ request, or 0. Every pair is request+1 except
    /// CANCEL_DEAL, whose reply is 0x282C rather than 0x2825.</summary>
    public static ushort ReplyFor(ushort request) => request switch
    {
        SDB_TRADE_BROKER_REGISTER_ITEM => DBS_TRADE_BROKER_REGISTER_ITEM,
        SDB_TRADE_BROKER_UNREGISTER_ITEM => DBS_TRADE_BROKER_UNREGISTER_ITEM,
        SDB_TRADE_BROKER_CALC_SOLD_ITEM => DBS_TRADE_BROKER_CALC_SOLD_ITEM,
        SDB_TRADE_BROKER_CALC_BOUGHT_ITEM => DBS_TRADE_BROKER_CALC_BOUGHT_ITEM,
        SDB_TRADE_BROKER_BUY_IT_NOW => DBS_TRADE_BROKER_BUY_IT_NOW,
        SDB_TRADE_BROKER_START_DEAL => DBS_TRADE_BROKER_START_DEAL,
        SDB_TRADE_BROKER_CANCEL_DEAL => DBS_TRADE_BROKER_CANCEL_DEAL,
        _ => 0,
    };

    // =========================================================================================
    // Client -> Arbiter parsers. Each takes the BODY; a u16 field offset inside the packet is
    // therefore body[i] - 4. Every one returns null rather than throwing on a short body.
    // =========================================================================================

    /// <summary>C_TRADE_BROKER_DEAL_CONFIRM (0x9FA3): `[i32 TradeId][i32 DealStatus]`.</summary>
    public static (int tradeId, int dealStatus)? ParseCDealConfirm(byte[] body)
        => body.Length < 8 ? null : (BitConverter.ToInt32(body, 0), BitConverter.ToInt32(body, 4));

    /// <summary>C_TRADE_BROKER_REJECT_SUGGEST (0xB33F): `[i32 BuyerDbId][i32 TradeId]`.</summary>
    public static (int buyerDbId, int tradeId)? ParseCRejectSuggest(byte[] body)
        => body.Length < 8 ? null : (BitConverter.ToInt32(body, 0), BitConverter.ToInt32(body, 4));

    /// <summary>C_TRADE_BROKER_DEAL_PRICE_UPDATE (0xDB7F): `[i32 TradeId][i64 Price]` - the i64
    /// is UNALIGNED at packet 0x08, which is body 4.</summary>
    public static (int tradeId, long price)? ParseCDealPriceUpdate(byte[] body)
        => body.Length < 12 ? null : (BitConverter.ToInt32(body, 0), BitConverter.ToInt64(body, 4));

    /// <summary>C_TRADE_BROKER_SUGGEST_DEAL (0xA788): `[i32 TradeId][i64 SuggestPrice]`.</summary>
    public static (int tradeId, long suggestPrice)? ParseCSuggestDeal(byte[] body)
        => body.Length < 12 ? null : (BitConverter.ToInt32(body, 0), BitConverter.ToInt64(body, 4));

    /// <summary>C_TRADE_BROKER_INPUT_PRICE (0x6F34): `[i64 ItemDbId][i32 ItemTemplateId]`.</summary>
    public static (long itemDbId, int itemTemplateId)? ParseCInputPrice(byte[] body)
        => body.Length < 12 ? null : (BitConverter.ToInt64(body, 0), BitConverter.ToInt32(body, 8));

    /// <summary>C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE (0xE53F) and
    /// C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE (0x8CFB): `[i32 PageNo]`.</summary>
    public static int? ParseCListPage(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    /// <summary>C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT (0x983A) and
    /// C_TRADE_BROKER_WAITING_ITEM_LIST_SORT (0x8863): `[i32 Criteria][u8 IsAscend]`.</summary>
    public static (int criteria, bool ascending)? ParseCListSort(byte[] body)
        => body.Length < 5 ? null : (BitConverter.ToInt32(body, 0), body[4] != 0);

    // =========================================================================================
    // The two search filters
    // =========================================================================================

    /// <summary>
    /// C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW (0x76B4), fixed part 0x6C. Four ref slots then 25
    /// scalars; the guard lands exactly on the end of <c>Wearable</c>, which is what says the
    /// field list is complete. BROKER-DESIGN.md section 4.1 has the full offset table.
    /// </summary>
    public readonly record struct HistorySearch(
        string Keyword, string Category, string ItemTemplateIdList, string SecondaryKeyword,
        int MinLevel, int MaxLevel, int Grade, int UnidentifiedItem,
        bool Masterpiece, bool Enchantable, int MinItemLevel, int MaxItemLevel,
        int OptionPassivityType, long OptionValue, int OptionSearchCompareType,
        int MinExtractLevel, int MaxExtractLevel, int MinEnchantLevel, int MaxEnchantLevel,
        long MinPrice, long MaxPrice, bool ExactMatch, bool EquipmentSet,
        long MinTCatPrice, long MaxTCatPrice,
        bool UseDetailSearch, bool Awakened, bool Unbindable, bool Wearable);

    /// <summary>The fixed part of C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW, packet-relative.</summary>
    public const int HistorySearchSize = 0x6C;

    public static HistorySearch? ParseCHistorySearch(byte[] body)
    {
        if (body == null || body.Length < HistorySearchSize - ClientHeaderSize) return null;
        int I(int packetOff) => BitConverter.ToInt32(body, packetOff - ClientHeaderSize);
        long L(int packetOff) => BitConverter.ToInt64(body, packetOff - ClientHeaderSize);
        bool B(int packetOff) => body[packetOff - ClientHeaderSize] != 0;
        return new HistorySearch(
            ReadWString(body, 0x04 - ClientHeaderSize), ReadWString(body, 0x06 - ClientHeaderSize),
            ReadWString(body, 0x08 - ClientHeaderSize), ReadWString(body, 0x0A - ClientHeaderSize),
            I(0x0C), I(0x10), I(0x14), I(0x18), B(0x1C), B(0x1D), I(0x1E), I(0x22),
            I(0x26), L(0x2A), I(0x32), I(0x36), I(0x3A), I(0x3E), I(0x42),
            L(0x46), L(0x4E), B(0x56), B(0x57), L(0x58), L(0x60),
            B(0x68), B(0x69), B(0x6A), B(0x6B));
    }

    /// <summary>The fixed part of C_TRADE_BROKER_WAITING_ITEM_LIST_NEW (0x8DC7). Nine bytes
    /// longer than the history filter: it adds <c>CanBargainItemsOnly</c> and orders its scalars
    /// differently, so the two are NOT interchangeable even though the field names overlap.</summary>
    public const int WaitingSearchSize = 0x75;

    // =========================================================================================
    // World -> Arbiter (SDB_) parsers. Payload index = frame offset - 6.
    // =========================================================================================

    private static bool TooShort(ushort op, byte[] p)
        => p == null || p.Length < MinFrameLength(op) - FrameHeaderSize;

    /// <summary>SDB_TRADE_BROKER_REGISTER_ITEM (0x2817), frame 0x16:
    /// `i32 DlmId@0E, i32 OwnerDbId@12, ref ItemBinary@06`.</summary>
    public static (uint dlmId, int ownerDbId)? ParseSdbRegisterItem(byte[] p)
        => TooShort(SDB_TRADE_BROKER_REGISTER_ITEM, p) ? null
            : (BitConverter.ToUInt32(p, 0x0E - FrameHeaderSize), BitConverter.ToInt32(p, 0x12 - FrameHeaderSize));

    /// <summary>SDB_TRADE_BROKER_UNREGISTER_ITEM (0x2819), frame 0x1E:
    /// `i32 DlmId@0E, i32 OwnerDbId@12, i32 Step@16, i32 TradeId@1A, ref ItemBinary@06`.</summary>
    public static (uint dlmId, int ownerDbId, int step, int tradeId)? ParseSdbUnregisterItem(byte[] p)
        => TooShort(SDB_TRADE_BROKER_UNREGISTER_ITEM, p) ? null
            : (BitConverter.ToUInt32(p, 0x0E - FrameHeaderSize), BitConverter.ToInt32(p, 0x12 - FrameHeaderSize),
               BitConverter.ToInt32(p, 0x16 - FrameHeaderSize), BitConverter.ToInt32(p, 0x1A - FrameHeaderSize));

    /// <summary>SDB_TRADE_BROKER_CALC_SOLD_ITEM (0x281B) and _CALC_BOUGHT_ITEM (0x281D), frame
    /// 0x22 - one layout: `i32 DlmId@16, i32 OwnerDbId@1A, i32 Step@1E`, with two refs
    /// (CalcList, ItemBinary) ahead of them.</summary>
    public static (uint dlmId, int ownerDbId, int step)? ParseSdbCalcItem(ushort op, byte[] p)
    {
        if (op != SDB_TRADE_BROKER_CALC_SOLD_ITEM && op != SDB_TRADE_BROKER_CALC_BOUGHT_ITEM) return null;
        if (TooShort(op, p)) return null;
        return (BitConverter.ToUInt32(p, 0x16 - FrameHeaderSize), BitConverter.ToInt32(p, 0x1A - FrameHeaderSize),
                BitConverter.ToInt32(p, 0x1E - FrameHeaderSize));
    }

    /// <summary>
    /// SDB_TRADE_BROKER_BUY_IT_NOW (0x281F), frame 0x27: `i32 DlmId@0E, i32 OwnerDbId@12,
    /// i32 Step@16, i32 TradeId@1A, u8 InstantBuy@1E, ref ItemBinary@06, i64 TotalPriceWithTax@1F`.
    /// <b>The shipped .def is missing TotalPriceWithTax</b> - BROKER-DESIGN.md section 5.
    /// </summary>
    public static (uint dlmId, int ownerDbId, int step, int tradeId, bool instantBuy, long totalPriceWithTax)?
        ParseSdbBuyItNow(byte[] p)
        => TooShort(SDB_TRADE_BROKER_BUY_IT_NOW, p) ? null
            : (BitConverter.ToUInt32(p, 0x0E - FrameHeaderSize), BitConverter.ToInt32(p, 0x12 - FrameHeaderSize),
               BitConverter.ToInt32(p, 0x16 - FrameHeaderSize), BitConverter.ToInt32(p, 0x1A - FrameHeaderSize),
               p[0x1E - FrameHeaderSize] != 0, BitConverter.ToInt64(p, 0x1F - FrameHeaderSize));

    /// <summary>SDB_TRADE_BROKER_START_DEAL (0x2821), frame 0x12 - no DlmId:
    /// `i32 BuyerDbId@06, i32 SellerDbId@0A, i32 TradeId@0E`.</summary>
    public static (int buyerDbId, int sellerDbId, int tradeId)? ParseSdbStartDeal(byte[] p)
        => TooShort(SDB_TRADE_BROKER_START_DEAL, p) ? null
            : (BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4), BitConverter.ToInt32(p, 8));

    /// <summary>SDB_TRADE_BROKER_CANCEL_DEAL (0x2824), frame 0x0E - no DlmId:
    /// `i32 UserDbId@06, i32 TradeId@0A`.</summary>
    public static (int userDbId, int tradeId)? ParseSdbCancelDeal(byte[] p)
        => TooShort(SDB_TRADE_BROKER_CANCEL_DEAL, p) ? null
            : (BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4));

    // =========================================================================================
    // Arbiter -> World (DBS_) builders. Fixed part only: the ref payloads (ItemBinary, TradeData,
    // CalcItemList) are ItemData-shaped and were not decoded (BROKER-DESIGN.md section 7), so a
    // handler passes the request's own bytes back rather than inventing a record.
    // =========================================================================================


    // =========================================================================================
    // T70: the TradeData / CalcItemList record, from cap_social3.log.
    //
    // T53 called the broker's ref payloads "ItemData-shaped and not decoded" and T55 decoded the
    // ItemBinary half (it is the 856-byte ItemTransactionAtom). The OTHER half - what
    // DBS_TRADE_BROKER_UNREGISTER_ITEM calls TradeData and the two CALC replies call
    // CalcItemList - is this 392-byte record, and cap_social3.log is the first capture that
    // contains one.
    //
    // Three distinct listings appear (ids 1 and 3 populated, plus the cleared form the Step-2
    // unregister returns). Cross-checking them is what separates a field from heap:
    //
    //   +0x000 i32   TradeId          1 / 3 / 0
    //   +0x004 i32   SellerDbId       2 / 2 / 0
    //   +0x008 wstr  SellerName       "Test", 37 wchars of room (0x4A bytes, to +0x51)
    //   +0x052 u16   -- uninitialised Arbiter stack: 25119 / 25103, and still set in the
    //   +0x05C u16   -- CLEARED record where every real field is zero. Not fields.
    //   +0x060 i64   ItemDbId         10027 / 10029 / 0
    //   +0x068 i32   TemplateId       200997 / 139093 / 0
    //   +0x06C i32   Amount           1 / 1 / 0
    //   +0x0B8 8xu16 RegisterTime     {year, month, day, hour, minute, second, 0, 0} -
    //                                 2026-09-16 22:32:16 and 22:32:47
    //   +0x0C8 i64   Price            10001 / 1 / 0
    //
    // Price is pinned by the other end of the trade: SDB_TRADE_BROKER_BUY_IT_NOW's
    // TotalPriceWithTax for listing 3 is 1, and listing 3's +0xC8 is 1.
    //
    // THE TWO-STEP PROTOCOL. Every broker operation except REGISTER runs twice, and the Step
    // field says which pass it is (cap_social3.log seq 1880..1883, 1946..1949, 2000..2003):
    //
    //   Step 1  request carries NO atoms (the ref is offset = frame length, count 0)
    //           reply carries the TradeData record and NO atoms
    //   Step 2  request carries the atoms World built from what Step 1 told it
    //           reply carries the TradeData record AND those atoms echoed
    //
    // So Step 1 is "read me the listing" and Step 2 is "commit". A handler that answers Step 1
    // with an empty TradeData gives World nothing to build the Step-2 atoms from, which is why
    // T55's empty forms could never complete a purchase.
    // =========================================================================================


    // =========================================================================================
    // T72: the S_ list bodies, decoded from cap_social3_client.log.
    //
    // Both lists use TERA's ordinary array encoding, with PACKET-relative offsets:
    //   body   [u16 count][u16 firstElementOffset] then the packet's own scalars
    //   element[u16 thisOffset][u16 nextOffset (0 on the last)][u16 nameOffset] then the fields,
    //          then the NUL-terminated UTF-16LE seller name at nameOffset.
    // The fixed part of an element is a constant size and the name follows it, so an element is
    // fixedSize + (name.Length + 1) * 2 bytes long.
    //
    // Field offsets below were cross-checked across the three listings in seq 1419 (trade ids
    // 1, 2 and 3, two different prices and three different templates); every one of them lands
    // at the same element-relative offset in all three, which is what makes them fields rather
    // than coincidences.
    // =========================================================================================

    /// <summary>Bytes of an S_TRADE_BROKER_WAITING_ITEM_LIST element before the seller name.</summary>
    public const int WaitingElementFixedSize = 92;
    public const int WlTradeId = 6;
    public const int WlItemDbId = 10;
    public const int WlTemplateId = 18;
    public const int WlAmount = 22;
    public const int WlPrice = 35;
    /// <summary>+43: price plus the broker's cut. 10001 comes back 11001 and 1 comes back 1, so
    /// the cut is <c>price / 10</c> truncated - and it is the same number
    /// SDB_TRADE_BROKER_BUY_IT_NOW carries as TotalPriceWithTax.</summary>
    public const int WlTotalPriceWithTax = 43;
    public const int WlSellerDbId = 55;

    /// <summary>
    /// T141. Two u8 flags in the waiting-list element that this builder leaves 0, measured
    /// across 85 live rows.
    /// <list type="bullet">
    /// <item><b>+51 varies per row.</b> On nine live Classic+ search pages (classic_live4.log
    /// seq 5938, 5954, 5979, 5983, 5993, 5996, 5999, 6002, 6034) it is 1 on 40 of 80 rows and
    /// 0 on the other 40, and the split follows neither template, amount, price, seller nor
    /// page. It is a per-listing attribute and nothing captured says which one.</item>
    /// <item><b>+83 is 1 on all 80</b> of those rows.</item>
    /// <item>Both are <b>0 on all 5 rows</b> of this project's own 100.02 listings
    /// (cap_social3_client.log seq 1419 and 1472), which is what the builder emits and what
    /// <c>T141_the_live_waiting_page_round_trips</c> pins.</item>
    /// </list>
    /// They are named here so the tail of the element is not mistaken for padding. Driving
    /// either one needs a meaning first, and a per-row source for +51.
    /// </summary>
    public const int WlUnknownFlagA = 51;
    /// <summary>See <see cref="WlUnknownFlagA"/>. 1 on every live Classic+ row, 0 on ours.</summary>
    public const int WlUnknownFlagB = 83;

    /// <summary>Bytes of an S_TRADE_BROKER_BOUGHT_ITEM_LIST element before the seller name.</summary>
    public const int BoughtElementFixedSize = 98;
    public const int BlTradeId = 6;
    public const int BlItemDbId = 14;
    public const int BlTemplateId = 22;
    public const int BlAmount = 26;
    /// <summary>+39 and +60: UNIX seconds, 15 apart in the capture - which is exactly the gap
    /// between the TradeData record's RegisterTime (22:32:47) and its SoldTime (22:33:02).</summary>
    public const int BlRegisterTime = 39;
    public const int BlPrice = 47;
    public const int BlSellerDbId = 55;
    public const int BlSoldFlag = 59;
    public const int BlSoldTime = 60;
    /// <summary>
    /// +68, i64: what the BUYER paid, i.e. <see cref="PriceWithTax"/> of the listing price and
    /// not the price itself. T141 settled it on a live Classic+ purchase: classic_live4.log's
    /// bought row (seq 6056) reads price 6562 and TotalPaid 7546, and the same trade's search
    /// row on seq 5938 carries TotalPriceWithTax 7546 - the same number, from the same frame
    /// set. The one row T74 had was priced at 1, where price and price-plus-fee are both 1,
    /// which is why it read as the price.
    /// </summary>
    public const int BlTotalPaid = 68;

    /// <summary>
    /// The broker's cut as whole percent. <b>10 on this project's own 100.02 server</b>, which
    /// is what the shipped captures prove (cap_social3_client.log: 10001 comes back 11001, and
    /// 1 comes back 1). T141 measured a live Classic+ server at <b>15</b>: all 80 rows of
    /// classic_live4.log's nine search pages satisfy <c>tax == price + price * 15 / 100</c>
    /// exactly and none of them satisfy the tenth rule. So the shape is
    /// <c>price + price * rate / 100</c> truncated, and the rate is server configuration.
    /// </summary>
    public const int DefaultBrokerFeePercent = 10;

    /// <summary>Overrides <see cref="BrokerFeePercent"/>, 0..100. Classic+ runs 15.</summary>
    public const string BrokerFeeEnvVariable = "TERASHARP_BROKER_FEE_PERCENT";

    private static int? _feePercent;

    /// <summary>
    /// <see cref="BrokerFeeEnvVariable"/> when it parses to 0..100, else
    /// <see cref="DefaultBrokerFeePercent"/>. Cached, because it is read once per listed row.
    /// </summary>
    public static int BrokerFeePercent
    {
        get
        {
            if (_feePercent is int c) return c;
            int v = DefaultBrokerFeePercent;
            var raw = Environment.GetEnvironmentVariable(BrokerFeeEnvVariable);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int parsed)
                && parsed >= 0 && parsed <= 100) v = parsed;
            _feePercent = v;
            return v;
        }
    }

    /// <summary>Tests only: forget the cached fee so the next read takes the env again.</summary>
    public static void ResetBrokerFeePercent() => _feePercent = null;

    /// <summary>The broker's cut, truncated. See <see cref="BrokerFeePercent"/>.</summary>
    public static long PriceWithTax(long price) => price + price * BrokerFeePercent / 100;

    /// <summary>
    /// S_TRADE_BROKER_WAITING_ITEM_LIST (0xFD2C) - the search result page. The empty form is
    /// <c>[count 0][offset 0][page][totalPage]</c>, 12 body bytes, which is seq 1231 exactly.
    /// </summary>
    public static byte[] BuildSWaitingItemListBody(
        IReadOnlyList<(int TradeId, long ItemDbId, int TemplateId, int Amount, long Price,
                       int SellerDbId, string SellerName)>? rows,
        uint currentPage = 0, uint totalPage = 1)
    {
        rows ??= Array.Empty<(int, long, int, int, long, int, string)>();
        const int header = 12;
        var sizes = new int[rows.Count];
        int total = header;
        for (int i = 0; i < rows.Count; i++)
        {
            sizes[i] = WaitingElementFixedSize + NameBytes(rows[i].SellerName);
            total += sizes[i];
        }

        var p = new byte[total];
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : ClientHeaderSize + header)).CopyTo(p, 2);
        BitConverter.GetBytes(currentPage).CopyTo(p, 4);
        BitConverter.GetBytes(totalPage).CopyTo(p, 8);

        int at = header;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int here = ClientHeaderSize + at;
            int next = i + 1 < rows.Count ? ClientHeaderSize + at + sizes[i] : 0;
            BitConverter.GetBytes((ushort)here).CopyTo(p, at);
            BitConverter.GetBytes((ushort)next).CopyTo(p, at + 2);
            BitConverter.GetBytes((ushort)(here + WaitingElementFixedSize)).CopyTo(p, at + 4);
            BitConverter.GetBytes(r.TradeId).CopyTo(p, at + WlTradeId);
            BitConverter.GetBytes(r.ItemDbId).CopyTo(p, at + WlItemDbId);
            BitConverter.GetBytes(r.TemplateId).CopyTo(p, at + WlTemplateId);
            BitConverter.GetBytes(r.Amount).CopyTo(p, at + WlAmount);
            BitConverter.GetBytes(r.Price).CopyTo(p, at + WlPrice);
            BitConverter.GetBytes(PriceWithTax(r.Price)).CopyTo(p, at + WlTotalPriceWithTax);
            BitConverter.GetBytes(r.SellerDbId).CopyTo(p, at + WlSellerDbId);
            WriteName(p, at + WaitingElementFixedSize, r.SellerName);
            at += sizes[i];
        }
        return p;
    }

    /// <summary>
    /// S_TRADE_BROKER_BOUGHT_ITEM_LIST (0x53C0) - what this character has bought and not yet
    /// collected. Its empty form is the 4-byte <c>[count 0][offset 0]</c> (seq 1491): unlike the
    /// waiting list it carries no page scalars.
    /// </summary>
    public static byte[] BuildSBoughtItemListBody(
        IReadOnlyList<(int TradeId, long ItemDbId, int TemplateId, int Amount, long Price,
                       int SellerDbId, string SellerName, long RegisterTime, long SoldTime)>? rows)
    {
        rows ??= Array.Empty<(int, long, int, int, long, int, string, long, long)>();
        const int header = 4;
        var sizes = new int[rows.Count];
        int total = header;
        for (int i = 0; i < rows.Count; i++)
        {
            sizes[i] = BoughtElementFixedSize + NameBytes(rows[i].SellerName);
            total += sizes[i];
        }

        var p = new byte[total];
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : ClientHeaderSize + header)).CopyTo(p, 2);

        int at = header;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int here = ClientHeaderSize + at;
            int next = i + 1 < rows.Count ? ClientHeaderSize + at + sizes[i] : 0;
            BitConverter.GetBytes((ushort)here).CopyTo(p, at);
            BitConverter.GetBytes((ushort)next).CopyTo(p, at + 2);
            BitConverter.GetBytes((ushort)(here + BoughtElementFixedSize)).CopyTo(p, at + 4);
            BitConverter.GetBytes(r.TradeId).CopyTo(p, at + BlTradeId);
            BitConverter.GetBytes(r.ItemDbId).CopyTo(p, at + BlItemDbId);
            BitConverter.GetBytes(r.TemplateId).CopyTo(p, at + BlTemplateId);
            BitConverter.GetBytes(r.Amount).CopyTo(p, at + BlAmount);
            BitConverter.GetBytes(r.RegisterTime).CopyTo(p, at + BlRegisterTime);
            BitConverter.GetBytes(r.Price).CopyTo(p, at + BlPrice);
            BitConverter.GetBytes(r.SellerDbId).CopyTo(p, at + BlSellerDbId);
            p[at + BlSoldFlag] = 1;
            BitConverter.GetBytes(r.SoldTime).CopyTo(p, at + BlSoldTime);
            BitConverter.GetBytes(PriceWithTax(r.Price)).CopyTo(p, at + BlTotalPaid);
            WriteName(p, at + BoughtElementFixedSize, r.SellerName);
            at += sizes[i];
        }
        return p;
    }

    /// <summary>
    /// S_TRADE_BROKER_HIGHEST_ITEM_LEVEL (0x7E53): one <b>float</b>, 469.0 in both captures
    /// (seq 134 and 3217). It is the cap the search window's item-level slider runs to, which is
    /// static server config rather than anything about this character.
    /// </summary>
    public const float HighestItemLevelDefault = 469.0f;

    public static byte[] BuildSHighestItemLevelBody(float level = HighestItemLevelDefault)
        => BitConverter.GetBytes(level);

    /// <summary>S_TRADE_BROKER_BUY_IT_NOW (0xEC54): one byte, 1 on the captured success
    /// (seq 1469).</summary>
    public static byte[] BuildSBuyItNowBody(bool ok) => new[] { (byte)(ok ? 1 : 0) };

    private static int NameBytes(string? name) => ((name?.Length ?? 0) + 1) * 2;

    private static void WriteName(byte[] p, int at, string? name)
    {
        foreach (char ch in name ?? string.Empty) { p[at++] = (byte)ch; p[at++] = (byte)(ch >> 8); }
    }

    /// <summary>The TradeData / CalcItemList record, 0x188 bytes.</summary>
    public const int TradeDataSize = 0x188;

    // ---- T71: the payload bytes ahead of the first ref target in each request ----
    /// <summary>SDB_TRADE_BROKER_REGISTER_ITEM, frame 0x16.</summary>
    public const int RegisterRequestHeader = 0x16 - FrameHeaderSize;      // 16
    /// <summary>SDB_TRADE_BROKER_UNREGISTER_ITEM, frame 0x1E.</summary>
    public const int UnregisterRequestHeader = 0x1E - FrameHeaderSize;    // 24
    /// <summary>SDB_TRADE_BROKER_CALC_*_ITEM, frame 0x22.</summary>
    public const int CalcRequestHeader = 0x22 - FrameHeaderSize;          // 28
    /// <summary>SDB_TRADE_BROKER_BUY_IT_NOW, frame 0x27.</summary>
    public const int BuyItNowRequestHeader = 0x27 - FrameHeaderSize;      // 33

    /// <summary>
    /// The listing PRICE, inside the op-53 (<c>TS_TRADE_BROKER_REGISTER</c>) atom of a register
    /// batch. T71: SDB_TRADE_BROKER_REGISTER_ITEM has no price field of its own - the dumper
    /// lists only DlmId, OwnerDbId and ItemBinary - so this is the only place it arrives.
    /// Confirmed on all three registers in cap_social3.log: listings 1 and 2 carry 10001 here
    /// and come back with Price 10001 in their TradeData, listing 3 carries 1 and comes back 1.
    /// </summary>
    public const int RegisterAtomPriceOffset = 0x288;

    /// <summary>
    /// The register batch's op-53 atom, or null. Everything the listing needs is in it: the item
    /// id, template and amount at the usual atom-head offsets, and the price at
    /// <see cref="RegisterAtomPriceOffset"/>.
    /// </summary>
    public static (long ItemDbId, int TemplateId, int Amount, long Price)? ReadRegisterAtom(byte[]? atoms)
    {
        if (atoms == null) return null;
        for (int o = 0; o + AtomSize <= atoms.Length; o += AtomSize)
        {
            if (BitConverter.ToUInt32(atoms, o + WarehouseHandlers.AtomOp) != WarehouseHandlers.TsBrokerRegister)
                continue;
            long amount = BitConverter.ToInt64(atoms, o + WarehouseHandlers.AtomDelta);
            return (BitConverter.ToInt64(atoms, o + WarehouseHandlers.AtomItemDbId),
                    BitConverter.ToInt32(atoms, o + WarehouseHandlers.AtomTemplateId),
                    amount > 0 && amount <= int.MaxValue ? (int)amount : 1,
                    BitConverter.ToInt64(atoms, o + RegisterAtomPriceOffset));
        }
        return null;
    }

    /// <summary>
    /// The <c>CalcList</c> of an SDB_TRADE_BROKER_CALC_* request: a packed array of i32 TradeIds
    /// behind the ref at payload 0. Both captured calcs carry exactly one.
    /// </summary>
    public static IReadOnlyList<int> ParseCalcList(byte[] p)
    {
        if (p == null || p.Length < 8) return Array.Empty<int>();
        int start = (int)BitConverter.ToUInt32(p, 0) - FrameHeaderSize;
        int bytes = (int)BitConverter.ToUInt32(p, 4);
        if (bytes <= 0 || start < CalcRequestHeader || bytes > p.Length - start) return Array.Empty<int>();
        var ids = new int[bytes / 4];
        for (int i = 0; i < ids.Length; i++) ids[i] = BitConverter.ToInt32(p, start + i * 4);
        return ids;
    }

    /// <summary>The cleared TradeData a successful unregister step 2 returns: 0x188 zero bytes.
    /// cap_social3.log seq 2003 - the record is present and every field in it is zero.</summary>
    public static byte[] BuildClearedTradeData() => new byte[TradeDataSize];

    /// <summary>
    /// The CalcItemList form: a TradeData record with the settlement block stamped on top.
    /// <paramref name="calcState"/> is <see cref="CalcStateSold"/> for the seller's collect and
    /// <see cref="CalcStateBought"/> for the buyer's - the one field the two captured calc
    /// records disagree on.
    /// </summary>
    public static byte[] BuildCalcItemData(byte[] tradeData, int calcState, int buyerDbId,
                                           long soldPrice, DateTime soldAt, bool instantBuy = true)
    {
        ArgumentNullException.ThrowIfNull(tradeData);
        var r = new byte[TradeDataSize];
        Array.Copy(tradeData, r, Math.Min(tradeData.Length, TradeDataSize));
        BitConverter.GetBytes(calcState).CopyTo(r, TdCalcState);
        BitConverter.GetBytes(buyerDbId).CopyTo(r, TdBuyerDbId);
        BitConverter.GetBytes(instantBuy ? 1 : 0).CopyTo(r, TdBuyerFlag);
        WriteSystemTime(r, TdSoldTime, soldAt);
        BitConverter.GetBytes(soldPrice).CopyTo(r, TdSoldPrice);
        BitConverter.GetBytes(soldPrice).CopyTo(r, TdCalcMoney);
        return r;
    }

    /// <summary>The 8xu16 {y, m, d, h, mi, s, 0, 0} both time fields use.</summary>
    private static void WriteSystemTime(byte[] r, int at, DateTime t)
    {
        if (t == default) return;
        foreach (ushort v in new[] { (ushort)t.Year, (ushort)t.Month, (ushort)t.Day,
                                     (ushort)t.Hour, (ushort)t.Minute, (ushort)t.Second })
        { BitConverter.GetBytes(v).CopyTo(r, at); at += 2; }
    }

    public const int TdTradeId = 0x000;
    public const int TdSellerDbId = 0x004;
    public const int TdSellerName = 0x008;
    /// <summary>37 wchars including the NUL, the same width the parcel record's names use.</summary>
    public const int TdNameMaxChars = 0x25;
    public const int TdItemDbId = 0x060;
    public const int TdTemplateId = 0x068;
    public const int TdAmount = 0x06C;
    public const int TdRegisterTime = 0x0B8;
    public const int TdPrice = 0x0C8;

    // ---- T71: the settlement half, set only in the CalcItemList form ----
    // The two CALC replies carry the SAME 0x188 record as TradeData with six more fields filled
    // in. Diffing seq 1947 and 1905 against seq 1881 - the same listing 3, one hour of the same
    // capture - isolates them exactly, and the two calc records differ from each other in one
    // field only (+0x58), which is the side that is collecting.
    /// <summary>+0x58: 3 on the seller's CALC_SOLD, 2 on the buyer's CALC_BOUGHT, 0 in a plain
    /// TradeData.</summary>
    public const int TdCalcState = 0x058;
    public const int TdBuyerDbId = 0x0D0;
    /// <summary>+0xD4: 1 on both captured calcs. Instant-buy, on the evidence of the
    /// <c>InstantBuy</c> byte the matching BUY_IT_NOW carried.</summary>
    public const int TdBuyerFlag = 0x0D4;
    /// <summary>+0xD8: the same 8xu16 form as RegisterTime - 2026-09-16 22:33:02 in both.</summary>
    public const int TdSoldTime = 0x0D8;
    public const int TdSoldPrice = 0x0E8;
    /// <summary>+0x100: the money the collecting side actually receives. 1 in both captures,
    /// which is also the price - this capture has no visible broker tax.</summary>
    public const int TdCalcMoney = 0x100;

    public const int CalcStateSold = 3;
    public const int CalcStateBought = 2;

    public readonly record struct TradeData(
        int TradeId, int SellerDbId, string SellerName, long ItemDbId, int TemplateId,
        int Amount, long Price);

    /// <summary>Reads one 0x188-byte TradeData record. Null when the buffer is short.</summary>
    public static TradeData? ParseTradeData(byte[] rec, int at = 0)
    {
        if (rec == null || at < 0 || at > rec.Length - TradeDataSize) return null;
        return new TradeData(
            BitConverter.ToInt32(rec, at + TdTradeId),
            BitConverter.ToInt32(rec, at + TdSellerDbId),
            TradeWString(rec, at + TdSellerName, TdNameMaxChars),
            BitConverter.ToInt64(rec, at + TdItemDbId),
            BitConverter.ToInt32(rec, at + TdTemplateId),
            BitConverter.ToInt32(rec, at + TdAmount),
            BitConverter.ToInt64(rec, at + TdPrice));
    }

    /// <summary>
    /// Builds one TradeData record. <paramref name="registerTime"/> is written in the
    /// {year, month, day, hour, minute, second} form the capture carries at +0xB8; pass
    /// <c>default</c> to leave it zero. The two uninitialised u16s at +0x52 and +0x5C are left
    /// zero on purpose - World never reads them, and re-sending another server's stack is the
    /// leak T51 spent a task removing from the guild blob.
    /// </summary>
    public static byte[] BuildTradeData(int tradeId, int sellerDbId, string? sellerName,
                                        long itemDbId, int templateId, int amount, long price,
                                        DateTime registerTime = default)
    {
        var r = new byte[TradeDataSize];
        BitConverter.GetBytes(tradeId).CopyTo(r, TdTradeId);
        BitConverter.GetBytes(sellerDbId).CopyTo(r, TdSellerDbId);
        WriteTradeWString(r, TdSellerName, sellerName, TdNameMaxChars);
        BitConverter.GetBytes(itemDbId).CopyTo(r, TdItemDbId);
        BitConverter.GetBytes(templateId).CopyTo(r, TdTemplateId);
        BitConverter.GetBytes(amount).CopyTo(r, TdAmount);
        BitConverter.GetBytes(price).CopyTo(r, TdPrice);
        WriteSystemTime(r, TdRegisterTime, registerTime);
        return r;
    }

    private static string TradeWString(byte[] rec, int at, int maxChars)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < maxChars && at + i * 2 + 1 < rec.Length; i++)
        {
            char ch = (char)(rec[at + i * 2] | (rec[at + i * 2 + 1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void WriteTradeWString(byte[] rec, int at, string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return;
        int n = Math.Min(value!.Length, maxChars - 1);
        for (int i = 0; i < n; i++) BitConverter.GetBytes((ushort)value[i]).CopyTo(rec, at + i * 2);
    }

    /// <summary>
    /// The size of one <c>ItemTransactionAtom</c>, which is what the broker's <c>ItemBinary</c>
    /// ref actually carries - the same 856-byte record the warehouse and item paths use
    /// (status/INVENTORY-DESIGN.md, status/MAIL-WAREHOUSE.md). T53 said "ItemData-shaped and not
    /// decoded"; T55 read the writers and it is the atom, which is already decoded.
    /// The proof is the stride in every one of the five reply writers: the copy loop advances by
    /// <c>0x358</c> and the byte-length slot is filled with <c>count * 0x358</c>.
    /// </summary>
    public const int AtomSize = 0x358;

    /// <summary>
    /// DBS_TRADE_BROKER_REGISTER_ITEM (0x2818), frame 0x13. Writer FUN_1407658c0
    /// (Arb_part_064.c:9859), in its own order:
    /// <code>
    ///   [06] u32 ItemBinary.offset   written 0, then backpatched to the running frame length
    ///   [0A] u32 ItemBinary.byteLen  count * 0x358
    ///   [0E] u32 DlmId
    ///   [12] u8  Success
    ///   [13] ItemTransactionAtom[]
    /// </code>
    /// <b>The offset slot is backpatched unconditionally</b>, before the emptiness check, so an
    /// empty answer carries offset 0x13 and length 0 - not two zeros. That is the same convention
    /// DBS_INIT_GUILD_DATA and the guild init arrays follow (status/GUILD-DESIGN.md section 11.4),
    /// and it is the one thing a hand-written empty form gets wrong.
    /// </summary>
    public static byte[] BuildDbsRegisterItem(uint dlmId, bool success, byte[]? atoms = null)
    {
        var body = atoms ?? Array.Empty<byte>();
        var p = new byte[(0x13 - FrameHeaderSize) + body.Length];
        BitConverter.GetBytes(0x13).CopyTo(p, 0x06 - FrameHeaderSize);
        BitConverter.GetBytes(body.Length).CopyTo(p, 0x0A - FrameHeaderSize);
        BitConverter.GetBytes(dlmId).CopyTo(p, 0x0E - FrameHeaderSize);
        p[0x12 - FrameHeaderSize] = (byte)(success ? 1 : 0);
        body.CopyTo(p, 0x13 - FrameHeaderSize);
        return p;
    }

    /// <summary>
    /// DBS_TRADE_BROKER_UNREGISTER_ITEM (0x281A), _CALC_SOLD_ITEM (0x281C), _CALC_BOUGHT_ITEM
    /// (0x281E) and _BUY_IT_NOW (0x2820) are one writer four times over (FUN_1406f60f0 and its
    /// three twins, Arb_part_060.c:12177/12330/12563/12794), frame 0x1F:
    /// <code>
    ///   [06] u32 refA.offset   backpatched to the running length (= 0x1F)
    ///   [0A] u32 refA.byteLen  raw bytes, written with FUN_1403c98b0
    ///   [0E] u32 refB.offset   backpatched (= 0x1F + refA.byteLen)
    ///   [12] u32 refB.byteLen  count * 0x358
    ///   [16] u32 DlmId
    ///   [1A] u32 Step
    ///   [1E] u8  Success
    /// </code>
    /// refA is the dumper's first ref - <c>TradeData</c> on the unregister/buy replies,
    /// <c>CalcItemList</c> on the two calc replies - and refB is <c>ItemBinary</c>, the atom
    /// array. <b>Both offset slots are backpatched whether or not there is anything to point
    /// at</b>, so an empty answer carries 0x1F in both and 0 in both lengths.
    ///
    /// <para>Echo the <c>Step</c> you were given. Its values were never traced
    /// (BROKER-DESIGN.md section 3) and inventing one tells World a stage completed that did
    /// not.</para>
    /// </summary>
    public static byte[] BuildDbsStepAck(uint dlmId, int step, bool success,
                                         byte[]? refA = null, byte[]? atoms = null)
    {
        var a = refA ?? Array.Empty<byte>();
        var b = atoms ?? Array.Empty<byte>();
        var p = new byte[(0x1F - FrameHeaderSize) + a.Length + b.Length];
        BitConverter.GetBytes(0x1F).CopyTo(p, 0x06 - FrameHeaderSize);
        BitConverter.GetBytes(a.Length).CopyTo(p, 0x0A - FrameHeaderSize);
        BitConverter.GetBytes(0x1F + a.Length).CopyTo(p, 0x0E - FrameHeaderSize);
        BitConverter.GetBytes(b.Length).CopyTo(p, 0x12 - FrameHeaderSize);
        BitConverter.GetBytes(dlmId).CopyTo(p, 0x16 - FrameHeaderSize);
        BitConverter.GetBytes(step).CopyTo(p, 0x1A - FrameHeaderSize);
        p[0x1E - FrameHeaderSize] = (byte)(success ? 1 : 0);
        a.CopyTo(p, 0x1F - FrameHeaderSize);
        b.CopyTo(p, 0x1F - FrameHeaderSize + a.Length);
        return p;
    }

    /// <summary>
    /// The refusal every one of the five DlmId-carrying requests gets while there is no listings
    /// table: the ids echoed, both refs empty, <c>Success = 0</c>. Returns null for an opcode
    /// that is not one of the five.
    ///
    /// <para>This is what stops the character's DB queue wedging. World does not need the answer
    /// to be yes - it needs an answer with its own DlmId in it, or DLMExistManager::Find misses
    /// and the item never completes (status/HANDOFF.md section 1).</para>
    /// </summary>
    public static byte[]? BuildEmptyRefusal(ushort request, uint dlmId, int step)
        => request switch
        {
            SDB_TRADE_BROKER_REGISTER_ITEM => BuildDbsRegisterItem(dlmId, success: false),
            SDB_TRADE_BROKER_UNREGISTER_ITEM => BuildDbsStepAck(dlmId, step, success: false),
            SDB_TRADE_BROKER_CALC_SOLD_ITEM => BuildDbsStepAck(dlmId, step, success: false),
            SDB_TRADE_BROKER_CALC_BOUGHT_ITEM => BuildDbsStepAck(dlmId, step, success: false),
            SDB_TRADE_BROKER_BUY_IT_NOW => BuildDbsStepAck(dlmId, step, success: false),
            _ => null,
        };

    /// <summary>DBS_TRADE_BROKER_CANCEL_DEAL (0x282C), frame 0x0A: `i32 UserDbId@06`.</summary>
    public static byte[] BuildDbsCancelDeal(int userDbId) => BitConverter.GetBytes(userDbId);

    /// <summary>DBS_TRADE_BROKER_ACCEPT_DEAL (0x2823), frame 0x1E - a PUSH, not a reply:
    /// `i32 UserDbId@06, i32 BuyerDbId@0A, i32 SellerDbId@0E, i32 TradeId@12, i64 AgreedPrice@16`.</summary>
    public static byte[] BuildDbsAcceptDeal(int userDbId, int buyerDbId, int sellerDbId, int tradeId, long agreedPrice)
    {
        var p = new byte[0x1E - FrameHeaderSize];
        BitConverter.GetBytes(userDbId).CopyTo(p, 0);
        BitConverter.GetBytes(buyerDbId).CopyTo(p, 4);
        BitConverter.GetBytes(sellerDbId).CopyTo(p, 8);
        BitConverter.GetBytes(tradeId).CopyTo(p, 12);
        BitConverter.GetBytes(agreedPrice).CopyTo(p, 16);
        return p;
    }

    // =========================================================================================
    // Arbiter -> client reply BODIES (T55). Built by hand rather than through SendByDef for the
    // reason ParcelHandlers gives: several broker .def files are wrong (section 5), and bytes
    // built here are testable without a def registry.
    //
    // Every one is the EMPTY form - an Arbiter with no listings table. The shapes are from the
    // S_ dumpers; the values are all zero, which is what "you have nothing" looks like.
    // =========================================================================================

    /// <summary>
    /// An ARRAY field reserves two u16 slots in the order <b>(count, offset)</b> - the reverse of
    /// a bytes field's (offset, count). status/GUILD-DESIGN.md section 5.3 proves it from
    /// S_GET_USER_GUILD_LOGO's writer, and Protocol/DefinitionWriter.cs already does it this way.
    /// An empty array is (0, 0): with no elements there is nothing for the offset to point at,
    /// and the real writers leave it at the reserved zero.
    /// </summary>
    public const int EmptyArraySlots = 4;

    /// <summary>S_TRADE_BROKER_BOUGHT_ITEM_LIST (0x53C0) and S_TRADE_BROKER_REGISTERED_ITEM_LIST
    /// (0xCDEA), min total 8: one array and nothing else.</summary>
    public static byte[] BuildSEmptyItemListBody() => new byte[EmptyArraySlots];

    // ===================================================================================
    // T81: the seller's two tabs, from cap_social3_client2.log - the SELLER's client of the
    // same session cap_social3_client.log was the buyer's. Both were empty in the buyer's
    // capture, which is why T72 and T74 left them on the empty form; here they have rows.
    // ===================================================================================

    /// <summary>
    /// One row of S_TRADE_BROKER_REGISTERED_ITEM_LIST - Active Listings. <b>66 bytes, and no
    /// name</b>: this is the seller's own list, so it repeats neither his name nor a buyer's,
    /// and the element opens with two u16s rather than the three the search list uses.
    /// <code>
    ///   +0  u16 here     +2  u16 next (0 = last)
    ///   +4  i32 TradeId          +8  i32 reserved (0)
    ///   +12 i64 ItemDbId         +20 i32 TemplateId    +24 i32 Amount
    ///   +28 i32 reserved (0)     +32 i64 RegisterTime  +40 i64 Price
    ///   +48 .. +65 zero
    /// </code>
    /// Pinned by six rows across frames 1258 (1 row, 74 B), 1436 (2, 140 B), 1457 (3, 206 B),
    /// 1620 (2) and 1627 (1) - the same three listings T71 put in the table, trade 1 / 2 / 3.
    /// </summary>
    public const int RegisteredElementSize = 66;
    public const int RlTradeId = 4;
    public const int RlItemDbId = 12;
    public const int RlTemplateId = 20;
    public const int RlAmount = 24;
    public const int RlRegisterTime = 32;
    public const int RlPrice = 40;

    /// <summary>
    /// S_TRADE_BROKER_REGISTERED_ITEM_LIST (0xCDEA). Rows come out <b>oldest first</b> - frames
    /// 1436 and 1457 list trade 1, then 2, then 3 - which is the opposite of the search list's
    /// newest-first ordering.
    /// </summary>
    public static byte[] BuildSRegisteredItemListBody(
        IReadOnlyList<(int TradeId, long ItemDbId, int TemplateId, int Amount, long Price,
                       long RegisterTime)>? rows)
    {
        rows ??= Array.Empty<(int, long, int, int, long, long)>();
        const int header = EmptyArraySlots;
        var p = new byte[header + rows.Count * RegisteredElementSize];
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : ClientHeaderSize + header)).CopyTo(p, 2);

        int at = header;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int here = ClientHeaderSize + at;
            int next = i + 1 < rows.Count ? here + RegisteredElementSize : 0;
            BitConverter.GetBytes((ushort)here).CopyTo(p, at);
            BitConverter.GetBytes((ushort)next).CopyTo(p, at + 2);
            BitConverter.GetBytes(r.TradeId).CopyTo(p, at + RlTradeId);
            BitConverter.GetBytes(r.ItemDbId).CopyTo(p, at + RlItemDbId);
            BitConverter.GetBytes(r.TemplateId).CopyTo(p, at + RlTemplateId);
            BitConverter.GetBytes(r.Amount).CopyTo(p, at + RlAmount);
            BitConverter.GetBytes(r.RegisterTime).CopyTo(p, at + RlRegisterTime);
            BitConverter.GetBytes(r.Price).CopyTo(p, at + RlPrice);
            at += RegisteredElementSize;
        }
        return p;
    }

    /// <summary>
    /// One row of S_TRADE_BROKER_SOLD_ITEM_LIST. It is the <b>bought-list element with one more
    /// i64 on the end</b>: every field from +0 to +75 is at the same offset and carries the same
    /// value as the buyer's view of the same trade (cap_social3_client.log seq 1486 against
    /// cap_social3_client2.log frame 1572, both trade 3), and the seller's row then adds an i64
    /// at +76 that reads 1 - the same number as TotalPaid, i.e. what the seller is owed.
    /// 98 -&gt; 106 bytes fixed, plus the name.
    /// </summary>
    public const int SoldElementFixedSize = 106;
    /// <summary>+76, i64: the seller's proceeds. One captured row, where it equals the price.</summary>
    public const int SlSellerProceeds = 76;

    /// <summary>S_TRADE_BROKER_SOLD_ITEM_LIST (0x5587), min total 0x18:
    /// `[u16 count][u16 off][i64 TotalCalcMoney][i64 TotalCalcTCatMoney]`. The second i64 is the
    /// one the shipped .def drops (section 5).</summary>
    public static byte[] BuildSSoldItemListBody(long totalCalcMoney = 0, long totalCalcTCatMoney = 0)
    {
        var p = new byte[0x18 - ClientHeaderSize];
        BitConverter.GetBytes(totalCalcMoney).CopyTo(p, 4);
        BitConverter.GetBytes(totalCalcTCatMoney).CopyTo(p, 12);
        return p;
    }

    /// <summary>The 20-byte fixed part of S_TRADE_BROKER_SOLD_ITEM_LIST.</summary>
    public const int SoldListFixedSize = 0x18 - ClientHeaderSize;

    /// <summary>
    /// S_TRADE_BROKER_SOLD_ITEM_LIST with rows - cap_social3_client2.log frame 1572, against the
    /// all-zero 24-byte form at 1402 / 1586 / 1641. <c>TotalCalcMoney</c> is the sum of what is
    /// waiting to be collected: 1 in that frame, and trade 3's price was 1, which is also why it
    /// cannot be told apart from a post-tax figure here. <c>TotalCalcTCatMoney</c> stays 0 - no
    /// captured row has ever carried a second currency.
    /// </summary>
    public static byte[] BuildSSoldItemListBody(
        IReadOnlyList<(int TradeId, long ItemDbId, int TemplateId, int Amount, long Price,
                       int SellerDbId, string SellerName, long RegisterTime, long SoldTime)>? rows)
    {
        rows ??= Array.Empty<(int, long, int, int, long, int, string, long, long)>();
        var sizes = new int[rows.Count];
        int total = SoldListFixedSize;
        long money = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            sizes[i] = SoldElementFixedSize + NameBytes(rows[i].SellerName);
            total += sizes[i];
            money += rows[i].Price;
        }

        var p = new byte[total];
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : ClientHeaderSize + SoldListFixedSize))
            .CopyTo(p, 2);
        BitConverter.GetBytes(money).CopyTo(p, 4);

        int at = SoldListFixedSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int here = ClientHeaderSize + at;
            int next = i + 1 < rows.Count ? ClientHeaderSize + at + sizes[i] : 0;
            BitConverter.GetBytes((ushort)here).CopyTo(p, at);
            BitConverter.GetBytes((ushort)next).CopyTo(p, at + 2);
            BitConverter.GetBytes((ushort)(here + SoldElementFixedSize)).CopyTo(p, at + 4);
            BitConverter.GetBytes(r.TradeId).CopyTo(p, at + BlTradeId);
            BitConverter.GetBytes(r.ItemDbId).CopyTo(p, at + BlItemDbId);
            BitConverter.GetBytes(r.TemplateId).CopyTo(p, at + BlTemplateId);
            BitConverter.GetBytes(r.Amount).CopyTo(p, at + BlAmount);
            BitConverter.GetBytes(r.RegisterTime).CopyTo(p, at + BlRegisterTime);
            BitConverter.GetBytes(r.Price).CopyTo(p, at + BlPrice);
            BitConverter.GetBytes(r.SellerDbId).CopyTo(p, at + BlSellerDbId);
            p[at + BlSoldFlag] = 1;
            BitConverter.GetBytes(r.SoldTime).CopyTo(p, at + BlSoldTime);
            BitConverter.GetBytes(PriceWithTax(r.Price)).CopyTo(p, at + BlTotalPaid);
            BitConverter.GetBytes(r.Price).CopyTo(p, at + SlSellerProceeds);
            WriteName(p, at + SoldElementFixedSize, r.SellerName);
            at += sizes[i];
        }
        return p;
    }

    /// <summary>S_TRADE_BROKER_HISTORY_ITEM_LIST (0x9372) and S_TRADE_BROKER_WAITING_ITEM_LIST
    /// (0xFD2C), min total 0x10: `[u16 count][u16 off][i32 CurrentPage][i32 TotalPage]`.
    /// An empty result is page 1 of 1 - page 0 of 0 is what made the real Arbiter's
    /// C_VIEW_GUILD_WAR crash, and the paging guard in CLAUDE.md's hard rules is about exactly
    /// this shape.</summary>
    public static byte[] BuildSPagedListBody(int currentPage = 1, int totalPage = 1)
    {
        var p = new byte[0x10 - ClientHeaderSize];
        BitConverter.GetBytes(currentPage).CopyTo(p, 4);
        BitConverter.GetBytes(totalPage).CopyTo(p, 8);
        return p;
    }

    /// <summary>S_TRADE_BROKER_INPUT_PRICE (0x4FE6), min total 0x2C: five i64 -
    /// `MinPrice, AvgPrice, MinTCatPrice, AvgTCatPrice, RegisterFeeRate`, from
    /// TradeBrokerSearchAgent::CalcMinAvgPrice. All zero when nothing has ever been listed.</summary>
    public static byte[] BuildSInputPriceBody(long minPrice = 0, long avgPrice = 0,
        long minTCatPrice = 0, long avgTCatPrice = 0, long registerFeeRate = 0)
    {
        var p = new byte[0x2C - ClientHeaderSize];
        BitConverter.GetBytes(minPrice).CopyTo(p, 0);
        BitConverter.GetBytes(avgPrice).CopyTo(p, 8);
        BitConverter.GetBytes(minTCatPrice).CopyTo(p, 16);
        BitConverter.GetBytes(avgTCatPrice).CopyTo(p, 24);
        BitConverter.GetBytes(registerFeeRate).CopyTo(p, 32);
        return p;
    }

    /// <summary>S_TRADE_BROKER_CALC_NOTIFY (0x6AF1), min total 0x0C:
    /// `[i32 SoldCount][i32 BoughtCount]` - the two numbers on the broker NPC's badge.</summary>
    public static byte[] BuildSCalcNotifyBody(int soldCount = 0, int boughtCount = 0)
    {
        var p = new byte[0x0C - ClientHeaderSize];
        BitConverter.GetBytes(soldCount).CopyTo(p, 0);
        BitConverter.GetBytes(boughtCount).CopyTo(p, 4);
        return p;
    }

    /// <summary>The four one-byte answers: S_TRADE_BROKER_SUGGEST_DEAL (0x7066),
    /// S_TRADE_BROKER_REQUEST_DEAL_RESULT (0xD0E6), S_TRADE_BROKER_BUY_IT_NOW (0xEC54) and the
    /// two CALC results - all min total 5, all one u8.</summary>
    public static byte[] BuildSFlagBody(bool value) => new[] { (byte)(value ? 1 : 0) };

    // =========================================================================================
    // The inter-server six
    // =========================================================================================

    /// <summary>SA_TRADE_BROKER_OPEN (0x1457), frame 0x0E: `i64 ArbiterUser@06`. World saying a
    /// player walked up to a broker NPC.</summary>
    public static long? ParseSaBrokerOpen(byte[] p)
        => TooShort(SA_TRADE_BROKER_OPEN, p) ? null : BitConverter.ToInt64(p, 0);

    /// <summary>SA_TRADE_BROKER_DEAL_OPEN (0x1459), frame 0x13:
    /// `i64 ArbiterUser@06, i32 TradeId@0E, u8 Result@12`.</summary>
    public static (long arbiterUser, int tradeId, bool result)? ParseSaBrokerDealOpen(byte[] p)
        => TooShort(SA_TRADE_BROKER_DEAL_OPEN, p) ? null
            : (BitConverter.ToInt64(p, 0), BitConverter.ToInt32(p, 8), p[12] != 0);

    /// <summary>AS_TRADE_BROKER_CLOSE (0x1458) and AS_TRADE_BROKER_DEAL_CLOSE (0x145B), frame
    /// 0x0A: `i32 UserDbId@06`.</summary>
    public static byte[] BuildAsBrokerClose(int userDbId) => BitConverter.GetBytes(userDbId);

    /// <summary>AS_TRADE_BROKER_DEAL_OPEN (0x145A), frame 0x12:
    /// `i32 BuyerDbId@06, i32 TradeId@0A, i32 OpenType@0E`.</summary>
    public static byte[] BuildAsBrokerDealOpen(int buyerDbId, int tradeId, int openType)
    {
        var p = new byte[0x12 - FrameHeaderSize];
        BitConverter.GetBytes(buyerDbId).CopyTo(p, 0);
        BitConverter.GetBytes(tradeId).CopyTo(p, 4);
        BitConverter.GetBytes(openType).CopyTo(p, 8);
        return p;
    }

    /// <summary>AS_TRADE_BROKER_ITEM_SOLD (0x286D), frame 0x0B:
    /// `i32 UserDbId@06, u8 InstantBuy@0A`.</summary>
    public static byte[] BuildAsBrokerItemSold(int userDbId, bool instantBuy)
    {
        var p = new byte[0x0B - FrameHeaderSize];
        BitConverter.GetBytes(userDbId).CopyTo(p, 0);
        p[4] = (byte)(instantBuy ? 1 : 0);
        return p;
    }

    // =========================================================================================
    // .def corrections
    // =========================================================================================

    /// <summary>
    /// The four shipped broker .def files that do not match the binary, checked field by field
    /// against the PDL dumpers (BROKER-DESIGN.md section 5). The first is the one that matters:
    /// SDB_TRADE_BROKER_BUY_IT_NOW is a live DB-proxy request and its .def is 8 bytes short of
    /// the price the buyer actually paid.
    ///
    /// <para>Two more look wrong to a naive field count and are NOT:
    /// C_TRADE_BROKER_CALC_BOUGHT_ITEM and S_TRADE_BROKER_BOUGHT_ITEM_LIST are array packets
    /// whose dumper emits the array through a different helper.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> CorrectedDefs { get; } = new Dictionary<string, string>
    {
        // missing int64 totalPriceWithTax after the binary ref
        ["SDB_TRADE_BROKER_BUY_IT_NOW"] = "int32  dlmId\nint32  ownerDbId\nint32  step\nint32  tradeId\n"
            + "bool   instantBuy\nbytes  itemBinary\nint64  totalPriceWithTax\n",
        // the name's ref slot is written FIRST, and itemEnchantCount is missing entirely
        ["S_TRADE_BROKER_DEAL_SUGGESTED"] = "ref    userName\nint32  userDbId\nint32  tradeId\n"
            + "int32  itemTemplateId\nint32  itemAmount\nint32  itemEnchantCount\n"
            + "int64  registeredPrice\nint64  suggestedPrice\nstring userName\n",
        // the .def has an extra int32 between price and sellerDealStatus - 4 bytes too long
        ["S_TRADE_BROKER_DEAL_INFO_UPDATE"] = "int64  price\nint32  sellerDealStatus\nint32  buyerDealStatus\n",
        // the guard is 0x18 = header + two int64; the .def has one
        ["S_TRADE_BROKER_SOLD_ITEM_LIST"] = "int64  totalCalcMoney\nint64  totalCalcTCatMoney\n",
    };

    private static readonly Dictionary<string, PacketDef> _overrides = new();
    private static readonly object _overrideLock = new();

    /// <summary>CorrectedDefs, then the shipped registry - the same contract as
    /// GuildHandlers.ResolveDef and ChatManager.ResolveDef, so a future wiring can chain them
    /// with <c>??</c>.</summary>
    public static PacketDef? ResolveDef(DefinitionRegistry? defs, string name)
    {
        lock (_overrideLock)
        {
            if (_overrides.TryGetValue(name, out var cached)) return cached;
            if (CorrectedDefs.TryGetValue(name, out var text))
            {
                var parsed = DefinitionParser.ParseText(name, text);
                _overrides[name] = parsed;
                return parsed;
            }
        }
        return defs?.Get(name);
    }

    // ---------------------------------- private helpers ----------------------------------

    /// <summary>Reads a NUL-terminated UTF-16LE string whose u16 PACKET offset sits at
    /// body[slotIndex]. "" for the 0/out-of-range offsets the real handlers fall back on.</summary>
    private static string ReadWString(byte[] body, int slotIndex)
    {
        if (slotIndex + 2 > body.Length) return string.Empty;
        int at = BitConverter.ToUInt16(body, slotIndex) - ClientHeaderSize;
        if (at < 0 || at >= body.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = at; i + 1 < body.Length; i += 2)
        {
            char ch = (char)(body[i] | (body[i + 1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
