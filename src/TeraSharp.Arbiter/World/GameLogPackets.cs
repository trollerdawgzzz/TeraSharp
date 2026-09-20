namespace TeraSharp.Arbiter.World;

/// <summary>
/// T115. The five LogDB writes World sends the Arbiter, decoded into one normalized row shape.
///
/// <para>These are the whole set: grepping the Arbiter for <c>Handler_SDB_*LOG*</c> finds
/// exactly <c>SDB_ITEM_TRADE_LOG</c>, <c>SDB_ADD_PVP_USER_LOG</c>, <c>SDB_ADD_PK_USER_LOG</c>,
/// <c>SDB_ADD_GROUP_DUEL_USER_LOG</c> and <c>SDB_CASH_ITEM_LOG</c>
/// (<c>SDB_INIT_LOSS_LOGINTIME_REVISION_SECOND</c> matches the grep and is not a log). The
/// SA_ family has two more - <c>SA_LOG_QUEST_END</c> and <c>SA_RELAY_LOG</c> - outside the
/// 0x27xx-0x29xx range this task covers.</para>
///
/// <para><b>All five stay one-way.</b> Every handler runs to <c>return 1</c> with no packet
/// writer in it, and cap_social2/3/4 confirm it for 0x288C: four frames arrive and not one A-&gt;W
/// frame follows any of them. Decoding them changes nothing on the wire - it only stops the
/// bytes being thrown away.</para>
///
/// <para>Offsets below are PAYLOAD-relative (payload index = frame offset - 6). Two reference
/// shapes appear, both frame-relative on the wire:</para>
/// <code>
///   list ref   [i32 firstElementOffset][i32 byteLength]     8 bytes
///   wstring    [i32 offset]                                 4 bytes
/// </code>
/// </summary>
public static class GameLogPackets
{
    public const ushort SDB_ITEM_TRADE_LOG = 0x27DD;
    public const ushort SDB_ADD_PVP_USER_LOG = 0x27FE;
    public const ushort SDB_ADD_PK_USER_LOG = 0x27FF;
    public const ushort SDB_ADD_GROUP_DUEL_USER_LOG = 0x2800;
    public const ushort SDB_CASH_ITEM_LOG = 0x288C;

    /// <summary>The five opcodes, for the allow-list and for tests.</summary>
    public static readonly ushort[] Opcodes =
    {
        SDB_ITEM_TRADE_LOG, SDB_ADD_PVP_USER_LOG, SDB_ADD_PK_USER_LOG,
        SDB_ADD_GROUP_DUEL_USER_LOG, SDB_CASH_ITEM_LOG,
    };

    // The retail log tool's groups. Only five are produced today - the other three exist so a
    // query written against them does not have to change when guild/mail/warehouse logs land.
    public const string CategoryUser = "user";
    public const string CategoryItem = "item";
    public const string CategoryTrade = "trade";
    public const string CategoryGuild = "guild";
    public const string CategoryParty = "party";
    public const string CategoryMail = "mail";
    public const string CategoryWarehouse = "warehouse";
    public const string CategoryPvp = "pvp";

    /// <summary>Every category the tool knows, in its own order.</summary>
    public static readonly string[] Categories =
    {
        CategoryUser, CategoryItem, CategoryTrade, CategoryGuild,
        CategoryParty, CategoryMail, CategoryWarehouse, CategoryPvp,
    };

    // ---- frame guards (payload = frame - 6) --------------------------------------------
    /// <summary>Guard <c>0x41 &lt; param_3</c>, so frame 0x42 and payload 60.</summary>
    public const int ItemTradeLogMinPayload = 0x42 - 6;
    /// <summary>Guard <c>0xd &lt; param_3</c> - shared by PVP, PK and CASH_ITEM.</summary>
    public const int DuelLogMinPayload = 0x0E - 6;
    /// <summary>Guard <c>0x29 &lt; param_3</c>, so frame 0x2A and payload 36.</summary>
    public const int GroupDuelLogMinPayload = 0x2A - 6;
    /// <summary>Same guard as the duel logs; the payload is one list reference.</summary>
    public const int CashItemLogMinPayload = 0x0E - 6;

    // ---- SDB_ITEM_TRADE_LOG (0x27DD) ---------------------------------------------------
    public const int TradeRequestorSendRef = 0;    // frame 0x06
    public const int TradeRequesteeSendRef = 8;    // frame 0x0E
    public const int TradeRequestorTooltipRef = 16;// frame 0x16
    public const int TradeRequesteeTooltipRef = 24;// frame 0x1E
    public const int TradeDlmId = 32;              // frame 0x26
    public const int TradeOwnerDbId = 36;          // frame 0x2A
    public const int TradeTargetDbId = 40;         // frame 0x2E
    public const int TradeRequestorMoney = 44;     // frame 0x32, i64
    public const int TradeRequesteeMoney = 52;     // frame 0x3A, i64

    // ---- SDB_ADD_PVP_USER_LOG / SDB_ADD_PK_USER_LOG ------------------------------------
    public const int DuelUserDbId = 0;             // frame 0x06
    public const int DuelOpponentDbId = 4;         // frame 0x0A

    // ---- SDB_ADD_GROUP_DUEL_USER_LOG (0x2800) ------------------------------------------
    public const int GroupBlueLeaderNameRef = 0;   // frame 0x06, wstring
    public const int GroupRedLeaderNameRef = 4;    // frame 0x0A, wstring
    public const int GroupBlueMembersRef = 8;      // frame 0x0E, list
    public const int GroupRedMembersRef = 16;      // frame 0x16, list
    public const int GroupBlueLeaderDbId = 24;     // frame 0x1E
    public const int GroupRedLeaderDbId = 28;      // frame 0x22
    public const int GroupTrailingValue = 32;      // frame 0x26, the dumper leaves it unnamed

    // ---- SDB_CASH_ITEM_LOG (0x288C) ----------------------------------------------------
    public const int CashListRef = 0;              // frame 0x06

    /// <summary>
    /// One <c>CashItemLog</c>. The struct is 0x28 in WorldServer
    /// (<c>CashItemLog::RTTI_Type_Descriptor</c>, vector stride 0x28) and the four captured
    /// frames are 54 bytes = 8-byte reference + one 40-byte element, so the size is pinned from
    /// both ends.
    /// </summary>
    public const int CashRecordSize = 0x28;
    public const int CashRecUnknown0 = 0;          // i64, 0 in every captured frame
    public const int CashRecItemDbId = 8;          // i64, the producer's item+0x358
    public const int CashRecUserDbId = 16;         // i64, see the note on ParseCashItemLog
    public const int CashRecTemplateId = 24;       // u32, item+0x360, datasheet-indexed
    public const int CashRecAmount = 28;           // u32, item+0x50
    public const int CashRecUnknown32 = 32;        // u32, item+0x374
    public const int CashRecReason = 36;           // u8,  a literal per producer
    // +37..39 is struct padding and is NOT always zero - the capture has 01 there three times
    // out of four, the same uninitialised-heap tail the 536-byte item records carry.

    /// <summary>
    /// One decoded log line, in the shape the <c>game_log</c> table stores. Everything the
    /// frame did not carry stays 0 / null rather than being invented.
    /// </summary>
    public readonly record struct GameLogEntry(
        string Category, string Action, long AccountId, long CharacterId, long TargetId,
        long ItemDbId, int TemplateId, long Amount, long Money, string? Extra);

    /// <summary>
    /// Read a list reference: <c>[i32 firstElementOffset][i32 byteLength]</c>, both
    /// FRAME-relative, so the payload index is the offset minus 6. False for a reference that
    /// does not fit the payload - the same guard the dumper applies
    /// (<c>offset != 0 &amp;&amp; offset &lt; frameLength</c>).
    /// </summary>
    public static bool TryReadListRef(byte[] payload, int refAt, out int first, out int bytes)
    {
        first = 0; bytes = 0;
        if (payload is null || refAt < 0 || refAt + 8 > payload.Length) return false;
        int frameOffset = BitConverter.ToInt32(payload, refAt);
        int len = BitConverter.ToInt32(payload, refAt + 4);
        if (frameOffset < 6 || len <= 0) return false;
        int at = frameOffset - 6;
        if (at < 0 || len > payload.Length - at) return false;
        first = at; bytes = len;
        return true;
    }

    /// <summary>How many whole records of <paramref name="recordSize"/> a reference spans.</summary>
    public static int ListCount(byte[] payload, int refAt, int recordSize)
        => recordSize > 0 && TryReadListRef(payload, refAt, out _, out int bytes)
            ? bytes / recordSize
            : 0;

    /// <summary>
    /// Read a wstring reference: one frame-relative <c>i32</c> offset to NUL-terminated UTF-16.
    /// Empty for offset 0, which the dumper substitutes with its own empty-string constant.
    /// </summary>
    public static string ReadWString(byte[] payload, int refAt)
    {
        if (payload is null || refAt < 0 || refAt + 4 > payload.Length) return string.Empty;
        int frameOffset = BitConverter.ToInt32(payload, refAt);
        if (frameOffset < 6) return string.Empty;
        int at = frameOffset - 6;
        if (at < 0 || at >= payload.Length) return string.Empty;
        int end = at;
        while (end + 1 < payload.Length && !(payload[end] == 0 && payload[end + 1] == 0)) end += 2;
        return end > at ? System.Text.Encoding.Unicode.GetString(payload, at, end - at) : string.Empty;
    }

    // =====================================================================================
    // The five decoders. Each returns the rows to file; an empty list means a frame shorter
    // than the real handler's own guard, which is dropped exactly as the Arbiter drops it.
    // =====================================================================================

    /// <summary>
    /// <c>SDB_ITEM_TRADE_LOG</c> (0x27DD) - one player-to-player trade, filed once per side so
    /// a query by either character finds it.
    ///
    /// <para>The four item lists (RequestorSend, RequesteeSend and the two tooltip blobs) are
    /// recorded as COUNTS, not contents: no capture has this frame, and the Arbiter's dumper
    /// hands each list straight to the generic reference printer without naming an element
    /// field, so an element layout here would be invention. The scalars are exact.</para>
    /// </summary>
    public static IReadOnlyList<GameLogEntry> ParseItemTradeLog(byte[] payload)
    {
        if (payload is null || payload.Length < ItemTradeLogMinPayload)
            return Array.Empty<GameLogEntry>();

        long owner = BitConverter.ToUInt32(payload, TradeOwnerDbId);
        long target = BitConverter.ToUInt32(payload, TradeTargetDbId);
        long ownerMoney = BitConverter.ToInt64(payload, TradeRequestorMoney);
        long targetMoney = BitConverter.ToInt64(payload, TradeRequesteeMoney);
        int ownerItems = ListBytes(payload, TradeRequestorSendRef);
        int targetItems = ListBytes(payload, TradeRequesteeSendRef);
        uint dlmId = BitConverter.ToUInt32(payload, TradeDlmId);

        string Extra(int mine, int theirs) => Json(
            ("dlmId", dlmId.ToString()),
            ("sendBytes", mine.ToString()),
            ("recvBytes", theirs.ToString()),
            ("tooltipBytes", ListBytes(payload, TradeRequestorTooltipRef).ToString()));

        return new[]
        {
            new GameLogEntry(CategoryTrade, "trade.send", 0, owner, target,
                0, 0, 0, ownerMoney, Extra(ownerItems, targetItems)),
            new GameLogEntry(CategoryTrade, "trade.recv", 0, target, owner,
                0, 0, 0, targetMoney, Extra(targetItems, ownerItems)),
        };
    }

    /// <summary>
    /// <c>SDB_ADD_PVP_USER_LOG</c> (0x27FE) and <c>SDB_ADD_PK_USER_LOG</c> (0x27FF). Identical
    /// frames - <c>[u32 UserDbId][u32 OpponentDbId]</c> - and identical dumpers; only the
    /// action differs, which is why they share a decoder.
    /// </summary>
    public static IReadOnlyList<GameLogEntry> ParseDuelUserLog(byte[] payload, string action)
    {
        if (payload is null || payload.Length < DuelLogMinPayload)
            return Array.Empty<GameLogEntry>();
        return new[]
        {
            new GameLogEntry(CategoryPvp, action, 0,
                BitConverter.ToUInt32(payload, DuelUserDbId),
                BitConverter.ToUInt32(payload, DuelOpponentDbId),
                0, 0, 0, 0, null),
        };
    }

    /// <summary>
    /// <c>SDB_ADD_GROUP_DUEL_USER_LOG</c> (0x2800) - one row per team leader, with the other
    /// leader as the target. Member lists are counted in bytes for the same reason the trade
    /// log's are: nothing names their element.
    /// </summary>
    public static IReadOnlyList<GameLogEntry> ParseGroupDuelUserLog(byte[] payload)
    {
        if (payload is null || payload.Length < GroupDuelLogMinPayload)
            return Array.Empty<GameLogEntry>();

        long blue = BitConverter.ToUInt32(payload, GroupBlueLeaderDbId);
        long red = BitConverter.ToUInt32(payload, GroupRedLeaderDbId);
        string blueName = ReadWString(payload, GroupBlueLeaderNameRef);
        string redName = ReadWString(payload, GroupRedLeaderNameRef);
        int blueBytes = ListBytes(payload, GroupBlueMembersRef);
        int redBytes = ListBytes(payload, GroupRedMembersRef);
        uint trailing = BitConverter.ToUInt32(payload, GroupTrailingValue);

        string Extra(string me, string them, int mine, int theirs) => Json(
            ("leader", me), ("opponent", them),
            ("memberBytes", mine.ToString()), ("opponentMemberBytes", theirs.ToString()),
            ("trailing", trailing.ToString()));

        return new[]
        {
            new GameLogEntry(CategoryPvp, "duel.group.blue", 0, blue, red,
                0, 0, 0, 0, Extra(blueName, redName, blueBytes, redBytes)),
            new GameLogEntry(CategoryPvp, "duel.group.red", 0, red, blue,
                0, 0, 0, 0, Extra(redName, blueName, redBytes, blueBytes)),
        };
    }

    /// <summary>
    /// <c>SDB_CASH_ITEM_LOG</c> (0x288C) - one row per 40-byte <c>CashItemLog</c>. The only one
    /// of the five with capture instances: cap_social2 seq 1849, cap_social3 seq 1037 and 1364,
    /// cap_social4 seq 8660, all 54-byte frames carrying exactly one record.
    ///
    /// <para>Field names come from the producer,
    /// <c>DBIncreaseUserInvenSize::ExecuteCommitSQL</c> (WorldServer.exe.c:1129672), which
    /// fills the struct in order from the item and the user and writes a literal 2 into the
    /// last byte - so <see cref="CashRecReason"/> identifies the producer, not the item.</para>
    ///
    /// <para><b>One field is deliberately not resolved.</b> <see cref="CashRecUserDbId"/> is
    /// whatever <c>FUN_1404ce180(user)</c> returns; the capture has 1003, 2, 1003 and 1, and
    /// 1003 matches no character in those sessions, so it is either an account id or a user id
    /// from a different space. It is stored as the character id AND echoed into the extra JSON
    /// under <c>rawUserId</c>, so a capture that settles it can be re-read without re-decoding.
    /// Guessing it into account_id would silently mis-file every cash row.</para>
    /// </summary>
    public static IReadOnlyList<GameLogEntry> ParseCashItemLog(byte[] payload)
    {
        if (payload is null || payload.Length < CashItemLogMinPayload)
            return Array.Empty<GameLogEntry>();
        if (!TryReadListRef(payload, CashListRef, out int at, out int bytes))
            return Array.Empty<GameLogEntry>();

        var rows = new List<GameLogEntry>(bytes / CashRecordSize);
        for (int off = at; off + CashRecordSize <= at + bytes; off += CashRecordSize)
        {
            long user = BitConverter.ToInt64(payload, off + CashRecUserDbId);
            rows.Add(new GameLogEntry(
                CategoryItem, "cash.item", 0, user, 0,
                BitConverter.ToInt64(payload, off + CashRecItemDbId),
                (int)BitConverter.ToUInt32(payload, off + CashRecTemplateId),
                BitConverter.ToUInt32(payload, off + CashRecAmount),
                0,
                Json(("rawUserId", user.ToString()),
                     ("reason", payload[off + CashRecReason].ToString()),
                     ("field32", BitConverter.ToUInt32(payload, off + CashRecUnknown32).ToString()))));
        }
        return rows;
    }

    /// <summary>The byte length a list reference spans, or 0.</summary>
    private static int ListBytes(byte[] payload, int refAt)
        => TryReadListRef(payload, refAt, out _, out int bytes) ? bytes : 0;

    /// <summary>
    /// A flat JSON object from name/value pairs. Hand-rolled rather than System.Text.Json so
    /// the extra column stays a plain string with no serializer options in play; values are
    /// escaped for the two characters that can appear in a character name on this server.
    /// </summary>
    public static string Json(params (string Key, string Value)[] fields)
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append('{');
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Escape(fields[i].Key)).Append('"').Append(':')
              .Append('"').Append(Escape(fields[i].Value)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\\' || c == '"') sb.Append('\\');
            if (c < ' ') { sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
