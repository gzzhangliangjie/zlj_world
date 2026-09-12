---
name: unity-batchmode
description: Run Unity 2022.3.57f1c2 in batch mode from PowerShell to compile-verify the VoxelCraft project offline, parse Editor.log for CS errors, and execute the SelfTest entry point.
whenToUse: Use whenever Unity script compilation must be verified, SelfTest must run, or a Unity project setting needs offline validation without opening the editor GUI.
---

# Unity Batch-Mode Verification (VoxelCraft, offline)

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
- Killing the process mid-import can leave `Library/` locked; if the next open hangs, delete `Library/` and reimport.
- China build (f1c2) behaves like the standard 2022.3.57f1 for batch CLI purposes.
- Never "fix" an error by deleting scripts; consult `voxelcraft-guide` skill for architecture intent.
