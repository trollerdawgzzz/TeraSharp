// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// The 63 one-way A-&gt;W configuration pushes the Arbiter sends straight after the handshake,
/// BUILT from <see cref="InterServerDefinitions"/> rather than replayed from a capture (T209b).
///
/// <para>Every field the captures show as zero, false or an empty list is simply left out here:
/// the writer emits a zeroed fixed field for an absent value, <c>count=0, offset=0</c> for an
/// absent array and <c>offset=&lt;end of the fixed part&gt;, length=0</c> for an absent bytes
/// field - which is exactly what retail puts on the wire. So the constants below are the whole
/// non-zero content of the burst, and Tests/T209b.cs proves the 63 frames come out byte for byte
/// as captured.</para>
///
/// <para>Two values move with the clock (see <see cref="DbProxyHandlers.AS_SYNC_DATE_TIME"/> and
/// <see cref="DbProxyHandlers.AS_SET_DARK_RIFT_DAILY_COMPLETED"/>); everything else is identical
/// across all four captures, a day apart and on both sides of a daily reset.</para>
/// </summary>
public static class HandshakeBurst
{
    /// <summary>Frames in the burst - unchanged from the capture.</summary>
    public const int FrameCount = 63;

    /// <summary>
    /// 0x15DE <c>AS_DUNGEON_PHASE_LAST_RESET_TIME.LastResetTime</c> = 2026-09-12T03:57:29Z. NOT a
    /// clock: all four captures carry this same instant, a day apart, so the real Arbiter is
    /// echoing a value it keeps in SQL. Ours is a constant until a capture shows it moving.
    /// </summary>
    public const long DungeonPhaseLastResetTime = 1789185449;

    /// <summary>0x28F8 <c>AS_EVENT_HUNTINGBONUS.ArbiterServerPlanetId</c> in every capture.</summary>
    public const int ArbiterServerPlanetId = 2800;

    /// <summary>0x160F <c>AS_GMEVENT_CHANGE_MAX_JOIN_COUNT.MaxJoinCount</c>.</summary>
    public const int GmEventMaxJoinCount = 1;

    /// <summary>0x1510 <c>AS_CHANGE_ACHIEVEMENT_SEASON.CurrentSeasonId</c>: no season running.</summary>
    public const int NoAchievementSeason = -1;

    /// <summary>The five festival ids 0x149D/0x149E/0x149F are sent for, in capture order.</summary>
    public static readonly int[] FestivalIds = { 1, 2, 3, 4, 5 };

    /// <summary>0x157F <c>AS_CONTENTS_ON_OFF_LIST.ContentsOnOffList</c>: three int32, 1 0 11.</summary>
    public static byte[] ContentsOnOff() => Int32Blob(1, 0, 11);

    /// <summary>0x150A <c>AS_SET_EP_SYSTEM_EVENT.ValueTable</c>: fifteen floats, all 1.0 (no event).</summary>
    public static byte[] EpSystemEventValues() => FloatBlob(15, 1.0f);

    /// <summary>0x29E2 <c>DBS_TBA_UPDATE_ROTATION.RotationHeroList</c> - the twelve hero template ids.</summary>
    public static readonly int[] TbaRotationHeroes =
    {
        20300, 21000, 21500, 22300, 22400, 22500,
        20100, 20400, 20600, 20700, 20900, 21100,
    };

    /// <summary>
    /// The whole burst in captured order, stamped for <paramref name="now"/>. Pass
    /// <paramref name="darkRiftReset"/> only to pin 0x14D1 (tests); by default it is
    /// <see cref="DbProxyHandlers.DailyResetUnixSeconds"/> for <paramref name="now"/>.
    /// </summary>
    public static List<(ushort op, byte[] payload)> Build(DateTimeOffset now, ulong? darkRiftReset = null)
    {
        ulong reset = darkRiftReset ?? DbProxyHandlers.DailyResetUnixSeconds(now);
        var frames = new List<(ushort, byte[])>(FrameCount);

        void Add(string name, params (string Field, object Value)[] fields)
        {
            var data = new Dictionary<string, object>(fields.Length, StringComparer.Ordinal);
            foreach (var (f, v) in fields) data[f] = v;
            var writer = new DefinitionWriter(PacketFraming.InterServer);
            frames.Add((InterServerDefinitions.Opcode(name),
                writer.Write(InterServerDefinitions.Def(name), data)));
        }

        Add("AS_SYNC_DATE_TIME", ("ArbiterTime", now.ToUnixTimeSeconds()));
        Add("AS_UPDATE_DAILY_EVENT_DAY");
        Add("AS_INITIALIZE_DUNGEON_ID");
        Add("AS_DUNGEON_DISABLED_LIST");
        Add("AS_DUNGEON_PHASE_LAST_RESET_TIME", ("LastResetTime", DungeonPhaseLastResetTime));
        Add("AS_INIT_TIMELINE_CHANGES");
        Add("AS_CONTENTS_ON_OFF_LIST", ("ContentsOnOffList", ContentsOnOff()));
        Add("AS_GUILD_WAR_ADMIN_MAINTAINCOST");
        Add("AS_VIP_STORE_SLOT_ON_OFF");
        Add("AS_VIP_STORE_SLOT_SALE");
        Add("AS_CHANGE_ACHIEVEMENT_SEASON", ("CurrentSeasonId", NoAchievementSeason));
        Add("AS_ACHIEVEMENT_SEASON_LIST");
        Add("DISABLE_DARK_RIFT_HUNTING_ZONE");
        Add("AS_SET_DARK_RIFT_DAILY_COMPLETED", ("LastResetDateTime", reset));
        Add("AS_RESET_ADMIN_NON_PK_SECTION");
        Add("AS_SET_HUNTING_EVENT_VALUE");
        Add("AS_BOT_CONFIGURATION");
        // Two of these: the begin half then the end half, each with its own flag set.
        Add("AS_EVENT_HUNTINGBONUS", ("AddBegin", true), ("ArbiterServerPlanetId", ArbiterServerPlanetId));
        Add("AS_EVENT_HUNTINGBONUS", ("AddEnd", true), ("ArbiterServerPlanetId", ArbiterServerPlanetId));
        Add("AS_SET_EP_SYSTEM_CONTENTS_ON_OFF_TABLE");
        Add("AS_SET_EP_SYSTEM_EVENT", ("ValueTable", EpSystemEventValues()));
        Add("AS_SET_JACKPOT_EVENT_VALUE_LIST");
        Add("AS_SEND_RESTRICTION_ITEM_LIST");
        foreach (int id in FestivalIds)
        {
            Add("AS_STOP_FESTIVAL", ("FestivalId", id));
            Add("AS_FESTIVAL_NPC_SPAWN", ("FestivalId", id));
            Add("AS_FESTIVAL_OBJECT_SPAWN", ("FestivalId", id));
        }
        Add("AS_LOAD_PRODUCT_SALE_INFO");
        Add("AS_BATTLE_FIELD_DISABLED_LIST");
        Add("AS_LOAD_ENCHANT_PROB_EVENT_INFO");
        Add("AS_ADD_AWAKEN_ENCHANT_DATA");
        Add("AS_ADD_AWAKEN_CHANGE_DATA");
        Add("AS_LOAD_GMEVENT_DATA");
        Add("AS_GMEVENT_LOAD_JOIN_COUNT");
        Add("AS_GMEVENT_CHANGE_MAX_JOIN_COUNT", ("MaxJoinCount", GmEventMaxJoinCount));
        Add("AS_GMEVENT_ONOFF", ("IsOn", true));
        Add("AS_SET_DUNGEON_ROOKIE_EVENT_VALUE_LIST");
        Add("AS_SET_DUAL_OPTION_OPEN_MATERIAL_LIST");
        Add("AS_SET_PLAY_GUIDE_EVENT_LIST");
        Add("AS_SET_PLAY_GUIDE_WEB_ADMIN_SETTINGS", ("CanRecvDailyBonus", true), ("CanRecvWeeklyBonus", true));
        Add("AS_SET_PLAY_GUIDE_EXTRA_BASE_REWARD_EVENT");
        Add("AS_SET_PLAY_GUIDE_EXTRA_DAILY_REWARD_EVENT");
        Add("AS_LOAD_EVENTSYSTEM_INFO");
        Add("AS_SET_FIELD_POINT_ADMIN_REWARD");
        Add("AS_ACCESSORY_TRANSFORM_COST_INFO");
        Add("AS_WORLD_PURCHASE_LIMIT", ("Success", true));
        Add("AS_LOAD_CONTINENT_CHANNEL_COUNT");
        Add("DBS_INGAMESHOP_CATEGORY_BEGIN", ("Success", true));
        Add("DBS_INGAMESHOP_CATEGORY_END", ("Success", true));
        Add("DBS_INGAMESHOP_PRODUCT_BEGIN", ("Success", true));
        Add("DBS_INGAMESHOP_PRODUCT_END", ("Success", true));
        Add("DBS_TBA_UPDATE_ROTATION", ("RotationHeroList", TbaRotationHeroes));

        return frames;
    }

    private static byte[] Int32Blob(params int[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(b, i * 4);
        return b;
    }

    private static byte[] FloatBlob(int count, float value)
    {
        var b = new byte[count * 4];
        for (int i = 0; i < count; i++) BitConverter.GetBytes(value).CopyTo(b, i * 4);
        return b;
    }
}
