using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Game;

namespace TeraSharp.Arbiter.Handlers;

public static class InventoryHandlers
{
    private static Dictionary<string, object> MakeItem(int id, long dbid, int slot, int amount, float itemLevel)
    {
        var passivitySets = new List<object>
        {
            new Dictionary<string, object>
            {
                ["index"] = 0u, ["masterworkBonus"] = 0, ["itemLevel"] = itemLevel,
                ["minItemLevel"] = itemLevel, ["maxItemLevel"] = itemLevel,
                ["passivities"] = new List<object>(),
            },
            new Dictionary<string, object>
            {
                ["index"] = 1u, ["masterworkBonus"] = 0, ["itemLevel"] = itemLevel,
                ["minItemLevel"] = itemLevel, ["maxItemLevel"] = itemLevel,
                ["passivities"] = new List<object>(),
            },
        };

        return new Dictionary<string, object>
        {
            ["id"] = id, ["dbid"] = (ulong)dbid, ["ownerId"] = 1UL, ["slot"] = (uint)slot, ["amount"] = amount,
            ["enchant"] = 0, ["durability"] = 30, ["soulboundStatus"] = 0, ["passivitySet"] = 0,
            ["extraPassivitySets"] = 0, ["remodel"] = 0, ["dye"] = 0u, ["dyeSecRemaining"] = 0,
            ["dyeDate"] = 0L, ["dyeExpiryDate"] = 0L, ["masterwork"] = false, ["enigma"] = 0,
            ["enchantAdvantage"] = 0, ["enchantBonus"] = 0, ["enchantBonusMaxPlus"] = 0,
            ["boundToPlayer"] = 0UL, ["awakened"] = false, ["liberationStatus"] = 0, ["feedstock"] = -1,
            ["boundToItem"] = 0, ["hasEtching"] = false, ["pcbang"] = false, ["xp"] = 0L,
            ["damaged"] = false, ["availableUntil"] = 0L, ["customString"] = "",
            ["crystals"] = new List<object>(),
            ["passivitySets"] = passivitySets,
            ["mergedPassivities"] = new List<object>(),
        };
    }

    public static void SendInventory(GameSession s, FakeCharacter chr, bool requested = false)
    {
        s.SendByDef("S_ITEMLIST", new Dictionary<string, object>
        {
            ["gameId"] = s.GameId, ["container"] = 14, ["pocket"] = 0, ["numPockets"] = 1,
            ["size"] = 24, ["money"] = 0L, ["lootPriority"] = 1348234336,
            ["open"] = requested, ["requested"] = requested, ["first"] = true, ["more"] = false, ["lastInBatch"] = false,
            ["items"] = new List<object>
            {
                MakeItem(chr.Weapon, 1, 1, 1, 5f),
                MakeItem(chr.Body, 2, 3, 1, 1f),
                MakeItem(chr.Hand, 3, 4, 1, 1f),
                MakeItem(chr.Feet, 4, 5, 1, 1f),
            },
        });

        s.SendByDef("S_ITEMLIST", new Dictionary<string, object>
        {
            ["gameId"] = s.GameId, ["container"] = 0, ["pocket"] = 0, ["numPockets"] = 1,
            ["size"] = 40, ["money"] = 0L, ["lootPriority"] = 1356867040,
            ["open"] = requested, ["requested"] = requested, ["first"] = true, ["more"] = false, ["lastInBatch"] = true,
            ["items"] = new List<object>
            {
                MakeItem(6550, 5, 0, 20, 0f),
                MakeItem(6560, 6, 1, 20, 0f),
            },
        });
    }

    public static void SendSkills(GameSession s)
    {
        long[] activeIds = { 10199, 60199, 140199, 160199, 9020100, 9030100, 60401301 };
        long[] inactiveIds = { 10002, 19500, 19501, 94001, 94002, 94003, 94005, 94006, 94007,
                               94008, 94009, 94010, 94011, 94012, 94013, 94014, 94015 };

        var skills = new List<object>();
        foreach (var id in activeIds)
            skills.Add(new Dictionary<string, object> { ["id"] = id, ["active"] = true });
        foreach (var id in inactiveIds)
            skills.Add(new Dictionary<string, object> { ["id"] = id, ["active"] = false });

        s.SendByDef("S_SKILL_LIST", new Dictionary<string, object> { ["skills"] = skills });
    }

    /// <summary>S_PLAYER_STAT_UPDATE from capture [343] — types matched to the .def exactly.</summary>
    public static void SendStats(GameSession s)
    {
        s.SendByDef("S_PLAYER_STAT_UPDATE", new Dictionary<string, object>
        {
            ["hp"] = 1897L, ["mp"] = 1308, ["drain"] = 0L, ["maxHp"] = 1897L, ["maxMp"] = 1308,
            ["power"] = 52, ["endurance"] = 80,
            ["critRate"] = 58f, ["critResist"] = 65f,
            ["critPower"] = 2f, ["critPowerPhysical"] = 2f, ["critPowerMagical"] = 2f,
            ["attackSpeed"] = (short)100, ["runSpeed"] = (short)110, ["walkSpeed"] = (short)56,
            ["impactFactor"] = 75, ["balanceFactor"] = 35,
            ["attackMin"] = 237, ["attackMax"] = 237,
            ["attackPhysicalMin"] = 0, ["attackPhysicalMax"] = 0,
            ["attackMagicalMin"] = 0, ["attackMagicalMax"] = 0,
            ["defense"] = 67, ["defensePhysical"] = 0, ["defenseMagical"] = 0,
            ["defenseReductionPhysical"] = 0, ["defenseReductionMagical"] = 0,
            ["piercingPhysical"] = 0f, ["piercingMagical"] = 0f, ["healValue"] = 0f,
            ["resistWeakening"] = 47f, ["resistPeriodic"] = 47f, ["resistStun"] = 47f,
            ["powerBonus"] = 0, ["enduranceBonus"] = 0, ["impactFactorBonus"] = 0, ["balanceFactorBonus"] = 0,
            ["runSpeedBonus"] = (short)60, ["walkSpeedBonus"] = (short)0, ["attackSpeedBonus"] = (short)0,
            ["critRateBonus"] = 0f, ["critResistBonus"] = 0f,
            ["critPowerBonus"] = 0f, ["critPowerPhysicalBonus"] = 0f, ["critPowerMagicalBonus"] = 0f,
            ["attackMinBonus"] = 0, ["attackMaxBonus"] = 0, ["defenseBonus"] = 0,
            ["attackPhysicalMinBonus"] = 0, ["attackPhysicalMaxBonus"] = 0, ["defensePhysicalBonus"] = 0,
            ["attackMagicalMinBonus"] = 0, ["attackMagicalMaxBonus"] = 0, ["defenseMagicalBonus"] = 0,
            ["defenseReductionPhysicalBonus"] = 0, ["defenseReductionMagicalBonus"] = 0,
            ["piercingPhysicalBonus"] = 0.001f, ["piercingMagicalBonus"] = 0.001f,
            ["resistWeakeningBonus"] = 0f, ["resistPeriodicBonus"] = 0f, ["resistStunBonus"] = 0f,
            ["level"] = (ushort)1, ["status"] = (ushort)0, ["conditionLevel"] = (ushort)0, ["alive"] = true,
            ["hpBonus"] = 0, ["mpBonus"] = 0,
            ["condition"] = 120, ["contitionMax"] = 120,
            ["stamina"] = 0, ["staminaMax"] = 0, ["staminaBonus"] = 0,
            ["infamy"] = 0, ["itemLevelInventory"] = 1f, ["itemLevel"] = 1.948f,
            ["edge"] = 0, ["edgePercentage"] = 0f, ["edgeTimeRemaining"] = 0, ["edgeMin"] = 0,
            ["trueLevel"] = 1,
            ["flightEnergy"] = 1000f, ["flightId"] = 0u, ["flightSpeedMul"] = 1f,
            ["fireEdge"] = 0u, ["iceEdge"] = 0u, ["lightningEdge"] = 0u,
            ["adventureCoins"] = 1400u, ["adventureCoinsMax"] = 1400u,
        });
    }
}
