// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T210Frame(string key)
    {
        var path = FindRepoFile(Path.Combine("data", "t210", "frames.json"))
            ?? throw new FileNotFoundException("tracked T210 frames.json missing");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return Convert.FromHexString(document.RootElement.GetProperty(key).GetProperty("hex").GetString()!);
    }

    /// <summary>S_CARD_DATA: three [u16 count][u16 firstOffset] pairs, offsets frame-relative.</summary>
    private static (int Count, int Offset) T210List(byte[] frame, int index) =>
        (BitConverter.ToUInt16(frame, 4 + index * 4), BitConverter.ToUInt16(frame, 6 + index * 4));

    /// <summary>
    /// T210. The reply to SDB_REQUEST_CARD_DATA after /@perfect_card_collection is right: what
    /// World hands the client is retail's own post-perfect load byte for byte, five identity bytes
    /// aside (the gameId's low byte and the four UTF-16 characters of the name).
    /// cap_bg3_client1 5663 vs cap_2man_b_client2 2705.
    /// </summary>
    [Test] public static void T210_post_perfect_card_load_matches_retail_byte_for_byte()
    {
        if (FixtureOrSkip(Path.Combine("data", "t210", "frames.json"), "T210 frames.json") is null) return;
        var ours = T210Frame("ours_refresh");
        var retail = T210Frame("retail_refresh");
        Hex.True(ours.Length == retail.Length && ours.Length == 2680,
            "both post-perfect loads are 2680 bytes, not " + ours.Length + "/" + retail.Length);
        var differing = Enumerable.Range(0, ours.Length).Where(i => ours[i] != retail[i]).ToArray();
        Hex.True(differing.SequenceEqual(new[] { 18, 42, 44, 46, 48 }),
            "only the gameId byte and the name differ, not " + string.Join(",", differing));

        foreach (var (label, frame) in new[] { ("ours", ours), ("retail", retail) })
        {
            var cards = T210List(frame, 0);
            Hex.True(cards.Count == 218 && cards.Offset == 52, label + ": 218 cards at offset 52");
            // element at its own frame offset: [u16 self][u16 next][u32 template][u32 amount]
            int at = cards.Offset + 4;
            Hex.True(BitConverter.ToUInt32(frame, at) == 300000 && BitConverter.ToUInt32(frame, at + 4) == 20,
                label + ": the first card is template 300000 at its activation amount");
            Hex.True(BitConverter.ToUInt32(frame, 26) == CharacterStore.DefaultCardInfo.PresetAmount,
                label + ": the refresh reports the reset preset amount");
            Hex.True(BitConverter.ToUInt32(frame, 34) == 3 && BitConverter.ToUInt32(frame, 38) == 10980,
                label + ": book level 3, 10980 points");
            Hex.True(T210List(frame, 2).Count == 0, label + ": the refresh claims no book rewards");
        }

        // The very next login on the same retail account reports the presets and rewards again -
        // so the command never deleted them, and neither may we.
        var later = T210Frame("retail_next_login");
        Hex.True(T210List(later, 0).Count == 218 && BitConverter.ToUInt32(later, 26) == 3
            && T210List(later, 1).Count == 3 && T210List(later, 2).Count == 8,
            "retail's next login: 218 cards, preset amount 3, 3 presets, 8 claimed rewards");
    }

    /// <summary>
    /// T210. T190's perfect deleted the presets, combines and claimed book rewards and forced the
    /// preset amount to 1, so the card panel stayed empty for good. It replaces the collection now,
    /// and the one-shot refresh view is what reports the reset.
    /// </summary>
    [Test] public static void T210_perfect_collection_keeps_presets_and_claimed_rewards()
    {
        using var store = StoreWithTwoAccounts();
        long account = store.AccountOf(1);
        Hex.True(account > 0, "character 1 has an account");

        store.SetCardInfo(account, new CharacterStore.CardInfoRow(3, 1, 0));
        store.AddCard(account, 300000, 1);
        Hex.True(store.MountCard(1, 0, 300000), "a card is mounted in preset 0");
        Hex.True(store.AddCardBookReward(account, 7), "a book reward is claimed");

        store.ReplaceCardCollection(account, 1,
            new[] { new CharacterStore.CardRow(300001, 20), new CharacterStore.CardRow(300002, 20) },
            new CharacterStore.CardInfoRow(1, 3, 10980));

        var cards = store.GetAccountCards(account);
        Hex.True(cards.Count == 2 && cards[0].CardTemplateId == 300001 && cards[0].Amount == 20,
            "the collection is replaced by the perfect one");
        var info = store.GetCardInfo(account);
        Hex.True(info.BookLevel == 3 && info.BookPoint == 10980, "level and points are the perfect ones");
        Hex.True(info.PresetAmount == 3, "preset amount survives (CardPresetIndex is left intact)");
        Hex.True(store.GetCardBookRewards(account).Contains(7), "a claimed book reward survives");

        DbProxyHandlers.ResetPerfectCardRefreshForTests();
        DbProxyHandlers.MarkPerfectCardRefresh(account);
        Hex.True(DbProxyHandlers.TakePerfectCardRefresh(account), "the refresh view is armed once");
        Hex.True(!DbProxyHandlers.TakePerfectCardRefresh(account), "and consumed by that one reply");
    }
}
