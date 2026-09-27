// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>Arb003:13541-13636. The native parser ignores the officer share attribute.</summary>
public static class GuildIncentiveSheet
{
    public sealed record Rate(int Size, float Minimum, float Maximum);
    public sealed record Data(bool Available, float Commission, int CoolDays, int MasterShare, int MemberShare,
        string Title, string Text, IReadOnlyList<Rate> Rates, IReadOnlyList<(int Rank, int Minimum)> Sizes);
    public static readonly SheetValue<Data> Entry = new("GuildConfig.xml (incentives)",
        "guild incentive rates, money shares and mail text", new(false, 0, 0, 0, 0, "", "", Array.Empty<Rate>(), Array.Empty<(int, int)>()), directory =>
        {
            string path = Path.Combine(directory, "GuildConfig.xml"); if (!File.Exists(path)) return null;
            var root = XDocument.Load(path).Root ?? throw new FormatException("Missing GuildConfig root");
            var rate = root.Element("GuildIncentiveRate") ?? throw new FormatException("Missing incentive rate");
            var share = root.Element("GuildIncentiveShare") ?? throw new FormatException("Missing incentive shares");
            float commission = (float?)rate.Attribute("commission") ?? -1;
            int days = (int?)rate.Attribute("coolTimeDay") ?? -1;
            int master = (int?)share.Attribute("master") ?? 0, member = (int?)share.Attribute("member") ?? 0;
            var rates = rate.Elements("Incentive").Select(x => new Rate((int?)x.Attribute("guildSize") ?? -1,
                (float?)x.Attribute("minRate") ?? -1, (float?)x.Attribute("maxRate") ?? -1)).ToArray();
            var sizes = root.Descendants("GuildSize").Select(x => ((int?)x.Attribute("rank") ?? -1,
                (int?)x.Attribute("accountNumOver") ?? -1)).OrderBy(x => x.Item1).ToArray();
            if (!float.IsFinite(commission) || commission < 0 || commission > 1 || days < 0 || master <= 0 || member <= 0
                || rates.Length == 0 || sizes.Length == 0 || rates.Select(x => x.Size).Distinct().Count() != rates.Length
                || rates.Any(x => x.Size < 0 || !float.IsFinite(x.Minimum) || !float.IsFinite(x.Maximum) || x.Minimum < 0 || x.Maximum < x.Minimum || x.Maximum > 1)
                || sizes.Any(x => x.Item1 < 0 || x.Item2 < 0)) throw new FormatException("Invalid guild incentive data");
            return new(true, commission, days, master, member, (string?)rate.Attribute("mailTitle") ?? "",
                (string?)rate.Attribute("mailtext") ?? "", rates, sizes);
        }, x => x.Rates.Count);
}
