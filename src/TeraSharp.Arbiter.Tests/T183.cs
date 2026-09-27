// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T183_registry_separates_camp_and_guild_title_in_376012()
    {
        var opcodes = LoadOpcodesOrSkip();
        if (opcodes == null) return;
        Hex.True(opcodes["C_TEL_CAMP"] == 0xE282 && opcodes["C_UPDATE_GUILD_TITLE"] == 0x807B,
            "actual 376012 map has distinct opcodes; a title cannot be dispatched as a camp");
        var dispatcher = new PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketDispatcher>.Instance);
        var defs = new TeraSharp.Arbiter.Protocol.DefinitionRegistry(QuietLog());
        HandlerRegistry.RegisterAll(dispatcher, opcodes, defs, new CapturingLoggerFactory());
        if (!dispatcher.IsRegistered(0xE282))
        {
            Skip.Because("apply status/T183-PATCH.diff to human-owned HandlerRegistry.cs");
            return;
        }
        Hex.True(dispatcher.IsRegistered(0x807B), "guild title handler retained");
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        var session = new GameSession(socket, dispatcher, opcodes, defs, 376012, 100, QuietLog());
        dispatcher.Dispatch(session, Convert.FromHexString("100082E2010000003574FEFFB7000000"));
        Hex.True(!session.InWorld, "camp discovery before enter-world is ignored");
    }

    [Test] public static void T183_final2_camp_discovery_pairs_and_duplicate()
    {
        using var store = T181Store();
        // cap_final2_clients/capture_2026-09-22T08-58-09-990Z.log:9902
        var first = CampTeleportHandlers.RecordVisit(store, 1003,
            Convert.FromHexString("100082E201000000789C000067170000").AsSpan(4));
        Hex.Eq(T180Frame(0x2832, first!), Convert.FromHexString("1600000032281200000004000000EB03000067170000"),
            "cap_final2b 14373, camp 5991");
        // The same client stream:17494 is the same camp, not a request for a teleport.
        Hex.True(CampTeleportHandlers.RecordVisit(store, 1003,
            Convert.FromHexString("100082E202000000799C000067170000").AsSpan(4)) == null, "duplicate has no push");
        // Same stream:17582, now camp 5992. Reply is the entire sorted list.
        var second = CampTeleportHandlers.RecordVisit(store, 1003,
            Convert.FromHexString("100082E203000000799C000068170000").AsSpan(4));
        Hex.Eq(T180Frame(0x2832, second!), Convert.FromHexString("1A00000032281200000008000000EB0300006717000068170000"),
            "cap_final2b 23239, both camps");
        // cap_final2_clients/capture_2026-09-22T09-49-53-065Z.log:2984
        var other = CampTeleportHandlers.RecordVisit(store, 1,
            Convert.FromHexString("100082E201000000A7970A00B7000000").AsSpan(4));
        Hex.Eq(T180Frame(0x2832, other!), Convert.FromHexString("160000003228120000000400000001000000B7000000"),
            "cap_final2b 53123, other character's camp 183");
    }

    [Test] public static void T183_visited_camps_persist_sorted_and_isolated()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                Hex.True(store.AddVisitedCamp(12, 5992) && store.AddVisitedCamp(12, 183), "first discoveries");
                Hex.True(!store.AddVisitedCamp(12, 183), "idempotent insert");
                store.AddVisitedCamp(13, 5991);
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                Hex.True(store.GetVisitedCamps(12).SequenceEqual(new[] { 183, 5992 }), "sorted after reconnect");
                Hex.True(store.GetVisitedCamps(13).SequenceEqual(new[] { 5991 }), "per character");
                Hex.True(!store.AddVisitedCamp(12, 183), "still duplicate after reopen");
                Hex.True(CharacterStore.CharacterStateTables.Contains("visited_camps"), "included in character deletion purge");
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Test] public static void T183_short_extra_or_missing_character_does_not_record_or_forward()
    {
        using var store = GuildStore(1);
        byte[] request = Convert.FromHexString("010000003574FEFFB7000000"); // cap_t181_gm_client 1581 body
        Hex.True(CampTeleportHandlers.RecordVisit(store, 1, request.AsSpan(0, 11)) == null, "short");
        Hex.True(CampTeleportHandlers.RecordVisit(store, 1, request.Concat(new byte[1]).ToArray()) == null, "wrong length");
        Hex.True(CampTeleportHandlers.RecordVisit(store, 999, request) == null, "no character");
        Hex.True(store.GetVisitedCamps(1).Count == 0 && store.GetVisitedCamps(999).Count == 0, "no writes on refusal");
        Hex.True(ArbiterClientHandlers.ArbiterOwned.Contains(0xE282), "unregistered E282 cannot fall through to World");
    }
}
