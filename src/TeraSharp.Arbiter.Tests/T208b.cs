// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Text.Json;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static JsonElement T208bCapture(string capture)
    {
        var path = FindRepoFile(Path.Combine("data", "t208b", "frames.json"))
            ?? throw new FileNotFoundException("tracked T208b frames.json missing");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(capture).Clone();
    }

    private static TimeSpan T208bAt(JsonElement row) =>
        TimeSpan.Parse(row.GetProperty("t").GetString()!, CultureInfo.InvariantCulture);

    /// <summary>
    /// T208b part 2. The wait between a battlefield entrance offer and the request that enters the
    /// player is native, not a bug we introduced: cap_bg1 measures it twice to the millisecond
    /// (offer 12390 15:36:15.135 -> request 12545 15:36:30.123, offer 12535 15:36:29.939 ->
    /// request 13168 15:36:44.938) and cap_bg3's single forced offer shows the same 15 seconds.
    /// A forced /@battlefield has no entrance popup to wait for, so the delay is now a knob.
    /// </summary>
    [Test] public static void T208b_entrance_request_interval_and_frame_match_retail()
    {
        if (FixtureOrSkip(Path.Combine("data", "t208b", "frames.json"), "T208b frames.json") is null) return;
        foreach (string capture in new[] { "cap_bg1", "cap_bg3" })
        {
            var rows = T208bCapture(capture);
            var offers = rows.GetProperty("offers").EnumerateArray().ToArray();
            var requests = rows.GetProperty("requests").EnumerateArray().ToArray();
            Hex.True(offers.Length == requests.Length && offers.Length > 0,
                capture + ": one entrance request per offer for this user");
            for (int i = 0; i < offers.Length; i++)
            {
                double seconds = (T208bAt(requests[i]) - T208bAt(offers[i])).TotalSeconds;
                Hex.True(Math.Abs(seconds - BattlefieldHandoff.DefaultEnterDelaySeconds) < 0.5,
                    $"{capture} offer {offers[i].GetProperty("n").GetInt32()} -> request "
                    + $"{requests[i].GetProperty("n").GetInt32()} is {seconds:F3}s, not the native 15");
                uint user = requests[i].GetProperty("user").GetUInt32();
                var expected = new byte[10];
                BitConverter.GetBytes(10).CopyTo(expected, 0);
                BitConverter.GetBytes(BattlefieldHandoff.AS_REQUEST_ENTER_BATTLEFIELD).CopyTo(expected, 4);
                BitConverter.GetBytes(user).CopyTo(expected, 6);
                Hex.Eq(Convert.FromHexString(requests[i].GetProperty("hex").GetString()!), expected,
                    capture + ": AS_REQUEST_ENTER_BATTLEFIELD is the bare user id");
            }
        }

        var old = Environment.GetEnvironmentVariable(BattlefieldHandoff.EnterDelayVariable);
        try
        {
            foreach (var (raw, want) in new[] {
                ((string?)null, 15), ("", 15), ("0", 0), ("1", 1), ("600", 600),
                ("601", 15), ("-1", 15), ("soon", 15) })
            {
                Environment.SetEnvironmentVariable(BattlefieldHandoff.EnterDelayVariable, raw);
                Hex.True(BattlefieldHandoff.EnterDelaySeconds() == want,
                    $"TERASHARP_BF_ENTER_DELAY '{raw}' -> {want}");
            }
        }
        finally { Environment.SetEnvironmentVariable(BattlefieldHandoff.EnterDelayVariable, old); }
    }

    /// <summary>
    /// T208b part 1. A relog whose stored blob still points inside the battleground only takes
    /// WorldEntry's return-from-instance path when dungeon_id holds that continent. T199 stored the
    /// return point but not the continent, so the frame still carried 115; with the battlefield
    /// World's links not up yet it went to a World that does not own 115 and was never answered at
    /// all (arbiter-bg3.log 20:53:19 - no SA_ENTER_WORLD and no SA_ENTER_WORLD_FAIL, so the T208
    /// retry never ran either).
    /// </summary>
    [Test] public static void T208b_battlefield_entry_stores_the_continent_it_must_return_from()
    {
        using var store = StoreWithTwoAccounts();
        var town = new CharacterStore.SystemReturnPoint(7005, 0, -1109, 7131, 2172);
        Hex.True(store.SaveBattlefieldReturn(1, 115, town), "battlefield entry stores continent + return point");

        Hex.True(store.GetSystemReturn(1) == town, "13CB's return point survives unchanged");
        var saved = store.GetDungeonReturn(1)!;
        Hex.True(saved.DungeonId == 115 && saved.Zone == 7005
            && (int)saved.X == -1109 && (int)saved.Y == 7131 && (int)saved.Z == 2172,
            "GetDungeonReturn now names the battlefield continent and the town to return to");

        // The condition WorldEntry.BuildEnterWorldPayload gates the substitution on.
        var blob = CrossWorldHandoff.StampLocation(new byte[15312],
            new CrossWorldHandoff.Teleport(0, 0, 10, 115, 0x0AF03922, 9368.1f, 99881.3f, 6756.6f, 5,
                Array.Empty<byte>()), 10);
        Hex.True(saved.DungeonId == BitConverter.ToInt32(blob, 236) && saved.Zone > 0,
            "a blob saved inside the battleground now matches dungeon_id, so the return point wins");

        Hex.True(store.ClearDungeonReturn(1) && store.GetDungeonReturn(1) == null,
            "a completed return clears it again (native CleanSysReturnLoc)");
        Hex.True(store.GetDungeonReturn(2) == null, "an untouched character has no return point");
    }
}
