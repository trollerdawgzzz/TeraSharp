// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Web;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T188 - the per-character state that outlives a World session, and the admin button that drops
/// it. A session that ended without a clean SA_LEAVE_WORLD (a World restart, a crash, a dropped
/// link) leaves the enter-world stamp, the game id, the one-shot Alt+A push, a queued match, a
/// party-match listing and any T180 hold behind; the next login inherits them, and until T188 the
/// only way out was a relog.
///
/// <para>What the reset must NOT do is touch anything durable: the repair has to be safe to run
/// on a character that is behaving.</para>
/// </summary>
public static partial class Tests
{
    [Test] public static void T188_reset_clears_the_state_a_world_restart_leaves_behind()
    {
        const int player = 1;
        PartyMatchManager.Reset();
        try
        {
            // The state a live session accumulates.
            DbProxyHandlers.MarkEnteredWorld(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            DbProxyHandlers.GameIdByPlayer[player] = 0x80000AF00000UL | player;
            Hex.True(Handlers.ArbiterClientHandlers.TryTakeGmSkillPush(player), "the push is taken once");

            var cleared = CharacterTransientState.Reset(player);

            Hex.True(cleared.Contains("enter-world"), $"the enter stamp and game id go: {string.Join(",", cleared)}");
            Hex.True(cleared.Contains("gm-push"), "and the one-shot Alt+A push is armed again");
            Hex.True(!DbProxyHandlers.GameIdByPlayer.ContainsKey(player), "no game id is left registered");
            Hex.True(DbProxyHandlers.SecondsInWorld(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == 0,
                     "and no enter stamp, so the next login does not bank a dead session's time");
            Hex.True(Handlers.ArbiterClientHandlers.TryTakeGmSkillPush(player),
                     "the push can be taken again after the reset");

            // A second reset finds nothing: the call is idempotent, which is what makes it safe
            // to run on a character that was never wedged.
            Handlers.ArbiterClientHandlers.ResetGmSkillPush(player);
            Hex.True(CharacterTransientState.Reset(player).Count == 0, "a clean character clears nothing");
        }
        finally
        {
            Handlers.ArbiterClientHandlers.ResetGmSkillPush(player);
            DbProxyHandlers.ForgetPlayer(player);
            PartyMatchManager.Reset();
        }
    }

    [Test] public static void T188_reset_drops_a_stale_hold_and_a_party_listing()
    {
        const int player = 1;
        PartyMatchManager.Reset();
        var controls = new WorldUserControls();
        try
        {
            // T180's hold is keyed by the World-side handle; a restart leaves it set for a user
            // who is no longer in any world, and World then freezes the character on its next
            // spawn (0x2930 answers held = 1).
            ulong handle = 0x0000AA0000000001UL;
            var now = DateTimeOffset.UtcNow;
            controls.TryHandle(WorldUserControls.SA_ADMIN_HOLD_CHARACTER,
                BuildT188HoldPayload(handle, hold: true),
                _ => new WorldControlUser(1, (uint)player, 1),
                new DungeonChannels(), _ => true, _ => { }, now);
            Hex.True(controls.IsHeld((uint)player, now), "the hold is on");

            Hex.True(controls.ForgetUser((uint)player, handle), "the reset finds it");
            Hex.True(!controls.IsHeld((uint)player, now), "and the character is not held any more");
            Hex.True(!controls.ForgetUser((uint)player, handle), "a second pass finds nothing");
        }
        finally { PartyMatchManager.Reset(); }
    }

    /// <summary><c>[u64 handle][u8 hold]</c> - SA_ADMIN_HOLD_CHARACTER's 9-byte body.</summary>
    static byte[] BuildT188HoldPayload(ulong handle, bool hold)
    {
        var p = new byte[9];
        BitConverter.GetBytes(handle).CopyTo(p, 0);
        p[8] = (byte)(hold ? 1 : 0);
        return p;
    }

    [Test] public static void T188_admin_reset_route_answers_and_logs()
    {
        using var store = StoreWithTwoAccounts();
        var api = NewAdminApi(store);
        try
        {
            DbProxyHandlers.MarkEnteredWorld(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var ok = api.Handle("POST", "/api/reset-character",
                body: "{\"id\":1,\"reason\":\"wedged after a World restart\"}", token: T101Token);
            Hex.True(ok.Status == 200 && ok.Body.Contains("\"cleared\":[\"enter-world\"]"),
                     $"the reset reports what it cleared: {ok.Body}");

            var again = api.Handle("POST", "/api/reset-character", body: "{\"id\":1}", token: T101Token);
            Hex.True(again.Status == 200 && again.Body.Contains("\"cleared\":[]"),
                     $"and running it twice is not an error: {again.Body}");

            var missing = api.Handle("POST", "/api/reset-character", body: "{\"id\":4242}", token: T101Token);
            Hex.True(missing.Status == 404, "an id that is not a character is result 2");
            var noId = api.Handle("POST", "/api/reset-character", body: "{}", token: T101Token);
            Hex.True(noId.Status == 400, "and no id at all is result 3");

            // The character row itself is untouched - the reset is not a wipe.
            var chr = store.GetCharacter(1);
            Hex.True(chr != null && chr.Level == 11, "the character is still there, at its level");
        }
        finally { DbProxyHandlers.ForgetPlayer(1); }
    }
}
