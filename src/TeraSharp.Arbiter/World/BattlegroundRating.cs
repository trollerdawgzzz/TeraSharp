// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// BattlegroundRating - T138c part 3. One number per character, moved by one packet.
//
// S_BATTLE_FIELD_RESULT (0x765F). One capture, classic_live2 record 325115, 40 bytes:
//
//   28 00 5F 76                       len 40, opcode 0x765F
//   04  01 00  10 00                  array A: count 1, offset 16
//   08  01 00  18 00                  array B: count 1, offset 24
//   0C  F8 FF FF FF                   i32 rating = -8          <-- the delta
//   10  10 00 00 00  02 00 00 00      A[0], 8 B: here 16, next 0, i32 2
//   18  18 00 00 00  D0 02 00 00
//       00 00 00 00  00 00 00 00      B[0], 16 B: here 24, next 0, i32 720, i32 0, i32 0
//
// and the WRITER agrees on the SHAPE (WorldServer.exe.c:2015570-2015590): after the opcode it
// reserves four u16 ref slots - two arrays - and then writes exactly one i32. The two vectors
// it fills are a vector of 8-byte entries and a vector of 16-byte entries, which is the two
// element strides above. Only the scalar is NAMED: the shipped .def calls it
// `int32 rating # positive for winning, negative(signed bit) for losing`. A[0]'s 2 and B[0]'s
// 720 are a single sample and are passed through as opaque numbers rather than christened.
//
// THE SIGN IS THE WIN FLAG. It is the one field in the frame whose meaning is documented, and
// the live sample (-8, a loss) sits inside the 5..12 band the T138 brief asked for, which is a
// pleasing accident and also the reason the band is not obviously wrong. So <see cref="Roll"/>
// decides the MAGNITUDE and the incoming sign decides the DIRECTION - we never guess a winner
// out of A[0].
//
// WHERE IT IS HOOKED. Nothing in TeraSharp emits this packet: there is no BattleField server.
// What there is, is the tunnel - every W->A client packet for a session passes through
// ArbiterClientHandlers.DeliverTunnelled - so when a real WorldServer/BattleFieldServer does
// send one, <see cref="OnTunnelled"/> rewrites its delta to our roll and applies the same
// number to characters.bg_rating. The client therefore shows exactly what the database stores,
// which is the whole point of doing it on the way past instead of after the fact.
//
// NO MMR. Nothing here is read by MatchQueueManager. The T138 brief is explicit: the rating is
// a leaderboard number, not a matchmaker input.
// =============================================================================================
public static class BattlegroundRating
{
    /// <summary>S_BATTLE_FIELD_RESULT.</summary>
    public const ushort S_BATTLE_FIELD_RESULT = 0x765F;

    /// <summary>The 4-byte client header both this packet and the rest of them carry.</summary>
    public const int HeaderSize = 4;

    /// <summary>Where the signed delta sits, as a PACKET index (body 8).</summary>
    public const int RatingOffset = 12;

    /// <summary>The smallest a result may move the rating. The T138 brief's 5.</summary>
    public const int MinDelta = 5;
    /// <summary>The largest. The T138 brief's 12.</summary>
    public const int MaxDelta = 12;

    /// <summary>The floor. A rating never goes below it, on any number of losses.</summary>
    public const int Floor = 0;

    /// <summary>A[0] and B[0] as the one capture has them, so the default build is that frame.</summary>
    public const int CaptureTeamValue = 2;
    /// <summary>B[0]'s first i32 in the capture.</summary>
    public const int CaptureScoreValue = 720;

    /// <summary>
    /// S_BATTLE_FIELD_RESULT, byte for byte. Defaults reproduce record 325115 exactly when
    /// <paramref name="rating"/> is -8.
    /// </summary>
    public static byte[] Build(int rating, int teamValue = CaptureTeamValue,
        int score = CaptureScoreValue, int unk1 = 0, int unk2 = 0)
    {
        const int AStride = 8, BStride = 16;
        int aAt = HeaderSize + 4 + 4 + 4;          // two array heads and the scalar
        int bAt = aAt + AStride;
        int len = bAt + BStride;

        var p = new byte[len];
        BitConverter.GetBytes((ushort)len).CopyTo(p, 0);
        BitConverter.GetBytes(S_BATTLE_FIELD_RESULT).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)1).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)aAt).CopyTo(p, 6);
        BitConverter.GetBytes((ushort)1).CopyTo(p, 8);
        BitConverter.GetBytes((ushort)bAt).CopyTo(p, 10);
        BitConverter.GetBytes(rating).CopyTo(p, RatingOffset);

        BitConverter.GetBytes((ushort)aAt).CopyTo(p, aAt);
        BitConverter.GetBytes((ushort)0).CopyTo(p, aAt + 2);
        BitConverter.GetBytes(teamValue).CopyTo(p, aAt + 4);

        BitConverter.GetBytes((ushort)bAt).CopyTo(p, bAt);
        BitConverter.GetBytes((ushort)0).CopyTo(p, bAt + 2);
        BitConverter.GetBytes(score).CopyTo(p, bAt + 4);
        BitConverter.GetBytes(unk1).CopyTo(p, bAt + 8);
        BitConverter.GetBytes(unk2).CopyTo(p, bAt + 12);
        return p;
    }

    /// <summary>True when this is an S_BATTLE_FIELD_RESULT long enough to hold the delta.</summary>
    public static bool IsResult(byte[]? packet)
        => packet != null && packet.Length >= RatingOffset + 4
           && BitConverter.ToUInt16(packet, 2) == S_BATTLE_FIELD_RESULT;

    /// <summary>The signed delta the sender put in, or 0 when this is not the packet.</summary>
    public static int ReadDelta(byte[]? packet)
        => IsResult(packet) ? BitConverter.ToInt32(packet!, RatingOffset) : 0;

    /// <summary>Overwrite the delta in place. No-op on anything that is not the packet.</summary>
    public static bool WriteDelta(byte[]? packet, int delta)
    {
        if (!IsResult(packet)) return false;
        BitConverter.GetBytes(delta).CopyTo(packet!, RatingOffset);
        return true;
    }

    /// <summary>
    /// The move one result makes: 5..12 up on a win, 5..12 down on a loss. Never 0, so every
    /// finished battleground shows the player something.
    /// </summary>
    public static int Roll(bool win, Random? rng = null)
    {
        int n = (rng ?? Random.Shared).Next(MinDelta, MaxDelta + 1);
        return win ? n : -n;
    }

    /// <summary>What one result did, kept so a later view packet can show the movement.</summary>
    /// <param name="Previous">The rating before.</param>
    /// <param name="Rating">The rating after, never below <see cref="Floor"/>.</param>
    /// <param name="Delta">What was rolled - which is NOT always
    /// <c>Rating - Previous</c>, because the floor can eat part of a loss.</param>
    public readonly record struct Result(int Previous, int Rating, int Delta)
    {
        /// <summary>What the rating actually moved by after the floor - the number the client must see,
        /// so a player on 3 who loses a roll of 8 is shown -3, not -8 (T138c review fix, restored T138f).</summary>
        public int Applied => Rating - Previous;
    }

    private static readonly ConcurrentDictionary<int, Result> LastResults = new();

    /// <summary>
    /// The last result for a character, if this process has seen one.
    /// <c>S_VIEW_BATTLE_FIELD_RESULT</c>'s def declares <c>uint32 previousrating</c> and
    /// <c>uint32 rating</c> and there is NO capture of that frame, so the packet is not built
    /// here - but the two numbers it wants are kept so that building it later is a layout
    /// problem and not a data problem.
    /// </summary>
    public static Result? LastResultFor(int characterId)
        => LastResults.TryGetValue(characterId, out var r) ? r : null;

    /// <summary>Tests only.</summary>
    public static void Reset() => LastResults.Clear();

    /// <summary>
    /// Roll, store and remember. <paramref name="store"/> may be null - a server running
    /// without a database still shows the client a movement, it just does not keep it.
    /// </summary>
    public static Result Apply(CharacterStore? store, int characterId, bool win, Random? rng = null)
    {
        int delta = Roll(win, rng);
        int previous = store?.GetBgRating(characterId) ?? 0;
        int after = store != null
            ? store.AdjustBgRating(characterId, delta)
            : Math.Max(Floor, previous + delta);
        var r = new Result(previous, after, delta);
        if (characterId > 0) LastResults[characterId] = r;
        return r;
    }

    /// <summary>
    /// A tunnelled S-&gt;C packet on its way to <paramref name="session"/>. When it is an
    /// S_BATTLE_FIELD_RESULT, the incoming SIGN says win or loss, our own roll replaces the
    /// magnitude, the character's row moves by it and the packet is rewritten so the client
    /// shows the same number the database now holds. Returns the applied result, or null when
    /// this was some other packet - which is the common case and costs one u16 compare.
    /// </summary>
    public static Result? OnTunnelled(GameSession? session, byte[]? packet, ILogger? log = null,
        Random? rng = null)
    {
        if (session == null || !IsResult(packet)) return null;
        int characterId = (int)(session.SelectedCharacter?.Id ?? 0);
        bool win = ReadDelta(packet) > 0;
        var r = Apply(Program.Store, characterId, win, rng);
        WriteDelta(packet, r.Applied);
        log?.LogInformation("battleground: character {Id} {Outcome} - rating {From} -> {To}, delta {Delta}",
            characterId, win ? "won" : "lost", r.Previous, r.Rating, r.Delta);
        return r;
    }
}
