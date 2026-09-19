using System.Globalization;

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
// =============================================================================================

/// <summary>The optional templateId -&gt; name sheet. Static: one per process, loaded once.</summary>
public static class ItemNames
{
    /// <summary>Points straight at a file and skips the search.</summary>
    public const string PathVariable = "TERASHARP_ITEM_NAMES";

    /// <summary>The conventional name under <c>data/</c>.</summary>
    public const string DefaultFileName = "item_names.tsv";

    private static readonly object Gate = new();
    private static Dictionary<int, string> _names = new();

    /// <summary>How many names are loaded. 0 means no sheet was found, which is not an error.</summary>
    public static int Count { get { lock (Gate) return _names.Count; } }

    /// <summary>The file the names came from, or empty.</summary>
    public static string Source { get; private set; } = string.Empty;

    /// <summary>The display name for a template, or the empty string when unknown.</summary>
    public static string Lookup(int templateId)
    {
        lock (Gate) return _names.TryGetValue(templateId, out var n) ? n : string.Empty;
    }

    /// <summary>
    /// Look for a sheet and load it. Safe to call when there is none - it just leaves the map
    /// empty - and safe to call twice. Returns how many names are loaded.
    /// </summary>
    public static int Load(string? dataRoot = null)
    {
        foreach (var candidate in Candidates(dataRoot))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            try
            {
                var parsed = Parse(File.ReadLines(candidate));
                lock (Gate) _names = parsed;
                Source = candidate;
                return parsed.Count;
            }
            catch (Exception)
            {
                // A malformed sheet is a cosmetic problem: ids still render. Try the next one.
            }
        }
        return Count;
    }

    private static IEnumerable<string> Candidates(string? dataRoot)
    {
        yield return Environment.GetEnvironmentVariable(PathVariable) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dataRoot))
            yield return Path.Combine(dataRoot, "data", DefaultFileName);
        yield return Path.Combine(AppContext.BaseDirectory, "data", DefaultFileName);
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
