// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;

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

    // =====================================================================================
    // T88 - C_CANCEL_DELETE_USER -> S_CANCEL_DELETE_USER
    //
    // cap_final_client2 frames 32 and 33, in the LOBBY - before S_GET_USER_LIST at 35, so no
    // character is picked and the World link is not in the picture. Arbiter-direct.
    //
    //   32  C->S  C_CANCEL_DELETE_USER  08 00 3C FE  01 00 00 00      (character 1)
    //   33  S->C  S_CANCEL_DELETE_USER  05 00 27 59  01               (ok)
    //
    // Handler_C_CANCEL_DELETE_USER (Arb_part_079.c:9104) guards packet >= 8, reads the u32 at
    // frame offset 4, and then compares two counts off the account: it cancels and answers with
    // the manager's bool only when `current < max`, otherwise it answers FALSE and raises system
    // message 0x2B3. The pending-delete character is still IN the list while the timer runs -
    // that is what S_GET_USER_LIST.deleteRemainSec reports - so the slot check really does need
    // a spare slot beyond the one the doomed character still occupies. Modelled literally.
    // =====================================================================================

    /// <summary>Opcode of the reply. No shipped def exists - it is a raw three-field frame.</summary>
    public const ushort S_CANCEL_DELETE_USER = 0x5927;

    /// <summary>Body minimum, i.e. the handler's packet guard of 8 minus the four header bytes.</summary>
    public const int CancelDeleteUserBodySize = 4;

    /// <summary>System message 0x2B3 - what the real handler raises when there is no free slot.</summary>
    public const int SmtCancelDeleteNoSlot = 691;

    /// <summary>S_CANCEL_DELETE_USER: <c>[u16 len=5][u16 op][u8 ok]</c>.</summary>
    public static byte[] BuildCancelDeleteUser(bool ok) => new byte[]
    {
        0x05, 0x00, unchecked((byte)S_CANCEL_DELETE_USER), (byte)(S_CANCEL_DELETE_USER >> 8),
        (byte)(ok ? 1 : 0),
    };

    /// <summary>
    /// C_CANCEL_DELETE_USER. Clears the <c>characters.delete_at</c> stamp
    /// <see cref="OnDeleteUser"/> set, when the account has room to keep the character.
    /// </summary>
    public bool OnCancelDeleteUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        if (body.Length < CancelDeleteUserBodySize)
        {
            s.Send(BuildCancelDeleteUser(false));
            return true;
        }

        int charId = BitConverter.ToInt32(body.Span);
        var chr = s.Account.Characters.Find(c => c.Id == (uint)charId);
        if (chr == null)
        {
            _log.LogWarning("C_CANCEL_DELETE_USER from {Id}: character {CId} not on the account", s.Id, charId);
            s.Send(BuildCancelDeleteUser(false));
            return true;
        }

        if (s.Account.Characters.Count >= QaUtilityCommands.CharacterSlots(Program.Store, (long)s.Account.AccountId))
        {
            // The real handler's else branch: answer false FIRST, then raise the message.
            _log.LogInformation("C_CANCEL_DELETE_USER from {Id}: no free slot, refusing", s.Id);
            s.Send(BuildCancelDeleteUser(false));
            s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object>
                { ["message"] = SocialHandlers.Smt(SmtCancelDeleteNoSlot) });
            return true;
        }

        bool ok = Program.Store?.CancelCharacterDelete(charId, (long)s.Account.AccountId) ?? false;
        _log.LogInformation("C_CANCEL_DELETE_USER from {Id}: character {CId} -> {Ok}", s.Id, charId, ok);
        s.Send(BuildCancelDeleteUser(ok));
        return true;
    }


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
    /// Where a brand-new character starts, from
    /// <c>Executable\Datasheet\CreateCharData.xml</c> (T16).
    ///
    /// <para>The real Arbiter resolves this in <c>CreateUserCallback</c> in two steps. It first
    /// asks <c>DatasheetManager::GetCreateCharData(classId, out)</c> for the class row and uses
    /// that row's <c>&lt;InitPos&gt;</c> — but only if the row carries a non-zero continent AND a
    /// non-zero x/y/z. Otherwise it falls back to
    /// <c>DatasheetManager::GetInitLocData(race, gender, class, out)</c>, which scans
    /// <c>&lt;InitLoc&gt;</c> for a row whose race/gender/class bitmasks all match and, failing
    /// that, takes the row flagged <c>default</c>.</para>
    ///
    /// <para>In the shipped datasheet that resolves to exactly two answers. Only one of the 13
    /// classes has an <c>&lt;InitPos&gt;</c>:</para>
    /// <code>
    ///   &lt;Char class="soulless" createdLevel="50" firstInvenSize="48"&gt;
    ///     &lt;InitPos continent="7087" pos="-48077,-52002,642" dist="0" dir="-111"/&gt;
    /// </code>
    /// <para>and there is a single <c>&lt;InitLoc default="true"&gt;</c> with
    /// <c>continent="5" pos="16260,1253,-4410" dist="100" dir="-18"</c>. Because that one row is
    /// the default, <c>GetInitLocData</c> returns it for every (race, gender, class) — so race
    /// and gender cannot change the answer with this data, and this method does not take a
    /// gender. Add the parameter if a gendered <c>&lt;InitLoc&gt;</c> row ever appears.</para>
    ///
    /// <para>The default is also exactly the position in the starter blob the real server sent
    /// for "Test" (cap_newchar.log packet 133), which is where this value came from before the
    /// datasheet was found — so nothing changes for the 12 non-soulless classes.</para>
    ///
    /// <para>Not modelled: the datasheet's <c>dist</c> (spawn scatter radius) and <c>dir</c>
    /// (facing). The real Arbiter carries both into the new character's row; we have no field
    /// for either, and the blob offsets for them are unidentified.</para>
    ///
    /// <para>T221: read from the sheet (<see cref="World.DatasheetLoader.StartPositions"/>), so a
    /// server that drops the soulless <c>&lt;InitPos&gt;</c> (tools/v31-feel.ps1, Reaper from level 1)
    /// starts its Reapers at the <c>&lt;InitLoc&gt;</c> default like every other class. The two
    /// positions below are the built-in, used only when the sheet is missing.</para>
    /// </summary>
    internal static (int Zone, float X, float Y, float Z) StartPositionFor(int race, int cls)
    {
        var table = World.DatasheetLoader.StartPositions.Value;
        return (uint)cls < (uint)table.Length ? table[cls] : DefaultStart;
    }

    /// <summary>Class id 8 — the one class with its own <c>&lt;InitPos&gt;</c>.</summary>
    internal const int SoullessClassId = 8;

    /// <summary><c>&lt;InitLoc default="true"&gt;</c>: Island of Dawn.</summary>
    internal static readonly (int Zone, float X, float Y, float Z) DefaultStart = (5, 16260f, 1253f, -4410f);

    /// <summary><c>&lt;Char class="soulless"&gt;&lt;InitPos&gt;</c>.</summary>
    internal static readonly (int Zone, float X, float Y, float Z) SoullessStart = (7087, -48077f, -52002f, 642f);

    /// <summary>T221. The shipped sheet as transcribed, per class id: soulless at its own
    /// <c>&lt;InitPos&gt;</c>, every other class at the default - the built-in for
    /// <see cref="World.DatasheetLoader.StartPositions"/>.</summary>
    internal static (int Zone, float X, float Y, float Z)[] BuiltInStartPositions()
    {
        var table = new (int Zone, float X, float Y, float Z)[World.DatasheetLoader.ClassNames.Length];
        for (int c = 0; c < table.Length; c++) table[c] = c == SoullessClassId ? SoullessStart : DefaultStart;
        return table;
    }

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
    // T50: every read here goes through DefField, which reinterprets rather than range-checks.
    // Convert.ToInt32 on the uint a `uint32` def field produces throws OverflowException for any
    // value past int.MaxValue, and Convert.ToUInt32 throws for any negative int - neither is a
    // malformed packet, and an exception out of a handler is logged at Error by PacketDispatcher.
    private static CreateUserRequest FromFields(IReadOnlyDictionary<string, object> f) => new()
    {
        Gender = DefField.I32(f, "gender"),
        Race = DefField.I32(f, "race"),
        Class = DefField.I32(f, "class"),
        Appearance = f.TryGetValue("appearance", out var av) ? av as byte[] ?? new byte[8] : new byte[8],
        IsSecondCharacter = DefField.Bool(f, "isSecondCharacter"),
        Appearance2 = DefField.U32(f, "appearance2"),
        IsRandomName = DefField.Bool(f, "isRandomName"),
        Name = DefField.Str(f, "name"),
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
        // T232: the row's four look columns, from the class's own kit. They used to stay at the
        // column default of 0 - the lobby list and InventoryHandlers both read them, so a new
        // character advertised item 0 in all four slots while its inventory carried the kit.
        var worn = TeraSharp.Arbiter.World.StarterInventory.WornLook(req.Class);
        return new CharacterRecord
        {
            Id = playerId,
            AccountId = accountId,
            Name = req.Name,
            Gender = req.Gender,
            Race = req.Race,
            Class = req.Class,
            Level = TeraSharp.Arbiter.World.StarterInventory.CreatedLevelFor(req.Class),   // T162: CreateCharData createdLevel
            TemplateId = ComputeTemplateId(req.Race, req.Gender, req.Class),
            Appearance = req.Appearance,
            Details = req.Details,
            Shape = req.Shape,
            Zone = zone, X = x, Y = y, Z = z,
            Weapon = worn.Weapon, Body = worn.Body, Hand = worn.Hand, Feet = worn.Feet,
            Position = position,
            // playerId is only known after the INSERT; when it is 0 here the caller patches
            // the blob again with the real row id (see OnCreateUser).
            WorldBlob = StarterBlob.Build(blobTemplate, playerId, req.Name, IdentityOf(req), zone, x, y, z),
        };
    }

    /// <summary>
    /// The identity block the world blob needs, straight from the create request. The three
    /// byte blocks are passed through by reference-copy semantics: ParseCreateUser and the .def
    /// path both allocate them at their struct sizes, and StarterBlob.Build copies them into the
    /// blob rather than keeping the arrays.
    /// </summary>
    internal static CharacterIdentity IdentityOf(CreateUserRequest req) => new()
    {
        Race = req.Race,
        Gender = req.Gender,
        Class = req.Class,
        Appearance = req.Appearance,
        Appearance2 = req.Appearance2,
        Details = req.Details,
        Shape = req.Shape,
    };

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
        var canStore = Program.Store;
        long canAccount = (long)s.Account.AccountId;
        int capacity = QaUtilityCommands.CharacterSlots(canStore, canAccount);
        // T228: the same ORDINAL test C_CREATE_USER applies, so the greyed button and the refusal
        // cannot disagree. A pending-delete character still holds its slot while the lobby stops
        // listing it, so the visible count alone said "room" where the ordinal space said "full".
        bool ok = canStore == null
            ? s.Account.Characters.Count < capacity
            : canStore.NextPosition(canAccount, capacity) != 0;
        _log.LogInformation("C_CAN_CREATE_USER from {Id} -> ok={Ok} ({N}/{Max})",
            s.Id, ok, s.Account.Characters.Count, capacity);
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

        var store = Program.Store;
        if (store == null)
        {
            _log.LogError("C_CREATE_USER from {Id}: no character store", s.Id);
            SendCreateResult(s, false);
            return true;
        }

        long accountId = (long)s.Account.AccountId;
        int slots = QaUtilityCommands.CharacterSlots(store, accountId);
        // T228: refuse on the ORDINAL, not on the visible count. The old count check passed while
        // every low slot was still held, so a character was created at slot 10 or 11 on an eight-slot
        // account and World then refused it with "delete 1 character before entering world" - World's
        // own limit reads the ordinal. 0 means no free slot in 1..slots.
        int position = store.NextPosition(accountId, slots);
        if (position == 0)
        {
            _log.LogInformation("C_CREATE_USER from {Id}: no free character slot (account {A}, all {S} taken)",
                s.Id, accountId, slots);
            SendCreateResult(s, false);
            return true;
        }

        if (NameTaken(s, req.Name))
        {
            _log.LogInformation("C_CREATE_USER from {Id}: name '{Name}' taken", s.Id, req.Name);
            SendCreateResult(s, false);
            return true;
        }

        // T209: the record is generated from CreateCharData.xml unless an operator dropped a
        // capture in, so it is seeded with THIS character's account and slot. The captured
        // template carried the capture's own account 1 slot 2 on every character we ever made.
        byte[] template;
        try
        {
            template = StarterBlob.LoadTemplate(new StarterBlob.Seed(
                AccountDbId: accountId,
                AccountName: s.Account.Name ?? string.Empty,
                SlotOrdinal: position,
                Level: World.StarterInventory.CreatedLevelFor(req.Class),
                CreatedUtc: DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // Only a malformed override reaches this now: a missing file generates the record.
            _log.LogError(ex, "C_CREATE_USER from {Id}: starter blob unusable, refusing to create", s.Id);
            SendCreateResult(s, false);
            return true;
        }

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
        record.WorldBlob = StarterBlob.Build(template, id, req.Name, IdentityOf(req), zone, x, y, z);
        // Build() copies the template, so re-stamp the three fields that say who owns the record.
        // They are outside its patch list, and on an operator-supplied override they would still
        // be the capture's account.
        StarterBlob.WriteAccount(record.WorldBlob, accountId, s.Account.Name, position);
        store.SaveWorldBlob(id, record.WorldBlob);

        s.Account.Characters.Add(FakeCharacter.FromRecord(record));

        // T162: made above level 1 by the sheet - World runs its own level commit (skills) at the
        // first spawn, the way T152b hands it a level set from outside.
        if (record.Level > 1) WorldLevelSync.Queue(id, record.Level);

        _log.LogInformation(
            "C_CREATE_USER from {Id}: created '{Name}' id={CId} template={T} race={R} gender={G} class={C} " +
            "slot={P} start=zone {Z} ({X},{Y},{Zz}) (identity patched into the blob)",
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
        // T50: C_DELETE_USER.1 is `uint32 id`, so the reader hands back a uint and roughly half
        // of all four-byte values are past int.MaxValue. Convert.ToInt32 threw OverflowException
        // on every one of them - 125 of the 126 client-fuzz errors were this line alone
        // (status/FUZZ-FINDINGS.txt). The real handler reinterprets the DWORD into a signed slot
        // and compares it against the account's characters, where 0xFFFFFFFF matches nothing.
        int charId = 0;
        var f = s.ReadByDef("C_DELETE_USER", body);
        if (f != null && f.TryGetValue("id", out var idv))
            charId = DefField.I32(idv);
        else if (body.Length >= 4)
            charId = BitConverter.ToInt32(body.Span);

        var chr = s.Account.Characters.Find(c => c.Id == (uint)charId);
        if (chr == null)
        {
            _log.LogWarning("C_DELETE_USER from {Id}: character {CId} not in account", s.Id, charId);
            SendDeleteResult(s, false);
            return true;
        }

        // T101b SCHEDULED the delete instead of dropping the row, which is right - but it took the
        // window from the hardcoded CharacterStore.DeleteExpireHours and never read
        // ServerConfig.xml. T224: <DeleteUser expireHour1 expireHour2 deletionSectionClassifyLevel>
        // is the policy, CharacterDeletion is the single code path, and expireHour 0 means an
        // immediate HARD delete. With expireHour2="0" the old code still parked the row for three
        // days and GetCharacters had no delete_at predicate, so the character was back in the next
        // S_GET_USER_LIST: the reported "delete doesn't stick".
        int level = Program.Store?.GetCharacter(charId)?.Level ?? 1;
        var outcome = CharacterDeletion.Delete(
            Program.Store, charId, (long)s.Account.AccountId, chr.Name,
            level, DateTimeOffset.UtcNow, CharacterDeletion.Current, out long deleteAt, _log);
        if (outcome == CharacterDeletion.Outcome.Refused)
        {
            _log.LogWarning("C_DELETE_USER from {Id}: store refused delete of {CId}", s.Id, charId);
            SendDeleteResult(s, false);
            return true;
        }

        // Only a HARD delete takes the character off the session's list. A scheduled one must stay:
        // retail's S_GET_USER_LIST is the same 1183 bytes either side of a successful
        // C_CANCEL_DELETE_USER (cap_final_client2 frames 11/35), and that handler's own slot check
        // (Arb_part_079.c:9104, "current < max") only makes sense while the doomed character still
        // occupies its slot. Removing it here also meant a cancel in the same session could not put
        // it back - nothing re-adds to Account.Characters.
        if (outcome == CharacterDeletion.Outcome.Hard) s.Account.Characters.Remove(chr);
        _log.LogInformation(
            "C_DELETE_USER from {Id}: '{Name}' id={CId} level {Level} -> {Outcome}, delete_at {At}",
            s.Id, chr.Name, charId, level, outcome, deleteAt);
        SendDeleteResult(s, true);
        return true;
    }

    // =====================================================================================
    // T76: the five S_GET_USER_LIST fields the character-select screen was drawing empty
    // =====================================================================================

    /// <summary>
    /// Overwrite the location / last-played / rested-xp slots of one S_GET_USER_LIST element
    /// with what the characters row actually holds.
    ///
    /// <para><b>Measured against cap_social_client frame 11</b>, a real S_GET_USER_LIST of
    /// 1169 bytes with two characters. Element stride is 472 bytes (the def
    /// S_GET_USER_LIST.18, whose header reads majorPatchVersion &gt;= 95 &amp;&amp; &lt; 101 -
    /// this client), and the two elements read:</para>
    /// <list type="bullet">
    /// <item>dob: worldId 1, guardId 1, sectionId 1, lastLogoutTime 1789387775,
    ///   restBonusXp 419, maxRestBonusXp 419</item>
    /// <item>Test: worldId 1, guardId 25, sectionId 599001, lastLogoutTime 1789393881,
    ///   restBonusXp 0, maxRestBonusXp 1523</item>
    /// </list>
    /// <para>Three things fall out of that. (1) The location trio is the same
    /// (worldId, guardId, sectionId) C_VISIT_NEW_SECTION reports and S_FRIEND_LIST carries -
    /// Test reads (1, 25, 599001) in BOTH packets. (2) lastLogoutTime is an ABSOLUTE unix
    /// second count, not an elapsed one: the same frame carries deleteRemainSec and
    /// banRemainSec = -1789393912, i.e. 0 minus the current time, which pins the capture at
    /// unix 1789393912 and makes Test s logout 31 seconds old. (3) restBonusXp is per
    /// character and maxRestBonusXp is per LEVEL (419 at level 1, 1523 at level 3).</para>
    ///
    /// <para><b>maxRestBonusXp is deliberately not touched here.</b> It comes from
    /// <c>RestBonusDataSheet</c> (RestBonusDataSheet::Load, Arb_part_006.c:5206; the packet
    /// writer reads a field called MaxRestBonusPoint, Arb_part_021.c:4532), which TeraSharp
    /// does not load - so guessing a formula from two samples would be worse than the constant
    /// already there.</para>
    /// </summary>
    public static void FillLobbyFields(
        Dictionary<string, object> element, CharacterStore? store, int characterId)
    {
        ArgumentNullException.ThrowIfNull(element);
        var r = store?.GetCharacter(characterId);
        if (r == null) return;

        // ---- T76: location, last played, rested xp ----
        element["worldId"] = r.LastWorld;
        element["guardId"] = r.LastGuard;
        element["sectionId"] = r.LastSection;
        element["lastLogoutTime"] = UnixSeconds(r.LastLogout);
        element["restBonusXp"] = r.RestBonus;

        // ---- T224: the delete countdown ----
        //
        // LoginHandlers ships (isDeleting false, deleteTime 0, deleteRemainSec 0) for every
        // character, so a scheduled delete was invisible in character select. Retail keeps the
        // doomed character listed and gates the UI on isDeleting: cap_final_client2 frame 11 reads
        // (1, 1789879815, 259182) and frame 35 - after C_CANCEL_DELETE_USER answered ok at frame 33
        // - reads (0, 1789879815, 259150). So the stamp SURVIVES the cancel, only the bool flips,
        // and remainSec is deleteTime - now recomputed per send: the two frames are 32 s apart and
        // banRemainSec drops by the same 32. 259200 s is 72 h, this deployment's expireHour2.
        var (isDeleting, deleteTime, deleteRemainSec) = CharacterDeletion.LobbyFields(
            store?.GetCharacterDeleteAt(characterId) ?? 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        element["isDeleting"] = isDeleting;
        element["deleteTime"] = deleteTime;
        element["deleteRemainSec"] = deleteRemainSec;

        // ---- T122: everything else the ROW owns ----
        //
        // The caller fills this element from s.Account.Characters, which is a snapshot taken
        // by FakeAccount.LoadFromStore at LOGIN. Anything that changed while the player was in
        // world - levelling, a GM command, moving, a gear change - changed the row and not the
        // snapshot, so character select kept showing the login-time values: level 8 in world,
        // level 1 on the way back out (live 2026-09-20).
        //
        // Rather than refresh the cache, this refreshes the ELEMENT. It is the same
        // GetCharacter call T76 already made per entry, so the fix costs no extra query and
        // needs no change in the human-owned LoginHandlers.OnGetUserList.
        element["level"] = r.Level;
        element["name"] = r.Name;
        element["position"] = r.Position;
        element["gender"] = r.Gender;
        element["race"] = r.Race;
        element["class"] = r.Class;
        element["weapon"] = r.Weapon;
        element["body"] = r.Body;
        element["hand"] = r.Hand;
        element["feet"] = r.Feet;
        if (r.Appearance is { Length: StarterBlob.AppearanceSize }) element["appearance"] = r.Appearance;
        if (r.Details is { Length: StarterBlob.DetailsSize }) element["details"] = r.Details;
        if (r.Shape is { Length: StarterBlob.ShapeSize }) element["shape"] = r.Shape;

        // hp and mp have no column - the saved world blob is their only source (T105 pinned
        // +208 and +216 next to the level at +204). A blob too short to hold them, or a
        // character that has never entered the world, leaves the caller's template values
        // alone rather than showing 0 hp in the lobby.
        long hp = StarterBlob.ReadHp(r.WorldBlob);
        int mp = StarterBlob.ReadMp(r.WorldBlob);
        if (hp > 0) element["hp"] = hp;
        if (mp > 0) element["mp"] = mp;

        // T191f: isNewCharacter (entry+425) is TRUE only until the character has been in the world
        // once. classic_live3's record 11 is 01 on entry 0 and 00 on entries 1..6; ours was 01 on
        // every entry of every login, because the caller hard-coded it. The record is
        // characters.entered_world, written by SDB_USER_ENTERWORLD.
        if (store != null) element["isNewCharacter"] = !store.HasEnteredWorld(characterId);

        // NOT refreshed, on purpose: `maxRestBonusXp` (RestBonusDataSheet, which we do not
        // load - see the note above), and exp, which S_GET_USER_LIST has no field for.
    }

    /// <summary>
    /// A stored UTC timestamp as the unix seconds S_GET_USER_LIST wants, 0 when never set.
    /// The column is written with SQLite datetime(now), which is UTC text, so the subtraction
    /// needs no kind conversion.
    /// </summary>
    public static long UnixSeconds(DateTime when)
    {
        if (when == default) return 0L;
        long secs = (long)(when - DateTime.UnixEpoch).TotalSeconds;
        return secs < 0 ? 0L : secs;
    }
}
