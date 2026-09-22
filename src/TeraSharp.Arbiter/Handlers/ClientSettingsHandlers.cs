// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// Client settings packets captured verbatim from a real 100.02 server.
/// These are opaque blobs the client needs to initialize its chat/UI systems.
/// </summary>
public static class ClientSettingsHandlers
{
    private const string UiSettingHex =
        "03 00 08 00 08 00 2A 00 1A 00 01 00 00 00 F0 67 BA 42 9A 99 80 42 4D 00 69 00 6E 00 69 00 6D 00 61 " +
        "00 70 00 00 00 2A 00 50 00 3C 00 01 00 00 00 BC F4 47 42 A0 1A 3F 41 57 00 6F 00 72 00 6C 00 64 00 4D 00 61 00 70 00 " +
        "32 00 00 00 50 00 00 00 62 00 01 00 00 00 DF CF 7C 42 A0 1A E7 40 4D 00 61 00 69 00 6E 00 4D 00 65 00 6E 00 75 00 00 00";

    private const string ChatOptionHex =
        "05 00 0C 00 00 00 00 00 0C 00 FD 00 14 00 5D 00 2D 00 3D 00 00 00 00 00 80 BF 00 00 80 BF 48 01 D8 " +
        "00 64 00 64 00 00 01 01 40 00 63 00 68 00 61 00 74 00 3A 00 31 00 00 00 43 00 48 00 41 00 54 00 54 00 59 00 50 00 45 " +
        "00 5F 00 4E 00 4F 00 52 00 4D 00 41 00 4C 00 00 00 5D 00 65 00 00 00 00 00 65 00 6D 00 CD 00 00 00 6D 00 75 00 09 00 " +
        "00 00 75 00 7D 00 01 00 00 00 7D 00 85 00 CB 00 00 00 85 00 8D 00 D0 00 00 00 8D 00 95 00 20 00 00 00 95 00 9D 00 21 " +
        "00 00 00 9D 00 A5 00 13 00 00 00 A5 00 AD 00 18 00 00 00 AD 00 B5 00 CC 00 00 00 B5 00 BD 00 D5 00 00 00 BD 00 C5 00 " +
        "03 00 00 00 C5 00 CD 00 D4 00 00 00 CD 00 D5 00 15 00 00 00 D5 00 DD 00 1A 00 00 00 DD 00 E5 00 07 00 00 00 E5 00 ED " +
        "00 CF 00 00 00 ED 00 F5 00 19 00 00 00 F5 00 00 00 02 00 00 00 FD 00 78 01 05 00 50 01 1E 01 2E 01 01 00 00 00 80 BF " +
        "00 00 80 BF 48 01 D8 00 64 00 64 00 00 00 01 40 00 63 00 68 00 61 00 74 00 3A 00 32 00 00 00 43 00 48 00 41 00 54 00 " +
        "54 00 59 00 50 00 45 00 5F 00 47 00 45 00 4E 00 45 00 52 00 41 00 4C 00 00 00 50 01 58 01 04 00 00 00 58 01 60 01 D7 " +
        "00 00 00 60 01 68 01 18 00 00 00 68 01 70 01 1B 00 00 00 70 01 00 00 D5 00 00 00 78 01 89 02 18 00 C9 01 99 01 A9 01 " +
        "02 00 00 00 80 BF 00 00 80 BF 48 01 D8 00 64 00 64 00 00 00 01 40 00 63 00 68 00 61 00 74 00 3A 00 33 00 00 00 43 00 " +
        "48 00 41 00 54 00 54 00 59 00 50 00 45 00 5F 00 4E 00 4F 00 52 00 4D 00 41 00 4C 00 00 00 C9 01 D1 01 68 00 00 00 D1 " +
        "01 D9 01 DB 00 00 00 D9 01 E1 01 6D 00 00 00 E1 01 E9 01 66 00 00 00 E9 01 F1 01 D2 00 00 00 F1 01 F9 01 CE 00 00 00 " +
        "F9 01 01 02 6C 00 00 00 01 02 09 02 D8 00 00 00 09 02 11 02 69 00 00 00 11 02 19 02 D9 00 00 00 19 02 21 02 D3 00 00 " +
        "00 21 02 29 02 CA 00 00 00 29 02 31 02 C9 00 00 00 31 02 39 02 67 00 00 00 39 02 41 02 6A 00 00 00 41 02 49 02 CF 00 " +
        "00 00 49 02 51 02 D0 00 00 00 51 02 59 02 6B 00 00 00 59 02 61 02 65 00 00 00 61 02 69 02 DA 00 00 00 69 02 71 02 CC " +
        "00 00 00 71 02 79 02 CB 00 00 00 79 02 81 02 CD 00 00 00 81 02 00 00 18 00 00 00 89 02 F4 02 03 00 DC 02 AA 02 BA 02 " +
        "03 00 00 00 80 BF 00 00 80 BF 48 01 D8 00 64 00 64 00 00 00 01 40 00 63 00 68 00 61 00 74 00 3A 00 34 00 00 00 43 00 " +
        "48 00 41 00 54 00 54 00 59 00 50 00 45 00 5F 00 57 00 48 00 49 00 53 00 50 00 45 00 52 00 00 00 DC 02 E4 02 07 00 00 " +
        "00 E4 02 EC 02 13 00 00 00 EC 02 00 00 18 00 00 00 F4 02 00 00 13 00 27 03 15 03 25 03 04 00 00 00 80 BF 00 00 80 BF " +
        "48 01 D8 00 64 00 64 00 00 00 00 40 00 63 00 68 00 61 00 74 00 3A 00 38 00 00 00 00 00 27 03 2F 03 19 00 00 00 2F 03 " +
        "37 03 01 00 00 00 37 03 3F 03 CB 00 00 00 3F 03 47 03 D0 00 00 00 47 03 4F 03 1A 00 00 00 4F 03 57 03 09 00 00 00 57 " +
        "03 5F 03 03 00 00 00 5F 03 67 03 02 00 00 00 67 03 6F 03 CC 00 00 00 6F 03 77 03 00 00 00 00 77 03 7F 03 18 00 00 00 " +
        "7F 03 87 03 15 00 00 00 87 03 8F 03 07 00 00 00 8F 03 97 03 D4 00 00 00 97 03 9F 03 CF 00 00 00 9F 03 A7 03 20 00 00 " +
        "00 A7 03 AF 03 CD 00 00 00 AF 03 B7 03 21 00 00 00 B7 03 00 00 D5 00 00 00";

    // S_LOAD_CLIENT_USER_SETTING — per-character UI state (hotkeys, shortcuts, presets, inventory layout)
    private const string UserSettingHex =
        "08 00 DF 03 08 97 56 12 04 32 23 43 50 52 70 08 97 56 52 2E 53 00 31 00 53 00 6B 00 69 00 6C 00 6C 00 48 00 6F 00 74 00 4B 00 65 00 79 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 32 78 DA E3 B8 5A EA C5 62 CC 60 C4 10 C4 C9 71 32 55 80 41 82 51 41 23 88 8B E3 5C AA C0 D5 54 30 9B 93 E3 1A 4C 98 93 E3 06 94 19 C1 08 00 65 8B 0B C3 F0 01 01 C0 02 38 52 E1 01 08 97 56 52 28 53 00 31 00 53 00 68 00 6F 00 72 00 74 00 43 00 75 00 74 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 A7 01 78 DA ED CC BD 0D C2 30 10 86 61 FF 11 9C 58 02 27 80 70 80 C2 15 0B 30 08 0C 91 81 68 A0 4E 05 0D 34 8C 10 81 52 21 51 C1 00 48 AC 01 77 FA 94 2D 70 F5 BC BA F3 D9 F3 C3 6D 44 2D 65 A3 32 2B 7C 12 44 3C 3E 75 A3 52 8A 41 A8 E2 7E 05 0F C9 35 D9 91 3D 2D 5D 6F 07 83 CA A9 76 F7 CF 02 07 0A AA F6 9D E2 53 A0 78 AD 31 28 F9 72 6B 79 A0 BC 08 27 15 3D 2C D9 39 AC D8 05 AC D9 23 D8 B0 C7 70 8F 3D 81 13 F6 14 EE B3 03 6C D9 25 9C B2 67 70 C6 9E C3 8E BD DC 6A 7D 31 CE 7E BB 27 FF D5 D5 0F B6 7B F3 5D F0 01 01 C0 02 9B 04 52 7B 08 97 56 52 36 53 00 31 00 45 00 71 00 75 00 69 00 70 00 6D 00 65 00 6E 00 74 00 50 00 72 00 65 00 73 00 65 00 74 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 34 78 DA E3 B8 1C 26 C4 25 C0 20 C1 A0 C0 A0 C1 60 C0 30 CA A6 90 CD 38 CA A6 98 CD 34 CA A6 98 CD 3C CA A6 98 CD 32 CA 26 97 2D 01 87 40 BE 34 00 E3 0E 66 3F F0 01 01 C0 02 E5 0C 52 3A 08 97 56 52 26 53 00 31 00 43 00 75 00 73 00 74 00 6F 00 6D 00 69 00 7A 00 65 00 4B 00 65 00 79 00 47 00 72 00 6F 00 75 00 70 00 7A 00 A2 01 04 08 98 92 01 F0 01 00 C0 02 04 52 5D 08 97 56 52 32 53 00 31 00 43 00 75 00 73 00 74 00 6F 00 6D 00 69 00 7A 00 65 00 50 00 61 00 64 00 42 00 75 00 74 00 74 00 6F 00 6E 00 47 00 72 00 6F 00 75 00 70 00 7A 00 A2 01 1B 08 97 56 1A 16 58 00 62 00 6F 00 78 00 54 00 79 00 70 00 65 00 53 00 5F 00 58 00 F0 01 00 C0 02 1B 52 3D 08 97 56 52 28 53 00 31 00 4E 00 50 00 43 00 47 00 75 00 69 00 6C 00 64 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 05 08 97 56 50 00 F0 01 00 C0 02 05 52 4D 08 97 56 52 38 53 00 31 00 53 00 6B 00 69 00 6C 00 6C 00 43 00 72 00 65 00 73 00 74 00 50 00 72 00 65 00 73 00 65 00 74 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 05 08 B1 5E 20 00 F0 01 00 C0 02 05 52 56 08 97 56 52 2A 53 00 31 00 49 00 6E 00 76 00 65 00 6E 00 74 00 6F 00 72 00 79 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 1C 08 B4 D0 0B 10 01 1A 12 08 00 10 00 18 01 18 02 18 03 18 04 18 05 18 06 18 07 20 01 F0 01 00 C0 02 1C 52 46 08 97 56 52 24 53 00 31 00 4F 00 72 00 64 00 65 00 72 00 73 00 43 00 6F 00 6E 00 74 00 72 00 6F 00 6C 00 6C 00 65 00 72 00 7A 00 A2 01 12 08 E6 B1 0A 10 01 18 00 20 00 28 00 30 00 38 00 40 00 F0 01 00 C0 02 12 52 38 08 97 56 52 24 53 00 31 00 51 00 75 00 65 00 73 00 74 00 53 00 75 00 6D 00 6D 00 61 00 72 00 79 00 56 00 69 00 65 00 77 00 7A 00 A2 01 04 08 F9 82 0B F0 01 00 C0 02 04";

    private static readonly byte[] UiSetting = ParseHex(UiSettingHex);
    private static readonly byte[] ChatOption = ParseHex(ChatOptionHex);
    private static readonly byte[] UserSetting = ParseHex(UserSettingHex);

    public static void SendChatOption(GameSession s) => s.SendRawBody("S_REPLY_CLIENT_CHAT_OPTION_SETTING", ChatOption);
    public static void SendUiSetting(GameSession s) => s.SendRawBody("S_REPLY_CLIENT_UI_SETTING", UiSetting);

    public static bool OnRequestChatOption(GameSession s, ReadOnlyMemory<byte> body) { SendChatOption(s); return true; }
    public static bool OnRequestUiSetting(GameSession s, ReadOnlyMemory<byte> body) { SendUiSetting(s); return true; }

    public static bool OnSaveChatOption(GameSession s, ReadOnlyMemory<byte> body)
    {
        s.SendByDef("S_SAVE_CLIENT_CHAT_OPTION_SETTING", new Dictionary<string, object> { ["result"] = (byte)1 });
        return true;
    }


    // =====================================================================
    // T19 — persisting the client's own option blobs.
    //
    // The client serialises its whole UI state — keybinds, hotbars, chat tabs, window layout,
    // inventory sort — and hands it to the Arbiter, which stores it verbatim and hands it back at
    // the next login. There are two scopes and they are separate rows:
    //
    //   per CHARACTER  C_SAVE_CLIENT_USER_SETTING    (40143 / 0x9CCF) -> S_LOAD_CLIENT_USER_SETTING    (28404 / 0x6EF4)
    //   per ACCOUNT    C_SAVE_CLIENT_ACCOUNT_SETTING                  -> S_LOAD_CLIENT_ACCOUNT_SETTING (52869 / 0xCE85)
    //
    // Body layout, all four packets, from the .def files (`bytes data`) and confirmed against the
    // capture: [u16 offset][u16 count][count bytes]. The offset is PACKET-relative — it counts the
    // 4-byte [u16 len][u16 opcode] header — so a blob that starts right after the descriptor has
    // offset 8, and its first byte is body[4]. The real handler reads exactly that:
    // Handler_C_SAVE_CLIENT_USER_SETTING (Arb_part_041.c:11024) treats its pointer as the PACKET
    // start and reads the packet's third and fourth u16 as the offset and the count.
    //
    // What the real Arbiter does with it:
    //   * User::SaveClientSetting (Arb_part_029.c:18757) / Account::SaveClientSetting
    //     (Arb_part_065.c:9779): drop the save outright when count is 0 or above 9000, otherwise
    //     write it to SQL and keep a copy. Nothing is parsed.
    //   * User::SendClientSetting (Arb_part_029.c:19563) writes opcode 0x6EF4, backpatches
    //     [offset][count] and appends the stored bytes — UNCONDITIONALLY, so a character with
    //     nothing stored gets an 8-byte packet whose body is `08 00 00 00`. That is exactly
    //     cap_newchar_client.log packet 309, the first login of the brand-new character "Test".
    //     Account::SendClientSetting does the same (it only logs when the length is 0).
    //
    // When the client saves: NOT only at logout. cap_newchar_client.log has
    // C_SAVE_CLIENT_USER_SETTING at packets 350, 2293 and 2297 — after the first spawn, after an
    // inventory change and after achievement progress — with the blob growing 991 -> 999 -> 1002
    // bytes. So the store has to be an upsert and has to tolerate a save at any moment.
    // C_SAVE_CLIENT_ACCOUNT_SETTING appears in neither capture: the account blob only changes
    // when the player edits account-scope options.
    //
    // When the server sends: S_LOAD_CLIENT_ACCOUNT_SETTING goes out twice — once right after
    // S_GET_USER_LIST at the lobby, and once in the post-spawn burst — and
    // S_LOAD_CLIENT_USER_SETTING only in that burst, immediately after the account one:
    //   S_FRIEND_GROUP_LIST, S_FRIEND_LIST, S_UPDATE_FRIEND_INFO,
    //   S_LOAD_CLIENT_ACCOUNT_SETTING, S_LOAD_CLIENT_USER_SETTING
    // (cap packets 306-309, lobby_proxy 265-268 and 920-923). TeraSharp already sends the user one
    // at that exact point, from HandlerRegistry's C_LOAD_TOPO_FIN in-world branch; T19 does not
    // move it. See status/CLIENT-SETTINGS.md for the one-line change that adds the account one.
    // =====================================================================

    /// <summary>
    /// Opcodes as they appear in the capture. The live path resolves names through the opcode
    /// table, not these; they are here to document the capture and to let the tests frame a body
    /// without loading data.json.
    /// </summary>
    public const ushort OpCSaveClientUserSetting = 0x9CCF;    // 40143
    public const ushort OpSLoadClientUserSetting = 0x6EF4;    // 28404
    public const ushort OpSLoadClientAccountSetting = 0xCE85; // 52869

    /// <summary>Frame header: [u16 length][u16 opcode]. The blob descriptor's offset counts it.</summary>
    public const int PacketHeaderSize = 4;
    /// <summary>Body header: [u16 offset][u16 count].</summary>
    public const int BlobDescriptorSize = 4;
    /// <summary>Offset a blob gets when it starts right after the descriptor: 4 + 4.</summary>
    public const int BlobOffset = PacketHeaderSize + BlobDescriptorSize;

    /// <summary>
    /// The body of an S_LOAD_* that carries nothing: <c>08 00 00 00</c>. Byte-identical to
    /// cap_newchar_client.log packet 309, which is what the real server sent "Test" — a character
    /// created minutes earlier — on its first login.
    /// </summary>
    public static byte[] EmptySettingBody() => new byte[] { BlobOffset, 0, 0, 0 };

    /// <summary>
    /// Pull the blob out of a C_SAVE_* body. <paramref name="body"/> excludes the 4-byte packet
    /// header, but the descriptor's offset includes it, hence the -PacketHeaderSize.
    ///
    /// <para>Returns an empty span for the shapes the real handler treats as "no data": a zero
    /// offset, an offset at or past the end of the packet, or a count that runs off the end. The
    /// real handler passes NULL to SaveClientSetting in those cases, which then refuses the save
    /// because the length check comes first — so an empty result must NOT clear what is stored.</para>
    /// </summary>
    public static ReadOnlySpan<byte> ParseSettingBlob(ReadOnlySpan<byte> body)
    {
        if (body.Length < BlobDescriptorSize) return ReadOnlySpan<byte>.Empty;
        int offset = body[0] | (body[1] << 8);
        int count = body[2] | (body[3] << 8);
        if (count <= 0) return ReadOnlySpan<byte>.Empty;

        int start = offset - PacketHeaderSize;             // offset is packet-relative
        if (offset == 0 || start < BlobDescriptorSize) return ReadOnlySpan<byte>.Empty;
        if (start > body.Length || count > body.Length - start) return ReadOnlySpan<byte>.Empty;
        return body.Slice(start, count);
    }

    /// <summary>
    /// Build an S_LOAD_* body around a stored blob: [u16 offset=8][u16 count][blob]. A null or
    /// empty blob gives <see cref="EmptySettingBody"/>, which is what the real Arbiter sends when
    /// it has nothing stored.
    /// </summary>
    public static byte[] BuildSettingBody(byte[]? blob)
    {
        if (blob == null || blob.Length == 0) return EmptySettingBody();
        var body = new byte[BlobDescriptorSize + blob.Length];
        body[0] = (byte)BlobOffset; body[1] = (byte)(BlobOffset >> 8);
        body[2] = (byte)blob.Length; body[3] = (byte)(blob.Length >> 8);
        blob.CopyTo(body, BlobDescriptorSize);
        return body;
    }

    /// <summary>
    /// What S_LOAD_CLIENT_USER_SETTING carries for a character that has never saved: the EMPTY
    /// form, exactly like the real Arbiter (cap_newchar_client.log packet 309). The client then
    /// builds its own class-appropriate defaults - hotbar, tooltips, window layout.
    ///
    /// <para>Until 2026-09-14 this returned <see cref="CapturedUserSettingBody"/> (one real
    /// character's saved UI, a valkyrie's). Live result: a new warrior spawned with the
    /// valkyrie's hotbar (empty for him) and item tooltips off. The chat window does NOT depend on
    /// the blob's content, only on the packet arriving after C_LOAD_TOPO_FIN, which is unchanged.</para>
    /// </summary>
    public static byte[] DefaultUserSettingBody() => EmptySettingBody();

    /// <summary>The old captured default (lobby_proxy.log packet 268), kept for tests and reference only.</summary>
    public static byte[] CapturedUserSettingBody() => (byte[])UserSetting.Clone();

    /// <summary>
    /// The default for S_LOAD_CLIENT_ACCOUNT_SETTING: EMPTY. Unlike the user one there is no
    /// incumbent captured default to preserve, and the only account blob in the captures is the
    /// human's own account options (graphics, sound, interface) — handing those to every new
    /// account would be the same mistake as the shared starter blob in T18. Empty is what the real
    /// Arbiter sends for an account with nothing stored.
    /// </summary>
    public static byte[] DefaultAccountSettingBody() => EmptySettingBody();

    /// <summary>
    /// S_LOAD_CLIENT_USER_SETTING: the character's stored blob, or the captured default.
    /// Timing is the caller's business — HandlerRegistry sends this from the C_LOAD_TOPO_FIN
    /// in-world branch and T19 deliberately does not change that.
    /// </summary>
    public static void SendUserSetting(GameSession s)
    {
        byte[]? stored = null;
        long charId = CharacterIdOf(s);
        if (charId > 0) stored = Program.Store?.LoadClientSetting(charId);
        s.SendRawBody("S_LOAD_CLIENT_USER_SETTING",
            stored != null ? BuildSettingBody(stored) : DefaultUserSettingBody());
    }

    /// <summary>S_LOAD_CLIENT_ACCOUNT_SETTING: the account's stored blob, or the empty form.</summary>
    public static void SendAccountSetting(GameSession s)
    {
        var stored = Program.Store?.LoadAccountSetting((long)s.Account.AccountId);
        s.SendRawBody("S_LOAD_CLIENT_ACCOUNT_SETTING",
            stored != null ? BuildSettingBody(stored) : DefaultAccountSettingBody());
    }

    /// <summary>
    /// C_SAVE_CLIENT_USER_SETTING. No reply: the capture has none, and the client does not wait
    /// for one (unlike C_SAVE_CLIENT_UI_SETTING, which gets S_SAVE_CLIENT_UI_SETTING).
    /// </summary>
    public static bool OnSaveUserSetting(GameSession s, ReadOnlyMemory<byte> body)
    {
        long charId = CharacterIdOf(s);
        var blob = ParseSettingBlob(body.Span);
        if (charId <= 0)
        {
            // No character selected yet — the real handler falls back to the session's lobby user;
            // we have nowhere to put it, so drop it rather than write it under the wrong id.
            return true;
        }
        if (blob.IsEmpty)
        {
            // Same as the real Arbiter: a zero-length save is refused and the stored blob stands.
            return true;
        }
        Program.Store?.SaveClientSetting(charId, blob.ToArray());
        return true;
    }

    /// <summary>C_SAVE_CLIENT_ACCOUNT_SETTING. No reply, same as the user one.</summary>
    public static bool OnSaveAccountSetting(GameSession s, ReadOnlyMemory<byte> body)
    {
        var blob = ParseSettingBlob(body.Span);
        if (blob.IsEmpty) return true;
        Program.Store?.SaveAccountSetting((long)s.Account.AccountId, blob.ToArray());
        return true;
    }

    /// <summary>
    /// The character these settings belong to. <c>SelectedCharacter</c> is set at C_SELECT_USER and
    /// is what the whole post-spawn burst keys off; PlayerId is the same value once in world and is
    /// the fallback for a session that has a player but no cached record.
    /// </summary>
    private static long CharacterIdOf(GameSession s)
        => s.SelectedCharacter != null ? s.SelectedCharacter.Id : s.PlayerId;

    private static byte[] ParseHex(string hex)
    {
        var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }
}
