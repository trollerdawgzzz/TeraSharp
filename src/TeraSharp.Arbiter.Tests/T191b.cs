// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T191b - the account-scope client settings do not survive a login, because the LOBBY send is a
/// hardcoded empty packet instead of the stored blob.
///
/// <para>The real Arbiter serves the same account blob at all three points of a login - at the
/// lobby right after <c>S_GET_USER_LIST</c>, and again as the first half of the post-spawn
/// account+user pair - and serves it again at the lobby after <c>C_RETURN_TO_LOBBY</c>
/// (cap_final2b_client2 packets 12 / 286 / 846 / 1119, all 667 bytes, all the same 659-byte blob).
/// We serve the stored blob post-spawn (cap_queue1_client 322) but an 8-byte EMPTY packet at the
/// lobby (packet 12), and the client reacts to that by pushing its freshly-initialised defaults
/// back at us (packet 31), overwriting the options the player had saved (packet 1329). That is the
/// round trip the tutorial/interface options ride on.</para>
///
/// <para>The tutorial tips themselves are NOT the cause and are already right: World decides a
/// simple tip with <c>User::CheckTutorialSimpleTip</c> and answers
/// <c>S_SIMPLE_TIP_REPEAT_CHECK [u32 tipId][u8 !show]</c>. Our 0x2873 load makes World answer 1
/// ("do not show") for tips 1, 2, 35, 39 and 41, exactly like the real Arbiter's own runs
/// (cap_queue4_client1 606-649 versus cap_social_client 376-402, cap_final2a_client1 443-488).</para>
/// </summary>
public static partial class Tests
{
    static Dictionary<uint, byte[]>? LoadT191bCaptureOrSkip() => LoadTsisOrSkip("cap_t191b.bin");

    [Test] public static void T191b_the_real_lobby_load_carries_the_stored_account_blob()
    {
        var cap = LoadT191bCaptureOrSkip();
        if (cap == null) return;

        // One blob, four send points, one account: the lobby packet is not a special empty form.
        var blob = ClientSettingsHandlers.ParseSettingBlob(FrameBody(cap[12])).ToArray();
        Hex.True(blob.Length == 659, $"the real account blob is 659 bytes, got {blob.Length}");
        foreach (uint seq in new uint[] { 286, 846, 1119 })
            Hex.Eq(FrameBody(cap[seq]), FrameBody(cap[12]),
                   $"cap_final2b_client2 packet {seq} carries the same body as the lobby packet 12");

        // And re-framing the stored blob reproduces the real packet byte for byte, at every point.
        Hex.Eq(ClientFrame(ClientSettingsHandlers.OpSLoadClientAccountSetting,
                           ClientSettingsHandlers.BuildSettingBody(blob)), cap[12],
               "S_LOAD_CLIENT_ACCOUNT_SETTING (cap_final2b_client2 packet 12)");

        // A second real account, a different blob length, same shape - so 659 is not a constant.
        var other = ClientSettingsHandlers.ParseSettingBlob(FrameBody(cap[200012])).ToArray();
        Hex.True(other.Length == 617, $"cap_social2_client packet 12 carries 617 bytes, got {other.Length}");
        Hex.Eq(ClientFrame(ClientSettingsHandlers.OpSLoadClientAccountSetting,
                           ClientSettingsHandlers.BuildSettingBody(other)), cap[200012],
               "S_LOAD_CLIENT_ACCOUNT_SETTING (cap_social2_client packet 12)");
    }

    [Test] public static void T191b_our_lobby_load_was_empty_while_a_blob_was_stored()
    {
        var cap = LoadT191bCaptureOrSkip();
        if (cap == null) return;

        // The bug, as the capture recorded it: 8 bytes at the lobby, the real blob post-spawn.
        Hex.Eq(FrameBody(cap[100012]), ClientSettingsHandlers.EmptySettingBody(),
               "our lobby packet was the empty form");
        Hex.True(cap[100012].Length == 8 && cap[100322].Length == 600,
                 $"lobby {cap[100012].Length} B vs post-spawn {cap[100322].Length} B");

        // The client's answer to an empty load is to push its own defaults back, which is how the
        // player's stored options get lost: packet 31 (592 B) overwrites what 1329 (606 B) saved.
        var pushedBack = ClientSettingsHandlers.ParseSettingBlob(FrameBody(cap[100031])).ToArray();
        var playerSaved = ClientSettingsHandlers.ParseSettingBlob(FrameBody(cap[101329])).ToArray();
        Hex.True(pushedBack.Length == 592 && playerSaved.Length == 606,
                 $"the re-save is {pushedBack.Length} B and the player's own save {playerSaved.Length} B");
        Hex.True(!pushedBack.AsSpan().SequenceEqual(playerSaved),
                 "and they are different bytes, so the re-save is a loss, not a no-op");

        // What the post-spawn send already does right, and what the lobby send has to do too.
        Hex.Eq(FrameBody(cap[100322]), ClientSettingsHandlers.BuildSettingBody(pushedBack),
               "our post-spawn load is the stored blob re-framed");
    }

    [Test] public static void T191b_a_saved_account_blob_comes_back_after_a_relog()
    {
        var cap = LoadT191bCaptureOrSkip();
        if (cap == null) return;
        using var store = StoreWithTwoAccounts();

        // Save -> relog -> load, with the bytes the client actually sent us (cap_queue1 1329).
        var saved = ClientSettingsHandlers.ParseSettingBlob(FrameBody(cap[101329])).ToArray();
        var account = store.GetOrCreateAccount("acct1");
        Hex.True(store.SaveAccountSetting(account.Id, saved), "the 606-byte save is accepted");

        // Both send points build from the same one place, so both must produce the same packet.
        var stored = store.LoadAccountSetting(account.Id);
        Hex.Eq(stored!, saved, "the store hands back the same bytes");
        var expected = ClientFrame(ClientSettingsHandlers.OpSLoadClientAccountSetting,
                                   ClientSettingsHandlers.BuildSettingBody(stored));
        Hex.True(expected.Length == 614, $"which frames to 614 bytes, got {expected.Length}");

        // The other account is untouched, and an account with nothing stored still gets the empty
        // form - that, and only that, is when the lobby packet may be 8 bytes long.
        var second = store.GetOrCreateAccount("acct2");
        Hex.True(store.LoadAccountSetting(second.Id) == null, "the second account has nothing stored");
        Hex.Eq(ClientSettingsHandlers.BuildSettingBody(store.LoadAccountSetting(second.Id)),
               ClientSettingsHandlers.EmptySettingBody(), "and gets 08 00 00 00");
    }
}
