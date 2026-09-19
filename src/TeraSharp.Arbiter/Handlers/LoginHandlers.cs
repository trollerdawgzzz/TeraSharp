using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Auth;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Game;

namespace TeraSharp.Arbiter.Handlers;

public sealed class LoginHandlers
{
    private readonly ILogger _log;
    public LoginHandlers(ILogger log) => _log = log;

    /// <summary>When true, C_SELECT_USER and C_LOAD_TOPO_FIN replay the capture verbatim instead of generating packets.</summary>
    public static bool PureReplay = false;

    public bool OnLoginArbiter(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_LOGIN_ARBITER", body);
        string name = f != null && f.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
        uint language = f != null && f.TryGetValue("language", out var l) ? Convert.ToUInt32(l) : 6;
        s.Account.Name = string.IsNullOrEmpty(name) ? s.Account.Name : name;

        // Auth gate (status/AUTH-DESIGN.md). Default provider accepts everything; TERASHARP_AUTH=true
        // validates the launcher's authKey ticket against tera-api's /authApi/GameAuthenticationLogin.
        string ticket = AuthTicket.Decode(f != null && f.TryGetValue("ticket", out var tk) ? tk : null);
        int patch = f != null && f.TryGetValue("patchVersion", out var pv) ? Convert.ToInt32(pv) : 0;
        var verdict = AuthProviders.Authenticate(Program.Auth,
            new AuthRequest(s.Account.Name, (long)s.Account.AccountId, ticket, "", language, patch));
        if (!verdict.Accepted)
        {
            _log.LogWarning("C_LOGIN_ARBITER: account '{Name}' rejected by {Provider}: {Code} {Msg}",
                s.Account.Name, Program.Auth.Name, verdict.Code, verdict.Message);
            s.SendByDef("S_LOGIN_ARBITER", new Dictionary<string, object>
            {
                ["success"] = false, ["loginQueue"] = false, ["status"] = (uint)verdict.Code, ["unk"] = 0u,
                ["language"] = language, ["pvpDisabled"] = false, ["unk1"] = (ushort)0, ["unk2"] = (ushort)0,
            });
            return true;
        }

        // Load account + characters from the DB.
        if (Program.Store != null) s.Account.LoadFromStore(Program.Store, s.Account.Name);

        _log.LogInformation("C_LOGIN_ARBITER: account '{Name}' (id {Id}) language {Lang}, {N} character(s)",
            s.Account.Name, s.Account.AccountId, language, s.Account.Characters.Count);

        s.SendByDef("S_LOADING_SCREEN_CONTROL_INFO", new Dictionary<string, object> { ["enableCustom"] = false });
        s.SendByDef("S_SERVER_BUILD_INFO", new Dictionary<string, object> { ["langType"] = 6, ["revision"] = 376056 });
        s.SendByDef("S_REMAIN_PLAY_TIME", new Dictionary<string, object> { ["accountType"] = 6, ["minutesLeft"] = 0 });
        s.SendByDef("S_LOGIN_ARBITER", new Dictionary<string, object>
        {
            // T89b: status is the tera-api privilege passthrough - every ordinary account in nine captures
            // got 31 (0b11111) and only a brand-new account got 0; 33 is the In-Game Operation Tool
            // account. Sending 0 tells the client it is a fresh account, which is what draws the
            // TERA / Battle Arena mode-select screen before character select.
            ["success"] = true, ["loginQueue"] = false,
            ["status"] = GmAccounts.LoginStatusFor(s.Account.Name, Program.Store?.GetAdminLevel((long)s.Account.AccountId) ?? 0),   // T104: 33 opens Alt+A
            ["unk"] = 0u,
            ["language"] = language, ["pvpDisabled"] = false, ["unk1"] = (ushort)0, ["unk2"] = (ushort)0,
        });
        s.SendByDef("S_LOGIN_ACCOUNT_INFO", new Dictionary<string, object>
        {
            ["accountId"] = s.Account.AccountId, ["antiCheatChecksumSeed"] = 0,
            ["dbServerName"] = "TeraSharp", ["apiServerAddress"] = "127.0.0.1", ["apiServerAuthToken"] = "",
        });
        return true;
    }

    public bool OnGetUserList(GameSession s, ReadOnlyMemory<byte> body)
    {
        _log.LogInformation("C_GET_USER_LIST -> {N} character(s)", s.Account.Characters.Count);

        var characters = new List<object>();
        foreach (var c in s.Account.Characters)
        {
            var entry = new Dictionary<string, object>
            {
                ["id"] = c.Id, ["gender"] = c.Gender, ["race"] = c.Race, ["class"] = c.Class,
                ["level"] = c.Level, ["hp"] = c.Hp, ["mp"] = c.Mp,
                ["worldId"] = c.WorldId, ["guardId"] = c.GuardId, ["sectionId"] = c.SectionId,
                ["lastLogoutTime"] = 0L, ["isDeleting"] = false, ["deleteTime"] = 0L, ["deleteRemainSec"] = 0,
                ["weapon"] = c.Weapon, ["earring1"] = 0, ["earring2"] = 0,
                ["body"] = c.Body, ["hand"] = c.Hand, ["feet"] = c.Feet, ["unkItem7"] = 0,
                ["ring1"] = 0, ["ring2"] = 0, ["underwear"] = 0, ["head"] = 0, ["face"] = 0,
                ["appearance"] = c.Appearance, ["isSecondCharacter"] = false, ["adminLevel"] = 0,
                ["isBanned"] = false, ["banEndTime"] = 0L, ["banRemainSec"] = 0, ["canUseStatus"] = 0,
                ["weaponModel"] = 0, ["unkModel2"] = 0, ["unkModel3"] = 0,
                ["bodyModel"] = 0u, ["handModel"] = 0u, ["feetModel"] = 0u,
                ["unkModel7"] = 0, ["unkModel8"] = 0, ["unkModel9"] = 0, ["unkModel10"] = 0,
                ["unkDye1"] = 0u, ["unkDye2"] = 0u, ["weaponDye"] = 0u, ["bodyDye"] = 0u,
                ["handDye"] = 0u, ["feetDye"] = 0u, ["unkDye7"] = 0u, ["unkDye8"] = 0u,
                ["unkDye9"] = 0u, ["underwearDye"] = 0u, ["styleBackDye"] = 0u,
                ["styleHeadDye"] = 0u, ["styleFaceDye"] = 0u,
                ["styleHead"] = 0, ["styleFace"] = 0, ["styleBack"] = 0,
                ["styleWeapon"] = 0, ["styleBody"] = 0, ["styleFootprint"] = 0,
                ["styleBodyDye"] = 0u, ["weaponEnchant"] = 0u,
                ["restBonusXp"] = 0L, ["maxRestBonusXp"] = 419L, ["showFace"] = true,
                ["styleHeadScale"] = 1.0f, ["styleHeadRotation"] = new float[] { 0, 0, 0 },
                ["styleHeadTranslation"] = new float[] { 0, 0, 0 }, ["styleHeadTranslationDebug"] = new float[] { 0, 0, 0 },
                ["styleFaceScale"] = 1.0f, ["styleFaceRotation"] = new float[] { 0, 0, 0 },
                ["styleFaceTranslation"] = new float[] { 0, 0, 0 }, ["styleFaceTranslationDebug"] = new float[] { 0, 0, 0 },
                ["styleBackScale"] = 1.0f, ["styleBackRotation"] = new float[] { 0, 0, 0 },
                ["styleBackTranslation"] = new float[] { 0, 0, 0 }, ["styleBackTranslationDebug"] = new float[] { 0, 0, 0 },
                ["usedStyleHeadTransform"] = false, ["isNewCharacter"] = true, ["tutorialState"] = 0,
                ["showStyle"] = true, ["appearance2"] = 100, ["achievementPoints"] = 0, ["laurel"] = 0,
                ["position"] = c.Position, ["guildLogoId"] = 0, ["awakeningLevel"] = 0,
                ["hasBrokerSales"] = false, ["petAdventureStatus"] = 0u,
                ["name"] = c.Name, ["details"] = c.Details, ["shape"] = c.Shape,
                ["guildName"] = "", ["customStrings"] = new List<object>(),
            };
            CharacterHandlers.FillLobbyFields(entry, Program.Store, (int)c.Id);   // T76: location, last login, rest bonus
            characters.Add(entry);
        }

        s.SendByDef("S_GET_USER_LIST", new Dictionary<string, object>
        {
            ["characters"] = characters, ["veteran"] = false, ["bonusBufSec"] = 0, ["maxCharacters"] = CharacterHandlers.MaxCharactersPerAccount,
            ["first"] = true, ["more"] = false, ["leftDelTimeAccountOver"] = 0,
            ["deletionSectionClassifyLevel"] = 5, ["deleteCharacterExpireHour1"] = 0, ["deleteCharacterExpireHour2"] = 72,
        });

        s.SendByDef("S_LOAD_CLIENT_ACCOUNT_SETTING", new Dictionary<string, object> { ["data"] = Array.Empty<byte>() });
        // T89b: two lobby packets the real Arbiter sends here (cap_final_client frames 13 and 15).
        s.Send(new byte[] { 0x08, 0x00, 0xB3, 0x57, 0x00, 0x00, 0x00, 0x00 });                      // S_DECO_UI_INFO, body all zero
        {
            var inv = new byte[17];
            inv[0] = 0x11; inv[1] = 0x00; inv[2] = 0x1D; inv[3] = 0xD4;                                 // S_CONFIRM_INVITE_CODE_BUTTON
            inv[4] = 0x0F; inv[5] = 0x00; inv[6] = 0x01;                                               // [u16 off=0x0F][u8 1]
            BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).CopyTo(inv, 7);           // [i64 unix]
            inv[15] = 0x00; inv[16] = 0x00;
            s.Send(inv);
        }
        s.SendByDef("S_ACCOUNT_PACKAGE_LIST", ArbiterClientHandlers.BuildAccountPackageFields(
            Program.Store?.GetAccountBenefits((int)s.Account.AccountId) ?? new List<TeraSharp.Arbiter.Persistence.CharacterStore.AccountBenefitRow>()));   // T84
        SendContentFlags(s);
        ClientSettingsHandlers.SendUiSetting(s);
        ClientSettingsHandlers.SendChatOption(s);
        return true;
    }

    private static void SendContentFlags(GameSession s)
    {
        int[] contents = { 2, 3, 4, 8, 9, 22, 23, 20, 21, 34 };
        bool[] disabled = { false, false, false, true, true, false, false, false, false, false };
        for (int i = 0; i < contents.Length; i++)
            s.SendByDef("S_UPDATE_CONTENTS_ON_OFF", new Dictionary<string, object>
            {
                ["content"] = contents[i], ["disabled"] = disabled[i],
            });
    }

    public bool OnSelectUser(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_SELECT_USER", body);
        int id = f != null && f.TryGetValue("id", out var v) ? Convert.ToInt32(v) : 0;

        var chr = s.Account.Characters.Find(c => c.Id == (uint)id) ?? s.Account.Characters.FirstOrDefault();
        if (chr == null) { _log.LogWarning("C_SELECT_USER: no character {Id}", id); return true; }

        s.SelectedCharacter = chr;
        // gameId from capture S_LOGIN/S_SPAWN_ME: 140737671856129 = 0x80000AF00001
        // Per-login counter from the World bridge (restarts at 1 per World process), like the real Arbiter.
        s.GameId = Program.World != null ? Program.World.AllocateGameId() : 0x80000AF00000UL | chr.Id;
        TeraSharp.Arbiter.World.DbProxyHandlers.GameIdByPlayer[(int)chr.Id] = s.GameId;
        s.PlayerId = chr.Id;

        _log.LogInformation("C_SELECT_USER: entering world as '{Name}' (gameId {GameId})", chr.Name, s.GameId);

        // If the real WorldServer is connected, hand the player to it and let it drive.
        var world = Program.World;
        if (world != null && world.IsConnected && !world.IsReady)
        {
            // World still loading: reject like the real Arbiter does (client shows "You can't enter right now").
            _log.LogWarning("C_SELECT_USER while World not ready - rejecting");
            s.SendByDef("S_SYSTEM_MESSAGE", new Dictionary<string, object> { ["message"] = "@769" });
            s.SendByDef("S_SELECT_USER", new Dictionary<string, object> { ["unk1"] = 0, ["unk2"] = 0, ["unk3"] = 72339069014638592UL });
            return true;
        }
        if (WorldEntry.EnterWorld(s, _log)) return true;

        if (PureReplay) { SpawnReplay.ReplayPhase1(s, _log); return true; }

        s.SendByDef("S_SELECT_USER", new Dictionary<string, object>
        {
            ["unk1"] = 1, ["unk2"] = 0, ["unk3"] = 72339069014638592UL,
        });
        s.SendByDef("S_BROCAST_GUILD_FLAG", new Dictionary<string, object>());
        SendContentFlags(s);

        s.SendByDef("S_LOGIN", new Dictionary<string, object>
        {
            ["templateId"] = chr.TemplateId, ["gameId"] = s.GameId, ["serverId"] = 2800u,
            ["playerId"] = s.PlayerId, ["actionMode"] = 0, ["alive"] = true, ["status"] = 0,
            ["walkSpeed"] = 56, ["runSpeed"] = 170, ["appearance"] = chr.Appearance,
            ["visible"] = true, ["isSecondCharacter"] = false, ["level"] = (ushort)chr.Level,
            ["awakeningLevel"] = 0u, ["profMineral"] = 0, ["profBug"] = 0, ["profHerb"] = 0, ["profEnergy"] = 0,
            ["profPet"] = (ushort)1, ["pkDeclareCount"] = 0, ["pkKillCount"] = 0,
            ["totalXp"] = 1L, ["levelXp"] = 1L, ["totalLevelXp"] = 840L,
            ["epLevel"] = 0, ["epXp"] = 0L, ["epDailyXp"] = 0,
            ["restBonusXp"] = 0L, ["maxRestBonusXp"] = 419L, ["xpBonusPercent"] = 0, ["dropBonusPercent"] = 0,
            ["weapon"] = chr.Weapon, ["body"] = chr.Body, ["hand"] = chr.Hand, ["feet"] = chr.Feet,
            ["underwear"] = 0, ["head"] = 0, ["face"] = 0, ["serverTime"] = 125140L, ["isPkServer"] = true,
            ["chatBanEndTime"] = 0L, ["title"] = 0,
            ["weaponModel"] = 0, ["bodyModel"] = 0, ["handModel"] = 0, ["feetModel"] = 0,
            ["weaponDye"] = 0, ["bodyDye"] = 0, ["handDye"] = 0, ["feetDye"] = 0,
            ["underwearDye"] = 0, ["styleBackDye"] = 0, ["styleHeadDye"] = 0, ["styleFaceDye"] = 0,
            ["weaponEnchant"] = 0, ["isWorldEventTarget"] = false, ["infamy"] = 0, ["showFace"] = true,
            ["styleHead"] = 0, ["styleFace"] = 0, ["styleBack"] = 0, ["styleWeapon"] = 0,
            ["styleBody"] = 0, ["styleFootprint"] = 0, ["styleBodyDye"] = 0, ["showStyle"] = true,
            ["titleCount"] = 0L, ["appearance2"] = 100, ["scale"] = 1.0f, ["guildLogoId"] = 0,
            ["name"] = chr.Name, ["details"] = chr.Details, ["shape"] = chr.Shape,
            ["servants"] = new List<object>(),
        });

        ClientSettingsHandlers.SendUserSetting(s);
        s.SendRawBody("S_USER_BLOCK_LIST", new byte[] { 0, 0, 0, 0 });
        s.SendRawBody("S_FRIEND_GROUP_LIST", new byte[] { 0x01,0x00,0x08,0x00,0x08,0x00,0x00,0x00,0x12,0x00,0x02,0x00,0x00,0x00,0x7D,0x59,0xCB,0x53,0x00,0x00 });
        s.SendRawBody("S_FRIEND_LIST", new byte[] { 0x00,0x00,0x00,0x00,0x0A,0x00,0xCA,0x4E,0x29,0x59,0x5F,0x4E,0x2F,0x66,0x09,0x61,0xEB,0x5F,0x84,0x76,0x00,0x4E,0x29,0x59,0x21,0x00,0x00,0x00 });
        s.SendByDef("S_INVEN_USERDATA", new Dictionary<string, object>());
        s.SendByDef("S_CHANGE_POCKET_NAME", new Dictionary<string, object>());
        InventoryHandlers.SendInventory(s, chr);
        InventoryHandlers.SendSkills(s);
        s.SendByDef("S_AVAILABLE_SOCIAL_LIST", new Dictionary<string, object>());
        s.SendByDef("S_CLEAR_QUEST_INFO", new Dictionary<string, object>());
        s.SendByDef("S_DAILY_QUEST_COMPLETE_COUNT", new Dictionary<string, object>());
        s.SendByDef("S_COMPLETED_MISSION_INFO", new Dictionary<string, object>());
        s.SendByDef("S_NPCGUILD_LIST", new Dictionary<string, object>());
        s.SendByDef("S_VIRTUAL_LATENCY", new Dictionary<string, object>());
        s.SendByDef("S_MOVE_DISTANCE_DELTA", new Dictionary<string, object>());
        s.SendByDef("S_MY_DESCRIPTION", new Dictionary<string, object>());
        s.SendByDef("S_FESTIVAL_LIST", new Dictionary<string, object>());

        s.SendByDef("S_LOAD_TOPO", new Dictionary<string, object>
        {
            ["zone"] = chr.Zone, ["x"] = chr.X, ["y"] = chr.Y, ["z"] = chr.Z, ["unk"] = 0,
        });

        var benefits = Program.Store?.GetAccountBenefits((int)s.Account.AccountId) ?? new List<TeraSharp.Arbiter.Persistence.CharacterStore.AccountBenefitRow>();
        s.SendByDef("S_ACCOUNT_BENEFIT_LIST", ArbiterClientHandlers.BuildAccountBenefitFields(benefits));   // T84
        s.SendByDef("S_SEND_USER_PLAY_TIME", ArbiterClientHandlers.BuildUserPlayTimeFields(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));   // T84
        s.SendByDef("S_ENABLE_DISABLE_SELLABLE_ITEM_LIST", ArbiterClientHandlers.BuildSellableItemListFields());   // T84 (frame 441)
        return true;
    }

    /// <summary>
    /// Client finished loading the zone. Spawn the player.
    /// S_SPAWN_ME from capture [319]: gameId + loc(-449,6239,1956) + angle + alive + isLord
    /// </summary>
    public bool OnLoadTopoFin(GameSession s, ReadOnlyMemory<byte> body)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) { _log.LogWarning("C_LOAD_TOPO_FIN without a selected character"); return true; }

        _log.LogInformation("C_LOAD_TOPO_FIN -> spawning '{Name}' at ({X},{Y},{Z}) in zone {Zone}",
            chr.Name, chr.X, chr.Y, chr.Z, chr.Zone);

        if (PureReplay) { SpawnReplay.ReplayPhase2(s, _log); return true; }

        s.SendByDef("S_SPAWN_ME", new Dictionary<string, object>
        {
            ["gameId"] = s.GameId,
            ["loc"] = new float[] { chr.X, chr.Y, chr.Z },
            ["w"] = (short)-3276,  // 0xF334 from capture
            ["alive"] = true,
            ["isLord"] = false,
        });

        InventoryHandlers.SendStats(s);

        // S_CURRENT_CHANNEL from capture: zone 7005, channel 1, density 0, type 1
        s.SendByDef("S_CURRENT_CHANNEL", new Dictionary<string, object>
        {
            ["zone"] = chr.Zone, ["channel"] = 1, ["density"] = 0, ["type"] = 1,
        });

        // Post-spawn state packets from capture
        s.SendByDef("S_USER_STATUS", new Dictionary<string, object>
        {
            ["gameId"] = s.GameId, ["status"] = 0, ["bySkill"] = false,
        });
        s.SendRawBody("S_LOAD_SKILL_SCRIPT_LIST", new byte[] { 0, 0, 0, 0 });
        s.SendRawBody("S_CREST_INFO", new byte[12]);
        s.SendRawBody("S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL", new byte[] { 0x08, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // Real server re-sends chat/UI settings after spawn
        ClientSettingsHandlers.SendUiSetting(s);
        ClientSettingsHandlers.SendChatOption(s);

        return true;
    }
}
