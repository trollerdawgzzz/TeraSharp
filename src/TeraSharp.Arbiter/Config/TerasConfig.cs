// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace TeraSharp.Arbiter;

/// <summary>
/// T204 - one settings file instead of thirty environment variables.
///
/// <para>Every <c>TERASHARP_*</c> setting is read through <see cref="Get"/>, which resolves in one
/// fixed order: <b>the environment wins</b>, then <c>teras.json</c>, then nothing (and the caller's
/// own default applies). That order is deliberate - a operator who exports a variable to try
/// something for one run must not have to edit a file, and a service that already sets variables
/// keeps working unchanged. It also means this class can never break an existing deployment: with
/// no <c>teras.json</c> anywhere, every lookup behaves exactly as
/// <c>Environment.GetEnvironmentVariable</c> did.</para>
///
/// <para>The file is grouped and commented for humans (<c>teras.example.json</c> in the repo root
/// is the annotated copy), and <see cref="Map"/> is the whole translation: a JSON path such as
/// <c>admin.token</c> is the variable <c>TERASHARP_ADMIN_TOKEN</c>. Anything not in the map can
/// still be set under the flat <c>env</c> object, so a new variable works before this table knows
/// about it.</para>
///
/// <para>An empty JSON string, an explicit <c>null</c> and a missing key all mean "unset", so a
/// generated file can list every key with its placeholder and still behave like a fresh install.
/// A malformed file is a single warning and then ignored - a settings typo must not stop a server
/// that was running five minutes ago.</para>
/// </summary>
public static class TerasConfig
{
    /// <summary>The file name looked for beside the executable and in the working directory.</summary>
    public const string FileName = "teras.json";
    /// <summary>Points at a settings file explicitly. Read from the environment only, for obvious reasons.</summary>
    public const string PathVariable = "TERASHARP_CONFIG";

    /// <summary>
    /// JSON path -&gt; variable name. The JSON side is the grouping a human wants; the variable side
    /// is what the code has always read. Both spellings resolve to the same setting.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["paths.data"] = "TERASHARP_DATA",
        ["paths.logs"] = "TERASHARP_LOGS",
        ["paths.db"] = "TERASHARP_DB",
        ["paths.datasheet"] = "TERASHARP_DATASHEET",
        ["paths.serverConfig"] = "TERASHARP_SERVERCONFIG",
        ["paths.starterBlob"] = "TERASHARP_STARTER_BLOB",
        ["paths.starterInventory"] = "TERASHARP_STARTER_INVENTORY",
        ["economy.synthItemRecords"] = "TERASHARP_SYNTH_ITEM_RECORDS",
        ["paths.itemStrSheet"] = "TERASHARP_ITEM_STRSHEET",
        ["paths.itemNames"] = "TERASHARP_ITEM_NAMES",
        ["listener.bind"] = "TERASHARP_BIND",
        ["planet.dbServerName"] = "TERASHARP_DB_SERVER_NAME",
        ["auth.enabled"] = "TERASHARP_AUTH",
        ["auth.url"] = "TERASHARP_AUTH_URL",
        ["auth.gmAccounts"] = "TERASHARP_GM_ACCOUNTS",
        ["admin.token"] = "TERASHARP_ADMIN_TOKEN",
        ["admin.port"] = "TERASHARP_ADMIN_PORT",
        ["gateway.address"] = "TERASHARP_API_GATEWAY",
        ["gateway.serve"] = "TERASHARP_API_GATEWAY_SERVE",
        ["gateway.bind"] = "TERASHARP_API_GATEWAY_BIND",
        ["gateway.jwtSecret"] = "TERASHARP_API_JWT_SECRET",
        ["shop.url"] = "TERASHARP_SHOP_URL",
        ["shop.hubListen"] = "TERASHARP_HUB_LISTEN",
        ["shop.hubEnabled"] = "TERASHARP_HUB_ENABLED",
        ["shop.serverId"] = "TERASHARP_HUB_SERVER_ID",
        ["knobs.logLevel"] = "TERASHARP_LOG_LEVEL",
        ["knobs.rankingSeason"] = "TERASHARP_RANKING_SEASON",
        ["knobs.startOverride"] = "TERASHARP_START_OVERRIDE",
        ["knobs.standalone"] = "TERASHARP_STANDALONE",
        ["matchmaking.entrySeconds"] = "TERASHARP_MATCH_ENTRY_SECONDS",
        ["matchmaking.minMembers"] = "TERASHARP_MATCH_MIN_MEMBERS",
        ["matchmaking.bgMaxHealers"] = "TERASHARP_BG_MAX_HEALERS",
        ["matchmaking.bgMaxTanks"] = "TERASHARP_BG_MAX_TANKS",
        ["matchmaking.bfEnterDelay"] = "TERASHARP_BF_ENTER_DELAY",
        ["economy.brokerFeePercent"] = "TERASHARP_BROKER_FEE_PERCENT",
        ["parcels.deleteSystemOnCollect"] = "TERASHARP_PARCEL_DELETE_ON_COLLECT",
        ["gateway.claimTokenMaxAge"] = "TERASHARP_ITEM_CLAIM_MAX_AGE",
    };

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _values;      // variable name -> value, from the file
    private static string? _path;
    private static string? _problem;
    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.Ordinal);
    private static Action<string>? _warn;

    /// <summary>The file that was loaded, or null when the process is running on the environment alone.</summary>
    public static string? LoadedPath { get { Ensure(); return _path; } }
    /// <summary>Why the file was ignored, when one was found and could not be used.</summary>
    public static string? Problem { get { Ensure(); return _problem; } }

    /// <summary>
    /// Where a warning about an unreadable file goes. Set once from Program before anything reads a
    /// setting; until then a problem is remembered and reported by <see cref="Describe"/>.
    /// </summary>
    public static void UseWarningSink(Action<string> warn)
    {
        lock (Gate) _warn = warn;
        if (Problem is { } p) Warn(p);
    }

    /// <summary>
    /// The value of a <c>TERASHARP_*</c> setting: the environment first, then <c>teras.json</c>,
    /// then null. An empty or whitespace-only value on either side counts as unset, because that is
    /// what a generated file full of placeholders contains.
    /// </summary>
    public static string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var fromEnv = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        Ensure();
        return _values is not null && _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    /// <summary>Where <see cref="Get"/> found a setting - for <c>--check-config</c>.</summary>
    public static string SourceOf(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return "env";
        Ensure();
        return _values is not null && _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? FileName : "default";
    }

    /// <summary>One line per settings source, for the top of <c>--check-config</c>.</summary>
    public static IEnumerable<string> Describe()
    {
        Ensure();
        yield return _path is null
            ? FileName + ": none found (environment only). tools/setup.ps1 writes one."
            : FileName + ": " + _path + " (" + (_values?.Count ?? 0) + " setting(s))";
        if (_problem is { } p) yield return "! " + p;
        yield return "the environment overrides the file, always.";
    }

    /// <summary>Tests only: forget the loaded file so the next lookup re-reads it.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _values = null; _path = null; _problem = null; Warned.Clear(); }
    }

    /// <summary>Tests only: use this file instead of searching.</summary>
    internal static void LoadForTests(string? path)
    {
        lock (Gate)
        {
            _values = null; _path = null; _problem = null; Warned.Clear();
            if (path is null) { _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); return; }
            _values = Read(path, out _problem);
            _path = _values is null ? null : path;
            _values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Ensure()
    {
        if (_values is not null) return;
        lock (Gate)
        {
            if (_values is not null) return;
            foreach (var candidate in Candidates())
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                var read = Read(candidate, out string? problem);
                if (read is null) { _problem = problem; Warn(problem!); continue; }
                _values = read; _path = candidate;
                return;
            }
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Search order: an explicit <c>TERASHARP_CONFIG</c>, then beside the executable (which is
    /// where a published build puts it), then the working directory (which is where
    /// <c>dotnet run</c> from a clone finds it).
    /// </summary>
    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable(PathVariable);
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), FileName);
    }

    /// <summary>
    /// Flatten the file into variable -&gt; value. Returns null with <paramref name="problem"/> set
    /// when the file cannot be used at all; unknown keys are skipped individually so one stray
    /// section never costs the rest of the file.
    /// </summary>
    private static Dictionary<string, string>? Read(string path, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = path + ": the root must be a JSON object; ignoring the file.";
                return null;
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in doc.RootElement.EnumerateObject())
            {
                if (section.Name.StartsWith('$')) continue;                       // $schema and friends
                if (section.Value.ValueKind != JsonValueKind.Object)
                {
                    // A top-level scalar named like a variable: TERASHARP_DATA at the root.
                    if (section.Name.StartsWith("TERASHARP_", StringComparison.OrdinalIgnoreCase)
                        && Scalar(section.Value) is { } flat) values[section.Name] = flat;
                    continue;
                }
                bool env = section.Name.Equals("env", StringComparison.OrdinalIgnoreCase);
                foreach (var leaf in section.Value.EnumerateObject())
                {
                    if (Scalar(leaf.Value) is not { } text) continue;
                    string? name = env ? leaf.Name
                        : Map.TryGetValue(section.Name + "." + leaf.Name, out var mapped) ? mapped : null;
                    if (name is not null) values[name] = text;
                }
            }
            return values;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = path + ": " + e.Message + " - ignoring the file and using the environment.";
            return null;
        }
    }

    /// <summary>A JSON scalar as the string the variable would have held. Null for anything else.</summary>
    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.TryGetInt64(out long l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : e.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static void Warn(string message)
    {
        Action<string>? sink;
        lock (Gate) sink = _warn;
        if (sink is null || !Warned.TryAdd(message, 0)) return;
        sink(message);
    }
}
