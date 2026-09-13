using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>Outcome of the Arbiter-side name check (see <see cref="CharacterHandlers.ValidateName"/>).</summary>
public enum NameCheck
{
    Ok = 0,
    /// <summary>RESTRICTION_ERROR 3 in the decompile: null or zero-length.</summary>
    Empty = 3,
    /// <summary>RESTRICTION_ERROR 2 in the decompile: wcslen &gt;= 0x24.</summary>
    TooLong = 2,
    /// <summary>Below the client's 2-character minimum.</summary>
    TooShort = 100,
    /// <summary>Contains something other than a letter.</summary>
    IllegalCharacter = 101,
    /// <summary>Already used by another character.</summary>
    Taken = 102,
}

/// <summary>Parsed C_CREATE_USER (client protocol 376012, def version 2).</summary>
public sealed class CreateUserRequest
{
    public int Gender { get; init; }
    public int Race { get; init; }
    public int Class { get; init; }
    /// <summary>8-byte "customize" block.</summary>
    public byte[] Appearance { get; init; } = new byte[8];
    public bool IsSecondCharacter { get; init; }
    public uint Appearance2 { get; init; }
    public bool IsRandomName { get; init; }
    public string Name { get; init; } = "";
    public byte[] Details { get; init; } = new byte[32];
    public byte[] Shape { get; init; } = new byte[64];
}

/// <summary>
/// Character-screen handlers.
///   C_CAN_CREATE_USER  -> S_CAN_CREATE_USER  { bool ok }
///   C_CHECK_USERNAME   -> S_CHECK_USERNAME   { byte result }   (1 = available)
///   C_CREATE_USER      -> S_CREATE_USER      { bool success }  (opcode 0x7160)
///   C_DELETE_USER      -> S_DELETE_USER      { bool success }
///
/// <para>Ground truth for the whole flow: D:\packetlogs\cap_newchar_client.log packets 30-38
/// (C_CAN_CREATE_USER -> S_CAN_CREATE_USER 01, C_CHECK_USERNAME "Test" -> S_CHECK_USERNAME 01,
/// C_CREATE_USER 146 B -> S_CREATE_USER 01, then C_GET_USER_LIST returns two characters), and
/// D:\packetlogs\cap_newchar.log 05:49:03 for that character's first enter-world.</para>
///
/// <para>Decompile: <c>Handler_C_CREATE_USER</c> Arb_part_079.c:9510,
/// <c>Handler_C_CHECK_USERNAME</c> Arb_part_079.c:9369, <c>Handler_C_CAN_CREATE_USER</c>
/// Arb_part_079.c:9204, <c>Handler_C_DELETE_USER</c> Arb_part_079.c:9852.</para>
/// </summary>
public sealed class CharacterHandlers
{
    private readonly ILogger _log;
    public CharacterHandlers(ILogger log) => _log = log;

    /// <summary>Fallback character-slot limit when the account has no configured maximum.</summary>
    public const int MaxCharactersPerAccount = 8;

    // ---- Template ID ----

    /// <summary>
    /// TemplateId encodes race/gender/class for the client and WorldServer data sheets:
    /// 10101 + race*200 + gender*100 + class. Checked against the capture: the created
    /// character is race 4 / gender 1 / class 12 and the existing "dob" row uses 11013,
    /// which is exactly 10101 + 800 + 100 + 12.
    /// </summary>
    internal static int ComputeTemplateId(int race, int gender, int cls) =>
        10101 + race * 200 + gender * 100 + cls;

    // ---- Start position ----

    /// <summary>
    /// Where a brand-new character starts.
    ///
    /// <para>The real Arbiter reads this per class from its CharacterData tables
    /// (<c>Handler_C_CREATE_USER</c> calls FUN_1409ab7c0(charDataMgr, class, out) and uses the
    /// zone+x/y/z it returns, falling back to FUN_1409acbb0(race, gender, class)). We do not
    /// have those tables, so <b>every class currently gets the one start position we have
    /// ground truth for</b>: zone 5, (16260, 1253, -4410) — the values in the starter blob the
    /// real server sent for "Test" (cap_newchar.log packet 133). Replace this with a per-class
    /// table when one is extracted; the shape of the method is already per-class.</para>
    /// </summary>
    internal static (int Zone, float X, float Y, float Z) StartPositionFor(int race, int cls)
        => (5, 16260f, 1253f, -4410f);

    // ---- Name validation ----

    /// <summary>
    /// Validate a character name.
    ///
    /// <para>The real Arbiter enforces two hard limits itself
    /// (<c>InputRestrictionHelper::CheckUserName</c>, Arb_part_078.c:13783): non-empty
    /// (RESTRICTION_ERROR 3) and <c>wcslen &lt; 0x24</c> (RESTRICTION_ERROR 2). Everything else
    /// is delegated to <c>InputRestriction::RestrictionCheck</c> (Arb_part_036.c:10853), which
    /// is a data-driven rule table loaded from server config we do not have — so the
    /// character-class rule below (2-16 letters, no digits/spaces/punctuation) is the standard
    /// client-side TERA rule, not a decompiled constant. It is deliberately stricter than the
    /// Arbiter's 1..35 so nothing the client would reject reaches the DB.</para>
    /// </summary>
    internal static NameCheck ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return NameCheck.Empty;
        if (name.Length >= 0x24) return NameCheck.TooLong;
        if (name.Length > StarterBlob.NameMaxChars) return NameCheck.TooLong;
        if (name.Length < 2) return NameCheck.TooShort;
        foreach (var ch in name)
            if (!char.IsLetter(ch)) return NameCheck.IllegalCharacter;
        return NameCheck.Ok;
    }

    /// <summary>Convenience wrapper kept for existing call sites.</summary>
    internal static bool IsValidName(string? name) => ValidateName(name) == NameCheck.Ok;

    // ---- C_CREATE_USER parsing ----

    /// <summary>
    /// Parse a raw C_CREATE_USER packet (including the 4-byte <c>[u16 len][u16 opcode]</c>
    /// header) straight from the wire layout, without needing the .def registry.
    ///
    /// <para>Offsets are read directly out of <c>Handler_C_CREATE_USER</c> (Arb_part_079.c:9510),
    /// where <c>puVar15</c> is the packet start as a <c>ushort*</c>:</para>
    /// <code>
    ///   0  u16 len                                packet length
    ///   2  u16 opcode
    ///   4  u16 nameOffset      puVar15[2]         packet-relative
    ///   6  u16 detailsOffset   puVar15[3]         ("CustomizingInfo", 4 x u64 = 32 bytes)
    ///   8  u16 detailsCount
    ///  10  u16 shapeOffset     puVar15[5]
    ///  12  u16 shapeCount      puVar15[6]         capped at 0x40 by the handler
    ///  14  i32 gender          *(u32*)(puVar15+7)
    ///  18  i32 race            *(u32*)(puVar15+9)
    ///  22  i32 class           *(u32*)(puVar15+0xb)
    ///  26  u64 appearance      *(u64*)(puVar15+0xd)     8-byte "customize"
    ///  34  u8  isSecondCharacter
    ///  35  u32 appearance2     *(u32*)(puVar15+0x23)
    ///  39  u8  isRandomName    *(char*)(puVar15+0x27)
    /// </code>
    /// <para>The handler rejects anything shorter than 0x28 bytes. Verified against
    /// cap_newchar_client.log packet 35 (146 bytes: name@0x28, details@0x32 len 0x20,
    /// shape@0x52 len 0x40).</para>
    /// </summary>
    internal static CreateUserRequest? ParseCreateUser(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 0x28) return null;

        ushort nameOff = BitConverter.ToUInt16(packet[4..]);
        ushort detailsOff = BitConverter.ToUInt16(packet[6..]);
        ushort detailsLen = BitConverter.ToUInt16(packet[8..]);
        ushort shapeOff = BitConverter.ToUInt16(packet[10..]);
        ushort shapeLen = BitConverter.ToUInt16(packet[12..]);

        var appearance = packet.Slice(26, 8).ToArray();

        return new CreateUserRequest
        {
            Gender = BitConverter.ToInt32(packet[14..]),
            Race = BitConverter.ToInt32(packet[18..]),
            Class = BitConverter.ToInt32(packet[22..]),
            Appearance = appearance,
            IsSecondCharacter = packet[34] != 0,
            Appearance2 = BitConverter.ToUInt32(packet[35..]),
            IsRandomName = packet[39] != 0,
            Name = ReadWideString(packet, nameOff),
            // The handler caps both at their struct sizes (0x20 / 0x40).
            Details = ReadBlock(packet, detailsOff, Math.Min(detailsLen, (ushort)32), 32),
            Shape = ReadBlock(packet, shapeOff, Math.Min(shapeLen, (ushort)64), 64),
        };
    }

    private static string ReadWideString(ReadOnlySpan<byte> packet, int offset)
    {
        if (offset <= 0 || offset >= packet.Length) return "";
        var sb = new StringBuilder();
        for (int i = offset; i + 1 < packet.Length; i += 2)
        {
            ushort c = BitConverter.ToUInt16(packet[i..]);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    private static byte[] ReadBlock(ReadOnlySpan<byte> packet, int offset, int count, int size)
    {
        var buf = new byte[size];
        if (offset <= 0 || count <= 0 || offset + count > packet.Length) return buf;
        packet.Slice(offset, count).CopyTo(buf);
        return buf;
    }

    /// <summary>Build the request from the .def codec output (the normal in-session path).</summary>
    private static CreateUserRequest FromFields(IReadOnlyDictionary<string, object> f) => new()
    {
        Gender = f.TryGetValue("gender", out var gv) ? Convert.ToInt32(gv) : 0,
        Race = f.TryGetValue("race", out var rv) ? Convert.ToInt32(rv) : 0,
        Class = f.TryGetValue("class", out var cv) ? Convert.ToInt32(cv) : 0,
        Appearance = f.TryGetValue("appearance", out var av) ? av as byte[] ?? new byte[8] : new byte[8],
        IsSecondCharacter = f.TryGetValue("isSecondCharacter", out var sv) && Convert.ToBoolean(sv),
        Appearance2 = f.TryGetValue("appearance2", out var a2) ? Convert.ToUInt32(a2) : 0u,
        IsRandomName = f.TryGetValue("isRandomName", out var rn) && Convert.ToBoolean(rn),
        Name = f.TryGetValue("name", out var nv) ? nv?.ToString() ?? "" : "",
        Details = f.TryGetValue("details", out var dv) ? dv as byte[] ?? new byte[32] : new byte[32],
        Shape = f.TryGetValue("shape", out var shv) ? shv as byte[] ?? new byte[64] : new byte[64],
    };

    // ---- Record building ----

    /// <summary>
    /// Turn a parsed C_CREATE_USER into the DB row, starter blob included. Pure apart from the
    /// blob template that is handed in, so tests can drive it without a session or a database.
    /// </summary>
    internal static CharacterRecord BuildRecord(
        CreateUserRequest req, long accountId, int position, byte[] blobTemplate, int playerId)
    {
        var (zone, x, y, z) = StartPositionFor(req.Race, req.Class);
        return new CharacterRecord
        {
            Id = playerId,
            AccountId = accountId,
            Name = req.Name,
            Gender = req.Gender,
            Race = req.Race,
            Class = req.Class,
            Level = 1,
            TemplateId = ComputeTemplateId(req.Race, req.Gender, req.Class),
            Appearance = req.Appearance,
            Details = req.Details,
            Shape = req.Shape,
            Zone = zone, X = x, Y = y, Z = z,
            Position = position,
            // playerId is only known after the INSERT; when it is 0 here the caller patches
            // the blob again with the real row id (see OnCreateUser).
            WorldBlob = StarterBlob.Build(blobTemplate, playerId, req.Name, zone, x, y, z),
        };
    }

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

    /// <summary>
    /// Global name check: the SQLite unique index on <c>characters.name</c> is COLLATE NOCASE,
    /// and the in-memory account list is checked too so a name created earlier in this session
    /// is caught even before the row is re-read.
    /// </summary>
    private static bool NameTaken(GameSession s, string name) =>
        Program.Store?.NameExists(name) == true ||
        s.Account.Characters.Exists(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---- Handlers ----

    /// <summary>
    /// C_CAN_CREATE_USER -> S_CAN_CREATE_USER { ok }.
    /// The real handler (Arb_part_079.c:9204) answers <c>currentCharacterCount &lt; maxSlots</c>
    /// and nothing else. Capture: packet 30 -> packet 32 <c>05 00 72 70 01</c>.
    /// </summary>
    public bool OnCanCreateUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        bool ok = s.Account.Characters.Count < MaxCharactersPerAccount;
        _log.LogInformation("C_CAN_CREATE_USER from {Id} -> ok={Ok} ({N}/{Max})",
            s.Id, ok, s.Account.Characters.Count, MaxCharactersPerAccount);
        s.SendByDef("S_CAN_CREATE_USER", new Dictionary<string, object> { ["ok"] = ok });
        return true;
    }

    /// <summary>
    /// C_CHECK_USERNAME -> S_CHECK_USERNAME { result }, 1 = available.
    /// The real handler (Arb_part_079.c:9369) chains three checks and sends false on any
    /// failure: <c>Account::CanUse_ReservedCharName</c>, <c>UserUtil::CheckIfUserNameExist</c>
    /// must be false, and <c>NamePreemptEventManager::CanGenerateTheName</c> must be true.
    /// We have no name-reservation or preempt-event system, so this is format + uniqueness.
    /// Capture: packet 33 ("Test") -> packet 34 <c>05 00 D8 84 01</c>.
    /// </summary>
    public bool OnCheckUsername(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_CHECK_USERNAME", body);
        string name = f != null && f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";

        var check = ValidateName(name);
        bool available = check == NameCheck.Ok && !NameTaken(s, name);

        _log.LogInformation("C_CHECK_USERNAME from {Id}: '{Name}' -> {Result}",
            s.Id, name, available ? "available" : check == NameCheck.Ok ? "taken" : check.ToString());

        s.SendByDef("S_CHECK_USERNAME", new Dictionary<string, object>
        {
            ["result"] = (byte)(available ? 1 : 0)
        });
        return true;
    }

    /// <summary>
    /// C_CREATE_USER -> S_CREATE_USER { success }.
    ///
    /// <para>Creates the DB row from the packet and gives it a full copy of the starter blob
    /// with the new playerId, the name and the start zone/position patched in. The real server
    /// answers the first <c>SDB_USER_ENTERWORLD</c> for a new character with <c>found=1</c> and
    /// exactly such a blob (cap_newchar.log packet 133) — it never sends <c>found=0</c>, so the
    /// row must not be left blob-less.</para>
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
        var req = FromFields(f);

        var check = ValidateName(req.Name);
        if (check != NameCheck.Ok)
        {
            _log.LogInformation("C_CREATE_USER from {Id}: name '{Name}' rejected ({Why})", s.Id, req.Name, check);
            SendCreateResult(s, false);
            return true;
        }

        if (s.Account.Characters.Count >= MaxCharactersPerAccount)
        {
            _log.LogInformation("C_CREATE_USER from {Id}: character limit reached", s.Id);
            SendCreateResult(s, false);
            return true;
        }

        if (NameTaken(s, req.Name))
        {
            _log.LogInformation("C_CREATE_USER from {Id}: name '{Name}' taken", s.Id, req.Name);
            SendCreateResult(s, false);
            return true;
        }

        var store = Program.Store;
        if (store == null)
        {
            _log.LogError("C_CREATE_USER from {Id}: no character store", s.Id);
            SendCreateResult(s, false);
            return true;
        }

        byte[] template;
        try { template = StarterBlob.LoadTemplate(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "C_CREATE_USER from {Id}: starter blob unavailable, refusing to create", s.Id);
            SendCreateResult(s, false);
            return true;
        }

        long accountId = (long)s.Account.AccountId;
        int position = store.NextPosition(accountId);

        // The blob carries the playerId, which SQLite only assigns on INSERT: insert first with
        // a placeholder blob, then rewrite the blob with the real row id.
        var record = BuildRecord(req, accountId, position, template, playerId: 0);
        int id;
        try
        {
            id = store.CreateCharacter(record);
        }
        catch (Exception ex)
        {
            // The UNIQUE index on name is the last line of defence against a race between
            // C_CHECK_USERNAME and C_CREATE_USER.
            _log.LogWarning(ex, "C_CREATE_USER from {Id}: insert failed for '{Name}'", s.Id, req.Name);
            SendCreateResult(s, false);
            return true;
        }

        var (zone, x, y, z) = StartPositionFor(req.Race, req.Class);
        record.WorldBlob = StarterBlob.Build(template, id, req.Name, zone, x, y, z);
        store.SaveWorldBlob(id, record.WorldBlob);

        s.Account.Characters.Add(FakeCharacter.FromRecord(record));

        _log.LogInformation(
            "C_CREATE_USER from {Id}: created '{Name}' id={CId} template={T} race={R} gender={G} class={C} " +
            "slot={P} start=zone {Z} ({X},{Y},{Zz})",
            s.Id, req.Name, id, record.TemplateId, req.Race, req.Gender, req.Class, position, zone, x, y, z);

        SendCreateResult(s, true);
        return true;
    }

    /// <summary>
    /// C_DELETE_USER -> S_DELETE_USER { success }. Ownership is checked twice: against the
    /// session's character list and again in the DELETE statement's WHERE clause.
    /// Decompile: Handler_C_DELETE_USER, Arb_part_079.c:9852.
    /// </summary>
    public bool OnDeleteUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        int charId = 0;
        var f = s.ReadByDef("C_DELETE_USER", body);
        if (f != null && f.TryGetValue("id", out var idv))
            charId = Convert.ToInt32(idv);
        else if (body.Length >= 4)
            charId = BitConverter.ToInt32(body.Span);

        var chr = s.Account.Characters.Find(c => c.Id == (uint)charId);
        if (chr == null)
        {
            _log.LogWarning("C_DELETE_USER from {Id}: character {CId} not in account", s.Id, charId);
            SendDeleteResult(s, false);
            return true;
        }

        bool deleted = Program.Store?.DeleteCharacter(charId, (long)s.Account.AccountId) ?? false;
        if (!deleted)
        {
            _log.LogWarning("C_DELETE_USER from {Id}: store refused delete of {CId}", s.Id, charId);
            SendDeleteResult(s, false);
            return true;
        }

        s.Account.Characters.Remove(chr);
        _log.LogInformation("C_DELETE_USER from {Id}: deleted '{Name}' id={CId}", s.Id, chr.Name, charId);
        SendDeleteResult(s, true);
        return true;
    }
}
