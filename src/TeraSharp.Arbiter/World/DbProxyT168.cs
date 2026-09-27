// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T168 - group C, slice 3: the rest of T165's C table (status/PERSISTENCE-MAP.md T168). Every
/// layout is the Arbiter's PDL dumper + writer (ArbiterServer.exe.c) against World's
/// Handler_DBS_* reader (WorldServer.exe.c); only 0x2900 and 0x2981 have captured pairs (their
/// T15-era builders, byte-exact). Everything else is DECOMPILE-DERIVED. Three kinds of answer:
/// <list type="bullet">
/// <item><b>Echo</b> (<see cref="T168Specs"/>, DbAckTable row syntax): the real Arbiter runs the
/// request's item transaction and hands its writer the same vector - the atoms come back with ids
/// and are applied here (T166's path, ItemEdits included). A few request fields are copied into
/// the reply (<see cref="T168Copies"/>).</item>
/// <item><b>Stored</b>: VIP, money, gold consumption, attendance, hidden passives, servants, book
/// rewards, guild member / name.</item>
/// <item><b>Refusal</b> (ok 0, nothing applied) where the answer is state TeraSharp does not keep
/// (partner style items, group duel returns, playtime-reward eligibility); and <b>silent</b>
/// (handled, no reply) for the two DlmId-less broker deal frames - there are no listings (T55),
/// so no deal exists to answer with.</item>
/// </list>
/// Offsets are PAYLOAD offsets (frame - 6) unless a row says otherwise.
/// </summary>
public sealed partial class DbProxyHandlers
{
    public const ushort SDB_ADD_SERVANT = 0x2717, DBS_ADD_SERVANT = 0x2718;
    public const ushort SDB_SERVANT_ADVENTURE_RECEIVE_REWARD = 0x2721;
    public const ushort SDB_SET_MONEY = 0x2746, DBS_SET_MONEY = 0x2748;
    public const ushort SDB_GET_MONEY = 0x2747, DBS_GET_MONEY = 0x2749;
    public const ushort SDB_EQUIP_PARTNER_STYLE_ITEM = 0x2750, DBS_EQUIP_PARTNER_STYLE_ITEM = 0x2751;
    public const ushort SDB_UNEQUIP_PARTNER_STYLE_ITEM = 0x2752, DBS_UNEQUIP_PARTNER_STYLE_ITEM = 0x2753;
    public const ushort SDB_ITEM_DELIVER = 0x276C, SDB_ITEM_CUSTOMIZING = 0x2772;
    public const ushort SDB_USER_LEARN_HIDE_PASSIVE_SKILL = 0x2798, DBS_USER_LEARN_HIDE_PASSIVE_SKILL = 0x2799;
    public const ushort SDB_CHANGE_GOLD_CONSUMPTION = 0x27B7, DBS_CHANGE_GOLD_CONSUMPTION = 0x27B8;
    public const ushort SDB_ADD_GUILDMEMBER2 = 0x27DB, DBS_ADD_GUILDMEMBER2 = 0x27DC;
    public const ushort SDB_DELETE_USER_ACHIEVEMENT = 0x2804;
    public const ushort SDB_GROUP_DUEL_RETURN = 0x2815, DBS_GROUP_DUEL_RETURN = 0x2816;
    public const ushort SDB_TRADE_BROKER_START_DEAL = 0x2821, SDB_TRADE_BROKER_CANCEL_DEAL = 0x2824;
    public const ushort SDB_LOAD_USER_VIP_INFO = 0x2835, DBS_LOAD_USER_VIP_INFO = 0x2836;
    public const ushort SDB_BUY_VIP_STORE_ITEM = 0x2839;
    public const ushort SDB_ADD_VIP_GAME_EXP = 0x283B, DBS_ADD_VIP_GAME_EXP = 0x283C;
    public const ushort SDB_RIGHT_ITEM_LIST = 0x2850, DBS_RIGHT_ITEM_LIST = 0x2851, SDB_USE_RIGHT_ITEM = 0x2852;
    public const ushort SDB_ASK_CHANGE_GUILD_NAME = 0x2858, DBS_ASK_CHANGE_GUILD_NAME = 0x2859;
    public const ushort SDB_CHANGE_ACCESSORY_TRANSFORM = 0x285E;
    public const ushort SDB_ITEM_POINT_STORE = 0x289B, SDB_ITEM_GUILD_STORE = 0x289D, SDB_POLITICS_POINT_STORE = 0x28AB;
    public const ushort SDB_INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT = 0x28C3, DBS_INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT = 0x28C4;
    public const ushort SDB_UPDATE_REDUCE_SERVANT_PERIOD = 0x28C7, SDB_UPDATE_REDUCE_SKILLPERIOD = 0x28CB;
    public const ushort SDB_SHARED_ACCOUNT_DATA = 0x28EE, SDB_UPDATE_ITEM_CUSTOMEXITEM = 0x28F6;
    public const ushort SDB_LOAD_ADDITIONAL_FATIGUEPOINT = 0x290A, DBS_LOAD_ADDITIONAL_FATIGUEPOINT = 0x290B;
    public const ushort SDB_ITEM_FLOATING_CASTLE_PASTS_STORE = 0x2928;
    public const ushort SDB_OPEN_FLOATING_CASTLE_PARTS_STORE = 0x292A, DBS_OPEN_FLOATING_CASTLE_PARTS_STORE = 0x292B;
    public const ushort SDB_UPDATE_CUSTOMIZING_COMBINE_RESULT = 0x292E;
    public const ushort SDB_ADMIN_USER_DAILY_ATTENDANCE = 0x2940, DBS_ADMIN_USER_DAILY_ATTENDANCE = 0x2941;
    public const ushort SDB_CHECK_PLAYTIME_REWARD = 0x294A, DBS_CHECK_PLAYTIME_REWARD = 0x294B;
    public const ushort SDB_OPEN_DUAL_OPTION = 0x2961, SDB_CHANGE_DUAL_OPTION_IDX = 0x2963;
    public const ushort SDB_ALCHEMY = 0x296B, SDB_CHANGE_EQUIPMENT_EXP = 0x296D;
    public const ushort SDB_RECEIVE_COLLECTION_BOOK_REWARD = 0x2996;
    public const ushort SDB_REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION = 0x299E;

    /// <summary>
    /// The echo family, DbAckTable row syntax (frame offsets). All but 0x28C7 / 0x28CB / 0x2804 /
    /// 0x2852 carry item records the real Arbiter runs (ExecTrans) and writes back from the same
    /// vector; those four echo a plain list (period, achievement and right-item lists).
    /// </summary>
    public static readonly IReadOnlyDictionary<ushort, DbAckTable.Spec> T168Specs = new[]
    {
        "2961>2962 q22 r19 d14:14 o18 b6=6 k18",   // OPEN_DUAL_OPTION             W:3020009 A:1278375
        "2963>2964 q22 r19 d14:14 o18 b6=6 k18",   // CHANGE_DUAL_OPTION_IDX       W:3010033 A:1261732
        "296B>296C q22 r19 d14:14 o18 b6=6 k18",   // ALCHEMY                      W:3009404 A:1260278
        "296D>296E q22 r19 d14:14 o18 b6=6 k18",   // CHANGE_EQUIPMENT_EXP (op 93) W:3010175 A:1262061
        "292E>292F q22 r19 d14:14 o18 b6=6 k18",   // UPDATE_CUSTOMIZING_COMBINE_RESULT W:3025211 A:1290844
        "28F6>28F7 q22 r19 d14:14 o18 b6=6 k18",   // UPDATE_ITEM_CUSTOMEXITEM     W:3025708 A:1292089
        "2772>2773 q26 r23 d14:14 o18 b6=6 k22",   // ITEM_CUSTOMIZING (+UpdateType) W:3014886 A:1269793
        "289B>289C q23 r19 d14:14 o18 b6=6 k18",   // ITEM_POINT_STORE (568-B)     W:3015446 A:1271144
        "28AB>28AC q22 r19 d14:14 o18 b6=6 k18",   // POLITICS_POINT_STORE (568-B) W:3020213 A:1278664
        "2928>2929 q26 r19 d14:14 o18 b6=6 k18",   // ITEM_FLOATING_CASTLE_PASTS_STORE (568-B) W:3015292 A:1270690
        "2839>283A q26 r19 d14:14 o18 b6=6 k18",   // BUY_VIP_STORE_ITEM (568-B)   W:3009788 A:1261074
        "289D>289E q38 r19 d22:14 o26 b6=14 k18",  // ITEM_GUILD_STORE (568-B)     W:3015350 A:1270829
        "276C>276D q34 r27 d22:22 o26 b6=6 b14=14 k26",   // ITEM_DELIVER owner + target W:3015060 A:1270088
        "285E>285F q30 r27 d22:22 o26 b6=6 b14=14 k26",   // CHANGE_ACCESSORY_TRANSFORM W:3009845 A:1261339
        "2721>2722 q30 r23 d14:14 o18 b6=6 k18",   // SERVANT_ADVENTURE_RECEIVE_REWARD W:3021911 A:1283160
        "2996>2997 q38 r27 d14:14 o26 b6=6 k18",   // RECEIVE_COLLECTION_BOOK_REWARD W:3020271 A:1278824
        "299E>299F q30 r31 d18:18 o14 b6=6 k30",   // GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION W:3021044 A:1436013
        "28EE>28EF q26 r15 d14:6 o18 k14 a6",      // SHARED_ACCOUNT_DATA (atoms applied) W:1643095 A:1284452
        "28C7>28C8 q30 r19 d22:14 o26 b6=14 k18",  // UPDATE_REDUCE_SERVANT_PERIOD (period list) W:3026115 A:1292604
        "28CB>28CC q30 r19 d22:14 o26 b6=14 k18",  // UPDATE_REDUCE_SKILLPERIOD (period list) W:3026173 A:1292837
        "2804>2805 q22 r19 d14:14 o18 b6=6 k18",   // DELETE_USER_ACHIEVEMENT (list) W:3012761 A:1265042
        "2852>2853 q30 r31 d14:14 o18 b6=6 k30",   // USE_RIGHT_ITEM (list + fields) W:3028244 A:1295819
    }.Select(r => DbAckTable.Parse(r)).ToDictionary(s => s.Op);

    /// <summary>Request fields the reply repeats: (request frame offset, reply frame offset), 4 bytes each.</summary>
    public static readonly IReadOnlyDictionary<ushort, (int Req, int Rep)[]> T168Copies = new Dictionary<ushort, (int, int)[]>
    {
        [SDB_ITEM_CUSTOMIZING] = new[] { (0x16, 0x12) },                                     // UpdateType
        [SDB_REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION] = new[] { (0x0E, 0x0E), (0x16, 0x16), (0x1A, 0x1A) },
        [SDB_SHARED_ACCOUNT_DATA] = new[] { (0x16, 0x0A) },                                  // SharedTaskType
        [SDB_RECEIVE_COLLECTION_BOOK_REWARD] = new[] { (0x1E, 0x13), (0x22, 0x17) },         // T190: BookType, RewardId (cap_2man_b15959)
        [SDB_USE_RIGHT_ITEM] = new[] { (0x12, 0x12), (0x16, 0x16), (0x1A, 0x1A) },           // owner, index, template
    };

    /// <summary>The echo reply for one <see cref="T168Specs"/> request (ids allocated, ItemEdits
    /// write-backs made, copies in).</summary>
    public static DbAckTable.Ack BuildT168Echo(ushort op, byte[] payload, Func<int>? allocateItemId)
    {
        var ack = DbAckTable.Build(T168Specs[op], payload, allocateItemId);
        foreach (var a in ack.Atoms)
            if (a.RecordSize == ItemAtomSize) ItemEdits.PatchReply(ack.Reply, a.SlotPayloadOffset);
        if (T168Copies.TryGetValue(op, out var copies))
            foreach (var (req, rep) in copies)
                Put(ack.Reply, rep - 6, Ep32(payload, req - 6));
        return ack;
    }

    /// <summary>T190. Arb063:18930-18949 and19059-19077: failed user/transaction validation
    /// preserves the offered atoms, does not allocate IDs, and writes Success0. Captured
    /// reward7/9 failures are cap_2man_b16035-16036 /16058-16059; their cause is not known.</summary>
    public static DbAckTable.Ack BuildCollectionBookReward(byte[] payload, bool success, Func<int>? allocateItemId = null)
    {
        var ack = success ? BuildT168Echo(SDB_RECEIVE_COLLECTION_BOOK_REWARD, payload, allocateItemId)
            : DbAckTable.Build(T168Specs[SDB_RECEIVE_COLLECTION_BOOK_REWARD], payload);
        if (!success)
            foreach (var (req, rep) in T168Copies[SDB_RECEIVE_COLLECTION_BOOK_REWARD])
                Put(ack.Reply, rep - 6, Ep32(payload, req - 6));
        ack.Reply[12] = success ? (byte)1 : (byte)0;
        return ack;
    }

    private bool OnT168Echo(WorldLink link, ushort op, byte[] payload)
    {
        var spec = T168Specs[op];
        if (op == SDB_RECEIVE_COLLECTION_BOOK_REWARD && _store != null
            && _store.GetCharacter(Ep32i(payload, 0x1A - 6)) == null)
        {
            var refused = BuildCollectionBookReward(payload, false);
            link.SendFrame(spec.ReplyOp, refused.Reply);
            return true;
        }
        var ack = BuildT168Echo(op, payload, _store is null ? null : new Func<int>(_store.NextItemId));
        int rows = 0;
        if (_store is not null)
        {
            foreach (var a in ack.Atoms)
            {
                var r = BagItems.ApplyReplyAtoms(_store, ack.Reply, a.SlotPayloadOffset, _store.NextItemId, _log, a.RecordSize);
                rows += r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
            }
            foreach (var at in spec.Applies)
            {
                var r = WarehouseHandlers.Apply(_store, WarehouseHandlers.ParseAtoms(payload, at.Req - 6, at.Stride), _store.NextItemId, _log);
                rows += r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
            }
            if (op == SDB_RECEIVE_COLLECTION_BOOK_REWARD)
                _store.AddCardBookReward(Ep64(payload, 0x12 - 6), Ep32i(payload, 0x22 - 6));
        }
        _log.LogInformation("{Op} -> 0x{Rop:X4}: player {Id} dlm {Dlm}, {Rows} item row(s)",
            DbProxyOpcodeNames.Describe(op), spec.ReplyOp, (int)DbAckTable.U32(payload, spec.ReqOwner - 6), ack.DlmId, rows);
        link.SendFrame(spec.ReplyOp, ack.Reply);
        return true;
    }

    // ------------------------------------------------------------------ money, gold, VIP

    /// <summary>SDB_SET_MONEY (GM, guard 0x16): DlmId@0, OwnerDBID@4, NewMoney i64@8 -&gt;
    /// [DlmId][ok][CurrentMoney i64] (spChangeMoney); World sets the money from the reply.</summary>
    private bool OnSetMoney(WorldLink link, byte[] payload)
    {
        int owner = Ep32i(payload, 4);
        long money = Ep64(payload, 8);
        long now = _store?.SetCharacterMoney(owner, money) ?? money;
        link.SendFrame(DBS_SET_MONEY, DlmOkI64(Ep32(payload, 0), true, now));
        return true;
    }

    /// <summary>SDB_GET_MONEY (GM, guard 0x0E): DlmId@0, OwnerDBID@4 -&gt; [DlmId][ok][CurrentMoney].
    /// World's reader is a no-op; the real Arbiter answers anyway.</summary>
    private bool OnGetMoney(WorldLink link, byte[] payload)
    {
        long money = _store?.GetCharacterMoney(Ep32i(payload, 4)) ?? 0;
        link.SendFrame(DBS_GET_MONEY, DlmOkI64(Ep32(payload, 0), true, money));
        return true;
    }

    /// <summary>SDB_CHANGE_GOLD_CONSUMPTION (guard 0x16): DlmId@0, OwnerDBID@4, Gold i64@8 -&gt; [DlmId][ok].</summary>
    private bool OnChangeGoldConsumption(WorldLink link, byte[] payload)
    {
        bool ok = _store?.SetGoldConsumption(Ep32i(payload, 4), Ep64(payload, 8)) ?? true;
        link.SendFrame(DBS_CHANGE_GOLD_CONSUMPTION, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_ADD_VIP_GAME_EXP (guard 0x12): DlmId@0, OwnerDBID@4, Delta@8 -&gt;
    /// [DlmId][ok][NewResult] - the new TOTAL, which World sets its VIP exp to.</summary>
    private bool OnAddVipGameExp(WorldLink link, byte[] payload)
    {
        int owner = Ep32i(payload, 4), delta = Ep32i(payload, 8);
        bool ok = true;
        int total = delta;
        if (_store is not null)
        {
            long account = _store.AccountOf(owner);   // T168b: 0 = no such character, ok 0
            ok = account != 0;
            total = ok ? _store.AddVipGameExp(account, delta) : 0;
        }
        var r = new byte[9];
        Put(r, 0, Ep32(payload, 0)); r[4] = (byte)(ok ? 1 : 0); Put(r, 5, (uint)total);
        link.SendFrame(DBS_ADD_VIP_GAME_EXP, r);
        return true;
    }

    /// <summary>
    /// SDB_LOAD_USER_VIP_INFO (guard 0x0E): DlmId@0, UserDbId@4 -&gt; DBS (0x2836, guard 0x2F):
    /// SlotList [count][first]@0, ok@8, DlmId@9, PubExp@13, GameExp@17, TokenAmount i64@21,
    /// LastResetTime i64@29, ResetCount@37. The VIP store's slot list is not kept: always empty.
    /// </summary>
    private bool OnLoadUserVipInfo(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 4);
        var vip = _store?.GetVipInfo(_store.AccountOf(user)) ?? new CharacterStore.VipInfoRow(0, 0, 0, 0, 0);
        link.SendFrame(DBS_LOAD_USER_VIP_INFO, BuildDbsLoadUserVipInfo(Ep32(payload, 0), vip));
        return true;
    }

    public static byte[] BuildDbsLoadUserVipInfo(uint dlmId, CharacterStore.VipInfoRow vip)
    {
        var r = new byte[41];
        r[8] = 1;
        Put(r, 9, dlmId);
        Put(r, 13, (uint)vip.PubExp);
        Put(r, 17, (uint)vip.GameExp);
        BitConverter.TryWriteBytes(r.AsSpan(21, 8), vip.TokenAmount);
        BitConverter.TryWriteBytes(r.AsSpan(29, 8), vip.LastResetTime);
        Put(r, 37, (uint)vip.ResetCount);
        return r;
    }

    // ------------------------------------------------------------------ attendance, playtime, fatigue, premium slot

    /// <summary>SDB_ADMIN_USER_DAILY_ATTENDANCE (GM, guard 0x16): DlmId@0, UserDbId@4, Period@8,
    /// LoginDay@12 -&gt; [DlmId][ok][AttendBitmap i64]; the day's bit is set in the stored bitmap.</summary>
    private bool OnAdminDailyAttendance(WorldLink link, byte[] payload)
    {
        int day = Ep32i(payload, 12);
        long bitmap = _store?.SetAttendanceDay(Ep32i(payload, 4), day) ?? (day is >= 0 and < 64 ? 1L << day : 0);
        link.SendFrame(DBS_ADMIN_USER_DAILY_ATTENDANCE, DlmOkI64(Ep32(payload, 0), true, bitmap));
        return true;
    }

    /// <summary>SDB_CHECK_PLAYTIME_REWARD (guard 0x16): DlmId@0, OwnerDBID@4, EventType@8,
    /// RewardId@12 -&gt; [DlmId][ok][EventId][ItemTid][ItemCount]. The real Arbiter asks its
    /// playtime-event manager (FUN_1406500c0), whose events TeraSharp does not load: REFUSAL -
    /// never eligible, so World never sends RECEIVE_PLAYTIME_REWARD.</summary>
    private bool OnCheckPlaytimeReward(WorldLink link, byte[] payload)
    {
        var r = new byte[17];
        Put(r, 0, Ep32(payload, 0));
        link.SendFrame(DBS_CHECK_PLAYTIME_REWARD, r);
        return true;
    }

    /// <summary>SDB_LOAD_ADDITIONAL_FATIGUEPOINT (guard 0x0E): DlmId@0, OwnerDbId@4 -&gt;
    /// [ok@0][DlmId@1][AdditionalPoint@5]. No additional points are ever granted here: 0.</summary>
    private bool OnLoadAdditionalFatiguePoint(WorldLink link, byte[] payload)
    {
        var r = new byte[9];
        r[0] = 1; Put(r, 1, Ep32(payload, 0));
        link.SendFrame(DBS_LOAD_ADDITIONAL_FATIGUEPOINT, r);
        return true;
    }

    /// <summary>SDB_INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT (guard 0x26): DlmId@0, ArbiterUser
    /// i64@4, OwnerDbId i64@12, SlotSetId@20, SlotPos@24, LimitType@28 -&gt;
    /// [DlmId][ok][SlotSetId][SlotPos]. Premium-slot cooltimes are not stored (0x28C1 is acked, not
    /// kept), so there is nothing to reset: the slot comes back as asked.</summary>
    private bool OnInitializePremiumSlotCooltime(WorldLink link, byte[] payload)
    {
        var r = new byte[13];
        Put(r, 0, Ep32(payload, 0)); r[4] = 1; Put(r, 5, Ep32(payload, 20)); Put(r, 9, Ep32(payload, 24));
        link.SendFrame(DBS_INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT, r);
        return true;
    }

    // ------------------------------------------------------------------ skills, servants, lists

    /// <summary>SDB_USER_LEARN_HIDE_PASSIVE_SKILL (guard 0x16): PassiveIdList ref@0 (i32 ids),
    /// DlmId@8, UserDbId@12 -&gt; [ResultList ref@0][DlmId@8][ok@12] + the result pairs
    /// (vector&lt;pair&lt;int,bool&gt;&gt; as memory: 8 bytes, [id][u8 learned][3 pad]).</summary>
    private bool OnLearnHidePassiveSkill(WorldLink link, byte[] payload)
    {
        var ids = new List<int>();
        long start = (long)Ep32(payload, 0) - 6, bytes = Ep32(payload, 4);
        if (start >= 16 && bytes > 0 && start + bytes <= payload.Length)
            for (long o = start; o + 4 <= start + bytes; o += 4) ids.Add(BitConverter.ToInt32(payload, (int)o));
        bool ok = _store is null || _store.CharacterExists(Ep32i(payload, 12));   // T168b
        var result = _store?.LearnHiddenPassives(Ep32i(payload, 12), ids) ?? ids.Select(i => (i, true)).ToList();
        var r = new byte[13 + result.Count * 8];
        Put(r, 0, 19); Put(r, 4, (uint)(result.Count * 8)); Put(r, 8, Ep32(payload, 8)); r[12] = (byte)(ok ? 1 : 0);
        for (int i = 0; i < result.Count; i++)
        {
            Put(r, 13 + 8 * i, (uint)result[i].Item1);
            r[17 + 8 * i] = (byte)(result[i].Item2 ? 1 : 0);
        }
        link.SendFrame(DBS_USER_LEARN_HIDE_PASSIVE_SKILL, r);
        return true;
    }

    /// <summary>
    /// SDB_ADD_SERVANT (guard 0x3A): ServantName str@0, ConditionalSkillIdList ref@4,
    /// ActiveAbilityIdList ref@12, ItemBinary ref@20, DlmId@28, UserDbId@32, Type@36,
    /// TemplateId@40, Energy@44, Period@48 -&gt; DBS (0x2718, guard 0x5B): ServantName str@16,
    /// ItemBinary ref@20 (echoed, applied), ServantPeriodList ref@28 (empty), DlmId@36, ok@40,
    /// ServantDbId i64@41, Type@49, TemplateId@53, Level@61 (1), BuffGrade@69, the first three
    /// conditional skills @73..; World reads the ref pair, the period list, DlmId and ok. The
    /// servant row is stored; the servant LOADS are still the replay table's.
    /// </summary>
    private bool OnAddServant(WorldLink link, byte[] payload)
    {
        const int reqFixed = 52, repFixed = 85;
        string name = ReadName(payload, (int)Ep32(payload, 0) - 6);
        int user = Ep32i(payload, 32), type = Ep32i(payload, 36), template = Ep32i(payload, 40);
        long servantId = _store?.AddServant(user, type, template, name, Ep32i(payload, 44), Ep32i(payload, 48)) ?? 1;
        bool ok = servantId != 0;   // T168b: 0 = no such character - refused, nothing applied
        byte[] atoms = Array.Empty<byte>();
        if (_store is not null && ok)
        {
            var cloned = WarehouseHandlers.CloneAtomsWithIds(payload, 20, reqFixed, _store.NextItemId);
            atoms = cloned.Atoms;
            WarehouseHandlers.Apply(_store, cloned.Parsed, _store.NextItemId, _log);
        }
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        var r = new byte[repFixed + nameBytes.Length + atoms.Length];
        Put(r, 16, (uint)(6 + repFixed));                                   // name
        Put(r, 20, (uint)(6 + repFixed + nameBytes.Length)); Put(r, 24, (uint)atoms.Length);
        Put(r, 28, (uint)(6 + r.Length)); Put(r, 32, 0);                    // empty period list
        Put(r, 36, Ep32(payload, 28)); r[40] = (byte)(ok ? 1 : 0);
        BitConverter.TryWriteBytes(r.AsSpan(41, 8), servantId);
        Put(r, 49, (uint)type); Put(r, 53, (uint)template); Put(r, 61, 1);
        var skills = ReadI32List(payload, 4);
        for (int i = 0; i < Math.Min(3, skills.Count); i++) Put(r, 73 + 4 * i, (uint)skills[i]);
        nameBytes.CopyTo(r, repFixed);
        atoms.CopyTo(r, repFixed + nameBytes.Length);
        link.SendFrame(DBS_ADD_SERVANT, r);
        return true;
    }

    /// <summary>SDB_RIGHT_ITEM_LIST (guard 0x0E): DlmId@0, OwnerDbId@4 -&gt; [RightItemList ref@0]
    /// [DlmId@8] - no rights are kept, so the list is empty.</summary>
    private bool OnRightItemList(WorldLink link, byte[] payload)
    {
        var r = new byte[12];
        Put(r, 0, 18); Put(r, 8, Ep32(payload, 0));
        link.SendFrame(DBS_RIGHT_ITEM_LIST, r);
        return true;
    }

    // ------------------------------------------------------------------ guild

    /// <summary>SDB_ADD_GUILDMEMBER2 (guard 0x1A): DlmId@0, GuildDbId@4, AddeeDbId@8,
    /// GuildGroupId@12, InvitorDbId@16 -&gt; [DlmId][ok] (spAddGuildMember); ok 0 when the
    /// character is already in a guild or unknown.</summary>
    private bool OnAddGuildMember2(WorldLink link, byte[] payload)
    {
        // T168b: guild_members.guild_id REFERENCES guilds - an unknown guild threw
        // "FOREIGN KEY constraint failed" and closed the World link. Refuse it (ok 0) instead.
        bool ok = true;
        if (payload.Length < 16)
        {
            _log.LogWarning("SDB_ADD_GUILDMEMBER2: {N}-byte payload, want 16 - refused (ok 0)", payload.Length);
            ok = false;
        }
        else if (_store is not null)
        {
            int guild = Ep32i(payload, 4);
            var c = _store.GetCharacter(Ep32i(payload, 8));
            if (_store.GetGuild(guild) is null)
            {
                _log.LogWarning("SDB_ADD_GUILDMEMBER2: no guild {G} - refused (ok 0)", guild);
                ok = false;
            }
            else
                ok = c is not null && _store.AddGuildMember(guild, c.Id, c.Name, c.Race, c.Class, c.Gender,
                                                            c.Level, c.AccountId, Ep32i(payload, 12)) != 0;
        }
        link.SendFrame(DBS_ADD_GUILDMEMBER2, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_ASK_CHANGE_GUILD_NAME (guard 0x12): NewName str@0, DlmId@4, UserDbId@8 -&gt;
    /// [DlmId][ok][ErrorType]: ok 1 / 0 when the name is free, ok 0 / 1 when it is taken (the
    /// error code is ours - no capture names it).</summary>
    private bool OnAskChangeGuildName(WorldLink link, byte[] payload)
    {
        string name = ReadName(payload, (int)Ep32(payload, 0) - 6);
        bool taken = name.Length == 0 || (_store?.GuildNameTaken(name) ?? false);
        var r = new byte[9];
        Put(r, 0, Ep32(payload, 4)); r[4] = (byte)(taken ? 0 : 1); Put(r, 5, taken ? 1u : 0u);
        link.SendFrame(DBS_ASK_CHANGE_GUILD_NAME, r);
        return true;
    }

    // ------------------------------------------------------------------ refusals and silent ones

    /// <summary>REFUSALS, decompile-derived shapes, ok 0 and nothing applied: the partner style
    /// items (StoreBinary ref@0 empty, DlmId@8, ok@12, Error@13 [+ WareCommision i64@17]), the
    /// group duel return (ItemBinary ref@0 empty, DlmId@8, ok@12, ErrorCode@13, SentAllByParcel@17)
    /// and the floating-castle parts store (PartsItemList [count][first]@0, DlmId@8, ok@12 = 1,
    /// empty). TeraSharp keeps no partner, group duel or floating-castle state.</summary>
    private bool OnT168Refusal(WorldLink link, ushort op, byte[] payload)
    {
        (ushort rop, int size, bool ok) = op switch
        {
            SDB_EQUIP_PARTNER_STYLE_ITEM => (DBS_EQUIP_PARTNER_STYLE_ITEM, 17, false),
            SDB_UNEQUIP_PARTNER_STYLE_ITEM => (DBS_UNEQUIP_PARTNER_STYLE_ITEM, 25, false),
            SDB_GROUP_DUEL_RETURN => (DBS_GROUP_DUEL_RETURN, 18, false),
            _ => (DBS_OPEN_FLOATING_CASTLE_PARTS_STORE, 13, true),
        };
        var r = new byte[size];
        if (op != SDB_OPEN_FLOATING_CASTLE_PARTS_STORE) Put(r, 0, (uint)(6 + size));   // empty ref
        Put(r, 8, Ep32(payload, op == SDB_OPEN_FLOATING_CASTLE_PARTS_STORE ? 0 : op == SDB_GROUP_DUEL_RETURN ? 16 : 8));
        r[12] = (byte)(ok ? 1 : 0);
        link.SendFrame(rop, r);
        return true;
    }

    /// <summary>SDB_TRADE_BROKER_START_DEAL / _CANCEL_DEAL: no DlmId. The real Arbiter hands them to
    /// its broker manager, which answers from the listing; T55 refuses every registration, so no
    /// listing and no deal exist here - handled, logged, not answered.</summary>
    private bool OnBrokerDeal(ushort op, byte[] payload)
    {
        _log.LogInformation("{Op}: {A} / {B} / {C} - no broker listings, nothing to answer",
            DbProxyOpcodeNames.Describe(op), Ep32i(payload, 0), Ep32i(payload, 4), Ep32i(payload, 8));
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] DlmOkI64(uint dlmId, bool ok, long value)
    {
        var r = new byte[13];
        Put(r, 0, dlmId); r[4] = (byte)(ok ? 1 : 0);
        BitConverter.TryWriteBytes(r.AsSpan(5, 8), value);
        return r;
    }

    private static List<int> ReadI32List(byte[] payload, int refAt)
    {
        var list = new List<int>();
        long start = (long)Ep32(payload, refAt) - 6, bytes = Ep32(payload, refAt + 4);
        if (start < 0 || bytes <= 0 || start + bytes > payload.Length) return list;
        for (long o = start; o + 4 <= start + bytes; o += 4) list.Add(BitConverter.ToInt32(payload, (int)o));
        return list;
    }
}
