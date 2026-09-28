// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Web;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T207c - the OpUent channel: no call tera-api can make is unhandled any more.
//
// arbiter-bg3.log has "hub: unhandled OpUent call 3 (0 B)" every ten seconds from 20:47:30. That
// call is hubFunctions.js's getServerStat: it sends inner id 3 to gusid.userentity with an EMPTY
// GetServerStatReq, which is why the payload is 0 bytes, and tera-api's ServerCheckActions runs it
// on a ten-second schedule.
//
// The whole OpUent surface tera-api has (lib/hubFunctions.js, "OpUent functions"):
//
//   1  QueryUserReq        -> answered, T207: is this account on this server
//   3  GetServerStatReq    -> answered here
//   5  GetAllServerStatReq -> refused by name; its Ans wants { serverId, lastMsg, ip, port } for
//                             every server in the platform and TeraSharp is one server
//
// and the OpArb surface, all already answered: 1 opmsg (the box API), 2 KickUser, 4 SendMessage,
// 6 BulkKick, 15 BoxNotiUser, 38 AddBenefit, 40 RemoveBenefit.
// =============================================================================================
public static partial class Tests
{
    /// <summary>The captured call: OpUent inner id 3, empty body, addressed to userentity.</summary>
    static byte[] T207cGetServerStatFrame(ulong jobId = 7)
        => HubCall(jobId, HubProtocol.UserEntityGusid, HubProtocol.GetServerStatReq, Array.Empty<byte>());

    /// <summary>
    /// T207c. The 0-byte call from the capture, driven through the real frame path, comes back as
    /// a GetServerStatAns naming this server and its user count - so tera-api's
    /// ServerCheckActions finds the row and marks the server available through the hub.
    /// </summary>
    [Test] public static void T207c_the_ten_second_server_stat_poll_is_answered()
    {
        using var store = new CharacterStore(":memory:", QuietLog());
        var hub = HubServer.ForTests(store,
            new HubServer.Hooks { OnlineAccounts = () => new long[] { 4, 9, 4 } }, QuietLog());

        // Exactly what the capture shows: inner id 3, zero payload.
        byte[] inner = HubProtocol.InnerBody(HubProtocol.Inner(HubProtocol.GetServerStatReq, Array.Empty<byte>()));
        Hex.True(inner.Length == 0, "GetServerStatReq has no fields - the capture's 0 B payload");

        var frames = hub.Handle(T207cGetServerStatFrame());
        Hex.True(frames.Count == 2, $"an ack and the answer, not {frames.Count}");
        Hex.True(HubProtocol.FrameId(frames[0]) == HubProtocol.SendMessageAns, "the ack comes first");
        var ack = HubProtocol.Parse(HubProtocol.FrameBody(frames[0]));
        Hex.True(HubProtocol.Scalar(ack, 4) == 1, "the ack says the call was answered");

        Hex.True(HubProtocol.FrameId(frames[1]) == HubProtocol.RecvMessageReq, "then the answer");
        var recv = HubProtocol.Parse(HubProtocol.FrameBody(frames[1]));
        Hex.True(HubProtocol.Scalar(recv, 1) == HubProtocol.UserEntityGusid
                 && HubProtocol.Scalar(recv, 2) == 7, "answered to userentity, under the job id it asked with");
        byte[] msg = HubProtocol.Bytes(recv, 3);
        Hex.True(HubProtocol.InnerId(msg) == HubProtocol.GetServerStatAns,
            "the answer is GetServerStatAns (Req + 1, the convention the OpArb pairs use)");

        // GetServerStatAns { repeated ServerInfo serverList = 1 },
        // ServerInfo { fixed32 serverId = 1, fixed32 userCnt = 2 } - opUent.js's own encoders.
        var ans = HubProtocol.Parse(HubProtocol.InnerBody(msg));
        var info = HubProtocol.Parse(HubProtocol.Bytes(ans, 1));
        Hex.True(HubProtocol.Scalar(info, 1) == HubServer.DefaultServerId,
            "serverList[0].serverId is the PLAIN server number ServerCheckActions compares against");
        Hex.True(HubProtocol.Scalar(info, 2) == 2,
            "userCnt counts DISTINCT accounts in world - account 4 twice is one user");
    }

    /// <summary>
    /// T207c. With no hook and no live World the answer is still well formed and says nobody is
    /// on - an empty server has to report available, not fall back to the port probe.
    /// </summary>
    [Test] public static void T207c_an_empty_server_still_reports_itself()
    {
        using var store = new CharacterStore(":memory:", QuietLog());
        var hub = HubServer.ForTests(store, new HubServer.Hooks(), QuietLog(), serverId: 4242);
        var answer = hub.Call(HubProtocol.UserEntityGusid, HubProtocol.GetServerStatReq, Array.Empty<byte>());
        Hex.True(answer is not null, "an empty server answers rather than refusing");
        var info = HubProtocol.Parse(HubProtocol.Bytes(HubProtocol.Parse(answer!.Value.Body), 1));
        Hex.True(HubProtocol.Scalar(info, 1) == 4242 && HubProtocol.Scalar(info, 2) == 0,
            "the configured server id, nobody online");
    }

    /// <summary>
    /// T207c. Every OpUent id tera-api can send is decided by name. 5 is refused - that is a
    /// deliberate answer, not a hole - and only an id tera-api has no function for reaches the
    /// unhandled branch.
    /// </summary>
    [Test] public static void T207c_every_opuent_call_tera_api_can_make_is_decided()
    {
        using var store = new CharacterStore(":memory:", QuietLog());
        var hub = HubServer.ForTests(store, new HubServer.Hooks(), QuietLog());

        foreach (ushort id in new ushort[] { HubProtocol.QueryUserReq, HubProtocol.GetServerStatReq })
            Hex.True(hub.Call(HubProtocol.UserEntityGusid, id, Array.Empty<byte>()) is not null,
                "OpUent " + id + " is answered");

        Hex.True(hub.Call(HubProtocol.UserEntityGusid, HubProtocol.GetAllServerStatReq,
                new HubProtocol.Writer().Fixed32(1, 0).ToArray()) is null,
            "OpUent 5 is refused, by name - tera-api catches it as a HubError");

        // The ids the OpArb side can carry, so the two channels are covered in one place.
        foreach (ushort id in new ushort[] { HubProtocol.KickUserReq, HubProtocol.SendMsgReq,
                     HubProtocol.BulkKickReq, HubProtocol.BoxNotiUserReq })
            Hex.True(hub.Call(hub.Gusid, id, new HubProtocol.Writer().Varint(1, 1).Bytes(2, Array.Empty<byte>()).ToArray()) is not null,
                "OpArb " + id + " is answered");
    }
}
