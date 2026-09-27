// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T204 - one settings file, and the rule that makes it safe to add: <b>the environment always
/// wins</b>. A deployment that already exports TERASHARP_* variables behaves identically with and
/// without a teras.json, which is what lets this ship without a migration.
///
/// <para>The other half of the task is that the three lists cannot drift: what the code reads
/// (<see cref="TerasConfig.Map"/>), what <c>--check-config</c> prints
/// (<c>SelfTest.KnownVariables</c>) and what the shipped example file documents. A setting missing
/// from any one of them is invisible in exactly the way that costs an evening.</para>
/// </summary>
public static partial class Tests
{
    /// <summary>A settings file in a temp directory, and the cleanup that must follow it.</summary>
    static string T204Write(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, TerasConfig.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Test] public static void T204_the_environment_wins_and_the_file_fills_the_rest()
    {
        string dir = T37TempDir();
        try
        {
            // brokerFeePercent is in the file; the environment sets it too. entrySeconds is only
            // in the file. gmAccounts is only in the environment. logLevel is in neither.
            string path = T204Write(dir, """
            {
              "economy":     { "brokerFeePercent": 15 },
              "matchmaking": { "entrySeconds": 300, "bgMaxHealers": 0 },
              "auth":        { "enabled": true, "url": "" }
            }
            """);
            Environment.SetEnvironmentVariable("TERASHARP_BROKER_FEE_PERCENT", "20");
            Environment.SetEnvironmentVariable("TERASHARP_GM_ACCOUNTS", "7");
            TerasConfig.LoadForTests(path);

            Hex.True(TerasConfig.Get("TERASHARP_BROKER_FEE_PERCENT") == "20", "the environment wins over the file");
            Hex.True(TerasConfig.SourceOf("TERASHARP_BROKER_FEE_PERCENT") == "env", "and says so");
            Hex.True(TerasConfig.Get("TERASHARP_MATCH_ENTRY_SECONDS") == "300", "a file-only setting is read");
            Hex.True(TerasConfig.SourceOf("TERASHARP_MATCH_ENTRY_SECONDS") == TerasConfig.FileName, "and says so");
            Hex.True(TerasConfig.Get("TERASHARP_GM_ACCOUNTS") == "7", "an env-only setting still works");
            Hex.True(TerasConfig.Get("TERASHARP_LOG_LEVEL") == null
                     && TerasConfig.SourceOf("TERASHARP_LOG_LEVEL") == "default",
                     "and a setting in neither place is unset, so the caller's default applies");

            // A JSON number and a JSON bool have to arrive as the text the variable would have
            // held, or every int.TryParse call site breaks.
            Hex.True(TerasConfig.Get("TERASHARP_AUTH") == "true", "true is the string true");
            Hex.True(TerasConfig.Get("TERASHARP_BG_MAX_HEALERS") == "0", "0 is the string 0, not unset");
            // "" means unset, which is what a generated file full of placeholders contains.
            Hex.True(TerasConfig.Get("TERASHARP_AUTH_URL") == null, "an empty string means unset");
            Hex.True(TerasConfig.LoadedPath == path && TerasConfig.Problem == null, "the file loaded cleanly");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERASHARP_BROKER_FEE_PERCENT", null);
            Environment.SetEnvironmentVariable("TERASHARP_GM_ACCOUNTS", null);
            TerasConfig.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Test] public static void T204_a_settings_file_can_never_stop_the_server()
    {
        string dir = T37TempDir();
        try
        {
            // Comments and a trailing comma: the shipped example has both, because it is meant to
            // be read. An unknown section and an unknown key are skipped one at a time, so one
            // stray line does not cost the rest of the file.
            string good = T204Write(Path.Combine(dir, "good"), """
            // TeraSharp settings
            {
              "paths":   { "logs": "C:\\logs", "notAThing": 1 },
              "future":  { "whatever": true },
              "env":     { "TERASHARP_RANKING_SEASON": 17 },
            }
            """);
            TerasConfig.LoadForTests(good);
            Hex.True(TerasConfig.Get("TERASHARP_LOGS") == @"C:\logs", "comments and trailing commas are fine");
            Hex.True(TerasConfig.Get("TERASHARP_RANKING_SEASON") == "17",
                     "and the env section reaches a variable the table does not know about");
            Hex.True(TerasConfig.Problem == null, "nothing was wrong with it");

            // Malformed, and a root that is not an object: one warning, then the environment alone.
            foreach (var (name, body) in new[] { ("broken", "{ this is not json"), ("array", "[1,2,3]") })
            {
                string bad = T204Write(Path.Combine(dir, name), body);
                TerasConfig.LoadForTests(bad);
                Hex.True(TerasConfig.Problem != null, $"{name}: the problem is reported");
                Hex.True(TerasConfig.LoadedPath == null, $"{name}: and the file is not treated as loaded");
                Hex.True(TerasConfig.Get("TERASHARP_LOGS") == null, $"{name}: no setting comes out of it");
                Hex.True(TerasConfig.Describe().Any(l => l.StartsWith("! ", StringComparison.Ordinal)),
                         $"{name}: and --check-config says so");
            }

            // No file at all is the pre-T204 world, exactly.
            TerasConfig.LoadForTests(null);
            Hex.True(TerasConfig.LoadedPath == null && TerasConfig.Problem == null, "no file is not a problem");
            Environment.SetEnvironmentVariable("TERASHARP_LOGS", @"C:\from-env");
            Hex.True(TerasConfig.Get("TERASHARP_LOGS") == @"C:\from-env", "and the environment still answers");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERASHARP_LOGS", null);
            TerasConfig.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Test] public static void T204_the_map_the_report_and_the_example_file_agree()
    {
        // Every mapped setting must be printed by --check-config, or a value can be in force and
        // invisible.
        var reported = new HashSet<string>(SelfTest.KnownVariables, StringComparer.Ordinal);
        var missing = TerasConfig.Map.Values.Where(v => !reported.Contains(v)).OrderBy(v => v, StringComparer.Ordinal).ToList();
        Hex.True(missing.Count == 0, "every teras.json setting is in --check-config: " + string.Join(", ", missing));

        // And every printed setting must be reachable from the file, or teras.json cannot replace
        // the environment. TERASHARP_CONFIG is the one exception - it says where the file is.
        var mapped = new HashSet<string>(TerasConfig.Map.Values, StringComparer.Ordinal);
        var unreachable = SelfTest.KnownVariables
            .Where(v => !mapped.Contains(v) && v != TerasConfig.PathVariable)
            .OrderBy(v => v, StringComparer.Ordinal).ToList();
        Hex.True(unreachable.Count == 0,
                 "every reported setting has a teras.json key (or belongs in env{}): " + string.Join(", ", unreachable));

        // The shipped example documents each one, and parses through the same loader.
        string? example = FindRepoFile("teras.example.json");
        if (example is null) { Skip.Because("teras.example.json is not in the tree"); return; }
        string dir = T37TempDir();
        try
        {
            string copy = Path.Combine(dir, TerasConfig.FileName);
            File.Copy(example, copy);
            TerasConfig.LoadForTests(copy);
            Hex.True(TerasConfig.Problem == null, "the example file parses: " + (TerasConfig.Problem ?? "ok"));

            string text = File.ReadAllText(example);
            // One deliberate omission: minMembers is reachable, so anyone who already has it set
            // still gets the retirement warning, but the example must not invite a new operator to
            // set it - it makes the matcher wrong on purpose (T184h).
            const string retired = "matchmaking.minMembers";
            Hex.True(TerasConfig.Map.ContainsKey(retired), "the retired knob is still reachable, so it can still warn");
            var undocumented = TerasConfig.Map.Keys
                .Where(k => k != retired && !text.Contains("\"" + k.Split('.')[1] + "\"", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            Hex.True(undocumented.Count == 0, "teras.example.json lists every key: " + string.Join(", ", undocumented));
            Hex.True(!text.Contains("\"minMembers\":", StringComparison.Ordinal),
                     "and does not set minMembers anywhere");

            // The two secrets must ship empty, and the test knob must not be listed at all.
            Hex.True(TerasConfig.Get("TERASHARP_ADMIN_TOKEN") == null
                     && TerasConfig.Get("TERASHARP_API_JWT_SECRET") == null,
                     "the example ships both secrets empty");
            Hex.True(TerasConfig.Get("TERASHARP_MATCH_MIN_MEMBERS") == null,
                     "and does not set the retired MIN_MEMBERS knob");
            // setup.ps1's own section must be there, because start.ps1 and stop.ps1 read it.
            foreach (string key in new[] { "root", "publicHost", "planetId", "serverName", "clientPort", "worldPort", "proxyPort" })
                Hex.True(text.Contains("\"" + key + "\"", StringComparison.Ordinal), "deployment." + key + " is documented");
        }
        finally { TerasConfig.ResetForTests(); Directory.Delete(dir, true); }
    }

    [Test] public static void T204_the_file_setup_writes_resolves_every_path_and_port()
    {
        // Byte-for-byte the shape tools\setup.ps1 emits, secrets replaced. If the emitter and the
        // loader ever disagree about a name, this is where it shows.
        string dir = T37TempDir();
        try
        {
            string path = T204Write(dir, """
            {
              "paths": {
                "data": "C:\\TERA_SERVER.100",
                "logs": "C:\\TERA_SERVER.100\\logs",
                "db": "",
                "datasheet": "",
                "serverConfig": "",
                "starterBlob": "C:\\TeraSharp\\data\\starter_blob.bin",
                "starterInventory": "C:\\TeraSharp\\data\\starter_inventory.bin",
                "itemStrSheet": "",
                "itemNames": ""
              },
              "listener": { "bind": "127.0.0.1" },
              "planet": { "dbServerName": "PlanetDB_2800" },
              "auth": { "enabled": true, "url": "http://127.0.0.1:8080", "gmAccounts": "1" },
              "admin": { "token": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "port": 8051 },
              "gateway": { "address": "127.0.0.1:8800", "serve": false, "bind": "", "jwtSecret": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
              "knobs": { "logLevel": "Warning", "rankingSeason": 15, "startOverride": "", "standalone": "" },
              "matchmaking": { "entrySeconds": 300, "bgMaxHealers": 2, "bgMaxTanks": 3 },
              "economy": { "brokerFeePercent": 10 },
              "deployment": {
                "root": "C:\\TERA_SERVER.100", "publicHost": "203.0.113.10", "planetId": 2800,
                "serverName": "TeraSharp", "clientPort": 7701, "worldPort": 7802, "proxyPort": 7801,
                "topologyFolder": ".\\Topology", "worldIds": "0,13"
              },
              "env": {}
            }
            """);
            TerasConfig.LoadForTests(path);

            Hex.True(TerasConfig.Get("TERASHARP_DATA") == @"C:\TERA_SERVER.100", "a Windows path survives the JSON escaping");
            Hex.True(TerasConfig.Get("TERASHARP_STARTER_BLOB")!.EndsWith(@"data\starter_blob.bin", StringComparison.Ordinal), "so does the blob path");
            Hex.True(TerasConfig.Get("TERASHARP_BIND") == "127.0.0.1", "the listener stays on loopback");
            Hex.True(TerasConfig.Get("TERASHARP_ADMIN_PORT") == "8051", "the admin web is off tera-api's 8050");
            Hex.True(TerasConfig.Get("TERASHARP_AUTH") == "true", "auth is closed");
            Hex.True(TerasConfig.Get("TERASHARP_ADMIN_TOKEN")!.Length == 48
                     && TerasConfig.Get("TERASHARP_API_JWT_SECRET")!.Length == 48, "both secrets are 48 characters");
            Hex.True(TerasConfig.Get("TERASHARP_DB") == null, "an unset db path falls back to <logs>\\terasharp.db");

            // The whole point: --check-config's notes go quiet on a file like this, apart from the
            // Alt+A note, which is advice rather than a fault.
            var notes = SelfTest.Notes().ToList();
            Hex.True(!notes.Any(n => n.Contains("auth is OPEN", StringComparison.Ordinal)), "auth is not open");
            Hex.True(!notes.Any(n => n.Contains("admin web is OFF", StringComparison.Ordinal)), "the admin web is on");
            Hex.True(!notes.Any(n => n.Contains("8050", StringComparison.Ordinal)), "and not on 8050");
            Hex.True(!notes.Any(n => n.Contains("not 127.0.0.1", StringComparison.Ordinal)), "and the bind is loopback");
        }
        finally { TerasConfig.ResetForTests(); Directory.Delete(dir, true); }
    }
}
