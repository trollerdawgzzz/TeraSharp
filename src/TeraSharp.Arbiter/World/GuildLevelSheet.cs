// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>T201: GuildConfigDataSheet, Arb003:13319-13368. No invented curve when absent.</summary>
public static class GuildLevelSheet
{
    public sealed record Level(int Id, long Exp, int Points);
    public sealed record Data(int Maximum, IReadOnlyList<Level> Levels)
    {
        public bool Available => Maximum > 0 && Levels.Count != 0;
        public long MaximumPoints => Levels.Sum(x => (long)x.Points);
        public long MaximumExp => Levels.First(x => x.Id == Maximum).Exp;
        public long ExpFor(int level) => Levels.First(x => x.Id == Math.Min(level, Maximum)).Exp;

        // Native advances only beyond a threshold.
        // It starts at the current level: subtracting XP never downgrades the guild.
        // Arb003:2080-2174, :3434-3500; Arb046:14983-15064.
        public int LevelFor(int current, long exp)
        {
            int result = current;
            foreach (var level in Levels.Where(x => x.Id >= current))
            {
                if (exp <= level.Exp) return result;
                result = level.Id;
            }
            return exp >= MaximumExp ? Maximum : result;
        }

        public long EarnedPoints(int oldLevel, int newLevel)
            => Levels.Where(x => x.Id > oldLevel && x.Id <= newLevel).Sum(x => (long)x.Points);
    }

    public static readonly SheetValue<Data> Entry = new("GuildConfig.xml (GuildLevelTable)",
        "guild QA level/XP curve and point cap", new(0, Array.Empty<Level>()), Read, d => d.Levels.Count);

    /// <summary>Arb033:1011-1035 and Arb028:18393-18426. Hour24 means the next midnight.</summary>
    public sealed record QuestReset(bool Available, DayOfWeek Day, int Hour)
    {
        public long LastReset(long nowUnix)
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix).LocalDateTime;
            int days = ((int)now.DayOfWeek - (int)Day + 7) % 7;
            var reset = now.Date.AddDays(-days).AddHours(Hour);
            if (reset > now) reset = reset.AddDays(-7);
            return new DateTimeOffset(reset).ToUnixTimeSeconds();
        }
    }

    public static readonly SheetValue<QuestReset> QuestEntry = new("GuildQuest/GuildQuestConfig.xml",
        "guild QA first-season eligibility", new(false, default, 0), directory =>
        {
            string path = Path.Combine(directory, "GuildQuest", "GuildQuestConfig.xml");
            if (!File.Exists(path)) return null;
            var root = XDocument.Load(path).Root!;
            if (!Enum.TryParse<DayOfWeek>((string?)root.Attribute("weeklyRewardResetWeekDay"), true, out var day)
                || (int)day < 0 || (int)day > 6) throw new FormatException("Invalid guild weekly reset day");
            int hour = (int?)root.Attribute("weeklyRewardResetHour") ?? -1;
            if (hour < 0 || hour > 24) throw new FormatException("Invalid guild weekly reset hour");
            return new(true, day, hour);
        }, d => d.Available ? 1 : 0);

    public static Data? Read(string directory)
    {
        string path = Path.Combine(directory, "GuildConfig.xml");
        return File.Exists(path) ? Parse(XDocument.Load(path)) : null;
    }

    public static Data Parse(XDocument document)
    {
        var tables = document.Descendants("GuildLevelTable").ToArray();
        if (tables.Length != 1) throw new FormatException("Expected exactly one GuildLevelTable");
        var table = tables[0];
        int maximum = (int?)table.Attribute("maxLevel") ?? 0;
        var levels = table.Elements("GuildLevel").Select(e => new Level(
            (int?)e.Attribute("level") ?? 0, (long?)e.Attribute("guildExpNeeded") ?? -1,
            (int?)e.Attribute("earnGuildPoint") ?? -1)).OrderBy(x => x.Id).ToArray();
        if (maximum < 1 || levels.Length != maximum
            || levels.Where((x, i) => x.Id != i + 1 || x.Exp < 0 || x.Points < 0
                || (i > 0 && x.Exp < levels[i - 1].Exp)).Any())
            throw new FormatException("Invalid GuildLevelTable");
        return new(maximum, levels);
    }
}
