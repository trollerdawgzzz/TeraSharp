// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201. Native QA account/VIP commands (registration Arb085:16234,16304-08,16394-16401).</summary>
public static class QaAccountCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "add_package", "remove_package", "clear_package", "set_account_res_level", "set_vip_level",
        "add_vip_game_exp", "add_vip_pub_exp", "add_vip_token", "clear_vip_token_recv", "clear_vip_reward_recv", "viptest",
    };

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || session.SelectedCharacter == null) return true;
        int id = (int)session.SelectedCharacter.Id;
        long account = store.AccountOf(id);
        string name = line.Name.ToLowerInvariant();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            if (name is "add_package" or "remove_package" or "clear_package")
            {
                Packages(session, store, line, now);
                return true;
            }
            if (name == "set_account_res_level")
            {
                int value = line.Args.Count == 1 ? QaGeneralCommands.NativeInt(line.Arg(0)) : 1;
                QaGeneralCommands.SetRestrictionLevel(session, unchecked((uint)value));
                ArbiterClientHandlers.SendToWorld(session, 0x14BC, Pair(id, value));
                return true;
            }
            if (name == "viptest")
            {
                if (line.Args.Count == 1 && line.Arg(0) is "on" or "off")
                    session.Send(ClientFrame(0x5C19, w => w.Write(line.Arg(0) == "on")));
                return true;
            }
            if (name == "clear_vip_reward_recv") { store.ClearVipLevelRewards(account); return true; }
            var sheet = VipSystemSheet.Entry.Value;
            if (!sheet.Available) { log.LogWarning("QA {Command}: VIP sheets unavailable", name); return true; }
            if (name == "clear_vip_token_recv")
            {
                // Separate daily-token date; do not overwrite the VIP shop reset timestamp.
                store.SetVipDailyTokenReceived(account, sheet.DailyBoundary(now) - 86400L);
                Notify(session, store, sheet, account, now, dailyAvailable: true);
                return true;
            }
            // Arb061:21-76,264-299,1586-1688: all three mutations honor VIPSetting.vipSystemOn.
            if (!sheet.Enabled || line.Args.Count == 0 || (name == "add_vip_token" && line.Args.Count != 1)) return true;
            int delta = QaGeneralCommands.NativeInt(line.Arg(0));
            if (name == "add_vip_token")
            {
                if (store.AddQaVipTokens(account, delta)) ArbiterClientHandlers.SendToWorld(session, 0x15DD, Pair(id, delta));
                return true;
            }
            var before = store.GetVipInfo(account);
            int oldLevel = sheet.LevelFor((long)before.GameExp + before.PubExp);
            if (name == "add_vip_pub_exp") store.AddQaVipPublisherExp(account, delta);
            else
            {
                int gameExp = name == "set_vip_level" ? checked(sheet.ExpFor(delta) - before.PubExp)
                    : checked(before.GameExp + delta);
                store.SetQaVipGameExp(account, gameExp);
            }
            Notify(session, store, sheet, account, now);
            // Native publisher-XP QA does not run ProcessOnLevelUp (Arb061:264-299).
            if (name != "add_vip_pub_exp")
            {
                var current = store.GetVipInfo(account);
                int newLevel = sheet.LevelFor((long)current.GameExp + current.PubExp);
                GrantLevelRewards(session, store, sheet, account, oldLevel, newLevel);
            }
        }
        catch (OverflowException) { log.LogWarning("QA {Command}: value exceeds native field width", name); }
        return true;
    }

    private static void Packages(GameSession session, CharacterStore store, GmCommandLine line, long now)
    {
        string name = line.Name.ToLowerInvariant();
        if (name == "add_package" && line.Args.Count is < 1 or > 3) return;
        if (name == "remove_package" && line.Args.Count is < 1 or > 2) return;
        string targetName = name == "add_package" && line.Args.Count == 3 ? line.Arg(2)
            : name == "remove_package" && line.Args.Count == 2 ? line.Arg(1)
            : name == "clear_package" && line.Args.Count == 1 ? line.Arg(0) : "";
        var target = targetName.Length == 0 || targetName.Equals(session.SelectedCharacter!.Name, StringComparison.OrdinalIgnoreCase)
            ? session : Program.World?.InWorldSessions().FirstOrDefault(s =>
                string.Equals(s.SelectedCharacter?.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (target?.SelectedCharacter == null) return; // Native resolves only currently online users.
        int id = (int)target.SelectedCharacter.Id;
        long account = store.AccountOf(id);
        if (name == "clear_package")
        {
            foreach (int old in store.ClearQaPackages(account)) Broadcast(0x15CF, Pair(id, old));
            return;
        }
        int package = QaGeneralCommands.NativeInt(line.Arg(0));
        if (!QaAccountSheet.Entry.Value.Ids.Contains(package)) return;
        if (name == "remove_package")
        {
            // AccountTraitBase::RemovePackage returns true even when the known package is absent.
            store.RemoveQaPackage(account, package); Broadcast(0x15CF, Pair(id, package)); return;
        }
        int seconds = line.Args.Count == 1 ? 9999999 : QaGeneralCommands.NativeInt(line.Arg(1));
        long expires = checked(now + seconds);
        // Arb003:1101-1108 special HuddleAdding package; the AS frame retains the requested expiry.
        long storedExpiry = package == 602 ? now + 7 * 86400L : expires;
        if (!store.AddQaPackage(account, package, storedExpiry)) return;
        var payload = new byte[16]; Pair(id, package).CopyTo(payload, 0); BitConverter.GetBytes(expires).CopyTo(payload, 8);
        Broadcast(0x15CE, payload);
    }

    private static void Broadcast(ushort opcode, byte[] payload)
    {
        if (Program.World is not { } world) return;
        // Arb040:2757-2779, Arb044:631-647: AccountTrait packages update every registered World.
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++)
            if (world.HasLinks(id)) world.SendFrame(id, opcode, payload);
    }

    private static void Notify(GameSession session, CharacterStore store, VipSystemSheet.Data sheet,
        long account, long now, bool? dailyAvailable = null)
    {
        var info = store.GetVipInfo(account);
        var world = new byte[20];
        Pair((int)session.SelectedCharacter!.Id, info.GameExp).CopyTo(world, 0);
        BitConverter.GetBytes(info.PubExp).CopyTo(world, 8); BitConverter.GetBytes(info.TokenAmount).CopyTo(world, 12);
        ArbiterClientHandlers.SendToWorld(session, 0x15DC, world);
        bool daily = dailyAvailable ?? store.GetVipDailyTokenReceived(account) < sheet.DailyBoundary(now);
        session.Send(BuildVipInfo(sheet, info, daily, now, VipDungeonRemaining(store, (int)session.SelectedCharacter.Id, sheet, now)));
    }

    // Arb061:17379-17478: only an exhausted VIP dungeon starts this reset countdown.
    public static long VipDungeonRemaining(CharacterStore store, int character, VipSystemSheet.Data sheet, long nowUnix)
    {
        var row = store.GetDungeonCoolTime(character, sheet.DungeonId);
        if (row?.Length != 52 || sheet.DungeonConstraint == null || sheet.DungeonResetHour is not int hour) return 0;
        if (QaDungeonInfo.Summarize(row, sheet.DungeonConstraint, "", DateTimeOffset.FromUnixTimeSeconds(nowUnix)).Remaining != 0) return 0;
        var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix).LocalDateTime;
        var reset = now.Date.AddDays(hour < now.Hour ? 1 : 0).AddHours(hour);
        return new DateTimeOffset(reset).ToUnixTimeSeconds() - nowUnix;
    }

    /// <summary>Arb065:3495-3511, native S_VIP_INFO. Greeting is empty unless a VIP store greeting event exists.</summary>
    public static byte[] BuildVipInfo(VipSystemSheet.Data sheet, CharacterStore.VipInfoRow info,
        bool dailyAvailable, long now, long dungeonRemaining = 0, string greeting = "")
        => ClientFrame(0x70C2, w =>
        {
            w.Write((ushort)53); w.Write(sheet.Enabled); w.Write(sheet.LevelFor((long)info.GameExp + info.PubExp));
            w.Write((long)info.GameExp); w.Write((long)info.PubExp); w.Write(info.TokenAmount); w.Write(dailyAvailable);
            w.Write(sheet.NextShopReset(now) - now); w.Write(dungeonRemaining);
            w.Write(sheet.LastShopReset(now) != info.LastResetTime); w.Write(Encoding.Unicode.GetBytes(greeting + '\0'));
        });

    private static void GrantLevelRewards(GameSession session, CharacterStore store, VipSystemSheet.Data sheet,
        long account, int oldLevel, int newLevel)
    {
        int receiver = (int)session.SelectedCharacter!.Id;
        foreach (var grade in sheet.Grades.Where(g => g.Level > oldLevel && g.Level <= newLevel))
        {
            // Arb065:6255-6290 claims each level once before mailing its lvUpToken item (6418-6487).
            if (!store.MarkVipLevelReward(account, grade.Level) || grade.RewardItem <= 0) continue;
            byte[] item = new byte[SystemParcelAttachments.ItemRecordSize];
            BitConverter.GetBytes(grade.RewardItem).CopyTo(item, 8); BitConverter.GetBytes(1).CopyTo(item, 12);
            int parcel = store.CreateParcel(0, sheet.Sender, receiver, sheet.Title, sheet.Body, 0, ParcelDbHandlers.ParcelTypeSystem);
            store.SetParcelRecord(parcel, SystemParcelAttachments.BuildRecord(parcel, receiver,
                session.SelectedCharacter.Name, sheet.Sender, sheet.Title, sheet.Body, 0, new[] { item }));
            store.AddParcelItem(parcel, 0, 0, grade.RewardItem, 1);
            var (unread, unclaimed) = store.GetParcelCounts(receiver);
            session.Send(ParcelHandlers.BuildReadRecvStatus((uint)unread, (uint)unclaimed, flag: true));
        }
    }

    private static byte[] Pair(int first, int second)
    { var p = new byte[8]; BitConverter.GetBytes(first).CopyTo(p, 0); BitConverter.GetBytes(second).CopyTo(p, 4); return p; }
    private static byte[] ClientFrame(ushort opcode, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write(opcode); write(writer);
        writer.Flush(); var bytes = stream.ToArray(); BitConverter.GetBytes(checked((ushort)bytes.Length)).CopyTo(bytes, 0); return bytes;
    }
}
