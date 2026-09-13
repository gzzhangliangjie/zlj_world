---
name: mob-replicator
description: Replicate a textured blocky Minecraft-style mob from a reference screenshot using the VoxelCraft MobReplicator editor tool - extracts chat-pasted reference images, calibrates the projection, fits box dimensions to the silhouette, paints a 64x32 skin by per-face inverse sampling, and renders side-by-side comparisons.
whenToUse: Use when the user provides a screenshot/render of a mob and wants it rebuilt as a textured box model, asks to "还原/复刻" a reference image into a VoxelCraft creature, or mentions the Mob Replicator.
---

# Mob Replicator (screenshot -> textured box model)

Pipeline (all in `VoxelCraft/Assets/Editor/MobReplicator.cs`):

```
reference PNG -> flood-fill background mask -> auto-calibration (align projected
spec bbox to the image bbox) -> silhouette fit (coordinate descent on body/head/
legH/legT, IoU vs reference mask, dual-facing yaw+180 auto-detect, vanilla prior
clamp 0.5x-2x) -> per-face inverse sampling paints a 64x32 skin -> rebuild boxes
-> render at reference resolution -> side-by-side comparison PNG
```

## 1. Getting reference images out of the chat

Pasted images are NOT lost: the GUI stores them content-addressed by SHA256 at

```
C:\Users\zlj10\AppData\Roaming\dsh-desktop\dsh-home\attachments\v1\objects\<aa>\<sha256>
```

Every `read_image` tool result in the conversation prints `Image sha256:<hash>`;
copy `<objects>\<hash[0..1]>\<hash>` to `D:\zlj world\_refs\<mob>.png` and verify
the SHA256 matches before use (PowerShell SHA256 over the bytes).

## 2. Inputs

- `D:\zlj world\_refs\chicken.png` / `pig.png` / `sheep.png` (transparent OR flat
  background both work; background is detected by flood fill + alpha < 0.1).
- Missing files fall back to synthetic stand-in renders, so the pipeline can
  always be smoke-tested.
- Presets with vanilla box sizes + our sheet layout (`McNet`-compatible texOffs)
  live in `SpecChicken()/SpecPig()/SpecSheep()`.

## 3. Running

```powershell
& "D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -quit `
  -projectPath "D:\zlj world\VoxelCraft" `
  -executeMethod VoxelCraft.Editor.MobReplicator.RunExperiment `
  -logFile "D:\zlj world\_logs\replicator_runN.log"
# no -nographics; poll Get-Process Unity; read the log for verdicts
```

Editor GUI alternative: `Tools > VoxelCraft > Mob Replicator` (yaw/pitch sliders,
per-mob buttons). Grep the log for `REPLICA fit` (reports yaw + IoU + fitted
dims), `REPLICA wrote`.

## 4. Outputs

- `_logs\replica_<mob>.png` - reference vs replica, side by side.
- `_logs\fit_<mob>_overlay.png` - red=reference mask, green=model mask (use when
  IoU looks wrong).
- `_logs\replica_<mob>_sheet.png` + `Assets/Resources/Textures/replica_<mob>_skin.png.bytes`
  - the generated 64x32 skin.

## 5. Gotchas (all bit us once)

- `Texture2D.SetPixel` row 0 is the BOTTOM; net rect `v` counts from the TOP
  (SkinnedBox does `1 - y/texH`). Painter must write `SetPixel(x, 31 - y, c)`.
- `projScale` is static: `SetCamera` MUST reset it to 1 or calibration compounds
  the stale scale (symptom: oscillating calibrate logs, dot-sparse mask).
- `Renderer.bounds`/`mesh.bounds` are garbage in batch mode - never frame or
  fit with them; use the software projector.
- `TexelPoint` must use the FACE's world extents (`f.uw/f.vh`), not size.x/y.
- Single-view depth is under-constrained: keep the 0.5x-2x vanilla prior clamp
  or the fit produces flat-slab degenerate models with high IoU.
- Visual parity with a wiki render needs more iterations: currently the palette
  and silhouette match; per-face pixel-level sharpness does not.
