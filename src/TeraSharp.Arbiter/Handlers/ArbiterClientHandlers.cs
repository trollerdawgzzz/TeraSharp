using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// The client packets the Arbiter owns that nothing was answering — T45, researched in
/// <c>status/CLIENT-REJECTS.md</c>.
///
/// <para><b>How they were found.</b> A live WorldServer prints
/// <c>handler has not been implemented yet!!! &lt;opcode&gt; &lt;len&gt;</c> for every client packet
/// TeraSharp forwards that it has no handler for. Eighteen opcodes showed up. All eighteen are
/// the <i>Arbiter's</i>: each has a <c>Handler_C_*</c> in ArbiterServer.exe, and several read
/// state only the Arbiter has (the item table, the visited-section list, the guild rows).
/// Forwarding them to World was always going to fail.</para>
///
/// <para><b>Two different bugs produced that one log line.</b> Twelve of the eighteen were simply
/// unregistered, so <c>PacketDispatcher</c>'s "forward unregistered opcodes to World when
/// in-world" fallback sent them on. The other six <i>were</i> registered — but through
/// <c>HandlerRegistry</c>'s <c>RegNoop</c> / <c>RegEmptyReply</c> helpers, which both begin
/// <c>if (s.InWorld) { Forward(s, op, body); return true; }</c>. That hedge is wrong for every
/// Arbiter-owned packet: standalone replies correctly and in-world forwards to a server that
/// cannot answer. status/CLIENT-REJECTS.md section 7 has the registry diff.</para>
///
/// <para>Everything here answers in <b>both</b> modes and never forwards.</para>
/// </summary>
public static class ArbiterClientHandlers
{
    // ------------------------------------------------------------------ opcodes
    // data.json maps."376012", each cross-checked against the Arbiter's own opcode->name switch
    // (Arb_part_003.c) - test Rejects_opcodes_match_the_name_table.

    public const ushort C_SHOW_ITEM_TOOLTIP_EX = 0x75C8;   // 30152
    public const ushort S_SHOW_ITEM_TOOLTIP = 0x5646;      // 22086
    public const ushort C_VISIT_NEW_SECTION = 0xDFA4;      // 57252
    public const ushort S_VISIT_NEW_SECTION = 0x5A23;      // 23075
    public const ushort C_CLIENT_LOG = 0x99B5;             // 39349
    public const ushort C_SERVER_TIME = 0x6A81;            // 27265
    public const ushort S_SERVER_TIME = 0x58CA;            // 22730
    public const ushort C_SAVE_CLIENT_UI_SETTING = 0xA98F; // 43407
    public const ushort S_SAVE_CLIENT_UI_SETTING = 0xF751; // 63313
    public const ushort C_TRADE_BROKER_HIGHEST_ITEM_LEVEL = 0xEAFB; // 60155
    public const ushort S_TRADE_BROKER_HIGHEST_ITEM_LEVEL = 0x7E53; // 32339
    public const ushort C_UPDATE_CONTENTS_PLAYTIME = 0xB76C;        // 46956
    public const ushort C_EVENT_GUIDE = 0x82E2;            // 33506
    public const ushort C_REQUEST_PARTY_MATCH_INFO = 0xEFD6;        // 61398
    public const ushort C_REQUEST_MY_PARTY_MATCH_INFO = 0x6E66;     // 28262
    public const ushort C_PARTY_MATCH_WINDOW_CLOSED = 0xFA6D;       // 64109
    public const ushort C_REQUEST_GUILD_INFO = 0x5B51;     // 23377
    public const ushort C_REQUEST_GUILD_LIST = 0x866B;     // 34411
    public const ushort C_DUNGEON_COOL_TIME_LIST = 0xD3F7; // 54263
    public const ushort C_VIEW_BATTLE_FIELD_RESULT = 0xEC3D;        // 60477
    public const ushort C_REQUEST_CANDIDATE_LIST = 0x81D7; // 33239
    public const ushort C_SHOW_AWESOMIUMWEB_SHOP = 0xCF8F; // 53135
    public const ushort S_SHOW_AWESOMIUMWEB_SHOP = 0xDFAE; // 57262
    public const ushort C_RESET_ALL_DUNGEON = 0x5867;      // 22631

    /// <summary>T60. 58817. The Arbiter owns it and forwards it to World as AS_ADD_TRADE_BAG.</summary>
    public const ushort C_ADD_TRADE_BAG = 0xE5C1;          // 58817
    /// <summary>The frame C_ADD_TRADE_BAG becomes. 54 bytes.</summary>
    public const ushort AS_ADD_TRADE_BAG = 0x1637;

    /// <summary>T62. 22073. Right-click a name; without the answer the menu never opens.</summary>
    public const ushort C_ASK_INTERACTIVE = 0x5639;        // 22073
    public const ushort S_ANSWER_INTERACTIVE = 0x85C8;     // 34248
    /// <summary>T62. 42458. Which cinematics this player has already seen.</summary>
    public const ushort C_WATCHED_MOVIES = 0xA5DA;         // 42458
    public const ushort S_WATCHED_MOVIES = 0x97A4;         // 38820
    /// <summary>T62. 31605. Name completion, one packet per keystroke.</summary>
    public const ushort C_FINDNAME = 0x7B75;               // 31605
    public const ushort S_FINDNAME = 0xF95D;               // 63837

    /// <summary>T75. The sections the client already knows it has seen. Server-&gt;client only.</summary>
    public const ushort S_VISITED_SECTION_LIST = 0xA853;   // 43091

    /// <summary>
    /// Every opcode above, as an explicit statement that these are the ARBITER's. The forwarding
    /// fallback in <c>PacketDispatcher</c> must never apply to one of them: forwarding produces
    /// World's "handler has not been implemented yet!!!" line, which is a dropped packet with a
    /// misleading log message rather than an error we can act on. See
    /// status/CLIENT-REJECTS.md section 7 for the (human-owned) dispatcher diff.
    /// </summary>
    public static readonly IReadOnlySet<ushort> ArbiterOwned = new HashSet<ushort>
    {
        C_SHOW_ITEM_TOOLTIP_EX, C_VISIT_NEW_SECTION, C_CLIENT_LOG, C_SERVER_TIME,
        C_SAVE_CLIENT_UI_SETTING, C_TRADE_BROKER_HIGHEST_ITEM_LEVEL, C_UPDATE_CONTENTS_PLAYTIME,
        C_EVENT_GUIDE, C_REQUEST_PARTY_MATCH_INFO, C_REQUEST_MY_PARTY_MATCH_INFO,
        C_PARTY_MATCH_WINDOW_CLOSED, C_REQUEST_GUILD_INFO, C_REQUEST_GUILD_LIST,
        C_DUNGEON_COOL_TIME_LIST, C_VIEW_BATTLE_FIELD_RESULT, C_REQUEST_CANDIDATE_LIST,
        C_SHOW_AWESOMIUMWEB_SHOP, C_RESET_ALL_DUNGEON,
        C_ADD_TRADE_BAG,   // T60 - the nineteenth, found the same way (live 2026-09-15)
        C_ASK_INTERACTIVE, C_WATCHED_MOVIES, C_FINDNAME,   // T62 - twenty, twenty-one, twenty-two
    };

    // =========================================================================================
    // 1. C_SHOW_ITEM_TOOLTIP_EX -> S_SHOW_ITEM_TOOLTIP    (the potion counter)
    // =========================================================================================

    /// <summary>
    /// C_SHOW_ITEM_TOOLTIP_EX (0x75C8), fixed part 0x26. Layout from the PDL dumper
    /// <c>FUN_1401e2680</c> (Arb_part_014.c:14071), whose guard is <c>0x25 &lt; param_2</c>:
    /// <code>
    /// [0x04] ref ItemOwnerName   [0x06] i32 ToolTipType      [0x0A] i64 ItemDbId
    /// [0x12] i64 ContentId       [0x1A] i32 CompareTemplateId
    /// [0x1E] i32 ItemOwnerPlanetId                           [0x22] i32 ItemOwnerDbId
    /// </code>
    /// </summary>
    public readonly record struct TooltipRequest(
        string ItemOwnerName, int ToolTipType, long ItemDbId, long ContentId,
        int CompareTemplateId, int ItemOwnerPlanetId, int ItemOwnerDbId);

    /// <summary>Minimum TOTAL packet length; the body guard is this minus the 4-byte header.</summary>
    public const int TooltipRequestPacketSize = 0x26;
    public const int TooltipRequestBodySize = TooltipRequestPacketSize - 4;

    /// <summary>Parses the BODY (packet offset - 4). Null when it is short.</summary>
    public static TooltipRequest? ParseTooltipRequest(ReadOnlySpan<byte> body)
    {
        if (body.Length < TooltipRequestBodySize) return null;
        return new TooltipRequest(
            ReadWString(body, 0),
            BitConverter.ToInt32(body[2..]),
            BitConverter.ToInt64(body[6..]),
            BitConverter.ToInt64(body[14..]),
            BitConverter.ToInt32(body[22..]),
            BitConverter.ToInt32(body[26..]),
            BitConverter.ToInt32(body[30..]));
    }

    // --- S_SHOW_ITEM_TOOLTIP (0x5646) ---
    // There is NO .def file for either half of this pair in tera_v100_MASTER_FINAL, so the whole
    // layout comes from the dumper FUN_1402da6b0 (Arb_part_023.c:~800), guard `0x12f < param_2`.
    // The fixed part is 0x130 and every field below lands in it with no gaps - the last one,
    // Damaged, sits at 0x12F, which is exactly what makes the reconstruction trustworthy.
    //
    // Ref block, 9 slots at 0x04..0x15, read by the dumper at puVar14[3]/[5]/[7]/[9]/[10]:
    //   0x04 count / 0x06 offset  CustomizingItemList    {i32 CustomizingItemTemplateId}
    //   0x08 count / 0x0A offset  OptionSetInfoList      {i32 CurOptionIndex, i32 MasterpieceStatReviseIndex,
    //                                                     f32 ItemLevel, f32 ItemMinLevel, f32 ItemMaxLevel}
    //   0x0C count / 0x0E offset  CombinePassiveList     {i32 PassiveId}
    //   0x10 count / 0x12 offset  CompareStatSetInfoList {24 x i32 then 8 x i64}
    //   0x14 ref                  ItemBoundOwner (wstring)
    // All four arrays are empty for anything we can answer today, and an empty array is
    // count = 0 / offset = 0.
    public const int TooltipFixedSize = 0x130;
    public const int TtCustomizingCount = 0x04, TtCustomizingOffset = 0x06;
    public const int TtOptionSetCount = 0x08, TtOptionSetOffset = 0x0A;
    public const int TtCombinePassiveCount = 0x0C, TtCombinePassiveOffset = 0x0E;
    public const int TtCompareStatCount = 0x10, TtCompareStatOffset = 0x12;
    public const int TtItemBoundOwner = 0x14;
    public const int TtToolTipType = 0x16;
    public const int TtItemDbId = 0x1A;
    public const int TtTemplateId = 0x22;
    public const int TtDbid = 0x26;
    public const int TtOwnerDbId = 0x2E;
    public const int TtInvenType = 0x36;
    public const int TtTabIndex = 0x3A;
    public const int TtInvenPos = 0x3E;
    public const int TtSavedCount = 0x42;
    public const int TtCount = 0x46;
    public const int TtEnchantCount = 0x4A;
    public const int TtDurability = 0x4E;
    public const int TtIsBound = 0x52;
    public const int TtOption = 0x53;
    public const int TtSelectedOptionIdx = 0x57;
    public const int TtOpenOptionIdx = 0x5B;
    public const int TtHavePaperDollCompare = 0x5F;
    public const int TtEnchantScrollPassives = 0x60;     // 8 x i32, increase 1..4 then decrease 1..4
    public const int TtEnchantScrollRemainTimes = 0x80;  // 8 x i64, same order
    public const int TtCurrentUnidentifiedItemGrade = 0xC0;
    public const int TtMasterpiece = 0xC4;
    public const int TtCurrentSlotItemLevel = 0xC5;      // f32
    public const int TtExteriorItemTemplateId = 0xC9;
    public const int TtDamaged = 0x12F;

    /// <summary>
    /// S_SHOW_ITEM_TOOLTIP for one stored item row. Built as a raw frame rather than through
    /// <c>SendByDef</c> for the same reason <see cref="ParcelHandlers"/> builds its three: there
    /// is no .def to drive the encoder with.
    ///
    /// <para>The fields that matter to the client's item cache — <c>ItemDbId</c>,
    /// <c>TemplateId</c>, <c>InvenType</c>, <c>InvenPos</c>, <c>Count</c> and
    /// <c>SavedCount</c> — come from the row. Everything else is zero, which is what a stack of
    /// consumables actually is: no enchant, no options, no colouring, not bound.</para>
    /// </summary>
    public static byte[] BuildShowItemTooltip(int toolTipType, CharacterStore.ItemRow row,
                                              string boundOwner = "")
    {
        ArgumentNullException.ThrowIfNull(row);
        boundOwner ??= string.Empty;
        int textBytes = (boundOwner.Length + 1) * 2;
        int len = TooltipFixedSize + textBytes;
        var p = new byte[len];
        p[0] = (byte)len; p[1] = (byte)(len >> 8);
        p[2] = unchecked((byte)S_SHOW_ITEM_TOOLTIP); p[3] = (byte)(S_SHOW_ITEM_TOOLTIP >> 8);

        // The four arrays stay empty: count 0, offset 0.
        BitConverter.GetBytes((ushort)TooltipFixedSize).CopyTo(p, TtItemBoundOwner);
        System.Text.Encoding.Unicode.GetBytes(boundOwner).CopyTo(p, TooltipFixedSize);

        BitConverter.GetBytes(toolTipType).CopyTo(p, TtToolTipType);
        BitConverter.GetBytes((long)row.ItemDbId).CopyTo(p, TtItemDbId);
        BitConverter.GetBytes(row.TemplateId).CopyTo(p, TtTemplateId);
        BitConverter.GetBytes((long)row.ItemDbId).CopyTo(p, TtDbid);
        BitConverter.GetBytes(row.OwnerDbId).CopyTo(p, TtOwnerDbId);
        BitConverter.GetBytes(row.InvenType).CopyTo(p, TtInvenType);
        BitConverter.GetBytes(row.Slot).CopyTo(p, TtInvenPos);
        BitConverter.GetBytes((int)row.Amount).CopyTo(p, TtSavedCount);
        BitConverter.GetBytes((int)row.Amount).CopyTo(p, TtCount);
        return p;
    }

    /// <summary>
    /// C_SHOW_ITEM_TOOLTIP_EX. The real handler resolves the item's owner (by name when the
    /// request carries one, otherwise by <c>ItemOwnerDbId</c>) and calls
    /// <c>User::ShowItemToolTip</c> / <c>User::SendItemToolTipLock</c>, both of which end in
    /// <c>User::SendToClientItemToolTip</c> — i.e. S_SHOW_ITEM_TOOLTIP built from the item's DB
    /// row. <b>Only the Arbiter can answer it</b>: the request names an <c>ItemDbId</c>, and item
    /// db ids are the Arbiter's to allocate (INVENTORY-DESIGN.md section 7). World has no item
    /// table, which is why it prints "handler has not been implemented yet!!!".
    /// </summary>
    public static bool OnShowItemTooltipEx(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var req = ParseTooltipRequest(body.Span);
        if (req == null)
        {
            log.LogWarning("C_SHOW_ITEM_TOOLTIP_EX: {Len} B body (want {Want})",
                body.Length, TooltipRequestBodySize);
            return true;
        }

        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) return true;

        // The client names the owner as 0 or -1 for its own items (live: -1 on 100.02); anything
        // else is a linked item belonging to someone else - we only have our own rows, so that goes
        // unanswered exactly as the real Arbiter's `lVar5 == 0` branch does.
        long owner = req.Value.ItemOwnerDbId > 0 ? req.Value.ItemOwnerDbId : (long)chr.Id;
        var match = FindItem(store, owner, req.Value.ItemDbId);
        if (match == null)
        {
            log.LogDebug("C_SHOW_ITEM_TOOLTIP_EX: item {Item} not owned by {Owner} - no reply",
                req.Value.ItemDbId, owner);
            return true;
        }

        s.Send(BuildShowItemTooltip(req.Value.ToolTipType, match));
        return true;
    }

    private static CharacterStore.ItemRow? FindItem(CharacterStore store, long ownerDbId, long itemDbId)
    {
        foreach (var row in store.GetInventoryItems(ownerDbId))
            if (row.ItemDbId == itemDbId) return row;
        return null;
    }

    // =========================================================================================
    // 2. C_VISIT_NEW_SECTION -> S_VISIT_NEW_SECTION       (exploration, and quests after a relog)
    // =========================================================================================

    /// <summary>
    /// C_VISIT_NEW_SECTION (0xDFA4): <c>[u32 mapId][u32 guardId][u32 sectionId]</c>, guard 0x10.
    /// <c>Handler_C_VISIT_NEW_SECTION</c> (FUN_1404f1f20) rejects <c>guardId &gt;= 0x40</c>
    /// before it records anything.
    /// </summary>
    public const int VisitPacketSize = 0x10;
    public const int MaxGuardId = 0x40;

    /// <summary>S_VISIT_NEW_SECTION (0x5A23): <c>[u8 isFirstVisit][u32 mapId][u32 guardId][u32 sectionId]</c>.
    /// Field order and types are the shipped <c>S_VISIT_NEW_SECTION.1.def</c>, which has no refs,
    /// so the body is just the four fixed fields in declaration order.</summary>
    public static byte[] BuildVisitNewSection(bool isFirstVisit, int mapId, int guardId, int sectionId)
    {
        const int len = 4 + 1 + 12;
        var p = new byte[len];
        p[0] = len; p[1] = 0;
        p[2] = unchecked((byte)S_VISIT_NEW_SECTION); p[3] = (byte)(S_VISIT_NEW_SECTION >> 8);
        p[4] = (byte)(isFirstVisit ? 1 : 0);
        BitConverter.GetBytes(mapId).CopyTo(p, 5);
        BitConverter.GetBytes(guardId).CopyTo(p, 9);
        BitConverter.GetBytes(sectionId).CopyTo(p, 13);
        return p;
    }

    /// <summary>
    /// AS_UPDATE_VISITED_SECTION_LIST (0x1439) payload:
    /// <c>[u32 entriesOffset=18][u32 entryBytes][u32 playerId][entries]</c>, offsets frame-relative.
    ///
    /// <para><b>This is the relog bug.</b> <c>HandlerRegistry</c> already sends this frame on
    /// every <c>C_LOAD_TOPO_FIN</c> — with an empty list, hard-coded, because nothing tracked
    /// visited sections. So after every relog World is told the character has explored nothing,
    /// and anything gated on exploration (quest steps that want a section visited, the
    /// teleport-scroll destinations) is reset. Feeding it the stored rows is the fix; the entry
    /// stride is 12 bytes, the same <c>(mapId, guardId, sectionId)</c> the client sends.</para>
    /// </summary>
    public const int VisitedEntrySize = 12;

    public static byte[] BuildUpdateVisitedSectionList(uint playerId,
        IReadOnlyList<CharacterStore.VisitedSection> sections)
    {
        sections ??= Array.Empty<CharacterStore.VisitedSection>();
        var p = new byte[12 + sections.Count * VisitedEntrySize];
        BitConverter.GetBytes(18u).CopyTo(p, 0);
        BitConverter.GetBytes((uint)(sections.Count * VisitedEntrySize)).CopyTo(p, 4);
        BitConverter.GetBytes(playerId).CopyTo(p, 8);
        for (int i = 0; i < sections.Count; i++)
        {
            int at = 12 + i * VisitedEntrySize;
            BitConverter.GetBytes(sections[i].MapId).CopyTo(p, at);
            BitConverter.GetBytes(sections[i].GuardId).CopyTo(p, at + 4);
            BitConverter.GetBytes(sections[i].SectionId).CopyTo(p, at + 8);
        }
        return p;
    }

    public static bool OnVisitNewSection(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (body.Length < VisitPacketSize - 4) return true;
        var b = body.Span;
        int mapId = BitConverter.ToInt32(b), guardId = BitConverter.ToInt32(b[4..]),
            sectionId = BitConverter.ToInt32(b[8..]);

        if ((uint)guardId >= MaxGuardId)
        {
            log.LogDebug("C_VISIT_NEW_SECTION: guard {Guard} is out of range - ignored", guardId);
            return true;
        }

        var chr = s.SelectedCharacter;
        var store = Program.Store;
        bool first = chr != null && store != null
                     && store.AddVisitedSection((int)chr.Id, mapId, guardId, sectionId);
        // T76: AddVisitedSection only records a section the FIRST time, so it cannot answer
        // where the character is now. S_FRIEND_LIST and S_GET_USER_LIST both carry this trio -
        // cap_social_client frame 11 has Test at (1, 25, 599001) and frames 1417/1437 have the
        // friend two at the same one - so the row keeps the latest, first visit or not.
        if (chr != null && store != null) store.SetLastSection((int)chr.Id, mapId, guardId, sectionId);
        if (first)
            log.LogInformation("C_VISIT_NEW_SECTION: {Name} discovered map {Map} guard {Guard} section {Section}",
                chr!.Name, mapId, guardId, sectionId);

        s.Send(BuildVisitNewSection(first, mapId, guardId, sectionId));
        return true;
    }

    // =========================================================================================
    // 3. C_CLIENT_LOG — the diagnostic nobody was reading
    // =========================================================================================

    /// <summary>
    /// C_CLIENT_LOG (0x99B5). <c>Handler_C_CLIENT_LOG</c> has no length guard and no reply: it
    /// hands the payload to one sink (<c>FUN_140790640</c>) and returns. The bodies observed live
    /// are 644..2894 bytes, which is the client telling the server what went wrong on ITS side —
    /// so the useful thing to do with it is not to ack it but to print it. Every readable
    /// UTF-16 run of four characters or more is logged.
    /// </summary>
    public static bool OnClientLog(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        string text = ExtractWideRuns(body.Span);
        if (text.Length == 0)
            log.LogInformation("C_CLIENT_LOG: {Len} B, no readable text", body.Length);
        else
            log.LogInformation("C_CLIENT_LOG ({Len} B): {Text}", body.Length, text);
        return true;
    }

    /// <summary>Every UTF-16LE run of <paramref name="min"/> printable characters or more,
    /// joined by " | ". The packet's own structure is unknown; its text is not.</summary>
    public static string ExtractWideRuns(ReadOnlySpan<byte> body, int min = 4)
    {
        var runs = new List<string>();
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < body.Length; i += 2)
        {
            char ch = (char)(body[i] | (body[i + 1] << 8));
            if (ch >= 0x20 && ch != 0xFFFD) { cur.Append(ch); continue; }
            if (cur.Length >= min) runs.Add(cur.ToString());
            cur.Clear();
        }
        if (cur.Length >= min) runs.Add(cur.ToString());
        return string.Join(" | ", runs);
    }

    // =========================================================================================
    // 4. The small ones — answered in both modes, never forwarded
    // =========================================================================================

    /// <summary>
    /// C_SERVER_TIME (0x6A81) -> S_SERVER_TIME (0x58CA), one i64.
    /// <c>Handler_C_SERVER_TIME</c> is eight lines: allocate, write 0x58ca, write one i64 from
    /// the clock, send. It has no length guard and the shipped <c>C_SERVER_TIME.1.def</c> is
    /// empty, matching the 4-byte packets observed live.
    /// </summary>
    public static byte[] BuildServerTime(long unixSeconds)
    {
        var p = new byte[12];
        p[0] = 12; p[1] = 0;
        p[2] = unchecked((byte)S_SERVER_TIME); p[3] = (byte)(S_SERVER_TIME >> 8);
        BitConverter.GetBytes(unixSeconds).CopyTo(p, 4);
        return p;
    }

    public static bool OnServerTime(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        s.Send(BuildServerTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return true;
    }

    /// <summary>S_SAVE_CLIENT_UI_SETTING (0xF751): one byte, the shipped def's <c>result</c>.</summary>
    public static byte[] BuildSaveUiSettingAck(bool ok = true)
    {
        var p = new byte[5];
        p[0] = 5; p[1] = 0;
        p[2] = unchecked((byte)S_SAVE_CLIENT_UI_SETTING); p[3] = (byte)(S_SAVE_CLIENT_UI_SETTING >> 8);
        p[4] = (byte)(ok ? 1 : 0);
        return p;
    }

    /// <summary>
    /// C_SAVE_CLIENT_UI_SETTING (0xA98F), guard 0x10. The UI layout blob, which the Arbiter
    /// stores and hands back as S_REPLY_CLIENT_UI_SETTING. Persisting it is
    /// <see cref="ClientSettingsHandlers"/>'s job and is not changed here; what T45 changes is
    /// that the ack is sent <b>in world too</b> rather than forwarded to a World that drops it.
    /// </summary>
    public static bool OnSaveClientUiSetting(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        s.Send(BuildSaveUiSettingAck(true));
        return true;
    }

    /// <summary>
    /// C_TRADE_BROKER_HIGHEST_ITEM_LEVEL (0xEAFB) -> S_TRADE_BROKER_HIGHEST_ITEM_LEVEL (0x7E53).
    /// The real handler writes one value through <c>FUN_1404aa390</c> (the float writer). T45
    /// sent 0 because there was no broker; cap_social3_client.log seq 134 and 3217 show the real
    /// Arbiter sending <b>469.0</b>, both times and for both characters, so it is static config
    /// (the cap the search window's item-level slider runs to) rather than anything about this
    /// character or the listings. A 0 collapses that slider to nothing.
    /// </summary>
    public static byte[] BuildTradeBrokerHighestItemLevel(float level)
    {
        var p = new byte[8];
        p[0] = 8; p[1] = 0;
        p[2] = unchecked((byte)S_TRADE_BROKER_HIGHEST_ITEM_LEVEL);
        p[3] = (byte)(S_TRADE_BROKER_HIGHEST_ITEM_LEVEL >> 8);
        BitConverter.GetBytes(level).CopyTo(p, 4);
        return p;
    }

    public static bool OnTradeBrokerHighestItemLevel(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        s.Send(BuildTradeBrokerHighestItemLevel(World.BrokerPackets.HighestItemLevelDefault));
        return true;
    }

    /// <summary>
    /// The packets whose real handler answers out of a manager we do not have — party matching,
    /// the lord-election candidate list, the guild browser, battleground results, the web shop,
    /// the dungeon-reset vote, playtime accounting and the event guide.
    ///
    /// <para>Each is accepted and logged at trace and <b>nothing is sent</b>. That is the right
    /// answer rather than a stub reply: these are all UI-initiated reads with no timeout, the
    /// client simply leaves the panel empty, and inventing a reply shape we cannot verify is how
    /// a client gets a malformed packet it cannot parse. What matters is that they stop being
    /// forwarded to World.</para>
    /// </summary>
    public static bool OnAcceptSilently(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
        => true;

    // =========================================================================================
    // 9. C_ADD_TRADE_BAG -> AS_ADD_TRADE_BAG                      (T60, the trade window)
    // =========================================================================================

    /// <summary>Minimum TOTAL length of C_ADD_TRADE_BAG - the handler's guard is `param_3 &lt; 0x34`.</summary>
    public const int AddTradeBagPacketSize = 0x34;    // 52
    /// <summary>The same as a BODY length, which is what PacketDispatcher compares.</summary>
    public const int AddTradeBagBodySize = AddTradeBagPacketSize - 4;   // 48
    /// <summary>AS_ADD_TRADE_BAG is 54 bytes: a 6-byte header and 48 of payload.</summary>
    public const int AddTradeBagFrameSize = 0x36;     // 54

    /// <summary>C_ADD_TRADE_BAG, read by absolute offset from Handler_C_ADD_TRADE_BAG.</summary>
    public readonly record struct AddTradeBagRequest(
        long TradeRequestor, long TradeRequestee, int ContractId, int TabIndex, int InvenPos,
        int MoveAmount, long Money);

    /// <summary>
    /// Parse C_ADD_TRADE_BAG from the FULL packet. Offsets from the PDL dumper
    /// <c>FUN_1401bd420</c> (Arb_part_013.c:8534) and confirmed against
    /// <c>Handler_C_ADD_TRADE_BAG</c> (Arb_part_040.c:17084), whose reads are at exactly these
    /// byte offsets.
    ///
    /// <para><b>Do not use C_ADD_TRADE_BAG.1.def.</b> Its field list is right but it starts at
    /// [0x04] where the binary starts at [0x0C], and its header comment names opcode 65204, which
    /// is a different build. The eight bytes at [0x04..0x0B] are read by neither the handler nor
    /// the dumper, so they are read by neither of us. status/CONTRACT-DESIGN.md section 8.</para>
    /// </summary>
    public static AddTradeBagRequest? ParseAddTradeBag(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < AddTradeBagPacketSize) return null;
        return new AddTradeBagRequest(
            BitConverter.ToInt64(packet[0x0C..]), BitConverter.ToInt64(packet[0x14..]),
            BitConverter.ToInt32(packet[0x1C..]), BitConverter.ToInt32(packet[0x20..]),
            BitConverter.ToInt32(packet[0x24..]), BitConverter.ToInt32(packet[0x28..]),
            BitConverter.ToInt64(packet[0x2C..]));
    }

    /// <summary>
    /// The AS_ADD_TRADE_BAG (0x1637) payload the real Arbiter forwards. The leading u64 is not
    /// copied from the packet - the Arbiter builds it from its own planet id and the SENDER's
    /// character db id (<c>CONCAT44(*(u32 *)(lVar9 + 0x120), DAT_140e2d020)</c>, so planet id in
    /// the low half and db id in the high half).
    /// </summary>
    public static byte[] BuildAsAddTradeBag(int planetId, int senderDbId, AddTradeBagRequest r)
    {
        var p = new byte[AddTradeBagFrameSize - 6];
        BitConverter.GetBytes(planetId).CopyTo(p, 0);
        BitConverter.GetBytes(senderDbId).CopyTo(p, 4);
        BitConverter.GetBytes(r.TradeRequestor).CopyTo(p, 8);
        BitConverter.GetBytes(r.TradeRequestee).CopyTo(p, 16);
        BitConverter.GetBytes(r.ContractId).CopyTo(p, 24);
        BitConverter.GetBytes(r.TabIndex).CopyTo(p, 28);
        BitConverter.GetBytes(r.InvenPos).CopyTo(p, 32);
        BitConverter.GetBytes(r.MoveAmount).CopyTo(p, 36);
        BitConverter.GetBytes(r.Money).CopyTo(p, 40);
        return p;
    }

    /// <summary>
    /// C_ADD_TRADE_BAG. Adding an item to a trade window; 52 bytes, seen live on 2026-09-15 being
    /// forwarded to World, which answered "handler has not been implemented yet". World was right:
    /// the client sends this to the ARBITER, which forwards it as AS_ADD_TRADE_BAG - World has no
    /// C_ handler for it and never did.
    ///
    /// <para><b>One thing the real handler does that this does not.</b> Before forwarding, it
    /// checks the item template against <c>RestrictionPeriodItemTradeDataSheet</c> and, if the
    /// account is younger than the sheet's hour count, answers the client with system message
    /// 0x115f carrying an `hour` parameter and forwards nothing. We have neither the datasheet nor
    /// an account age, so every item is treated as unrestricted - which is what the real Arbiter
    /// does for an item that is not on the sheet.</para>
    /// </summary>
    public static bool OnAddTradeBag(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var packet = new byte[body.Length + 4];
        BitConverter.GetBytes((ushort)packet.Length).CopyTo(packet, 0);
        BitConverter.GetBytes(C_ADD_TRADE_BAG).CopyTo(packet, 2);
        body.Span.CopyTo(packet.AsSpan(4));

        var req = ParseAddTradeBag(packet);
        if (req == null)
        {
            log.LogWarning("C_ADD_TRADE_BAG: {Len} B packet (want {Want})", packet.Length, AddTradeBagPacketSize);
            return true;
        }
        var chr = s.SelectedCharacter;
        if (chr == null) return true;

        var payload = BuildAsAddTradeBag(TeraSharp.Arbiter.World.ContractBroker.PlanetId, (int)chr.Id, req.Value);
        Program.World?.SendFrame(AS_ADD_TRADE_BAG, payload);
        log.LogInformation(
            "C_ADD_TRADE_BAG: {Name} -> AS_ADD_TRADE_BAG contract {Cid}, pocket {Tab} slot {Slot} x{N}{Money}",
            chr.Name, req.Value.ContractId, req.Value.TabIndex, req.Value.InvenPos, req.Value.MoveAmount,
            req.Value.Money != 0 ? $", {req.Value.Money} money" : string.Empty);
        return true;
    }

    // =========================================================================================
    // 10. C_ASK_INTERACTIVE -> S_ANSWER_INTERACTIVE          (T62, the right-click context menu)
    // =========================================================================================

    /// <summary>
    /// C_ASK_INTERACTIVE (0x5639, 22073). Right-clicking a name - in the friend list, the party
    /// window or a chat line - asks the Arbiter who that is before the client will open the
    /// context menu. Without an answer the menu never opens, which is why friend delete was
    /// unreachable.
    ///
    /// <para>Layout from <c>Handler_C_ASK_INTERACTIVE</c> (FUN_1404dba80, Arb_part_040.c:19168),
    /// whose guard is <c>param_3 &lt; 0xE</c>:
    /// <c>[0x04] u16 TargetName offset</c> <c>[0x06] i32 AskType</c>
    /// <c>[0x0A] i32 TargetPlanetId</c>, then the NUL-terminated UTF-16LE name.</para>
    /// </summary>
    public const int AskInteractivePacketSize = 0x0E;                        // 14
    public const int AskInteractiveBodySize = AskInteractivePacketSize - 4;  // 10
    /// <summary>S_ANSWER_INTERACTIVE's fixed part: seven fields then the name.</summary>
    public const int AnswerInteractivePacketSize = 0x18;                     // 24

    /// <summary>C_ASK_INTERACTIVE, read at the handler's own offsets.</summary>
    public readonly record struct AskInteractiveRequest(int AskType, int TargetPlanetId, string TargetName);

    /// <summary>Parse C_ASK_INTERACTIVE from the BODY (the dispatcher strips the 4-byte header).</summary>
    public static AskInteractiveRequest? ParseAskInteractive(ReadOnlySpan<byte> body)
    {
        if (body.Length < AskInteractiveBodySize) return null;
        return new AskInteractiveRequest(
            BitConverter.ToInt32(body[0x02..]),     // packet 0x06
            BitConverter.ToInt32(body[0x06..]),     // packet 0x0A
            ReadWString(body, 0x00));               // the offset slot at packet 0x04
    }

    /// <summary>
    /// S_ANSWER_INTERACTIVE (0x85C8). Field names and order are the PDL dumper FUN_14024f7e0
    /// (Arb_part_018.c:9818); the writer in the handler emits them in exactly this order:
    /// <c>[0x04] u16 TargetName offset</c> <c>[0x06] i32 AskType</c> <c>[0x0A] i32 TargetTemplateId</c>
    /// <c>[0x0E] i32 TargetLevel</c> <c>[0x12] bool TargetInParty</c> <c>[0x13] bool TargetInGuild</c>
    /// <c>[0x14] i32 TargetPlanetId</c>, then the name at 0x18.
    /// </summary>
    public static byte[] BuildAnswerInteractive(int askType, int targetTemplateId, int targetLevel,
        bool targetInParty, bool targetInGuild, int targetPlanetId, string targetName)
    {
        var n = WString(targetName);
        int total = AnswerInteractivePacketSize + n.Length;
        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_ANSWER_INTERACTIVE).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)AnswerInteractivePacketSize).CopyTo(p, 4);
        BitConverter.GetBytes(askType).CopyTo(p, 6);
        BitConverter.GetBytes(targetTemplateId).CopyTo(p, 10);
        BitConverter.GetBytes(targetLevel).CopyTo(p, 14);
        p[18] = (byte)(targetInParty ? 1 : 0);
        p[19] = (byte)(targetInGuild ? 1 : 0);
        BitConverter.GetBytes(targetPlanetId).CopyTo(p, 20);
        n.CopyTo(p, AnswerInteractivePacketSize);
        return p;
    }

    /// <summary>
    /// Is this character in a party? Parties are in-memory Arbiter state (status/PARTY-DESIGN.md),
    /// so nothing in CharacterStore can answer it. The human wires this to
    /// <c>pm.FindByMember(id) != null</c> next to the other PartyManager wiring; until then the
    /// flag is false, which only costs the client the "leave party" menu entry.
    /// </summary>
    public static Func<int, bool>? PartyLookup;

    /// <summary>
    /// C_ASK_INTERACTIVE. The real handler requires the target to be IN WORLD
    /// (<c>FUN_14082dc50(userMgr, name, 2)</c> is a by-name lookup with state 2) and answers
    /// nothing at all when the lookup misses - no packet, no error. We do the same, then fall
    /// back to the stored character row so a right-click on an offline friend still opens a menu.
    /// </summary>
    public static bool OnAskInteractive(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var req = ParseAskInteractive(body.Span);
        if (req == null)
        {
            log.LogWarning("C_ASK_INTERACTIVE: {Len} B body (want {Want})", body.Length, AskInteractiveBodySize);
            return true;
        }
        var r = req.Value;
        if (r.TargetName.Length == 0) return true;

        int templateId = 0, level = 0, dbId = 0;
        var bridge = Program.World;
        if (bridge != null)
        {
            foreach (var other in bridge.InWorldSessions())
            {
                var c = other.SelectedCharacter;
                if (c == null || !string.Equals(c.Name, r.TargetName, StringComparison.OrdinalIgnoreCase)) continue;
                dbId = (int)c.Id; templateId = c.TemplateId; level = c.Level;
                break;
            }
        }
        if (dbId == 0)
        {
            var row = Program.Store?.GetCharacterByName(r.TargetName);
            if (row == null)
            {
                // The real Arbiter sends nothing for a name it cannot resolve.
                log.LogDebug("C_ASK_INTERACTIVE: '{Name}' resolves to nobody - no answer", r.TargetName);
                return true;
            }
            dbId = row.Id; templateId = row.TemplateId; level = row.Level;
        }

        bool inParty = PartyLookup != null && PartyLookup(dbId);
        bool inGuild = (Program.Store?.GetGuildIdOf(dbId) ?? 0) != 0;

        s.Send(BuildAnswerInteractive(r.AskType, templateId, level, inParty, inGuild,
                                      TeraSharp.Arbiter.World.ContractBroker.PlanetId, r.TargetName));
        log.LogInformation("C_ASK_INTERACTIVE: {Who} asked about {Name} (type {Type}, party {P}, guild {G})",
            s.SelectedCharacter?.Name, r.TargetName, r.AskType, inParty, inGuild);
        return true;
    }

    // =========================================================================================
    // 11. C_WATCHED_MOVIES -> S_WATCHED_MOVIES               (T62, the intro cutscene replay)
    // =========================================================================================

    /// <summary>
    /// C_WATCHED_MOVIES (0xA5DA, 42458). The client asks, once per session, which cinematics this
    /// player has already seen; anything missing from the answer is played again. Unanswered, the
    /// intro replays on every relog.
    ///
    /// <para><c>Handler_C_WATCHED_MOVIES</c> (FUN_1404f2300, Arb_part_041.c:14640) guards on
    /// <c>param_3 &lt; 4</c> - i.e. the header only - reads no field, and calls
    /// <c>Account::SendWatchedMoviesToClient</c>.</para>
    /// </summary>
    public const int WatchedMoviesPacketSize = 4;
    public const int WatchedMoviesBodySize = 0;
    /// <summary>S_WATCHED_MOVIES' fixed part: <c>[u16 count][u16 firstOffset]</c>.</summary>
    public const int WatchedMoviesReplyFixedSize = 8;
    /// <summary>One list entry: <c>[u16 here][u16 next][u32 movieId]</c>.</summary>
    public const int WatchedMovieEntrySize = 8;

    /// <summary>
    /// S_WATCHED_MOVIES (0x97A4), built the way <c>Account::SendWatchedMoviesToClient</c>
    /// (FUN_1407856c0, Arb_part_065.c:12231) builds it: the standard TERA list encoding, where the
    /// count sits at 0x04, the offset of the first element at 0x06, and each element starts with
    /// its OWN offset and the offset of the next (0 on the last).
    /// </summary>
    public static byte[] BuildWatchedMovies(IReadOnlyList<int>? movieIds)
    {
        movieIds ??= Array.Empty<int>();
        int n = movieIds.Count;
        int total = WatchedMoviesReplyFixedSize + n * WatchedMovieEntrySize;
        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_WATCHED_MOVIES).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)n).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(n == 0 ? 0 : WatchedMoviesReplyFixedSize)).CopyTo(p, 6);
        for (int i = 0; i < n; i++)
        {
            int at = WatchedMoviesReplyFixedSize + i * WatchedMovieEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i == n - 1 ? 0 : at + WatchedMovieEntrySize)).CopyTo(p, at + 2);
            BitConverter.GetBytes(movieIds[i]).CopyTo(p, at + 4);
        }
        return p;
    }

    /// <summary>
    /// C_WATCHED_MOVIES. Answers from the stored per-character list.
    ///
    /// <para><b>The real Arbiter stores this per ACCOUNT, not per character</b> -
    /// <c>Account::CachedWatchedMoviesWithLock</c> (FUN_1407095a0) reads it with the stored
    /// procedure <c>spLoadUserWatchedMovies</c> and merges the ReplayMovieData sheet on top.
    /// We store it per character because that is the row TeraSharp owns; if two characters on one
    /// account should share the flag, the store method is the one place to change.</para>
    /// </summary>
    public static bool OnWatchedMovies(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var chr = s.SelectedCharacter;
        var movies = chr != null && Program.Store != null
            ? Program.Store.GetWatchedMovies((int)chr.Id)
            : (IReadOnlyList<int>)Array.Empty<int>();
        s.Send(BuildWatchedMovies(movies));
        log.LogInformation("C_WATCHED_MOVIES: {Name} has seen {N} cinematic(s)", chr?.Name, movies.Count);
        return true;
    }

    // =========================================================================================
    // 12. C_FINDNAME -> S_FINDNAME                           (T62, name completion while typing)
    // =========================================================================================

    /// <summary>
    /// C_FINDNAME (0x7B75, 31605). Sent on every keystroke while a name is being typed; the body
    /// grows 2 bytes per character. <c>Handler_C_FINDNAME</c> (FUN_1404e0620, Arb_part_041.c:2602)
    /// guards on <c>param_3 &lt; 10</c>:
    /// <c>[0x04] u16 Query offset</c> <c>[0x06] i32 FindType</c>, then the NUL-terminated name.
    /// </summary>
    public const int FindNamePacketSize = 10;
    public const int FindNameBodySize = FindNamePacketSize - 4;              // 6
    /// <summary>S_FINDNAME's fixed part: two string offsets and the echoed type.</summary>
    public const int FindNameReplyFixedSize = 12;
    /// <summary>
    /// <c>User::FindNameLog</c> (FUN_1403846b0, Arb_part_028.c:16187) stops at ten matches -
    /// every one of its three passes re-tests <c>list.size() &lt; 10</c>.
    /// </summary>
    public const int FindNameMaxResults = 10;
    /// <summary>
    /// The character the real handler joins matches with. It is a single wchar from
    /// <c>DAT_140b41838</c>, which Ghidra did not type, so this is the ONE byte in T62 that is a
    /// guess rather than a reading. A comma is what the client's own name lists use. If completion
    /// shows one run-together string, this is the constant to change.
    /// </summary>
    public const char FindNameSeparator = ',';
    /// <summary>Only type 1 does anything; FindNameLog returns an empty list for anything else.</summary>
    public const int FindNameTypeNameLog = 1;

    /// <summary>C_FINDNAME, read at the handler's own offsets.</summary>
    public readonly record struct FindNameRequest(int FindType, string Query);

    /// <summary>Parse C_FINDNAME from the BODY.</summary>
    public static FindNameRequest? ParseFindName(ReadOnlySpan<byte> body)
    {
        if (body.Length < FindNameBodySize) return null;
        return new FindNameRequest(BitConverter.ToInt32(body[0x02..]), ReadWString(body, 0x00));
    }

    /// <summary>
    /// S_FINDNAME (0xF95D): <c>[0x04] u16 Query offset</c> <c>[0x06] u16 Result offset</c>
    /// <c>[0x08] i32 FindType</c>, then the echoed query and then the joined match list - both
    /// NUL-terminated UTF-16LE, and the result is an empty string when nothing matched.
    ///
    /// <para>Pinned against the real Arbiter's own replies in
    /// <c>D:\packetlogs\cap_social_client_ctl.txt</c> frames 1093 / 1096 / 1098.</para>
    /// </summary>
    public static byte[] BuildFindName(int findType, string query, IReadOnlyList<string>? matches)
    {
        var q = WString(query);
        var joined = matches == null || matches.Count == 0
            ? string.Empty
            : string.Join(FindNameSeparator, matches);
        var r = WString(joined);
        int total = FindNameReplyFixedSize + q.Length + r.Length;
        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_FINDNAME).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)FindNameReplyFixedSize).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(FindNameReplyFixedSize + q.Length)).CopyTo(p, 6);
        BitConverter.GetBytes(findType).CopyTo(p, 8);
        q.CopyTo(p, FindNameReplyFixedSize);
        r.CopyTo(p, FindNameReplyFixedSize + q.Length);
        return p;
    }

    /// <summary>
    /// C_FINDNAME. The real handler searches the friend list, then the recent-name log, then the
    /// guild roster, stopping at ten. TeraSharp has no name log, so the brief's substitute is a
    /// prefix match over the characters table - a superset that behaves the same for the case that
    /// matters (typing a friend's name) and never returns more than the same ten.
    /// </summary>
    public static bool OnFindName(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var req = ParseFindName(body.Span);
        if (req == null)
        {
            log.LogWarning("C_FINDNAME: {Len} B body (want {Want})", body.Length, FindNameBodySize);
            return true;
        }
        var r = req.Value;
        IReadOnlyList<string> matches = Array.Empty<string>();
        if (r.FindType == FindNameTypeNameLog && r.Query.Length > 0 && Program.Store != null)
            matches = Program.Store.FindCharacterNamesByPrefix(r.Query, FindNameMaxResults,
                                                              s.SelectedCharacter?.Name);
        s.Send(BuildFindName(r.FindType, r.Query, matches));
        return true;
    }

    // =========================================================================================
    // 13. S_VISITED_SECTION_LIST                            (T75, the intro cutscene replay)
    // =========================================================================================

    /// <summary>
    /// S_VISITED_SECTION_LIST (0xA853). The client plays a section's intro cinematic when it has
    /// no record of having been there. T45 stored the visits and pushed them to World as
    /// AS_UPDATE_VISITED_SECTION_LIST, but never told the CLIENT - so on Island of Dawn the intro
    /// replayed on every relog. Inside an instance it did not, because the instance's section is
    /// re-entered within the session and S_VISIT_NEW_SECTION alone is enough.
    ///
    /// <para>The real Arbiter sends it immediately after C_LOAD_TOPO_FIN:
    /// <c>D:\packetlogs\cap_social_client_ctl.txt</c> frame 256 is the C_LOAD_TOPO_FIN and 257 is
    /// this packet - the same point where HandlerRegistry already pushes the World-side list.</para>
    ///
    /// <para>Standard TERA list encoding, the same shape as S_WATCHED_MOVIES: count at 0x04, the
    /// first entry's offset at 0x06, each entry carrying its own offset and the next one's.
    /// The three u32 fields are the same (mapId, guardId, sectionId) triple that
    /// S_VISIT_NEW_SECTION and the stored row carry.</para>
    /// </summary>
    public const int VisitedSectionListFixedSize = 8;
    /// <summary><c>[u16 here][u16 next][u32 mapId][u32 guardId][u32 sectionId]</c>.</summary>
    public const int VisitedSectionListEntrySize = 16;

    public static byte[] BuildVisitedSectionList(IReadOnlyList<CharacterStore.VisitedSection>? sections)
    {
        sections ??= Array.Empty<CharacterStore.VisitedSection>();
        int n = sections.Count;
        int total = VisitedSectionListFixedSize + n * VisitedSectionListEntrySize;
        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_VISITED_SECTION_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)n).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(n == 0 ? 0 : VisitedSectionListFixedSize)).CopyTo(p, 6);
        for (int i = 0; i < n; i++)
        {
            int at = VisitedSectionListFixedSize + i * VisitedSectionListEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i == n - 1 ? 0 : at + VisitedSectionListEntrySize)).CopyTo(p, at + 2);
            BitConverter.GetBytes(sections[i].MapId).CopyTo(p, at + 4);
            BitConverter.GetBytes(sections[i].GuardId).CopyTo(p, at + 8);
            BitConverter.GetBytes(sections[i].SectionId).CopyTo(p, at + 12);
        }
        return p;
    }

    /// <summary>
    /// The whole stored list for this character, ready to send. Returns an empty packet (8 bytes,
    /// both slots zero) when nothing is stored, which is still an answer - the client only replays
    /// an intro for a section absent from it.
    /// </summary>
    public static byte[] BuildVisitedSectionListFor(GameSession s)
    {
        var chr = s?.SelectedCharacter;
        var rows = chr != null && Program.Store != null
            ? Program.Store.GetVisitedSections((int)chr.Id)
            : (IReadOnlyList<CharacterStore.VisitedSection>)Array.Empty<CharacterStore.VisitedSection>();
        return BuildVisitedSectionList(rows);
    }
    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Reads a NUL-terminated UTF-16LE string whose u16 PACKET offset sits at
    /// <paramref name="slotIndex"/> of the BODY. Empty for the 0 / out-of-range offsets the real
    /// handlers fall back on (<c>if ((uVar1 == 0) || (*param_2 &lt;= uVar1)) puVar6 = &amp;DAT_140d3e020;</c>).
    /// </summary>
    /// <summary>A NUL-terminated UTF-16LE string, as the inter-server and client writers emit it.</summary>
    public static byte[] WString(string? s)
    {
        s ??= string.Empty;
        var b = new byte[s.Length * 2 + 2];
        System.Text.Encoding.Unicode.GetBytes(s, 0, s.Length, b, 0);
        return b;   // the two trailing zero bytes are the terminator
    }

    public static string ReadWString(ReadOnlySpan<byte> body, int slotIndex)
    {
        if (slotIndex + 2 > body.Length) return string.Empty;
        int at = BitConverter.ToUInt16(body[slotIndex..]) - 4;      // packet-relative -> body index
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
