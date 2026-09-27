// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record GuildIncentiveResult(int Error, int GuildId, IReadOnlyList<int> Receivers);

    /// <summary>Arb046:4489-4615; 13662-13872; Arb083:972-1006. One mail per account,
    /// online character first, otherwise most recent guild-member logout. Chief account excluded
    /// from ordinary shares. Native uses float money conversion, then 10000 client-money units.</summary>
    public GuildIncentiveResult GiveQaGuildIncentive(int user, float rate, GuildIncentiveSheet.Data sheet,
        IReadOnlySet<int> online, long now)
    {
        lock (_lock)
        {
            int guildId = GetGuildIdOf(user); var guild = GetGuild(guildId);
            GuildIncentiveResult Refuse(int code) => new(code, guildId, Array.Empty<int>());
            if (guild == null || guild.ChiefDbId != user) return Refuse(0xEEF);
            if (!sheet.Available) return Refuse(0xF29);
            if (now - GetGuildIncentiveTime(guildId) < (long)sheet.CoolDays * 86400) return Refuse(0xF28);
            var members = GetGuildMembers(guildId);
            int accounts = members.Select(x => x.AccountId).Distinct().Count(), size = 0;
            foreach (var candidate in sheet.Sizes) { if (accounts < candidate.Minimum) break; size = candidate.Rank; }
            var range = sheet.Rates.FirstOrDefault(x => x.Size == size);
            if (range == null || !float.IsFinite(rate) || rate < range.Minimum || rate > range.Maximum) return Refuse(0xF29);
            if (guild.Money <= 0) return Refuse(0xF27);
            long deduction = (long)((float)guild.Money * rate);
            long unit = (long)((1.0 - sheet.Commission) * (float)deduction * 10000.0)
                / ((accounts - 1L) * sheet.MemberShare + sheet.MasterShare);
            if (deduction < 0 || deduction > guild.Money) return Refuse(0xF27);
            AddGuildMoney(guildId, -deduction); SetGuildIncentiveTime(guildId, now);
            AddGuildLog(guildId, 0x2D, guild.Name, actorDbId: user, paramI64: deduction);
            var receivers = new List<int>();
            Send(user, unit * sheet.MasterShare);
            long chiefAccount = AccountOf(user);
            foreach (var account in members.Where(x => x.AccountId != chiefAccount).GroupBy(x => x.AccountId))
            {
                var recipient = account.OrderByDescending(x => online.Contains(x.UserDbId)).ThenByDescending(x => x.LastLogoutTime).First();
                Send(recipient.UserDbId, unit * sheet.MemberShare);
            }
            return new(0, guildId, receivers);

            void Send(int receiver, long money)
            {
                if (money <= 0 || GetCharacter(receiver) is not { } who) return;
                int parcel = CreateParcel(0, guild.Name, receiver, sheet.Title, sheet.Text, money, ParcelDbHandlers.ParcelTypeSystem);
                SetParcelRecord(parcel, SystemParcelAttachments.BuildRecord(parcel, receiver, who.Name,
                    guild.Name, sheet.Title, sheet.Text, money, Array.Empty<byte[]>()));
                receivers.Add(receiver);
            }
        }
    }
}
