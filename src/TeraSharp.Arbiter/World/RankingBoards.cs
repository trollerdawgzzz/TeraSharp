using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>One line of a leaderboard, already ranked.</summary>
/// <param name="Rank">1-based, dense: equal scores share a rank.</param>
/// <param name="Score">Dungeon clears for PvE, kills for PvP.</param>
public readonly record struct RankingRow(
    int Rank, int CharacterId, string Name, int Class, int Level, long Score);

/// <summary>
/// T119. The two leaderboards, built from our own data, and the two packets that carry them.
///
/// <para><b>There is no capture.</b> The real Arbiter never answers C_REQUEST_P*_RANKING on
/// this build - confirmed live - so both layouts come from the binary and the shipped .def:</para>
/// <list type="bullet">
/// <item><b>S_PVP_RANKING_LIST</b> (0x62FA) from <c>S_PVP_RANKING_LIST.1.def</c>:
/// <c>array players { int32 unk; byte unk2; int32 rank; int32 rating; int32 class; string name }</c>.
/// The def's own comments call the first two "probably previous rank" and "probably the
/// up/down icon", and we send 0 for both rather than invent a trend.</item>
/// <item><b>S_PVE_RANKING_LIST</b> (0xBEDC) has a .def with NO fields, so its layout is read
/// straight off the writer, <c>PVERankingSystemManager::SendRankList</c> (Arb_part_050.c:9747,
/// which stamps 0xBEDC at :9758). Every offset below is that function's own store:
/// <code>
///   *puVar24 = here;  puVar24[1] = next;  puVar24[2] = nameOffset;   // +0 +2 +4, u16
///   *(u8 *)(puVar24 + 3)          = IsRookie(..)                     // +6
///   *(u32*)((char*)puVar24 + 7)   = rankInfo+0x1C                    // +7
///   *(u32*)((char*)puVar24 + 0xb) = rankInfo+0x18                    // +11
///   *(u32*)((char*)puVar24 + 0xf) = rankInfo+0x08                    // +15
///   *(u64*)((char*)puVar24 + 0x13)= rankInfo+0x10                    // +19
///   *(u32*)((char*)puVar24 + 0x1b)= node+0x18                        // +27
///   *local_b0 += 0x1f;  puVar24[2] = *local_b0;                      // then the name
/// </code>
/// so the element is 31 fixed bytes and a NUL-terminated UTF-16 name.</item>
/// </list>
///
/// <para><b>T126 named the three PvE scalars T119 could not</b>, from two witnesses that agree.
/// The element source is a <c>ReturnRankInfo&lt;T&gt;</c>, and the two boards share its shape:
/// <code>
///   ReturnRankInfo&lt;int&gt;       0x10 B: ?(+0)  int score(+4)                a(+8)   b(+0xC)
///   ReturnRankInfo&lt;LevelTime&gt; 0x20 B: ?(+0)  int level(+8) i64 time(+0x10) a(+0x18) b(+0x1C)
/// </code>
/// and both writers copy those four the same way - <c>score -&gt; +0x0F</c>, <c>a -&gt; +0x0B</c>,
/// <c>b -&gt; +0x07</c> (Arb_part_050.c:9812-9821 and :10266-10270). The PvP <c>.def</c> names
/// the two that matter: <c>rank</c> at +11 and <c>rating</c> at +15. So <b>+11 is the rank and
/// +15 is the score</b>, on both boards - which is the opposite of what T119 shipped.</para>
///
/// <para>The PvE i64 at +19 is <c>LevelTime.time</c>. <c>S_USER_PVE_RANKING</c>'s def - the
/// companion packet, written from the same <c>Score()</c> call - names that pair
/// <c>stageLevel</c> and <c>clearTime</c> (<c>*(lVar18+0x38)</c> i32 then <c>*(+0x40)</c> i64,
/// Arb_part_050.c:10054-10058), so the PvE element is
/// <c>rookie, changedRank, rank, stageLevel, clearTime, class</c>.</para>
///
/// <para><b>What is still ours to choose.</b> <c>changedRank</c> (+7) is 0: we keep no previous
/// season to diff against, and an invented arrow is worse than none. <c>rookie</c> (+6) is then
/// <c>IsRookie(viewer, rank, changedRank)</c> = <c>(viewer &lt; rank + 0) &amp;&amp; (rank &lt;= viewer)</c>,
/// which is always false - "we do not model the rookie band" rather than a flag that means
/// nothing. And the PvE board has no clear TIME behind it: TeraSharp stores clear COUNTS, so
/// the count goes in <c>clearTime</c> and the character's level in <c>stageLevel</c>. That is
/// stated here rather than hidden, and it is the one field on either board whose units the
/// client will read differently from how we mean them.</para>
///
/// <para><b>Season.</b> These packets carry none; the season lives in
/// <c>S_P*_LEADER_BOARD_INFO</c>, which T91 pinned to a real 52-byte capture and which we push
/// at enter-world with the season <see cref="CurrentSeason"/> names, so the season the
/// client asks for is the season we advertised - the real handler answers only
/// <c>season == current</c> (now) or <c>season &lt; current</c> (the last three), and a season
/// above the current one does nothing at all.</para>
/// </summary>
public static class RankingBoards
{
    /// <summary>
    /// The season number the live Classic+ server was on when
    /// <c>D:\packetlogs\classic_live.npcap</c> was taken (T130): both
    /// <c>S_P*_LEADER_BOARD_INFO</c> frames carry <c>season = 15</c>, and every
    /// <c>C_REQUEST_P*_RANKING</c> in that capture asks for 15. T119 assumed 1, which was
    /// T91's own capture from 2022 and is four years of seasons out of date.
    /// </summary>
    public const int DefaultSeason = 15;

    /// <summary>Override for <see cref="CurrentSeason"/>. A whole number; anything else is ignored.</summary>
    public const string SeasonEnvVariable = "TERASHARP_RANKING_SEASON";

    private static int? _season;

    /// <summary>
    /// The season <c>ArbiterClientHandlers.BuildP*LeaderBoardInfo</c> advertises and the only
    /// one the ranking handler answers with rows. Read once from
    /// <see cref="SeasonEnvVariable"/>, else <see cref="DefaultSeason"/>.
    ///
    /// <para>It has to be one number for both: the client asks for whatever season the info
    /// frame told it about, and the handler compares what it is asked for against this. A
    /// mismatch is an empty board, silently - which is exactly the bug that would follow from
    /// bumping one and not the other.</para>
    /// </summary>
    public static int CurrentSeason
    {
        get
        {
            if (_season is int s) return s;
            int v = DefaultSeason;
            var raw = Environment.GetEnvironmentVariable(SeasonEnvVariable);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int parsed)
                && parsed > 0) v = parsed;
            _season = v;
            return v;
        }
    }

    /// <summary>Tests only: forget the cached season so the next read takes the env again.</summary>
    public static void ResetSeason() => _season = null;

    /// <summary>S_PVE_RANKING_LIST, from data.json's 376012 map.</summary>
    public const ushort S_PVE_RANKING_LIST = 0xBEDC;
    /// <summary>S_PVP_RANKING_LIST, same map.</summary>
    public const ushort S_PVP_RANKING_LIST = 0x62FA;

    /// <summary>Rows per page. The client asks for no page, so this bounds one frame.</summary>
    public const int PageSize = 50;

    /// <summary>The aggregate class selector - T118's crash analysis: 0..14 index, 16 is all.</summary>
    public const int AllClasses = 0x10;

    // ---- the PvE element (T126: three slots re-pointed, see the class remarks) ----
    public const int PveElementFixedSize = 0x1F;
    public const int PveHereOffset = 0;
    public const int PveNextOffset = 2;
    public const int PveNameRefOffset = 4;
    public const int PveRookieOffset = 6;        // IsRookie(..)
    public const int PveChangedRankOffset = 7;   // src+0x1C - the band's width
    public const int PveRankOffset = 11;         // src+0x18 - the band's base
    public const int PveStageLevelOffset = 15;   // src+0x08 - LevelTime.level
    public const int PveClearTimeOffset = 19;    // src+0x10 - LevelTime.time, i64
    public const int PveClassOffset = 27;        // user record +0x18, beside the name

    // ---- the PvP element ----
    // The .def and the writer disagree on the first two scalars: the def declares
    // `int32 unk; byte unk2`, which encodes as i32@6 + u8@10, while the writer stores
    // `*(u8 *)(puVar27 + 3)` at +6 and `*(u32 *)((char *)puVar27 + 7)` at +7
    // (Arb_part_050.c:10266-10267) - byte FIRST. The decompile wins, so the byte sits at +6.
    // Both fields are 0 in every frame we send, so the bytes are the same either way and the
    // def cross-check in the tests still holds; only the shape is corrected.
    public const int PvpElementFixedSize = 23;
    public const int PvpHereOffset = 0;
    public const int PvpNextOffset = 2;
    public const int PvpNameRefOffset = 4;
    public const int PvpRookieOffset = 6;        // writer: the IsRookie byte
    public const int PvpChangedRankOffset = 7;   // src+0x0C - the band's width
    public const int PvpRankOffset = 11;         // src+0x08 - def `rank`
    public const int PvpRatingOffset = 15;       // src+0x04 - def `rating`, the score
    public const int PvpClassOffset = 19;

    /// <summary>The 4-byte client header both packets carry.</summary>
    public const int HeaderSize = 4;
    /// <summary>[u16 count][u16 firstElementOffset] - the list head, right after the header.</summary>
    public const int ListHeadSize = 4;

    /// <summary>
    /// Whether a class filter admits a character. T118 pinned the range the real Arbiter
    /// survives: 0..14 index one array, 16 is the aggregate that skips the index entirely.
    /// Anything else never reaches here - the handler refuses it first.
    /// </summary>
    public static bool ClassMatches(int filter, int characterClass)
        => filter == AllClasses || filter == characterClass;

    /// <summary>
    /// Rank a scored list: order by score descending, then by character id so the order is
    /// stable between two calls, and give equal scores the same (dense) rank.
    /// </summary>
    public static List<RankingRow> Rank(IEnumerable<CharacterStore.RankingScore> scores,
                                        int classFilter)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var kept = new List<CharacterStore.RankingScore>();
        foreach (var s in scores)
            if (s.Score > 0 && ClassMatches(classFilter, s.Class)) kept.Add(s);
        kept.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score)
                                               : a.CharacterId.CompareTo(b.CharacterId));

        var rows = new List<RankingRow>(kept.Count);
        long lastScore = long.MinValue;
        int rank = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            if (kept[i].Score != lastScore) { rank = i + 1; lastScore = kept[i].Score; }
            rows.Add(new RankingRow(rank, kept[i].CharacterId, kept[i].Name,
                                    kept[i].Class, kept[i].Level, kept[i].Score));
        }
        return rows;
    }

    /// <summary>
    /// One page, with the requester's own row appended when the page does not already hold it -
    /// which is what "my rank" on the leaderboard window means. Out-of-range pages come back
    /// empty rather than clamped: an empty list is a legal frame and a wrong page is not.
    /// </summary>
    public static List<RankingRow> Page(IReadOnlyList<RankingRow> all, int page, int selfId,
                                        int pageSize = PageSize)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (pageSize <= 0) pageSize = PageSize;
        var outp = new List<RankingRow>(pageSize + 1);
        if ((uint)page <= int.MaxValue / pageSize)
            for (int i = page * pageSize; i < all.Count && i < (page + 1) * pageSize; i++)
                outp.Add(all[i]);

        if (selfId > 0)
        {
            bool onPage = false;
            foreach (var r in outp) if (r.CharacterId == selfId) { onPage = true; break; }
            if (!onPage)
                foreach (var r in all)
                    if (r.CharacterId == selfId) { outp.Add(r); break; }
        }
        return outp;
    }

    /// <summary>
    /// S_PVE_RANKING_LIST (0xBEDC): rookie, changedRank, rank, stageLevel, clearTime, class,
    /// name - see the class remarks for how T126 pinned those six names.
    /// <paramref name="viewerRank"/> is the requester's own rank, the value the server feeds
    /// IsRookie; it changes no byte while changedRank is 0, and is taken rather than assumed
    /// so the flag stays the server's formula and not a constant.
    /// </summary>
    public static byte[] BuildPveRankingList(IReadOnlyList<RankingRow>? rows, int viewerRank = 0)
    {
        rows ??= Array.Empty<RankingRow>();
        var names = new byte[rows.Count][];
        int size = HeaderSize + ListHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            names[i] = WStringZ(rows[i].Name);
            size += PveElementFixedSize + names[i].Length;
        }

        var p = new byte[size];
        BitConverter.GetBytes((ushort)size).CopyTo(p, 0);
        BitConverter.GetBytes(S_PVE_RANKING_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : HeaderSize + ListHeadSize)).CopyTo(p, 6);

        int at = HeaderSize + ListHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int nameAt = at + PveElementFixedSize;
            int next = i + 1 < rows.Count ? nameAt + names[i].Length : 0;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at + PveHereOffset);
            BitConverter.GetBytes((ushort)next).CopyTo(p, at + PveNextOffset);
            BitConverter.GetBytes((ushort)nameAt).CopyTo(p, at + PveNameRefOffset);
            const int changedRank = 0;
            p[at + PveRookieOffset] = IsRookie(viewerRank, r.Rank, changedRank);
            BitConverter.GetBytes(changedRank).CopyTo(p, at + PveChangedRankOffset);
            BitConverter.GetBytes(r.Rank).CopyTo(p, at + PveRankOffset);
            BitConverter.GetBytes(r.Level).CopyTo(p, at + PveStageLevelOffset);
            BitConverter.GetBytes(r.Score).CopyTo(p, at + PveClearTimeOffset);
            BitConverter.GetBytes(r.Class).CopyTo(p, at + PveClassOffset);
            names[i].CopyTo(p, nameAt);
            at = nameAt + names[i].Length;
        }
        return p;
    }

    /// <summary>
    /// S_PVP_RANKING_LIST (0x62FA): rookie, changedRank, rank, rating, class, name.
    /// <c>rating</c> carries the kill count. <c>changedRank</c> is 0 - no previous season to
    /// diff against - and <c>rookie</c> follows from it, as on the PvE board.
    /// </summary>
    public static byte[] BuildPvpRankingList(IReadOnlyList<RankingRow>? rows, int viewerRank = 0)
    {
        rows ??= Array.Empty<RankingRow>();
        var names = new byte[rows.Count][];
        int size = HeaderSize + ListHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            names[i] = WStringZ(rows[i].Name);
            size += PvpElementFixedSize + names[i].Length;
        }

        var p = new byte[size];
        BitConverter.GetBytes((ushort)size).CopyTo(p, 0);
        BitConverter.GetBytes(S_PVP_RANKING_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)rows.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(rows.Count == 0 ? 0 : HeaderSize + ListHeadSize)).CopyTo(p, 6);

        int at = HeaderSize + ListHeadSize;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int nameAt = at + PvpElementFixedSize;
            int next = i + 1 < rows.Count ? nameAt + names[i].Length : 0;
            BitConverter.GetBytes((ushort)at).CopyTo(p, at + PvpHereOffset);
            BitConverter.GetBytes((ushort)next).CopyTo(p, at + PvpNextOffset);
            BitConverter.GetBytes((ushort)nameAt).CopyTo(p, at + PvpNameRefOffset);
            const int changedRank = 0;
            p[at + PvpRookieOffset] = IsRookie(viewerRank, r.Rank, changedRank);
            BitConverter.GetBytes(changedRank).CopyTo(p, at + PvpChangedRankOffset);
            BitConverter.GetBytes(r.Rank).CopyTo(p, at + PvpRankOffset);
            BitConverter.GetBytes((int)Math.Clamp(r.Score, int.MinValue, int.MaxValue))
                .CopyTo(p, at + PvpRatingOffset);
            BitConverter.GetBytes(r.Class).CopyTo(p, at + PvpClassOffset);
            names[i].CopyTo(p, nameAt);
            at = nameAt + names[i].Length;
        }
        return p;
    }

    /// <summary>
    /// The server's own formula, both boards: <c>(viewer &lt; base + width) &amp;&amp; (base &lt;= viewer)</c>
    /// over the +11 and +7 values (<c>P*RankingSystemManager::IsRookie(int,int,int)</c>, inlined
    /// at Arb_part_050.c:9797 and :10247). With a width of 0 the band is empty and the answer is
    /// always 0; it is written as the formula so a future width makes the flag correct for free.
    /// </summary>
    public static byte IsRookie(int viewer, int bandBase, int bandWidth)
        => (byte)(bandWidth > 0 && bandBase <= viewer && viewer < bandBase + bandWidth ? 1 : 0);

    // =====================================================================
    // T126 - the companion packet. PVERankingSystemManager::SendNowSeasonRank sends TWO
    // frames: the list, then the requester's own line (Arb_part_050.c:10074 / :10518). T119
    // sent only the first, so the "my record" row under the board had nothing to fill it.
    // =====================================================================

    /// <summary>S_USER_PVE_RANKING, from data.json's 376012 map.</summary>
    public const ushort S_USER_PVE_RANKING = 0xE748;
    /// <summary>S_USER_PVP_RANKING, same map.</summary>
    public const ushort S_USER_PVP_RANKING = 0xC844;

    /// <summary>25 bytes: header + <c>byte rookie, i32 changedRank, i32 rank, i32 stageLevel,
    /// i64 clearTime</c>.</summary>
    public const int UserPveRankingSize = HeaderSize + 1 + 4 + 4 + 4 + 8;
    /// <summary>17 bytes: header + <c>byte rookie, i32 changedRank, i32 rank, i32 score</c>.</summary>
    public const int UserPvpRankingSize = HeaderSize + 1 + 4 + 4 + 4;

    /// <summary>
    /// S_USER_PVE_RANKING (0xE748), pinned to <c>S_USER_PVE_RANKING.1.def</c> and to the write
    /// order of <c>SendToSession&lt;PKT_S_USER_PVE_RANKING_WRITE, bool, int&amp;, int&amp;, int&amp;,
    /// __int64&amp;&gt;</c> (Arb_part_050.c:10068-10078) - the two agree field for field.
    /// <para>An unranked requester sends rank 0 with everything else 0, which is the same shape
    /// the server sends when <c>Rank()</c> misses its tree.</para>
    /// </summary>
    public static byte[] BuildUserPveRanking(int rank, int stageLevel, long clearTime,
                                             int changedRank = 0, int viewerRank = 0)
    {
        var p = new byte[UserPveRankingSize];
        BitConverter.GetBytes((ushort)UserPveRankingSize).CopyTo(p, 0);
        BitConverter.GetBytes(S_USER_PVE_RANKING).CopyTo(p, 2);
        p[4] = IsRookie(viewerRank, rank, changedRank);
        BitConverter.GetBytes(changedRank).CopyTo(p, 5);
        BitConverter.GetBytes(rank).CopyTo(p, 9);
        BitConverter.GetBytes(stageLevel).CopyTo(p, 13);
        BitConverter.GetBytes(clearTime).CopyTo(p, 17);
        return p;
    }

    /// <summary>
    /// S_USER_PVP_RANKING (0xC844), from <c>S_USER_PVP_RANKING.1.def</c> and
    /// <c>PKT_S_USER_PVP_RANKING_WRITE(bool, int&amp;, int&amp;, int&amp;)</c> (Arb_part_050.c:10512-10520):
    /// the PvE five minus the i64.
    /// </summary>
    public static byte[] BuildUserPvpRanking(int rank, int score, int changedRank = 0,
                                             int viewerRank = 0)
    {
        var p = new byte[UserPvpRankingSize];
        BitConverter.GetBytes((ushort)UserPvpRankingSize).CopyTo(p, 0);
        BitConverter.GetBytes(S_USER_PVP_RANKING).CopyTo(p, 2);
        p[4] = IsRookie(viewerRank, rank, changedRank);
        BitConverter.GetBytes(changedRank).CopyTo(p, 5);
        BitConverter.GetBytes(rank).CopyTo(p, 9);
        BitConverter.GetBytes(score).CopyTo(p, 13);
        return p;
    }

    /// <summary>
    /// T133. Whether the requester's own line (<c>S_USER_P*_RANKING</c>) follows the list.
    /// <c>SendNowSeasonRank</c> guards it with
    /// <c>param_5 == *(int *)(local_80 + 0x2f) || param_5 == 0x10</c> (Arb_part_050.c:9961):
    /// the class asked for is the requester's own, or the aggregate. classic_live agrees on
    /// all nine of its requests - the one that asked for a class the viewer is not (frame
    /// 5846, class 0 from a class-9 player) got the list alone.
    /// <para>A viewer with no character has no class, so <paramref name="viewerClass"/> of -1
    /// matches only the aggregate.</para>
    /// </summary>
    public static bool SendsSelfRank(int classFilter, int viewerClass)
        => classFilter == AllClasses || classFilter == viewerClass;

    /// <summary>The requester's own row on the FULL board, or null when they are not on it.</summary>
    public static RankingRow? Self(IReadOnlyList<RankingRow> all, int selfId)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (selfId <= 0) return null;
        foreach (var r in all) if (r.CharacterId == selfId) return r;
        return null;
    }

    /// <summary>NUL-terminated UTF-16LE, the only string form on this wire.</summary>
    public static byte[] WStringZ(string? s)
    {
        s ??= string.Empty;
        var b = new byte[(s.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(s).CopyTo(b, 0);
        return b;
    }
}
