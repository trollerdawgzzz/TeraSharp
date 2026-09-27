// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;
using System.Xml;

namespace TeraSharp.Arbiter.World;

/// <summary>T199: custom leaderboard movement, configured in XML rather than native MMR.</summary>
public static class BattlegroundRatingSheet
{
    public const string FileName = "TeraSharpBattlegroundRating.xml";
    public sealed record Bounds(int Minimum, int Maximum);

    // The same XML ships as an editable file and an embedded fallback. There is no second
    // C# copy of its numeric policy, so changing the distributed defaults changes both.
    private static readonly (Bounds Value, string Source) Defaults = ReadDefaultPolicy(
        Path.Combine(AppContext.BaseDirectory, "custom-datasheets", FileName));
    public static readonly SheetValue<Bounds> Entry = new(FileName + " <Rating>",
        "custom battleground rating movement; fallback source: " + Defaults.Source,
        Defaults.Value, Read, _ => 1);

    public static Bounds? Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root?.Name != "TeraSharpBattlegroundRating") return null;
        var rows = root.Elements("Rating").ToArray();
        if (rows.Length != 1 || !int.TryParse((string?)rows[0].Attribute("minDelta"), out int min)
            || !int.TryParse((string?)rows[0].Attribute("maxDelta"), out int max)
            || min <= 0 || max < min || max == int.MaxValue) return null;
        return new(min, max);
    }

    private static Bounds? Read(string dir)
    {
        string path = Path.Combine(dir, FileName);
        // Load/Probe remain scoped to the supplied directory, like every other native sheet.
        // Published defaults are loaded separately; they must not create a partial native
        // Executable/Datasheet tree that shadows the real World's data during discovery.
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    internal static (Bounds Value, string Source) ReadDefaultPolicy(string publishedPath)
    {
        try
        {
            if (File.Exists(publishedPath) && Parse(File.ReadAllText(publishedPath)) is { } value)
                return (value, "published XML " + publishedPath);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { }
        return (ReadEmbedded(), "embedded " + FileName);
    }

    private static Bounds ReadEmbedded()
    {
        using var stream = typeof(BattlegroundRatingSheet).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException("Missing bundled " + FileName);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd()) ?? throw new InvalidOperationException("Invalid bundled " + FileName);
    }
}
