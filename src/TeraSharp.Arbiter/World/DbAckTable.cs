// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T150b - the generic DB-proxy ack, OPT-IN per opcode. A <see cref="Rows"/> row (PINNED) is
/// proven twice: byte-exact against a live real-Arbiter pair (data/cap_t150.bin, pinned by the
/// T150 tests) AND by World's Handler_DBS_* reading exactly the fields the row writes.
///
/// <para><b>T165, group A (<see cref="Unpinned"/>):</b> the twins whose World reader reads only
/// [DlmId][ok] - plus, where the real Arbiter writes its request atoms straight back, that atom
/// list - are answered now from both decompiles alone. Each logs once at Information the first
/// time it fires ("generic ack 0x.... NAME - capture a real pair to pin"); a capture moves it to
/// <see cref="Rows"/>. B and C (<see cref="DbAckGroups"/>) can never be a row: loading one throws.</para>
///
/// <para>T150 answered 181 opcodes this way from the decompile alone and regressed live (a
/// level-70 character showed level 1 and could not move); it was reverted. T150 answered every
/// twin whatever its World reader parsed; T165 answers only readers that parse nothing else.</para>
///
/// <para>Layouts are from the Arbiter's PDL dumpers (FUN_14016bb70 i32, FUN_14016c570 u8,
/// FUN_14016bd70 binary ref [u32 offset][u32 length]); offsets are FRAME offsets, payload =
/// frame - 6. An empty binary ref is [offset = end of frame][length 0], as the live 0x27C4 shows.
/// Row syntax: <c>OOOO&gt;RRRR</c> request and reply opcode; <c>qN</c> / <c>rN</c> fixed
/// request / reply size; <c>dA:B</c> DlmId request A, reply B; <c>oA</c> the request's owner
/// (logging); <c>kB</c> Success, always 1; <c>eB.N</c> an error field, 0; <c>bB=A</c> the
/// binary ref at request A echoed at reply B (item records get allocated ids and are applied);
/// <c>aA</c> the 856-byte item atoms behind the request ref at A, applied and NOT echoed
/// (<c>aA.568</c> for 568-byte ItemTransactionGiveTake records).</para>
///
/// <para><b>T162, the one exception to "a live pair":</b> the level-jump family has no capture,
/// and is in on request because unanswered it wedges the scroll user's DB queue. It is proven by
/// both decompiles instead: the Arbiter's Handler_SDB_INCREMENT_CHARACTER_LEVEL[_JUMP|_PERFECT_JUMP]
/// (ArbiterServer.exe.c:1268756 / 1268885 / 1269012) and World's Handler_DBS_* (WorldServer.exe.c
/// :3014069 / 3014112 / 3014155) - see the rows.</para>
/// </summary>
public static class DbAckTable
{
    public readonly record struct Copy(int Req, int Rep, int Size);
    public readonly record struct RefSlot(int Rep, int Req);

    /// <summary>Request atoms applied but not echoed: the ref's frame offset and the record size.</summary>
    public readonly record struct ApplyRef(int Req, int Stride);

    public sealed record Spec(ushort Op, ushort ReplyOp, int ReqFixed, int RepFixed, int ReqDlm, int RepDlm,
                              int ReqOwner, int[] Ok, (int Off, int Size)[] Err, RefSlot[] Refs, ApplyRef[] Applies,
                              bool Pinned = true);

    /// <summary>An echoed ref that held item records: the reply PAYLOAD offset of its slot and the
    /// record stride (856 ItemTransactionAtom or 568 ItemTransactionGiveTake).</summary>
    public readonly record struct AtomRef(int SlotPayloadOffset, int RecordSize);

    public sealed record Ack(byte[] Reply, uint DlmId, IReadOnlyList<AtomRef> Atoms, int DroppedRefs);

    private static readonly string[] Rows =
    {
        // cap_social3.log 3348 -> 3350. World (WorldServer.exe.c:3013788) reads DlmId@6, Success@10.
        "279A>279B q26 r11 d14:6 o18 k10",
        // cap_social2.log 2668 -> 2670, cap_social3.log 3627 -> 3628. World (:3011031) reads the
        // ItemBinary ref @6/@10 into the context, DlmId@0E, Success@12.
        "27C3>27C4 q27 r19 d14:14 o18 b6=6 k18",
        // cap_social3.log 1203 -> 1204 (six 568-byte records, three allocated ids). World
        // (:3015598) reads the ref @6/@10, DlmId@0E, Success@12.
        "27CD>27CE q22 r19 d14:14 o18 b6=6 k18",
        // cap_social2.log 2964 -> 2965. World (:3019749) reads DlmId@6, Success@10, ResultNum@0B.
        "27DE>27DF q19 r15 d6:6 o10 k10 e11.4",
        // T162, decompile only (no capture). World builds all three with FUN_1405f3b00 / 3db0 and
        // the 0x28d9 twin (WorldServer.exe.c:1096174): [ref @6 -> 856-byte atoms][DlmId @0E]
        // [UserDbId @12] - the item the scroll uses up. The Arbiter needs >= 0x16 bytes, finds
        // the user by @12, runs the atoms, and answers [DlmId @6][ok @0A]; it never touches the
        // level. World's handlers read exactly those two (>= 0x0B), and on success
        // ExecuteCommitSQL levels the character itself - DBLevelExpContext to the item's
        // combatItemArg1 - which reaches us as S_UPDATE_EXP_LEVEL (0x273B) like any level-up.
        "28D9>28DA q22 r11 d14:6 o18 k10 a6",
        "28DB>28DC q22 r11 d14:6 o18 k10 a6",
        "28DD>28DE q22 r11 d14:6 o18 k10 a6",
        // (T166's enchanting ops are group C: real handlers, DbProxyHandlers.ItemUpgradeSpecs - never rows.)
    };

    /// <summary>
    /// T165 group A, decompile only - status/PERSISTENCE-MAP.md T165 has the table. W: World's
    /// Handler_DBS_* (WorldServer.exe.c line) and every field it reads; A: the Arbiter's
    /// Handler_SDB_* (ArbiterServer.exe.c line). "bare": the Arbiter answers [DlmId][ok] after its
    /// SQL; "echo": it runs the request atoms (ExecTrans) and its writer is handed that same vector,
    /// so the reply is the 0x27CD shape; "apply": it runs the atoms and answers bare (T162's shape).
    /// Unread reply fields go out zero, unread refs empty.
    /// </summary>
    private static readonly string[] Unpinned =
    {
        "2719>271A q42 r19 d14:14 o18 k18 b6=6", // CONSUME_EXP_ITEM_FOR_PET W:3011089 DlmId@E ItemBinary@6 ok@12; echo A:1263588
        "271B>271C q22 r19 d14:14 o18 k18 b6=6", // USE_SERVANT_FEED W:3028300 DlmId@E ItemBinary@6 ok@12; echo A:1295897
        "271D>271E q26 r19 d14:14 o18 k18 b6=6", // USE_SERVANT_STORAGE_ITEM W:3028356 DlmId@E ItemBinary@6 ok@12; echo A:1296025
        "271F>2720 q38 r19 d14:14 o18 k18 b6=6", // SERVANT_ADVENTURE_REDUCE_TIME W:3021970 DlmId@E ItemBinary@6 ok@12; echo A:1283289
        "2730>2731 q22 r19 d14:14 o18 k18 b6=6", // GIVE_QUEST_BACKUP_ITEM W:3013587 DlmId@E OwnerTransactions@6 ok@12; echo A:1267286
        "2783>2784 q18 r19 d6:6 o10 k18", // CLEAR_QUEST W:3010904 DlmId@6 ok@12; bare A:1263057
        "2794>2795 q26 r11 d14:6 o18 k10", // USER_UPDATE_LAST_MOUNT_SKILL W:3028096 DlmId@6 ok@A; bare A:1563970
        "2796>2797 q22 r11 d6:6 o10 k10", // USER_DELETE_LAST_MOUNT_SKILL W:3026940 DlmId@6 ok@A; bare A:1562859
        "279C>279D q18 r11 d6:6 o10 k10", // GUILD_RESET_PERK W:3013829 DlmId@6 ok@A; bare A:1268072
        "279E>279F q26 r11 d6:6 o10 k10", // START_GUILD_PERK_AND_COST_GUILD_MONEY W:3022916 DlmId@6 ok@A; bare A:1285399
        "27A9>27AA q34 r11 d10:6 o14 k10", // ADD_TELEPORT_TO_POS_LIST W:3009230 DlmId@6 ok@A; bare A:1259907
        "27AB>27AC q34 r11 d10:6 o14 k10", // DELETE_TELEPORT_TO_POS_LIST W:3012718 DlmId@6 ok@A; bare A:1264930
        "27AD>27AE q38 r11 d14:6 o18 k10", // RENAME_TELEPORT_TO_POS_LIST W:3020889 DlmId@6 ok@A; bare A:1279414
        "27E8>27E9 q26 r15 d6:6 o10 k10", // GROUP_DUEL_BET_MONEY W:3013688 DlmId@6 ok@A; bare A:1267576
        "27FC>27FD q42 r19 d22:14 o26 k18 b6=6", // GROUP_DUEL_BETTING_ITEM W:3013645 DlmId@E ok@12; echo A:1267429
        "2806>2807 q18 r11 d6:6 o10 k10", // APPLY_TITLE W:3009544 DlmId@6 ok@A; bare A:1260599
        "2837>2838 q34 r11 d14:7 o18 k6", // RESET_VIP_STORE W:3021474 DlmId@7 ok@6; bare A:1282006
        "2841>2842 q22 r11 d6:7 o10 k6", // SET_INVEN_POCKET_GET_FILTER W:3022028 DlmId@7 ok@6; bare A:1283418
        "2843>2844 q26 r11 d14:7 o18 k6", // APPLY_INVEN_POCKET_SORT W:3009458 DlmId@7 ok@6; bare A:1260421
        "2845>2846 q30 r11 d18:7 o22 k6 a10", // CHANGE_POCKET_NAME_AND_COST_USER_MONEY W:3010475 DlmId@7 ok@6; apply A:1262671
        "285A>285B q26 r19 d18:14 o22 k18 b6 a10", // DO_CHANGE_GUILD_NAME W:3012948 DlmId@E ok@12; apply A:1265451
        "285C>285D q34 r11 d22:6 o26 k10 a14", // DO_CHANGE_LOOK W:3012991 DlmId@6 ok@A; apply A:1265935
        "2860>2861 q30 r11 d14:6 o18 k10", // CHANGE_FACE_CUSTOM W:3010233 DlmId@6 ok@A; bare A:1561614
        // 2870 DONT_REPEAT_TUTORIAL_SIMPLE_TIP: T191 real additive popup-count persistence.
        "287E>287F q22 r19 d14:14 o18 k18 b6=6", // CHANGE_ITEM_EXTERIOR W:3010417 DlmId@E ItemBinary@6 ok@12; echo A:1262545
        "2880>2881 q22 r19 d14:14 o18 k18 b6=6", // RESTORE_ITEM_EXTERIOR W:3021559 DlmId@E ItemBinary@6 ok@12; echo A:1282190
        "2882>2883 q22 r19 d14:14 o18 k18 b6=6", // CHANGE_ITEM_COLORING W:3010359 DlmId@E ItemBinary@6 ok@12; echo A:1262419
        "2884>2885 q22 r19 d14:14 o18 k18 b6=6", // DECREASE_ITEM_COLORING_LEFTTIME W:3012062 DlmId@E ItemBinary@6 ok@12; echo A:1264089
        "2886>2887 q22 r19 d14:14 o18 k18 b6=6", // REVIVE_FOR_DUNGEON_RETRY W:3021740 DlmId@E ItemBinary@6 ok@12; echo A:1282577
        "2888>2889 q22 r19 d14:14 o18 k18 b6=6", // TURN_ON_WORK_OBJECT W:3025007 DlmId@E OwnerTransactions@6 ok@12; echo A:1290401
        "288D>288E q34 r19 d14:14 o18 k18 b6=6", // USE_CHRONOSCROLL W:3028139 DlmId@E ok@12; echo A:1436232
        "28A3>28A4 q22 r11 d14:6 o18 k10", // DELETE_QUEST_COMPLETED W:3012620 DlmId@6 ok@A; bare A:1264821
        "28B5>28B4 q18 r11 d6:7 k6", // CHECK_SUMMON_FRIEND W:3010657 DlmId@7 ok@6; bare A:1262983
        "28BF>28C0 q47 r11 d6:6 o18 k10", // UPDATE_PREMIUM_SLOT W:3025837 DlmId@6 ok@A; bare A:1562573
        "28CD>28CE q22 r11 d14:6 o18 k10 a6.568", // CHANGE_ENCHAT_SCROLL_PASSIVE W:3010091 DlmId@6 ok@A; apply A:1261860
        "28D1>28D2 q26 r19 d14:14 o18 k18 b6=6", // USER_LEARN_SOCIAL W:3027405 DlmId@E ItemBinary@6 ok@12; echo A:1563823
        "28D3>28D4 q18 r11 d6:6 o10 k10", // USER_FORGET_SOCIAL W:3027096 DlmId@6 ok@A; bare A:1563128
        "28D5>28D6 q14 r11 d6:6 o10 k10", // USER_CLEAR_ALL_SOCIAL W:3026897 DlmId@6 ok@A; bare A:1562791
        "28D7>28D8 q22 r11 d14:6 o18 k10 a6", // INCREMENT_CHARACTER_SOCKET W:3014186 DlmId@6 ok@A; apply A:1269113
        "28DF>28E0 q22 r11 d14:6 o18 k10 a6", // COMPLETE_STORY_QUEST W:3010988 DlmId@6 ok@A; apply A:1263285
        "28FC>28FD q22 r19 d14:14 o18 k18 b6=6", // EXCHANGE_TOKEN_ITEM_TO_POINT W:3013300 DlmId@E OwnerBinary@6 ok@12; echo A:1266740
        "2904>2905 q22 r19 d14:14 o18 k18 b6=6", // SET_TOKEN_POINT_QA W:3022616 DlmId@E OwnerBinary@6 ok@12; echo A:1284309
        "290E>290F q22 r11 d6:7 o10 k6", // NEW_FATIGABILITY W:3019968 DlmId@7 ok@6; bare A:1278268
        "292C>292D q22 r19 d14:14 o18 k18 b6=6", // COLLECT_USEITEM_IN_FLOATING_CASTLE W:3010945 DlmId@E ok@12; echo A:1263159
        "2938>2939 q14 r11 d6:6 o10 k10", // RESET_DAILY_ATTENDANCE W:3021330 DlmId@6 ok@A; bare A:1281558
        "2946>2947 q26 r11 d18:6 o22 k10 a10", // REQUEST_MEGAPHONE W:3021186 DlmId@6 ok@A; apply A:1280123
        "294C>294D q34 r19 d14:14 o18 k18 b6=6", // RECEIVE_PLAYTIME_REWARD W:3020331 DlmId@E ItemBinary@6 ok@12; echo A:1279099
        "296F>2970 q18 r11 d6:6 o10 k10", // UPDATE_USERAWAKENGRADE W:3026422 DlmId@6 ok@A; bare A:1293296
        "297D>297E q18 r11 d6:6 o10 k10", // APPLY_MAXACTPOINT_ACCOUNTTRAIT W:3009501 DlmId@6 ok@A; bare A:1260514
        "297F>2980 q14 r11 d6:6 o10 k10", // DISAPPLY_MAXACTPOINT_ACCOUNTTRAIT W:3012819 DlmId@6 ok@A; bare A:1265189
        // T201 UPDATE_PURCHASE_LIMIT2983 now has real additive persistence and its native2984 ack.
    };

    private static readonly Dictionary<ushort, Spec> ByOp = Index(Rows, Unpinned);

    public static bool Covers(ushort op) => ByOp.ContainsKey(op);
    public static Spec? For(ushort op) => ByOp.TryGetValue(op, out var s) ? s : null;
    public static IReadOnlyCollection<Spec> All => ByOp.Values;

    /// <summary>
    /// The table. Throws for a row whose opcode is in group B or C (<see cref="DbAckGroups.Refused"/>)
    /// or a duplicate - so a deny-listed request cannot be answered generically, whatever is added.
    /// </summary>
    public static Dictionary<ushort, Spec> Index(IEnumerable<string> pinned, IEnumerable<string> unpinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(unpinned);
        var d = new Dictionary<ushort, Spec>();
        foreach (var (row, pin) in pinned.Select(r => (r, true)).Concat(unpinned.Select(r => (r, false))))
        {
            var s = Parse(row, pin);
            if (DbAckGroups.Refused(s.Op))
                throw new InvalidOperationException($"DbAckTable: 0x{s.Op:X4} is deny-listed (T165 group B/C) "
                    + "and must never get a generic ack: " + (DbAckGroups.DenySpec.TryGetValue(s.Op, out var why) ? why : DbAckGroups.RealHandlerSpec[s.Op]));
            if (!d.TryAdd(s.Op, s)) throw new InvalidOperationException($"DbAckTable: 0x{s.Op:X4} twice");
        }
        return d;
    }

    public static Spec Parse(string row, bool pinned = true)
    {
        ArgumentNullException.ThrowIfNull(row);
        var t = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 4 || t[0].Length != 9 || t[0][4] != '>') throw new FormatException(row);
        ushort op = ushort.Parse(t[0].AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber);
        ushort rop = ushort.Parse(t[0].AsSpan(5, 4), System.Globalization.NumberStyles.HexNumber);
        int q = 0, r = 0, qd = -1, rd = -1, owner = -1;
        var ok = new List<int>(); var err = new List<(int, int)>(); var refs = new List<RefSlot>(); var applies = new List<ApplyRef>();
        for (int i = 1; i < t.Length; i++)
        {
            string x = t[i], v = x[1..];
            switch (x[0])
            {
                case 'q': q = int.Parse(v); break;
                case 'r': r = int.Parse(v); break;
                case 'o': owner = int.Parse(v); break;
                case 'a':
                {
                    var p = v.Split('.');
                    applies.Add(new ApplyRef(int.Parse(p[0]), p.Length > 1 ? int.Parse(p[1]) : DbProxyHandlers.ItemAtomSize));
                    break;
                }
                case 'k': ok.Add(int.Parse(v)); break;
                case 'd': { var p = v.Split(':'); qd = int.Parse(p[0]); rd = int.Parse(p[1]); break; }
                case 'e': { var p = v.Split('.'); err.Add((int.Parse(p[0]), int.Parse(p[1]))); break; }
                case 'b':
                {
                    var p = v.Split('=');
                    refs.Add(new RefSlot(int.Parse(p[0]), p.Length > 1 ? int.Parse(p[1]) : -1));
                    break;
                }
                default: throw new FormatException(row);
            }
        }
        if (q < 6 || r < 6 || qd < 6 || rd < 6 || qd + 4 > q || rd + 4 > r) throw new FormatException(row);
        if (ok.Any(k => k < 6 || k >= r) || refs.Any(x => x.Rep < 6 || x.Rep + 8 > r)
            || applies.Any(x => x.Req < 6 || x.Req + 8 > q || (x.Stride != DbProxyHandlers.ItemAtomSize && x.Stride != DbProxyHandlers.ItemGiveTakeSize)))
            throw new FormatException(row);
        refs.Sort((a, b) => a.Rep.CompareTo(b.Rep));
        return new Spec(op, rop, q, r, qd, rd, owner, ok.ToArray(), err.ToArray(), refs.ToArray(), applies.ToArray(), pinned);
    }

    /// <summary>
    /// The reply for <paramref name="payload"/> (the request, frame header stripped). Never throws
    /// on a hostile payload: a field it is too short for reads as 0, and a ref pointing outside it
    /// goes back empty and is counted in <see cref="Ack.DroppedRefs"/>. With
    /// <paramref name="allocateItemId"/> null, records are echoed as they came and none are
    /// reported for applying.
    /// </summary>
    public static Ack Build(Spec s, byte[] payload, Func<int>? allocateItemId = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(payload);
        int reqFixed = s.ReqFixed - 6;
        var fx = new byte[s.RepFixed - 6];
        var tail = new List<byte>();
        var atoms = new List<AtomRef>();
        int dropped = 0;

        uint dlm = U32(payload, s.ReqDlm - 6);
        Put32(fx, s.RepDlm - 6, dlm);
        foreach (int k in s.Ok) fx[k - 6] = 1;
        foreach (var (off, size) in s.Err) Array.Clear(fx, off - 6, size);

        foreach (var rf in s.Refs)
        {
            int slot = rf.Rep - 6;
            uint at = (uint)(s.RepFixed + tail.Count);
            int stride = 0;
            byte[] data = rf.Req < 6 ? Array.Empty<byte>()
                : EchoBinary(payload, rf.Req - 6, reqFixed, allocateItemId, out stride, ref dropped);
            Put32(fx, slot, at);
            Put32(fx, slot + 4, (uint)data.Length);
            tail.AddRange(data);
            if (stride > 0) atoms.Add(new AtomRef(slot, stride));
        }

        var reply = new byte[fx.Length + tail.Count];
        fx.CopyTo(reply, 0);
        tail.CopyTo(reply, fx.Length);
        return new Ack(reply, dlm, atoms, dropped);
    }

    /// <summary>
    /// 856 or 568 when the bytes are a packed array of item records: the length divides by the
    /// stride and every record's +0 is its own index (true of every atom list in cap_newchar,
    /// cap_social2 and cap_social4, and of the 568-byte list in 0x27CD). 0 otherwise.
    /// </summary>
    public static int AtomStride(byte[] p, int start, int length)
    {
        if (start < 0 || length <= 0 || (long)start + length > p.Length) return 0;
        foreach (int rs in new[] { DbProxyHandlers.ItemAtomSize, DbProxyHandlers.ItemGiveTakeSize })
        {
            if (length % rs != 0) continue;
            bool seq = true;
            for (int i = 0; seq && i < length / rs; i++)
                seq = BitConverter.ToInt32(p, start + i * rs) == i;
            if (seq) return rs;
        }
        return 0;
    }

    private static byte[] EchoBinary(byte[] p, int slot, int minStart, Func<int>? alloc, out int stride, ref int dropped)
    {
        stride = 0;
        if ((uint)slot + 8u > (uint)p.Length) return Array.Empty<byte>();
        uint off = BitConverter.ToUInt32(p, slot), len = BitConverter.ToUInt32(p, slot + 4);
        if (len == 0) return Array.Empty<byte>();
        long start = (long)off - 6;
        if (start < minStart || start + len > p.Length) { dropped++; return Array.Empty<byte>(); }
        int st = (int)start, n = (int)len;
        int rs = AtomStride(p, st, n);
        if (rs > 0 && alloc != null)
        {
            var (bytes, _) = WarehouseHandlers.CloneAtomsWithIds(p, slot, minStart, alloc, rs);
            if (bytes.Length == n) { stride = rs; return bytes; }
        }
        var copy = new byte[n];
        Array.Copy(p, st, copy, 0, n);
        return copy;
    }

    public static uint U32(byte[] p, int at)
        => at >= 0 && (uint)at + 4u <= (uint)p.Length ? BitConverter.ToUInt32(p, at) : 0u;

    private static void Put32(byte[] b, int at, uint v) => BitConverter.TryWriteBytes(b.AsSpan(at, 4), v);
}
