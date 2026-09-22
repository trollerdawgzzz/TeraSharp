// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;

namespace TeraSharp.Arbiter.Protocol;

/// <summary>
/// Packet layouts where the def in <c>tera_v100_MASTER_FINAL</c> is a LATER patch version than the
/// client we serve, so the file def would put an extra field on the wire.
///
/// <para>The def folder is community data covering several patches; a def whose header says
/// <c>majorPatchVersion &gt;= 101</c> describes 101+, not 100.02. For an EMPTY list that costs
/// nothing (only the count/offset header goes out), which is why this never showed up while the
/// friend list was always empty. The first real friend row would have shifted every field after
/// it and handed the client garbage.</para>
///
/// <para>Each override below is measured against the real Arbiter's own writer in the decompile
/// (element stride, field-by-field), so the size is proof, not a guess - see
/// status/FRIENDS.md section 2.</para>
///
/// <para>Registered at a version far above any file def so <see cref="DefinitionRegistry.Get(string)"/>
/// - which keeps the highest version - picks these.</para>
/// </summary>
public static class V100Definitions
{
    /// <summary>Higher than any version in the def folder (the highest there is 5).</summary>
    public const int Version = 100;

    private static readonly ConditionalWeakTable<DefinitionRegistry, object> Done = new();

    /// <summary>
    /// S_FRIEND_LIST for 100.02. The file def (S_FRIEND_LIST.2, marked
    /// <c>majorPatchVersion &gt;= 101</c>) also carries <c>dungeonGauntletDifficultyId</c> after
    /// sectionId; the 100.02 writer does not.
    /// <para>Proof: <c>User::SendFriendListNoLock</c> (Arb_part_030.c:8) advances the body by
    /// <c>0x3f</c> = 63 bytes per element - here/next 4 + three string offsets 6 + playerId 4 +
    /// group 4 + seven int32 28 + summonable 1 + lastOnline 8 + type 4 + bonds 4 = 63. With the
    /// extra int32 it would be 67.</para>
    /// </summary>
    public const string FriendList = @"
ref      friends
ref      personalNote
string   personalNote
array    friends
- ref    name
- ref    myNote
- ref    theirNote
- uint32 playerId
- int32  group
- int32  level
- int32  race
- int32  class
- int32  gender
- int32  worldId
- int32  guardId
- int32  sectionId
- bool   summonable
- int64  lastOnline
- uint32 type
- int32  bonds
- string name
- string myNote
- string theirNote
";

    /// <summary>
    /// S_UPDATE_FRIEND_INFO for 100.02 - same story, same missing field.
    /// <para>Proof: <c>User::SendUpdateFriendListInfo</c> (Arb_part_030.c:3300) writes 53-byte
    /// elements - here/next 4 + name offset 2 + playerId 4 + eight int32 32 + three bytes 3 +
    /// lastOnline 8 = 53. The file def (S_UPDATE_FRIEND_INFO.2) would be 57.</para>
    /// </summary>
    public const string UpdateFriendInfo = @"
array    friends
- ref    name
- uint32 playerId
- int32  level
- int32  race
- int32  class
- int32  gender
- int32  status
- int32  worldId
- int32  guardId
- int32  sectionId
- bool   updated
- bool   isWorldEventTarget
- bool   summonable
- int64  lastOnline
- string name
";

    /// <summary>
    /// S_OPEN_GUILD_WAR_WINDOW for 100.02 - T80. The shipped def
    /// (S_OPEN_GUILD_WAR_WINDOW.1) carries ONLY the two int32 counters and no war list at all,
    /// so it cannot describe a window with a war in it.
    /// <para>Proof: the writer at Arb_part_059.c:2659 advances the body by <c>0x48</c> = 72
    /// bytes per element and writes, in order, a longlong at +12, a byte at +20, a u32 at +21,
    /// a u64 at +25, a u32 at +33, a byte at +37, then the same six again from +38, then a u64
    /// date at +64 - here/next 4 + four string offsets 8 + two 26-byte guild blocks + 8 = 72.
    /// cap_social4_client frame 3389 is byte-for-byte that.</para>
    /// </summary>
    public const string OpenGuildWarWindow = @"
int32    thisGuildDeclareCount
int32    thisGuildDeclareLimit
array    wars
- int64  attackGuildId
- byte   attackFlag
- int32  attackUnk1
- int64  attackMoney
- int32  attackUnk2
- byte   attackUnk3
- int64  defendGuildId
- byte   defendFlag
- int32  defendUnk1
- int64  defendMoney
- int32  defendUnk2
- byte   defendUnk3
- int64  date
- string attackName
- string attackEmblem
- string defendName
- string defendEmblem
";

    /// <summary>
    /// S_CHECK_TO_DECLARE_GUILD_WAR for 100.02 - T80. The shipped def has ONE int32 after the
    /// three strings; the writer has THREE.
    /// <para>Proof: the send at Arb_part_058.c:8227 is
    /// <c>SendToSession&lt;PKT_S_CHECK_TO_DECLARE_GUILD_WAR_WRITE, bool, const wchar_t*,
    /// const wchar_t*, const wchar_t*, int&amp;, int&amp;, int&amp;&gt;</c> and it writes a bool
    /// then three u32 then the three strings - 19 fixed bytes, which is exactly where the
    /// @1788 string starts in cap_social4_client frame 3382. With one int32 it would be 11 and
    /// every string offset after it would be garbage.</para>
    /// <para>The last two are named from their values: 0 and 10 in that frame, the same pair
    /// S_OPEN_GUILD_WAR_WINDOW carries two frames later.</para>
    /// </summary>
    public const string CheckToDeclareGuildWar = @"
byte     result
int32    guildWarMoney
int32    thisGuildDeclareCount
int32    thisGuildDeclareLimit
string   reasonSysMsgFormatted
string   myGuildImageId
string   opponentGuildImageId
";

    /// <summary>
    /// Register every 100.02 override on this registry, once. Safe to call on every packet -
    /// the registry is a process singleton and the second call is a dictionary probe.
    /// </summary>
    public static void EnsureRegistered(DefinitionRegistry defs)
    {
        if (defs is null || Done.TryGetValue(defs, out _)) return;
        lock (Done)
        {
            if (Done.TryGetValue(defs, out _)) return;
            defs.Register(DefinitionParser.ParseText("S_FRIEND_LIST", FriendList, Version));
            defs.Register(DefinitionParser.ParseText("S_UPDATE_FRIEND_INFO", UpdateFriendInfo, Version));
            defs.Register(DefinitionParser.ParseText("S_OPEN_GUILD_WAR_WINDOW", OpenGuildWarWindow, Version));
            defs.Register(DefinitionParser.ParseText("S_CHECK_TO_DECLARE_GUILD_WAR", CheckToDeclareGuildWar, Version));
            Done.Add(defs, new object());
        }
    }
}
