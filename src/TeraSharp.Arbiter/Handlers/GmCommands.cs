// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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

    // ------------------------------------------------------------------ T104: the Alt+A gate

    /// <summary>
    /// The value the client reads to decide whether Alt+A opens the In-Game Operation Tool:
    /// <c>S_LOGIN_ARBITER.status</c>, body +2 (packet offset 6), a u32.
    ///
    /// <para><b>It is the only thing that differs.</b> Diffing the lobby of
    /// <c>cap_final_gm_client2.log</c> - the one captured session where the panel opened -
    /// against <c>cap_final_client2.log</c>, <c>cap_final_client.log</c> and
    /// <c>cap_final_gm_client.log</c> frame by frame over frames 3..49: every packet is the same
    /// name, the same length and the same bytes except this one u32, which reads <c>0x21</c> (33)
    /// in the session that opened it and <c>0x1F</c> (31) in all three that did not.
    /// S_LOGIN_ACCOUNT_INFO is 544 bytes in all four and differs only in the account id and the
    /// random session strings; S_GET_USER_LIST carries no per-character admin flag (the only byte
    /// that separates the lists is inside restBonusXp); the ten S_UPDATE_CONTENTS_ON_OFF toggles
    /// are contents 2, 3, 4, 8, 9, 22, 23, 20, 21, 34 with the same on/off bytes in the same
    /// order in all four.</para>
    /// </summary>
    public const uint LoginStatusOperator = 33;

    /// <summary>The ordinary value - 31 in every non-operator capture we have.</summary>
    public const uint LoginStatusNormal = 31;

    /// <summary>
    /// <see cref="LoginStatusOperator"/> when this login should get the tool, otherwise
    /// <see cref="LoginStatusNormal"/>.
    ///
    /// <para>T89b drove this from <see cref="IsListed(string?)"/> alone, i.e. from
    /// <c>TERASHARP_GM_ACCOUNTS</c>. That is a trap in two ways. The env value has to hold the
    /// <b>numeric tera-api accountDBID</b>, because that is what the launcher puts in
    /// <c>C_LOGIN_ARBITER.name</c> (see <c>AuthRequest.AccountName</c>) - a display name in there
    /// never matches. And a privilege set in tera-api cannot reach here at all:
    /// <c>GameAuthenticationLogin</c> answers <c>{Return, ReturnCode, Msg}</c> and
    /// <c>AuthResult</c> has no privilege field, so TeraSharp never sees it.</para>
    ///
    /// <para><paramref name="storedAdminLevel"/> closes that: it is
    /// <c>CharacterStore.GetAdminLevel(accountId)</c>, the <c>accounts.admin_level</c> column
    /// that <c>set_admin_level</c> and the admin web tool's <c>POST /api/gm-level</c> (T101b)
    /// both write. Either route now opens the panel.</para>
    /// </summary>
    public static uint LoginStatusFor(string? accountName, int storedAdminLevel)
        => LevelFor(accountName, storedAdminLevel) >= MinimumAdminLevel
            ? LoginStatusOperator
            : LoginStatusNormal;
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
    /// HANDLER's own constant - a field on the ArbiterBypassCommandHandler object
    /// (Arb_part_067.c:6898). The handler is
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
                Execute(s, store!, chr!, line, commandType);
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
        // The Arbiter's own table is checked BEFORE the forward, and the forward is the default.
        // Two reasons, both learned the hard way in T47:
        //   1. GM-COMMANDS-FULL.md lists all 608 names, the Arbiter's included, so asking
        //      IsWorldCommand first sent Arbiter-owned commands to World.
        //   2. GmCommandCatalog loads those markdown files from disk and leaves both sets EMPTY
        //      when it cannot find them - which is the normal case for a deployed binary with no
        //      status/ folder beside it. Every command then fell through to Unknown, which is
        //      exactly what the live test saw: "/@teleport warriortwo" -> Unknown, with teleport
        //      sitting in GM-COMMANDS-FULL.md line 384 all along.
        // Forwarding by default makes the catalogue an optimisation rather than a dependency.
        if (GmCommandCatalog.IsArbiterCommand(line.Name)) return GmDispatch.NotImplemented;
        return GmDispatch.ForwardToWorld;
    }

    /// <summary>
    /// <b>Divergence from the original, on purpose.</b> The real Arbiter knows all 608 names
    /// because both tables are compiled into it, so a name in neither is a typo and
    /// <c>ArbiterCommandDistributor::OnUnregisteredCommand</c> (Arb_part_085.c:12895) answers
    /// "Invalid QA Command" plus S_COMMAND_HELP with near matches. We only know the names when
    /// the catalogue files are on disk, so a typo would be indistinguishable from a real command
    /// and would be refused locally instead of reaching World. We forward instead and let World
    /// reject: a typo costs one wasted frame, a real command that we failed to recognise costs a
    /// feature. <see cref="GmDispatch.Unknown"/> is therefore no longer produced by
    /// <see cref="Classify"/>; it is kept so the enum and its tests stay meaningful.
    /// </summary>
    public const string ForwardByDefaultNote =
        "T47: a command that is not in Implemented and not Arbiter-owned is forwarded to World.";

    /// <summary>The Arbiter-side commands TeraSharp actually runs.</summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(
        new[] { "set_admin_level", "create_user", "testitem", "clear_inven", "warehousegold_max", "query_point",
                "vis", "invis", "vaporize", "invisible" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// T128. The two World names this class now ALSO handles locally. They stay in
    /// <see cref="Implemented"/> so <see cref="Classify"/> routes them here, and
    /// <see cref="Execute"/> forwards them to World afterwards anyway: the
    /// client-side switch is ours, the world-side vaporize is World's, and a GM typing
    /// <c>/@vaporize</c> wants both. <c>vis</c> and <c>invis</c> are ours alone - World has no
    /// such command and would only log an unknown one - so they are not in this set.
    /// </summary>
    public static readonly IReadOnlySet<string> AlsoForwarded = new HashSet<string>(
        new[] { "vaporize", "invisible" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The account's effective admin level: the higher of the stored row and what the
    /// TERASHARP_GM_ACCOUNTS allow-list grants.
    ///
    /// <para><b>T47 widened this twice.</b> It is <c>max</c>, not "allow-list wins", so an account
    /// stored above <see cref="GmAccounts.GmAdminLevel"/> is not demoted by being listed. And the
    /// list is matched against the CHARACTER name as well as the account name: the variable is
    /// called GM_ACCOUNTS, but the name a person has to hand is the one they typed at the
    /// character screen, and putting it in the list is the obvious thing to do. Matching both is
    /// strictly more forgiving and cannot grant anything the list does not already name.</para>
    ///
    /// <para>Why it mattered: <c>WorldEntry</c> calls this to fill AS_ENTER_WORLD payload 111
    /// (T46), and a 0 there is what World prints as <c>AdminLevel[0]</c>. If it still logs 0
    /// after this, the value in the env matches neither name - that is the thing to check, and
    /// the level is otherwise resolved from the <c>accounts.admin_level</c> row.</para>
    /// </summary>
    public static int LevelOf(GameSession s, CharacterStore? store)
    {
        if (s == null) return 0;
        int stored = 0;
        if (store != null)
        {
            var acct = store.GetAccount(s.Account.Name);
            stored = acct?.AdminLevel ?? 0;
        }
        return LevelOf(s.Account.Name, s.SelectedCharacter?.Name, stored,
                       Environment.GetEnvironmentVariable(GmAccounts.EnvVariable));
    }

    /// <summary>The pure half of <see cref="LevelOf(GameSession, CharacterStore?)"/>, for tests.</summary>
    public static int LevelOf(string? accountName, string? characterName, int storedLevel, string? envValue)
    {
        bool listed = GmAccounts.IsListed(accountName, envValue)
                   || GmAccounts.IsListed(characterName, envValue);
        return listed ? Math.Max(storedLevel, GmAccounts.GmAdminLevel) : storedLevel;
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

    private void Execute(GameSession s, CharacterStore store, FakeCharacter chr, GmCommandLine line,
                         int commandType)
    {
        switch (line.Name.ToLowerInvariant())
        {
            case "set_admin_level": SetAdminLevel(s, store, line); break;
            case "create_user":     CreateUser(s, store, line); break;
            case "testitem":        TestItem(s, line); break;
            case "clear_inven":     ClearInven(s, chr); break;
            case "warehousegold_max": WarehouseGoldMaxCommand(s, line); break;
            case "query_point":     QueryPoint(s); break;

            // T128 - the visibility toggle. /@vis and /@invis set it; /@vaporize and
            // /@invisible flip it, which is what the client's own Alt+A does, and are
            // forwarded to World as well so nothing that worked before stops working.
            case "vis":       Visibility(s, invisible: false); break;
            case "invis":     Visibility(s, invisible: true); break;
            case "vaporize":
            case "invisible": Visibility(s, !ArbiterClientHandlers.IsGmInvisible((int)s.PlayerId)); break;
        }

        if (AlsoForwarded.Contains(line.Name)) ForwardToWorld(s, line, commandType);
    }


    /// <summary>
    /// T128. Flip the client-side GM visibility switch and say so.
    ///
    /// <para>This is the frame cap_final_gm_client2 546 carries - <c>09 00 BE 64 00 00 00 00 00</c>,
    /// S_ADMIN_GM_SKILL with skill 0 and enabled 0 - which is the reply the real Arbiter sent
    /// when the tool asked to turn invisibility off at 542. Sending it directly is what makes
    /// the toggle take without Alt+A.</para>
    ///
    /// <para><b>What this does not do.</b> S_ADMIN_GM_SKILL is the CLIENT-side switch: it is
    /// what lets the GM cast and stops the client drawing itself vaporized. Being hidden from
    /// other players is World's, and it has no client-facing packet of its own -
    /// <c>SDB_USER_VAPORIZED</c> (0x282D) is World-&gt;DbProxy persistence. That is why
    /// <c>/@vaporize</c> still goes to World as well.</para>
    /// </summary>
    private void Visibility(GameSession s, bool invisible)
    {
        ArbiterClientHandlers.SendGmInvisible(s, invisible);
        _log.LogInformation("GM visibility: account '{Acct}' player {Id} is now {State}",
            s.Account.Name, s.PlayerId, invisible ? "INVISIBLE" : "visible");
        SendCustom(s, invisible
            ? "You are now invisible. You cannot cast while invisible.\n"
            : "You are now visible and can cast.\n");
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

/// <summary>
/// T89. The In-Game Operation Tool's own packets - the C_ADMIN_* family the GM client sends when
/// the tool window is open, as opposed to the <c>/@</c> chat commands <see cref="GmCommands"/>
/// handles. cap_final_gm_client2.log is a full session of it.
///
/// <para>Every handler here is gated on the same admin level the chat commands use. The tool is
/// only ever opened by a client that already passed that gate, but the packets are ordinary
/// client packets and an ordinary client can send them, so the gate is theirs too - a
/// non-GM gets silence and a log line rather than another player's IP address.</para>
///
/// <para>T91 added the warehouse and skill tabs and the action row, T93 the inventory tab, so
/// every C_ADMIN_* the capture holds is now answered. The kinds of
/// <c>C_ADMIN_REQUEST_USERINFO</c> that the real Arbiter answered with nothing (2, 3, 14) and
/// the <c>C_ADMIN_REQUEST_USERACTION</c> ids past 12 and 13 are still refused - see
/// status/STATUS.md T89 / T91 / T93.</para>
/// </summary>
public static class GmAdminTool
{
    /// <summary>
    /// The live sessions, for the two packets that have to reach somebody other than the caller:
    /// the warning goes to the warned player and the distance list is everyone nearby. Left null,
    /// both fall back to the caller alone - a GM tool that can only see the GM is wrong but safe,
    /// and silence would hang the window.
    /// </summary>
    public static Func<IReadOnlyList<GameSession>>? OnlineSessions { get; set; }

    private static IReadOnlyList<GameSession> Online(GameSession s)
    {
        var all = OnlineSessions?.Invoke();
        return all is { Count: > 0 } ? all : new[] { s };
    }

    // ---- T161b: World's /@goto <name> - SA_CHAR_LOC (0x1443) ----
    // cap_crash 27181 / 27227 / 27822 (World -> us, 42 / 42 / 28 B): [u32 0x12 = the name's FRAME
    // offset][u64 requester gameId][wchar name]; World then waits. The real Handler_SA_CHAR_LOC finds
    // the target by name and answers AS_REQUEST_TELEPORT (0x13B1) with its cached position
    // (cap_social4 882 -> 883), or AS_CHAR_LOC (0x1444, two game ids) when the target is in an
    // instance on the same World; nothing when nobody has the name (cap_social4 8289, 8308). We
    // run the panel's "go to" instead (T155, cap_final 916-918): World fills in the target's LIVE
    // position and the 0x2826 answer goes on as 0x2827 - our cached position is only the last
    // S_LOAD_TOPO.

    /// <summary>SA_CHAR_LOC's requester (payload 4) and the name at the frame offset in payload 0.</summary>
    public static (ulong Requester, string Name) ReadCharLoc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return (0, "");
        ulong gameId = BitConverter.ToUInt64(payload[4..]);
        uint off = BitConverter.ToUInt32(payload);
        if (off < 6 || off - 6 >= (uint)payload.Length) return (gameId, "");
        var s = payload[(int)(off - 6)..];
        int n = 0;
        while (n + 1 < s.Length && (s[n] | s[n + 1]) != 0) n += 2;
        return (gameId, System.Text.Encoding.Unicode.GetString(s[..n]));
    }

    /// <summary>The ask for it: the T155 record, action 12, the GM who typed it as requester.</summary>
    public static byte[] BuildCharLocAsk(int gmId, string? gmName, int targetId, string? targetName)
        => ArbiterClientHandlers.BuildAskUserAction(gmId, gmName, targetId, targetName, ArbiterClientHandlers.UserActionGoTo);

    /// <summary>SA_CHAR_LOC: ask the target's World (0x2825); WorldBridge forwards the 0x2826 as 0x2827.</summary>
    public const ushort AS_CHAR_LOC = 0x1444;

    /// <summary>AS_CHAR_LOC: the two gameIds, requester first.</summary>
    public static byte[] BuildCharLoc(ulong gmGameId, ulong targetGameId)
    {
        var p = new byte[16];
        BitConverter.GetBytes(gmGameId & 0x7FFFFFFFFFFFFFFFUL).CopyTo(p, 0);
        BitConverter.GetBytes(targetGameId & 0x7FFFFFFFFFFFFFFFUL).CopyTo(p, 8);
        return p;
    }

    public static bool OnSaCharLoc(WorldBridge? bridge, byte[] payload, ILogger log)
    {
        var (gameId, name) = ReadCharLoc(payload);
        var gm = bridge?.PlayerForGameId(gameId);
        if (gm?.SelectedCharacter == null || name.Length == 0)
        {
            log.LogInformation("SA_CHAR_LOC: requester 0x{G:X} / '{Name}' unresolved - dropped", gameId, name);
            return false;
        }
        var t = bridge!.InWorldSessions().FirstOrDefault(o =>
            string.Equals(o.SelectedCharacter?.Name, name, StringComparison.OrdinalIgnoreCase));
        if (t?.SelectedCharacter == null)
        {
            log.LogInformation("SA_CHAR_LOC: '{Name}' is not online - nothing sent, as the real handler", name);
            return false;
        }
        // T172: Handler_SA_CHAR_LOC (Arb_part_062.c:4486) answers a target on the GM's own World with
        // AS_CHAR_LOC [u64 gm gameId][u64 target gameId] and World moves the GM itself (cap_final2b
        // 7277 -> 7278, 26202 -> 26203); only a target elsewhere takes the ask path below.
        if (t.CurrentWorldId == gm.CurrentWorldId)
        {
            bool here = ArbiterClientHandlers.SendToWorld(gm, AS_CHAR_LOC, BuildCharLoc(gm.GameId, t.GameId));
            log.LogInformation("SA_CHAR_LOC: /@goto '{Name}' for '{Gm}' {How}", name, gm.SelectedCharacter.Name,
                here ? "answered (0x1444, same World)" : "- no World");
            return here;
        }
        bool sent = ArbiterClientHandlers.SendToWorld(t, ArbiterClientHandlers.AS_ASK_ADMIN_REQUEST_USERACTION,
            BuildCharLocAsk((int)gm.PlayerId, gm.SelectedCharacter.Name, (int)t.PlayerId, t.SelectedCharacter.Name));
        log.LogInformation("SA_CHAR_LOC: /@goto '{Name}' for '{Gm}' {How}", name, gm.SelectedCharacter.Name,
            sent ? "asked of World (0x2825)" : "- no World");
        return sent;
    }

    /// <summary>The gate. Same level the chat commands need, resolved the same way.</summary>
    private static bool Allowed(GameSession s, ILogger log, string what)
    {
        if (GmCommandHandlers.LevelOf(s, Program.Store) >= GmAccounts.MinimumAdminLevel) return true;
        log.LogWarning("{What}: {Name} is not a GM - dropped", what, s.SelectedCharacter?.Name);
        return false;
    }

    /// <summary>C_ADMIN_REQUEST_CUSTOM_BOOKMARK (0x9504), frame 524: <c>[i32 page]</c>.</summary>
    public static bool OnRequestCustomBookmark(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_REQUEST_CUSTOM_BOOKMARK")) return true;
        long account = (long)(s.Account?.AccountId ?? 0);
        var rows = account > 0 ? Program.Store?.GetGmBookmarks(account) : null;
        s.Send(ArbiterClientHandlers.BuildAdminBookmarkList(
            ArbiterClientHandlers.S_ADMIN_CUSTOM_BOOKMARK_LIST, rows));
        return true;
    }

    /// <summary>C_ADMIN_REQUEST_DEFAULT_BOOKMARK (0x6E39), frame 525. The default list is server
    /// configuration rather than anything of the GM's, and it is empty in the capture (frame
    /// 528), so that is what goes out.</summary>
    public static bool OnRequestDefaultBookmark(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_REQUEST_DEFAULT_BOOKMARK")) return true;
        s.Send(ArbiterClientHandlers.BuildAdminBookmarkList(
            ArbiterClientHandlers.S_ADMIN_DEFAULT_BOOKMARK_LIST, null));
        return true;
    }

    /// <summary>
    /// C_ADMIN_ADD_CUSTOM_BOOKMARK (0x811A), frame 1167:
    /// <c>[u16 nameOffset=0x1A][i32 index][i32 zone][f32 x][f32 y][f32 z][wstr name]</c>.
    /// The tool redraws from the list that comes back, so the answer is the refreshed list.
    /// </summary>
    public const int AddBookmarkBodySize = 0x1A - 4;

    public static bool OnAddCustomBookmark(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_ADD_CUSTOM_BOOKMARK")) return true;
        var b = body.Span;
        long account = (long)(s.Account?.AccountId ?? 0);
        if (b.Length >= AddBookmarkBodySize && account > 0 && Program.Store is not null)
        {
            int index = BitConverter.ToInt32(b[2..]);
            int zone = BitConverter.ToInt32(b[6..]);
            float x = BitConverter.ToSingle(b[10..]);
            float y = BitConverter.ToSingle(b[14..]);
            float z = BitConverter.ToSingle(b[18..]);
            string name = ArbiterClientHandlers.ReadWString(b, 0);
            Program.Store.AddGmBookmark(account, index, zone, x, y, z, name);
            log.LogInformation("C_ADMIN_ADD_CUSTOM_BOOKMARK: '{Name}' at zone {Zone} ({X}, {Y}, {Z})",
                name, zone, (int)x, (int)y, (int)z);
        }
        return OnRequestCustomBookmark(s, body, log);
    }

    /// <summary>C_ADMIN_GMEVENT_STATUS (0xBD39), frame 526: no body at all.</summary>
    public static bool OnGmEventStatus(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GMEVENT_STATUS")) return true;
        s.Send(ArbiterClientHandlers.BuildAdminGmEventStatus());
        return true;
    }

    /// <summary>
    /// C_ADMIN_CHECK_USERNAME (0x581A), frame 712:
    /// <c>[u16 nameOffset=0x12][pdid 8][i32 0][wstr name]</c>. A name the GM typed becomes the
    /// db id every other tool packet keys on, so an unknown name answers with id 0 rather than
    /// with nothing - the window is waiting on the reply either way.
    /// </summary>
    public static bool OnCheckUsername(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_CHECK_USERNAME")) return true;
        string name = ArbiterClientHandlers.ReadWString(body.Span, 0);
        var chr = Program.Store?.GetCharacterByName(name);
        s.Send(ArbiterClientHandlers.BuildAdminCheckUsername(
            name, chr?.Id ?? 0, chr?.TemplateId ?? 0, chr?.Id ?? 0));
        return true;
    }

    /// <summary>C_ADMIN_GET_USER_INFO_BY_DBID (0xEFF2), frame 714:
    /// <c>[i32 userDbId][i32 0]</c>.</summary>
    public static bool OnGetUserInfoByDbId(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GET_USER_INFO_BY_DBID")) return true;
        var b = body.Span;
        int id = b.Length >= 4 ? BitConverter.ToInt32(b) : 0;
        var chr = Program.Store?.GetCharacter(id);
        bool online = false;
        foreach (var live in Online(s)) if (live.SelectedCharacter?.Id == id) { online = true; break; }
        s.Send(ArbiterClientHandlers.BuildAdminGetUserInfoByDbId(
            id, online, chr?.Level ?? 0, chr?.TemplateId ?? 0, chr?.Name ?? string.Empty, LocalIp));
        return true;
    }

    /// <summary>The address the captured replies carry for every row. We do not record the
    /// client's remote endpoint anywhere the tool could read it, and the capture is a loopback
    /// session, so this is what it showed.</summary>
    public const string LocalIp = "127.0.0.1";

    /// <summary>
    /// C_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE (0x5A0E), frame 1319: <c>[i32 distance]</c> - 125
    /// in the capture. The reply (1320) is one row per player in range, and the GM's own
    /// character is in it. Positions come from the character rows; the Arbiter does not track a
    /// live position, so a player who has moved since their last stored point reads stale.
    /// </summary>
    public static bool OnGetUserInfoListByDistance(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE")) return true;
        var rows = new List<(int, float, float, float, string, string)>();
        foreach (var live in Online(s))
        {
            var chr = live.SelectedCharacter;
            if (chr is null) continue;
            rows.Add(((int)chr.Id, chr.X, chr.Y, chr.Z, chr.Name, LocalIp));
        }
        s.Send(ArbiterClientHandlers.BuildAdminUserInfoListByDistance(rows));
        return true;
    }

    /// <summary>
    /// C_ADMIN_WARNING_MESSAGE (0xC544), frame 1399:
    /// <c>[u16 messageOffset=0x0A][i32 userDbId][wstr message]</c>. The reply goes to the WARNED
    /// player (cap_final_gm_client frame 1424), not back to the tool.
    /// </summary>
    public static bool OnWarningMessage(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_WARNING_MESSAGE")) return true;
        var b = body.Span;
        int target = b.Length >= 6 ? BitConverter.ToInt32(b[2..]) : 0;
        string message = ArbiterClientHandlers.ReadWString(b, 0);

        var frame = ArbiterClientHandlers.BuildAdminWarningMessage(message);
        bool sent = false;
        foreach (var live in Online(s))
        {
            if (live.SelectedCharacter?.Id != target) continue;
            live.Send(frame);
            sent = true;
            break;
        }
        log.LogInformation("C_ADMIN_WARNING_MESSAGE: '{Msg}' -> player {Id}{Note}",
            message, target, sent ? "" : " (not online - dropped)");
        return true;
    }

    /// <summary>
    /// C_ADMIN_REQUEST_USERINFO (0x9A56), frames 723 / 746 / 755 / 758 / 768 / 777 / 995 / 1006:
    /// <c>[u16 nameOffset=0x1A][i32 userDbId][pdid 8][i32 0][i32 kind][wstr name]</c>.
    /// <para>Kind 6 is the warehouse and kind 1 is the inventory. The inventory reply
    /// (S_ADMIN_GET_USERINFO_INVEN, frame 724) is eleven 400-byte item records behind a 39-byte
    /// head and is NOT built here - 400 bytes of item is a decode of its own, and a wrong one
    /// would draw a wrong inventory for a GM making a decision. The tool leaves that tab empty
    /// rather than wrong. Kinds 2, 3, 5 and 14 are answered by nothing in the capture either.</para>
    /// </summary>
    public const int UserInfoKindOffset = 18;      // body index; packet +22

    public static bool OnRequestUserInfo(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_REQUEST_USERINFO")) return true;
        var b = body.Span;
        int target = b.Length >= 6 ? BitConverter.ToInt32(b[2..]) : 0;
        int kind = b.Length >= UserInfoKindOffset + 4 ? BitConverter.ToInt32(b[UserInfoKindOffset..]) : 0;

        switch (kind)
        {
            case ArbiterClientHandlers.UserInfoKindWarehouse:
                s.Send(ArbiterClientHandlers.BuildAdminGetUserInfoWarehouse());
                return true;

            case ArbiterClientHandlers.UserInfoKindSkill:
                // T91: the skill tab's second list is the LEARNED CRESTS, which we do hold
                // (T83's `crests` table). The skill list itself is not modelled, so it goes out
                // empty - half a tab from our own rows beats a whole one invented.
                s.Send(ArbiterClientHandlers.BuildAdminGetUserInfoSkill(
                    null, Program.Store?.GetCrests(target)));
                return true;

            case ArbiterClientHandlers.UserInfoKindInven:
                // T93.
                s.Send(ArbiterClientHandlers.BuildAdminGetUserInfoInven(
                    InvenRowsFor(Program.Store, target),
                    Program.Store?.GetCharacter(target)?.Money ?? 0));
                return true;

            default:
                // Everything the capture does not answer is answered here the same way it was
                // there - with nothing.
                log.LogInformation("C_ADMIN_REQUEST_USERINFO: kind {Kind} for player {Id} - not served",
                    kind, target);
                return true;
        }
    }

    /// <summary>INVEN_TYPE 14, the worn pocket - the second of the two containers the writer
    /// appends to the inventory reply.</summary>
    public const int InvenTypeEquipped = 14;

    /// <summary>
    /// T93. The character's rows in the order frame 724 lists them. The writer appends two
    /// containers - the inventory (<c>param_2 + 0x3c80</c>) and then the equipment
    /// (<c>+ 0x3d18</c>) - and inside each one the capture is in ITEM DB ID order, not slot
    /// order: the bag runs slots 1, 2, 0, 3, 4, 5, 6 while its db ids run 10016, 10018, 10021,
    /// 10022, 10023, 10030, 10032, and the four worn pieces follow at 10011..10014.
    ///
    /// <para>Everything past the row is left at its default. The enchant, option and durability
    /// fields belong to World's item object and the three stat floats to the item template
    /// sheet; the Arbiter's <c>items</c> table holds neither, and a GM reading a made-up
    /// enchant level is worse served than one reading a blank.</para>
    /// </summary>
    public static List<ArbiterClientHandlers.AdminInvenItem> InvenRowsFor(
        CharacterStore? store, long ownerDbId)
    {
        var list = new List<ArbiterClientHandlers.AdminInvenItem>();
        var rows = store?.GetInventoryItems(ownerDbId);
        if (rows is null || rows.Count == 0) return list;

        var sorted = new List<CharacterStore.ItemRow>(rows);
        sorted.Sort((a, b) =>
        {
            int ga = a.InvenType == InvenTypeEquipped ? 1 : 0;
            int gb = b.InvenType == InvenTypeEquipped ? 1 : 0;
            return ga != gb ? ga - gb : a.ItemDbId.CompareTo(b.ItemDbId);
        });

        foreach (var r in sorted)
            list.Add(new ArbiterClientHandlers.AdminInvenItem
            {
                TemplateId = r.TemplateId,
                ItemDbId = r.ItemDbId,
                OwnerDbId = r.OwnerDbId,
                InvenType = r.InvenType,
                InvenPos = r.Slot,
                Count = r.Amount > int.MaxValue ? int.MaxValue : (int)r.Amount,
            });
        return list;
    }

    /// <summary>
    /// C_ADMIN_REQUEST_USERACTION (0xA3DB), frames 786 and 956:
    /// <c>[u16 nameOffset=0x16][i32 userDbId][pdid 8][i32 action][wstr name]</c>.
    ///
    /// <para>One opcode for every button on the tool's action row, and the capture presses two of
    /// them. <b>12 is a teleport</b>: frame 786 is followed by S_ABNORMALITY_END,
    /// S_CLEAR_ALL_HOLDED_ABNORMALITY, S_LOAD_TOPO, S_LOAD_HINT, S_INVEN_USERDATA and two
    /// S_ITEMLIST - a full zone reload, which is what a teleport looks like from the client.
    /// <b>13 is unnamed</b>: frame 956 is followed by an S_SOCIAL that also appears twice more
    /// with no request near it, so nothing distinguishes it. Both are World's work - the Arbiter
    /// does not move characters - so both are forwarded, and every other action id is refused
    /// with a log line rather than guessed at.</para>
    /// </summary>
    public const int UserActionOffset = 14;       // body index; packet +18
    /// <summary>Action 12, "go to": the zone reload at frames 786..796 is the GM arriving (T155).</summary>
    public const int UserActionTeleport = ArbiterClientHandlers.UserActionGoTo;
    /// <summary>Action 13: summon - the TARGET moves, so the GM's client shows nothing (T155).</summary>
    public const int UserActionUnnamed13 = ArbiterClientHandlers.UserActionSummon;

    /// <summary>
    /// T155: Handler_C_ADMIN_REQUEST_USERACTION finds the target by dbId (by name when it is 0)
    /// and sends 11 / 12 / 13 to the TARGET's World as AS_ASK_ADMIN_REQUEST_USERACTION; World's
    /// SA answer comes back through WorldBridge and goes on as 0x2827 (cap_final 916-918,
    /// 1081-1083). 10 is the Arbiter's own disconnect job and stays a log line here, like
    /// C_ADMIN_LOBBY. The real 13 first runs a 10-second AdminTargetUserCallJob countdown
    /// (@1337 to the target each second); ours asks at once.
    /// </summary>
    public static bool OnRequestUserAction(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_REQUEST_USERACTION")) return true;
        var b = body.Span;
        int target = b.Length >= 6 ? BitConverter.ToInt32(b[2..]) : 0;
        int action = b.Length >= UserActionOffset + 4 ? BitConverter.ToInt32(b[UserActionOffset..]) : 0;
        string name = ArbiterClientHandlers.ReadWString(b, 0);

        if (action is ArbiterClientHandlers.UserActionGoTo or ArbiterClientHandlers.UserActionSummon
            or ArbiterClientHandlers.UserActionResurrect)
        {
            var t = FindTarget(s, target, name);
            if (t?.SelectedCharacter == null)
            {
                log.LogInformation("C_ADMIN_REQUEST_USERACTION: '{Name}' (player {Id}) is not online - action {Action} dropped",
                    name, target, action);
                return true;
            }
            var ask = ArbiterClientHandlers.BuildAskUserAction((int)s.PlayerId, s.SelectedCharacter?.Name,
                (int)t.PlayerId, t.SelectedCharacter.Name, action);
            bool sent = ArbiterClientHandlers.SendToWorld(t, ArbiterClientHandlers.AS_ASK_ADMIN_REQUEST_USERACTION, ask);
            log.LogInformation("C_ADMIN_REQUEST_USERACTION: action {Action} on '{Name}' (player {Id}) {How}",
                action, name, t.PlayerId, sent ? "asked of World (0x2825)" : "- no World, standalone");
            return true;
        }

        if (action == ArbiterClientHandlers.UserActionKick)
        {
            log.LogInformation("C_ADMIN_REQUEST_USERACTION: kick '{Name}' (player {Id}) - the Arbiter's disconnect job, not carried out",
                name, target);
            return true;
        }

        log.LogWarning("C_ADMIN_REQUEST_USERACTION: action {Action} on '{Name}' is none of 10-13 - refused", action, name);
        return true;
    }

    /// <summary>The online target: by player id, or by name when the id is 0 (the binary's order).</summary>
    private static GameSession? FindTarget(GameSession s, int playerId, string name)
    {
        var hit = playerId > 0 ? Program.World?.SessionForPlayerId(playerId) : null;
        if (hit != null) return hit;
        foreach (var o in Online(s))
            if (playerId > 0 ? o.PlayerId == playerId
                : name.Length > 0 && string.Equals(o.SelectedCharacter?.Name, name, StringComparison.OrdinalIgnoreCase))
                return o;
        return null;
    }

    // =========================== T99: the rest of the tool ===========================
    //
    // Every one of these is gated the same way in the binary - `*(int *)(user + 0x3b98) < 1`
    // rejects with GM_NOT_ENOUGH_AUTHORITY - which is the same admin level Allowed() tests.

    public const int GmTeleportBodySize = 0x16 - 4;
    public const int GmMapTeleportBodySize = 0x12 - 4;   // T155: the handler's own guard, zone + x + y
    public const int AdminLobbyBodySize = 10 - 4;
    public const int GameIdBodySize = 8;
    public const int DungeonIdBodySize = 4;
    public const int BookmarkIndexBodySize = 4;
    public const int GmEventNoticeBodySize = 2;

    /// <summary>
    /// C_ADMIN_GM_TELEPORT (0xEC4B): <c>[u16 _][i32 zone][f32 x][f32 y][f32 z]</c> - 22 bytes,
    /// the handler's own guard. T155: handed to World as AS_ADMIN_REQUEST_USERACTION action 100
    /// with the packet's zone and position (cap_multiworld 2497 -&gt; 3060); World moves the GM
    /// and its S_LOAD_TOPO comes back through the tunnel. A zone World refuses (2243, 5101 -
    /// position 0) is answered by World too.
    /// </summary>
    public static bool OnGmTeleport(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GM_TELEPORT")) return true;
        var b = body.Span;
        if (b.Length < GmTeleportBodySize)
        {
            log.LogWarning("C_ADMIN_GM_TELEPORT: {Len} B body (want {Want})", b.Length, GmTeleportBodySize);
            return true;
        }
        bool sent = ArbiterClientHandlers.SendToWorld(s, ArbiterClientHandlers.AS_ADMIN_REQUEST_USERACTION,
            ArbiterClientHandlers.BuildGmTeleportRequest((int)s.PlayerId, s.SelectedCharacter?.Name, b));
        log.LogInformation("C_ADMIN_GM_TELEPORT: zone {Zone} ({X}, {Y}, {Z}) {How}",
            BitConverter.ToInt32(b[2..]), BitConverter.ToSingle(b[6..]),
            BitConverter.ToSingle(b[10..]), BitConverter.ToSingle(b[14..]),
            sent ? "handed to World (0x2827 action 100)" : "- no World, standalone");
        return true;
    }

    /// <summary>C_ADMIN_GM_MAPTELEPORT (0xDC53): <c>[u16 nameOffset][i32 zone][f32 x][f32 y]</c>
    /// then the name. T155: action 0x67 with z = 16777215, World finds the ground (no sample).</summary>
    public static bool OnGmMapTeleport(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GM_MAPTELEPORT")) return true;
        var b = body.Span;
        if (b.Length < GmMapTeleportBodySize) return true;
        bool sent = ArbiterClientHandlers.SendToWorld(s, ArbiterClientHandlers.AS_ADMIN_REQUEST_USERACTION,
            ArbiterClientHandlers.BuildMapTeleportRequest((int)s.PlayerId, s.SelectedCharacter?.Name, b));
        log.LogInformation("C_ADMIN_GM_MAPTELEPORT: '{Name}' zone {Id} {How}",
            ArbiterClientHandlers.ReadWString(b, 0), BitConverter.ToInt32(b[2..]),
            sent ? "handed to World (0x2827 action 0x67)" : "- no World, standalone");
        return true;
    }

    /// <summary>
    /// C_ADMIN_LOBBY (0xD80D): <c>[u16 reasonOffset][i32 userDbId]</c> then the reason - send a
    /// player back to character select. The handler looks the target up and calls the session
    /// manager; ending somebody else's session is the human-owned half of this build, so the
    /// order is logged and answered.
    /// </summary>
    public static bool OnAdminLobby(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_LOBBY")) return true;
        var b = body.Span;
        int target = b.Length >= AdminLobbyBodySize ? BitConverter.ToInt32(b[2..]) : 0;
        log.LogInformation("C_ADMIN_LOBBY: player {Id} to the lobby ('{Why}') - not carried out",
            target, ArbiterClientHandlers.ReadWString(b, 0));
        return true;
    }

    /// <summary>C_ADMIN_REMOVE_NPC (0x830D) and C_ADMIN_VANISH_PET (0x6F81): one
    /// <c>[i64 gameId]</c> each, both forwarded to World in the binary. T155: REMOVE_NPC is
    /// 0x2827 action 0x66 (no sample); VANISH_PET is its own opcode and still a log line.</summary>
    public static bool OnAdminRemoveNpc(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_REMOVE_NPC")) return true;
        var b = body.Span;
        long gameId = b.Length >= GameIdBodySize ? BitConverter.ToInt64(b) : 0;
        bool sent = gameId != 0 && ArbiterClientHandlers.SendToWorld(s, ArbiterClientHandlers.AS_ADMIN_REQUEST_USERACTION,
            ArbiterClientHandlers.BuildRemoveNpcRequest((int)s.PlayerId, s.SelectedCharacter?.Name, gameId));
        log.LogInformation("C_ADMIN_REMOVE_NPC: game id 0x{Id:X} {How}", gameId,
            sent ? "handed to World (0x2827 action 0x66)" : "- not sent");
        return true;
    }

    public static bool OnAdminVanishPet(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
        => LogGameIdOrder(s, body, log, "C_ADMIN_VANISH_PET");

    private static bool LogGameIdOrder(GameSession s, ReadOnlyMemory<byte> body, ILogger log, string what)
    {
        if (!Allowed(s, log, what)) return true;
        var b = body.Span;
        long gameId = b.Length >= GameIdBodySize ? BitConverter.ToInt64(b) : 0;
        log.LogInformation("{What}: game id 0x{Id:X} - World's to do", what, gameId);
        return true;
    }

    /// <summary>
    /// C_ADMIN_GET_DUNGEON_USER_LIST (0xE2C8): <c>[i32 DungeonId]</c>. The reply is one row per
    /// live instance; the Arbiter does not run dungeons and we track no instances, so the list
    /// is empty - which the tool draws as "nobody inside" rather than hanging.
    /// </summary>
    public static bool OnAdminGetDungeonUserList(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GET_DUNGEON_USER_LIST")) return true;
        s.Send(ArbiterClientHandlers.BuildAdminGetDungeonUserList(null));
        return true;
    }

    /// <summary>
    /// C_ADMIN_REMOVE_CUSTOM_BOOKMARK (0xBAA9): <c>[i32 index]</c>, min frame 8. The handler
    /// deletes that index and then re-sends the whole list, exactly as the add does (T89), so
    /// the tool redraws from the answer.
    /// </summary>
    public static bool OnRemoveCustomBookmark(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_REMOVE_CUSTOM_BOOKMARK")) return true;
        var b = body.Span;
        long account = (long)(s.Account?.AccountId ?? 0);
        if (b.Length >= BookmarkIndexBodySize && account > 0)
            Program.Store?.DeleteGmBookmark(account, BitConverter.ToInt32(b));
        return OnRequestCustomBookmark(s, body, log);
    }

    /// <summary>
    /// C_ADMIN_GMEVENT_NOTICE (0xD622): <c>[u16 noticeOffset]</c> then the line. It goes to the
    /// GM-event manager, which runs the OX quiz and the summon events this build has none of -
    /// there is no event to notice about - so the line is logged rather than broadcast to every
    /// player as a GM event that is not running.
    /// </summary>
    public static bool OnGmEventNotice(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GMEVENT_NOTICE")) return true;
        log.LogInformation("C_ADMIN_GMEVENT_NOTICE: '{Notice}' - no GM event is running",
            ArbiterClientHandlers.ReadWString(body.Span, 0));
        return true;
    }

    /// <summary>The message id skill 0 answers with (frames 547 and 2949).</summary>
    public const int GmSkillOffMessage = 1436;
    /// <summary>The message id skill 2 answers with (frame 1211). Skill 1 has no sample.</summary>
    public const int GmSkillHideFromMobsMessage = 1439;

    /// <summary>
    /// C_ADMIN_GM_SKILL (0x8949): <c>[u64 gameId][i32 skill]</c>, a 12-byte body. Alt+A, and
    /// the tool's GM-skill buttons.
    ///
    /// <para><b>T128 corrected this.</b> The trailing i32 is the skill INDEX, not an enable
    /// flag - <c>C_ADMIN_GM_SKILL.1.def</c> says <c>0 = Invisible, 1 = Invincible,
    /// 2 = Hide from Mobs</c> - and the packet is a TOGGLE, not a set. T89 read it as a flag
    /// and answered <c>skill=0, enabled=(value != 0)</c>, which got frame 546 right by
    /// accident (skill 0 and value 0 both being zero) and everything else wrong: pressing
    /// "Hide from Mobs" answered <c>enabled=1</c> for INVISIBILITY.</para>
    ///
    /// <para>cap_final_gm_client2 has three requests and they settle it:</para>
    /// <list type="bullet">
    /// <item>542 <c>skill=0</c> -&gt; 546 <c>S_ADMIN_GM_SKILL 00 00 00 00 00</c> + 547
    /// <c>S_SYSTEM_MESSAGE @1436</c>. Invisibility had been ON since the enter-world push at
    /// 99, so the toggle turned it off.</item>
    /// <item>2942 <c>skill=0</c> -&gt; 2948 / 2949, the same pair on the second character.</item>
    /// <item>1210 <c>skill=2</c> -&gt; 1211 <c>S_SYSTEM_MESSAGE @1439</c> and <b>nothing
    /// else</b>. The S_ADMIN_GM_SKILL echo belongs to skill 0 alone.</item>
    /// </list>
    ///
    /// <para>Skill 1 has no sample, so it gets neither frame - a log line and silence, rather
    /// than a message id we would be inventing. The state is tracked so <c>/@vis</c> and the
    /// tool agree about which way the next toggle goes.</para>
    /// </summary>
    public static bool OnGmSkill(GameSession s, ReadOnlyMemory<byte> body, ILogger log)
    {
        if (!Allowed(s, log, "C_ADMIN_GM_SKILL")) return true;
        var b = body.Span;
        int skill = b.Length >= 12 ? BitConverter.ToInt32(b[8..]) : ArbiterClientHandlers.GmSkillInvisible;
        int playerId = (int)s.PlayerId;   // the key the enter-world push uses

        // T148: with a World behind us the toggle is World's - it vaporized this GM at spawn and
        // only it can release them. Every reply (S_ADMIN_GM_SKILL, @1436/@1439, the respawn)
        // then comes back through the tunnel, as frames 775 / 546-549 do on the real server.
        // The local answers below are the standalone path only.
        if (ArbiterClientHandlers.RequestWorldGmSkill(s, skill))
        {
            log.LogInformation("C_ADMIN_GM_SKILL: skill {Skill} for player {Id} handed to World (0x2827)",
                skill, playerId);
            return true;
        }

        if (skill == ArbiterClientHandlers.GmSkillInvisible)
        {
            bool now = !ArbiterClientHandlers.IsGmInvisible(playerId);
            ArbiterClientHandlers.SendGmInvisible(s, now);
            Smt(s, GmSkillOffMessage);
            log.LogInformation("C_ADMIN_GM_SKILL: invisibility toggled {State} for player {Id}",
                now ? "ON" : "OFF", playerId);
            return true;
        }

        if (skill == ArbiterClientHandlers.GmSkillHideFromMobs) { Smt(s, GmSkillHideFromMobsMessage); return true; }

        log.LogInformation("C_ADMIN_GM_SKILL: skill {Skill} has no captured reply - answering nothing",
            skill);
        return true;
    }

    /// <summary>One S_SYSTEM_MESSAGE carrying a bare <c>@id</c>, the form frames 547 and 1211 use.</summary>
    private static void Smt(GameSession s, int id)
        => s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = "@" + id });
}

/// <summary>
/// T152b. A level set OUTSIDE World (the web tool's /api/set-level) is handed to World as the QA
/// command <c>perfect_level N</c>, so World runs its own level commit.
///
/// <para>Why: World learns a character's skill ranks itself, in
/// <c>DBLevelExpContext::ExecuteCommitSQL</c> -&gt; <c>AutoLearnSkills</c>, which learns every
/// type-11 row IsSkillLearnable allows. cap_social4 1401 <c>perfect_level 65</c> -&gt; 1402
/// SDB_UPDATE_EXP_LEVEL -&gt; 168 SDB_USER_LEARN_SKILL in 0.8 s; 7539 <c>perfect_level 20</c> -&gt;
/// 43. A real level-70 valkyrie's blob holds 164 active / 22 passive entries (cap_final 411).
/// `test` (10) was put at 70 by the store alone: no 0x273B in any of its taps, and its blob still
/// held the seven creation skills (cap_skills3 714) - every rank World would have granted was
/// missing, which is what the Learned Skills window shows as "[Click to obtain]".</para>
///
/// <para>Offline characters are queued and sent right behind their next S_SPAWN_ME. The queue
/// is in memory: a restart before that login drops it, and set-level is simply run again.</para>
/// </summary>
public static class WorldLevelSync
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Pending = new();

    /// <summary>The QA command World levels with - the one the real GM tool sends.</summary>
    public static string CommandLine(int level) => "perfect_level " + level.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>AS_ADMIN_COMMAND for it: cap_social4 1401 is BuildFrame(1, 65), byte for byte.</summary>
    public static byte[] BuildFrame(int playerId, int level)
        => GmCommandHandlers.BuildWorldForward(playerId, GmCommandHandlers.BypassModeWorld, CommandLine(level));

    /// <summary>Queue a level for World; sent now if the character is in the world.</summary>
    public static void Queue(int playerId, int level)
    {
        if (playerId <= 0 || level <= 0) return;
        Pending[playerId] = level;
        var s = Program.World?.SessionForPlayerId(playerId);
        if (s is { InWorld: true }) OnSpawn(s);
    }

    /// <summary>Take the queued level, if any. Once only.</summary>
    public static bool TryTake(int playerId, out int level) => Pending.TryRemove(playerId, out level);

    /// <summary>Is anything queued for this character? Tests and the log.</summary>
    public static bool IsPending(int playerId) => Pending.ContainsKey(playerId);

    /// <summary>Called behind the tunnelled S_SPAWN_ME: send the queued level, if any.</summary>
    public static void OnSpawn(GameSession s)
    {
        if (s == null) return;
        var w = Program.World;
        if (w == null || !TryTake((int)s.PlayerId, out int level)) return;
        w.SendFrame(s.CurrentWorldId, GmCommandHandlers.AS_ADMIN_COMMAND, BuildFrame((int)s.PlayerId, level));
        s.Log?.LogInformation("level sync: {Line} handed to World for player {Id} (auto-learns follow)",
            CommandLine(level), s.PlayerId);
    }
}

