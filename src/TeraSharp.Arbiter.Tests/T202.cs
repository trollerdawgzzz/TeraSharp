// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T202 - "Receive all" consumed the parcel and lost the attachment.
///
/// <para>The button is <c>SDB_RECV_PARCEL_EX</c> (0x277d), not <c>SDB_RECV_PARCEL</c>, and it is
/// two-step like its single-parcel sibling. Our handler ignored <c>Step</c>: it answered step 1
/// with <c>ParcelCount = 0</c> and an empty list - so World decided there was nothing to collect
/// and never sent step 2, the only frame that carries the insert atoms - while the same pass
/// already marked every parcel collected and paid the gold. cap_mail1 21483/21484 is that
/// exchange: 7 parcels claimed, zero atoms, and no second 0x277d anywhere before World deleted
/// the rows at 21518.</para>
///
/// <para>Ground truth for the shape is the real Arbiter's single-parcel collects, which carry the
/// same records and the same atoms: cap_final2b 10030/10031 (step 1, the full 3544-byte record
/// with two attachment slots) and 10032/10033 (step 2, ids 10078 and 10079 allocated for the two
/// op-7 inserts), plus cap_social2 2508/2509 where an op-9 money atom and an op-2 merge are
/// echoed with nothing allocated.</para>
/// </summary>
public static partial class Tests
{
    static Dictionary<uint, byte[]>? LoadT202CaptureOrSkip() => LoadTsisOrSkip("cap_t202.bin");

    /// <summary>An SDB_RECV_PARCEL_EX request: the 29-byte header plus an atom block at +8.</summary>
    static byte[] T202ExRequest(uint dlmId, uint step, int owner, bool isAll, byte[]? atoms,
                                IReadOnlyList<int>? parcelIds = null)
    {
        atoms ??= Array.Empty<byte>();
        int header = ParcelDbHandlers.RecvExRequestSize;
        var chain = new byte[(parcelIds?.Count ?? 0) * 12];
        for (int i = 0; i < (parcelIds?.Count ?? 0); i++)
        {
            uint here = (uint)(6 + header + i * 12);
            BitConverter.GetBytes(here).CopyTo(chain, i * 12);
            BitConverter.GetBytes(i + 1 < parcelIds!.Count ? here + 12 : 0u).CopyTo(chain, i * 12 + 4);
            BitConverter.GetBytes(parcelIds[i]).CopyTo(chain, i * 12 + 8);
        }
        var p = new byte[header + chain.Length + atoms.Length];
        BitConverter.GetBytes((uint)(parcelIds?.Count ?? 0)).CopyTo(p, ParcelDbHandlers.RecvExReqRefA);
        BitConverter.GetBytes(chain.Length == 0 ? 0u : (uint)(6 + header)).CopyTo(p, ParcelDbHandlers.RecvExReqRefA + 4);
        BitConverter.GetBytes((uint)(6 + header + chain.Length)).CopyTo(p, ParcelDbHandlers.RecvExReqRefB);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(p, ParcelDbHandlers.RecvExReqRefB + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, ParcelDbHandlers.RecvExReqDlmId);
        BitConverter.GetBytes(step).CopyTo(p, ParcelDbHandlers.RecvExReqStep);
        BitConverter.GetBytes(owner).CopyTo(p, ParcelDbHandlers.RecvExReqOwnerDbId);
        p[ParcelDbHandlers.RecvExReqIsAllParcel] = (byte)(isAll ? 1 : 0);
        chain.CopyTo(p, header);
        atoms.CopyTo(p, header + chain.Length);
        return p;
    }

    /// <summary>The atom block out of a captured SDB_RECV_PARCEL, re-owned and re-parcelled.</summary>
    static byte[] T202Atoms(byte[] request, int newOwner, int newParcelId)
    {
        int at = (int)BitConverter.ToUInt32(request, ParcelDbHandlers.RecvReqTransListRef) - 6;
        int len = (int)BitConverter.ToUInt32(request, ParcelDbHandlers.RecvReqTransListRef + 4);
        var atoms = new byte[len];
        Array.Copy(request, at, atoms, 0, len);
        int size = DbProxyHandlers.ItemAtomSize;
        for (int o = 0; o + size <= atoms.Length; o += size)
        {
            if (BitConverter.ToInt64(atoms, o + WarehouseHandlers.AtomSrcOwner) != 0)
                BitConverter.GetBytes((long)newOwner).CopyTo(atoms, o + WarehouseHandlers.AtomSrcOwner);
            if (BitConverter.ToInt64(atoms, o + WarehouseHandlers.AtomDstOwner) != 0)
                BitConverter.GetBytes((long)newOwner).CopyTo(atoms, o + WarehouseHandlers.AtomDstOwner);
            if (BitConverter.ToUInt32(atoms, o + WarehouseHandlers.AtomOp) == WarehouseHandlers.TsRecvParcel)
                BitConverter.GetBytes(newParcelId).CopyTo(atoms, o + WarehouseHandlers.AtomRecvParcelId);
        }
        return atoms;
    }

    /// <summary>A system reward parcel with the attachments in its stored 0xdd8 record.</summary>
    static int T202SystemParcel(CharacterStore store, int receiver, string receiverName,
                                long money, params (int Template, int Amount)[] items)
    {
        int parcel = store.CreateParcel(0, "@Achievement:6913", receiver, "@2051", string.Empty,
                                        money, ParcelDbHandlers.ParcelTypeSystem);
        var records = new List<byte[]>();
        foreach (var (template, amount) in items)
        {
            var item = new byte[SystemParcelAttachments.ItemRecordSize];
            BitConverter.GetBytes(template).CopyTo(item, 8);
            BitConverter.GetBytes(amount).CopyTo(item, 12);
            records.Add(item);
            store.AddParcelItem(parcel, records.Count - 1, 0, template, amount);
        }
        store.SetParcelRecord(parcel, SystemParcelAttachments.BuildRecord(
            parcel, receiver, receiverName, "@Achievement:6913", "@2051", string.Empty, money, records));
        return parcel;
    }

    [Test] public static void T202_receive_all_step1_enumerates_and_claims_nothing()
    {
        var cap = LoadT202CaptureOrSkip();
        if (cap == null) return;
        using var store = T45Store();

        // What the real step 1 has to hand World: the FULL record, attachment slots included.
        // cap_final2b 10031 is the reference - parcel 12, type 102, two slots at +0xd8/+0x288.
        var real = cap[10031];
        int recOff = 25, slot0 = SystemParcelAttachments.ParcelItemsOffset;
        Hex.True(BitConverter.ToUInt32(real, 4) == SystemParcelAttachments.ParcelRecordSize,
                 $"the real step-1 record is 0xdd8 bytes, got {BitConverter.ToUInt32(real, 4)}");
        Hex.True(BitConverter.ToInt32(real, recOff + slot0 + 8) == 202090
                 && BitConverter.ToInt32(real, recOff + slot0 + SystemParcelAttachments.ItemRecordSize + 8) == 202243,
                 "and it carries both attachment templates");

        int p1 = T202SystemParcel(store, 1, "t1", money: 0, (202090, 1), (202243, 1));
        int p2 = T202SystemParcel(store, 1, "t1", money: 500, (90089, 1));

        // cap_mail1 21483's own request, owner re-pointed at our character: IsAllParcel, step 1.
        var request = T202ExRequest(dlmId: 983, step: 1, owner: 1, isAll: true, atoms: null);
        var captured = cap[121483][..ParcelDbHandlers.RecvExRequestSize];
        var normalised = (byte[])request.Clone();
        Array.Clear(captured, ParcelDbHandlers.RecvExReqOwnerDbId, 4);
        Array.Clear(normalised, ParcelDbHandlers.RecvExReqOwnerDbId, 4);
        Hex.Eq(normalised, captured,
               "the request we answer is cap_mail1 21483 itself, owner apart: count 0, head 0,"
               + " ref B at frame 35 with no atoms, step 1, IsAllParcel 1");

        var (op, body) = RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, store);
        Hex.True(op == DbProxyHandlers.DBS_RECV_PARCEL_EX, $"reply opcode 0x{op:X4}");

        int stride = ParcelDbHandlers.RecvExNodeSize + SystemParcelAttachments.ParcelRecordSize;
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspRefA) == 2
                 && BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspRefA + 4) == 6 + ParcelDbHandlers.RecvExReplyHeader,
                 $"two records chained from frame offset 35, got count {BitConverter.ToUInt32(body, 0)} head {BitConverter.ToUInt32(body, 4)}");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspNoParcel) == 2,
                 $"ParcelCount is what World tests before it sends step 2, got {BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspNoParcel)}");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspStep) == 1 && body[ParcelDbHandlers.RecvExRspSuccess] == 1,
                 "step 1, success");
        Hex.True(body.Length == ParcelDbHandlers.RecvExReplyHeader + 2 * stride,
                 $"29 + 2 x (16 + 0xdd8), got {body.Length}");

        // Each node: [here][next][dataOffset][0xdd8], and the record behind it is the parcel's.
        for (int i = 0; i < 2; i++)
        {
            int at = ParcelDbHandlers.RecvExReplyHeader + i * stride;
            uint here = (uint)(6 + ParcelDbHandlers.RecvExReplyHeader + i * stride);
            Hex.True(BitConverter.ToUInt32(body, at) == here, $"node {i} points at itself");
            Hex.True(BitConverter.ToUInt32(body, at + 4) == (i == 0 ? here + (uint)stride : 0u), $"node {i} next");
            Hex.True(BitConverter.ToUInt32(body, at + 8) == here + (uint)ParcelDbHandlers.RecvExNodeSize
                     && BitConverter.ToUInt32(body, at + 12) == SystemParcelAttachments.ParcelRecordSize,
                     $"node {i} data offset and 0xdd8 length");
            int rec = at + ParcelDbHandlers.RecvExNodeSize;
            Hex.True(BitConverter.ToInt32(body, rec + ParcelDbHandlers.ParcelDataParcelId) == (i == 0 ? p1 : p2)
                     && BitConverter.ToInt32(body, rec + ParcelDbHandlers.ParcelDataParcelType) == ParcelDbHandlers.ParcelTypeSystem,
                     $"record {i} is the parcel's own");
            Hex.True(BitConverter.ToInt32(body, rec + slot0 + 8) == (i == 0 ? 202090 : 90089),
                     $"record {i} carries its first attachment template");
        }

        // The point of the fix: step 1 reads, it does not claim.
        Hex.True(store.GetParcel(p1) is { IsRecved: false } && store.GetParcel(p2) is { IsRecved: false },
                 "neither parcel is marked collected");
        Hex.True(store.GetCharacter(1)!.Money == 0, "and the 500 gold has not been paid");
        Hex.True(store.CountInventoryItems(1) == 0, "and no item row exists yet");

        // The reply we used to send, for the record: 35 in the count slot and ParcelCount 0.
        Hex.True(BitConverter.ToUInt32(cap[121484], 0) == 35 && BitConverter.ToUInt32(cap[121484], 24) == 0,
                 "cap_mail1 21484 is the old reply: count 35, ParcelCount 0");
        Hex.True(BitConverter.ToUInt32(ParcelDbHandlers.BuildDbsRecvParcelEx(null, null, 1, 1, 0, true), 0) == 0,
                 "an empty record list now writes 0 there, not the frame length");
    }

    [Test] public static void T202_receive_all_step2_lands_the_items_and_survives_a_relog()
    {
        var cap = LoadT202CaptureOrSkip();
        if (cap == null) return;
        using var store = T45Store();

        int parcel = T202SystemParcel(store, 1, "t1", money: 700, (202090, 1), (202243, 1));
        var atoms = T202Atoms(cap[10032], newOwner: 1, newParcelId: parcel);
        var request = T202ExRequest(dlmId: 983, step: 2, owner: 1, isAll: true, atoms);

        var (op, body) = RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, store);
        Hex.True(op == DbProxyHandlers.DBS_RECV_PARCEL_EX, $"reply opcode 0x{op:X4}");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspStep) == 2
                 && BitConverter.ToUInt32(body, ParcelDbHandlers.RecvExRspNoParcel) == 1,
                 "step 2 reports the one parcel it committed");

        // The attachments are real rows now, at the (pocket, slot) World chose - 0:14 and 0:15.
        var rows = store.GetInventoryItems(1);
        Hex.True(rows.Count == 2, $"two item rows, got {rows.Count}");
        Hex.True(rows.All(r => r.ItemDbId > 0), "each with an allocated id");
        Hex.True(rows.Any(r => r.TemplateId == 202090 && r.InvenType == 0 && r.Slot == 14)
                 && rows.Any(r => r.TemplateId == 202243 && r.InvenType == 0 && r.Slot == 15),
                 "in the slots the atoms named: " + string.Join(", ", rows.Select(r => $"{r.TemplateId}@{r.InvenType}:{r.Slot}")));

        // The parcel is spent and the gold is paid - once.
        Hex.True(store.GetParcel(parcel) is { IsRecved: true }, "the parcel is collected");
        Hex.True(store.GetCharacter(1)!.Money == 700, $"the attached gold is credited once, got {store.GetCharacter(1)!.Money}");

        // A relog: the load is rebuilt from the rows, so the items are still there.
        var served = BagItems.SplitPayload(BagItems.BuildPayload(store.GetInventoryItems(1), reqId: 1, ownerId: 1), 1);
        Hex.True(served.Count == 2 && served.All(r => r.TemplateId is 202090 or 202243),
                 $"the next login serves both attachments, got {served.Count}");

        // Running the same commit twice must not pay twice or duplicate the rows.
        RunHandler1(DbProxyHandlers.SDB_RECV_PARCEL_EX, request, store);
        Hex.True(store.GetCharacter(1)!.Money == 700, "a repeated commit pays nothing more");
    }

    [Test] public static void T202_the_real_collect_allocates_ids_for_inserts_and_nothing_else()
    {
        var cap = LoadT202CaptureOrSkip();
        if (cap == null) return;

        // cap_final2b 10032 -> 10033: two op-7 inserts arrive with ItemDbId 0 and come back as
        // 10078 and 10079; the op-37 marker is echoed untouched. Our clone is byte-exact.
        byte[] Atoms(byte[] frame, int refAt)
        {
            int at = (int)BitConverter.ToUInt32(frame, refAt) - 6;
            int len = (int)BitConverter.ToUInt32(frame, refAt + 4);
            var a = new byte[len]; Array.Copy(frame, at, a, 0, len); return a;
        }
        var ids = new IdCounter(10078);
        var (mine, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            cap[10032], ParcelDbHandlers.RecvReqTransListRef, ParcelDbHandlers.RecvRequestSize, ids.Next);
        Hex.Eq(mine, Atoms(cap[10033], ParcelDbHandlers.RecvRspTransListRef),
               "DBS_RECV_PARCEL atoms (cap_final2b 10032 -> 10033)");
        Hex.True(ids.Calls == 2, $"exactly two ids allocated, got {ids.Calls}");
        Hex.True(parsed.Count == 3 && parsed[2].Op == WarehouseHandlers.TsRecvParcel,
                 "three atoms, the last one the op-37 marker");
        Hex.True(ParcelDbHandlers.ParcelIdsFromAtoms(mine).SequenceEqual(new[] { 12 }),
                 "and the marker names parcel 12 at atom +0x278");

        // cap_social2 2508 -> 2509: an op-9 money atom and an op-2 merge. Nothing is allocated,
        // so the echo is the request's atom block unchanged.
        var noAlloc = new IdCounter(99000);
        var (same, parsed2) = WarehouseHandlers.CloneAtomsWithIds(
            cap[202508], ParcelDbHandlers.RecvReqTransListRef, ParcelDbHandlers.RecvRequestSize, noAlloc.Next);
        Hex.Eq(same, Atoms(cap[202509], ParcelDbHandlers.RecvRspTransListRef),
               "DBS_RECV_PARCEL atoms (cap_social2 2508 -> 2509)");
        Hex.True(noAlloc.Calls == 0, $"a money-and-merge collect allocates nothing, got {noAlloc.Calls}");
        Hex.True(parsed2.Any(a => a.Op == WarehouseHandlers.TsChangeMoney && a.Delta == 1230000),
                 "the money is an op-9 atom of +1230000, which is World's to credit");
    }

    [Test] public static void T202_a_system_reward_is_in_the_inbox_and_never_in_the_sent_box()
    {
        var cap = LoadT202CaptureOrSkip();
        if (cap == null) return;

        // The real Arbiter, same user, both views: three system mails in the inbox and an empty
        // Sent box. ViewType 0xffffffff and 1 both mean Sent (cap_social2 2419/2420).
        Hex.True(BitConverter.ToUInt32(cap[52833], ParcelDbHandlers.ListReqViewType) == 0xFFFFFFFFu
                 && BitConverter.ToUInt32(cap[52834], ParcelDbHandlers.ListRspParcelCount) == 0
                 && cap[52834].Length == ParcelDbHandlers.ListReplyHeader,
                 "cap_final2b 52833/52834: view -1 -> 0 rows, the 35-byte empty frame");
        Hex.True(BitConverter.ToUInt32(cap[9986], ParcelDbHandlers.ListReqViewType) == 0
                 && BitConverter.ToUInt32(cap[9987], ParcelDbHandlers.ListRspParcelCount) == 2,
                 "and view 0 for the same mailbox returns rows");
        Hex.True(BitConverter.ToUInt32(cap[202419], ParcelDbHandlers.ListReqViewType) == 1
                 && BitConverter.ToUInt32(cap[202420], ParcelDbHandlers.ListRspParcelCount) == 1
                 && BitConverter.ToInt32(cap[202420], ParcelDbHandlers.ListReplyHeader
                        + ParcelDbHandlers.ParcelDataSenderDbId) == 1003,
                 "cap_social2 2419/2420: view 1 returns the parcel the user SENT");

        using var store = T45Store();
        int reward = T202SystemParcel(store, 1, "t1", money: 0, (202090, 1));
        int sent = store.CreateParcel(1, "t1", 2, "hi", string.Empty, 100, 1);
        Hex.True(reward > 0 && sent > 0, "one reward in, one parcel out");

        ParcelDbHandlers.BuildParcelList(store, 1, 0u, out uint inbox, out _);
        ParcelDbHandlers.BuildParcelList(store, 1, 1u, out uint sentOne, out _);
        ParcelDbHandlers.BuildParcelList(store, 1, 0xFFFFFFFFu, out uint sentMinusOne, out _);
        Hex.True(inbox == 1 && sentOne == 1 && sentMinusOne == 1,
                 $"inbox {inbox}, sent(1) {sentOne}, sent(-1) {sentMinusOne}");
        Hex.True(store.GetParcelsSentBy(1).Single().ParcelId == sent,
                 "and the Sent box holds the parcel the character sent, not the reward");
        Hex.True(store.GetParcelsSentBy(2).Count == 0, "the receiver has sent nothing");

        // The wire, with cap_mail1 29480's own ViewType: the Sent view must not serve the inbox.
        var request = new byte[ParcelDbHandlers.ListRequestSize];
        BitConverter.GetBytes(1740u).CopyTo(request, ParcelDbHandlers.ListReqDlmId);
        BitConverter.GetBytes(1u).CopyTo(request, ParcelDbHandlers.ListReqUserDbId);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(request, ParcelDbHandlers.ListReqViewType);
        var (op, body) = RunHandler1(DbProxyHandlers.SDB_LIST_PARCEL, request, store);
        Hex.True(op == DbProxyHandlers.DBS_LIST_PARCEL, $"reply opcode 0x{op:X4}");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.ListRspParcelCount) == 1
                 && BitConverter.ToInt32(body, ParcelDbHandlers.ListReplyHeader
                        + ParcelDbHandlers.ParcelDataSenderDbId) == 1,
                 "the Sent view returns the sent parcel, sender 1 - not the system reward");
        Hex.True(BitConverter.ToUInt32(body, ParcelDbHandlers.ListRspViewType) == 0xFFFFFFFFu,
                 "and the ViewType is still echoed");
    }
}
