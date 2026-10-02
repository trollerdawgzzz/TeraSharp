// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T234 - "Receive all" left every reward mail in the mailbox, listed as received.
//
// The claim worked after T202: the attachments landed, the gold was paid, the row was marked
// is_recved. Nothing then removed the row, so the next SDB_LIST_PARCEL served it again with
// status 2 forever. T228 D's "four reward mails on every login" is that, not a re-grant:
// arbiter-t228d.log records zero parcel writes across two logins, and terasharp.db holds six
// @2051 rows for character 13 and seven for character 9, every one parcel_type 102 is_recved 1.
//
// Retail is no better, pinned three ways - cap_final2b 9987 vs 10063 and 28054 vs 28113 list the
// same parcels before and after a collect, there is not one 0x2811 frame in that capture, and in
// WorldServer the only writer of 0x2811 (FUN_140cab160, via DeleteParcelContext) is reached only
// from User::Handler_C_DELETE_PARCEL. The delete is the player's Delete button.
//
// So delete-on-collect is OURS: system parcels only, on by default, one switch to turn it off.
// status/MAIL-WAREHOUSE.md section 15 has the full evidence. These tests cover our behaviour and
// the retail behaviour the switch restores; no capture is needed for either.
// =============================================================================================
public static partial class Tests
{
    /// <summary>Sets <c>TERASHARP_PARCEL_DELETE_ON_COLLECT</c> for one test and puts it back.</summary>
    private sealed class T234Switch : IDisposable
    {
        private readonly string? _old;
        internal T234Switch(string? value)
        {
            _old = Environment.GetEnvironmentVariable(ParcelDbHandlers.DeleteOnCollectVariable);
            Environment.SetEnvironmentVariable(ParcelDbHandlers.DeleteOnCollectVariable, value);
        }
        public void Dispose()
            => Environment.SetEnvironmentVariable(ParcelDbHandlers.DeleteOnCollectVariable, _old);
    }

    /// <summary>
    /// A collect step-2 atom block: one op-7 insert for the attachment and the op-37 marker that
    /// names the parcel (T170, parcel id at atom +0x278). The same two atoms cap_final2b 28088
    /// carries, built rather than replayed so this file needs no capture.
    /// </summary>
    static byte[] T234Atoms(int owner, int parcelId, int template, long amount, uint slot = 14)
    {
        int size = DbProxyHandlers.ItemAtomSize;
        var a = new byte[size * 2];
        BitConverter.GetBytes(WarehouseHandlers.TsInsertItem).CopyTo(a, WarehouseHandlers.AtomOp);
        BitConverter.GetBytes(0L).CopyTo(a, WarehouseHandlers.AtomItemDbId);
        BitConverter.GetBytes(template).CopyTo(a, WarehouseHandlers.AtomTemplateId);
        BitConverter.GetBytes((long)owner).CopyTo(a, WarehouseHandlers.AtomDstOwner);
        BitConverter.GetBytes(0u).CopyTo(a, WarehouseHandlers.AtomDstInven);
        BitConverter.GetBytes(slot).CopyTo(a, WarehouseHandlers.AtomDstSlot);
        BitConverter.GetBytes(amount).CopyTo(a, WarehouseHandlers.AtomDelta);

        BitConverter.GetBytes(WarehouseHandlers.TsRecvParcel).CopyTo(a, size + WarehouseHandlers.AtomOp);
        BitConverter.GetBytes(parcelId).CopyTo(a, size + WarehouseHandlers.AtomRecvParcelId);
        return a;
    }

    /// <summary>An SDB_LIST_PARCEL request: the inbox of one user unless ViewType says otherwise.</summary>
    static byte[] T234ListRequest(uint dlmId, int userDbId, uint viewType = 0)
    {
        var p = new byte[ParcelDbHandlers.ListRequestSize];
        BitConverter.GetBytes(dlmId).CopyTo(p, ParcelDbHandlers.ListReqDlmId);
        BitConverter.GetBytes(userDbId).CopyTo(p, ParcelDbHandlers.ListReqUserDbId);
        BitConverter.GetBytes(viewType).CopyTo(p, ParcelDbHandlers.ListReqViewType);
        return p;
    }

    /// <summary>The wire status a listing would report for a parcel: 0 unread, 1 read, 2 claimed.</summary>
    static int T234WireStatus(CharacterStore store, int parcelId)
        => BitConverter.ToInt32(
            ParcelDbHandlers.ServedParcelRecord(store, store.GetParcel(parcelId)!, full: false),
            ParcelDbHandlers.ParcelDataIsRead);

    [Test] public static void T234_a_collected_system_reward_leaves_the_mailbox_and_keeps_its_reward()
    {
        using var store = T45Store();
        using var deleteOnCollect = new T234Switch("1");

        int parcel = T202SystemParcel(store, 1, "t1", money: 700, (202090, 1));
        Hex.True(store.GetParcelsFor(1).Count == 1, "the reward is in the inbox");

        var request = T202ExRequest(dlmId: 983, step: 2, owner: 1, isAll: true,
                                    atoms: T234Atoms(owner: 1, parcel, 202090, 1));
        var (op, body) = RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, store);
        Hex.True(op == DbProxyHandlers.DBS_RECV_PARCEL_EX, $"reply opcode 0x{op:X4}");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspStep) == 2
                 && BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspNoParcel) == 1
                 && body[ParcelDbHandlers.RecvExRspSuccess] == 1,
                 "step 2 still reports the one parcel it committed");

        // The row is gone - not listed as received, gone.
        Hex.True(store.GetParcel(parcel) is null, "the row is removed, not left at status 2");
        Hex.True(store.GetParcelsFor(1).Count == 0, "and the inbox is empty");
        ParcelDbHandlers.BuildParcelList(store, 1, 0u, out uint listed, out _);
        Hex.True(listed == 0, $"the next listing has nothing to serve, got {listed}");

        // The claim itself is untouched: the delete must not undo what step 2 just did.
        var rows = store.GetInventoryItems(1);
        Hex.True(rows.Count == 1 && rows[0].TemplateId == 202090 && rows[0].ItemDbId > 0
                 && rows[0].InvenType == 0 && rows[0].Slot == 14,
                 "the attachment is a real bag row at the slot the atom named: "
                 + string.Join(", ", rows.Select(r => $"{r.TemplateId}@{r.InvenType}:{r.Slot}")));
        Hex.True(store.GetCharacter(1)!.Money == 700,
                 $"and the attached gold is still paid, got {store.GetCharacter(1)!.Money}");

        // The single-parcel path does the same thing, and its reply still carries the record it
        // built before the row went (SDB_RECV_PARCEL answers with the ParcelData, T74).
        int second = T202SystemParcel(store, 1, "t1", money: 0, (90089, 1));
        var single = new byte[ParcelDbHandlers.RecvRequestSize + DbProxyHandlers.ItemAtomSize * 2];
        BitConverter.GetBytes((uint)(6 + ParcelDbHandlers.RecvRequestSize)).CopyTo(single, ParcelDbHandlers.RecvReqTransListRef);
        BitConverter.GetBytes((uint)(DbProxyHandlers.ItemAtomSize * 2)).CopyTo(single, ParcelDbHandlers.RecvReqTransListRef + 4);
        BitConverter.GetBytes(1041u).CopyTo(single, ParcelDbHandlers.RecvReqDlmId);
        BitConverter.GetBytes(2u).CopyTo(single, ParcelDbHandlers.RecvReqStep);
        BitConverter.GetBytes(second).CopyTo(single, ParcelDbHandlers.RecvReqParcelId);
        T234Atoms(owner: 1, second, 90089, 1, slot: 15).CopyTo(single, ParcelDbHandlers.RecvRequestSize);

        var (op2, body2) = RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL, single, store);
        Hex.True(op2 == DbProxyHandlers.DBS_RECV_PARCEL && body2[ParcelDbHandlers.RecvRspSuccess] == 1,
                 "the single-parcel collect still succeeds");
        Hex.True(BitConverter.ToUInt32(body2, ParcelDbHandlers.RecvRspParcelDataRef + 4) > 0,
                 "and its reply still carries the parcel record, built before the row was dropped");
        Hex.True(store.GetParcel(second) is null && store.CountInventoryItems(1) == 2,
                 "row gone, second attachment kept");

        // Re-running a commit against a deleted row claims nothing and pays nothing. (It still
        // re-applies the atoms - a replayed op-7 gets a fresh id, same as T202 - so the money is
        // the assertion that matters, not the item count.)
        RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, store);
        Hex.True(store.GetCharacter(1)!.Money == 700 && store.GetParcelsFor(1).Count == 0,
                 "a repeated commit against a deleted row pays nothing and lists nothing");
    }

    [Test] public static void T234_the_switch_off_restores_retails_received_row()
    {
        using var store = T45Store();
        using var deleteOnCollect = new T234Switch("0");

        Hex.True(!ParcelDbHandlers.DeleteSystemParcelOnCollect, "0 means off");
        Hex.True(!ParcelDbHandlers.ShouldDeleteOnCollect(
                     new CharacterStore.ParcelRow(1, 0, "@Achievement:6913", 1, "@2051", "", 0,
                                                  ParcelDbHandlers.ParcelTypeSystem, 0, true, true)),
                 "and no row is a candidate while it is off");

        int parcel = T202SystemParcel(store, 1, "t1", money: 0, (202090, 1));
        var request = T202ExRequest(dlmId: 984, step: 2, owner: 1, isAll: true,
                                    atoms: T234Atoms(owner: 1, parcel, 202090, 1));
        RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, store);

        // Retail's behaviour, which is what the switch buys back: the row survives the collect
        // and the listing reports status 2 - cap_final2b 28054 -> 28068 -> 28113, 0 -> 1 -> 2.
        Hex.True(store.GetParcel(parcel) is { IsRecved: true }, "the row survives, marked collected");
        Hex.True(T234WireStatus(store, parcel) == 2, "and lists as status 2, attachments claimed");

        // Nor does the listing sweep while it is off, however many claimed rows have piled up.
        int old = T202SystemParcel(store, 1, "t1", money: 0, (90089, 1));
        store.SetParcelRecved(old, 1);
        var (op, body) = RunHandler1(DbProxyHandlers.SDB_LIST_PARCEL, T234ListRequest(985, 1), store);
        Hex.True(op == DbProxyHandlers.DBS_LIST_PARCEL
                 && BitConverter.ToUInt32(body, ParcelDbHandlers.ListRspParcelCount) == 2,
                 "both claimed rows are still listed");
        Hex.True(store.GetParcelsFor(1).Count == 2, "and both are still in the table");
    }

    [Test] public static void T234_the_listing_sweeps_the_backlog_and_leaves_everything_else()
    {
        using var store = T45Store();
        using var deleteOnCollect = new T234Switch("1");

        // What an earlier build left behind: claimed system rewards that re-listed every login.
        var stale = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            int id = T202SystemParcel(store, 1, "t1", money: 0, (201100, i + 1));
            store.SetParcelRecved(id, 1);
            stale.Add(id);
        }
        int unclaimed = T202SystemParcel(store, 1, "t1", money: 0, (202090, 1));
        int fromAPlayer = store.CreateParcel(2, "t2", 1, "hi", "body", 100, 1);
        store.SetParcelRecved(fromAPlayer, 1);
        int othersInbox = T202SystemParcel(store, 2, "t2", money: 0, (201100, 1));
        store.SetParcelRecved(othersInbox, 2);
        Hex.True(store.GetParcelsFor(1).Count == 5, "five rows in character 1's inbox to start");

        var (op, body) = RunHandler1(DbProxyHandlers.SDB_LIST_PARCEL, T234ListRequest(986, 1), store);
        Hex.True(op == DbProxyHandlers.DBS_LIST_PARCEL, $"reply opcode 0x{op:X4}");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.ListRspParcelCount) == 2,
                 $"two rows left to list, got {BitConverter.ToUInt32(body, ParcelDbHandlers.ListRspParcelCount)}");
        Hex.True(body.Length == ParcelDbHandlers.ListReplyHeader + 2 * ParcelDbHandlers.ParcelDataNoMsgSize,
                 $"and the body is two 0x9e8 records, got {body.Length} B");

        Hex.True(stale.All(id => store.GetParcel(id) is null), "every claimed reward is gone");
        Hex.True(store.GetParcelItems(stale[0]).Count == 0, "its attachment rows went with it");
        Hex.True(store.GetParcel(unclaimed) is { IsRecved: false }, "the UNCLAIMED reward is untouched");
        Hex.True(store.GetParcel(fromAPlayer) is { IsRecved: true },
                 "and a player's mail keeps retail behaviour - it waits for C_DELETE_PARCEL");

        // The sweep is per receiver and per view: another character's backlog is not ours to
        // clear, and the Sent view (any non-zero ViewType, T202) never sweeps at all.
        Hex.True(store.GetParcel(othersInbox) is { IsRecved: true }, "character 2's row is left alone");
        int stillClaimed = T202SystemParcel(store, 1, "t1", money: 0, (201100, 1));
        store.SetParcelRecved(stillClaimed, 1);
        RunHandler1(DbProxyHandlers.SDB_LIST_PARCEL, T234ListRequest(987, 1, viewType: 0xFFFFFFFFu), store);
        Hex.True(store.GetParcel(stillClaimed) is { IsRecved: true }, "a Sent listing sweeps nothing");

        // Default-on is the whole point of T234, so say it - but only where no teras.json could
        // be answering for the variable instead.
        using (new T234Switch(null))
            if (TeraSharp.Arbiter.TerasConfig.LoadedPath is null)
                Hex.True(ParcelDbHandlers.DeleteSystemParcelOnCollect,
                         "unset means on: a reward mail the player cannot clear is worse than the divergence");
    }
    // =========================================================================================
    // T234b - the badge. S_PARCEL_READ_RECV_STATUS (0xF26E) is what the mail icon's number is
    // made of, and the real Arbiter pushes it itself from every SDB handler that moves a parcel
    // (six call sites; ParcelHandlers.PushReadRecvStatus lists them). We pushed it only at login
    // and from the two client-facing handlers, so World's cached count stood until the next
    // login. T234 made that visible: "Receive all" claims without reading, so the rows kept
    // is_read = 0 and an emptied mailbox still read "2".
    // =========================================================================================

    /// <summary>The 13-byte badge frame: <c>[u16 13][u16 0xF26E][u32 unread][u32 readUnclaimed][u8]</c>.</summary>
    static byte[] T234bBadge(uint unread, uint readUnclaimed)
        => ParcelHandlers.BuildReadRecvStatus(unread, readUnclaimed);

    /// <summary>An SDB_DELETE_PARCEL request: one id list, a user, and the Sent-box flag.</summary>
    static byte[] T234bDeleteRequest(uint dlmId, int userDbId, bool isSendParcel, params int[] ids)
    {
        var p = new byte[ParcelDbHandlers.DeleteRequestSize + ids.Length * 4];
        BitConverter.GetBytes((uint)(6 + ParcelDbHandlers.DeleteRequestSize)).CopyTo(p, ParcelDbHandlers.DeleteReqDelListRef);
        BitConverter.GetBytes((uint)(ids.Length * 4)).CopyTo(p, ParcelDbHandlers.DeleteReqDelListRef + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, ParcelDbHandlers.DeleteReqDlmId);
        BitConverter.GetBytes(userDbId).CopyTo(p, ParcelDbHandlers.DeleteReqUserDbId);
        p[ParcelDbHandlers.DeleteReqIsSendParcel] = (byte)(isSendParcel ? 1 : 0);
        for (int i = 0; i < ids.Length; i++) BitConverter.GetBytes(ids[i]).CopyTo(p, ParcelDbHandlers.DeleteRequestSize + i * 4);
        return p;
    }

    [Test] public static void T234b_the_badge_frame_is_the_pair_that_brackets_a_real_collect()
    {
        // cap_social2's client tap, around the one collect it holds: 1707 opens parcel 4, 1709 is
        // the badge at readUnclaimed 2, 1715 collects it, 1716 is the badge at 1 - and only then
        // 1717 S_RECV_PARCEL. Two u32 counters and the trailing u8 the def does not declare.
        Hex.Eq(T234bBadge(0, 2), Convert.FromHexString("0D006EF2000000000200000000"),
               "cap_social2 client 1709: 0 unread, 2 read-and-unclaimed");
        Hex.Eq(T234bBadge(0, 1), Convert.FromHexString("0D006EF2000000000100000000"),
               "cap_social2 client 1716: the claim took one off readUnclaimed");
        Hex.True(T234bBadge(0, 0).Length == ParcelHandlers.ReadRecvStatusFrameSize,
                 "13 bytes, not the 12 the def would give");

        // And the counters are the store's, read the way the capture pair implies: opening sets
        // is_read, claiming sets is_recved, and a claimed row counts in neither column.
        using var store = T45Store();
        int unopened = store.CreateParcel(2, "t2", 1, "a", "", 0, 1);
        int opened = store.CreateParcel(2, "t2", 1, "b", "", 0, 1);
        store.SetParcelRead(opened, 1);
        Hex.True(store.GetParcelCounts(1) == (1, 1), $"one unread, one read-unclaimed, got {store.GetParcelCounts(1)}");
        store.SetParcelRecved(opened, 1);
        Hex.True(store.GetParcelCounts(1) == (1, 0), $"the claim empties readUnclaimed, got {store.GetParcelCounts(1)}");
        Hex.True(unopened > 0, "and the unopened one is still unread");
    }

    [Test] public static void T234b_a_receive_all_that_clears_the_rows_pushes_the_badge_down()
    {
        using var e = new T201UtilityEnvironment();
        using var deleteOnCollect = new T234Switch("1");

        // Two reward mails, neither opened - which is the whole point: "Receive all" never sets
        // is_read, so both count as unread and the badge says 2.
        int p1 = T202SystemParcel(e.Store, e.CallerId, "Utility", money: 0, (202090, 1));
        int p2 = T202SystemParcel(e.Store, e.CallerId, "Utility", money: 0, (90089, 1));
        Hex.True(e.Store.GetParcelCounts(e.CallerId) == (2, 0),
                 $"two unread rewards, got {e.Store.GetParcelCounts(e.CallerId)}");
        Hex.True(e.Caller.Available == 0 && e.Target.Available == 0, "nothing pending before the collect");

        var request = T202ExRequest(dlmId: 983, step: 2, owner: e.CallerId, isAll: true,
                                    atoms: null, parcelIds: new[] { p1, p2 });
        var (op, body) = RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, e.Store);
        Hex.True(op == DbProxyHandlers.DBS_RECV_PARCEL_EX
                 && BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspNoParcel) == 2,
                 "both parcels committed");
        Hex.True(e.Store.GetParcel(p1) is null && e.Store.GetParcel(p2) is null, "and both rows are gone (T234)");

        // One push, for the one receiver, carrying the count the DB now holds - not the 2 the
        // client was still showing.
        Hex.Eq(e.Caller.Frame(), T234bBadge(0, 0), "the badge drops to the real count, immediately");
        Hex.True(e.Caller.Available == 0, "exactly one push - the badge set is the owner, once");
        Hex.True(e.Target.Available == 0, "and nobody else is told");

        // With the switch off the rows survive, so the same push reports the count they still
        // make: retail keeps a claimed row, and an unopened one still reads as unread.
        using (new T234Switch("0"))
        {
            int p3 = T202SystemParcel(e.Store, e.CallerId, "Utility", money: 0, (201100, 1));
            var again = T202ExRequest(dlmId: 984, step: 2, owner: e.CallerId, isAll: true,
                                      atoms: null, parcelIds: new[] { p3 });
            RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, again, e.Store);
            Hex.True(e.Store.GetParcel(p3) is { IsRecved: true }, "the row survives with the switch off");
            Hex.Eq(e.Caller.Frame(), T234bBadge(1, 0),
                   "and the badge is still pushed - it just reports the row that is still there");
        }
    }

    [Test] public static void T234b_the_listing_sweep_and_a_mailbox_delete_both_refresh_the_badge()
    {
        using var e = new T201UtilityEnvironment();
        using var deleteOnCollect = new T234Switch("1");

        // The state a player actually logs in to: two rewards already claimed by an earlier
        // build's Receive all, never opened, plus one that is genuinely new. Badge: 3.
        var stale = new[]
        {
            T202SystemParcel(e.Store, e.CallerId, "Utility", money: 0, (201100, 1)),
            T202SystemParcel(e.Store, e.CallerId, "Utility", money: 0, (201100, 2)),
        };
        foreach (int id in stale) e.Store.SetParcelRecved(id, e.CallerId);
        int fresh = T202SystemParcel(e.Store, e.CallerId, "Utility", money: 0, (202090, 1));
        Hex.True(e.Store.GetParcelCounts(e.CallerId) == (3, 0),
                 $"three unread as far as the counters know, got {e.Store.GetParcelCounts(e.CallerId)}");

        var (op, body) = RunHandler1(DbProxyHandlers.SDB_LIST_PARCEL, T234ListRequest(985, e.CallerId), e.Store);
        Hex.True(op == DbProxyHandlers.DBS_LIST_PARCEL
                 && BitConverter.ToUInt32(body, ParcelDbHandlers.ListRspParcelCount) == 1,
                 "the sweep leaves the one real parcel to list");
        Hex.Eq(e.Caller.Frame(), T234bBadge(1, 0), "and the badge follows the sweep down to 1");
        Hex.True(e.Caller.Available == 0, "one push per sweep");

        // A second listing sweeps nothing, so it pushes nothing - retail's Handler_SDB_LIST_PARCEL
        // has no push at all, and ours only earns one when it actually removed a row.
        RunHandler1(DbProxyHandlers.SDB_LIST_PARCEL, T234ListRequest(986, e.CallerId), e.Store);
        Hex.True(e.Caller.Available == 0, "an idle listing is silent");

        // The manual path: C_DELETE_PARCEL reaches us as SDB_DELETE_PARCEL, and the real handler
        // pushes for an inbox delete only (Arb_part_071.c:15231, guarded on IsSendParcel == 0).
        // T234b reads two fields of that request we used to ignore, so pin the layout against the
        // one real delete there is - cap_social2 2401, user 1003 dropping parcel 3 from the inbox.
        Hex.Eq(T234bDeleteRequest(0xCD, 1003, isSendParcel: false, 3),
               Convert.FromHexString("1700000004000000CD000000EB0300000003000000"),
               "cap_social2 2401: list at frame 23, 4 bytes, DlmId 0xcd, user 1003, IsSendParcel 0, id 3");
        RunHandler1(DbProxyHandlers.SDB_DELETE_PARCEL,
                    T234bDeleteRequest(987, e.CallerId, isSendParcel: false, fresh), e.Store);
        Hex.True(e.Store.GetParcel(fresh) is null, "the parcel is deleted");
        Hex.Eq(e.Caller.Frame(), T234bBadge(0, 0), "and the badge empties with it");

        int sent = e.Store.CreateParcel(e.CallerId, "Utility", e.TargetId, "hi", "", 0, 1);
        RunHandler1(DbProxyHandlers.SDB_DELETE_PARCEL,
                    T234bDeleteRequest(988, e.CallerId, isSendParcel: true, sent), e.Store);
        Hex.True(e.Store.GetParcel(sent) is null, "a Sent-box delete still deletes");
        Hex.True(e.Caller.Available == 0, "but pushes nothing - the Sent box has no badge");
    }

    [Test] public static void T234b_an_offline_receiver_is_never_pushed()
    {
        using var e = new T201UtilityEnvironment();

        // Offline is the normal case, not a failure: their badge is rebuilt from the DB at login
        // (C_LOAD_TOPO_FIN, which is where the real Arbiter's own login push lives).
        T202SystemParcel(e.Store, e.TargetId, "Target", money: 0, (202090, 1));
        Hex.True(!ParcelHandlers.PushReadRecvStatus(e.TargetId), "an offline character is not pushed");
        Hex.True(e.Target.Available == 0, "and nothing is written to their socket");
        Hex.True(!ParcelHandlers.PushReadRecvStatus(0)
                 && !ParcelHandlers.PushReadRecvStatus(-1),
                 "and a missing or negative receiver is refused rather than broadcast");

        e.TargetOnline();
        Hex.True(ParcelHandlers.PushReadRecvStatus(e.TargetId), "once in world, the push lands");
        Hex.Eq(e.Target.Frame(), T234bBadge(1, 0), "with their own counters, not the caller's");
        Hex.True(e.Caller.Available == 0, "the caller's badge is untouched");
    }
}
