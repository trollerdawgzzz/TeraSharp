// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201. Arb085:16427-16503 registration; auth remains in the central QA dispatcher.</summary>
public static class QaGuildCardCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "add_guild_point", "add_guild_money", "add_guild_exp", "guild_level",
        "guild_quest_usable", "reset_guild_quest", "set_card_preset_amount",
        "add_card_collection_point", "card_collection_level", "reset_card_collection",
    };

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || session.SelectedCharacter == null) return true;
        int characterId = (int)session.SelectedCharacter.Id;
        string name = line.Name.ToLowerInvariant();
        try
        {
            if (name.Contains("guild", StringComparison.Ordinal))
            {
                GuildActions actions;
                if (name == "guild_quest_usable")
                {
                    if (line.Arg(0) is not ("on" or "off")) return true;
                    actions = GuildWiring.Guilds.SetQaGuildQuestUsable(characterId, line.Arg(0) == "on");
                }
                else if (name == "reset_guild_quest")
                    actions = GuildWiring.Guilds.ResetQaGuildQuest(characterId);
                else
                    actions = ApplyGuildEconomy(store, characterId, line);
                GuildWiring.Dispatcher(session, log).Dispatch(actions, "T201 " + name);
                return true;
            }

            long accountId = store.AccountOf(characterId);
            if (accountId == 0) return true;
            if (name == "reset_card_collection")
            {
                store.ResetCardCollection(accountId);
                // Arb044:1030-1061: client reset first, then AS_UPDATE_CARD_COLLECTION.
                session.Send(new byte[] { 4, 0, 0xE3, 0xDA });
            }
            else
            {
                if (line.Args.Count == 0 || !int.TryParse(line.Arg(0), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int value)) return true;
                if (name == "set_card_preset_amount")
                {
                    if (value <= 0) return true; // Arb044:3253-3297.
                    store.UpdateCardInfo(accountId, x => x with { PresetAmount = value });
                }
                else
                {
                    var sheet = CardCollectionSheet.Entry.Value;
                    if (!sheet.Available)
                    {
                        log.LogWarning("QA {Command}: card collection sheets unavailable", name);
                        return true;
                    }
                    if (name == "card_collection_level")
                    {
                        var level = sheet.Levels.FirstOrDefault(x => x.Id == value);
                        if (level == null) return true;
                        store.UpdateCardInfo(accountId, x => x with { BookLevel = value, BookPoint = level.NeedPoints });
                    }
                    else // Arb040:3224-3275: signed point delta, floor at zero.
                        store.UpdateCardInfo(accountId, x =>
                        {
                            int points = Math.Max(0, checked(x.BookPoint + value));
                            return x with { BookLevel = sheet.LevelFor(points), BookPoint = points };
                        });
                }
            }
            ArbiterClientHandlers.SendToWorld(session, 0x2985, BitConverter.GetBytes(accountId));
        }
        catch (OverflowException)
        {
            log.LogWarning("QA {Command}: numeric result exceeds its native field width", name);
        }
        return true;
    }

    /// <summary>Native state changes and packet order: Arb040:3515/3921/3952; Arb044:3756;
    /// Arb046:14983-15064,15426-15493,15663-15743,15831-15917.</summary>
    public static GuildActions ApplyGuildEconomy(CharacterStore store, int characterId, GmCommandLine line)
    {
        var actions = new GuildActions();
        int guildId = store.GetGuildIdOf(characterId);
        if (guildId == 0 || line.Args.Count != 1 || !long.TryParse(line.Arg(0), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long value)) return actions;
        string name = line.Name.ToLowerInvariant();
        var curve = name == "add_guild_money" ? null : GuildLevelSheet.Entry.Value;
        if (curve != null && !curve.Available) return actions.Reject("GuildConfig GuildLevelTable unavailable");
        var before = store.GetGuild(guildId)!;
        var after = store.UpdateGuildEconomy(guildId, g =>
        {
            switch (name)
            {
                case "add_guild_money":
                    long money = checked(g.Money + value);
                    return money < 0 ? null : g with { Money = money };
                case "add_guild_point":
                    long points = checked(g.Point + value);
                    return points < 0 ? null : g with { Point = Math.Min(points, curve!.MaximumPoints) };
                case "guild_level":
                    if (value <= 0 || value > int.MaxValue) return null;
                    int level = Math.Min((int)value, curve!.Maximum);
                    return g with { Level = level, Exp = curve.ExpFor(level) }; // Points are preserved.
                case "add_guild_exp":
                    long exp = checked(g.Exp + value);
                    int changed = curve!.LevelFor(g.Level, exp);
                    return g with { Level = changed, Exp = changed >= curve.Maximum ? curve.MaximumExp : exp,
                        Point = checked(g.Point + curve.EarnedPoints(g.Level, changed)) };
                default: return null;
            }
        });
        if (after == null) return actions;
        bool levelUp = after.Level > before.Level;
        var members = store.GetGuildMembers(guildId);
        if (levelUp)
            foreach (var member in members)
                actions.Client(GuildClientAction.Def(member.UserDbId, "S_SYSTEM_MESSAGE", new Dictionary<string, object>
                { ["message"] = SocialHandlers.Smt(1108, "GuildLevel", after.Level.ToString(CultureInfo.InvariantCulture)) }));

        // Level QAC sends the World mirror before client level info; add-exp sends it last.
        if (name != "add_guild_exp") Mirror();
        if (name is "guild_level" or "add_guild_exp")
            foreach (var member in members)
                actions.Client(GuildClientAction.Def(member.UserDbId, "S_GUILD_LEVEL_INFO_CHANGED", new Dictionary<string, object>
                { ["guildDbId"] = guildId, ["newLevel"] = after.Level, ["newExp"] = after.Exp, ["isLevelUp"] = (byte)(levelUp ? 1 : 0) }));
        if (name is "add_guild_point" or "add_guild_exp")
            foreach (var member in members)
                actions.Client(GuildClientAction.Def(member.UserDbId, "S_GUILD_POINT_INFO_CHANGED", new Dictionary<string, object>
                { ["guildDbId"] = guildId, ["newPoint"] = after.Point }));
        if (name == "add_guild_money")
            foreach (var member in members)
                actions.Client(GuildClientAction.Def(member.UserDbId, "S_GUILD_MONEY_INFO_CHANGED", new Dictionary<string, object>
                { ["guildDbId"] = guildId, ["newMoney"] = after.Money }));
        if (name == "add_guild_exp") Mirror();
        return actions;

        void Mirror() => actions.World(0x144E, GuildWiring.BuildGuildDataPush(store, guildId)!);
    }
}
