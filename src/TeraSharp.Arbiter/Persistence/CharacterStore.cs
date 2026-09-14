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
    /// <summary>Total exp, from SDB_UPDATE_EXP_LEVEL (0x273B). Not shown in the lobby; kept so
    /// the row is a complete answer for anything that later needs it.</summary>
    public long Exp { get; set; }
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

    // ---- T21: the dungeon return point (the real Arbiter's SysReturnLoc) ----
    /// <summary>Continent to return to when a dungeon enter-world fails, 0 = none. User+0x1a8.</summary>
    public int ReturnZone { get; set; }
    /// <summary>Return position, stored as the real Arbiter stores it: ints, not floats. User+0x1b0/4/8.</summary>
    public float ReturnX { get; set; }
    public float ReturnY { get; set; }
    public float ReturnZ { get; set; }
    /// <summary>The instance this character last entered (DungeonEnterContext+0), 0 = none.</summary>
    public int DungeonId { get; set; }
    /// <summary>
    /// ChannelInstanceId of that instance, as WorldServer allocated it in the 0x13C0 response
    /// (DungeonEnterContext+140, 0x0AF00001 in the capture). Goes in AS_ENTER_WORLD [52] when
    /// the saved zone IS that instance; 0 = none.
    /// </summary>
    public int InstancePdId { get; set; }
}

public sealed class AccountRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>
    /// GM level, 0 for a normal account. The real Arbiter keeps this per CHARACTER at
    /// User+0x3b98, loads it from the AdminLevel column and writes it back through
    /// dbo.spUpdateUserAdminLevel; every gate in the binary tests only >= 1, and its own
    /// set_go command assigns 5. We keep it on the ACCOUNT so a login can read it before a
    /// character is picked. status/GM-DESIGN.md.
    /// </summary>
    public int AdminLevel { get; set; }
}

/// <summary>
/// The appearance/identity block C_CREATE_USER carries, in the shape the world blob wants it.
/// Race, gender and class decide the model WorldServer spawns; the three byte blocks are the
/// character-creator sliders. Kept here rather than taking
/// <c>Handlers.CreateUserRequest</c> directly so Persistence does not depend on Handlers.
/// </summary>
public sealed class CharacterIdentity
{
    public int Race { get; init; }
    public int Gender { get; init; }
    public int Class { get; init; }
    /// <summary>8-byte "customize" block (blob offset 288).</summary>
    public byte[] Appearance { get; init; } = new byte[StarterBlob.AppearanceSize];
    /// <summary>The second appearance word (blob offset 296); 100 in the capture.</summary>
    public uint Appearance2 { get; init; }
    /// <summary>32-byte creator detail sliders (blob offset 312).</summary>
    public byte[] Details { get; init; } = new byte[StarterBlob.DetailsSize];
    /// <summary>64-byte creator shape sliders (blob offset 344).</summary>
    public byte[] Shape { get; init; } = new byte[StarterBlob.ShapeSize];
}


/// <summary>
/// The per-race/gender/class default skill lists a brand-new character starts with — T18.
///
/// <para><b>Why this exists.</b> A level-1 character's skills are not granted by WorldServer and
/// they are not in <c>CreateCharData.xml</c> (that sheet only carries the starter items; its
/// header comment says it is loaded by the Arbiter alone, and it has no skill entries). The real
/// ArbiterServer writes them into the character's world blob at CREATION, in
/// <c>AccountManager::ExecCreateDefaultSkills(int, UserData *)</c> (Arb_part_080.c:12875): it
/// looks the character's (race, class, gender) up in the <c>DefaultSkillInfo</c> datasheet
/// (<c>DatasheetManager::LoadDefaultSkillInfo</c>, Arb_part_085.c:5992 — the file on disk is
/// <c>Executable\Datasheet\DefaultSkillSet.xml</c>), runs <c>spInsertSkillLearned</c> /
/// <c>spInsertPassiveSkill</c> per id, and for every row that inserted writes
/// <c>[u32 skillId][u8 0]</c> into <c>UserData + 0x1C20</c> (active) and
/// <c>UserData + 0x1AE0</c> (passive), stepping 8 bytes. UserData IS the 15312-byte blob, so
/// those are blob offsets 7200 and 6880 — see <see cref="StarterBlob.ActiveSkillsOffset"/>.</para>
///
/// <para>WorldServer then reads them straight back out: <c>UserEnterWorldContext::SetRecvData</c>
/// memcpys our <c>DBS_USER_ENTERWORLD</c> blob to <c>context + 0xA8</c>
/// (WorldServer.exe.c:1661252), and <c>UpdateRecvedData</c> copies <c>context + 0x1CC8</c> /
/// <c>+ 0x1B88</c> (= blob 7200 / 6880) into <c>User + 0x8460</c> / <c>User + 0x8320</c>.
/// <c>User::SendMySkillList</c> (WorldServer.exe.c:2193096) walks exactly those two arrays to
/// build <c>S_SKILL_LIST</c>. Verified against the capture: the 7 active + 17 passive ids in
/// <c>data/starter_blob.bin</c> are byte-identical to the 24 entries of the first
/// <c>S_SKILL_LIST</c> in <c>D:\packetlogs\cap_newchar_client.log</c> (packet 81).</para>
///
/// <para><b>The bug this fixes.</b> <c>data/starter_blob.bin</c> is a Popori-female Glaiver's
/// blob ("Test", race 4 / gender 1 / class 12 = Elin valkyrie), so before T18 every character we
/// created — warrior included — shipped the VALKYRIE's skill ids. None of a warrior's own skills
/// were ever in <c>S_SKILL_LIST</c>, so the client drew his whole skill tree as not-yet-learned:
/// icons present, all locked.</para>
///
/// <para>Generated from <c>Executable\Datasheet\DefaultSkillSet.xml</c> (99 rows, the complete
/// sheet). Race, gender and class are the Arbiter's own numeric ids, decompiled from its
/// name-&gt;id lookups: race <c>FUN_1400c4e20</c> (Arb_part_006.c:523) = Human 0, HighElf 1,
/// Aman 2, Castanic 3, Popori 4, Baraka 5; class <c>FUN_140065e00</c> (Arb_part_003.c:2179) =
/// Warrior 0, Lancer 1, Slayer 2, Berserker 3, Sorcerer 4, Archer 5, Priest 6, Elementalist 7,
/// Soulless 8, Engineer 9, Fighter 10, Assassin 11, Glaiver 12; gender Male 0, Female 1
/// (<c>LoadDefaultSkillInfo</c> maps the literal "Female" to 1).</para>
///
/// <para>To regenerate after a datasheet change, re-read that XML and re-emit
/// <see cref="Rows"/>; the format is <c>"race,gender,class|active;ids|passive;ids"</c>.</para>
/// </summary>
public static class DefaultSkillSet
{
    /// <summary>One row per creatable race/gender/class. Order is irrelevant; the lookup is a dictionary.</summary>
    internal static readonly string[] Rows =
    {
        "0,0,0|10100;20100;9020100;60401301|10001;19100;19101;19102",  // Human Male Warrior
        "0,0,1|10100;20100;9020100;260100;60401301|10000;19100;19101;19102",  // Human Male Lancer
        "0,0,2|10100;40100;9020100;60401301|10002;19100;19101;19102",  // Human Male Slayer
        "0,0,3|10100;20100;290100;9020100;60401301|10000;19100;19101;19102;14070",  // Human Male Berserker
        "0,0,4|10100;70100;9020100;60401301|10002;19100;19101;19102",  // Human Male Sorcerer
        "0,0,5|10100;60100;9020100;60401301|10002;19100;19101;19102",  // Human Male Archer
        "0,0,6|10100;180100;9020100;380100;60401301|10002;19100;19101;19102",  // Human Male Priest
        "0,0,7|10100;170100;180100;9020100;60401301|10002;19100;19101;19102",  // Human Male Elementalist
        "0,0,10|10100;20100;400100;9020100;60401301|10002;19100;19101;19102;92001;92002;92003;92004;92005;92006;92007;92008;92009;92010;92011;92012",  // Human Male Fighter
        "0,1,0|10100;20100;9020100;60401301|10001;19100;19101;19102",  // Human Female Warrior
        "0,1,1|10100;20100;9020100;260100;60401301|10000;19100;19101;19102",  // Human Female Lancer
        "0,1,2|10100;40100;9020100;60401301|10002;19100;19101;19102",  // Human Female Slayer
        "0,1,3|10100;20100;290100;9020100;60401301|10000;19100;19101;19102;14070",  // Human Female Berserker
        "0,1,4|10100;70100;9020100;60401301|10002;19100;19101;19102",  // Human Female Sorcerer
        "0,1,5|10100;60100;9020100;60401301|10002;19100;19101;19102",  // Human Female Archer
        "0,1,6|10100;180100;9020100;380100;60401301|10002;19100;19101;19102",  // Human Female Priest
        "0,1,7|10100;170100;180100;9020100;60401301|10002;19100;19101;19102",  // Human Female Elementalist
        "0,1,10|10100;20100;400100;9020100;60401301|10002;19100;19101;19102;92001;92002;92003;92004;92005;92006;92007;92008;92009;92010;92011;92012",  // Human Female Fighter
        "1,0,0|10100;20100;9020100;60401301|10001;19200;19201",  // Highelf Male Warrior
        "1,0,1|10100;20100;9020100;260100;60401301|10000;19200;19201",  // Highelf Male Lancer
        "1,0,2|10100;40100;9020100;60401301|10002;19200;19201",  // Highelf Male Slayer
        "1,0,3|10100;20100;290100;9020100;60401301|10000;19200;19201;14070",  // Highelf Male Berserker
        "1,0,4|10100;70100;9020100;60401301|10000;19200;19201;14070",  // Highelf Male Sorcerer
        "1,0,5|10100;60100;9020100;60401301|10002;19200;19201",  // Highelf Male Archer
        "1,0,6|10100;180100;9020100;380100;60401301|10002;19200;19201",  // Highelf Male Priest
        "1,0,7|10100;170100;180100;9020100;60401301|10002;19200;19201",  // Highelf Male Elementalist
        "1,1,0|10100;20100;9020100;60401301|10001;19200;19201",  // Highelf Female Warrior
        "1,1,1|10100;20100;9020100;260100;60401301|10000;19200;19201",  // Highelf Female Lancer
        "1,1,2|10100;40100;9020100;60401301|10002;19200;19201",  // Highelf Female Slayer
        "1,1,3|10100;20100;290100;9020100;60401301|10000;19200;19201;14070",  // Highelf Female Berserker
        "1,1,4|10100;70100;9020100;60401301|10002;19200;19201",  // Highelf Female Sorcerer
        "1,1,5|10100;60100;9020100;60401301|10002;19200;19201",  // Highelf Female Archer
        "1,1,6|10100;180100;9020100;380100;60401301|10002;19200;19201",  // Highelf Female Priest
        "1,1,7|10100;170100;180100;9020100;60401301|10002;19200;19201",  // Highelf Female Elementalist
        "1,1,9|10100;400100;9020100;60401301|10003;19200;19201;91013;91014;91003;91004;91006",  // Highelf Female Engineer
        "2,0,0|10100;20100;9020100;60401301|10001;19400;19401;19402",  // Aman Male Warrior
        "2,0,1|10100;20100;9020100;260100;60401301|10000;19400;19401;19402",  // Aman Male Lancer
        "2,0,2|10100;40100;9020100;60401301|10002;19400;19401;19402",  // Aman Male Slayer
        "2,0,3|10100;20100;290100;9020100;60401301|10000;19400;19401;19402;14070",  // Aman Male Berserker
        "2,0,4|10100;70100;9020100;60401301|10002;19400;19401;19402",  // Aman Male Sorcerer
        "2,0,5|10100;60100;9020100;60401301|10002;19400;19401;19402",  // Aman Male Archer
        "2,0,6|10100;180100;9020100;380100;60401301|10002;19400;19401;19402",  // Aman Male Priest
        "2,0,7|10100;170100;180100;9020100;60401301|10002;19400;19401;19402",  // Aman Male Elementalist
        "2,1,0|10100;20100;9020100;60401301|10001;19400;19401;19402",  // Aman Female Warrior
        "2,1,1|10100;20100;9020100;260100;60401301|10000;19400;19401;19402",  // Aman Female Lancer
        "2,1,2|10100;40100;9020100;60401301|10002;19400;19401;19402",  // Aman Female Slayer
        "2,1,3|10100;20100;290100;9020100;60401301|10000;19400;19401;19402;14070",  // Aman Female Berserker
        "2,1,4|10100;70100;9020100;60401301|10002;19400;19401;19402",  // Aman Female Sorcerer
        "2,1,5|10100;60100;9020100;60401301|10002;19400;19401;19402",  // Aman Female Archer
        "2,1,6|10100;180100;9020100;380100;60401301|10002;19400;19401;19402",  // Aman Female Priest
        "2,1,7|10100;170100;180100;9020100;60401301|10002;19400;19401;19402",  // Aman Female Elementalist
        "3,0,0|10100;20100;9020100;60401301|10001;19300;19301;19302",  // Castanic Male Warrior
        "3,0,1|10100;20100;9020100;260100;60401301|10000;19300;19301;19302",  // Castanic Male Lancer
        "3,0,2|10100;40100;9020100;60401301|10002;19300;19301;19302",  // Castanic Male Slayer
        "3,0,3|10100;20100;290100;9020100;60401301|10000;19300;19301;19302;14070",  // Castanic Male Berserker
        "3,0,4|10100;70100;9020100;60401301|10002;19300;19301;19302",  // Castanic Male Sorcerer
        "3,0,5|10100;60100;9020100;60401301|10002;19300;19301;19302",  // Castanic Male Archer
        "3,0,6|10100;180100;9020100;380100;60401301|10002;19300;19301;19302",  // Castanic Male Priest
        "3,0,7|10100;170100;180100;9020100;60401301|10002;19300;19301;19302",  // Castanic Male Elementalist
        "3,1,0|10100;20100;9020100;60401301|10001;19300;19301;19302",  // Castanic Female Warrior
        "3,1,1|10100;20100;9020100;260100;60401301|10000;19300;19301;19302",  // Castanic Female Lancer
        "3,1,2|10100;40100;9020100;60401301|10002;19300;19301;19302",  // Castanic Female Slayer
        "3,1,3|10100;20100;290100;9020100;60401301|10000;19300;19301;19302;14070",  // Castanic Female Berserker
        "3,1,4|10100;70100;9020100;60401301|10002;19300;19301;19302",  // Castanic Female Sorcerer
        "3,1,5|10100;60100;9020100;60401301|10002;19300;19301;19302",  // Castanic Female Archer
        "3,1,6|10100;180100;9020100;380100;60401301|10002;19300;19301;19302",  // Castanic Female Priest
        "3,1,7|10100;170100;180100;9020100;60401301|10002;19300;19301;19302",  // Castanic Female Elementalist
        "3,1,9|10100;400100;9020100;60401301|10003;19300;19301;19302;91013;91014;91003;91004;91006",  // Castanic Female Engineer
        "3,1,12|10199;60199;140199;160199;9020100;60401301|10002;19300;19301;19302;94001;94002;94003;94005;94006;94007;94008;94009;94010;94011;94012;94013;94014;94015",  // Castanic Female Glaiver
        "4,0,0|10100;20100;9020100;9030100;60401301|10001;19500;19501",  // Popori Male Warrior
        "4,0,1|10100;20100;9020100;9030100;260100;60401301|10000;19500;19501",  // Popori Male Lancer
        "4,0,2|10100;40100;9020100;9030100;60401301|10002;19500;19501",  // Popori Male Slayer
        "4,0,3|10100;20100;290100;9020100;9030100;60401301|10000;19500;19501;14070",  // Popori Male Berserker
        "4,0,4|10100;70100;9020100;9030100;60401301|10002;19500;19501",  // Popori Male Sorcerer
        "4,0,5|10100;60100;9020100;9030100;60401301|10002;19500;19501",  // Popori Male Archer
        "4,0,6|10100;180100;9020100;9030100;380100;60401301|10002;19500;19501",  // Popori Male Priest
        "4,0,7|10100;170100;180100;9020100;9030100;60401301|10002;19500;19501",  // Popori Male Elementalist
        "4,0,10|10100;20100;400100;9020100;9030100;60401301|10002;19500;19501;92001;92002;92003;92004;92005;92006;92007;92008;92009;92010;92011;92012",  // Popori Male Fighter
        "4,1,0|10100;20100;9020100;9030100;60401301|10001;19500;19501",  // Popori Female Warrior
        "4,1,1|10100;20100;9020100;9030100;260100;60401301|10000;19500;19501",  // Popori Female Lancer
        "4,1,2|10100;40100;9020100;9030100;60401301|10002;19500;19501",  // Popori Female Slayer
        "4,1,3|10100;20100;290100;9020100;9030100;60401301|10000;19500;19501;14070",  // Popori Female Berserker
        "4,1,4|10100;70100;9020100;9030100;60401301|10002;19500;19501",  // Popori Female Sorcerer
        "4,1,5|10100;60100;9020100;9030100;60401301|10002;19500;19501",  // Popori Female Archer
        "4,1,6|10100;180100;9020100;9030100;380100;60401301|10002;19500;19501",  // Popori Female Priest
        "4,1,7|10100;170100;180100;9020100;9030100;60401301|10002;19500;19501",  // Popori Female Elementalist
        "4,1,8|10100;30100;140100;150100;400100;9020100;9030100;111111;60401301|10001;19500;19501;90001",  // Popori Female Soulless
        "4,1,9|10100;400100;9020100;9030100;60401301|10003;19500;19501;91013;91014;91003;91004;91006",  // Popori Female Engineer
        "4,1,10|10100;20100;400100;9020100;9030100;60401301|10002;19500;19501;92001;92002;92003;92004;92005;92006;92007;92008;92009;92010;92011;92012",  // Popori Female Fighter
        "4,1,11|10100;20100;70100;9020100;9030100;60401301|10002;19500;19501;93001;93002;93003;93004;93005;93006;93008",  // Popori Female Assassin
        "4,1,12|10199;60199;140199;160199;9020100;9030100;60401301|10002;19500;19501;94001;94002;94003;94005;94006;94007;94008;94009;94010;94011;94012;94013;94014;94015",  // Popori Female Glaiver
        "5,0,0|10100;20100;9020100;60401301|10001;19601;19602",  // Baraka Male Warrior
        "5,0,1|10100;20100;9020100;260100;60401301|10000;19601;19602",  // Baraka Male Lancer
        "5,0,2|10100;40100;9020100;60401301|10002;19601;19602",  // Baraka Male Slayer
        "5,0,3|10100;20100;290100;9020100;60401301|10000;19601;19602;14070",  // Baraka Male Berserker
        "5,0,4|10100;70100;9020100;60401301|10002;19601;19602",  // Baraka Male Sorcerer
        "5,0,5|10100;60100;9020100;60401301|10002;19601;19602",  // Baraka Male Archer
        "5,0,6|10100;180100;9020100;380100;60401301|10002;19601;19602",  // Baraka Male Priest
        "5,0,7|10100;170100;180100;9020100;60401301|10002;19601;19602",  // Baraka Male Elementalist
    };

    private static readonly Dictionary<(int race, int gender, int cls), (int[] active, int[] passive)> Table = Parse();

    private static Dictionary<(int, int, int), (int[], int[])> Parse()
    {
        var map = new Dictionary<(int, int, int), (int[], int[])>(Rows.Length);
        foreach (var row in Rows)
        {
            var parts = row.Split('|');
            var key = parts[0].Split(',');
            map[(int.Parse(key[0]), int.Parse(key[1]), int.Parse(key[2]))] = (Ids(parts[1]), Ids(parts[2]));
        }
        return map;
    }

    private static int[] Ids(string list) =>
        list.Length == 0 ? Array.Empty<int>()
                         : list.Split(';').Select(int.Parse).ToArray();

    /// <summary>Number of race/gender/class combinations the sheet covers.</summary>
    public static int Count => Table.Count;

    /// <summary>
    /// The default skills for one character, or false when the sheet has no row for that
    /// combination. The real Arbiter's map lookup simply finds nothing in that case and inserts
    /// no skills, so the caller must clear the blob's skill regions rather than leave whatever
    /// the template carried — see <see cref="StarterBlob.ApplyDefaultSkills"/>.
    /// </summary>
    public static bool TryGet(int race, int gender, int cls, out int[] active, out int[] passive)
    {
        if (Table.TryGetValue((race, gender, cls), out var v)) { active = v.active; passive = v.passive; return true; }
        active = Array.Empty<int>();
        passive = Array.Empty<int>();
        return false;
    }
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
///   192  u32   race                           (4)
///   196  u32   gender                         (1)
///   200  u32   class                          (12)
///   208  u32   hp        100000 in the template, 1915 after the first save — runtime, left alone
///   216  u32   mp        100000 in the template
///   220  f32   x                              (16260)
///   224  f32   y                              (1253)
///   228  f32   z                              (-4410)
///   236  u32   zone                           (5; 9827 after the character walked into Velika)
///   288  8 B   appearance                     (65 01 07 04 0E 0E 04 00)
///   296  u32   appearance2                    (100)
///   304  u32   NOT ours                       (0xFFFFF334; AS_ENTER_WORLD copies it to [72])
///   312  32 B  details                        (creator sliders)
///   344  64 B  shape                          (creator sliders)
/// </code>
///
/// The seven identity fields (192/196/200/288/296/312/344) are exactly the values
/// C_CREATE_USER carried for "Test" — verified field by field against
/// <c>cap_newchar_client.log</c> packet 35, so a blob built from that packet's request must
/// come back byte-identical to this template. Leaving them at the template's values is what
/// made every new character spawn as an Elin valkyrie regardless of what the player picked.
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

    // --- identity block (T12) ---
    public const int RaceOffset = 192;
    public const int GenderOffset = 196;
    public const int ClassOffset = 200;
    public const int AppearanceOffset = 288;   public const int AppearanceSize = 8;
    public const int Appearance2Offset = 296;
    public const int DetailsOffset = 312;      public const int DetailsSize = 32;
    public const int ShapeOffset = 344;        public const int ShapeSize = 64;
    /// <summary>
    /// Between <see cref="Appearance2Offset"/> and <see cref="DetailsOffset"/> and NOT ours:
    /// <c>WorldEntry</c> copies this u32 into <c>AS_ENTER_WORLD</c> payload[72]. Listed as a
    /// constant only so the tests can assert we leave it alone.
    /// </summary>
    public const int EnterWorldParamOffset = 304;
    /// <summary>Smallest blob that carries a complete position block (<see cref="ZoneOffset"/> + 4).</summary>
    public const int PositionBlockEnd = ZoneOffset + 4;

    // --- default skills (T18) ---
    // The two skill arrays inside the blob, both proven from BOTH sides of the wire:
    //   * the real Arbiter writes them at UserData + 0x1AE0 / + 0x1C20 in
    //     AccountManager::ExecCreateDefaultSkills (Arb_part_080.c:12875);
    //   * WorldServer reads them back at blob + 0x1AE0 / + 0x1C20 in
    //     TutorialUserEnterWorldContext::UpdateRecvedData (WorldServer.exe.c:1663323) -- the blob
    //     lands at context + 0xA8 (UserEnterWorldContext::SetRecvData, :1661252), and the copies
    //     are from context + 0x1B88 and context + 0x1CC8 -- into User + 0x8320 / + 0x8460, which
    //     is exactly what User::SendMySkillList (:2193096) turns into S_SKILL_LIST.
    // The two regions are adjacent (6880 + 40*8 == 7200) and everything past the last entry is
    // zero in the captured blob, so clearing a whole region touches nothing else.
    // See the DefaultSkillSet class above for the whole story.
    /// <summary>Passive skill slots: 40 x 8 bytes at blob offset 6880 (0x1AE0).</summary>
    public const int PassiveSkillsOffset = 6880;
    public const int PassiveSkillSlots = 40;
    /// <summary>Active skill slots: 500 x 8 bytes at blob offset 7200 (0x1C20).</summary>
    public const int ActiveSkillsOffset = 7200;
    public const int ActiveSkillSlots = 500;
    /// <summary>One slot: [u32 skillId][u8 flag = 0][3 bytes padding], all zero when unused.</summary>
    public const int SkillEntrySize = 8;

    /// <summary>
    /// Read zone and x/y/z out of a world blob. READ-ONLY: the blob is WorldServer's opaque
    /// struct and is never modified here. False when the buffer is too short to hold the
    /// position block, in which case the outputs are meaningless and must be ignored.
    /// </summary>
    public static bool TryReadPosition(byte[] blob, out int zone, out float x, out float y, out float z)
    {
        zone = 0; x = y = z = 0f;
        if (blob == null || blob.Length < PositionBlockEnd) return false;
        x = BitConverter.ToSingle(blob, XOffset);
        y = BitConverter.ToSingle(blob, YOffset);
        z = BitConverter.ToSingle(blob, ZOffset);
        zone = BitConverter.ToInt32(blob, ZoneOffset);
        return true;
    }

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
    /// Copy the template and patch in the per-character fields: identity (race/gender/class and
    /// the three creator blocks), playerId, name, and the start position. Pure — every other
    /// byte of the 15312 is left exactly as the real ArbiterServer sent it, including the u32 at
    /// <see cref="EnterWorldParamOffset"/>.
    /// </summary>
    public static byte[] Build(byte[] template, int playerId, string name, CharacterIdentity identity,
                               int zone, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(identity);
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

        // Identity. Without this every character wears the template's body: the capture is an
        // Elin (race 4) female (gender 1) valkyrie (class 12), so a human warrior spawned as one.
        BitConverter.TryWriteBytes(blob.AsSpan(RaceOffset, 4), identity.Race);
        BitConverter.TryWriteBytes(blob.AsSpan(GenderOffset, 4), identity.Gender);
        BitConverter.TryWriteBytes(blob.AsSpan(ClassOffset, 4), identity.Class);
        BitConverter.TryWriteBytes(blob.AsSpan(Appearance2Offset, 4), identity.Appearance2);
        PatchBlock(blob, AppearanceOffset, AppearanceSize, identity.Appearance);
        PatchBlock(blob, DetailsOffset, DetailsSize, identity.Details);
        PatchBlock(blob, ShapeOffset, ShapeSize, identity.Shape);

        BitConverter.TryWriteBytes(blob.AsSpan(XOffset, 4), x);
        BitConverter.TryWriteBytes(blob.AsSpan(YOffset, 4), y);
        BitConverter.TryWriteBytes(blob.AsSpan(ZOffset, 4), z);
        BitConverter.TryWriteBytes(blob.AsSpan(ZoneOffset, 4), zone);

        // The class's starting skills. Without this every character keeps the template's, and
        // the template is an Elin valkyrie: a warrior's own skills never reach S_SKILL_LIST and
        // the client draws his whole tree as not-yet-learned. See status/SKILLS.md.
        ApplyDefaultSkills(blob, identity.Race, identity.Gender, identity.Class);

        return blob;
    }

    /// <summary>
    /// Overwrite a fixed-size window with <paramref name="src"/>, zero-filling the remainder.
    /// The parser already pads every block to its struct size, so a short source only happens on
    /// a malformed request; zero-filling keeps the template's Elin sliders from showing through
    /// the gap, and never throws in the middle of character creation.
    /// </summary>
    private static void PatchBlock(byte[] blob, int offset, int size, byte[]? src)
    {
        Array.Clear(blob, offset, size);
        if (src != null && src.Length > 0)
            Array.Copy(src, 0, blob, offset, Math.Min(src.Length, size));
    }


    /// <summary>
    /// Overwrite the blob's two skill regions with the class's defaults from
    /// <see cref="DefaultSkillSet"/>. Returns false when the sheet has no row for this
    /// race/gender/class, in which case BOTH regions are cleared.
    ///
    /// <para>Clearing is deliberate: the template is a Popori-female Glaiver's blob, so leaving
    /// its arrays alone would hand an unknown combination the valkyrie's skills — which is the
    /// exact bug T18 fixes. The real Arbiter's lookup simply finds no row and inserts nothing, so
    /// an empty list is what it would have produced too.</para>
    ///
    /// <para>The entry layout matches the Arbiter's writer byte for byte:
    /// <c>*(u32*)(p - 4) = skillId; *p = 0; p += 8</c> — the id, a zero flag byte, and two
    /// padding bytes that stay zero.</para>
    /// </summary>
    public static bool ApplyDefaultSkills(byte[] blob, int race, int gender, int cls)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < ActiveSkillsOffset + ActiveSkillSlots * SkillEntrySize)
            throw new ArgumentException($"blob is {blob.Length} bytes, too short to hold the skill regions", nameof(blob));

        bool found = DefaultSkillSet.TryGet(race, gender, cls, out var active, out var passive);
        WriteSkillRegion(blob, PassiveSkillsOffset, PassiveSkillSlots, passive);
        WriteSkillRegion(blob, ActiveSkillsOffset, ActiveSkillSlots, active);
        return found;
    }

    /// <summary>Zero a skill region, then write one 8-byte entry per id. Ids past the slot count are dropped.</summary>
    private static void WriteSkillRegion(byte[] blob, int offset, int slots, int[] ids)
    {
        Array.Clear(blob, offset, slots * SkillEntrySize);
        int n = Math.Min(ids.Length, slots);
        for (int i = 0; i < n; i++)
            BitConverter.TryWriteBytes(blob.AsSpan(offset + i * SkillEntrySize, 4), ids[i]);
    }

    /// <summary>Read a skill region back (used by tests and logging); stops at the first empty slot.</summary>
    public static int[] ReadSkillRegion(byte[] blob, int offset, int slots)
    {
        var ids = new List<int>();
        for (int i = 0; i < slots; i++)
        {
            int id = BitConverter.ToInt32(blob, offset + i * SkillEntrySize);
            if (id == 0) break;
            ids.Add(id);
        }
        return ids.ToArray();
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
  exp INTEGER NOT NULL DEFAULT 0,
  template_id INTEGER NOT NULL,
  zone INTEGER NOT NULL DEFAULT 7005,
  x REAL NOT NULL DEFAULT -449, y REAL NOT NULL DEFAULT 6239, z REAL NOT NULL DEFAULT 1956,
  appearance BLOB NOT NULL, details BLOB NOT NULL, shape BLOB NOT NULL,
  weapon INTEGER NOT NULL DEFAULT 0, body INTEGER NOT NULL DEFAULT 0,
  hand INTEGER NOT NULL DEFAULT 0, feet INTEGER NOT NULL DEFAULT 0,
  position INTEGER NOT NULL DEFAULT 1,
  last_logout TEXT,
  world_blob BLOB,
  -- T21: the system return point. The real Arbiter keeps this in dbo.spUpdateSysReturnLoc and
  -- restores it when WorldServer answers AS_ENTER_WORLD with SA_ENTER_WORLD_FAIL -- see
  -- status/ENTER-WORLD-FALLBACK.md. return_zone 0 means no return point (the real Arbiter
  -- tests 0 < User+0x1a8).
  return_zone INTEGER NOT NULL DEFAULT 0,
  return_x REAL NOT NULL DEFAULT 0, return_y REAL NOT NULL DEFAULT 0, return_z REAL NOT NULL DEFAULT 0,
  dungeon_id INTEGER NOT NULL DEFAULT 0,
  instance_pdid INTEGER NOT NULL DEFAULT 0,
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

-- T30: friend groups. The client owns the index (2..10, validated by User::UpdateFriendGroup);
-- group 1 is the implicit ungrouped bucket and is never a row here - it only ever appears as
-- friends.group_id. Index 2 is the sample group User::ProvideSampleFriendGroup seeds once per
-- character. status/FRIENDS.md.
CREATE TABLE IF NOT EXISTS friend_groups (
  character_id INTEGER NOT NULL REFERENCES characters(id),
  group_index  INTEGER NOT NULL,
  name         TEXT    NOT NULL DEFAULT '',
  updated_at   TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (character_id, group_index)
);

CREATE TABLE IF NOT EXISTS quests (
  -- id is the questDbId the Arbiter returns on an INSERT write (sqlType 22) and that World then
  -- carries in every later write for the quest (DBStartQuestContext::SetQuestDbId). It has to be
  -- persistent, so it is the row id, not a counter: a process counter would hand World a
  -- different id for the same quest after a restart.
  id         INTEGER PRIMARY KEY,
  owner_id   INTEGER NOT NULL REFERENCES characters(id),
  quest_id   INTEGER NOT NULL,
  status     INTEGER NOT NULL,          -- record+8:  1 = in progress, 2 = complete
  step       INTEGER NOT NULL,          -- record+12
  record     BLOB    NOT NULL,          -- the raw 80-byte QuestData, last write wins
  updated_at TEXT    NOT NULL DEFAULT (datetime('now')),
  UNIQUE (owner_id, quest_id)
);
CREATE INDEX IF NOT EXISTS ix_quests_owner ON quests(owner_id);

-- T22: achievements. `payload` is the whole SDB_UPDATE_USER_ACHIEVEMENT (0x27FA) body World
-- sends on every zone change and logout - 35 lists plus the 1184-byte Data blob - kept
-- verbatim, last write wins, and taken apart again when DBS_LOAD_USER_ACHIEVEMENT (0x27F9) is
-- rebuilt. Storing the message rather than 35 parsed tables is deliberate: the lists are
-- opaque counter vectors World owns, and a parsed schema would have to be re-derived every
-- time World's build changes. status/ACHIEVEMENTS.md.
CREATE TABLE IF NOT EXISTS achievements (
  owner_id   INTEGER PRIMARY KEY REFERENCES characters(id),
  payload    BLOB NOT NULL,
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- The accomplished list is NOT in the 0x27FA save: it is built one record at a time by
-- SDB_ACCOMPLISH_USER_ACHIEVEMENT (0x2802), and the real Arbiter keeps the FIRST record for an
-- achievement and rejects every later one - proven across two capture sessions, where a
-- re-submitted 5991 came back as an empty 0x2803 and the load still carried the original
-- timestamp. Hence INSERT OR IGNORE on (owner, achievement).
CREATE TABLE IF NOT EXISTS achievements_done (
  owner_id       INTEGER NOT NULL REFERENCES characters(id),
  achievement_id INTEGER NOT NULL,
  record         BLOB    NOT NULL,   -- the raw 24-byte record, first write wins
  created_at     TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_id, achievement_id)
);
CREATE INDEX IF NOT EXISTS ix_achievements_done_owner ON achievements_done(owner_id);

-- T25: dungeon cool times. `record` is the raw 52-byte DungeonCoolTimeElem World sends in
-- SA_UPDATE_DUNGEON_COOLTIME (0x13B6), kept verbatim and handed straight back in
-- DBS_LOAD_DUNGEON_COOL_TIME (0x2868) list 0 and in the AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD
-- (0x148D) pushes. `clear_count` is the separate scalar SA_UPDATE_DUNGEON_CLEAR_COUNT (0x13B7)
-- carries; it is stored but not yet served, because no capture has ever shown a non-empty
-- ClearCountList and its element layout is therefore unverified. status/DUNGEON-COOLTIME.md.
CREATE TABLE IF NOT EXISTS dungeon_cooldowns (
  owner_id    INTEGER NOT NULL REFERENCES characters(id),
  dungeon_id  INTEGER NOT NULL,
  record      BLOB,
  clear_count INTEGER NOT NULL DEFAULT 0,
  updated_at  TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_id, dungeon_id)
);
CREATE INDEX IF NOT EXISTS ix_dungeon_cooldowns_owner ON dungeon_cooldowns(owner_id);

-- T26: reputations. `record` is the raw 52-byte ReputationData World sends in
-- SDB_UPDATE_REPUTATION_INFO (0x2891); the real Arbiter's ReputationDataManager stores that
-- struct verbatim in a std::map keyed on record+4 and GetAllReputationData copies it straight
-- back out, so keeping the bytes is exactly what it does. status/REPUTATION-FATIGABILITY.md.
CREATE TABLE IF NOT EXISTS reputations (
  owner_id      INTEGER NOT NULL REFERENCES characters(id),
  reputation_id INTEGER NOT NULL,
  record        BLOB    NOT NULL,
  updated_at    TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_id, reputation_id)
);
CREATE INDEX IF NOT EXISTS ix_reputations_owner ON reputations(owner_id);

-- T26: fatigability. PER ACCOUNT, not per character: dob and Test share account 1 and the
-- point they are served is the same running total, proven three times across the captures
-- (2505 + 15 = 2520; 2520 + 135 + 0 = 2655; 2655 + 270 + 15 = 2940 - the second and third chains
-- cross both characters). SDB_UPDATE_FATIGABILITY_POINT (0x2910) carries a DELTA, not a total.
CREATE TABLE IF NOT EXISTS fatigability (
  account_id INTEGER PRIMARY KEY REFERENCES accounts(id),
  cur_point  INTEGER NOT NULL DEFAULT 0,
  updated_at BLOB,            -- the raw 16-byte TIMESTAMP of the last update, null = never
  tail       INTEGER NOT NULL DEFAULT 0
);

-- T22: the two other per-character login loads the captures pin completely.
-- Tutorial tips: SDB_ADD_TUTORIAL_SIMPLE_TIP (0x286E) adds one id at a time and
-- DBS_LOAD_TUTORIAL_SIMPLE_TIP (0x2873) serves them back in the order they were added.
CREATE TABLE IF NOT EXISTS tutorial_tips (
  owner_id   INTEGER NOT NULL REFERENCES characters(id),
  tip_id     INTEGER NOT NULL,
  created_at TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_id, tip_id)
);
CREATE INDEX IF NOT EXISTS ix_tutorial_tips_owner ON tutorial_tips(owner_id);

-- Seren guide: SDB_UPDATE_SEREN_GUIDE_INFO (0x2944) sets one (type, id) pair and
-- DBS_INIT_SEREN_GUIDE_INFO (0x2943) serves a fixed six-row table back.
CREATE TABLE IF NOT EXISTS seren_guide (
  owner_id   INTEGER NOT NULL REFERENCES characters(id),
  seren_type INTEGER NOT NULL,
  seren_id   INTEGER NOT NULL,
  updated_at TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_id, seren_type)
);

-- Monotonic id sequences the DB-proxy layer hands to WorldServer. Not SQLite rowids: the
-- values leave the process (DBS_ITEM_SINGLE returns them to World, which keys its in-memory
-- items by them), so they must keep climbing across restarts even with no table behind them.
CREATE TABLE IF NOT EXISTS counters (
  name  TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);

-- Opaque client option blobs, saved by the client and replayed to it at login — T19.
-- The Arbiter stores these verbatim and never parses them: they are the client's own
-- serialised UI state (keybinds, hotbars, chat tabs, window layout). Two scopes, exactly
-- as the real ArbiterServer has them:
--   User::SaveClientSetting    (Arb_part_029.c:18757) -> spSaveClientSettingForUser,    per character
--   Account::SaveClientSetting (Arb_part_065.c:9779)  -> spSaveClientSettingForAccount, per account
-- Both reject a blob of 0 bytes or more than 9000; see CharacterStore.MaxClientSettingBytes.
CREATE TABLE IF NOT EXISTS client_settings (
  character_id INTEGER PRIMARY KEY,
  blob         BLOB NOT NULL,
  updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TABLE IF NOT EXISTS account_settings (
  account_id INTEGER PRIMARY KEY,
  blob       BLOB NOT NULL,
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
-- ---- Guilds (T39). Schema from status/GUILD-DESIGN.md section 3.3, which derives it
-- column-by-column from the real Arbiter's spLoadAllGuild / spLoadAllGuildMemberData /
-- spLoadGuildGroup binds. Two deliberate deviations from the original, both documented there:
-- timestamps are unix seconds rather than tagTIMESTAMP_STRUCT (GuildPackets.BuildTimestamp
-- converts at the wire edge), and guild_members is keyed by user_db_id alone because
-- spLeaveGuildMember takes only a userDbId - a character is in at most one guild.
--
-- REFERENCES clauses are documentation, not enforcement: this DB never sets
-- PRAGMA foreign_keys, exactly like the friends/quests tables above, so DeleteGuild deletes
-- its children explicitly.
--
-- Not columns, on purpose: member_count, account_count, max_account_count, guild_size,
-- policy_point, quest points. The real Arbiter computes every one of them at send time
-- (max accounts = config + add_account_limit; guild_size from level), and a stored copy
-- would drift.
CREATE TABLE IF NOT EXISTS guilds (
  guild_id              INTEGER PRIMARY KEY AUTOINCREMENT,
  name                  TEXT    NOT NULL UNIQUE COLLATE NOCASE,
  chief_db_id           INTEGER NOT NULL,
  create_date           INTEGER NOT NULL DEFAULT 0,
  level                 INTEGER NOT NULL DEFAULT 1,
  exp                   INTEGER NOT NULL DEFAULT 0,
  point                 INTEGER NOT NULL DEFAULT 0,
  money                 INTEGER NOT NULL DEFAULT 0,
  announce              TEXT    NOT NULL DEFAULT '',
  recommendation_point  INTEGER NOT NULL DEFAULT 0,
  title                 TEXT    NOT NULL DEFAULT '',
  logo                  BLOB,
  logo_id               INTEGER NOT NULL DEFAULT 0,
  promotion             TEXT    NOT NULL DEFAULT '',
  war_acceptable        INTEGER NOT NULL DEFAULT 0,
  war_toggle_time       INTEGER NOT NULL DEFAULT 0,
  general_coin          INTEGER NOT NULL DEFAULT 0,
  forever_emblem_id     INTEGER NOT NULL DEFAULT 0,
  emblem_id             INTEGER NOT NULL DEFAULT 0,
  preference            INTEGER NOT NULL DEFAULT 0,
  join_min_level        INTEGER NOT NULL DEFAULT 1,
  join_max_level        INTEGER NOT NULL DEFAULT 70,
  join_type             INTEGER NOT NULL DEFAULT 1,
  last_week_play_time   INTEGER NOT NULL DEFAULT 0,
  this_week_play_time   INTEGER NOT NULL DEFAULT 0,
  last_incentive_time   INTEGER NOT NULL DEFAULT 0,
  add_account_limit     INTEGER NOT NULL DEFAULT 0,
  created_at            TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- spLoadAllGuildMemberData's 17 columns. name/level/race/class/gender are DUPLICATED from
-- characters on purpose: the real Arbiter keeps them in GuildMemberData so an OFFLINE member
-- still renders in S_GUILD_MEMBER_LIST. state (online/offline) and can_guild_war are runtime
-- only and are not stored - the load loop forces state to 2 (offline).
CREATE TABLE IF NOT EXISTS guild_members (
  user_db_id            INTEGER PRIMARY KEY REFERENCES characters(id),
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  name                  TEXT    NOT NULL,
  world_id              INTEGER NOT NULL DEFAULT 0,
  guard_id              INTEGER NOT NULL DEFAULT 0,
  section_id            INTEGER NOT NULL DEFAULT 0,
  guild_group_id        INTEGER NOT NULL DEFAULT 2,
  user_level            INTEGER NOT NULL DEFAULT 1,
  race                  INTEGER NOT NULL DEFAULT 0,
  user_class            INTEGER NOT NULL DEFAULT 0,
  gender                INTEGER NOT NULL DEFAULT 0,
  introduce             TEXT    NOT NULL DEFAULT '',
  last_logout_time      INTEGER NOT NULL DEFAULT 0,
  account_id            INTEGER NOT NULL DEFAULT 0,
  guild_join_date       INTEGER NOT NULL DEFAULT 0,
  weekly_contribution   INTEGER NOT NULL DEFAULT 0,
  total_contribution    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_guild_members_guild ON guild_members(guild_id);

-- GuildGroupData = {i32 GuildGroupId, wchar Name[16], i32 Authority}. Authority is a bitmask;
-- Guild::HaveGuildAuthorityWithLock tests (wanted & group.authority) != 0, and the chief
-- bypasses it entirely.
CREATE TABLE IF NOT EXISTS guild_groups (
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  guild_group_id        INTEGER NOT NULL,
  name                  TEXT    NOT NULL,
  authority             INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, guild_group_id)
);

-- spInsertGuildApply / spLoadGuildApplyList / spDeleteGuildApply*
CREATE TABLE IF NOT EXISTS guild_applies (
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  user_db_id            INTEGER NOT NULL REFERENCES characters(id),
  join_msg              TEXT    NOT NULL DEFAULT '',
  applied_at            INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, user_db_id)
);

-- spAddInviteUserToGuild / spLoadInviteUserToGuild / spDeleteInviteUserToGuild*
CREATE TABLE IF NOT EXISTS guild_invites (
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  user_db_id            INTEGER NOT NULL REFERENCES characters(id),
  invitor_db_id         INTEGER NOT NULL DEFAULT 0,
  invited_at            INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, user_db_id)
);

-- spCreateGuildLog / spLoadGuildLog. The real proc binds 13 parameters; these are the ones
-- S_GUILD_HISTORY can actually render (log_time, action_type, actor_name, detail) plus the
-- ids worth keeping.
CREATE TABLE IF NOT EXISTS guild_log (
  id                    INTEGER PRIMARY KEY AUTOINCREMENT,
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  action_type           INTEGER NOT NULL DEFAULT 0,
  log_time              INTEGER NOT NULL DEFAULT 0,
  actor_db_id           INTEGER NOT NULL DEFAULT 0,
  actor_name            TEXT    NOT NULL DEFAULT '',
  target_name           TEXT    NOT NULL DEFAULT '',
  param_int             INTEGER NOT NULL DEFAULT 0,
  param_i64             INTEGER NOT NULL DEFAULT 0,
  detail                TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_guild_log_guild_time ON guild_log(guild_id, log_time DESC, id DESC);

-- spLoadGuildPerkList returns {int perkId, tinyint, tinyint}. Stored so DBS_INIT_GUILD_PERK_LIST
-- has rows to build from; nothing reads them yet.
CREATE TABLE IF NOT EXISTS guild_perks (
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  perk_id               INTEGER NOT NULL,
  flag_a                INTEGER NOT NULL DEFAULT 0,
  flag_b                INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, perk_id)
);
");
        // CREATE TABLE IF NOT EXISTS does nothing to a DB that already has `characters`, so
        // columns added later need their own idempotent step. terasharp.db predates `exp`.
        AddColumnIfMissing("characters", "exp", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_zone", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_x", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_y", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_z", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "dungeon_id", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "instance_pdid", "INTEGER NOT NULL DEFAULT 0");
        // T30: friends carry a group and the requester's greeting; blocks carry a note; the
        // character carries the profile message the friend panel shows and the once-only flag
        // behind dbo.spIsProvideSampleFriendGroup.
        AddColumnIfMissing("friends", "group_id", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing("friends", "memo", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("blocks", "memo", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("characters", "profile_message", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("characters", "sample_group_provided", "INTEGER NOT NULL DEFAULT 0");
        // T32: GM level, per account (see AccountRecord.AdminLevel).
        AddColumnIfMissing("accounts", "admin_level", "INTEGER NOT NULL DEFAULT 0");
        // T39: when this character last left a guild (User+0x3c48), for Guild::CanRejoinGuild.
        AddColumnIfMissing("characters", "guild_leave_time", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>ALTER TABLE ADD COLUMN, but a no-op when the column is already there.</summary>
    private void AddColumnIfMissing(string table, string column, string decl)
    {
        using (var probe = _db.CreateCommand())
        {
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
            probe.Parameters.AddWithValue("$c", column);
            if ((long)probe.ExecuteScalar()! > 0) return;
        }
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
        _log.LogInformation("Migrated {Table}: added column {Column}", table, column);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }


    // ---- Client settings (T19) ----

    /// <summary>
    /// Largest blob either scope will store. The real Arbiter's <c>User::SaveClientSetting</c>
    /// (Arb_part_029.c:18757) and <c>Account::SaveClientSetting</c> (Arb_part_065.c:9779) both
    /// open with <c>if (len == 0 || 9000 &lt; len) { log; return; }</c> — a save outside that range
    /// is dropped and the previously stored blob is left alone. The in-memory buffer behind it is
    /// declared <c>unsigned char(&amp;)[9000]</c> in the S_LOAD writer's signature.
    /// </summary>
    public const int MaxClientSettingBytes = 9000;

    /// <summary>
    /// Store one character's client settings, replacing whatever was there. Returns false — and
    /// changes nothing — for a blob the real Arbiter would have refused (empty, or over
    /// <see cref="MaxClientSettingBytes"/>). The client saves several times per session, so this
    /// is an upsert, not an insert.
    /// </summary>
    public bool SaveClientSetting(long characterId, byte[] blob)
    {
        if (blob == null || blob.Length == 0 || blob.Length > MaxClientSettingBytes)
        {
            _log.LogWarning("SaveClientSetting: refusing a {Len}-byte blob for character {Id} (the real Arbiter drops 0 and >{Max})",
                blob?.Length ?? -1, characterId, MaxClientSettingBytes);
            return false;
        }
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO client_settings(character_id, blob, updated_at) VALUES($c, $b, datetime('now'))
ON CONFLICT(character_id) DO UPDATE SET blob = excluded.blob, updated_at = excluded.updated_at";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$b", blob);
            cmd.ExecuteNonQuery();
        }
        _log.LogDebug("Saved {Len} bytes of client settings for character {Id}", blob.Length, characterId);
        return true;
    }

    /// <summary>One character's stored client settings, or null when nothing has been saved yet.</summary>
    public byte[]? LoadClientSetting(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT blob FROM client_settings WHERE character_id = $c";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var blob = (byte[])r["blob"];
            return blob.Length == 0 ? null : blob;
        }
    }

    /// <summary>Account-scope equivalent of <see cref="SaveClientSetting"/>.</summary>
    public bool SaveAccountSetting(long accountId, byte[] blob)
    {
        if (blob == null || blob.Length == 0 || blob.Length > MaxClientSettingBytes)
        {
            _log.LogWarning("SaveAccountSetting: refusing a {Len}-byte blob for account {Id} (the real Arbiter drops 0 and >{Max})",
                blob?.Length ?? -1, accountId, MaxClientSettingBytes);
            return false;
        }
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO account_settings(account_id, blob, updated_at) VALUES($a, $b, datetime('now'))
ON CONFLICT(account_id) DO UPDATE SET blob = excluded.blob, updated_at = excluded.updated_at";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$b", blob);
            cmd.ExecuteNonQuery();
        }
        _log.LogDebug("Saved {Len} bytes of account settings for account {Id}", blob.Length, accountId);
        return true;
    }

    /// <summary>One account's stored client settings, or null when nothing has been saved yet.</summary>
    public byte[]? LoadAccountSetting(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT blob FROM account_settings WHERE account_id = $a";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var blob = (byte[])r["blob"];
            return blob.Length == 0 ? null : blob;
        }
    }

    // ---- Accounts ----

    /// <summary>The account row for a name, or null when it does not exist yet.</summary>
    public AccountRecord? GetAccount(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, admin_level FROM accounts WHERE name = $n";
            cmd.Parameters.AddWithValue("$n", name);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new AccountRecord { Id = r.GetInt64(0), Name = r.GetString(1), AdminLevel = r.GetInt32(2) };
        }
    }

    /// <summary>The account row by id, or null.</summary>
    public AccountRecord? GetAccountById(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, admin_level FROM accounts WHERE id = $i";
            cmd.Parameters.AddWithValue("$i", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new AccountRecord { Id = r.GetInt64(0), Name = r.GetString(1), AdminLevel = r.GetInt32(2) };
        }
    }

    /// <summary>
    /// The GM level for an account - what dbo.spUpdateUserAdminLevel does on the real server,
    /// except keyed on the account rather than the character (status/GM-DESIGN.md section 3).
    /// Negative levels are clamped to 0.
    /// </summary>
    public bool SetAdminLevel(long accountId, int level)
    {
        if (level < 0) level = 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET admin_level = $l WHERE id = $i";
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$i", accountId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The stored GM level, 0 when the account is unknown.</summary>
    public int GetAdminLevel(long accountId) => GetAccountById(accountId)?.AdminLevel ?? 0;

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

    /// <summary>
    /// Store the WorldServer state struct after SDB_UPDATE_USER_DATA, and mirror the position
    /// out of it onto the row (T6).
    ///
    /// The blob is opaque and is <b>read only</b> here — nothing is written back into it. The
    /// row copy exists because the character-select screen and any future real
    /// <c>AS_ENTER_WORLD</c> builder need zone/x/y/z as columns, and World only ever hands us
    /// those numbers inside the blob:
    ///   x/y/z = f32 at <see cref="StarterBlob.XOffset"/>/224/228, zone = u32 at
    ///   <see cref="StarterBlob.ZoneOffset"/>. Offset 208 is HP, not zone (verified in
    ///   cap_newchar.log: 236 is 5 on the starting island and 9827 in Velika, while 208 tracks
    ///   damage).
    /// A short buffer (never seen on the wire; 0x27CB always carries the full 15312 bytes)
    /// still updates the blob, it just leaves the position columns alone.
    /// </summary>
    public void SaveWorldBlob(int characterId, byte[] blob)
    {
        lock (_lock)
        {
            bool hasPos = StarterBlob.TryReadPosition(blob, out int zone, out float x, out float y, out float z);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = hasPos
                ? "UPDATE characters SET world_blob = $b, zone = $zone, x = $x, y = $y, z = $z, last_logout = datetime('now') WHERE id = $id"
                : "UPDATE characters SET world_blob = $b, last_logout = datetime('now') WHERE id = $id";
            cmd.Parameters.AddWithValue("$b", blob);
            cmd.Parameters.AddWithValue("$id", characterId);
            if (hasPos)
            {
                cmd.Parameters.AddWithValue("$zone", zone);
                cmd.Parameters.AddWithValue("$x", x);
                cmd.Parameters.AddWithValue("$y", y);
                cmd.Parameters.AddWithValue("$z", z);
            }
            int n = cmd.ExecuteNonQuery();
            if (n != 1) { _log.LogWarning("SaveWorldBlob: character {Id} not found", characterId); return; }

            if (hasPos)
                _log.LogInformation("Saved world blob ({Len} bytes) for character {Id}; zone {Zone} ({X:F1}, {Y:F1}, {Z:F1})",
                    blob.Length, characterId, zone, x, y, z);
            else
                _log.LogInformation("Saved world blob ({Len} bytes) for character {Id} (too short for a position)",
                    blob.Length, characterId);
        }
    }

    /// <summary>
    /// Level/exp from SDB_UPDATE_EXP_LEVEL (0x273B). World sends a level only when it changed
    /// (the field is 0 on an exp-only update, matching the real Arbiter's
    /// <c>User::UpdateUserExpAndRestBonusPoint</c> vs <c>User::UpdateUserLevel</c> split), so
    /// <paramref name="level"/> is null on those and the stored level is left alone.
    /// Returns true when a row was updated — that is the ok byte the 0x273C reply carries.
    /// </summary>
    public bool UpdateLevelAndExp(int characterId, int? level, long exp)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = level.HasValue
                ? "UPDATE characters SET level = $l, exp = $e WHERE id = $id"
                : "UPDATE characters SET exp = $e WHERE id = $id";
            if (level.HasValue) cmd.Parameters.AddWithValue("$l", level.Value);
            cmd.Parameters.AddWithValue("$e", exp);
            cmd.Parameters.AddWithValue("$id", characterId);
            bool ok = cmd.ExecuteNonQuery() == 1;
            if (!ok) _log.LogWarning("UpdateLevelAndExp: character {Id} not found", characterId);
            else if (level.HasValue) _log.LogInformation("Character {Id} reached level {L} (exp {E})", characterId, level.Value, exp);
            return ok;
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

    // ---- Dungeon return point (T21) ----
    //
    // The real Arbiter keeps five ints on the User (0x1a8 continent, 0x1ac channelInstanceId,
    // 0x1b0/4/8 x/y/z as INTS) and writes them through dbo.spUpdateSysReturnLoc, so they survive
    // a restart. Handler_SA_RESPONSE_ENTER_DUNGEON (ArbiterServer.exe.c, scope tracer
    // "bool __cdecl Handler_SA_RESPONSE_ENTER_DUNGEON(...)") calls
    // User::UpdateSysReturnLoc(returnContinent, currentChannelInstanceId, (int)x, (int)y, (int)z)
    // with the return fields the DungeonEnterContext carries, and User::EnterWorldFail reads them
    // back when WorldServer refuses the enter. Layout and evidence: status/ENTER-WORLD-FALLBACK.md.
    //
    // We keep the same five values plus the dungeon id, on the character row.

    /// <summary>
    /// Where a character goes when WorldServer refuses to let it into its saved instance.
    /// DungeonId is DungeonEnterContext+0 (the instance entered), InstancePdId is
    /// DungeonEnterContext+140 (WorldServer's handle for it), Zone/X/Y/Z are
    /// DungeonEnterContext+56/44/48/52 (the continent and position to fall back to).
    /// </summary>
    public sealed record DungeonReturnPoint(int DungeonId, int InstancePdId, int Zone, float X, float Y, float Z);

    /// <summary>
    /// Record the return point a dungeon entry carries (SA_REQUEST_ENTER_DUNGEON 0x13BE, and again
    /// on the 0x13C0 response). The real Arbiter truncates the coordinates to int on the way in and
    /// widens them back to float on the way out, so we do the same - otherwise a re-sent
    /// AS_ENTER_WORLD would not be byte-identical to the real one.
    /// Returns false when the character row is missing.
    /// </summary>
    public bool SaveDungeonReturn(int characterId, int dungeonId, int returnZone, float x, float y, float z)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET dungeon_id = $d, return_zone = $rz, "
                            + "return_x = $rx, return_y = $ry, return_z = $rzz WHERE id = $id";
            cmd.Parameters.AddWithValue("$d", dungeonId);
            cmd.Parameters.AddWithValue("$rz", returnZone);
            cmd.Parameters.AddWithValue("$rx", (float)(int)x);
            cmd.Parameters.AddWithValue("$ry", (float)(int)y);
            cmd.Parameters.AddWithValue("$rzz", (float)(int)z);
            cmd.Parameters.AddWithValue("$id", characterId);
            bool ok = cmd.ExecuteNonQuery() == 1;
            if (!ok) _log.LogWarning("SaveDungeonReturn: character {Id} not found", characterId);
            else _log.LogInformation(
                "Character {Id} entered dungeon {Dg}; return point zone {Z} ({X:F0}, {Y:F0}, {Zz:F0})",
                characterId, dungeonId, returnZone, x, y, z);
            return ok;
        }
    }

    /// <summary>
    /// The ChannelInstanceId WorldServer allocated for the instance, from the 0x13C0 response.
    /// It is what AS_ENTER_WORLD [52] must carry on a relog into that instance (capture
    /// 0x0AF00001); 0 clears it.
    /// </summary>
    public bool SaveInstancePdId(int characterId, int pdId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET instance_pdid = $p WHERE id = $id";
            cmd.Parameters.AddWithValue("$p", pdId);
            cmd.Parameters.AddWithValue("$id", characterId);
            bool ok = cmd.ExecuteNonQuery() == 1;
            if (!ok) _log.LogWarning("SaveInstancePdId: character {Id} not found", characterId);
            return ok;
        }
    }

    /// <summary>
    /// User::CleanSysReturnLoc - UpdateSysReturnLoc(0,0,0,0,0). The real Arbiter calls it on a
    /// dungeon response that is not flagged to set a return point, and on a normal zone change.
    /// </summary>
    public bool ClearDungeonReturn(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET dungeon_id = 0, instance_pdid = 0, "
                            + "return_zone = 0, return_x = 0, return_y = 0, return_z = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// The stored return point, or null when there is none. Mirrors the real Arbiter's guard in
    /// User::EnterWorldFail: <c>if (0 &lt; User+0x1a8)</c> - a zone of 0 means "nothing saved",
    /// and the fallback then comes from the continent table instead.
    /// </summary>
    public DungeonReturnPoint? GetDungeonReturn(int characterId)
    {
        var c = GetCharacter(characterId);
        if (c == null || c.ReturnZone <= 0) return null;
        return new DungeonReturnPoint(c.DungeonId, c.InstancePdId, c.ReturnZone, c.ReturnX, c.ReturnY, c.ReturnZ);
    }

    // ---- Quests (T17) ----

    /// <summary>Status value meaning "completed" in the 80-byte record at +8.</summary>
    public const int QuestStatusComplete = 2;

    /// <summary>
    /// Last-write-wins upsert of one quest, keyed (owner, quest). Returns the row id, which is
    /// the <c>questDbId</c> the 0x272F reply carries back on an INSERT write — see
    /// status/QUEST-DESIGN.md. The id is allocated on the first write for a quest and never
    /// changes after, so World's copy stays valid across restarts.
    /// </summary>
    public int UpsertQuest(int ownerId, int questId, int status, int step, byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            int id;
            using (var sel = _db.CreateCommand())
            {
                sel.CommandText = "SELECT id FROM quests WHERE owner_id = $o AND quest_id = $q";
                sel.Parameters.AddWithValue("$o", ownerId);
                sel.Parameters.AddWithValue("$q", questId);
                var existing = sel.ExecuteScalar();
                id = existing is null or DBNull ? 0 : Convert.ToInt32(existing);
            }

            using var cmd = _db.CreateCommand();
            if (id != 0)
            {
                cmd.CommandText =
                    "UPDATE quests SET status = $s, step = $p, record = $r, updated_at = datetime('now') WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
            }
            else
            {
                cmd.CommandText =
                    "INSERT INTO quests(owner_id, quest_id, status, step, record) VALUES($o, $q, $s, $p, $r); " +
                    "SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$o", ownerId);
                cmd.Parameters.AddWithValue("$q", questId);
            }
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$p", step);
            cmd.Parameters.AddWithValue("$r", record);

            if (id != 0) { cmd.ExecuteNonQuery(); return id; }
            id = Convert.ToInt32(cmd.ExecuteScalar()!);
            _log.LogInformation("Quest {Q} started for character {Id} (questDbId {Db})", questId, ownerId, id);
            return id;
        }
    }

    /// <summary>
    /// The raw 80-byte records for a character's quests that are still in progress, oldest row
    /// first. Completed quests are deliberately excluded: nothing in any capture shows where the
    /// 0x272D reply puts them (status/QUEST-DESIGN.md).
    /// </summary>
    public List<byte[]> GetActiveQuestRecords(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<byte[]>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT record FROM quests WHERE owner_id = $o AND status <> $c ORDER BY id";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$c", QuestStatusComplete);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r["record"]);
            return list;
        }
    }

    /// <summary>Quest ids this character has completed — stored, but not served yet.</summary>
    public List<int> GetCompletedQuestIds(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<int>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT quest_id FROM quests WHERE owner_id = $o AND status = $c ORDER BY id";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$c", QuestStatusComplete);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt32(0));
            return list;
        }
    }

    /// <summary>Total quest rows for a character, of any status.</summary>
    public int CountQuests(int ownerId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM quests WHERE owner_id = $o";
            cmd.Parameters.AddWithValue("$o", ownerId);
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    // ---- Achievements (T22) ----

    /// <summary>
    /// Store the raw SDB_UPDATE_USER_ACHIEVEMENT (0x27FA) payload for a character, replacing
    /// whatever was there. World sends the complete set on every zone change and logout, so last
    /// write wins is the whole story. Returns false for an obviously bad payload (too short to
    /// hold the 288-byte header), which is left unstored - the caller still acks the DLM item.
    /// </summary>
    public bool SaveAchievements(int ownerId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < 288)
        {
            _log.LogWarning("SaveAchievements: {Len} B payload for character {Id} is too short to be 0x27FA",
                payload.Length, ownerId);
            return false;
        }
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO achievements (owner_id, payload, updated_at) "
                            + "VALUES ($o, $p, datetime('now')) "
                            + "ON CONFLICT(owner_id) DO UPDATE SET payload = $p, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>The stored 0x27FA payload, or null for a character that has never saved.</summary>
    public byte[]? GetAchievements(int ownerId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM achievements WHERE owner_id = $o";
            cmd.Parameters.AddWithValue("$o", ownerId);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    /// <summary>
    /// Record accomplished achievements, keeping the first record for each and ignoring repeats.
    /// Returns, in request order, only the records that were actually new - that list is exactly
    /// what DBS_ACCOMPLISH_USER_ACHIEVEMENT (0x2803) sends back.
    /// </summary>
    public List<byte[]> AddAccomplishedAchievements(int ownerId, IReadOnlyList<(int Id, byte[] Record)> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var added = new List<byte[]>();
        lock (_lock)
        {
            foreach (var (id, record) in records)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO achievements_done (owner_id, achievement_id, record) "
                                + "VALUES ($o, $a, $r)";
                cmd.Parameters.AddWithValue("$o", ownerId);
                cmd.Parameters.AddWithValue("$a", id);
                cmd.Parameters.AddWithValue("$r", record);
                if (cmd.ExecuteNonQuery() == 1) added.Add(record);
            }
        }
        return added;
    }

    /// <summary>
    /// Every accomplished-achievement record for a character, in the order they were earned -
    /// which is the order the real Arbiter's AccomplishedAchievementList carries them in.
    /// </summary>
    public List<byte[]> GetAccomplishedAchievements(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<byte[]>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT record FROM achievements_done WHERE owner_id = $o ORDER BY rowid";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r["record"]);
            return list;
        }
    }

    // ---- Tutorial tips and the seren guide (T22) ----

    /// <summary>
    /// Record a tutorial tip the character has seen. Repeats are ignored, which keeps the load
    /// order stable: DBS_LOAD_TUTORIAL_SIMPLE_TIP serves the tips in the order they were first
    /// added (cap_newchar.log adds 1, 2, 35, 39 and the relog capture serves exactly that order).
    /// Returns true when the tip was new.
    /// </summary>
    public bool AddTutorialTip(int ownerId, int tipId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO tutorial_tips (owner_id, tip_id) VALUES ($o, $t)";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$t", tipId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>The character's tutorial tips, oldest first.</summary>
    public List<int> GetTutorialTips(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<int>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT tip_id FROM tutorial_tips WHERE owner_id = $o ORDER BY rowid";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt32(0));
            return list;
        }
    }

    /// <summary>One seren-guide slot, last write wins.</summary>
    public void SetSerenGuide(int ownerId, int serenType, int serenId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO seren_guide (owner_id, seren_type, seren_id, updated_at) "
                            + "VALUES ($o, $t, $i, datetime('now')) "
                            + "ON CONFLICT(owner_id, seren_type) DO UPDATE SET seren_id = $i, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$t", serenType);
            cmd.Parameters.AddWithValue("$i", serenId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every stored seren-guide slot for a character, as (type, id).</summary>
    public Dictionary<int, int> GetSerenGuide(int ownerId)
    {
        lock (_lock)
        {
            var map = new Dictionary<int, int>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT seren_type, seren_id FROM seren_guide WHERE owner_id = $o";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt32(0)] = r.GetInt32(1);
            return map;
        }
    }

    // ---- Dungeon cool times (T25) ----

    /// <summary>
    /// Store the 52-byte DungeonCoolTimeElem for one dungeon, last write wins. World sends the
    /// whole element on every change (SA_UPDATE_DUNGEON_COOLTIME), so there is nothing to merge.
    /// </summary>
    public void UpsertDungeonCoolTime(int ownerId, int dungeonId, byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO dungeon_cooldowns (owner_id, dungeon_id, record, updated_at) "
                            + "VALUES ($o, $d, $r, datetime('now')) "
                            + "ON CONFLICT(owner_id, dungeon_id) DO UPDATE SET record = $r, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$d", dungeonId);
            cmd.Parameters.AddWithValue("$r", record);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// SA_UPDATE_DUNGEON_CLEAR_COUNT (0x13B7): <c>dbo.spUpdateDungeonClearCount(userDbId,
    /// continentId, clearCount)</c>. Stored on the same row; a dungeon can have a clear count
    /// without ever having had a cool time, hence the nullable record column.
    /// </summary>
    public void SetDungeonClearCount(int ownerId, int dungeonId, int clearCount)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO dungeon_cooldowns (owner_id, dungeon_id, clear_count, updated_at) "
                            + "VALUES ($o, $d, $c, datetime('now')) "
                            + "ON CONFLICT(owner_id, dungeon_id) DO UPDATE SET clear_count = $c, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$d", dungeonId);
            cmd.Parameters.AddWithValue("$c", clearCount);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every stored cool-time element for a character, ordered by dungeon id so the reply is
    /// reproducible. Rows that only ever carried a clear count have no element and are skipped.
    /// </summary>
    public List<byte[]> GetDungeonCoolTimes(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<byte[]>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT record FROM dungeon_cooldowns WHERE owner_id = $o AND record IS NOT NULL "
                            + "ORDER BY dungeon_id";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r["record"]);
            return list;
        }
    }

    /// <summary>The stored element for one dungeon, or null.</summary>
    public byte[]? GetDungeonCoolTime(int ownerId, int dungeonId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT record FROM dungeon_cooldowns WHERE owner_id = $o AND dungeon_id = $d";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$d", dungeonId);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    /// <summary>
    /// SA_DELETE_DUNGEON_COOLTIME (0x13BD): drop the cool time but keep the clear count, which
    /// is what DungeonInfoManager::DeleteCoolTime does - the two live in different containers.
    /// </summary>
    public bool ClearDungeonCoolTime(int ownerId, int dungeonId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE dungeon_cooldowns SET record = NULL, updated_at = datetime('now') "
                            + "WHERE owner_id = $o AND dungeon_id = $d";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$d", dungeonId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    // ---- Reputations (T26) ----

    /// <summary>
    /// Store one 52-byte ReputationData, keyed on the reputation id at record+4. Insert and
    /// update are the same operation for us: the real Arbiter's two paths
    /// (ReputationDataManager::AddNewReputationInfo and ::UpdateReputationInfo) both end with
    /// the incoming struct in the map, so last write wins.
    /// </summary>
    public void UpsertReputation(int ownerId, int reputationId, byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO reputations (owner_id, reputation_id, record, updated_at) "
                            + "VALUES ($o, $r, $b, datetime('now')) "
                            + "ON CONFLICT(owner_id, reputation_id) DO UPDATE SET record = $b, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$r", reputationId);
            cmd.Parameters.AddWithValue("$b", record);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A character's reputations, ordered by reputation id - the order
    /// ReputationDataManager::GetAllReputationData produces, because it walks a std::map keyed
    /// on that id.
    /// </summary>
    public List<byte[]> GetReputations(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<byte[]>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT record FROM reputations WHERE owner_id = $o ORDER BY reputation_id";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r["record"]);
            return list;
        }
    }

    // ---- Fatigability (T26) ----

    /// <summary>One account's fatigue state. <paramref name="Timestamp"/> is the raw 16-byte
    /// TIMESTAMP of the last update, or null when there has never been one.</summary>
    public sealed record FatigabilityRow(int CurPoint, byte[]? Timestamp, int Tail);

    /// <summary>
    /// Add a fatigue delta to an account and stamp the update time. Returns the new total.
    /// The delta really is a delta - three capture chains prove it, including two that cross
    /// from one character to another on the same account.
    /// </summary>
    public FatigabilityRow AddFatigabilityPoints(long accountId, int delta, byte[] timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO fatigability (account_id, cur_point, updated_at) VALUES ($a, $d, $t) "
                            + "ON CONFLICT(account_id) DO UPDATE SET cur_point = cur_point + $d, updated_at = $t";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.Parameters.AddWithValue("$t", timestamp);
            cmd.ExecuteNonQuery();
        }
        return GetFatigability(accountId);
    }

    /// <summary>An account's fatigue state; zeros and no timestamp when it has never been set.</summary>
    public FatigabilityRow GetFatigability(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT cur_point, updated_at, tail FROM fatigability WHERE account_id = $a";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new FatigabilityRow(0, null, 0);
            return new FatigabilityRow(r.GetInt32(0), r["updated_at"] as byte[], r.GetInt32(2));
        }
    }

    // ---- Id sequences ----

    /// <summary>
    /// First item DB id we hand out. The ids baked into <c>data/starter_inventory.bin</c> are
    /// 7..12 and every character is currently served that same list, so the counter starts well
    /// clear of them. (In the capture the real Arbiter's sequence was still in the teens; ours
    /// only has to be non-zero, distinct and monotonic.)
    /// </summary>
    public const int FirstItemId = 1000;

    /// <summary>One fresh item DB id.</summary>
    public int NextItemId() => ReserveItemIds(1);

    /// <summary>
    /// Reserve <paramref name="count"/> consecutive item DB ids and return the first. The
    /// counter lives in <c>counters</c>, so ids never repeat across a restart — a repeat would
    /// hand World an id it already has an item for.
    /// </summary>
    public int ReserveItemIds(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "count must be at least 1");
        lock (_lock)
        {
            using (var bump = _db.CreateCommand())
            {
                bump.CommandText =
                    "INSERT INTO counters(name, value) VALUES($k, $seed) " +
                    "ON CONFLICT(name) DO UPDATE SET value = value + $n";
                bump.Parameters.AddWithValue("$k", ItemIdCounter);
                bump.Parameters.AddWithValue("$seed", FirstItemId + count - 1);
                bump.Parameters.AddWithValue("$n", count);
                bump.ExecuteNonQuery();
            }
            using var sel = _db.CreateCommand();
            sel.CommandText = "SELECT value FROM counters WHERE name = $k";
            sel.Parameters.AddWithValue("$k", ItemIdCounter);
            int last = Convert.ToInt32(sel.ExecuteScalar()!);
            return last - count + 1;
        }
    }

    private const string ItemIdCounter = "item_id";

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
        Exp = r.GetInt64(r.GetOrdinal("exp")),
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
        ReturnZone = r.GetInt32(r.GetOrdinal("return_zone")),
        ReturnX = (float)r.GetDouble(r.GetOrdinal("return_x")),
        ReturnY = (float)r.GetDouble(r.GetOrdinal("return_y")),
        ReturnZ = (float)r.GetDouble(r.GetOrdinal("return_z")),
        DungeonId = r.GetInt32(r.GetOrdinal("dungeon_id")),
        InstancePdId = r.GetInt32(r.GetOrdinal("instance_pdid")),
    };

    // ---- Friends (T30) ----

    /// <summary>
    /// One row of the friends table. <paramref name="Type"/> is the relation the real Arbiter
    /// keeps at UserFriendInfo+0xE8 and ships as S_FRIEND_LIST.type: 0 = mutual friend,
    /// 1 = a request I sent, 2 = a request I received. <paramref name="GroupId"/> is 1 for
    /// ungrouped. <paramref name="Memo"/> is my note about them (max 20 chars).
    /// </summary>
    public sealed record FriendRow(int FriendId, int Type, int GroupId, string Memo);

    /// <summary>Every friend row for a character, oldest first - the order the real Arbiter
    /// walks its vector, and therefore the order S_FRIEND_LIST is built in.</summary>
    public List<FriendRow> GetFriendRows(int characterId)
    {
        lock (_lock)
        {
            var list = new List<FriendRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT friend_id, type, group_id, memo FROM friends "
                            + "WHERE character_id = $cid ORDER BY created_at, friend_id";
            cmd.Parameters.AddWithValue("$cid", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new FriendRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
            return list;
        }
    }

    /// <summary>One friend row, or null when they are not on the list.</summary>
    public FriendRow? GetFriendRow(int characterId, int friendId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT friend_id, type, group_id, memo FROM friends "
                            + "WHERE character_id = $c AND friend_id = $f";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$f", friendId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new FriendRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3));
        }
    }

    /// <summary>Get all friends for a character (type: 0=mutual, 1=outgoing request, 2=incoming request).</summary>
    public List<(int FriendId, int Type)> GetFriends(int characterId)
    {
        var rows = GetFriendRows(characterId);
        var list = new List<(int, int)>(rows.Count);
        foreach (var row in rows) list.Add((row.FriendId, row.Type));
        return list;
    }

    /// <summary>
    /// Insert or update one direction of a friendship. The real Arbiter has exactly this
    /// last-write-wins shape: both AddNewFriend and the accept path end in
    /// User::AddToFriendListNoLock, which overwrites the record it finds (memo included).
    /// </summary>
    public void UpsertFriend(int characterId, int friendId, int type, string memo, int groupId = 1)
    {
        ArgumentNullException.ThrowIfNull(memo);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO friends(character_id, friend_id, type, group_id, memo) "
                            + "VALUES($c,$f,$t,$g,$m) "
                            + "ON CONFLICT(character_id, friend_id) DO UPDATE SET "
                            + "type = $t, group_id = $g, memo = $m";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$f", friendId);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.Parameters.AddWithValue("$m", memo);
            cmd.ExecuteNonQuery();
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

    /// <summary>dbo.spChangeFriendMemo. Truncates to 20 chars, as wcsncpy_s(.., 0x15, ..) does.</summary>
    public bool SetFriendMemo(int characterId, int friendId, string memo)
    {
        ArgumentNullException.ThrowIfNull(memo);
        if (memo.Length > 20) memo = memo[..20];
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE friends SET memo = $m WHERE character_id = $c AND friend_id = $f";
            cmd.Parameters.AddWithValue("$m", memo);
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$f", friendId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>dbo.spChangeFriendGroupId.</summary>
    public bool SetFriendGroup(int characterId, int friendId, int groupId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE friends SET group_id = $g WHERE character_id = $c AND friend_id = $f";
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$f", friendId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---- Friend groups (T30) ----

    /// <summary>The character's groups, by index. Group 1 (ungrouped) is never one of them.</summary>
    public List<(int Index, string Name)> GetFriendGroups(int characterId)
    {
        lock (_lock)
        {
            var list = new List<(int, string)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT group_index, name FROM friend_groups "
                            + "WHERE character_id = $c ORDER BY group_index";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>dbo.spUpdateFriendGroupList - create or rename, keyed on the index the client chose.</summary>
    public void UpsertFriendGroup(int characterId, int index, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > 40) name = name[..40];
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO friend_groups(character_id, group_index, name) VALUES($c,$i,$n) "
                            + "ON CONFLICT(character_id, group_index) DO UPDATE SET "
                            + "name = $n, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$i", index);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// dbo.spDeleteFriendGroup. User::DeleteFriendGroup moves every member back to group 1
    /// first, so a deleted group never leaves friends pointing at a group that is gone.
    /// </summary>
    public bool DeleteFriendGroup(int characterId, int index)
    {
        lock (_lock)
        {
            using (var move = _db.CreateCommand())
            {
                move.CommandText = "UPDATE friends SET group_id = 1 "
                                 + "WHERE character_id = $c AND group_id = $i";
                move.Parameters.AddWithValue("$c", characterId);
                move.Parameters.AddWithValue("$i", index);
                move.ExecuteNonQuery();
            }
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM friend_groups WHERE character_id = $c AND group_index = $i";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$i", index);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// The dbo.spIsProvideSampleFriendGroup guard: true exactly once per character, the first
    /// time anything asks. The caller then seeds group 2 and the default profile message.
    /// </summary>
    public bool TryProvideSampleFriendGroup(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET sample_group_provided = 1 "
                            + "WHERE id = $id AND sample_group_provided = 0";
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The profile message shown as personalNote in S_FRIEND_LIST (spLoadUserFriendProfile).</summary>
    public string GetProfileMessage(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT profile_message FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteScalar() as string ?? "";
        }
    }

    /// <summary>dbo.spUpdateUserFriendProfile. Truncated to 30 chars like the 31-wchar buffer.</summary>
    public void SetProfileMessage(int characterId, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > 30) message = message[..30];
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET profile_message = $m WHERE id = $id";
            cmd.Parameters.AddWithValue("$m", message);
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Blocks ----

    /// <summary>One blocked character and my note about them (max 40 chars).</summary>
    public sealed record BlockRow(int BlockedId, string Memo);

    /// <summary>The block list with memos, oldest first.</summary>
    public List<BlockRow> GetBlockRows(int characterId)
    {
        lock (_lock)
        {
            var list = new List<BlockRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT blocked_id, memo FROM blocks "
                            + "WHERE character_id = $cid ORDER BY created_at, blocked_id";
            cmd.Parameters.AddWithValue("$cid", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new BlockRow(r.GetInt32(0), r.GetString(1)));
            return list;
        }
    }

    public List<int> GetBlocks(int characterId)
    {
        var rows = GetBlockRows(characterId);
        var list = new List<int>(rows.Count);
        foreach (var row in rows) list.Add(row.BlockedId);
        return list;
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

    /// <summary>dbo.spEditBlockedUserMemo. Truncates to 40 chars (wcsncpy_s(.., 0x29, ..)).</summary>
    public bool SetBlockMemo(int characterId, int blockedId, string memo)
    {
        ArgumentNullException.ThrowIfNull(memo);
        if (memo.Length > 40) memo = memo[..40];
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE blocks SET memo = $m WHERE character_id = $c AND blocked_id = $b";
            cmd.Parameters.AddWithValue("$m", memo);
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$b", blockedId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }


    // =======================================================================================
    // Guilds (T39). Research and the column-by-column derivation: status/GUILD-DESIGN.md.
    //
    // Guilds are the mirror image of parties: a party lives only in Arbiter RAM and is never
    // persisted, a guild is persisted BY THE ARBITER in SQL and World only gets a read-only
    // mirror. That is why this lives here and not in a manager - status/GUILD-DESIGN.md section 0.
    //
    // Every method below maps to a named stored procedure in the real Arbiter; the comment on
    // each says which. Timestamps are unix seconds (see the DDL note in Migrate).
    // =======================================================================================

    /// <summary>Announce is a wchar[201] in GuildData (Guild::UpdateGuildAnnounce truncates with
    /// <c>wcsncpy_s(guild+0x118, 0xc9, ...)</c>), so 200 characters plus the NUL.</summary>
    public const int MaxGuildAnnounce = 200;
    /// <summary>GuildTitle is a wchar[15]: <c>wcsncpy_s(guild+0x2b4, 0xf, ...)</c>.</summary>
    public const int MaxGuildTitle = 14;
    /// <summary>GuildPromotion (the recruit blurb) is a wchar[201], same as the announce.</summary>
    public const int MaxGuildPromotion = 200;
    /// <summary>A member's own note: <c>wcsncpy_s(member+0x90, 0x1f, ...)</c>.</summary>
    public const int MaxGuildIntroduce = 30;
    /// <summary>GuildName is a wchar[37].</summary>
    public const int MaxGuildName = 36;
    /// <summary>GuildGroupData.Name is a wchar[16].</summary>
    public const int MaxGuildGroupName = 15;
    /// <summary>Guild::UpdateGuildLogo rejects <c>8000 &lt; len</c> before it binds the varbinary.</summary>
    public const int MaxGuildLogoBytes = 8000;

    /// <summary>The group id a new member gets. Hardcoded in Guild::AddUserToGuildMemberNoLock
    /// (<c>local_dc = 2</c>).</summary>
    public const int DefaultGuildGroupId = 2;

    /// <summary>One row of <c>guilds</c>. Field order follows spLoadAllGuild's 28 column binds.</summary>
    public sealed record GuildRow(
        int GuildId, string Name, int ChiefDbId, long CreateDate, int Level, long Exp, long Point,
        long Money, string Announce, int RecommendationPoint, string Title, int LogoId,
        string Promotion, bool WarAcceptable, long WarToggleTime, int GeneralCoin,
        int ForeverEmblemId, int EmblemId, int Preference, int JoinMinLevel, int JoinMaxLevel,
        int JoinType, long LastWeekPlayTime, long ThisWeekPlayTime, long LastIncentiveTime,
        int AddAccountLimit);

    /// <summary>One row of <c>guild_members</c> - the persisted half of GuildMemberData (0xF0).
    /// State and CanGuildWar are runtime-only and deliberately absent.</summary>
    public sealed record GuildMemberRow(
        int UserDbId, int GuildId, string Name, int WorldId, int GuardId, int SectionId,
        int GuildGroupId, int UserLevel, int Race, int UserClass, int Gender, string Introduce,
        long LastLogoutTime, long AccountId, long GuildJoinDate, int WeeklyContribution,
        long TotalContribution);

    /// <summary>GuildGroupData (0x28): id, wchar[16] name, authority bitmask.</summary>
    public sealed record GuildGroupRow(int GuildGroupId, string Name, int Authority);

    public sealed record GuildApplyRow(int UserDbId, string JoinMsg, long AppliedAt);
    public sealed record GuildInviteRow(int GuildId, int UserDbId, int InvitorDbId, long InvitedAt);
    public sealed record GuildLogRow(
        long Id, int ActionType, long LogTime, int ActorDbId, string ActorName, string TargetName,
        int ParamInt, long ParamI64, string Detail);

    private const string GuildColumns =
        "guild_id, name, chief_db_id, create_date, level, exp, point, money, announce, " +
        "recommendation_point, title, logo_id, promotion, war_acceptable, war_toggle_time, " +
        "general_coin, forever_emblem_id, emblem_id, preference, join_min_level, join_max_level, " +
        "join_type, last_week_play_time, this_week_play_time, last_incentive_time, add_account_limit";

    private static GuildRow ReadGuild(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), r.GetInt64(5),
        r.GetInt64(6), r.GetInt64(7), r.GetString(8), r.GetInt32(9), r.GetString(10), r.GetInt32(11),
        r.GetString(12), r.GetInt32(13) != 0, r.GetInt64(14), r.GetInt32(15), r.GetInt32(16),
        r.GetInt32(17), r.GetInt32(18), r.GetInt32(19), r.GetInt32(20), r.GetInt32(21),
        r.GetInt64(22), r.GetInt64(23), r.GetInt64(24), r.GetInt32(25));

    private const string GuildMemberColumns =
        "user_db_id, guild_id, name, world_id, guard_id, section_id, guild_group_id, user_level, " +
        "race, user_class, gender, introduce, last_logout_time, account_id, guild_join_date, " +
        "weekly_contribution, total_contribution";

    private static GuildMemberRow ReadGuildMember(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
        r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetString(11),
        r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), r.GetInt32(15), r.GetInt64(16));

    private static string Clamp(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max];
    }

    // ---- guilds ----

    /// <summary>
    /// spCreateGuild(nvarchar name, int chiefDbId, bit warAcceptable) -> OUT int guildDbId.
    /// Returns 0 when the name is already taken, which is what the OUT parameter being 0 means
    /// in GuildManager::CreateGuildData (`if (local_158[0] == 0) ... abort`).
    /// Also seeds the two ranks SDB_CREATE_GUILD2 carries names for and inserts the chief.
    /// </summary>
    public int CreateGuild(string name, int chiefDbId, bool warAcceptable,
        string masterGroupName = "Master", string memberGroupName = "Member", long createdAt = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0) return 0;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var probe = _db.CreateCommand())
            {
                probe.Transaction = tx;
                probe.CommandText = "SELECT COUNT(*) FROM guilds WHERE name = $n COLLATE NOCASE";
                probe.Parameters.AddWithValue("$n", name);
                if ((long)probe.ExecuteScalar()! > 0) return 0;
            }
            if (createdAt == 0) createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int guildId;
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT INTO guilds(name, chief_db_id, create_date, war_acceptable) " +
                    "VALUES($n,$c,$d,$w); SELECT last_insert_rowid()";
                ins.Parameters.AddWithValue("$n", Clamp(name, MaxGuildName));
                ins.Parameters.AddWithValue("$c", chiefDbId);
                ins.Parameters.AddWithValue("$d", createdAt);
                ins.Parameters.AddWithValue("$w", warAcceptable ? 1 : 0);
                guildId = Convert.ToInt32(ins.ExecuteScalar()!);
            }

            // The two groups SDB_CREATE_GUILD2 names. Group 1 is the officer rank (authority
            // 0x7F = every bit we have seen used); group 2 is the plain-member rank and is the
            // id Guild::AddUserToGuildMemberNoLock hands every new member.
            InsertGroup(tx, guildId, 1, masterGroupName, GuildAuthorityAll);
            InsertGroup(tx, guildId, DefaultGuildGroupId, memberGroupName, 0);
            tx.Commit();
            return guildId;
        }
    }

    /// <summary>Every authority bit the decompile actually tests, OR-ed. Guild::HaveGuildAuthorityWithLock
    /// masks seen in callers: 0x01 invite/applications, 0x02, 0x04 announce, 0x10, 0x20, 0x40.</summary>
    public const int GuildAuthorityAll = 0x7F;
    /// <summary>Bit 0: invite a user and manage the apply list. C_INVITE_USER_TO_GUILD's handler
    /// and GuildJoinManager::SendGuildApplyList both test it (mask 1).</summary>
    public const int GuildAuthorityInvite = 0x01;
    /// <summary>Bit 2: change the announce. Guild::UpdateGuildAnnounce tests mask 4.</summary>
    public const int GuildAuthorityAnnounce = 0x04;

    private void InsertGroup(SqliteTransaction tx, int guildId, int groupId, string name, int authority)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO guild_groups(guild_id, guild_group_id, name, authority) " +
                          "VALUES($g,$i,$n,$a)";
        cmd.Parameters.AddWithValue("$g", guildId);
        cmd.Parameters.AddWithValue("$i", groupId);
        cmd.Parameters.AddWithValue("$n", Clamp(name, MaxGuildGroupName));
        cmd.Parameters.AddWithValue("$a", authority);
        cmd.ExecuteNonQuery();
    }

    /// <summary>spLoadAllGuild, one row.</summary>
    public GuildRow? GetGuild(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {GuildColumns} FROM guilds WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$g", guildId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadGuild(r) : null;
        }
    }

    /// <summary>GuildManager::GetGuildWithLock(const wchar_t *) - the by-name lookup
    /// C_APPLY_GUILD and C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD use when the id is 0.</summary>
    public GuildRow? GetGuildByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {GuildColumns} FROM guilds WHERE name = $n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$n", name);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadGuild(r) : null;
        }
    }

    /// <summary>spLoadAllGuild with no parameter - every guild, for the DBS_INIT_GUILD_* boot load.</summary>
    public List<GuildRow> GetAllGuilds()
    {
        lock (_lock)
        {
            var list = new List<GuildRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {GuildColumns} FROM guilds ORDER BY guild_id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadGuild(r));
            return list;
        }
    }

    /// <summary>True when a guild already has this name. spCreateGuild's uniqueness check, and
    /// the answer C_CHECK_CHANGE_GUILDNAME needs.</summary>
    public bool GuildNameTaken(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM guilds WHERE name = $n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$n", name);
            return (long)cmd.ExecuteScalar()! > 0;
        }
    }

    /// <summary>spUpdateGuildAnnounce(int guildDbId, nvarchar announce). Truncated to 200 chars,
    /// exactly as Guild::UpdateGuildAnnounce does when it copies into GuildData+0x118.</summary>
    public bool UpdateGuildAnnounce(int guildId, string announce)
        => SetGuildText(guildId, "announce", Clamp(announce, MaxGuildAnnounce));

    /// <summary>spUpdateGuildTitle(int guildDbId, nvarchar title). wchar[15].</summary>
    public bool UpdateGuildTitle(int guildId, string title)
        => SetGuildText(guildId, "title", Clamp(title, MaxGuildTitle));

    /// <summary>spUpdateGuildPromotionStr - the recruit blurb S_GUILD_INFO carries as GuildPromotion.</summary>
    public bool UpdateGuildPromotion(int guildId, string promotion)
        => SetGuildText(guildId, "promotion", Clamp(promotion, MaxGuildPromotion));

    private bool SetGuildText(int guildId, string column, string value)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE guilds SET {column} = $v WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// spUpdateGuildJoinCondition. The four values C_SET_GUILD_JOIN_CONDITION carries; the
    /// introduction string it also carries is the guild's promotion blurb, so pass it to
    /// <see cref="UpdateGuildPromotion"/>.
    /// </summary>
    public bool UpdateGuildJoinCondition(int guildId, int minLevel, int maxLevel, int joinType, int preference)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET join_min_level = $min, join_max_level = $max, " +
                              "join_type = $t, preference = $p WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$min", minLevel);
            cmd.Parameters.AddWithValue("$max", maxLevel);
            cmd.Parameters.AddWithValue("$t", joinType);
            cmd.Parameters.AddWithValue("$p", preference);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// spUpdateGuildLogo(int guildDbId, varbinary logo, int newLogoId). The real Arbiter computes
    /// the new id as <c>Guild+0x2434 + 1</c> and stores the byte length beside it; we bump the
    /// stored id the same way. Returns the new logo id, or 0 when the guild is missing or the
    /// blob is over 8000 bytes - the cap Guild::UpdateGuildLogo enforces before binding.
    /// </summary>
    public int UpdateGuildLogo(int guildId, byte[]? logo)
    {
        if (logo != null && logo.Length > MaxGuildLogoBytes) return 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET logo = $l, logo_id = logo_id + 1 WHERE guild_id = $g; " +
                              "SELECT logo_id FROM guilds WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$l", (object?)logo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$g", guildId);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }
    }

    /// <summary>The raw logo image, or null. Guild::SendGuildLogo reads it to build
    /// S_GET_USER_GUILD_LOGO; it never crosses the World link.</summary>
    public byte[]? GetGuildLogo(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT logo FROM guilds WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$g", guildId);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return null;
            return (byte[])r["logo"];
        }
    }

    /// <summary>spChangeGuildChief(int guildDbId, int newChiefDbId).</summary>
    public bool ChangeGuildChief(int guildId, int newChiefDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET chief_db_id = $c WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$c", newChiefDbId);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// spDeleteGuild(int guildDbId), plus the children the real Arbiter drops through
    /// spDeleteGuildMemberDataOfGuild / spDeleteGuildGroup / spDeleteGuildApplyForGuildSide /
    /// spDeleteInviteUserToGuildForGuildSide. Explicit because this DB never enables
    /// PRAGMA foreign_keys.
    /// </summary>
    public bool DeleteGuild(int guildId)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "guild_members", "guild_groups", "guild_applies",
                                          "guild_invites", "guild_log", "guild_perks" })
            {
                using var del = _db.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {table} WHERE guild_id = $g";
                del.Parameters.AddWithValue("$g", guildId);
                del.ExecuteNonQuery();
            }
            int n;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM guilds WHERE guild_id = $g";
                cmd.Parameters.AddWithValue("$g", guildId);
                n = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n > 0;
        }
    }

    // ---- members ----

    /// <summary>spLoadAllGuildMemberData(int guildDbId), in join order - the order
    /// S_GUILD_MEMBER_LIST walks the map.</summary>
    public List<GuildMemberRow> GetGuildMembers(int guildId)
    {
        lock (_lock)
        {
            var list = new List<GuildMemberRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {GuildMemberColumns} FROM guild_members " +
                              "WHERE guild_id = $g ORDER BY guild_join_date, user_db_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadGuildMember(r));
            return list;
        }
    }

    /// <summary>One member row, or null when the character is in no guild. The lookup
    /// spLeaveGuildMember implies: a character is in at most one guild.</summary>
    public GuildMemberRow? GetGuildMember(int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {GuildMemberColumns} FROM guild_members WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$u", userDbId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadGuildMember(r) : null;
        }
    }

    /// <summary>The guild a character belongs to, or 0. User+0x1b54 in the real Arbiter.</summary>
    public int GetGuildIdOf(int userDbId) => GetGuildMember(userDbId)?.GuildId ?? 0;

    /// <summary>
    /// spAddGuildMember(int userDbId, int guildDbId, int guildGroupId) -> OUT rows, OUT joinDate.
    /// Returns the join date it stored, or 0 when the character is already in a guild - the real
    /// Arbiter's OUT rows == 0 case.
    /// </summary>
    public long AddGuildMember(int guildId, int userDbId, string name, int race, int userClass,
        int gender, int level, long accountId, int guildGroupId = DefaultGuildGroupId, long joinDate = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
        {
            using var probe = _db.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM guild_members WHERE user_db_id = $u";
            probe.Parameters.AddWithValue("$u", userDbId);
            if ((long)probe.ExecuteScalar()! > 0) return 0;

            if (joinDate == 0) joinDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO guild_members(user_db_id, guild_id, name, guild_group_id, user_level, " +
                "race, user_class, gender, account_id, guild_join_date) " +
                "VALUES($u,$g,$n,$grp,$lvl,$race,$cls,$sex,$acc,$jd)";
            cmd.Parameters.AddWithValue("$u", userDbId);
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$grp", guildGroupId);
            cmd.Parameters.AddWithValue("$lvl", level);
            cmd.Parameters.AddWithValue("$race", race);
            cmd.Parameters.AddWithValue("$cls", userClass);
            cmd.Parameters.AddWithValue("$sex", gender);
            cmd.Parameters.AddWithValue("$acc", accountId);
            cmd.Parameters.AddWithValue("$jd", joinDate);
            cmd.ExecuteNonQuery();
            return joinDate;
        }
    }

    /// <summary>
    /// spLeaveGuildMember(int userDbId) - the SAME proc for leaving and being kicked; there is
    /// no spBanishGuildMember (status/GUILD-DESIGN.md section 3.1). Its second OUT parameter is
    /// the leave time, which the real Arbiter keeps at User+0x3c48 and Guild::CanRejoinGuild
    /// reads later, so it is stamped on the character here.
    /// </summary>
    public bool RemoveGuildMember(int userDbId, long leftAt = 0)
    {
        lock (_lock)
        {
            if (leftAt == 0) leftAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int n;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM guild_members WHERE user_db_id = $u";
                cmd.Parameters.AddWithValue("$u", userDbId);
                n = cmd.ExecuteNonQuery();
            }
            if (n > 0)
            {
                using var stamp = _db.CreateCommand();
                stamp.CommandText = "UPDATE characters SET guild_leave_time = $t WHERE id = $u";
                stamp.Parameters.AddWithValue("$t", leftAt);
                stamp.Parameters.AddWithValue("$u", userDbId);
                stamp.ExecuteNonQuery();
            }
            return n > 0;
        }
    }

    /// <summary>
    /// When this character last left a guild, 0 if never. User+0x3c48 in the real Arbiter;
    /// Guild::CanRejoinGuild compares it against a config cooldown to answer
    /// S_REQUEST_COOLTIME_TO_JOIN_GUILD.
    /// </summary>
    public long GetGuildLeaveTime(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT guild_leave_time FROM characters WHERE id = $u";
            cmd.Parameters.AddWithValue("$u", characterId);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
        }
    }

    /// <summary>spUpdateGuildMember(int userDbId, int guildDbId, int newGuildGroupId) -
    /// Guild::ChangeGuildGroup, i.e. a rank change.</summary>
    public bool SetGuildMemberGroup(int userDbId, int guildGroupId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guild_members SET guild_group_id = $grp WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$grp", guildGroupId);
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>spUpdateUserGuildIntroduce(int userDbId, nvarchar introduce). Truncated to 30
    /// chars, as Guild::UpdateGuildmemberIntroduce does (wcsncpy_s(member+0x90, 0x1f, ...)).</summary>
    public bool UpdateGuildMemberIntroduce(int userDbId, string introduce)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guild_members SET introduce = $i WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$i", Clamp(introduce, MaxGuildIntroduce));
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The five fields AS_UPDATE_GUILD_MEMBER carries, written back when a member moves
    /// or levels. The real Arbiter keeps them in GuildMemberData so an offline member still
    /// renders in S_GUILD_MEMBER_LIST.</summary>
    public bool UpdateGuildMemberLocation(int userDbId, int worldId, int guardId, int sectionId,
        int level, long lastLogoutTime)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guild_members SET world_id = $w, guard_id = $g, section_id = $s, " +
                              "user_level = $l, last_logout_time = $t WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$w", worldId);
            cmd.Parameters.AddWithValue("$g", guardId);
            cmd.Parameters.AddWithValue("$s", sectionId);
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$t", lastLogoutTime);
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>spUpdateGuildContributionPoint(.., int weekly, bigint total).</summary>
    public bool AddGuildContribution(int userDbId, int weekly, long total)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guild_members SET weekly_contribution = weekly_contribution + $w, " +
                              "total_contribution = total_contribution + $t WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$w", weekly);
            cmd.Parameters.AddWithValue("$t", total);
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---- groups / ranks ----

    /// <summary>spLoadGuildGroup(int guildDbId).</summary>
    public List<GuildGroupRow> GetGuildGroups(int guildId)
    {
        lock (_lock)
        {
            var list = new List<GuildGroupRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT guild_group_id, name, authority FROM guild_groups " +
                              "WHERE guild_id = $g ORDER BY guild_group_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new GuildGroupRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2)));
            return list;
        }
    }

    /// <summary>One group, or null. The lookup behind Guild::HaveGuildAuthorityWithLock.</summary>
    public GuildGroupRow? GetGuildGroup(int guildId, int guildGroupId)
    {
        foreach (var g in GetGuildGroups(guildId))
            if (g.GuildGroupId == guildGroupId) return g;
        return null;
    }

    /// <summary>
    /// Guild::HaveGuildAuthorityWithLock(User*, enum GuildAuthority): the chief always passes;
    /// anyone else passes when <c>(wanted &amp; group.authority) != 0</c>.
    /// </summary>
    public bool HasGuildAuthority(int guildId, int userDbId, int wanted)
    {
        var guild = GetGuild(guildId);
        if (guild == null) return false;
        if (guild.ChiefDbId == userDbId) return true;
        var member = GetGuildMember(userDbId);
        if (member == null || member.GuildId != guildId) return false;
        var group = GetGuildGroup(guildId, member.GuildGroupId);
        return group != null && (wanted & group.Authority) != 0;
    }

    /// <summary>spCreateGuildGroup(int guildDbId, int guildGroupId, nvarchar name, int authority).</summary>
    public bool CreateGuildGroup(int guildId, int guildGroupId, string name, int authority)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO guild_groups(guild_id, guild_group_id, name, authority) " +
                              "VALUES($g,$i,$n,$a)";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$i", guildGroupId);
            cmd.Parameters.AddWithValue("$n", Clamp(name, MaxGuildGroupName));
            cmd.Parameters.AddWithValue("$a", authority);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>spUpdateGuildGroupAuthority(int guildDbId, int guildGroupId, int authority, nvarchar name).
    /// Note the real proc renames AND re-authorises in one call.</summary>
    public bool SetGuildGroupAuthority(int guildId, int guildGroupId, int authority, string name)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guild_groups SET authority = $a, name = $n " +
                              "WHERE guild_id = $g AND guild_group_id = $i";
            cmd.Parameters.AddWithValue("$a", authority);
            cmd.Parameters.AddWithValue("$n", Clamp(name, MaxGuildGroupName));
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$i", guildGroupId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>spDeleteGuildGroup(int guildDbId, int guildGroupId). Members on the removed rank
    /// fall back to the default group, the same way DeleteFriendGroup moves orphans to 1.</summary>
    public bool DeleteGuildGroup(int guildId, int guildGroupId)
    {
        if (guildGroupId == DefaultGuildGroupId) return false;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var move = _db.CreateCommand())
            {
                move.Transaction = tx;
                move.CommandText = "UPDATE guild_members SET guild_group_id = $d " +
                                   "WHERE guild_id = $g AND guild_group_id = $i";
                move.Parameters.AddWithValue("$d", DefaultGuildGroupId);
                move.Parameters.AddWithValue("$g", guildId);
                move.Parameters.AddWithValue("$i", guildGroupId);
                move.ExecuteNonQuery();
            }
            int n;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM guild_groups WHERE guild_id = $g AND guild_group_id = $i";
                cmd.Parameters.AddWithValue("$g", guildId);
                cmd.Parameters.AddWithValue("$i", guildGroupId);
                n = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n > 0;
        }
    }

    // ---- applies and invites ----

    /// <summary>spInsertGuildApply. One row per (guild, applicant); re-applying overwrites the
    /// message, which is what an INSERT on the real primary key does.</summary>
    public void InsertGuildApply(int guildId, int userDbId, string joinMsg, long appliedAt = 0)
    {
        lock (_lock)
        {
            if (appliedAt == 0) appliedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO guild_applies(guild_id, user_db_id, join_msg, applied_at) " +
                              "VALUES($g,$u,$m,$t) ON CONFLICT(guild_id, user_db_id) DO UPDATE SET " +
                              "join_msg = $m, applied_at = $t";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$u", userDbId);
            cmd.Parameters.AddWithValue("$m", joinMsg ?? "");
            cmd.Parameters.AddWithValue("$t", appliedAt);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>spLoadGuildApplyList(int guildDbId), oldest first.</summary>
    public List<GuildApplyRow> GetGuildApplies(int guildId)
    {
        lock (_lock)
        {
            var list = new List<GuildApplyRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT user_db_id, join_msg, applied_at FROM guild_applies " +
                              "WHERE guild_id = $g ORDER BY applied_at, user_db_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new GuildApplyRow(r.GetInt32(0), r.GetString(1), r.GetInt64(2)));
            return list;
        }
    }

    /// <summary>spDeleteGuildApply(guildDbId, userDbId).</summary>
    public bool DeleteGuildApply(int guildId, int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM guild_applies WHERE guild_id = $g AND user_db_id = $u";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>spDeleteGuildApplyForUserSide(userDbId) - every application this character has
    /// out, dropped the moment they join a guild.</summary>
    public int DeleteGuildAppliesOfUser(int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM guild_applies WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>spAddInviteUserToGuild(guildDbId, userDbId, invitorDbId).</summary>
    public void AddGuildInvite(int guildId, int userDbId, int invitorDbId, long invitedAt = 0)
    {
        lock (_lock)
        {
            if (invitedAt == 0) invitedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO guild_invites(guild_id, user_db_id, invitor_db_id, invited_at) " +
                              "VALUES($g,$u,$i,$t) ON CONFLICT(guild_id, user_db_id) DO UPDATE SET " +
                              "invitor_db_id = $i, invited_at = $t";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$u", userDbId);
            cmd.Parameters.AddWithValue("$i", invitorDbId);
            cmd.Parameters.AddWithValue("$t", invitedAt);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>spLoadInviteUserToGuild(userDbId) - every guild that has invited this character.</summary>
    public List<GuildInviteRow> GetGuildInvites(int userDbId)
    {
        lock (_lock)
        {
            var list = new List<GuildInviteRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT guild_id, user_db_id, invitor_db_id, invited_at FROM guild_invites " +
                              "WHERE user_db_id = $u ORDER BY invited_at, guild_id";
            cmd.Parameters.AddWithValue("$u", userDbId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GuildInviteRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3)));
            return list;
        }
    }

    /// <summary>spDeleteInviteUserToGuild(guildDbId, userDbId).</summary>
    public bool DeleteGuildInvite(int guildId, int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM guild_invites WHERE guild_id = $g AND user_db_id = $u";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>spDeleteInviteUserToGuildForUserSide(userDbId).</summary>
    public int DeleteGuildInvitesOfUser(int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM guild_invites WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery();
        }
    }

    // ---- history log ----

    /// <summary>spCreateGuildLog. ActionType 0x0B is the join entry GuildUtil::UserJoinToGuild
    /// writes (FUN_140566090(guildDbId, 0xb, ...)).</summary>
    public void AddGuildLog(int guildId, int actionType, string actorName, string targetName = "",
        int actorDbId = 0, int paramInt = 0, long paramI64 = 0, string detail = "", long logTime = 0)
    {
        lock (_lock)
        {
            if (logTime == 0) logTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO guild_log(guild_id, action_type, log_time, actor_db_id, actor_name, " +
                "target_name, param_int, param_i64, detail) VALUES($g,$a,$t,$ai,$an,$tn,$pi,$p6,$d)";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$a", actionType);
            cmd.Parameters.AddWithValue("$t", logTime);
            cmd.Parameters.AddWithValue("$ai", actorDbId);
            cmd.Parameters.AddWithValue("$an", actorName ?? "");
            cmd.Parameters.AddWithValue("$tn", targetName ?? "");
            cmd.Parameters.AddWithValue("$pi", paramInt);
            cmd.Parameters.AddWithValue("$p6", paramI64);
            cmd.Parameters.AddWithValue("$d", detail ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>spLoadGuildLog(int guildDbId), newest first, one page. Guild::SendGuildHistory
    /// pages by 0x14 rows (FUN_14056f060(..., 0x14)); page numbers are 1-based.</summary>
    public List<GuildLogRow> GetGuildLog(int guildId, int page, int pageSize = GuildHistoryPageSize)
    {
        if (page < 1) page = 1;
        lock (_lock)
        {
            var list = new List<GuildLogRow>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT id, action_type, log_time, actor_db_id, actor_name, target_name, " +
                "param_int, param_i64, detail FROM guild_log WHERE guild_id = $g " +
                "ORDER BY log_time DESC, id DESC LIMIT $n OFFSET $o";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$n", pageSize);
            cmd.Parameters.AddWithValue("$o", (page - 1) * pageSize);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GuildLogRow(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3),
                    r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt64(7), r.GetString(8)));
            return list;
        }
    }

    /// <summary>Guild::SendGuildHistory's page size: 0x14 rows.</summary>
    public const int GuildHistoryPageSize = 0x14;

    /// <summary>How many pages S_GUILD_HISTORY should advertise as LastPage. Always at least 1,
    /// so an empty log still renders an empty page rather than page 1 of 0.</summary>
    public int CountGuildLogPages(int guildId, int pageSize = GuildHistoryPageSize)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM guild_log WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$g", guildId);
            long n = (long)cmd.ExecuteScalar()!;
            return n <= 0 ? 1 : (int)((n + pageSize - 1) / pageSize);
        }
    }

    public void Dispose() => _db.Dispose();
}
