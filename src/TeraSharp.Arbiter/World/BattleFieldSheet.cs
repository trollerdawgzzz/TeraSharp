// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml;
using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// BattleFieldSheet - Executable\Datasheet\BattleFieldData.xml, read once, T157.
//
// World builds the battleground tab (S_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST) from this same
// file: cap_social4 seq 8218 lists exactly the sheet's battlegrounds a level-70 may enter
// (5 10 11 26-30 37-40 46 47 70 118 119 - not 71, 110 or 156, whose level bands exclude him).
// The matcher reads it too, so a battleground taken out of the sheet leaves the tab AND the
// queue. What the Arbiter needs from each <BattleField>: id, type, and <CommonData>'s
// maxTeamMember / minLevel / maxLevel. The healer and lancer numbers are not in the sheet -
// they are the T138 brief's, per battleground type, in MatchComposition.TypeRules.
//
// Located like every other sheet: TERASHARP_DATASHEET, else <TERASHARP_DATA>\Executable\
// Datasheet (HandshakeData.DatasheetDirectory). Missing or unreadable: no battleground is
// offered to the matcher, and MatchWiring says so once.
// =============================================================================================

/// <summary>One <c>&lt;BattleField&gt;</c> row: the parts the matcher uses.</summary>
public sealed record BattleFieldEntry(int Id, string Type, int TeamSize, int MinLevel, int MaxLevel);

/// <summary>BattleFieldData.xml, parsed and cached for the process.</summary>
public static class BattleFieldSheet
{
    public const string FileName = "BattleFieldData.xml";

    private static readonly object Gate = new();
    private static IReadOnlyList<BattleFieldEntry>? _current;
    private static bool _loaded;
    private static string _source = "";

    /// <summary>The rows in file order. Comments are skipped, so a commented-out battleground is gone.</summary>
    public static List<BattleFieldEntry> Parse(string xml) => From(XDocument.Parse(xml));

    /// <summary>The sheet in <paramref name="datasheetDir"/>, or null when it is absent or not XML.</summary>
    public static List<BattleFieldEntry>? Load(string? datasheetDir)
    {
        if (string.IsNullOrEmpty(datasheetDir)) return null;
        var path = Path.Combine(datasheetDir, FileName);
        if (!File.Exists(path)) return null;
        try { return From(XDocument.Load(path)); }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static List<BattleFieldEntry> From(XDocument doc)
    {
        var list = new List<BattleFieldEntry>();
        foreach (var bf in doc.Descendants("BattleField"))
        {
            if (!int.TryParse((string?)bf.Attribute("id"), out int id)) continue;
            var cd = bf.Element("CommonData");
            list.Add(new BattleFieldEntry(id, ((string?)bf.Attribute("type") ?? "").Trim(),
                Int(cd, "maxTeamMember"), Int(cd, "minLevel"), Int(cd, "maxLevel")));
        }
        return list;
    }

    private static int Int(XElement? e, string name)
        => e != null && int.TryParse(((string?)e.Attribute(name))?.Trim(), out int v) ? v : 0;

    /// <summary>
    /// The process's sheet: loaded on first use from <see cref="HandshakeData.DatasheetDirectory"/>
    /// and kept. Null when it could not be read.
    /// </summary>
    public static IReadOnlyList<BattleFieldEntry>? Current
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    var dir = HandshakeData.DatasheetDirectory();
                    _current = Load(dir);
                    _source = Path.Combine(dir, FileName);
                    _loaded = true;
                }
                return _current;
            }
        }
    }

    /// <summary>Where <see cref="Current"/> came from (or was looked for), for the log.</summary>
    public static string Source { get { lock (Gate) return _source; } }

    /// <summary>The row for <paramref name="battleFieldId"/>, or null when the sheet does not have it.</summary>
    public static BattleFieldEntry? Find(int battleFieldId)
    {
        var sheet = Current;
        if (sheet == null) return null;
        foreach (var e in sheet) if (e.Id == battleFieldId) return e;
        return null;
    }

    /// <summary>Test seam: use these rows (null = no sheet). <see cref="ResetForTest"/> goes back to the disk.</summary>
    internal static void SetForTest(IReadOnlyList<BattleFieldEntry>? rows)
    {
        lock (Gate) { _current = rows; _loaded = true; _source = "(test)"; }
    }

    /// <summary>T163: what DatasheetLoader read (or its built-in, null = no battleground) becomes the process's sheet.</summary>
    internal static void Use(IReadOnlyList<BattleFieldEntry>? rows, string source)
    {
        lock (Gate) { _current = rows; _loaded = true; _source = source; }
    }

    /// <summary>
    /// T163: DatasheetLoader's entry for this sheet, so LoadAll logs it and --check-config lists
    /// it. The built-in is NO battleground - there was never a transcribed table to fall back to
    /// after T157 - so a missing sheet reads "not found, using built-in (0 entries)".
    /// </summary>
    public static readonly ISheetValue Entry = new SheetEntry();

    private sealed class SheetEntry : ISheetValue
    {
        public string Sheet => FileName + " <BattleField>";
        private const string Consumer = "battleground tab + queue: ids, team sizes, level bands (MatchComposition)";

        public SheetStatus Load(string datasheetDir)
        {
            var rows = BattleFieldSheet.Load(datasheetDir);
            Use(rows, Path.Combine(datasheetDir ?? "", FileName));
            return new SheetStatus(Sheet, rows != null, rows?.Count ?? 0, Consumer);
        }

        public SheetStatus Probe(string datasheetDir)
        {
            var rows = BattleFieldSheet.Load(datasheetDir);
            return new SheetStatus(Sheet, rows != null, rows?.Count ?? 0, Consumer);
        }

        public SheetStatus Status
        {
            get { var rows = Current; return new SheetStatus(Sheet, rows != null, rows?.Count ?? 0, Consumer); }
        }

        public void UseBuiltIn() => Use(null, "(built-in: no battleground)");
    }

    /// <summary>Test seam: forget the cached sheet so the next use reads the disk again.</summary>
    internal static void ResetForTest()
    {
        lock (Gate) { _current = null; _loaded = false; _source = ""; }
    }
}
