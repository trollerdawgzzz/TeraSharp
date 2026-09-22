# status/ALTA-DIFF.md - T129, the full-payload Alt+A diff

Real: `<captures>\cap_final_gm_client2.log` - the original ArbiterServer.exe, panel OPENS
(`C_ADMIN_REQUEST_CUSTOM_BOOKMARK` at record 524).
Ours: `<captures>\cap_t124.log` - TeraSharp after T124, same D:\Tera 100-class client,
panel does NOT open (zero `C_ADMIN_*` in the whole capture; 25 `C_REQUEST_PVE_RANKING` instead).

Window: every `S->C` frame from `S_LOGIN_ARBITER` (record 7 in both) to the first
`C_ADMIN_*` / `C_REQUEST_PVE_RANKING` - 484 frames real, 382 frames ours.

Method note: `_ctl.txt` truncates every payload at 64 bytes (`reframe-client.ps1 -Bytes`),
so this diff was taken from the raw `.log` records, whose `HEX:` line is the WHOLE frame
(`len` includes the 4-byte `[u16 len][u16 opcode]` header and equals the hex byte count -
checked on all 3418 + 659 records, zero mismatches). Byte offsets below are body-relative.

## Summary (the gate)

 1. Every GM/admin-scoped packet is ALREADY byte-identical: `S_LOGIN_ARBITER` (status 33),
    `S_ADMIN_GM_SKILL`, `S_ADMIN_HOLD_CHARACTER`, and `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL`
    (`08 00 0A 00 00 00 00 00` in both). None of them is the gate.
 2. The client SENDS `C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL` on our stack too - records 132
    and 376 - and gets the identical reply. So Alt+A fires and the client-side GM check passes.
    What fails is the Awesomium view rendering, which is downstream of every packet here.
 3. THE ONE ACCOUNT-SCOPED CONTENT DIFFERENCE LEFT IS THE PORT:
        real   apiServerAddress = 127.0.0.1:8800
        ours   apiServerAddress = 127.0.0.1:8040
    Same def, same refs (22/50/80), same 544 bytes, same `dbServerName`, a valid 231-char
    HS256 token in both. The client builds the panel URL from this field; 8040 was T124's
    assumed default and is NOT what the working session used. Nothing in docs/ARCHITECTURE.md
    serves 8040 either (tera-api is :81 / :8050, ARBITER API :8080). 8800 is the measured value.
 4. `S_VERSION_INFO` (13 B, `0B 00 00 00 00 00 01 00 00`, real record 286 - after C_LOAD_TOPO_FIN,
    before the URL request) is the only non-content S_ opcode we never send. Unproven as a gate,
    but it is the only other candidate; try it second, after the port.
 5. `S_SELECT_USER` in this capture is STILL the pre-T124 shape
    (`01 | 00 00 | 00 00 00 00 00 00 01 01` vs real `01 | 01 00 | 00 x8`), so the running
    binary has T124's `S_LOGIN_ACCOUNT_INFO` change but NOT its `BuildSelectUserFields` fix.
 6. Brief premise corrected: `S_LOGIN` is 421 bytes in BOTH, not 421 vs 433, and every byte it
    differs in is character state - `cid`, `level` (0x46 vs 0x08), `expShown`, `restedCurrent`,
    `chest`, `title`. `S_LOAD_TOPO` differs only in zone id and spawn coords. Both content.
 7. 29 S_ opcodes appear in real and never in ours, 6 the reverse - all world simulation or
    client settings (full lists below), none account- or GM-scoped.

## Next step

Set `TERASHARP_API_GATEWAY=127.0.0.1:8800` and re-capture. If the panel still does not open,
confirm what is actually listening on 8800 on the box (the original stack's admin web service),
point the env var at that, and only then look at `S_VERSION_INFO`.

## Table

`real bytes` / `ours` show the first 16 BODY bytes of the first occurrence, with the count and
frame length. Rows where both sides are byte-identical AND appear the same number of times are
listed once at the end rather than in the table.

### Account / GM scoped

| opcode | real bytes | ours | field | verdict |
|---|---|---|---|---|
| `S_LOGIN_ACCOUNT_INFO` | x1 len 544: 16 00 32 00 50 00 01 00 00 00 00 00 00 00 57 73 ... | x1 len 544: 16 00 32 00 50 00 01 00 00 00 00 00 00 00 4F D8 ... | apiServerAddress + antiCheatChecksumSeed | **ACCOUNT - THE GATE** |
| `S_SELECT_USER` | x1 len 15: 01 01 00 00 00 00 00 00 00 00 00 | x1 len 15: 01 00 00 00 00 00 00 00 00 01 01 | unk2 / unk3 field order | account - T124 fix NOT in the running build |
| `S_VERSION_INFO` | x1 len 13: 0B 00 00 00 00 00 01 00 00 | ABSENT | whole frame absent | build-scoped - unexplained, see note 4 |
| `S_LOGIN_ARBITER` | x1 len 23: 01 00 21 00 00 00 00 00 00 00 06 00 00 00 00 00 ... | identical | status = 33 | GM/account - IDENTICAL, not the gate |
| `S_ADMIN_GM_SKILL` | x1 len 9: 00 00 00 00 01 | identical | on/off | GM - IDENTICAL, not the gate |
| `S_ADMIN_HOLD_CHARACTER` | x1 len 5: 00 | x2, same bytes | hold flag | GM - IDENTICAL, count only |
| `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` | x1 len 12: 08 00 0A 00 00 00 00 00 | x2, same bytes | whole body | GM - IDENTICAL, count only |

### Present in both, bytes or count differ (content)

| opcode | real bytes | ours | field | verdict |
|---|---|---|---|---|
| `S_ABNORMALITY_BEGIN` | x30 len 44: 02 00 F0 0A 00 80 00 00 00 00 00 00 00 00 00 00 ... | x4 len 44: 01 00 F0 0A 00 80 00 00 00 00 00 00 00 00 00 00 ... | - | content |
| `S_ACTIVATE_CARD_COMBINE_LIST_DATA` | x1 len 26: 00 00 00 00 12 00 02 00 F0 0A 00 80 00 00 64 00 ... | x1 len 26: 00 00 00 00 12 00 01 00 F0 0A 00 80 00 00 64 00 ... | - | content |
| `S_AVAILABLE_EVENT_MATCHING_LIST` | x1 len 1793: 09 00 BE 00 03 00 8E 00 03 00 5E 00 3D 00 00 00 ... | x1 len 94: 00 00 00 00 00 00 00 00 00 00 00 00 03 00 00 00 ... | - | content |
| `S_CARD_DATA` | x1 len 82: 01 00 32 00 01 00 3E 00 00 00 00 00 2A 00 02 00 ... | x1 len 62: 00 00 00 00 01 00 32 00 00 00 00 00 2A 00 01 00 ... | - | content |
| `S_CHANGE_CARD_PRESET` | x1 len 8: 00 00 00 00 | x2 len 8: 00 00 00 00 | - | content |
| `S_CHANGE_EP_EXP_DAILY_LIMIT` | x1 len 8: 87 C7 04 00 | x1 len 8: 00 00 00 00 | - | content |
| `S_CLEAR_WORLD_QUEST_VILLAGER_INFO` | x10 len 4: (empty) | x20 len 4: (empty) | - | content |
| `S_COMPLETED_MISSION_INFO` | x1 len 8: 00 00 00 00 | x2 len 8: 00 00 00 00 | - | content |
| `S_CONFIRM_INVITE_CODE_BUTTON` | x1 len 17: 0F 00 01 E2 4E B7 6A 00 00 00 00 00 00 | x1 len 17: 0F 00 01 12 AF BB 6A 00 00 00 00 00 00 | - | content |
| `S_CREST_INFO` | x1 len 394: 2A 00 10 00 3C 00 00 00 00 00 00 00 10 00 19 00 ... | x2 len 16: 00 00 00 00 00 00 00 00 00 00 00 00 | - | content |
| `S_CURRENT_CHANNEL` | x2 len 20: 05 00 00 00 01 00 00 00 00 00 00 00 01 00 00 00 | x2 len 20: 5D 1B 00 00 01 00 00 00 00 00 00 00 01 00 00 00 | - | content |
| `S_DAILY_QUEST_COMPLETE_COUNT` | x1 len 9: 00 00 1E 00 00 | x2 len 9: 00 00 1E 00 00 | - | content |
| `S_DUNGEON_UI_HIGHLIGHT` | x1 len 26: 02 00 08 00 08 00 11 00 01 00 00 00 00 11 00 00 ... | x1 len 26: 02 00 08 00 08 00 11 00 01 00 00 00 00 11 00 00 ... | - | content |
| `S_ENABLE_DISABLE_SELLABLE_ITEM_LIST` | x2 len 43: 00 00 00 00 03 00 13 00 00 00 00 00 01 01 01 13 ... | x1 len 43: 00 00 00 00 03 00 13 00 00 00 00 00 01 01 01 13 ... | - | content |
| `S_FATIGABILITY_POINT` | x2 len 16: 01 00 00 00 A0 0F 00 00 A0 0F 00 00 | x1 len 16: 01 00 00 00 A0 0F 00 00 A0 0F 00 00 | - | content |
| `S_FIELD_POINT_REWARD_INFO_LIST` | x1 len 76: 01 00 08 00 08 00 00 00 02 00 14 00 28 00 00 00 ... | x1 len 48: 01 00 08 00 08 00 00 00 01 00 14 00 28 00 00 00 ... | - | content |
| `S_FRIEND_GROUP_LIST` | x1 len 24: 01 00 08 00 08 00 00 00 12 00 02 00 00 00 7D 59 ... | x1 len 34: 01 00 08 00 08 00 00 00 12 00 02 00 00 00 46 00 ... | - | content |
| `S_FRIEND_LIST` | x1 len 32: 00 00 00 00 0A 00 CA 4E 29 59 5F 4E 2F 66 09 61 ... | x1 len 12: 00 00 00 00 0A 00 00 00 | - | content |
| `S_GET_USER_LIST` | x1 len 1181: 02 00 23 00 01 00 00 00 00 03 00 00 00 01 00 00 ... | x1 len 2341: 04 00 23 00 00 00 00 00 00 08 00 00 00 01 00 00 ... | - | content |
| `S_GUILD_PERK_LIST` | x2 len 92: 00 00 00 00 4C 00 54 00 03 00 00 00 01 00 00 00 ... | x1 len 80: 00 00 00 00 4C 00 4E 00 03 00 00 00 01 00 00 00 ... | - | content |
| `S_INGAMESHOP_PRODUCT_DATA` | x106 len 244: 2C 00 68 00 96 00 A4 00 B2 00 B4 00 B6 00 B8 00 ... | x106 len 244: 2C 00 68 00 96 00 A4 00 B2 00 B4 00 B6 00 B8 00 ... | - | content |
| `S_INVEN_USERDATA` | x2 len 44: 02 00 F0 0A 00 80 00 00 00 00 80 3F EB 5C F9 3F ... | x2 len 44: 01 00 F0 0A 00 80 00 00 00 00 80 3F EB 5C F9 3F ... | - | content |
| `S_ITEMLIST` | x4 len 885: 04 00 31 00 02 00 F0 0A 00 80 00 00 0E 00 00 00 ... | x4 len 885: 04 00 31 00 01 00 F0 0A 00 80 00 00 0E 00 00 00 ... | - | content |
| `S_ITEM_CUSTOM_STRING` | x2 len 16: 00 00 00 00 02 00 F0 0A 00 80 00 00 | x1 len 16: 00 00 00 00 01 00 F0 0A 00 80 00 00 | - | content |
| `S_LOAD_ACHIEVEMENT_LIST` | x1 len 436: 19 00 24 00 00 00 00 00 02 00 F0 0A 00 80 00 00 ... | x1 len 36: 00 00 00 00 00 00 00 00 01 00 F0 0A 00 80 00 00 ... | - | content |
| `S_LOAD_CLIENT_ACCOUNT_SETTING` | x2 len 640: 08 00 78 02 08 97 56 12 04 32 23 43 50 52 38 08 ... | x2 len 8: 08 00 00 00 | - | content |
| `S_LOAD_CLIENT_USER_SETTING` | x1 len 1190: 08 00 9E 04 08 97 56 12 04 32 23 43 50 52 AF 01 ... | x2 len 1051: 08 00 13 04 08 97 56 12 04 32 23 43 50 52 7F 08 ... | - | content |
| `S_LOAD_EP_INFO` | x1 len 52: 00 00 00 00 0A 01 00 00 9C CA 16 00 00 00 00 00 ... | x1 len 52: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ... | - | content |
| `S_LOAD_HINT` | x1 len 308: 19 00 08 00 08 00 14 00 57 02 00 00 7C 00 00 00 ... | x1 len 8: 00 00 00 00 | - | content |
| `S_LOAD_TOPO` | x1 len 21: 05 00 00 00 D3 50 83 46 A3 0A 9D 44 00 50 8A C5 ... | x1 len 21: 5D 1B 00 00 BE 96 36 45 29 C6 11 46 00 C0 E9 44 ... | zone id + spawn coords | content |
| `S_LOGIN` | x1 len 421: 00 00 00 00 3D 01 45 01 20 00 65 01 40 00 05 2B ... | x1 len 421: 00 00 00 00 3D 01 45 01 20 00 65 01 40 00 05 2B ... | character state: cid, level, expShown, restedCurrent, chest, title | content |
| `S_NPCGUILD_LIST` | x1 len 304: 0C 00 10 00 02 00 F0 0A 00 80 00 00 10 00 28 00 ... | x1 len 40: 01 00 10 00 01 00 F0 0A 00 80 00 00 10 00 00 00 ... | - | content |
| `S_NPC_LOCATION` | x30 len 44: FD 9E 00 00 00 80 0C 00 B1 43 87 46 3E 4E F1 44 ... | x2 len 44: 57 0C 01 00 00 80 0C 00 75 62 4F 45 67 0F 20 46 ... | - | content |
| `S_PLAYER_STAT_UPDATE` | x4 len 351: C4 4F 01 00 00 00 00 00 1C 05 00 00 00 00 00 00 ... | x4 len 351: 06 0B 00 00 00 00 00 00 78 01 00 00 00 00 00 00 ... | - | content |
| `S_QUEST_BALLOON` | x1 len 86: 0E 00 0B B7 00 00 00 80 0C 00 40 00 6D 00 6F 00 ... | x4 len 90: 0E 00 B1 0B 01 00 00 80 0C 00 40 00 6D 00 6F 00 ... | - | content |
| `S_QUEST_INFO` | x15 len 101: 01 00 0E 00 0C 00 00 01 00 00 0E 00 00 00 01 00 ... | x2 len 101: 01 00 0E 00 0C 00 00 01 00 00 0E 00 00 00 01 00 ... | - | content |
| `S_QUEST_VILLAGER_INFO` | x12 len 17: D2 9C 00 00 00 80 0C 00 00 00 00 00 01 | x28 len 17: 74 0B 01 00 00 80 0C 00 00 00 00 00 01 | - | content |
| `S_REPLY_CLIENT_CHAT_OPTION_SETTING` | x2 len 959: 05 00 0C 00 00 00 00 00 0C 00 FD 00 14 00 5D 00 ... | x3 len 959: 05 00 0C 00 00 00 00 00 0C 00 FD 00 14 00 5D 00 ... | - | content |
| `S_REPLY_CLIENT_UI_SETTING` | x2 len 298: 07 00 08 00 08 00 2C 00 1A 00 01 00 00 00 DF CF ... | x3 len 116: 03 00 08 00 08 00 2A 00 1A 00 01 00 00 00 F0 67 ... | - | content |
| `S_RP_SKILL_POLISHING_EXP_INFO` | x1 len 40: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ... | x1 len 40: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ... | - | content |
| `S_SEND_USER_PLAY_TIME` | x1 len 16: DB 05 00 00 C3 6C AB 6A 00 00 00 00 | x1 len 16: 45 07 00 00 C7 D1 AF 6A 00 00 00 00 | - | content |
| `S_SERVER_TIME` | x1 len 12: C5 6C AB 6A 00 00 00 00 | x1 len 12: C9 D1 AF 6A 00 00 00 00 | - | content |
| `S_SKILL_CATEGORY` | x3 len 9: CB 32 00 00 00 | x1 len 9: D1 32 00 00 00 | - | content |
| `S_SKILL_LIST` | x1 len 775: 3B 00 08 00 08 00 15 00 23 2C 00 00 00 00 00 00 ... | x1 len 463: 23 00 08 00 08 00 15 00 3B 28 00 00 00 00 00 00 ... | - | content |
| `S_SKILL_PERIOD` | x1 len 16: 00 00 00 00 02 00 F0 0A 00 80 00 00 | x1 len 16: 00 00 00 00 01 00 F0 0A 00 80 00 00 | - | content |
| `S_SOCIAL` | x5 len 21: C7 9E 00 00 00 80 0C 00 02 00 00 00 00 00 00 00 ... | x8 len 21: 4F 16 01 00 00 80 0C 00 02 00 00 00 00 00 00 00 ... | - | content |
| `S_SPAWN_ME` | x1 len 28: 02 00 F0 0A 00 80 00 00 D3 50 83 46 A3 0A 9D 44 ... | x1 len 28: 01 00 F0 0A 00 80 00 00 BE 96 36 45 29 C6 11 46 ... | - | content |
| `S_SPAWN_NPC` | x30 len 157: 00 00 00 00 00 00 00 00 87 00 C2 9E 00 00 00 80 ... | x28 len 149: 00 00 00 00 00 00 00 00 87 00 74 0B 01 00 00 80 ... | - | content |
| `S_SPAWN_WORKOBJECT` | x6 len 38: 26 82 00 00 00 80 15 00 91 3F 01 00 00 80 89 46 ... | x1 len 38: 04 97 00 00 00 80 15 00 DD 25 03 00 67 7F 8B 45 ... | - | content |
| `S_UPDATE_ACHIEVEMENT_PROGRESS` | x3 len 65468: 40 06 08 00 08 00 28 00 01 00 14 00 06 00 00 00 ... | x3 len 65468: 40 06 08 00 08 00 28 00 01 00 14 00 06 00 00 00 ... | - | content |
| `S_UPDATE_EVENT_SYSTEM` | x3 len 8: 00 00 00 00 | x4 len 8: 00 00 00 00 | - | content |
| `S_USER_EXTERNAL_CHANGE` | x2 len 242: 02 00 F0 0A 00 80 00 00 AD E6 00 00 9C 3A 00 00 ... | x2 len 242: 01 00 F0 0A 00 80 00 00 AD E6 00 00 9C 3A 00 00 ... | - | content |
| `S_USER_ITEM_EQUIP_CHANGER` | x1 len 292: 17 00 10 00 02 00 F0 0A 00 80 00 00 10 00 1C 00 ... | x1 len 292: 17 00 10 00 01 00 F0 0A 00 80 00 00 10 00 1C 00 ... | - | content |
| `S_USER_STATUS` | x3 len 17: 02 00 F0 0A 00 80 00 00 00 00 00 00 00 | x1 len 17: 01 00 F0 0A 00 80 00 00 00 00 00 00 00 | - | content |
| `S_VISITED_SECTION_LIST` | x1 len 40: 02 00 08 00 08 00 18 00 01 00 00 00 01 00 00 00 ... | x1 len 24: 01 00 08 00 08 00 00 00 01 00 00 00 01 00 00 00 ... | - | content |
| `S_VISIT_NEW_SECTION` | x1 len 17: 00 01 00 00 00 19 00 00 00 D9 23 09 00 | x1 len 17: 00 01 00 00 00 01 00 00 00 01 00 00 00 | - | content |
| `S_WORLD_QUEST_VILLAGER_INFO` | x10 len 16168: F9 01 08 00 08 00 28 00 15 00 00 00 FA 03 00 00 ... | x20 len 72: 02 00 08 00 08 00 28 00 09 00 00 00 E9 03 00 00 ... | - | content |

### In real, absent in ours (29)

| opcode | real bytes | ours | field | verdict |
|---|---|---|---|---|
| `S_ABNORMALITY_END` | x11 len 16: 02 00 F0 0A 00 80 00 00 E0 EC E6 05 | ABSENT | - | content |
| `S_ACTION_END` | x7 len 46: 01 00 F0 0A 00 80 00 00 3E F5 81 46 39 13 9A 44 ... | ABSENT | - | content |
| `S_ACTION_STAGE` | x6 len 83: 00 00 00 00 01 00 F0 0A 00 80 00 00 48 F7 81 46 ... | ABSENT | - | content |
| `S_ATTENDANCE_EVENT_REWARD_COUNT` | x1 len 8: 00 00 00 00 | ABSENT | - | content |
| `S_CREATURE_ROTATE` | x6 len 18: 8E B7 00 00 00 80 0C 00 8D C3 00 00 00 00 | ABSENT | - | content |
| `S_DESPAWN_NPC` | x1 len 32: D6 9E 00 00 00 80 0C 00 BF E3 80 46 78 12 A7 44 ... | ABSENT | - | content |
| `S_EACH_SKILL_RESULT` | x1 len 119: 00 00 00 00 01 00 F0 0A 00 80 00 00 00 00 00 00 ... | ABSENT | - | content |
| `S_EP_SYSTEM_DAILY_EVENT_EXP_ON_OFF` | x1 len 8: 00 00 00 00 | ABSENT | - | content |
| `S_EVENT_QUEST_SUMMARY` | x9 len 30: 01 00 16 00 A0 08 00 00 01 00 00 00 01 00 00 00 ... | ABSENT | - | content |
| `S_FIELD_POINT_INFO` | x1 len 20: 00 00 00 00 A0 86 01 00 FF FF FF FF 00 00 00 00 | ABSENT | - | content |
| `S_GET_USER_GUILD_LOGO` | x2 len 16: 10 00 00 00 02 00 00 00 02 00 00 00 | ABSENT | - | content |
| `S_GUILD_APPLY_COUNT` | x1 len 8: 00 00 00 00 | ABSENT | - | content |
| `S_GUILD_INFO` | x1 len 270: 02 00 C8 00 98 00 A0 00 A8 00 AA 00 C4 00 C6 00 ... | ABSENT | - | content |
| `S_GUILD_MEMBER_LIST` | x1 len 253: 02 00 57 00 47 00 4F 00 03 00 00 00 01 00 00 00 ... | ABSENT | - | content |
| `S_GUILD_NAME` | x1 len 58: 14 00 1C 00 36 00 38 00 02 00 F0 0A 00 80 00 00 ... | ABSENT | - | content |
| `S_GUILD_QUEST_LIST` | x1 len 1154: 05 00 8B 00 03 00 61 00 51 00 59 00 03 00 00 00 ... | ABSENT | - | content |
| `S_HUNTING_ZONE_EVENT_LIST` | x1 len 8: 00 00 00 00 | ABSENT | - | content |
| `S_LOAD_SKILL_SCRIPT_LIST` | x1 len 8: 00 00 00 00 | ABSENT | - | content |
| `S_NPC_AI_EVENT` | x3 len 16: 8E B7 00 00 00 80 0C 00 04 00 00 00 | ABSENT | - | content |
| `S_NPC_OCCUPIER_INFO` | x1 len 28: D6 9E 00 00 00 80 0C 00 00 00 00 00 00 00 00 00 ... | ABSENT | - | content |
| `S_NPC_STATUS` | x10 len 33: 8E B7 00 00 00 80 0C 00 00 D3 C7 FD FF 05 00 00 ... | ABSENT | - | content |
| `S_REQUEST_INVITE_GUILD_TAG` | x1 len 8: 00 00 00 00 | ABSENT | - | content |
| `S_SIMPLE_TIP_REPEAT_CHECK` | x5 len 9: 01 00 00 00 01 | ABSENT | - | content |
| `S_SPAWN_USER` | x1 len 547: 00 00 00 00 02 00 13 02 87 01 8F 01 97 01 B1 01 ... | ABSENT | - | content |
| `S_SYSTEM_MESSAGE` | x3 len 16: 06 00 40 00 38 00 38 00 38 00 00 00 | ABSENT | - | content |
| `S_TRADE_BROKER_CALC_NOTIFY` | x1 len 12: 00 00 00 00 00 00 00 00 | ABSENT | - | content |
| `S_USER_LOCATION` | x2 len 47: 01 00 F0 0A 00 80 00 00 4C 6D 82 46 B0 82 93 44 ... | ABSENT | - | content |
| `S_VIEW_PARTY_INVITE` | x1 len 34: 00 00 00 00 01 00 0C 00 0C 00 00 00 1A 00 0C 00 ... | ABSENT | - | content |

### In ours, absent in real (6)

| opcode | real bytes | ours | field | verdict |
|---|---|---|---|---|
| `S_PLAYER_CHANGE_MP` | ABSENT | x3 len 36: 80 01 00 00 1C 05 00 00 08 00 00 00 00 00 00 00 ... | - | content |
| `S_RESULT_SEREN_GUIDE` | ABSENT | x1 len 20: 01 00 0C 00 04 00 00 00 0C 00 00 00 26 00 00 00 | - | content |
| `S_SAVE_CLIENT_CHAT_OPTION_SETTING` | ABSENT | x1 len 5: 01 | - | content |
| `S_SAVE_CLIENT_UI_SETTING` | ABSENT | x2 len 5: 01 | - | content |
| `S_SHOW_NPC_TO_MAP` | ABSENT | x1 len 8: 00 00 00 00 | - | content |
| `S_UPDATE_EVENT_MATCHING_BONUS_INFO` | ABSENT | x2 len 23: 00 00 00 00 01 00 01 00 00 00 00 00 00 00 00 00 ... | - | content |

### Byte-identical and same count (39) - no row needed

`S_ACCOUNT_BENEFIT_LIST`, `S_ACCOUNT_PACKAGE_LIST`, `S_ADMIN_GM_SKILL`, `S_ARTISAN_RECIPE_LIST`, `S_ARTISAN_SKILL_LIST`, `S_AVAILABLE_SOCIAL_LIST`, `S_BROCAST_GUILD_FLAG`, `S_CHANGE_POCKET_NAME`, `S_CLEAR_QUEST_INFO`, `S_CURRENT_ELECTION_STATE`, `S_DECO_UI_INFO`, `S_F2P_PremiumUser_Permission`, `S_FESTIVAL_LIST`, `S_GMEVENT_OFF_GUIDE_MESSAGE`, `S_GUARD_PK_POLICY`, `S_INGAMESHOP_CATEGORY_BEGIN`, `S_INGAMESHOP_CATEGORY_DATA`, `S_INGAMESHOP_CATEGORY_END`, `S_INGAMESHOP_PRODUCT_BEGIN`, `S_INGAMESHOP_PRODUCT_END`, `S_LOGIN_ARBITER`, `S_MOVE_DISTANCE_DELTA`, `S_MY_DESCRIPTION`, `S_PARCEL_READ_RECV_STATUS`, `S_PET_INCUBATOR_INFO_CHANGE`, `S_PLAYER_CHANGE_ALL_PROF`, `S_PVE_LEADER_BOARD_INFO`, `S_PVP_LEADER_BOARD_INFO`, `S_REQUEST_SERVANT_INFO_LIST`, `S_RESPONSE_SERVANT_ADVENTURE_LIST`, `S_RP_SKILL_POLISHING_LIST`, `S_START_COOLTIME_SERVANT_SKILL`, `S_TOTAL_GUILD_WAR_DATA`, `S_TRADE_BROKER_HIGHEST_ITEM_LEVEL`, `S_UPDATE_CONTENTS_ON_OFF`, `S_UPDATE_FRIEND_INFO`, `S_USER_BLOCK_LIST`, `S_VIRTUAL_LATENCY`, `S_WEAK_POINT`

## T131 - the two candidates, resolved

### 1. S_VERSION_INFO: built, tested, needs ONE registry line

`S_VERSION_INFO.1.def` = `int32 revision / string description / byte display`. Decoded from
cap_final_gm_client2 record 286 (`0D 00 67 B7 0B 00 00 00 00 00 01 00 00`):

```
body  0  u16  ref description = 11   (packet-relative -> body index 7)
      2  i32  revision        = 0    NOT 376056; S_SERVER_BUILD_INFO carries the build revision
      6  byte display         = 1
      7       description     = ""   (the bare UTF-16 terminator)
```

2 + 4 + 1 + 2 = 9 body bytes, + 4 header = 13. The def and the capture agree exactly, and the
test asserts the builder against BOTH.

`ArbiterClientHandlers.BuildVersionInfo(int revision = 0, bool display = true)` returns that
frame. Its position is fixed across both captured logins - after S_LOAD_CLIENT_USER_SETTING,
immediately before S_PARCEL_READ_RECV_STATUS - so the send site is the C_LOAD_TOPO_FIN burst in
`Handlers/HandlerRegistry.cs`. That file is human-owned, so THE LINE IS NOT APPLIED. Add it:

```
                ClientSettingsHandlers.SendUserSetting(s);
                s.Send(ArbiterClientHandlers.BuildVersionInfo());   // T131: cap frame 286, before the parcel status
                ParcelHandlers.SendReadRecvStatus(s);        // T42: 13-byte S_PARCEL_READ_RECV_STATUS (cap frame 312)
```

i.e. one new line between HandlerRegistry.cs:102 and :103 as they stand today.

### 2. S_SELECT_USER: the source is correct; the capture predates the build

Both trees already call it - `Handlers/LoginHandlers.cs:180`
`s.SendByDef("S_SELECT_USER", ArbiterClientHandlers.BuildSelectUserFields());` (and :173 for the
refusal) - and `ArbiterClientHandlers.BuildSelectUserFields` returns `unk1 = 1, unk2 = (ushort)1,
unk3 = 0UL`, which is the capture's `01 | 01 00 | 00 x8`. Nothing to fix in source.

Why cap_t124 still shows the old shape: master's `ArbiterClientHandlers.cs` was last written at
1789913259597 and `src\TeraSharp.Arbiter\bin\Release\net8.0\TeraSharp.Arbiter.dll` was built
at 1789906810382 - the build is 6449 s OLDER than the file. cap_t124.log (1789907491945) came
from that older binary. Rebuild and re-capture; there is no code change to make.

### 3. The port: 8800 is SERVER config, not a client constant

`Executable\DeploymentConfig.xml`, the original ArbiterServer's own config:

```
<APIServer ip="127.0.0.1" port="8800" protocol="http" ttl="120" />
```

and `DeploymentConfig.xml.orig` - the untouched vendor file, whose shop URLs still point at
`172.16.200.119` before this box localised them - carries the SAME 8800. So 8800 was never
localised: it is the vendor default for the Arbiter's APIServer element, which the Arbiter copies
into `apiServerAddress`. `ttl="120"` is the clincher - it is exactly the `exp = iat + 120` in the
T123/T124 token. This one XML element is the source of the whole Alt+A field set.

`arb_gw\config_arb_gw.txt` has no 8800 at all (it is `rest_url=http://127.0.0.1:8080/api`,
`web_shop_url=http://127.0.0.1:81/...`), so the gateway the client is pointed at is NOT arb_gw.

Conclusion: **the client uses whatever `apiServerAddress` the server sends - it does not hardcode
a port.** 8040 is not wrong because it is 8040; it is wrong because nothing serves it. Setting
`TERASHARP_API_GATEWAY=127.0.0.1:8800` only helps if something is listening there, and on this box
nothing is (the original ArbiterServer is not running). The real work is standing up the admin-tool
HTTP endpoint and pointing the env var at it. Matching 8800 is still worth doing - it is what the
client was built against and costs nothing.

NOT CHECKED: `D:\Tera 100\S1Game\Config` and the client exe. That folder is not connected to this
session and a folder-access request for it was refused, so the client-side half of the question is
answered from the server config only. To confirm against the exe, connect `D:\Tera 100`.

## T132 - serving apiServerAddress: what the research found, and what was built

### What the client asks for: STILL UNKNOWN, and here is every place it is not

The Alt+A URL is built IN THE CLIENT from `apiServerAddress` + `apiServerAuthToken`. Four sources
were checked and none of them carries the path:

| Source | What is actually there |
|---|---|
| `ArbiterServer.exe.c` (59 MB, grepped) | No admin-tool path. Its ONLY URL is `L"http://%s/Default.aspx?v=%s"` at line 1440066, filled with the hardcoded a hardcoded retail address and `"Live-100.02 TW #9 (Gold)"` and fired through `InternetOpenUrlW` as `ArbiterServer` - a retail phone-home at startup, unrelated to Alt+A. (Worth blocking before going public.) |
| `WebApp\ContentsControl\Awesomium\*` | A DIFFERENT feature. `AwesomiumUrlControl.aspx` is a GM page managing a per-server (Title, Url) list - the in-game web panel. All logic is compiled into `WebApp\bin\*.dll`; the .aspx files are markup only. |
| `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` | Serves that same list. Body in the working capture is `08 00 0A 00 00 00 00 00` = TWO string refs at packet 8 and 10, BOTH EMPTY. The panel opened with an empty admin-tool URL, which is the proof the URL does not come from this packet. (The shipped def declares only `string title` - it under-declares the second string.) |
| `tera-api` | `API_GATEWAY_LISTEN_PORT=8040`, and its own `.env.example` calls it the API "for receiving connections from the external website (like billing)". Not an admin tool. |

The one source that would answer it - the client install, `D:\Tera 100\S1Game\Config` and the exe -
is not connected to this session and a folder-access request for it was refused (T131). **So 8040
was wrong twice over: wrong port AND wrong service.**

### What was built: a probe, not the finished tool

`Web/ApiGatewayServer.cs` serves EVERY path at `TERASHARP_API_GATEWAY`'s port, logs the request in
full - method, path, query, every header, cookies, user-agent - reports whether a token arrived and
whether it verifies against `TERASHARP_API_JWT_SECRET`, and returns a minimal no-script HTML page
so an Awesomium shell has a document to load. One Alt+A press turns the unknown above into a
measured fact, and T133 can then serve the real page at the path the log names.

It reads the token from all three carriers because we do not yet know which the client uses:
`Authorization: Bearer`, then the query (`token`, `authKey`, `apiServerAuthToken`, `jwt`, ... ),
then `Cookie`. Whichever one fires is what the log reports.

```
  TERASHARP_API_GATEWAY_SERVE=1              required - off by default, because it binds a port
  TERASHARP_API_GATEWAY=<reachable ip>:8800  the SAME value the client is told
  TERASHARP_API_GATEWAY_BIND=0.0.0.0         only when the client is not on this box
  TERASHARP_API_JWT_SECRET=<key>             so the log can say VERIFIED rather than just PRESENT
```

Unlike `AdminServer` (127.0.0.1 only, always) this can bind beyond loopback - the client that has
to reach it is usually another machine - which is exactly why it is opt-in. A `+` / `0.0.0.0`
prefix needs `netsh http add urlacl url=http://+:8800/ user=%USERNAME%` or an elevated process;
the bind failure is logged with that command, never thrown.

### The default port moved: 8040 -> 8800

`ApiGatewayToken.DefaultAddress` is now `127.0.0.1:8800`, matching `DeploymentConfig.xml`'s
`<APIServer ip=127.0.0.1 port=8800 protocol=http ttl=120 />`. T124's two `Address()` assertions
that expected 8040 were updated with it.

### NOT APPLIED - one wiring line, `Program.cs` is human-owned

Beside the existing `AdminServer.TryStart(...)`:

```
        var apiGateway = ApiGatewayServer.TryStart(log);   // T132: the listener at apiServerAddress
```

and dispose it with the other servers. When `TERASHARP_API_GATEWAY_SERVE` is unset it logs one
line saying nothing is serving the address and returns null, so adding it is safe either way.

### What to do next

1. Add the wiring line, set the four env vars, restart.
2. Press Alt+A on a GM account and read the `api-gateway probe:` log line.
3. That line is T133's specification: the path, the query, and where the token rides.


---

## T143 - the replay bisection, and why it cannot find a packet

**Asked for:** replay `cap_final_gm_client2`'s S->C stream to a client, press Alt+A, and if it
opens, bisect prefixes to the single packet whose presence flips the panel.

**Result: there is no such packet, and the two captures already on disk prove it.** No replay
was run - this was settled by reading `cap_final_gm_client2.log` (real, panel OPENS) against
`cap_t124.log` (ours, panel does NOT open) directly.

### The proof

The client's Alt+A handler is GM-gated on the client side, and the first thing it does past
that gate is ask the server for the admin-tool URL. Both stacks get that far:

| | real | ours |
|---|---|---|
| client asks `C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL` | record 405 | record **132** (and again at 376) |
| server answers `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` | 412 | 133 (and 377) |
| the answer, whole frame | `0C 00 CE F2 08 00 0A 00 00 00 00 00` | **byte-identical** |
| panel populates (`C_ADMIN_REQUEST_CUSTOM_BOOKMARK`) | 524 | never - zero `C_ADMIN_*` in 659 records |

So **Alt+A already fires on TeraSharp and our answer is already correct byte-for-byte.** Whatever
stops the panel happens after that exchange, in the client.

**The reply carries no URL.** Its body is `08 00 | 0A 00 00 00 00 00` - a packet-relative string
ref pointing at offset 8, where the two bytes are `00 00`, i.e. the empty string. The real server
tells the real client "your admin-tool URL is empty" and the real client opens the panel anyway.
The page the Awesomium view loads is therefore built client-side, from `apiServerAddress` in
`S_LOGIN_ACCOUNT_INFO` - which is section 3's measured difference, 127.0.0.1:**8800** real
against 127.0.0.1:**8040** ours.

**The 111 records between the identical answer and the panel populating are world simulation.**
Every S->C opcode in real's 412..524 window: `S_NPC_LOCATION` x25, `S_ABNORMALITY_BEGIN` x12,
`S_NPC_STATUS` x10, `S_ABNORMALITY_END` x9, `S_CREATURE_ROTATE` x6, `S_ACTION_END` x6,
`S_ACTION_STAGE` x5, `S_SIMPLE_TIP_REPEAT_CHECK` x5, `S_SOCIAL` x5, `S_NPC_AI_EVENT` x3, and
fourteen more of the same kind. Not one is account-, GM- or admin-scoped.

**Across the whole login-to-panel window** (`S_LOGIN_ARBITER` record 7 to the first `C_ADMIN_*`)
real sends 129 distinct S->C opcodes and we send 107. Of the 28 real-only ones, 27 are guild,
party, event or world content (`S_GUILD_INFO`, `S_SPAWN_USER`, `S_EACH_SKILL_RESULT`, ...). The
twenty-eighth is `S_VERSION_INFO`, already named in section 4 - and it cannot be the Alt+A gate,
because it arrives at real record 286 while our client fires Alt+A at record 132 having never
received it.

### Why a prefix bisection was the wrong instrument

A prefix bisection finds a packet whose **presence** flips a behaviour. The difference here is
not presence: both sides send the same GM packets, and the one packet that matters
(`S_LOGIN_ACCOUNT_INFO`) is present in both and differs only in **content** - a port number
inside a 544-byte body. Halving the stream would never isolate it; it would either be in every
prefix or in none.

A full replay of `cap_final_gm_client2` would in fact carry the real `apiServerAddress` of
:8800, so if something answers on 8800 on that box the panel would likely open - and the
bisection would then converge on `S_LOGIN_ACCOUNT_INFO` and blame the wrong thing, its presence
rather than one field in it.

### Conclusion, and the correction to the brief's second branch

The gate is outside the Arbiter->client stream, but it is **not** a hub or launcher GM flag. The
GM flag demonstrably works: `S_LOGIN_ARBITER` status 33, `S_ADMIN_GM_SKILL` at record 72, and
above all the client voluntarily emitting the GM-gated `C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL`
all say the client considers this account a GM. The gate is the **Awesomium view's HTTP fetch of
the admin page at `apiServerAddress`**, which on our stack points at a port where nothing is
listening.

### The cheap decisive experiment (replaces the bisection)

`Program.cs` now has T132's wiring line, so:

1. `TERASHARP_API_GATEWAY=127.0.0.1:8800`, `TERASHARP_API_GATEWAY_SERVE=1`, and
   `TERASHARP_API_JWT_SECRET` set to tera-api's `API_PORTAL_SECRET`.
2. Restart the Arbiter, log in on a GM account, press Alt+A.
3. Read the `api-gateway probe:` log line. It prints the path, the query and where the token
   rides - which is the specification for the real handler, and is the thing no capture in this
   project contains.

If no probe line appears, the client is not fetching at all and `S_VERSION_INFO` becomes worth
sending. If one appears, the panel's requirement is an HTTP response, and no packet change can
substitute for it.

---

## T144b - Alt+A, full-payload diff: the gate is in S_SELECT_USER

Captures: `cap_altA_gm.log` (ours, T144 build, 851 records) against `cap_final_gm_client2.log`
(real, 3418 records, panel opens at 524), with `cap_final_client2.log` (real, ordinary account)
and `cap_final_gm_client.log` (real, **same GM account, different character**) as controls.
Windows aligned on the post-spawn Alt+A: real 2..412, ours 2..380.

### The control that settles it

`cap_final_gm_client` is the same GM account, same `S_LOGIN_ARBITER` status 33, same
`S_ADMIN_GM_SKILL`, Alt+A pressed at record 323 - and no panel. It selected character 1003,
whose `adminLevel` is 0. So the gate is neither the account, nor the status byte, nor the
GM-skill packet, nor the URL response - it is per-character, and it is set at character select.

| capture | acct status | S_ADMIN_GM_SKILL | S_GET_USER_LIST adminLevel | S_SELECT_USER body | panel |
|---|---|---|---|---|---|
| cap_final_gm_client2 (real) | 33 | sent | **1** | `01 01000000 00000000 00 00` | **OPENS** |
| cap_final_gm_client (real) | 33 | sent | 0 | `01 00000000 00000000 00 00` | no |
| cap_final_client2 (real) | 31 | not sent | 0 | `01 00000000 00000000 00 00` | no |
| cap_altA_gm (**ours**) | 33 | sent | **5** | `01 00000000 00000000 01 01` | no |

### S_SELECT_USER is mis-typed in the shipped def

The def says `byte unk1 / uint16 unk2 / uint64 unk3`. The writer says otherwise. Its PDL
signature is `PKT_S_SELECT_USER_WRITE, bool, int, enum SelectUserErrorCode, bool, bool`, and the
two emit helpers fix the widths - `FUN_140351320` advances the cursor by 1, `FUN_1400554e0` by 4:

| body | type | name | source |
|---|---|---|---|
| 0 | bool | accepted | literal `1` on the success path |
| 1 | **int32** | **adminLevel** | `*(int *)(User + 0x3B98)` |
| 5 | int32 | SelectUserErrorCode | literal `0` on the success path |
| 9 | bool | isFirstLoginToday | `User::IsFirstLoginToday()` |
| 10 | bool | isFirstLoginTodayByAccount | `User::IsFirstLoginTodayByAccount()` |

1 + 4 + 4 + 1 + 1 = 11 = the captured body. `User+0x3B98` is written by
`User::UpdateAdminLevel(int)` (persisted through `dbo.spUpdateUserAdminLevel`) and is the same
field `S_GET_USER_LIST.adminLevel` reads, which is why the two frames always agree on the real
server and disagreed on ours.

### The defect

`WorldEntry.EnterWorld` - the path taken whenever World is connected, i.e. every live login -
still carried the pre-T123 literal `unk2 = 0, unk3 = 72339069014638592UL`. That writes
`adminLevel = 0` and sets **both** first-login bools. T123 fixed only
`ArbiterClientHandlers.BuildSelectUserFields`, which the standalone path uses; the World path
was never updated, so the fix never reached a live session. Both call sites now go through the
helper and pass `GmCommandHandlers.LevelOf`.

### Everything else in the window

| opcode | field | real | ours | verdict |
|---|---|---|---|---|
| S_CHECK_VERSION, S_LOADING_SCREEN_CONTROL_INFO, S_REMAIN_PLAY_TIME | whole frame | - | identical | - |
| S_LOGIN_ARBITER | status | 33 | 33 | identical (ordinary account: 31) |
| S_LOGIN_ACCOUNT_INFO | accountId, dbServerName | 1, PlanetDB_2800 | same | - |
| S_LOGIN_ACCOUNT_INFO | antiCheatChecksumSeed, apiServerAuthToken | 619351, JWT | 941901, JWT | session nonce |
| S_LOGIN_ACCOUNT_INFO | apiServerAddress | 127.0.0.1:8800 | 203.0.113.10:8800 | deploy config, not a gate - see below |
| S_GET_USER_LIST | veteran, maxCharacters | 1, 3 | 0, 8 | account state |
| S_GET_USER_LIST | adminLevel (elem+142) | 1 | 5 | both > 0; T144 works |
| S_GET_USER_LIST | level, hp, guildName, achievementPoints, guildLogoId | - | - | character state |
| **S_SELECT_USER** | **body 1 adminLevel** | **1** | **0** | **the gate** |
| **S_SELECT_USER** | **body 9/10 first-login bools** | **0, 0** | **1, 1** | **same defect** |
| S_ADMIN_GM_SKILL | whole frame | `09 00 BE 64 00 00 00 00 01` | identical | - |
| S_ADMIN_HOLD_CHARACTER | whole frame | `05 00 0E A3 00` | identical | - |
| S_CURRENT_CHANNEL | whole frame | `14 00 23 6D ...` | identical | - |
| S_VERSION_INFO | revision, description, display | 0, "", 1 | identical | T131 shipped |
| S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL | whole frame | `0C 00 CE F2 08 00 0A 00 00 00 00 00` | identical | identical on the NON-GM session too |
| S_USER_STATUS | gameId | ...F00002 | ...F00001 | entity id |
| S_LOGIN, S_SPAWN_ME, S_LOAD_TOPO, S_PLAYER_STAT_UPDATE, S_SERVER_TIME | all | - | - | character state; real-GM = real-normal, so no admin bit |
| S_GUILD_*, S_GET_USER_GUILD_LOGO, S_EVENT_QUEST_SUMMARY, S_SKILL_CATEGORY, S_NPC_*, S_ABNORMALITY_*, S_ACTION_* | - | present | absent | our character has no guild and the zone is empty |
| C->S, whole window | - | 27 frames | 26 frames | only `C_GET_USER_GUILD_LOGO` x2 extra (guild) |

Between the real URL response (412) and the panel (524) the client sends only
`C_SIMPLE_TIP_REPEAT_CHECK` x5, `C_GUARD_PK_POLICY` and `C_VISIT_NEW_SECTION`, and the server
sends nothing but ambient world traffic. There is no second keypress and no handshake: the
panel opens off state the client already had.

### Correction to the T129/T131/T132 conclusion above

The earlier sections concluded the gate was the Awesomium view's HTTP fetch at
`apiServerAddress`. That is wrong, and the probe never firing was the evidence against it rather
than for it: `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` is byte-identical in all three real
sessions, including the two where the panel never opened, so it cannot be what distinguishes
them. The client decides whether to open the panel before it would fetch anything, and it
decides on `S_SELECT_USER` body 1. `apiServerAddress` still matters for what the panel then
*shows*, and should be set to an address the client can reach, but it is not the gate.

### Note, not acted on

`User+0x3B98` is reset to 0 at LeaveWorld when it equals exactly 5 (`leaveType != 2`) - the real
server treats level 5 as a session-scoped GM grant. `GmCommandHandlers.LevelOf` returns 5 for
listed operators. Harmless here (the checks around it are all `> 0`), but it is why the real GM
capture carries 1 and not 5.

---

## T148 - GM held/vaporized: the World half of the invisibility toggle

| frame (cap_final.log, real) | what | ours before | ours now |
|---|---|---|---|
| 408 AS_ENTER_WORLD `[111]` | adminLevel 1 | LevelOf (T142b) | same |
| 495 SDB_USER_VAPORIZED `01 00 00 00 01` | World: `User::EnterWorld` -> `SetVaporized(1)` when admin > 0 | dropped | tracked -> `IsGmInvisible` |
| 618/619 0x2930 -> 0x2931 `[reqId] 01 00` | LoadHoldCharacterStatus, every character; payload[5] = held | `01 00` | same - never held |
| 769 AS_ADMIN_REQUEST_USERACTION (0x2827) | tool's C_ADMIN_GM_SKILL(0) -> action 0x65, arg = skill | **never sent** - answered the client locally | sent; World answers |
| 774 SDB_USER_VAPORIZED `... 00` | World releases | - | tracked |
| 775 S_ADMIN_GM_SKILL 00, then S_LOAD_TOPO | World's reply + respawn (the short loading screen) | ours, local, no respawn | World's, through the tunnel |

- **Hold is not it.** 0x2930 is a load at every spawn; the real Arbiter answers held = 0 for GMs
  too (cap_social4 x7, cap_final x16), and so do we. The hold flag (User+0xA610 -> +0x175) only
  freezes C_PLAYER_LOCATION.
- **Vaporize is.** Since T142b sent the admin level in AS_ENTER_WORLD, World vaporizes every GM at
  spawn. Our Alt+A toggle flipped the client only, so World kept the character vaporized for the
  whole session while the client showed it visible.
- 0x2827 payload: `[u32 14 = record frame offset][u32 208]` + record: name/dbId x2 (8/84, 88/164),
  zone 168, position 176-187 (= last S_LOAD_TOPO), action 188, arg 192. World's case 0x65 reads
  164, 188, 192 only. Bytes past the names and the 20-byte tail are uninitialised stack on the real
  Arbiter; ours are zero.
- Learn path (`User::LearnSkillWithoutContract` -> `IsSkillLearnable`) does not read the vaporize
  flag directly; the refusal is expected to clear once the GM toggles Invisible OFF. If it does
  not, the next suspect is World's admin level itself (AS_ENTER_WORLD[111] = 0 restores the
  pre-T142b state; cap_social4's GM ran with 0 and learned 210 skills).
- Not changed: `/@vis` `/@invis` still flip the client switch only (T128); `/@vaporize`
  `/@invisible` still also forward the World QA command.

---

## T152 - learns refused after relog: the spawn push inverted the GM's visibility

cap_skills2.log holds both sessions of character `test` (10), adminLevel 5 in AS_ENTER_WORLD both times.

| | session A 02:38 (learns work) | session B 03:14 (learns refused) |
|---|---|---|
| AS_LEARN_ALL_CREST_ACQUIRABLE | 691 B (pre-T151 echo) | 19 B empty (T151) - skills unaffected: S_SKILL_LIST / S_SKILL_LEARN_LIST in B carry every A learn and offer the next ranks |
| SDB_INCREASE_INVENTORY_SIZE (T150b) | not sent | not sent |
| 0x282D at spawn | none | none - World does not vaporize at adminLevel 5 |
| client told at spawn | - | **our** S_ADMIN_GM_SKILL 01 "invisible" (client 76) |
| GM panel | untouched | Invisible OFF -> 0x2827 -> World **vaporizes** (5896 `0a 00 00 00 01`, @1435); panel re-toggles 51 ms later (@1436) but no 0x282D 00 ever follows |
| learn attempts | 48 x SDB_USER_LEARN_SKILL answered | 2 x C_SKILL_LEARN_REQUEST (skills 60401301, 60199, isActive 0) -> @3534 (0xDCE, IsSkillLearnable false); no SDB write |

The spawn S_ADMIN_GM_SKILL is World's, not the Arbiter's: cap_final 495 SDB_USER_VAPORIZED 01 then 496 SA_BYPASS_TO_CLIENT
`09 00 BE 64 00 00 00 00 01`. Our World-path push duplicated it when World vaporizes and contradicted it when World does not.
Removed; the tracker follows SDB_USER_VAPORIZED only. Not changed: the injected S_ADMIN_HOLD_CHARACTER 00 after S_SPAWN_ME
(World sends its own through the 0x2931 reply; ours is a harmless duplicate).

## T155 - the GM tool's World-bound buttons

Coordinate teleport sample: cap_multiworld_client 2497 C_ADMIN_GM_TELEPORT (zone 5, 16920 / 1232 / -4427) -> A->W 3060
0x2827 action 100 with the packet's zone and position -> 2501/2502 S_FESTIVAL_LIST / S_LOAD_TOPO. (cap_final_gm_client2
548-549 follow C_ADMIN_GM_SKILL 542, the T148 toggle, not a teleport.) World's Handler_AS_ADMIN_REQUEST_USERACTION has six
cases (12, 13, 100, 0x65, 0x66, 0x67) and drops the rest.

| Button | Client packet | Route (T155) | Sample |
|---|---|---|---|
| Coordinate teleport | C_ADMIN_GM_TELEPORT | 0x2827 action 100 - wired | mw 2497 -> 3060; 2243 / 5101 -> 2768 / 6171 |
| Map teleport | C_ADMIN_GM_MAPTELEPORT `[nameOff][zone][x][y]` | 0x2827 action 0x67, z = 0x4b7fffff - wired | none |
| Go to | C_ADMIN_REQUEST_USERACTION 12 | 0x2825 (GM's pos) -> World 0x2826 (target's pos) -> 0x2827 verbatim - wired | cf 916 / 917 / 918 |
| Summon | C_ADMIN_REQUEST_USERACTION 13 | same chain - wired; the real one first runs a 10 s AdminTargetUserCallJob (@1337 to the target), ours asks at once | cf 1081 / 1082 / 1083 |
| Resurrection | C_ADMIN_REQUEST_USERACTION 11 | 0x2825 only, World runs User::ResurrectNow - wired | none |
| Kick | C_ADMIN_REQUEST_USERACTION 10 | Arbiter-local AdminTargetUserDisconnectJob (@1333, 3 s) - logged, not carried out | none |
| Delete monster | C_ADMIN_REMOVE_NPC `[i64 gameId]` | 0x2827 action 0x66, id at payload 200 - wired | none |
| Kill NPCs / Restrict | C_ADMIN_KILL_NPC, C_ADMIN_HOLD_CHARACTER, C_ADMIN_CHAT_BAN | World's own User:: handlers - tunnelled, unchanged | mw 11557 (kill) |
| Movement speed | C_ADMIN `speed N` | AS_ADMIN_COMMAND - unchanged | mw 3682 |
| Hide from mobs / Invincible | C_ADMIN_GM_SKILL 2 / 1 | 0x2827 0x65 (T148) | mw 2923 / 2974 |
| Warn, bookmarks | C_ADMIN_WARNING_MESSAGE, *_BOOKMARK | Arbiter-local, answered (T89 / T99) | gm_client2 1399, 524, 1167 |
| Collision | none identified | - | none |

Records: payload 8 / 84 requester name / dbId, 88 / 164 target, 168 zone, 172 channel, 176 x y z, 188 action, 192 arg,
200 i64 game id; 82-83, 162-163, 196-199, 209-215 are stack junk on the real Arbiter, zero on ours.
0x2826 is routed by payload 84 (Handler_SA_ADMIN_REQUEST_USERACTION); dropped when the requester is offline.
