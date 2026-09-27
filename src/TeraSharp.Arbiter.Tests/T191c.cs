// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.IO.Compression;
using System.Text.Json;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    /// <summary>T191c evidence: the settings frames the two clients received, whole client packets.</summary>
    private static byte[] T191cFrame(string capture, int packet)
    {
        var path = FindRepoFile(Path.Combine("data", "t191c", "frames.json"))
            ?? throw new FileNotFoundException("tracked T191c frames.json missing");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var frame = document.RootElement.GetProperty(capture).EnumerateArray()
            .Single(f => f.GetProperty("n").GetInt32() == packet);
        return Convert.FromHexString(frame.GetProperty("hex").GetString()!);
    }

    /// <summary>[u16 len][u16 op][u16 blobOffset=8][u16 blobLen][blob] - the blob only.</summary>
    private static byte[] T191cBlob(byte[] frame)
    {
        Hex.True(frame.Length >= 8 && BitConverter.ToUInt16(frame, 4) == 8
            && BitConverter.ToUInt16(frame, 6) == frame.Length - 8, "settings frame carries one blob at offset 8");
        return frame[8..];
    }

    private static int T191cVarint(byte[] b, ref int i)
    {
        int value = 0, shift = 0;
        while (true)
        {
            byte c = b[i++];
            value |= (c & 0x7f) << shift; shift += 7;
            if ((c & 0x80) == 0) return value;
        }
    }

    /// <summary>
    /// One named section of a client-settings blob. Records are
    /// <c>08 97 56 52 [len][name UTF-16] ... a2 01 [varint len][value]</c>; the value is a raw
    /// zlib stream. Returns null when the blob has no such section.
    /// </summary>
    private static byte[]? T191cSection(byte[] blob, string name)
    {
        var key = System.Text.Encoding.Unicode.GetBytes(name);
        var marker = new byte[] { 0x08, 0x97, 0x56, 0x52 };
        for (int i = 0; i + marker.Length < blob.Length; i++)
        {
            if (blob.AsSpan(i, marker.Length).SequenceEqual(marker) == false) continue;
            int at = i + marker.Length;
            int len = blob[at++];
            if (len != key.Length || at + len > blob.Length || !blob.AsSpan(at, len).SequenceEqual(key)) continue;
            at += len;
            while (at + 2 < blob.Length && !(blob[at] == 0xa2 && blob[at + 1] == 0x01)) at++;
            at += 2;
            int size = T191cVarint(blob, ref at);
            using var input = new MemoryStream(blob, at, size);
            using var inflate = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }
        return null;
    }

    /// <summary>The repeated 12-byte field-2 records inside S1ShortCutController.</summary>
    private static List<(int A, int B, int Slot, int D)> T191cShortCutList(byte[] section)
    {
        var list = new List<(int, int, int, int)>();
        for (int i = 0; i + 14 <= section.Length; i++)
        {
            if (section[i] != 0x12 || section[i + 1] != 0x0c) continue;
            int at = i + 3;
            int a = T191cVarint(section, ref at); at++;
            int b = T191cVarint(section, ref at); at++;
            int slot = T191cVarint(section, ref at); at++;
            int d = T191cVarint(section, ref at);
            list.Add((a, b, slot, d));
            i += 13;
        }
        return list;
    }

    /// <summary>
    /// T191c. The real Arbiter loads the client's per-character settings once per world entry,
    /// after C_LOAD_TOPO_FIN: cap_2man_b_client1 312 (fin) -> 322 account, 323 user, and nowhere
    /// else. TeraSharp also pushed one at character select, so the client got the same blob twice
    /// per entry (cap_bg2_client1 51 and 342, byte-identical) and merged the incoming
    /// S1ShortCutController list into its live one. That list doubled every entry - 1024 records
    /// in cap_polish_client and cap_instance1_client1, 131072 here - until the blob passed the
    /// 9000-byte cap and every C_SAVE_CLIENT_USER_SETTING was refused, which is why the tutorial
    /// state behind the WASD prompt never survived a relog.
    /// </summary>
    [Test] public static void T191c_duplicate_user_setting_load_grew_the_shortcut_list()
    {
        if (FixtureOrSkip(Path.Combine("data", "t191c", "frames.json"), "T191c frames.json") is null) return;

        var retail = T191cBlob(T191cFrame("cap_2man_b_client1", 323));
        var retailShortCut = T191cSection(retail, "S1ShortCutController");
        Hex.True(retailShortCut != null && T191cShortCutList(retailShortCut).Count == 0,
            "retail's stored blob carries no S1ShortCutController field-2 records at all");
        Hex.True(retail.Length < CharacterStore.MaxClientSettingBytes / 4,
            "retail's whole per-character blob stays around a kilobyte");

        var first = T191cFrame("cap_bg2_client1", 51);
        var second = T191cFrame("cap_bg2_client1", 342);
        Hex.Eq(second, first, "TeraSharp sent the same user-setting load twice for one world entry");

        var ours = T191cSection(T191cBlob(first), "S1ShortCutController")!;
        var records = T191cShortCutList(ours);
        Hex.True(records.Count == 131072, "the runaway list holds 131072 records, not " + records.Count);
        Hex.True(records.Count(r => r == (9999, 65000001, 10, 0)) == 65536
            && records.Count(r => r == (9999, 65000002, 18, 0)) == 65536,
            "all of them are copies of the same two records - the client appends, it does not merge by slot");

        // The saves the client then made (arbiter-bg2.log 20:01:33/20:03:17/20:04:02) are refused,
        // and retail refuses the same sizes: User::SaveClientSetting Arb_part_029.c:18757 and
        // Account::SaveClientSetting Arb_part_065.c:9793 both open with (len == 0 || 9000 < len).
        foreach (int refused in new[] { 10094, 14557, 19004 })
            Hex.True(refused > CharacterStore.MaxClientSettingBytes, refused + "-byte save is over the retail cap");
    }

    /// <summary>
    /// T191c. Once a character's stored blob is past the cap nothing can ever be written again,
    /// so the recovery is to throw it away and let the client rebuild a small one.
    /// </summary>
    [Test] public static void T191c_clearing_a_runaway_blob_lets_saves_work_again()
    {
        using var store = StoreWithTwoAccounts();
        var small = new byte[512]; small[0] = 0x08;
        Hex.True(store.SaveClientSetting(1, small) && store.LoadClientSetting(1)!.Length == 512,
            "a normal blob is stored");

        Hex.True(!store.SaveClientSetting(1, new byte[19004]), "a 19004-byte blob is refused like retail");
        Hex.True(store.LoadClientSetting(1)!.Length == 512, "the refused save leaves the stored blob alone");

        Hex.True(store.ClearClientSetting(1) && store.LoadClientSetting(1) == null,
            "clearing drops it so the next login serves an empty body");
        Hex.True(store.SaveClientSetting(1, small) && store.LoadClientSetting(1)!.Length == 512,
            "saves work again once the runaway is gone");
        Hex.True(!store.ClearClientSetting(2), "a character with nothing stored reports no change");
    }
}
