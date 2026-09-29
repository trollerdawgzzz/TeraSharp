// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T191g - the window layout was acked and thrown away.
//
// Three blobs carry client state, not two. The hotbar rides in the USER blob and the options in
// the ACCOUNT blob, and both are stored (T19/T191b/T191c) - which is why the hotbar survives a
// relog. The WINDOW LAYOUT is its own pair, C_SAVE_CLIENT_UI_SETTING (0xA98F) ->
// S_REPLY_CLIENT_UI_SETTING (0x5FAE), and nothing stored it:
//
//   cap_wasd2_client   26/32/50/463  S_REPLY_CLIENT_UI_SETTING  116 B, identical every time,
//                      33            C_SAVE_CLIENT_UI_SETTING   115 B, ignored
//   cap_2man_b_client1 29/513/1894   S_REPLY_CLIENT_UI_SETTING  486 B, the account's own
//                      33/539        C_SAVE_CLIENT_UI_SETTING   461 B
//
// Ours was the captured default - one client's three windows - handed to every account forever.
//
// The two shapes differ in exactly one field. Save records carry a u8 flag, reply records a u32,
// and the save body has an 8-byte preamble the reply does not, so a reply is
// save + 3 per record - 8 bytes: ours 115 + 9 - 8 = 116, retail 461 + 33 - 8 = 486. Both hold.
// =============================================================================================
public static partial class Tests
{
    /// <summary>cap_wasd2_client packet 33, body (the frame without its 4-byte header).</summary>
    static readonly byte[] T191gSaveBody = Hex.B(
        "03 00 10 00 01 00 00 00 00 00 00 00 10 00 31 00 1F 00 01 DF CF 7C 42 A0 1A E7 40 4D 00 61 00 69 00 6E 00 " +
        "4D 00 65 00 6E 00 75 00 00 00 31 00 50 00 40 00 01 F0 67 BA 42 9A 99 80 42 4D 00 69 00 6E 00 69 00 6D 00 " +
        "61 00 70 00 00 00 50 00 00 00 5F 00 01 BC F4 47 42 A0 1A 3F 41 57 00 6F 00 72 00 6C 00 64 00 4D 00 61 00 " +
        "70 00 32 00 00 00");

    /// <summary>The same three windows in the reply's own record shape, offsets frame-relative.</summary>
    static readonly byte[] T191gReplyBody = Hex.B(
        "03 00 08 00 08 00 2C 00 1A 00 01 00 00 00 DF CF 7C 42 A0 1A E7 40 4D 00 61 00 69 00 6E 00 4D 00 65 00 6E 00 " +
        "75 00 00 00 2C 00 4E 00 3E 00 01 00 00 00 F0 67 BA 42 9A 99 80 42 4D 00 69 00 6E 00 69 00 6D 00 61 00 70 00 " +
        "00 00 4E 00 00 00 60 00 01 00 00 00 BC F4 47 42 A0 1A 3F 41 57 00 6F 00 72 00 6C 00 64 00 4D 00 61 00 70 00 " +
        "32 00 00 00");

    /// <summary>T191g. The save the client sent becomes the reply it gets back, window for window.</summary>
    [Test] public static void T191g_a_saved_layout_converts_to_the_reply_shape()
    {
        var reply = ClientSettingsHandlers.BuildUiSettingReply(T191gSaveBody);
        Hex.True(reply != null, "the captured save walks cleanly");
        Hex.Eq(reply!, T191gReplyBody, "cap_wasd2_client 33, in S_REPLY_CLIENT_UI_SETTING's shape");
        Hex.True(reply.Length + 4 == T191gSaveBody.Length + 4 + 3 * 3 - 8,
            $"a reply is save + 3 per record - 8: {T191gSaveBody.Length + 4} -> {reply.Length + 4}");
    }

    /// <summary>
    /// T191g. The same arithmetic on retail's own layout: cap_2man_b_client1 saves 461 B and is
    /// replied 486 B. Eleven records, so +33-8 = +25. The record count is what the sizes pin.
    /// </summary>
    [Test] public static void T191g_the_retail_pair_sizes_agree_with_the_record_shapes()
    {
        const int saveFrame = 461, replyFrame = 486, records = 11;
        Hex.True(saveFrame + records * 3 - 8 == replyFrame,
            $"{saveFrame} + {records}*3 - 8 should be {replyFrame}");
        int count = BitConverter.ToUInt16(T191gSaveBody);
        Hex.True(count == 3, $"and ours declares its own count the same way, got {count}");
    }

    /// <summary>T191g. Nonsense is refused, so a bad blob serves the default instead of half a layout.</summary>
    [Test] public static void T191g_a_blob_that_does_not_walk_is_refused()
    {
        Hex.True(ClientSettingsHandlers.BuildUiSettingReply(Array.Empty<byte>()) == null, "empty");
        Hex.True(ClientSettingsHandlers.BuildUiSettingReply(Hex.B("03 00 10 00 01 00 00 00")) == null,
            "a header claiming three records with none behind it");
        var truncated = T191gSaveBody[..40];
        Hex.True(ClientSettingsHandlers.BuildUiSettingReply(truncated) == null, "a truncated save");
        Hex.True(ClientSettingsHandlers.DefaultUiSettingBody().Length == 112,
            "and the default is still the captured 116-byte frame");
    }

    /// <summary>T191g. The store round-trips the layout under the account, byte for byte.</summary>
    [Test] public static void T191g_the_store_round_trips_the_layout()
    {
        using var store = StoreWithTwoAccounts();
        long account = store.GetAccount("acct1")!.Id;
        Hex.True(store.LoadUiSetting(account) == null, "nothing stored to begin with");
        Hex.True(store.SaveUiSetting(account, T191gSaveBody), "the save is accepted");
        Hex.Eq(store.LoadUiSetting(account)!, T191gSaveBody, "and comes back byte for byte");
        Hex.True(!store.SaveUiSetting(account, Array.Empty<byte>()), "an empty save is refused");
        Hex.Eq(store.LoadUiSetting(account)!, T191gSaveBody, "and the stored layout stands");
    }
}
