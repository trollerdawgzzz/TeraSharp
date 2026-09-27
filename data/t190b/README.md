# T190b relog/matching evidence

Source: `D:/packetlogs/cap_2man_b_client1.log` and the new-run portion of
`cap_2man_b.log`; hashes and the excluded old `cap_2man.log` prefix are in
`source-manifest.json`. Client references are original records. Tap references
are original TCP chunk plus byte offset, never reassembled frame ordinals.

Regenerate from the repo root:

```powershell
python tools/t190-evidence.py
python tools/t190b-evidence.py
```

The first command produces the ignored private cache `obj/t190/frames.json`;
the second uses it and exports only matching/party/topology controls. No login
authentication or personal settings are exported. `capture-frames.json` retains
complete selected frames, including AS_ENTER_WORLD13799+0. `capture-decoded.json`
contains the decoded9781 rows, SYS triples, pool/progress/reset state, and exact
tunnel references. `matching-window.tsv` enumerates every matching/party/dungeon
named packet from client1 #1719 through #2554 (including ordinary party updates
so the census does not silently omit them).

`party-frames.json` separately pins AS_ENTER_WORLD13799+0, the15350B
SA_ENTER_WORLD13913+15, and AS_NOTIFY_USER_IN_PARTY14041+0 (`0x13AD`,10B).
These are the exact relog party-handler input/output fixtures; the large input
contains character world state, not login authentication.

Tunnel matching compares the complete embedded client frame. Identical repeats
are resolved using adjacent unique packets and wire order. Empty `tap_refs`
means no uniquely resolved tunnel copy, not a proof of packet ownership.

Installed F732/SYS/progress/pool definitions omit arrays. F732 decoding uses
WorldServer.exe.c:693375–693386,694912–694959 and scalar names
Arb_part_024.c:11673–11910. WorldServer.exe.c:1995438–1995553 defines its40B SYS
triples as `[UserDbId,UserDbId,Role]`; the distinct Arbiter formation packet uses
`[ServerId,UserDbId,Role]`. Neither is assigned an unproved pending-entry meaning.
S_PARTY_MEMBER_LIST uses its complete .8.def;
87AC uses S_CHANGE_EVENT_MATCHING_STATE.2.def. Empty highlight/name definitions
are preserved as raw capture evidence rather than guessed fields.
