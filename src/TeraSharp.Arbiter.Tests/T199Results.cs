// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static JsonDocument? T199ResultFrames()
    {
        string? path = FindRepoFile(Path.Combine("data", "t199-results", "frames.json"));
        if (path == null) { Skip.Because("data/t199-results/frames.json absent"); return null; }
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte[] T199ResultFrame(JsonDocument frames, string capture, int record, int offset = 0)
        => Convert.FromHexString(frames.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            (f.TryGetProperty("source_record", out var source) ? source.GetInt32() : f.GetProperty("n").GetInt32()) == record
            && (!f.TryGetProperty("source_offset", out var at) || at.GetInt32() == offset)).GetProperty("hex").GetString()!);

    [Test] public static void T199_retail_results_and_kills_keep_bytes_except_configured_custom_rating()
    {
        using var frames = T199ResultFrames(); if (frames == null) return;
        using var store = T181Store();
        using var env = new T185Environment(null);
        string map = Path.GetTempFileName(), dir = Path.Combine(Path.GetTempPath(), "t199-rating-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var property = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? previous = property.GetValue(null);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            File.WriteAllText(Path.Combine(dir, BattlegroundRatingSheet.FileName),
                "<TeraSharpBattlegroundRating><Rating minDelta=\"7\" maxDelta=\"7\"/></TeraSharpBattlegroundRating>");
            Hex.True(BattlegroundRatingSheet.Entry.Load(dir).FromSheet, "fixed test policy loaded from XML");
            property.SetValue(null, store);
            var definitions = new DefinitionRegistry(QuietLog());
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            foreach (var (capture, init, result, owner, nativeDelta, customDelta, worldRecord, worldOffset) in new[]
                { ("cap_bg1_client1", 3176, 5410, 1, -33, -7, 16224, 9602),
                  ("cap_bg1_client2", 3014, 5559, 1003, 0, 7, 16223, 1097) })
            {
                using var client = new T185Client(definitions, opcodes, QuietLog());
                client.Session.PlayerId = (uint)owner;
                client.Session.SelectedCharacter = new FakeCharacter { Id = (uint)owner };
                store.SetBgRating(owner, 40);
                byte[] initial = T199ResultFrame(frames, capture, init);
                ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])initial.Clone());
                Hex.Eq(client.Frame(), initial, "retail INIT unchanged, self party type captured");
                foreach (var f in frames.RootElement.GetProperty(capture).EnumerateArray()
                    .Where(f => f.GetProperty("op").GetInt32() is 0x6C76 or 0xAF11))
                {
                    byte[] packet = Convert.FromHexString(f.GetProperty("hex").GetString()!);
                    ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])packet.Clone());
                    Hex.Eq(client.Frame(), packet, "World-owned kill/score packet unchanged");
                }
                byte[] native = T199ResultFrame(frames, capture, result);
                byte[] envelope = T199ResultFrame(frames, "cap_bg1", worldRecord, worldOffset);
                Hex.Eq(TunnelFrames.ParseBypassToClient(envelope[6..])!.ClientPacket, native, "entire retail result from World to client");
                Hex.True(native.Length == 56 && BattlegroundRating.ReadDelta(native) == nativeDelta,
                    "native loser -33, native winner 0; zero is not proof of loss");
                ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])native.Clone());
                byte[] expected = (byte[])native.Clone(); BitConverter.GetBytes(customDelta).CopyTo(expected, 12);
                Hex.Eq(client.Frame(), expected, "only configured custom delta changes; all 52 other bytes exact");
                Hex.True(store.GetBgRating(owner) == 40 + customDelta, "custom leaderboard persists the displayed movement");

                if (owner == 1003)
                {
                    var malformed = (byte[])native.Clone(); malformed[6] = 255; malformed[7] = 255;
                    Hex.True(BattlegroundRating.OnTunnelled(client.Session, malformed) == null, "invalid winner list cannot change rating");
                    var draw = (byte[])native.Clone(); Array.Clear(draw, 4, 4);
                    Hex.True(BattlegroundRating.OnTunnelled(client.Session, draw) == null, "empty winner list is a draw, no invented loss");
                    BattlegroundRating.OnTunnelled(client.Session, new byte[] { 4, 0, 0x90, 0x8E });
                    Hex.True(BattlegroundRating.OnTunnelled(client.Session, (byte[])native.Clone()) == null, "finish clears team context; unknown zero delta is unchanged");
                    byte[] loss = T199ResultFrame(frames, "cap_bg1_client1", 5410);
                    Hex.True(BattlegroundRating.OnTunnelled(client.Session, loss)!.Value.Applied == -7, "known nonzero sign remains the fallback without INIT context");
                    BattlegroundRating.OnTunnelled(client.Session, initial);
                    BattlegroundRating.OnTunnelled(client.Session, new byte[] { 4, 0, 0x66, 0xF2 });
                    Hex.True(BattlegroundRating.OnTunnelled(client.Session, (byte[])native.Clone()) == null, "new login clears previous battleground team");
                }
            }
        }
        finally
        {
            property.SetValue(null, previous); BattlegroundRating.Reset(); BattlegroundRatingSheet.Entry.UseBuiltIn();
            File.Delete(map); Directory.Delete(dir, true);
        }
    }

    [Test] public static void T199_end_results_and_score_snapshots_are_one_way_and_survive_restart()
    {
        using var frames = T199ResultFrames(); if (frames == null) return;
        string path = Path.Combine(Path.GetTempPath(), "t199-results-" + Guid.NewGuid() + ".db");
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                long account = store.GetOrCreateAccount("t199").Id;
                store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "loser", Level = 70 });
                store.CreateCharacter(new CharacterRecord { AccountId = account, Name = "winner", Level = 70 });
                byte[] result = T199ResultFrame(frames, "cap_bg1", 16228, 22)[6..];
                BitConverter.GetBytes(2).CopyTo(result, 24); // captured UserDbId1003 -> this store's winner2
                Hex.True(BattlegroundResults.TryResults(result, out var rows) && rows.Count == 2, "native linked result list decoded");
                Hex.True(rows[0] == new BattlegroundResults.Result(3, 1, 2, 2, 1, 0, 0, 37, 0, 37)
                    && rows[1] == new BattlegroundResults.Result(3, 0, 1, 1, 2, 0, 0, -33, 0, 37), "every native result field pinned");
                RunHandler(BattlegroundResults.BSA_END_BATTLE_FIELD_RESULT_LIST, result, 0, store);
                foreach (var (record, offset) in new[] { (13025, 0), (16076, 0), (16230, 46) })
                    RunHandler(BattlegroundResults.BSA_UPDATE_BATTLE_FIELD_LOG,
                        T199ResultFrame(frames, "cap_bg1", record, offset)[6..], 0, store);
                byte[] final = T199ResultFrame(frames, "cap_bg1", 16230, 46)[6..];
                Hex.True(BattlegroundResults.TryScore(final, out var score) && score!.LogId == 2 && score.Finished
                    && score.Blue.Kills == 2 && score.Blue.Deaths == 1 && score.Red.Kills == 1 && score.Red.Deaths == 2,
                    "final native kill/death totals and finished flag");
                Hex.True(store.GetBgRating(1) == 0 && store.GetBgRating(2) == 0, "native grade points do not double-apply custom rating");
                store.SetBgRating(2, 40);
                BattlegroundRating.Apply(store, 2, true, new Random(199));
                var bad = (byte[])result.Clone(); BitConverter.GetBytes(uint.MaxValue).CopyTo(bad, 4);
                RunHandler(BattlegroundResults.BSA_END_BATTLE_FIELD_RESULT_LIST, bad, 0, store);
                RunHandler(BattlegroundResults.BSA_UPDATE_BATTLE_FIELD_LOG, final[..20], 0, store);
            }
            using var reopened = new CharacterStore(path, QuietLog());
            var results = reopened.QueryGameLog(action: "battleground.result");
            var scores = reopened.QueryGameLog(action: "battleground.score");
            Hex.True(results.Count == 2 && scores.Count == 3, "both records and all three native snapshots survive restart, no malformed partial rows");
            using var stored = JsonDocument.Parse(results.Single(r => r.CharacterId == 2).Extra);
            Hex.True(stored.RootElement.GetProperty("Result").GetProperty("NativeGradePoint").GetInt32() == 37,
                "native +37 retained separately from custom +5..12");
            Hex.True(reopened.GetBgRating(2) is >= 45 and <= 52, "custom rating survives the same restart");
        }
        finally
        {
            BattlegroundRating.Reset();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test] public static void T199_rating_bounds_come_from_XML_and_invalid_ranges_use_bundled_XML()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t199-bounds-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, BattlegroundRatingSheet.FileName);
        try
        {
            File.WriteAllText(path, "<TeraSharpBattlegroundRating><Rating minDelta=\"19\" maxDelta=\"23\"/></TeraSharpBattlegroundRating>");
            Hex.True(BattlegroundRatingSheet.Entry.Load(dir).FromSheet, "normal datasheet directory takes precedence over published defaults");
            for (int i = 0; i < 16; i++)
                Hex.True(BattlegroundRating.Roll(true) is >= 19 and <= 23 && BattlegroundRating.Roll(false) is >= -23 and <= -19,
                    "both directions use changed sheet bounds");
            foreach (var (min, max) in new[] { (0, 12), (12, 5), (1, int.MaxValue) })
            {
                File.WriteAllText(path, $"<TeraSharpBattlegroundRating><Rating minDelta=\"{min}\" maxDelta=\"{max}\"/></TeraSharpBattlegroundRating>");
                Hex.True(!BattlegroundRatingSheet.Entry.Load(dir).FromSheet && BattlegroundRating.MinDelta == 5 && BattlegroundRating.MaxDelta == 12,
                    "invalid range cannot reach Random.Next; exact bundled XML defaults selected");
            }
            Hex.True(DatasheetLoader.All.Contains(BattlegroundRatingSheet.Entry), "startup and check-config report the custom policy sheet");
            File.WriteAllText(path, "<TeraSharpBattlegroundRating><Rating minDelta=\"29\" maxDelta=\"31\"/></TeraSharpBattlegroundRating>");
            var published = BattlegroundRatingSheet.ReadDefaultPolicy(path);
            Hex.True(published.Value == new BattlegroundRatingSheet.Bounds(29, 31)
                && published.Source == "published XML " + path, "editable published XML supplies fallback values and reports its actual source");
            string empty = Path.Combine(dir, "empty"); Directory.CreateDirectory(empty);
            var status = BattlegroundRatingSheet.Entry.Load(empty);
            Hex.True(!status.FromSheet && BattlegroundRatingSheet.Entry.Value == BattlegroundRatingSheet.Entry.BuiltIn
                && status.Line.Contains("fallback source:"), "published fallback does not masquerade as a sheet in an unrelated empty directory");
        }
        finally { BattlegroundRatingSheet.Entry.UseBuiltIn(); Directory.Delete(dir, true); }
    }
}
