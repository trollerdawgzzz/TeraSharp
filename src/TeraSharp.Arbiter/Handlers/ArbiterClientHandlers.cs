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

        // The owner the request names, when it names one that is not us, is a different
        // character's item - a linked item in chat. We only have our own rows, so anything else
        // goes unanswered exactly as the real Arbiter's `lVar5 == 0` branch does.
        long owner = req.Value.ItemOwnerDbId != 0 ? req.Value.ItemOwnerDbId : (long)chr.Id;
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
    /// The real handler writes one value through <c>FUN_1404aa390</c> (the float writer) and we
    /// have no broker, so 0 is the honest answer: the client uses it to pre-fill the
    /// item-level filter in the broker search.
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
        s.Send(BuildTradeBrokerHighestItemLevel(0f));
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

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Reads a NUL-terminated UTF-16LE string whose u16 PACKET offset sits at
    /// <paramref name="slotIndex"/> of the BODY. Empty for the 0 / out-of-range offsets the real
    /// handlers fall back on (<c>if ((uVar1 == 0) || (*param_2 &lt;= uVar1)) puVar6 = &amp;DAT_140d3e020;</c>).
    /// </summary>
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
