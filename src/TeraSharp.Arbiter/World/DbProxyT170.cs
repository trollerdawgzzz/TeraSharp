// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T170 - the last two of T165's group C that have a reason to be answered:
/// <list type="bullet">
/// <item>0x27E6 SDB_USER_CLEAR_ALL_SKILL - <b>pinned</b>: cap_clearallskill 902 -&gt; 903 (data/cap_t170_skill.bin).</item>
/// <item>0x2969 SDB_UPDATE_EVENTSYSTEM_PROGRESS - <b>decompile-derived</b> (no capture): Arbiter
/// Handler_SDB_UPDATE_EVENTSYSTEM_PROGRESS + EventSystemManager::UpdateProgressInfo /
/// GetProgressInfo, World Handler_DBS_UPDATE_EVENTSYSTEM_PROGRESS.</item>
/// </list>
/// Offsets are PAYLOAD offsets (frame - 6) unless marked frame-relative.
/// </summary>
public sealed partial class DbProxyHandlers
{
    public const ushort SDB_USER_CLEAR_ALL_SKILL = 0x27E6, DBS_USER_CLEAR_ALL_SKILL = 0x27E7;
    public const ushort SDB_UPDATE_EVENTSYSTEM_PROGRESS = 0x2969, DBS_UPDATE_EVENTSYSTEM_PROGRESS = 0x296A;

    /// <summary>DBS_USER_CLEAR_ALL_SKILL fixed part: [ref SkillLearned][ref PassiveLearned][DlmId][ok].</summary>
    public const int ClearAllSkillReplyFixed = 21;

    /// <summary>
    /// SDB_USER_CLEAR_ALL_SKILL (guard 0x0E): DlmId@0, UserDbId@4. The real Arbiter clears the
    /// user's two skill arrays (User::ClearAllSkill) and sends them back: 500 x 8 active and
    /// 40 x 8 passive - all zero, nothing survives the clear (cap_clearallskill 903, 4347 B).
    /// No such user: ok 0 and both refs empty (the handler's null-user branch).
    /// </summary>
    private bool OnUserClearAllSkill(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 4);
        bool ok = _store is null || _store.ClearAllSkills(user);
        _log.LogInformation("SDB_USER_CLEAR_ALL_SKILL: user {U} -> ok {Ok}", user, ok);
        link.SendFrame(DBS_USER_CLEAR_ALL_SKILL, BuildDbsClearAllSkill(Ep32(payload, 0), ok));
        return true;
    }

    public static byte[] BuildDbsClearAllSkill(uint dlmId, bool ok)
    {
        int active = ok ? StarterBlob.ActiveSkillSlots * StarterBlob.SkillEntrySize : 0;
        int passive = ok ? StarterBlob.PassiveSkillSlots * StarterBlob.SkillEntrySize : 0;
        const uint start = 6 + ClearAllSkillReplyFixed;
        var r = new byte[ClearAllSkillReplyFixed + active + passive];
        Put(r, 0, start); Put(r, 4, (uint)active);
        Put(r, 8, start + (uint)active); Put(r, 12, (uint)passive);
        Put(r, 16, dlmId); r[20] = (byte)(ok ? 1 : 0);
        return r;
    }

    /// <summary>Request element (frame-relative, 0x23 B): [self][next][EventId i64 @8][u8 @0x10]
    /// [UserDbId i32 @0x11][AccountDbId i64 @0x15][Value i32 @0x1D][Flag1 u8 @0x21][Flag2 u8 @0x22].</summary>
    public const int EventProgressReqElem = 0x23;
    /// <summary>Reply element (0x22 B): [self][next][EventId i64][UserDbId i32][AccountDbId i64]
    /// [Value i32][Flag1 u8][Flag2 u8].</summary>
    public const int EventProgressRepElem = 0x22;
    private const int EventProgressMaxElems = 1024;

    /// <summary>
    /// SDB_UPDATE_EVENTSYSTEM_PROGRESS (guard 0x0F, no DlmId): list [count][first]@0, Overwrite u8@8
    /// (frame 0x0E) -&gt; DBS (0x296A): the same header, then one element per update read back as
    /// stored. Decompile-derived: the value is set (spUpdateUserEventSystemProgressInfo), Flag1 is kept
    /// from the stored row unless Overwrite; UserDbId 0 AND AccountDbId 0 is the Arbiter's assert,
    /// so such a list is refused (logged, no reply - the Arbiter's failure path sends none either).
    /// The real Arbiter broadcasts the reply to every World; TeraSharp answers the asking link.
    /// The Arbiter's event-definition check (unknown EventId = no reply) is not modelled: no
    /// event sheet is loaded, every id is accepted.
    /// </summary>
    private bool OnUpdateEventSystemProgress(WorldLink link, byte[] payload)
    {
        var reply = BuildDbsUpdateEventSystemProgress(payload, _store, out int n, out string? refused);
        if (reply is null)
        {
            _log.LogWarning("SDB_UPDATE_EVENTSYSTEM_PROGRESS: {Why} - no reply", refused);
            return true;
        }
        _log.LogInformation("SDB_UPDATE_EVENTSYSTEM_PROGRESS: {N} progress row(s) stored", n);
        link.SendFrame(DBS_UPDATE_EVENTSYSTEM_PROGRESS, reply);
        return true;
    }

    public static byte[]? BuildDbsUpdateEventSystemProgress(byte[] payload, CharacterStore? store,
                                                            out int count, out string? refused)
    {
        count = 0; refused = null;
        if (payload.Length < 9) { refused = $"{payload.Length}-byte payload, want 9"; return null; }
        bool overwrite = payload[8] != 0;
        int frameLen = payload.Length + 6;

        var elems = new List<(long Ev, int User, long Acct, int Value, bool F1, bool F2)>();
        uint at = Ep32(payload, 4);
        for (int guard = 0; at != 0 && guard < EventProgressMaxElems; guard++)
        {
            if (at < 15u || (ulong)at + EventProgressReqElem > (ulong)frameLen) break;
            int p = (int)at - 6;
            if (BitConverter.ToUInt32(payload, p) != at) break;
            long ev = BitConverter.ToInt64(payload, p + 8);
            int user = BitConverter.ToInt32(payload, p + 0x11);
            long acct = BitConverter.ToInt64(payload, p + 0x15);
            if (user == 0 && acct == 0) { refused = $"event {ev}: UserDbId and AccountDbId both 0"; return null; }
            elems.Add((ev, user, acct, BitConverter.ToInt32(payload, p + 0x1D), payload[p + 0x21] != 0, payload[p + 0x22] != 0));
            uint next = BitConverter.ToUInt32(payload, p + 4);
            if (next <= at) break;
            at = next;
        }

        var r = new byte[9 + elems.Count * EventProgressRepElem];
        Put(r, 0, (uint)elems.Count);
        Put(r, 4, elems.Count == 0 ? 0u : 15u);
        r[8] = (byte)(overwrite ? 1 : 0);
        for (int i = 0; i < elems.Count; i++)
        {
            var e = elems[i];
            var row = store?.SetEventProgress(e.Ev, e.User, e.Acct, e.Value, e.F1, e.F2, overwrite)
                      ?? new CharacterStore.EventProgressRow(e.Ev, e.User, e.Acct, e.Value, e.F1, e.F2);
            int o = 9 + i * EventProgressRepElem;
            uint self = (uint)(6 + o);
            Put(r, o, self);
            Put(r, o + 4, i + 1 < elems.Count ? self + EventProgressRepElem : 0u);
            BitConverter.TryWriteBytes(r.AsSpan(o + 8, 8), row.EventId);
            Put(r, o + 0x10, (uint)row.UserId);
            BitConverter.TryWriteBytes(r.AsSpan(o + 0x14, 8), row.AccountId);
            Put(r, o + 0x1C, (uint)row.Value);
            r[o + 0x20] = (byte)(row.Flag1 ? 1 : 0);
            r[o + 0x21] = (byte)(row.Flag2 ? 1 : 0);
        }
        count = elems.Count;
        return r;
    }
}
