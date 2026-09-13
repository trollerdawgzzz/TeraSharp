using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// When WorldServer is connected, character select hands the player over to
/// the real WorldServer. Arbiter first acknowledges the selection to the client
/// (S_SELECT_USER + content flags â€” these are Arbiter-owned), then sends
/// 0x138E (player enter) + 0x2738 (char data) to World, which drives the rest
/// via the 0x13F7 tunnel.
/// </summary>
public static class WorldEntry
{
    public static bool EnterWorld(GameSession s, ILogger log)
    {
        var w = Program.World;
        if (w == null || !w.IsReady) { if (w != null && w.IsConnected) log.LogWarning("World connected but not ready - using standalone"); return false; }

        var chr = s.SelectedCharacter;
        if (chr == null) { log.LogWarning("EnterWorld: no selected character"); return false; }

        // 1. Arbiter-side acknowledgement â€” closes the character select screen.
        s.SendByDef("S_SELECT_USER", new Dictionary<string, object>
        {
            ["unk1"] = 1, ["unk2"] = 0, ["unk3"] = 72339069014638592UL,
        });
        s.SendByDef("S_BROCAST_GUILD_FLAG", new Dictionary<string, object>());
        int[] contents = { 2, 3, 4, 8, 9, 22, 23, 20, 21, 34 };
        bool[] disabled = { false, false, false, true, true, false, false, false, false, false };
        for (int i = 0; i < contents.Length; i++)
            s.SendByDef("S_UPDATE_CONTENTS_ON_OFF", new Dictionary<string, object>
            {
                ["content"] = contents[i], ["disabled"] = disabled[i],
            });

        // 2. Allocate a unique tunnel key for this session; World echoes it in
        //    0x13F7 headers so we can route packets to the right client.
        s.TunnelKey = w.AllocateTunnelKey();

        // 3. Switch to tunnel mode BEFORE telling World, so nothing is missed.
        //    RegisterPlayer (inside EnterWorld) creates a fresh reorder buffer.
        s.EnterWorld();

        // 4. Tell World about the player â€” built from character data, not replayed.
        var record = Program.Store?.GetCharacter((int)chr.Id);
        var enterPayload = BuildEnterWorldPayload(s.GameId, chr, record?.WorldBlob, s.TunnelKey);
        w.SendFrame(WorldBridge.OpPlayerEnter, enterPayload);

        // 5. Send character data (world blob) to World.
        // (removed) A pre-emptive 0x2738 with playerId in the DLM-id slot completed the WRONG DLM item on a
        // fresh World whenever playerId != 1 (crash in DBLoadPromotionContext::ExecuteCommit, 2026-09-14).
        // World asks with 0x2711 and DbProxyHandlers.OnUserEnterWorld answers with the live id.

        // Chat/UI settings are Arbiter-owned and World never sends them. Without the chat-option
        // setting the client's chat window has no channel tabs configured, so S_CHAT arrives but
        // never renders (system messages use a separate UI path, which is why !test showed but
        // chat didn't). Send them here so the chat window initializes in World mode too.
        ClientSettingsHandlers.SendUserSetting(s);
        ClientSettingsHandlers.SendUiSetting(s);
        ClientSettingsHandlers.SendChatOption(s);

        log.LogInformation("Handed session {Id} to WorldServer (gameId {G}, char '{Name}')",
            s.Id, s.GameId, chr.Name);
        return true;
    }

    /// <summary>
    /// Build the 183-byte AS_ENTER_WORLD (0x138E) payload from character data.
    /// Writer: FUN_140360710 (Arb_part_027.c:12906), caller at Arb_part_028.c:15542.
    ///
    /// Layout: 4 offset u32s (two unused string offsets, raw-data offset, raw-data length),
    /// then sequential fields: two u64 session handles, playerId, server/template IDs,
    /// zone, position float3, level(?), various world-config fields, gameId, and trailing
    /// fixed fields. Last 16 bytes are raw data (world session state, zeros for new chars).
    ///
    /// Fields identified from decompile:
    ///   param_5  [32]     = playerId          (User+0x120)
    ///   param_8  [48]     = zone              (User+0x19c)
    ///   param_10 [56..67] = x, y, z           (User+0x18c/0x190/0x194)
    ///   param_15 [84..91] = gameId            (User+0x5718, high bit masked)
    ///
    /// Fields from world struct (capture defaults used, World re-derives from blob):
    ///   param_7  [44]     = 194942            (WorldSession+0x3108)
    ///   param_13 [76]     = 2000              (WorldSession+0x744)
    ///   param_20 [103]    = 6                 (WorldSession+0x758)
    ///   param_27 [129]    = 3                 (FUN_1407176f0, server config)
    /// </summary>
    internal static byte[] BuildEnterWorldPayload(ulong gameId, FakeCharacter chr, uint tunnelKey = 5)
        => BuildEnterWorldPayload(gameId, chr, null, tunnelKey);

    internal static byte[] BuildEnterWorldPayload(ulong gameId, FakeCharacter chr, byte[]? worldBlob, uint tunnelKey = 5)
    {
        var buf = new byte[183];
        var w = new SpanWriter(buf);

        // [0..15] Four offset u32s
        w.U32(0);                           // off1: unused string offset
        w.U32(0);                           // off2: unused string offset
        w.U32(6 + 167);                     // off3: frame-relative offset to raw data (=173)
        w.U32(16);                          // off4: raw data length

        // [16..23] Opaque handle 1 â€” Account::GetClientSession() pointer (WorldSession+0x768).
        //          World stores it, echoes it in some SA_ messages, but does not use it for
        //          routing. Zero is safe for a private server.
        w.U64(0);

        // [24..31] Opaque handle 2 â€” User struct 'this' pointer in the real Arbiter.
        //          World echoes this in SA_ messages (e.g. 0x1626).  We send gameId so
        //          WorldBridge can route by it when multi-player lands.
        w.U64(gameId);

        // [32..35] Player ID (param_5, from User+0x120)
        w.U32((uint)chr.Id);

        // [36..43] Unknown u64 (param_6, from User+0xb0). Capture=1, possibly serverId.
        w.U64(1);

        // [44..47] World session state (param_7, WorldSession+0x3108, from world blob
        //          deserialization). World re-derives from blob; capture default.
        w.U32(194942);

        // [48..51] Zone (param_8, User+0x19c)
        w.U32((uint)chr.Zone);

        // [52..55] param_9 (User+0x1a0). Real Arbiter sends -1 (lobby_tap.log pkt 127) and World then
        //          restores position from the blob; 0 (old capture) makes x/y/z authoritative.
        w.U32(0xFFFFFFFF);

        // [56..67] Position float3 (param_10, User+0x18c/190/194)
        // Prefer the blob's last-saved position (offset 220) so this is right even if World honours it.
        float px = chr.X, py = chr.Y, pz = chr.Z;
        if (worldBlob != null && worldBlob.Length >= 232)
        {
            px = BitConverter.ToSingle(worldBlob, 220);
            py = BitConverter.ToSingle(worldBlob, 224);
            pz = BitConverter.ToSingle(worldBlob, 228);
        }
        w.Float(px); w.Float(py); w.Float(pz);

        // [68..71] Likely character level (param_11, FUN_1403860c0 of User). Capture=1.
        w.U32((uint)chr.Level);

        // [72..75] Likely maxHP base (param_12, User+0x1e0, from DB col 76 /
        //          HeroWorldDataSheet+0x34). Capture=20446.
        // Real Arbiter copies this from the blob (u32 @304): dob 0x5AC4, fresh char 0xFFFFF334 (cap_newchar pkt 130).
        w.U32(worldBlob != null && worldBlob.Length >= 308 ? BitConverter.ToUInt32(worldBlob, 304) : 20446u);

        // [76..79] Client-reported session parameter (param_13, WorldSession+0x744).
        //          Default is 2048; capture shows 2000. World re-derives from blob.
        w.U32(2000);

        // [80..83] Tunnel slot index (param_14, PacketBypassManager::BypassStart()
        //          return value). Per-session: AllocateTunnelKey() assigns 5, 6, 7...
        w.U32(tunnelKey);

        // [84..91] Game ID (param_15, User+0x5718 & 0x7FFFFFFFFFFFFFFF)
        w.U64(gameId & 0x7FFFFFFFFFFFFFFF);

        // [92..93] Two u8 flags from WorldSession bit-field (param_16, param_17). Capture=0.
        w.U8(0);
        w.U8(0);

        // [94..101] Unknown u64 (param_18). Capture=0.
        w.U64(0);

        // [102] Unknown u8 (param_19). Capture=0.
        w.U8(0);

        // [103..106] From WorldSession+0x758 (param_20). Capture=6.
        w.U32(6);

        // [107..110] From WorldSession bit-field (param_21). Capture=0.
        w.U32(0);

        // [111..114] From User+0x3b98 (param_22). Capture=0.
        w.U32(0);

        // [115..116] Two u8s (param_23 from User+0x3c2a, param_24 from FUN_140386af0).
        w.U8(0);
        w.U8(0);

        // [117..120] Unknown u32 (param_25). Capture=0.
        w.U32(0);

        // [121..128] Unknown u64 (param_26). Capture=0.
        w.U64(0);

        // [129..132] Server config constant (param_27, FUN_1407176f0). Capture=3.
        w.U32(3);

        // [133..160] Seven u32s + two more (params 28-35). Capture: 20,7,3,0,0,0,0,0.
        w.U32(20);
        w.U32(7);
        w.U32(3);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        w.U32(0);

        // [165..166] Two u8s (param_36 from User+0x8a55, param_37). Capture=0.
        w.U8(0);
        w.U8(0);

        // [167..182] Raw data (16 bytes from WorldSession+0x3f48). Zeros for a fresh
        //            session; World populates from the blob.
        // Capture: 00 3C 10 B8 00 00 00 00 00 00 00 00 00 00 00 00
        // Use zeros â€” World re-reads from blob anyway.

        return buf;
    }

    /// <summary>
    /// Build the 0x2738 character data payload: [u32 blobOffset=19][u32 blobLen]
    /// [u32 playerId][u8 found][blob]. Same structure as DBS_USER_ENTERWORLD.
    /// </summary>
    internal static byte[] BuildCharacterDataPayload(int playerId, byte[]? worldBlob)
    {
        int blobLen = worldBlob?.Length ?? 0;
        bool found = worldBlob != null && blobLen == DbProxyHandlers.WorldBlobSize;
        var reply = new byte[13 + (found ? blobLen : 0)];
        BitConverter.GetBytes(19).CopyTo(reply, 0);                        // offset to blob (frame-relative)
        BitConverter.GetBytes(found ? blobLen : 0).CopyTo(reply, 4);       // blob length
        BitConverter.GetBytes(playerId).CopyTo(reply, 8);                  // player ID
        reply[12] = (byte)(found ? 1 : 0);                                 // found flag
        if (found) worldBlob!.CopyTo(reply, 13);
        return reply;
    }

    /// <summary>Helper to write sequential fields into a byte span.</summary>
    internal ref struct SpanWriter
    {
        private readonly Span<byte> _buf;
        private int _pos;

        public SpanWriter(Span<byte> buf) { _buf = buf; _pos = 0; }
        public int Position => _pos;

        public void U8(byte v) { _buf[_pos++] = v; }
        public void U32(uint v) { BitConverter.TryWriteBytes(_buf[_pos..], v); _pos += 4; }
        public void U64(ulong v) { BitConverter.TryWriteBytes(_buf[_pos..], v); _pos += 8; }
        public void Float(float v) { BitConverter.TryWriteBytes(_buf[_pos..], v); _pos += 4; }
    }
}

