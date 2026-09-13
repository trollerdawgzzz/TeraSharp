using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Persistence;

/// <summary>
/// A character as the Arbiter sees it: the identity/appearance fields the client
/// needs for the select screen, plus the opaque 15312-byte world state struct
/// that WorldServer reads (DBS_USER_ENTERWORLD) and writes (SDB_UPDATE_USER_DATA).
/// </summary>
public sealed class CharacterRecord
{
    public int Id { get; set; }
    public long AccountId { get; set; }
    public string Name { get; set; } = "";
    public int Gender { get; set; }
    public int Race { get; set; }
    public int Class { get; set; }
    public int Level { get; set; } = 1;
    public int TemplateId { get; set; }
    public int Zone { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public byte[] Appearance { get; set; } = new byte[8];
    public byte[] Details { get; set; } = new byte[32];
    public byte[] Shape { get; set; } = new byte[64];
    public int Weapon { get; set; }
    public int Body { get; set; }
    public int Hand { get; set; }
    public int Feet { get; set; }
    public int Position { get; set; } = 1;
    public DateTime LastLogout { get; set; }
    /// <summary>Opaque WorldServer state (15312 bytes). Null for never-entered characters.</summary>
    public byte[]? WorldBlob { get; set; }
}

public sealed class AccountRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// The 15312-byte WorldServer state struct for a freshly created character.
///
/// Ground truth: <c>data/starter_blob.bin</c> — the blob the real ArbiterServer sent in
/// <c>DBS_USER_ENTERWORLD</c> (0x2738, <c>found=1</c>) for "Test", playerId 2, on its very
/// first enter-world (D:\packetlogs\cap_newchar.log packet 133, 05:49:03). The real server
/// answers <c>found=1</c> with a complete starter blob; it never answers <c>found=0</c>, so
/// creation must produce a blob, not leave it null.
///
/// Only a handful of fields are per-character. Offsets below were verified by diffing
/// packet 133 against the later <c>SDB_UPDATE_USER_DATA</c> (0x27CB) saves of the same
/// character in the same capture (packets 700 / 2553 / 4189):
///
/// <code>
///   112  u32   playerId                       (2 in the template)
///   116  wstr  name, UTF-16LE, null-terminated ("Test"; zero up to 116+2*17)
///   200  u32   class                          (12)
///   208  u32   hp        100000 in the template, 1915 after the first save — runtime, left alone
///   216  u32   mp        100000 in the template
///   220  f32   x                              (16260)
///   224  f32   y                              (1253)
///   228  f32   z                              (-4410)
///   236  u32   zone                           (5; 9827 after the character walked into Velika)
/// </code>
///
/// NOTE: <c>status/HANDOFF.md</c> and the T6/T8 task notes say the zone is "u32 at 208".
/// It is not — 208 is hp. The zone is the u32 at 236. Verified three ways in
/// cap_newchar.log: 236 is 5 on the starter blob and on the first save (character still on
/// the starting island) and 9827 on both later saves (character in Velika), while 208 goes
/// 100000 -> 1915 -> 1915 -> 2026 as the character takes damage and levels.
///
/// Everything else in the blob is identical between a level-1 and a level-58 character and
/// must be copied verbatim. Never parse or synthesise the rest of it.
/// </summary>
public static class StarterBlob
{
    public const int Size = 15312;

    public const int PlayerIdOffset = 112;
    public const int NameOffset = 116;
    /// <summary>Client-enforced maximum character name length; the name region is zeroed to (Max+1) code units.</summary>
    public const int NameMaxChars = 16;
    public const int XOffset = 220;
    public const int YOffset = 224;
    public const int ZOffset = 228;
    public const int ZoneOffset = 236;

    private static byte[]? _cached;

    /// <summary>
    /// Locate and load <c>data/starter_blob.bin</c>. Checked in order:
    /// <c>TERASHARP_STARTER_BLOB</c>, then a <c>data/starter_blob.bin</c> found by walking up
    /// from the running assembly (so it works from bin/Debug and from a publish folder), then
    /// <c>&lt;TERASHARP_DATA&gt;/TeraSharp/data/starter_blob.bin</c>. Cached after the first hit.
    /// </summary>
    public static byte[] LoadTemplate()
    {
        if (_cached != null) return _cached;
        foreach (var candidate in CandidatePaths())
        {
            if (candidate == null || !File.Exists(candidate)) continue;
            var bytes = File.ReadAllBytes(candidate);
            if (bytes.Length != Size)
                throw new InvalidDataException($"starter blob '{candidate}' is {bytes.Length} bytes, expected {Size}");
            _cached = bytes;
            return bytes;
        }
        throw new FileNotFoundException(
            "starter_blob.bin not found. Set TERASHARP_STARTER_BLOB or put it in the repo's data/ folder.");
    }

    /// <summary>Test seam: inject a template instead of reading it from disk.</summary>
    internal static void SetTemplateForTest(byte[]? template) => _cached = template;

    private static IEnumerable<string?> CandidatePaths()
    {
        yield return Environment.GetEnvironmentVariable("TERASHARP_STARTER_BLOB");

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "data", "starter_blob.bin");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        var root = Environment.GetEnvironmentVariable("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
        yield return Path.Combine(root, "TeraSharp", "data", "starter_blob.bin");
    }

    /// <summary>
    /// Copy the template and patch in the per-character fields. Pure — every other byte of
    /// the 15312 is left exactly as the real ArbiterServer sent it.
    /// </summary>
    public static byte[] Build(byte[] template, int playerId, string name, int zone, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Length != Size)
            throw new ArgumentException($"template must be {Size} bytes, got {template.Length}", nameof(template));
        name ??= "";
        if (name.Length > NameMaxChars)
            throw new ArgumentException($"name '{name}' is longer than {NameMaxChars} characters", nameof(name));

        var blob = (byte[])template.Clone();

        BitConverter.TryWriteBytes(blob.AsSpan(PlayerIdOffset, 4), playerId);

        // Clear the whole name region first: the template still holds "Test".
        Array.Clear(blob, NameOffset, (NameMaxChars + 1) * 2);
        for (int i = 0; i < name.Length; i++)
            BitConverter.TryWriteBytes(blob.AsSpan(NameOffset + i * 2, 2), (ushort)name[i]);

        BitConverter.TryWriteBytes(blob.AsSpan(XOffset, 4), x);
        BitConverter.TryWriteBytes(blob.AsSpan(YOffset, 4), y);
        BitConverter.TryWriteBytes(blob.AsSpan(ZOffset, 4), z);
        BitConverter.TryWriteBytes(blob.AsSpan(ZoneOffset, 4), zone);

        return blob;
    }

    /// <summary>Read back the name written at <see cref="NameOffset"/> (used by tests and logging).</summary>
    public static string ReadName(byte[] blob)
    {
        var chars = new List<char>();
        for (int i = 0; i < NameMaxChars; i++)
        {
            ushort c = BitConverter.ToUInt16(blob, NameOffset + i * 2);
            if (c == 0) break;
            chars.Add((char)c);
        }
        return new string(chars.ToArray());
    }
}

/// <summary>
/// SQLite-backed persistence for accounts and characters. Single-file DB,
/// created on first run.
/// </summary>
public sealed class CharacterStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger _log;
    private readonly object _lock = new();

    public CharacterStore(string path, ILogger log)
    {
        _log = log;
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Migrate();
        _log.LogInformation("CharacterStore open: {Path}", path);
    }

    private void Migrate()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS accounts (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TABLE IF NOT EXISTS characters (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id),
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  gender INTEGER NOT NULL, race INTEGER NOT NULL, class INTEGER NOT NULL,
  level INTEGER NOT NULL DEFAULT 1,
  template_id INTEGER NOT NULL,
  zone INTEGER NOT NULL DEFAULT 7005,
  x REAL NOT NULL DEFAULT -449, y REAL NOT NULL DEFAULT 6239, z REAL NOT NULL DEFAULT 1956,
  appearance BLOB NOT NULL, details BLOB NOT NULL, shape BLOB NOT NULL,
  weapon INTEGER NOT NULL DEFAULT 0, body INTEGER NOT NULL DEFAULT 0,
  hand INTEGER NOT NULL DEFAULT 0, feet INTEGER NOT NULL DEFAULT 0,
  position INTEGER NOT NULL DEFAULT 1,
  last_logout TEXT,
  world_blob BLOB,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_characters_account ON characters(account_id);

CREATE TABLE IF NOT EXISTS friends (
  character_id INTEGER NOT NULL REFERENCES characters(id),
  friend_id INTEGER NOT NULL REFERENCES characters(id),
  type INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (character_id, friend_id)
);

CREATE TABLE IF NOT EXISTS blocks (
  character_id INTEGER NOT NULL REFERENCES characters(id),
  blocked_id INTEGER NOT NULL REFERENCES characters(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (character_id, blocked_id)
);
");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- Accounts ----

    public AccountRecord GetOrCreateAccount(string name)
    {
        lock (_lock)
        {
            using var sel = _db.CreateCommand();
            sel.CommandText = "SELECT id, name FROM accounts WHERE name = $n";
            sel.Parameters.AddWithValue("$n", name);
            using (var r = sel.ExecuteReader())
                if (r.Read()) return new AccountRecord { Id = r.GetInt64(0), Name = r.GetString(1) };

            using var ins = _db.CreateCommand();
            ins.CommandText = "INSERT INTO accounts(name) VALUES($n); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$n", name);
            long id = (long)ins.ExecuteScalar()!;
            _log.LogInformation("Created account '{Name}' id={Id}", name, id);
            return new AccountRecord { Id = id, Name = name };
        }
    }

    // ---- Characters ----

    public List<CharacterRecord> GetCharacters(long accountId)
    {
        lock (_lock)
        {
            var list = new List<CharacterRecord>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM characters WHERE account_id = $a ORDER BY position, id";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    public CharacterRecord? GetCharacter(int id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public CharacterRecord? GetCharacterByName(string name)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM characters WHERE name = $n";
            cmd.Parameters.AddWithValue("$n", name);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public bool NameExists(string name)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM characters WHERE name = $n";
            cmd.Parameters.AddWithValue("$n", name);
            return cmd.ExecuteScalar() != null;
        }
    }

    /// <summary>Number of characters on an account (character-slot limit check for C_CAN_CREATE_USER).</summary>
    public int CountCharacters(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id = $a";
            cmd.Parameters.AddWithValue("$a", accountId);
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Next free lobby slot for an account. S_GET_USER_LIST orders by `position`, so every
    /// character on an account needs a distinct one or the select screen stacks them.
    /// </summary>
    public int NextPosition(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(position), 0) + 1 FROM characters WHERE account_id = $a";
            cmd.Parameters.AddWithValue("$a", accountId);
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    public int CreateCharacter(CharacterRecord c)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO characters(account_id,name,gender,race,class,level,template_id,zone,x,y,z,
  appearance,details,shape,weapon,body,hand,feet,position,world_blob)
VALUES($a,$n,$g,$r,$c,$l,$t,$zone,$x,$y,$z,$ap,$de,$sh,$w,$b,$h,$f,$p,$blob);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$a", c.AccountId);
            cmd.Parameters.AddWithValue("$n", c.Name);
            cmd.Parameters.AddWithValue("$g", c.Gender);
            cmd.Parameters.AddWithValue("$r", c.Race);
            cmd.Parameters.AddWithValue("$c", c.Class);
            cmd.Parameters.AddWithValue("$l", c.Level);
            cmd.Parameters.AddWithValue("$t", c.TemplateId);
            cmd.Parameters.AddWithValue("$zone", c.Zone);
            cmd.Parameters.AddWithValue("$x", c.X);
            cmd.Parameters.AddWithValue("$y", c.Y);
            cmd.Parameters.AddWithValue("$z", c.Z);
            cmd.Parameters.AddWithValue("$ap", c.Appearance);
            cmd.Parameters.AddWithValue("$de", c.Details);
            cmd.Parameters.AddWithValue("$sh", c.Shape);
            cmd.Parameters.AddWithValue("$w", c.Weapon);
            cmd.Parameters.AddWithValue("$b", c.Body);
            cmd.Parameters.AddWithValue("$h", c.Hand);
            cmd.Parameters.AddWithValue("$f", c.Feet);
            cmd.Parameters.AddWithValue("$p", c.Position);
            cmd.Parameters.AddWithValue("$blob", (object?)c.WorldBlob ?? DBNull.Value);
            int id = (int)(long)cmd.ExecuteScalar()!;
            c.Id = id;
            _log.LogInformation("Created character '{Name}' id={Id} account={A}", c.Name, id, c.AccountId);
            return id;
        }
    }

    /// <summary>Store the WorldServer state struct after SDB_UPDATE_USER_DATA.</summary>
    public void SaveWorldBlob(int characterId, byte[] blob)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET world_blob = $b, last_logout = datetime('now') WHERE id = $id";
            cmd.Parameters.AddWithValue("$b", blob);
            cmd.Parameters.AddWithValue("$id", characterId);
            int n = cmd.ExecuteNonQuery();
            if (n == 1) _log.LogInformation("Saved world blob ({Len} bytes) for character {Id}", blob.Length, characterId);
            else _log.LogWarning("SaveWorldBlob: character {Id} not found", characterId);
        }
    }

    public void UpdateLevelAndPosition(int characterId, int level, int zone, float x, float y, float z)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET level=$l, zone=$zone, x=$x, y=$y, z=$z WHERE id=$id";
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$zone", zone);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteCharacter(int id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Delete a character only if it belongs to <paramref name="accountId"/>. Returns false if it
    /// does not exist or belongs to someone else, so a forged C_DELETE_USER cannot delete another
    /// account's character even if the in-memory list check is bypassed.
    /// </summary>
    public bool DeleteCharacter(int id, long accountId)
    {
        lock (_lock)
        {
            using var friends = _db.CreateCommand();
            friends.CommandText = @"
DELETE FROM friends WHERE character_id IN (SELECT id FROM characters WHERE id = $id AND account_id = $a)
                       OR friend_id    IN (SELECT id FROM characters WHERE id = $id AND account_id = $a);
DELETE FROM blocks  WHERE character_id IN (SELECT id FROM characters WHERE id = $id AND account_id = $a)
                       OR blocked_id   IN (SELECT id FROM characters WHERE id = $id AND account_id = $a);";
            friends.Parameters.AddWithValue("$id", id);
            friends.Parameters.AddWithValue("$a", accountId);
            friends.ExecuteNonQuery();

            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM characters WHERE id = $id AND account_id = $a";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$a", accountId);
            bool deleted = cmd.ExecuteNonQuery() == 1;
            if (deleted) _log.LogInformation("Deleted character {Id} from account {A}", id, accountId);
            else _log.LogWarning("DeleteCharacter: {Id} not found on account {A}", id, accountId);
            return deleted;
        }
    }

    private static CharacterRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("id")),
        AccountId = r.GetInt64(r.GetOrdinal("account_id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Gender = r.GetInt32(r.GetOrdinal("gender")),
        Race = r.GetInt32(r.GetOrdinal("race")),
        Class = r.GetInt32(r.GetOrdinal("class")),
        Level = r.GetInt32(r.GetOrdinal("level")),
        TemplateId = r.GetInt32(r.GetOrdinal("template_id")),
        Zone = r.GetInt32(r.GetOrdinal("zone")),
        X = (float)r.GetDouble(r.GetOrdinal("x")),
        Y = (float)r.GetDouble(r.GetOrdinal("y")),
        Z = (float)r.GetDouble(r.GetOrdinal("z")),
        Appearance = (byte[])r["appearance"],
        Details = (byte[])r["details"],
        Shape = (byte[])r["shape"],
        Weapon = r.GetInt32(r.GetOrdinal("weapon")),
        Body = r.GetInt32(r.GetOrdinal("body")),
        Hand = r.GetInt32(r.GetOrdinal("hand")),
        Feet = r.GetInt32(r.GetOrdinal("feet")),
        Position = r.GetInt32(r.GetOrdinal("position")),
        LastLogout = r["last_logout"] is string s ? DateTime.Parse(s) : DateTime.MinValue,
        WorldBlob = r["world_blob"] is byte[] b ? b : null,
    };

    // ---- Friends ----

    /// <summary>Get all friends for a character (type: 0=mutual, 1=outgoing request, 2=incoming request).</summary>
    public List<(int FriendId, int Type)> GetFriends(int characterId)
    {
        lock (_lock)
        {
            var list = new List<(int, int)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT friend_id, type FROM friends WHERE character_id = $cid";
            cmd.Parameters.AddWithValue("$cid", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetInt32(1)));
            return list;
        }
    }

    public bool AddFriend(int characterId, int friendId, int type = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO friends(character_id, friend_id, type) VALUES($c,$f,$t)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$f", friendId);
            cmd.Parameters.AddWithValue("$t", type);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool RemoveFriend(int characterId, int friendId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM friends WHERE character_id = $c AND friend_id = $f";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$f", friendId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---- Blocks ----

    public List<int> GetBlocks(int characterId)
    {
        lock (_lock)
        {
            var list = new List<int>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT blocked_id FROM blocks WHERE character_id = $cid";
            cmd.Parameters.AddWithValue("$cid", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt32(0));
            return list;
        }
    }

    public bool AddBlock(int characterId, int blockedId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO blocks(character_id, blocked_id) VALUES($c,$b)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$b", blockedId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool RemoveBlock(int characterId, int blockedId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM blocks WHERE character_id = $c AND blocked_id = $b";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$b", blockedId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void Dispose() => _db.Dispose();
}
