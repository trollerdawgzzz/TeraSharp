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

    /// <summary>handshake_burst.bin: parsed, not just present - a truncated file parses to fewer frames.</summary>
    public static SelfTestResult CheckHandshakeBurst(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new SelfTestResult("handshake burst", false, $"not found (looked at {Show(path)})");
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

    // ---- The whole set ----

    /// <summary>
    /// Every dependency, in the order it matters at startup. <paramref name="dataDir"/> is the
    /// repo's data folder (where starter_blob.bin and friends live).
    /// </summary>
    public static List<SelfTestResult> RunAll(
        ILogger log, string dataRoot, string packetLogs, string dbPath, string dataDir, string versionKey)
    {
        string Data(string name) => Path.Combine(dataDir, name);
        return new List<SelfTestResult>
        {
            CheckOpcodes(Path.Combine(dataRoot, "tera-server-proxy", "data", "data.json"), versionKey),
            CheckDefinitions(Path.Combine(dataRoot, "tera_v100_MASTER_FINAL"), log),
            CheckFixedSize("starter blob", Data("starter_blob.bin"), DbProxyHandlers.WorldBlobSize),
            CheckFixedSize("starter inventory", Data("starter_inventory.bin"), DbProxyHandlers.StarterInventorySize),
            CheckRecordFile("promotion records", Data("promotions_147E.bin"), DbProxyHandlers.PromotionRecordSize),
            CheckHandshakeBurst(Data(DbProxyHandlers.HandshakeBurstFile)),
            CheckExists("world replay log", Path.Combine(packetLogs, "arb_world.log")),
            CheckExists("spawn replay", Path.Combine(packetLogs, "full_replay.txt"), required: false),
            CheckFolder("Datasheet folder", Path.Combine(dataRoot, "Executable", "Datasheet"),
                "DefaultSkillSet.xml", required: false),
            CheckDatabase(dbPath),
        };
    }

    /// <summary>One line per check, then a summary. Returns the number of REQUIRED failures.</summary>
    public static int Report(IReadOnlyList<SelfTestResult> results, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(results);
        int failures = 0, warnings = 0;
        foreach (var r in results)
        {
            if (r.Pass) log.LogInformation("{Line}", Format(r));
            else if (r.Required) { failures++; log.LogError("{Line}", Format(r)); }
            else { warnings++; log.LogWarning("{Line}", Format(r)); }
        }
        if (failures == 0 && warnings == 0)
            log.LogInformation("selftest: {N}/{Total} PASS - the deploy is complete", results.Count, results.Count);
        else if (failures == 0)
            log.LogWarning("selftest: {P}/{N} PASS, {W} optional missing - it will run",
                results.Count - warnings, results.Count, warnings);
        else
            log.LogError("selftest: {F} REQUIRED dependency(ies) missing - do not start this build",
                failures);
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
