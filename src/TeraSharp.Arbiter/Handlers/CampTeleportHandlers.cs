// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Buffers.Binary;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// T183: records discovery of a camp; the actual teleport is C_TELEPORT_TO_CAMP.
/// Arb_part_041.c:12558-12711, Handler_C_TEL_CAMP: read CampId at frame +12,
/// add to User's persisted set and, only when new, send 2832 to that User's World.
/// cap_final2 client 08-58-09-990Z 9902/17582 -> cap_final2b 14373/23239.
/// </summary>
public static class CampTeleportHandlers
{
    public const ushort C_TEL_CAMP = 0xE282;
    public const ushort AS_CAMP_TEL_ADDED = 0x2832;
    public const int BodySize = 12;

    public static bool OnTelCamp(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (!session.InWorld || session.SelectedCharacter == null || Program.Store == null) return true;
        var reply = RecordVisit(Program.Store, (int)session.SelectedCharacter.Id, body.Span);
        if (reply != null)
            ArbiterClientHandlers.SendToWorld(session, AS_CAMP_TEL_ADDED, reply);
        return true; // No S_ response and no AS_BYPASS_FROM_CLIENT forwarding.
    }

    public static byte[]? RecordVisit(CharacterStore store, int userDbId, ReadOnlySpan<byte> body)
    {
        // Full 16-byte observed layout: two client words, then signed CampId.
        // C_UPDATE_GUILD_TITLE is 0x807B in the shipped 376012 map, not E282.
        if (body.Length != BodySize || store.GetCharacter(userDbId) == null) return null;
        int campId = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
        if (!store.AddVisitedCamp(userDbId, campId)) return null;
        return BuildCampList(userDbId, store.GetVisitedCamps(userDbId));
    }

    public static byte[] BuildCampList(int userDbId, IReadOnlyList<int> camps)
    {
        // WorldServer.exe.c:2980580-2980634: ref/byte-size/UserDbId, then i32s.
        var body = new byte[checked(12 + camps.Count * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(body, 18);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), camps.Count * 4);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), userDbId);
        for (int i = 0; i < camps.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12 + i * 4), camps[i]);
        return body;
    }
}
