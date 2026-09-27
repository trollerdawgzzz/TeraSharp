// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_guild_incentive_uses_sheet_account_shares_and_system_parcels()
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-incentive-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "GuildConfig.xml"), "<GuildConfig><GuildSizeTable><GuildSize rank='0' accountNumOver='1'/></GuildSizeTable>"
            + "<GuildIncentiveRate commission='0.25' coolTimeDay='7' mailTitle='@guildMail:101' mailtext='@guildMail:102'><Incentive guildSize='0' minRate='0.1' maxRate='0.3'/></GuildIncentiveRate>"
            + "<GuildIncentiveShare master='5' officer='999' member='1'/></GuildConfig>");
        try
        {
            GuildIncentiveSheet.Entry.Load(directory); using var h = new T201AccountHarness();
            int guild = h.Store.CreateGuild("PayGuild", 1, false); h.Store.AddGuildMember(guild, 1, "g1", 0, 0, 0, 70, 1);
            long account = h.Store.GetOrCreateAccount("member").Id;
            int old = h.Store.CreateCharacter(new CharacterRecord { Name = "old", AccountId = account });
            int recent = h.Store.CreateCharacter(new CharacterRecord { Name = "recent", AccountId = account });
            h.Store.AddGuildMember(guild, old, "old", 0, 0, 0, 70, account);
            h.Store.AddGuildMember(guild, recent, "recent", 0, 0, 0, 70, account);
            h.Store.SetQaLastLogout(old, new DateTime(2020, 1, 1)); h.Store.SetQaLastLogout(recent, new DateTime(2021, 1, 1));
            h.Store.AddGuildMoney(guild, 1000);
            T181WithOperators("t39", () => h.Run("give_guild_incentive 0.2"));
            Hex.True(BitConverter.ToUInt16(h.Main.Frame(), 4) == 0x144E && BitConverter.ToUInt16(h.Instance.Frame(), 4) == 0x144E,
                "Arb04615390 updates World guild money before mail notifications");
            var chief = h.Store.GetParcelsFor(1).Single(); var member = h.Store.GetParcelsFor(recent).Single();
            Hex.True(h.Store.GetGuild(guild)!.Money == 800 && chief.Money == 1250000 && member.Money == 250000,
                "native float deduction200, commission25%, master/member5:1, account count2 and10000 client-money units");
            Hex.True(h.Store.GetParcelsFor(old).Count == 0 && chief.SenderName == "PayGuild" && chief.Title == "@guildMail:101"
                && chief.ParcelType == ParcelDbHandlers.ParcelTypeSystem, "one mail per account chooses recent offline guild member; sender is guild name");
            Hex.Eq(h.Store.GetParcelRecord(chief.ParcelId)!.AsSpan(SystemParcelAttachments.ParcelMoneyOffset, 8).ToArray(),
                Convert.FromHexString("D012130000000000"), "native ParcelData money64 at0x950 contains1250000");
            var cooldown = h.Store.GiveQaGuildIncentive(1, .2f, GuildIncentiveSheet.Entry.Value, new HashSet<int>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Hex.True(cooldown.Error == 0xF28 && h.Store.GetParcelsFor(1).Count == 1, "native seven-day cooldown prevents duplicate grant");
            h.Store.SetGuildIncentiveTime(guild, 0);
            var invalid = h.Store.GiveQaGuildIncentive(1, .4f, GuildIncentiveSheet.Entry.Value, new HashSet<int>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Hex.True(invalid.Error == 0xF29 && h.Store.GetGuild(guild)!.Money == 800, "rate outside size-specific sheet range changes nothing");
            var next = h.Store.GiveQaGuildIncentive(1, .2f, GuildIncentiveSheet.Entry.Value, new HashSet<int> { old }, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Hex.True(next.Error == 0 && h.Store.GetParcelsFor(old).Count == 1 && h.Store.GetParcelsFor(recent).Count == 1,
                "native online account representative takes priority over newest offline logout");
            Hex.True(h.Store.GiveQaGuildIncentive(old, .2f, GuildIncentiveSheet.Entry.Value, new HashSet<int>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Error == 0xEEF,
                "only guild chief may distribute funds");
        }
        finally { GuildIncentiveSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true); }
    }
}
