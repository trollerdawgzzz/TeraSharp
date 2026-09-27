# T184d local capture evidence

Report: [T184d-CLIENT-STATE.md](../../status/T184d-CLIENT-STATE.md).

- [OPCODES.md](OPCODES.md): every server opcode and automatic definition coverage.
- [ALL-FIELDS.md](ALL-FIELDS.md): full field comparison index, with source-derived corrections.
- [field-ledger.tsv](field-ledger.tsv): every field occurrence and original capture record.
- [SELECTED-CHARACTER.md](SELECTED-CHARACTER.md): all selected-character lobby fields.
- [UI-WINDOWS.md](UI-WINDOWS.md), [GFX-OPTIONS.md](GFX-OPTIONS.md), [CLIENT-SETTINGS.md](CLIENT-SETTINGS.md): expanded UI/options analysis.
- `manual.json`, `manual-field-ledger.tsv`, `settings-fields.tsv`, `settings-*.json`: all manual/opaque-blob decodes.
- `flip-*.hex`: isolated experimental frames, not deployed fixes. Match the source record and scope described in the report before using one; captured identity fields are not portable to other users.

Generated files are deliberately local-only. `decoded.jsonl` and `input.jsonl` contain private captured login credentials. Only this README and `.gitignore` are tracked here.

Local analysis helpers are in `obj/t184d`. The decode uses the repository .NET codec, `V100Definitions`, and `../tera_v100_MASTER_FINAL`; input preparation uses `obj/t184b/audit.py` to read complete original HEX records. Preparation records the login-to-FIN bounds, not a 64-byte `_ctl.txt` excerpt. `tables.py` applies `manual.json` corrections before writing the field index; it retains the original definition coverage in OPCODES.

Reproduction order from this worktree (the already-created helper files are local, not shipped): `prepare.py`; `dotnet run --project obj/t184d/Decode.csproj -- obj/t184d/input.jsonl data/t184d/decoded.jsonl data/t184d/schemas.json`; `detail.py`; `manual.py`; `finish.py`; `tables.py`; `evidence.py`. Redirect verbose helper output to local files. The research report documents remaining incomplete definitions; an exact byte round-trip is not proof of a field's semantic name.
