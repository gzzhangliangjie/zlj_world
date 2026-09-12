---
name: voxelcraft-guide
description: Project map, coding conventions, asset pipeline, licensing and change recipes for the VoxelCraft Unity voxel game in this workspace.
whenToUse: Use before modifying VoxelCraft source, adding block types, touching textures/audio, writing its README, or deciding how a feature should be structured.
---

# VoxelCraft Project Guide

> **Deep reference:** `VoxelCraft/Docs/TECHNICAL.md` — full implementation details (algorithms, formulas, scheduling, API pitfalls, extension recipes) with file/method anchors. Consult it before modifying any subsystem.

## What this project is

A Minecraft-like first-person voxel sandbox built for Unity 2022.3.57f1c2, Developed against ROADMAP.md at the workspace root. Core: chunked streaming terrain, player movement (walk/sprint/jump/fly/swim), block place/break with hotbar. Optional M7: block sounds, extra blocks, animals, third-person character.

## Hard rules

1. **Zero external UPM packages.** `Packages/manifest.json` stays empty-dependency (offline machine; built-in modules only). No InputSystem, no Burst, no URP.
2. **Built-in render pipeline, legacy Input, IMGUI** (OnGUI). Custom shaders live in `Assets/Resources/Shaders/` and are loaded with `Resources.Load<Shader>` (never `Shader.Find` — build stripping).
3. **Code and comments in English; user-facing docs (README.md) in Chinese.** No Chinese string literals inside C# (font/locale safety).
4. **Scene stays near-empty.** Everything self-bootstraps from `RuntimeInitializeOnLoadMethod` (Core/GameBoot.cs → Game.cs composition root). No drag-and-drop scene wiring, ever.
5. Textures/sounds ship as raw bytes under `Assets/Resources/` (`.png.bytes` / `.ogg.bytes`) and are loaded with `Resources.Load<TextAsset>` + `LoadImage`/audio decode path — **never** rely on TextureImporter/AudioImporter settings (importer metas are not hand-authored).

## Module map (Assets/Scripts/)

- `Core/` — VoxelMath (chunk consts: 16×16×80, SeaLevel 30), BlockType enum, BlockDatabase (defs: opaque/solid/tile slots/placeable), GameBoot, Game (composition root + serialized tunables incl. seed, viewRadius=7).
- `Gen/` — Noise (Perlin2D+fBm+integer hash), TerrainGenerator (height/biome/surface rules/water fill/deterministic trees, tree canopy writes must stay cross-chunk safe via margin scan).
- `World/` — Chunk (byte[] voxel storage), ChunkMesher (face culling, corner AO, face-shaded vertex colors, water top lowered to 0.9, solid+water meshes), World (chunk dict, per-frame budgeted generation ≤8ms, 8-neighbor readiness gate, DDA voxel raycast, SetBlock rebuilds self + border neighbors synchronously).
- `Player/` — PlayerMotor (CharacterController; walk 4.3, sprint 7, jump ≈1.25 blocks, fly 12 toggle F, water swim), MouseLook, BlockInteraction (DDA pick ≤6, break LMB 0.22s repeat, place RMB with player-AABB overlap check, hotbar 1-9 + wheel).
- `UI/` — Hud (crosshair, hotbar icons from atlas, help overlay, FPS/pos/seed readout; Esc toggles cursor lock).
- `Art/` — TextureFactory (three-layer supply, below), material creation.
- `../Editor/SelfTest.cs` — batch-mode regression (`VoxelCraft.Editor.SelfTest.RunAll`), logs `SELFTEST PASS/FAIL`.

## Texture supply (three layers, in order)

1. External real textures: `Assets/Resources/Textures/*.png.bytes` (canonical names below). Loaded via TextAsset→LoadImage→GetPixels32 into the 4×4(+) atlas. Any missing tile falls through.
2. Procedural fallback painted by TextureFactory (16×16 pixel-art) so the project runs on any machine.
3. Future re-skin: drop same-named `.png.bytes` files to replace a tile or the whole set.

Canonical tile names: `grass_top, grass_side, dirt, stone, sand, log_side, log_top, leaves, water, plank, cobble, glass, snow, brick` (+M7-B extras: `bedrock, coal_ore, iron_ore, gold_ore, diamond_ore, gravel, ice, obsidian, mossy, stone_brick`).

## Asset licensing (STRICT)

- Style A set (minetest_game): **CC BY-SA 3.0**, credit "celeron55 (Perttu Ahola) and Minetest Game contributors"; license copy staged at `_external/chosen_textures/LICENSE-minetest_game.txt` and must ship in the project + README attribution.
- Style B set (VoxeLibre textures): mostly **Pixel Perfection by XSSheep, CC BY-SA 4.0** (verbatim copies per VoxeLibre LEGAL.md), rest CC BY-SA 3.0; staged at `_external/styleB_voxelibre/`. Attribution + license copy required either way.
- Keep BY-SA license text next to the shipped textures and list authors in README; do not mix files between sets without keeping provenance.

## Change recipes

- **Add a block type:** extend BlockType enum → add BlockDatabase row (tiles, opaque, solid) → atlas slot in TextureFactory tile table → (if placeable) hotbar entry in BlockInteraction. World gen integration only if terrain should produce it.
- **Retune world:** Game component fields (seed, viewRadius) or TerrainGenerator constants; never scatter magic numbers outside these two.
- **Verify anything:** use the `unity-batchmode` skill (compile + SelfTest). SelfTest must stay green before reporting a milestone complete.

## Staged external assets (outside the Unity project)

- `_external/chosen_textures/` — style A tiles (14) + license.
- `_external/styleB_voxelibre/` — style B tiles (24: core + ores/bedrock/etc. + composed grass_side + water first frame).
- `_external/minetest_game/`, `_external/mineclone2/` — sparse clones (git), sources for re-extraction.
- Local Asset Store packs (animated, not voxel-style): Animals FREE (7 animals FBX + 30 .anim), Battle Wizard (38 action FBX + controller) — only for M7-C/D if the low-poly look is chosen over procedural blocky mobs.
