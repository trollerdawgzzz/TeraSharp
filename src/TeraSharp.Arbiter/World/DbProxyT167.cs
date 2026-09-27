// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T167 - group C, slice 2: collection cards, EP pages, skill polishing and the dungeon rank
/// record, as real handlers (status/PERSISTENCE-MAP.md T167). Layouts are the Arbiter's PDL
/// dumpers and writers (ArbiterServer.exe.c) checked against World's Handler_DBS_* readers
/// (WorldServer.exe.c). Only the three login loads have captured pairs (cap_social4 329/330,
/// 446/447, 431/432 - every one an empty state) and are byte-exact; everything else is
/// DECOMPILE-DERIVED and says so where it is built. Offsets below are PAYLOAD offsets (frame - 6).
/// Every handler answers, whatever the payload length: a short frame reads as zeros.
/// T190 adds real cap_2man_b pairs for card load/preset/combine writes; see T190Cards.cs.
/// </summary>
public sealed partial class DbProxyHandlers
{
    public const ushort SDB_SKILL_POLISHING_UNLOCK_OPTION = 0x2971, DBS_SKILL_POLISHING_UNLOCK_OPTION = 0x2972;
    public const ushort SDB_SKILL_POLISHING_CHANGE_OPTION = 0x2973, DBS_SKILL_POLISHING_CHANGE_OPTION = 0x2974;
    public const ushort DBS_LOAD_SKILL_POLISHING = 0x2976;
    public const ushort SDB_SKILL_POLISHING_UPGRADE_LEVEL = 0x2977, DBS_SKILL_POLISHING_UPGRADE_LEVEL = 0x2978;
    public const ushort SDB_SKILL_POLISHING_ADD_EXP = 0x2979, DBS_SKILL_POLISHING_ADD_EXP = 0x297A;
    public const ushort DBS_RESPONSE_CARD_DATA = 0x2987;
    public const ushort SDB_CREATE_CARD_INFO = 0x298E, DBS_CREATE_CARD_INFO = 0x298F;
    public const ushort SDB_INCREASE_CARD_PRESET = 0x2990, DBS_INCREASE_CARD_PRESET = 0x2991;
    public const ushort SDB_ACTIVATE_CARD_COMBINE_LIST = 0x2992, DBS_ACTIVATE_CARD_COMBINE_LIST = 0x2993;
    public const ushort SDB_DEACTIVATE_CARD_COMBINE_LIST = 0x2994, DBS_DEACTIVATE_CARD_COMBINE_LIST = 0x2995;
    public const ushort SDB_CHANGE_CARD_PRESET = 0x2998, DBS_CHANGE_CARD_PRESET = 0x2999;
    public const ushort SDB_EXPAND_EP_PAGE = 0x299A, DBS_EXPAND_EP_PAGE = 0x299B;
    public const ushort SDB_CHANGE_EP_PAGE = 0x299C, DBS_CHANGE_EP_PAGE = 0x299D;
    public const ushort SDB_RESET_EXTRA_POINT_DATA = 0x27B5, DBS_RESET_EXTRA_POINT_DATA = 0x27B6;
    public const ushort DBS_USER_LOAD_EP_PERK = 0x27BA;
    public const ushort SDB_USER_INCREASE_EP_POINT_BY_ITEM = 0x27BF, DBS_USER_INCREASE_EP_POINT_BY_ITEM = 0x27C0;
    public const ushort SDB_UPDATE_DUNGEON_RANK_RECORD = 0x286B;

    /// <summary>Request fixed parts (frame guard - 6) of the two frames whose atoms are echoed or applied.</summary>
    public const int IncreaseCardPresetRequestHeader = 24, IncreaseCardPresetReplyHeader = 17;

    // ------------------------------------------------------------------ cards

    /// <summary>
    /// T210. Accounts whose collection was just perfected. Native
    /// Account::ResetCardCollectionBook resets the in-memory Account, so the refresh the command
    /// asks for describes THAT: preset amount 1, no presets, no combines, no claimed book rewards
    /// (cap_2man_b_client2 2705). The stored rows are untouched and the next login reports them
    /// again (cap_2man_b_client2 3634). One shot, so only that refresh sees it.
    /// </summary>
    private static readonly HashSet<long> PerfectCardRefresh = new();

    public static void MarkPerfectCardRefresh(long accountId)
    { if (accountId > 0) lock (PerfectCardRefresh) PerfectCardRefresh.Add(accountId); }

    internal static bool TakePerfectCardRefresh(long accountId)
    { lock (PerfectCardRefresh) return PerfectCardRefresh.Remove(accountId); }

    internal static void ResetPerfectCardRefreshForTests()
    { lock (PerfectCardRefresh) PerfectCardRefresh.Clear(); }

    /// <summary>SDB_REQUEST_CARD_DATA (0x2986, guard 0x16): DlmId@0, AccountDbId i64@4, UserDbId@12.</summary>
    private bool OnRequestCardData(WorldLink link, byte[] payload)
    {
        long account = Ep64(payload, 4);
        int character = Ep32i(payload, 12);
        bool perfected = TakePerfectCardRefresh(account);   // T210
        var stored = _store?.GetCardInfo(account) ?? CharacterStore.DefaultCardInfo;
        var info = perfected
            ? (stored with { PresetAmount = CharacterStore.DefaultCardInfo.PresetAmount })
            : stored;
        var reply = BuildDbsResponseCardData(Ep32(payload, 0), ok: true, info,
            perfected ? 0 : _store?.GetCardPresetIndex(character) ?? 0,
            _store?.GetAccountCards(account) ?? Array.Empty<CharacterStore.CardRow>(),
            perfected ? Array.Empty<CharacterStore.CardMountRow>()
                      : _store?.GetCardMounts(character) ?? Array.Empty<CharacterStore.CardMountRow>(),
            perfected ? Array.Empty<CharacterStore.CardCombineRow>()
                      : _store?.GetCardCombines(account) ?? Array.Empty<CharacterStore.CardCombineRow>(),
            perfected ? Array.Empty<int>() : _store?.GetCardBookRewards(account));   // T168
        link.SendFrame(DBS_RESPONSE_CARD_DATA, reply);
        return true;
    }

    /// <summary>
    /// DBS_RESPONSE_CARD_DATA (0x2987), guard 0x3A: four [count][first] lists - Cards, Presets,
    /// ActivatedCardCombineList, ReceivedCollectionBookRewards - then DlmId@32, ok@36,
    /// PresetAmount@37, PresetIndex@41, BookLevel@45, BookPoint@49. Elements follow in list order
    /// as [self][next] + data: cards (template, amount), presets (index, template), combines
    /// (id, level) at 16 bytes, rewards (id) at 12 (Handler_SDB_REQUEST_CARD_DATA :1279588; the
    /// empty form is cap_social4 447). Rewards come from T168's card_book_rewards.
    /// </summary>
    public static byte[] BuildDbsResponseCardData(uint dlmId, bool ok, CharacterStore.CardInfoRow info, int presetIndex,
        IReadOnlyList<CharacterStore.CardRow> cards, IReadOnlyList<CharacterStore.CardMountRow> presets,
        IReadOnlyList<CharacterStore.CardCombineRow> combines, IReadOnlyList<int>? rewards = null)
    {
        var w = new PdlWriter(53);
        w.Put32(32, dlmId);
        w.Put8(36, ok ? (byte)1 : (byte)0);
        w.Put32(37, (uint)info.PresetAmount);
        w.Put32(41, (uint)presetIndex);
        w.Put32(45, (uint)info.BookLevel);
        w.Put32(49, (uint)info.BookPoint);
        w.List(0, cards.Select(c => new[] { c.CardTemplateId, c.Amount }));
        w.List(8, presets.Select(m => new[] { m.PresetIndex, m.CardTemplateId }));
        w.List(16, combines.Select(c => new[] { c.CombineListId, c.Level }));
        w.List(24, (rewards ?? Array.Empty<int>()).Select(id => new[] { id }));   // T168: 12-byte elements
        return w.ToArray();
    }

    /// <summary>SDB_CHANGE_CARD_PRESET (guard 0x1A): DlmId@0, AccountDbId i64@4, UserDbId@12,
    /// ChangePresetIndex@16 -&gt; [DlmId][ok][CurrentPresetIndex] (spChangeCardPreset). Decompile-derived.</summary>
    private bool OnChangeCardPreset(WorldLink link, byte[] payload)
    {
        int character = Ep32i(payload, 12), index = Ep32i(payload, 16);
        bool ok = _store?.SetCardPresetIndex(character, index) ?? true;
        var r = new byte[9];
        Put(r, 0, Ep32(payload, 0)); r[4] = (byte)(ok ? 1 : 0); Put(r, 5, (uint)index);
        link.SendFrame(DBS_CHANGE_CARD_PRESET, r);
        return true;
    }

    /// <summary>SDB_INCREASE_CARD_PRESET (guard 0x1E): ItemBinary ref@0 (the item it uses up),
    /// DlmId@8, AccountDbId i64@12, UserDbId@20 -&gt; [ItemBinary ref@0][DlmId@8][ok@12]
    /// [CurrentPresetAmount@13] + the atoms, echoed with ids as ITEM_SIMPLE_ATOM's are (the
    /// writer is handed the request's own vector). Amount = stored + 1 (spChangeCardPresetAmount).
    /// Decompile-derived.</summary>
    private bool OnIncreaseCardPreset(WorldLink link, byte[] payload)
    {
        byte[] atoms = Array.Empty<byte>();
        int amount = CharacterStore.DefaultCardInfo.PresetAmount + 1;
        if (_store is not null)
        {
            var cloned = WarehouseHandlers.CloneAtomsWithIds(payload, 0, IncreaseCardPresetRequestHeader, _store.NextItemId);
            atoms = cloned.Atoms;
            WarehouseHandlers.Apply(_store, cloned.Parsed, _store.NextItemId, _log);
            amount = _store.IncreaseCardPresetAmount(Ep64(payload, 12));
        }
        var r = new byte[IncreaseCardPresetReplyHeader + atoms.Length];
        Put(r, 0, (uint)(6 + IncreaseCardPresetReplyHeader)); Put(r, 4, (uint)atoms.Length);
        Put(r, 8, Ep32(payload, 8)); r[12] = 1; Put(r, 13, (uint)amount);
        atoms.CopyTo(r, IncreaseCardPresetReplyHeader);
        link.SendFrame(DBS_INCREASE_CARD_PRESET, r);
        return true;
    }

    /// <summary>SDB_CREATE_CARD_INFO (guard 0x22): DlmId@0, AccountDbId i64@4, PresetAmount@12,
    /// PresetIndex@16, BookLevel@20, BookPoint@24 -&gt; the same five after the write (spCreateCardInfo).
    /// The index is per character and this frame names none, so it is echoed, not stored.
    /// Decompile-derived.</summary>
    private bool OnCreateCardInfo(WorldLink link, byte[] payload)
    {
        var row = new CharacterStore.CardInfoRow(Ep32i(payload, 12), Ep32i(payload, 20), Ep32i(payload, 24));
        _store?.SetCardInfo(Ep64(payload, 4), row);
        var r = new byte[21];
        Put(r, 0, Ep32(payload, 0)); r[4] = 1;
        Put(r, 5, (uint)row.PresetAmount); Put(r, 9, Ep32(payload, 16));
        Put(r, 13, (uint)row.BookLevel); Put(r, 17, (uint)row.BookPoint);
        link.SendFrame(DBS_CREATE_CARD_INFO, r);
        return true;
    }

    /// <summary>SDB_ACTIVATE / _DEACTIVATE_CARD_COMBINE_LIST (guard 0x1A): DlmId@0, AccountDbId
    /// i64@4, CombineListId@12, Level@16 -&gt; [DlmId][ok][CombineListId][Level]. Decompile-derived.</summary>
    private bool OnCardCombine(WorldLink link, byte[] payload, bool activate)
    {
        long account = Ep64(payload, 4);
        int id = Ep32i(payload, 12), level = Ep32i(payload, 16);
        bool ok = true;
        if (_store is not null)
        {
            if (activate) _store.SetCardCombine(account, id, level);
            else ok = _store.RemoveCardCombine(account, id);
        }
        var r = new byte[13];
        Put(r, 0, Ep32(payload, 0)); r[4] = (byte)(ok ? 1 : 0); Put(r, 5, (uint)id); Put(r, 9, (uint)level);
        link.SendFrame(activate ? DBS_ACTIVATE_CARD_COMBINE_LIST : DBS_DEACTIVATE_CARD_COMBINE_LIST, r);
        return true;
    }

    // ------------------------------------------------------------------ EP pages

    /// <summary>
    /// SDB_USER_LOAD_EP_PERK (0x27B9, guard 0x0E): DlmId@0, UserDbId@4 -&gt; DBS (0x27BA): the
    /// five pages [count 5][first]@0, DlmId@8, ok@12, UsedEp@13, PreEpLevel@17,
    /// PreEpTotalPoint@21, CurrentPage@25, MaxPage@29; then per page a 16-byte
    /// [self][next][perkCount][perkFirst] followed by its perks [self][next][perkId][level]
    /// (Handler_SDB_USER_LOAD_EP_PERK :1294987; the empty form is cap_social4 432).
    /// </summary>
    private bool OnLoadEpPerk(WorldLink link, byte[] payload)
    {
        int character = Ep32i(payload, 4);
        var pages = _store?.GetEpPages(character) ?? new CharacterStore.EpPageRow(0, 0, 0, 0, 0);
        var perks = _store?.GetEpPerks(character) ?? Array.Empty<CharacterStore.EpPerkRow>();
        link.SendFrame(DBS_USER_LOAD_EP_PERK, BuildDbsUserLoadEpPerk(Ep32(payload, 0), ok: true, pages, perks));
        return true;
    }

    public static byte[] BuildDbsUserLoadEpPerk(uint dlmId, bool ok, CharacterStore.EpPageRow pages,
        IReadOnlyList<CharacterStore.EpPerkRow> perks)
    {
        var w = new PdlWriter(33);
        w.Put32(8, dlmId);
        w.Put8(12, ok ? (byte)1 : (byte)0);
        w.Put32(13, (uint)pages.UsedEp);
        w.Put32(17, (uint)pages.PreEpLevel);
        w.Put32(21, (uint)pages.PreEpTotalPoint);
        w.Put32(25, (uint)pages.CurrentPage);
        w.Put32(29, (uint)pages.MaxPage);
        int prevPage = -1;
        for (int page = 0; page < CharacterStore.EpPageCount; page++)
        {
            int at = w.Element(0, ref prevPage, 16);
            var mine = perks.Where(p => p.Page == page).ToArray();
            int prevPerk = -1;
            foreach (var p in mine)
            {
                int e = w.Element(at - 6 + 8, ref prevPerk, 16);   // the page's own [count][first]
                w.Put32(e - 6 + 8, (uint)p.PerkId);
                w.Put32(e - 6 + 12, (uint)p.Level);
            }
        }
        return w.ToArray();
    }

    /// <summary>SDB_CHANGE_EP_PAGE (guard 0x16): DlmId@0, UserDbId@4, NewEpPage@8, NewUsedEpPoint@12
    /// -&gt; [DlmId][ok] (spChangeCurrentEpPage). Decompile-derived.</summary>
    private bool OnChangeEpPage(WorldLink link, byte[] payload)
    {
        bool ok = _store?.ChangeEpPage(Ep32i(payload, 4), Ep32i(payload, 8), Ep32i(payload, 12)) ?? true;
        link.SendFrame(DBS_CHANGE_EP_PAGE, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_EXPAND_EP_PAGE (guard 0x16): OwnerTransactions ref@0 (the item), DlmId@8,
    /// UserDbId@12 -&gt; [DlmId][ok]. MaxPage + 1 becomes the current page (spExpandEpPage); the
    /// atoms run and are not echoed. Decompile-derived.</summary>
    private bool OnExpandEpPage(WorldLink link, byte[] payload)
    {
        bool ok = true;
        if (_store is not null)
        {
            WarehouseHandlers.Apply(_store, WarehouseHandlers.ParseAtoms(payload, 0, ItemAtomSize), _store.NextItemId, _log);
            ok = _store.ExpandEpPage(Ep32i(payload, 12));
        }
        link.SendFrame(DBS_EXPAND_EP_PAGE, DlmOk(Ep32(payload, 8), ok));
        return true;
    }

    /// <summary>SDB_RESET_EXTRA_POINT_DATA (guard 0x0E): DlmId@0, OwnerDBID@4 -&gt; [DlmId][ok]
    /// (spResetExtraPointData). Decompile-derived.</summary>
    private bool OnResetExtraPointData(WorldLink link, byte[] payload)
    {
        bool ok = _store?.ResetExtraPointData(Ep32i(payload, 4)) ?? true;
        link.SendFrame(DBS_RESET_EXTRA_POINT_DATA, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_USER_INCREASE_EP_POINT_BY_ITEM (guard 0x12): DlmId@0, UserDbId@4,
    /// GainEpPoint@8 -&gt; [DlmId][ok]; the gain goes on top of the stored points. Decompile-derived.</summary>
    private bool OnIncreaseEpPointByItem(WorldLink link, byte[] payload)
    {
        bool ok = _store?.AddEpPoint(Ep32i(payload, 4), Ep32i(payload, 8)) ?? true;
        link.SendFrame(DBS_USER_INCREASE_EP_POINT_BY_ITEM, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>The perks SDB_USER_LEARN_EP_PERK (0x27BB) carries: EpPerkList ref@0 of 8-byte
    /// (perk id, level) pairs, the byte count at @4 (Handler_SDB_USER_LEARN_EP_PERK :1294797).</summary>
    public static List<(int PerkId, int Level)> ReadEpPerkList(byte[] payload)
    {
        var list = new List<(int, int)>();
        long start = (long)Ep32(payload, 0) - 6, bytes = Ep32(payload, 4);
        if (start < 0 || bytes <= 0 || start + bytes > payload.Length) return list;
        for (long o = start; o + 8 <= start + bytes; o += 8)
            list.Add((BitConverter.ToInt32(payload, (int)o), BitConverter.ToInt32(payload, (int)o + 4)));
        return list;
    }

    // ------------------------------------------------------------------ skill polishing

    /// <summary>
    /// SDB_LOAD_SKILL_POLISHING (0x2975, guard 0x0E): DlmId@0, UserDbId@4 -&gt; DBS (0x2976):
    /// OptionList [count][first]@0, LevelList [count][first]@8, DlmId@16, ok@20, Level@21,
    /// Point@25, TotalPoint@29, Exp i64@33; option elements 17 bytes [self][next][id][effect][u8
    /// applied], then level elements 16 bytes [self][next][id][effect] (Handler :1276553; the
    /// empty form is cap_social4 330, in every captured session).
    /// </summary>
    private bool OnLoadSkillPolishing(WorldLink link, byte[] payload)
    {
        int character = Ep32i(payload, 4);
        link.SendFrame(DBS_LOAD_SKILL_POLISHING, BuildDbsLoadSkillPolishing(Ep32(payload, 0), ok: true,
            _store?.GetPolishing(character) ?? new CharacterStore.PolishingRow(0, 0, 0, 0),
            _store?.GetPolishingOptions(character) ?? Array.Empty<CharacterStore.PolishingOptionRow>(),
            _store?.GetPolishingLevels(character) ?? Array.Empty<CharacterStore.PolishingLevelRow>()));
        return true;
    }

    public static byte[] BuildDbsLoadSkillPolishing(uint dlmId, bool ok, CharacterStore.PolishingRow row,
        IReadOnlyList<CharacterStore.PolishingOptionRow> options, IReadOnlyList<CharacterStore.PolishingLevelRow> levels)
    {
        var w = new PdlWriter(41);
        w.Put32(16, dlmId);
        w.Put8(20, ok ? (byte)1 : (byte)0);
        w.Put32(21, (uint)row.Level);
        w.Put32(25, (uint)row.Point);
        w.Put32(29, (uint)row.TotalPoint);
        w.Put64(33, (ulong)row.Exp);
        int prev = -1;
        foreach (var o in options)
        {
            int e = w.Element(0, ref prev, 17);
            w.Put32(e - 6 + 8, (uint)o.PolishingId);
            w.Put32(e - 6 + 12, (uint)o.EffectId);
            w.Put8(e - 6 + 16, o.Applied ? (byte)1 : (byte)0);
        }
        prev = -1;
        foreach (var l in levels)
        {
            int e = w.Element(8, ref prev, 16);
            w.Put32(e - 6 + 8, (uint)l.PolishingId);
            w.Put32(e - 6 + 12, (uint)l.EffectId);
        }
        return w.ToArray();
    }

    /// <summary>SDB_SKILL_POLISHING_ADD_EXP (guard 0x2A): OwnerTransactions ref@0, DlmId@8,
    /// UserDbId@12, Level@16, Point@20, TotalPoint@24, Exp i64@28 - World's new values, stored as
    /// they come; the atoms run -&gt; [DlmId][ok]. Decompile-derived.</summary>
    private bool OnPolishingAddExp(WorldLink link, byte[] payload)
    {
        if (_store is not null)
        {
            ApplyRequestAtoms(payload, 0);
            _store.SetPolishing(Ep32i(payload, 12), new CharacterStore.PolishingRow(
                Ep32i(payload, 16), Ep32i(payload, 20), Ep32i(payload, 24), Ep64(payload, 28)));
        }
        link.SendFrame(DBS_SKILL_POLISHING_ADD_EXP, DlmOk(Ep32(payload, 8), true));
        return true;
    }

    /// <summary>SDB_SKILL_POLISHING_CHANGE_OPTION (guard 0x1A): DlmId@0, UserDbId@4, PolishingId@8,
    /// the effect to apply @12, the one applied now @16 -&gt; [DlmId][ok]; ok 0 when the option was
    /// never unlocked (spChangeSkillPolishingOption). Decompile-derived.</summary>
    private bool OnPolishingChangeOption(WorldLink link, byte[] payload)
    {
        bool ok = _store?.ApplyPolishingOption(Ep32i(payload, 4), Ep32i(payload, 8), Ep32i(payload, 12),
                                               Ep32i(payload, 16), create: false) ?? true;
        link.SendFrame(DBS_SKILL_POLISHING_CHANGE_OPTION, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_SKILL_POLISHING_UNLOCK_OPTION (guard 0x26): OwnerTransactions ref@0, DlmId@8,
    /// UserDbId@12, PolishingId@16, UnlockEffectId@20, AppliedEffectId@24, RequiredPoint@28 -&gt;
    /// [DlmId][ok]. The new option becomes the applied one, then the points are spent; ok 0 when
    /// they are short (FUN_1403c9260). Decompile-derived.</summary>
    private bool OnPolishingUnlockOption(WorldLink link, byte[] payload)
    {
        bool ok = true;
        if (_store is not null)
        {
            int ch = Ep32i(payload, 12);
            ApplyRequestAtoms(payload, 0);
            _store.ApplyPolishingOption(ch, Ep32i(payload, 16), Ep32i(payload, 20), Ep32i(payload, 24), create: true);
            ok = _store.SpendPolishingPoint(ch, Ep32i(payload, 28));
        }
        link.SendFrame(DBS_SKILL_POLISHING_UNLOCK_OPTION, DlmOk(Ep32(payload, 8), ok));
        return true;
    }

    /// <summary>SDB_SKILL_POLISHING_UPGRADE_LEVEL (guard 0x22): OwnerTransactions ref@0, DlmId@8,
    /// UserDbId@12, PolishingId@16, EffectId@20, RequiredPoint@24 -&gt; [DlmId][ok]; the level map
    /// entry takes the effect, then the points are spent (spUpgradeSkillPolishingLevel). Decompile-derived.</summary>
    private bool OnPolishingUpgradeLevel(WorldLink link, byte[] payload)
    {
        bool ok = true;
        if (_store is not null)
        {
            int ch = Ep32i(payload, 12);
            ApplyRequestAtoms(payload, 0);
            _store.SetPolishingLevel(ch, Ep32i(payload, 16), Ep32i(payload, 20));
            ok = _store.SpendPolishingPoint(ch, Ep32i(payload, 24));
        }
        link.SendFrame(DBS_SKILL_POLISHING_UPGRADE_LEVEL, DlmOk(Ep32(payload, 8), ok));
        return true;
    }

    // ------------------------------------------------------------------ dungeon rank

    /// <summary>
    /// SDB_UPDATE_DUNGEON_RANK_RECORD (guard 0x3F): MvpName str@0, Result str@4, Binary ref@8
    /// (len@12), UserDbId@16, DungeonId@20, SeasonNum@24, TopPointRecord@28, TopTimeRecord@32,
    /// PlayDate i64@36, NewScore u8@44, NowTimePoint@45, NowKillPoint@49, NowBonusPoint@53.
    /// It carries no DlmId and is never answered: the real Arbiter sends World nothing (World's
    /// reader is a no-op) and turns it into S_DUNGEON_RANK_END_POINT (0x8B06) for the player's
    /// client, which is not built here (no capture of it). Stored, and the PvE board ranks by it.
    /// Decompile-derived.
    /// </summary>
    private bool OnUpdateDungeonRankRecord(byte[] payload)
    {
        var row = new CharacterStore.DungeonRankRow(Ep32i(payload, 16), Ep32i(payload, 20), Ep32i(payload, 24),
            Ep32i(payload, 28), Ep32i(payload, 32), Ep64(payload, 36), payload.Length > 44 && payload[44] != 0,
            Ep32i(payload, 45), Ep32i(payload, 49), Ep32i(payload, 53),
            ReadName(payload, (int)Ep32(payload, 0) - 6));
        _store?.RecordDungeonRank(row);
        _log.LogInformation("SDB_UPDATE_DUNGEON_RANK_RECORD: player {Id} dungeon {D} season {S}: {P} point(s), {T} time",
            row.CharacterId, row.DungeonId, row.Season, row.TopPoint, row.TopTime);
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private void ApplyRequestAtoms(byte[] payload, int refPayloadOffset)
    {
        if (_store is null) return;
        WarehouseHandlers.Apply(_store, WarehouseHandlers.ParseAtoms(payload, refPayloadOffset, ItemAtomSize),
            _store.NextItemId, _log);
    }

    /// <summary>[u32 DlmId][u8 ok] - the reply every T167 update frame gets.</summary>
    public static byte[] DlmOk(uint dlmId, bool ok)
    {
        var r = new byte[5];
        Put(r, 0, dlmId);
        r[4] = (byte)(ok ? 1 : 0);
        return r;
    }

    private static void Put(byte[] b, int at, uint v) => BitConverter.TryWriteBytes(b.AsSpan(at, 4), v);

    /// <summary>
    /// The PDL list writer every T167 load shares: a fixed part, then elements appended in list
    /// order, each [u32 self][u32 next] + data where self is its own FRAME offset; a list header
    /// is [u32 count][u32 first] (first = 0 when empty, as every captured empty list is).
    /// </summary>
    private sealed class PdlWriter
    {
        private readonly List<byte> _b;
        public PdlWriter(int fixedPayload) => _b = new List<byte>(new byte[fixedPayload]);
        public byte[] ToArray() => _b.ToArray();
        public void Put8(int at, byte v) => _b[at] = v;
        public void Put32(int at, uint v) { for (int i = 0; i < 4; i++) _b[at + i] = (byte)(v >> (8 * i)); }
        public void Put64(int at, ulong v) { for (int i = 0; i < 8; i++) _b[at + i] = (byte)(v >> (8 * i)); }
        private uint Get32(int at) => (uint)(_b[at] | _b[at + 1] << 8 | _b[at + 2] << 16 | _b[at + 3] << 24);

        /// <summary>Appends one element of <paramref name="size"/> bytes to the list whose
        /// [count][first] header is at payload <paramref name="header"/>; returns its frame offset.
        /// <paramref name="prev"/> is the previous element's frame offset (-1 for none).</summary>
        public int Element(int header, ref int prev, int size)
        {
            int h = header;
            int self = _b.Count + 6;
            _b.AddRange(new byte[size]);
            Put32(self - 6, (uint)self);
            Put32(h, Get32(h) + 1);
            if (prev < 0) Put32(h + 4, (uint)self);
            else Put32(prev - 6 + 4, (uint)self);
            prev = self;
            return self;
        }

        public void List(int header, IEnumerable<int[]> rows)
        {
            int prev = -1;
            foreach (var row in rows)
            {
                int e = Element(header, ref prev, 8 + 4 * row.Length);
                for (int i = 0; i < row.Length; i++) Put32(e - 6 + 8 + 4 * i, (uint)row[i]);
            }
        }
    }
}
