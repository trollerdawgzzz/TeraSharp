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

    /// <summary>T82. 22603. Show me that player s guild crest.</summary>
    public const ushort C_GET_USER_GUILD_LOGO = 0x584B;    // 22603
    /// <summary>T82. 32250.</summary>
    public const ushort S_GET_USER_GUILD_LOGO = 0x7DFA;    // 32250
    /// <summary>T82. 31107. The skill-polishing window s two loads.</summary>
    public const ushort C_RQ_SKILL_POLISHING_LIST = 0x7983;      // 31107
    public const ushort S_RP_SKILL_POLISHING_LIST = 0xDA7E;      // 55934
    /// <summary>T82. 44343 -&gt; 59984.</summary>
    public const ushort C_RQ_SKILL_POLISHING_EXP_INFO = 0xAD37;  // 44343
    public const ushort S_RP_SKILL_POLISHING_EXP_INFO = 0xEA50;  // 59984
    // ---- T83: the card page and the guild perk / crest windows ----
    /// <summary>T83. 45149. "Show me my own card page."</summary>
    public const ushort C_REQUEST_MY_ACTIVATE_CARD_COMBINE_LIST_DATA = 0xB05D;
    /// <summary>T83. 61369. The same for another player, addressed by gameId.</summary>
    public const ushort C_REQUEST_OTHERS_ACTIVATE_CARD_COMBINE_LIST_DATA_WITH_GAMEID = 0xEFB9;
    /// <summary>T83. 55579. Another player's card data, addressed by gameId.</summary>
    public const ushort C_REQUEST_OTHERS_CARD_DATA_WITH_GAMEID = 0xD91B;
    /// <summary>T83. 25504. The guild window's perk tab.</summary>
    public const ushort C_REQUEST_GUILD_PERK_LIST = 0x63A0;

    public const ushort S_CARD_DATA = 0x507C;              // 20604
    public const ushort S_ACTIVATE_CARD_COMBINE_LIST_DATA = 0x6D12;   // 27922
    public const ushort S_CHANGE_CARD_PRESET = 0xDCBA;     // 56506
    public const ushort S_GUILD_PERK_LIST = 0xE9FC;        // 59900
    public const ushort S_CREST_INFO = 0xB04B;             // 45131
    public const ushort S_SHOW_CREST_LEARN = 0xEBD1;       // 60369
    public const ushort S_GUILD_APPLY_COUNT = 0xC89A;      // 51354

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
        // T78 - the other three packets of the party-matching window. The first three of the
        // window (C_REQUEST_PARTY_MATCH_INFO / _MY_ / _WINDOW_CLOSED, declared above) were
        // already here because they were on the accept-silently list; these three were still
        // being FORWARDED, which is how the publish and the link button produced World's
        // "handler has not been implemented yet!!!". All six are answered by
        // World.PartyMatchManager now - status/PARTY-MATCH.md.
        World.PartyMatchManager.C_REGISTER_PARTY_INFO,
        World.PartyMatchManager.C_UNREGISTER_PARTY_INFO,
        World.PartyMatchManager.C_REQUEST_PARTY_MATCH_LINK,
        // T80 - the five guild-war packets. All five were being FORWARDED, so the window,
        // the history page, the check, the declare and the withdrawal all produced World's
        // "handler has not been implemented yet!!!". World.GuildWarManager answers them now
        // - status/GUILD-WAR.md.
        World.GuildWarManager.C_OPEN_GUILD_WAR_WINDOW,
        World.GuildWarManager.C_VIEW_GUILD_WAR,
        World.GuildWarManager.C_CHECK_TO_DECLARE_GUILD_WAR,
        World.GuildWarManager.C_DECLARE_GUILD_WAR,
        World.GuildWarManager.C_WITHDRAW_GUILD_WAR,
        // T82 - three more the Arbiter answers from its own tables. The two skill-polishing
        // loads were on the RegNoop list, which is not the same thing: a noop registers the
        // opcode (so it is not forwarded) but sends NOTHING, and the real Arbiter answers both
        // in the lobby burst - cap_social4_client frames 145 and 146.
        C_GET_USER_GUILD_LOGO, C_RQ_SKILL_POLISHING_LIST, C_RQ_SKILL_POLISHING_EXP_INFO,
        // T83 - cards + guild perks
        C_REQUEST_MY_ACTIVATE_CARD_COMBINE_LIST_DATA, C_REQUEST_OTHERS_ACTIVATE_CARD_COMBINE_LIST_DATA_WITH_GAMEID,
        C_REQUEST_OTHERS_CARD_DATA_WITH_GAMEID, C_REQUEST_GUILD_PERK_LIST,
        // T87 - the telemetry and housekeeping group. Every one has a Handler_C_* in the
        // Arbiter and none has a World handler, so a forward can only produce World s
        // "handler has not been implemented yet!!!". C_CANCEL_RETURN_TO_LOBBY is deliberately
        // absent: the registry has answered it since T33 and it does forward to World.
        C_PONG, C_CHECK_RTT, C_PLAY_TIME, C_REQUEST_PLAYTIME, C_REQUEST_PERF, C_SEND_UI_LOG,
        C_GET_MY_IP, C_XIGNCODE_SECURITY_DATA, C_INVALID_BUILD_VERSION,
        C_REQUEST_LATEST_UPDATE_NOTIFICATION, C_CONFIRM_UPDATE_NOTIFICATION,
        C_SECOND_PASSWORD_AUTH, C_SECOND_PASSWORD_REGISTER, C_REFRESH_API_ACCESS_TOKEN,
        C_CANCEL_EXIT,
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
        else
            // T79: this branch is the one that stops the zone cinematic. cap_newchar_client frame
            // 436 answers isFirstVisit 1 and the intro plays; cap_social_client frame 374 answers
            // 0 for the same section on an existing character and it does not. If the intro keeps
            // replaying, this line says whether the stored row survived the relog - and a live log
            // with no line at all means C_VISIT_NEW_SECTION is not reaching us.
            log.LogInformation("C_VISIT_NEW_SECTION: {Name} re-entered map {Map} guard {Guard} "
                + "section {Section} - isFirstVisit 0, no cinematic", chr?.Name, mapId, guardId, sectionId);

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
    // =========================================================================================
    // 14. Guild crest and the skill-polishing window                                     (T82)
    // =========================================================================================

    /// <summary>
    /// C_GET_USER_GUILD_LOGO (0x584B) -&gt; S_GET_USER_GUILD_LOGO (0x7DFA). Right-clicking a
    /// player whose name carries a guild tag asks for that guild s crest.
    ///
    /// <para><b>Arbiter-built.</b> The crest is a column on the guilds row - the Arbiter owns
    /// guild storage outright (status/GUILD-DESIGN.md section 0) and World only ever gets a
    /// read-only mirror, so nothing about this request crosses the link. cap_social4_client
    /// frame 2910 is the request, <c>EB 03 00 00  02 00 00 00</c> = player 1003, guild 2, and
    /// 2911 is the answer, <c>10 00 00 00  EB 03 00 00  02 00 00 00</c>.</para>
    ///
    /// <para>The shipped <c>S_GET_USER_GUILD_LOGO.1.def</c> is RIGHT: a <c>bytes</c> ref is
    /// <c>[u16 offset][u16 count]</c>, and an EMPTY blob still gets a real offset - 16, the
    /// packet length - because DefinitionWriter patches the slot to the current end whether or
    /// not there is data. That is exactly the 10 00 00 00 the capture carries, so a guild with
    /// no uploaded crest reproduces byte for byte.</para>
    /// </summary>
    public const int GetUserGuildLogoBodySize = 8;

    /// <summary>The field set for one crest. A null or empty blob is the no-crest form.</summary>
    public static Dictionary<string, object> BuildGuildLogoFields(int playerId, int guildId, byte[]? logo)
        => new()
        {
            ["playerId"] = playerId,
            ["guildId"] = guildId,
            ["logo"] = logo ?? Array.Empty<byte>(),
        };

    /// <summary>Handler for C_GET_USER_GUILD_LOGO. Body is <c>[i32 playerId][i32 guildId]</c>.</summary>
    public static bool OnGetUserGuildLogo(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (body.Length < GetUserGuildLogoBodySize) return true;
        var b = body.Span;
        int playerId = BitConverter.ToInt32(b);
        int guildId = BitConverter.ToInt32(b[4..]);
        var logo = Program.Store?.GetGuildLogo(guildId);
        log.LogDebug("C_GET_USER_GUILD_LOGO: player {Pid} guild {Gid} -> {N} B crest",
            playerId, guildId, logo?.Length ?? 0);
        s.SendByDef("S_GET_USER_GUILD_LOGO", BuildGuildLogoFields(playerId, guildId, logo));
        return true;
    }

    // =========================================================================================
    // 14. Cards, crests and guild perks                                              (T83)
    //
    // From cap_social4_client.log (client 1) and cap_social4_client2.log (client 2). Two
    // accounts, five characters between them, so every layout below has at least three
    // independent instances.
    // =========================================================================================

    /// <summary>
    /// The four bytes every one of these packets carries in the middle of its owner block.
    /// <c>SA_ENTER_WORLD</c> (0x138C) puts the same eight bytes at payload +24 for every
    /// character in every capture - cap_social, cap_social2, cap_social3, cap_social4 and
    /// cap_newchar all read <c>01 00 F0 0A 00 80 00 00</c> for the first character to enter -
    /// so this is the world half of a pdid and the u16 in front of it is a per-enter-world
    /// serial, not a character id. cap_social4 counts 1 (dob), 2 (two), 4 (New), 5 (dob again):
    /// the same character gets a different number on its second login.
    /// </summary>
    public const uint PdidWorldWord = 0x80000AF0;

    /// <summary>The eight bytes: <c>[u16 serial][u32 PdidWorldWord][u16 0]</c>.</summary>
    public const int PdidSize = 8;

    private static void WritePdid(byte[] p, int at, int serial)
    {
        BitConverter.GetBytes((ushort)serial).CopyTo(p, at);
        BitConverter.GetBytes(PdidWorldWord).CopyTo(p, at + 2);
        // the trailing u16 is zero in every captured frame
    }

    /// <summary>
    /// S_CARD_DATA (0x507C). Pushed at enter-world for your own character and sent in reply to
    /// <c>C_REQUEST_OTHERS_CARD_DATA_WITH_GAMEID</c> for somebody else's.
    /// <code>
    ///   +0x04 u16 count / +0x06 u16 off    array A - empty in all six captured frames
    ///   +0x08 u16 count / +0x0A u16 off    array B - one 12-byte element in all six
    ///   +0x0C i32 0
    ///   +0x10 u16 nameOffset (0x2A)
    ///   +0x12 pdid (8 B)
    ///   +0x1A i32 1   +0x1E i32 0   +0x22 i32 1   +0x26 i32 0
    ///   +0x2A wstr ownerName
    ///   element: [u16 here][u16 next][i32 0][i32 0]
    /// </code>
    /// <para>Every captured frame shows array A empty and array B holding exactly one all-zero
    /// element, on characters that owned no cards. The account that DID register one (card
    /// 310010, cap_social4.log seq 7032) never reopened the page in the tap, so what a populated
    /// row looks like is not pinned by anything - which is why <paramref name="slots"/> defaults
    /// to the captured single empty slot rather than being filled from the cards table.</para>
    /// </summary>
    public const int CardDataFixedSize = 0x2A;
    public const int CardDataSlotSize = 12;

    /// <summary>
    /// T86. The same packet, assembled for one character: array B carries that character's
    /// mounts, one element per mounted card, as <c>[i32 presetIndex][i32 cardTemplateId]</c>.
    ///
    /// <para>Only the EMPTY form is pinned - every captured frame is a character with nothing
    /// mounted and an empty collection, and it carries exactly one all-zero element, which is
    /// what a character with no mounts still gets here. Array A stays empty for the same reason
    /// squared: no captured frame has an element in it, so its stride is unknown, and the account
    /// collection (which is the obvious candidate for it) has nowhere it could go without
    /// inventing one.</para>
    /// </summary>
    public static byte[] BuildCardDataFor(CharacterStore? store, int characterId,
                                          string? ownerName, int pdidSerial)
    {
        var mounts = store?.GetCardMounts(characterId);
        if (mounts is null || mounts.Count == 0) return BuildCardData(ownerName, pdidSerial);

        var slots = new List<(int, int)>(mounts.Count);
        foreach (var m in mounts) slots.Add((m.PresetIndex, m.CardTemplateId));
        return BuildCardData(ownerName, pdidSerial, slots);
    }

    /// <summary>T86. The preset the client is told is active: the lowest one this character has a
    /// mount in, and 0 when it has none - which is what all seventy-two captured frames carry,
    /// every one of them from a character with nothing mounted.</summary>
    public static byte[] BuildChangeCardPresetFor(CharacterStore? store, int characterId)
    {
        var mounts = store?.GetCardMounts(characterId);
        return BuildChangeCardPreset(mounts is null || mounts.Count == 0 ? 0 : mounts[0].PresetIndex);
    }

    public static byte[] BuildCardData(string? ownerName, int pdidSerial,
                                       IReadOnlyList<(int A, int B)>? slots = null)
    {
        slots ??= new[] { (0, 0) };
        var name = WString(ownerName);
        int nameAt = CardDataFixedSize;
        int slotsAt = nameAt + name.Length;
        int total = slotsAt + slots.Count * CardDataSlotSize;

        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_CARD_DATA).CopyTo(p, 2);
        // array A stays [0][0]; array B points past the name
        BitConverter.GetBytes((ushort)slots.Count).CopyTo(p, 8);
        BitConverter.GetBytes((ushort)(slots.Count == 0 ? 0 : slotsAt)).CopyTo(p, 10);
        BitConverter.GetBytes((ushort)nameAt).CopyTo(p, 0x10);
        WritePdid(p, 0x12, pdidSerial);
        BitConverter.GetBytes(1).CopyTo(p, 0x1A);
        BitConverter.GetBytes(1).CopyTo(p, 0x22);
        name.CopyTo(p, nameAt);
        for (int i = 0; i < slots.Count; i++)
        {
            int at = slotsAt + i * CardDataSlotSize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i == slots.Count - 1 ? 0 : at + CardDataSlotSize)).CopyTo(p, at + 2);
            BitConverter.GetBytes(slots[i].A).CopyTo(p, at + 4);
            BitConverter.GetBytes(slots[i].B).CopyTo(p, at + 8);
        }
        return p;
    }

    /// <summary>
    /// S_ACTIVATE_CARD_COMBINE_LIST_DATA (0x6D12) - the answer to both
    /// <c>C_REQUEST_MY_ACTIVATE_CARD_COMBINE_LIST_DATA</c> and the WITH_GAMEID form.
    /// <code>
    ///   +0x04 u16 count / +0x06 u16 off    empty in all six captured frames
    ///   +0x08 u16 nameOffset (0x12)
    ///   +0x0A pdid (8 B)
    ///   +0x12 wstr ownerName
    /// </code>
    /// The array is the list of ACTIVE card combinations; nobody in either tap had one.
    /// </summary>
    public const int ActivateCardListFixedSize = 0x12;

    public static byte[] BuildActivateCardCombineList(string? ownerName, int pdidSerial)
    {
        var name = WString(ownerName);
        var p = new byte[ActivateCardListFixedSize + name.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ACTIVATE_CARD_COMBINE_LIST_DATA).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)ActivateCardListFixedSize).CopyTo(p, 8);
        WritePdid(p, 0x0A, pdidSerial);
        name.CopyTo(p, ActivateCardListFixedSize);
        return p;
    }

    /// <summary>S_CHANGE_CARD_PRESET (0xDCBA): one i32, the preset now active. All seventy-two
    /// captured frames across the two clients carry 0.</summary>
    public static byte[] BuildChangeCardPreset(int preset = 0)
    {
        var p = new byte[8];
        BitConverter.GetBytes((ushort)8).CopyTo(p, 0);
        BitConverter.GetBytes(S_CHANGE_CARD_PRESET).CopyTo(p, 2);
        BitConverter.GetBytes(preset).CopyTo(p, 4);
        return p;
    }

    /// <summary>
    /// The scalars of S_GUILD_PERK_LIST that no store column is provably behind. Both captured
    /// guilds, side by side - <c>fdh</c> (frame 1611) and <c>sdg</c> (3136 and 3247):
    /// <code>
    ///   +0x0C  3 / 2          +0x10  1 / 1003     +0x14  1 / 1       +0x18  1 / 0
    ///   +0x1C  0 / 0          +0x20  0 / 0        +0x24  0 / 0       +0x28  100 / 100
    ///   +0x2C  0 / 0          +0x30  i64 0 / 10000000                +0x38  2 / 2
    ///   +0x3C  2 / 2          +0x40  0 / 0        +0x44  i64 0x6AAB41B1 / 0x6AAB19B2
    /// </code>
    /// Two instances is enough to say these are real fields rather than uninitialised stack, and
    /// not enough to name them: the obvious readings all break on one of the pair. (+0x44 looks
    /// like a creation time until you notice the guild whose window opened FIRST has the LATER
    /// stamp.) They are parameters with the captured defaults until a third guild turns up.
    /// </summary>
    public sealed record GuildPerkScalars
    {
        public int A { get; init; } = 3;
        public int B { get; init; } = 1;
        public int C { get; init; } = 1;
        public int D { get; init; } = 1;
        public int E { get; init; }
        public int F { get; init; }
        public int G { get; init; }
        public int MaxPoint { get; init; } = 100;
        public int I { get; init; }
        public long Money { get; init; }
        public int K { get; init; } = 2;
        public int L { get; init; } = 2;
        public int M { get; init; }
        public long Timestamp { get; init; }
    }

    /// <summary>
    /// S_GUILD_PERK_LIST (0xE9FC). Fixed part 0x4C, then the guild name, the master's name and
    /// the perk array. The element is ten bytes - <c>[u16 here][u16 next][i32 perkId][u8][u8]</c>
    /// - which is exactly <c>spLoadGuildPerkList</c>'s <c>{int perkId, tinyint, tinyint}</c>
    /// behind the usual pair, i.e. the <c>guild_perks</c> table row for row.
    /// </summary>
    public const int GuildPerkListFixedSize = 0x4C;
    public const int GuildPerkEntrySize = 10;

    public static byte[] BuildGuildPerkList(string? guildName, string? masterName,
        IReadOnlyList<CharacterStore.GuildPerkRow>? perks = null, GuildPerkScalars? scalars = null)
    {
        perks ??= Array.Empty<CharacterStore.GuildPerkRow>();
        var s = scalars ?? new GuildPerkScalars();
        var gn = WString(guildName);
        var mn = WString(masterName);
        int gnAt = GuildPerkListFixedSize, mnAt = gnAt + gn.Length, perksAt = mnAt + mn.Length;
        var p = new byte[perksAt + perks.Count * GuildPerkEntrySize];

        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_GUILD_PERK_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)perks.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(perks.Count == 0 ? 0 : perksAt)).CopyTo(p, 6);
        BitConverter.GetBytes((ushort)gnAt).CopyTo(p, 8);
        BitConverter.GetBytes((ushort)mnAt).CopyTo(p, 10);
        BitConverter.GetBytes(s.A).CopyTo(p, 0x0C);
        BitConverter.GetBytes(s.B).CopyTo(p, 0x10);
        BitConverter.GetBytes(s.C).CopyTo(p, 0x14);
        BitConverter.GetBytes(s.D).CopyTo(p, 0x18);
        BitConverter.GetBytes(s.E).CopyTo(p, 0x1C);
        BitConverter.GetBytes(s.F).CopyTo(p, 0x20);
        BitConverter.GetBytes(s.G).CopyTo(p, 0x24);
        BitConverter.GetBytes(s.MaxPoint).CopyTo(p, 0x28);
        BitConverter.GetBytes(s.I).CopyTo(p, 0x2C);
        BitConverter.GetBytes(s.Money).CopyTo(p, 0x30);
        BitConverter.GetBytes(s.K).CopyTo(p, 0x38);
        BitConverter.GetBytes(s.L).CopyTo(p, 0x3C);
        BitConverter.GetBytes(s.M).CopyTo(p, 0x40);
        BitConverter.GetBytes(s.Timestamp).CopyTo(p, 0x44);
        gn.CopyTo(p, gnAt);
        mn.CopyTo(p, mnAt);
        for (int i = 0; i < perks.Count; i++)
        {
            int at = perksAt + i * GuildPerkEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i == perks.Count - 1 ? 0 : at + GuildPerkEntrySize)).CopyTo(p, at + 2);
            BitConverter.GetBytes(perks[i].PerkId).CopyTo(p, at + 4);
            p[at + 8] = (byte)perks[i].FlagA;
            p[at + 9] = (byte)perks[i].FlagB;
        }
        return p;
    }

    /// <summary>
    /// S_CREST_INFO (0xB04B): <c>[u16 count][u16 firstOffset][i32 CrestPoint][i32 CrestExPoint]</c>
    /// then nine-byte elements <c>[u16 here][u16 next][i32 crestId][u8 flag]</c>.
    ///
    /// <para>The two i32s are SA_CREST_POINT's <c>NewPoint</c> and <c>NewExPoint</c> - frames
    /// 5105 and 5108 both read 10 at +0x08 right after the crest-point write, and the frames
    /// before the write read 0. The element ids at frame 5108 (33000, 33008, 33012, 33018,
    /// 33020, 33029, 33031, 33033, 33034, 33038, 33041) are exactly the ids
    /// SA_LEARN_ALL_CREST_ACQUIRABLE grants, which is where the <c>crests</c> table gets them.</para>
    /// </summary>
    public const int CrestInfoFixedSize = 0x10;
    public const int CrestInfoEntrySize = 9;

    public static byte[] BuildCrestInfo(IReadOnlyList<int>? crestIds, int point = 0, int exPoint = 0)
    {
        crestIds ??= Array.Empty<int>();
        var p = new byte[CrestInfoFixedSize + crestIds.Count * CrestInfoEntrySize];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_CREST_INFO).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)crestIds.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(crestIds.Count == 0 ? 0 : CrestInfoFixedSize)).CopyTo(p, 6);
        BitConverter.GetBytes(point).CopyTo(p, 8);
        BitConverter.GetBytes(exPoint).CopyTo(p, 0x0C);
        for (int i = 0; i < crestIds.Count; i++)
        {
            int at = CrestInfoFixedSize + i * CrestInfoEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i == crestIds.Count - 1 ? 0 : at + CrestInfoEntrySize)).CopyTo(p, at + 2);
            BitConverter.GetBytes(crestIds[i]).CopyTo(p, at + 4);
        }
        return p;
    }

    /// <summary>S_SHOW_CREST_LEARN (0xEBD1): a bare four-byte push, no body at all
    /// (frame 5109). It opens the crest-learning window.</summary>
    public static byte[] BuildShowCrestLearn()
        => new byte[] { 0x04, 0x00, unchecked((byte)S_SHOW_CREST_LEARN), (byte)(S_SHOW_CREST_LEARN >> 8) };

    /// <summary>S_GUILD_APPLY_COUNT (0xC89A): one i32, how many applications are waiting
    /// (frame 3143, a guild with none).</summary>
    public static byte[] BuildGuildApplyCount(int count)
    {
        var p = new byte[8];
        BitConverter.GetBytes((ushort)8).CopyTo(p, 0);
        BitConverter.GetBytes(S_GUILD_APPLY_COUNT).CopyTo(p, 2);
        BitConverter.GetBytes(count).CopyTo(p, 4);
        return p;
    }

    // ---- the four client requests -----------------------------------------------------

    /// <summary>The pdid serial we send. TeraSharp has no enter-world counter, and the client
    /// only uses the value to tell one owner's page from another's, so the player's own id is
    /// both stable and unique - which the captured serial is not (the same character got 1 and
    /// then 5 on its second login).</summary>
    private static int PdidSerialFor(GameSession s) => (int)s.PlayerId;

    /// <summary>The name shown on a card page: the requested character's, or the caller's.</summary>
    private static string OwnerName(GameSession s) => s.SelectedCharacter?.Name ?? string.Empty;

    /// <summary>C_REQUEST_MY_ACTIVATE_CARD_COMBINE_LIST_DATA (0xB05D), body empty.</summary>
    public static bool OnRequestMyActivateCardCombineList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        s.Send(BuildActivateCardCombineList(OwnerName(s), PdidSerialFor(s)));
        return true;
    }

    /// <summary>
    /// T86. The card page of one character, from its ACCOUNT's collection and its OWN mounts -
    /// the two halves the three DB writes keep apart. The collection is looked up for the log
    /// line and for the emptiness test; it has no slot on the wire that any capture pins.
    /// </summary>
    public static void SendCardPage(GameSession s, int characterId, string? name, int serial, ILogger log)
    {
        var store = Program.Store;
        long account = store?.AccountOf(characterId) ?? 0;
        int owned = account > 0 ? store!.GetAccountCards(account).Count : 0;
        log.LogDebug("card page for {Name} (character {Id}, account {A}): {N} card(s), {M} mount(s)",
            name, characterId, account, owned, store?.GetCardMounts(characterId).Count ?? 0);
        s.Send(BuildCardDataFor(store, characterId, name, serial));
    }

    /// <summary>
    /// C_RQ_SKILL_POLISHING_LIST -&gt; S_RP_SKILL_POLISHING_LIST, and
    /// C_RQ_SKILL_POLISHING_EXP_INFO -&gt; S_RP_SKILL_POLISHING_EXP_INFO.
    ///
    /// <para><b>Arbiter-built, and both were silently dropped.</b> Both C_ packets were on
    /// HandlerRegistry s RegNoop list, which stops them reaching World but sends nothing back -
    /// so the skill-polishing panel never populated. The real Arbiter answers both in the lobby
    /// burst, before the world hand-off: cap_social4_client frame 145 is an
    /// S_RP_SKILL_POLISHING_LIST of eight zero bytes (two empty arrays) and 146 is an
    /// S_RP_SKILL_POLISHING_EXP_INFO of thirty-six (three int32 and three int64, all zero).</para>
    ///
    /// <para>Both shipped defs are right, and both frames are the empty form for a character
    /// that has polished nothing - which is every character TeraSharp has, because nothing in
    /// the tree grants polishing points. Serving the zeros is the whole fix.</para>
    /// </summary>
    public static Dictionary<string, object> BuildSkillPolishingListFields()
        => new()
        {
            ["optionEffects"] = new List<object>(),
            ["levelEffects"] = new List<object>(),
        };

    /// <inheritdoc cref="BuildSkillPolishingListFields"/>
    public static Dictionary<string, object> BuildSkillPolishingExpFields()
        => new()
        {
            ["currentPoint"] = 0,
            ["totalPoint"] = 0,
            ["level"] = 0,
            ["currentExp"] = 0L,
            ["prevLevelMaxExp"] = 0L,
            ["currentLevelMaxExp"] = 0L,
        };

    /// <summary>Handler for C_RQ_SKILL_POLISHING_LIST.</summary>
    public static bool OnRqSkillPolishingList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        s.SendByDef("S_RP_SKILL_POLISHING_LIST", BuildSkillPolishingListFields());
        return true;
    }

    /// <summary>Handler for C_RQ_SKILL_POLISHING_EXP_INFO.</summary>
    public static bool OnRqSkillPolishingExpInfo(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        s.SendByDef("S_RP_SKILL_POLISHING_EXP_INFO", BuildSkillPolishingExpFields());
        return true;
    }

    /// C_REQUEST_OTHERS_ACTIVATE_CARD_COMBINE_LIST_DATA_WITH_GAMEID (0xEFB9), body
    /// <c>[u64 gameId]</c>. We answer for whoever that gameId belongs to when a live session
    /// owns it, and for the asker otherwise - an unanswered request leaves the window spinning.
    /// </summary>
    public static bool OnRequestOthersActivateCardCombineList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var (name, serial, _) = OtherOwner(s, body);
        s.Send(BuildActivateCardCombineList(name, serial));
        return true;
    }

    /// <summary>C_REQUEST_OTHERS_CARD_DATA_WITH_GAMEID (0xD91B), body <c>[u64 gameId]</c>.</summary>
    public static bool OnRequestOthersCardData(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        // T86: the OTHER character's account, not ours - the collection follows the account the
        // character belongs to, so the page has to be drawn from that one.
        var (name, serial, characterId) = OtherOwner(s, body);
        SendCardPage(s, characterId, name, serial, log);
        return true;
    }

    /// <summary>
    /// Resolves an inter-server gameId to one of our character ids. Left null, the two
    /// WITH_GAMEID requests answer for the CALLER rather than going unanswered - a card page
    /// showing the wrong name is a cosmetic bug; an unanswered request is a window that never
    /// opens. <c>DbProxyHandlers</c> has the same hook and the same default.
    /// </summary>
    public static Func<ulong, int>? PlayerIdForGameId { get; set; }

    /// <summary>The card-page owner an 8-byte gameId body names, or the caller.</summary>
    private static (string Name, int Serial, int CharacterId) OtherOwner(
        GameSession s, ReadOnlyMemory<byte> body)
    {
        if (body.Length >= 8 && Program.Store is not null)
        {
            ulong gameId = BitConverter.ToUInt64(body.Span);
            int playerId = PlayerIdForGameId?.Invoke(gameId) ?? 0;
            if (playerId > 0)
            {
                var chr = Program.Store.GetCharacter(playerId);
                if (chr is not null) return (chr.Name, playerId, playerId);
            }
        }
        return (OwnerName(s), PdidSerialFor(s), (int)(s.SelectedCharacter?.Id ?? 0));
    }

    /// <summary>
    /// C_REQUEST_GUILD_PERK_LIST (0x63A0), body empty. A player with no guild gets the empty
    /// form rather than silence - the tab is opened from the guild window, which a guildless
    /// player can still open.
    /// </summary>
    public static bool OnRequestGuildPerkList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var store = Program.Store;
        var chr = s.SelectedCharacter;
        if (store is null || chr is null) { s.Send(BuildGuildPerkList(null, null)); return true; }

        int guildId = store.GetGuildIdOf((int)chr.Id);
        var guild = guildId > 0 ? store.GetGuild(guildId) : null;
        if (guild is null) { s.Send(BuildGuildPerkList(null, null)); return true; }

        string master = store.GetCharacter(guild.ChiefDbId)?.Name ?? string.Empty;
        s.Send(BuildGuildPerkList(guild.Name, master, store.GetGuildPerks(guildId)));
        s.Send(BuildGuildApplyCount(store.GetGuildApplies(guildId).Count));
        return true;
    }

    /// <summary>The crest window, from the character's own rows. Sent at enter-world and again
    /// whenever the points change.</summary>
    public static byte[] BuildCrestInfoFor(GameSession s)
    {
        var chr = s?.SelectedCharacter;
        if (chr is null || Program.Store is null) return BuildCrestInfo(null);
        var (point, exPoint) = Program.Store.GetCrestPoints((int)chr.Id);
        return BuildCrestInfo(Program.Store.GetCrests((int)chr.Id), point, exPoint);
    }

    // =========================================================================================
    // 15. The lobby account lists, play time and the sellable-item config             (T84)
    // =========================================================================================

    /// <summary>
    /// S_ACCOUNT_PACKAGE_LIST (0xE9A9) and S_ACCOUNT_BENEFIT_LIST (0x88E0) - the two cash-shop
    /// lists the lobby burst carries. <b>Arbiter-built:</b> cap_social4_client frames 14, 47 and
    /// 48 all arrive BEFORE S_LOGIN at frame 50, so no character is picked and World is not in
    /// the picture at all. LoginHandlers already sends both - with an EMPTY list, which is why
    /// nothing the account owns ever showed.
    ///
    /// <para>Both shipped defs are RIGHT. <c>S_ACCOUNT_PACKAGE_LIST.3</c> is a 16-byte element
    /// (<c>uint32 packageId</c>, <c>int64 expirationDate</c>) and frame 14 is 4 + 3 x 16 = 52;
    /// <c>S_ACCOUNT_BENEFIT_LIST.1</c> is a 29-byte element behind a one-byte header field and
    /// frame 47 is 5 + 29 = 34, frame 48 5 + 2 x 29 = 63. The three packages in frame 14 are
    /// 0x215 (533), 0x216 (534) and 0x3E8 (1000), expiring 1791961199, 2100409199 and
    /// 1786777199.</para>
    ///
    /// <para><b>The def name <c>timeRemaining</c> is wrong.</b> Frame 47 carries 1791961199 in
    /// that slot, the same absolute unix second S_ACCOUNT_PACKAGE_LIST gives package 533 as its
    /// <c>expirationDate</c> - it is an expiry, not a countdown.</para>
    /// </summary>
    public const ushort S_ACCOUNT_PACKAGE_LIST = 0xE9A9;   // 59817
    /// <inheritdoc cref="S_ACCOUNT_PACKAGE_LIST"/>
    public const ushort S_ACCOUNT_BENEFIT_LIST = 0x88E0;   // 35040

    /// <summary>S_ACCOUNT_PACKAGE_LIST from the account_benefits rows.</summary>
    public static Dictionary<string, object> BuildAccountPackageFields(
        IReadOnlyList<Persistence.CharacterStore.AccountBenefitRow>? benefits)
    {
        var rows = new List<object>();
        foreach (var b in benefits ?? Array.Empty<Persistence.CharacterStore.AccountBenefitRow>())
            rows.Add(new Dictionary<string, object>
            {
                ["packageId"] = (uint)b.PackageId,
                ["expirationDate"] = b.ExpiresAt,
            });
        return new Dictionary<string, object> { ["accountBenefits"] = rows };
    }

    /// <summary>
    /// S_ACCOUNT_BENEFIT_LIST from the same rows. The five slots the shipped def calls unk2..unk5
    /// are 0 in every captured element; <c>unk1</c> is the row s stored value.
    /// </summary>
    public static Dictionary<string, object> BuildAccountBenefitFields(
        IReadOnlyList<Persistence.CharacterStore.AccountBenefitRow>? benefits)
    {
        var rows = new List<object>();
        foreach (var b in benefits ?? Array.Empty<Persistence.CharacterStore.AccountBenefitRow>())
            rows.Add(new Dictionary<string, object>
            {
                ["packageId"] = (uint)b.PackageId,
                ["unk1"] = (uint)b.Value,
                ["unk2"] = 0u,
                ["timeRemaining"] = unchecked((int)b.ExpiresAt),
                ["unk3"] = 0u,
                ["unk4"] = 0u,
                ["unk5"] = (byte)0,
            });
        return new Dictionary<string, object>
        {
            ["unk"] = (byte)1,   // 01 in frames 47 and 48
            ["accountBenefits"] = rows,
        };
    }

    /// <summary>
    /// S_SEND_USER_PLAY_TIME (0xA7E0). <b>Arbiter-built.</b> The shipped
    /// <c>S_SEND_USER_PLAY_TIME.2.def</c> is RIGHT - <c>uint32 totalPlaytime</c> then
    /// <c>uint64 localServerTime</c>, twelve bytes, which is what cap_social4_client frames 78
    /// and 2957 are: 237 seconds at unix 1789608265, then 1446 seconds at 1789608607. Both
    /// numbers move with the session, so it is seconds played and the server s clock.
    /// LoginHandlers already sends the packet with an empty field set, i.e. two zeroes.
    /// </summary>
    public const ushort S_SEND_USER_PLAY_TIME = 0xA7E0;    // 42976

    /// <inheritdoc cref="S_SEND_USER_PLAY_TIME"/>
    public static Dictionary<string, object> BuildUserPlayTimeFields(int totalPlaySeconds, long serverTimeUnix)
        => new()
        {
            ["totalPlaytime"] = (uint)(totalPlaySeconds < 0 ? 0 : totalPlaySeconds),
            ["localServerTime"] = (ulong)(serverTimeUnix < 0 ? 0 : serverTimeUnix),
        };

    /// <summary>
    /// S_ENABLE_DISABLE_SELLABLE_ITEM_LIST (0x667C). <b>Arbiter-built, and we never sent it.</b>
    /// cap_social4_client frames 441 and 442 are 39 bytes: three <c>bool</c> flags, all 1, and
    /// three <c>array&lt;uint32&gt;</c> of which only the SECOND has anything in it - item ids
    /// 1164, 1167 and 1170. The shipped <c>S_ENABLE_DISABLE_SELLABLE_ITEM_LIST.2.def</c> is
    /// RIGHT: three array refs (12 bytes) then three bools is a 15-byte header, and 15 + 3 x 8
    /// is the 39 on the wire.
    ///
    /// <para>It is server CONFIG, not per-character state - both captured frames are identical
    /// and neither follows a request - so the captured values are the default here rather than a
    /// table. They are the three items the shop may sell.</para>
    /// </summary>
    public const ushort S_ENABLE_DISABLE_SELLABLE_ITEM_LIST = 0x667C;   // 26236

    /// <summary>The three item ids frames 441 and 442 carry in the second list.</summary>
    public static readonly int[] DefaultSellableItems = { 1164, 1167, 1170 };

    /// <inheritdoc cref="S_ENABLE_DISABLE_SELLABLE_ITEM_LIST"/>
    public static Dictionary<string, object> BuildSellableItemListFields(
        IReadOnlyList<int>? list1 = null, IReadOnlyList<int>? list2 = null, IReadOnlyList<int>? list3 = null)
    {
        // An array<uint32> element is the bare number, not a record: DefinitionWriter calls
        // WritePrimitive on the item itself when the def gave the array an element kind.
        static List<object> Ids(IReadOnlyList<int>? src)
        {
            var rows = new List<object>();
            foreach (int id in src ?? Array.Empty<int>()) rows.Add((uint)id);
            return rows;
        }
        return new Dictionary<string, object>
        {
            ["enabled1"] = true,
            ["enabled2"] = true,
            ["enabled3"] = true,
            ["items1"] = Ids(list1),
            ["items2"] = Ids(list2 ?? DefaultSellableItems),
            ["items3"] = Ids(list3),
        };
    }

    // =========================================================================================
    // 16. Telemetry, acks and the lobby's housekeeping packets                        (T87)
    //
    // Sixteen client packets that all have a real Handler_C_* in ArbiterServer.exe and none of
    // which World can answer. Only C_PONG appears in any capture (cap_social4_client frames 32
    // and 4390, answering S_PING frame 31 - both bare four-byte frames), so the layouts below
    // come from the decompiled handlers and their writers, not from bytes on the wire.
    //
    // The decompile's length guards are PACKET lengths; the dispatcher's minLen is a BODY
    // length, so every constant here is the guard minus four.
    // =========================================================================================

    public const ushort C_PONG = 0x8091;                               // 32913
    public const ushort S_PING = 0x9611;                               // 38417
    public const ushort C_CHECK_RTT = 0xF290;                          // 62096
    public const ushort S_CHECK_RTT = 0x52B0;                          // 21168
    public const ushort C_PLAY_TIME = 0x7CFE;                          // 31998
    public const ushort S_PLAY_TIME = 0xA2BE;                          // 41662
    public const ushort C_REQUEST_PLAYTIME = 0xBC06;                   // 48134
    public const ushort C_REQUEST_PERF = 0xD71E;                       // 55070
    public const ushort C_SEND_UI_LOG = 0xACE5;                        // 44261
    public const ushort C_GET_MY_IP = 0xAD47;                          // 44359
    public const ushort S_GET_MY_IP = 0xA9DF;                          // 43487
    public const ushort C_XIGNCODE_SECURITY_DATA = 0x9219;             // 37401
    public const ushort C_INVALID_BUILD_VERSION = 0x7F08;              // 32520
    public const ushort C_REQUEST_LATEST_UPDATE_NOTIFICATION = 0x779E; // 30622
    public const ushort S_ANNOUNCE_UPDATE_NOTIFICATION = 0x5C2E;       // 23598
    public const ushort C_CONFIRM_UPDATE_NOTIFICATION = 0xFE00;        // 65024
    public const ushort C_SECOND_PASSWORD_AUTH = 0x871F;               // 34591
    public const ushort C_SECOND_PASSWORD_REGISTER = 0x761E;           // 30238
    public const ushort C_REFRESH_API_ACCESS_TOKEN = 0xC2BA;           // 49850
    public const ushort S_REFRESH_API_ACCESS_TOKEN = 0x55E4;           // 22004
    public const ushort C_CANCEL_EXIT = 0xE488;                        // 58504

    // Body minimums, i.e. the decompiled packet guard minus the four header bytes. A guard of
    // 4 therefore admits an empty body - C_PONG and the bare S_CHECK_RTT prove that is real.
    public const int CheckRttBodySize = 0;                 // Handler guard: packet >= 4
    public const int RequestPlaytimeBodySize = 0;          // packet >= 4
    public const int GetMyIpBodySize = 0;                  // packet >= 4
    public const int InvalidBuildVersionBodySize = 0;      // packet >= 4
    public const int LatestUpdateNotificationBodySize = 0; // packet >= 4
    public const int RefreshApiAccessTokenBodySize = 0;    // packet >= 4
    public const int SecondPasswordBodySize = 2;           // packet >= 6
    public const int RequestPerfBodySize = 4;              // packet >= 8
    public const int SendUiLogBodySize = 4;                // packet >= 8
    public const int XignCodeSecurityDataBodySize = 4;     // packet >= 8
    public const int ConfirmUpdateNotificationBodySize = 4;// packet >= 8

    /// <summary>The peer address S_GET_MY_IP reports. Set from the registry once the session
    /// exposes its socket endpoint; until then every client is told the loopback.</summary>
    public static Func<GameSession, string>? RemoteIpLookup { get; set; }

    /// <summary>Seconds played this session, for S_PLAY_TIME. Unset means zero, which is what
    /// LoginHandlers already passes S_SEND_USER_PLAY_TIME.</summary>
    public static Func<GameSession, int>? PlayTimeLookup { get; set; }

    /// <summary>Loopback, used when <see cref="RemoteIpLookup"/> is unset.</summary>
    public const string DefaultMyIp = "127.0.0.1";

    /// <summary>
    /// S_CHECK_RTT (0x52B0) - opcode and nothing else. <c>Handler_C_CHECK_RTT</c> opens the
    /// writer, calls the u16 opcode write and sends: no payload write follows, so the whole
    /// frame is four bytes, the same shape as the S_PING / C_PONG pair in the capture.
    /// </summary>
    public static byte[] BuildCheckRttAck()
        => new byte[] { 0x04, 0x00, unchecked((byte)S_CHECK_RTT), (byte)(S_CHECK_RTT >> 8) };

    /// <summary>
    /// S_PLAY_TIME (0xA2BE): <c>[u16 len=8][u16 op][u32 seconds]</c>. The real handler reads the
    /// counter at User+0x1E4 and writes it with the u32 primitive - one scalar, no refs.
    /// </summary>
    public static byte[] BuildPlayTime(uint seconds)
    {
        var p = new byte[8];
        p[0] = 8; p[1] = 0;
        p[2] = unchecked((byte)S_PLAY_TIME); p[3] = (byte)(S_PLAY_TIME >> 8);
        BitConverter.GetBytes(seconds).CopyTo(p, 4);
        return p;
    }

    /// <summary>
    /// S_GET_MY_IP (0xA9DF): <c>[u16 len][u16 op][u16 offset=6][wchar ip][u16 0]</c>.
    ///
    /// <para>Same writer shape as S_SHOW_PARCEL_MESSAGE - reserve the u16 string slot, backpatch
    /// it to the running frame length (always 6, because nothing else precedes the data), then
    /// append the wide string with its terminator. The real handler formats the peer address
    /// with <c>%d.%d.%d.%d</c> into a 24-wchar buffer first.</para>
    /// </summary>
    public static byte[] BuildMyIp(string? ip)
    {
        var text = WString(string.IsNullOrEmpty(ip) ? DefaultMyIp : ip);
        var p = new byte[6 + text.Length];
        p[0] = (byte)p.Length; p[1] = (byte)(p.Length >> 8);
        p[2] = unchecked((byte)S_GET_MY_IP); p[3] = (byte)(S_GET_MY_IP >> 8);
        p[4] = 6; p[5] = 0;
        text.CopyTo(p, 6);
        return p;
    }

    /// <summary>
    /// S_REFRESH_API_ACCESS_TOKEN (0x55E4) - byte-identical in shape to S_GET_MY_IP:
    /// <c>[u16 len][u16 op][u16 offset=6][wchar token][u16 0]</c>.
    ///
    /// <para>The real handler mints the token from the account db id and the planet id and, when
    /// that fails, logs and answers NOTHING - it returns 0 with no packet written. We have no
    /// web API, so the token is whatever the caller passes.</para>
    /// </summary>
    public static byte[] BuildRefreshApiAccessToken(string? token)
    {
        var text = WString(token);
        var p = new byte[6 + text.Length];
        p[0] = (byte)p.Length; p[1] = (byte)(p.Length >> 8);
        p[2] = unchecked((byte)S_REFRESH_API_ACCESS_TOKEN); p[3] = (byte)(S_REFRESH_API_ACCESS_TOKEN >> 8);
        p[4] = 6; p[5] = 0;
        text.CopyTo(p, 6);
        return p;
    }

    /// <summary>
    /// S_ANNOUNCE_UPDATE_NOTIFICATION (0x5C2E):
    /// <c>[u16 len][u16 op][u16 offTitle][u16 offBody][u32 id][wchar title][wchar body]</c>.
    ///
    /// <para>Two string slots are reserved BEFORE the u32 - headers first, then scalars, the
    /// same ordering the def codec uses - so the first string always starts at 12 and the
    /// second at 12 plus the first string's bytes. The id is the notification's sequence
    /// number, which C_CONFIRM_UPDATE_NOTIFICATION echoes back.</para>
    /// </summary>
    public static byte[] BuildAnnounceUpdateNotification(uint id, string? title, string? message)
    {
        var a = WString(title);
        var b = WString(message);
        var p = new byte[12 + a.Length + b.Length];
        p[0] = (byte)p.Length; p[1] = (byte)(p.Length >> 8);
        p[2] = unchecked((byte)S_ANNOUNCE_UPDATE_NOTIFICATION); p[3] = (byte)(S_ANNOUNCE_UPDATE_NOTIFICATION >> 8);
        BitConverter.GetBytes((ushort)12).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(12 + a.Length)).CopyTo(p, 6);
        BitConverter.GetBytes(id).CopyTo(p, 8);
        a.CopyTo(p, 12);
        b.CopyTo(p, 12 + a.Length);
        return p;
    }

    // ---------------------------------------------------------------------------- handlers

    /// <summary>C_PONG (0x8091). The real handler is the empty one - it takes no arguments at
    /// all and returns 1. The client sends it unprompted after S_PING; nothing answers it.</summary>
    public static bool OnPong(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s; _ = body; log.LogTrace("C_PONG");
        return true;
    }

    /// <summary>C_CHECK_RTT (0xF290) -> S_CHECK_RTT. The client's round-trip probe.</summary>
    public static bool OnCheckRtt(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        s.Send(BuildCheckRttAck());
        return true;
    }

    /// <summary>C_PLAY_TIME (0x7CFE) -> S_PLAY_TIME. No length guard in the real handler at
    /// all - it only checks that the session has a user.</summary>
    public static bool OnPlayTime(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        int seconds = PlayTimeLookup?.Invoke(s) ?? 0;
        s.Send(BuildPlayTime(seconds < 0 ? 0u : (uint)seconds));
        return true;
    }

    /// <summary>C_REQUEST_PLAYTIME (0xBC06). <b>Store, no reply.</b> The real handler stamps the
    /// current time onto the account's play-time tracker (Account+0x30F0) through two setters
    /// and writes no packet. We have no such tracker, so this is an accept.</summary>
    public static bool OnRequestPlaytime(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s; _ = body; log.LogTrace("C_REQUEST_PLAYTIME");
        return true;
    }

    /// <summary>C_REQUEST_PERF (0xD71E). <b>Not a client reply at all.</b> The real handler
    /// broadcasts AS_REQUEST_PERF (0x1446) to every World and reports the answers to the admin
    /// tool channel; the requesting client gets nothing back. Accept and drop.</summary>
    public static bool OnRequestPerf(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s; _ = body; log.LogDebug("C_REQUEST_PERF - admin-tool path, not modelled");
        return true;
    }

    /// <summary>The three UI-log counters C_SEND_UI_LOG selects between. The decompile gives
    /// them no names - three adjacent setters on the user object.</summary>
    public const int UiLogMinKind = 1, UiLogMaxKind = 3;

    /// <summary>C_SEND_UI_LOG (0xACE5). <b>Store, no reply.</b> Body word 0 is a selector of
    /// 1, 2 or 3 picking one of three counters on the user; anything else makes the real
    /// handler return FALSE, which is the only rejection path in this whole group.</summary>
    public static bool OnSendUiLog(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s;
        if (body.Length < SendUiLogBodySize) return false;
        int kind = BitConverter.ToInt32(body.Span[..4]);
        if (kind < UiLogMinKind || kind > UiLogMaxKind)
        {
            log.LogDebug("C_SEND_UI_LOG kind {Kind} is outside 1..3 - rejected, as the real handler does", kind);
            return false;
        }
        log.LogTrace("C_SEND_UI_LOG kind {Kind}", kind);
        return true;
    }

    /// <summary>C_GET_MY_IP (0xAD47) -> S_GET_MY_IP.</summary>
    public static bool OnGetMyIp(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        s.Send(BuildMyIp(RemoteIpLookup?.Invoke(s)));
        return true;
    }

    /// <summary>C_XIGNCODE_SECURITY_DATA (0x9219). <b>A forward, not a reply.</b> The real
    /// handler wraps the client's blob as AX_PONG_SECURITY_DATA (0x426B) and sends it to the
    /// anti-cheat server's session, never to the client. With no such server the blob is
    /// accepted and dropped - and it must not be forwarded to World either.</summary>
    public static bool OnXignCodeSecurityData(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s; log.LogTrace("C_XIGNCODE_SECURITY_DATA ({Len} bytes) - no anti-cheat server", body.Length);
        return true;
    }

    /// <summary>C_INVALID_BUILD_VERSION (0x7F08). The client telling us its PDL build does not
    /// match. The real handler logs the mismatch and disconnects the session unconditionally -
    /// the length check only decides whether a second line is logged first.</summary>
    public static bool OnInvalidBuildVersion(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body;
        log.LogWarning("C_INVALID_BUILD_VERSION from {Id} - protocol version mismatch, closing", s.Id);
        s.Close();
        return true;
    }

    /// <summary>C_REQUEST_LATEST_UPDATE_NOTIFICATION (0x779E) -> S_ANNOUNCE_UPDATE_NOTIFICATION.
    /// The patch-note banner. We have no notification source, so the reply is the empty one:
    /// id 0 and two empty strings, twelve bytes of header plus two terminators.</summary>
    public static bool OnRequestLatestUpdateNotification(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        s.Send(BuildAnnounceUpdateNotification(0, null, null));
        return true;
    }

    /// <summary>C_CONFIRM_UPDATE_NOTIFICATION (0xFE00). <b>Store, no reply.</b> Body word 0 is
    /// the id the client is acknowledging; the real handler compares it with the one held on
    /// the account and only acts when they differ.</summary>
    public static bool OnConfirmUpdateNotification(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s;
        if (body.Length < ConfirmUpdateNotificationBodySize) return false;
        log.LogTrace("C_CONFIRM_UPDATE_NOTIFICATION id {Id}", BitConverter.ToInt32(body.Span[..4]));
        return true;
    }

    /// <summary>C_SECOND_PASSWORD_AUTH (0x871F) and C_SECOND_PASSWORD_REGISTER (0x761E).
    /// <b>Accept, no reply.</b> Both read a string ref at body offset 0 and hand it to the
    /// second-password manager, which answers asynchronously with S_SECOND_PASSWORD_AUTH_RESULT
    /// or S_SECOND_PASSWORD_REGISTER_RESULT. The client only sends either after the server has
    /// prompted with S_REQUEST_SECOND_PASSWORD_AUTH / _REGISTER, which we never send - so this
    /// path is unreachable in practice and is registered to keep it off the World link.</summary>
    public static bool OnSecondPassword(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = s;
        if (body.Length < SecondPasswordBodySize) return false;
        log.LogInformation("second-password packet received but the feature is not modelled - ignored");
        return true;
    }

    /// <summary>C_REFRESH_API_ACCESS_TOKEN (0xC2BA) -> S_REFRESH_API_ACCESS_TOKEN, with an
    /// empty token: we mint nothing, and an empty string is what the real writer would produce
    /// for an empty token rather than the no-packet failure path.</summary>
    public static bool OnRefreshApiAccessToken(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body; _ = log;
        s.Send(BuildRefreshApiAccessToken(null));
        return true;
    }

    /// <summary>C_CANCEL_EXIT (0xE488). The twin of C_CANCEL_RETURN_TO_LOBBY, which the registry
    /// already handles - but with <b>no reply</b>: there is no S_CANCEL_EXIT opcode, and the
    /// real handler only queues the cancel job. So this cancels the countdown and tells World,
    /// exactly as the lobby twin does, and sends the client nothing.</summary>
    public static bool OnCancelExit(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        _ = body;
        log.LogInformation("C_CANCEL_EXIT from {Id}", s.Id);
        s.PendingLobbyReturn?.Cancel();
        s.PendingLobbyReturn = null;
        if (s.InWorld) Program.World?.SendUserCancelRequestExit(s.PlayerId);
        return true;
    }

    // =========================================================================================
    // 15. The In-Game Operation Tool                                                    (T89)
    //
    // cap_final_gm_client2.log is the GM's client. Every layout below is from a frame there;
    // the packets that need World's data (S_ADMIN_GET_USERINFO_INVEN 4439 B frame 724,
    // _WAREHOUSE frame 747, _SKILL 665 B frame 1428) are NOT here - they are World's to answer.
    // =========================================================================================

    public const ushort C_ADMIN_REQUEST_CUSTOM_BOOKMARK = 0x9504;
    public const ushort C_ADMIN_REQUEST_DEFAULT_BOOKMARK = 0x6E39;
    public const ushort C_ADMIN_ADD_CUSTOM_BOOKMARK = 0x811A;
    public const ushort C_ADMIN_GMEVENT_STATUS = 0xBD39;
    public const ushort C_ADMIN_CHECK_USERNAME = 0x581A;
    public const ushort C_ADMIN_GET_USER_INFO_BY_DBID = 0xEFF2;
    public const ushort C_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE = 0x5A0E;
    public const ushort C_ADMIN_WARNING_MESSAGE = 0xC544;
    public const ushort C_ADMIN_GM_SKILL = 0x8949;

    public const ushort S_ADMIN_CUSTOM_BOOKMARK_LIST = 0xE4A4;
    public const ushort S_ADMIN_DEFAULT_BOOKMARK_LIST = 0xE430;
    public const ushort S_ADMIN_GMEVENT_STATUS = 0x6F21;
    public const ushort S_ADMIN_CHECK_USERNAME = 0x7495;
    public const ushort S_ADMIN_GET_USER_INFO_BY_DBID = 0xC3E0;
    public const ushort S_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE = 0xB12D;
    public const ushort S_ADMIN_WARNING_MESSAGE = 0x838B;
    public const ushort S_ADMIN_GM_SKILL = 0x64BE;
    public const ushort S_ADMIN_HOLD_CHARACTER = 0xA30E;

    /// <summary>S_ADMIN_HOLD_CHARACTER (0xA30E): one byte, 0 in all four captured frames
    /// (gm_client 443 / 1139, gm_client2 402 / 675). It is pushed at enter-world - a held
    /// character is one the tool has frozen.</summary>
    public static byte[] BuildAdminHoldCharacter(bool held = false)
        => new byte[] { 0x05, 0x00, unchecked((byte)S_ADMIN_HOLD_CHARACTER), (byte)(S_ADMIN_HOLD_CHARACTER >> 8),
                        (byte)(held ? 1 : 0) };

    /// <summary>
    /// S_ADMIN_CUSTOM_BOOKMARK_LIST (0xE4A4) and S_ADMIN_DEFAULT_BOOKMARK_LIST (0xE430) - one
    /// layout, two opcodes.
    /// <code>
    ///   +0x04 u16 count / +0x06 u16 firstOffset / +0x08 i32 page / +0x0C i32 totalPage
    ///   element 26 B + name:
    ///     +0 u16 here  +2 u16 next  +4 u16 nameOffset
    ///     +6 i32 index  +10 i32 zone  +14 f32 x  +18 f32 y  +22 f32 z   then the name
    /// </code>
    /// Frames 527 / 528 are the empty form (page 1 of 1, which is what an empty list carries
    /// here - not page 0) and 1168 is the one row.
    /// </summary>
    public const int AdminBookmarkFixedSize = 0x10;
    public const int AdminBookmarkElementSize = 26;

    public static byte[] BuildAdminBookmarkList(ushort opcode,
        IReadOnlyList<CharacterStore.GmBookmarkRow>? rows, int page = 1, int totalPage = 1)
    {
        rows ??= Array.Empty<CharacterStore.GmBookmarkRow>();
        var names = new byte[rows.Count][];
        int total = AdminBookmarkFixedSize;
        for (int i = 0; i < rows.Count; i++)
        {
            names[i] = WString(rows[i].Name);
            total += AdminBookmarkElementSize + names[i].Length;
        }

        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(opcode).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : AdminBookmarkFixedSize)).CopyTo(p, 6);
        BitConverter.GetBytes(page).CopyTo(p, 8);
        BitConverter.GetBytes(totalPage).CopyTo(p, 12);

        int at = AdminBookmarkFixedSize;
        for (int i = 0; i < rows.Count; i++)
        {
            int size = AdminBookmarkElementSize + names[i].Length;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? at + size : 0)).CopyTo(p, at + 2);
            BitConverter.GetBytes((ushort)(at + AdminBookmarkElementSize)).CopyTo(p, at + 4);
            BitConverter.GetBytes(rows[i].Index).CopyTo(p, at + 6);
            BitConverter.GetBytes(rows[i].Zone).CopyTo(p, at + 10);
            // Whole numbers: frame 1167 sends 16920.03 and 1168 returns 16920.
            BitConverter.GetBytes((float)(int)rows[i].X).CopyTo(p, at + 14);
            BitConverter.GetBytes((float)(int)rows[i].Y).CopyTo(p, at + 18);
            BitConverter.GetBytes((float)(int)rows[i].Z).CopyTo(p, at + 22);
            names[i].CopyTo(p, at + AdminBookmarkElementSize);
            at += size;
        }
        return p;
    }

    /// <summary>S_ADMIN_GMEVENT_STATUS (0x6F21), frame 529: twenty-one bytes, all zero - no
    /// GM event is running, which is the only state either capture shows.</summary>
    public static byte[] BuildAdminGmEventStatus()
    {
        var p = new byte[25];
        BitConverter.GetBytes((ushort)25).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GMEVENT_STATUS).CopyTo(p, 2);
        return p;
    }

    /// <summary>
    /// S_ADMIN_CHECK_USERNAME (0x7495), frame 713:
    /// <c>[u16 nameOffset=0x1C][i32 templateId][i32 userDbId][pdid 8][6 reserved bytes][wstr name]</c>.
    /// The tool asks it before anything else, to turn a typed name into a db id.
    /// </summary>
    public const int AdminCheckUsernameFixedSize = 0x1C;

    public static byte[] BuildAdminCheckUsername(string? name, int userDbId, int templateId, int pdidSerial)
    {
        var n = WString(name);
        var p = new byte[AdminCheckUsernameFixedSize + n.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_CHECK_USERNAME).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)AdminCheckUsernameFixedSize).CopyTo(p, 4);
        BitConverter.GetBytes(templateId).CopyTo(p, 6);
        BitConverter.GetBytes(userDbId).CopyTo(p, 10);
        WritePdid(p, 14, pdidSerial);
        n.CopyTo(p, AdminCheckUsernameFixedSize);
        return p;
    }

    /// <summary>
    /// S_ADMIN_GET_USER_INFO_BY_DBID (0xC3E0), frame 715:
    /// <c>[u16 nameOffset=21][u16 ipOffset=29][i32 userDbId][u8 online][i32 level]
    /// [i32 templateId][wstr name][wstr ip]</c>. The level is what climbs across the capture's
    /// nine instances (29, 33, 36, 102, 103, 104) while everything else stays put.
    /// </summary>
    public const int AdminUserInfoFixedSize = 21;

    public static byte[] BuildAdminGetUserInfoByDbId(int userDbId, bool online, int level,
                                                     int templateId, string? name, string? ip)
    {
        var n = WString(name);
        var a = WString(ip);
        var p = new byte[AdminUserInfoFixedSize + n.Length + a.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GET_USER_INFO_BY_DBID).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)AdminUserInfoFixedSize).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(AdminUserInfoFixedSize + n.Length)).CopyTo(p, 6);
        BitConverter.GetBytes(userDbId).CopyTo(p, 8);
        p[12] = (byte)(online ? 1 : 0);
        BitConverter.GetBytes(level).CopyTo(p, 13);
        BitConverter.GetBytes(templateId).CopyTo(p, 17);
        n.CopyTo(p, AdminUserInfoFixedSize);
        a.CopyTo(p, AdminUserInfoFixedSize + n.Length);
        return p;
    }

    /// <summary>
    /// S_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE (0xB12D), frame 1320: <c>[u16 count][u16 firstOff]</c>
    /// then 24-byte elements <c>[u16 here][u16 next][u16 nameOff][u16 ipOff][i32 userDbId]
    /// [f32 x][f32 y][f32 z]</c>, each followed by its name and its ip. Live coordinates, so
    /// these are NOT truncated the way a bookmark's are.
    /// </summary>
    public const int AdminDistanceElementSize = 24;

    public static byte[] BuildAdminUserInfoListByDistance(
        IReadOnlyList<(int UserDbId, float X, float Y, float Z, string Name, string Ip)>? rows)
    {
        rows ??= Array.Empty<(int, float, float, float, string, string)>();
        var names = new byte[rows.Count][];
        var ips = new byte[rows.Count][];
        int total = 8;
        for (int i = 0; i < rows.Count; i++)
        {
            names[i] = WString(rows[i].Name);
            ips[i] = WString(rows[i].Ip);
            total += AdminDistanceElementSize + names[i].Length + ips[i].Length;
        }

        var p = new byte[total];
        BitConverter.GetBytes((ushort)total).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : 8)).CopyTo(p, 6);

        int at = 8;
        for (int i = 0; i < rows.Count; i++)
        {
            int size = AdminDistanceElementSize + names[i].Length + ips[i].Length;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? at + size : 0)).CopyTo(p, at + 2);
            BitConverter.GetBytes((ushort)(at + AdminDistanceElementSize)).CopyTo(p, at + 4);
            BitConverter.GetBytes((ushort)(at + AdminDistanceElementSize + names[i].Length)).CopyTo(p, at + 6);
            BitConverter.GetBytes(rows[i].UserDbId).CopyTo(p, at + 8);
            BitConverter.GetBytes(rows[i].X).CopyTo(p, at + 12);
            BitConverter.GetBytes(rows[i].Y).CopyTo(p, at + 16);
            BitConverter.GetBytes(rows[i].Z).CopyTo(p, at + 20);
            names[i].CopyTo(p, at + AdminDistanceElementSize);
            ips[i].CopyTo(p, at + AdminDistanceElementSize + names[i].Length);
            at += size;
        }
        return p;
    }

    /// <summary>S_ADMIN_WARNING_MESSAGE (0x838B), cap_final_gm_client frame 1424:
    /// <c>[u16 messageOffset=6][wstr message]</c>. It goes to the WARNED player's client, not to
    /// the tool's - which is why it is in the other capture.</summary>
    public static byte[] BuildAdminWarningMessage(string? message)
    {
        var m = WString(message);
        var p = new byte[6 + m.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_WARNING_MESSAGE).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)6).CopyTo(p, 4);
        m.CopyTo(p, 6);
        return p;
    }

    /// <summary>S_ADMIN_GM_SKILL (0x64BE), frames 99 and 546: <c>[i32][u8]</c>. The push at
    /// enter-world carries 0 / 1 and the reply to the tool's request carries 0 / 0 for a
    /// requested value of 0 - two samples, so the reply mirrors "did you ask for something".</summary>
    public static byte[] BuildAdminGmSkill(int value = 0, bool on = false)
    {
        var p = new byte[9];
        BitConverter.GetBytes((ushort)9).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GM_SKILL).CopyTo(p, 2);
        BitConverter.GetBytes(value).CopyTo(p, 4);
        p[8] = (byte)(on ? 1 : 0);
        return p;
    }

    // =========================================================================================
    // 16. The tool's user-info replies, and the two leaderboard pushes                  (T91)
    // =========================================================================================

    public const ushort C_ADMIN_REQUEST_USERINFO = 0x9A56;
    public const ushort C_ADMIN_REQUEST_USERACTION = 0xA3DB;
    public const ushort S_ADMIN_GET_USERINFO_INVEN = 0xBBED;
    public const ushort S_ADMIN_GET_USERINFO_WAREHOUSE = 0x7B41;
    public const ushort S_ADMIN_GET_USERINFO_SKILL = 0xA3B4;
    public const ushort S_PVP_LEADER_BOARD_INFO = 0xB724;
    public const ushort S_PVE_LEADER_BOARD_INFO = 0x819F;

    /// <summary>
    /// The <c>kind</c> C_ADMIN_REQUEST_USERINFO asks for, an i32 at packet +22. The capture
    /// presses six of them: 1 answers with the inventory (frame 723 -&gt; 724), 6 with the
    /// warehouse (746 -&gt; 747 and four more) and 5 with the skill tab (1427 -&gt; 1428).
    /// Kinds 2, 3 and 14 (frames 758, 995, 1006) are answered by nothing the tap recorded.
    /// </summary>
    public const int UserInfoKindInven = 1;
    /// <summary>Kind 5 is the tab S_ADMIN_GET_USERINFO_SKILL answers - frame 1427 asks for 5
    /// and 1428 is the reply.</summary>
    public const int UserInfoKindSkill = 5;
    public const int UserInfoKindWarehouse = 6;

    /// <summary>
    /// S_ADMIN_GET_USERINFO_WAREHOUSE (0x7B41), frames 747 / 756 / 769 / 778 / 1007 - five
    /// instances, all of the same empty warehouse, and they differ in exactly one place. The
    /// body is 42 bytes:
    /// <code>
    ///   +0x04 .. +0x0F   twelve zero bytes
    ///   +0x10 i32  1     +0x14 i32 0   +0x18 i32 0   +0x1C i32 0x47
    ///   +0x20 .. +0x2B   twelve zero bytes
    ///   +0x2C u16  0x48 in 747 / 756 / 769 / 778, and 0 in 1007
    /// </code>
    /// Nothing in the capture moves an item in or out of that warehouse, so which of those is a
    /// slot count and which is a page is not pinned - they are parameters with the captured
    /// defaults, and an empty warehouse reproduces frame 747 byte for byte.
    /// </summary>
    public const int AdminWarehouseBodySize = 42;

    public static byte[] BuildAdminGetUserInfoWarehouse(
        int a = 1, int b = 0x47, int tail = 0x48)
    {
        var p = new byte[4 + AdminWarehouseBodySize];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GET_USERINFO_WAREHOUSE).CopyTo(p, 2);
        BitConverter.GetBytes(a).CopyTo(p, 0x10);
        BitConverter.GetBytes(b).CopyTo(p, 0x1C);
        BitConverter.GetBytes((ushort)tail).CopyTo(p, 0x2C);
        return p;
    }

    /// <summary>
    /// S_ADMIN_GET_USERINFO_SKILL (0xA3B4), frame 1428. TWO arrays, which is why the single-list
    /// reading never closed: 20 + 42*13 + 11*9 = 665 exactly.
    /// <code>
    ///   +0x04 u16 skillCount  +0x06 u16 skillOffset (always 20)
    ///   +0x08 u16 crestCount  +0x0A u16 crestOffset (= 20 + skillCount*13)
    ///   +0x0C i32 (10)        +0x10 i32 0
    ///   skill 13 B: [u16 here][u16 next][i32 skillId][i32 0][u8 active]
    ///   crest  9 B: [u16 here][u16 next][i32 crestId][u8 0]
    /// </code>
    /// <para>The trailing byte of a skill entry is not a constant: the first 25 of the 42 carry
    /// 1 and the last 17 carry 0, and that tail is 10002, 19500, 19501 and the whole 94001..94015
    /// block - TERA's passive range. So it reads as active-vs-passive. The middle i32 is 0 in all
    /// 42, and the crest entries' trailing byte is 0 in all 11.</para>
    /// <para>The second list is the LEARNED CRESTS: its eleven ids - 33000, 33008, 33012, 33018,
    /// 33020, 33029, 33031, 33033, 33034, 33038, 33041 - are the same eleven
    /// <c>S_CREST_INFO</c> carries at cap_social4_client frame 5108, which is the
    /// <c>crests</c> table T83 filled. The skill list is not modelled (T79: SDB_USER_FORGET_SKILL
    /// is acked without storing one), so it goes out empty.</para>
    /// </summary>
    public const int AdminSkillFixedSize = 20;
    public const int AdminSkillEntrySize = 13;
    public const int AdminCrestEntrySize = 9;

    public static byte[] BuildAdminGetUserInfoSkill(
        IReadOnlyList<(int Id, bool Active)>? skillIds, IReadOnlyList<int>? crestIds, int unk = 10)
    {
        skillIds ??= Array.Empty<(int, bool)>();
        crestIds ??= Array.Empty<int>();
        int skillsAt = AdminSkillFixedSize;
        int crestsAt = skillsAt + skillIds.Count * AdminSkillEntrySize;
        var p = new byte[crestsAt + crestIds.Count * AdminCrestEntrySize];

        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GET_USERINFO_SKILL).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)skillIds.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(skillIds.Count == 0 ? 0 : skillsAt)).CopyTo(p, 6);
        BitConverter.GetBytes((ushort)crestIds.Count).CopyTo(p, 8);
        BitConverter.GetBytes((ushort)(crestIds.Count == 0 ? 0 : crestsAt)).CopyTo(p, 10);
        BitConverter.GetBytes(unk).CopyTo(p, 12);

        for (int i = 0; i < skillIds.Count; i++)
        {
            int at = skillsAt + i * AdminSkillEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            // Each list terminates on its own: the last skill's next is 0, not the crest list.
            BitConverter.GetBytes((ushort)(i + 1 < skillIds.Count ? at + AdminSkillEntrySize : 0))
                .CopyTo(p, at + 2);
            BitConverter.GetBytes(skillIds[i].Id).CopyTo(p, at + 4);
            p[at + 12] = (byte)(skillIds[i].Active ? 1 : 0);
        }
        for (int i = 0; i < crestIds.Count; i++)
        {
            int at = crestsAt + i * AdminCrestEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < crestIds.Count ? at + AdminCrestEntrySize : 0)).CopyTo(p, at + 2);
            BitConverter.GetBytes(crestIds[i]).CopyTo(p, at + 4);
        }
        return p;
    }

    /// <summary>
    /// S_PVP_LEADER_BOARD_INFO (0xB724) and S_PVE_LEADER_BOARD_INFO (0x819F) - one layout, two
    /// opcodes, 52 bytes each, pushed at enter-world (cap_final_client frames 288 and 289).
    /// <code>
    ///   +0x04 u16 count (3)  +0x06 u16 firstOffset (0x1C)
    ///   +0x08 i32 season (1) +0x0C i64 seasonStart  +0x14 i64 seasonEnd
    ///   element 8 B: [u16 here][u16 next][i32 value]
    /// </code>
    /// The two captured stamps are unix 1660205710 and 1662624910 - 2022-08-11 and 2022-09-08,
    /// four years before the capture and exactly four weeks apart to the second, so this is a
    /// season long closed and the three values are its final ones (10 / 30 / 37 for PvP,
    /// 3126 / 3203 / 9126 for PvE). Both opcodes carry the same pair.
    /// </summary>
    public const int LeaderBoardFixedSize = 0x1C;
    public const int LeaderBoardEntrySize = 8;
    public const long LeaderBoardCapturedStart = 1660205710L;
    public const long LeaderBoardCapturedEnd = 1662624910L;
    public static readonly int[] LeaderBoardCapturedPvp = { 10, 30, 37 };
    public static readonly int[] LeaderBoardCapturedPve = { 3126, 3203, 9126 };

    public static byte[] BuildLeaderBoardInfo(ushort opcode, IReadOnlyList<int>? values,
        int season = 1, long start = LeaderBoardCapturedStart, long end = LeaderBoardCapturedEnd)
    {
        values ??= Array.Empty<int>();
        var p = new byte[LeaderBoardFixedSize + values.Count * LeaderBoardEntrySize];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(opcode).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)values.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(values.Count == 0 ? 0 : LeaderBoardFixedSize)).CopyTo(p, 6);
        BitConverter.GetBytes(season).CopyTo(p, 8);
        BitConverter.GetBytes(start).CopyTo(p, 12);
        BitConverter.GetBytes(end).CopyTo(p, 20);
        for (int i = 0; i < values.Count; i++)
        {
            int at = LeaderBoardFixedSize + i * LeaderBoardEntrySize;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < values.Count ? at + LeaderBoardEntrySize : 0)).CopyTo(p, at + 2);
            BitConverter.GetBytes(values[i]).CopyTo(p, at + 4);
        }
        return p;
    }

    /// <summary>The two frames as the capture pushes them, for the enter-world burst.</summary>
    public static byte[] BuildPvpLeaderBoardInfo()
        => BuildLeaderBoardInfo(S_PVP_LEADER_BOARD_INFO, LeaderBoardCapturedPvp);

    public static byte[] BuildPveLeaderBoardInfo()
        => BuildLeaderBoardInfo(S_PVE_LEADER_BOARD_INFO, LeaderBoardCapturedPve);

    // =========================================================================================
    // 17. S_ADMIN_GET_USERINFO_INVEN - the tool's inventory tab                         (T93)
    // =========================================================================================

    /// <summary>
    /// The head, from the PDL dumper at Arb_part_018.c:7506. Its guard is
    /// <c>if (0x26 &lt; param_2)</c>, so the minimum frame is 39 bytes, which is exactly the head:
    /// <code>
    ///   +0x04 u16 count            +0x06 u16 firstOffset
    ///   +0x08 i64 CreatureId       +0x10 i64 Money
    ///   +0x18 u8  ShowInven        +0x19 u8  IsFirstPacket   +0x1A u8 NeedNextPacket
    ///   +0x1B i32 MaxInvenSlotCount
    ///   +0x1F i64 TCatAmount
    /// </code>
    /// <para>Four of those are literals in the writer
    /// (<c>User::_Send_S_ADMIN_GET_USERINFO_INVEN</c>, Arb_part_031.c:5254): CreatureId is
    /// written as a bare <c>0</c>, ShowInven and IsFirstPacket as <c>1</c>, NeedNextPacket as
    /// <c>0</c>. The capture agrees - frame 724's CreatureId is zero even though the packet is
    /// entirely about one character, whose id is repeated in every element as OwnerDbId.</para>
    /// <para>NeedNextPacket is the paging flag. The writer sets it from nothing we can see and
    /// the capture never pages, so the whole inventory goes in one frame; a character with enough
    /// rows to overflow the 64 KB frame would need that flag and a continuation, which no capture
    /// pins.</para>
    /// </summary>
    public const int AdminInvenHeadSize = 39;

    /// <summary>
    /// The 40 slots the capture reports. The writer reads it from <c>param_2 + 0x3ba0</c> - a
    /// live field on World's character - so it is a parameter here with the captured default.
    /// </summary>
    public const int AdminInvenMaxSlotCount = 40;

    /// <summary>
    /// The item element. The dumper names 22 scalars and the writer advances by <c>0x68</c>
    /// after each one, which is where the fixed part ends:
    /// <code>
    ///   +0x00 u16 here             +0x02 u16 next
    ///   +0x08 u16 statCount        +0x0A u16 statOffset
    ///   +0x10 i32 TemplateId       +0x14 i64 Dbid          +0x1C i64 OwnerDbId
    ///   +0x24 i32 InvenType        +0x28 i32 TabIndex      +0x2C i32 InvenPos
    ///   +0x30 i32 Count            +0x34 i32 EnchantCount  +0x38 i32 Durability
    ///   +0x3C u8  IsBound          +0x3D u8  Masterpiece
    ///   +0x3E i32 CurrentUnidentifiedItemGrade
    ///   +0x42 i32 EnchantAdjustment            +0x46 i32 EnchantBoosterPoint
    ///   +0x4A i32 EnchantBoosterMaxGrade       +0x4E i32 CumulatedEnchantAmount
    ///   +0x52 u8  Awakened         +0x53 i32 UnbindCount   +0x57 i32 SelectedOptionIdx
    ///   +0x5B i32 OpenOptionIdx    +0x5F i64 EquipmentExp  +0x67 u8 Damaged
    /// </code>
    /// <para>+0x04 and +0x0C are a second pair of list slots the writer zeroes and never fills;
    /// they are zero in all eleven captured elements.</para>
    /// </summary>
    public const int AdminInvenItemFixedSize = 0x68;

    /// <summary>
    /// Behind each item hang TWO stat blocks, which the dumper does not name - it stops at
    /// <c>Damaged</c>, because a PDL dumper skips arrays nested inside an array element. The
    /// writer builds them (Arb_part_031.c:5447): an index <c>0, 1</c> at +0x08, three values
    /// read out of the item template sheet at +0x10/+0x14/+0x18, and its own list of 15 option
    /// slots.
    /// <code>
    ///   +0x00 u16 here   +0x02 u16 next   +0x04 u16 optionCount   +0x06 u16 optionOffset
    ///   +0x08 i32 index (0 then 1)        +0x0C i32 0
    ///   +0x10 f32        +0x14 f32        +0x18 f32
    /// </code>
    /// <para>The three floats are the same in both blocks in all eleven captured items and they
    /// track the template, not the row: 121 / 121 / 149.8 for the two weapons (17000, 17005),
    /// 5 / 5 / 5 and 1 / 1 / 1 for the worn armour, 0 / 0 / 0 for everything stackable. They
    /// come from the item datasheet the Arbiter loads and we do not have, so they are per-item
    /// parameters that default to zero rather than anything invented.</para>
    /// <para>The 15 option entries are <c>[u16 here][u16 next][i32 value]</c> and every one of
    /// the 330 in frame 724 carries 0.</para>
    /// </summary>
    public const int AdminInvenStatBlockSize = 0x1C;
    public const int AdminInvenStatBlocks = 2;
    public const int AdminInvenOptionSlots = 15;
    public const int AdminInvenOptionEntrySize = 8;

    /// <summary>104 + 2 * (28 + 15 * 8) = 400, the stride frame 724 walks.</summary>
    public const int AdminInvenItemSize = AdminInvenItemFixedSize
        + AdminInvenStatBlocks * (AdminInvenStatBlockSize
                                  + AdminInvenOptionSlots * AdminInvenOptionEntrySize);

    /// <summary>
    /// One row of the tool's inventory tab, named as the dumper names it. Everything past
    /// <c>Count</c> is World's item object rather than anything the Arbiter's <c>items</c> table
    /// holds, so it defaults to what an unenchanted, unbound, unmodified item reads as.
    /// </summary>
    public sealed class AdminInvenItem
    {
        public int TemplateId { get; init; }
        public long ItemDbId { get; init; }
        public long OwnerDbId { get; init; }
        public int InvenType { get; init; }
        public int TabIndex { get; init; }
        public int InvenPos { get; init; }
        public int Count { get; init; }
        public int EnchantCount { get; init; }
        public int Durability { get; init; }
        public bool IsBound { get; init; }
        public bool Masterpiece { get; init; }
        public int CurrentUnidentifiedItemGrade { get; init; }
        public int EnchantAdjustment { get; init; }
        public int EnchantBoosterPoint { get; init; }
        public int EnchantBoosterMaxGrade { get; init; }

        /// <summary>
        /// -1 in nine of the eleven captured rows and a small positive number in the two
        /// enchantable weapons (2 and 3), so -1 is "this item does not accumulate enchantment"
        /// rather than zero.
        /// </summary>
        public int CumulatedEnchantAmount { get; init; } = -1;

        public bool Awakened { get; init; }
        public int UnbindCount { get; init; }
        public int SelectedOptionIdx { get; init; }
        public int OpenOptionIdx { get; init; }
        public long EquipmentExp { get; init; }
        public bool Damaged { get; init; }

        /// <summary>The three template values both stat blocks carry. See AdminInvenStatBlockSize.</summary>
        public float StatA { get; init; }
        public float StatB { get; init; }
        public float StatC { get; init; }
    }

    /// <summary>
    /// S_ADMIN_GET_USERINFO_INVEN (0xBBED), cap_final_gm_client2 frame 724 - 39 + 11 * 400 =
    /// 4439 bytes. The reply to <c>C_ADMIN_REQUEST_USERINFO</c> kind 1.
    /// </summary>
    public static byte[] BuildAdminGetUserInfoInven(
        IReadOnlyList<AdminInvenItem>? items, long money = 0, long tcatAmount = 0,
        int maxInvenSlotCount = AdminInvenMaxSlotCount,
        bool showInven = true, bool isFirstPacket = true, bool needNextPacket = false)
    {
        items ??= Array.Empty<AdminInvenItem>();
        var p = new byte[AdminInvenHeadSize + items.Count * AdminInvenItemSize];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADMIN_GET_USERINFO_INVEN).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)items.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(items.Count == 0 ? 0 : AdminInvenHeadSize)).CopyTo(p, 6);
        // +0x08 CreatureId stays zero: the writer passes a literal 0.
        BitConverter.GetBytes(money).CopyTo(p, 0x10);
        p[0x18] = (byte)(showInven ? 1 : 0);
        p[0x19] = (byte)(isFirstPacket ? 1 : 0);
        p[0x1A] = (byte)(needNextPacket ? 1 : 0);
        BitConverter.GetBytes(maxInvenSlotCount).CopyTo(p, 0x1B);
        BitConverter.GetBytes(tcatAmount).CopyTo(p, 0x1F);

        int blockStride = AdminInvenStatBlockSize
                          + AdminInvenOptionSlots * AdminInvenOptionEntrySize;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            int at = AdminInvenHeadSize + i * AdminInvenItemSize;
            int statsAt = at + AdminInvenItemFixedSize;

            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < items.Count ? at + AdminInvenItemSize : 0))
                .CopyTo(p, at + 2);
            BitConverter.GetBytes((ushort)AdminInvenStatBlocks).CopyTo(p, at + 8);
            BitConverter.GetBytes((ushort)statsAt).CopyTo(p, at + 10);

            BitConverter.GetBytes(it.TemplateId).CopyTo(p, at + 0x10);
            BitConverter.GetBytes(it.ItemDbId).CopyTo(p, at + 0x14);
            BitConverter.GetBytes(it.OwnerDbId).CopyTo(p, at + 0x1C);
            BitConverter.GetBytes(it.InvenType).CopyTo(p, at + 0x24);
            BitConverter.GetBytes(it.TabIndex).CopyTo(p, at + 0x28);
            BitConverter.GetBytes(it.InvenPos).CopyTo(p, at + 0x2C);
            BitConverter.GetBytes(it.Count).CopyTo(p, at + 0x30);
            BitConverter.GetBytes(it.EnchantCount).CopyTo(p, at + 0x34);
            BitConverter.GetBytes(it.Durability).CopyTo(p, at + 0x38);
            p[at + 0x3C] = (byte)(it.IsBound ? 1 : 0);
            p[at + 0x3D] = (byte)(it.Masterpiece ? 1 : 0);
            BitConverter.GetBytes(it.CurrentUnidentifiedItemGrade).CopyTo(p, at + 0x3E);
            BitConverter.GetBytes(it.EnchantAdjustment).CopyTo(p, at + 0x42);
            BitConverter.GetBytes(it.EnchantBoosterPoint).CopyTo(p, at + 0x46);
            BitConverter.GetBytes(it.EnchantBoosterMaxGrade).CopyTo(p, at + 0x4A);
            BitConverter.GetBytes(it.CumulatedEnchantAmount).CopyTo(p, at + 0x4E);
            p[at + 0x52] = (byte)(it.Awakened ? 1 : 0);
            BitConverter.GetBytes(it.UnbindCount).CopyTo(p, at + 0x53);
            BitConverter.GetBytes(it.SelectedOptionIdx).CopyTo(p, at + 0x57);
            BitConverter.GetBytes(it.OpenOptionIdx).CopyTo(p, at + 0x5B);
            BitConverter.GetBytes(it.EquipmentExp).CopyTo(p, at + 0x5F);
            p[at + 0x67] = (byte)(it.Damaged ? 1 : 0);

            for (int k = 0; k < AdminInvenStatBlocks; k++)
            {
                int blockAt = statsAt + k * blockStride;
                int optAt = blockAt + AdminInvenStatBlockSize;

                BitConverter.GetBytes((ushort)blockAt).CopyTo(p, blockAt);
                // Each list terminates on its own: the last block's next is 0, not the next item.
                BitConverter.GetBytes((ushort)(k + 1 < AdminInvenStatBlocks ? blockAt + blockStride : 0))
                    .CopyTo(p, blockAt + 2);
                BitConverter.GetBytes((ushort)AdminInvenOptionSlots).CopyTo(p, blockAt + 4);
                BitConverter.GetBytes((ushort)optAt).CopyTo(p, blockAt + 6);
                BitConverter.GetBytes(k).CopyTo(p, blockAt + 8);
                BitConverter.GetBytes(it.StatA).CopyTo(p, blockAt + 0x10);
                BitConverter.GetBytes(it.StatB).CopyTo(p, blockAt + 0x14);
                BitConverter.GetBytes(it.StatC).CopyTo(p, blockAt + 0x18);

                for (int o = 0; o < AdminInvenOptionSlots; o++)
                {
                    int e = optAt + o * AdminInvenOptionEntrySize;
                    BitConverter.GetBytes((ushort)e).CopyTo(p, e);
                    BitConverter.GetBytes((ushort)(o + 1 < AdminInvenOptionSlots
                        ? e + AdminInvenOptionEntrySize : 0)).CopyTo(p, e + 2);
                }
            }
        }
        return p;
    }

    // =========================================================================================
    // 18. The guild list, the wanted board, the level ranking and the flag             (T95)
    // =========================================================================================
    //
    // Every layout here comes from the Arbiter's own PDL dumpers (Arb_part_013/014/015/018/020/
    // 022/024.c) and five of the seven replies are pinned to a captured frame. The shipped .def
    // files were checked and NOT used: S_GUILD_LEVEL_RANKING_LIST.def is missing IsOccupation,
    // S_REPLY_INVITE_GUILD_LIST.def and S_REPLY_GUILD_WANTED_WRITING_LIST.def have no array at
    // all, S_BROCAST_GUILD_FLAG.def calls the whole body one int32, and their opcode comments
    // are from another protocol version. The decompile wins, and where a capture exists it wins
    // over the decompile.

    public const ushort C_REQUEST_GUILD_LIST_PAGE = 0xD1D1;
    public const ushort C_REQUEST_GUILD_LIST_SORT = 0x8883;
    public const ushort S_REPLY_GUILD_LIST = 0x5F75;
    public const ushort C_REQUEST_GUILD_WANTED_WRITING_LIST = 0x595A;
    public const ushort C_REQUEST_GUILD_WANTED_WRITING_LIST_PAGE = 0x7249;
    public const ushort C_REQUEST_SET_GUILD_WANTED_WRITING = 0x8F7B;
    public const ushort S_REPLY_GUILD_WANTED_WRITING_LIST = 0x56CB;
    public const ushort S_REPLY_SET_GUILD_WANTED_WRITING = 0xC8FA;
    public const ushort C_REQUEST_INVITE_GUILD_LIST = 0x6632;
    public const ushort C_REQUEST_INVITE_GUILD_LIST_PAGE = 0x89AB;
    public const ushort S_REPLY_INVITE_GUILD_LIST = 0xF216;
    public const ushort C_REQUEST_GUILD_LEVEL_RANKING = 0xB919;
    public const ushort S_GUILD_LEVEL_RANKING_LIST = 0xDFA0;
    public const ushort C_GET_GUILD_WARE_HISTORY = 0x8324;
    public const ushort S_GUILD_WARE_HISTORY = 0x99B0;
    public const ushort C_RECOMMEND_GUILD = 0xD114;
    public const ushort C_RECOMMEND_USER_GUILD = 0x6DD9;
    public const ushort C_UPDATE_GUILD_FLAG = 0xE39D;
    public const ushort S_UPDATE_GUILD_FLAG = 0x7BA3;
    public const ushort C_REQUEST_GUILD_FLAG_IMAGE_DATA = 0x8947;
    public const ushort S_REQUEST_GUILD_FLAG_IMAGE_DATA = 0xA795;
    public const ushort S_BROCAST_GUILD_FLAG = 0xDA23;

    /// <summary>
    /// How many rows a page of any of these boards holds. NOTHING pins this: the only populated
    /// list in any capture is cap_social4_client frame 1640, two guilds on page 1 of 1, which
    /// every page size above one satisfies. The client renders whatever arrives and takes the
    /// page count from the head, so this only decides where the "next page" button appears.
    /// </summary>
    public const int GuildBoardPageSize = 20;

    /// <summary>
    /// One page: the index of the first row, how many rows fit, and the page count the head
    /// carries. An empty board is page 1 of ZERO pages - cap_social3_client2 frames 2000, 2011,
    /// 2035 and 2040 all carry CurPageNum 1 with TotalPageCount 0, so the page number is
    /// clamped up to 1 and the count is not.
    /// </summary>
    public static (int First, int Count, int TotalPages) GuildBoardPage(
        int total, int page, int size = GuildBoardPageSize)
    {
        if (size <= 0) size = 1;
        if (total < 0) total = 0;
        int totalPages = (total + size - 1) / size;
        if (page < 1) page = 1;
        if (totalPages > 0 && page > totalPages) page = totalPages;
        long first = (long)(page - 1) * size;
        if (first > total) first = total;                       // unsigned-safe: page is packet data
        int count = total - (int)first;
        if (count > size) count = size;
        if (count < 0) count = 0;
        return ((int)first, count, totalPages);
    }

    /// <summary>The page number a paging request carries, clamped to something sane. The field
    /// is an i32 straight off the wire, so a negative or absurd page is a page-1 request.</summary>
    public static int ReadPageNumber(ReadOnlySpan<byte> body, int at = 0)
    {
        if (at + 4 > body.Length) return 1;
        int page = BitConverter.ToInt32(body[at..]);
        return page < 1 || page > 1_000_000 ? 1 : page;
    }

    // ------------------------------- S_REPLY_GUILD_LIST -------------------------------

    /// <summary>
    /// One row of the guild-search window. Dumper order is GuildDbId, Name, MemberCount,
    /// JoinType, GuildLogoId, PromotionStr, GuildPreference, JoinMinLevel, JoinMaxLevel; the
    /// three strings take their offset slots first, then the six i32 follow, which is the 0x22
    /// the element guard demands.
    /// <para><c>GuildLogoId</c> is a STRING here, not the <c>logo_id</c> int of the guilds row -
    /// the same image-id string S_UPDATE_GUILD_FLAG and S_REQUEST_GUILD_FLAG_IMAGE_DATA carry.
    /// Both captured rows have it empty, so no capture pins a non-empty one.</para>
    /// </summary>
    public sealed record GuildListEntry(int GuildDbId, string Name, int MemberCount, int JoinType,
                                        string GuildLogoId, string PromotionStr,
                                        int GuildPreference, int JoinMinLevel, int JoinMaxLevel);

    public const int GuildListHeadSize = 20;
    public const int GuildListEntryFixedSize = 0x22;

    /// <summary>
    /// S_REPLY_GUILD_LIST (0x5F75). cap_social4_client frame 1640 - two guilds, page 1 of 1 -
    /// and cap_social3_client2 frame 2000, the empty form.
    /// </summary>
    public static byte[] BuildReplyGuildList(IReadOnlyList<GuildListEntry>? rows,
        int curPage = 1, int totalPages = 0, int totalGuilds = 0)
    {
        rows ??= Array.Empty<GuildListEntry>();
        int size = GuildListHeadSize;
        foreach (var r in rows)
            size += GuildListEntryFixedSize + WStringSize(r.Name) + WStringSize(r.GuildLogoId)
                    + WStringSize(r.PromotionStr);

        var p = new byte[size];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_REPLY_GUILD_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : GuildListHeadSize)).CopyTo(p, 6);
        BitConverter.GetBytes(curPage).CopyTo(p, 8);
        BitConverter.GetBytes(totalPages).CopyTo(p, 0x0C);
        BitConverter.GetBytes(totalGuilds).CopyTo(p, 0x10);

        int at = GuildListHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int tail = at + GuildListEntryFixedSize;
            int next = tail + WStringSize(r.Name) + WStringSize(r.GuildLogoId)
                       + WStringSize(r.PromotionStr);
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? next : 0)).CopyTo(p, at + 2);
            tail = WriteSlotAndString(p, at + 4, tail, r.Name);
            tail = WriteSlotAndString(p, at + 6, tail, r.GuildLogoId);
            tail = WriteSlotAndString(p, at + 8, tail, r.PromotionStr);
            BitConverter.GetBytes(r.GuildDbId).CopyTo(p, at + 0x0A);
            BitConverter.GetBytes(r.MemberCount).CopyTo(p, at + 0x0E);
            BitConverter.GetBytes(r.JoinType).CopyTo(p, at + 0x12);
            BitConverter.GetBytes(r.GuildPreference).CopyTo(p, at + 0x16);
            BitConverter.GetBytes(r.JoinMinLevel).CopyTo(p, at + 0x1A);
            BitConverter.GetBytes(r.JoinMaxLevel).CopyTo(p, at + 0x1E);
            at = next;
        }
        return p;
    }

    /// <summary>Bytes a NUL-terminated UTF-16LE string takes on the wire.</summary>
    public static int WStringSize(string? s) => ((s?.Length ?? 0) + 1) * 2;

    /// <summary>
    /// Write the u16 offset slot at <paramref name="slotAt"/>, put the string at
    /// <paramref name="tailAt"/>, and return where the next string goes. An EMPTY string still
    /// gets a real offset and its two terminator bytes - the writer patches the slot to the
    /// current end whether or not there is text, which is what T51's S_GET_USER_GUILD_LOGO note
    /// records and what both captured guild rows do with their two empty strings.
    /// </summary>
    public static int WriteSlotAndString(byte[] p, int slotAt, int tailAt, string? value)
    {
        BitConverter.GetBytes((ushort)tailAt).CopyTo(p, slotAt);
        var bytes = WString(value);
        bytes.CopyTo(p, tailAt);
        return tailAt + bytes.Length;
    }

    // ---------------------------- S_REPLY_INVITE_GUILD_LIST ----------------------------

    /// <summary>
    /// The "ask a guild to invite me" window. Same row as the guild list minus JoinType and
    /// PromotionStr: two string slots and five i32, which is the 0x1C the element guard demands.
    /// </summary>
    public sealed record InviteGuildEntry(int GuildDbId, string Name, int MemberCount,
                                          string GuildLogoId, int GuildPreference,
                                          int JoinMinLevel, int JoinMaxLevel);

    public const int InviteGuildHeadSize = 16;
    public const int InviteGuildEntryFixedSize = 0x1C;

    /// <summary>S_REPLY_INVITE_GUILD_LIST (0xF216), cap_social3_client2 frames 2040 / 2123 -
    /// both empty.</summary>
    public static byte[] BuildReplyInviteGuildList(IReadOnlyList<InviteGuildEntry>? rows,
        int curPage = 1, int totalPages = 0)
    {
        rows ??= Array.Empty<InviteGuildEntry>();
        int size = InviteGuildHeadSize;
        foreach (var r in rows)
            size += InviteGuildEntryFixedSize + WStringSize(r.Name) + WStringSize(r.GuildLogoId);

        var p = new byte[size];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_REPLY_INVITE_GUILD_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : InviteGuildHeadSize)).CopyTo(p, 6);
        BitConverter.GetBytes(curPage).CopyTo(p, 8);
        BitConverter.GetBytes(totalPages).CopyTo(p, 0x0C);

        int at = InviteGuildHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int tail = at + InviteGuildEntryFixedSize;
            int next = tail + WStringSize(r.Name) + WStringSize(r.GuildLogoId);
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? next : 0)).CopyTo(p, at + 2);
            tail = WriteSlotAndString(p, at + 4, tail, r.Name);
            tail = WriteSlotAndString(p, at + 6, tail, r.GuildLogoId);
            BitConverter.GetBytes(r.GuildDbId).CopyTo(p, at + 8);
            BitConverter.GetBytes(r.MemberCount).CopyTo(p, at + 0x0C);
            BitConverter.GetBytes(r.GuildPreference).CopyTo(p, at + 0x10);
            BitConverter.GetBytes(r.JoinMinLevel).CopyTo(p, at + 0x14);
            BitConverter.GetBytes(r.JoinMaxLevel).CopyTo(p, at + 0x18);
            at = next;
        }
        return p;
    }

    // --------------------------- S_GUILD_LEVEL_RANKING_LIST ---------------------------

    /// <summary>
    /// One row of the guild ranking window: three strings then six scalars and a bool, 0x27.
    /// <c>IsOccupation</c> is the one the shipped .def drops - it is a real byte at +0x26 and
    /// the element guard counts it.
    /// </summary>
    public sealed record GuildRankingEntry(int Ranking, int PreRanking, string GuildLogoId,
                                           string GuildName, string GuildChiefName,
                                           int GuildPreference, long GuildCreateDate,
                                           int MemberCount, int GuildLevel, bool IsOccupation);

    public const int GuildRankingHeadSize = 16;
    public const int GuildRankingEntryFixedSize = 0x27;

    /// <summary>S_GUILD_LEVEL_RANKING_LIST (0xDFA0). cap_social4_client frames 1638 / 1660 and
    /// cap_social3_client2 2011 / 2021 - four instances, all the empty form, all page 1 of 0
    /// even though two guilds existed by then. The real Arbiter builds this ranking on a
    /// schedule, so an unranked server sends an empty board rather than the live list.</summary>
    public static byte[] BuildGuildLevelRankingList(IReadOnlyList<GuildRankingEntry>? rows,
        int page = 1, int totalPages = 0)
    {
        rows ??= Array.Empty<GuildRankingEntry>();
        int size = GuildRankingHeadSize;
        foreach (var r in rows)
            size += GuildRankingEntryFixedSize + WStringSize(r.GuildLogoId)
                    + WStringSize(r.GuildName) + WStringSize(r.GuildChiefName);

        var p = new byte[size];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_GUILD_LEVEL_RANKING_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : GuildRankingHeadSize)).CopyTo(p, 6);
        BitConverter.GetBytes(page).CopyTo(p, 8);
        BitConverter.GetBytes(totalPages).CopyTo(p, 0x0C);

        int at = GuildRankingHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int tail = at + GuildRankingEntryFixedSize;
            int next = tail + WStringSize(r.GuildLogoId) + WStringSize(r.GuildName)
                       + WStringSize(r.GuildChiefName);
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? next : 0)).CopyTo(p, at + 2);
            tail = WriteSlotAndString(p, at + 4, tail, r.GuildLogoId);
            tail = WriteSlotAndString(p, at + 6, tail, r.GuildName);
            tail = WriteSlotAndString(p, at + 8, tail, r.GuildChiefName);
            BitConverter.GetBytes(r.Ranking).CopyTo(p, at + 0x0A);
            BitConverter.GetBytes(r.PreRanking).CopyTo(p, at + 0x0E);
            BitConverter.GetBytes(r.GuildPreference).CopyTo(p, at + 0x12);
            BitConverter.GetBytes(r.GuildCreateDate).CopyTo(p, at + 0x16);
            BitConverter.GetBytes(r.MemberCount).CopyTo(p, at + 0x1E);
            BitConverter.GetBytes(r.GuildLevel).CopyTo(p, at + 0x22);
            p[at + 0x26] = (byte)(r.IsOccupation ? 1 : 0);
            at = next;
        }
        return p;
    }

    // ----------------------- S_REPLY_GUILD_WANTED_WRITING_LIST -----------------------

    public const int GuildWantedHeadSize = 26;
    public const int GuildWantedEntryFixedSize = 0x25;

    /// <summary>
    /// S_REPLY_GUILD_WANTED_WRITING_LIST (0x56CB). Head: CanBeWriting u8, InviteAuthority u8,
    /// RemainTime i64, CurPageNum, TotalPageCount. Element: UserName and PromotionStr slots,
    /// then UserDbId, Level, ClassType, GuildPreference, GuildSize, WritingDate i64,
    /// CanBeInvite u8.
    /// <para>cap_social3_client2 frame 2035 is the empty board a character sees before posting -
    /// CanBeWriting 1, RemainTime 0 - and frame 2140 is the same board right after frame 2137
    /// posted: one row, CanBeWriting 0 and RemainTime 86400, one day to the second.</para>
    /// </summary>
    public static byte[] BuildReplyGuildWantedWritingList(
        IReadOnlyList<CharacterStore.GuildWantedRow>? rows, bool canBeWriting = true,
        bool inviteAuthority = false, long remainTime = 0, int curPage = 1, int totalPages = 0)
    {
        rows ??= Array.Empty<CharacterStore.GuildWantedRow>();
        int size = GuildWantedHeadSize;
        foreach (var r in rows)
            size += GuildWantedEntryFixedSize + WStringSize(r.UserName) + WStringSize(r.PromotionStr);

        var p = new byte[size];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_REPLY_GUILD_WANTED_WRITING_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : GuildWantedHeadSize)).CopyTo(p, 6);
        p[8] = (byte)(canBeWriting ? 1 : 0);
        p[9] = (byte)(inviteAuthority ? 1 : 0);
        BitConverter.GetBytes(remainTime).CopyTo(p, 0x0A);
        BitConverter.GetBytes(curPage).CopyTo(p, 0x12);
        BitConverter.GetBytes(totalPages).CopyTo(p, 0x16);

        int at = GuildWantedHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int tail = at + GuildWantedEntryFixedSize;
            int next = tail + WStringSize(r.UserName) + WStringSize(r.PromotionStr);
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? next : 0)).CopyTo(p, at + 2);
            tail = WriteSlotAndString(p, at + 4, tail, r.UserName);
            tail = WriteSlotAndString(p, at + 6, tail, r.PromotionStr);
            BitConverter.GetBytes(r.UserDbId).CopyTo(p, at + 8);
            BitConverter.GetBytes(r.Level).CopyTo(p, at + 0x0C);
            BitConverter.GetBytes(r.ClassType).CopyTo(p, at + 0x10);
            BitConverter.GetBytes(r.GuildPreference).CopyTo(p, at + 0x14);
            BitConverter.GetBytes(r.GuildSize).CopyTo(p, at + 0x18);
            BitConverter.GetBytes(r.WritingDate).CopyTo(p, at + 0x1C);
            // CanBeInvite is the reader's authority over THIS row, not the poster's state: it is
            // 0 in frame 2140, whose reader is the poster himself and in no guild.
            p[at + 0x24] = 0;
            at = next;
        }
        return p;
    }

    /// <summary>S_REPLY_SET_GUILD_WANTED_WRITING (0xC8FA), cap_social3_client2 frame 2138: one
    /// byte, and the capture's is 1.</summary>
    public static byte[] BuildReplySetGuildWantedWriting(bool success)
    {
        var p = new byte[5];
        BitConverter.GetBytes((ushort)5).CopyTo(p, 0);
        BitConverter.GetBytes(S_REPLY_SET_GUILD_WANTED_WRITING).CopyTo(p, 2);
        p[4] = (byte)(success ? 1 : 0);
        return p;
    }

    // ------------------------------- S_GUILD_WARE_HISTORY -------------------------------

    /// <summary>
    /// One line of the guild bank log. Dumper: LogId i64, LogTime i64, ActionType, ActorName,
    /// ItemTemplateId, ItemDbId i64, ItemAmountDelta, MoneyDelta i64 - one string slot and
    /// 44 bytes of scalars, which is the 0x32 the element guard demands. (The shipped .def has
    /// two extra i32 and MoneyDelta as an i32; it does not add up to 0x32 and is not used.)
    /// </summary>
    public sealed record GuildWareHistoryEntry(long LogId, long LogTime, int ActionType,
                                               string ActorName, int ItemTemplateId,
                                               long ItemDbId, int ItemAmountDelta, long MoneyDelta);

    public const int GuildWareHistoryHeadSize = 16;
    public const int GuildWareHistoryEntryFixedSize = 0x32;

    /// <summary>
    /// S_GUILD_WARE_HISTORY (0x99B0). No capture holds one: nobody opened the guild bank log
    /// against the tap. We keep no bank log either - the guild warehouse moves items through the
    /// same item atoms everything else does and nothing records who moved what - so this answers
    /// the window with an empty page rather than leaving it spinning.
    /// </summary>
    public static byte[] BuildGuildWareHistory(IReadOnlyList<GuildWareHistoryEntry>? rows,
        int viewPage = 1, int lastPage = 0)
    {
        rows ??= Array.Empty<GuildWareHistoryEntry>();
        int size = GuildWareHistoryHeadSize;
        foreach (var r in rows) size += GuildWareHistoryEntryFixedSize + WStringSize(r.ActorName);

        var p = new byte[size];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_GUILD_WARE_HISTORY).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : GuildWareHistoryHeadSize)).CopyTo(p, 6);
        BitConverter.GetBytes(viewPage).CopyTo(p, 8);
        BitConverter.GetBytes(lastPage).CopyTo(p, 0x0C);

        int at = GuildWareHistoryHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int tail = at + GuildWareHistoryEntryFixedSize;
            int next = tail + WStringSize(r.ActorName);
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? next : 0)).CopyTo(p, at + 2);
            WriteSlotAndString(p, at + 4, tail, r.ActorName);
            BitConverter.GetBytes(r.LogId).CopyTo(p, at + 6);
            BitConverter.GetBytes(r.LogTime).CopyTo(p, at + 0x0E);
            BitConverter.GetBytes(r.ActionType).CopyTo(p, at + 0x16);
            BitConverter.GetBytes(r.ItemTemplateId).CopyTo(p, at + 0x1A);
            BitConverter.GetBytes(r.ItemDbId).CopyTo(p, at + 0x1E);
            BitConverter.GetBytes(r.ItemAmountDelta).CopyTo(p, at + 0x26);
            BitConverter.GetBytes(r.MoneyDelta).CopyTo(p, at + 0x2A);
            at = next;
        }
        return p;
    }

    // ---------------------------------- the guild flag ----------------------------------

    /// <summary>
    /// S_UPDATE_GUILD_FLAG (0x7BA3), the answer to C_UPDATE_GUILD_FLAG: <c>[u16 imageIdOffset]
    /// [u8 Success]</c> then the string. The flag is identified by a STRING image id everywhere
    /// it appears - here, in S_REQUEST_GUILD_FLAG_IMAGE_DATA, in each S_BROCAST_GUILD_FLAG
    /// element and as GuildLogoId in the guild list - and the guilds row holds an int
    /// <c>logo_id</c>, so we send its decimal form. Nothing pins the real format: no capture
    /// carries a non-empty image id.
    /// </summary>
    public static byte[] BuildUpdateGuildFlag(bool success, string? imageId)
    {
        var text = WString(imageId);
        var p = new byte[7 + text.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_UPDATE_GUILD_FLAG).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)7).CopyTo(p, 4);
        p[6] = (byte)(success ? 1 : 0);
        text.CopyTo(p, 7);
        return p;
    }

    /// <summary>
    /// S_REQUEST_GUILD_FLAG_IMAGE_DATA (0xA795): <c>[u16 imageIdOffset][u16 imageOffset]
    /// [u16 imageCount]</c>, then the id string and the bytes. A <c>bytes</c> ref is
    /// <c>[offset][count]</c> in that order and an empty blob still gets a real offset - the end
    /// of the packet - which is the rule T51 pinned on S_GET_USER_GUILD_LOGO.
    /// </summary>
    public static byte[] BuildRequestGuildFlagImageData(string? imageId, byte[]? image)
    {
        var text = WString(imageId);
        image ??= Array.Empty<byte>();
        var p = new byte[10 + text.Length + image.Length];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_REQUEST_GUILD_FLAG_IMAGE_DATA).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)10).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(10 + text.Length)).CopyTo(p, 6);
        BitConverter.GetBytes((ushort)image.Length).CopyTo(p, 8);
        text.CopyTo(p, 10);
        image.CopyTo(p, 10 + text.Length);
        return p;
    }

    /// <summary>One castle's flag: the image id string and the castle it flies over.</summary>
    public sealed record GuildFlagEntry(string GuildFlagId, int FloatingCastleId);

    public const int GuildFlagEntryFixedSize = 10;

    /// <summary>
    /// S_BROCAST_GUILD_FLAG (0xDA23): a list of <c>[u16 here][u16 next][u16 guildFlagIdOffset]
    /// [i32 FloatingCastleId]</c>. Eighteen instances across the captures and every one of them
    /// is the eight-byte empty form - no floating castle was ever taken against the tap - so
    /// that is what goes out at enter-world.
    /// </summary>
    public static byte[] BuildBrocastGuildFlag(IReadOnlyList<GuildFlagEntry>? rows)
    {
        rows ??= Array.Empty<GuildFlagEntry>();
        int size = 8;
        foreach (var r in rows) size += GuildFlagEntryFixedSize + WStringSize(r.GuildFlagId);

        var p = new byte[size];
        BitConverter.GetBytes((ushort)p.Length).CopyTo(p, 0);
        BitConverter.GetBytes(S_BROCAST_GUILD_FLAG).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : 8)).CopyTo(p, 6);

        int at = 8;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int tail = at + GuildFlagEntryFixedSize;
            int next = tail + WStringSize(r.GuildFlagId);
            BitConverter.GetBytes((ushort)at).CopyTo(p, at);
            BitConverter.GetBytes((ushort)(i + 1 < rows.Count ? next : 0)).CopyTo(p, at + 2);
            WriteSlotAndString(p, at + 4, tail, r.GuildFlagId);
            BitConverter.GetBytes(r.FloatingCastleId).CopyTo(p, at + 6);
            at = next;
        }
        return p;
    }

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


/// <summary>
/// T95. The guild-search window, the wanted board, the level ranking, the guild-bank log and
/// the flag - eleven client packets that were either forwarded to World (which answers them
/// with "handler has not been implemented yet!!!") or, in C_REQUEST_GUILD_LIST's case, accepted
/// silently since T51, which leaves the window spinning on an empty list.
///
/// <para>Guild storage is the Arbiter's outright (status/GUILD-DESIGN.md section 0), so every
/// one of these is answered here from the <c>guilds</c>, <c>guild_members</c> and (new)
/// <c>guild_wanted</c> tables. Five of the seven replies are byte-exact against a captured
/// frame; the two that are not - S_GUILD_WARE_HISTORY and S_REQUEST_GUILD_FLAG_IMAGE_DATA -
/// follow their PDL dumpers and go out empty, because we keep no bank log and no capture holds
/// a non-empty flag.</para>
/// </summary>
public static class GuildBoard
{
    // Body sizes are the dumper's fixed part minus the 4-byte frame header.
    public const int RequestGuildListBodySize = 0x16 - 4;   // SearchWord slot + four i32
    public const int PageBodySize = 4;                      // [i32 PageNumber]
    public const int SortBodySize = 4;                      // [i32 GuildSortCriteria]
    public const int SetWantedBodySize = 0x0E - 4;          // slot + GuildSize + GuildPreference
    public const int RecommendGuildBodySize = 3;            // slot + [u8 InGuildList]
    public const int RecommendUserBodySize = 2;             // slot
    public const int UpdateFlagBodySize = 4;                // [u16 offset][u16 count]
    public const int FlagImageBodySize = 2;                 // slot

    /// <summary>
    /// What the window last searched for. C_REQUEST_GUILD_LIST carries the filter and
    /// C_REQUEST_GUILD_LIST_PAGE / _SORT carry only a page or a sort key, so the filter has to
    /// survive between them. Keyed by character, like every other per-player board here.
    /// </summary>
    public sealed record Query(string Word, int Level, int Size, int JoinType, int Preference,
                               int Sort);

    private static readonly Dictionary<int, Query> Queries = new();
    private static readonly object Gate = new();

    private static Query QueryOf(int characterId)
    {
        lock (Gate)
            return Queries.TryGetValue(characterId, out var q)
                ? q : new Query("", 0, -1, 0, 0, 0);
    }

    private static void Remember(int characterId, Query q)
    {
        if (characterId <= 0) return;
        lock (Gate) Queries[characterId] = q;
    }

    /// <summary>Drop a character's remembered search - called when the session ends.</summary>
    public static void Forget(int characterId)
    {
        lock (Gate) Queries.Remove(characterId);
    }

    private static int Me(GameSession s) => (int)(s.SelectedCharacter?.Id ?? 0);

    /// <summary>The image-id string form of a guilds row's int <c>logo_id</c>. Empty for a
    /// guild that has never uploaded one, which is every guild in every capture.</summary>
    public static string LogoIdOf(int logoId)
        => logoId == 0 ? "" : logoId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------- the guild search -------------------------------

    /// <summary>
    /// C_REQUEST_GUILD_LIST (0x866B), cap_social4_client frame 1639 and cap_social3_client2
    /// 1999: <c>[u16 searchWordOffset][i32 SearchLevel][i32 GuildSize][i32 JoinType]
    /// [i32 GuildPreference]</c>, then the word.
    /// </summary>
    public static Query ParseRequestGuildList(ReadOnlySpan<byte> body)
    {
        if (body.Length < RequestGuildListBodySize) return new Query("", 0, -1, 0, 0, 0);
        return new Query(
            ArbiterClientHandlers.ReadWString(body, 0),
            BitConverter.ToInt32(body[2..]),
            BitConverter.ToInt32(body[6..]),
            BitConverter.ToInt32(body[10..]),
            BitConverter.ToInt32(body[14..]),
            0);
    }

    /// <summary>
    /// The rows a query matches, already sorted. <c>SearchWord</c>, <c>JoinType</c> and
    /// <c>GuildPreference</c> filter on equality and a substring; <c>SearchLevel</c> is the
    /// searching character's own level and drops guilds whose join range excludes it.
    /// <para><c>GuildSize</c> is NOT applied: it is -1 in both captured requests, which is
    /// "any", and nothing pins what a non-negative value means - it is a bucket in the client's
    /// dropdown, not a member count, since the wanted board asks a poster for the same field.
    /// Filtering on a guess would hide guilds from the window.</para>
    /// </summary>
    public static List<ArbiterClientHandlers.GuildListEntry> Matching(
        CharacterStore? store, Query q)
    {
        var hits = new List<ArbiterClientHandlers.GuildListEntry>();
        if (store is null) return hits;

        var rows = new List<CharacterStore.GuildRow>();
        foreach (var g in store.GetAllGuilds())
        {
            if (q.Word.Length > 0
                && g.Name.IndexOf(q.Word, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (q.JoinType > 0 && g.JoinType != q.JoinType) continue;
            if (q.Preference > 0 && g.Preference != q.Preference) continue;
            if (q.Level > 0 && g.JoinMaxLevel > 0
                && (q.Level < g.JoinMinLevel || q.Level > g.JoinMaxLevel)) continue;
            rows.Add(g);
        }

        // The sort key is unpinned - no capture sends C_REQUEST_GUILD_LIST_SORT - so 1 and 2 are
        // the two orders the window offers and anything else is by name.
        rows.Sort((a, b) => q.Sort switch
        {
            1 => b.Level.CompareTo(a.Level),
            2 => b.CreateDate.CompareTo(a.CreateDate),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        });

        foreach (var g in rows)
            hits.Add(new ArbiterClientHandlers.GuildListEntry(
                g.GuildId, g.Name, store.CountGuildMembers(g.GuildId), g.JoinType,
                LogoIdOf(g.LogoId), g.Promotion, g.Preference, g.JoinMinLevel, g.JoinMaxLevel));
        return hits;
    }

    private static void SendGuildListPage(GameSession s, Query q, int page)
    {
        var all = Matching(Program.Store, q);
        var (first, count, pages) = ArbiterClientHandlers.GuildBoardPage(all.Count, page);
        var slice = all.GetRange(first, count);
        s.Send(ArbiterClientHandlers.BuildReplyGuildList(
            slice, pages == 0 ? 1 : Math.Min(page < 1 ? 1 : page, pages), pages, all.Count));
    }

    public static bool OnRequestGuildList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var q = ParseRequestGuildList(body.Span);
        Remember(Me(s), q);
        SendGuildListPage(s, q, 1);
        return true;
    }

    public static bool OnRequestGuildListPage(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        SendGuildListPage(s, QueryOf(Me(s)), ArbiterClientHandlers.ReadPageNumber(body.Span));
        return true;
    }

    public static bool OnRequestGuildListSort(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var b = body.Span;
        int criteria = b.Length >= 4 ? BitConverter.ToInt32(b) : 0;
        var q = QueryOf(Me(s)) with { Sort = criteria };
        Remember(Me(s), q);
        SendGuildListPage(s, q, 1);
        return true;
    }

    // ------------------------------- the wanted board -------------------------------

    /// <summary>
    /// The head of the board as this character sees it: whether the post button is live, and
    /// how long until it is. cap_social3_client2 frame 2140 is this one day after frame 2137.
    /// </summary>
    public static (bool CanWrite, long Remain) WantedCooldown(CharacterStore? store,
        int characterId, long nowUnix)
    {
        long last = store?.GetGuildWantedTime(characterId) ?? 0;
        if (last <= 0) return (true, 0);
        long remain = last + CharacterStore.GuildWantedCooldownSeconds - nowUnix;
        return remain <= 0 ? (true, 0) : (false, remain);
    }

    private static void SendWantedPage(GameSession s, int page)
    {
        var store = Program.Store;
        int me = Me(s);
        var all = store?.GetGuildWanted() ?? new List<CharacterStore.GuildWantedRow>();
        var (first, count, pages) = ArbiterClientHandlers.GuildBoardPage(all.Count, page);
        var (canWrite, remain) = WantedCooldown(store, me, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // InviteAuthority is the reader's right to invite off this board, which is a guild
        // authority we do hold - HasGuildAuthority is what C_INVITE_USER_TO_GUILD tests.
        int guildId = store?.GetGuildIdOf(me) ?? 0;
        bool invite = guildId > 0 && store is not null
                      && store.HasGuildAuthority(guildId, me, CharacterStore.GuildAuthorityInvite);
        s.Send(ArbiterClientHandlers.BuildReplyGuildWantedWritingList(
            all.GetRange(first, count), canWrite, invite, remain,
            pages == 0 ? 1 : Math.Min(page < 1 ? 1 : page, pages), pages));
    }

    public static bool OnRequestGuildWantedWritingList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        SendWantedPage(s, 1);
        return true;
    }

    public static bool OnRequestGuildWantedWritingListPage(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        SendWantedPage(s, ArbiterClientHandlers.ReadPageNumber(body.Span));
        return true;
    }

    /// <summary>
    /// C_REQUEST_SET_GUILD_WANTED_WRITING (0x8F7B), cap_social3_client2 frame 2137:
    /// <c>[u16 promotionOffset][i32 GuildSize][i32 GuildPreference]</c> then the text. The reply
    /// is one byte (frame 2138 says 1) and the client then re-asks for the list, which is how
    /// frame 2140 comes to show the cooldown.
    /// </summary>
    public static bool OnRequestSetGuildWantedWriting(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var b = body.Span;
        int me = Me(s);
        var store = Program.Store;
        bool ok = false;
        if (b.Length >= SetWantedBodySize && me > 0 && store is not null)
        {
            int size = BitConverter.ToInt32(b[2..]);
            int preference = BitConverter.ToInt32(b[6..]);
            string promotion = ArbiterClientHandlers.ReadWString(b, 0);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var (canWrite, remain) = WantedCooldown(store, me, now);
            if (!canWrite)
                log.LogInformation("C_REQUEST_SET_GUILD_WANTED_WRITING: {Id} must wait {Remain}s",
                    me, remain);
            else
                ok = store.SetGuildWanted(me, size, preference, promotion, now);
        }
        s.Send(ArbiterClientHandlers.BuildReplySetGuildWantedWriting(ok));
        return true;
    }

    // ---------------------------- ask a guild for an invite ----------------------------

    private static void SendInvitePage(GameSession s, int page)
    {
        var store = Program.Store;
        var all = new List<ArbiterClientHandlers.InviteGuildEntry>();
        if (store is not null)
            foreach (var g in Matching(store, new Query("", 0, -1, 0, 0, 0)))
                all.Add(new ArbiterClientHandlers.InviteGuildEntry(
                    g.GuildDbId, g.Name, g.MemberCount, g.GuildLogoId, g.GuildPreference,
                    g.JoinMinLevel, g.JoinMaxLevel));
        var (first, count, pages) = ArbiterClientHandlers.GuildBoardPage(all.Count, page);
        s.Send(ArbiterClientHandlers.BuildReplyInviteGuildList(
            all.GetRange(first, count), pages == 0 ? 1 : Math.Min(page < 1 ? 1 : page, pages), pages));
    }

    public static bool OnRequestInviteGuildList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        SendInvitePage(s, 1);
        return true;
    }

    public static bool OnRequestInviteGuildListPage(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        SendInvitePage(s, ArbiterClientHandlers.ReadPageNumber(body.Span));
        return true;
    }

    // -------------------------------- the level ranking --------------------------------

    /// <summary>
    /// Every guild by level, highest first. All four captured replies are EMPTY even though two
    /// guilds existed by then, because the real Arbiter publishes this ranking from a scheduled
    /// job rather than live; we have no such job, so we serve the live order and leave
    /// PreRanking at 0 - we keep no previous ranking to compare against - and IsOccupation
    /// false, since no floating castle is held in this build.
    /// </summary>
    public static List<ArbiterClientHandlers.GuildRankingEntry> Ranking(CharacterStore? store)
    {
        var rows = new List<ArbiterClientHandlers.GuildRankingEntry>();
        if (store is null) return rows;
        var guilds = store.GetAllGuilds();
        guilds.Sort((a, b) =>
        {
            int byLevel = b.Level.CompareTo(a.Level);
            if (byLevel != 0) return byLevel;
            int byExp = b.Exp.CompareTo(a.Exp);
            return byExp != 0 ? byExp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        for (int i = 0; i < guilds.Count; i++)
        {
            var g = guilds[i];
            rows.Add(new ArbiterClientHandlers.GuildRankingEntry(
                i + 1, 0, LogoIdOf(g.LogoId), g.Name,
                store.GetCharacter(g.ChiefDbId)?.Name ?? "", g.Preference, g.CreateDate,
                store.CountGuildMembers(g.GuildId), g.Level, false));
        }
        return rows;
    }

    public static bool OnRequestGuildLevelRanking(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        int page = ArbiterClientHandlers.ReadPageNumber(body.Span);
        var all = Ranking(Program.Store);
        var (first, count, pages) = ArbiterClientHandlers.GuildBoardPage(all.Count, page);
        s.Send(ArbiterClientHandlers.BuildGuildLevelRankingList(
            all.GetRange(first, count), pages == 0 ? 1 : Math.Min(page, pages), pages));
        return true;
    }

    // ------------------------------- the guild bank log -------------------------------

    /// <summary>
    /// C_GET_GUILD_WARE_HISTORY (0x8324) -&gt; S_GUILD_WARE_HISTORY (0x99B0). We keep no bank
    /// log - guild warehouse moves go through the same item atoms as every other container and
    /// nothing records the actor - so the window gets an empty page. Answering is still the
    /// point: forwarded, this is one of World's "handler has not been implemented yet!!!" lines.
    /// </summary>
    public static bool OnGetGuildWareHistory(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        s.Send(ArbiterClientHandlers.BuildGuildWareHistory(
            null, ArbiterClientHandlers.ReadPageNumber(body.Span)));
        return true;
    }

    // --------------------------------- recommendations ---------------------------------

    /// <summary>
    /// C_RECOMMEND_GUILD (0xD114): <c>[u16 nameOffset][u8 InGuildList]</c> then the guild name.
    /// C_RECOMMEND_USER_GUILD (0x6DD9): <c>[u16 nameOffset]</c> then a character name, whose
    /// guild is the one being recommended. Neither has a reply packet of its own - the window
    /// re-reads the guild info - and both land on <c>guilds.recommendation_point</c>, which is
    /// the column AS_SET_GUILD_RECOMMENDATION_POINT (0x1411) is the inter-server form of.
    /// </summary>
    public static bool OnRecommendGuild(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        string name = ArbiterClientHandlers.ReadWString(body.Span, 0);
        var guild = name.Length == 0 ? null : Program.Store?.GetGuildByName(name);
        if (guild is null)
        {
            log.LogInformation("C_RECOMMEND_GUILD: no guild '{Name}'", name);
            return true;
        }
        Program.Store?.AddGuildRecommendation(guild.GuildId, 1);
        return true;
    }

    public static bool OnRecommendUserGuild(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        string name = ArbiterClientHandlers.ReadWString(body.Span, 0);
        var store = Program.Store;
        var who = name.Length == 0 ? null : store?.GetCharacterByName(name);
        int guildId = who is null ? 0 : store?.GetGuildIdOf(who.Id) ?? 0;
        if (guildId <= 0)
        {
            log.LogInformation("C_RECOMMEND_USER_GUILD: '{Name}' is in no guild", name);
            return true;
        }
        store?.AddGuildRecommendation(guildId, 1);
        return true;
    }

    // ------------------------------------ the flag ------------------------------------

    /// <summary>
    /// C_UPDATE_GUILD_FLAG (0xE39D): one <c>bytes</c> ref, <c>[u16 offset][u16 count]</c>. The
    /// image goes to the same <c>guilds.logo</c> column C_UPDATE_GUILD_LOGO writes, under the
    /// same 8000-byte cap Guild::UpdateGuildLogo enforces, and is chief-only for the same
    /// reason. The reply carries the new image id.
    /// </summary>
    public static byte[]? ParseFlagImage(ReadOnlySpan<byte> body)
    {
        if (body.Length < UpdateFlagBodySize) return null;
        int at = BitConverter.ToUInt16(body) - 4;               // packet-relative
        int count = BitConverter.ToUInt16(body[2..]);
        if (count == 0) return Array.Empty<byte>();
        if (at < 0 || count > body.Length || at > body.Length - count) return null;
        return body.Slice(at, count).ToArray();
    }

    public static bool OnUpdateGuildFlag(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        var store = Program.Store;
        int me = Me(s);
        var image = ParseFlagImage(body.Span);
        int guildId = store?.GetGuildIdOf(me) ?? 0;
        var guild = guildId > 0 ? store!.GetGuild(guildId) : null;

        if (image is null || guild is null || guild.ChiefDbId != me
            || image.Length > CharacterStore.MaxGuildLogoBytes)
        {
            log.LogWarning("C_UPDATE_GUILD_FLAG: refused for {Id} ({Len} B)", me, image?.Length ?? -1);
            s.Send(ArbiterClientHandlers.BuildUpdateGuildFlag(false, ""));
            return true;
        }

        int logoId = store!.UpdateGuildLogo(guildId, image);
        s.Send(ArbiterClientHandlers.BuildUpdateGuildFlag(logoId != 0, LogoIdOf(logoId)));
        return true;
    }

    /// <summary>
    /// C_REQUEST_GUILD_FLAG_IMAGE_DATA (0x8947): one wide string, the image id the client saw in
    /// a guild list row or a flag broadcast. We resolve it back to the guild whose
    /// <c>logo_id</c> it is and send that blob; an id we do not know answers with the id and no
    /// bytes, which is the empty form of the same packet rather than silence.
    /// </summary>
    public static bool OnRequestGuildFlagImageData(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        string imageId = ArbiterClientHandlers.ReadWString(body.Span, 0);
        byte[]? image = null;
        var store = Program.Store;
        if (store is not null && int.TryParse(imageId, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int logoId) && logoId != 0)
            foreach (var g in store.GetAllGuilds())
                if (g.LogoId == logoId) { image = store.GetGuildLogo(g.GuildId); break; }

        s.Send(ArbiterClientHandlers.BuildRequestGuildFlagImageData(imageId, image));
        return true;
    }
}
