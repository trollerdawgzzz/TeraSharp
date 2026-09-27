// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    private readonly HashSet<int> _qaWantedCooldownCleared = new();
    private readonly Dictionary<int, long> _qaGuildPlayStart = new();
    private readonly Dictionary<(int Guild, int Quest, long Started, long Ends), long> _qaQuestMinutes = new();
    public void AddQaGuildQuestMinutes(int guild, int minutes)
    {
        lock (_lock)
            foreach (var quest in GetGuildQuests(guild).Where(x => x.Status == 1))
            {
                var old = _qaQuestMinutes.Keys.FirstOrDefault(x => x.Guild == guild && x.Quest == quest.QuestId);
                var key = old == default ? (guild, quest.QuestId, quest.StartedAt, quest.EndsAt) : old;
                _qaQuestMinutes[key] = _qaQuestMinutes.GetValueOrDefault(key) + minutes * 60L;
            }
    }
    private GuildQuestState QaQuestDeadline(GuildQuestState quest) => quest with
    { EndsAt = quest.EndsAt + _qaQuestMinutes.GetValueOrDefault((quest.GuildId, quest.QuestId, quest.StartedAt, quest.EndsAt)) };
    private void ResetQaQuestDeadline(int guild, int? quest = null)
    {
        foreach (var key in _qaQuestMinutes.Keys.Where(k => k.Guild == guild && (quest == null || k.Quest == quest)).ToArray())
            _qaQuestMinutes.Remove(key);
    }
    public void ResetQaGuildWarToggleTime(int guild) => QaUpdateGuild("war_toggle_time=$value", guild, 0);
    public void ClearQaGuildWantedCooldown(int character) { lock (_lock) _qaWantedCooldownCleared.Add(character); }
    public bool SetQaGuildRecommendation(int guild, int value)
        => QaUpdateGuild("recommendation_point=$value", guild, value);
    public bool AddQaGuildWeeklyTime(int guild, long milliseconds)
        => QaUpdateGuild("this_week_play_time=this_week_play_time+$value", guild, milliseconds);
    public bool SetQaGuildEmblem(int guild, int emblem, bool forever)
        => QaUpdateGuild((forever ? "forever_emblem_id" : "emblem_id") + "=$value", guild, emblem);
    public void AdvanceQaGuildWeek(IEnumerable<int>? online = null, DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            long now = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds();
            foreach (int user in online ?? Array.Empty<int>())
            {
                if (!_qaGuildPlayStart.TryGetValue(user, out long start) || start == 0) continue;
                int guild = GetGuildIdOf(user);
                if (guild != 0 && now > start) AddQaGuildWeeklyTime(guild, now - start);
                _qaGuildPlayStart[user] = now;
            }
            using var cmd = _db.CreateCommand(); cmd.CommandText = "UPDATE guilds SET last_week_play_time=this_week_play_time,this_week_play_time=0"; cmd.ExecuteNonQuery();
        }
    }
    public int? AddQaGuildGeneralCoin(int guild, int delta, int maximum)
    {
        lock (_lock)
        {
            var row = GetGuild(guild); if (row == null || (long)row.GeneralCoin + delta < 0) return null;
            int applied = (int)Math.Min(delta, (long)maximum - row.GeneralCoin);
            return QaUpdateGuild("general_coin=general_coin+$value", guild, applied) ? applied : null;
        }
    }
    public bool AddQaGuildContribution(int user, int delta, int maximum)
    {
        lock (_lock)
        {
            var member = GetGuildMember(user); if (member == null || member.WeeklyContribution >= maximum) return false;
            int applied = (int)Math.Min(delta, (long)maximum - member.WeeklyContribution);
            return AddGuildContribution(user, applied, applied);
        }
    }
    private bool QaUpdateGuild(string assignment, int guild, long value)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "UPDATE guilds SET " + assignment + " WHERE guild_id=$id";
            cmd.Parameters.AddWithValue("$value", value); cmd.Parameters.AddWithValue("$id", guild); return cmd.ExecuteNonQuery() == 1;
        }
    }
    public void SetQaLastLogout(int user, DateTime localDate)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET last_logout=$date WHERE id=$u; UPDATE guild_members SET last_logout_time=$epoch WHERE user_db_id=$u";
            cmd.Parameters.AddWithValue("$date", localDate.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$epoch", new DateTimeOffset(localDate).ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$u", user); cmd.ExecuteNonQuery();
        }
    }
}
