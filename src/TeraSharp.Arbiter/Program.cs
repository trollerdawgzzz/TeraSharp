// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Auth;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter;

public static class Program
{
    private const int ProtocolVersion = 376012;
    private const int MajorPatchVersion = 100;
    private const string ProtocolVersionKey = "376012";

    private static string DataRoot =>
        TerasConfig.Get("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
    private static string DataJsonPath => Path.Combine(DataRoot, "tera-server-proxy", "data", "data.json");
    private static string DefinitionsPath => Path.Combine(DataRoot, "tera_v100_MASTER_FINAL");
    private static string PacketLogsPath =>
        TerasConfig.Get("TERASHARP_LOGS") ?? "logs";
    private static string WorldReplayPath => Path.Combine(PacketLogsPath, "arb_world.log");
    private static string SpawnReplayPath => Path.Combine(PacketLogsPath, "full_replay.txt");
    private static string DbPath =>
        TerasConfig.Get("TERASHARP_DB") ?? Path.Combine(PacketLogsPath, "terasharp.db");

    private static string BindAddress => TerasConfig.Get("TERASHARP_BIND") ?? "127.0.0.1";
    private const int BindPort = 7701;

    /// <summary>Login authority (status/AUTH-DESIGN.md). AcceptAll by default; TERASHARP_AUTH=true selects tera-api.</summary>
    public static IAuthProvider Auth { get; internal set; } = new AcceptAllAuthProvider();

    public static WorldBridge? World { get; private set; }
    public static CharacterStore? Store { get; private set; }

    public static async Task Main(string[] args)
    {
        var logFolder = PacketLogsPath;
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            // T106: console shows Warning+ by default (TERASHARP_LOG_LEVEL overrides); the daily file takes everything
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
             .AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
                 null, TeraSharp.Arbiter.Web.ArbiterLogProvider.ConsoleLevel());
            b.AddProvider(new TeraSharp.Arbiter.Web.ArbiterLogProvider(logFolder));
            b.SetMinimumLevel(LogLevel.Debug);
        });

        var log = loggerFactory.CreateLogger("Arbiter");
        // T204: teras.json, if there is one. The environment still overrides it, so this changes
        // nothing for a deployment that already exports variables.
        TerasConfig.UseWarningSink(m => log.LogWarning("{Message}", m));
        bool selfTest = args.Any(a => a is "--selftest" or "/selftest");
        Auth = AuthProviders.FromEnvironment(log);
        log.LogInformation("TeraSharp Arbiter starting (protocol {Proto}, patch {Patch})", ProtocolVersion, MajorPatchVersion);
        log.LogInformation("Settings: {File}", TerasConfig.LoadedPath ?? "(environment only)");
        log.LogInformation("Data root: {Root}, logs: {Logs}, db: {Db}", DataRoot, PacketLogsPath, DbPath);
        ItemNames.DataRoot = DataRoot;      // T113: item strsheets resolve under the data root
        if (args.Any(a => a is "--check-config" or "/check-config"))
        {
            Console.WriteLine(SelfTest.BuildConfigReport(new[] {
                ("data root", DataRoot), ("opcodes", DataJsonPath), ("definitions", DefinitionsPath),
                ("logs", PacketLogsPath), ("db", DbPath),
                ("game port", BindAddress + ":" + BindPort),
                ("admin web", "127.0.0.1:" + (TerasConfig.Get("TERASHARP_ADMIN_PORT") ?? "8050")) }));
            return;
        }
        log.LogInformation("Auth provider: {Name}", Auth.Name);
        TeraSharp.Arbiter.World.DatasheetLoader.LoadAll(loggerFactory.CreateLogger("Datasheets"));   // T159: one line per sheet at startup

        // T184h: report the retired partial-group setting so deployed configurations are corrected.
        int matchMin = MatchQueueManager.MinMembersOverride();
        if (matchMin > 0)
            log.LogWarning("{Var}={N} is retired and ignored. Dungeon matching requires the "
                + "configured MatchingRoleTemplate totalUser and roles; remove this setting.",
                MatchQueueManager.MinMembersVariable, matchMin);

        OpcodeTable opcodes;
        try
        {
            opcodes = OpcodeTable.LoadFromFile(DataJsonPath, ProtocolVersionKey);
            log.LogInformation("Loaded {Count} opcodes for protocol {Ver}", opcodes.Count, opcodes.Version);
        }
        catch (Exception ex) { log.LogCritical(ex, "Failed to load opcode table from {P}", DataJsonPath); return; }

        DefinitionRegistry defs;
        try { defs = DefinitionRegistry.LoadFromFolder(DefinitionsPath, loggerFactory.CreateLogger<DefinitionRegistry>()); }
        catch (Exception ex) { log.LogCritical(ex, "Failed to load definitions from {P}", DefinitionsPath); return; }

        // Inline defs for packets not in the data folder.
        // S_PREPARE_EXIT: countdown before client close (mirrors S_PREPARE_RETURN_TO_LOBBY).
        // S_EXIT: final close signal with category field.
        defs.RegisterIfMissing("S_PREPARE_EXIT", ("int32", "time"));
        defs.RegisterIfMissing("S_EXIT", ("int32", "category"));

        SpawnReplay.Load(SpawnReplayPath, log);

        var worldReplay = WorldReplayTable.Load(WorldReplayPath, loggerFactory.CreateLogger<WorldReplayTable>());
        // Persistence
        Store = new CharacterStore(DbPath, loggerFactory.CreateLogger<CharacterStore>());
        if (selfTest)
        {
            // --selftest (T37, status/LIVE-CHECKLIST.md): verify every file/folder/schema dependency and
            // exit non-zero on a required failure, so a bad deploy is caught before the first login.
            // ship.ps1 publishes data\ next to the exe.
            var results = SelfTest.RunAll(log, DataRoot, PacketLogsPath, DbPath,
                Path.Combine(AppContext.BaseDirectory, "data"), ProtocolVersionKey);
            int failed = SelfTest.Report(results, log);
            Store.Dispose();
            Environment.Exit(failed == 0 ? 0 : 1);
        }
        SeedFromCapture(Store, worldReplay, log);

        World = new WorldBridge(worldReplay, loggerFactory.CreateLogger<WorldBridge>())
        {
            DbProxy = new DbProxyHandlers(Store, loggerFactory.CreateLogger<DbProxyHandlers>()),
        };
        // SA_ENTER_WORLD_FAIL (0x138D) hooks: map the gameId World echoes back to the session and
        // re-send AS_ENTER_WORLD at the stored return point (status/ENTER-WORLD-FALLBACK.md).
        {
            var world = World; var dbProxy = World.DbProxy!;
            dbProxy.PlayerIdForGameId = gameId => (int)(world.PlayerForGameId(gameId)?.SelectedCharacter?.Id ?? 0);
            dbProxy.ResendEnterWorld = f =>
            {
                var s = world.PlayerForGameId(f.ArbiterUser);
                if (s != null) WorldEntry.ResendEnterWorld(s, f, log);
            };
        }

        var dispatcher = new PacketDispatcher(loggerFactory.CreateLogger<PacketDispatcher>());
        HandlerRegistry.RegisterAll(dispatcher, opcodes, defs, loggerFactory);
        log.LogInformation("Registered {Count} packet handler(s)", dispatcher.RegisteredCount);

        var endpoint = new IPEndPoint(IPAddress.Parse(BindAddress), BindPort);
        var server = new TcpServer(endpoint, dispatcher, opcodes, defs, ProtocolVersion, MajorPatchVersion, loggerFactory);
        // T101: admin web (loopback only, TERASHARP_ADMIN_TOKEN required; null when unset)
        using var admin = TeraSharp.Arbiter.Web.AdminServer.TryStart(Store, () => (World?.InWorldSessions() ?? new List<GameSession>())
            .Select(s => new TeraSharp.Arbiter.Web.AdminOnlineRow((int)(s.SelectedCharacter?.Id ?? 0),
                s.SelectedCharacter?.Name ?? "", s.SelectedCharacter?.Level ?? 0,
                s.SelectedCharacter?.Zone ?? 0, s.Account.Name)).ToList(), log);
        var apiGateway = TeraSharp.Arbiter.Web.ApiGatewayServer.TryStart(log);   // T132: probe listener at apiServerAddress (TERASHARP_API_GATEWAY_SERVE)
        if (admin != null)
        {
            admin.Api.StartedAt = DateTimeOffset.UtcNow;
            ArbiterClientHandlers.PlayTimeLookup = s => { int id = (int)(s.SelectedCharacter?.Id ?? 0);
                return (int)((Store?.GetCharacterPlaySeconds(id) ?? 0)
                    + DbProxyHandlers.SecondsInWorld(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds())); };   // T113
            admin.Api.WorldStatus = () => (World?.LinkCount ?? 0, World?.IsReady ?? false, World?.InWorldSessions().Count ?? 0);
            admin.Api.KickPlayer = id => { var s = World?.SessionForPlayerId(id); if (s == null) return false; s.Close(); return true; };
            admin.Api.Announce = text => { var all = World?.InWorldSessions() ?? new List<GameSession>();
                foreach (var s in all) s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = text });
                return all.Count; };
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; log.LogInformation("Shutdown requested"); cts.Cancel(); };

        await Task.WhenAll(World.RunAsync(cts.Token), server.RunAsync(cts.Token));
        Store.Dispose();
        log.LogInformation("Arbiter stopped");
    }

    /// <summary>
    /// First run: create account 1 / character 1 ("dob") using the world blob from
    /// the capture, so the existing character keeps working. After this the DB is
    /// the source of truth and the capture blob is never used again.
    /// </summary>
    private static void SeedFromCapture(CharacterStore store, WorldReplayTable replay, ILogger log)
    {
        var acct = store.GetOrCreateAccount("1");
        if (store.GetCharacters(acct.Id).Count > 0) return;

        var body = replay.CharacterDataBody; // DBS_USER_ENTERWORLD payload: [off][len][replyId][found][blob]
        byte[]? blob = null;
        if (body != null && body.Length >= 13 + DbProxyHandlers.WorldBlobSize)
        {
            blob = new byte[DbProxyHandlers.WorldBlobSize];
            Array.Copy(body, 13, blob, 0, blob.Length);
        }

        var fake = new Game.FakeCharacter();
        store.CreateCharacter(new CharacterRecord
        {
            AccountId = acct.Id,
            Name = fake.Name,
            Gender = fake.Gender, Race = fake.Race, Class = fake.Class,
            Level = fake.Level, TemplateId = fake.TemplateId,
            Zone = fake.Zone, X = fake.X, Y = fake.Y, Z = fake.Z,
            Appearance = fake.Appearance, Details = fake.Details, Shape = fake.Shape,
            Weapon = fake.Weapon, Body = fake.Body, Hand = fake.Hand, Feet = fake.Feet,
            Position = fake.Position,
            WorldBlob = blob,
        });
        log.LogInformation("Seeded DB with '{Name}' (blob {Len} bytes from capture)", fake.Name, blob?.Length ?? 0);
    }
}
