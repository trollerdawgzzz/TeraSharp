// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_timeline_commands_broadcast_native_patches_and_reset_only_QA_intervals()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-timeline-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string? oldDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET"); using var env = new T185Environment(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir);
            File.WriteAllText(Path.Combine(dir, "ContinentData.xml"), "<ContinentData><Continent id='9781'/></ContinentData>");
            File.WriteAllText(Path.Combine(dir, "DungeonConstraint.xml"), "<DungeonConstraint><Constraint continentId='9781'><EnableTimeList/></Constraint></DungeonConstraint>");
            File.WriteAllText(Path.Combine(dir, "EventMatching.xml"), "<EventMatching><Event id='44' active='true' type='Dungeon'><EnableTimeList/></Event><Event id='45' active='true' type='BattleField'/></EventMatching>");
            string map = Path.Combine(dir, "map.json"); File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 51, 0); using var world13 = new T192WorldPeer(bridge, 52, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); client.Session.PlayerId = 1; client.Session.GameId = 2013001; client.Session.Account.Name = "t201-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "timeline" }; client.Session.EnterWorld();
            var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            string Message() => Encoding.Unicode.GetString(client.Frame()[6..]).TrimEnd('\0');
            T181WithOperators(null, () =>
            {
                foreach (string name in QaTimelineCommands.Names) Run(name + " 9781 0 100 200");
                Hex.True(main.Available == 0 && world13.Available == 0 && client.Available == 0, "all six controls refused before sends");
            });
            T181WithOperators("t201-op", () =>
            {
                Run("add_timeline_dungeon 9781 0 100 200");
                var frame = main.Frame(); Hex.Eq(world13.Frame(), frame, "native schedule broadcast reaches every registered World");
                Hex.True(BitConverter.ToUInt16(frame, 4) == 0x1583 && frame.Length == 52 && BitConverter.ToInt32(frame, 6) == 1
                    && BitConverter.ToInt32(frame, 10) == 22 && BitConverter.ToInt32(frame, 14) == 0 && BitConverter.ToInt32(frame, 18) == 9781,
                    "native1583 header/count/first/type/dungeon");
                Hex.Eq(frame[22..], Convert.FromHexString("16000000000000000000000000000000010108000000100E0000201C0000"),
                    "native30B QAinterval: self22,next0,uid0,QA/enabled1,all-days8,3600/7200 seconds");
                Hex.True(Message() == "구간 추가 완료.", "native add confirmation");
                Run("show_timeline_dungeon 9781"); Hex.True(Message() == "던전 9781 오픈 시간:", "native diagnostic heading");
                for (int i = 0; i < 7; i++) Hex.True(Message().Contains("01:00 - ") , "all-days expands to seven one-hour intervals");
                Run("add_timeline_eventmatching 44 7 100 200");
                var eventFrame = main.Frame(); Hex.Eq(world13.Frame(), eventFrame, "event schedule also broadcasts");
                Hex.True(BitConverter.ToInt32(eventFrame, 14) == 1 && BitConverter.ToInt32(eventFrame, 40) == 0, "type1 and Sunday enum0"); Message();
                Run("show_timeline_eventmatching 44"); Message(); Hex.True(Message() == "일요일 01:00 - 일요일 02:00", "event timeline union");
                Run("add_timeline_eventmatching 45 0 100 200");
                Hex.True(Message() == "type=BattleField인 지령서는 수정할 수 없습니다." && main.Available == 0, "native battlefield-event rejection");
                foreach (var (name, type) in new[] { ("reset_timeline_dungeon", 0), ("reset_timeline_eventmatching", 1) })
                {
                    Run(name); Hex.Eq(main.Frame(), T180Frame(0x1582, QaDungeonCommands.IntBytes(0, 0, type)), "reset restores permanent-only initialization");
                    world13.Frame(); Hex.True(Message() == "시간표 초기화 완료.", "native reset confirmation");
                }
                Run("show_timeline_dungeon 9781"); Hex.True(Message() == "던전 9781: 언제나 닫혀 있음.", "reset preserves initially closed sheet timeline");
            });
        }
        finally
        {
            T185Environment.SetWorld(null); QaTimelineCommands.ResetForTests(); Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDir);
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }

    [Test] public static void T201_native_competition_records_improve_persist_rank_and_delete_only_the_caller()
    {
        string path = Path.Combine(Path.GetTempPath(), "t201-competition-" + Guid.NewGuid() + ".db");
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var acct = store.GetOrCreateAccount("competition");
                store.CreateCharacter(new CharacterRecord { AccountId = acct.Id, Name = "First", Class = 0 });
                store.CreateCharacter(new CharacterRecord { AccountId = acct.Id, Name = "Second", Class = 1 });
                var first = store.UpdateCompetitionResult(1, 3203, 15, 0, 3, 1000)!;
                Hex.Eq(QaCompetitionCommands.BuildResult(3, 1000, first), Convert.FromHexString("1D0047F20103000000E803000000000000000000000000000000000000"),
                    "Arb065:10374 F247 new record uses improved1 and old0/0");
                var worse = store.UpdateCompetitionResult(1, 3203, 15, 0, 2, 900)!;
                Hex.True(!worse.Improved && worse.OldStage == 3 && worse.OldTime == 1000, "lower stage never replaces a higher-stage record");
                var better = store.UpdateCompetitionResult(1, 3203, 15, 0, 3, 900)!;
                Hex.True(better.Improved && better.OldTime == 1000, "same stage faster clear improves");
                store.UpdateCompetitionResult(2, 3203, 15, 1, 4, 5000);
                store.UpdateCompetitionResult(1, 3126, 15, 0, 7, 1000);
            }
            using (var reopened = new CharacterStore(path, QuietLog()))
            {
                var rows = QaCompetitionCommands.Rank(reopened.GetCompetitionScores(3203, 15), 16);
                Hex.True(rows.Count == 2 && rows[0].CharacterId == 2 && rows[0].Level == 4 && rows[0].Score == 5000
                    && rows[1].CharacterId == 1 && rows[1].Score == 900, "board persists and orders stage descending before time ascending");
                Hex.True(reopened.GetCompetitionScores(3203, 14).Count == 0, "season scope");
                reopened.DeleteCompetitionResult(1, 3203, 15);
                Hex.True(reopened.GetCompetitionScores(3203, 15).Single().CharacterId == 2 && reopened.GetCompetitionScores(3126, 15).Count == 1,
                    "personal clear preserves other users and other continents");
            }
        }
        finally { SqliteConnection.ClearAllPools(); foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Test] public static void T201_competition_commands_require_operator_and_both_native_sheet_gates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-rank-commands-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string? oldDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET"); using var env = new T185Environment(null);
        using var store = new CharacterStore(":memory:", QuietLog());
        var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!; object? oldStore = prop.GetValue(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir); prop.SetValue(null, store);
            File.WriteAllText(Path.Combine(dir, "ContinentData.xml"), "<ContinentData><Continent id='3203'/><Continent id='9034'/><Continent id='3126'/></ContinentData>");
            File.WriteAllText(Path.Combine(dir, "CompetitionDungeon.xml"), "<CompetitionDungeon><Dungeon id='3203'/><Dungeon id='9034'/><Dungeon id='3126'/></CompetitionDungeon>");
            File.WriteAllText(Path.Combine(dir, "Leaderboards.xml"), "<Leaderboards><ContentsTypeList type='dungeon'><ContentInfo id='3203'/><ContentInfo id='3126' seasonOut='true'/></ContentsTypeList></Leaderboards>");
            string map = Path.Combine(dir, "map.json"); File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            var account = store.GetOrCreateAccount("rank-op"); int owner = store.CreateCharacter(new CharacterRecord { AccountId = account.Id, Name = "Ranker", Class = 1 });
            client.Session.PlayerId = (uint)owner; client.Session.GameId = 2013002; client.Session.Account.Name = "rank-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = (uint)owner, Name = "Ranker", Class = 1 }; client.Session.EnterWorld();
            var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            T181WithOperators(null, () =>
            {
                foreach (string name in QaCompetitionCommands.Names) Run(name + " 3203 3 1000");
                Hex.True(client.Available == 0 && store.GetCompetitionScores(3203, RankingBoards.CurrentSeason).Count == 0, "all competition controls reject nonoperators before writes/sends");
            });
            T181WithOperators("rank-op", () =>
            {
                Run("update_pverank 9034 3 1000"); Run("update_pverank 3126 3 1000");
                Hex.True(client.Available == 0 && store.GetCompetitionScores(9034, RankingBoards.CurrentSeason).Count == 0
                    && store.GetCompetitionScores(3126, RankingBoards.CurrentSeason).Count == 0, "missing board and seasonOut board are refused even with competition rows");
                Run("update_pverank 3203 3 1000");
                Hex.Eq(client.Frame(), Convert.FromHexString("1D0047F20103000000E803000000000000000000000000000000000000"), "central dispatch emits native F247 for eligible dungeon");
                Run("rank_sort"); Hex.True(store.GetCompetitionScores(3203, RankingBoards.CurrentSeason).Single().Score == 1000 && client.Available == 0, "synchronous ranking remains immediately readable without a flush packet");
                Run("clear_pverank_player 3203"); Hex.True(store.GetCompetitionScores(3203, RankingBoards.CurrentSeason).Count == 0 && client.Available == 0, "personal clear removes record without an invented reply");
            });
        }
        finally
        {
            prop.SetValue(null, oldStore); Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDir);
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }
}
