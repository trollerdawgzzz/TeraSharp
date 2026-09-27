// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    static Dictionary<int, byte[]> T190CardFrames()
    {
        string path = FindRepoFile(Path.Combine("data", "t190", "cards", "frames.json"))
            ?? throw new FileNotFoundException("T190 card capture fixture missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("source_record").GetInt32(),
            r => Convert.FromHexString(r.GetProperty("hex").GetString()!));
    }

    static void T190LoadCardSheet()
    {
        string file = FindRepoFile(Path.Combine("data", "t190", "cards", "CardTemplate.xml"))
            ?? throw new FileNotFoundException("T190 card sheet fixture missing");
        Hex.True(CardCollectionSheet.Entry.Load(Path.GetDirectoryName(file)!).FromSheet, "actual card sheet attributes load");
    }

    [Test] public static void T190Cards_captured_card_DB_layouts_and_account_state_round_trip()
    {
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "frames.json"), "T190 card frames.json") is null) return;
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "CardTemplate.xml"), "T190 CardTemplate.xml") is null) return;
        var frames = T190CardFrames();
        T190LoadCardSheet();
        try
        {
            using var store = GuildStore(1);
            store.SetCharacterMoney(1, 10_000_000_000);
            store.AddCard(1, 311034, 20);
            store.SetCardInfo(1, new(1, 1, 60));
            store.MountCard(1, 0, 311034);
            var handlers = FreshHandlers(store);
            byte[] Pair(int request, int response)
            {
                var got = RunHandler1(BitConverter.ToUInt16(frames[request], 4), frames[request][6..], store, handlers);
                Hex.True(got.op == BitConverter.ToUInt16(frames[response], 4), $"card {request}->{response} opcode");
                Hex.Eq(got.body, frames[response][6..], $"cap_2man_b {request}->{response}");
                return got.body;
            }

            Pair(13126, 13127); // nonempty load:20 owned fragments, one mount,60 book points.
            Pair(13319, 13320); // UNMOUNT,19B reply.
            Hex.True(store.GetCardMounts(1).Count == 0, "unmount is persistent");
            Pair(13343, 13344); // MOUNT,19B reply.
            Pair(13358, 13359); // INCREASE,879B reply with complete op9 money atom.
            Pair(13372, 13373); // CHANGE,15B reply.
            Pair(15072, 15075); // REGISTER card311031 amount5, source offset18.
            Hex.True(store.GetAccountCards(1).Single(c => c.CardTemplateId == 311031).Amount == 5,
                "registered quantity persists on account1");
            Hex.True(store.GetCardInfo(1) == new CharacterStore.CardInfoRow(2, 1, 75),
                "book points60+5*3=75 (client2#2423), preserving increased preset count");
            Hex.True(store.GetCardPresetIndex(1) == 1 && store.GetCardMounts(1).Single().CardTemplateId == 311034,
                "selected preset and mounted card survive independent writes");
            Pair(16097, 16098); // ACTIVATE combine3,19B.
            Hex.True(store.GetCardCombines(1).Single() == new CharacterStore.CardCombineRow(3, 1), "combine3 persisted");
            Pair(16128, 16129); // DEACTIVATE combine3,19B.
            Hex.True(store.GetCardCombines(1).Count == 0, "combine3 removed");
            var load = RunHandler1(DbProxyHandlers.SDB_LOAD_2986, frames[13126][6..], store, handlers).body;
            Hex.True(BitConverter.ToInt32(load, 37) == 2 && BitConverter.ToInt32(load, 41) == 1
                && BitConverter.ToInt32(load, 45) == 1 && BitConverter.ToInt32(load, 49) == 75,
                "next World card load returns the updated preset/book state");
        }
        finally { CardCollectionSheet.Entry.UseBuiltIn(); }
    }

    [Test] public static void T190Cards_perfect_collection_matches_captured_refresh_and_full_load()
    {
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "frames.json"), "T190 card frames.json") is null) return;
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "CardTemplate.xml"), "T190 CardTemplate.xml") is null) return;
        var frames = T190CardFrames();
        T190LoadCardSheet();
        try
        {
            using var store = GuildStore(1);
            store.AddCard(1, 311034, 20);
            store.MountCard(1, 0, 311034);
            store.SetCardInfo(1, new(3, 1, 195));
            store.SetCardPresetIndex(1, 0); // actual pre-command state, tap13506->13507.
            store.SetCardCombine(1, 3, 1);
            store.AddCardBookReward(1, 1);
            long other = store.GetOrCreateAccount("t190-other").Id;
            store.AddCard(other, 311034, 7);

            var command = GmCommandParser.Parse("perfect_card_collection");
            Hex.True(GmCommandHandlers.Classify(true, 5, command) == GmDispatch.Local
                && GmCommandHandlers.Classify(true, 0, command) == GmDispatch.NotAuthorised,
                "Arbiter owns the command and retains the existing GM gate");
            var refresh = GmCommandHandlers.PerfectCardCollection(store, 1, 1);
            Hex.Eq(refresh!, frames[15530][6..], "cap_2man_b15530 DBS_REFRESH_CARD_DATA AccountDbId1");
            var reply = RunHandler1(DbProxyHandlers.SDB_LOAD_2986, frames[15533][6..], store);
            Hex.Eq(reply.body, frames[15535][6..], "all3541 payload bytes:218 cards,10980 points,level3,one empty preset");
            Hex.True(store.GetCardCombines(1).Count == 0 && store.GetCardBookRewards(1).Count == 0
                && store.GetCardMounts(1).Count == 0 && store.GetCardPresetIndex(1) == 0,
                "native reset clears the old collection arrangement and rewards");
            Hex.True(store.GetAccountCards(other).Single().Amount == 7, "another account is unchanged");
            var curve = CardCollectionSheet.Entry.Value;
            Hex.True(curve.LevelFor(2499) == 1 && curve.LevelFor(2500) == 2
                && curve.LevelFor(7500) == 3 && curve.LevelFor(50000) == 6, "native inclusive level thresholds");
        }
        finally { CardCollectionSheet.Entry.UseBuiltIn(); }
    }

    [Test] public static void T190Cards_collection_reward_echoes_book_type_and_persists_only_success()
    {
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "frames.json"), "T190 card frames.json") is null) return;
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "CardTemplate.xml"), "T190 CardTemplate.xml") is null) return;
        var frames = T190CardFrames();
        // One captured successful layout and one captured refusal layout. The real reward7
        // transaction's failure reason is not in this tap; it is NOT a template denylist.
        var ok = DbProxyHandlers.BuildCollectionBookReward(frames[15958][6..], true, () => 10156);
        Hex.Eq(ok.Reply, frames[15959][6..], "all877 reply bytes, BookType1 and RewardId1");
        var no = DbProxyHandlers.BuildCollectionBookReward(frames[16035][6..], false,
            () => throw new Exception("failed transaction must not allocate"));
        Hex.Eq(no.Reply, frames[16036][6..], "real reward7 refusal preserves op7 ItemDbId0");

        using var store = GuildStore(1);
        var got = RunHandler1(DbProxyHandlers.SDB_RECEIVE_COLLECTION_BOOK_REWARD, frames[15958][6..], store).body;
        var expected = frames[15959][6..];
        Array.Copy(got, 21 + 16, expected, 21 + 16, 4); // allocated item ID alone is local DB state.
        Hex.Eq(got, expected, "live successful handler matches the whole reply apart from allocated ID");
        Hex.True(store.GetCardBookRewards(1).SequenceEqual(new[] { 1 })
            && store.GetInventoryItems(1).Single().TemplateId == 69000, "successful reward and item persist together");

        // Decompile-marked missing-user refusal, Arb063:18930-18931. Same captured reply
        // shape; do not claim this was why the real, present user failed reward7.
        using var missing = new CharacterStore(":memory:", QuietLog());
        var refused = RunHandler1(DbProxyHandlers.SDB_RECEIVE_COLLECTION_BOOK_REWARD, frames[16035][6..], missing);
        Hex.Eq(refused.body, frames[16036][6..], "no user: failure0 and unchanged atoms");
        Hex.True(missing.GetCardBookRewards(1).Count == 0 && missing.GetInventoryItems(1).Count == 0,
            "refusal writes neither inventory nor claimed reward");
    }
}
