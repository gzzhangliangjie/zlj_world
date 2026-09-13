using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.Items;
using VoxelCraft.Player;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Offline regression entry point invoked by Unity batch mode:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; -executeMethod VoxelCraft.Editor.SelfTest.RunAll -logFile &lt;log&gt;
    /// Every case logs a "SELFTEST PASS &lt;case&gt;" or "SELFTEST FAIL &lt;case&gt; : reason" line.
    /// The batch run is judged by these lines, never by the process exit code alone.
    /// </summary>
    public static class SelfTest
    {
        public static void RunAll()
        {
            int pass = 0, fail = 0;

            Report("m0.skeleton", true, "editor assembly compiles");

            // ----- M1: atlas + shaders + block database -----
            TextureFactory.AtlasResult atlas = SafeBuildAtlas();
            if (atlas != null)
            {
                Eval("m1.external_textures", atlas.externalTiles.Count == 24,
                    $"external tiles {atlas.externalTiles.Count}/24, procedural fallback {atlas.proceduralTiles.Count}");
                Eval("m1.atlas_size",
                    atlas.atlas.width == TextureFactory.AtlasCols * TextureFactory.TileSize &&
                    atlas.atlas.height == TextureFactory.AtlasRows * TextureFactory.TileSize,
                    $"{atlas.atlas.width}x{atlas.atlas.height}");
                Eval("m1.icons", atlas.icons.Count == 22, $"icon count {atlas.icons.Count}/22");

                // Every non-air block must have a name (validates the def table size).
                bool defsOk = true;
                string bad = "";
                for (int t = 1; t < 23 && defsOk; t++)
                {
                    var def = BlockDatabase.Get((BlockType)t);
                    if (def.name == null || def.name.Length == 0)
                    {
                        defsOk = false;
                        bad = $"block {(BlockType)t} has no name";
                    }
                }
                Eval("m1.block_defs", defsOk, bad);

                // Every atlas tile must contain visible pixels somewhere
                // (5-point sampling so legitimately transparent-centred tiles like
                //  glass / leaves / water do not false-fail).
                bool tilesAlive = true;
                string deadTile = "";
                for (int tile = 0; tile < 24 && tilesAlive; tile++)
                {
                    var rect = atlas.TileRect((TileId)tile);
                    bool anyOpaque = false;
                    for (int s = 0; s < 5 && !anyOpaque; s++)
                    {
                        float fx = s == 4 ? 0.5f : (s == 0 ? 0.1f : s == 1 ? 0.9f : s == 2 ? 0.1f : 0.9f);
                        float fy = s == 4 ? 0.5f : (s == 0 || s == 1 ? 0.5f : (s == 2 ? 0.1f : 0.9f));
                        int px = Mathf.Clamp(Mathf.RoundToInt((rect.xMin + rect.width * fx) * atlas.atlas.width), 0, atlas.atlas.width - 1);
                        int py = Mathf.Clamp(Mathf.RoundToInt((rect.yMin + rect.height * fy) * atlas.atlas.height), 0, atlas.atlas.height - 1);
                        if (atlas.atlas.GetPixel(px, py).a > 0.02f)
                        {
                            anyOpaque = true;
                        }
                    }
                    if (!anyOpaque)
                    {
                        tilesAlive = false;
                        deadTile = ((TileId)tile).ToString();
                    }
                }
                Eval("m1.atlas_pixels", tilesAlive, tilesAlive ? "visible pixels in every tile" : $"tile {deadTile} fully transparent");
            }
            else
            {
                Report("m1.atlas_build", false, "TextureFactory.Build threw");
            }

            Shader blocks = Resources.Load<Shader>("Shaders/BlocksShader");
            Shader water = Resources.Load<Shader>("Shaders/WaterShader");
            Eval("m1.shaders", blocks != null && water != null && blocks.isSupported && water.isSupported,
                $"blocks={(blocks != null)} water={(water != null)}");

            // ----- M2: deterministic terrain, trees, biome variety -----
            var gen = new TerrainGenerator(1337);
            var ca = new Chunk(0, 0);
            gen.Generate(ca);
            var cb = new Chunk(0, 0);
            gen.Generate(cb);
            Eval("m2.determinism", ca.blocks.SequenceEqual(cb.blocks), "regenerating the same chunk is byte-identical");

            var gen2 = new TerrainGenerator(1337);
            var cc = new Chunk(0, 0);
            gen2.Generate(cc);
            Eval("m2.determinism_instance", ca.blocks.SequenceEqual(cc.blocks), "fresh generator instance is byte-identical");

            // Trees: scan a wide area for anchors, then verify the containing chunk
            // really grows logs + leaves (anchor location independent).
            int anchors = 0;
            int ax = int.MinValue, az = int.MinValue;
            for (int dz = -256; dz <= 256 && ax == int.MinValue; dz += 4)
            {
                for (int dx = -256; dx <= 256; dx += 4)
                {
                    if (gen.TreeAt(dx, dz))
                    {
                        if (ax == int.MinValue)
                        {
                            ax = dx;
                            az = dz;
                        }
                    }
                }
            }
            for (int dz = -256; dz <= 256; dz += 3)
            {
                for (int dx = -256; dx <= 256; dx += 3)
                {
                    if (gen.TreeAt(dx, dz)) anchors++;
                }
            }
            int chunkLogs = 0, chunkLeaves = 0;
            if (ax != int.MinValue)
            {
                var treeChunk = new Chunk(VoxelMath.ChunkCoord(ax), VoxelMath.ChunkCoord(az));
                gen.Generate(treeChunk);
                for (int i = 0; i < treeChunk.blocks.Length; i++)
                {
                    var t = (BlockType)treeChunk.blocks[i];
                    if (t == BlockType.Log) chunkLogs++;
                    else if (t == BlockType.Leaves) chunkLeaves++;
                }
            }
            Eval("m2.trees_present", anchors >= 30 && chunkLogs >= 3 && chunkLeaves >= 20,
                $"anchors={anchors} in 171x171 grid; containing chunk logs={chunkLogs} leaves={chunkLeaves}");

            int waterCols = 0, snowCols = 0, desertCols = 0, grassCols = 0;
            int minH = int.MaxValue, maxH = int.MinValue;
            for (int dz = -800; dz <= 800; dz += 16)
            {
                for (int dx = -800; dx <= 800; dx += 16)
                {
                    int h = gen.HeightAt(dx, dz);
                    minH = Mathf.Min(minH, h);
                    maxH = Mathf.Max(maxH, h);
                    if (h < VoxelMath.SeaLevel - 2) waterCols++;
                    if (h >= TerrainGenerator.SnowLine) snowCols++;
                    if (gen.IsDesert(dx, dz)) desertCols++;
                    else if (h > VoxelMath.SeaLevel + 1 && h < TerrainGenerator.SnowLine) grassCols++;
                }
            }
            Eval("m2.biome_variety", waterCols > 0 && snowCols > 0 && desertCols > 0 && grassCols > 0,
                $"water={waterCols} snow={snowCols} desert={desertCols} grass={grassCols} of 10101 samples");
            Eval("m2.height_bounds", minH >= 1 && maxH <= TerrainGenerator.MaxTerrainHeight,
                $"minH={minH} maxH={maxH} bounds=[1,{TerrainGenerator.MaxTerrainHeight}]");

            // ----- M3: meshing + streaming -----
            var rects = new Rect[24];
            for (int i = 0; i < 24; i++)
            {
                rects[i] = atlas.TileRect((TileId)i);
            }
            var sim = new WorldSim(1337) { tileRects = rects, dataRadius = 2, meshRadius = 1, unloadRadius = 4 };
            var remeshedT = new List<Chunk>();
            sim.Step(0, 0, 5000f, 5000f, remeshedT, null);
            Eval("m3.streaming_mesh", remeshedT.Count == 9 && sim.GetChunk(0, 0).meshBuilt,
                $"remeshed {remeshedT.Count}/9 chunks in one budget step");

            // Independent face recount over the meshed 3x3 area (rules replicated on purpose).
            long expectedSolid = 0, expectedWater = 0;
            for (int cz = -1; cz <= 1; cz++)
            {
                for (int cx = -1; cx <= 1; cx++)
                {
                    var c = sim.GetChunk(cx, cz);
                    for (int y = 0; y < VoxelMath.ChunkHeight; y++)
                    {
                        for (int z = 0; z < VoxelMath.ChunkSize; z++)
                        {
                            for (int x = 0; x < VoxelMath.ChunkSize; x++)
                            {
                                var b = c.GetLocal(x, y, z);
                                if (b == BlockType.Air)
                                {
                                    continue;
                                }
                                int wx = cx * VoxelMath.ChunkSize + x;
                                int wz = cz * VoxelMath.ChunkSize + z;
                                for (int f = 0; f < 6; f++)
                                {
                                    var nv = FaceOffsets[f];
                                    var nb = sim.GetBlock(wx + nv.x, y + nv.y, wz + nv.z);
                                    if (!TestFaceVisible(b, nb))
                                    {
                                        continue;
                                    }
                                    if (b == BlockType.Water) expectedWater++;
                                    else expectedSolid++;
                                }
                            }
                        }
                    }
                }
            }
            long solidQuads = 0, waterQuads = 0;
            foreach (var c in remeshedT)
            {
                solidQuads += c.solidMeshData.Indices.Count / 6;
                waterQuads += c.waterMeshData.Indices.Count / 6;
            }
            Eval("m3.face_culling", solidQuads == expectedSolid && waterQuads == expectedWater,
                $"solid {solidQuads}=={expectedSolid}, water {waterQuads}=={expectedWater}");

            // AO must bake multiple shade levels into vertex colors.
            var shadeValues = new HashSet<byte>();
            var centerChunk = sim.GetChunk(0, 0);
            foreach (var col in centerChunk.solidMeshData.Colors)
            {
                shadeValues.Add(col.r);
            }
            Eval("m3.ao_variety", shadeValues.Count >= 3, $"{shadeValues.Count} distinct shade values");

            // Water surface must be lowered to 0.9 where air is above.
            int wwx = int.MinValue, wwz = 0;
            for (int z = 0; z <= 640 && wwx == int.MinValue; z += 4)
            {
                for (int x = 0; x <= 640; x += 4)
                {
                    if (sim.generator.HeightAt(x, z) < VoxelMath.SeaLevel - 2)
                    {
                        wwx = x;
                        wwz = z;
                        break;
                    }
                }
            }
            bool waterLowered = false;
            long waterQuads2 = 0;
            if (wwx != int.MinValue)
            {
                var sim2 = new WorldSim(1337) { tileRects = rects, dataRadius = 1, meshRadius = 0, unloadRadius = 2 };
                var rem2 = new List<Chunk>();
                int wcx = VoxelMath.ChunkCoord(wwx);
                int wcz = VoxelMath.ChunkCoord(wwz);
                sim2.Step(wcx, wcz, 5000f, 5000f, rem2, null);
                var wc = sim2.GetChunk(wcx, wcz);
                if (wc != null && wc.waterMeshData != null)
                {
                    waterQuads2 = wc.waterMeshData.Indices.Count / 6;
                    foreach (var v in wc.waterMeshData.Vertices)
                    {
                        float frac = v.y - Mathf.Floor(v.y);
                        if (Mathf.Abs(frac - 0.9f) < 0.03f)
                        {
                            waterLowered = true;
                            break;
                        }
                    }
                }
            }
            Eval("m3.water_surface", wwx != int.MinValue && waterQuads2 > 0 && waterLowered,
                $"water chunk quads={waterQuads2}, lowered vertex={waterLowered}");

            // Border edits must dirty the neighbour chunks that share geometry.
            var affectedT = new List<Chunk>();
            sim.SetBlock(16, 40, 0, BlockType.Stone, affectedT); // lx==0 && lz==0 of chunk (1,0)
            bool dirtyOk = sim.GetChunk(1, 0).meshDirty && sim.GetChunk(0, 0).meshDirty && sim.GetChunk(1, -1).meshDirty;
            Eval("m3.edit_dirty", dirtyOk && affectedT.Count >= 3, $"affected chunks={affectedT.Count}");

            // Streaming away must unload out-of-range chunks.
            var remeshedU = new List<Chunk>();
            var unloadedU = new List<Chunk>();
            sim.Step(40, 40, 4000f, 4000f, remeshedU, unloadedU);
            Eval("m3.unload", sim.GetChunk(0, 0) == null && unloadedU.Count >= 9,
                $"unloaded={unloadedU.Count}, origin chunk removed={sim.GetChunk(0, 0) == null}");

            // ----- M5: DDA voxel raycast -----
            var simR = new WorldSim(1337) { tileRects = rects, dataRadius = 1, meshRadius = 0, unloadRadius = 2 };
            simR.Step(0, 0, 5000f, 5000f, new List<Chunk>(), null);
            simR.SetBlock(10, 70, 0, BlockType.Stone, null);
            simR.SetBlock(5, 70, 2, BlockType.Water, null);
            simR.SetBlock(8, 70, 2, BlockType.Stone, null);
            simR.SetBlock(12, 70, 12, BlockType.Stone, null);
            simR.SetBlock(14, 70, 5, BlockType.Stone, null);

            bool hitA = simR.Raycast(new Vector3(0.5f, 70.5f, 0.5f), Vector3.right, 12f, out Vector3Int hA, out Vector3Int pA);
            Eval("m5.raycast_axis", hitA && hA == new Vector3Int(10, 70, 0) && pA == new Vector3Int(9, 70, 0),
                $"hit={hA} place={pA}");

            bool hitB = simR.Raycast(new Vector3(0.5f, 70.5f, 0.5f), Vector3.up, 12f, out _, out _);
            Eval("m5.raycast_miss", !hitB, "upward ray hits nothing");

            bool hitC = simR.Raycast(new Vector3(0.5f, 70.5f, 2.5f), Vector3.right, 12f, out Vector3Int hC, out Vector3Int pC);
            Eval("m5.raycast_water_pass", hitC && hC == new Vector3Int(8, 70, 2) && pC == new Vector3Int(7, 70, 2),
                $"water passed, hit={hC} place={pC}");

            bool hitD = simR.Raycast(new Vector3(0.5f, 70.5f, 0.5f), new Vector3(1f, 0f, 1f), 20f, out Vector3Int hD, out Vector3Int pD);
            bool diagOk = hitD && hD == new Vector3Int(12, 70, 12) &&
                          (pD == new Vector3Int(11, 70, 12) || pD == new Vector3Int(12, 70, 11));
            Eval("m5.raycast_diagonal", diagOk, $"hit={hD} place={pD}");

            bool hitE = simR.Raycast(new Vector3(0.5f, 70.5f, 5.5f), Vector3.right, 6f, out _, out _);
            Eval("m5.raycast_reach", !hitE, "block at 13.5m is outside 6m reach");

            // ----- M7A: audio library -----
            AudioLibrary.Build();
            bool audioOk = AudioLibrary.totalClips >= 40;
            string[] audioNeed = { "grass", "dirt", "sand", "stone", "wood", "glass", "snow", "ice" };
            foreach (string grp in audioNeed)
            {
                var set = AudioLibrary.GetGroup(grp);
                audioOk &= set != null && set.dig != null && set.dig.Length > 0;
            }
            Eval("m7a.audio_library", audioOk,
                $"clips={AudioLibrary.totalClips}, groups={AudioLibrary.groups.Count}");

            // ----- M7B: ores, gravel pockets, frozen lakes, hotbar pages -----
            int coal = 0, iron = 0, gold = 0, diamond = 0, gravelCount = 0, obsidian = 0;
            for (int cz2 = -2; cz2 <= 2; cz2++)
            {
                for (int cx2 = -2; cx2 <= 2; cx2++)
                {
                    var c = new Chunk(cx2, cz2);
                    gen.Generate(c);
                    for (int i2 = 0; i2 < c.blocks.Length; i2++)
                    {
                        switch ((BlockType)c.blocks[i2])
                        {
                            case BlockType.CoalOre: coal++; break;
                            case BlockType.IronOre: iron++; break;
                            case BlockType.GoldOre: gold++; break;
                            case BlockType.DiamondOre: diamond++; break;
                            case BlockType.Gravel: gravelCount++; break;
                            case BlockType.Obsidian: obsidian++; break;
                        }
                    }
                }
            }
            Eval("m7b.ores", coal > 20 && iron > 10 && gravelCount > 20 && obsidian > 0,
                $"coal={coal} iron={iron} gold={gold} diamond={diamond} gravel={gravelCount} obsidian={obsidian}");

            int iceX = int.MinValue, iceZ = 0;
            for (int z3 = -800; z3 <= 800 && iceX == int.MinValue; z3 += 8)
            {
                for (int x3 = -800; x3 <= 800; x3 += 8)
                {
                    if (gen.BiomeAt(x3, z3) < TerrainGenerator.ColdWaterThreshold &&
                        gen.HeightAt(x3, z3) < VoxelMath.SeaLevel - 2)
                    {
                        iceX = x3;
                        iceZ = z3;
                        break;
                    }
                }
            }
            bool iceOk = false;
            if (iceX != int.MinValue)
            {
                var iceChunk = new Chunk(VoxelMath.ChunkCoord(iceX), VoxelMath.ChunkCoord(iceZ));
                gen.Generate(iceChunk);
                foreach (byte b in iceChunk.blocks)
                {
                    if ((BlockType)b == BlockType.Ice)
                    {
                        iceOk = true;
                        break;
                    }
                }
            }
            Eval("m7b.ice", iceOk,
                iceX == int.MinValue ? "no cold water column found" : $"cold water at ({iceX},{iceZ}) freezes");

            var biGo = new GameObject("BiTest");
            var bi = biGo.AddComponent<BlockInteraction>();
            bool pagesOk = bi.hotbarPages.Length == 2 && bi.hotbar.Length == 9;
            for (int p = 0; p < bi.hotbarPages.Length && pagesOk; p++)
            {
                for (int s = 0; s < bi.hotbarPages[p].Length; s++)
                {
                    if (!BlockDatabase.Get(bi.hotbarPages[p][s]).placeable)
                    {
                        pagesOk = false;
                    }
                }
            }
            Eval("m7b.hotbar_pages", pagesOk, $"pages={bi.hotbarPages.Length}, all slots placeable={pagesOk}");
            UnityEngine.Object.DestroyImmediate(biGo);

            // ----- M7C: creature skins, models, surface query -----
            bool creatureMats = true;
            foreach (string sp in new[] { "pig", "cow", "sheep", "chicken" })
            {
                foreach (string part in new[] { "body", "face", "leg" })
                {
                    var m = CreatureTextureFactory.Get(sp, part);
                    if (m == null || m.mainTexture == null)
                    {
                        creatureMats = false;
                    }
                }
            }
            Eval("m7c.creature_skins", creatureMats, "12 species-part materials built");

            int sh = simR.SurfaceHeight(0, 0);
            int manualSurface = -1;
            for (int y = VoxelMath.ChunkHeight - 1; y >= 0; y--)
            {
                var b = simR.GetBlock(0, y, 0);
                if (b != BlockType.Air && !BlockDatabase.IsLiquid(b))
                {
                    manualSurface = y;
                    break;
                }
            }
            simR.SetBlock(0, manualSurface + 5, 0, BlockType.Stone, null);
            int sh2 = simR.SurfaceHeight(0, 0);
            Eval("m7c.surface_height", sh == manualSurface && sh2 == manualSurface + 5,
                $"surface {sh}=={manualSurface}, after placement {sh2}=={manualSurface + 5}");

            var animalGo = new GameObject("AnimalTest");
            var animal = animalGo.AddComponent<Creatures.BlockyAnimal>();
            animal.species = "cow";
            animal.BuildModel();
            int animalParts = animalGo.GetComponentsInChildren<Transform>().Length;
            UnityEngine.Object.DestroyImmediate(animalGo);
            Eval("m7c.animal_model", animalParts >= 11, $"{animalParts} transforms in cow model");

            // ----- M7D: third-person player model -----
            var rigGo = new GameObject("RigTest");
            var rig = rigGo.AddComponent<Player.ThirdPersonRig>();
            rig.BuildModel();
            int rigParts = rigGo.GetComponentsInChildren<Transform>(true).Length;
            UnityEngine.Object.DestroyImmediate(rigGo);
            Eval("m7d.player_model", rigParts >= 10, $"{rigParts} transforms in player model (inactive included)");

            // ----- M8: tools, inventory, drops, tree-aware surface -----
            bool handSoft = ToolRules.BreakRule(ToolType.Hand, BlockType.Dirt).allowed;
            bool handStone = ToolRules.BreakRule(ToolType.Hand, BlockType.Stone).allowed;
            bool pickStone = ToolRules.BreakRule(ToolType.Pickaxe, BlockType.Stone).allowed;
            bool swordStone = ToolRules.BreakRule(ToolType.Sword, BlockType.Stone).allowed;
            float axeWood = ToolRules.BreakRule(ToolType.Axe, BlockType.Log).interval;
            bool handWoodOk = ToolRules.BreakRule(ToolType.Hand, BlockType.Log).allowed;
            float handWoodTime = ToolRules.BreakRule(ToolType.Hand, BlockType.Log).interval;
            bool clockBreaks = ToolRules.BreakRule(ToolType.Clock, BlockType.Dirt).allowed;
            bool pickBedrock = ToolRules.BreakRule(ToolType.Pickaxe, BlockType.Bedrock).allowed;
            Eval("m8.tool_rules",
                handSoft && !handStone && pickStone && !swordStone && handWoodOk && axeWood <= 0.25f &&
                handWoodTime >= 0.6f && !clockBreaks && !pickBedrock,
                "hand=soft+slow-wood, pick=stone-only, sword=not-stone, clock harmless, bedrock immortal");

            Inventory.Reset();
            Inventory.Add(Inventory.DropFor(BlockType.Grass));
            Inventory.Add(Inventory.DropFor(BlockType.Grass));
            Inventory.Add(Inventory.DropFor(BlockType.Stone));
            bool invA = Inventory.Get(BlockType.Dirt) == 2 && Inventory.Get(BlockType.Cobble) == 1;
            Inventory.Add(Inventory.DropFor(BlockType.Leaves));
            bool invB = Inventory.Get(BlockType.Leaves) == 0;
            bool consumeOk = Inventory.TryConsume(BlockType.Dirt) && Inventory.Get(BlockType.Dirt) == 1;
            bool consumeEmpty = !Inventory.TryConsume(BlockType.Snow);
            Inventory.creative = true;
            bool creativePlace = Inventory.TryConsume(BlockType.Ice) && Inventory.Get(BlockType.Ice) == 0;
            Inventory.Reset();
            Eval("m8.inventory", invA && invB && consumeOk && consumeEmpty && creativePlace,
                "drops (grass->dirt, stone->cobble, leaves->none), consume gating, creative bypass");

            ItemDrops.world = null; // editor-safe: null context is tolerated
            ItemDrops.player = null;
            ItemDrops.iconOf = null;
            ItemDrops.ClearAll();
            ItemDrops.Spawn(BlockType.Dirt, Vector3.zero);
            ItemDrops.Spawn(BlockType.Leaves, Vector3.zero); // drop-table filtering lives in BlockInteraction, Spawn only skips Air
            ItemDrops.Spawn(BlockType.Air, Vector3.zero);     // skipped
            ItemDrops.SpawnMeat(Vector3.zero, 2);
            int spawned = ItemDrops.LiveCount;
            bool dropsOk = spawned == 4; // dirt + leaves + 2 meat
            ItemDrops.ClearAll();
            bool dropsCleared = ItemDrops.LiveCount == 0;
            Eval("m8.item_drops", dropsOk && dropsCleared,
                $"spawned={spawned} (air skipped), cleared={dropsCleared}");

            simR.SetBlock(3, 72, 3, BlockType.Leaves, null);
            simR.SetBlock(3, 71, 3, BlockType.Log, null);
            int shRaw = simR.SurfaceHeight(3, 3);
            int shTree = simR.SurfaceHeight(3, 3, true);
            Eval("m8.surface_trees", shRaw == 72 && shTree < 71 && shTree >= 0,
                $"raw={shRaw}, ignoring trees={shTree}");

            // ----- M8b: farming -----
            bool wheatFlag = Items.Crops.IsWheat(BlockType.Wheat2) && !Items.Crops.IsWheat(BlockType.Dirt);
            bool mature = Items.Crops.IsMature(BlockType.Wheat3) && !Items.Crops.IsMature(BlockType.Wheat2);
            bool soil = Items.Crops.IsSoil(BlockType.Grass) && Items.Crops.IsSoil(BlockType.Dirt) &&
                        !Items.Crops.IsSoil(BlockType.Stone);
            Inventory.Reset();
            Items.Crops.Harvest(BlockType.Wheat3); // mature: 1-2 carrots + 1 seed
            bool harvestOk = Inventory.carrots >= 1 && Inventory.seeds >= 1;
            Items.Crops.Harvest(BlockType.Wheat1); // immature: seed only
            bool harvestImmature = Inventory.carrots <= 3 && Inventory.seeds >= 2;
            bool seedsGate = !Inventory.TryConsumeSeeds(99) && Inventory.seeds >= 0;
            Eval("m8b.crops", wheatFlag && mature && soil && harvestOk && harvestImmature && seedsGate,
                $"mature={harvestOk} immature-seed={harvestImmature} seed-gate={seedsGate}");

            // ----- M9: real skins -----
            var pigSkin = CreatureTextureFactory.GetSkinMaterial("pig_skin");
            var cowSkin = CreatureTextureFactory.GetSkinMaterial("cow_skin");
            var sheepSkin = CreatureTextureFactory.GetSkinMaterial("sheep_skin");
            var chickenSkin = CreatureTextureFactory.GetSkinMaterial("chicken_skin");
            var playerSkin = CreatureTextureFactory.GetSkinMaterial("player_skin");
            bool skinsOk = pigSkin != null && cowSkin != null && sheepSkin != null &&
                           chickenSkin != null && playerSkin != null;
            Eval("m9.skin_sheets", skinsOk,
                $"pig={(pigSkin != null)} cow={(cowSkin != null)} sheep={(sheepSkin != null)} " +
                $"chicken={(chickenSkin != null)} player={(playerSkin != null)}");

            var skinTex = playerSkin != null ? playerSkin.mainTexture as Texture2D : null;
            bool missingGone = CreatureTextureFactory.GetSkinMaterial("no_such_skin") == null;
            Eval("m9.skin_layout", skinTex != null && skinTex.width == 64 && skinTex.height == 32 && missingGone,
                $"player sheet {skinTex?.width}x{skinTex?.height}, absent skins return null");

            var skinnedPigGo = new GameObject("SkinPigTest");
            var skinnedPig = skinnedPigGo.AddComponent<Creatures.BlockyAnimal>();
            skinnedPig.species = "pig";
            skinnedPig.BuildModel();
            int skinnedPigParts = skinnedPigGo.GetComponentsInChildren<Transform>(true).Length;
            UnityEngine.Object.DestroyImmediate(skinnedPigGo);
            Eval("m9.animal_skinned", skinnedPigParts >= 11, $"{skinnedPigParts} transforms in pig model");

            var skinnedRigGo = new GameObject("SkinRigTest");
            var skinnedRig = skinnedRigGo.AddComponent<Player.ThirdPersonRig>();
            skinnedRig.BuildModel();
            int skinnedRigParts = skinnedRigGo.GetComponentsInChildren<Transform>(true).Length;
            UnityEngine.Object.DestroyImmediate(skinnedRigGo);
            Eval("m9.player_skinned", skinnedRigParts >= 11, $"{skinnedRigParts} transforms in player model (inactive included)");

            // Every face rect of every species net must sit inside the 64x32
            // sheet and sample at least one visible pixel (catches UV layout
            // regressions like mismatched MC-rotated torso nets).
            bool uvOk = true;
            string uvBad = "";
            foreach (string sp in new[] { "pig", "cow", "sheep", "chicken" })
            {
                var spSkin = CreatureTextureFactory.GetSkinMaterial(sp + "_skin");
                var spTex = spSkin != null ? spSkin.mainTexture as Texture2D : null;
                if (spTex == null)
                {
                    uvOk = false;
                    uvBad = "no sheet for " + sp;
                    break;
                }
                Creatures.BlockyAnimal.GetSpeciesNets(sp, out var bodyNet, out _, out var headNet, out var legNet);
                if (!NetHasPixels(spTex, bodyNet) || !NetHasPixels(spTex, headNet) || !NetHasPixels(spTex, legNet))
                {
                    uvOk = false;
                    uvBad = "empty or out-of-bounds rect in " + sp + " nets";
                    break;
                }
            }
            if (uvOk)
            {
                var pTex = playerSkin != null ? playerSkin.mainTexture as Texture2D : null;
                if (pTex == null ||
                    !NetHasPixels(pTex, BoxBuilder.McNet(0, 0, 8, 8, 8)) ||
                    !NetHasPixels(pTex, BoxBuilder.McNet(16, 16, 8, 12, 4)) ||
                    !NetHasPixels(pTex, BoxBuilder.McNet(40, 16, 4, 12, 4)) ||
                    !NetHasPixels(pTex, BoxBuilder.McNet(0, 16, 4, 12, 4)))
                {
                    uvOk = false;
                    uvBad = "empty or out-of-bounds rect in player nets";
                }
            }
            Eval("m9.skin_uv_nets", uvOk,
                uvOk ? "every face rect in-bounds with visible pixels" : uvBad);

            // ----- M10: held item models -----
            bool heldOk = true;
            string heldBad = "ok";
            foreach (ToolType heldTool in System.Enum.GetValues(typeof(ToolType)))
            {
                var toolGo = VoxelCraft.Art.ItemModelFactory.BuildTool(heldTool);
                var toolMf = toolGo != null ? toolGo.GetComponent<MeshFilter>() : null;
                if (toolMf == null || toolMf.sharedMesh == null || toolMf.sharedMesh.triangles.Length == 0)
                {
                    heldOk = false;
                    heldBad = "missing/empty tool model: " + heldTool;
                    break;
                }
            }
            if (heldOk)
            {
                foreach (string food in new[] { "meat", "seeds", "carrot" })
                {
                    var foodGo = VoxelCraft.Art.ItemModelFactory.BuildFood(food);
                    var foodMf = foodGo != null ? foodGo.GetComponent<MeshFilter>() : null;
                    if (foodMf == null || foodMf.sharedMesh == null || foodMf.sharedMesh.triangles.Length == 0)
                    {
                        heldOk = false;
                        heldBad = "missing/empty food model: " + food;
                        break;
                    }
                }
            }
            if (heldOk && atlas != null)
            {
                for (int t = 1; t <= 22; t++)
                {
                    var bt = (BlockType)t;
                    var blockGo = VoxelCraft.Art.ItemModelFactory.BuildBlock(atlas, bt);
                    var blockMf = blockGo != null ? blockGo.GetComponent<MeshFilter>() : null;
                    var blockMr = blockGo != null ? blockGo.GetComponent<MeshRenderer>() : null;
                    if (blockMf == null || blockMr == null || blockMf.sharedMesh == null ||
                        blockMr.sharedMaterial == null || blockMr.sharedMaterial.mainTexture == null)
                    {
                        heldOk = false;
                        heldBad = "missing block model: " + bt;
                        break;
                    }
                }
            }
            Eval("m10.item_models", heldOk,
                heldOk ? "tool/food/block held models all have meshes" : heldBad);

            // Held item view builds its anchors even without references.
            var heldGo = new GameObject("SelfTestHeldView");
            var heldView = heldGo.AddComponent<VoxelCraft.Player.HeldItemView>();
            heldView.Build();
            bool anchorsOk = heldView.AnchorsReady;
            UnityEngine.Object.DestroyImmediate(heldGo);
            Eval("m10.held_view", anchorsOk,
                anchorsOk ? "anchors created without refs" : "anchor build failed");

            Debug.Log($"SELFTEST SUMMARY pass={pass} fail={fail} | unity={Application.unityVersion}");
            Debug.Log(fail > 0 ? "SELFTEST RESULT: FAIL" : "SELFTEST RESULT: PASS");

            void Eval(string name, bool ok, string detail)
            {
                if (ok) pass++; else fail++;
                Report(name, ok, detail);
            }
        }

        private static readonly Vector3Int[] FaceOffsets =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        /// <summary>Independent replication of the mesher visibility rules.</summary>
        private static bool TestFaceVisible(BlockType b, BlockType nb)
        {
            if (b == BlockType.Water)
            {
                return nb == BlockType.Air ||
                       (!BlockDatabase.IsOpaque(nb) && nb != BlockType.Water && nb != BlockType.Glass);
            }
            if (BlockDatabase.IsOpaque(b))
            {
                return !BlockDatabase.IsOpaque(nb);
            }
            return !BlockDatabase.IsOpaque(nb) && nb != b;
        }

        /// <summary>
        /// True when every rect of a skin net (Vector4 = u,v,w,h, v from top)
        /// lies inside the sheet and holds at least one visible pixel. The -Y
        /// bottom face (index 3) only needs to be in-bounds: MC artists often
        /// leave those hidden faces fully transparent (e.g. chicken head).
        /// </summary>
        private static bool NetHasPixels(Texture2D tex, Vector4[] net)
        {
            if (tex == null)
            {
                return false;
            }
            for (int i = 0; i < net.Length; i++)
            {
                var r = net[i];
                if (r.x < 0f || r.y < 0f || r.x + r.z > tex.width || r.y + r.w > tex.height)
                {
                    return false;
                }
                if (i == 3)
                {
                    continue;
                }
                bool any = false;
                for (int s = 0; s < 5 && !any; s++)
                {
                    float fx = s == 4 ? 0.5f : (s == 0 ? 0.15f : s == 1 ? 0.85f : s == 2 ? 0.15f : 0.85f);
                    float fy = s == 4 ? 0.5f : (s == 0 || s == 1 ? 0.5f : (s == 2 ? 0.15f : 0.85f));
                    int px = Mathf.Clamp(Mathf.RoundToInt(r.x + r.z * fx), 0, tex.width - 1);
                    int pyTop = Mathf.Clamp(Mathf.RoundToInt(r.y + r.w * fy), 0, tex.height - 1);
                    // rect v counts from the top, GetPixel counts from the bottom
                    if (tex.GetPixel(px, tex.height - 1 - pyTop).a > 0.02f)
                    {
                        any = true;
                    }
                }
                if (!any)
                {
                    return false;
                }
            }
            return true;
        }

        private static TextureFactory.AtlasResult SafeBuildAtlas()
        {
            try
            {
                return TextureFactory.Build();
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                return null;
            }
        }

        private static void Report(string name, bool ok, string detail)
        {
            Debug.Log(ok
                ? $"SELFTEST PASS {name} : {detail}"
                : $"SELFTEST FAIL {name} : {detail}");
        }
    }
}
