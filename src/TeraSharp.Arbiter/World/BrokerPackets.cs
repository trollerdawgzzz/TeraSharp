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

    /// <summary>The TradeData / CalcItemList record, 0x188 bytes.</summary>
    public const int TradeDataSize = 0x188;

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
        if (registerTime != default)
        {
            int at = TdRegisterTime;
            foreach (ushort v in new[] { (ushort)registerTime.Year, (ushort)registerTime.Month,
                                         (ushort)registerTime.Day, (ushort)registerTime.Hour,
                                         (ushort)registerTime.Minute, (ushort)registerTime.Second })
            { BitConverter.GetBytes(v).CopyTo(r, at); at += 2; }
        }
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
