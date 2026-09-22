// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T147 - the crafting half of the Arbiter&lt;-&gt;World DB proxy. Research: status/CRAFTING.md.
///
/// <para><b>Who builds what.</b> The two client packets the crafting window reads,
/// S_ARTISAN_SKILL_LIST (0x57AB) and S_ARTISAN_RECIPE_LIST (0x61FE), are written by
/// WorldServer and reach the client inside SA_BYPASS_TO_CLIENT (cap_social4.log seq 337, 560,
/// 4978, 5603, 5802 - bypass link 2, about 15 ms after the Arbiter's two load replies). The
/// Arbiter has no writer for either. What the Arbiter owns is the data behind them: the learned
/// recipe list and the skill proficiencies, both per character, both answered through the DB
/// proxy. Feed World the right rows and World builds the list the client shows.</para>
///
/// <para><b>Why the window did not work.</b> TeraSharp answered the two loads (always empty) and
/// nothing else. SDB_LEARN_ITEM_RECIPE, SDB_DELETE_ITEM_RECIPE_LIST, SDB_SET_RECIPE_BOOKMARK,
/// SDB_UPDATE_SKILL_PROF and both SDB_ITEM_PRODUCE steps carry a DlmId and had no answer, so the
/// first recipe scroll or the first craft head-blocked that character's DB queue.</para>
///
/// <para>Every layout below is from the message's own PDL dumper in ArbiterServer.exe.c
/// (the function that names every field for the packet log) and, for the two loads, from the
/// World-side parser Handler_DBS_LOAD_ITEM_RECIPE. Offsets are PAYLOAD-relative
/// (frame - 6); ref slots hold FRAME-relative offsets, as everywhere else on this link.</para>
/// </summary>
public static class ArtisanDb
{
    /// <summary>World-built, tunnelled to the client. Named here for the logs and the tests.</summary>
    public const ushort S_ARTISAN_RECIPE_LIST = 0x61FE;
    /// <summary>World-built, tunnelled to the client.</summary>
    public const ushort S_ARTISAN_SKILL_LIST = 0x57AB;

    // ---------------------------------------------------------------- record formats

    /// <summary>
    /// One RecipeInfo, 28 bytes - the element of DBS_LOAD_ITEM_RECIPE's RecipeIds array.
    /// <c>User::LoadUserRecipeNoLock</c> fills it from dbo.spLoadItemRecipe's four columns and
    /// the reply writer copies it raw; Handler_DBS_LOAD_ITEM_RECIPE reads the same four fields
    /// back. <c>User::LearnItemRecipeNoLock(int,bool)</c> names +4, and
    /// <c>User::SetItemRecipeBookmarkNoLock(int,bool)</c> names +24.
    /// </summary>
    public const int RecipeInfoSize = 28;
    public const int RecipeIdOffset = 0;         // i32
    public const int RecipeExtractOffset = 4;    // u8, then three bytes World does not read
    public const int RecipeLearnedAtOffset = 8;  // 16-byte ODBC TIMESTAMP_STRUCT
    public const int RecipeBookmarkOffset = 24;  // u8, then three bytes World does not read

    /// <summary>One skill proficiency, 8 bytes: dbo.spLoadSkillProf's two int columns.</summary>
    public const int SkillProfSize = 8;
    public const int SkillProfIdOffset = 0;
    public const int SkillProfValueOffset = 4;

    /// <summary>Records per list we will ever send or read. Far above any real character.</summary>
    public const int MaxListRecords = 4096;

    // ---------------------------------------------------------------- request layouts

    /// <summary>SDB_LOAD_ITEM_RECIPE / SDB_LOAD_SKILL_PROF: DlmId@0, OwnerDBID@4.</summary>
    public const int LoadRequestSize = 8;

    /// <summary>SDB_LEARN_ITEM_RECIPE: ItemBinary ref@0, DlmId@8, OwnerDBID@12, RecipeId@16,
    /// Extract u8@20. The dumper's guard is frame &gt; 0x1A.</summary>
    public const int LearnReqBinaryRef = 0, LearnReqDlmId = 8, LearnReqOwner = 12,
                     LearnReqRecipeId = 16, LearnReqExtract = 20, LearnRequestSize = 21;

    /// <summary>SDB_DELETE_ITEM_RECIPE_LIST: RecipeIds ref@0 (a u32 array, byte count),
    /// DlmId@8, OwnerDbId@12.</summary>
    public const int DeleteReqIdsRef = 0, DeleteReqDlmId = 8, DeleteReqOwner = 12, DeleteRequestSize = 16;

    /// <summary>SDB_SET_RECIPE_BOOKMARK: DlmId@0, OwnerDBID@4, RecipeId@8, Flag u8@12.</summary>
    public const int BookmarkReqRecipeId = 8, BookmarkReqFlag = 12, BookmarkRequestSize = 13;

    /// <summary>SDB_ITEM_PRODUCE_STEP1: DlmId@0, OwnerDbId@4, Type@8, DeltaPoint@12. Type is a
    /// FatigabilityType and DeltaPoint goes to FatigabilityController::ChangeFatigabilityPoint on
    /// the ACCOUNT - step 1 spends production points, it touches no item. T147b: World's
    /// DBItemProduceTransaction::ExecuteTransaction always writes Type 1 and DeltaPoint = minus
    /// the recipe's cost, and classic_craft.log shows the cost land: S_FATIGABILITY_POINT goes
    /// 1340, 1335, 1330 over two crafts of recipe 286040 (-5 each) and 1230 to 1210 and 1150 to
    /// 1130 over two of 286050 (-20).</summary>
    public const int Step1ReqType = 8, Step1ReqDeltaPoint = 12, Step1RequestSize = 16;

    /// <summary>SDB_ITEM_PRODUCE_STEP2: ItemBinary ref@0, ItemEnchantData ref@8, DlmId@16,
    /// OwnerDBID@20, SkillProfId@24, SkillProfValue@28.</summary>
    public const int Step2ReqBinaryRef = 0, Step2ReqEnchantRef = 8, Step2ReqDlmId = 16, Step2ReqOwner = 20,
                     Step2ReqProfId = 24, Step2ReqProfValue = 28, Step2RequestSize = 32;

    /// <summary>SDB_UPDATE_SKILL_PROF: DlmId@0, OwnerDBID@4, SkillProfId@8, Value@12.</summary>
    public const int UpdateProfReqId = 8, UpdateProfReqValue = 12, UpdateProfRequestSize = 16;

    // ---------------------------------------------------------------- reply layouts

    /// <summary>
    /// The four ref replies (DBS_LOAD_ITEM_RECIPE, DBS_LOAD_SKILL_PROF, DBS_LEARN_ITEM_RECIPE,
    /// DBS_ITEM_PRODUCE_STEP2): ref off@0, ref byte count@4, DlmId@8, Success u8@12, then the data.
    /// The offset slot is backpatched to the running frame length even when the list is empty,
    /// so it is always 19 - arb_world_2026-09-13 seq 369/371, cap_newchar seq 172/174 and
    /// lobby_tap seq 141/143 all read <c>13 00 00 00 00 00 00 00 [dlm] 01</c>.
    /// </summary>
    public const int RefReplyHeader = 13;

    public readonly record struct Recipe(int RecipeId, bool Extract, long LearnedAtUnix, bool Bookmark);
    public readonly record struct SkillProf(int SkillProfId, int Value);

    private static uint U32(byte[] p, int at) => BitConverter.ToUInt32(p, at);

    // ---------------------------------------------------------------- builders

    /// <summary>One RecipeInfo. A zero learn time is sent as an all-zero TIMESTAMP, which is what
    /// the real loader leaves for a year below 2000.</summary>
    public static byte[] BuildRecipeInfo(Recipe r)
    {
        var b = new byte[RecipeInfoSize];
        BitConverter.GetBytes(r.RecipeId).CopyTo(b, RecipeIdOffset);
        b[RecipeExtractOffset] = (byte)(r.Extract ? 1 : 0);
        if (r.LearnedAtUnix > 0)
            DbProxyHandlers.EncodeDbDateTime(DateTimeOffset.FromUnixTimeSeconds(r.LearnedAtUnix).UtcDateTime)
                .CopyTo(b, RecipeLearnedAtOffset);
        b[RecipeBookmarkOffset] = (byte)(r.Bookmark ? 1 : 0);
        return b;
    }

    /// <summary>[ref 19][ref byteCount][DlmId][Success] + data.</summary>
    public static byte[] BuildRefReply(byte[]? data, uint dlmId, bool ok)
    {
        data ??= Array.Empty<byte>();
        var p = new byte[RefReplyHeader + data.Length];
        BitConverter.GetBytes((uint)(6 + RefReplyHeader)).CopyTo(p, 0);
        BitConverter.GetBytes((uint)data.Length).CopyTo(p, 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        p[12] = (byte)(ok ? 1 : 0);
        data.CopyTo(p, RefReplyHeader);
        return p;
    }

    /// <summary>DBS_LOAD_ITEM_RECIPE (0x2761). Empty is byte-identical to the captures.</summary>
    public static byte[] BuildDbsLoadItemRecipe(uint dlmId, IReadOnlyList<Recipe>? rows, bool ok = true)
    {
        rows ??= Array.Empty<Recipe>();
        int n = Math.Min(rows.Count, MaxListRecords);
        var data = new byte[n * RecipeInfoSize];
        for (int i = 0; i < n; i++) BuildRecipeInfo(rows[i]).CopyTo(data, i * RecipeInfoSize);
        return BuildRefReply(data, dlmId, ok);
    }

    /// <summary>DBS_LOAD_SKILL_PROF (0x2765). Empty is byte-identical to the captures.</summary>
    public static byte[] BuildDbsLoadSkillProf(uint dlmId, IReadOnlyList<SkillProf>? rows, bool ok = true)
    {
        rows ??= Array.Empty<SkillProf>();
        int n = Math.Min(rows.Count, MaxListRecords);
        var data = new byte[n * SkillProfSize];
        for (int i = 0; i < n; i++)
        {
            BitConverter.GetBytes(rows[i].SkillProfId).CopyTo(data, i * SkillProfSize + SkillProfIdOffset);
            BitConverter.GetBytes(rows[i].Value).CopyTo(data, i * SkillProfSize + SkillProfValueOffset);
        }
        return BuildRefReply(data, dlmId, ok);
    }

    /// <summary>DBS_SET_RECIPE_BOOKMARK, DBS_ITEM_PRODUCE_STEP1 and DBS_UPDATE_SKILL_PROF:
    /// DlmId@0, Success u8@4 - five bytes, dumper order.</summary>
    public static byte[] BuildDlmThenSuccess(uint dlmId, bool ok)
    {
        var p = new byte[5];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        p[4] = (byte)(ok ? 1 : 0);
        return p;
    }

    /// <summary>DBS_DELETE_ITEM_RECIPE_LIST (0x2763): the one reply here with the byte FIRST -
    /// Success u8@0, DlmId@1, as both the dumper and the writer order it.</summary>
    public static byte[] BuildDbsDeleteRecipeList(bool ok, uint dlmId)
    {
        var p = new byte[5];
        p[0] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(dlmId).CopyTo(p, 1);
        return p;
    }

    // ---------------------------------------------------------------- parsers
    // Every one returns null on a short payload rather than reading past it.

    public static (uint DlmId, int OwnerDbId)? ParseLoad(byte[] p)
        => p.Length < LoadRequestSize ? null : (U32(p, 0), (int)U32(p, 4));

    public static (uint DlmId, int OwnerDbId, int RecipeId, bool Extract)? ParseLearn(byte[] p)
        => p.Length < LearnRequestSize ? null
            : (U32(p, LearnReqDlmId), (int)U32(p, LearnReqOwner), (int)U32(p, LearnReqRecipeId), p[LearnReqExtract] != 0);

    public static (uint DlmId, int OwnerDbId, int RecipeId, bool Flag)? ParseBookmark(byte[] p)
        => p.Length < BookmarkRequestSize ? null
            : (U32(p, 0), (int)U32(p, 4), (int)U32(p, BookmarkReqRecipeId), p[BookmarkReqFlag] != 0);

    public static (uint DlmId, int OwnerDbId, int Type, int DeltaPoint)? ParseStep1(byte[] p)
        => p.Length < Step1RequestSize ? null
            : (U32(p, 0), (int)U32(p, 4), (int)U32(p, Step1ReqType), (int)U32(p, Step1ReqDeltaPoint));

    public static (uint DlmId, int OwnerDbId, int SkillProfId, int SkillProfValue)? ParseStep2(byte[] p)
        => p.Length < Step2RequestSize ? null
            : (U32(p, Step2ReqDlmId), (int)U32(p, Step2ReqOwner), (int)U32(p, Step2ReqProfId), (int)U32(p, Step2ReqProfValue));

    public static (uint DlmId, int OwnerDbId, int SkillProfId, int Value)? ParseUpdateProf(byte[] p)
        => p.Length < UpdateProfRequestSize ? null
            : (U32(p, 0), (int)U32(p, 4), (int)U32(p, UpdateProfReqId), (int)U32(p, UpdateProfReqValue));

    public static (uint DlmId, int OwnerDbId)? ParseDeleteHeader(byte[] p)
        => p.Length < DeleteRequestSize ? null : (U32(p, DeleteReqDlmId), (int)U32(p, DeleteReqOwner));

    /// <summary>
    /// The u32 recipe ids behind SDB_DELETE_ITEM_RECIPE_LIST's ref. The count slot is a BYTE
    /// count - Handler_SDB_DELETE_ITEM_RECIPE_LIST reads (count - 1) / 4 + 1 ints.
    /// Everything is checked unsigned: an offset or count that does not land inside the payload,
    /// after the fixed header, yields an empty list.
    /// </summary>
    public static IReadOnlyList<int> ParseDeleteRecipeIds(byte[] p)
    {
        if (p.Length < DeleteRequestSize) return Array.Empty<int>();
        uint off = U32(p, DeleteReqIdsRef), bytes = U32(p, DeleteReqIdsRef + 4);
        if (off < 6 || bytes == 0 || bytes % 4 != 0) return Array.Empty<int>();
        uint start = off - 6;
        if (start < DeleteRequestSize || start > (uint)p.Length || bytes > (uint)p.Length - start)
            return Array.Empty<int>();
        int n = (int)Math.Min(bytes / 4, (uint)MaxListRecords);
        var ids = new int[n];
        for (int i = 0; i < n; i++) ids[i] = (int)U32(p, (int)start + 4 * i);
        return ids;
    }

    // ---------------------------------------------------------------- gathering

    /// <summary>
    /// Gathering proficiency is not a SkillProf row. It is four ints of UserData - the world
    /// blob - that the real Arbiter binds from its profMineral / profBug / profEnergy / profHerb
    /// columns when it fills the enter-world record (Arb_part_032.c:17830-17833, +0x1C8 to
    /// +0x1D4, eight bytes past money at +0x1C0), and writes back one at a time through
    /// S_UPDATE_PROF_MINERAL..HERB (0x273D-0x2740), each answered by D_UPDATE_PROF_RESULT
    /// (0x2741, [DlmId][u8 Success]). Opcodes and fields run in the same order, so
    /// kind = op - 0x273D indexes both. All four are 0 in every captured blob (nobody gathered).
    /// </summary>
    public const ushort FirstGatheringProfOp = 0x273D;
    public const int GatheringProfKinds = 4;
    public const int GatheringProfBlobOffset = 0x1C8;
    /// <summary>DlmId@0, OwnerDBID@4, Value@8 - the handler's guard is frame &gt;= 0x12.</summary>
    public const int GatheringProfRequestSize = 12;

    /// <summary>
    /// World's ProficiencyType, which is also the <c>type</c> of the client's
    /// S_PLAYER_CHANGE_PROF. DBUserProfContext::BeginTransaction picks the opcode from it:
    /// 1 herb (0x2740), 2 mineral (0x273D), 3 bug (0x273E), 4 energy (0x273F). classic_craft.log
    /// agrees from the client side: the two plant nodes (collection templates 3 and 1, frames
    /// 8022 and 8040) are followed by type 1, the ore node (template 101, frame 9272) by type 2.
    /// </summary>
    public const int ProfHerb = 1, ProfMineral = 2, ProfBug = 3, ProfEnergy = 4;

    /// <summary>The S_UPDATE_PROF_* opcode World sends for a ProficiencyType; 0 for none.</summary>
    public static ushort GatheringOpFor(int proficiencyType) => proficiencyType switch
    {
        ProfHerb => 0x2740,
        ProfMineral => 0x273D,
        ProfBug => 0x273E,
        ProfEnergy => 0x273F,
        _ => 0,
    };

    /// <summary>0 mineral, 1 bug, 2 energy, 3 herb; -1 for any other opcode.</summary>
    public static int GatheringKindOf(ushort op)
        => op >= FirstGatheringProfOp && op < FirstGatheringProfOp + GatheringProfKinds ? op - FirstGatheringProfOp : -1;

    public static (uint DlmId, int OwnerDbId, int Value)? ParseGatheringProf(byte[] p)
        => p.Length < GatheringProfRequestSize ? null : (U32(p, 0), (int)U32(p, 4), (int)U32(p, 8));

    /// <summary>
    /// Write the stored gathering proficiencies into a world blob on its way out in
    /// DBS_USER_ENTERWORLD, as the real Arbiter's column binding does. Only kinds that have a
    /// stored value are written, so a character that never gathered is sent its blob unchanged.
    /// Returns how many fields were written; a null or short blob writes nothing.
    /// </summary>
    public static int StampGatheringProfs(byte[]? blob, IReadOnlyDictionary<int, int>? profs)
    {
        if (blob == null || profs == null || blob.Length < GatheringProfBlobOffset + 4 * GatheringProfKinds) return 0;
        int n = 0;
        foreach (var (kind, value) in profs)
        {
            if ((uint)kind >= GatheringProfKinds) continue;
            BitConverter.GetBytes(value).CopyTo(blob, GatheringProfBlobOffset + 4 * kind);
            n++;
        }
        return n;
    }
}
