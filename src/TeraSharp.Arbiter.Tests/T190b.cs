// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T190b_enter_world_restores_the_existing_party_without_reforming_it()
    {
        // cap_2man_b tap raw13799@0 AS_ENTER_WORLD: frame100..108 / payload94..102.
        // Arb028:15773-15781 obtains the existing PartyId/IsSys; 15847-15850 passes them
        // to the writer. A party-less login uses zero, while a normal party keeps IsSys=0.
        PartyWiring.ResetForTests();
        try
        {
            var pm = PartyWiring.Manager;
            var chr = new FakeCharacter { Id = 1003 };
            byte[] PartyFields() => WorldEntry.BuildEnterWorldPayload(0x80000AF00003, chr)[94..103];
            Hex.Eq(PartyFields(), new byte[9], "an unpartied character sends no party binding");
            pm.Register(P(1, 1003, "New"));
            pm.Register(P(2, 1, "dobb"));
            var joined = pm.OnWorldFrame(PartyPackets.SA_JOIN_PARTY, SaJoinPartyPayload(1003, 1));
            Hex.True(joined.Rejected == null, joined.Rejected ?? "normal party formed");
            var normal = pm.FindByMember(1003)!;
            byte[] normalFields = new byte[9];
            BitConverter.GetBytes(normal.Id).CopyTo(normalFields, 0);
            Hex.Eq(PartyFields(), normalFields, "normal party id is retained without a system-party flag");

            var formed = pm.FormMatchedParty(T138dMembers((1, MatchRole.Dps), (1003, MatchRole.Dps)),
                false, 9781);
            Hex.True(formed.Rejected == null, formed.Rejected ?? "matched party formed");
            var matched = pm.FindByMember(1003)!;
            pm.Unregister(1);
            pm.Register(P(3, 1003, "New"));
            Hex.True(ReferenceEquals(pm.FindByMember(1003), matched),
                "lobby/reselect preserves the same matched party over the suspended normal party");
            var capturedFields = Convert.FromHexString("010000003000F00A01");
            // The capture's process-issued party id is state; preserve the exact IsSys byte.
            BitConverter.GetBytes(matched.Id).CopyTo(capturedFields, 0);
            Hex.Eq(PartyFields(), capturedFields,
                "cap_2man_b raw13799@0: the existing system party is restored in AS_ENTER_WORLD");
        }
        finally { PartyWiring.ResetForTests(); }
    }
}
