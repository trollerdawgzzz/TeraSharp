# The TERA / Battle Arena mode screen (T100 - research only, nothing changed)

**Package.** `S1UI_GameModeSelectScene.gpk` (302,723 B), UE3 897/17: plain header 0x00-0x80, then ONE LZO chunk - flags `02 00 00 00` @0x6D, chunk entry @0x75 = (uncompOff 0x81, uncompSize 0xC307F, compOff 0x91, compSize 0x49DF2), 7 blocks of 0x20000, block header @0x91.
**Contents.** Inflated: 24 names, 8 imports, **2 exports** - `GameModeSelectScene` of class `GFxUI.GFxMovieInfo` (797,857 B @0x44F) and one ObjectRedirector. Its `RawData` is a GFX v9 movie (`47 46 58 09`) at export+0xC1, 0xC2B7A B, from `...\UIResourceData\bin\GameModeSelectScene.swf`.

**The ActionScript is AS2, not AS3.** `g4.view.gameModeSelectScene.GameModeSelectScene` extends `g4.core.View`, with `GameModeSelectSceneController` and `IGameModeSelectScene`. `configUI` wires `enterBtn`, `prevBtn` and the radio group to three calls into native code - `ToGame_GameModeSelectScene_ModeSelect`, `..._EnterBtnClick`, `..._PrevBtnClick` - plus `ToGame_InitUI`.
**Native opens it**, via `EventBroadCaster.OnGameEventShowWindow` with `ViewID.GAME_MODE_SELECT_SCENE` = "GameModeSelectScene". The movie holds no decision of its own.

**So the GPK is the wrong layer, and there is no minimal byte patch.** The most an edited SWF can do is auto-fire ModeSelect+EnterBtnClick from `configUI`, which still flashes the screen; and any edit means re-LZO-ing the chunk and rewriting the four sizes above, so it is not reversible in place. No launcher argument and no replacement GPK for this is published - the one RaGEZONE 100.02 thread that asks ("remove the option to chose between Tera and Arena") has no answer.

**Try the server first.** The gate is server-side and TeraSharp already sends the captured value: `S_LOGIN_ARBITER.status` = 31 for players / 33 for the tool account (T89b, `LoginHandlers.cs:56`). If the screen still appears with that, the next candidate is the content-flag burst: `SendContentFlags` sends ids 2,3,4,8,9,22,23,20,21,34 with 8 and 9 disabled, exactly as cap_final_client does - flip one id at a time and watch, rather than patching the client.
**If it must be the client**, the target is `S1Game.exe`, not this package: find the caller that raises `OnGameEventShowWindow("GameModeSelectScene")` and what it tests. That needs the binary staged, which this task did not do.

Tooling note: the chunk inflates with `python-lzo` (`apt-get install liblzo2-dev && pip install python-lzo`), `lzo.decompress(block, False, uncompressedSize)` per block.
