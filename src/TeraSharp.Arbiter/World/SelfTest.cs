// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.World;

/// <summary>One dependency check: what was looked at, whether it is usable, and the detail line.</summary>
/// <param name="Required">False for a check whose failure is a warning, not a bad deploy.</param>
public sealed record SelfTestResult(string Name, bool Pass, string Detail, bool Required = true);

/// <summary>
/// Startup dependency check — <c>TeraSharp.Arbiter.exe --selftest</c>.
///
/// <para>Every data file, folder and DB table the Arbiter needs is loaded here and reported as one
/// PASS/FAIL line, so a bad deploy is caught in five seconds instead of at the first login. It is
/// the deploy-side companion to status/LIVE-CHECKLIST.md: run it after copying a build, then start
/// WorldServer and work down the checklist.</para>
///
/// <para>Nothing here is clever - it deliberately re-checks by hand what the handlers would load
/// lazily, because the failure mode it exists to catch is "the file is not where the deployed
/// binary looks". Each check prints the resolved PATH, which is what makes a wrong
/// TERASHARP_DATA / TERASHARP_LOGS obvious.</para>
/// </summary>
public static class SelfTest
{
    /// <summary>The tables CharacterStore creates. A missing one means the DB predates a migration.</summary>
    public static readonly string[] RequiredTables =
    {
        "accounts", "characters", "friends", "blocks", "friend_groups", "quests",
        "achievements", "achievements_done", "dungeon_cooldowns", "reputations",
        "fatigability", "tutorial_tips", "seren_guide", "counters",
        "client_settings", "account_settings",
        "items", "warehouses", "parcels", "parcel_items",   // T42
        "guilds", "guild_members", "guild_groups", "guild_applies", "guild_invites", "guild_log", "guild_perks",   // T39
        "visited_sections",   // T45
        "broker_listings",    // T71
    };

    /// <summary>Columns added by a later migration - the ones a stale terasharp.db will be missing.</summary>
    public static readonly (string Table, string Column)[] RequiredColumns =
    {
        ("characters", "return_zone"),            // T21 enter-world fallback
        ("characters", "instance_pdid"),          // T21
        ("characters", "profile_message"),        // T30 friends
        ("characters", "sample_group_provided"),  // T30
        ("friends", "group_id"),                  // T30
        ("friends", "memo"),                      // T30
        ("blocks", "memo"),                       // T30
        ("accounts", "admin_level"),              // T32 GM
        ("characters", "money"),                  // T59 character money
        ("characters", "ep_exp"),                 // T77 EP panel
        ("characters", "ep_level"),               // T77
    };

    /// <summary>Packets whose def must exist or a login dies mid-sequence.</summary>
    public static readonly string[] RequiredDefs =
    {
        "C_LOGIN_ARBITER", "S_LOGIN_ARBITER", "S_LOGIN_ACCOUNT_INFO", "S_GET_USER_LIST",
        "C_CREATE_USER", "S_SPAWN_ME", "S_FRIEND_LIST", "S_FRIEND_GROUP_LIST",
        "S_USER_BLOCK_LIST", "S_SYSTEM_MESSAGE", "S_SYSTEM_MESSAGE_CUSTOM", "C_ADMIN",
    };

    /// <summary>Opcodes that must be in data.json for the same reason.</summary>
    public static readonly string[] RequiredOpcodes =
    {
        "C_LOGIN_ARBITER", "C_GET_USER_LIST", "C_SELECT_USER", "C_LOAD_TOPO_FIN",
        "C_ADMIN", "C_OP_COMMAND", "S_SYSTEM_MESSAGE_CUSTOM", "S_FRIEND_LIST",
    };

    // ---- The individual checks (pure: give them a path, they answer) ----

    /// <summary>A file that must exist and be exactly <paramref name="expected"/> bytes.</summary>
    public static SelfTestResult CheckFixedSize(string name, string? path, int expected, bool required = true)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new SelfTestResult(name, false, $"not found (looked at {Show(path)})", required);
        long len = new FileInfo(path).Length;
        return len == expected
            ? new SelfTestResult(name, true, $"{len} B  {path}", required)
            : new SelfTestResult(name, false, $"{len} B, expected {expected}  {path}", required);
    }

    /// <summary>A file that must exist and be a whole number of <paramref name="unit"/>-byte records.</summary>
    public static SelfTestResult CheckRecordFile(string name, string? path, int unit, bool required = true)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new SelfTestResult(name, false, $"not found (looked at {Show(path)})", required);
        long len = new FileInfo(path).Length;
        if (len == 0 || unit <= 0 || len % unit != 0)
            return new SelfTestResult(name, false, $"{len} B is not a multiple of {unit}  {path}", required);
        return new SelfTestResult(name, true, $"{len / unit} record(s) of {unit} B  {path}", required);
    }

    /// <summary>A file that only has to be there.</summary>
    /// <summary>
    /// T215. The client port is the one thing a player can reach directly, and it does NOT check
    /// GM privilege on C_ADMIN - the proxy on 7801 is the only gate. Binding it anywhere but
    /// loopback hands the admin opcodes to the internet, so this is a FAILED self-test, not a
    /// note: <c>--selftest</c> exits non-zero and <c>start.ps1</c> refuses to boot without
    /// <c>-Insecure</c>.
    /// </summary>
    public static SelfTestResult CheckLoopbackBind()
    {
        string bind = (TerasConfig.Get("TERASHARP_BIND") ?? "127.0.0.1").Trim();
        bool loopback = bind.Length == 0 || bind == "127.0.0.1" || bind == "::1"
                        || bind.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        return new SelfTestResult("listener bind", loopback,
            loopback ? bind.Length == 0 ? "127.0.0.1 (default)" : bind
                     : bind + " is not loopback - the client port does not check GM privilege on "
                            + "C_ADMIN; put the proxy in front and keep TERASHARP_BIND=127.0.0.1");
    }

    public static SelfTestResult CheckExists(string name, string? path, bool required = true)
        => string.IsNullOrEmpty(path) || !File.Exists(path)
            ? new SelfTestResult(name, false, $"not found (looked at {Show(path)})", required)
            : new SelfTestResult(name, true, $"{new FileInfo(path).Length} B  {path}", required);

    /// <summary>A folder that only has to be there, with an optional file inside it.</summary>
    public static SelfTestResult CheckFolder(string name, string? folder, string? mustContain = null, bool required = true)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new SelfTestResult(name, false, $"not found (looked at {Show(folder)})", required);
        if (mustContain != null && !File.Exists(Path.Combine(folder, mustContain)))
            return new SelfTestResult(name, false, $"{mustContain} missing from {folder}", required);
        int files = Directory.EnumerateFiles(folder).Take(5000).Count();
        return new SelfTestResult(name, true, $"{files} file(s)  {folder}", required);
    }

    /// <summary>
    /// The SQLite file: every required table and migrated column, plus PRAGMA user_version for
    /// the record. Runs on its own read-only connection so it never disturbs the live store.
    /// </summary>
    public static SelfTestResult CheckDatabase(string? dbPath)
    {
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            return new SelfTestResult("DB schema", false, $"not found (looked at {Show(dbPath)})");
        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            db.Open();

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            var missingTables = RequiredTables.Where(t => !tables.Contains(t)).ToArray();

            var missingColumns = new List<string>();
            foreach (var (table, column) in RequiredColumns)
            {
                if (!tables.Contains(table)) continue;            // already reported as a missing table
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
                cmd.Parameters.AddWithValue("$c", column);
                if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) missingColumns.Add($"{table}.{column}");
            }

            long userVersion = 0;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version";
                userVersion = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }

            if (missingTables.Length > 0 || missingColumns.Count > 0)
                return new SelfTestResult("DB schema", false,
                    $"missing table(s): {Join(missingTables)}; missing column(s): {Join(missingColumns)}  {dbPath}");

            return new SelfTestResult("DB schema", true,
                $"{tables.Count} table(s), all {RequiredTables.Length} required present, "
                + $"user_version {userVersion}  {dbPath}");
        }
        catch (Exception ex)
        {
            return new SelfTestResult("DB schema", false, $"{ex.GetType().Name}: {ex.Message}  {dbPath}");
        }
    }

    /// <summary>data.json: the opcode map for this protocol version.</summary>
    public static SelfTestResult CheckOpcodes(string? dataJsonPath, string versionKey)
    {
        if (string.IsNullOrEmpty(dataJsonPath) || !File.Exists(dataJsonPath))
            return new SelfTestResult("opcode map", false, $"not found (looked at {Show(dataJsonPath)})");
        try
        {
            var table = OpcodeTable.LoadFromFile(dataJsonPath, versionKey);
            var missing = RequiredOpcodes.Where(n => !table.TryGetCode(n, out _)).ToArray();
            return missing.Length == 0
                ? new SelfTestResult("opcode map", true, $"{table.Count} opcodes for {versionKey}  {dataJsonPath}")
                : new SelfTestResult("opcode map", false, $"missing: {Join(missing)}  {dataJsonPath}");
        }
        catch (Exception ex)
        {
            return new SelfTestResult("opcode map", false, $"{ex.GetType().Name}: {ex.Message}  {dataJsonPath}");
        }
    }

    /// <summary>tera_v100_MASTER_FINAL: the packet definitions the codec is driven by.</summary>
    public static SelfTestResult CheckDefinitions(string? folder, ILogger log)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new SelfTestResult("packet defs", false, $"not found (looked at {Show(folder)})");
        try
        {
            var defs = DefinitionRegistry.LoadFromFolder(folder, log);
            var missing = RequiredDefs.Where(n => !defs.Has(n)).ToArray();
            return missing.Length == 0
                ? new SelfTestResult("packet defs", true, $"{defs.Count} packet(s)  {folder}")
                : new SelfTestResult("packet defs", false, $"missing: {Join(missing)}  {folder}");
        }
        catch (Exception ex)
        {
            return new SelfTestResult("packet defs", false, $"{ex.GetType().Name}: {ex.Message}  {folder}");
        }
    }

    /// <summary>
    /// handshake_burst.bin: parsed, not just present - a truncated file parses to fewer frames.
    /// <para>T209b: no longer part of <see cref="Report"/> - the burst is built from defs, so the
    /// file is evidence for the tests and may be absent. Pass <c>required: false</c> for that.</para>
    /// </summary>
    public static SelfTestResult CheckHandshakeBurst(string? path, bool required = true)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new SelfTestResult("handshake burst", !required,
                required ? $"not found (looked at {Show(path)})"
                         : "not present - the burst is built from defs (T209b)");
        try
        {
            var frames = DbProxyHandlers.ParseBurst(File.ReadAllBytes(path));
            if (frames == null)
                return new SelfTestResult("handshake burst", false, $"unparseable  {path}");
            return frames.Count == DbProxyHandlers.HandshakeBurstFrameCount
                ? new SelfTestResult("handshake burst", true, $"{frames.Count} frames  {path}")
                : new SelfTestResult("handshake burst", false,
                    $"{frames.Count} frames, expected {DbProxyHandlers.HandshakeBurstFrameCount}  {path}");
        }
        catch (Exception ex)
        {
            return new SelfTestResult("handshake burst", false, $"{ex.GetType().Name}: {ex.Message}  {path}");
        }
    }

    /// <summary>
    /// T209b: what a deploy must satisfy now is that the burst BUILDS - 63 frames out of
    /// Protocol/InterServerDefinitions - not that a capture file sits next to the binary. A def
    /// text that stops parsing, or a field renamed on one side only, fails here.
    /// </summary>
    public static SelfTestResult CheckHandshakeBurstBuild()
    {
        try
        {
            var frames = HandshakeBurst.Build(DateTimeOffset.UtcNow);
            int bytes = 0;
            foreach (var (_, payload) in frames) bytes += payload.Length + 6;
            return frames.Count == HandshakeBurst.FrameCount
                ? new SelfTestResult("handshake burst", true, $"{frames.Count} frames / {bytes} B built from defs")
                : new SelfTestResult("handshake burst", false,
                    $"built {frames.Count} frames, expected {HandshakeBurst.FrameCount}");
        }
        catch (Exception ex)
        {
            return new SelfTestResult("handshake burst", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- T58: the three wiring checks. A bad deploy is a build where the code is fine and the
    //      DATA next to it is not, so each of these re-runs the real registration/allow-list code
    //      against the files that shipped rather than trusting a constant. ----

    /// <summary>
    /// <c>ArbiterOwned</c> opcodes that deliberately have NO dispatcher handler. Empty on purpose:
    /// the set exists precisely so <c>PacketDispatcher</c> never forwards one to World, and an
    /// entry with no handler is logged and dropped - a silent dead end for the feature that sends
    /// it. If an opcode ever should be accepted-and-ignored, put it here with the reason rather
    /// than leaving this check red.
    /// </summary>
    public static readonly ushort[] ArbiterOwnedWithoutHandler = Array.Empty<ushort>();

    /// <summary>
    /// Every opcode <c>DbProxyHandlers.IsHandledRequest</c> answers. It is a switch, not a set, so
    /// the only honest way to enumerate it is to ask it about every opcode - 65536 predicate calls,
    /// which costs nothing once at startup and cannot drift from the switch the way a parallel
    /// list would.
    /// </summary>
    public static IReadOnlyList<ushort> AllowListOpcodes()
    {
        var ops = new List<ushort>();
        for (int op = 0; op <= ushort.MaxValue; op++)
            if (DbProxyHandlers.IsHandledRequest((ushort)op)) ops.Add((ushort)op);
        return ops;
    }

    /// <summary>
    /// Runs the REAL <c>HandlerRegistry.RegisterAll</c> against a throwaway dispatcher and the
    /// data.json that shipped, then reports two things:
    /// <list type="bullet">
    /// <item>every name the registry registers resolves in the <paramref name="versionKey"/> map -
    /// <c>Reg</c> logs "not in opcode map" per miss, so the capture below is the whole list;</item>
    /// <item>every <c>ArbiterOwned</c> opcode came out of that with a handler.</item>
    /// </list>
    /// A wrong data.json passes every other check in this file and then silently unregisters half
    /// the login chain, which is the failure this exists to catch.
    /// </summary>
    public static List<SelfTestResult> CheckHandlers(string? dataJsonPath, string versionKey, string? defsFolder)
    {
        var results = new List<SelfTestResult>();
        OpcodeTable table;
        DefinitionRegistry defs;
        try
        {
            if (string.IsNullOrEmpty(dataJsonPath) || !File.Exists(dataJsonPath))
                throw new FileNotFoundException($"opcode map not found (looked at {Show(dataJsonPath)})");
            if (string.IsNullOrEmpty(defsFolder) || !Directory.Exists(defsFolder))
                throw new DirectoryNotFoundException($"packet defs not found (looked at {Show(defsFolder)})");
            table = OpcodeTable.LoadFromFile(dataJsonPath, versionKey);
            defs = DefinitionRegistry.LoadFromFolder(defsFolder,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }
        catch (Exception ex)
        {
            results.Add(new SelfTestResult("handler registry", false, $"{ex.GetType().Name}: {ex.Message}"));
            results.Add(new SelfTestResult("Arbiter-owned", false, "not checked - the registry could not be built"));
            return results;
        }

        var capture = new CaptureLoggerFactory();
        var dispatcher = new TeraSharp.Arbiter.Network.PacketDispatcher(
            Microsoft.Extensions.Logging.LoggerFactoryExtensions
                .CreateLogger<TeraSharp.Arbiter.Network.PacketDispatcher>(capture));
        try
        {
            TeraSharp.Arbiter.Handlers.HandlerRegistry.RegisterAll(dispatcher, table, defs, capture);
        }
        catch (Exception ex)
        {
            results.Add(new SelfTestResult("handler registry", false, $"RegisterAll threw {ex.GetType().Name}: {ex.Message}"));
            results.Add(new SelfTestResult("Arbiter-owned", false, "not checked - RegisterAll threw"));
            return results;
        }

        var unmapped = capture.Errors
            .Where(e => e.Contains("not in opcode map", StringComparison.Ordinal))
            .Select(e => e.Split(' ')[0])
            .ToList();
        results.Add(unmapped.Count == 0
            ? new SelfTestResult("handler registry", true,
                $"{dispatcher.RegisteredCount} opcode(s) registered, every name resolved in {versionKey}")
            : new SelfTestResult("handler registry", false,
                $"{unmapped.Count} name(s) not in the {versionKey} map: {Join(unmapped)}"));

        var owned = TeraSharp.Arbiter.Handlers.ArbiterClientHandlers.ArbiterOwned;
        var noHandler = owned
            .Where(op => !dispatcher.IsRegistered(op) && Array.IndexOf(ArbiterOwnedWithoutHandler, op) < 0)
            .Select(op => $"0x{op:X4} ({op})")
            .ToList();
        results.Add(noHandler.Count == 0
            ? new SelfTestResult("Arbiter-owned", true,
                $"all {owned.Count} opcode(s) have a handler")
            : new SelfTestResult("Arbiter-owned", false,
                $"{noHandler.Count} of {owned.Count} would be dropped, not forwarded: {Join(noHandler)}"));
        return results;
    }

    /// <summary>
    /// Every allow-list opcode has a row in status/PERSISTENCE-MAP.md.
    ///
    /// <para>The test project checks the other direction (documented =&gt; answered). This is the
    /// one T54 found had drifted: 27 opcodes the code answered had no row, so the map understated
    /// what the Arbiter owns. Optional, because a published build does not ship status/.</para>
    /// </summary>
    public static SelfTestResult CheckPersistenceMap(string? mapPath)
    {
        const string name = "persistence map";
        if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
            return new SelfTestResult(name, false, $"not found (looked at {Show(mapPath)})", Required: false);
        try
        {
            var documented = ParseDocumentedOpcodes(File.ReadAllText(mapPath));
            var allow = AllowListOpcodes();
            var missing = allow.Where(op => !documented.Contains(op)).Select(op => $"0x{op:X4}").ToList();
            return missing.Count == 0
                ? new SelfTestResult(name, true,
                    $"{allow.Count} allow-list opcode(s), all documented ({documented.Count} row(s))  {mapPath}",
                    Required: false)
                : new SelfTestResult(name, false,
                    $"{missing.Count} allow-list opcode(s) have no row: {Join(missing)}  {mapPath}",
                    Required: false);
        }
        catch (Exception ex)
        {
            return new SelfTestResult(name, false, $"{ex.GetType().Name}: {ex.Message}  {mapPath}", Required: false);
        }
    }

    /// <summary>
    /// The request opcode out of every table row whose first cell is a bare <c>0x…</c> and whose
    /// second cell names a reply (or "none"). Same shape as the test project's parser, so a row
    /// that satisfies one satisfies the other; backticked first cells are skipped on purpose -
    /// that is how the one-way and client-opcode tables opt out.
    /// </summary>
    public static HashSet<ushort> ParseDocumentedOpcodes(string markdown)
    {
        var found = new HashSet<ushort>();
        foreach (var raw in (markdown ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|')) continue;
            var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length < 2) continue;
            var cell = cells[0].Trim();
            if (!cell.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            var reply = cells[1].Trim();
            if (!reply.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && !reply.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            var bits = cell.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length < 1) continue;
            if (ushort.TryParse(bits[0].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var op))
                found.Add(op);
        }
        return found;
    }

    /// <summary>Walks up from the running binary looking for a repo-relative file; null if absent.</summary>
    public static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Collects Error-level messages so RegisterAll can be run without a real log.</summary>
    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        public readonly List<string> Errors = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Errors);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CaptureLogger : ILogger
        {
            private readonly List<string> _sink;
            public CaptureLogger(List<string> sink) => _sink = sink;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error && formatter != null) _sink.Add(formatter(state, exception));
            }
        }
    }

    // ---- The whole set ----

    /// <summary>
    /// Every dependency, in the order it matters at startup. <paramref name="dataDir"/> is the
    /// repo's data folder (where starter_blob.bin and friends live).
    /// </summary>
    public static List<SelfTestResult> RunAll(
        ILogger log, string dataRoot, string packetLogs, string dbPath, string dataDir, string versionKey)
    {
        string Data(string name) => Path.Combine(dataDir, name);
        var results = new List<SelfTestResult>
        {
            CheckOpcodes(Path.Combine(dataRoot, "tera-server-proxy", "data", "data.json"), versionKey),
            CheckDefinitions(Path.Combine(dataRoot, "tera_v100_MASTER_FINAL"), log),
            // T209: starter_blob.bin is an OVERRIDE now - StarterBlob.Generate builds the record
            // when no file is there - so its absence is a WARN, not a bad deploy. A file of the
            // wrong SIZE is still reported, because that is a truncated copy rather than none.
            CheckFixedSize("starter blob", Data("starter_blob.bin"), DbProxyHandlers.WorldBlobSize,
                required: false),
            // T209c part 2: starter_inventory.bin is retired. economy.synthItemRecords defaults
            // to true and StarterInventory.BuildSynthetic builds the records, so there is no file
            // left to check for. A deployment that still has one keeps using it.
            // T209b: the burst is built from defs, so the check is that it builds - the capture
            // file is the tests' evidence now and no longer has to ship.
            CheckHandshakeBurstBuild(),
            CheckExists("world replay log", Path.Combine(packetLogs, "arb_world.log")),
            CheckExists("spawn replay", Path.Combine(packetLogs, "full_replay.txt"), required: false),
            CheckFolder("Datasheet folder", Path.Combine(dataRoot, "Executable", "Datasheet"),
                "DefaultSkillSet.xml", required: false),
            CheckDatabase(dbPath),
            CheckLoopbackBind(),   // T215: not loopback is a failure, not a warning
        };

        // T58: the wiring checks. These run the real registration and allow-list code against
        // the data that shipped, so a build whose code is right and whose data.json is wrong
        // fails here instead of at the first login.
        results.AddRange(CheckHandlers(
            Path.Combine(dataRoot, "tera-server-proxy", "data", "data.json"), versionKey,
            Path.Combine(dataRoot, "tera_v100_MASTER_FINAL")));
        results.Add(CheckPersistenceMap(FindRepoFile(Path.Combine("status", "PERSISTENCE-MAP.md"))));
        return results;
    }

    // =========================================================================================
    // T113: --check-config. Everything this process resolved, then exit.
    //
    // The sibling of --selftest: that one asks whether the DEPLOY is complete, this one asks
    // what the CONFIGURATION came out as. Almost every go-live mistake is an environment
    // variable that is unset, set on the wrong account, or set to the wrong kind of value -
    // TERASHARP_GM_ACCOUNTS holding a display name instead of an accountDBID is the classic -
    // and none of those show up as an error anywhere. They show up as "it did not work".
    // =========================================================================================

    /// <summary>Every TERASHARP_* variable, in the order the docs discuss them.</summary>
    public static readonly string[] KnownVariables =
    {
        "TERASHARP_AUTH", "TERASHARP_AUTH_URL",
        "TERASHARP_GM_ACCOUNTS",
        "TERASHARP_ADMIN_TOKEN", "TERASHARP_ADMIN_PORT",
        "TERASHARP_BIND",
        "TERASHARP_DATA", "TERASHARP_DB", "TERASHARP_LOGS",
        "TERASHARP_LOG_LEVEL",
        "TERASHARP_ITEM_STRSHEET", "TERASHARP_ITEM_NAMES",
        "TERASHARP_DATASHEET", "TERASHARP_STARTER_BLOB", "TERASHARP_STARTER_INVENTORY",
        "TERASHARP_SYNTH_ITEM_RECORDS",                                                    // T209
        "TERASHARP_START_OVERRIDE",
        "TERASHARP_API_GATEWAY", "TERASHARP_DB_SERVER_NAME", "TERASHARP_API_JWT_SECRET",   // T124: Alt+A
        "TERASHARP_API_GATEWAY_SERVE", "TERASHARP_API_GATEWAY_BIND",                       // T132: the probe at that address
        // T204: the rest, so this list and teras.json describe the same set and --check-config is
        // the one place to look. Every one of these was readable and unreported before.
        "TERASHARP_SERVERCONFIG", "TERASHARP_STANDALONE", "TERASHARP_RANKING_SEASON",
        "TERASHARP_MATCH_ENTRY_SECONDS", "TERASHARP_MATCH_MIN_MEMBERS",
        "TERASHARP_BF_ENTER_DELAY",                                                        // T208b
        "TERASHARP_BG_MAX_HEALERS", "TERASHARP_BG_MAX_TANKS", "TERASHARP_BROKER_FEE_PERCENT",
        // T207: the shop page and the hub tera-api delivers purchases through.
        "TERASHARP_SHOP_URL", "TERASHARP_HUB_LISTEN", "TERASHARP_HUB_ENABLED", "TERASHARP_HUB_SERVER_ID",
        "TERASHARP_PARCEL_DELETE_ON_COLLECT",                                              // T234
        "TERASHARP_ITEM_CLAIM_MAX_AGE",                                                    // T230
    };

    /// <summary>Variables whose value must never reach a log or a console.</summary>
    public static bool IsSecret(string name)
        => name.EndsWith("_TOKEN", StringComparison.Ordinal)
        || name.EndsWith("_PASSWORD", StringComparison.Ordinal)
        // T124: TERASHARP_API_JWT_SECRET is an HS256 signing key. Without this suffix
        // --check-config printed it in full, which is the leak this predicate exists to stop.
        || name.EndsWith("_SECRET", StringComparison.Ordinal)
        || name.EndsWith("_KEY", StringComparison.Ordinal);

    /// <summary>
    /// What to show for a variable: the value, <c>(unset)</c>, or - for a secret - its length
    /// only. Printing an admin token into a log file that gets pasted into a bug report is
    /// exactly how a loopback-only tool stops being loopback-only.
    /// </summary>
    public static string Display(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(unset)";
        if (IsSecret(name)) return "(set, " + value.Length + " chars)";
        return value;
    }

    /// <summary>
    /// The report. <paramref name="resolved"/> is what Program actually computed - the paths and
    /// ports after every default and override - because re-deriving them here would be a second
    /// implementation that could disagree with the first.
    /// </summary>
    public static string BuildConfigReport(IReadOnlyList<(string Label, string Value)> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var sb = new System.Text.StringBuilder();
        sb.Append("TeraSharp configuration").Append(Environment.NewLine);

        // T204: which file the settings came from, before the settings themselves - reading the
        // right values out of the wrong file is the mistake this block exists to catch.
        sb.Append(Environment.NewLine).Append("  settings").Append(Environment.NewLine);
        foreach (var line in TerasConfig.Describe())
            sb.Append("    ").Append(line).Append(Environment.NewLine);

        sb.Append(Environment.NewLine).Append("  values").Append(Environment.NewLine);
        foreach (var name in KnownVariables)
        {
            string shown = Display(name, TerasConfig.Get(name));
            sb.Append("    ").Append(name.PadRight(30)).Append(shown.PadRight(34))
              .Append('[').Append(TerasConfig.SourceOf(name)).Append(']').Append(Environment.NewLine);
        }

        sb.Append(Environment.NewLine).Append("  resolved").Append(Environment.NewLine);
        foreach (var (label, value) in resolved)
            sb.Append("    ").Append(label.PadRight(30)).Append(value).Append(Environment.NewLine);

        // T159: where every sheet-backed value came from - the sheet, or the built-in copy.
        sb.Append(Environment.NewLine).Append("  datasheets").Append(Environment.NewLine);
        foreach (var line in DatasheetLoader.Describe())
            sb.Append("    ").Append(line).Append(Environment.NewLine);

        sb.Append(Environment.NewLine).Append("  notes").Append(Environment.NewLine);
        foreach (var note in Notes())
            sb.Append("    ").Append(note).Append(Environment.NewLine);
        return sb.ToString();
    }

    /// <summary>
    /// The things that are legal, silent, and almost always a mistake. Each one has cost
    /// somebody an afternoon.
    /// </summary>
    public static IEnumerable<string> Notes()
    {
        string? gm = TerasConfig.Get("TERASHARP_GM_ACCOUNTS");
        if (!string.IsNullOrWhiteSpace(gm))
        {
            bool allNumeric = true;
            foreach (var part in gm.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (!long.TryParse(part.Trim(), out _)) { allNumeric = false; break; }
            if (!allNumeric)
                yield return "! TERASHARP_GM_ACCOUNTS has a non-numeric entry. The launcher puts the "
                           + "tera-api accountDBID in C_LOGIN_ARBITER.name, so a display name never "
                           + "matches and that account silently gets a normal login.";
        }

        // T215: auth first - it is the one setting that decides whether this is a server or an
        // open door, and setup.ps1 writes auth.enabled=true so an OFF here means somebody turned
        // it off.
        if (!Auth.AuthProviders.EnabledFromEnvironment(TerasConfig.Get("TERASHARP_AUTH")))
            yield return "! auth is OPEN - every login is accepted. TERASHARP_AUTH=true, with "
                       + "TERASHARP_AUTH_URL pointing at tera-api. start.ps1 refuses to boot "
                       + "without it unless it is given -Insecure.";
        else if (string.IsNullOrWhiteSpace(TerasConfig.Get("TERASHARP_AUTH_URL")))
            yield return "! TERASHARP_AUTH is on but TERASHARP_AUTH_URL is empty - there is nowhere "
                       + "to validate a ticket against.";

        string? token = TerasConfig.Get("TERASHARP_ADMIN_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            yield return "- the admin web is OFF (TERASHARP_ADMIN_TOKEN is unset), which fails closed.";
        else if (token.Length < 24)
            yield return "! TERASHARP_ADMIN_TOKEN is short. It is the only thing in front of the "
                       + "write endpoints; use 48 characters of randomness.";

        string? port = TerasConfig.Get("TERASHARP_ADMIN_PORT");
        if (string.IsNullOrWhiteSpace(port) || port.Trim() == "8050")
            yield return "! the admin web is on 8050, which !SECURITY_TODO lists as tera-api's own "
                       + "admin panel. Whichever starts second loses. Set TERASHARP_ADMIN_PORT=8051.";

        // T215: this one is now a FAILING self-test as well (CheckLoopbackBind), so the note
        // says where the error comes from rather than being the only sign of it.
        if (!CheckLoopbackBind().Pass)
            yield return "! TERASHARP_BIND is not 127.0.0.1. Port 7701 does not check GM privilege "
                       + "on C_ADMIN - the proxy on 7801 is the only gate. Keep it on loopback: "
                       + "--selftest fails on this and start.ps1 refuses to boot without -Insecure.";

        // T124: the two fields that open the In-Game Operation Tool. Not a failure - nothing
        // on this stack verifies the token - but an unset key is worth one line.
        if (!Auth.ApiGatewayToken.HasConfiguredSecret)
            yield return "- " + Auth.ApiGatewayToken.SecretVariable + " is unset, so the Alt+A token is "
                       + "signed with a per-process key. The panel still opens (tera-api has no "
                       + "jwt.verify); set it to API_PORTAL_SECRET before enabling verification.";

        yield return "auth mode: " + Auth.AuthProviders.DescribeMode();
        yield return Auth.ApiGatewayToken.DescribeMode();
    }

    /// <summary>One line per check, then a summary. Returns the number of REQUIRED failures.</summary>
    public static int Report(IReadOnlyList<SelfTestResult> results, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(results);
        int failures = 0, warnings = 0;
        var sb = new System.Text.StringBuilder();
        sb.Append("selftest: ").Append(results.Count).Append(" check(s)").Append(Environment.NewLine);
        foreach (var r in results)
        {
            if (!r.Pass) { if (r.Required) failures++; else warnings++; }
            sb.Append("  ").Append(Format(r)).Append(Environment.NewLine);
        }

        string summary =
            failures > 0
                ? $"selftest: {failures} REQUIRED dependency(ies) missing - do not start this build"
                : warnings > 0
                    ? $"selftest: {results.Count - warnings}/{results.Count} PASS, {warnings} optional missing - it will run"
                    : $"selftest: {results.Count}/{results.Count} PASS - the deploy is complete";
        sb.Append("  ").Append(summary);

        // T58: ONE write. On netcup the old per-line ILogger calls interleaved with the console
        // writer and only 4 of 10 lines survived; building the whole block first and emitting it
        // in a single Console write plus an explicit flush is what makes it atomic. The logger
        // still gets the same block, so a file/service sink loses nothing.
        try
        {
            Console.Out.Write(sb.ToString());
            Console.Out.Write(Environment.NewLine);
            Console.Out.Flush();
        }
        catch (IOException) { /* no console (running as a service) - the log call below carries it */ }

        if (failures > 0) log.LogError("{Report}", sb.ToString());
        else if (warnings > 0) log.LogWarning("{Report}", sb.ToString());
        else log.LogInformation("{Report}", sb.ToString());
        return failures;
    }

    /// <summary>`[PASS] name - detail`, padded so the columns line up in a console log.</summary>
    public static string Format(SelfTestResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string tag = r.Pass ? "PASS" : r.Required ? "FAIL" : "WARN";
        return $"[{tag}] {r.Name,-20} {r.Detail}";
    }

    private static string Show(string? path) => string.IsNullOrEmpty(path) ? "(no path)" : path;

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }
}
