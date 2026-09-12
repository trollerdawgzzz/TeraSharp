using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Character-screen handlers.
///   C_CAN_CREATE_USER   -> S_CAN_CREATE_USER  { ok }
///   C_CHECK_USERNAME     -> S_CHECK_USERNAME   { result }   (1 = available)
///   C_CREATE_USER        -> S_CREATE_USER      { success }
///   C_DELETE_USER        -> S_DELETE_USER       { success }
///
/// Character creation (decompile: Handler_C_CREATE_USER, Arb_part_079.c:9510):
///   Client sends gender/race/class/appearance(8B)/details(32B)/shape(64B)/name.
///   Arbiter validates name (2-16 chars, globally unique), computes templateId
///   (10101 + race*200 + gender*100 + class), creates the DB row with no world
///   blob. On first enter-world DBS_USER_ENTERWORLD goes out with found=0;
///   WorldServer initialises a fresh character from the template.
/// </summary>
public sealed class CharacterHandlers
{
    private readonly ILogger _log;
    public CharacterHandlers(ILogger log) => _log = log;

    // ---- Template ID (decompile: 10101 + race*200 + gender*100 + class) ----

    /// <summary>
    /// TemplateId encodes race/gender/class into one int used by the client
    /// and WorldServer to look up the base data sheet.
    /// Formula from the decompile (FakeCharacter default 11013 = race4/gender1/class12).
    /// </summary>
    internal static int ComputeTemplateId(int race, int gender, int cls) =>
        10101 + race * 200 + gender * 100 + cls;

    // ---- Helpers ----

    private static void SendCreateResult(GameSession s, bool success)
    {
        if (s.Definitions.Has("S_CREATE_USER"))
            s.SendByDef("S_CREATE_USER", new Dictionary<string, object> { ["success"] = success });
        else
            s.SendRawBody("S_CREATE_USER", new[] { (byte)(success ? 1 : 0) });
    }

    private static void SendDeleteResult(GameSession s, bool success)
    {
        if (s.Definitions.Has("S_DELETE_USER"))
            s.SendByDef("S_DELETE_USER", new Dictionary<string, object> { ["success"] = success });
        else
            s.SendRawBody("S_DELETE_USER", new[] { (byte)(success ? 1 : 0) });
    }

    /// <summary>Validate a character name: 2-16 chars, not blank.</summary>
    internal static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length >= 2 && name.Length <= 16;

    // ---- Handlers ----

    public bool OnCanCreateUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        bool ok = s.Account.Characters.Count < 8;
        _log.LogInformation("C_CAN_CREATE_USER from {Id} -> ok={Ok}", s.Id, ok);
        s.SendByDef("S_CAN_CREATE_USER", new Dictionary<string, object> { ["ok"] = ok });
        return true;
    }

    public bool OnCheckUsername(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_CHECK_USERNAME", body);
        string name = f != null && f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";

        bool available = IsValidName(name) &&
            !(Program.Store?.NameExists(name) == true) &&
            !s.Account.Characters.Exists(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        _log.LogInformation("C_CHECK_USERNAME from {Id}: '{Name}' -> {Result}",
            s.Id, name, available ? "available" : "rejected");

        s.SendByDef("S_CHECK_USERNAME", new Dictionary<string, object>
        {
            ["result"] = (byte)(available ? 1 : 0)
        });
        return true;
    }

    /// <summary>
    /// Character creation. Reads fields via the def codec, validates, creates
    /// a DB row (no world blob — WorldServer initialises on first enter), and
    /// adds the character to the in-memory account list.
    /// </summary>
    public bool OnCreateUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_CREATE_USER", body);
        if (f == null)
        {
            _log.LogWarning("C_CREATE_USER from {Id}: no def available, cannot parse", s.Id);
            SendCreateResult(s, false);
            return true;
        }

        string name = f.TryGetValue("name", out var nv) ? nv?.ToString() ?? "" : "";
        int gender = f.TryGetValue("gender", out var gv) ? Convert.ToInt32(gv) : 0;
        int race = f.TryGetValue("race", out var rv) ? Convert.ToInt32(rv) : 0;
        int cls = f.TryGetValue("class", out var cv) ? Convert.ToInt32(cv) : 0;
        byte[] appearance = f.TryGetValue("appearance", out var apv) ? apv as byte[] ?? new byte[8] : new byte[8];
        byte[] details = f.TryGetValue("details", out var dev) ? dev as byte[] ?? new byte[32] : new byte[32];
        byte[] shape = f.TryGetValue("shape", out var shv) ? shv as byte[] ?? new byte[64] : new byte[64];

        // Validate name
        if (!IsValidName(name))
        {
            _log.LogInformation("C_CREATE_USER from {Id}: invalid name '{Name}'", s.Id, name);
            SendCreateResult(s, false);
            return true;
        }

        // Character limit
        if (s.Account.Characters.Count >= 8)
        {
            _log.LogInformation("C_CREATE_USER from {Id}: character limit reached", s.Id);
            SendCreateResult(s, false);
            return true;
        }

        // Global name uniqueness (store + in-memory)
        var store = Program.Store;
        if (store?.NameExists(name) == true ||
            s.Account.Characters.Exists(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _log.LogInformation("C_CREATE_USER from {Id}: name '{Name}' taken", s.Id, name);
            SendCreateResult(s, false);
            return true;
        }

        int templateId = ComputeTemplateId(race, gender, cls);

        var record = new CharacterRecord
        {
            AccountId = (long)s.Account.AccountId,
            Name = name,
            Gender = gender,
            Race = race,
            Class = cls,
            Level = 1,
            TemplateId = templateId,
            Appearance = appearance,
            Details = details,
            Shape = shape,
            // Default starting position (Velika). WorldServer re-derives from
            // the class template on first enter-world (found=0).
            Zone = 7005,
            X = -449f, Y = 6239f, Z = 1956f,
        };

        if (store != null)
        {
            store.CreateCharacter(record);
            s.Account.Characters.Add(FakeCharacter.FromRecord(record));
        }

        _log.LogInformation("C_CREATE_USER from {Id}: created '{Name}' id={CId} template={T}",
            s.Id, name, record.Id, templateId);
        SendCreateResult(s, true);
        return true;
    }

    /// <summary>
    /// Character deletion. Reads the character ID, verifies ownership,
    /// removes from DB and in-memory list.
    /// Decompile: Handler_C_DELETE_USER, Arb_part_079.c:9831.
    /// </summary>
    public bool OnDeleteUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        int charId = 0;
        var f = s.ReadByDef("C_DELETE_USER", body);
        if (f != null && f.TryGetValue("id", out var idv))
            charId = Convert.ToInt32(idv);
        else if (body.Length >= 4)
            charId = BitConverter.ToInt32(body.Span);

        // Verify the character belongs to this account
        var chr = s.Account.Characters.Find(c => c.Id == (uint)charId);
        if (chr == null)
        {
            _log.LogWarning("C_DELETE_USER from {Id}: character {CId} not in account", s.Id, charId);
            SendDeleteResult(s, false);
            return true;
        }

        var store = Program.Store;
        store?.DeleteCharacter(charId);
        s.Account.Characters.Remove(chr);

        _log.LogInformation("C_DELETE_USER from {Id}: deleted '{Name}' id={CId}", s.Id, chr.Name, charId);
        SendDeleteResult(s, true);
        return true;
    }
}
