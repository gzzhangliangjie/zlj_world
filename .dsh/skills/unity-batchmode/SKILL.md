---
name: unity-batchmode
description: Run Unity 2022.3.57f1c2 in batch mode from PowerShell to compile-verify the VoxelCraft project offline, parse Editor.log for CS errors, and execute the SelfTest entry point.
whenToUse: Use whenever Unity script compilation must be verified, SelfTest must run, or a Unity project setting needs offline validation without opening the editor GUI.
---

# Unity Batch-Mode Verification (VoxelCraft, offline)

## Preferred entry point: Tools/unity-cli.ps1 (since 2026-09-14)

Prefer the project CLI over hand-typed batchmode lines. It adds lock queuing,
instance-race retry, log-based verdicts (never exit code alone), and correct
process exit codes for CI/agents:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\unity-cli.ps1" <command>
# status | compile-fast | compile | test | build webgl|win | publish | snapshot | log | clean | ci
```

- `compile-fast` = MSBuild over the Unity-generated csproj, ~20s, no Unity launch
  (syntax check only; authoritative checks still need real Unity).
- `ci` = compile-fast + `SelfTest.RunAll` — the standard milestone gate; exit 0 = green.
- All logs land in `_logs\` with timestamps. Full reference: `VoxelCraft/Docs/UNITY_CLI.md`.

The raw commands below still work and document what the CLI wraps. This capability is also packaged as the `unity-cli` skill - prefer invoking that.

## Environment facts (verified on this machine)

- Unity editor: `D:\Unity\2022.3.57f1c2\Editor\Unity.exe` (China build; backup: `D:\Unity\2023.1.4f1\Editor\Unity.exe`).
- License file `C:\ProgramData\Unity\Unity_lic.ulf` exists → batch activation already satisfied.
- Project path: `D:\zlj world\VoxelCraft`.
- Default editor log: `%LOCALAPPDATA%\Unity\Editor\Editor.log`. Always pass `-logFile <path>` to get a clean isolated log instead.

## Standard compile check

```powershell
& "D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -quit `
  -projectPath "D:\zlj world\VoxelCraft" `
  -logFile "D:\zlj world\Vexternal_tmp_compile.log"
```

Then judge ONLY by log content, never by exit code alone:

```powershell
Select-String -Path <log> -Pattern "error CS" | Select-Object -First 20
Select-String -Path <log> -Pattern "SELFTEST" | Select-Object -First 20
```

- `error CS####` lines = script compile failures; fix and rerun.
- Shader import errors appear as lines containing `Shader error`; also treat as failure.
- First run after ProjectSettings changes may rebuild `Library/` (1-3 min). Long Import steps are normal.

## Run SelfTest (offline regression)

```powershell
& "D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -quit `
  -projectPath "D:\zlj world\VoxelCraft" `
  -executeMethod VoxelCraft.Editor.SelfTest.RunAll `
  -logFile "D:\zlj world\VoxelCraft_selftest.log"
```

`SelfTest.RunAll` prints `SELFTEST PASS` or `SELFTEST FAIL: <case>` lines. Any FAIL = do not report the milestone as done.

## Pitfalls

- Do NOT add `-nographics` when shader import correctness matters (shaders may skip real compilation).
- If a previous batch Unity instance still runs, a second launch fails with "It looks like another Unity instance is running" — check `Get-Process Unity` first.
- **Exit code 140063 with NO log file written (2026-09-14, verified twice)** = Unity aborted before log init. Two causes on this machine: (a) **DSH file sandbox too tight** - under workspace-write policy Unity died on startup writes 8/8 times, while the same command under danger-full-access finished a full SelfTest in ~9s on a warm Library; (b) instance race against another session batch-running Unity back-to-back. Remedy: check the session's file policy first, then launch only when no Unity process exists AND `Temp\UnityLockfile` is gone; retry with jittered backoff. `unity-cli.ps1` does all of this automatically.
- `Start-Process -PassThru`: run `$null = $p.Handle` immediately after spawn, otherwise `.ExitCode` may read back EMPTY after exit (Windows PowerShell behavior, 2026-09-14 verified).
- Killing the process mid-import can leave `Library/` locked; if the next open hangs, delete `Library/` and reimport.
- China build (f1c2) behaves like the standard 2022.3.57f1 for batch CLI purposes.
- Never "fix" an error by deleting scripts; consult `voxelcraft-guide` skill for architecture intent.
