using System.Net;
using Microsoft.Extensions.Logging;
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
        Environment.GetEnvironmentVariable("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
    private static string DataJsonPath => Path.Combine(DataRoot, "tera-server-proxy", "data", "data.json");
    private static string DefinitionsPath => Path.Combine(DataRoot, "tera_v100_MASTER_FINAL");
    private static string PacketLogsPath =>
        Environment.GetEnvironmentVariable("TERASHARP_LOGS") ?? @"D:\packetlogs";
    private static string WorldReplayPath => Path.Combine(PacketLogsPath, "arb_world.log");
    private static string SpawnReplayPath => Path.Combine(PacketLogsPath, "full_replay.txt");
    private static string DbPath =>
        Environment.GetEnvironmentVariable("TERASHARP_DB") ?? Path.Combine(PacketLogsPath, "terasharp.db");

    private static string BindAddress => Environment.GetEnvironmentVariable("TERASHARP_BIND") ?? "127.0.0.1";
    private const int BindPort = 7701;

    /// <summary>When true, C_LOGIN_ARBITER validates accounts against tera-api. Default off (accept all).</summary>
    public static bool AuthEnabled { get; internal set; }
    /// <summary>Base URL of the tera-api auth endpoint (default http://127.0.0.1:8080).</summary>
    public static string AuthApiUrl { get; internal set; } = "http://127.0.0.1:8080";

    public static WorldBridge? World { get; private set; }
    public static CharacterStore? Store { get; private set; }

    public static async Task Main()
    {
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            b.SetMinimumLevel(LogLevel.Debug);
        });

        var log = loggerFactory.CreateLogger("Arbiter");
        AuthEnabled = Environment.GetEnvironmentVariable("TERASHARP_AUTH") is "true" or "1";
        AuthApiUrl = Environment.GetEnvironmentVariable("TERASHARP_AUTH_URL") ?? "http://127.0.0.1:8080";
        log.LogInformation("TeraSharp Arbiter starting (protocol {Proto}, patch {Patch})", ProtocolVersion, MajorPatchVersion);
        log.LogInformation("Data root: {Root}, logs: {Logs}, db: {Db}", DataRoot, PacketLogsPath, DbPath);
        log.LogInformation("Auth: {Enabled} (API {Url})", AuthEnabled ? "enabled" : "disabled", AuthApiUrl);

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
        SeedFromCapture(Store, worldReplay, log);

        World = new WorldBridge(worldReplay, loggerFactory.CreateLogger<WorldBridge>())
        {
            DbProxy = new DbProxyHandlers(Store, loggerFactory.CreateLogger<DbProxyHandlers>()),
        };

        var dispatcher = new PacketDispatcher(loggerFactory.CreateLogger<PacketDispatcher>());
        HandlerRegistry.RegisterAll(dispatcher, opcodes, defs, loggerFactory);
        log.LogInformation("Registered {Count} packet handler(s)", dispatcher.RegisteredCount);

        var endpoint = new IPEndPoint(IPAddress.Parse(BindAddress), BindPort);
        var server = new TcpServer(endpoint, dispatcher, opcodes, defs, ProtocolVersion, MajorPatchVersion, loggerFactory);

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
