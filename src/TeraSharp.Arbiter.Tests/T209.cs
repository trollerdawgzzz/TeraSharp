// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T209 - the creation record, generated instead of cloned.
//
// The test that matters is T209_the_generated_blob_matches_the_capture: it builds the record from
// zeros with the captured character's own inputs and compares it to data/starter_blob.bin. The
// comparison masks two things and NOTHING else, and asserts the mask is exactly those two, so a
// future field that goes missing cannot hide behind a widening exception:
//
//   1. the eleven padding bytes above a bool or u8 field, which the real Arbiter leaves
//      uninitialised (two captures of the same state carry different values there);
//   2. the three date structs that carry the capture's own wall clock, each of which is checked
//      separately for round-tripping the time it was given.
// =============================================================================================
public static partial class Tests
{
    /// <summary>The captured blob, or null when the fixture is not in this tree.</summary>
    static byte[]? T209Capture()
    {
        string? path = FindRepoFile(Path.Combine("data", "starter_blob.bin"));
        return path == null ? null : File.ReadAllBytes(path);
    }

    /// <summary>The seed that describes the captured character: account 1 named "1", second slot,
    /// level 1, the sheet's default facing and bag size.</summary>
    static StarterBlob.Seed T209Seed(DateTime when) => new(
        AccountDbId: 1, AccountName: "1", SlotOrdinal: 2, Level: 1,
        Direction: StarterBlob.DirectionFromDegrees(-18), InvenSlotCount: 40, CreatedUtc: when);

    [Test] public static void T209_the_generated_blob_matches_the_capture()
    {
        var captured = T209Capture();
        if (captured == null) { Skip.Because("data/starter_blob.bin missing"); return; }
        Hex.True(captured.Length == StarterBlob.Size, "the fixture is the 15312-byte record");

        var generated = StarterBlob.Generate(T209Seed(new DateTime(2026, 9, 13, 5, 48, 51, DateTimeKind.Utc)));
        Hex.True(generated.Length == StarterBlob.Size, "so is the generated one");

        // Everything Build() writes is identity, and Build patches it onto either template; this
        // test is about the bytes Build does NOT touch. Apply the capture's identity so the two
        // are comparable, exactly as character creation would.
        var identity = new CharacterIdentity
        {
            Race = BitConverter.ToInt32(captured, StarterBlob.RaceOffset),
            Gender = BitConverter.ToInt32(captured, StarterBlob.GenderOffset),
            Class = BitConverter.ToInt32(captured, StarterBlob.ClassOffset),
            Appearance = captured[StarterBlob.AppearanceOffset..(StarterBlob.AppearanceOffset + StarterBlob.AppearanceSize)],
            Appearance2 = BitConverter.ToUInt32(captured, StarterBlob.Appearance2Offset),
            Details = captured[StarterBlob.DetailsOffset..(StarterBlob.DetailsOffset + StarterBlob.DetailsSize)],
            Shape = captured[StarterBlob.ShapeOffset..(StarterBlob.ShapeOffset + StarterBlob.ShapeSize)],
        };
        StarterBlob.TryReadPosition(captured, out int zone, out float x, out float y, out float z);
        int playerId = BitConverter.ToInt32(captured, StarterBlob.PlayerIdOffset);
        string name = StarterBlob.ReadName(captured);
        Hex.True(name == "Test" && playerId == 2, "the fixture is Test, player 2");

        var built = StarterBlob.Build(generated, playerId, name, identity, zone, x, y, z);
        StarterBlob.WriteAccount(built, 1, "1", 2);

        // The mask: the padding runs, plus the three clock structs.
        var mask = new bool[StarterBlob.Size];
        foreach (var (offset, length, _) in StarterBlob.UninitializedRuns)
            for (int i = 0; i < length; i++) mask[offset + i] = true;
        foreach (int at in StarterBlob.ClockDateOffsets)
            for (int i = 0; i < StarterBlob.DateStructSize; i++) mask[at + i] = true;

        var wrong = new List<int>();
        for (int i = 0; i < StarterBlob.Size; i++)
            if (!mask[i] && built[i] != captured[i]) wrong.Add(i);
        Hex.True(wrong.Count == 0,
            "every byte outside the mask is derived, not copied - first bad offset 0x"
            + (wrong.Count > 0 ? wrong[0].ToString("X4") : "none") + ", " + wrong.Count + " total");

        // And the mask is not wider than it claims: every masked byte outside the clock structs
        // really does differ, so none of the eleven is secretly reproducible and left excused.
        foreach (var (offset, length, why) in StarterBlob.UninitializedRuns)
        {
            bool differs = false;
            for (int i = 0; i < length; i++) if (built[offset + i] != captured[offset + i]) differs = true;
            Hex.True(differs, "0x" + offset.ToString("X4") + " is genuinely uninitialised (" + why + ")");
        }
        Hex.True(StarterBlob.UninitializedRuns.Length == 7, "seven padding runs, eleven bytes");
        int padded = 0;
        foreach (var (_, length, _) in StarterBlob.UninitializedRuns) padded += length;
        Hex.True(padded == 11, padded + " padding bytes");
    }

    [Test] public static void T209_the_date_structs_carry_the_clock_they_were_given()
    {
        var when = new DateTime(2026, 9, 13, 2, 52, 52, 840, DateTimeKind.Utc);
        var blob = StarterBlob.Generate(new StarterBlob.Seed(CreatedUtc: when));

        foreach (int at in StarterBlob.ClockDateOffsets)
            Hex.True(StarterBlob.ReadDate(blob, at) == when,
                "0x" + at.ToString("X4") + " round-trips the creation time, milliseconds included");
        foreach (int at in StarterBlob.NeverDateOffsets)
            Hex.True(StarterBlob.ReadDate(blob, at) == StarterBlob.NeverDate,
                "0x" + at.ToString("X4") + " is 1970-01-01, the record's never");

        // The nanosecond field is what proves the struct's shape: 840 ms is 840000000 ns.
        Hex.True(BitConverter.ToUInt32(blob, StarterBlob.ClockDateOffsets[0] + 12) == 840_000_000u,
            "the fraction is nanoseconds, which is how the capture's 840 ms and 793 ms decoded");

        // A date the struct cannot hold becomes the never date rather than wrapping.
        var odd = StarterBlob.Generate(new StarterBlob.Seed(CreatedUtc: DateTime.MinValue));
        Hex.True(StarterBlob.ReadDate(odd, StarterBlob.ClockDateOffsets[0]) != default,
            "a default creation time still writes a readable date");
    }

    [Test] public static void T209_the_generated_record_is_correct_where_the_clone_was_wrong()
    {
        // The captured template carried the CAPTURE's account on it, so every character this
        // server created claimed to be account 1's second character. The generated one does not.
        var blob = StarterBlob.Generate(new StarterBlob.Seed(
            AccountDbId: 4242, AccountName: "someone-else", SlotOrdinal: 3));
        Hex.True(BitConverter.ToInt64(blob, StarterBlob.AccountDbIdOffset) == 4242,
            "the account id is the character's own");
        Hex.True(BitConverter.ToInt32(blob, StarterBlob.SlotOrdinalOffset) == 3, "and so is the slot");
        Hex.True(System.Text.Encoding.Unicode.GetString(blob, StarterBlob.AccountNameOffset,
            "someone-else".Length * 2) == "someone-else", "and the account name");

        // A name longer than the field is truncated, never written past its room.
        var big = StarterBlob.Generate(new StarterBlob.Seed(AccountName: new string('x', 200)));
        for (int i = StarterBlob.PlayerIdOffset; i < StarterBlob.PlayerIdOffset + 4; i++)
            Hex.True(big[i] == 0, "an over-long account name cannot reach the playerId field");

        // The values World depends on for a NEW character, each for its own reason.
        Hex.True(BitConverter.ToInt32(blob, StarterBlob.ActPointOffset) == -1,
            "ActPoint is -1 so User::InitActPoint fills it in rather than skipping");
        Hex.True(blob[StarterBlob.TutorialPlayingOffset] == 0,
            "isTutorialPlaying is 0 - User::UpdateUserData refuses to save while it is set");
        Hex.True(BitConverter.ToInt32(blob, StarterBlob.ObserverTypeOffset) == -1,
            "ObserverType is -1: a positive value grants an unchecked teleport");
        Hex.True(blob[StarterBlob.IsAliveOffset] == 1, "a new character is alive");
        Hex.True(BitConverter.ToSingle(blob, StarterBlob.ConditionOffset) == 120.0f,
            "condition is the 120.0 World restamps anyway");

        // The facing the sheet gives, and the mapping that proved it.
        Hex.True(StarterBlob.DirectionFromDegrees(-18) == -3276,
            "dir=-18 maps to the captured -3276, which is what confirmed the offset");
    }

    [Test] public static void T209_the_blob_file_is_an_override_not_a_requirement()
    {
        string? path = FindRepoFile(Path.Combine("data", "starter_blob.bin"));
        string? old = Environment.GetEnvironmentVariable("TERASHARP_STARTER_BLOB");
        try
        {
            // A bad env path does NOT disable the file: CandidatePaths also walks up to the
            // repo's data/ folder. So which branch LoadTemplate takes depends on the tree, and
            // this test checks whichever one this tree can actually reach.
            Environment.SetEnvironmentVariable("TERASHARP_STARTER_BLOB",
                Path.Combine(Path.GetTempPath(), "t209-absent-" + Guid.NewGuid() + ".bin"));
            StarterBlob.ResetTemplateCacheForTests();
            var loaded = StarterBlob.LoadTemplate(new StarterBlob.Seed(AccountDbId: 7));
            Hex.True(loaded.Length == StarterBlob.Size, "LoadTemplate always yields the record");

            if (StarterBlob.LoadTemplateFile() == null)
            {
                // A tree with no fixture - the public mirror. This is the branch that proves the
                // file is no longer required: before T209 this threw FileNotFoundException and
                // character creation was refused.
                Hex.True(BitConverter.ToInt64(loaded, StarterBlob.AccountDbIdOffset) == 7,
                    "with no file the record is generated and the seed reached it");
            }
            else
            {
                // A tree with the fixture - the override wins, verbatim, seed ignored.
                Hex.Eq(loaded, StarterBlob.LoadTemplateFile()!, "the override is served verbatim");
                Hex.True(BitConverter.ToInt64(loaded, StarterBlob.AccountDbIdOffset)
                         != 7, "and the seed does not overwrite an operator's own capture");
            }

            if (path != null)
            {
                Environment.SetEnvironmentVariable("TERASHARP_STARTER_BLOB", path);
                StarterBlob.ResetTemplateCacheForTests();
                Hex.Eq(StarterBlob.LoadTemplate(), File.ReadAllBytes(path),
                    "an explicit TERASHARP_STARTER_BLOB is honoured");
            }

            // And the generate path is reachable on its own in every tree.
            var made = StarterBlob.Generate(new StarterBlob.Seed(AccountDbId: 7));
            Hex.True(made.Length == StarterBlob.Size
                     && BitConverter.ToInt64(made, StarterBlob.AccountDbIdOffset) == 7,
                "Generate stands alone, fixture or no fixture");

            // A file of the wrong length is still a bad deploy, not something to generate over.
            string truncated = Path.Combine(Path.GetTempPath(), "t209-short-" + Guid.NewGuid() + ".bin");
            File.WriteAllBytes(truncated, new byte[16]);
            Environment.SetEnvironmentVariable("TERASHARP_STARTER_BLOB", truncated);
            StarterBlob.ResetTemplateCacheForTests();
            bool threw = false;
            try { StarterBlob.LoadTemplate(); } catch (InvalidDataException) { threw = true; }
            Hex.True(threw, "a truncated override throws rather than being silently ignored");
            File.Delete(truncated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERASHARP_STARTER_BLOB", old);
            StarterBlob.ResetTemplateCacheForTests();
        }
    }
    /// <summary>
    /// T209 - the starter kit without the file, behind <c>economy.synthItemRecords</c>. The item
    /// LIST always came from CreateCharData.xml; what the file supplied was the 536-byte record
    /// each item is cut from. The synthesised payload has to be the same shape and the same named
    /// fields, because World reads those and nothing else.
    /// </summary>
    [Test] public static void T209_the_starter_kit_can_be_built_without_the_inventory_blob()
    {
        string? path = FindRepoFile(Path.Combine("data", "starter_inventory.bin"));
        if (path == null) { Skip.Because("data/starter_inventory.bin missing"); return; }
        var captured = File.ReadAllBytes(path);

        const int glaiver = 12, playerId = 2;
        const uint reqId = 7;
        var copied = TeraSharp.Arbiter.World.StarterInventory.Build(captured, glaiver, playerId, reqId);
        var made = TeraSharp.Arbiter.World.StarterInventory.BuildSynthetic(glaiver, playerId, reqId);
        Hex.True(copied != null && made != null, "both paths build a payload for the glaiver kit");

        // Same length, same 13-byte header: the wire shape does not change with the setting.
        Hex.True(made!.Length == copied!.Length, "the synthesised payload is the same size");
        Hex.Eq(made[..13], copied[..13], "and carries the same list header, reqId and flag");

        // Every named field matches; only the unnamed remainder of each record differs.
        int size = TeraSharp.Arbiter.World.DbProxyHandlers.StarterInventoryItemSize;
        int start = TeraSharp.Arbiter.World.DbProxyHandlers.StarterInventoryItemStart;
        int records = (copied.Length - start) / size;
        Hex.True(records == 6, records + " records in the glaiver kit");
        for (int i = 0; i < records; i++)
        {
            int at = start + i * size;
            foreach (int field in new[]
            {
                TeraSharp.Arbiter.World.StarterInventory.RecordIdOffset,
                TeraSharp.Arbiter.World.StarterInventory.RecordTemplateIdOffset,
                TeraSharp.Arbiter.World.StarterInventory.RecordAmountOffset,
                TeraSharp.Arbiter.World.StarterInventory.RecordPocketOffset,
                TeraSharp.Arbiter.World.StarterInventory.RecordSlotOffset,
                TeraSharp.Arbiter.World.DbProxyHandlers.StarterInventoryOwnerOffset,
            })
                Hex.True(BitConverter.ToInt32(made, at + field) == BitConverter.ToInt32(copied, at + field),
                    "record " + i + " field +" + field + " agrees with the captured one");
        }

        // T209c part 2: the setting is ON unless it is turned off, and both spellings work.
        string? old = Environment.GetEnvironmentVariable("TERASHARP_SYNTH_ITEM_RECORDS");
        try
        {
            foreach (var (value, expected) in new (string?, bool)[]
                { (null, true), ("", true), ("0", false), ("no", false), ("1", true), ("true", true), ("True", true) })
            {
                Environment.SetEnvironmentVariable("TERASHARP_SYNTH_ITEM_RECORDS", value);
                TerasConfig.ResetForTests();
                Hex.True(TeraSharp.Arbiter.World.DbProxyHandlers.SynthItemRecords == expected,
                    "synthItemRecords is " + expected + " for " + (value ?? "unset"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERASHARP_SYNTH_ITEM_RECORDS", old);
            TerasConfig.ResetForTests();
        }
    }
    // ===================================================================== T209c

    /// <summary>
    /// T209c - SA_LOAD_GUARD (0x147D) with no guards. The family was misnamed AS_PROMOTION_* and
    /// was answered by replaying 23 guard records out of a capture of a server that HAD castles.
    /// This one has none, and retail's own walk emits no AS_LOAD_GUARD frame when the guard tree
    /// is empty (Arb_part_074.c:4035 -> Arb_part_081.c:3130), so the correct answer is the
    /// election state and the finish marker, nothing between them.
    /// </summary>
    [Test] public static void T209c_load_guard_sends_the_election_state_and_finish_with_no_records()
    {
        var frames = RunHandler(DbProxyHandlers.SA_LOAD_GUARD, Array.Empty<byte>(), 2);

        Hex.True(frames[0].op == DbProxyHandlers.AS_ELECTION_STATE,
            "first frame is AS_ELECTION_STATE 0x1484, not a list header");
        Hex.Eq(frames[0].body, BitConverter.GetBytes(DbProxyHandlers.ElectionStateNone),
            "it carries one u32 - the lord-election state - and 0 is no election");
        Hex.True(frames[1].op == DbProxyHandlers.AS_LOAD_GUARD_FINISH
                 && frames[1].body.Length == 0,
            "last frame is AS_LOAD_GUARD_FINISH 0x1480 with an empty payload");

        foreach (var f in frames)
            Hex.True(f.op != DbProxyHandlers.AS_LOAD_GUARD,
                "and not one AS_LOAD_GUARD 0x147E - this planet has no castles");

        // The opcodes kept their numbers; only their names and the record loop changed.
        Hex.True(DbProxyHandlers.SA_LOAD_GUARD == 0x147D
                 && DbProxyHandlers.AS_LOAD_GUARD == 0x147E
                 && DbProxyHandlers.AS_LOAD_GUARD_FINISH == 0x1480
                 && DbProxyHandlers.AS_ELECTION_STATE == 0x1484,
            "the four opcodes are the ones the Arbiter's own name table gives");
    }

    /// <summary>
    /// T209c - the answer does not depend on any file. The handler used to return false when
    /// promotions_147E.bin was missing, which handed the request to the replay table; now there
    /// is nothing to miss, so the reply is the same whatever is or is not in data/.
    /// </summary>
    [Test] public static void T209c_load_guard_needs_no_data_file()
    {
        // Twice, and byte-identical: nothing is cached off disk and nothing is stamped with a clock.
        var first = RunHandler(DbProxyHandlers.SA_LOAD_GUARD, Array.Empty<byte>(), 2);
        var second = RunHandler(DbProxyHandlers.SA_LOAD_GUARD, new byte[] { 1, 2, 3, 4 }, 2);
        Hex.True(first.Count == second.Count, "the same two frames either way");
        for (int i = 0; i < first.Count; i++)
        {
            Hex.True(first[i].op == second[i].op, "frame " + i + " keeps its opcode");
            Hex.Eq(second[i].body, first[i].body, "frame " + i + " is byte-identical, request bytes ignored");
        }

        // The retired file is not referenced anywhere in the shipped source any more.
        string? source = FindRepoFile(Path.Combine("src", "TeraSharp.Arbiter", "World", "DbProxyHandlers.cs"));
        if (source != null)
            Hex.True(!File.ReadAllText(source).Contains("LoadDataFile(\"promotions_147E.bin\""),
                "DbProxyHandlers no longer loads promotions_147E.bin");
    }

    /// <summary>
    /// T209d - a freshly created character must not be routed to a battleground World.
    ///
    /// <para>The live case, from D:\packetlogs\cap_t209.log. World 10 is the battleground
    /// WorldServer; its SA_WORLD_CONTINENT_LIST roster is 18 rows and the first of them is
    /// continent 5 at levels 22-38 - and continent 5 is where every created character starts
    /// (CreateCharData.xml InitLoc, "start=zone 5 (16260,1253,-4410)" in both arbiter-t209.log
    /// and the pre-T209 arbiter-mail1.log). The roster used to be folded into the same table
    /// ServerConfig.xml seeds, so continent 5 became world 10's, and both creations went out on
    /// world 10's link: tap frames 0x138E at 04:22:33 and 04:22:36, continent 5, instance -1, no
    /// SDB_USER_ENTERWORLD back, client stuck on the loading screen. In arbiter-mail1.log, where
    /// world 10 was never started, the identical character entered on world 0's link.</para>
    /// </summary>
    [Test] public static void T209d_a_battleground_roster_does_not_own_the_creation_continent()
    {
        try
        {
            DungeonRouting.ResetForTest();
            WorldRouting.HasLinks = w => w == 0 || w == 10 || w == 13;

            // World 10's roster exactly as the capture carries it (18 continents, planet 2800).
            int[] bgContinents = { 5, 10, 11, 26, 27, 28, 29, 30, 37, 38, 39, 40, 46, 47, 70, 71, 118, 119 };
            Hex.True(WorldContinentList.Apply(DungeonRouting.Channels, T209dRoster(10, bgContinents)) == 18,
                "world 10 claims its 18 battleground continents");
            Hex.True(WorldContinentList.Apply(DungeonRouting.Channels, T209dRoster(13, new[] { 9781 })) == 1,
                "world 13 claims Velik's Sanctuary");

            // The hand-off still resolves both - that is what the roster is for (T138b).
            Hex.True(DungeonRouting.Channels.WorldForContinent(9781) == 13
                     && DungeonRouting.Channels.WorldForContinent(5) == 10,
                "a roster claim still answers the dungeon hand-off");

            // A plain enter-world does not.
            Hex.True(DungeonRouting.WorldForEnterWorld(T209dEnter(5, uint.MaxValue)) == 0,
                "the new character goes to the catch-all World, not the battleground World");
            Hex.True(DungeonRouting.WorldForEnterWorld(T209dEnter(9781, uint.MaxValue)) == 0,
                "and so does any other continent nobody was configured to own");

            // Config still wins, and an announced channel still wins - the battleground relog
            // in cap_t209 (continent 115, instance -1, world 10's link) keeps working.
            DungeonRouting.Channels.MapContinent(102, 10);
            Hex.True(DungeonRouting.WorldForEnterWorld(T209dEnter(102, uint.MaxValue)) == 10,
                "ServerConfig.xml's owner is still the owner");
            DungeonRouting.Channels.Add(10, T209dChannel(115, 7));
            Hex.True(DungeonRouting.WorldForEnterWorld(T209dEnter(115, uint.MaxValue)) == 10,
                "an announced channel still owns its continent for a plain re-entry");

            // And an instanced entry is untouched.
            Hex.True(DungeonRouting.WorldForEnterWorld(T209dEnter(115, 7)) == 10,
                "the instanced entry resolves through the channel as before");
        }
        finally { DungeonRouting.ResetForTest(); }
    }

    /// <summary>An SA_WORLD_CONTINENT_LIST payload: header then one 16-byte row per continent.</summary>
    static byte[] T209dRoster(int worldId, int[] continents)
    {
        var p = new byte[WorldContinentList.MinPayload + continents.Length * 16];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), continents.Length);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), 22);
        BitConverter.TryWriteBytes(p.AsSpan(8, 4), 2800);
        BitConverter.TryWriteBytes(p.AsSpan(12, 4), worldId);
        for (int i = 0; i < continents.Length; i++)
        {
            int o = WorldContinentList.MinPayload + i * 16;
            BitConverter.TryWriteBytes(p.AsSpan(o, 4), 22);
            BitConverter.TryWriteBytes(p.AsSpan(o + 4, 4), 38);
            BitConverter.TryWriteBytes(p.AsSpan(o + 8, 4), continents[i]);
        }
        return p;
    }

    /// <summary>An AS_ENTER_WORLD payload with just the two fields routing reads.</summary>
    static byte[] T209dEnter(int continentId, uint instance)
    {
        var p = new byte[DungeonRouting.EnterWorldChannelInstanceOffset + 4];
        BitConverter.TryWriteBytes(p.AsSpan(DungeonRouting.EnterWorldContinentOffset, 4), continentId);
        BitConverter.TryWriteBytes(p.AsSpan(DungeonRouting.EnterWorldChannelInstanceOffset, 4), instance);
        return p;
    }

    /// <summary>An SA_ADD_DUNGEON_CHANNEL payload, continent and channel only.</summary>
    static byte[] T209dChannel(int continentId, int channelId)
    {
        var p = new byte[DungeonChannels.AddMinPayload];
        BitConverter.TryWriteBytes(p.AsSpan(DungeonChannels.ContinentIdOffset, 4), continentId);
        BitConverter.TryWriteBytes(p.AsSpan(DungeonChannels.ChannelIdOffset, 4), channelId);
        return p;
    }
}
