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
/// <para><b>What we cannot name, and do not pretend to.</b> Three of the PvE scalars have no
/// name in the def (empty), the dumper (none for this packet) or any capture. What the binary
/// does tell us is the relation: <c>PVERankingSystemManager::IsRookie(int,int,int)</c> is
/// <c>(myLevel &lt; B + A) &amp;&amp; (B &lt;= myLevel)</c> over the +11 and +7 values, so those two are a
/// LEVEL BAND - a base and a width - and not the entry's own level. We are the data source
/// here, so the choice is ours and it is spelled out in <see cref="BuildPveRankingList"/>:
/// +15 rank, +19 score, +27 class, +11 the entry's level, +7 zero, and IsRookie computed from
/// what we wrote so the frame is self-consistent whatever the client does with it.</para>
///
/// <para><b>Season.</b> These packets carry none; the season lives in
/// <c>S_P*_LEADER_BOARD_INFO</c>, which T91 pinned to a real 52-byte capture and which we push
/// at enter-world with season 1. <see cref="CurrentSeason"/> is that same 1, so the season the
/// client asks for is the season we advertised - the real handler answers only
/// <c>season == current</c> (now) or <c>season &lt; current</c> (the last three), and a season
/// above the current one does nothing at all.</para>
/// </summary>
public static class RankingBoards
{
    /// <summary>The season <c>ArbiterClientHandlers.BuildP*LeaderBoardInfo</c> advertises.</summary>
    public const int CurrentSeason = 1;

    /// <summary>S_PVE_RANKING_LIST, from data.json's 376012 map.</summary>
    public const ushort S_PVE_RANKING_LIST = 0xBEDC;
    /// <summary>S_PVP_RANKING_LIST, same map.</summary>
    public const ushort S_PVP_RANKING_LIST = 0x62FA;

    /// <summary>Rows per page. The client asks for no page, so this bounds one frame.</summary>
    public const int PageSize = 50;

    /// <summary>The aggregate class selector - T118's crash analysis: 0..14 index, 16 is all.</summary>
    public const int AllClasses = 0x10;

    // ---- the PvE element, from SendRankList ----
    public const int PveElementFixedSize = 0x1F;
    public const int PveHereOffset = 0;
    public const int PveNextOffset = 2;
    public const int PveNameRefOffset = 4;
    public const int PveRookieOffset = 6;
    public const int PveBandWidthOffset = 7;    // rankInfo+0x1C
    public const int PveBandBaseOffset = 11;    // rankInfo+0x18
    public const int PveRankOffset = 15;        // rankInfo+0x08
    public const int PveScoreOffset = 19;       // rankInfo+0x10, i64
    public const int PveClassOffset = 27;       // node+0x18, beside the name

    // ---- the PvP element, from S_PVP_RANKING_LIST.1.def ----
    // String references come before the fixed scalars in this encoding, which is exactly the
    // order SendRankList writes the PvE one in - the two agree, which is the only cross-check
    // available without a capture.
    public const int PvpElementFixedSize = 23;
    public const int PvpHereOffset = 0;
    public const int PvpNextOffset = 2;
    public const int PvpNameRefOffset = 4;
    public const int PvpPrevRankOffset = 6;     // def: int32 unk, "probably previous rank"
    public const int PvpTrendOffset = 10;       // def: byte unk2, the up/down icon
    public const int PvpRankOffset = 11;
    public const int PvpRatingOffset = 15;
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
    /// S_PVE_RANKING_LIST (0xBEDC). Layout from SendRankList - see the class remarks.
    ///
    /// <para>The three slots the binary does not name are filled as: +15 rank, +19 score (the
    /// only 64-bit field, and a clear count fits where a LevelTime went), +27 class (the value
    /// the writer reads from the same node as the name), +11 the entry's level and +7 a band
    /// width of 0. IsRookie is then computed the way the server computes it, from those two,
    /// which with a width of 0 is always false - a deliberate "we do not model the rookie
    /// band" rather than a flag that means nothing.</para>
    /// </summary>
    public static byte[] BuildPveRankingList(IReadOnlyList<RankingRow>? rows, int viewerLevel = 0)
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
            const int bandWidth = 0;
            p[at + PveRookieOffset] = (byte)(bandWidth > 0 && r.Level <= viewerLevel
                                             && viewerLevel < r.Level + bandWidth ? 1 : 0);
            BitConverter.GetBytes(bandWidth).CopyTo(p, at + PveBandWidthOffset);
            BitConverter.GetBytes(r.Level).CopyTo(p, at + PveBandBaseOffset);
            BitConverter.GetBytes(r.Rank).CopyTo(p, at + PveRankOffset);
            BitConverter.GetBytes(r.Score).CopyTo(p, at + PveScoreOffset);
            BitConverter.GetBytes(r.Class).CopyTo(p, at + PveClassOffset);
            names[i].CopyTo(p, nameAt);
            at = nameAt + names[i].Length;
        }
        return p;
    }

    /// <summary>
    /// S_PVP_RANKING_LIST (0x62FA), from the shipped .def. <c>rating</c> carries the kill
    /// count; <c>unk</c> (previous rank) and <c>unk2</c> (the trend arrow) are 0, because we
    /// keep no history to compare against and a made-up arrow is worse than none.
    /// </summary>
    public static byte[] BuildPvpRankingList(IReadOnlyList<RankingRow>? rows)
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
            BitConverter.GetBytes(0).CopyTo(p, at + PvpPrevRankOffset);
            p[at + PvpTrendOffset] = 0;
            BitConverter.GetBytes(r.Rank).CopyTo(p, at + PvpRankOffset);
            BitConverter.GetBytes((int)Math.Clamp(r.Score, int.MinValue, int.MaxValue))
                .CopyTo(p, at + PvpRatingOffset);
            BitConverter.GetBytes(r.Class).CopyTo(p, at + PvpClassOffset);
            names[i].CopyTo(p, nameAt);
            at = nameAt + names[i].Length;
        }
        return p;
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
