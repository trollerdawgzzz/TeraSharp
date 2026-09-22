// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Xml;

namespace TeraSharp.Arbiter.Protocol;

// =============================================================================================
// ItemNames - T101c. templateId -> display name, for the admin tool's inventory tables.
//
// WHY IT IS A FILE AND NOT A LOOKUP. The names live in StrSheet_Item inside the client
// DataCenter (tera-api/data/datasheets/DataCenter_Final_*.dat, 61 MB, packed). tera-api reads it
// with its own DataCenter parser into an in-memory Map and serialises that Map to the opaque
// dc_*.bin blobs under tera-api/data/cache - there is no table and no HTTP route to ask. Parsing
// a DataCenter inside the Arbiter to label a column in an admin page is the wrong trade, so this
// reads a plain two-column file instead, and everything works without one: an unknown template
// renders as its id, which is what T101/T101b showed anyway.
//
// FORMAT.  <templateId><TAB or comma><name>, one per line. # and ; start a comment. A header
// line whose first field is not a number is skipped. UTF-8.
//
// WHERE IT LOOKS, in order: $TERASHARP_ITEM_NAMES, then <dataRoot>/data/item_names.tsv, then
// data/item_names.tsv beside the exe.
//
// T113: AND the real thing. WebApp\AppResource\ItemData\StrSheet_Item_NAEU.xml is the client's
// own item strsheet, already unpacked on this box - 3.4 MB, 19320 <String id= string= toolTip=>
// elements. That is the source T101c went looking for and could not find, and no DataCenter
// parser is needed to read it. It is streamed with an XmlReader rather than loaded as an
// XDocument, because this is a low-memory VPS and holding 3.4 MB of DOM to label one column is
// the wrong trade; and it loads LAZILY, on the first lookup, so a server that never opens the
// admin page never pays for it at all.
// =============================================================================================

/// <summary>The optional templateId -&gt; name sheet. Static: one per process, loaded once.</summary>
public static class ItemNames
{
    /// <summary>Points straight at a file and skips the search.</summary>
    public const string PathVariable = "TERASHARP_ITEM_NAMES";

    /// <summary>The conventional name under <c>data/</c>.</summary>
    public const string DefaultFileName = "item_names.tsv";

    /// <summary>T113: points straight at the client's own StrSheet_Item XML.</summary>
    public const string StrSheetVariable = "TERASHARP_ITEM_STRSHEET";

    /// <summary>The regional strsheet, relative to the data root. Ids from 150000 up.</summary>
    public static readonly string StrSheetRelativePath =
        Path.Combine("WebApp", "AppResource", "ItemData", "StrSheet_Item_NAEU.xml");

    /// <summary>
    /// The FULL strsheet beside it - a different id range, not a superset. This is the one that
    /// has the items the captures use, so it is loaded as well and the two are merged.
    /// </summary>
    public static readonly string StrSheetFullRelativePath =
        Path.Combine("WebApp", "AppResource", "ItemData", "StrSheet_Item.xml");

    private static readonly object Gate = new();
    private static Dictionary<int, string> _names = new();

    /// <summary>
    /// Where the relative candidates below are rooted. Program sets it once at startup - one
    /// line - because nothing in Protocol can know what TERASHARP_DATA came out as, and without
    /// it only the two explicit env vars would ever resolve.
    /// </summary>
    public static string? DataRoot { get; set; }

    /// <summary>How many names are loaded. 0 means no sheet was found, which is not an error.
    /// Loads on first ask, like <see cref="Lookup"/>, so the admin page's figure is the truth
    /// rather than "nothing has looked anything up yet".</summary>
    public static int Count
    {
        get { EnsureLoaded(); lock (Gate) return _names.Count; }
    }

    /// <summary>The file or files the names came from, semicolon-separated, or empty.</summary>
    public static string Source { get; private set; } = string.Empty;

    /// <summary>
    /// The display name for a template, or the empty string when unknown. The first call loads
    /// whichever sheet is configured; every call after that is a dictionary hit.
    /// </summary>
    public static string Lookup(int templateId)
    {
        EnsureLoaded();
        lock (Gate) return _names.TryGetValue(templateId, out var n) ? n : string.Empty;
    }

    private static bool _tried;

    /// <summary>The lazy half. Tries once, whether or not it finds anything.</summary>
    public static void EnsureLoaded(string? dataRoot = null)
    {
        lock (Gate) { if (_tried) return; }
        Load(dataRoot ?? DataRoot);
    }

    /// <summary>Forget what is loaded, so the next lookup loads again. For tests.</summary>
    public static void Reset()
    {
        lock (Gate) { _names = new Dictionary<int, string>(); _tried = false; }
        Source = string.Empty;
    }

    /// <summary>
    /// Look for a sheet and load it. Safe to call when there is none - it just leaves the map
    /// empty - and safe to call twice. Returns how many names are loaded.
    /// </summary>
    public static int Load(string? dataRoot = null)
    {
        lock (Gate) { _tried = true; }
        var merged = new Dictionary<int, string>();
        var used = new List<string>();

        // MERGE, do not stop at the first hit. StrSheet_Item.xml and StrSheet_Item_NAEU.xml are
        // DISJOINT on this box - measured: 34539 ids in the first, 19110 in the second, ZERO in
        // both - and the ids TeraSharp's own captures use (88375 Stormcry Axe, 200997 Minor
        // Battle Solution, 310010 Card Fragment, 10027 Cleavework Sword) are in the FULL sheet,
        // not the NAEU one. Loading only the file the brief named would have labelled none of
        // them. Earlier candidates win a conflict, so an explicit override still overrides.
        foreach (var candidate in Candidates(dataRoot))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            try
            {
                var parsed = candidate.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    ? ParseStrSheet(candidate)
                    : Parse(File.ReadLines(candidate));
                if (parsed.Count == 0) continue;
                foreach (var kv in parsed)
                    if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
                used.Add(candidate);
            }
            catch (Exception)
            {
                // A malformed sheet is a cosmetic problem: ids still render. Try the next one.
            }
        }

        if (merged.Count == 0) return Count;
        lock (Gate) _names = merged;
        Source = string.Join("; ", used);
        return merged.Count;
    }

    private static IEnumerable<string> Candidates(string? dataRoot)
    {
        // The explicit ones win, then the client's own strsheet, then a hand-made TSV.
        yield return Environment.GetEnvironmentVariable(StrSheetVariable) ?? string.Empty;
        yield return Environment.GetEnvironmentVariable(PathVariable) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            // Both sheets: they cover different id ranges and neither is a superset.
            yield return Path.Combine(dataRoot, StrSheetRelativePath);
            yield return Path.Combine(dataRoot, StrSheetFullRelativePath);
            yield return Path.Combine(dataRoot, "data", DefaultFileName);
        }
        yield return Path.Combine(AppContext.BaseDirectory, "data", DefaultFileName);
    }

    /// <summary>
    /// The client's <c>StrSheet_Item_*.xml</c>: <c>&lt;String id= string= toolTip=/&gt;</c>
    /// elements under a <c>&lt;StrSheet_Item&gt;</c> root. Streamed one element at a time, and
    /// the tooltip - which is most of the bytes - is never read.
    /// </summary>
    public static Dictionary<int, string> ParseStrSheet(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,   // never fetch what a data file asks for
            XmlResolver = null,
        });
        return ParseStrSheet(reader);
    }

    /// <summary>As above, from a reader, so a test can drive it from a string.</summary>
    public static Dictionary<int, string> ParseStrSheet(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var map = new Dictionary<int, string>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "String", StringComparison.Ordinal)) continue;

            string? idText = reader.GetAttribute("id");
            string? name = reader.GetAttribute("string");
            // Thousands of rows carry string="" - unused template ids. They are not names.
            if (string.IsNullOrEmpty(idText) || string.IsNullOrWhiteSpace(name)) continue;
            if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0)
                map[id] = name;
        }
        return map;
    }

    /// <summary>The parser, separate so a test can drive it without a file.</summary>
    public static Dictionary<int, string> Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<int, string>();
        foreach (var raw in lines)
        {
            if (raw == null) continue;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

            int cut = line.IndexOf('\t');
            if (cut < 0) cut = line.IndexOf(',');
            if (cut <= 0) continue;

            if (!int.TryParse(line[..cut].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int id) || id <= 0)
                continue;   // the header line lands here

            string name = line[(cut + 1)..].Trim();
            if (name.Length != 0) map[id] = name;
        }
        return map;
    }
}
