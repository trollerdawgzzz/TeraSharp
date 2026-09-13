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
}

public sealed class AccountRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
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
");
        // CREATE TABLE IF NOT EXISTS does nothing to a DB that already has `characters`, so
        // columns added later need their own idempotent step. terasharp.db predates `exp`.
        AddColumnIfMissing("characters", "exp", "INTEGER NOT NULL DEFAULT 0");
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
