# T184h clear-count evidence

`clear-count-frames.json` contains four unmodified frames from the retail two-player run:

| Source | Record | Frame |
|---|---|---|
| cap_2man_client2 | 822 | C_DUNGEON_CLEAR_COUNT_LIST, 16 bytes |
| cap_2man | 1464+0 | AS13F6 forwarding that request, 46 bytes |
| cap_2man | 1466+0 | SA13F7 carrying World's response |
| cap_2man_client2 | 824 | S_DUNGEON_CLEAR_COUNT_LIST, 337 bytes, 25 rows |

The live forwarding test normalizes only the generated AS13F6 monotonic tick (full offsets22–29). Stored fixture bytes are never normalized. The returned337-byte client frame matches the payload carried by SA13F7.

`dungeon-clear-sheets/` contains41 minimal XML projections: DungeonMatching, DungeonNewbieBonus and39 matching DungeonData headers/quest conditions. `source-manifest.json` records the original local paths, byte lengths and SHA-256 hashes. These are test inputs, not replacement deployment sheets. The level70 eligible set reproduces the captured25-row order;9781 has one clear and remains a newbie below the sheet threshold10.

Regenerate from the original captures and current local sheets, from the repository root:

```powershell
python tools/t184g-evidence.py
python tools/t184h-clear-evidence.py
```

The second exporter reads the first exporter's private `obj/t184g/frames.json` cache. Capture references for notification and party-capacity tests remain in their existing T184f fixtures: client2:819/client1:1230 (@2173), client1:1281/client2:910 (roster), tap1921+0 (139E), client1:1286/client2:915 (SYS).
