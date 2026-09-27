// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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

    /// <summary>
    /// T159: the rows in use - DefaultSkillSet.xml itself when it is on this machine
    /// (<see cref="World.DatasheetLoader.DefaultSkills"/>), else <see cref="BuiltInTable"/>.
    /// </summary>
    private static IReadOnlyDictionary<(int Race, int Gender, int Class), (int[] Active, int[] Passive)> Table
        => World.DatasheetLoader.DefaultSkills.Value;

    /// <summary><see cref="Rows"/> parsed - the built-in the loader falls back to.</summary>
    public static readonly IReadOnlyDictionary<(int Race, int Gender, int Class), (int[] Active, int[] Passive)> BuiltInTable = Parse();

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
        if (Table.TryGetValue((race, gender, cls), out var v)) { active = v.Active; passive = v.Passive; return true; }
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
public static partial class StarterBlob
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

    /// <summary>
    /// T105. i32 character level at blob offset 204 (0xCC), the field the real Arbiter binds its
    /// <c>userLevel</c> column to when it fills the enter-world record (Arb_part_032.c:17801,
    /// three slots after <c>gender</c> at +0xC4 and <c>class</c> at +0xC8 - our
    /// <see cref="GenderOffset"/> / <see cref="ClassOffset"/> - and in the same call list as
    /// <c>money</c> at +0x1C0).
    /// <para>Pinned across six real 0x2738 blobs in cap_newchar.log and cap_social4.log: 1, 1, 1
    /// for three fresh characters, 8 for two that had levelled, and 70 for "dob" after
    /// /@perfect_level. The neighbours move with it and are NOT level - +208 is hp
    /// (1953 at level 1, 2878 at 8, 85956 at 70) and +216 is mp.</para>
    /// <para>Like money, World never writes this back in a way we can rely on: a GM command that
    /// changes the row leaves the saved blob at the old level, which is why the lobby showed 70
    /// and enter-world served 3 (live 2026-09-19).</para>
    /// </summary>
    public const int LevelOffset = 204;
    /// <summary>Smallest blob that carries the level field.</summary>
    public const int LevelBlockEnd = LevelOffset + 4;

    /// <summary>Level as a blob carries it; 0 when the buffer is too short.</summary>
    public static int ReadLevel(byte[]? blob) =>
        blob == null || blob.Length < LevelBlockEnd ? 0 : BitConverter.ToInt32(blob, LevelOffset);

    /// <summary>
    /// Stamp the row's level into a blob on its way out, exactly as <see cref="WriteMoney"/>
    /// does. A level of 0 is not written - that is "we do not know", not "level 0" - so a row
    /// that predates the level column cannot wipe a blob that has the right value already.
    /// </summary>
    public static bool WriteLevel(byte[]? blob, int level)
    {
        if (blob == null || blob.Length < LevelBlockEnd || level <= 0) return false;
        BitConverter.TryWriteBytes(blob.AsSpan(LevelOffset, 4), level);
        return true;
    }

    /// <summary>
    /// T122. Current hp and mp, the two neighbours of <see cref="LevelOffset"/> that the T105
    /// note above already identifies: +208 reads 1953 at level 1, 2878 at 8 and 85956 at 70
    /// across the same six blobs, and +216 moves with it. They are the ONLY source for these
    /// two - the characters row has no hp or mp column - and the lobby was showing a
    /// hard-coded 100000 for both.
    ///
    /// <para>Read-only, deliberately. The T105 note calls +208 runtime state that World owns;
    /// stamping it on the way out the way <see cref="WriteLevel"/> does would overwrite the
    /// hp a player actually has with whatever the Arbiter last guessed.</para>
    /// </summary>
    public const int HpOffset = 208;
    public const int MpOffset = 216;
    /// <summary>Smallest blob that carries both.</summary>
    public const int VitalsBlockEnd = MpOffset + 4;

    /// <summary>Hp as the blob carries it, or 0 when the buffer is too short.</summary>
    public static long ReadHp(byte[]? blob) =>
        blob == null || blob.Length < VitalsBlockEnd ? 0L : BitConverter.ToUInt32(blob, HpOffset);

    /// <summary>Mp as the blob carries it, or 0 when the buffer is too short.</summary>
    public static int ReadMp(byte[]? blob) =>
        blob == null || blob.Length < VitalsBlockEnd ? 0 : (int)BitConverter.ToUInt32(blob, MpOffset);

    /// <summary>
    /// T105. Exp has NO pinned offset. The same six blobs cannot separate it: /@perfect_level
    /// leaves exp at the level's base, so the level-1 and level-70 blobs of the same character
    /// differ in 640 runs and none of them reads as a total-exp counter. Guessing an offset here
    /// would overwrite hp or mp, so the row's exp column stays unstamped until a capture of a
    /// character that EARNED its way up pins it.
    /// </summary>
    public const int ExpOffset = -1;
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

    // --- bag size (T150) ---
    /// <summary>
    /// i32 MaxInvenSlotCount at blob offset 15088 (0x3AF0): the real Arbiter's
    /// User::UpdateMaxInvenSlotCountNoLock (Arb_part_030.c:12399) stores it at User + 0x3BA0, and
    /// the world blob is User + 0xB0 (the same base that puts the player id at User + 0x120 =
    /// blob + 0x70, our <see cref="PlayerIdOffset"/>). 40 in <c>data/starter_blob.bin</c>.
    /// </summary>
    public const int MaxInvenSlotCountOffset = 0x3AF0;
    /// <summary>i32 expandInvenCount at blob offset 0x3B00 (User + 0x3BB0,
    /// User::UpdateExpandInvenCountNoLock, Arb_part_030.c:10492). 0 in the starter blob.</summary>
    public const int ExpandInvenCountOffset = 0x3B00;

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
    /// <summary>
    /// T209: the file is now an OVERRIDE, not a requirement. When one of the candidate paths has
    /// it, those bytes win - an operator with a capture of their own build can still drop it in.
    /// Otherwise the record is generated (<see cref="Generate"/>), which is what a stock install
    /// does. Either way the caller gets 15312 bytes and <see cref="Build"/> patches the same
    /// fields on top, so nothing downstream can tell the difference.
    /// </summary>
    public static byte[] LoadTemplate(Seed? seed = null)
        => LoadTemplateFile() ?? Generate(seed ?? new Seed());

    /// <summary>The override, or null when no candidate path has a well-formed file. A file of
    /// the wrong length is a bad deploy and still throws - silently generating over it would hide
    /// a truncated copy.</summary>
    public static byte[]? LoadTemplateFile()
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
        return null;
    }

    /// <summary>For tests: forget a cached override so the next load re-resolves.</summary>
    internal static void ResetTemplateCacheForTests() => _cached = null;

    /// <summary>Test seam: inject a template instead of reading it from disk.</summary>
    internal static void SetTemplateForTest(byte[]? template) => _cached = template;

    private static IEnumerable<string?> CandidatePaths()
    {
        yield return TerasConfig.Get("TERASHARP_STARTER_BLOB");

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "data", "starter_blob.bin");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        var root = TerasConfig.Get("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
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
public sealed partial class CharacterStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger _log;
    private readonly object _lock = new();

    public CharacterStore(string path, ILogger log)
    {
        _log = log;
        DbPath = path;
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Migrate();
        _log.LogInformation("CharacterStore open: {Path}", path);
    }

    /// <summary>T206: the file this store was opened on, so the admin tool can report where the
    /// database lives and how big it has grown without guessing from configuration.</summary>
    public string DbPath { get; } = string.Empty;

    /// <summary>T206: one table and its row count, for the admin Settings screen.</summary>
    public sealed record TableRow(string Name, long Rows);

    /// <summary>T206: the whole database report - byte size from the page geometry (which is
    /// correct even while a WAL is open, unlike a bare FileInfo on the main file), the schema
    /// version and every table with its row count.</summary>
    public sealed record DatabaseStats(string Path, long Bytes, long PageCount, long PageSize,
        long UserVersion, long WalBytes, IReadOnlyList<TableRow> Tables);

    /// <summary>T206: read the database report. Never throws - a refused pragma or a table that
    /// vanished mid-walk yields a zero rather than failing the whole screen.</summary>
    public DatabaseStats GetDatabaseStats()
    {
        lock (_lock)
        {
            long pages = Scalar("PRAGMA page_count"), size = Scalar("PRAGMA page_size");
            long version = Scalar("PRAGMA user_version");
            var names = new List<string>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) names.Add(r.GetString(0));
            }
            var tables = new List<TableRow>(names.Count);
            foreach (string name in names)
            {
                // The name came from sqlite_master, so it cannot be hostile, but quote it anyway.
                tables.Add(new TableRow(name, Scalar("SELECT COUNT(*) FROM \"" + name.Replace("\"", "\"\"") + "\"")));
            }
            long wal = 0;
            try
            {
                var info = new FileInfo(DbPath + "-wal");
                if (info.Exists) wal = info.Length;
            }
            catch { }
            return new DatabaseStats(DbPath, pages * size, pages, size, version, wal, tables);
        }
    }

    /// <summary>One number from a statement, or 0. Caller holds <c>_lock</c>.</summary>
    private long Scalar(string sql)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            object? value = cmd.ExecuteScalar();
            return value == null || value is DBNull ? 0 : Convert.ToInt64(value);
        }
        catch { return 0; }
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
  -- T138c: the battleground rating. Every S_BATTLE_FIELD_RESULT moves it by a random 5..12,
  -- up on a win and down on a loss, floored at 0 - see World/BattlegroundRating.cs. It is a
  -- LEADERBOARD number and nothing else: S_PVP_RANKING_LIST's `rating` field renders it, and
  -- the matchmaker never reads it (the T138 brief: no MMR).
  bg_rating INTEGER NOT NULL DEFAULT 0,
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

-- T203: the planet-wide server-first claim. Retail keeps the same thing in the
-- ServerAchievement table (GameDatabaseDefinition.xml:1829, described there as the
-- server-first achievement list) with just achievementId and userDbId, loaded once into
-- GServerAchievementManager and consulted by User::AccomplishAchievement before it grants an
-- achievement whose sheet serverUnique is non-zero. The achievement id is the PRIMARY KEY,
-- which is what makes INSERT OR IGNORE the whole first-claimant-wins rule: exactly one row can
-- ever exist. party_id is only meaningful for serverUnique 2 and 3, where the same PARTY that
-- first claimed it may still be granted it (CanAccomplishPartyAchievementNoLock,
-- Arb_part_056.c:16992). No foreign key: retail's table is keyed on the achievement, not the
-- character, and deleting a character does not release a server first.
CREATE TABLE IF NOT EXISTS server_achievements (
  achievement_id INTEGER PRIMARY KEY,
  owner_id       INTEGER NOT NULL,
  party_id       INTEGER NOT NULL DEFAULT 0,
  created_at     TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- T25: dungeon cool times. `record` is the raw 52-byte DungeonCoolTimeElem World sends in
-- SA_UPDATE_DUNGEON_COOLTIME (0x13B6), kept verbatim and handed straight back in
-- DBS_LOAD_DUNGEON_COOL_TIME (0x2868) list 0 and in the AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD
-- (0x148D) pushes. `clear_count` is the separate scalar SA_UPDATE_DUNGEON_CLEAR_COUNT (0x13B7)
-- carries. T134 finally observed the client-facing list: classic_live2 records 13158 and
-- 19420 pin S_DUNGEON_CLEAR_COUNT_LIST, so GetDungeonClearCounts below now serves it. The
-- World-side ClearCountList inside DBS_LOAD_DUNGEON_COOL_TIME is still unobserved and still
-- answered empty. status/DUNGEON-COOLTIME.md, status/MULTIWORLD-DESIGN.md.
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
-- Tutorial tips: both ADD (0x286E) and DONT_REPEAT (0x2870) increment a popup count.
-- LOAD (0x2873) serves (tip_id, popup_count) in ascending tip_id order (T191).
CREATE TABLE IF NOT EXISTS tutorial_tips (
  owner_id   INTEGER NOT NULL REFERENCES characters(id),
  tip_id     INTEGER NOT NULL,
  popup_count INTEGER NOT NULL DEFAULT 1,
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

-- T199: spCreateBattleFieldLog returns a durable identity, separate from the World instance id.
CREATE TABLE IF NOT EXISTS battlefield_logs (
  log_id INTEGER PRIMARY KEY AUTOINCREMENT,
  battlefield_id INTEGER NOT NULL, template_id INTEGER NOT NULL,
  blue_party INTEGER NOT NULL, red_party INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
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
-- T98: the guild-quest board. PlanetDB keeps GuildQuest rows per guild and
-- GuildQuestManager runs them; only the LIST was ever captured (the start/finish packets sit
-- behind a 7-day cooldown), so this is the state S_GUILD_QUEST_LIST reads and nothing more.
-- The catalogue itself is sheet data, not rows - see GuildPackets.GuildQuestCatalogue.
-- T101: every write the admin web tool makes. WEBADMIN-DESIGN.md section 2 notes that the
-- retail tool logs only a free-text reason and never stamps WHO did it; this keeps the source
-- IP and the result code as well, which is strictly more than the original had.
-- T101b: character restrictions (bans and mutes). WEBADMIN-DESIGN.md section 4 lists this as
-- one of the four new tables; the retail shape is (character, type, level, until, reason).
CREATE TABLE IF NOT EXISTS restrictions (
  character_id INTEGER NOT NULL REFERENCES characters(id),
  type         INTEGER NOT NULL,            -- 1 ban, 2 mute
  level        INTEGER NOT NULL DEFAULT 0,
  until        INTEGER NOT NULL DEFAULT 0,  -- unix seconds; 0 = permanent
  reason       TEXT    NOT NULL DEFAULT (''),
  set_at       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, type)
);

-- T101b: where a soft-deleted character s items wait out the grace window, so a restore can
-- put them back. The retail tool calls this DeletedItemData.
CREATE TABLE IF NOT EXISTS deleted_items (
  item_db_id   INTEGER PRIMARY KEY,
  character_id INTEGER NOT NULL,
  inven_type   INTEGER NOT NULL DEFAULT 0,
  slot         INTEGER NOT NULL DEFAULT 0,
  template_id  INTEGER NOT NULL DEFAULT 0,
  amount       INTEGER NOT NULL DEFAULT 0,
  record       BLOB,
  deleted_at   INTEGER NOT NULL DEFAULT 0
);

-- T101c: scheduled and instant announces. WEBADMIN-DESIGN.md section 4 lists this as one of
-- the four new tables the tool needs; it is the last of them. An INSTANT announce is sent and
-- logged and never lands here - only the scheduled ones are rows, because only they have to
-- survive a restart.
CREATE TABLE IF NOT EXISTS announces (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  text      TEXT    NOT NULL,
  start_at  INTEGER NOT NULL DEFAULT 0,   -- unix seconds; when it first goes out
  end_at    INTEGER NOT NULL DEFAULT 0,   -- 0 = no end
  interval_sec INTEGER NOT NULL DEFAULT 0,-- 0 = once
  last_sent INTEGER NOT NULL DEFAULT 0,
  enabled   INTEGER NOT NULL DEFAULT 1,
  created_by TEXT   NOT NULL DEFAULT (''),
  created_at INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS admin_log (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  at         INTEGER NOT NULL,            -- unix seconds
  source_ip  TEXT    NOT NULL DEFAULT (''),
  action     TEXT    NOT NULL,
  target     TEXT    NOT NULL DEFAULT (''),
  reason     TEXT    NOT NULL DEFAULT (''),
  result     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS guild_quests (
  guild_id      INTEGER NOT NULL REFERENCES guilds(guild_id),
  quest_id      INTEGER NOT NULL,
  status        INTEGER NOT NULL DEFAULT 0,   -- 0 available, 1 running
  started_at    INTEGER NOT NULL DEFAULT 0,   -- unix seconds
  ends_at       INTEGER NOT NULL DEFAULT 0,   -- unix seconds; remainSec is this minus now
  starter_db_id INTEGER NOT NULL DEFAULT 0,
  progress      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, quest_id)
);

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

-- T95: the guild wanted board - the looking-for-a-guild ads S_REPLY_GUILD_WANTED_WRITING_LIST
-- lists. One row per character, because C_REQUEST_SET_GUILD_WANTED_WRITING has no ad id and the
-- capture's reply carries the poster's UserDbId as the element key. `written_at` is both the
-- WritingDate the row shows and the start of the posting cooldown: cap_social3_client2 frame
-- 2140 comes back with CanBeWriting 0 and RemainTime 86400 immediately after frame 2137 posted.
CREATE TABLE IF NOT EXISTS guild_wanted (
  user_db_id            INTEGER NOT NULL PRIMARY KEY REFERENCES characters(id),
  guild_size            INTEGER NOT NULL DEFAULT 0,
  guild_preference      INTEGER NOT NULL DEFAULT 0,
  promotion_str         TEXT    NOT NULL DEFAULT '',
  written_at            INTEGER NOT NULL DEFAULT 0
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
  account_id       INTEGER NOT NULL,
  card_template_id INTEGER NOT NULL,
  amount           INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (account_id, card_template_id)
);
-- ix_cards_account is created by MigrateCardsToAccount, not here: on an upgrade `cards` still
-- has T83's character_id shape when this block runs and the index would fail on a column that
-- does not exist yet.

-- T89: the In-Game Operation Tool's custom bookmarks - the teleport shortcuts a GM saves.
-- C_ADMIN_ADD_CUSTOM_BOOKMARK carries (index, zone, x, y, z, name) and the list that comes back
-- carries the coordinates TRUNCATED TO WHOLE NUMBERS: cap_final_gm_client2 frame 1167 sends
-- 16920.03 / 1232.46 / -4427.045 and frame 1168 returns 16920 / 1232 / -4427. Per account,
-- because the tool is opened from an account and not from a character.
-- T99: the string a player writes onto an item - C_SET_ITEM_STRING for a blank one and
-- C_REWRITE_ITEM_STRING for one already written. Keyed on the item, because both packets
-- name the item by db id and nothing else reads it.
CREATE TABLE IF NOT EXISTS item_strings (
  item_db_id     INTEGER NOT NULL PRIMARY KEY,
  text           TEXT    NOT NULL DEFAULT '',
  written_by     INTEGER NOT NULL DEFAULT 0,
  written_at     INTEGER NOT NULL DEFAULT 0
);

-- T99: the in-world message boards C_WRITE_BOARD posts to and S_BOARD_ITEM_LIST lists.
-- BoardId is the board's own id, which the client sends in every one of the three packets.
CREATE TABLE IF NOT EXISTS board_posts (
  post_id        INTEGER PRIMARY KEY AUTOINCREMENT,
  board_id       INTEGER NOT NULL,
  writer_id      INTEGER NOT NULL DEFAULT 0,
  writer         TEXT    NOT NULL DEFAULT '',
  contents       TEXT    NOT NULL DEFAULT '',
  written_at     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_board_posts ON board_posts(board_id, post_id);

CREATE TABLE IF NOT EXISTS gm_bookmarks (
  account_id     INTEGER NOT NULL,
  bookmark_index INTEGER NOT NULL,
  zone           INTEGER NOT NULL DEFAULT 0,
  x              REAL    NOT NULL DEFAULT 0,
  y              REAL    NOT NULL DEFAULT 0,
  z              REAL    NOT NULL DEFAULT 0,
  name           TEXT    NOT NULL DEFAULT '',
  PRIMARY KEY (account_id, bookmark_index)
);

-- T86: and the per-CHARACTER half. SDB_MOUNT_CARD is the only card frame that names a
-- character (UserDbId at frame 0x12), and it names a preset with it, so a mount is one card in
-- one slot of one character's preset - not a column on the account-wide collection.
CREATE TABLE IF NOT EXISTS card_mounts (
  character_id     INTEGER NOT NULL,
  preset_index     INTEGER NOT NULL,
  card_template_id INTEGER NOT NULL,
  PRIMARY KEY (character_id, preset_index, card_template_id)
);
CREATE INDEX IF NOT EXISTS ix_card_mounts_character ON card_mounts(character_id);

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

-- T183: User::CacheCampTeleport / spLoadVisitedCamp and spAddVisitedCamp.
-- Separate from visited_sections: C_TEL_CAMP carries one signed camp id.
CREATE TABLE IF NOT EXISTS visited_camps (
  character_id INTEGER NOT NULL,
  camp_id      INTEGER NOT NULL,
  PRIMARY KEY (character_id, camp_id)
);

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

-- T104: and the ACCOUNT-scoped one, which is what the real Arbiter actually keeps. The whole
-- chain is on the Account object, not the User: Handler_SA_WATCH_MOVIE (Arb_part_062.c:18768)
-- takes User+0x3f40 - the account - and calls Account::InsertWatchedMovieWithLock(movieId),
-- which de-duplicates against the std::set at Account+0x3000 and only then runs
-- dbo.spInsertUserWatchedMovie; the read is Account::CachedWatchedMoviesWithLock
-- (Arb_part_061.c:6450) behind a once-per-account latch at Account+0x2ff8, running
-- dbo.spLoadUserWatchedMovies. T62 keyed the table on the character because that was the row
-- we owned; with SA_WATCH_MOVIE landing (T104) the account is the right key, and a second
-- character on the same account no longer sits through the intro.
CREATE TABLE IF NOT EXISTS watched_movies_account (
  account_id INTEGER NOT NULL,
  movie_id   INTEGER NOT NULL,
  watched_at TEXT    NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (account_id, movie_id)
);

-- T115: the LogDB writes World sends and the Arbiter used to throw away - SDB_ITEM_TRADE_LOG,
-- SDB_ADD_PVP_USER_LOG, SDB_ADD_PK_USER_LOG, SDB_ADD_GROUP_DUEL_USER_LOG and SDB_CASH_ITEM_LOG.
-- One normalized row per logged event, in the shape the retail log tool groups by, so a new
-- log opcode adds an action and not a table. `extra` is a flat JSON object for whatever that
-- frame carried that has no column - GameLogPackets.Json writes it.
-- status/GAME-LOG.md.
CREATE TABLE IF NOT EXISTS game_log (
  log_id       INTEGER PRIMARY KEY AUTOINCREMENT,
  logged_at    INTEGER NOT NULL DEFAULT 0,
  category     TEXT    NOT NULL DEFAULT '',
  action       TEXT    NOT NULL DEFAULT '',
  account_id   INTEGER NOT NULL DEFAULT 0,
  character_id INTEGER NOT NULL DEFAULT 0,
  target_id    INTEGER NOT NULL DEFAULT 0,
  item_db_id   INTEGER NOT NULL DEFAULT 0,
  template_id  INTEGER NOT NULL DEFAULT 0,
  amount       INTEGER NOT NULL DEFAULT 0,
  money        INTEGER NOT NULL DEFAULT 0,
  extra        TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_game_log_char ON game_log(character_id, log_id);
CREATE INDEX IF NOT EXISTS ix_game_log_acct ON game_log(account_id, log_id);
CREATE INDEX IF NOT EXISTS ix_game_log_cat ON game_log(category, log_id);
CREATE INDEX IF NOT EXISTS ix_game_log_time ON game_log(logged_at);

-- T147: crafting. The real Arbiter keeps both per character (User+0x120 is the key it binds):
-- dbo.spLoadItemRecipe returns recipe id, extract bit, learn time and bookmark bit, and
-- dbo.spLoadSkillProf returns skill-prof id and value. status/CRAFTING.md.
CREATE TABLE IF NOT EXISTS item_recipes (
  character_id INTEGER NOT NULL,
  recipe_id    INTEGER NOT NULL,
  extract      INTEGER NOT NULL DEFAULT 0,
  bookmark     INTEGER NOT NULL DEFAULT 0,
  learned_at   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, recipe_id)
);
CREATE TABLE IF NOT EXISTS skill_profs (
  character_id  INTEGER NOT NULL,
  skill_prof_id INTEGER NOT NULL,
  value         INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, skill_prof_id)
);
-- T147: gathering proficiency. Not a skill_profs row - four ints of UserData the real Arbiter
-- binds from its profMineral/profBug/profEnergy/profHerb columns at enter-world. kind 0..3 in
-- that order; only rows written by S_UPDATE_PROF_* exist, and only they are stamped.
CREATE TABLE IF NOT EXISTS gathering_profs (
  character_id INTEGER NOT NULL,
  kind         INTEGER NOT NULL,
  value        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, kind)
);

-- T154: the guilds holding a Civil Unrest city, per league and season - what
-- SDB_LOAD_CITY_GUILD_INFO (0x2954) is answered from. Empty means no owning guild, which is what
-- the real Arbiter answered in every capture; a CU winner can be written here later. The two
-- times are World's i64s, stored as they come (their encoding is not pinned yet).
CREATE TABLE IF NOT EXISTS city_guild (
  league_id          INTEGER NOT NULL,
  season_id          INTEGER NOT NULL,
  guild_db_id        INTEGER NOT NULL,
  tower_build_time   INTEGER NOT NULL DEFAULT 0,
  tower_destroy_time INTEGER NOT NULL DEFAULT 0,
  total_kill         INTEGER NOT NULL DEFAULT 0,
  total_death        INTEGER NOT NULL DEFAULT 0,
  total_destroy      INTEGER NOT NULL DEFAULT 0,
  maintain_bonus     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (league_id, season_id, guild_db_id)
);

-- T156: the Vanguard Initiative (event matching), the Arbiter's half. World builds every client
-- packet of that window itself from its own EventMatching datasheet; what it keeps in the
-- Arbiter is per-character progress and three reset stamps.
-- daily_event is spLoadUserDailyEventCompleted's row: the five completion counts
-- (DailyEventCompletionInfo, SDB_UPDATE_USER_DAILY_EVENT_COUNT's 20-byte record), the
-- character's extra-reward reset stamp (unix seconds, as World sends it), and the bool + int
-- SDB_UPDATE_GET_EXTRA_REWARD writes. SDB_LOAD_USER_DAILY_EVENT answers from it; no row is the
-- never-played answer (success 0, all zero).
CREATE TABLE IF NOT EXISTS daily_event (
  character_id         INTEGER PRIMARY KEY,
  count0               INTEGER NOT NULL DEFAULT 0,
  count1               INTEGER NOT NULL DEFAULT 0,
  count2               INTEGER NOT NULL DEFAULT 0,
  count3               INTEGER NOT NULL DEFAULT 0,
  count4               INTEGER NOT NULL DEFAULT 0,
  extra_reward_reset   INTEGER NOT NULL DEFAULT 0,
  got_extra_reward     INTEGER NOT NULL DEFAULT 0,
  extra_reward_value   INTEGER NOT NULL DEFAULT 0
);

-- T156: per-character add-reward receive counts (SDB_UPDATE / SDB_LOAD_ADDITIONAL_REWARD_RECV_COUNT).
-- World's daily add-reward reset (SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME) empties it.
CREATE TABLE IF NOT EXISTS event_matching_reward (
  character_id  INTEGER NOT NULL,
  event_id      INTEGER NOT NULL,
  acquire_num   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, event_id)
);

-- T167: what DBS_RESPONSE_CARD_DATA (0x2987) is answered from, beside `cards` and `card_mounts`.
-- card_info is the account's preset count and collection-book level/points (no row = 1, 1, 0,
-- the captured defaults); card_combines the activated combine lists (id -> level). The selected
-- preset is per character: characters.card_preset_index.
CREATE TABLE IF NOT EXISTS card_info (
  account_id     INTEGER PRIMARY KEY,
  preset_amount  INTEGER NOT NULL DEFAULT 1,
  book_level     INTEGER NOT NULL DEFAULT 1,
  book_point     INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS card_combines (
  account_id       INTEGER NOT NULL,
  combine_list_id  INTEGER NOT NULL,
  level            INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, combine_list_id)
);

-- T167: learned EP perks per page (DBS_USER_LOAD_EP_PERK's five page maps). The page scalars
-- are characters.ep_used_point / ep_current_page / ep_max_page / ep_pre_level / ep_pre_total_point.
CREATE TABLE IF NOT EXISTS ep_perks (
  character_id  INTEGER NOT NULL,
  page_index    INTEGER NOT NULL,
  perk_id       INTEGER NOT NULL,
  perk_level    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, page_index, perk_id)
);

-- T167: skill polishing - DBS_LOAD_SKILL_POLISHING's level/point/total/exp, its option list
-- (polishing id, effect id) -> applied, and its level list polishing id -> effect id.
CREATE TABLE IF NOT EXISTS skill_polishing (
  character_id  INTEGER PRIMARY KEY,
  level         INTEGER NOT NULL DEFAULT 0,
  point         INTEGER NOT NULL DEFAULT 0,
  total_point   INTEGER NOT NULL DEFAULT 0,
  exp           INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS skill_polishing_options (
  character_id  INTEGER NOT NULL,
  polishing_id  INTEGER NOT NULL,
  effect_id     INTEGER NOT NULL,
  applied       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, polishing_id, effect_id)
);
CREATE TABLE IF NOT EXISTS skill_polishing_levels (
  character_id  INTEGER NOT NULL,
  polishing_id  INTEGER NOT NULL,
  effect_id     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, polishing_id)
);

-- T167: SDB_UPDATE_DUNGEON_RANK_RECORD - World's best point / time per character, dungeon and
-- season, with the last run's breakdown. The PvE board ranks by these points.
CREATE TABLE IF NOT EXISTS dungeon_rank_records (
  character_id  INTEGER NOT NULL,
  dungeon_id    INTEGER NOT NULL,
  season        INTEGER NOT NULL,
  top_point     INTEGER NOT NULL DEFAULT 0,
  top_time      INTEGER NOT NULL DEFAULT 0,
  play_date     INTEGER NOT NULL DEFAULT 0,
  new_score     INTEGER NOT NULL DEFAULT 0,
  time_point    INTEGER NOT NULL DEFAULT 0,
  kill_point    INTEGER NOT NULL DEFAULT 0,
  bonus_point   INTEGER NOT NULL DEFAULT 0,
  mvp_name      TEXT    NOT NULL DEFAULT '',
  PRIMARY KEY (character_id, dungeon_id, season)
);

-- T168: VIP (DBS_LOAD_USER_VIP_INFO / ADD_VIP_GAME_EXP), per account.
CREATE TABLE IF NOT EXISTS vip_info (
  account_id       INTEGER PRIMARY KEY,
  pub_exp          INTEGER NOT NULL DEFAULT 0,
  game_exp         INTEGER NOT NULL DEFAULT 0,
  token_amount     INTEGER NOT NULL DEFAULT 0,
  last_reset_time  INTEGER NOT NULL DEFAULT 0,
  reset_count      INTEGER NOT NULL DEFAULT 0
);
-- T168: SDB_USER_LEARN_HIDE_PASSIVE_SKILL.
CREATE TABLE IF NOT EXISTS hidden_passives (
  character_id  INTEGER NOT NULL,
  passive_id    INTEGER NOT NULL,
  PRIMARY KEY (character_id, passive_id)
);
-- T168: SDB_ADD_SERVANT. The servant loads (SA_LOAD_SERVANT_*) are still the replay table's.
CREATE TABLE IF NOT EXISTS servants (
  servant_db_id  INTEGER PRIMARY KEY AUTOINCREMENT,
  character_id   INTEGER NOT NULL,
  type           INTEGER NOT NULL DEFAULT 0,
  template_id    INTEGER NOT NULL DEFAULT 0,
  name           TEXT    NOT NULL DEFAULT '',
  energy         INTEGER NOT NULL DEFAULT 0,
  period         INTEGER NOT NULL DEFAULT 0
);
-- T168: collection-book rewards received (DBS_RESPONSE_CARD_DATA's last list).
CREATE TABLE IF NOT EXISTS card_book_rewards (
  account_id  INTEGER NOT NULL,
  reward_id   INTEGER NOT NULL,
  PRIMARY KEY (account_id, reward_id)
);
-- T170: EventSystemManager's progress map, keyed like the Arbiter's (event, user, account);
-- spUpdateUserEventSystemProgressInfo's six columns. Decompile-derived: no capture.
CREATE TABLE IF NOT EXISTS eventsystem_progress (
  event_id    INTEGER NOT NULL,
  user_id     INTEGER NOT NULL,
  account_id  INTEGER NOT NULL,
  value       INTEGER NOT NULL DEFAULT 0,
  flag1       INTEGER NOT NULL DEFAULT 0,
  flag2       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (event_id, user_id, account_id)
);
");
        // CREATE TABLE IF NOT EXISTS does nothing to a DB that already has `characters`, so
        // columns added later need their own idempotent step. terasharp.db predates `exp`.
        AddColumnIfMissing("characters", "exp", "INTEGER NOT NULL DEFAULT 0");
        // T88: the scheduled-delete stamp C_CANCEL_DELETE_USER clears. 0 = no delete pending,
        // which is what every row had before this column existed.
        AddColumnIfMissing("characters", "delete_at", "INTEGER NOT NULL DEFAULT 0");
        // T90: the guild-incentive cooldown Guild::CanGiveGuildMoneyIncentive checks.
        AddColumnIfMissing("guilds", "last_incentive_at", "INTEGER NOT NULL DEFAULT 0");
        // T101b: the soft delete. delete_at (T88) is WHEN the row goes; these two are the
        // audit half - that it went, and who sent it there.
        // T101c: the account page's play-time figure, now fed by rolling up the characters.
        AddColumnIfMissing("accounts", "play_time_sec", "INTEGER NOT NULL DEFAULT 0");
        // T113: seconds this CHARACTER has spent in world, stamped at leave-world.
        AddColumnIfMissing("characters", "play_seconds", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "deleted_at", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "deleted_by", "TEXT NOT NULL DEFAULT ('')");
        AddColumnIfMissing("characters", "return_zone", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_channel", "INTEGER NOT NULL DEFAULT 0"); // T199 SysReturnLoc
        AddColumnIfMissing("characters", "return_x", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_y", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "return_z", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "dungeon_id", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "instance_pdid", "INTEGER NOT NULL DEFAULT 0");
        // T138c: the battleground rating. See the schema comment and BattlegroundRating.
        AddColumnIfMissing("characters", "bg_rating", "INTEGER NOT NULL DEFAULT 0");
        // T30: friends carry a group and the requester's greeting; blocks carry a note; the
        // character carries the profile message the friend panel shows and the once-only flag
        // behind dbo.spIsProvideSampleFriendGroup.
        AddColumnIfMissing("friends", "group_id", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing("friends", "memo", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("blocks", "memo", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("characters", "profile_message", "TEXT NOT NULL DEFAULT ''");
        // T97: the other two the "my profile" window writes. C_CHANGE_MY_PROFILE already had a
        // column (T30's profile_message); C_UPDATE_MY_DESCRIPTION is the longer free-text one
        // and C_CHANGE_MY_STATE is the online/away/busy flag User::ChangeUserState sets.
        AddColumnIfMissing("characters", "description", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("characters", "player_state", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "sample_group_provided", "INTEGER NOT NULL DEFAULT 0");
        // T32: GM level, per account (see AccountRecord.AdminLevel).
        AddColumnIfMissing("accounts", "admin_level", "INTEGER NOT NULL DEFAULT 0");
        // T201: set_go writes the native per-user admin level; null retains legacy account policy.
        AddColumnIfMissing("characters", "qa_admin_level", "INTEGER");
        AddColumnIfMissing("friends", "friendship_gage", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("accounts", "chat_ban_until", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "qa_voice", "INTEGER");
        AddColumnIfMissing("characters", "observer_type", "INTEGER NOT NULL DEFAULT -1");
        // A deliberate QA clear must remain empty instead of being mistaken for an unseeded bag.
        AddColumnIfMissing("characters", "inventory_cleared", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "tutorial_cleared", "INTEGER NOT NULL DEFAULT 0");
        // T181: mark only experiment-created rows, so removing an operator from the allow-list
        // can remove its experiment grants without deleting separately granted benefits.
        AddColumnIfMissing("account_benefits", "teleport_experiment", "INTEGER NOT NULL DEFAULT 0");
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
        MigrateAccountEp();

        // T83: the two crest counters SA_CREST_POINT carries. T77 answered that frame and kept
        // neither, so S_CREST_INFO had nothing but zeros to show.
        AddColumnIfMissing("characters", "crest_point", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "crest_ex_point", "INTEGER NOT NULL DEFAULT 0");
        // T197: an untouched legacy zero must not replace a valid saved World crestPoint.
        AddColumnIfMissing("characters", "crest_points_known", "INTEGER NOT NULL DEFAULT 0");
        Exec("UPDATE characters SET crest_points_known=1 WHERE crest_points_known=0 AND (crest_point<>0 OR crest_ex_point<>0)");
        // T197: NULL preserves an older world's saved flag until the first real apply write.
        AddColumnIfMissing("crests", "applied", "INTEGER NULL");

        // T167: the selected card preset and the EP page state (DBS_RESPONSE_CARD_DATA,
        // DBS_USER_LOAD_EP_PERK). All zero for a character that never used them - the captures.
        AddColumnIfMissing("characters", "card_preset_index", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_used_point", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_current_page", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_max_page", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_pre_level", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "ep_pre_total_point", "INTEGER NOT NULL DEFAULT 0");
        // T168: gold consumption (SDB_CHANGE_GOLD_CONSUMPTION) and the attendance bitmap a GM set
        // (SDB_ADMIN_USER_DAILY_ATTENDANCE); attend_set tells "never set" from "set to 0".
        AddColumnIfMissing("characters", "gold_consumption", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "attend_bitmap", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("characters", "attend_set", "INTEGER NOT NULL DEFAULT 0");
        // T170: the guild war's two sides (cap_final2a/2b - declare, opposite declare, withdraw,
        // give up). T80 rows are one-sided: the attacker declared, the defender did not.
        AddColumnIfMissing("guild_wars", "attack_declared", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing("guild_wars", "defend_declared", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("guild_wars", "defend_money", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("guild_wars", "defend_declares", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("guild_war_history", "defend_declares", "INTEGER NOT NULL DEFAULT 0");

        // T191: existing rows represent one ADD, which is the old load's constant value.
        AddColumnIfMissing("tutorial_tips", "popup_count", "INTEGER NOT NULL DEFAULT 1");
        MigrateCardsToAccount();
        MigrateSharedStarterItemIds();
    }

    /// <summary>
    /// T105. Every character used to be seeded with the SAME starter item ids (7..12, the ids
    /// the capture allocated for the first character ever created). Since T44 made the bag real
    /// rows, <see cref="UpsertItem"/>'s <c>ON CONFLICT(item_db_id)</c> then MOVED those rows to
    /// whoever logged in last, and the loser came back with an empty bag - which
    /// <c>SDB_USER_LOAD_INVENTORY</c> re-seeded, stealing them back. That is the live
    /// 2026-09-19 "player 3 -&gt; 6 starter items ... seeded 6 starter row(s)" for a character
    /// that had played.
    ///
    /// <para>New seeds now take ids from <see cref="ReserveItemIds"/>. This renumbers the rows
    /// already in the file - anything below <see cref="FirstItemId"/>, which only a starter seed
    /// can have produced - so an existing database stops colliding too. The owner is not
    /// touched: whoever holds the row keeps it. The item's own record blob carries the id at
    /// offset 0 (BagItems.RecordIdOffset), so that is patched with it, otherwise the rebuilt
    /// 0x27A4 would hand World the old id.</para>
    /// </summary>
    private void MigrateSharedStarterItemIds()
    {
        var legacy = new List<(int Id, long Owner)>();
        using (var find = _db.CreateCommand())
        {
            find.CommandText = "SELECT item_db_id, owner_db_id FROM items WHERE item_db_id < $f ORDER BY item_db_id";
            find.Parameters.AddWithValue("$f", FirstItemId);
            using var r = find.ExecuteReader();
            while (r.Read()) legacy.Add((r.GetInt32(0), r.GetInt64(1)));
        }
        if (legacy.Count == 0) return;

        int first = ReserveItemIds(legacy.Count);
        for (int i = 0; i < legacy.Count; i++)
        {
            int fresh = first + i;
            using var move = _db.CreateCommand();
            move.CommandText = "UPDATE items SET item_db_id = $new WHERE item_db_id = $old";
            move.Parameters.AddWithValue("$new", fresh);
            move.Parameters.AddWithValue("$old", legacy[i].Id);
            move.ExecuteNonQuery();

            using var read = _db.CreateCommand();
            read.CommandText = "SELECT record FROM items WHERE item_db_id = $id";
            read.Parameters.AddWithValue("$id", fresh);
            if (read.ExecuteScalar() is byte[] rec && rec.Length >= 4)
            {
                BitConverter.TryWriteBytes(rec.AsSpan(0, 4), fresh);
                using var put = _db.CreateCommand();
                put.CommandText = "UPDATE items SET record = $r WHERE item_db_id = $id";
                put.Parameters.AddWithValue("$r", rec);
                put.Parameters.AddWithValue("$id", fresh);
                put.ExecuteNonQuery();
            }
        }
        _log.LogWarning("T105: renumbered {N} shared starter item id(s) below {F} - they were "
            + "the same on every character and were being re-owned on each login", legacy.Count, FirstItemId);
    }

    /// <summary>
    /// T86. T83 keyed <c>cards</c> on <c>character_id</c> with a <c>preset</c> column, which was
    /// the wrong shape twice over: SDB_REGISTER_CARD carries an <c>AccountDbId</c> and no
    /// character at all, and SDB_MOUNT_CARD carries a character AND a preset index, so one card
    /// can sit in several characters' presets at once. The collection is account-wide and the
    /// mounts are per character.
    ///
    /// <para>The DDL's <c>CREATE TABLE IF NOT EXISTS</c> is a no-op against an existing old
    /// table, so this rebuilds it: the id in the old <c>character_id</c> column is read as the
    /// account it always was (T85b - the dispatch was passing the AccountDbId through a parameter
    /// named characterId), and any row with a preset other than -1 becomes a mount for the
    /// character of that name, when one exists.</para>
    /// </summary>
    private void MigrateCardsToAccount()
    {
        bool old;
        using (var probe = _db.CreateCommand())
        {
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('cards') WHERE name = 'character_id'";
            old = Convert.ToInt64(probe.ExecuteScalar() ?? 0L) > 0;
        }
        if (old)
        {
            _log.LogInformation("cards: migrating to the account-wide shape (T86)");
            Exec(@"
ALTER TABLE cards RENAME TO cards_v1;
DROP INDEX IF EXISTS ix_cards_character;
CREATE TABLE cards (
  account_id       INTEGER NOT NULL,
  card_template_id INTEGER NOT NULL,
  amount           INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (account_id, card_template_id)
);
INSERT INTO cards(account_id, card_template_id, amount)
  SELECT character_id, card_template_id, SUM(amount) FROM cards_v1
  GROUP BY character_id, card_template_id;
INSERT OR IGNORE INTO card_mounts(character_id, preset_index, card_template_id)
  SELECT character_id, preset, card_template_id FROM cards_v1 WHERE preset >= 0;
DROP TABLE cards_v1;");
        }

        Exec("CREATE INDEX IF NOT EXISTS ix_cards_account ON cards(account_id);");
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

    /// <summary>
    /// T191c. Throw away one character's stored client settings so the next login serves an
    /// empty body and the client re-seeds a small blob. The recovery for a character whose blob
    /// has run away past <see cref="MaxClientSettingBytes"/>: every save is refused from then on
    /// (the real Arbiter refuses the same way, see that constant), so nothing the client stores -
    /// including the tutorial/first-run state behind the WASD prompt - can ever persist again.
    /// Returns false when the character had nothing stored.
    /// </summary>
    public bool ClearClientSetting(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM client_settings WHERE character_id = $c";
            cmd.Parameters.AddWithValue("$c", characterId);
            int n = cmd.ExecuteNonQuery();
            if (n > 0) _log.LogInformation("Cleared the stored client settings for character {Id}", characterId);
            return n > 0;
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

    public int? GetCharacterAdminLevel(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT qa_admin_level FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            object? value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToInt32(value);
        }
    }

    /// <summary>T201 set_go, native UserManager::SetUserAdminLevel uses UserDbId (Arb044:3653).</summary>
    public void SetCharacterAdminLevel(long characterId, int level)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET qa_admin_level=$v WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId); cmd.Parameters.AddWithValue("$v", level);
            cmd.ExecuteNonQuery();
        }
    }

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

    /// <summary>
    /// T206: every character, id order, capped. There was no server-wide character list - the
    /// only ways in were by account or by exact name - so a mail run to everyone had nothing to
    /// iterate. The cap is the caller's, not a page: this is for one bulk write, not browsing.
    /// </summary>
    public List<CharacterRecord> GetAllCharacters(int limit = 1000)
    {
        if (limit < 1) limit = 1;
        var rows = new List<CharacterRecord>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM characters ORDER BY id LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(Read(r));
        }
        return rows;
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
            // T172: characters.id is a plain INTEGER PRIMARY KEY, so a new character takes the id
            // of a deleted one - and inherited its visited sections (isFirstVisit 0, no cinematic).
            PurgeCharacterStateLocked(id);
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

    // ============================================================ T150: bag size

    /// <summary>
    /// SDB_INCREASE_INVENTORY_SIZE, tab 0. Rounds <paramref name="newSlotCount"/> down to a
    /// multiple of 8 (as the original does) and raises the world blob's MaxInvenSlotCount to it -
    /// never lowers it - then adds <paramref name="expandDelta"/> to expandInvenCount. Patches the
    /// stored blob in place: the column is read raw (GetCharacter would stamp money and level into
    /// its copy) and written without touching last_logout or the position columns.
    /// Returns found = false when the character or its blob is missing.
    /// </summary>
    public (bool Found, int Slots, int Expand) IncreaseInventorySize(int characterId, int newSlotCount, int expandDelta)
    {
        lock (_lock)
        {
            byte[]? blob;
            using (var get = _db.CreateCommand())
            {
                get.CommandText = "SELECT world_blob FROM characters WHERE id = $id";
                get.Parameters.AddWithValue("$id", characterId);
                blob = get.ExecuteScalar() as byte[];
            }
            if (blob == null || blob.Length < StarterBlob.ExpandInvenCountOffset + 4) return (false, 0, 0);

            int slots = BitConverter.ToInt32(blob, StarterBlob.MaxInvenSlotCountOffset);
            int expand = BitConverter.ToInt32(blob, StarterBlob.ExpandInvenCountOffset);
            int want = newSlotCount - newSlotCount % 8;   // truncates toward zero, like the original
            bool dirty = false;
            if (want > slots) { slots = want; dirty = true; }
            if (expandDelta != 0) { expand += expandDelta; dirty = true; }
            if (!dirty) return (true, slots, expand);

            BitConverter.TryWriteBytes(blob.AsSpan(StarterBlob.MaxInvenSlotCountOffset, 4), slots);
            BitConverter.TryWriteBytes(blob.AsSpan(StarterBlob.ExpandInvenCountOffset, 4), expand);
            using var put = _db.CreateCommand();
            put.CommandText = "UPDATE characters SET world_blob = $b WHERE id = $id";
            put.Parameters.AddWithValue("$b", blob);
            put.Parameters.AddWithValue("$id", characterId);
            put.ExecuteNonQuery();
            return (true, slots, expand);
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
            _qaGuildPlayStart[characterId] = GetGuildIdOf(characterId) == 0 ? 0 : new DateTimeOffset(DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
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

    // ============================================================ T193: account EP (Extra Point)

    /// <summary>Native progress belongs to Account (Arb_part_061:16547), while perks and PRE
    /// belong to User. Preserve the old columns for recovery. With no old EP write timestamps,
    /// migrate one intact row per account: highest exp, then points/level, then lowest character id.
    /// Never add sibling balances or re-import legacy rows after an account reset.</summary>
    private void MigrateAccountEp()
    {
        using var probe = _db.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='account_ep'";
        if (Convert.ToInt64(probe.ExecuteScalar()) != 0) return;
        using var transaction = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
CREATE TABLE account_ep (
  account_id INTEGER PRIMARY KEY REFERENCES accounts(id),
  ep_exp INTEGER NOT NULL DEFAULT 0, ep_level INTEGER NOT NULL DEFAULT 0,
  ep_point INTEGER NOT NULL DEFAULT 0, ep_daily_exp INTEGER NOT NULL DEFAULT 0,
  ep_reserve_bonus INTEGER NOT NULL DEFAULT 0, ep_daily_limit INTEGER NOT NULL DEFAULT 0,
  ep_reset_time INTEGER NOT NULL DEFAULT 0
);
INSERT INTO account_ep
SELECT account_id, ep_exp, ep_level, ep_point, ep_daily_exp, ep_reserve_bonus, ep_daily_limit, ep_reset_time
FROM (SELECT account_id, ep_exp, ep_level, ep_point, ep_daily_exp, ep_reserve_bonus, ep_daily_limit, ep_reset_time,
             ROW_NUMBER() OVER (PARTITION BY account_id ORDER BY ep_exp DESC, ep_point DESC, ep_level DESC, id) AS choice
      FROM characters) WHERE choice=1;";
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    private void EnsureAccountEpLocked(long characterId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO account_ep(account_id) SELECT account_id FROM characters WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", characterId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The character's account EP panel. The six numbers come from SDB_UPDATE_EXTRA_POINT (0x27B1) in
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
            EnsureAccountEpLocked(characterId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE account_ep SET ep_exp = $e, ep_level = $l, ep_point = $p, "
                            + "ep_daily_exp = $d, ep_reserve_bonus = $r, ep_daily_limit = $m "
                            + "WHERE account_id = (SELECT account_id FROM characters WHERE id = $id)";
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

    /// <summary>Explicit account level/point update. SDB_UPDATE_PRE_EP_INFO must not call this:
    /// its previous-login counters belong only to the requesting character.</summary>
    public bool SetCharacterEpLevel(long characterId, int epLevel, int epPoint)
    {
        lock (_lock)
        {
            EnsureAccountEpLocked(characterId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE account_ep SET ep_level = $l, ep_point = $p "
                            + "WHERE account_id = (SELECT account_id FROM characters WHERE id = $id)";
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
            EnsureAccountEpLocked(characterId);
            using var cmd = _db.CreateCommand();
            // Arb_part_064:11069: daily reset also clears today's earned EP exp.
            cmd.CommandText = "UPDATE account_ep SET ep_daily_exp = 0, ep_reserve_bonus = $r, ep_reset_time = $t "
                            + "WHERE account_id = (SELECT account_id FROM characters WHERE id = $id)";
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
            EnsureAccountEpLocked(characterId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ep_exp, ep_level, ep_point, ep_daily_exp, "
                            + "ep_reserve_bonus, ep_daily_limit, ep_reset_time "
                            + "FROM account_ep WHERE account_id = (SELECT account_id FROM characters WHERE id = $id)";
            cmd.Parameters.AddWithValue("$id", characterId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new EpRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
                             r.GetInt32(4), r.GetInt32(5), r.GetInt64(6));
        }
    }

    public bool SetCharacterEpDailyLimit(int characterId, int limit)
        => WriteAccountEp(characterId, "ep_daily_limit=$value", limit);

    private bool WriteAccountEp(int characterId, string assignments, int value = 0)
    {
        lock (_lock)
        {
            EnsureAccountEpLocked(characterId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE account_ep SET " + assignments
                + " WHERE account_id=(SELECT account_id FROM characters WHERE id=$id)";
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
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

    /// <summary>T199: native UpdateSysReturnLoc stores the source channel separately
    /// from the destination instance. Arb030:16047-16075; cap_bg1:12546/12594.</summary>
    public sealed record SystemReturnPoint(int Continent, uint Channel, int X, int Y, int Z);

    public bool SaveSystemReturn(int characterId, SystemReturnPoint point)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET return_zone=$zone, return_channel=$channel, "
                + "return_x=$x, return_y=$y, return_z=$z WHERE id=$id";
            cmd.Parameters.AddWithValue("$zone", point.Continent);
            cmd.Parameters.AddWithValue("$channel", (long)point.Channel);
            cmd.Parameters.AddWithValue("$x", point.X); cmd.Parameters.AddWithValue("$y", point.Y);
            cmd.Parameters.AddWithValue("$z", point.Z); cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public SystemReturnPoint? GetSystemReturn(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT return_zone,return_channel,return_x,return_y,return_z FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            using var r = cmd.ExecuteReader();
            return r.Read() && r.GetInt32(0) > 0 ? new(r.GetInt32(0), unchecked((uint)r.GetInt64(1)),
                (int)r.GetDouble(2), (int)r.GetDouble(3), (int)r.GetDouble(4)) : null;
        }
    }

    /// <summary>
    /// T208b. The battleground entry point: 13CB's system return point, plus <c>dungeon_id</c> set to
    /// the battlefield continent. That last column is what makes a relog work: the blob World saves
    /// inside a battleground carries continent 115, and
    /// <c>WorldEntry.BuildEnterWorldPayload</c> only replaces it with the return point when
    /// <c>GetDungeonReturn().DungeonId</c> equals that continent. T199 stored the return point but
    /// never the continent, so a battleground relog still carried 115 - and when the battlefield
    /// World has no links yet the frame goes to a World that does not own the continent and is never
    /// answered at all (arbiter-bg3.log 20:53:19: no SA_ENTER_WORLD, no 0x138D, the client sits on
    /// the loading screen, and the abandoned session leaves a held leave only
    /// /api/reset-character clears).
    /// </summary>
    public bool SaveBattlefieldReturn(int characterId, int battlefieldContinent, SystemReturnPoint point)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET dungeon_id=$d, return_zone=$zone, return_channel=$channel, "
                + "return_x=$x, return_y=$y, return_z=$z WHERE id=$id";
            cmd.Parameters.AddWithValue("$d", battlefieldContinent);
            cmd.Parameters.AddWithValue("$zone", point.Continent);
            cmd.Parameters.AddWithValue("$channel", (long)point.Channel);
            cmd.Parameters.AddWithValue("$x", point.X); cmd.Parameters.AddWithValue("$y", point.Y);
            cmd.Parameters.AddWithValue("$z", point.Z); cmd.Parameters.AddWithValue("$id", characterId);
            bool ok = cmd.ExecuteNonQuery() == 1;
            if (ok) _log.LogInformation(
                "Character {Id} entered battlefield continent {C}; return point zone {Z} ({X}, {Y}, {Zz})",
                characterId, battlefieldContinent, point.Continent, point.X, point.Y, point.Z);
            else _log.LogWarning("SaveBattlefieldReturn: character {Id} not found", characterId);
            return ok;
        }
    }

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
                            + "return_zone = 0, return_channel = 0, return_x = 0, return_y = 0, return_z = 0 WHERE id = $id";
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

    /// <summary>
    /// T164. SDB_MARK_AS_QUEST_COMPLETED: <paramref name="questId"/> is complete. A row in
    /// progress keeps its record with +8 set to 2; a quest never started gets a minimal 80-byte
    /// QuestData (questId at +4, status 2, 0xFFFFFFFF at +76 - status/QUEST-DESIGN.md). Returns
    /// true only when it was not complete before, which is what the Arbiter's reply lists.
    /// </summary>
    public bool MarkQuestCompleted(int ownerId, int questId)
    {
        if (questId <= 0 || NoSuchOwner("MarkQuestCompleted", ownerId)) return false;
        lock (_lock)
        {
            int id = 0, status = 0;
            byte[]? record = null;
            using (var sel = _db.CreateCommand())
            {
                sel.CommandText = "SELECT id, status, record FROM quests WHERE owner_id = $o AND quest_id = $q";
                sel.Parameters.AddWithValue("$o", ownerId);
                sel.Parameters.AddWithValue("$q", questId);
                using var r = sel.ExecuteReader();
                if (r.Read()) { id = r.GetInt32(0); status = r.GetInt32(1); record = (byte[])r["record"]; }
            }
            if (id != 0 && status == QuestStatusComplete) return false;

            if (record == null || record.Length < 80)
            {
                var fresh = new byte[80];
                if (record != null) Array.Copy(record, fresh, record.Length);
                record = fresh;
            }
            BitConverter.GetBytes(questId).CopyTo(record, 4);
            BitConverter.GetBytes(QuestStatusComplete).CopyTo(record, 8);
            BitConverter.GetBytes(0).CopyTo(record, 12);
            if (id == 0) BitConverter.GetBytes(-1).CopyTo(record, 76);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = id != 0
                ? "UPDATE quests SET status = $s, step = 0, record = $r, updated_at = datetime('now') WHERE id = $id"
                : "INSERT INTO quests(owner_id, quest_id, status, step, record) VALUES($o, $q, $s, 0, $r)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$q", questId);
            cmd.Parameters.AddWithValue("$s", QuestStatusComplete);
            cmd.Parameters.AddWithValue("$r", record);
            cmd.ExecuteNonQuery();
            _log.LogInformation("Quest {Q} marked complete for character {Id}", questId, ownerId);
            return true;
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
    /// <summary>T168b. The guild / account twins of <see cref="NoSuchOwner"/>: guild_members.guild_id
    /// REFERENCES guilds, so an id World (or a hostile frame) invents must never reach the INSERT -
    /// the SqliteException would close the World link. vip_info and card_book_rewards have no FK,
    /// but a row for an account that does not exist is garbage all the same.</summary>
    private bool NoSuchGuild(string what, int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM guilds WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$g", guildId);
            if ((long)cmd.ExecuteScalar()! > 0) return false;
        }
        _log.LogWarning("{What}: no guild row for id {Id} - write dropped", what, guildId);
        return true;
    }

    private bool NoSuchAccount(string what, long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM accounts WHERE id=$a";
            cmd.Parameters.AddWithValue("$a", accountId);
            if ((long)cmd.ExecuteScalar()! > 0) return false;
        }
        _log.LogWarning("{What}: no account row for id {Id} - write dropped", what, accountId);
        return true;
    }

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

    /// <summary>One planet-wide server-first claim.</summary>
    public sealed record ServerAchievementClaim(int AchievementId, int OwnerId, long PartyId, string ClaimedAt);

    /// <summary>
    /// T203. Claim a server-first achievement for <paramref name="ownerId"/>. Returns true only
    /// for the FIRST claimant: the achievement id is the primary key, so <c>INSERT OR IGNORE</c>
    /// plus the row count is the whole race-free rule - the same thing retail gets from
    /// <c>ServerAchievementManager::SetAccomplishedNoLock</c> under its own lock.
    /// </summary>
    public bool TryClaimServerAchievement(int achievementId, int ownerId, long partyId = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO server_achievements (achievement_id, owner_id, party_id) "
                            + "VALUES ($a, $o, $p)";
            cmd.Parameters.AddWithValue("$a", achievementId);
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$p", partyId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>T203. Who holds a server-first achievement, or null while nobody does.</summary>
    public ServerAchievementClaim? GetServerAchievementClaim(int achievementId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT achievement_id, owner_id, party_id, created_at FROM server_achievements "
                            + "WHERE achievement_id = $a";
            cmd.Parameters.AddWithValue("$a", achievementId);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new ServerAchievementClaim(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetString(3))
                : null;
        }
    }

    /// <summary>T203. Every server-first claim, oldest first - the admin API's winners list.</summary>
    public List<ServerAchievementClaim> GetServerAchievements()
    {
        lock (_lock)
        {
            var list = new List<ServerAchievementClaim>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT achievement_id, owner_id, party_id, created_at FROM server_achievements "
                            + "ORDER BY created_at, achievement_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ServerAchievementClaim(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetString(3)));
            return list;
        }
    }

    /// <summary>
    /// T203. Release one server-first claim, or all of them when <paramref name="achievementId"/>
    /// is 0. Retail has the same two operators as QA commands
    /// (<c>ArbiterQACommandHandler::ClearServerAchievement</c> / <c>ClearAllServerAchievement</c>,
    /// backed by <c>spClearServerAchievement</c>). Returns how many rows went.
    /// </summary>
    public int ClearServerAchievements(int achievementId = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            if (achievementId > 0)
            {
                cmd.CommandText = "DELETE FROM server_achievements WHERE achievement_id = $a";
                cmd.Parameters.AddWithValue("$a", achievementId);
            }
            else cmd.CommandText = "DELETE FROM server_achievements";
            return cmd.ExecuteNonQuery();
        }
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
    /// Record one popup. Native User::UpdateSimpleTip adds the supplied count (Arb_part_030.c:15374).
    /// A repeated ADD increments the existing row; it does not add a duplicate tip.
    /// </summary>
    public bool AddTutorialTip(int ownerId, int tipId) => AddTutorialTipCount(ownerId, tipId, 1);

    public bool AddTutorialTipCount(int ownerId, int tipId, int count)
    {
        if (NoSuchOwner("AddTutorialTipCount", ownerId)) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO tutorial_tips (owner_id, tip_id, popup_count) VALUES ($o, $t, $n)
ON CONFLICT(owner_id, tip_id) DO UPDATE SET popup_count = popup_count + excluded.popup_count";
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$t", tipId);
            cmd.Parameters.AddWithValue("$n", count);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>The character's tutorial ids, in native map order.</summary>
    public List<int> GetTutorialTips(int ownerId) => GetTutorialTipCounts(ownerId).Select(t => t.TipId).ToList();

    /// <summary>spClearAllSimpleTip(UserDbId), GameDatabaseDefinition.xml:8258.</summary>
    public void ClearTutorialTips(int ownerId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM tutorial_tips WHERE owner_id=$id; UPDATE characters SET tutorial_cleared=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", ownerId); cmd.ExecuteNonQuery();
        }
    }

    public bool TutorialWasCleared(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT tutorial_cleared FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) != 0;
        }
    }

    public List<(int TipId, int PopupCount)> GetTutorialTipCounts(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<(int TipId, int PopupCount)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT tip_id, popup_count FROM tutorial_tips WHERE owner_id = $o ORDER BY tip_id";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetInt32(1)));
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

    /// <summary>One row of the T134 clear-count window.</summary>
    public readonly record struct DungeonClearCount(int DungeonId, int Clears);

    /// <summary>
    /// Every dungeon this character has a clear count for, ordered by dungeon id so the reply is
    /// reproducible. T134: this is what S_DUNGEON_CLEAR_COUNT_LIST serves.
    ///
    /// <para>The capture's reply carries a FIXED roster of 14 dungeon ids whether or not the
    /// character has cleared them - a character with ten clears of 9830 and thirty-four of 9739
    /// still gets all fourteen rows, the other twelve at 0. That roster is client-side content,
    /// not a row set, so the caller supplies it and this only supplies the counts.</para>
    /// </summary>
    public List<DungeonClearCount> GetDungeonClearCounts(int ownerId)
    {
        lock (_lock)
        {
            var list = new List<DungeonClearCount>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT dungeon_id, clear_count FROM dungeon_cooldowns "
                            + "WHERE owner_id = $o AND clear_count > 0 ORDER BY dungeon_id";
            cmd.Parameters.AddWithValue("$o", ownerId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new DungeonClearCount(Convert.ToInt32(r["dungeon_id"]), Convert.ToInt32(r["clear_count"])));
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
            int tradeId = Convert.ToInt32(cmd.ExecuteScalar()!);
            SnapshotBrokerPriceKey(tradeId, itemDbId);
            return tradeId;
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
    // ------------------------------------------------------------ T88: the character rename

    /// <summary>
    /// Rename a character. Returns false when the row is gone or the new name collides - the
    /// UNIQUE COLLATE NOCASE index on <c>characters.name</c> is the real gate, so a race with
    /// another rename fails here rather than corrupting the table.
    /// </summary>
    /// <summary>The stored name of one character, or null when the row is gone.</summary>
    public string? GetCharacterName(int id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT name FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string;
        }
    }

    public bool RenameCharacter(int id, string newName)
    {
        if (string.IsNullOrEmpty(newName)) return false;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE characters SET name=$n WHERE id=$id";
                cmd.Parameters.AddWithValue("$n", newName);
                cmd.Parameters.AddWithValue("$id", id);
                return cmd.ExecuteNonQuery() == 1;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                _log.LogWarning("RenameCharacter {Id} -> {Name} refused: {Msg}", id, newName, ex.Message);
                return false;
            }
        }
    }

    // ------------------------------------------------------- T88: the scheduled delete

    /// <summary>
    /// Mark a character for deletion at <paramref name="deleteAtUnix"/> without removing the
    /// row. The real Arbiter keeps the character listed while the timer runs - that is what
    /// <c>S_GET_USER_LIST.deleteRemainSec</c> reports and what C_CANCEL_DELETE_USER undoes.
    /// Ownership-checked, like <see cref="DeleteCharacter"/>.
    /// </summary>
    public bool ScheduleCharacterDelete(int id, long accountId, long deleteAtUnix)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET delete_at=$d WHERE id=$id AND account_id=$a";
            cmd.Parameters.AddWithValue("$d", deleteAtUnix);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$a", accountId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// Clear a pending delete. Returns FALSE when the character is not on the account or has
    /// nothing pending - the caller answers S_CANCEL_DELETE_USER with that same bool, so a
    /// cancel for a character that was never scheduled must not report success.
    /// </summary>
    public bool CancelCharacterDelete(int id, long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM characters WHERE id=$id AND account_id=$a AND delete_at<>0";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$a", accountId);
            if (cmd.ExecuteScalar() == null) return false;
        }
        // T101b: the delete is a SOFT delete now - OnDeleteUser parks the items rather than
        // dropping them - so clearing delete_at on its own would bring the character back
        // empty-handed. RestoreDeletedCharacter undoes both halves, and the admin tool's
        // restore lands on it through here as well.
        return RestoreDeletedCharacter(id);
    }

    /// <summary>The unix second this character is due to be removed, or 0 when none is set.</summary>
    public long GetCharacterDeleteAt(int id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT delete_at FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0L : Convert.ToInt64(v);
        }
    }

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
DELETE FROM guild_wanted      WHERE user_db_id = $id;
DELETE FROM guild_invites     WHERE user_db_id = $id;
DELETE FROM guild_members     WHERE user_db_id = $id;
DELETE FROM item_recipes      WHERE character_id = $id;
DELETE FROM skill_profs       WHERE character_id = $id;
DELETE FROM gathering_profs   WHERE character_id = $id;
DELETE FROM restrictions      WHERE character_id = $id;";
            kids.Parameters.AddWithValue("$id", id);
            kids.ExecuteNonQuery();
            PurgeCharacterStateLocked(id);   // T172

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
        // T105: the row is the truth for level, the same way it is for money.
        StarterBlob.WriteLevel(c.WorldBlob, c.Level);
        // T201: spChangeUserCustomizeInfo is Arbiter-owned; a later World save must not undo the selected voice.
        if (r["qa_voice"] is long voice)
        {
            if (c.Appearance.Length >= 2) c.Appearance[1] = unchecked((byte)voice);
            if (c.WorldBlob is { Length: > 289 }) c.WorldBlob[289] = unchecked((byte)voice);
        }
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
    // ------------------------- T97: description and state -------------------------

    /// <summary>
    /// C_UPDATE_MY_DESCRIPTION (0xC72D). The real handler runs the text past the net moderator
    /// and hands it to <c>User::UpdateUserDescription</c>, which keeps it on the user object;
    /// here it is a column, so it survives a relog the way the profile message does.
    /// </summary>
    public void SetMyDescription(int characterId, string description)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET description = $d WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.Parameters.AddWithValue("$d", description ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    public string GetMyDescription(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT description FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return cmd.ExecuteScalar() as string ?? "";
        }
    }

    /// <summary>
    /// C_CHANGE_MY_STATE (0x6E08), one i32. <c>User::ChangeUserState(enum PlayerState)</c> is
    /// the whole handler - no reply, no broadcast from the Arbiter - so this is a stored flag
    /// and nothing else reads it yet.
    /// </summary>
    public void SetMyState(int characterId, int state)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET player_state = $s WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.Parameters.AddWithValue("$s", state);
            cmd.ExecuteNonQuery();
        }
    }

    public int GetMyState(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT player_state FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            var v = cmd.ExecuteScalar();
            return v is null || v is DBNull ? 0 : Convert.ToInt32(v);
        }
    }

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

    /// <summary>
    /// T95. One wanted-board ad, already joined to the poster's character row - the element of
    /// S_REPLY_GUILD_WANTED_WRITING_LIST carries the name, level and class as well as the three
    /// fields C_REQUEST_SET_GUILD_WANTED_WRITING wrote, and nothing else reads this table.
    /// </summary>
    public sealed record GuildWantedRow(int UserDbId, string UserName, int Level, int ClassType,
                                        int GuildPreference, int GuildSize, long WritingDate,
                                        string PromotionStr);
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
        long WarId, int AttackGuildId, int DefendGuildId, long DeclaredAt, long Money, int State,
        bool AttackDeclared = true, bool DefendDeclared = false, long DefendMoney = 0, int DefendDeclares = 0);

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

    /// <summary>War states - the record's +0xcc (GuildWarManager::OppositeDeclareGuildWar): 6 only
    /// the attacker has declared, 7 only the defender, 8 both (mutual).</summary>
    public const int GuildWarStateDefenderOnly = 7, GuildWarStateMutual = 8;

    /// <summary>T170. One side declares (or re-declares) on a live war: that side's flag and money,
    /// the state from both flags, and the defender's declaration counter. False: no such war.</summary>
    public bool DeclareGuildWarSide(long warId, bool defenderSide, long money)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = defenderSide
                ? "UPDATE guild_wars SET defend_declared=1, defend_money=$m, defend_declares=defend_declares+1, " +
                  "state=CASE WHEN attack_declared=1 THEN 8 ELSE 7 END WHERE war_id=$w"
                : "UPDATE guild_wars SET attack_declared=1, money=$m, " +
                  "state=CASE WHEN defend_declared=1 THEN 8 ELSE 6 END WHERE war_id=$w";
            cmd.Parameters.AddWithValue("$m", money);
            cmd.Parameters.AddWithValue("$w", warId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>T170. One side of a MUTUAL war takes its declaration back: its flag and money go to
    /// 0 and the state becomes the other side's alone (cap_final2b 14883: 8 -&gt; 6). Returns the new
    /// state, 0 when there was no such war.</summary>
    public int WithdrawGuildWarSide(long warId, bool defenderSide)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = defenderSide
                ? "UPDATE guild_wars SET defend_declared=0, defend_money=0, state=6 WHERE war_id=$w"
                : "UPDATE guild_wars SET attack_declared=0, money=0, state=7 WHERE war_id=$w";
            cmd.Parameters.AddWithValue("$w", warId);
            return cmd.ExecuteNonQuery() == 1 ? (defenderSide ? GuildWarStateDeclared : GuildWarStateDefenderOnly) : 0;
        }
    }

    /// <summary>T170. Guild money moves (a war declaration, a surrender's reparation). Clamped at 0;
    /// returns the new total, -1 when the guild does not exist.</summary>
    /// <summary>
    /// T206: set a guild's level outright. <c>AddGuildMoney</c>-style deltas are what the game
    /// uses; an operator correcting a wrong value needs to say what the value IS.
    /// </summary>
    public bool SetGuildLevel(int guildId, int level)
    {
        if (level < 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET level=$l WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>T206: set a guild's money outright. Negative is refused, not clamped.</summary>
    public bool SetGuildMoney(int guildId, long money)
    {
        if (money < 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET money=$m WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$m", money);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public long AddGuildMoney(int guildId, long delta)
    {
        if (!AddGuildQuestReward(guildId, 0, delta)) return -1;
        return GetGuild(guildId)?.Money ?? -1;
    }

    /// <summary>T170: one guild_wars row, both sides.</summary>
    private static GuildWarRow ReadGuildWar(Microsoft.Data.Sqlite.SqliteDataReader r)
        => new(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5),
               r.GetInt64(6) != 0, r.GetInt64(7) != 0, r.GetInt64(8), r.GetInt32(9));

    /// <summary>The state a freshly declared war carries - the value the capture s window shows.</summary>
    public const int GuildWarStateDeclared = 6;

    /// <summary>Every live war this guild is on either side of, oldest first.</summary>
    public List<GuildWarRow> GetGuildWars(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT war_id, attack_guild_id, defend_guild_id, declared_at, money, state, " +
                "attack_declared, defend_declared, defend_money, defend_declares " +
                "FROM guild_wars WHERE attack_guild_id=$g OR defend_guild_id=$g ORDER BY war_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            var rows = new List<GuildWarRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(ReadGuildWar(r));
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
                    "SELECT war_id, attack_guild_id, defend_guild_id, declared_at, money, state, " +
                "attack_declared, defend_declared, defend_money, defend_declares " +
                    "FROM guild_wars WHERE war_id=$w";
                get.Parameters.AddWithValue("$w", warId);
                using var r = get.ExecuteReader();
                if (r.Read())
                    live = ReadGuildWar(r);
            }
            if (live == null) return null;

            using (var ins = _db.CreateCommand())
            {
                ins.CommandText =
                    "INSERT INTO guild_war_history(war_id, attack_guild_id, defend_guild_id, " +
                    "declared_at, ended_at, result, defend_declares) VALUES($w,$a,$d,$t,$e,$r,$dd)";
                ins.Parameters.AddWithValue("$dd", live.DefendDeclares);
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
                "(SELECT COUNT(*) FROM guild_war_history WHERE attack_guild_id=$g) + " +
                // T170: an opposite declaration counts for the defender (cap_final2a_client1
                // 4077: sdg's counter goes 0 -> 1 when it declares back, and stays 1 after).
                "(SELECT COALESCE(SUM(defend_declares), 0) FROM guild_wars WHERE defend_guild_id=$g) + " +
                "(SELECT COALESCE(SUM(defend_declares), 0) FROM guild_war_history WHERE defend_guild_id=$g)";
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

    // -------------------------------------------------------- T101b: restrictions

    /// <summary>Restriction types. The retail catalogue has more; these are the two the tool sets.</summary>
    public const int RestrictionBan = 1, RestrictionMute = 2;

    /// <summary>One restriction on one character.</summary>
    public sealed record RestrictionRow(int CharacterId, int Type, int Level, long Until,
        string Reason, long SetAt);

    /// <summary>Place or replace a restriction. <paramref name="until"/> 0 is permanent.</summary>
    public bool AddRestriction(int characterId, int type, int level, long until, string reason, long setAt)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO restrictions(character_id, type, level, until, reason, set_at) " +
                "VALUES($c,$t,$l,$u,$r,$s) ON CONFLICT(character_id, type) DO UPDATE SET " +
                "level=excluded.level, until=excluded.until, reason=excluded.reason, set_at=excluded.set_at";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$u", until);
            cmd.Parameters.AddWithValue("$r", reason ?? string.Empty);
            cmd.Parameters.AddWithValue("$s", setAt);
            return cmd.ExecuteNonQuery() >= 1;
        }
    }

    /// <summary>Lift one restriction. False when there was none.</summary>
    public bool RemoveRestriction(int characterId, int type)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM restrictions WHERE character_id=$c AND type=$t";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$t", type);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Every restriction on this character, in type order.</summary>
    public List<RestrictionRow> GetRestrictions(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT character_id, type, level, until, reason, set_at FROM restrictions " +
                "WHERE character_id=$c ORDER BY type";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<RestrictionRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new RestrictionRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
                    r.GetInt64(3), r.GetString(4), r.GetInt64(5)));
            return rows;
        }
    }

    /// <summary>Is this character banned right now? An expired <c>until</c> does not count.</summary>
    public bool IsRestricted(int characterId, int type, long nowUnix)
    {
        foreach (var r in GetRestrictions(characterId))
            if (r.Type == type && (r.Until == 0 || r.Until > nowUnix)) return true;
        return false;
    }

    // -------------------------------------------------------- T101b: the soft delete

    /// <summary>The retail grace window, <c>deleteCharacterExpireHour2</c> in S_GET_USER_LIST.</summary>
    public const int DeleteExpireHours = 72;

    /// <summary>
    /// Schedule a delete and park the character s items, instead of dropping the row. The row
    /// stays listed for the whole window - that is what <c>deleteRemainSec</c> reports - and
    /// <see cref="PurgeExpiredDeletes"/> is what finally removes it.
    /// </summary>
    public bool SoftDeleteCharacter(int id, long accountId, string byWhom, long deleteAtUnix, long nowUnix)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var own = _db.CreateCommand())
            {
                own.Transaction = tx;
                own.CommandText = "SELECT 1 FROM characters WHERE id=$id AND account_id=$a";
                own.Parameters.AddWithValue("$id", id);
                own.Parameters.AddWithValue("$a", accountId);
                if (own.ExecuteScalar() == null) { tx.Rollback(); return false; }
            }
            using (var park = _db.CreateCommand())
            {
                park.Transaction = tx;
                park.CommandText =
                    "INSERT OR REPLACE INTO deleted_items(item_db_id, character_id, inven_type, slot, " +
                    "template_id, amount, record, deleted_at) " +
                    "SELECT item_db_id, owner_db_id, inven_type, slot, template_id, amount, record, $n " +
                    "FROM items WHERE owner_db_id=$id";
                park.Parameters.AddWithValue("$id", id);
                park.Parameters.AddWithValue("$n", nowUnix);
                park.ExecuteNonQuery();
            }
            using (var drop = _db.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = "DELETE FROM items WHERE owner_db_id=$id";
                drop.Parameters.AddWithValue("$id", id);
                drop.ExecuteNonQuery();
            }
            using (var stamp = _db.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText =
                    "UPDATE characters SET delete_at=$d, deleted_at=$n, deleted_by=$w WHERE id=$id";
                stamp.Parameters.AddWithValue("$d", deleteAtUnix);
                stamp.Parameters.AddWithValue("$n", nowUnix);
                stamp.Parameters.AddWithValue("$w", byWhom ?? string.Empty);
                stamp.Parameters.AddWithValue("$id", id);
                if (stamp.ExecuteNonQuery() != 1) { tx.Rollback(); return false; }
            }
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Undo a soft delete: clear the stamps and put the parked items back. This is the retail
    /// WA_UNDELETE_USER, and it works for the whole window rather than only before the client
    /// confirms - which is what T101 phase 1 could not do.
    /// </summary>
    public bool RestoreDeletedCharacter(int id)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var back = _db.CreateCommand())
            {
                back.Transaction = tx;
                back.CommandText =
                    "INSERT OR REPLACE INTO items(item_db_id, owner_db_id, inven_type, slot, " +
                    "template_id, amount, record, updated_at) " +
                    "SELECT item_db_id, character_id, inven_type, slot, template_id, amount, record, " +
                    "datetime('now') FROM deleted_items WHERE character_id=$id";
                back.Parameters.AddWithValue("$id", id);
                back.ExecuteNonQuery();
            }
            using (var clear = _db.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM deleted_items WHERE character_id=$id";
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }
            using (var stamp = _db.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText =
                    "UPDATE characters SET delete_at=0, deleted_at=0, deleted_by='' WHERE id=$id";
                stamp.Parameters.AddWithValue("$id", id);
                if (stamp.ExecuteNonQuery() != 1) { tx.Rollback(); return false; }
            }
            tx.Commit();
            return true;
        }
    }

    /// <summary>Characters waiting out their window, newest first.</summary>
    public List<CharacterRecord> GetDeletedCharacters(int limit)
    {
        if (limit < 1) limit = 1;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM characters WHERE deleted_at <> 0 ORDER BY deleted_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            var ids = new List<int>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt32(0));
            var rows = new List<CharacterRecord>();
            foreach (int id in ids) { var c = GetCharacter(id); if (c != null) rows.Add(c); }
            return rows;
        }
    }

    /// <summary>
    /// Hard-delete everything whose window has run out. Returns how many went. Called on a
    /// timer; <paramref name="expireHours"/> is <see cref="DeleteExpireHours"/> unless a test
    /// wants to move it.
    /// </summary>
    public int PurgeExpiredDeletes(long nowUnix, int expireHours = DeleteExpireHours)
    {
        long cutoff = nowUnix - (long)expireHours * 3600L;
        List<int> doomed = new();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM characters WHERE deleted_at <> 0 AND deleted_at <= $c";
            cmd.Parameters.AddWithValue("$c", cutoff);
            using var r = cmd.ExecuteReader();
            while (r.Read()) doomed.Add(r.GetInt32(0));
        }
        int gone = 0;
        foreach (int id in doomed)
        {
            lock (_lock)
            {
                using var drop = _db.CreateCommand();
                drop.CommandText = "DELETE FROM deleted_items WHERE character_id=$id";
                drop.Parameters.AddWithValue("$id", id);
                drop.ExecuteNonQuery();
            }
            var c = GetCharacter(id);
            if (c != null && DeleteCharacter(id, c.AccountId)) gone++;
        }
        if (gone > 0) _log.LogInformation("purged {N} character(s) past the {H} h delete window", gone, expireHours);
        return gone;
    }

    // ------------------------------------------------------------ T101: the admin web tool

    /// <summary>One line of the admin audit trail.</summary>
    public sealed record AdminLogRow(long At, string SourceIp, string Action, string Target,
        string Reason, int Result);

    /// <summary>Record one admin action. Never throws into a request path - a failed audit
    /// write is logged and swallowed, because losing the action is worse than losing the log.</summary>
    // -------------------------------------------------------- T101c: announces

    /// <summary>One scheduled announce. <c>IntervalSec</c> 0 means it goes out once.</summary>
    public sealed record AnnounceRow(long Id, string Text, long StartAt, long EndAt,
        long IntervalSec, long LastSent, bool Enabled, string CreatedBy, long CreatedAt);

    /// <summary>Schedule an announce. Returns its id.</summary>
    public long AddAnnounce(string text, long startAt, long endAt, long intervalSec,
                            string createdBy, long nowUnix)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO announces(text, start_at, end_at, interval_sec, enabled, created_by, created_at) " +
                "VALUES($t,$s,$e,$i,1,$b,$n); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", text ?? string.Empty);
            cmd.Parameters.AddWithValue("$s", startAt);
            cmd.Parameters.AddWithValue("$e", endAt);
            cmd.Parameters.AddWithValue("$i", intervalSec);
            cmd.Parameters.AddWithValue("$b", createdBy ?? string.Empty);
            cmd.Parameters.AddWithValue("$n", nowUnix);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0L : Convert.ToInt64(v);
        }
    }

    /// <summary>Every scheduled announce, newest first.</summary>
    public List<AnnounceRow> GetAnnounces(int limit = 100)
    {
        if (limit < 1) limit = 1;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT id, text, start_at, end_at, interval_sec, last_sent, enabled, created_by, created_at " +
                "FROM announces ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<AnnounceRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new AnnounceRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
                    r.GetInt64(4), r.GetInt64(5), r.GetInt32(6) != 0, r.GetString(7), r.GetInt64(8)));
            return rows;
        }
    }

    /// <summary>Drop one scheduled announce. False when there was no such id.</summary>
    public bool DeleteAnnounce(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM announces WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// The announces due at <paramref name="nowUnix"/>, and their <c>last_sent</c> stamped so the
    /// same tick cannot send one twice. A one-shot (interval 0) is disabled as it goes out.
    /// </summary>
    public List<AnnounceRow> TakeDueAnnounces(long nowUnix)
    {
        lock (_lock)
        {
            var due = new List<AnnounceRow>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, text, start_at, end_at, interval_sec, last_sent, enabled, created_by, created_at " +
                    "FROM announces WHERE enabled<>0 AND start_at<=$n AND (end_at=0 OR end_at>=$n) " +
                    "AND (last_sent=0 OR (interval_sec>0 AND last_sent+interval_sec<=$n)) ORDER BY id";
                cmd.Parameters.AddWithValue("$n", nowUnix);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    due.Add(new AnnounceRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
                        r.GetInt64(4), r.GetInt64(5), r.GetInt32(6) != 0, r.GetString(7), r.GetInt64(8)));
            }
            foreach (var a in due)
            {
                using var up = _db.CreateCommand();
                up.CommandText = a.IntervalSec > 0
                    ? "UPDATE announces SET last_sent=$n WHERE id=$i"
                    : "UPDATE announces SET last_sent=$n, enabled=0 WHERE id=$i";
                up.Parameters.AddWithValue("$n", nowUnix);
                up.Parameters.AddWithValue("$i", a.Id);
                up.ExecuteNonQuery();
            }
            return due;
        }
    }

    // -------------------------------------------------------- T113: character play time

    /// <summary>
    /// Seconds this character has spent in world. <c>S_SEND_USER_PLAY_TIME.totalPlaytime</c> and
    /// <c>S_PLAY_TIME</c> both report it; the real Arbiter keeps the live counter at User+0x1E4
    /// and we keep the committed total here.
    /// </summary>
    public long GetCharacterPlaySeconds(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT play_seconds FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0L : Convert.ToInt64(v);
        }
    }

    /// <summary>
    /// Add a finished session to the character AND to its account, in one transaction. Returns
    /// the character's new total. A negative or zero delta is ignored rather than rolled back -
    /// a clock that went backwards must not eat a player's history.
    /// </summary>
    public long AddCharacterPlaySeconds(int characterId, long deltaSeconds)
    {
        if (deltaSeconds <= 0) return GetCharacterPlaySeconds(characterId);
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var chr = _db.CreateCommand())
            {
                chr.Transaction = tx;
                chr.CommandText = "UPDATE characters SET play_seconds = play_seconds + $d WHERE id=$id";
                chr.Parameters.AddWithValue("$d", deltaSeconds);
                chr.Parameters.AddWithValue("$id", characterId);
                if (chr.ExecuteNonQuery() != 1) { tx.Rollback(); return 0L; }
            }
            using (var acct = _db.CreateCommand())
            {
                acct.Transaction = tx;
                acct.CommandText =
                    "UPDATE accounts SET play_time_sec = play_time_sec + $d " +
                    "WHERE id = (SELECT account_id FROM characters WHERE id=$id)";
                acct.Parameters.AddWithValue("$d", deltaSeconds);
                acct.Parameters.AddWithValue("$id", characterId);
                acct.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return GetCharacterPlaySeconds(characterId);
    }

    // -------------------------------------------------------- T101c: account play time

    /// <summary>
    /// Seconds this account has been in world - the sum of its characters' <c>play_seconds</c>.
    /// T113 wired it: <see cref="AddCharacterPlaySeconds"/> adds to both in one transaction when
    /// a session ends, so this and the per-character figure cannot drift.
    /// </summary>
    public long GetAccountPlayTime(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT play_time_sec FROM accounts WHERE id=$a";
            cmd.Parameters.AddWithValue("$a", accountId);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0L : Convert.ToInt64(v);
        }
    }

    /// <summary>Add to the running total. Returns the new total.</summary>
    public long AddAccountPlayTime(long accountId, long deltaSeconds)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE accounts SET play_time_sec = MAX(0, play_time_sec + $d) WHERE id=$a";
            cmd.Parameters.AddWithValue("$d", deltaSeconds);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.ExecuteNonQuery();
        }
        return GetAccountPlayTime(accountId);
    }

    public bool AddAdminLog(long atUnix, string sourceIp, string action, string target,
        string reason, int result)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO admin_log(at, source_ip, action, target, reason, result) " +
                    "VALUES($t,$i,$a,$g,$r,$s)";
                cmd.Parameters.AddWithValue("$t", atUnix);
                cmd.Parameters.AddWithValue("$i", sourceIp ?? string.Empty);
                cmd.Parameters.AddWithValue("$a", action ?? string.Empty);
                cmd.Parameters.AddWithValue("$g", target ?? string.Empty);
                cmd.Parameters.AddWithValue("$r", reason ?? string.Empty);
                cmd.Parameters.AddWithValue("$s", result);
                return cmd.ExecuteNonQuery() == 1;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                _log.LogWarning("AddAdminLog failed: {Msg}", ex.Message);
                return false;
            }
        }
    }

    /// <summary>The most recent admin actions, newest first.</summary>
    public List<AdminLogRow> GetAdminLog(int limit)
    {
        if (limit < 1) limit = 1;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT at, source_ip, action, target, reason, result FROM admin_log " +
                "ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<AdminLogRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new AdminLogRow(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.GetString(3), r.GetString(4), r.GetInt32(5)));
            return rows;
        }
    }

    /// <summary>
    /// Accounts whose name contains <paramref name="term"/>, or the single account whose id it
    /// is. An empty term lists the first <paramref name="limit"/> accounts, which is what the
    /// admin page opens with. LIKE wildcards in the term are escaped, as GetNamesStartingWith
    /// does - an operator typing % should search for a percent sign, not for everything.
    /// </summary>
    public List<AccountRecord> SearchAccounts(string? term, int limit)
    {
        if (limit < 1) limit = 1;
        term ??= string.Empty;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            if (long.TryParse(term, out long id))
            {
                cmd.CommandText = "SELECT id, name, admin_level FROM accounts WHERE id = $i";
                cmd.Parameters.AddWithValue("$i", id);
            }
            else
            {
                cmd.CommandText =
                    "SELECT id, name, admin_level FROM accounts WHERE name LIKE $p ESCAPE '\\' " +
                    "ORDER BY name LIMIT $n";
                string escaped = term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                cmd.Parameters.AddWithValue("$p", "%" + escaped + "%");
                cmd.Parameters.AddWithValue("$n", limit);
            }
            var rows = new List<AccountRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new AccountRecord { Id = r.GetInt64(0), Name = r.GetString(1), AdminLevel = r.GetInt32(2) });
            return rows;
        }
    }

    // ------------------------------------------------------------- T98: guild quests

    /// <summary>One guild s state for one quest of the catalogue.</summary>
    public sealed record GuildQuestState(int GuildId, int QuestId, int Status, long StartedAt,
        long EndsAt, int StarterDbId, int Progress);

    /// <summary>Quest states for this guild, in quest order. Empty when none has ever run.</summary>
    public List<GuildQuestState> GetGuildQuests(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT guild_id, quest_id, status, started_at, ends_at, starter_db_id, progress " +
                "FROM guild_quests WHERE guild_id=$g ORDER BY quest_id";
            cmd.Parameters.AddWithValue("$g", guildId);
            var rows = new List<GuildQuestState>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(QaQuestDeadline(new GuildQuestState(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
                    r.GetInt64(3), r.GetInt64(4), r.GetInt32(5), r.GetInt32(6))));
            return rows;
        }
    }

    /// <summary>The quest this guild is running, or null. Status 1 is running.</summary>
    public GuildQuestState? GetRunningGuildQuest(int guildId)
    {
        foreach (var q in GetGuildQuests(guildId)) if (q.Status == 1) return q;
        return null;
    }

    /// <summary>Upsert one quest state. Start and finish are not modelled - the capture never
    /// exercised them - so this is the seam a later task drives, and what the list reads.</summary>
    public bool SetGuildQuest(int guildId, int questId, int status, long startedAt, long endsAt,
        int starterDbId, int progress)
    {
        lock (_lock)
        {
            ResetQaQuestDeadline(guildId, questId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO guild_quests(guild_id, quest_id, status, started_at, ends_at, " +
                "starter_db_id, progress) VALUES($g,$q,$s,$a,$e,$u,$p) " +
                "ON CONFLICT(guild_id, quest_id) DO UPDATE SET status=excluded.status, " +
                "started_at=excluded.started_at, ends_at=excluded.ends_at, " +
                "starter_db_id=excluded.starter_db_id, progress=excluded.progress";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$q", questId);
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$a", startedAt);
            cmd.Parameters.AddWithValue("$e", endsAt);
            cmd.Parameters.AddWithValue("$u", starterDbId);
            cmd.Parameters.AddWithValue("$p", progress);
            return cmd.ExecuteNonQuery() >= 1;
        }
    }

    /// <summary>
    /// T135. The reward a finished guild quest pays, applied in one statement so the two
    /// columns cannot drift apart. Both deltas are read off classic_live2: across its three
    /// finishes the guild exp went 157620 -&gt; 157640 -&gt; 157660 and the funds 711 -&gt; 712 -&gt;
    /// 713, so +20 and +1, while <c>point</c> stayed 14 throughout - the point push after a
    /// finish is a refresh, not a reward, and this does not touch it.
    /// </summary>
    public bool AddGuildQuestReward(int guildId, long expDelta, long moneyDelta)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE guilds SET exp = MAX(0, exp + $x), money = MAX(0, money + $m) " +
                "WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$x", expDelta);
            cmd.Parameters.AddWithValue("$m", moneyDelta);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>T201: spUpdateGuildLevelAndExp/Point/Money. Compute under the store lock;
    /// rejected native QA changes return null without silently clamping a negative balance.</summary>
    public GuildRow? UpdateGuildEconomy(int guildId, Func<GuildRow, GuildRow?> update)
    {
        lock (_lock)
        {
            var current = GetGuild(guildId);
            if (current == null || update(current) is not GuildRow next) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE guilds SET level=$l,exp=$e,point=$p,money=$m WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$l", next.Level);
            cmd.Parameters.AddWithValue("$e", next.Exp);
            cmd.Parameters.AddWithValue("$p", next.Point);
            cmd.Parameters.AddWithValue("$m", next.Money);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() == 1 ? next : null;
        }
    }

    /// <summary>Arb028:9281-9329, ClearQuestStatusInfoByQAC -> spDeleteGuildQuest.</summary>
    public void ResetGuildQuestStatus(int guildId)
    {
        lock (_lock)
        {
            ResetQaQuestDeadline(guildId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM guild_quests WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Unix second of this guild s last money incentive, 0 when it has never had one.</summary>
    public long GetGuildIncentiveTime(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MAX(last_incentive_at,last_incentive_time) FROM guilds WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$g", guildId);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0L : Convert.ToInt64(v);
        }
    }

    /// <summary>Stamp the incentive cooldown. False when the guild row is gone.</summary>
    public bool SetGuildIncentiveTime(int guildId, long whenUnix)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            // T201: T90's added cooldown column and the native GuildData mirror field must agree.
            cmd.CommandText = "UPDATE guilds SET last_incentive_at=$t,last_incentive_time=$t WHERE guild_id=$g";
            cmd.Parameters.AddWithValue("$t", whenUnix);
            cmd.Parameters.AddWithValue("$g", guildId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// spAddGuildMember(int userDbId, int guildDbId, int guildGroupId) -> OUT rows, OUT joinDate.
    /// Returns the join date it stored, or 0 when the character is already in a guild - the real
    /// Arbiter's OUT rows == 0 case.
    /// </summary>
    public long AddGuildMember(int guildId, int userDbId, string name, int race, int userClass,
        int gender, int level, long accountId, int guildGroupId = DefaultGuildGroupId, long joinDate = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (NoSuchOwner("AddGuildMember", userDbId) || NoSuchGuild("AddGuildMember", guildId)) return 0;
        lock (_lock)
        {
            if (CountGuildMembers(guildId) >= Handlers.QaGuildCommands.MemberMaximum) return 0;
            using var probe = _db.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM guild_members WHERE user_db_id = $u";
            probe.Parameters.AddWithValue("$u", userDbId);
            if ((long)probe.ExecuteScalar()! > 0) return 0;

            if (joinDate == 0) joinDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _qaGuildPlayStart[userDbId] = joinDate * 1000;
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

    // ============================================================ T83/T86: cards
    //
    // Two tables, because the three card frames name two different owners. The dumpers
    // (Arb_part_017.c:12213 / 11096 / 17206):
    //
    //   SDB_REGISTER_CARD  guard 0x19  DlmId@06, AccountDbId@0A (i64), CardTemplateId@12, Amount@16
    //   SDB_MOUNT_CARD     guard 0x1D  DlmId@06, AccountDbId@0A (i64), UserDbId@12,
    //                                  PresetIndex@16, CardTemplateId@1A
    //   SDB_UNMOUNT_CARD   guard 0x1D  the same as the mount
    //
    // The register frame has no character in it at all and the mount frame has both plus a preset
    // index, so the collection belongs to the account and each character arranges its own presets
    // out of it. In cap_social4 both ids read 1, which is exactly why T83 could key the whole
    // thing on the character and nothing complained.

    /// <summary>One owned card, account-wide.</summary>
    public sealed record CardRow(int CardTemplateId, int Amount);

    /// <summary>One mounted card: which slot of which character's preset it sits in.</summary>
    public sealed record CardMountRow(int PresetIndex, int CardTemplateId);

    /// <summary>
    /// SDB_REGISTER_CARD. Adds <paramref name="amount"/> to what this ACCOUNT already has of
    /// that card, which is what "register" means: cap_social4.log seq 7032 registers card 311034
    /// with amount 1 for AccountDbId 1, and seq 7078 adds 19 more of the same one.
    /// </summary>
    public void AddCard(long accountId, int cardTemplateId, int amount,
        int collectionPoints = 0, Func<int, int>? levelForPoints = null)
    {
        if (accountId <= 0 || cardTemplateId == 0) return;

        lock (_lock)
        {
            // T85: an UPDATE then a conditional INSERT rather than ON CONFLICT DO UPDATE. The
            // upsert form was the one write in T83 that never landed, and it is the only one in
            // the class that reuses a bound parameter inside its DO UPDATE clause; two plain
            // statements under the same lock do the same job with nothing to be clever about.
            using (var up = _db.CreateCommand())
            {
                up.CommandText =
                    "UPDATE cards SET amount = amount + $a WHERE account_id=$k AND card_template_id=$t";
                up.Parameters.AddWithValue("$a", amount);
                up.Parameters.AddWithValue("$k", accountId);
                up.Parameters.AddWithValue("$t", cardTemplateId);
                if (up.ExecuteNonQuery() > 0)
                {
                    UpdateBook();
                    return;
                }
            }

            using var ins = _db.CreateCommand();
            ins.CommandText =
                "INSERT INTO cards(account_id, card_template_id, amount) VALUES($k,$t,$a)";
            ins.Parameters.AddWithValue("$k", accountId);
            ins.Parameters.AddWithValue("$t", cardTemplateId);
            ins.Parameters.AddWithValue("$a", amount);
            ins.ExecuteNonQuery();
            UpdateBook();

            // Account::AddCardAmount updates amount, BookPoint and BookLevel together
            // (Arb060:19005-19007,19067-19069). Preserve preset count when registering.
            void UpdateBook()
            {
                if (levelForPoints == null) return; // No sheet: no invented point value.
                var info = GetCardInfo(accountId);
                int points = checked(info.BookPoint + amount * collectionPoints);
                SetCardInfo(accountId, info with { BookPoint = points, BookLevel = levelForPoints(points) });
            }
        }
    }

    /// <summary>
    /// T190/T201. Account::ResetCardCollectionBook followed by PerfectCardCollection.
    /// GameDatabaseDefinition.xml:21055-21075 deletes all account presets, while leaving the
    /// separate CardPresetIndex table intact; Arb065:8787-8840 restores info defaults.
    ///
    /// <para>T210: it replaces the COLLECTION, and nothing else durable. Retail's refresh right
    /// after the command reports preset amount 1 with no presets and no claimed book rewards
    /// (cap_2man_b_client2 2705), and the very NEXT login reports 3 presets and 8 claimed rewards
    /// again (cap_2man_b_client2 3634, cap_bg1_client1 182) - so those rows were never deleted;
    /// only the in-memory Account was reset, which is what that refresh reply describes
    /// (DbProxyHandlers.MarkPerfectCardRefresh). T190 deleted the rows, so after one
    /// /@perfect_card_collection the card panel was empty for good.</para>
    /// </summary>
    public void ReplaceCardCollection(long accountId, int characterId, IReadOnlyList<CardRow> cards, CardInfoRow info)
    {
        lock (_lock)
        {
            using (var wipe = _db.CreateCommand())
            {
                wipe.CommandText = "DELETE FROM cards WHERE account_id=$a";
                wipe.Parameters.AddWithValue("$a", accountId);
                wipe.ExecuteNonQuery();
            }
            foreach (var card in cards) AddCard(accountId, card.CardTemplateId, card.Amount);
            // preset_amount IS the CardPresetIndex value the native reset leaves intact.
            SetCardInfo(accountId, info with { PresetAmount = GetCardInfo(accountId).PresetAmount });
        }
    }

    /// <summary>Every card on this account, lowest template first.</summary>
    public IReadOnlyList<CardRow> GetAccountCards(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT card_template_id, amount FROM cards WHERE account_id=$k ORDER BY card_template_id";
            cmd.Parameters.AddWithValue("$k", accountId);
            var rows = new List<CardRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new CardRow(r.GetInt32(0), r.GetInt32(1)));
            return rows;
        }
    }

    /// <summary>
    /// SDB_MOUNT_CARD. The character has to own the card through its account - a mount for a card
    /// the collection does not hold is World and us disagreeing, and writing it would hide that.
    /// </summary>
    public bool MountCard(int characterId, int presetIndex, int cardTemplateId)
    {
        if (NoSuchOwner("MountCard", characterId)) return false;
        if (presetIndex < 0 || cardTemplateId == 0) return false;
        if (!AccountHasCard(AccountOf(characterId), cardTemplateId))
        {
            _log.LogWarning("MountCard: character {Id} has no card {Card} on its account - not mounted",
                characterId, cardTemplateId);
            return false;
        }

        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT OR IGNORE INTO card_mounts(character_id, preset_index, card_template_id) " +
                "VALUES($c,$p,$t)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$p", presetIndex);
            cmd.Parameters.AddWithValue("$t", cardTemplateId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>SDB_UNMOUNT_CARD. It repeats the preset index the mount used rather than sending
    /// a sentinel, so the row is identified the same way it was created.</summary>
    public bool UnmountCard(int characterId, int presetIndex, int cardTemplateId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "DELETE FROM card_mounts WHERE character_id=$c AND preset_index=$p AND card_template_id=$t";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$p", presetIndex);
            cmd.Parameters.AddWithValue("$t", cardTemplateId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>This character's mounts, by preset then template.</summary>
    public IReadOnlyList<CardMountRow> GetCardMounts(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT preset_index, card_template_id FROM card_mounts WHERE character_id=$c " +
                "ORDER BY preset_index, card_template_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<CardMountRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new CardMountRow(r.GetInt32(0), r.GetInt32(1)));
            return rows;
        }
    }

    // ============================================================ T167: cards, EP pages, polishing, dungeon rank

    /// <summary>T167. The account half of DBS_RESPONSE_CARD_DATA's header. An account with no row
    /// reads as the defaults every capture shows for one that never touched cards: one preset,
    /// collection book level 1, no points (cap_social4 447, 670, 5070, 5692).</summary>
    public sealed record CardInfoRow(int PresetAmount, int BookLevel, int BookPoint);
    public static readonly CardInfoRow DefaultCardInfo = new(1, 1, 0);

    public CardInfoRow GetCardInfo(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT preset_amount, book_level, book_point FROM card_info WHERE account_id=$a";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new CardInfoRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : DefaultCardInfo;
        }
    }

    /// <summary>SDB_CREATE_CARD_INFO: the row World asks for, replacing any earlier one.</summary>
    public void SetCardInfo(long accountId, CardInfoRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO card_info (account_id, preset_amount, book_level, book_point) "
                            + "VALUES ($a, $n, $l, $p)";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$n", row.PresetAmount);
            cmd.Parameters.AddWithValue("$l", row.BookLevel);
            cmd.Parameters.AddWithValue("$p", row.BookPoint);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>SDB_INCREASE_CARD_PRESET: one more preset (spChangeCardPresetAmount with the
    /// current amount + 1). Returns the new amount.</summary>
    public int IncreaseCardPresetAmount(long accountId)
    {
        var cur = GetCardInfo(accountId);
        var next = cur with { PresetAmount = cur.PresetAmount + 1 };
        SetCardInfo(accountId, next);
        return next.PresetAmount;
    }

    /// <summary>The preset a character has selected (the Arbiter's per-character map on the
    /// account); 0 for one that never chose.</summary>
    public int GetCardPresetIndex(int characterId)
        => (int)ScalarLong("SELECT card_preset_index FROM characters WHERE id=$c", characterId);

    public bool SetCardPresetIndex(int characterId, int presetIndex)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET card_preset_index=$p WHERE id=$c";
            cmd.Parameters.AddWithValue("$p", presetIndex);
            cmd.Parameters.AddWithValue("$c", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public sealed record CardCombineRow(int CombineListId, int Level);

    /// <summary>SDB_ACTIVATE_CARD_COMBINE_LIST: the combine list at this level (map insert or replace).</summary>
    public void SetCardCombine(long accountId, int combineListId, int level)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO card_combines (account_id, combine_list_id, level) VALUES ($a, $i, $l)";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$i", combineListId);
            cmd.Parameters.AddWithValue("$l", level);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>SDB_DEACTIVATE_CARD_COMBINE_LIST. False when the list was not active.</summary>
    public bool RemoveCardCombine(long accountId, int combineListId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM card_combines WHERE account_id=$a AND combine_list_id=$i";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$i", combineListId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<CardCombineRow> GetCardCombines(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT combine_list_id, level FROM card_combines WHERE account_id=$a ORDER BY combine_list_id";
            cmd.Parameters.AddWithValue("$a", accountId);
            var rows = new List<CardCombineRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new CardCombineRow(r.GetInt32(0), r.GetInt32(1)));
            return rows;
        }
    }

    /// <summary>T167. DBS_USER_LOAD_EP_PERK's five scalars (the Arbiter's user +0x8910..+0x8920).
    /// A character that never touched EP reads all zeros, as every capture does.</summary>
    public sealed record EpPageRow(int UsedEp, int PreEpLevel, int PreEpTotalPoint, int CurrentPage, int MaxPage);
    public sealed record EpPerkRow(int Page, int PerkId, int Level);

    /// <summary>The number of perk pages DBS_USER_LOAD_EP_PERK always carries (the Arbiter's
    /// fixed array of five maps, cap_social4 432).</summary>
    public const int EpPageCount = 5;

    public EpPageRow GetEpPages(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ep_used_point, ep_pre_level, ep_pre_total_point, ep_current_page, ep_max_page "
                            + "FROM characters WHERE id=$c";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new EpPageRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4))
                            : new EpPageRow(0, 0, 0, 0, 0);
        }
    }

    /// <summary>Every learned perk, page then perk id - the order the Arbiter's maps iterate.</summary>
    public IReadOnlyList<EpPerkRow> GetEpPerks(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT page_index, perk_id, perk_level FROM ep_perks WHERE character_id=$c ORDER BY page_index, perk_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<EpPerkRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new EpPerkRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            return rows;
        }
    }

    /// <summary>SDB_CHANGE_EP_PAGE (spChangeCurrentEpPage): the page and the points used on it.</summary>
    public bool ChangeEpPage(int characterId, int page, int usedEp)
        => ExecCharacter("UPDATE characters SET ep_current_page=$a, ep_used_point=$b WHERE id=$c", characterId, page, usedEp);

    /// <summary>SDB_EXPAND_EP_PAGE (spExpandEpPage): one more page, which becomes the current one.</summary>
    public bool ExpandEpPage(int characterId)
        => ExecCharacter("UPDATE characters SET ep_max_page=ep_max_page+1, ep_current_page=ep_max_page+1 WHERE id=$c", characterId, 0, 0);

    /// <summary>SDB_UPDATE_PRE_EP_INFO (spUpdateUserPreEPInfo) - DBS_USER_LOAD_EP_PERK's PreEpLevel / PreEpTotalPoint.</summary>
    public bool SetEpPre(int characterId, int level, int totalPoint)
        => ExecCharacter("UPDATE characters SET ep_pre_level=$a, ep_pre_total_point=$b WHERE id=$c", characterId, level, totalPoint);

    /// <summary>SDB_USER_LEARN_EP_PERK: each (perk, level) into the CURRENT page (spUpdateEpPerk),
    /// then the used points go up by what the request spent (spAddUsedExtraPoint).</summary>
    public void LearnEpPerks(int characterId, IEnumerable<(int PerkId, int Level)> perks, int usedEpDelta)
    {
        ArgumentNullException.ThrowIfNull(perks);
        int page = GetEpPages(characterId).CurrentPage;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (perk, level) in perks)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO ep_perks (character_id, page_index, perk_id, perk_level) VALUES ($c, $p, $k, $l)";
                cmd.Parameters.AddWithValue("$c", characterId);
                cmd.Parameters.AddWithValue("$p", page);
                cmd.Parameters.AddWithValue("$k", perk);
                cmd.Parameters.AddWithValue("$l", level);
                cmd.ExecuteNonQuery();
            }
            using (var up = _db.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = "UPDATE characters SET ep_used_point=ep_used_point+$d WHERE id=$c";
                up.Parameters.AddWithValue("$d", usedEpDelta);
                up.Parameters.AddWithValue("$c", characterId);
                up.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>SDB_USER_RESET_EP_PERK (spResetEpPerk + spResetUserExtraPointData): the current
    /// page's perks go and the used points drop to 0.</summary>
    public void ResetEpPerks(int characterId)
    {
        int page = GetEpPages(characterId).CurrentPage;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM ep_perks WHERE character_id=$c AND page_index=$p; "
                            + "UPDATE characters SET ep_used_point=0 WHERE id=$c";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$p", page);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>SDB_RESET_EXTRA_POINT_DATA (spResetExtraPointData): the EP progress T77 stores
    /// goes back to zero - exp, level, points, daily exp, reserve bonus and its reset stamp
    /// (the six fields the Arbiter clears). The daily limit is a setting, not progress, and stays.</summary>
    public bool ResetExtraPointData(int characterId)
        => WriteAccountEp(characterId, "ep_exp=0, ep_level=0, ep_point=0, ep_daily_exp=0, "
                       + "ep_reserve_bonus=0, ep_reset_time=0");

    /// <summary>SDB_USER_INCREASE_EP_POINT_BY_ITEM: the item's points on top of the stored ones.</summary>
    public bool AddEpPoint(int characterId, int gain)
        => WriteAccountEp(characterId, "ep_point=ep_point+$value", gain);

    /// <summary>T167. DBS_LOAD_SKILL_POLISHING's scalars (the Arbiter's user +0x8980..+0x8990).</summary>
    public sealed record PolishingRow(int Level, int Point, int TotalPoint, long Exp);
    public sealed record PolishingOptionRow(int PolishingId, int EffectId, bool Applied);
    public sealed record PolishingLevelRow(int PolishingId, int EffectId);

    public PolishingRow GetPolishing(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT level, point, total_point, exp FROM skill_polishing WHERE character_id=$c";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new PolishingRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3))
                            : new PolishingRow(0, 0, 0, 0);
        }
    }

    /// <summary>SDB_SKILL_POLISHING_ADD_EXP: World sends the four new values, not a delta.</summary>
    public void SetPolishing(int characterId, PolishingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO skill_polishing (character_id, level, point, total_point, exp) "
                            + "VALUES ($c, $l, $p, $t, $e)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$l", row.Level);
            cmd.Parameters.AddWithValue("$p", row.Point);
            cmd.Parameters.AddWithValue("$t", row.TotalPoint);
            cmd.Parameters.AddWithValue("$e", row.Exp);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The Arbiter's point spend (FUN_1403c9260): refused unless 0 &lt;= required &lt;= point.</summary>
    public bool SpendPolishingPoint(int characterId, int required)
    {
        var cur = GetPolishing(characterId);
        if (required < 0 || required > cur.Point) return false;
        SetPolishing(characterId, cur with { Point = cur.Point - required });
        return true;
    }

    /// <summary>SDB_SKILL_POLISHING_UPGRADE_LEVEL (spUpgradeSkillPolishingLevel): the level map's
    /// entry for this polishing id becomes the new effect.</summary>
    public void SetPolishingLevel(int characterId, int polishingId, int effectId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO skill_polishing_levels (character_id, polishing_id, effect_id) VALUES ($c, $i, $e)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$i", polishingId);
            cmd.Parameters.AddWithValue("$e", effectId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The option map is keyed (polishing id, effect id) -&gt; applied. Unlock
    /// (spUnlockSkillPolishingOption) and change (spChangeSkillPolishingOption) both clear the
    /// applied flag of the effect that was applied and set it on the new one; unlock also creates
    /// the new one, change needs it to exist already. Returns false when change finds no such option.</summary>
    public bool ApplyPolishingOption(int characterId, int polishingId, int newEffectId, int previousEffectId, bool create)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            if (!create)
            {
                using var q = _db.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "SELECT COUNT(*) FROM skill_polishing_options WHERE character_id=$c AND polishing_id=$i AND effect_id=$e";
                q.Parameters.AddWithValue("$c", characterId);
                q.Parameters.AddWithValue("$i", polishingId);
                q.Parameters.AddWithValue("$e", newEffectId);
                if (Convert.ToInt64(q.ExecuteScalar()) == 0) return false;
            }
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE skill_polishing_options SET applied=0 WHERE character_id=$c AND polishing_id=$i AND effect_id=$o; "
                            + "INSERT INTO skill_polishing_options (character_id, polishing_id, effect_id, applied) VALUES ($c, $i, $e, 1) "
                            + "ON CONFLICT(character_id, polishing_id, effect_id) DO UPDATE SET applied=1";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$i", polishingId);
            cmd.Parameters.AddWithValue("$o", previousEffectId);
            cmd.Parameters.AddWithValue("$e", newEffectId);
            cmd.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    public IReadOnlyList<PolishingOptionRow> GetPolishingOptions(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT polishing_id, effect_id, applied FROM skill_polishing_options WHERE character_id=$c "
                            + "ORDER BY polishing_id, effect_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<PolishingOptionRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new PolishingOptionRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2) != 0));
            return rows;
        }
    }

    public IReadOnlyList<PolishingLevelRow> GetPolishingLevels(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT polishing_id, effect_id FROM skill_polishing_levels WHERE character_id=$c ORDER BY polishing_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<PolishingLevelRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new PolishingLevelRow(r.GetInt32(0), r.GetInt32(1)));
            return rows;
        }
    }

    /// <summary>T167. One SDB_UPDATE_DUNGEON_RANK_RECORD: a character's best point / time for a
    /// dungeon in a season, as World computed them, plus this run's breakdown.</summary>
    public sealed record DungeonRankRow(int CharacterId, int DungeonId, int Season, int TopPoint, int TopTime,
                                        long PlayDate, bool NewScore, int TimePoint, int KillPoint, int BonusPoint,
                                        string MvpName);

    /// <summary>Keeps one row per (character, dungeon, season): World's TopPointRecord and
    /// TopTimeRecord are already the bests, so the latest frame simply replaces the row.</summary>
    public void RecordDungeonRank(DungeonRankRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO dungeon_rank_records (character_id, dungeon_id, season, top_point, "
                + "top_time, play_date, new_score, time_point, kill_point, bonus_point, mvp_name) "
                + "VALUES ($c, $d, $s, $p, $t, $pd, $n, $tp, $kp, $bp, $m)";
            cmd.Parameters.AddWithValue("$c", row.CharacterId);
            cmd.Parameters.AddWithValue("$d", row.DungeonId);
            cmd.Parameters.AddWithValue("$s", row.Season);
            cmd.Parameters.AddWithValue("$p", row.TopPoint);
            cmd.Parameters.AddWithValue("$t", row.TopTime);
            cmd.Parameters.AddWithValue("$pd", row.PlayDate);
            cmd.Parameters.AddWithValue("$n", row.NewScore ? 1 : 0);
            cmd.Parameters.AddWithValue("$tp", row.TimePoint);
            cmd.Parameters.AddWithValue("$kp", row.KillPoint);
            cmd.Parameters.AddWithValue("$bp", row.BonusPoint);
            cmd.Parameters.AddWithValue("$m", row.MvpName ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<DungeonRankRow> GetDungeonRanks(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT character_id, dungeon_id, season, top_point, top_time, play_date, new_score, "
                + "time_point, kill_point, bonus_point, mvp_name FROM dungeon_rank_records WHERE character_id=$c "
                + "ORDER BY season, dungeon_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<DungeonRankRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new DungeonRankRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                    r.GetInt64(5), r.GetInt32(6) != 0, r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetString(10)));
            return rows;
        }
    }

    /// <summary>
    /// T211. One row of the dungeon record board: a character's best for one dungeon in one
    /// season, with the identity columns <c>S_DUNGEON_RANK_RECORD_LIST</c> renders beside it.
    /// </summary>
    public readonly record struct DungeonRankBoardRow(int CharacterId, string Name, string GuildName,
        int Class, int Race, int Gender, int TopPoint, int TopTime, long PlayDate);

    /// <summary>
    /// T211. The board for one dungeon and season, best first - highest point, then fastest time.
    /// The guild name is a LEFT JOIN, so a guildless character is an empty string rather than a
    /// missing row.
    /// </summary>
    public IReadOnlyList<DungeonRankBoardRow> GetDungeonRankBoard(int dungeonId, int season, int limit = 100)
    {
        if (limit <= 0) limit = 1;
        if (limit > 200) limit = 200;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT r.character_id, c.name, COALESCE(g.name, ''), c.class, c.race, c.gender, " +
                "r.top_point, r.top_time, r.play_date " +
                "FROM dungeon_rank_records r " +
                "JOIN characters c ON c.id = r.character_id AND c.deleted_at = 0 " +
                "LEFT JOIN guild_members m ON m.user_db_id = r.character_id " +
                "LEFT JOIN guilds g ON g.guild_id = m.guild_id " +
                "WHERE r.dungeon_id = $d AND r.season = $s " +
                "ORDER BY r.top_point DESC, r.top_time ASC, r.character_id ASC LIMIT $n";
            cmd.Parameters.AddWithValue("$d", dungeonId);
            cmd.Parameters.AddWithValue("$s", season);
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<DungeonRankBoardRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new DungeonRankBoardRow(r.GetInt32(0), r.GetString(1), r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt64(8)));
            return rows;
        }
    }

    /// <summary>
    /// T211. Where a character sits on the <c>bg_rating</c> board: 1 for the highest, 0 when the
    /// character has never finished a battleground. Ties share the better rank, as
    /// <see cref="GetPvpBoard"/> renders them.
    /// </summary>
    public int GetBgRatingRank(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT CASE WHEN c.bg_rating <= 0 THEN 0 ELSE " +
                "(SELECT COUNT(*) + 1 FROM characters o WHERE o.deleted_at = 0 AND o.bg_rating > c.bg_rating) " +
                "END FROM characters c WHERE c.id = $id AND c.deleted_at = 0";
            cmd.Parameters.AddWithValue("$id", characterId);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
        }
    }

    private bool ExecCharacter(string sql, int characterId, int a, int b)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$c", characterId);
            if (sql.Contains("$a")) cmd.Parameters.AddWithValue("$a", a);
            if (sql.Contains("$b")) cmd.Parameters.AddWithValue("$b", b);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    private long ScalarLong(string sql, int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$c", characterId);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }
    }

    // ============================================================ T168: VIP, gold, attendance, hidden passives, servants, book rewards

    /// <summary>T168. DBS_LOAD_USER_VIP_INFO's scalars, per account (the Arbiter's VIP object hangs
    /// off the account, +0x3F40 +0x30F8). No row reads as all zeros.</summary>
    public sealed record VipInfoRow(int PubExp, int GameExp, long TokenAmount, long LastResetTime, int ResetCount);

    public VipInfoRow GetVipInfo(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT pub_exp, game_exp, token_amount, last_reset_time, reset_count FROM vip_info WHERE account_id=$a";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            var result = r.Read() ? new VipInfoRow(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4))
                                 : new VipInfoRow(0, 0, 0, 0, 0);
            return _qaVipPubExp.TryGetValue(accountId, out int publisherExp) ? result with { PubExp = publisherExp } : result;
        }
    }

    /// <summary>SDB_ADD_VIP_GAME_EXP: the delta on top of the stored exp; returns the new total
    /// (DBS_ADD_VIP_GAME_EXP's NewResult, which World sets the exp to).</summary>
    public int AddVipGameExp(long accountId, int delta)
    {
        if (NoSuchAccount("AddVipGameExp", accountId)) return 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO vip_info (account_id, game_exp) VALUES ($a, $d) "
                            + "ON CONFLICT(account_id) DO UPDATE SET game_exp = MAX(-2147483648, MIN(2147483647, game_exp + $d))";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.ExecuteNonQuery();
            using var q = _db.CreateCommand();
            q.CommandText = "SELECT game_exp FROM vip_info WHERE account_id=$a";
            q.Parameters.AddWithValue("$a", accountId);
            return Convert.ToInt32(q.ExecuteScalar());
        }
    }

    /// <summary>SDB_CHANGE_GOLD_CONSUMPTION (spUpdateGoldConsumption): stored as World sends it.</summary>
    public bool SetGoldConsumption(int characterId, long gold)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET gold_consumption=$g WHERE id=$c";
            cmd.Parameters.AddWithValue("$g", gold);
            cmd.Parameters.AddWithValue("$c", characterId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public long GetGoldConsumption(int characterId)
        => ScalarLong("SELECT gold_consumption FROM characters WHERE id=$c", characterId);

    /// <summary>The attendance bitmap (DBS_*_DAILY_ATTENDANCE's AttendBitmap), or null when the
    /// character has none stored.</summary>
    public long? GetAttendance(int characterId)
    {
        long v = ScalarLong("SELECT attend_bitmap FROM characters WHERE id=$c", characterId);
        long set = ScalarLong("SELECT attend_set FROM characters WHERE id=$c", characterId);
        return set != 0 ? v : null;
    }

    /// <summary>SDB_ADMIN_USER_DAILY_ATTENDANCE (GM): mark one login day; returns the bitmap.</summary>
    public long SetAttendanceDay(int characterId, int loginDay)
    {
        long cur = GetAttendance(characterId) ?? 0;
        if (loginDay >= 0 && loginDay < 64) cur |= 1L << loginDay;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET attend_bitmap=$b, attend_set=1 WHERE id=$c";
            cmd.Parameters.AddWithValue("$b", cur);
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.ExecuteNonQuery();
        }
        return cur;
    }

    /// <summary>SDB_USER_LEARN_HIDE_PASSIVE_SKILL: each id learned; the bool is true for an id this
    /// call added (false: already known).</summary>
    public List<(int PassiveId, bool Learned)> LearnHiddenPassives(int characterId, IEnumerable<int> passiveIds)
    {
        ArgumentNullException.ThrowIfNull(passiveIds);
        var result = new List<(int, bool)>();
        if (NoSuchOwner("LearnHiddenPassives", characterId)) return result;
        lock (_lock)
        {
            foreach (int id in passiveIds)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO hidden_passives (character_id, passive_id) VALUES ($c, $p)";
                cmd.Parameters.AddWithValue("$c", characterId);
                cmd.Parameters.AddWithValue("$p", id);
                result.Add((id, cmd.ExecuteNonQuery() == 1));
            }
        }
        return result;
    }

    public IReadOnlyList<int> GetHiddenPassives(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT passive_id FROM hidden_passives WHERE character_id=$c ORDER BY passive_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(r.GetInt32(0));
            return rows;
        }
    }

    /// <summary>T168. One servant, as SDB_ADD_SERVANT creates it.</summary>
    public sealed record ServantRow(long ServantDbId, int CharacterId, int Type, int TemplateId, string Name, int Energy, int Period);

    /// <summary>SDB_ADD_SERVANT: a new servant with a fresh db id (returned).</summary>
    public long AddServant(int characterId, int type, int templateId, string name, int energy, int period)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (NoSuchOwner("AddServant", characterId)) return 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO servants (character_id, type, template_id, name, energy, period) "
                            + "VALUES ($c, $t, $tp, $n, $e, $p)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$tp", templateId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$e", energy);
            cmd.Parameters.AddWithValue("$p", period);
            cmd.ExecuteNonQuery();
            using var id = _db.CreateCommand();
            id.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(id.ExecuteScalar());
        }
    }

    public IReadOnlyList<ServantRow> GetServants(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT servant_db_id, character_id, type, template_id, name, energy, period FROM servants "
                            + "WHERE character_id=$c ORDER BY servant_db_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var rows = new List<ServantRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new ServantRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));
            return rows;
        }
    }

    /// <summary>SDB_RECEIVE_COLLECTION_BOOK_REWARD (spReceiveCollectionBookReward): false when the
    /// account already has it. DBS_RESPONSE_CARD_DATA's ReceivedCollectionBookRewards list.</summary>
    public bool AddCardBookReward(long accountId, int rewardId)
    {
        if (NoSuchAccount("AddCardBookReward", accountId)) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO card_book_rewards (account_id, reward_id) VALUES ($a, $r)";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$r", rewardId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<int> GetCardBookRewards(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT reward_id FROM card_book_rewards WHERE account_id=$a ORDER BY reward_id";
            cmd.Parameters.AddWithValue("$a", accountId);
            var rows = new List<int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(r.GetInt32(0));
            return rows;
        }
    }

    // ============================================================ T170: clear all skills, event progress

    /// <summary>
    /// SDB_USER_CLEAR_ALL_SKILL (User::ClearAllSkill): spClearAllSkill, then the Arbiter memsets
    /// 0x10E0 bytes at User+0x1B90 - blob 6880..11200, both skill regions (40 passive + 500 active
    /// slots). Nothing survives: cap_clearallskill 903 answers 4000 + 320 zero bytes. False when the
    /// character does not exist; a character with no blob yet has nothing to clear.
    /// </summary>
    public bool ClearAllSkills(int characterId)
    {
        if (NoSuchOwner("ClearAllSkills", characterId)) return false;
        const int start = StarterBlob.PassiveSkillsOffset;
        const int size = StarterBlob.PassiveSkillSlots * StarterBlob.SkillEntrySize
                       + StarterBlob.ActiveSkillSlots * StarterBlob.SkillEntrySize;
        lock (_lock)
        {
            using var q = _db.CreateCommand();
            q.CommandText = "SELECT world_blob FROM characters WHERE id=$id";
            q.Parameters.AddWithValue("$id", characterId);
            if (q.ExecuteScalar() is not byte[] blob || blob.Length < start + size) return true;
            Array.Clear(blob, start, size);
            using var u = _db.CreateCommand();
            u.CommandText = "UPDATE characters SET world_blob=$b WHERE id=$id";
            u.Parameters.AddWithValue("$b", blob);
            u.Parameters.AddWithValue("$id", characterId);
            u.ExecuteNonQuery();
        }
        return true;
    }

    public sealed record EventProgressRow(long EventId, int UserId, long AccountId, int Value, bool Flag1, bool Flag2);

    /// <summary>
    /// EventSystemManager::UpdateUserProgressInfoInDb: the value is SET, not added. Flag1 is kept
    /// from the stored row unless <paramref name="overwriteFlag1"/> (the request's header flag).
    /// Returns what GetProgressInfo would read back - the reply's element.
    /// </summary>
    public EventProgressRow SetEventProgress(long eventId, int userId, long accountId, int value,
                                             bool flag1, bool flag2, bool overwriteFlag1)
    {
        lock (_lock)
        {
            if (!overwriteFlag1)
            {
                using var q = _db.CreateCommand();
                q.CommandText = "SELECT flag1 FROM eventsystem_progress WHERE event_id=$e AND user_id=$u AND account_id=$a";
                q.Parameters.AddWithValue("$e", eventId);
                q.Parameters.AddWithValue("$u", userId);
                q.Parameters.AddWithValue("$a", accountId);
                if (q.ExecuteScalar() is long kept) flag1 = kept != 0;
            }
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO eventsystem_progress (event_id, user_id, account_id, value, flag1, flag2) "
                            + "VALUES ($e, $u, $a, $v, $f1, $f2) ON CONFLICT(event_id, user_id, account_id) "
                            + "DO UPDATE SET value=$v, flag1=$f1, flag2=$f2";
            cmd.Parameters.AddWithValue("$e", eventId);
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$f1", flag1 ? 1 : 0);
            cmd.Parameters.AddWithValue("$f2", flag2 ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        return new EventProgressRow(eventId, userId, accountId, value, flag1, flag2);
    }

    public IReadOnlyList<EventProgressRow> GetEventProgress(int userId)
    {
        var list = new List<EventProgressRow>();
        lock (_lock)
        {
            using var q = _db.CreateCommand();
            q.CommandText = "SELECT event_id, user_id, account_id, value, flag1, flag2 FROM eventsystem_progress "
                          + "WHERE user_id=$u ORDER BY event_id";
            q.Parameters.AddWithValue("$u", userId);
            using var r = q.ExecuteReader();
            while (r.Read())
                list.Add(new EventProgressRow(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3),
                                              r.GetInt64(4) != 0, r.GetInt64(5) != 0));
        }
        return list;
    }

    // ============================================================ T89: GM bookmarks

    /// <summary>One saved teleport shortcut. The coordinates are whole numbers - see the DDL.</summary>
    public sealed record GmBookmarkRow(int Index, int Zone, float X, float Y, float Z, string Name);

    // ------------------------- T99: item strings and boards -------------------------

    /// <summary>One board post, newest last - the order Board::SendBoardItemList walks.</summary>
    public sealed record BoardPostRow(long PostId, int BoardId, int WriterId, string Writer,
                                      string Contents, long WrittenAt);

    /// <summary>
    /// C_SET_ITEM_STRING (0x601E) and C_REWRITE_ITEM_STRING (0x65F1). Both end in
    /// <c>User::SetItemString</c>-shaped work on the Arbiter's own item row, and neither sends
    /// a reply. Writing over an existing string is what the rewrite packet is for, so this is
    /// one upsert for both.
    /// </summary>
    public bool SetItemString(long itemDbId, string text, int writerId, long whenUnix)
    {
        if (itemDbId <= 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO item_strings(item_db_id, text, written_by, written_at) " +
                "VALUES($i, $t, $w, $d) " +
                "ON CONFLICT(item_db_id) DO UPDATE SET text = $t, written_by = $w, written_at = $d";
            cmd.Parameters.AddWithValue("$i", itemDbId);
            cmd.Parameters.AddWithValue("$t", text ?? "");
            cmd.Parameters.AddWithValue("$w", writerId);
            cmd.Parameters.AddWithValue("$d", whenUnix);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The string written on an item, or empty. S_PREVIEW_ITEM carries it.</summary>
    public string GetItemString(long itemDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT text FROM item_strings WHERE item_db_id = $i";
            cmd.Parameters.AddWithValue("$i", itemDbId);
            return cmd.ExecuteScalar() as string ?? "";
        }
    }

    /// <summary>C_WRITE_BOARD (0xEDA6): one post onto one board.</summary>
    public long AddBoardPost(int boardId, int writerId, string writer, string contents, long whenUnix)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO board_posts(board_id, writer_id, writer, contents, written_at) " +
                "VALUES($b, $wid, $w, $c, $d); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$b", boardId);
            cmd.Parameters.AddWithValue("$wid", writerId);
            cmd.Parameters.AddWithValue("$w", writer ?? "");
            cmd.Parameters.AddWithValue("$c", contents ?? "");
            cmd.Parameters.AddWithValue("$d", whenUnix);
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        }
    }

    /// <summary>Every post on a board, oldest first.</summary>
    // =====================================================================
    // T115 - the game log. status/GAME-LOG.md.
    // =====================================================================

    // =====================================================================
    // T119 - the two leaderboard sources. status/LEADERBOARD.md section 6.
    // =====================================================================

    /// <summary>
    /// One character's score on one board, before it is ranked. <paramref name="Rating"/> is
    /// T138c's <c>characters.bg_rating</c>: it is what S_PVP_RANKING_LIST's <c>rating</c> field
    /// renders and it takes no part in the ORDER, which is still the score. It is a trailing
    /// optional so every five-argument construction still compiles.
    /// </summary>
    public readonly record struct RankingScore(
        int CharacterId, string Name, int Class, int Level, long Score, int Rating = 0);

    /// <summary>How many characters either board will consider. Far above any population this
    /// server will see, and a bound on the query rather than on the frame.</summary>
    public const int RankingScoreLimit = 500;

    /// <summary>
    /// The PvE board: total dungeon clears per character.
    ///
    /// <para><c>dungeon_cooldowns.clear_count</c> is the only dungeon progress TeraSharp
    /// stores, and it is live - <c>SA_UPDATE_DUNGEON_CLEAR_COUNT</c> (0x13B7) writes it through
    /// <see cref="SetDungeonClearCount"/> on every clear. The T119 brief asked for a
    /// <c>game_log</c> dungeon category; there is no such category (T115 created eight, and
    /// none of the five log opcodes carries a dungeon clear), so this is the nearest real
    /// source rather than an invented one.</para>
    ///
    /// <para>T167: a character with SDB_UPDATE_DUNGEON_RANK_RECORD rows is ranked by rank points
    /// instead - the sum over dungeons of its best TopPointRecord (any season). Clear counts
    /// stay the score of a character without one, so a server with no ranked dungeon run
    /// shows the same board as before.</para>
    /// </summary>
    public List<RankingScore> GetPveRankingScores(int limit = RankingScoreLimit)
        => RankingScores(
            "SELECT id, name, class, level, score FROM (" +
            "SELECT c.id, c.name, c.class, c.level, COALESCE(rp.pts, dc.clears, 0) AS score " +
            "FROM characters c " +
            "LEFT JOIN (SELECT character_id, SUM(best) AS pts FROM (SELECT character_id, dungeon_id, " +
            "MAX(top_point) AS best FROM dungeon_rank_records GROUP BY character_id, dungeon_id) " +
            "GROUP BY character_id) rp ON rp.character_id = c.id " +
            "LEFT JOIN (SELECT owner_id, SUM(clear_count) AS clears FROM dungeon_cooldowns GROUP BY owner_id) dc " +
            "ON dc.owner_id = c.id WHERE c.deleted_at = 0) WHERE score > 0 " +
            "ORDER BY score DESC, id LIMIT $take", limit);

    /// <summary>
    /// The PvP board: <b>battleground rating</b> per character, T138d.
    ///
    /// <para>T119 shipped this as a KILL board, counted out of the game log T115 fills from
    /// <c>SDB_ADD_PVP_USER_LOG</c> (0x27FE), because there was no rating to rank by. T138c
    /// added <c>characters.bg_rating</c> and put it in the frame's <c>rating</c> field while
    /// leaving the ORDER on kills, which meant the column and the ordering disagreed. They
    /// agree now: the board is the rating ladder the client's own field name says it is, and
    /// the score and the rating are the same number.</para>
    ///
    /// <para>A server where nobody has finished a battleground therefore has an EMPTY PvP
    /// board, which is correct - a ladder with no games played has no standings. The kill
    /// counts are still in <c>game_log</c> and still queryable; they are simply not this
    /// board.</para>
    /// </summary>
    public List<RankingScore> GetPvpRankingScores(int limit = RankingScoreLimit)
        => RankingScores(
            "SELECT c.id, c.name, c.class, c.level, c.bg_rating AS score, c.bg_rating " +
            "FROM characters c WHERE c.deleted_at = 0 AND c.bg_rating > 0 " +
            "ORDER BY score DESC, c.id LIMIT $take", limit);

    /// <summary>
    /// Kills per character, the number T119's PvP board used to rank by. Kept because the data
    /// is real and the admin views read it; <see cref="GetPvpRankingScores"/> no longer does.
    /// Only rows where the character is the ACTOR count - being the target of a kill is the
    /// other player's score, not yours.
    /// </summary>
    public List<RankingScore> GetPvpKillScores(int limit = RankingScoreLimit)
        => RankingScores(
            "SELECT c.id, c.name, c.class, c.level, COUNT(*) AS score, c.bg_rating " +
            "FROM game_log g JOIN characters c ON c.id = g.character_id " +
            "WHERE g.category = 'pvp' AND g.action = 'pvp.kill' AND c.deleted_at = 0 " +
            "GROUP BY c.id HAVING score > 0 ORDER BY score DESC, c.id LIMIT $take", limit);

    private List<RankingScore> RankingScores(string sql, int limit)
    {
        if (limit <= 0 || limit > RankingScoreLimit) limit = RankingScoreLimit;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$take", limit);
            var rows = new List<RankingScore>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new RankingScore(r.GetInt32(0), r.GetString(1), r.GetInt32(2),
                                          r.GetInt32(3), r.GetInt64(4),
                                          r.FieldCount > 5 && !r.IsDBNull(5) ? r.GetInt32(5) : 0));
            return rows;
        }
    }

    // =====================================================================
    // T138c - the battleground rating. status/MULTIWORLD-DESIGN.md section T138c.
    // =====================================================================

    /// <summary>
    /// <c>characters.bg_rating</c>, or 0 for a character that has never finished a
    /// battleground (and for one that does not exist - a missing row is a zero rating, not an
    /// exception, because the leaderboard asks about ids it got from elsewhere).
    /// </summary>
    public int GetBgRating(int characterId)
    {
        if (characterId <= 0) return 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT bg_rating FROM characters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", characterId);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }
    }

    /// <summary>Set the rating outright, floored at 0. GM and test path.</summary>
    public void SetBgRating(int characterId, int rating)
    {
        if (characterId <= 0) return;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET bg_rating = $r WHERE id = $id";
            cmd.Parameters.AddWithValue("$r", Math.Max(0, rating));
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Move the rating by <paramref name="delta"/> and return what it became. The floor is 0
    /// and it is applied IN SQL - <c>MAX(0, bg_rating + $d)</c> - rather than by reading,
    /// clamping and writing back, so two results landing at once cannot lose one another's
    /// move. Both statements run inside the store's own lock.
    /// </summary>
    public int AdjustBgRating(int characterId, int delta)
    {
        if (characterId <= 0) return 0;
        lock (_lock)
        {
            using (var up = _db.CreateCommand())
            {
                up.CommandText = "UPDATE characters SET bg_rating = MAX(0, bg_rating + $d) WHERE id = $id";
                up.Parameters.AddWithValue("$d", delta);
                up.Parameters.AddWithValue("$id", characterId);
                up.ExecuteNonQuery();
            }
            using var read = _db.CreateCommand();
            read.CommandText = "SELECT bg_rating FROM characters WHERE id = $id";
            read.Parameters.AddWithValue("$id", characterId);
            var v = read.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }
    }

    /// <summary>One <c>game_log</c> row, as <see cref="QueryGameLog"/> returns it.</summary>
    public sealed record GameLogRow(
        long LogId, long LoggedAt, string Category, string Action, long AccountId,
        long CharacterId, long TargetId, long ItemDbId, int TemplateId, long Amount,
        long Money, string Extra);

    /// <summary>The largest page <see cref="QueryGameLog"/> will hand back in one call.</summary>
    public const int GameLogMaxPageSize = 200;

    /// <summary>
    /// File one decoded log line. <paramref name="loggedAt"/> 0 means now - the frames carry no
    /// timestamp of their own, so arrival time is the only honest one, and it is recorded
    /// rather than derived at query time.
    /// </summary>
    public long AddGameLog(string category, string action, long accountId, long characterId,
                           long targetId, long itemDbId, int templateId, long amount,
                           long money, string? extra, long loggedAt = 0)
    {
        if (loggedAt <= 0) loggedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO game_log (logged_at, category, action, account_id, character_id, " +
                "target_id, item_db_id, template_id, amount, money, extra) " +
                "VALUES ($t, $c, $a, $acc, $chr, $tgt, $item, $tpl, $amt, $money, $x); " +
                "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", loggedAt);
            cmd.Parameters.AddWithValue("$c", category ?? string.Empty);
            cmd.Parameters.AddWithValue("$a", action ?? string.Empty);
            cmd.Parameters.AddWithValue("$acc", accountId);
            cmd.Parameters.AddWithValue("$chr", characterId);
            cmd.Parameters.AddWithValue("$tgt", targetId);
            cmd.Parameters.AddWithValue("$item", itemDbId);
            cmd.Parameters.AddWithValue("$tpl", templateId);
            cmd.Parameters.AddWithValue("$amt", amount);
            cmd.Parameters.AddWithValue("$money", money);
            cmd.Parameters.AddWithValue("$x", extra ?? string.Empty);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// Read the log back, newest first.
    ///
    /// <para>Every filter is optional: 0 / null means "any". <paramref name="fromUnix"/> and
    /// <paramref name="toUnix"/> are inclusive. <paramref name="page"/> is zero-based and
    /// checked as UNSIGNED, and <paramref name="pageSize"/> is clamped to
    /// <see cref="GameLogMaxPageSize"/>, so a hostile page number can only produce an empty
    /// result and never a runaway read.</para>
    /// </summary>
    public List<GameLogRow> QueryGameLog(long accountId = 0, long characterId = 0,
                                         string? category = null, string? action = null, long fromUnix = 0,
                                         long toUnix = 0, int page = 0, int pageSize = 50)
    {
        if (pageSize <= 0) pageSize = 50;
        if (pageSize > GameLogMaxPageSize) pageSize = GameLogMaxPageSize;
        if ((uint)page > int.MaxValue / pageSize) return new List<GameLogRow>();

        var where = new List<string>();
        if (accountId != 0) where.Add("account_id = $acc");
        if (characterId != 0) where.Add("character_id = $chr OR target_id = $chr");
        if (!string.IsNullOrEmpty(category)) where.Add("category = $cat");
        if (!string.IsNullOrEmpty(action)) where.Add("action LIKE $act ESCAPE '\\'");
        if (fromUnix > 0) where.Add("logged_at >= $from");
        if (toUnix > 0) where.Add("logged_at <= $to");

        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            var sql = new System.Text.StringBuilder(
                "SELECT log_id, logged_at, category, action, account_id, character_id, " +
                "target_id, item_db_id, template_id, amount, money, extra FROM game_log");
            for (int i = 0; i < where.Count; i++)
                sql.Append(i == 0 ? " WHERE (" : " AND (").Append(where[i]).Append(')');
            sql.Append(" ORDER BY log_id DESC LIMIT $take OFFSET $skip");
            cmd.CommandText = sql.ToString();

            if (accountId != 0) cmd.Parameters.AddWithValue("$acc", accountId);
            if (characterId != 0) cmd.Parameters.AddWithValue("$chr", characterId);
            if (!string.IsNullOrEmpty(category)) cmd.Parameters.AddWithValue("$cat", category);
            if (!string.IsNullOrEmpty(action)) cmd.Parameters.AddWithValue("$act", action.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            if (fromUnix > 0) cmd.Parameters.AddWithValue("$from", fromUnix);
            if (toUnix > 0) cmd.Parameters.AddWithValue("$to", toUnix);
            cmd.Parameters.AddWithValue("$take", pageSize);
            cmd.Parameters.AddWithValue("$skip", (long)page * pageSize);

            var rows = new List<GameLogRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new GameLogRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
                    r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                    r.GetInt64(7), r.GetInt32(8), r.GetInt64(9), r.GetInt64(10), r.GetString(11)));
            return rows;
        }
    }

    /// <summary>How many rows a <see cref="QueryGameLog"/> with the same filters would match.</summary>
    public long CountGameLog(long accountId = 0, long characterId = 0, string? category = null, string? action = null, long fromUnix = 0, long toUnix = 0)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            var where = new List<string>();
            if (accountId != 0) where.Add("account_id = $acc");
            if (characterId != 0) where.Add("character_id = $chr OR target_id = $chr");
            if (!string.IsNullOrEmpty(category)) where.Add("category = $cat");
            if (!string.IsNullOrEmpty(action)) where.Add("action LIKE $act ESCAPE '\\'");
            if (fromUnix > 0) where.Add("logged_at >= $from");
            if (toUnix > 0) where.Add("logged_at <= $to");
            var sql = new System.Text.StringBuilder("SELECT COUNT(*) FROM game_log");
            for (int i = 0; i < where.Count; i++)
                sql.Append(i == 0 ? " WHERE (" : " AND (").Append(where[i]).Append(')');
            cmd.CommandText = sql.ToString();
            if (accountId != 0) cmd.Parameters.AddWithValue("$acc", accountId);
            if (characterId != 0) cmd.Parameters.AddWithValue("$chr", characterId);
            if (!string.IsNullOrEmpty(category)) cmd.Parameters.AddWithValue("$cat", category);
            if (!string.IsNullOrEmpty(action)) cmd.Parameters.AddWithValue("$act", action.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            if (fromUnix > 0) cmd.Parameters.AddWithValue("$from", fromUnix);
            if (toUnix > 0) cmd.Parameters.AddWithValue("$to", toUnix);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public List<BoardPostRow> GetBoardPosts(int boardId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT post_id, board_id, writer_id, writer, contents, written_at " +
                "FROM board_posts WHERE board_id = $b ORDER BY post_id";
            cmd.Parameters.AddWithValue("$b", boardId);
            var rows = new List<BoardPostRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new BoardPostRow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2),
                                          r.GetString(3), r.GetString(4), r.GetInt64(5)));
            return rows;
        }
    }

    /// <summary>
    /// T99. C_ADMIN_REMOVE_CUSTOM_BOOKMARK -&gt; <c>Bookmark::DeleteCustomBookmark(index)</c>,
    /// after which the handler re-sends the whole list. Deleting an index that is not there is
    /// not an error - the tool sends the same index twice on a double click.
    /// </summary>
    public bool DeleteGmBookmark(long accountId, int index)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "DELETE FROM gm_bookmarks WHERE account_id = $a AND bookmark_index = $i";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$i", index);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>C_ADMIN_ADD_CUSTOM_BOOKMARK. Re-adding the same index overwrites it.</summary>
    public void AddGmBookmark(long accountId, int index, int zone, float x, float y, float z, string? name)
    {
        if (accountId <= 0) return;
        lock (_lock)
        {
            using var del = _db.CreateCommand();
            del.CommandText = "DELETE FROM gm_bookmarks WHERE account_id=$k AND bookmark_index=$i";
            del.Parameters.AddWithValue("$k", accountId);
            del.Parameters.AddWithValue("$i", index);
            del.ExecuteNonQuery();

            using var ins = _db.CreateCommand();
            ins.CommandText =
                "INSERT INTO gm_bookmarks(account_id, bookmark_index, zone, x, y, z, name) " +
                "VALUES($k,$i,$z,$x,$y,$w,$n)";
            ins.Parameters.AddWithValue("$k", accountId);
            ins.Parameters.AddWithValue("$i", index);
            ins.Parameters.AddWithValue("$z", zone);
            // Truncated on the way IN as well as out, so the row and the wire agree.
            ins.Parameters.AddWithValue("$x", (float)(int)x);
            ins.Parameters.AddWithValue("$y", (float)(int)y);
            ins.Parameters.AddWithValue("$w", (float)(int)z);
            ins.Parameters.AddWithValue("$n", name ?? string.Empty);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>This account's bookmarks, lowest index first.</summary>
    public IReadOnlyList<GmBookmarkRow> GetGmBookmarks(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT bookmark_index, zone, x, y, z, name FROM gm_bookmarks WHERE account_id=$k " +
                "ORDER BY bookmark_index";
            cmd.Parameters.AddWithValue("$k", accountId);
            var rows = new List<GmBookmarkRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new GmBookmarkRow(r.GetInt32(0), r.GetInt32(1), (float)r.GetDouble(2),
                                           (float)r.GetDouble(3), (float)r.GetDouble(4), r.GetString(5)));
            return rows;
        }
    }

    /// <summary>The account a character belongs to, or 0. The card page needs it for every
    /// character it draws, its own and anybody else's.</summary>
    public long AccountOf(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT account_id FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    private bool AccountHasCard(long accountId, int cardTemplateId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cards WHERE account_id=$k AND card_template_id=$t";
            cmd.Parameters.AddWithValue("$k", accountId);
            cmd.Parameters.AddWithValue("$t", cardTemplateId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
    }

    // ============================================================ T83: crests

    /// <summary>Record a learned crest. Returns true the first time, like AddVisitedSection.</summary>
    public bool AddCrest(int characterId, int crestId, int value = 0)
    {
        lock (_lock)
        {
            if (crestId <= 0 || !CharacterExists(characterId)) return false;
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO crests(character_id, crest_id, value, applied) VALUES($c,$i,$v,0) " +
                "ON CONFLICT(character_id, crest_id) DO NOTHING";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$i", crestId);
            cmd.Parameters.AddWithValue("$v", value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>T201, spResetCardCollectionBook. The account owns presets for every character.</summary>
    public void ResetCardCollection(long accountId)
    {
        lock (_lock)
        {
            using var transaction = _db.BeginTransaction();
            using var reset = _db.CreateCommand();
            reset.Transaction = transaction;
            reset.CommandText = "DELETE FROM cards WHERE account_id=$a; "
                + "DELETE FROM card_info WHERE account_id=$a; "
                + "DELETE FROM card_combines WHERE account_id=$a; "
                + "DELETE FROM card_book_rewards WHERE account_id=$a; "
                + "DELETE FROM card_mounts WHERE character_id IN "
                + "(SELECT id FROM characters WHERE account_id=$a);";
            reset.Parameters.AddWithValue("$a", accountId);
            reset.ExecuteNonQuery();
            transaction.Commit();
            SetCardInfo(accountId, DefaultCardInfo);
        }
    }

    /// <summary>T201: atomic read/modify/write for signed QA card point/preset changes.</summary>
    public CardInfoRow UpdateCardInfo(long accountId, Func<CardInfoRow, CardInfoRow> update)
    {
        lock (_lock)
        {
            var next = update(GetCardInfo(accountId));
            SetCardInfo(accountId, next);
            return next;
        }
    }

    // Native UserData has 192 eight-byte CrestData slots at +0x34C4: id, useNow, three padding
    // bytes (WorldServer.exe.c:317870–317877). Arb031:938–1068 commits useNow independently
    // of the later World blob save. The SQL flags therefore override that snapshot on entry.
    public const int CrestBlobOffset = 0x34C4, CrestBlobSlots = 192, CrestBlobStride = 8;
    public const int CrestPointBlobOffset = 0x3AEC;

    private static Dictionary<int, int> CrestBlobEntries(byte[]? blob)
    {
        var slots = new Dictionary<int, int>();
        if (blob == null || blob.Length < CrestBlobOffset + CrestBlobSlots * CrestBlobStride) return slots;
        for (int i = 0; i < CrestBlobSlots; i++)
        {
            int at = CrestBlobOffset + i * CrestBlobStride, id = BitConverter.ToInt32(blob, at);
            if (id > 0) slots.TryAdd(id, at);
        }
        return slots;
    }

    /// <summary>1467 replaces the active set; 1469 changes one learned crest. Never learns an unknown id.</summary>
    public bool SetCrestUse(int characterId, IReadOnlyList<int> ids, bool replace, bool applied = true)
    {
        if (ids.Count > CrestBlobSlots || ids.Any(id => id <= 0) || (!replace && ids.Count != 1)) return false;
        lock (_lock)
        {
            if (!CharacterExists(characterId)) return false;
            using var tx = _db.BeginTransaction();
            // Bootstrap only missing/unknown SQL flags from THIS character's saved World blob.
            // Already committed flags must survive a stale SDB_UPDATE_USER_DATA snapshot.
            byte[]? blob;
            using (var get = _db.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = "SELECT world_blob FROM characters WHERE id=$c";
                get.Parameters.AddWithValue("$c", characterId);
                blob = get.ExecuteScalar() as byte[];
            }
            foreach (var (id, at) in CrestBlobEntries(blob))
            {
                using var put = _db.CreateCommand(); put.Transaction = tx;
                put.CommandText = "INSERT INTO crests(character_id,crest_id,value,applied) VALUES($c,$i,0,$a) "
                    + "ON CONFLICT(character_id,crest_id) DO UPDATE SET applied=COALESCE(crests.applied,excluded.applied)";
                put.Parameters.AddWithValue("$c", characterId); put.Parameters.AddWithValue("$i", id);
                put.Parameters.AddWithValue("$a", blob![at + 4] != 0 ? 1 : 0); put.ExecuteNonQuery();
            }
            if (replace)
            {
                using var clear = _db.CreateCommand(); clear.Transaction = tx;
                clear.CommandText = "UPDATE crests SET applied=0 WHERE character_id=$c";
                clear.Parameters.AddWithValue("$c", characterId); clear.ExecuteNonQuery();
            }
            int changed = 0;
            foreach (int id in ids)
            {
                using var set = _db.CreateCommand(); set.Transaction = tx;
                set.CommandText = "UPDATE crests SET applied=$a WHERE character_id=$c AND crest_id=$i";
                set.Parameters.AddWithValue("$c", characterId); set.Parameters.AddWithValue("$i", id);
                set.Parameters.AddWithValue("$a", applied ? 1 : 0); changed += set.ExecuteNonQuery();
            }
            tx.Commit();
            return replace || changed == 1;
        }
    }

    /// <summary>Overlay independently committed glyph state; preserve slot padding and every unrelated field.</summary>
    public void StampCrests(int characterId, byte[]? blob)
    {
        if (blob == null || blob.Length < CrestBlobOffset + CrestBlobSlots * CrestBlobStride) return;
        lock (_lock)
        {
            // Arb032:17859 loads base crestPoint here; extra points travel in AS_ENTER_WORLD.
            var points = GetKnownCrestPoints(characterId);
            if (points is { } known && blob.Length >= CrestPointBlobOffset + 4)
                BitConverter.GetBytes(known.Point).CopyTo(blob, CrestPointBlobOffset);
            var slots = CrestBlobEntries(blob);
            using var get = _db.CreateCommand();
            get.CommandText = "SELECT crest_id,applied FROM crests WHERE character_id=$c ORDER BY crest_id";
            get.Parameters.AddWithValue("$c", characterId);
            using var rows = get.ExecuteReader();
            while (rows.Read())
            {
                int id = rows.GetInt32(0);
                if (id <= 0) continue;
                if (!slots.TryGetValue(id, out int at))
                {
                    at = -1;
                    for (int i = 0; i < CrestBlobSlots; i++)
                    {
                        int candidate = CrestBlobOffset + i * CrestBlobStride;
                        if (BitConverter.ToInt32(blob, candidate) == 0) { at = candidate; break; }
                    }
                    if (at < 0) continue;
                    BitConverter.GetBytes(id).CopyTo(blob, at);
                    blob[at + 4] = 0;
                    slots[id] = at;
                }
                if (!rows.IsDBNull(1)) blob[at + 4] = (byte)(rows.GetInt32(1) != 0 ? 1 : 0);
            }
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

    /// <summary>SA_CREST_POINT's base / extra points. Native Arb031:1186–1213 replaces base
    /// but only increases extra. World sends their sum and the applied crests' USED cost.</summary>
    public bool SetCrestPoints(int characterId, int point, int exPoint)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET crest_point=$p, crest_ex_point=MAX(crest_ex_point,$e), crest_points_known=1 WHERE id=$id";
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

    /// <summary>Null means no authoritative point write was observed; preserve the legacy snapshot.</summary>
    public (int Point, int ExPoint)? GetKnownCrestPoints(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT crest_point,crest_ex_point FROM characters WHERE id=$id AND crest_points_known<>0";
            cmd.Parameters.AddWithValue("$id", characterId);
            using var row = cmd.ExecuteReader();
            return row.Read() ? (row.GetInt32(0), row.GetInt32(1)) : null;
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
        // T85: guild_perks.guild_id REFERENCES guilds(guild_id), so a row for a guild we do not
        // have throws FOREIGN KEY constraint failed out of the store and takes the link with it.
        // The same shape as NoSuchOwner, which the character-keyed writes have had since T30.
        if (GetGuild(guildId) is null)
        {
            _log.LogWarning("UpsertGuildPerk: no guild row for id {Id} - write dropped", guildId);
            return;
        }
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

    // ------------------------- T95: the wanted board -------------------------

    /// <summary>
    /// A character may re-post once a day. cap_social3_client2 frame 2140 answers the list
    /// request right after the post with RemainTime 86400 - one day to the second - and
    /// CanBeWriting 0, so this is the window the real Arbiter enforces.
    /// </summary>
    public const long GuildWantedCooldownSeconds = 86400;

    /// <summary>
    /// C_REQUEST_SET_GUILD_WANTED_WRITING. One row per character: re-posting replaces the ad and
    /// restarts the cooldown, which is what the capture's single row does. Returns false when
    /// the character has no row of its own, so an ad can never outlive its poster.
    /// </summary>
    public bool SetGuildWanted(int userDbId, int guildSize, int guildPreference,
                               string promotionStr, long whenUnix)
    {
        if (GetCharacter(userDbId) is null) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO guild_wanted(user_db_id, guild_size, guild_preference, promotion_str, written_at) " +
                "VALUES($u, $s, $p, $m, $w) " +
                "ON CONFLICT(user_db_id) DO UPDATE SET guild_size = $s, guild_preference = $p, " +
                "promotion_str = $m, written_at = $w";
            cmd.Parameters.AddWithValue("$u", userDbId);
            cmd.Parameters.AddWithValue("$s", guildSize);
            cmd.Parameters.AddWithValue("$p", guildPreference);
            cmd.Parameters.AddWithValue("$m", promotionStr ?? "");
            cmd.Parameters.AddWithValue("$w", whenUnix);
            _qaWantedCooldownCleared.Remove(userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>When this character last posted, or 0. The cooldown reads it.</summary>
    public long GetGuildWantedTime(int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            if (_qaWantedCooldownCleared.Contains(userDbId)) return 0;
            cmd.CommandText = "SELECT written_at FROM guild_wanted WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$u", userDbId);
            var v = cmd.ExecuteScalar();
            return v is null || v is DBNull ? 0 : Convert.ToInt64(v);
        }
    }

    /// <summary>
    /// Every ad, newest first. No capture holds more than one row, so the order is ours; newest
    /// first is the one a board is read in. Characters already in a guild are excluded - the ad
    /// is a request to be invited, and the wanted board is where C_INVITE_USER_TO_GUILD's
    /// <c>fromWantedList</c> looks (GuildHandlers.MsgNotOnWantedList).
    /// </summary>
    public List<GuildWantedRow> GetGuildWanted()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT w.user_db_id, c.name, c.level, c.class, w.guild_preference, w.guild_size, " +
                "       w.written_at, w.promotion_str " +
                "FROM guild_wanted w JOIN characters c ON c.id = w.user_db_id " +
                "WHERE NOT EXISTS (SELECT 1 FROM guild_members m WHERE m.user_db_id = w.user_db_id) " +
                "ORDER BY w.written_at DESC, w.user_db_id";
            var rows = new List<GuildWantedRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new GuildWantedRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2),
                                            r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                                            r.GetInt64(6), r.GetString(7)));
            return rows;
        }
    }

    /// <summary>Drop this character's ad - joining a guild takes it off the board.</summary>
    public bool ClearGuildWanted(int userDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM guild_wanted WHERE user_db_id = $u";
            cmd.Parameters.AddWithValue("$u", userDbId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>T95. How many characters are in the guild - the MemberCount every guild-list
    /// element carries.</summary>
    public int CountGuildMembers(int guildId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM guild_members WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$g", guildId);
            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }
    }

    /// <summary>T95. C_RECOMMEND_GUILD / C_RECOMMEND_USER_GUILD both land here: the
    /// <c>recommendation_point</c> column of the guilds row, which AS_SET_GUILD_RECOMMENDATION_
    /// POINT (0x1411) is the inter-server form of.</summary>
    public bool AddGuildRecommendation(int guildId, int delta)
    {
        if (GetGuild(guildId) is null) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE guilds SET recommendation_point = MAX(0, recommendation_point + $d) " +
                "WHERE guild_id = $g";
            cmd.Parameters.AddWithValue("$g", guildId);
            cmd.Parameters.AddWithValue("$d", delta);
            return cmd.ExecuteNonQuery() > 0;
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

    /// <summary>
    /// T99. One item by its db id, whoever owns it - C_PREVIEW_ITEM names items by id alone and
    /// nothing else in the packet says whose they are.
    /// </summary>
    public ItemRow? GetItem(int itemDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT item_db_id, owner_db_id, inven_type, slot, template_id, amount, record " +
                "FROM items WHERE item_db_id = $id";
            cmd.Parameters.AddWithValue("$id", itemDbId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            byte[]? rec = r.IsDBNull(6) ? null : (byte[])r["record"];
            return new ItemRow(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3),
                               r.GetInt32(4), r.GetInt64(5), rec);
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

    /// <summary>User::ClearInven(false), Arb028:9086-9200. Warehouse and character money survive.</summary>
    public void ClearInventory(long characterId)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM items WHERE owner_db_id=$id AND inven_type NOT IN " + WarehouseInvenTypes
                + "; UPDATE characters SET inventory_cleared=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId); cmd.ExecuteNonQuery(); tx.Commit();
        }
    }

    public bool InventoryWasCleared(long characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT inventory_cleared FROM characters WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", characterId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) != 0;
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
    /// <summary>
    /// T142b. <paramref name="allowEmpty"/> is the ONLY way to clear a bag through this method.
    /// Without it an empty <paramref name="rows"/> over a character who owns something is
    /// refused and logged, because every way this has gone wrong has looked the same: a caller
    /// that could not resolve the owner, or could not parse a kit, hands us nothing and the
    /// character's bag is gone. Deleting a bag is <see cref="DeleteAllItems"/>'s job, and it is
    /// called from exactly one place (DeleteCharacter). An empty replace on an already-empty
    /// bag is a no-op either way, so the guard never blocks a legitimate caller.
    /// </summary>
    public void ReplaceInventory(long ownerDbId, IReadOnlyList<ItemRow> rows, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_lock)
        {
            if (rows.Count == 0 && !allowEmpty)
            {
                int had = CountInventoryItems(ownerDbId);
                if (had > 0)
                {
                    _log.LogWarning(
                        "ReplaceInventory: REFUSED an empty bag over {N} item row(s) for owner {Owner}. "
                        + "Pass allowEmpty to clear a bag on purpose; DeleteAllItems is the delete path.",
                        had, ownerDbId);
                    return;
                }
            }
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

    /// <summary>T181. Seed the three captured packages only for an explicitly allow-listed
    /// account. Revocation removes only rows this experiment inserted. Existing grants survive.</summary>
    public void SyncTeleportExperiment(long accountId, bool isOperator)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$a", accountId);
            if (!isOperator)
            {
                cmd.CommandText = "DELETE FROM account_benefits WHERE account_id=$a AND teleport_experiment=1";
                cmd.ExecuteNonQuery();
            }
            else
            {
                cmd.CommandText = "INSERT OR IGNORE INTO account_benefits " +
                    "(account_id,package_id,expires_at,value,teleport_experiment) VALUES($a,$p,$e,0,1)";
                var package = cmd.Parameters.Add("$p", SqliteType.Integer);
                var expires = cmd.Parameters.Add("$e", SqliteType.Integer);
                foreach (var seed in World.AccountBenefitExperiment.Seeds)
                {
                    package.Value = seed.PackageId;
                    expires.Value = seed.ExpiresAt;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Every benefit on this account, in package order.</summary>
    public List<AccountBenefitRow> GetAccountBenefits(long accountId)
    {
        lock (_lock)
        {
            if (_qaPackages.TryGetValue(accountId, out var runtime)) return runtime.Values.OrderBy(x => x.PackageId).ToList();
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
                "expires_at=excluded.expires_at, value=excluded.value, teleport_experiment=0";
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
                "parcel_type, status, is_read, is_recved FROM parcels WHERE parcel_id=$id AND status<3";
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
                "FROM parcels WHERE receiver_db_id=$r AND status<3";
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
                "parcel_type, status, is_read, is_recved FROM parcels WHERE receiver_db_id=$r AND status<3 ORDER BY parcel_id";
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

    /// <summary>
    /// T202. The SENT box: the parcels this character sent, for <c>SDB_LIST_PARCEL</c> with a
    /// non-zero ViewType. System mail has <c>sender_db_id = 0</c>, so it never appears here -
    /// which is exactly what the real Arbiter does (cap_final2b 52834: three system mails in the
    /// inbox, an empty Sent box).
    /// </summary>
    public IReadOnlyList<ParcelRow> GetParcelsSentBy(int senderDbId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT parcel_id, sender_db_id, sender_name, receiver_db_id, title, message, money, " +
                "parcel_type, status, is_read, is_recved FROM parcels WHERE sender_db_id=$s AND sender_db_id<>0 AND status<3 ORDER BY parcel_id";
            cmd.Parameters.AddWithValue("$s", senderDbId);
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

    /// <summary>T206: one attachment row, as the admin Mail screen lists them.</summary>
    public sealed record ParcelItemRow(int ParcelId, int Slot, int ItemDbId, int TemplateId, long Amount);

    /// <summary>
    /// T206: every attachment on one parcel, slot order. <see cref="CountParcelItems"/> could
    /// only say how many there were, so the admin tool had no way to show WHAT was attached.
    /// </summary>
    public IReadOnlyList<ParcelItemRow> GetParcelItems(int parcelId)
    {
        var rows = new List<ParcelItemRow>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT slot, item_db_id, template_id, amount FROM parcel_items " +
                "WHERE parcel_id=$p ORDER BY slot";
            cmd.Parameters.AddWithValue("$p", parcelId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new ParcelItemRow(parcelId, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3)));
        }
        return rows;
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
    /// <summary>
    /// T172. The per-character tables with no foreign key to characters(id): nothing stops their
    /// rows outliving the character, and the next character gets the same id. Cleared on delete
    /// and again on create (for rows a delete before T172 left behind). game_log is kept.
    /// </summary>
    public static readonly string[] CharacterStateTables =
    {
        "visited_sections", "visited_camps", "watched_movies", "crests", "card_mounts", "daily_event", "event_matching_reward",
        "ep_perks", "skill_polishing", "skill_polishing_options", "skill_polishing_levels",
        "dungeon_rank_records", "hidden_passives", "servants", "deleted_items",
    };

    private void PurgeCharacterStateLocked(int characterId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = string.Concat(CharacterStateTables.Select(t => $"DELETE FROM {t} WHERE character_id = $id;"));
        cmd.Parameters.AddWithValue("$id", characterId);
        int n = cmd.ExecuteNonQuery();
        if (n > 0) _log.LogInformation("Character {Id}: {N} stale row(s) of an earlier character with this id removed", characterId, n);
    }

    /// <summary>T183: the real Arbiter inserts once into its visited-camp set (Arb_part_027.c:16709-16750).</summary>
    public bool AddVisitedCamp(int characterId, int campId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO visited_camps(character_id, camp_id) VALUES($c,$id)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$id", campId);
            return cmd.ExecuteNonQuery() != 0;
        }
    }

    /// <summary>T183: std::set traversal order, including after reconnect (Arb_part_028.c:19541-19592).</summary>
    public IReadOnlyList<int> GetVisitedCamps(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT camp_id FROM visited_camps WHERE character_id=$c ORDER BY camp_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            var camps = new List<int>();
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) camps.Add(rows.GetInt32(0));
            return camps;
        }
    }

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

    // ---- T104: the same pair keyed the way the real Arbiter keys it, on the ACCOUNT ----

    /// <summary>
    /// Mark a cinematic as seen for a whole account - <c>Account::InsertWatchedMovieWithNoLock</c>
    /// plus <c>dbo.spInsertUserWatchedMovie</c>. Returns true the first time, false for a repeat,
    /// which is the same answer the real one's set-insert gives before it decides to run the
    /// stored procedure at all.
    /// </summary>
    public bool AddWatchedMovieForAccount(long accountId, int movieId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO watched_movies_account(account_id, movie_id) VALUES($a,$m) " +
                "ON CONFLICT(account_id, movie_id) DO NOTHING";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$m", movieId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Every cinematic this ACCOUNT has seen, oldest first - what
    /// <c>Account::CachedWatchedMoviesWithLock</c> hands
    /// <c>Account::SendWatchedMoviesToClient</c>. The per-character rows T62 wrote are folded in
    /// so a database that predates T104 does not replay the intro once more on the way past.
    /// </summary>
    public IReadOnlyList<int> GetWatchedMoviesForAccount(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT movie_id FROM watched_movies_account WHERE account_id=$a " +
                "UNION SELECT movie_id FROM watched_movies WHERE character_id IN " +
                "(SELECT id FROM characters WHERE account_id=$a) ORDER BY movie_id";
            cmd.Parameters.AddWithValue("$a", accountId);
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

    // =====================================================================
    // T147 - crafting: learned recipes and skill proficiencies. status/CRAFTING.md.
    // =====================================================================

    /// <summary>A character's learned recipes, in the order they were learned - the order the
    /// real Arbiter's in-memory vector keeps, since LearnItemRecipeNoLock appends.</summary>
    public List<TeraSharp.Arbiter.World.ArtisanDb.Recipe> GetItemRecipes(int characterId)
    {
        var list = new List<TeraSharp.Arbiter.World.ArtisanDb.Recipe>();
        if (characterId <= 0) return list;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT recipe_id, extract, learned_at, bookmark FROM item_recipes "
                            + "WHERE character_id = $c ORDER BY rowid";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TeraSharp.Arbiter.World.ArtisanDb.Recipe(r.GetInt32(0), r.GetInt64(1) != 0, r.GetInt64(2), r.GetInt64(3) != 0));
        }
        return list;
    }

    /// <summary>Learn a recipe. False when the character already knew it - the row is left as
    /// it was, learn time and bookmark included.</summary>
    public bool LearnItemRecipe(int characterId, int recipeId, bool extract, long learnedAtUnix)
    {
        if (characterId <= 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO item_recipes (character_id, recipe_id, extract, bookmark, learned_at) "
                            + "VALUES ($c, $r, $e, 0, $t)";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$r", recipeId);
            cmd.Parameters.AddWithValue("$e", extract ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", learnedAtUnix);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Forget one recipe. False when the character did not know it.</summary>
    public bool DeleteItemRecipe(int characterId, int recipeId)
    {
        if (characterId <= 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM item_recipes WHERE character_id = $c AND recipe_id = $r";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$r", recipeId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Set or clear a recipe's bookmark. False when the character does not know it.</summary>
    public bool SetItemRecipeBookmark(int characterId, int recipeId, bool bookmark)
    {
        if (characterId <= 0) return false;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE item_recipes SET bookmark = $b WHERE character_id = $c AND recipe_id = $r";
            cmd.Parameters.AddWithValue("$b", bookmark ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$r", recipeId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>A character's skill proficiencies, by id.</summary>
    public List<TeraSharp.Arbiter.World.ArtisanDb.SkillProf> GetSkillProfs(int characterId)
    {
        var list = new List<TeraSharp.Arbiter.World.ArtisanDb.SkillProf>();
        if (characterId <= 0) return list;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT skill_prof_id, value FROM skill_profs WHERE character_id = $c ORDER BY skill_prof_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new TeraSharp.Arbiter.World.ArtisanDb.SkillProf(r.GetInt32(0), r.GetInt32(1)));
        }
        return list;
    }

    /// <summary>Set one proficiency outright - World sends the new value, not a delta.</summary>
    public void SetSkillProf(int characterId, int skillProfId, int value)
    {
        if (characterId <= 0) return;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO skill_profs (character_id, skill_prof_id, value) VALUES ($c, $p, $v) "
                            + "ON CONFLICT(character_id, skill_prof_id) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$p", skillProfId);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>T147. The gathering proficiencies World has written, kind (0 mineral, 1 bug,
    /// 2 energy, 3 herb) -&gt; value. Kinds never written are absent, not 0.</summary>
    public Dictionary<int, int> GetGatheringProfs(int characterId)
    {
        var map = new Dictionary<int, int>();
        if (characterId <= 0) return map;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT kind, value FROM gathering_profs WHERE character_id = $c";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt32(0)] = r.GetInt32(1);
        }
        return map;
    }

    /// <summary>T154. One city-owning guild, as DBS_LOAD_CITY_GUILD_INFO carries it.</summary>
    public sealed record CityGuildRow(int LeagueId, int SeasonId, int GuildDbId, long TowerBuildTime,
                                      long TowerDestroyTime, int TotalKill, int TotalDeath,
                                      int TotalDestroy, int MaintainBonus);

    /// <summary>T154. The guilds holding a city in this league and season, by guild id. Empty is
    /// "no owning guild".</summary>
    public IReadOnlyList<CityGuildRow> GetCityGuilds(int leagueId, int seasonId)
    {
        var list = new List<CityGuildRow>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT guild_db_id, tower_build_time, tower_destroy_time, total_kill, total_death, "
                + "total_destroy, maintain_bonus FROM city_guild WHERE league_id = $l AND season_id = $s ORDER BY guild_db_id";
            cmd.Parameters.AddWithValue("$l", leagueId);
            cmd.Parameters.AddWithValue("$s", seasonId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new CityGuildRow(leagueId, seasonId, r.GetInt32(0), r.GetInt64(1), r.GetInt64(2),
                                          r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)));
        }
        return list;
    }

    /// <summary>T154. Store (or replace) one city-owning guild - for a Civil Unrest result.</summary>
    public void SetCityGuild(CityGuildRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO city_guild (league_id, season_id, guild_db_id, tower_build_time, "
                + "tower_destroy_time, total_kill, total_death, total_destroy, maintain_bonus) "
                + "VALUES ($l, $s, $g, $b, $d, $k, $x, $y, $m)";
            cmd.Parameters.AddWithValue("$l", row.LeagueId);
            cmd.Parameters.AddWithValue("$s", row.SeasonId);
            cmd.Parameters.AddWithValue("$g", row.GuildDbId);
            cmd.Parameters.AddWithValue("$b", row.TowerBuildTime);
            cmd.Parameters.AddWithValue("$d", row.TowerDestroyTime);
            cmd.Parameters.AddWithValue("$k", row.TotalKill);
            cmd.Parameters.AddWithValue("$x", row.TotalDeath);
            cmd.Parameters.AddWithValue("$y", row.TotalDestroy);
            cmd.Parameters.AddWithValue("$m", row.MaintainBonus);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------ T156: the Vanguard Initiative

    /// <summary>T156. One character's daily_event row - what DBS_LOAD_USER_DAILY_EVENT carries.
    /// <paramref name="Counts"/> is always five long.</summary>
    public sealed record DailyEventRow(int CharacterId, int[] Counts, long ExtraRewardReset,
                                       bool GotExtraReward, int ExtraRewardValue);

    /// <summary>T156. The character's daily_event row, or null when it has never had one.</summary>
    public DailyEventRow? GetDailyEvent(int characterId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT count0, count1, count2, count3, count4, extra_reward_reset, got_extra_reward, "
                + "extra_reward_value FROM daily_event WHERE character_id = $c";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var counts = new int[5];
            for (int i = 0; i < 5; i++) counts[i] = r.GetInt32(i);
            return new DailyEventRow(characterId, counts, r.GetInt64(5), r.GetInt32(6) != 0, r.GetInt32(7));
        }
    }

    /// <summary>
    /// T156. SDB_UPDATE_USER_DAILY_EVENT_COUNT: replace the five completion counts and, when
    /// <paramref name="extraRewardReset"/> is positive, the extra-reward stamp too (the real
    /// Arbiter only calls spSetUserDailyEventExtraRewardResetTime for a positive time). A row
    /// created here starts stamped <paramref name="nowUnix"/>: a brand-new character's first
    /// write carries time 0 and its next load reads back the time of that write.
    /// </summary>
    public void SetDailyEventCounts(int characterId, int[] counts, long extraRewardReset, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (characterId <= 0) return;
        lock (_lock)
        {
            EnsureDailyEventRow(characterId, nowUnix);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE daily_event SET count0 = $a, count1 = $b, count2 = $d, count3 = $e, count4 = $f"
                + (extraRewardReset > 0 ? ", extra_reward_reset = $t" : "") + " WHERE character_id = $c";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$a", counts.Length > 0 ? counts[0] : 0);
            cmd.Parameters.AddWithValue("$b", counts.Length > 1 ? counts[1] : 0);
            cmd.Parameters.AddWithValue("$d", counts.Length > 2 ? counts[2] : 0);
            cmd.Parameters.AddWithValue("$e", counts.Length > 3 ? counts[3] : 0);
            cmd.Parameters.AddWithValue("$f", counts.Length > 4 ? counts[4] : 0);
            if (extraRewardReset > 0) cmd.Parameters.AddWithValue("$t", extraRewardReset);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>T156. SDB_UPDATE_GET_EXTRA_REWARD: the bool and int the daily_event load returns.</summary>
    public void SetDailyEventExtraReward(int characterId, bool got, int value, long nowUnix)
    {
        if (characterId <= 0) return;
        lock (_lock)
        {
            EnsureDailyEventRow(characterId, nowUnix);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE daily_event SET got_extra_reward = $g, extra_reward_value = $v WHERE character_id = $c";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$g", got ? 1 : 0);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private void EnsureDailyEventRow(int characterId, long nowUnix)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO daily_event (character_id, extra_reward_reset) VALUES ($c, $t)";
        cmd.Parameters.AddWithValue("$c", characterId);
        cmd.Parameters.AddWithValue("$t", nowUnix);
        cmd.ExecuteNonQuery();
    }

    /// <summary>T156. The character's add-reward receive counts, by event id.</summary>
    public IReadOnlyList<(int EventId, int AcquireNum)> GetEventMatchingRewards(int characterId)
    {
        var list = new List<(int, int)>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT event_id, acquire_num FROM event_matching_reward WHERE character_id = $c ORDER BY event_id";
            cmd.Parameters.AddWithValue("$c", characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetInt32(1)));
        }
        return list;
    }

    /// <summary>T156. Set (or overwrite) one add-reward receive count.</summary>
    public void SetEventMatchingReward(int characterId, int eventId, int acquireNum)
    {
        if (characterId <= 0) return;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO event_matching_reward (character_id, event_id, acquire_num) VALUES ($c, $e, $n) "
                + "ON CONFLICT(character_id, event_id) DO UPDATE SET acquire_num = $n";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$e", eventId);
            cmd.Parameters.AddWithValue("$n", acquireNum);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>T156. The daily add-reward reset: every character's counts go. Returns the rows removed.</summary>
    public int ClearEventMatchingRewards()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM event_matching_reward";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>T156. A named value in <c>counters</c>, or <paramref name="fallback"/> when it was never set.</summary>
    public long GetCounterValue(string name, long fallback)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM counters WHERE name = $k";
            cmd.Parameters.AddWithValue("$k", name);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? fallback : Convert.ToInt64(v);
        }
    }

    /// <summary>T156. Set a named value in <c>counters</c>.</summary>
    public void SetCounterValue(string name, long value)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO counters(name, value) VALUES($k, $v) ON CONFLICT(name) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$k", name);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public const string BattlefieldUniqueCounter = "battlefield_unique_id";

    /// <summary>GameDatabaseDefinition.xml:6991: first issue inserts0, subsequent issues increment.</summary>
    public int IssueBattlefieldUniqueId()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO counters(name,value) VALUES($k,0) ON CONFLICT(name) DO UPDATE SET value=value+1 RETURNING value";
            cmd.Parameters.AddWithValue("$k", BattlefieldUniqueCounter);
            return unchecked((int)Convert.ToInt64(cmd.ExecuteScalar()!));
        }
    }

    public sealed record BattlefieldLogRow(int LogId, long BattlefieldId, int TemplateId, long BlueParty, long RedParty);

    /// <summary>Native spCreateBattleFieldLog (GameDatabaseDefinition.xml:15844); each request inserts a new identity.</summary>
    public int CreateBattlefieldLog(long battlefieldId, int templateId, long blueParty, long redParty)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO battlefield_logs(battlefield_id,template_id,blue_party,red_party) "
                + "VALUES($b,$t,$blue,$red); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$b", battlefieldId); cmd.Parameters.AddWithValue("$t", templateId);
            cmd.Parameters.AddWithValue("$blue", blueParty); cmd.Parameters.AddWithValue("$red", redParty);
            return checked((int)(long)cmd.ExecuteScalar()!);
        }
    }

    public BattlefieldLogRow? GetBattlefieldLog(int logId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT log_id,battlefield_id,template_id,blue_party,red_party FROM battlefield_logs WHERE log_id=$id";
            cmd.Parameters.AddWithValue("$id", logId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new(r.GetInt32(0),r.GetInt64(1),r.GetInt32(2),r.GetInt64(3),r.GetInt64(4)) : null;
        }
    }

    /// <summary>Set one gathering proficiency outright - User::UpdateUserProf* assigns it.</summary>
    public void SetGatheringProf(int characterId, int kind, int value)
    {
        if (characterId <= 0) return;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO gathering_profs (character_id, kind, value) VALUES ($c, $k, $v) "
                            + "ON CONFLICT(character_id, kind) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$c", characterId);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() { DisposeQaCommerce(); lock (_lock) _db.Dispose(); }
}
