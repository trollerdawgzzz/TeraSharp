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

    // ---- T76: the lobby / friend-panel fields that were shipping as zero ----
    /// <summary>
    /// When this character last entered the world. S_FRIEND_LIST.lastOnline is
    /// <c>now - this</c> in seconds, which is what User::SendFriendListNoLock computes
    /// (Arb_part_030.c: <c>lVar15 = now - storedTime</c>, and 0 when the stored time is
    /// unset). In cap_social_client frames 1417/1437 the friend <c>two</c> is ONLINE and the
    /// field still carries 582 then 585 - three seconds apart - so it is an elapsed count
    /// from a fixed origin, not zero-for-online.
    /// </summary>
    public DateTime LastLogin { get; set; }
    /// <summary>
    /// Last known section, the (worldId, guardId, sectionId) trio C_VISIT_NEW_SECTION
    /// carries. The same three numbers appear in S_FRIEND_LIST and in S_GET_USER_LIST:
    /// cap_social_client frame 11 gives the character Test (1, 25, 599001) and frames
    /// 1417/1437 give the friend two the identical (1, 25, 0x000923D9 = 599001).
    /// </summary>
    public int LastWorld { get; set; }
    /// <inheritdoc cref="LastWorld"/>
    public int LastGuard { get; set; }
    /// <inheritdoc cref="LastWorld"/>
    public int LastSection { get; set; }
    /// <summary>
    /// Rested-xp points, the <c>i64 restBonusPoint</c> at frame offset 26 of
    /// SDB_UPDATE_EXP_LEVEL (0x273B) - the only place World reports it. The real Arbiter
    /// feeds it to User::UpdateUserExpAndRestBonusPoint / UpdateUserLevel and serves it back
    /// as S_GET_USER_LIST.restBonusXp: 419 for dob and 0 for Test in cap_social_client frame 11.
    /// </summary>
    public long RestBonus { get; set; }
    /// <summary>Opaque WorldServer state (15312 bytes). Null for never-entered characters.</summary>
    public byte[]? WorldBlob { get; set; }

    /// <summary>
    /// Character money ("gold"), the <c>money</c> column - T59.
    ///
    /// <para>Money is the ARBITER's field, not part of the blob as World saves it: World writes
    /// blob + 0x1C0 back as zero in all six <c>SDB_UPDATE_USER_DATA</c> frames of cap_newchar.log
    /// even though the character had 230 gold by the last one. The real Arbiter binds its SQL
    /// <c>money</c> column straight into <c>UserData + 0x1C0</c> when it builds the enter-world
    /// record (Arb_part_032.c:17798), and we do the same: <see cref="CharacterStore.Read"/>
    /// stamps this value into <see cref="WorldBlob"/> at <see cref="StarterBlob.MoneyOffset"/> on
    /// every load, so both 0x2738 builders serve it without having to know about it.</para>
    /// </summary>
    public long Money { get; set; }

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

    // --- money (T59) ---
    /// <summary>
    /// i64 character money at blob offset 448 (0x1C0). Proven from BOTH sides of the wire:
    ///   * the real Arbiter binds its SQL <c>money</c> column to <c>UserData + 0x1C0</c> when it
    ///     fills the enter-world record (Arb_part_032.c:17798, next to <c>gender</c> at +0xC4 and
    ///     <c>class</c> at +0xC8, which are our <see cref="GenderOffset"/> / <see cref="ClassOffset"/>);
    ///   * WorldServer's enter-world finisher calls <c>Inventory::SetMoney(User + 0xA478,
    ///     *(__int64 *)(context + 0x268))</c> (WorldServer.exe.c:1663604), and the blob lands at
    ///     <c>context + 0xA8</c> (<c>UserEnterWorldContext::SetRecvData</c>, :1661252) - so the
    ///     field it reads is blob + 0x268 - 0xA8 = 0x1C0. <c>SetMoney</c> is a plain assignment
    ///     (<c>*(__int64 *)(inv + 0x78) = money</c>, :1484331), so the blob value is ABSOLUTE.
    /// Zero in <c>data/starter_blob.bin</c>, and zero in every <c>SDB_UPDATE_USER_DATA</c> of
    /// cap_newchar.log including the ones taken after the character had earned 230 gold: World
    /// never writes this field back, which is why it has to live in its own column.
    /// </summary>
    public const int MoneyOffset = 448;
    /// <summary>Smallest blob that carries the money field.</summary>
    public const int MoneyBlockEnd = MoneyOffset + 8;

    /// <summary>Money as a blob carries it; 0 when the buffer is too short to hold the field.</summary>
    public static long ReadMoney(byte[]? blob) =>
        blob == null || blob.Length < MoneyBlockEnd ? 0L : BitConverter.ToInt64(blob, MoneyOffset);

    /// <summary>
    /// Stamp money into a blob on its way out in 0x2738. A null or short buffer is not an error
    /// - a character that has never entered the world has no blob - so this returns false and
    /// writes nothing rather than throwing. Unlike <see cref="Build"/>, which composes a blob
    /// once at character creation, this runs on every load - it is the one field we put back
    /// into a blob World has already saved, and World never sets it itself.
    /// </summary>
    public static bool WriteMoney(byte[]? blob, long money)
    {
        if (blob == null || blob.Length < MoneyBlockEnd) return false;
        BitConverter.TryWriteBytes(blob.AsSpan(MoneyOffset, 8), money);
        return true;
    }

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
  -- T76: last enter-world, last known section and rested xp. See CharacterRecord.
  last_login TEXT,
  last_world INTEGER NOT NULL DEFAULT 0,
  last_guard INTEGER NOT NULL DEFAULT 0,
  last_section INTEGER NOT NULL DEFAULT 0,
  rest_bonus INTEGER NOT NULL DEFAULT 0,
  -- T77: the EP (Extra Point) panel. Six numbers World writes with SDB_UPDATE_EXTRA_POINT
  -- (0x27B1) and reads back through AS_LOAD_EXTRAPOINT_DATA (0x1555) at enter-world; the
  -- seventh is the daily reset stamp SDB_UPDATE_DAILY_EXTRA_POINT (0x27AF) carries.
  ep_exp INTEGER NOT NULL DEFAULT 0,
  ep_level INTEGER NOT NULL DEFAULT 0,
  ep_point INTEGER NOT NULL DEFAULT 0,
  ep_daily_exp INTEGER NOT NULL DEFAULT 0,
  ep_reserve_bonus INTEGER NOT NULL DEFAULT 0,
  ep_daily_limit INTEGER NOT NULL DEFAULT 0,
  ep_reset_time INTEGER NOT NULL DEFAULT 0,
  world_blob BLOB,
  -- T59: character money (gold). Not in the blob World saves - it is stamped into the blob at
  -- StarterBlob.MoneyOffset when a character is read, exactly as the real Arbiter binds its own
  -- `money` column into UserData + 0x1C0. The op-9 atom carries a signed DELTA, so this is the
  -- running total.
  money INTEGER NOT NULL DEFAULT 0,
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

-- T80: guild war. The real Arbiter keeps these in PlanetDB as GuildWar and
-- GuildWarHistory, so unlike the party board this IS persisted. state is the war record s
-- +0xcc field the S_OPEN_GUILD_WAR_WINDOW writer switches on (6/7/8 are the three it
-- renders; 5 is the one it skips) - status/GUILD-WAR.md section 4.
CREATE TABLE IF NOT EXISTS guild_wars (
  war_id           INTEGER PRIMARY KEY AUTOINCREMENT,
  attack_guild_id  INTEGER NOT NULL,
  defend_guild_id  INTEGER NOT NULL,
  declared_at      INTEGER NOT NULL DEFAULT 0,
  money            INTEGER NOT NULL DEFAULT 0,
  state            INTEGER NOT NULL DEFAULT 6
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_wars_pair
  ON guild_wars(attack_guild_id, defend_guild_id);

CREATE TABLE IF NOT EXISTS guild_war_history (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  war_id           INTEGER NOT NULL,
  attack_guild_id  INTEGER NOT NULL,
  defend_guild_id  INTEGER NOT NULL,
  declared_at      INTEGER NOT NULL DEFAULT 0,
  ended_at         INTEGER NOT NULL DEFAULT 0,
  result           INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_guild_war_history_attack
  ON guild_war_history(attack_guild_id);
CREATE INDEX IF NOT EXISTS ix_guild_war_history_defend
  ON guild_war_history(defend_guild_id);

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
-- T83: the card collection. SDB_REGISTER_CARD (0x2988) adds one, SDB_MOUNT_CARD (0x298A) puts
-- it in a preset slot and SDB_UNMOUNT_CARD (0x298C) takes it out again; all three carry
-- UserDbId at payload 4 and the card's template id, and until T83 we acked them and threw the
-- contents away. preset is -1 while the card is owned but not mounted.
CREATE TABLE IF NOT EXISTS cards (
  character_id     INTEGER NOT NULL,
  card_template_id INTEGER NOT NULL,
  amount           INTEGER NOT NULL DEFAULT 1,
  preset           INTEGER NOT NULL DEFAULT -1,
  PRIMARY KEY (character_id, card_template_id)
);
CREATE INDEX IF NOT EXISTS ix_cards_character ON cards(character_id);

-- T83: learned crests (glyphs). SA_LEARN_ALL_CREST_ACQUIRABLE (0x1463) is the only frame in any
-- capture that names them - T50 answered it correctly and then discarded the ids. S_CREST_INFO
-- lists them back, nine bytes each.
CREATE TABLE IF NOT EXISTS crests (
  character_id INTEGER NOT NULL,
  crest_id     INTEGER NOT NULL,
  value        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, crest_id)
);

CREATE TABLE IF NOT EXISTS guild_perks (
  guild_id              INTEGER NOT NULL REFERENCES guilds(guild_id),
  perk_id               INTEGER NOT NULL,
  flag_a                INTEGER NOT NULL DEFAULT 0,
  flag_b                INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, perk_id)
);

-- T42: item rows. ONE table for every container, keyed by (owner_db_id, inven_type) exactly as
-- the real server's `Items` table is - `spUpdateItemOwner` moves a row between containers and
-- `spUpdateItemPos` moves it inside one, and there is no separate warehouse table.
-- `inven_type` is enum INVEN_TYPE, the same pocket id the 536-byte item record carries at +28:
-- 0 bag, 1 account warehouse, 3 guild, 9 character warehouse, 12 style, 14 equipped.
-- status/MAIL-WAREHOUSE.md section 6.
--
-- Only warehouse pockets are actually written today. The bag still comes from
-- data/starter_inventory.bin at login (T20), so a bag row here would be a second, disagreeing
-- source of truth; WarehouseHandlers.Apply deliberately skips atoms that touch no warehouse.
-- `record` is the 536-byte ItemData when we were given one, else null and the reply synthesises it.
CREATE TABLE IF NOT EXISTS items (
  item_db_id  INTEGER PRIMARY KEY,
  owner_db_id INTEGER NOT NULL,
  inven_type  INTEGER NOT NULL,
  slot        INTEGER NOT NULL DEFAULT 0,
  template_id INTEGER NOT NULL DEFAULT 0,
  amount      INTEGER NOT NULL DEFAULT 0,
  record      BLOB,
  updated_at  TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_items_container ON items(owner_db_id, inven_type, slot);

-- T42: per-container money and slot count. The item rows live in `items`; this is the rest of
-- what DBS_VIEW_WAREHOUSE has to answer (CurrentMoney and MaxSlotCount), and what
-- SDB_INCREASE_WAREHOUSE_SIZE grows. Keyed the same way as the items:
-- account-keyed for INVEN_TYPE {1,4,5,8,12}, character-keyed otherwise
-- (TransSQLExec::IsAccountDbIdInvenType, mask 0x1132).
CREATE TABLE IF NOT EXISTS warehouses (
  owner_db_id INTEGER NOT NULL,
  inven_type  INTEGER NOT NULL,
  slot_count  INTEGER NOT NULL DEFAULT 0,
  money       INTEGER NOT NULL DEFAULT 0,
  updated_at  TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_db_id, inven_type)
);

-- T42: mail. The Arbiter owns parcels in SQL - World never touches the table, it asks over
-- SDB_LIST_PARCEL / SDB_MAKE_PARCEL / SDB_RECV_PARCEL / SDB_RETURN_PARCEL / SDB_DELETE_PARCEL.
-- `is_read` is what C_SHOW_PARCEL_MESSAGE sets and what S_PARCEL_READ_RECV_STATUS counts;
-- `is_recved` is set once the attachments have been claimed. status/MAIL-WAREHOUSE.md section 3.
CREATE TABLE IF NOT EXISTS parcels (
  parcel_id      INTEGER PRIMARY KEY,
  sender_db_id   INTEGER NOT NULL DEFAULT 0,
  sender_name    TEXT    NOT NULL DEFAULT '',
  receiver_db_id INTEGER NOT NULL DEFAULT 0,
  title          TEXT    NOT NULL DEFAULT '',
  message        TEXT    NOT NULL DEFAULT '',
  money          INTEGER NOT NULL DEFAULT 0,
  parcel_type    INTEGER NOT NULL DEFAULT 0,
  status         INTEGER NOT NULL DEFAULT 0,
  is_read        INTEGER NOT NULL DEFAULT 0,
  is_recved      INTEGER NOT NULL DEFAULT 0,
  created_at     TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_parcels_receiver ON parcels(receiver_db_id);

-- T84: the account s cash-shop packages / benefits. Two client packets read this, both in
-- the LOBBY burst before any character is picked - S_ACCOUNT_PACKAGE_LIST
-- (cap_social4_client frame 14, three rows) and S_ACCOUNT_BENEFIT_LIST (frames 47 and 48,
-- one row then two). A row, not a column on accounts: the list is variable-length and the
-- capture already shows three entries for one account.
-- expires_at is a unix second count; value is the number the benefit list carries in the
-- slot the shipped def calls unk1 (0x23E726 and 0x12867226 in the capture).
CREATE TABLE IF NOT EXISTS account_benefits (
  account_id  INTEGER NOT NULL REFERENCES accounts(id),
  package_id  INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL DEFAULT 0,
  value       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, package_id)
);

-- T71: the trade broker's listings. One row per registered item, from
-- SDB_TRADE_BROKER_REGISTER_ITEM until the seller collects the proceeds or cancels.
-- `trade_id` is the TradeId every broker frame keys on and the client shows; `state` is the
-- lifecycle in status/BROKER-DESIGN.md. The item itself is NOT deleted when it is listed - it
-- moves to inventory type 6, the broker's holding pocket, which is what the op-44 atom in the
-- register batch does. So `items` stays the single source of truth for where a thing is.
CREATE TABLE IF NOT EXISTS broker_listings (
  trade_id      INTEGER PRIMARY KEY,
  seller_db_id  INTEGER NOT NULL DEFAULT 0,
  seller_name   TEXT    NOT NULL DEFAULT '',
  item_db_id    INTEGER NOT NULL DEFAULT 0,
  template_id   INTEGER NOT NULL DEFAULT 0,
  amount        INTEGER NOT NULL DEFAULT 0,
  price         INTEGER NOT NULL DEFAULT 0,
  buyer_db_id   INTEGER NOT NULL DEFAULT 0,
  state         INTEGER NOT NULL DEFAULT 0,
  registered_at TEXT    NOT NULL DEFAULT (datetime('now')),
  sold_at       TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_broker_seller ON broker_listings(seller_db_id);
CREATE INDEX IF NOT EXISTS ix_broker_buyer ON broker_listings(buyer_db_id);
CREATE INDEX IF NOT EXISTS ix_broker_template ON broker_listings(template_id);

-- T42: parcel attachments. Max 5 in the real server (hard-coded, five slots at
-- ParcelData +0xd8 + i*0x1b0); we keep the cap as a check in code rather than in the schema.
CREATE TABLE IF NOT EXISTS parcel_items (
  parcel_id   INTEGER NOT NULL REFERENCES parcels(parcel_id),
  slot        INTEGER NOT NULL,
  item_db_id  INTEGER NOT NULL DEFAULT 0,
  template_id INTEGER NOT NULL DEFAULT 0,
  amount      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (parcel_id, slot)
);

-- T45: the exploration record. C_VISIT_NEW_SECTION carries (mapId, guardId, sectionId) and the
-- Arbiter answers S_VISIT_NEW_SECTION with isFirstVisit; it also pushes the whole list to World
-- as AS_UPDATE_VISITED_SECTION_LIST on every C_LOAD_TOPO_FIN, which is what exploration quests
-- and the teleport-scroll list read. Before T45 that push carried an empty list on every relog.
CREATE TABLE IF NOT EXISTS visited_sections (
  character_id INTEGER NOT NULL,
  map_id       INTEGER NOT NULL,
  guard_id     INTEGER NOT NULL,
  section_id   INTEGER NOT NULL,
  visited_at   TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (character_id, map_id, guard_id, section_id)
);
CREATE INDEX IF NOT EXISTS ix_visited_character ON visited_sections(character_id);

-- T62: the cinematics this character has already watched. C_WATCHED_MOVIES asks for the list
-- once per session and the client replays anything missing from the answer, which is why the
-- intro played again on every relog. The real Arbiter keeps this per ACCOUNT, loaded with
-- spLoadUserWatchedMovies; we key it on the character because that is the row we own.
CREATE TABLE IF NOT EXISTS watched_movies (
  character_id INTEGER NOT NULL,
  movie_id     INTEGER NOT NULL,
  watched_at   TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (character_id, movie_id)
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
        // T45: the exact ParcelData bytes World gave us in SDB_MAKE_PARCEL, so DBS_LIST_PARCEL
        // can list a parcel back byte-exactly (the 0x9e8 interior is not pinned by any capture).
        AddColumnIfMissing("parcels", "record", "BLOB");
        // T59: character money. terasharp.db predates it, and every existing character starts
        // at 0 - which is what they had, since nothing was storing it.
        AddColumnIfMissing("characters", "money", "INTEGER NOT NULL DEFAULT 0");

        // T76: the lobby / friend-panel fields.
        AddColumnIfMissing("characters", "last_login", "TEXT");
        AddColumnIfMissing("characters", "last_world", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "last_guard", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "last_section", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "rest_bonus", "INTEGER NOT NULL DEFAULT 0");

        // T77: EP. Seven columns rather than one blob because World writes them from three
        // different messages and never sends all seven together.
        AddColumnIfMissing("characters", "ep_exp", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_level", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_point", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_daily_exp", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_reserve_bonus", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_daily_limit", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_reset_time", "INTEGER NOT NULL DEFAULT 0");

        // T83: the two crest counters SA_CREST_POINT carries. T77 answered that frame and kept
        // neither, so S_CREST_INFO had nothing but zeros to show.
        AddColumnIfMissing("characters", "crest_point", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "crest_ex_point", "INTEGER NOT NULL DEFAULT 0");
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
            cmd.CommandText = "SELECT * FROM characters WHERE name = $n COLLATE NOCASE";
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
  appearance,details,shape,weapon,body,hand,feet,position,world_blob,money)
VALUES($a,$n,$g,$r,$c,$l,$t,$zone,$x,$y,$z,$ap,$de,$sh,$w,$b,$h,$f,$p,$blob,$money);
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
            cmd.Parameters.AddWithValue("$money", c.Money);          // T59, normally 0
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
    /// The blob is opaque and is <b>read only</b> here — nothing is written back into it (the one
    /// field we ever write into a blob, <c>money</c>, is stamped on the way OUT, in
    /// <see cref="Read"/>; World sends that field back as zero every time). The
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

    // ============================================================ T59: character money

    /// <summary>Current money; 0 for a character that does not exist.</summary>
    public long GetCharacterMoney(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT money FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Apply one op-9 money atom. <paramref name="delta"/> is SIGNED and RELATIVE, not a new
    /// total: <c>Inventory::PrepareMoneyTransaction</c> (WorldServer.exe.c:1407fa050) refuses the
    /// change when <c>*(__int64 *)(inv + 0x78) + amount &lt; 0</c> - i.e. it adds the value to the
    /// money it already holds - then writes that same value to <c>atom + 0x50</c> and pushes the
    /// atom. It also emits NO atom at all when the amount is 0, which an absolute update could
    /// never do. status/INVENTORY-DESIGN.md section 8.
    ///
    /// <para>Clamped at 0. World has already refused anything that would go negative, so a
    /// negative total can only come from a frame World did not send; the clamp is the same
    /// unsigned-index hardening every other packet-derived value gets. Returns the new total,
    /// or 0 when the character does not exist.</para>
    /// </summary>
    public long AddCharacterMoney(long characterId, long delta)
    {
        lock (_lock)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "UPDATE characters SET money = MAX(0, money + $d) WHERE id = $id";
                cmd.Parameters.AddWithValue("$d", delta);
                cmd.Parameters.AddWithValue("$id", characterId);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    _log.LogWarning("AddCharacterMoney: character {Id} not found ({Delta})", characterId, delta);
                    return 0;
                }
            }
        }
        return GetCharacterMoney(characterId);
    }

    /// <summary>
    /// Set money outright, clamped at 0. This is the shape a GM <c>set_money</c> and the
    /// A-&gt;W <c>DBS_UPDATE_USER_MONEY</c> (0x27EA) push both want - World's handler for that
    /// one ends in <c>Inventory::SetMoney</c>, a plain assignment. Returns the new total.
    /// </summary>
    public long SetCharacterMoney(long characterId, long money)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET money = $m WHERE id = $id";
            cmd.Parameters.AddWithValue("$m", money < 0 ? 0L : money);
            cmd.Parameters.AddWithValue("$id", characterId);
            if (cmd.ExecuteNonQuery() != 1)
            {
                _log.LogWarning("SetCharacterMoney: character {Id} not found", characterId);
                return 0;
            }
            return money < 0 ? 0L : money;
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

    /// <summary>
    /// Stamp <c>last_login</c> - T76. Called when the character actually enters the world,
    /// which is the origin S_FRIEND_LIST.lastOnline counts from.
    /// </summary>
    public bool StampLogin(int characterId) => StampLogin(characterId, DateTime.UtcNow);

    /// <summary>
    /// <see cref="StampLogin(int)"/> with an explicit instant, so a test can place a login in
    /// the past. Written in the same text shape SQLite datetime(now) produces, which is what
    /// the reader parses.
    /// </summary>
    public bool StampLogin(int characterId, DateTime whenUtc)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET last_login = $t WHERE id = $id";
            cmd.Parameters.AddWithValue("$t", whenUtc.ToString("yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// Remember where the character is - T76. C_VISIT_NEW_SECTION is the only packet that
    /// carries the trio, and it only fires on a section the character has not seen before, so
    /// this is a last-KNOWN section rather than a live one. That is still what the two list
    /// packets want: S_GET_USER_LIST is drawn at the character-select screen and
    /// S_FRIEND_LIST for a friend who may be offline.
    /// </summary>
    public bool SetLastSection(int characterId, int worldId, int guardId, int sectionId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET last_world = $w, last_guard = $g, " +
                              "last_section = $s WHERE id = $id";
            cmd.Parameters.AddWithValue("$w", worldId);
            cmd.Parameters.AddWithValue("$g", guardId);
            cmd.Parameters.AddWithValue("$s", sectionId);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// Store rested xp - T76. The value is <c>i64 restBonusPoint</c> at frame offset 26 of
    /// SDB_UPDATE_EXP_LEVEL (0x273B); before T76 the handler read it and only logged it, so
    /// the character-select screen always drew 0%.
    /// </summary>
    public bool SetRestBonus(int characterId, long restBonus)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET rest_bonus = $r WHERE id = $id";
            cmd.Parameters.AddWithValue("$r", restBonus);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    // ============================================================ T77: EP (Extra Point)

    /// <summary>
    /// A character's EP panel. The six numbers come from SDB_UPDATE_EXTRA_POINT (0x27B1) in
    /// this order, which is the order its dumper names them:
    /// <c>NewEpExp@0E i64, NewEpLevel@16, NewEpPoint@1A, NewDailyEpExp@1E, NewReserveBonus@22,
    /// NewDailyLimit@26</c>. <see cref="ResetTime"/> is the seventh and arrives separately, on
    /// SDB_UPDATE_DAILY_EXTRA_POINT (0x27AF), so it is defaulted rather than positional.
    /// </summary>
    public sealed record EpRow(
        long EpExp, int EpLevel, int EpPoint, int DailyEpExp,
        int ReserveBonus, int DailyLimit, long ResetTime = 0);

    /// <summary>The whole panel, from SDB_UPDATE_EXTRA_POINT. ResetTime is left alone: that
    /// frame does not carry it.</summary>
    public bool SetCharacterEp(long characterId, EpRow ep)
    {
        ArgumentNullException.ThrowIfNull(ep);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET ep_exp = $e, ep_level = $l, ep_point = $p, "
                            + "ep_daily_exp = $d, ep_reserve_bonus = $r, ep_daily_limit = $m "
                            + "WHERE id = $id";
            cmd.Parameters.AddWithValue("$e", ep.EpExp);
            cmd.Parameters.AddWithValue("$l", ep.EpLevel);
            cmd.Parameters.AddWithValue("$p", ep.EpPoint);
            cmd.Parameters.AddWithValue("$d", ep.DailyEpExp);
            cmd.Parameters.AddWithValue("$r", ep.ReserveBonus);
            cmd.Parameters.AddWithValue("$m", ep.DailyLimit);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Level and point alone, from SDB_UPDATE_PRE_EP_INFO (0x27C1). World sends this
    /// first and the full update second, so writing only these two is deliberate.</summary>
    public bool SetCharacterEpLevel(long characterId, int epLevel, int epPoint)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET ep_level = $l, ep_point = $p WHERE id = $id";
            cmd.Parameters.AddWithValue("$l", epLevel);
            cmd.Parameters.AddWithValue("$p", epPoint);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>The daily reserve bonus and the stamp it resets on, from
    /// SDB_UPDATE_DAILY_EXTRA_POINT (0x27AF).</summary>
    public bool SetCharacterEpDaily(long characterId, int reserveBonus, long resetTime)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET ep_reserve_bonus = $r, ep_reset_time = $t "
                            + "WHERE id = $id";
            cmd.Parameters.AddWithValue("$r", reserveBonus);
            cmd.Parameters.AddWithValue("$t", resetTime);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>What AS_LOAD_EXTRAPOINT_DATA has to answer with, or null for a character that
    /// does not exist. A character who has never touched EP reads back all zeros, which is
    /// exactly what both captured characters send.</summary>
    public EpRow? GetCharacterEp(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ep_exp, ep_level, ep_point, ep_daily_exp, "
                            + "ep_reserve_bonus, ep_daily_limit, ep_reset_time "
                            + "FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new EpRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
                             r.GetInt32(4), r.GetInt32(5), r.GetInt64(6));
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
        if (NoSuchOwner("UpsertQuest", ownerId)) return 0;
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

    // ---- Foreign-key guards (T50) ----

    /// <summary>True when a <c>characters</c> row with this id exists.</summary>
    public bool CharacterExists(long id)
    {
        if (id <= 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM characters WHERE id = $i LIMIT 1";
            cmd.Parameters.AddWithValue("$i", id);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// The guard in front of every per-character INSERT. Returns true when the write must be
    /// dropped because one of the character ids it would store has no row.
    ///
    /// <para>T50. Microsoft.Data.Sqlite issues <c>PRAGMA foreign_keys = 1</c> unless the
    /// connection string says otherwise, so the REFERENCES clauses in this schema ARE enforced -
    /// the comment above the guild tables saying they are documentation only is wrong, and was
    /// written before anyone had watched one fail. An INSERT naming a character that does not
    /// exist therefore throws SqliteException 19, and on the World side that exception unwinds
    /// out of DbProxyHandlers into WorldLink.ReceiveLoop, which has no per-frame catch: one bad
    /// owner id closes the World link and disconnects every player. 19 of the 21 DB-proxy fuzz
    /// failures were exactly this, all of them SDB_UPDATE_USER_ACHIEVEMENT (0x27FA) for a player
    /// id the payload invented. Dropping the write with a warning is what the real Arbiter does
    /// with a row it cannot key - and the caller still sends its DLM ack, which is what keeps the
    /// user's DB queue moving (status/HANDOFF.md section 1).</para>
    /// </summary>
    private bool NoSuchOwner(string what, params long[] ids)
    {
        foreach (long id in ids)
        {
            if (CharacterExists(id)) continue;
            _log.LogWarning("{What}: no character row for id {Id} - write dropped", what, id);
            return true;
        }
        return false;
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
        if (NoSuchOwner("SaveAchievements", ownerId)) return false;
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
        if (NoSuchOwner("AddAccomplishedAchievements", ownerId)) return new List<byte[]>();
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
        if (NoSuchOwner("AddTutorialTip", ownerId)) return false;
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
        if (NoSuchOwner("SetSerenGuide", ownerId)) return;
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
        if (NoSuchOwner("UpsertDungeonCoolTime", ownerId)) return;
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
        if (NoSuchOwner("SetDungeonClearCount", ownerId)) return;
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
        if (NoSuchOwner("UpsertReputation", ownerId)) return;
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


    // =============================================================== T71: the trade broker

    /// <summary>
    /// One broker listing. <paramref name="State"/> is
    /// <see cref="BrokerListed"/> / <see cref="BrokerSold"/> / <see cref="BrokerSellerPaid"/> /
    /// <see cref="BrokerBuyerCollected"/> / <see cref="BrokerCancelled"/>.
    /// </summary>
    public sealed record BrokerListingRow(
        int TradeId, int SellerDbId, string SellerName, long ItemDbId, int TemplateId,
        int Amount, long Price, int BuyerDbId, int State, string RegisteredAt, string SoldAt);

    /// <summary>On sale.</summary>
    public const int BrokerListed = 0;
    /// <summary>Bought, but neither side has collected.</summary>
    public const int BrokerSold = 1;
    /// <summary>The seller has taken the proceeds (SDB_TRADE_BROKER_CALC_SOLD_ITEM step 2).</summary>
    public const int BrokerSellerPaid = 2;
    /// <summary>The buyer has taken the item (SDB_TRADE_BROKER_CALC_BOUGHT_ITEM step 2).</summary>
    public const int BrokerBuyerCollected = 3;
    /// <summary>Withdrawn by the seller (SDB_TRADE_BROKER_UNREGISTER_ITEM step 2).</summary>
    public const int BrokerCancelled = 4;

    /// <summary>Registers a listing and returns its TradeId.</summary>
    public int CreateBrokerListing(int sellerDbId, string sellerName, long itemDbId,
                                   int templateId, int amount, long price)
    {
        ArgumentNullException.ThrowIfNull(sellerName);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO broker_listings(seller_db_id, seller_name, item_db_id, template_id, amount, price) " +
                "VALUES($s,$sn,$i,$t,$a,$p); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$s", sellerDbId);
            cmd.Parameters.AddWithValue("$sn", sellerName);
            cmd.Parameters.AddWithValue("$i", itemDbId);
            cmd.Parameters.AddWithValue("$t", templateId);
            cmd.Parameters.AddWithValue("$a", amount);
            cmd.Parameters.AddWithValue("$p", price);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }

    private const string BrokerColumns =
        "trade_id, seller_db_id, seller_name, item_db_id, template_id, amount, price, " +
        "buyer_db_id, state, registered_at, sold_at";

    private static BrokerListingRow ReadBrokerRow(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4),
        r.GetInt32(5), r.GetInt64(6), r.GetInt32(7), r.GetInt32(8), r.GetString(9), r.GetString(10));

    public BrokerListingRow? GetBrokerListing(int tradeId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {BrokerColumns} FROM broker_listings WHERE trade_id=$id";
            cmd.Parameters.AddWithValue("$id", tradeId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadBrokerRow(r) : null;
        }
    }

    /// <summary>Everything this character put up, newest first - what
    /// <c>C_TRADE_BROKER_REGISTERED_ITEM_LIST</c> shows.</summary>
    public IReadOnlyList<BrokerListingRow> GetBrokerListingsOf(int sellerDbId, int state = BrokerListed)
        => QueryBroker($"SELECT {BrokerColumns} FROM broker_listings WHERE seller_db_id=$k AND state=$st "
                       + "ORDER BY trade_id DESC", sellerDbId, state);

    /// <summary>Everything this character bought and has not collected -
    /// <c>C_TRADE_BROKER_BOUGHT_ITEM_LIST</c>.</summary>
    public IReadOnlyList<BrokerListingRow> GetBrokerPurchasesOf(int buyerDbId, int state = BrokerSold)
        => QueryBroker($"SELECT {BrokerColumns} FROM broker_listings WHERE buyer_db_id=$k AND state=$st "
                       + "ORDER BY trade_id DESC", buyerDbId, state);

    private IReadOnlyList<BrokerListingRow> QueryBroker(string sql, int key, int state)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$st", state);
            var rows = new List<BrokerListingRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(ReadBrokerRow(r));
            return rows;
        }
    }

    /// <summary>
    /// One page of what is on sale. <paramref name="templateId"/> 0 means "everything".
    /// The page window is bounds-checked as UNSIGNED and against the row count, so a
    /// packet-supplied page can never index backwards or off the end
    /// (status/ARBITER-SECURITY-NOTES.md).
    /// </summary>
    public IReadOnlyList<BrokerListingRow> SearchBrokerListings(int templateId, int page, int pageSize)
    {
        if (pageSize <= 0 || pageSize > 500) pageSize = 100;
        long skip = (long)(uint)page * pageSize;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                $"SELECT {BrokerColumns} FROM broker_listings WHERE state=$st "
                + "AND ($t = 0 OR template_id = $t) ORDER BY price ASC, trade_id DESC LIMIT $n OFFSET $o";
            cmd.Parameters.AddWithValue("$st", BrokerListed);
            cmd.Parameters.AddWithValue("$t", templateId);
            cmd.Parameters.AddWithValue("$n", pageSize);
            cmd.Parameters.AddWithValue("$o", skip);
            var rows = new List<BrokerListingRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(ReadBrokerRow(r));
            return rows;
        }
    }

    /// <summary>How many listings a search would return - the total the paged client windows show.</summary>
    public int CountBrokerListings(int templateId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM broker_listings WHERE state=$st AND ($t = 0 OR template_id = $t)";
            cmd.Parameters.AddWithValue("$st", BrokerListed);
            cmd.Parameters.AddWithValue("$t", templateId);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }

    /// <summary>Marks a listing sold. Refuses unless it is still <see cref="BrokerListed"/>, so
    /// two buyers racing the same TradeId cannot both win.</summary>
    public bool SellBrokerListing(int tradeId, int buyerDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE broker_listings SET state=$new, buyer_db_id=$b, " +
                              "sold_at=datetime('now') WHERE trade_id=$id AND state=$old";
            cmd.Parameters.AddWithValue("$new", BrokerSold);
            cmd.Parameters.AddWithValue("$b", buyerDbId);
            cmd.Parameters.AddWithValue("$id", tradeId);
            cmd.Parameters.AddWithValue("$old", BrokerListed);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Moves a listing to a terminal state, only from the state it is allowed to leave.</summary>
    public bool SetBrokerListingState(int tradeId, int fromState, int toState)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE broker_listings SET state=$new WHERE trade_id=$id AND state=$old";
            cmd.Parameters.AddWithValue("$new", toState);
            cmd.Parameters.AddWithValue("$id", tradeId);
            cmd.Parameters.AddWithValue("$old", fromState);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Re-prices a listing that is still on sale.</summary>
    public bool SetBrokerListingPrice(int tradeId, long price)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE broker_listings SET price=$p WHERE trade_id=$id AND state=$st";
            cmd.Parameters.AddWithValue("$p", price);
            cmd.Parameters.AddWithValue("$id", tradeId);
            cmd.Parameters.AddWithValue("$st", BrokerListed);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public bool DeleteBrokerListing(int tradeId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM broker_listings WHERE trade_id=$id";
            cmd.Parameters.AddWithValue("$id", tradeId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

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
            // T44: the item rows outlive the `characters` row unless they are cleared here -
            // and item DB ids never repeat, so an orphan row would surface in a later
            // character's warehouse view if the ids were ever reused.
            using (var items = _db.CreateCommand())
            {
                items.CommandText = "DELETE FROM items WHERE owner_db_id = $id";
                items.Parameters.AddWithValue("$id", id);
                items.ExecuteNonQuery();
            }
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
            // Ownership check first; nothing below runs for someone else's character.
            using (var own = _db.CreateCommand())
            {
                own.CommandText = "SELECT 1 FROM characters WHERE id = $id AND account_id = $a";
                own.Parameters.AddWithValue("$id", id);
                own.Parameters.AddWithValue("$a", accountId);
                if (own.ExecuteScalar() == null)
                {
                    _log.LogWarning("DeleteCharacter: {Id} not found on account {A}", id, accountId);
                    return false;
                }
            }

            // Microsoft.Data.Sqlite enforces foreign keys, and every per-character table added
            // since T17 references characters(id) - delete the children first (live failure
            // 2026-09-14 22:46: SQLite Error 19 on C_DELETE_USER for a character with quest rows).
            using var kids = _db.CreateCommand();
            kids.CommandText = @"
DELETE FROM friends           WHERE character_id = $id OR friend_id = $id;
DELETE FROM blocks            WHERE character_id = $id OR blocked_id = $id;
DELETE FROM friend_groups     WHERE character_id = $id;
DELETE FROM quests            WHERE owner_id = $id;
DELETE FROM achievements      WHERE owner_id = $id;
DELETE FROM achievements_done WHERE owner_id = $id;
DELETE FROM dungeon_cooldowns WHERE owner_id = $id;
DELETE FROM reputations       WHERE owner_id = $id;
DELETE FROM tutorial_tips     WHERE owner_id = $id;
DELETE FROM seren_guide       WHERE owner_id = $id;
DELETE FROM client_settings   WHERE character_id = $id;
DELETE FROM guild_applies     WHERE user_db_id = $id;
DELETE FROM guild_invites     WHERE user_db_id = $id;
DELETE FROM guild_members     WHERE user_db_id = $id;";
            kids.Parameters.AddWithValue("$id", id);
            kids.ExecuteNonQuery();

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

    /// <summary>
    /// Materialise one row. T59: this is also where <c>money</c> is stamped into the world blob
    /// (<see cref="StarterBlob.MoneyOffset"/>), so every caller that ships a 0x2738 -
    /// <c>DbProxyHandlers.OnUserEnterWorld</c> and <c>WorldEntry.BuildCharacterDataPayload</c> -
    /// serves the stored value without a change of its own. The blob byte array belongs to this
    /// record and is freshly read from SQLite on every call, so stamping it mutates nothing shared.
    /// </summary>
    private static CharacterRecord Read(SqliteDataReader r)
    {
        var c = new CharacterRecord
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
            LastLogin = r["last_login"] is string li ? DateTime.Parse(li) : DateTime.MinValue,
            LastWorld = r.GetInt32(r.GetOrdinal("last_world")),
            LastGuard = r.GetInt32(r.GetOrdinal("last_guard")),
            LastSection = r.GetInt32(r.GetOrdinal("last_section")),
            RestBonus = r.GetInt64(r.GetOrdinal("rest_bonus")),
            WorldBlob = r["world_blob"] is byte[] b ? b : null,
            ReturnZone = r.GetInt32(r.GetOrdinal("return_zone")),
            ReturnX = (float)r.GetDouble(r.GetOrdinal("return_x")),
            ReturnY = (float)r.GetDouble(r.GetOrdinal("return_y")),
            ReturnZ = (float)r.GetDouble(r.GetOrdinal("return_z")),
            DungeonId = r.GetInt32(r.GetOrdinal("dungeon_id")),
            InstancePdId = r.GetInt32(r.GetOrdinal("instance_pdid")),
            Money = r.GetInt64(r.GetOrdinal("money")),
        };
        StarterBlob.WriteMoney(c.WorldBlob, c.Money);
        return c;
    }

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
        if (NoSuchOwner("UpsertFriend", characterId, friendId)) return;
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
        if (NoSuchOwner("AddFriend", characterId, friendId)) return false;
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
        if (NoSuchOwner("UpsertFriendGroup", characterId)) return;
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
        if (NoSuchOwner("AddBlock", characterId, blockedId)) return false;
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

    /// <summary>
    /// spChangeGuildName(int guildDbId, nvarchar newName) - T52. Unlike the other three text
    /// setters the name is UNIQUE, so this checks first and answers false when it is taken; the
    /// real Arbiter gets the same answer out of SDB_ASK_CHANGE_GUILD_NAME. Returns false for an
    /// empty name, a name already in use by ANOTHER guild, or a guild id that does not exist.
    /// Renaming a guild to the name it already has succeeds and changes nothing.
    /// </summary>
    public bool RenameGuild(int guildId, string newName)
    {
        string name = Clamp(newName ?? string.Empty, MaxGuildName);
        if (name.Length == 0) return false;
        lock (_lock)
        {
            using (var probe = _db.CreateCommand())
            {
                probe.CommandText = "SELECT guild_id FROM guilds WHERE name = $n";
                probe.Parameters.AddWithValue("$n", name);
                var taken = probe.ExecuteScalar();
                if (taken != null && taken != DBNull.Value && Convert.ToInt32(taken) != guildId) return false;
            }
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET name = $n WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

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
    // ---------------------------------------------------------------- T80: guild war

    /// <summary>One live war. <paramref name="State"/> is the war record s +0xcc field.</summary>
    public sealed record GuildWarRow(
        long WarId, int AttackGuildId, int DefendGuildId, long DeclaredAt, long Money, int State);

    /// <summary>One finished war. <paramref name="Result"/> is S_VIEW_GUILD_WAR.result:
    /// 0 declared, 1 withdrew, 2 surrendered.</summary>
    public sealed record GuildWarHistoryRow(
        long WarId, int AttackGuildId, int DefendGuildId, long DeclaredAt, long EndedAt, int Result);

    /// <summary>
    /// Declare a war. Returns the new war id, or 0 when this exact pair is already at war -
    /// the unique index on (attacker, defender) is what makes the second declare a no-op rather
    /// than a duplicate row.
    /// </summary>
    public long DeclareGuildWar(int attackGuildId, int defendGuildId, long declaredAt, long money,
                                int state = GuildWarStateDeclared)
    {
        lock (_lock)
        {
            using (var ins = _db.CreateCommand())
            {
                ins.CommandText =
                    "INSERT INTO guild_wars(attack_guild_id, defend_guild_id, declared_at, money, state) " +
                    "VALUES($a,$d,$t,$m,$s) ON CONFLICT(attack_guild_id, defend_guild_id) DO NOTHING";
                ins.Parameters.AddWithValue("$a", attackGuildId);
                ins.Parameters.AddWithValue("$d", defendGuildId);
                ins.Parameters.AddWithValue("$t", declaredAt);
                ins.Parameters.AddWithValue("$m", money);
                ins.Parameters.AddWithValue("$s", state);
                if (ins.ExecuteNonQuery() != 1) return 0;   // already at war with them
            }
            using var get = _db.CreateCommand();
            get.CommandText =
                "SELECT war_id FROM guild_wars WHERE attack_guild_id=$a AND defend_guild_id=$d";
            get.Parameters.AddWithValue("$a", attackGuildId);
            get.Parameters.AddWithValue("$d", defendGuildId);
            object? id = get.ExecuteScalar();
            return id is long l ? l : 0;
        }
    }

    /// <summary>The state a freshly declared war carries - the value the capture s window shows.</summary>
    public const int GuildWarStateDeclared = 6;

    /// <summary>Every live war this guild is on either side of, oldest first.</summary>
    public List<GuildWarRow> GetGuildWars(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT war_id, attack_guild_id, defend_guild_id, declared_at, money, state " +
                "FROM guild_wars WHERE attack_guild_id=$g OR defend_guild_id=$g ORDER BY war_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            var rows = new List<GuildWarRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new GuildWarRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2),
                                         r.GetInt64(3), r.GetInt64(4), r.GetInt32(5)));
            return rows;
        }
    }

    /// <summary>The live war between these two, in either direction, or null.</summary>
    public GuildWarRow? GetGuildWarBetween(int guildA, int guildB)
    {
        foreach (var w in GetGuildWars(guildA))
            if ((w.AttackGuildId == guildA && w.DefendGuildId == guildB)
                || (w.AttackGuildId == guildB && w.DefendGuildId == guildA)) return w;
        return null;
    }

    /// <summary>
    /// End a war: the live row moves to guild_war_history with a result. Returns the row that
    /// was moved, or null when there was no such war.
    /// </summary>
    public GuildWarHistoryRow? EndGuildWar(long warId, int result, long endedAt)
    {
        lock (_lock)
        {
            GuildWarRow? live = null;
            using (var get = _db.CreateCommand())
            {
                get.CommandText =
                    "SELECT war_id, attack_guild_id, defend_guild_id, declared_at, money, state " +
                    "FROM guild_wars WHERE war_id=$w";
                get.Parameters.AddWithValue("$w", warId);
                using var r = get.ExecuteReader();
                if (r.Read())
                    live = new GuildWarRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2),
                                           r.GetInt64(3), r.GetInt64(4), r.GetInt32(5));
            }
            if (live == null) return null;

            using (var ins = _db.CreateCommand())
            {
                ins.CommandText =
                    "INSERT INTO guild_war_history(war_id, attack_guild_id, defend_guild_id, " +
                    "declared_at, ended_at, result) VALUES($w,$a,$d,$t,$e,$r)";
                ins.Parameters.AddWithValue("$w", live.WarId);
                ins.Parameters.AddWithValue("$a", live.AttackGuildId);
                ins.Parameters.AddWithValue("$d", live.DefendGuildId);
                ins.Parameters.AddWithValue("$t", live.DeclaredAt);
                ins.Parameters.AddWithValue("$e", endedAt);
                ins.Parameters.AddWithValue("$r", result);
                ins.ExecuteNonQuery();
            }
            using (var del = _db.CreateCommand())
            {
                del.CommandText = "DELETE FROM guild_wars WHERE war_id=$w";
                del.Parameters.AddWithValue("$w", warId);
                del.ExecuteNonQuery();
            }
            return new GuildWarHistoryRow(live.WarId, live.AttackGuildId, live.DefendGuildId,
                                          live.DeclaredAt, endedAt, result);
        }
    }

    /// <summary>Finished wars this guild was on either side of, newest first.</summary>
    public List<GuildWarHistoryRow> GetGuildWarHistory(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT war_id, attack_guild_id, defend_guild_id, declared_at, ended_at, result " +
                "FROM guild_war_history WHERE attack_guild_id=$g OR defend_guild_id=$g " +
                "ORDER BY id DESC";
            cmd.Parameters.AddWithValue("$g", guildId);
            var rows = new List<GuildWarHistoryRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new GuildWarHistoryRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2),
                                                r.GetInt64(3), r.GetInt64(4), r.GetInt32(5)));
            return rows;
        }
    }

    /// <summary>
    /// How many wars this guild has DECLARED - live plus finished. This is what
    /// S_OPEN_GUILD_WAR_WINDOW.thisGuildDeclareCount carries: cap_social4_client frame 3389 has
    /// 1 right after the declare and frame 4453 still has 1 after the withdraw, so it counts
    /// declarations made, not wars currently running.
    /// </summary>
    public int CountGuildWarDeclarations(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT (SELECT COUNT(*) FROM guild_wars WHERE attack_guild_id=$g) + " +
                "(SELECT COUNT(*) FROM guild_war_history WHERE attack_guild_id=$g)";
            cmd.Parameters.AddWithValue("$g", guildId);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

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
        if (NoSuchOwner("AddGuildMember", userDbId)) return 0;
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
        if (NoSuchOwner("InsertGuildApply", userDbId)) return;
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

    // ============================================================ T83: cards

    /// <summary>One owned card. <paramref name="Preset"/> is -1 when it is not mounted.</summary>
    public sealed record CardRow(int CardTemplateId, int Amount, int Preset);

    /// <summary>
    /// SDB_REGISTER_CARD. Adds <paramref name="amount"/> to what this character already has of
    /// that card, which is what "register" means: cap_social4.log seq 7032 registers card 310010
    /// with amount 1 and the reply echoes both back.
    /// </summary>
    public void AddCard(int characterId, int cardTemplateId, int amount)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO cards(character_id, card_template_id, amount) VALUES($c,$t,$a) " +
                "ON CONFLICT(character_id, card_template_id) DO UPDATE SET amount = amount + $a";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$t", cardTemplateId);
            cmd.Parameters.AddWithValue("$a", amount);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>SDB_MOUNT_CARD / SDB_UNMOUNT_CARD. <paramref name="preset"/> -1 unmounts. A card
    /// the character does not own is not created here - the mount would be for a row World has
    /// and we do not, and inventing it hides the disagreement.</summary>
    public bool SetCardPreset(int characterId, int cardTemplateId, int preset)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE cards SET preset=$p WHERE character_id=$c AND card_template_id=$t";
            cmd.Parameters.AddWithValue("$p", preset);
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$t", cardTemplateId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Every card this character owns, lowest template first.</summary>
    public IReadOnlyList<CardRow> GetCards(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT card_template_id, amount, preset FROM cards WHERE character_id=$c " +
                "ORDER BY card_template_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<CardRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new CardRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            return rows;
        }
    }

    // ============================================================ T83: crests

    /// <summary>Record a learned crest. Returns true the first time, like AddVisitedSection.</summary>
    public bool AddCrest(int characterId, int crestId, int value = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO crests(character_id, crest_id, value) VALUES($c,$i,$v) " +
                "ON CONFLICT(character_id, crest_id) DO NOTHING";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$i", crestId);
            cmd.Parameters.AddWithValue("$v", value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The crest ids this character has learned, in id order - the order
    /// S_CREST_INFO's eleven elements are in at cap_social4_client frame 5108.</summary>
    public IReadOnlyList<int> GetCrests(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT crest_id FROM crests WHERE character_id=$c ORDER BY crest_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(r.GetInt32(0));
            return rows;
        }
    }

    /// <summary>SA_CREST_POINT's NewPoint / NewExPoint, which S_CREST_INFO carries at +0x08
    /// and +0x0C.</summary>
    public bool SetCrestPoints(int characterId, int point, int exPoint)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET crest_point=$p, crest_ex_point=$e WHERE id=$id";
            cmd.Parameters.AddWithValue("$p", point);
            cmd.Parameters.AddWithValue("$e", exPoint);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>(point, exPoint); (0, 0) for a character that does not exist.</summary>
    public (int Point, int ExPoint) GetCrestPoints(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT crest_point, crest_ex_point FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
        }
    }

    // ============================================================ T83: guild perks

    /// <summary>One row of <c>spLoadGuildPerkList</c>: <c>{int perkId, tinyint, tinyint}</c>,
    /// which is exactly the six bytes S_GUILD_PERK_LIST's 10-byte element carries after its
    /// [here][next] pair.</summary>
    public sealed record GuildPerkRow(int PerkId, int FlagA, int FlagB);

    /// <summary>spLoadGuildPerkList(int guildDbId).</summary>
    public IReadOnlyList<GuildPerkRow> GetGuildPerks(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT perk_id, flag_a, flag_b FROM guild_perks WHERE guild_id=$g ORDER BY perk_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            var rows = new List<GuildPerkRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new GuildPerkRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            return rows;
        }
    }

    /// <summary>Add or replace one perk row.</summary>
    public void UpsertGuildPerk(int guildId, int perkId, int flagA = 0, int flagB = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO guild_perks(guild_id, perk_id, flag_a, flag_b) VALUES($g,$p,$a,$b) " +
                "ON CONFLICT(guild_id, perk_id) DO UPDATE SET flag_a=$a, flag_b=$b";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$p", perkId);
            cmd.Parameters.AddWithValue("$a", flagA);
            cmd.Parameters.AddWithValue("$b", flagB);
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
        if (NoSuchOwner("AddGuildInvite", userDbId)) return;
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
        // T48: `page` reaches here from a packet. `if (page < 1) page = 1` alone left
        // (page - 1) * pageSize free to overflow - page = int.MaxValue gave OFFSET -40, which
        // SQLite silently treats as 0 and answers with page 1. Clamp so the multiply cannot wrap.
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = GuildHistoryPageSize;
        if (page > MaxPageNumber) page = MaxPageNumber;
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

    /// <summary>
    /// The largest page number any paginated query will act on. T48: the point is not the value
    /// but that <c>page * pageSize</c> must be unable to overflow for ANY int the client sends -
    /// status/ARBITER-SECURITY-NOTES.md bug #2. A million pages is more rows than this server
    /// will ever hold and leaves three orders of magnitude of headroom under int.MaxValue.
    /// </summary>
    public const int MaxPageNumber = 1_000_000;

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


    // ================================================================= T42: items
    // One table for every container. `inven_type` is enum INVEN_TYPE (0 bag, 1 account
    // warehouse, 3 guild, 9 character warehouse, 12 style, 14 equipped) and it is what picks
    // the container, exactly as it does inside the real Arbiter's TransSQLExec::GetInven.
    // status/MAIL-WAREHOUSE.md section 6.

    /// <summary>One stored item row.</summary>
    public sealed record ItemRow(int ItemDbId, long OwnerDbId, int InvenType, int Slot,
                                 int TemplateId, long Amount, byte[]? Record);

    /// <summary>
    /// Insert the row, or overwrite an existing one with the same id. Used for an insert atom
    /// and for the move of an item whose id we have never seen (the bag is not tracked, so the
    /// first thing anyone banks always lands here).
    /// </summary>
    public void UpsertItem(int itemDbId, long ownerDbId, int invenType, int slot,
                           int templateId, long amount, byte[]? record = null)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO items(item_db_id, owner_db_id, inven_type, slot, template_id, amount, record, updated_at) " +
                "VALUES($id,$o,$t,$s,$tpl,$a,$r,datetime('now')) " +
                "ON CONFLICT(item_db_id) DO UPDATE SET owner_db_id=$o, inven_type=$t, slot=$s, " +
                "template_id=CASE WHEN $tpl <> 0 THEN $tpl ELSE template_id END, " +
                "amount=$a, record=COALESCE($r, record), updated_at=datetime('now')";
            cmd.Parameters.AddWithValue("$id", itemDbId);
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            cmd.Parameters.AddWithValue("$s", slot);
            cmd.Parameters.AddWithValue("$tpl", templateId);
            cmd.Parameters.AddWithValue("$a", amount);
            cmd.Parameters.AddWithValue("$r", (object?)record ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>dbo.spUpdateItemOwner. False when no row has that id.</summary>
    public bool MoveItem(int itemDbId, long ownerDbId, int invenType, int slot)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE items SET owner_db_id=$o, inven_type=$t, slot=$s, updated_at=datetime('now') " +
                "WHERE item_db_id=$id";
            cmd.Parameters.AddWithValue("$id", itemDbId);
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            cmd.Parameters.AddWithValue("$s", slot);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Add the atom's signed delta to a row's amount. False when no row has that id.</summary>
    public bool AddItemAmount(int itemDbId, long delta)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE items SET amount = amount + $d, updated_at=datetime('now') WHERE item_db_id=$id";
            cmd.Parameters.AddWithValue("$id", itemDbId);
            cmd.Parameters.AddWithValue("$d", delta);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteItem(int itemDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE item_db_id=$id";
            cmd.Parameters.AddWithValue("$id", itemDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Drop rows a negative delta took to zero. The real server deletes the row in the same
    /// transaction that empties it (DO_TS_DELETE_ITEM is always paired with the amount change);
    /// doing it as a sweep after the batch means the order of the atoms in one message does not
    /// matter.
    /// </summary>
    public int PruneEmptyItems()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE amount <= 0";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every row in one container, in slot order — the order DBS_VIEW_WAREHOUSE lists them.</summary>
    public IReadOnlyList<ItemRow> GetItems(long ownerDbId, int invenType)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT item_db_id, owner_db_id, inven_type, slot, template_id, amount, record " +
                "FROM items WHERE owner_db_id=$o AND inven_type=$t ORDER BY slot, item_db_id";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            var rows = new List<ItemRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                byte[]? rec = r.IsDBNull(6) ? null : (byte[])r["record"];
                rows.Add(new ItemRow(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3),
                                     r.GetInt32(4), r.GetInt64(5), rec));
            }
            return rows;
        }
    }

    public int CountItems(long ownerDbId, int invenType)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE owner_db_id=$o AND inven_type=$t";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }


    // ==================================== T44: the bag ====================================
    // The bag (INVEN_TYPE 0) and the worn slots (14) now live in the SAME `items` table as the
    // warehouse pockets, keyed the same way. That is how the real server's `Items` table works -
    // `spUpdateItemOwner` moves a row between containers and `spUpdateItemPos` moves it inside
    // one - and it is why T42's "skip any atom that touches no warehouse" rule is gone: there is
    // one source of truth now, so every atom lands somewhere.
    //
    // The warehouse pockets are excluded from the inventory load by INVEN_TYPE, using the same
    // {1,3,9,12} set the Arbiter tests with its 0x120A mask. Anything else the character owns -
    // bag, worn gear, and any inventory tab we have not identified - is part of DBS_USER_LOAD_
    // INVENTORY (0x27A4) and is served in (pocket, slot) order, which is the order the capture
    // lists them in.

    /// <summary>INVEN_TYPE values that belong to a warehouse, not to the character's inventory.</summary>
    private const string WarehouseInvenTypes = "(1, 3, 9, 12)";

    /// <summary>
    /// Everything in the character's own inventory - every pocket that is not a warehouse - in
    /// the order DBS_USER_LOAD_INVENTORY lists them: by pocket, then slot. The capture puts the
    /// two bag potions (pocket 0, slots 0 and 1) before the four worn items (pocket 14), which
    /// is exactly this ordering.
    /// </summary>
    public IReadOnlyList<ItemRow> GetInventoryItems(long ownerDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT item_db_id, owner_db_id, inven_type, slot, template_id, amount, record " +
                "FROM items WHERE owner_db_id=$o AND inven_type NOT IN " + WarehouseInvenTypes + " " +
                "ORDER BY inven_type, slot, item_db_id";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            var rows = new List<ItemRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                byte[]? rec = r.IsDBNull(6) ? null : (byte[])r["record"];
                rows.Add(new ItemRow(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3),
                                     r.GetInt32(4), r.GetInt64(5), rec));
            }
            return rows;
        }
    }

    /// <summary>
    /// How many inventory rows the character has. Zero means "never seeded", which is what
    /// DbProxyHandlers.OnLoadInventory uses to decide whether to write the starter kit out.
    /// </summary>
    public int CountInventoryItems(long ownerDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM items WHERE owner_db_id=$o AND inven_type NOT IN " + WarehouseInvenTypes;
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }

    /// <summary>
    /// The row at one (owner, pocket, slot), or null. Needed by the atoms that identify an item
    /// by where it is rather than by its id - a slot swap, and any amount change that arrives
    /// with item DB id 0.
    /// </summary>
    public ItemRow? FindItemAt(long ownerDbId, int invenType, int slot)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT item_db_id, owner_db_id, inven_type, slot, template_id, amount, record " +
                "FROM items WHERE owner_db_id=$o AND inven_type=$t AND slot=$s LIMIT 1";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            cmd.Parameters.AddWithValue("$s", slot);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            byte[]? rec = r.IsDBNull(6) ? null : (byte[])r["record"];
            return new ItemRow(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3),
                               r.GetInt32(4), r.GetInt64(5), rec);
        }
    }

    /// <summary>
    /// T79. The row holding <paramref name="templateId"/> in one pocket, lowest slot first, or
    /// null. The warehouse amount atom (TS op 0x11) names a template and nothing else - its
    /// ItemDbId and its src slot are both 0 in every capture - so this is the only way to find
    /// the row it means. Stacks of one template share a row, which is why "lowest slot" is not
    /// an arbitrary tie-break: there is normally only one.
    /// </summary>
    public ItemRow? FindItemByTemplate(long ownerDbId, int invenType, long templateId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT item_db_id, owner_db_id, inven_type, slot, template_id, amount, record " +
                "FROM items WHERE owner_db_id=$o AND inven_type=$t AND template_id=$tpl " +
                "ORDER BY slot, item_db_id LIMIT 1";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            cmd.Parameters.AddWithValue("$tpl", templateId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            byte[]? rec = r.IsDBNull(6) ? null : (byte[])r["record"];
            return new ItemRow(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3),
                               r.GetInt32(4), r.GetInt64(5), rec);
        }
    }

    /// <summary>
    /// Exchange the positions of two rows (DO_TS_CHANGE_ITEM_POS with both slots occupied - the
    /// op the client sends when you drag one item onto another). Done in one lock so a reader
    /// can never see both items in the same slot.
    /// </summary>
    public bool SwapItemPositions(int itemA, int itemB)
    {
        if (itemA == itemB) return false;
        lock (_lock)
        {
            using var read = _db.CreateCommand();
            read.CommandText = "SELECT item_db_id, inven_type, slot FROM items WHERE item_db_id IN ($a,$b)";
            read.Parameters.AddWithValue("$a", itemA);
            read.Parameters.AddWithValue("$b", itemB);
            var pos = new Dictionary<int, (int Inven, int Slot)>();
            using (var r = read.ExecuteReader())
                while (r.Read()) pos[r.GetInt32(0)] = (r.GetInt32(1), r.GetInt32(2));
            if (!pos.ContainsKey(itemA) || !pos.ContainsKey(itemB)) return false;

            using var w = _db.CreateCommand();
            w.CommandText =
                "UPDATE items SET inven_type=$ta, slot=$sa, updated_at=datetime('now') WHERE item_db_id=$a; " +
                "UPDATE items SET inven_type=$tb, slot=$sb, updated_at=datetime('now') WHERE item_db_id=$b";
            w.Parameters.AddWithValue("$a", itemA);
            w.Parameters.AddWithValue("$b", itemB);
            w.Parameters.AddWithValue("$ta", pos[itemB].Inven);
            w.Parameters.AddWithValue("$sa", pos[itemB].Slot);
            w.Parameters.AddWithValue("$tb", pos[itemA].Inven);
            w.Parameters.AddWithValue("$sb", pos[itemA].Slot);
            w.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>
    /// Write a character's whole inventory in one go, replacing whatever was there. Used once
    /// per character, to seed the starter kit into rows the first time the inventory is loaded;
    /// the 536-byte <c>record</c> is kept verbatim so the rebuilt 0x27A4 is byte-identical to
    /// what StarterInventory produced.
    /// </summary>
    public void ReplaceInventory(long ownerDbId, IReadOnlyList<ItemRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_lock)
        {
            using (var del = _db.CreateCommand())
            {
                del.CommandText =
                    "DELETE FROM items WHERE owner_db_id=$o AND inven_type NOT IN " + WarehouseInvenTypes;
                del.Parameters.AddWithValue("$o", ownerDbId);
                del.ExecuteNonQuery();
            }
            // UpsertItem takes _lock itself; C# locks are re-entrant on the same thread, so
            // this stays one atomic replace from any other thread's point of view.
            foreach (var row in rows)
                UpsertItem(row.ItemDbId, ownerDbId, row.InvenType, row.Slot,
                           row.TemplateId, row.Amount, row.Record);
        }
    }

    /// <summary>Every item the character owns, warehouse pockets included. For DeleteCharacter.</summary>
    public int DeleteAllItems(long ownerDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE owner_db_id=$o";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            return cmd.ExecuteNonQuery();
        }
    }

    // ============================================================ T42: warehouses

    // ---------------------------------------------------------------- T84: account benefits

    /// <summary>One cash-shop package / benefit on an account. <c>ExpiresAt</c> is
    /// unix seconds - the capture s three rows expire 1791961199, 2100409199 and 1786777199.</summary>
    public sealed record AccountBenefitRow(long AccountId, int PackageId, long ExpiresAt, long Value);

    /// <summary>Every benefit on this account, in package order.</summary>
    public List<AccountBenefitRow> GetAccountBenefits(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT account_id, package_id, expires_at, value FROM account_benefits " +
                "WHERE account_id=$a ORDER BY package_id";
            cmd.Parameters.AddWithValue("$a", accountId);
            var rows = new List<AccountBenefitRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new AccountBenefitRow(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)));
            return rows;
        }
    }

    /// <summary>Grant or refresh one benefit. Upsert, so re-granting extends rather than duplicates.</summary>
    public bool GrantAccountBenefit(long accountId, int packageId, long expiresAt, long value = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO account_benefits(account_id, package_id, expires_at, value) " +
                "VALUES($a,$p,$e,$v) ON CONFLICT(account_id, package_id) DO UPDATE SET " +
                "expires_at=excluded.expires_at, value=excluded.value";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$p", packageId);
            cmd.Parameters.AddWithValue("$e", expiresAt);
            cmd.Parameters.AddWithValue("$v", value);
            return cmd.ExecuteNonQuery() >= 1;
        }
    }

    /// <summary>Drop one benefit. Returns true when a row went.</summary>
    public bool RevokeAccountBenefit(long accountId, int packageId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM account_benefits WHERE account_id=$a AND package_id=$p";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$p", packageId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Money and slot count for one container. Zeroes for a container never used.</summary>
    public (long Money, int SlotCount) GetWarehouse(long ownerDbId, int invenType)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT money, slot_count FROM warehouses WHERE owner_db_id=$o AND inven_type=$t";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt32(1)) : (0L, 0);
        }
    }

    /// <summary>dbo.spUpdateWareMoney. The atom's delta is signed: positive stores, negative withdraws.</summary>
    public long AddWarehouseMoney(long ownerDbId, int invenType, long delta)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO warehouses(owner_db_id, inven_type, money, updated_at) VALUES($o,$t,$d,datetime('now')) " +
                "ON CONFLICT(owner_db_id, inven_type) DO UPDATE SET money = money + $d, updated_at=datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.ExecuteNonQuery();
        }
        return GetWarehouse(ownerDbId, invenType).Money;
    }

    /// <summary>dbo.spUpdateWarehouseSlotCount. Returns the new slot count.</summary>
    public int AddWarehouseSlots(long ownerDbId, int invenType, int delta)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO warehouses(owner_db_id, inven_type, slot_count, updated_at) VALUES($o,$t,$d,datetime('now')) " +
                "ON CONFLICT(owner_db_id, inven_type) DO UPDATE SET slot_count = slot_count + $d, updated_at=datetime('now')";
            cmd.Parameters.AddWithValue("$o", ownerDbId);
            cmd.Parameters.AddWithValue("$t", invenType);
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.ExecuteNonQuery();
        }
        return GetWarehouse(ownerDbId, invenType).SlotCount;
    }

    /// <summary>dbo.spClearWarehouse: drop every item in the container and zero its money.</summary>
    public int ClearWarehouse(long ownerDbId, int invenType)
    {
        lock (_lock)
        {
            int n;
            using (var del = _db.CreateCommand())
            {
                del.CommandText = "DELETE FROM items WHERE owner_db_id=$o AND inven_type=$t";
                del.Parameters.AddWithValue("$o", ownerDbId);
                del.Parameters.AddWithValue("$t", invenType);
                n = del.ExecuteNonQuery();
            }
            using var zero = _db.CreateCommand();
            zero.CommandText = "UPDATE warehouses SET money = 0, updated_at=datetime('now') WHERE owner_db_id=$o AND inven_type=$t";
            zero.Parameters.AddWithValue("$o", ownerDbId);
            zero.Parameters.AddWithValue("$t", invenType);
            zero.ExecuteNonQuery();
            return n;
        }
    }

    // =============================================================== T42: parcels

    /// <summary>One stored parcel, without its attachments.</summary>
    public sealed record ParcelRow(int ParcelId, int SenderDbId, string SenderName, int ReceiverDbId,
                                   string Title, string Message, long Money, int ParcelType,
                                   int Status, bool IsRead, bool IsRecved);

    /// <summary>The real server's hard cap: five attachment slots per parcel.</summary>
    public const int MaxParcelAttachments = 5;

    /// <summary>dbo.spCreateParcel. Returns the allocated parcel id.</summary>
    public int CreateParcel(int senderDbId, string senderName, int receiverDbId, string title,
                            string message, long money, int parcelType = 0)
    {
        ArgumentNullException.ThrowIfNull(senderName);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO parcels(sender_db_id, sender_name, receiver_db_id, title, message, money, parcel_type) " +
                "VALUES($sid,$sn,$rid,$t,$m,$money,$pt); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$sid", senderDbId);
            cmd.Parameters.AddWithValue("$sn", senderName);
            cmd.Parameters.AddWithValue("$rid", receiverDbId);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$m", message);
            cmd.Parameters.AddWithValue("$money", money);
            cmd.Parameters.AddWithValue("$pt", parcelType);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }

    public ParcelRow? GetParcel(int parcelId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT parcel_id, sender_db_id, sender_name, receiver_db_id, title, message, money, " +
                "parcel_type, status, is_read, is_recved FROM parcels WHERE parcel_id=$id";
            cmd.Parameters.AddWithValue("$id", parcelId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ParcelRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3),
                                 r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt32(7),
                                 r.GetInt32(8), r.GetInt32(9) != 0, r.GetInt32(10) != 0);
        }
    }

    /// <summary>
    /// T79. When the parcel row was written, UTC. The served ParcelData record carries it as six
    /// u16s at +0xAC and the client computes the retention countdown and the claim delay from it,
    /// so a parcel with no row reads back <see cref="DateTime.UtcNow"/> rather than year 0.
    /// </summary>
    public DateTime GetParcelCreatedUtc(int parcelId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT created_at FROM parcels WHERE parcel_id=$id";
            cmd.Parameters.AddWithValue("$id", parcelId);
            var raw = cmd.ExecuteScalar() as string;
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeUniversal
                       | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : DateTime.UtcNow;
        }
    }

    /// <summary>
    /// ParcelManager::SetParcelRead. Only the receiver may flip the flag - the real Arbiter
    /// applies the same test (User+0x120 == ParcelData+0x50), it just applies it after it has
    /// already sent the body. False when the parcel is missing or belongs to someone else.
    /// </summary>
    public bool SetParcelRead(int parcelId, int receiverDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE parcels SET is_read = 1 WHERE parcel_id=$id AND receiver_db_id=$r";
            cmd.Parameters.AddWithValue("$id", parcelId);
            cmd.Parameters.AddWithValue("$r", receiverDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetParcelRecved(int parcelId, int receiverDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE parcels SET is_recved = 1 WHERE parcel_id=$id AND receiver_db_id=$r";
            cmd.Parameters.AddWithValue("$id", parcelId);
            cmd.Parameters.AddWithValue("$r", receiverDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// The two counters S_PARCEL_READ_RECV_STATUS carries: parcels never opened, and parcels
    /// that have been opened but whose attachments are still unclaimed. The def calls them
    /// <c>totalUnread</c> and <c>readUnclaimedParcels</c>.
    /// </summary>
    public (int Unread, int ReadUnclaimed) GetParcelCounts(int receiverDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT SUM(CASE WHEN is_read = 0 THEN 1 ELSE 0 END), " +
                "       SUM(CASE WHEN is_read <> 0 AND is_recved = 0 THEN 1 ELSE 0 END) " +
                "FROM parcels WHERE receiver_db_id=$r";
            cmd.Parameters.AddWithValue("$r", receiverDbId);
            using var r2 = cmd.ExecuteReader();
            if (!r2.Read()) return (0, 0);
            int unread = r2.IsDBNull(0) ? 0 : r2.GetInt32(0);
            int unclaimed = r2.IsDBNull(1) ? 0 : r2.GetInt32(1);
            return (unread, unclaimed);
        }
    }

    public IReadOnlyList<ParcelRow> GetParcelsFor(int receiverDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT parcel_id, sender_db_id, sender_name, receiver_db_id, title, message, money, " +
                "parcel_type, status, is_read, is_recved FROM parcels WHERE receiver_db_id=$r ORDER BY parcel_id";
            cmd.Parameters.AddWithValue("$r", receiverDbId);
            var rows = new List<ParcelRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new ParcelRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3),
                                       r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt32(7),
                                       r.GetInt32(8), r.GetInt32(9) != 0, r.GetInt32(10) != 0));
            return rows;
        }
    }

    public bool DeleteParcel(int parcelId)
    {
        lock (_lock)
        {
            using (var items = _db.CreateCommand())
            {
                items.CommandText = "DELETE FROM parcel_items WHERE parcel_id=$id";
                items.Parameters.AddWithValue("$id", parcelId);
                items.ExecuteNonQuery();
            }
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM parcels WHERE parcel_id=$id";
            cmd.Parameters.AddWithValue("$id", parcelId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Attach an item to a parcel. Slots 0..4; anything else is rejected.</summary>
    public bool AddParcelItem(int parcelId, int slot, int itemDbId, int templateId, long amount)
    {
        if (slot < 0 || slot >= MaxParcelAttachments) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO parcel_items(parcel_id, slot, item_db_id, template_id, amount) " +
                "VALUES($p,$s,$i,$t,$a) ON CONFLICT(parcel_id, slot) DO UPDATE SET " +
                "item_db_id=$i, template_id=$t, amount=$a";
            cmd.Parameters.AddWithValue("$p", parcelId);
            cmd.Parameters.AddWithValue("$s", slot);
            cmd.Parameters.AddWithValue("$i", itemDbId);
            cmd.Parameters.AddWithValue("$t", templateId);
            cmd.Parameters.AddWithValue("$a", amount);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int CountParcelItems(int parcelId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM parcel_items WHERE parcel_id=$p";
            cmd.Parameters.AddWithValue("$p", parcelId);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }


    // =========================================================== T45: parcels, the World half

    /// <summary>
    /// The exact <c>ParcelData</c> bytes World handed us in <c>SDB_MAKE_PARCEL</c>, or null.
    ///
    /// <para>Kept for the same reason <c>items.record</c> is kept (INVENTORY-DESIGN.md section 7):
    /// the 2536-byte <c>ParcelDataNoMsg</c> interior is not pinned by any capture or dumper, so
    /// the only way <c>DBS_LIST_PARCEL</c> can list a parcel back byte-exactly is to replay the
    /// record World itself produced. A parcel with no stored record is listed from the
    /// synthesised form in <see cref="World.ParcelDbHandlers.BuildParcelDataNoMsg"/>.</para>
    /// </summary>
    public byte[]? GetParcelRecord(int parcelId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT record FROM parcels WHERE parcel_id=$id";
            cmd.Parameters.AddWithValue("$id", parcelId);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return null;
            return (byte[])r.GetValue(0);
        }
    }

    /// <summary>Store the ParcelData bytes for a parcel. A null or empty record clears it.</summary>
    public bool SetParcelRecord(int parcelId, byte[]? record)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE parcels SET record = $b WHERE parcel_id=$id";
            cmd.Parameters.AddWithValue("$id", parcelId);
            if (record is null || record.Length == 0) cmd.Parameters.AddWithValue("$b", DBNull.Value);
            else cmd.Parameters.AddWithValue("$b", record);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// <c>ParcelManager::ReturnParcel</c>: the parcel goes back to whoever sent it. Sender and
    /// receiver swap, and the read/claimed flags reset so it shows up as new mail. False when
    /// the parcel does not exist or is not this character's.
    /// </summary>
    public bool ReturnParcel(int parcelId, int receiverDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE parcels SET receiver_db_id = sender_db_id, sender_db_id = $r, " +
                "is_read = 0, is_recved = 0 WHERE parcel_id=$id AND receiver_db_id=$r";
            cmd.Parameters.AddWithValue("$id", parcelId);
            cmd.Parameters.AddWithValue("$r", receiverDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ================================================= T45: visited sections (exploration)

    /// <summary>
    /// One entry of the visited-section list. <c>C_VISIT_NEW_SECTION</c> carries
    /// <c>(mapId, guardId, sectionId)</c> and <c>User::CanVisitNewSection</c> rejects a
    /// guardId of 0x40 or more, so the trio is small and fixed.
    /// </summary>
    public sealed record VisitedSection(int MapId, int GuardId, int SectionId);

    /// <summary>
    /// Record a visit. Returns true the FIRST time this character sees this section - which is
    /// exactly what <c>S_VISIT_NEW_SECTION.isFirstVisit</c> carries and what the exploration
    /// quests key on.
    /// </summary>
    public bool AddVisitedSection(int characterId, int mapId, int guardId, int sectionId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO visited_sections(character_id, map_id, guard_id, section_id) " +
                "VALUES($c,$m,$g,$s) ON CONFLICT(character_id, map_id, guard_id, section_id) DO NOTHING";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$m", mapId);
            cmd.Parameters.AddWithValue("$g", guardId);
            cmd.Parameters.AddWithValue("$s", sectionId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Every section this character has visited, in insertion order.</summary>
    public IReadOnlyList<VisitedSection> GetVisitedSections(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT map_id, guard_id, section_id FROM visited_sections " +
                "WHERE character_id=$c ORDER BY rowid";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<VisitedSection>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new VisitedSection(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            return rows;
        }
    }

    // ---- T62: watched cinematics, and the name-completion lookup -------------------------

    /// <summary>
    /// Mark a cinematic as seen. Returns true the first time, like AddVisitedSection.
    /// </summary>
    public bool AddWatchedMovie(int characterId, int movieId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO watched_movies(character_id, movie_id) VALUES($c,$m) " +
                "ON CONFLICT(character_id, movie_id) DO NOTHING";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$m", movieId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Every cinematic this character has seen, in the order they were seen.</summary>
    public IReadOnlyList<int> GetWatchedMovies(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT movie_id FROM watched_movies WHERE character_id=$c ORDER BY rowid";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(r.GetInt32(0));
            return rows;
        }
    }

    /// <summary>
    /// Character names starting with <paramref name="prefix"/>, for C_FINDNAME. LIKE wildcards in
    /// the typed text are escaped, so a name containing % or _ cannot turn a keystroke into a
    /// table scan that matches everything. <paramref name="exclude"/> drops the asker's own name,
    /// which the real handler never returns either (it searches friends, the name log and the
    /// guild - none of which contain you).
    /// </summary>
    public IReadOnlyList<string> FindCharacterNamesByPrefix(string prefix, int limit, string? exclude = null)
    {
        if (string.IsNullOrEmpty(prefix) || limit <= 0) return Array.Empty<string>();
        var escaped = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT name FROM characters WHERE name LIKE $p ESCAPE '\\' " +
                "AND ($x IS NULL OR name <> $x) ORDER BY name LIMIT $n";
            cmd.Parameters.AddWithValue("$p", escaped + "%");
            cmd.Parameters.AddWithValue("$x", (object?)exclude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(r.GetString(0));
            return rows;
        }
    }

    public bool HasVisitedSection(int characterId, int mapId, int guardId, int sectionId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM visited_sections " +
                "WHERE character_id=$c AND map_id=$m AND guard_id=$g AND section_id=$s";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$m", mapId);
            cmd.Parameters.AddWithValue("$g", guardId);
            cmd.Parameters.AddWithValue("$s", sectionId);
            return Convert.ToInt64(cmd.ExecuteScalar()!) > 0;
        }
    }

    public void Dispose() => _db.Dispose();
}
