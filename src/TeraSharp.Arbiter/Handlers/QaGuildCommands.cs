// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaGuildCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "set_guild_rec", "add_guild_contribution_point", "add_guild_this_week_play_time", "show_guild_week_play_time",
        "set_guild_play_time_next_week", "init_guild_wanted_writing_rewrite_time", "Guildaddmax",
        "guild_join_cooltime", "guild_cooltime", "set_floating_castle_coin", "set_logout_time",
        "create_guild", "join_guild", "guildwar_declare", "guildwar_cancel", "guildwar_surrender",
        "assign_guild_emblem", "collect_guild_emblem",
        "give_guild_incentive",
        "toggle_guildwar_acceptable", "reset_guildwar_toggle_cool", "guild_quest_clear", "guild_quest_add_limit_time",
    };
    public static int MemberMaximumOverride { get; private set; }
    public static bool CooldownEnabled { get; private set; } = true;
    public static int MemberMaximum => MemberMaximumOverride > 0 ? MemberMaximumOverride : 300; // Arb045:19864 native hard guard.
    public static void ResetForTests() { MemberMaximumOverride = 0; CooldownEnabled = true; }

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || session.SelectedCharacter == null) return true;
        int user = (int)session.SelectedCharacter.Id, value = QaGeneralCommands.NativeInt(line.Arg(0));
        int guild = store.GetGuildIdOf(user);
        switch (line.Name.ToLowerInvariant())
        {
            case "toggle_guildwar_acceptable":
                // Native wrapper permits only PvP, but Guild::SetGuildWarAcceptable rejects PvP.
                // PE and decompile agree: data/t201/native-proof/guildwar-toggle.asm.
                if (!QaGuildSheet.Mode.Value.Pve && store.GetGuild(guild)?.ChiefDbId == user)
                    GmCommandHandlers.SendCustom(session, "Cannot change GuildWar acceptable-state");
                break;
            case "reset_guildwar_toggle_cool":
                if (guild != 0 && store.HasGuildAuthority(guild, user, 0x10))
                {
                    if (QaGuildSheet.Mode.Value.Pve) store.ResetQaGuildWarToggleTime(guild);
                    GmCommandHandlers.SendCustom(session, "Reset GuildWar acceptable-state toggle cooltime");
                }
                break;
            case "guild_quest_clear":
                GuildWiring.Dispatcher(session, log).Dispatch(GuildWiring.Guilds.ResetQaGuildQuest(user), "qa-clear-guild-quest");
                break;
            case "guild_quest_add_limit_time":
                if (guild != 0 && line.Args.Count == 1)
                {
                    store.AddQaGuildQuestMinutes(guild, value);
                    var refresh = new GuildActions();
                    refresh.Client(GuildClientAction.Raw(user, "S_GUILD_QUEST_LIST", GuildWiring.Guilds.BuildGuildQuestList(store.GetGuild(guild)!, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), user)));
                    GuildWiring.Dispatcher(session, log).Dispatch(refresh, "qa-guild-quest-deadline");
                }
                break;
            case "give_guild_incentive":
                if (line.Args.Count != 1) break;
                float.TryParse(line.Arg(0), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rate);
                var online = Program.World?.InWorldSessions().Select(s => (int)s.PlayerId).ToHashSet() ?? new HashSet<int>();
                var incentive = store.GiveQaGuildIncentive(user, rate, GuildIncentiveSheet.Entry.Value, online, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var grant = new GuildActions();
                if (incentive.Error != 0)
                    grant.Client(GuildClientAction.Def(user, "S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = SocialHandlers.Smt(incentive.Error) }));
                else
                {
                    grant.World(0x144E, GuildWiring.BuildGuildDataPush(store, incentive.GuildId)!);
                    foreach (var m in store.GetGuildMembers(incentive.GuildId))
                    {
                        grant.Client(GuildClientAction.Def(m.UserDbId, "S_GUILD_MONEY_INFO_CHANGED", new Dictionary<string, object>
                            { ["guildDbId"] = incentive.GuildId, ["newMoney"] = store.GetGuild(incentive.GuildId)!.Money }));
                        grant.Client(GuildClientAction.Def(m.UserDbId, "S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = SocialHandlers.Smt(0xF26) }));
                    }
                    foreach (int receiver in incentive.Receivers)
                    {
                        var (unread, unclaimed) = store.GetParcelCounts(receiver);
                        grant.Client(GuildClientAction.Raw(receiver, "S_PARCEL_READ_RECV_STATUS", ParcelHandlers.BuildReadRecvStatus((uint)unread, (uint)unclaimed, false)[4..]));
                    }
                }
                GuildWiring.Dispatcher(session, log).Dispatch(grant, "qa-guild-incentive");
                break;
            case "create_guild":
                if (line.Args.Count != 2) { GmCommandHandlers.SendCustom(session, "create_guild [guild-name] [first-username]"); break; }
                if (guild != 0) { GmCommandHandlers.SendCustom(session, "You already have a guild!"); break; }
                if (store.GetGuildByName(line.Arg(0)) != null) { GmCommandHandlers.SendCustom(session, $"Guild[{line.Arg(0)}] already exists!"); break; }
                var first = Online(line.Arg(1));
                if (first == null) { GmCommandHandlers.SendCustom(session, $"User[{line.Arg(1)}] doesn't exist!"); break; }
                var created = GuildWiring.CreateGuildFromWorld(user, line.Arg(0), "Master", "Member", new[] { (int)first.PlayerId });
                GmCommandHandlers.SendCustom(session, created.GuildId == 0 ? $"Cannot create a guild[{line.Arg(0)}]!" : $"Create a guild[{line.Arg(0)}] successfully!");
                break;
            case "join_guild":
                if (line.Args.Count == 0 || guild != 0) break;
                var joined = store.GetGuildByName(line.Arg(0)); var who = store.GetCharacter(user);
                if (joined == null || who == null || store.AddGuildMember(joined.GuildId, user, who.Name, who.Race, who.Class, who.Gender, who.Level, who.AccountId) == 0) break;
                store.DeleteGuildAppliesOfUser(user); store.DeleteGuildInvitesOfUser(user); store.ClearGuildWanted(user);
                store.AddGuildLog(joined.GuildId, GuildHandlers.GuildLogJoin, who.Name, actorDbId: user);
                var actions = new GuildActions(); GuildWiring.Guilds.EmitMemberAddedForWorld(actions, joined.GuildId, user);
                GuildWiring.Dispatcher(session, log).Dispatch(actions, "qa-join-guild");
                GmCommandHandlers.SendCustom(session, $"User [{who.Name}] joined guild [{joined.Name}]!");
                break;
            case "guildwar_declare":
                if (line.Args.Count == 1)
                {
                    byte[] name = System.Text.Encoding.Unicode.GetBytes(line.Arg(0) + '\0');
                    GuildWarManager.OnClientPacket(session, GuildWarManager.C_DECLARE_GUILD_WAR, new byte[] { 6, 0 }.Concat(name).ToArray());
                }
                break;
            case "guildwar_cancel": case "guildwar_surrender":
                if (line.Args.Count == 1 && store.GetGuildByName(line.Arg(0)) is { } opponent)
                    GuildWarManager.OnClientPacket(session, line.Name.Equals("guildwar_cancel", StringComparison.OrdinalIgnoreCase)
                        ? GuildWarManager.C_WITHDRAW_GUILD_WAR : GuildWarManager.C_GIVE_UP_GUILD_WAR, BitConverter.GetBytes(opponent.GuildId));
                break;
            case "assign_guild_emblem": case "collect_guild_emblem":
                bool assigning = line.Name.Equals("assign_guild_emblem", StringComparison.OrdinalIgnoreCase);
                // Native collect requires two arguments but ignores the second, and writes the supplied id again.
                if (guild == 0 || line.Args.Count != (assigning ? 1 : 2) || !QaGuildSheet.Emblems.Value.TryGetValue(value, out bool forever)) break;
                if (store.SetQaGuildEmblem(guild, value, forever))
                {
                    var emblem = new byte[9]; BitConverter.GetBytes(guild).CopyTo(emblem, 0); emblem[4] = (byte)(forever ? 1 : 0); BitConverter.GetBytes(value).CopyTo(emblem, 5);
                    Broadcast(0x15AF, emblem);
                    if (assigning)
                    {
                        // Arb072:11493-11500 -> Arb045:19407-19515: SystemRewardMsg(type12,id,0),
                        // one 20-byte linked entry in S_SYSTEM_REWARD_MESSAGE. This is not a guild log.
                        byte[] reward = new byte[24];
                        BitConverter.GetBytes((ushort)1).CopyTo(reward, 0);
                        BitConverter.GetBytes((ushort)8).CopyTo(reward, 2);
                        BitConverter.GetBytes((ushort)8).CopyTo(reward, 4);
                        BitConverter.GetBytes(12).CopyTo(reward, 8);
                        BitConverter.GetBytes(value).CopyTo(reward, 12);
                        var rewards = new GuildActions();
                        foreach (var member in store.GetGuildMembers(guild))
                            rewards.Client(GuildClientAction.Raw(member.UserDbId, "S_SYSTEM_REWARD_MESSAGE", reward));
                        GuildWiring.Dispatcher(session, log).Dispatch(rewards, "qa-guild-emblem-reward");
                    }
                }
                break;
            case "guildaddmax": if (line.Args.Count == 1) MemberMaximumOverride = Math.Max(0, value); break;
            case "guild_join_cooltime":
                if (line.Args.Count > 0 && value >= 0) GuildHandlers.RejoinCooldownSeconds = unchecked(value * 3600);
                break;
            case "guild_cooltime":
                if (line.Args.Count == 1 && line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase)) CooldownEnabled = true;
                else if (line.Args.Count == 1 && line.Arg(0).Equals("off", StringComparison.OrdinalIgnoreCase)) CooldownEnabled = false;
                break;
            case "set_guild_rec":
                if (guild != 0 && line.Args.Count == 1 && value >= 0 && store.SetQaGuildRecommendation(guild, value))
                    Broadcast(0x1411, Pair(guild, value));
                break;
            case "add_guild_contribution_point":
                if (guild != 0 && line.Args.Count == 1 && store.AddQaGuildContribution(user, value, QaGuildSheet.Entry.Value.ContributionLimit))
                {
                    byte[]? data = GuildWiring.BuildGuildDataPush(store, guild);
                    if (data != null) Broadcast(0x144E, data);
                }
                break;
            case "set_floating_castle_coin":
                if (guild != 0 && line.Args.Count == 1 && store.AddQaGuildGeneralCoin(guild, value, QaGuildSheet.Entry.Value.CastleCoinLimit) is int delta)
                {
                    byte[] body = new byte[12]; Pair(guild, 0).CopyTo(body, 0); BitConverter.GetBytes(delta).CopyTo(body, 8);
                    // Native Guild::UpdateGuildGeneralCoinWithLock sends to server0 only (Arb046:15264).
                    Program.World?.SendFrame(0, 0x15A4, body);
                }
                break;
            case "add_guild_this_week_play_time":
                if (line.Args.Count != 2) break;
                var target = store.GetGuildByName(line.Arg(0));
                if (target == null) { GmCommandHandlers.SendCustom(session, "Fail Guild Search"); break; }
                long milliseconds = unchecked(QaGeneralCommands.NativeInt(line.Arg(1)) * 1000);
                store.AddQaGuildWeeklyTime(target.GuildId, milliseconds);
                GmCommandHandlers.SendCustom(session, $"[GuildName = {target.Name}] [add playtime = {milliseconds}] [ThisWeekPlayTime = {store.GetGuild(target.GuildId)!.ThisWeekPlayTime}]");
                break;
            case "show_guild_week_play_time":
                if (line.Args.Count != 1) break;
                var shown = store.GetGuildByName(line.Arg(0));
                GmCommandHandlers.SendCustom(session, shown == null ? "Fail Guild Search" : $"[LastWeekPlayTime = {shown.LastWeekPlayTime}] [ThisWeekPlayTime = {shown.ThisWeekPlayTime}]");
                break;
            case "set_guild_play_time_next_week":
                store.AdvanceQaGuildWeek(Program.World?.InWorldSessions().Select(s => (int)s.PlayerId));
                GmCommandHandlers.SendCustom(session, "All guilds set [ThisWeekPlayTime -> LastWeekPlayTime], [ThisWeekPlaytime = 0] finish!");
                break;
            case "init_guild_wanted_writing_rewrite_time":
                store.ClearQaGuildWantedCooldown(user); GmCommandHandlers.SendCustom(session, "Init Rewrite Time Complete"); break;
            case "set_logout_time":
                if (line.Args.Count != 4) break;
                var targetUser = Program.World?.InWorldSessions().FirstOrDefault(s => string.Equals(s.SelectedCharacter?.Name, line.Arg(0), StringComparison.OrdinalIgnoreCase));
                if (targetUser?.SelectedCharacter == null) break;
                try
                {
                    var date = new DateTime(QaGeneralCommands.NativeInt(line.Arg(1)), QaGeneralCommands.NativeInt(line.Arg(2)), QaGeneralCommands.NativeInt(line.Arg(3)), 0, 0, 0, DateTimeKind.Local);
                    store.SetQaLastLogout((int)targetUser.SelectedCharacter.Id, date);
                }
                catch (ArgumentOutOfRangeException) { log.LogWarning("set_logout_time: invalid calendar date"); }
                break;
        }
        return true;
    }
    private static byte[] Pair(int first, int second)
    { var body = new byte[8]; BitConverter.GetBytes(first).CopyTo(body, 0); BitConverter.GetBytes(second).CopyTo(body, 4); return body; }
    private static GameSession? Online(string name) => Program.World?.InWorldSessions().FirstOrDefault(s =>
        string.Equals(s.SelectedCharacter?.Name, name, StringComparison.OrdinalIgnoreCase));
    private static void Broadcast(ushort opcode, byte[] body)
    {
        if (Program.World is not { } world) return;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++)
            if (world.HasLinks(id)) world.SendFrame(id, opcode, body);
    }
}
