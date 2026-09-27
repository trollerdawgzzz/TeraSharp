// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>T201. Native AccountTraitBase validates package ids before either add or remove.</summary>
public static class QaAccountSheet
{
    public sealed record Packages(IReadOnlySet<int> Ids);
    public static readonly SheetValue<Packages> Entry = new("AccountTrait.xml (QA packages)",
        "validated runtime account packages", new(new HashSet<int>()), directory =>
        {
            string path = Path.Combine(directory, "AccountTrait.xml");
            if (!File.Exists(path)) return null;
            return new(XDocument.Load(path).Descendants("Package")
                .Select(x => (int?)x.Attribute("id") ?? throw new FormatException("Package without id"))
                .ToHashSet());
        }, x => x.Ids.Count);
}

/// <summary>Arb003:19122-19500, VIPSystem/VIPBenefit/VIPShop; missing sheets disable VIP.</summary>
public static class VipSystemSheet
{
    public sealed record Grade(int Level, int Exp, int RewardItem);
    public sealed record Data(bool Available, bool Enabled, IReadOnlyList<Grade> Grades,
        int DailyResetHour, int DungeonId, IReadOnlyList<int> ShopResetHours,
        string Sender, string Title, string Body)
    {
        public int? DungeonResetHour { get; init; }
        public XElement? DungeonConstraint { get; init; }
        public int LevelFor(long exp) => Grades.LastOrDefault(x => exp >= x.Exp)?.Level ?? 0;
        public int ExpFor(int level) => Grades.FirstOrDefault(x => x.Level == level)?.Exp ?? 0;
        public long NextShopReset(long now) => ShopBoundary(now, next: true);
        public long LastShopReset(long now) => ShopBoundary(now, next: false);
        private long ShopBoundary(long nowUnix, bool next)
        {
            if (ShopResetHours.Count == 0) return nowUnix;
            var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix).LocalDateTime;
            var candidates = ShopResetHours.Select(h => now.Date.AddHours(h));
            var result = next ? candidates.FirstOrDefault(x => x.Hour > now.Hour)
                : candidates.LastOrDefault(x => x.Hour <= now.Hour);
            if (result == default) result = now.Date.AddDays(next ? 1 : -1)
                .AddHours(next ? ShopResetHours[0] : ShopResetHours[^1]);
            return new DateTimeOffset(result).ToUnixTimeSeconds();
        }
        // Arb003:4371-4396 compares hours strictly, including the exact-hour case.
        public long DailyBoundary(long nowUnix)
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix).LocalDateTime;
            var reset = now.Date.AddDays(DailyResetHour < now.Hour ? 1 : 0).AddHours(DailyResetHour);
            return new DateTimeOffset(reset).ToUnixTimeSeconds();
        }
    }
    public static readonly SheetValue<Data> Entry = new("VIPSystem.xml + VIPBenefit.xml + VIPShop.xml",
        "QA VIP XP/token, rewards and reset times", new(false, false, Array.Empty<Grade>(), 0, 0,
            Array.Empty<int>(), "", "", ""), Read, x => x.Grades.Count);

    public static Data? Read(string directory)
    {
        string[] paths = { "VIPSystem.xml", "VIPBenefit.xml", "VIPShop.xml" };
        if (paths.Any(x => !File.Exists(Path.Combine(directory, x)))) return null;
        var system = XDocument.Load(Path.Combine(directory, paths[0]));
        var benefits = XDocument.Load(Path.Combine(directory, paths[1]));
        var shop = XDocument.Load(Path.Combine(directory, paths[2])).Root
            ?? throw new FormatException("Missing VIPShop root");
        var settings = system.Descendants("VIPSetting").ToArray();
        if (settings.Length != 1) throw new FormatException("Expected exactly one VIPSetting");
        var setting = settings[0];
        int maximum = (int?)setting.Attribute("maxGrade") ?? -1;
        int hour = (int?)setting.Attribute("dailyTokenResetHour") ?? -1;
        var rewards = new Dictionary<int, int>();
        foreach (var benefit in benefits.Descendants("Benefit"))
        {
            int id = (int?)benefit.Attribute("id") ?? throw new FormatException("Benefit without id");
            int item = (int?)benefit.Elements("Property").FirstOrDefault(p => (string?)p.Attribute("name") == "lvUpToken")?.Attribute("value") ?? 0;
            if (!rewards.TryAdd(id, item)) throw new FormatException("Duplicate VIP Benefit id");
        }
        var grades = setting.Elements("Grade").Select(x => new Grade(
            (int?)x.Attribute("level") ?? throw new FormatException("Grade without level"),
            (int?)x.Attribute("totalExp") ?? throw new FormatException("Grade without totalExp"),
            rewards.GetValueOrDefault((int?)x.Attribute("level") ?? -1)))
            .OrderBy(x => x.Level).ToArray();
        var hours = ((string?)shop.Attribute("resetHour") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).Distinct().OrderBy(x => x).ToArray();
        if (maximum < 0 || grades.Length != maximum + 1 || hour is < 0 or > 23 || hours.Length == 0
            || hours.Any(x => x is < 0 or > 23) || grades.Where((x, i) => x.Level != i || x.Exp < 0
                || x.RewardItem < 0 || (i > 0 && x.Exp < grades[i - 1].Exp)).Any())
            throw new FormatException("Invalid VIP grade/reset data");
        var mails = system.Descendants("LvUpMail").ToArray();
        if (mails.Length != 1) throw new FormatException("Expected exactly one LvUpMail");
        var mail = mails[0];
        int dungeon = (int?)setting.Attribute("vipDungeon") ?? 0;
        string worldPath = Path.Combine(directory, "WorldData.xml"), constraintPath = Path.Combine(directory, "DungeonConstraint.xml");
        int? resetHour = File.Exists(worldPath) ? (int?)XDocument.Load(worldPath).Root?.Element("Dungeon")?.Attribute("resetTime") : null;
        if (resetHour is < 0 or > 23) throw new FormatException("Invalid WorldData Dungeon.resetTime");
        var constraint = File.Exists(constraintPath) ? XDocument.Load(constraintPath).Descendants("Constraint")
            .FirstOrDefault(x => (int?)x.Attribute("continentId") == dungeon) : null;
        return new(true, (bool?)setting.Attribute("vipSystemOn") ?? false, grades, hour,
            dungeon, hours, (string?)mail.Attribute("sender") ?? "",
            (string?)mail.Attribute("title") ?? "", (string?)mail.Attribute("body") ?? "")
            { DungeonResetHour = resetHour, DungeonConstraint = constraint };
    }
}
