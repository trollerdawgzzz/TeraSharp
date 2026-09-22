// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T172 - census close-out (status/T169-CENSUS.md). Four W->A frames that had no handler, each
/// with a real pair in cap_final2a/2b. Offsets are PAYLOAD offsets (frame - 6).
/// </summary>
public sealed partial class DbProxyHandlers
{
    public const ushort SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD = 0x1436;
    public const ushort SA_BROADCAST_SYSTEM_MESSAGE_NOT_IN_SPECIAL_PLACE = 0x1437;
    public const ushort SA_CREST_USE_LIST = 0x1467, AS_CREST_USE_LIST = 0x1468;
    public const ushort S_SYSTEM_MESSAGE_OP = 0xF30E;
    public const ushort SDB_CHANGE_CITY_WAR_STATE = 0x2958, DBS_CHANGE_CITY_WAR_STATE = 0x295A;

    /// <summary>
    /// SDB_CHANGE_CITY_WAR_STATE <c>[i32 a][i32 b][i32 state]</c> -> DBS <c>[a][u32 ok 1][b][state]</c>.
    /// Every pair: cap_final2a/2b 111 -> 112 (state 2, at World start), cap_final2b 57115 -> 57116 (3),
    /// 57117 -> 57119 (4). T23 sealed it as a one-way periodic because no capture had one; these do.
    /// </summary>
    public static byte[] BuildChangeCityWarStateReply(byte[] payload)
    {
        var r = new byte[16];
        if (payload.Length >= 4) Array.Copy(payload, 0, r, 0, 4);
        r[4] = 1;
        if (payload.Length >= 12) Array.Copy(payload, 4, r, 8, 8);
        return r;
    }

    /// <summary>
    /// 0x1436 / 0x1437 <c>[u32 msgRef][u32 0][u32 0x7F][wstring]</c> (UserManager::BroadcastSystemMessage
    /// ToWholeWorld[NotInSpecialPlace]): S_SYSTEM_MESSAGE with the same string to every player.
    /// cap_final2b 53725 (0x1437 "@4133") -> S_SYSTEM_MESSAGE <c>12 00 0E F3 06 00 "@4133"</c> on both
    /// clients (cli 09-49-53 3417, 09-50-23 3046). "Not in special place" is not modelled: all players.
    /// </summary>
    public static string ReadBroadcastMessage(byte[] payload)
    {
        if (payload.Length < 12) return "";
        int at = (int)BitConverter.ToUInt32(payload, 0) - 6;
        if (at < 12 || at >= payload.Length) return "";
        int end = at;
        while (end + 1 < payload.Length && (payload[end] | payload[end + 1]) != 0) end += 2;
        return System.Text.Encoding.Unicode.GetString(payload, at, end - at);
    }

    public static byte[] BuildSystemMessage(string text)
    {
        int len = 6 + (text.Length + 1) * 2;
        var p = new byte[len];
        BitConverter.GetBytes((ushort)len).CopyTo(p, 0);
        BitConverter.GetBytes(S_SYSTEM_MESSAGE_OP).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)6).CopyTo(p, 4);
        System.Text.Encoding.Unicode.GetBytes(text).CopyTo(p, 6);
        return p;
    }

    private bool OnWorldBroadcast(WorldBridge? bridge, byte[] payload)
    {
        string text = ReadBroadcastMessage(payload);
        if (text.Length == 0 || bridge == null) return true;
        var packet = BuildSystemMessage(text);
        int n = 0;
        foreach (var s in bridge.InWorldSessions()) { s.Send(packet); n++; }
        _log.LogInformation("World broadcast '{Text}' -> {N} player(s)", text, n);
        return true;
    }

    /// <summary>
    /// SA_CREST_USE_LIST (0x1467, User::CrestApplyList): the applied crests, DlmId @24. The reply
    /// (0x1468) is <c>[u32 ref 0x13][u32 0][DlmId][u8 ok 1]</c> - an empty refusal list. Both real
    /// pairs (cap_final2b 4746/4747 DlmId 0x294, 4807/4808 0x295). Without it the user's DB queue
    /// waits on the DlmId. Which crests are applied is not stored (the crests table has no flag).
    /// </summary>
    public static byte[] BuildCrestUseListReply(byte[] payload)
    {
        var r = new byte[13];
        BitConverter.GetBytes(0x13u).CopyTo(r, 0);
        if (payload.Length >= 28) Array.Copy(payload, 24, r, 8, 4);
        r[12] = 1;
        return r;
    }
}
