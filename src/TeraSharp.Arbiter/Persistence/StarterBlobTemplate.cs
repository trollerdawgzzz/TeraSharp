// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Text;

namespace TeraSharp.Arbiter.Persistence;

// =============================================================================================
// StarterBlob.Generate - T209. The creation-time UserData record, built from zeros.
//
// WHY. data/starter_blob.bin is 15312 bytes the real ArbiterServer sent in SDB_UPDATE_USER_DATA
// for one character: "Test", a Popori female Glaiver on account 1. Every character this server
// created started as a clone of it, so the file was a hard runtime dependency - no file, no
// character creation - and 15080 of its bytes are zero anyway.
//
// WHAT IT TOOK. Only 232 of the 15312 bytes are non-zero, in 92 runs. 173 of those come from
// CreateCharData.xml, DefaultSkillSet.xml, the C_CREATE_USER request and six documented
// constants; T209 named the rest against ImportCharacterManager::Import_Users
// (Arb_part_032.c:17732), which parses a character export straight into this record and so
// labels each column against its offset. Cross-checked against User::UpdateUserData
// (Arb_part_031.c:1372), AccountManager::ExecCreateUser (Arb_part_080.c:13413) and World's
// UpdateUserDataContext::ExecuteTransaction (WorldServer.exe.c:1638343).
//
// WHAT IS NOT REPRODUCIBLE, and why that is fine. Eleven bytes differ from the capture and
// always will: every one of them is the padding ABOVE a bool or u8 field, which the real
// Arbiter never initialises - it is whatever was in the record's backing memory. Two captures of
// the identical character state carry different values there (114 and 193 at +0x3AF4; be, 3c, aa
// and 00 at +0x3B7B), so those bytes are noise, not data. Neither binary reads them: both treat
// the field as one byte. UninitializedRuns lists all seven runs and T209 asserts the set is
// exactly that - add a field there and the test tightens rather than silently widening.
//
// The three wall-clock date structs are reproducible in shape but not in value: they are the
// original capture's clock. Generate takes the time as a parameter so a test can pin it.
// =============================================================================================
public static partial class StarterBlob
{
    // ---- offsets T209 named. The blob is UserData; repo comments elsewhere say User+X = blob+X+0xB0.

    /// <summary>i64 <c>accountDBID</c> (Arb_part_032.c:17726). Was the capture's account 1 on
    /// every character this server created, because the whole template was cloned.</summary>
    public const int AccountDbIdOffset = 0x0000;

    /// <summary>UTF-16LE <c>accountName</c> (Arb_part_032.c:17727).</summary>
    public const int AccountNameOffset = 0x0008;

    /// <summary>The account name's room, in characters, before <see cref="PlayerIdOffset"/>.</summary>
    public const int AccountNameMaxChars = 48;

    /// <summary>bool <c>isAlive</c> (Arb_part_032.c:17808), read as one byte into User+0x198
    /// (Arb_part_031.c:1397). A new character is alive.</summary>
    public const int IsAliveOffset = 0x00E8;

    /// <summary>The character's 1-based slot on its account - <c>characters.position</c>. Not in
    /// the Import_Users column list, but it tracks the slot across every captured blob and is
    /// constant per character over levels 1 to 70.</summary>
    public const int SlotOrdinalOffset = 0x01B8;

    /// <summary>f32 <c>condition</c> (Arb_part_032.c:17836). <c>User::UpdateUserData</c> stamps
    /// 120.0 back over whatever arrives (Arb_part_031.c:1429) and World does the same
    /// (WorldServer.exe.c:998618), so the value is fixed however a character was made.</summary>
    public const int ConditionOffset = 0x1A40;

    /// <summary>u16 <c>profPet</c>, the homunculus gathering proficiency
    /// (Arb_part_032.c:17843). 1 in every captured blob, including at level 70.</summary>
    public const int ProfPetOffset = 0x1ABC;

    /// <summary>u8 <c>pegasusStage</c> (Arb_part_032.c:17852).</summary>
    public const int PegasusStageOffset = 0x3ACC;

    /// <summary>i64 <c>totalExp</c> (Arb_part_032.c:17853), bound to <c>spUpdateUserTotalExp</c>
    /// (Arb_part_031.c:1421). 1 at level 1 for every race and class; 3930 at 3, 299488 at 8.</summary>
    public const int TotalExpOffset = 0x3AD0;

    /// <summary>i32 <c>guildRecommendCount</c> (Arb_part_032.c:17867), with
    /// <c>lastRecommendCountUpdateTime</c> in the date struct straight after it.</summary>
    public const int GuildRecommendCountOffset = 0x3B58;

    /// <summary>i32 <c>ActPoint</c>. <c>User::InitActPoint</c> treats -1 here as "not yet
    /// initialised" and fills it in (WorldServer.exe.c:896638), which is exactly what a new
    /// character wants - writing a number would skip World's own initialisation.</summary>
    public const int ActPointOffset = 0x3B70;

    /// <summary>u8 <c>isFacialAttachment</c> (Arb_part_032.c:17873).</summary>
    public const int FacialAttachmentOffset = 0x3B78;

    /// <summary>The unnamed u8 beside it; <c>User::UpdateUserData</c> binds it with the byte
    /// binder (Arb_part_031.c:1410).</summary>
    public const int FacialAttachmentFlag2Offset = 0x3B79;

    /// <summary>u8 <c>isTutorialPlaying</c> (Arb_part_032.c:17874). <c>User::UpdateUserData</c>
    /// REFUSES TO SAVE AT ALL while this is non-zero (Arb_part_031.c:1373), so a new character
    /// must start at 0 or its first save is silently dropped.</summary>
    public const int TutorialPlayingOffset = 0x3B7A;

    /// <summary>i32 <c>ObserverType</c> - GM spectator mode. <c>User::AdminSetObserverMode</c>
    /// owns it (Arb_part_028.c:1311) and a positive value grants an unchecked teleport
    /// (Arb_part_062.c:15116), so -1 is the only safe value to create with.</summary>
    public const int ObserverTypeOffset = 0x3B7C;

    /// <summary>An unnamed u8 read one byte at a time by both binaries
    /// (Arb_part_031.c:1412, WorldServer.exe.c:948342). Zero in every captured blob.</summary>
    public const int UnnamedFlag3B80Offset = 0x3B80;

    /// <summary>u8 "older account" flag. <c>Account::LoadRookieUserInfo</c> sets it when the
    /// account's oldest character predates a datasheet threshold (Arb_part_065.c:1143) and
    /// <c>ExecCreateUser</c> repeats that at the end of creation (Arb_part_080.c:13646).</summary>
    public const int RookieFlagOffset = 0x3BCC;

    // ---- the nine date structs -------------------------------------------------------------

    /// <summary>
    /// The record's repeating 16-byte date: <c>u16 year, month, day, hour, minute, second</c>
    /// then <c>u32</c> nanoseconds - an ODBC <c>SQL_TIMESTAMP_STRUCT</c>. Proof it is
    /// nanoseconds and not something else: the capture's two live fractions are 840000000 and
    /// 793000000, exactly 840 ms and 793 ms.
    /// </summary>
    public const int DateStructSize = 16;

    /// <summary>The six dates a new character carries as 1970-01-01, the record's "never".</summary>
    public static readonly int[] NeverDateOffsets = { 0x0110, 0x1A74, 0x1A84, 0x3B5C, 0x3B88, 0x3B98 };

    /// <summary>The three that carry a real clock: <c>lastRestBonusApplyTime</c> (0x1A8), the
    /// 0x1A94 stamp, and the account-age stamp <c>Account::LoadRookieUserInfo</c> writes
    /// (0x3BBC). A generated blob stamps all three with the creation time.</summary>
    public static readonly int[] ClockDateOffsets = { 0x01A8, 0x1A94, 0x3BBC };

    // ---- the irreducible eleven ------------------------------------------------------------

    /// <summary>
    /// The only bytes a generated blob cannot match, and the reason: each run is the padding
    /// above a bool or u8 field, left uninitialised by the real Arbiter. Both binaries read the
    /// field as a single byte, so nothing consumes these. T209 asserts this list is complete.
    /// </summary>
    public static readonly (int Offset, int Length, string Why)[] UninitializedRuns =
    {
        (0x00E9, 3, "above the isAlive bool at 0x00E8"),
        (0x01BC, 1, "after the slot ordinal at 0x01B8"),
        (0x3ACD, 1, "above the pegasusStage byte at 0x3ACC"),
        (0x3AF4, 1, "between MaxInvenSlotCount (0x3AF0) and expandInvenCount (0x3B00); two captures of the same state carry 114 and 193"),
        (0x3B7B, 1, "the fourth byte of the flag group at 0x3B78"),
        (0x3B81, 3, "above the unnamed byte at 0x3B80"),
        (0x3BCD, 1, "above the older-account flag at 0x3BCC"),
    };

    // ---- creation constants ----------------------------------------------------------------

    /// <summary>Hp and mp at creation. Read-only at runtime (see <see cref="HpOffset"/>); this
    /// is only the value a brand-new character is made with.</summary>
    public const int CreationHp = 100000, CreationMp = 100000;

    /// <summary><c>condition</c>, which World restamps anyway.</summary>
    public const float CreationCondition = 120.0f;

    /// <summary><c>totalExp</c> at level 1.</summary>
    public const long CreationTotalExp = 1;

    /// <summary>Bag slots when <c>CreateCharData.xml</c> names none: the sheet's own comment
    /// gives 8x5, and every captured blob carries 40.</summary>
    public const int DefaultInvenSlotCount = 40;

    /// <summary>
    /// Spawn facing as the blob carries it: <c>&lt;InitLoc dir&gt;</c> in degrees mapped onto the
    /// client's 16-bit turn. The sheet's default <c>dir="-18"</c> gives -18/360 x 65536 = -3276.8,
    /// truncated to -3276 - which is the value in the captured blob, so the mapping is confirmed
    /// rather than assumed. <c>CharacterHandlers</c> called this offset unidentified before T209.
    /// </summary>
    public static int DirectionFromDegrees(double degrees)
        => (int)(degrees / 360.0 * 65536.0);

    /// <summary>What <see cref="Generate"/> needs that is not in a datasheet: the account, the
    /// character's slot, and the clock. Everything else has a default that matches creation.</summary>
    public sealed record Seed(
        long AccountDbId = 0,
        string AccountName = "",
        int SlotOrdinal = 1,
        int Level = 1,
        int Hp = CreationHp,
        int Mp = CreationMp,
        long TotalExp = CreationTotalExp,
        int Direction = -3276,
        int InvenSlotCount = DefaultInvenSlotCount,
        DateTime CreatedUtc = default);

    /// <summary>
    /// Build the 15312-byte creation record from zeros. The result is a drop-in for the captured
    /// template: <see cref="Build"/> patches the same fields on top of either one.
    /// </summary>
    public static byte[] Generate(Seed seed)
    {
        var blob = new byte[Size];

        WriteAccount(blob, seed.AccountDbId, seed.AccountName, seed.SlotOrdinal);

        BitConverter.TryWriteBytes(blob.AsSpan(LevelOffset, 4), seed.Level);
        BitConverter.TryWriteBytes(blob.AsSpan(HpOffset, 4), seed.Hp);
        BitConverter.TryWriteBytes(blob.AsSpan(MpOffset, 4), seed.Mp);
        BitConverter.TryWriteBytes(blob.AsSpan(TotalExpOffset, 8), seed.TotalExp);
        BitConverter.TryWriteBytes(blob.AsSpan(EnterWorldParamOffset, 4), seed.Direction);
        BitConverter.TryWriteBytes(blob.AsSpan(MaxInvenSlotCountOffset, 4), seed.InvenSlotCount);

        blob[IsAliveOffset] = 1;
        blob[PegasusStageOffset] = 0;
        blob[FacialAttachmentOffset] = 1;
        blob[FacialAttachmentFlag2Offset] = 1;
        blob[TutorialPlayingOffset] = 0;
        blob[UnnamedFlag3B80Offset] = 0;
        blob[RookieFlagOffset] = 1;

        BitConverter.TryWriteBytes(blob.AsSpan(ConditionOffset, 4), CreationCondition);
        BitConverter.TryWriteBytes(blob.AsSpan(ProfPetOffset, 2), (ushort)1);
        BitConverter.TryWriteBytes(blob.AsSpan(GuildRecommendCountOffset, 4), 1);
        BitConverter.TryWriteBytes(blob.AsSpan(ActPointOffset, 4), -1);
        BitConverter.TryWriteBytes(blob.AsSpan(ObserverTypeOffset, 4), -1);

        foreach (int at in NeverDateOffsets) WriteDate(blob, at, NeverDate);
        var now = seed.CreatedUtc == default ? DateTime.UtcNow : seed.CreatedUtc;
        foreach (int at in ClockDateOffsets) WriteDate(blob, at, now);

        return blob;
    }

    /// <summary>1970-01-01, the record's "this never happened".</summary>
    public static readonly DateTime NeverDate = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Stamp the three fields that say WHO owns this record. The captured template carried the
    /// capture's own account on it, so before T209 every character this server created claimed
    /// to be account 1's second character no matter whose it was.
    /// </summary>
    public static bool WriteAccount(byte[]? blob, long accountDbId, string? accountName, int slotOrdinal)
    {
        if (blob == null || blob.Length < PlayerIdOffset) return false;
        BitConverter.TryWriteBytes(blob.AsSpan(AccountDbIdOffset, 8), accountDbId);
        BitConverter.TryWriteBytes(blob.AsSpan(SlotOrdinalOffset, 4), slotOrdinal);

        Array.Clear(blob, AccountNameOffset, AccountNameMaxChars * 2);
        string name = accountName ?? string.Empty;
        if (name.Length > AccountNameMaxChars - 1) name = name[..(AccountNameMaxChars - 1)];
        Encoding.Unicode.GetBytes(name, 0, name.Length, blob, AccountNameOffset);
        return true;
    }

    /// <summary>One ODBC date struct. Anything outside the struct's range writes the never date
    /// rather than wrapping into a nonsense year.</summary>
    public static void WriteDate(byte[] blob, int offset, DateTime when)
    {
        if (blob.Length < offset + DateStructSize) return;
        if (when.Year is < 1 or > 9999) when = NeverDate;
        var span = blob.AsSpan(offset, DateStructSize);
        span.Clear();
        BitConverter.TryWriteBytes(span[..2], (ushort)when.Year);
        BitConverter.TryWriteBytes(span[2..4], (ushort)when.Month);
        BitConverter.TryWriteBytes(span[4..6], (ushort)when.Day);
        BitConverter.TryWriteBytes(span[6..8], (ushort)when.Hour);
        BitConverter.TryWriteBytes(span[8..10], (ushort)when.Minute);
        BitConverter.TryWriteBytes(span[10..12], (ushort)when.Second);
        BitConverter.TryWriteBytes(span[12..16], (uint)when.Millisecond * 1_000_000u);
    }

    /// <summary>Read one back, for tests and for the admin tool.</summary>
    public static DateTime ReadDate(byte[] blob, int offset)
    {
        if (blob.Length < offset + DateStructSize) return default;
        int year = BitConverter.ToUInt16(blob, offset), month = BitConverter.ToUInt16(blob, offset + 2);
        int day = BitConverter.ToUInt16(blob, offset + 4), hour = BitConverter.ToUInt16(blob, offset + 6);
        int minute = BitConverter.ToUInt16(blob, offset + 8), second = BitConverter.ToUInt16(blob, offset + 10);
        uint ns = BitConverter.ToUInt32(blob, offset + 12);
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day is < 1 or > 31) return default;
        try
        {
            return new DateTime(year, month, day, hour, minute, second, (int)(ns / 1_000_000u), DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException) { return default; }
    }

    /// <summary>
    /// A copy of <paramref name="blob"/> with the eleven uninitialised bytes zeroed, so two
    /// blobs can be compared on the bytes that mean something. This is what makes the T209
    /// equality test honest rather than a test against a table of magic values.
    /// </summary>
    public static byte[] WithoutUninitialized(byte[] blob)
    {
        var copy = (byte[])blob.Clone();
        foreach (var (offset, length, _) in UninitializedRuns)
            if (offset + length <= copy.Length) Array.Clear(copy, offset, length);
        return copy;
    }

    /// <summary>How the generated blob is described in <c>--selftest</c> and the settings screen.</summary>
    public static string Describe(bool fromFile, string? path)
        => fromFile
            ? "from " + path
            : string.Create(CultureInfo.InvariantCulture,
                $"generated ({Size} B; {UninitializedRuns.Length} padding runs left zero)");
}
