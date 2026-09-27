// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201: Arb085's dungeon/BG controls. The two global wipes are centrally denied.</summary>
public static class QaDungeonCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "reset_dungeon", "set_dungeoncool", "phaselevel", "clear_bf_cool", "set_bf_result",
        "update_bf_score", "get_bf_result_resettime", "dungeon_log",
        "dungeon_onoff", "info_dungeon",
    };
    private static readonly object Gate = new();
    // Native changes the in-memory ContinentData.coolTimeMin, not a character's saved expiry.
    private static readonly Dictionary<int, int> DungeonCoolMinutes = new();
    internal static int? CoolMinutesOverride(int continent)
    { lock (Gate) return DungeonCoolMinutes.TryGetValue(continent, out int n) ? n : null; }
    internal static void ResetForTests() { lock (Gate) DungeonCoolMinutes.Clear(); }

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (!session.InWorld || session.SelectedCharacter == null) return true;
        var args = line.Args; int user = (int)session.PlayerId;
        switch (line.Name.ToLowerInvariant())
        {
            case "reset_dungeon": // Arb044:1134-1206 / Arb039:10042-10110; all World mirrors.
                var party = PartyWiring.Manager.FindByMember(user);
                Broadcast(PartyPackets.AS_RESET_ALL_DUNGEON, PartyPackets.BuildAsResetAllDungeon(
                    PartyPackets.PlanetId, user, party?.Id ?? 0, party?.IsSys ?? false, party?.Count ?? 0));
                break;
            case "set_dungeoncool": // Arb044:3424-3505: destination is the continent owner.
                if (Ints(args, 2, out var cool) && HasContinent(cool[0]))
                {
                    lock (Gate) DungeonCoolMinutes[cool[0]] = cool[1];
                    int world = DungeonRouting.Channels.WorldForContinent(cool[0]) ?? DungeonRouting.Channels.CatchAllWorldId;
                    if (Program.World?.HasLinks(world) == true)
                        Program.World.SendFrame(world, 0x13B4, IntBytes(PartyPackets.PlanetId, user, cool[0], cool[1]));
                }
                break;
            case "phaselevel": // Arb028:7595-7675; save before notifying the user's current World.
                if (Ints(args, 2, out var phase) && HasContinent(phase[0])
                    && store?.SetDungeonPhase(user, phase[0], phase[1], DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == true)
                    ArbiterClientHandlers.SendToWorld(session, 0x15E1, IntBytes(user, phase[0], phase[1]));
                break;
            case "clear_bf_cool": // Arb072:11204-11373: cooldown broadcast, count reset current World.
                Broadcast(0x1525, IntBytes(user));
                ArbiterClientHandlers.SendToWorld(session, 0x1561, IntBytes(user));
                break;
            case "set_bf_result": SetResult(session, store, args, false); break;
            case "update_bf_score": SetResult(session, store, args, true); break;
            case "get_bf_result_resettime":
                DateTimeOffset? stamp;
                lock (Gate)
                {
                    stamp = null;
                    if (stamp == null && store != null)
                    {
                        // Arb071:16627 LoadBattleFieldInfo calls spLoadSeasonInfo(type=2).
                        // A missing row is initialized once to UTC now (SQL:14520-14537).
                        long epoch = store.GetCounterValue("native_bg_season_last_time_2", 0);
                        if (epoch == 0) { epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); store.SetCounterValue("native_bg_season_last_time_2", epoch); }
                        stamp = DateTimeOffset.FromUnixTimeSeconds(epoch);
                    }
                }
                if (stamp is { } t)
                    GmCommandHandlers.SendCustom(session, $"[UTC] YYYY_M_D H:M:S : [{t.Year}]_[{t.Month}]_[{t.Day}] [{t.Hour}]:[{t.Minute}]:[{t.Second}]");
                break;
            case "dungeon_log": // Arb044:7872-7989, process/World state, no SQL.
                if (args.Count == 0) break;
                bool on = args[0].Equals("on", StringComparison.OrdinalIgnoreCase) || args[0] == "1";
                bool off = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) || args[0] == "0";
                if (!on && !off) { GmCommandHandlers.SendCustom(session, "Invalid Params"); break; }
                Broadcast(0x14FE, new[] { on ? (byte)1 : (byte)0 });
                GmCommandHandlers.SendCustom(session, on ? "Dungeon-Log on" : "Dungeon-Log off");
                break;
            case "dungeon_onoff": // Arb040:14174-14255 -> Arb061:7753; spUpdateDungeonOff.
                if (args.Count == 2 && int.TryParse(args[0], out int dungeon) && args[1] is "on" or "off" && store != null)
                {
                    bool enabled = args[1] == "on"; store.SetDungeonEnabled(dungeon, enabled);
                    var payload = new byte[5]; BitConverter.GetBytes(dungeon).CopyTo(payload, 0); payload[4] = enabled ? (byte)1 : (byte)0;
                    Broadcast(0x1580, payload);
                    if (!enabled) MatchWiring.DisableDungeon(dungeon);
                }
                break;
            case "info_dungeon": ShowDungeonInfo(session, store); break;
        }
        return true;
    }

    private static void SetResult(GameSession session, CharacterStore? store, IReadOnlyList<string> args, bool scoreOnly)
    {
        // Native set_bf_result's >5 guard is off by one: it reads seven required arguments.
        if (!Ints(args, scoreOnly ? 2 : 7, out var values)) return;
        var row = BattleFieldSheet.Find(values[0]); if (row == null) return;
        int type = NativeBattlefieldType(row.Type); if (type < 0) return;
        int win, loss, draw, kill, death, assist, field8, field10, grade;
        if (scoreOnly) { win = loss = draw = kill = death = assist = field8 = field10 = 5; grade = values[1]; }
        else
        {
            win = values[1]; draw = values[2]; loss = values[3]; grade = values[4]; field8 = values[5]; field10 = values[6];
            kill = death = assist = 0;
            if (args.Count >= 10)
            {
                if (!Ints(args, 10, out values)) return;
                kill = values[7]; death = values[8]; assist = values[9];
            }
        }
        int user = (int)session.PlayerId;
        if (store?.SetNativeBattlefieldResult(user, type, win, loss, draw, kill, death, assist, field8, field10, grade) != true) return;
        // Arb045:3918-3967 AS_ADMIN_BATTLE_FIELD_RESULT; grade precedes kill/death/assist on the wire.
        ArbiterClientHandlers.SendToWorld(session, 0x13C9,
            IntBytes(user, row.Id, type, win, loss, draw, grade, kill, death, assist, field8, field10));
    }

    internal static int NativeBattlefieldType(string type) => type switch
    {
        // Protocol enum from Arb085:13166-13450. The type text/IDs themselves come from the sheet.
        "StrongholdOccupation" => 0, "PvP" => 1, "PvE" => 2, "Round_PvP" => 3, "Cannon" => 4,
        "Free_Fight" => 5, "BasePointBF" => 6, "StrongholdOccupation_Non_Ranking" => 200,
        "Round_PvP_Non_Ranking" => 203, "Snow_Event" => 204, "Kumas_World" => 206,
        "RoundPve" => 208, "World_Of_Tank" => 209, "Kumas_Boss" => 210, "BountyHunt" => 211, _ => -1,
    };

    private static bool Ints(IReadOnlyList<string> args, int count, out int[] values)
    {
        values = new int[count]; if (args.Count < count) return false;
        for (int i = 0; i < count; i++) if (!int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return false;
        return true;
    }
    internal static byte[] IntBytes(params int[] values)
    {
        var p = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(p, i * 4); return p;
    }
    private static void Broadcast(ushort op, byte[] p)
    {
        var bridge = Program.World; if (bridge == null) return;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, op, p);
    }
    internal static bool HasContinent(int id)
    {
        try
        {
            string path = Path.Combine(HandshakeData.DatasheetDirectory(), "ContinentData.xml");
            return File.Exists(path) && XDocument.Load(path).Descendants("Continent").Any(e => (int?)e.Attribute("id") == id);
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException or FormatException) { return false; }
    }

    internal static byte[] BuildPhaseLoad(byte[] request, CharacterStore.DungeonPhaseState state)
    {
        var p = new byte[21 + state.Rows.Count * 24]; BitConverter.GetBytes(27).CopyTo(p, 0);
        BitConverter.GetBytes(state.Rows.Count * 24).CopyTo(p, 4); p[8] = 1;
        if (request.Length >= 4) request.AsSpan(0, 4).CopyTo(p.AsSpan(9));
        BitConverter.GetBytes(state.ResetEpoch).CopyTo(p, 13);
        for (int i = 0; i < state.Rows.Count; i++) state.Rows[i].CopyTo(p, 21 + i * 24);
        return p;
    }
    internal static byte[] BuildBattlefieldLoad(byte[] request, IReadOnlyList<byte[]> rows)
    {
        var p = new byte[13 + rows.Count * 64]; BitConverter.GetBytes(19).CopyTo(p, 0);
        BitConverter.GetBytes(rows.Count * 64).CopyTo(p, 4); p[8] = 1;
        if (request.Length >= 4) request.AsSpan(0, 4).CopyTo(p.AsSpan(9));
        for (int i = 0; i < rows.Count; i++) rows[i].CopyTo(p, 13 + i * 64);
        return p;
    }
    internal static byte[] BuildDisabledDungeons(IReadOnlyList<int> ids)
    {
        // Arb060:79-149: contiguous raw int list, byte length, full-frame offset 14.
        return IntBytes(new[] { 14, ids.Count * 4 }.Concat(ids).ToArray());
    }

    private static void ShowDungeonInfo(GameSession session, CharacterStore? store)
    {
        if (store != null) QaDungeonInfo.Send(session, store);
    }
}
