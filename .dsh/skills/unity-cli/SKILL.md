---
name: unity-cli
description: Run the VoxelCraft Unity CLI (Tools/unity-cli.ps1, shorthand vx.cmd) - fast MSBuild compile check, full batchmode compile, SelfTest regression, WebGL/Windows builds, publish, model snapshots, Unity log parsing, project clean, and the one-command ci gate, with automatic lock queuing, instance-race retry, and verdict-by-log.
whenToUse: Use for any Unity workflow in this workspace - verifying script compilation, running SelfTest before claiming a milestone done, building/publishing WebGL or Windows, inspecting what a batch run logged, or cleaning the project. Prefer this skill over hand-typed batchmode commands (raw reference lives in the unity-batchmode skill).
---

# Unity CLI (VoxelCraft, Tools/unity-cli.ps1)

## Golden rules

1. Never hand-type Unity batchmode lines - call the CLI. It adds lock queuing,
   conflict retry, log-based verdicts, and correct exit codes.
2. Verdicts come from Unity's own log (`error CS####`, `SELFTEST PASS/FAIL`,
   `WEBGL/WINDOWS BUILD OK/FAIL`), never from the exit code alone.
3. CLI exit codes: `0` ok, `1` failure, `2` usage - safe for CI and agent gates.
4. All logs land in the workspace-root `_logs\` with timestamps; the project
   root must stay clean.

## Invocation (from the workspace root)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\unity-cli.ps1" <command>
# shorthand:
cmd /c "Tools\vx.cmd" <command>
```

| command | what it does | Unity? | typical time |
|---|---|---|---|
| `status` | env/project snapshot: processes, lock, license, csproj drift, recent builds/logs | no | <1s |
| `compile-fast` | MSBuild over the Unity-generated csproj (game + editor assemblies) | no | 1-20s |
| `compile` | full batchmode compile check (authoritative) | yes | 1-3min (warm Library ~10s) |
| `test` | batchmode `VoxelCraft.Editor.SelfTest.RunAll` | yes | 1-3min (warm ~10s) |
| `build webgl` / `build win` | player build + artifact-exists check | yes | minutes |
| `publish [msg]` | build webgl + commit + push (delegates `Tools\publish-webgl.ps1`) | yes | minutes |
| `snapshot` | batchmode `ModelSnapshot.Run` PNGs into `_logs` | yes | 1-2min |
| `log [file]` | parse any Unity log, print verdict (newest in `_logs` if omitted) | no | <1s |
| `clean [-Yes]` | delete `Library/Temp/obj/Logs` (refuses while locked) | no | seconds |
| `ci [-WithWebGL]` | gate: compile-fast (auto-upgrades to full compile on csproj drift) + test | both | ~10s-4min |

## Standard flows

- Edited existing `.cs` files: `vx compile-fast` for the quick loop, then
  `vx test` / `vx ci` before claiming anything done.
- **Milestone gate (mandatory): `vx ci` must be green.**
- Added NEW `.cs` files: the csproj is stale - `vx ci` detects the drift and
  upgrades itself to a full Unity compile (or open Unity once to regenerate).
- Ship: `vx ci` green, then `vx publish "note"` (push triggers Pages deploy).
- A batch run "failed mysteriously"? `vx log` - it reads the newest `_logs` file
  and prints the verdict.

## Failure modes the CLI already handles (do not re-diagnose)

- **Lock queueing**: launches only when no Unity process exists AND no
  `VoxelCraft\Temp\UnityLockfile`; on conflict it retries up to 8 times with
  15-25s jittered backoff. Other agent sessions batch-run this workspace
  continuously - let the CLI queue, do not kill their Unity.
- **Exit 140063 with NO log file** = Unity aborted before log init. Verified
  causes (2026-09-14): (a) DSH file sandbox too tight - workspace-write policy
  kills Unity's startup writes, danger-full-access works; (b) instance race.
  Check the session's file policy first.
- **`Start-Process -PassThru` ExitCode reads back empty** unless
  `$null = $p.Handle` is captured right after spawn (the CLI does this).
- **Launcher-exits-early quirk**: bootstrap exits while the child editor still
  imports - the CLI waits for the child and a 30s-silent log before judging.

## Reference

- Full docs: `VoxelCraft/Docs/UNITY_CLI.md`
- Raw batchmode commands behind the CLI: `unity-batchmode` skill
- Project conventions: `voxelcraft-guide` skill
