// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using System.Text.Json;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// Native end/score records are separate from TeraSharp's custom rating.
/// Arb_part_061.c:18169-18404,19863-19911; field names Arb_part_013.c:5001-5253,7285-7546.
/// cap_bg1:16228+22 (two results), 13025/16076/16230+46 (periodic and final scores).
/// Both are one-way; the native handlers issue no acknowledgment.
/// </summary>
public static class BattlegroundResults
{
    public const ushort BSA_END_BATTLE_FIELD_RESULT_LIST = 0x13D5;
    public const ushort BSA_UPDATE_BATTLE_FIELD_LOG = 0x13E3;

    public sealed record Result(int BattleFieldType, int Outcome, int UserDbId, int Kills,
        int Deaths, int Assists, int Captures, int NativeGradePoint, int Destroys, int BattleFieldId);
    public sealed record TeamScore(string MasterName, int MasterDbId, int PlanetId, int Members,
        int Score, int Kills, int Assists, int Deaths, int Wins, int Losses, int Draws, int Destroys);
    public sealed record ScoreLog(int LogId, TeamScore Blue, TeamScore Red, int CannonWinner, bool Finished);

    public static bool TryResults(byte[] payload, out List<Result> rows)
    {
        rows = new();
        if (payload.Length < 12) return false;
        uint count = BitConverter.ToUInt32(payload, 0), next = BitConverter.ToUInt32(payload, 4);
        int kind = BitConverter.ToInt32(payload, 8);
        if (count > (uint)((payload.Length - 12) / 44)) return false;
        var seen = new HashSet<uint>();
        for (uint i = 0; i < count; i++)
        {
            if (next < 18 || next - 6 > (uint)payload.Length || (uint)payload.Length - (next - 6) < 44
                || !seen.Add(next)) return false;
            int at = (int)(next - 6);
            if (BitConverter.ToUInt32(payload, at) != next) return false;
            int outcome = BitConverter.ToInt32(payload, at + 8), owner = BitConverter.ToInt32(payload, at + 12);
            if ((uint)outcome > 2 || owner <= 0) return false;
            rows.Add(new(kind, outcome, owner, BitConverter.ToInt32(payload, at + 16),
                BitConverter.ToInt32(payload, at + 20), BitConverter.ToInt32(payload, at + 24),
                BitConverter.ToInt32(payload, at + 28), BitConverter.ToInt32(payload, at + 32),
                BitConverter.ToInt32(payload, at + 36), BitConverter.ToInt32(payload, at + 40)));
            next = BitConverter.ToUInt32(payload, at + 4);
        }
        return next == 0;
    }

    public static bool TryScore(byte[] payload, out ScoreLog? score)
    {
        score = null;
        if (payload.Length < 105 || !TryName(payload, 0, out string blue) || !TryName(payload, 4, out string red)) return false;
        TeamScore Team(string name, int at) => new(name,
            BitConverter.ToInt32(payload, at), BitConverter.ToInt32(payload, at + 4),
            BitConverter.ToInt32(payload, at + 8), BitConverter.ToInt32(payload, at + 12),
            BitConverter.ToInt32(payload, at + 16), BitConverter.ToInt32(payload, at + 20),
            BitConverter.ToInt32(payload, at + 24), BitConverter.ToInt32(payload, at + 28),
            BitConverter.ToInt32(payload, at + 32), BitConverter.ToInt32(payload, at + 36),
            BitConverter.ToInt32(payload, at + 40));
        score = new(BitConverter.ToInt32(payload, 8), Team(blue, 12), Team(red, 56),
            BitConverter.ToInt32(payload, 100), payload[104] != 0);
        return true;
    }

    private static bool TryName(byte[] payload, int offsetField, out string value)
    {
        value = "";
        uint offset = BitConverter.ToUInt32(payload, offsetField);
        if (offset == 0) return true;
        if (offset < 111 || offset - 6 > (uint)payload.Length - 2) return false;
        int at = (int)offset - 6;
        for (int end = at; end <= payload.Length - 2; end += 2)
            if (payload[end] == 0 && payload[end + 1] == 0)
            {
                value = Encoding.Unicode.GetString(payload, at, end - at);
                return true;
            }
        return false;
    }

    public static bool FileResults(CharacterStore? store, byte[] payload)
    {
        if (store == null || !TryResults(payload, out var rows)) return true;
        foreach (var row in rows)
        {
            var character = store.GetCharacter(row.UserDbId);
            if (character == null) continue;
            store.AddGameLog(GameLogPackets.CategoryPvp, "battleground.result", character.AccountId,
                row.UserDbId, 0, 0, row.BattleFieldId, row.Kills, 0,
                JsonSerializer.Serialize(new { Result = row, Payload = Convert.ToHexString(payload) }));
        }
        return true;
    }

    public static bool FileScore(CharacterStore? store, byte[] payload)
    {
        if (store != null && TryScore(payload, out var score))
            store.AddGameLog(GameLogPackets.CategoryPvp, "battleground.score", 0, 0, 0, score!.LogId, 0,
                0, 0, JsonSerializer.Serialize(new { Score = score, Payload = Convert.ToHexString(payload) }));
        return true;
    }
}
