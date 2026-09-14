using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>One parsed GM command line: the verb and its arguments.</summary>
public sealed record GmCommandLine(string Name, IReadOnlyList<string> Args, string Raw)
{
    public string Arg(int i) => i >= 0 && i < Args.Count ? Args[i] : "";

    /// <summary>
    /// The line as the real Arbiter rebuilds it before forwarding: the verb, then each argument
    /// prefixed with a single space (ArbiterBypassCommandHandler::HandleCommand writes 0x20
    /// between them). Quoting is NOT re-added - the Arbiter never had any.
    /// </summary>
    public string Rebuilt()
    {
        var sb = new StringBuilder(Name);
        foreach (var a in Args) sb.Append(' ').Append(a);
        return sb.ToString();
    }
}

/// <summary>
/// Splits a GM command line into a verb and arguments.
///
/// <para>The real Arbiter streams the wide string into a <c>std::basic_stringstream</c> and takes
/// whitespace-separated tokens (CommandDistributor::ProcessCommand, Arb_part_007.c:3300-3325) -
/// no quoting, no escapes. We add double-quoted arguments on top, because character names with
/// spaces exist and a test server needs them; everything else is the same split.</para>
///
/// <para>The <c>/@</c> a player types never reaches the server: the client strips it and sends
/// the bare line in C_ADMIN / C_OP_COMMAND (there is no '/' or '@' comparison anywhere in
/// ArbiterServer.exe - status/GM-DESIGN.md section 1). We tolerate a leading <c>/@</c>, <c>/</c>
/// or <c>@</c> anyway so a raw test client can send either form.</para>
/// </summary>
public static class GmCommandParser
{
    public static GmCommandLine? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string raw = line.Trim();

        string body = raw;
        if (body.StartsWith("/@", StringComparison.Ordinal)) body = body[2..];
        else if (body.Length > 0 && (body[0] == '/' || body[0] == '@')) body = body[1..];
        body = body.TrimStart();
        if (body.Length == 0) return null;

        var tokens = Tokenise(body);
        if (tokens.Count == 0) return null;

        string name = tokens[0];
        tokens.RemoveAt(0);
        return new GmCommandLine(name, tokens, raw);
    }

    /// <summary>Whitespace split, with double quotes grouping one argument.</summary>
    public static List<string> Tokenise(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        var current = new StringBuilder();
        bool inQuotes = false, started = false;
        foreach (char c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; started = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            current.Append(c);
            started = true;
        }
        if (started) result.Add(current.ToString());
        return result;
    }
}

/// <summary>
/// Which side owns which command. The real Arbiter learns the World's list at runtime -
/// WorldServer sends SA_REGISTER_WORLD_COMMAND (0x144C) and
/// ArbiterCommandDistributor::RegisterWorldCommands registers every name into a bypass bucket
/// whose handler is a null stub (Arb_part_085.c:16540-16578). We have no such handshake, so the
/// two catalogues the human extracted from the binaries are the list:
/// <c>status/GM-COMMANDS-ARBITER.md</c> (192 the Arbiter owns) and
/// <c>status/GM-COMMANDS-FULL.md</c> (416 the World owns).
/// </summary>
public static class GmCommandCatalog
{
    private static readonly object Gate = new();
    private static HashSet<string>? _arbiter;
    private static HashSet<string>? _world;

    /// <summary>Names the Arbiter owns. Empty when the catalogue file was not found.</summary>
    public static IReadOnlySet<string> ArbiterCommands { get { EnsureLoaded(); return _arbiter!; } }

    /// <summary>Names the World owns - these are forwarded verbatim.</summary>
    public static IReadOnlySet<string> WorldCommands { get { EnsureLoaded(); return _world!; } }

    public static bool IsWorldCommand(string name) => WorldCommands.Contains(name);
    public static bool IsArbiterCommand(string name) => ArbiterCommands.Contains(name);

    /// <summary>Replace the loaded catalogue (tests, and any future 0x144C handshake).</summary>
    public static void Set(IEnumerable<string> arbiter, IEnumerable<string> world)
    {
        lock (Gate)
        {
            _arbiter = new HashSet<string>(arbiter, StringComparer.OrdinalIgnoreCase);
            _world = new HashSet<string>(world, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Pull the command names out of one of the catalogue markdown files: every table row that
    /// starts with a backticked name, i.e. <c>| `add_exp` | ... |</c>.
    /// </summary>
    public static List<string> ParseMarkdown(string markdown)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return names;
        foreach (var rawLine in markdown.Split('\n'))
        {
            string line = rawLine.TrimStart();
            if (line.Length < 4 || line[0] != '|') continue;
            int open = line.IndexOf('`');
            if (open < 0) continue;
            int close = line.IndexOf('`', open + 1);
            if (close <= open + 1) continue;
            string name = line[(open + 1)..close].Trim();
            if (name.Length == 0 || name.Contains(' ')) continue;
            names.Add(name);
        }
        return names;
    }

    private static void EnsureLoaded()
    {
        if (_arbiter != null && _world != null) return;
        lock (Gate)
        {
            if (_arbiter != null && _world != null) return;
            _arbiter = new HashSet<string>(LoadFile("GM-COMMANDS-ARBITER.md"), StringComparer.OrdinalIgnoreCase);
            _world = new HashSet<string>(LoadFile("GM-COMMANDS-FULL.md"), StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<string> LoadFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "status", fileName);
            if (File.Exists(candidate))
            {
                try { return ParseMarkdown(File.ReadAllText(candidate)); }
                catch (IOException) { return new List<string>(); }
            }
        }
        return new List<string>();
    }
}

/// <summary>Who may run GM commands. See status/GM-DESIGN.md section 3.</summary>
public static class GmAccounts
{
    /// <summary>Comma-, semicolon- or space-separated account names.</summary>
    public const string EnvVariable = "TERASHARP_GM_ACCOUNTS";

    /// <summary>
    /// The level a listed account gets. The real Arbiter's own top level is 5: the
    /// <c>set_go on</c> command writes <c>User+0x3b98 = 5</c>
    /// (ArbiterQACommandHandler::SetGameOperator, Arb_part_044.c:3653). Every gate in the binary
    /// only tests <c>&gt;= 1</c>, so 5 is "all of it".
    /// </summary>
    public const int GmAdminLevel = 5;

    /// <summary>The level the gate demands - the binary tests <c>level &lt; 1</c> everywhere.</summary>
    public const int MinimumAdminLevel = 1;

    /// <summary>Is this account name in the allow-list value?</summary>
    public static bool IsListed(string? accountName, string? envValue)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(envValue)) return false;
        foreach (var part in envValue.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(part.Trim(), accountName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>As above, against the live environment.</summary>
    public static bool IsListed(string? accountName)
        => IsListed(accountName, Environment.GetEnvironmentVariable(EnvVariable));

    /// <summary>
    /// The level for this login: the allow-list wins (it is the bootstrap - without it nobody
    /// could ever run set_admin_level), otherwise whatever is stored on the account row.
    /// </summary>
    public static int LevelFor(string? accountName, int storedLevel)
        => IsListed(accountName) ? GmAdminLevel : storedLevel;
}

/// <summary>What the dispatcher decided to do with a line.</summary>
public enum GmDispatch
{
    /// <summary>Empty or unparseable - nothing happens.</summary>
    Empty,
    /// <summary>The session has no character/user object; the real Arbiter drops these.</summary>
    NoUser,
    /// <summary>Admin level below 1: the real Arbiter writes an abuse log and sends NOTHING.</summary>
    NotAuthorised,
    /// <summary>We implement it here.</summary>
    Local,
    /// <summary>The World owns it - forward with AS opcode 0x2829.</summary>
    ForwardToWorld,
    /// <summary>An Arbiter command we have not implemented yet.</summary>
    NotImplemented,
    /// <summary>In neither catalogue: S_SYSTEM_MESSAGE_CUSTOM "Invalid QA Command".</summary>
    Unknown,
}

/// <summary>
/// The GM command channel: C_ADMIN (0xA45C) and C_OP_COMMAND (0xE394), both carrying one wide
/// string. Full write-up, with every decompile reference: status/GM-DESIGN.md.
///
/// <para>Reply channel is S_SYSTEM_MESSAGE_CUSTOM (0x994C) - a single wide string, a plain
/// literal, never an <c>@id</c>. That is what every Arbiter-side command answers with
/// (FUN_1404d3c60, Arb_part_040.c:13453, a printf into a 2048-wchar buffer).</para>
/// </summary>
public sealed class GmCommandHandlers
{
    private readonly ILogger _log;
    public GmCommandHandlers(ILogger log) => _log = log;

    /// <summary>The Arbiter-&gt;World relay for a command the World owns. No symbolic name exists
    /// for it: 0x2829 is absent from the opcode-&gt;name table in Arb_part_003.c.</summary>
    /// <summary>
    /// <c>AS_ADMIN_COMMAND</c> - the name World's own opcode table gives 0x2829
    /// (WorldServer.exe.c:246833). T32 called it AS_BYPASS_COMMAND, after the Arbiter-side
    /// handler class that builds it; the wire name is this one.
    /// </summary>
    public const ushort AS_ADMIN_COMMAND = 0x2829;

    /// <summary>T32's name for <see cref="AS_ADMIN_COMMAND"/>. Kept so older call sites compile.</summary>
    public const ushort AS_BYPASS_COMMAND = AS_ADMIN_COMMAND;

    /// <summary>AS_ADMIN - the broadcast form (no user), one wide string. Not used here.</summary>
    public const ushort AS_ADMIN = 0x1473;

    /// <summary>C_ADMIN carries CommandType 1, C_OP_COMMAND carries 0 (the handlers differ only
    /// in this argument to ProcessCommand).</summary>
    public const int CommandTypeAdmin = 1, CommandTypeOp = 0;

    /// <summary>
    /// What goes in the forward's third field. The Arbiter's dumper calls that field
    /// <c>CommandType</c> (FUN_14017bdf0, Arb_part_011.c:5334), and the writer fills it from the
    /// HANDLER's own constant, not from the client packet: <c>lVar18 = param_1[0x24]</c>, where
    /// param_1 is the ArbiterBypassCommandHandler (Arb_part_067.c:6898). The handler is
    /// constructed twice, with 1 and 0 (Arb_part_033.c:13685/13691), and World commands live in
    /// the bucket that carries 1.
    ///
    /// <para>So this is deliberately NOT the client's C_ADMIN/C_OP_COMMAND type - see
    /// <see cref="CommandTypeAdmin"/> - and <c>ForwardToWorld</c> ignoring its own
    /// <c>commandType</c> argument is correct, not an oversight. Verified against the dumper and
    /// against World's Handler_AS_ADMIN_COMMAND in T46.</para>
    /// </summary>
    public const int BypassModeWorld = 1;

    /// <summary>The literal the real Arbiter sends for a command in neither table
    /// (ArbiterCommandDistributor::OnUnregisteredCommand, Arb_part_085.c:12923). It also follows
    /// with S_COMMAND_HELP (0x4F79) listing near matches; we do not.</summary>
    public const string InvalidCommandMessage = "Invalid QA Command\n";

    // The Arbiter's own create_user literals, verbatim (Arb_part_040.c:13406-13450).
    public const string CreateUserUsage = "create_user [username]";
    public static string AlreadyExistsMessage(string name) => $"User[{name}] already exists!";
    public static string CreatedMessage(string name) => $"Create a new user[{name}]!";
    public static string CannotCreateMessage(string name) => $"Cannot create a new user[{name}]!";

    /// <summary>query_point's only implemented branch in the real Arbiter (Arb_part_044.c:262).</summary>
    public const string QueryPointFailureMessage = "Can't request coin";

    /// <summary>warehousegold_max: a process-global cap (DAT_140f04088), 0 = no cap.</summary>
    public static long WarehouseGoldMax { get; internal set; }

    // ---- Entry points ----

    /// <summary>C_ADMIN (0xA45C): [string command].</summary>
    public bool OnAdminCommand(GameSession s, ReadOnlyMemory<byte> body)
        => Handle(s, body, "C_ADMIN", CommandTypeAdmin);

    /// <summary>C_OP_COMMAND (0xE394): [string command].</summary>
    public bool OnOpCommand(GameSession s, ReadOnlyMemory<byte> body)
        => Handle(s, body, "C_OP_COMMAND", CommandTypeOp);

    private bool Handle(GameSession s, ReadOnlyMemory<byte> body, string packet, int commandType)
    {
        var f = s.ReadByDef(packet, body);
        string text = f != null && f.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
        var line = GmCommandParser.Parse(text);
        if (line == null) return true;

        var chr = s.SelectedCharacter;
        var store = Program.Store;
        int level = LevelOf(s, store);
        var verdict = Classify(chr != null, level, line);

        _log.LogInformation("{Packet} from account '{Acct}' level {Level}: {Line} -> {Verdict}",
            packet, s.Account.Name, level, line.Rebuilt(), verdict);

        switch (verdict)
        {
            case GmDispatch.NoUser:
            case GmDispatch.NotAuthorised:
                // The real Arbiter answers NOTHING here: LogWrapperEx::LogAbuseCommandUser writes
                // a server-side abuse log and the handler returns (Arb_part_085.c:14812).
                return true;

            case GmDispatch.ForwardToWorld:
                ForwardToWorld(s, line, commandType);
                return true;

            case GmDispatch.Local:
                Execute(s, store!, chr!, line);
                return true;

            case GmDispatch.NotImplemented:
                SendCustom(s, $"{line.Name}: an Arbiter command TeraSharp does not implement yet\n");
                return true;

            default:
                SendCustom(s, InvalidCommandMessage);
                return true;
        }
    }

    /// <summary>
    /// The whole decision, without a session: who may run what, and which side owns it.
    /// </summary>
    public static GmDispatch Classify(bool hasUser, int adminLevel, GmCommandLine? line)
    {
        if (line == null || line.Name.Length == 0) return GmDispatch.Empty;
        if (!hasUser) return GmDispatch.NoUser;
        if (adminLevel < GmAccounts.MinimumAdminLevel) return GmDispatch.NotAuthorised;
        if (Implemented.Contains(line.Name)) return GmDispatch.Local;
        if (GmCommandCatalog.IsWorldCommand(line.Name)) return GmDispatch.ForwardToWorld;
        if (GmCommandCatalog.IsArbiterCommand(line.Name)) return GmDispatch.NotImplemented;
        return GmDispatch.Unknown;
    }

    /// <summary>The Arbiter-side commands TeraSharp actually runs.</summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(
        new[] { "set_admin_level", "create_user", "testitem", "clear_inven", "warehousegold_max", "query_point" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The account's effective admin level: allow-list first, then the stored row.</summary>
    public static int LevelOf(GameSession s, CharacterStore? store)
    {
        int stored = 0;
        if (store != null)
        {
            var acct = store.GetAccount(s.Account.Name);
            stored = acct?.AdminLevel ?? 0;
        }
        return GmAccounts.LevelFor(s.Account.Name, stored);
    }

    // ---- The forward ----

    private void ForwardToWorld(GameSession s, GmCommandLine line, int commandType)
    {
        var chr = s.SelectedCharacter;
        int playerId = chr == null ? (int)s.PlayerId : (int)chr.Id;
        var payload = BuildWorldForward(playerId, BypassModeWorld, line.Rebuilt());
        Program.World?.SendFrame(AS_BYPASS_COMMAND, payload);
    }

    /// <summary>
    /// The payload of the Arbiter-&gt;World forward, byte for byte as
    /// ArbiterBypassCommandHandler::HandleCommand writes it (Arb_part_067.c:6890-6903):
    ///
    /// <code>
    /// FUN_140350eb0(pkt, 0x2829);   // [u32 frameLen][u16 opcode]
    /// *slot = 0; FUN_14013d0b0(pkt, *slot);   // u32 string offset, backpatched
    /// FUN_14013d0b0(pkt, userId);             // u32
    /// FUN_14013d0b0(pkt, bypassMode);         // u32
    /// *slot = *len;                            // = 18, frame-relative
    /// FUN_140351030(pkt, line);                // UTF-16LE + a u16 0 terminator
    /// </code>
    ///
    /// So, payload (frame minus its 6-byte header): [0] u32 offset = 18, [4] u32 userId,
    /// [8] u32 mode, [12] the command line as UTF-16LE with a terminator.
    /// The offset is frame-relative, like every other offset on the Arbiter-World protocol.
    ///
    /// <para><b>T46 verified this three ways</b> and it is byte-exact:</para>
    /// <list type="number">
    /// <item>The Arbiter's own PDL dumper for AS_ADMIN_COMMAND (FUN_14017bdf0,
    /// Arb_part_011.c:5334) names the fields and their FRAME offsets: <c>Command</c> ref at 6,
    /// <c>UserDbId</c> at 10, <c>CommandType</c> at 0x0E, string at 0x12 - i.e. payload 0, 4, 8,
    /// 12. Its guard is <c>0x11 &lt; len</c>, so the minimum frame is 18.</item>
    /// <item>The second writer (Arb_part_040.c:8820, the <c>clear_recipe_world</c> path) emits
    /// exactly this order with <c>UserDbId</c> from <c>User+0x120</c> and CommandType 1.</item>
    /// <item>World's <c>Handler_AS_ADMIN_COMMAND</c> (WorldServer.exe.c:2977905) guards on
    /// <c>len &lt; 0x12</c>, looks the user up by the u32 at frame 10, bounds-checks the string
    /// ref at frame 6 against the frame length, reads CommandType at frame 0x0E and dispatches.
    /// It performs <b>no admin-level check of its own</b>.</item>
    /// </list>
    /// </summary>
    public static byte[] BuildWorldForward(int userId, int mode, string line)
    {
        line ??= "";
        var text = Encoding.Unicode.GetBytes(line);
        var payload = new byte[12 + text.Length + 2];
        BitConverter.GetBytes(6 + 12).CopyTo(payload, 0);      // 18: where the string starts
        BitConverter.GetBytes(userId).CopyTo(payload, 4);
        BitConverter.GetBytes(mode).CopyTo(payload, 8);
        text.CopyTo(payload, 12);                               // terminator stays zero
        return payload;
    }

    // ---- The six Arbiter-side commands ----

    private void Execute(GameSession s, CharacterStore store, FakeCharacter chr, GmCommandLine line)
    {
        switch (line.Name.ToLowerInvariant())
        {
            case "set_admin_level": SetAdminLevel(s, store, line); break;
            case "create_user":     CreateUser(s, store, line); break;
            case "testitem":        TestItem(s, line); break;
            case "clear_inven":     ClearInven(s, chr); break;
            case "warehousegold_max": WarehouseGoldMaxCommand(s, line); break;
            case "query_point":     QueryPoint(s); break;
        }
    }

    /// <summary>
    /// <c>set_admin_level [character] [level]</c> - ArbiterQACommandHandler::SetAdminLevel
    /// (Arb_part_044.c:2657): two arguments, look the CHARACTER up by name, then
    /// User::UpdateAdminLevel -> dbo.spUpdateUserAdminLevel. Fewer than two arguments, or an
    /// unknown name, does nothing at all and says nothing.
    /// <para>We store the level on the ACCOUNT row (the character's owner), because that is
    /// where a login can read it before a character is selected.</para>
    /// </summary>
    private void SetAdminLevel(GameSession s, CharacterStore store, GmCommandLine line)
    {
        if (line.Args.Count < 2) return;                       // silent, as the Arbiter
        if (!int.TryParse(line.Arg(1), out int level)) return; // _wtol would give 0; we refuse
        var target = store.GetCharacterByName(line.Arg(0));
        if (target == null) return;                            // silent, as the Arbiter

        store.SetAdminLevel(target.AccountId, level);
        _log.LogWarning("GM set_admin_level: '{Target}' (account {Acct}) -> level {Level} by '{By}'",
            target.Name, target.AccountId, level, s.Account.Name);
        SendCustom(s, $"Admin level of [{target.Name}] is now {level}\n");
    }

    /// <summary>
    /// <c>create_user [username]</c> - Arb_part_040.c:13406. The three literals below are the
    /// Arbiter's own, verbatim.
    /// </summary>
    private void CreateUser(GameSession s, CharacterStore store, GmCommandLine line)
    {
        string name = line.Arg(0);
        if (name.Length == 0) { SendCustom(s, CreateUserUsage); return; }
        if (store.GetCharacterByName(name) != null)
        {
            SendCustom(s, AlreadyExistsMessage(name));
            return;
        }

        try
        {
            byte[] template = StarterBlob.LoadTemplate();
            var req = new CreateUserRequest
            {
                Name = name, Race = 0, Gender = 0, Class = 1,
                Appearance = new byte[8], Details = new byte[32], Shape = new byte[64],
            };
            long accountId = (long)s.Account.AccountId;
            var record = CharacterHandlers.BuildRecord(
                req, accountId, store.NextPosition(accountId), template, playerId: 0);
            int id = store.CreateCharacter(record);
            var (zone, x, y, z) = CharacterHandlers.StartPositionFor(req.Race, req.Class);
            store.SaveWorldBlob(id, StarterBlob.Build(
                template, id, name, CharacterHandlers.IdentityOf(req), zone, x, y, z));
            s.Account.Characters.Add(FakeCharacter.FromRecord(record));
            _log.LogInformation("GM create_user: '{Name}' id {Id} on account {Acct}", name, id, accountId);
            SendCustom(s, CreatedMessage(name));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GM create_user failed for '{Name}'", name);
            SendCustom(s, CannotCreateMessage(name));
        }
    }

    /// <summary>
    /// <c>testitem</c> - ArbiterQACommandHandler::TestItem (FUN_14053d020, Arb_part_044.c:6765)
    /// is an EMPTY STUB in this build: the whole body is the scope tracer. It reads no argument,
    /// changes nothing and answers nothing. We keep the behaviour and say so, rather than
    /// inventing an item grant the real server never had (items are World's during play).
    /// </summary>
    private void TestItem(GameSession s, GmCommandLine line)
    {
        _log.LogInformation("GM testitem (stub in the real Arbiter too): {Args}", line.Rebuilt());
        SendCustom(s, "testitem is an empty stub in ArbiterServer.exe - nothing to do\n");
    }

    /// <summary>
    /// <c>clear_inven</c> - Arb_part_040.c:8714. It ignores its arguments entirely and queues an
    /// async job on the CALLING user (the neighbouring clear_parcel does take a username, so the
    /// omission looks deliberate). TeraSharp does not persist inventory - it lives in the world
    /// blob World owns - so there is nothing here to delete; this says so instead of pretending.
    /// </summary>
    private void ClearInven(GameSession s, FakeCharacter chr)
    {
        _log.LogInformation("GM clear_inven for '{Name}': no Arbiter-side inventory to clear", chr.Name);
        SendCustom(s, "clear_inven: the Arbiter does not store inventory (World owns it)\n");
    }

    /// <summary>
    /// <c>warehousegold_max [amount]</c> - Arb_part_045.c:4614. Exactly one argument, parsed as
    /// int64, negatives clamped to 0, written to a process-global. No DB write, no World notify,
    /// and the real one answers nothing.
    /// </summary>
    private void WarehouseGoldMaxCommand(GameSession s, GmCommandLine line)
    {
        if (!TryParseWarehouseGoldMax(line, out long amount)) return;   // silent, as the Arbiter
        WarehouseGoldMax = amount;
        _log.LogInformation("GM warehousegold_max = {Amount}", WarehouseGoldMax);
        SendCustom(s, $"warehousegold_max = {WarehouseGoldMax}\n");
    }

    /// <summary>
    /// warehousegold_max's argument: exactly one, int64, negatives clamped to 0. False means the
    /// real Arbiter would have done nothing at all (its arg-count test is an exact
    /// <c>== 0x20</c> on the vector's byte size, i.e. exactly one element).
    /// </summary>
    public static bool TryParseWarehouseGoldMax(GmCommandLine line, out long amount)
    {
        amount = 0;
        if (line is null || line.Args.Count != 1) return false;
        long v = long.TryParse(line.Arg(0), out long parsed) ? parsed : 0;
        amount = v > 0 ? v : 0;
        return true;
    }

    /// <summary>
    /// <c>query_point</c> - Arb_part_044.c:262 -> Account::RequestUpdateCoin. Only the FAILURE
    /// branch is implemented in the real Arbiter: if the request cannot be made it sends
    /// <c>Can't request coin</c> and there is no success branch at all. TeraSharp has no billing
    /// service, so that failure is exactly our case - byte for byte the same reply.
    /// </summary>
    private void QueryPoint(GameSession s)
    {
        SendCustom(s, QueryPointFailureMessage);
    }

    // ---- The reply channel ----

    /// <summary>S_SYSTEM_MESSAGE_CUSTOM (0x994C): one wide string, a literal.</summary>
    public static void SendCustom(GameSession s, string message)
        => s.SendByDef("S_SYSTEM_MESSAGE_CUSTOM", new Dictionary<string, object>
        {
            ["formatted"] = message ?? "",
        });
}
