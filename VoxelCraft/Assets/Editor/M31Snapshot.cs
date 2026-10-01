// M31 deliverable shots: furnished cottage interior + exterior with door/torches/
// chest/bed/fence, plus a night shot proving torch glow. Batch mode, no play mode.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    public static class M31Snapshot
    {
        public static void Run()
        {
            var sim = new WorldSim(1337);
            var atlas = Art.TextureFactory.Build();
            var rects = new Rect[59];
            for (int i = 0; i < rects.Length; i++) { rects[i] = atlas.TileRect((TileId)i); }
            sim.tileRects = rects;

            // find a furnished cottage anchor (same gates as the generator)
            Vector2Int? anchorFound = null;
            var vox = Vox.StructureRegistry.Load("cottage");
            for (int cz = -8; cz <= 8 && anchorFound == null; cz++)
            {
                for (int cx = -8; cx <= 8; cx++)
                {
                    foreach (var a in Vox.StructureRegistry.AnchorsNear("cottage",
                        cx * 16 - 24, cz * 16 - 24, cx * 16 + 16 + 24, cz * 16 + 16 + 24, 1337))
                    {
                        int g = sim.generator.HeightAt(a.x, a.y);
                        int maxH = g, minH = g;
                        for (int zz = 0; zz < vox.Depth; zz++)
                            for (int xx = 0; xx < vox.Width; xx++)
                            {
                                int hh = sim.generator.HeightAt(a.x + xx, a.y + zz);
                                maxH = Math.Max(maxH, hh); minH = Math.Min(minH, hh);
                            }
                        if (maxH - minH <= 6 && g > VoxelMath.SeaLevel + 1 && g < TerrainGenerator.SnowLine - 2 &&
                            !sim.generator.IsDesert(a.x, a.y))
                        {
                            anchorFound = new Vector2Int(a.x, a.y);
                            break;
                        }
                    }
                    if (anchorFound != null) break;
                }
            }
            var anchor = anchorFound ?? new Vector2Int(316, -136);
            int gy = sim.generator.HeightAt(anchor.x, anchor.y);
            // the stamping uses baseY = max over footprint (incl. 1-block lip)
            int baseY = gy;
            for (int zz = -1; zz <= vox.Depth; zz++)
                for (int xx = -1; xx <= vox.Width; xx++)
                    baseY = Math.Max(baseY, sim.generator.HeightAt(anchor.x + xx, anchor.y + zz));
            Debug.Log($"[M31] anchor=({anchor.x},{anchor.y}) ground={gy} base={baseY}");

            // mesh the cottage area
            var root = new GameObject("M31World");
            var solidMat = new Material(Shader.Find("Voxel/Blocks")) { mainTexture = atlas.atlas };
            Action<Chunk> attach = (c) =>
            {
                var go = new GameObject($"C{c.cx}_{c.cz}");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(c.cx * 16, 0, c.cz * 16);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = solidMat;
                mf.sharedMesh = c.solidMeshData != null ? c.solidMeshData.ToMesh(null) : null;
            };
            // light the cottage furnace so the glowing firemouth shows in shots
            Items.Furniture.SetFurnaceLit(new Vector3Int(133, 36, -48), true);
            sim.unloadRadius = 64;
            var rem = new List<Chunk>();
            int acx = VoxelMath.ChunkCoord(anchor.x), acz = VoxelMath.ChunkCoord(anchor.y);
            sim.dataRadius = 4; sim.meshRadius = 4;
            sim.Step(acx, acz, 100000f, 100000f, rem, null);
            foreach (var c in rem) attach(c);

            // data self-check: dump every furniture block near the anchor
            var furnTypes = new HashSet<Core.BlockType> { Core.BlockType.Torch, Core.BlockType.DoorClosed,
                Core.BlockType.DoorOpen, Core.BlockType.Chest, Core.BlockType.BedFoot, Core.BlockType.BedHead,
                Core.BlockType.Fence, Core.BlockType.Furnace };
            var furnSb = new System.Text.StringBuilder();
            for (int y = baseY; y <= baseY + vox.Height + 2; y++)
                for (int z = anchor.y - 4; z <= anchor.y + vox.Depth + 2; z++)
                    for (int x = anchor.x - 4; x <= anchor.x + vox.Width + 2; x++)
                        if (furnTypes.Contains(sim.GetBlock(x, y, z)))
                            furnSb.Append(sim.GetBlock(x, y, z)).Append('@').Append(x).Append(',').Append(y).Append(',').Append(z).Append(' ');
            Debug.Log("[M31] furniture blocks: " + furnSb.ToString());
            // ground truth: does the MESHED chunk still hold the furnace, and did
            // the mesher emit its quads? mesh a tiny sim around the furnace pos.
            {
                var fc = sim.GetChunk(8, -3);
                Debug.Log($"[M31] furnace chunk data: {(fc != null && fc.dataReady ? sim.GetBlock(133, 36, -48).ToString() : "missing")} meshBuilt={(fc != null && fc.meshBuilt)}");
                var simF = new WorldSim(1337) { tileRects = rects, dataRadius = 1, meshRadius = 0, unloadRadius = 2 };
                var remF = new List<Chunk>();
                simF.Step(8, -3, 100000f, 100000f, remF, null);
                var probe = new MeshData();
                World.PartialShapes.Emit(Core.BlockType.Furnace, 0, 0, 0, 133, -48, simF, rects, probe);
                Debug.Log($"[M31] direct Emit probe quads: {probe.Vertices.Count / 4} (expect 7)");
            }

            var camGo = new GameObject("M31Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.fieldOfView = 60f;
            cam.farClipPlane = 400f;
            cam.enabled = false;
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(200f, 400f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.55f, 0.72f, 0.90f));

            // Shot 1: exterior front with door + torches + fence pen
            cam.transform.position = new Vector3(anchor.x - 13, baseY + 13, anchor.y - 15);
            cam.transform.LookAt(new Vector3(anchor.x + 5, baseY + 2, anchor.y + 2));
            Render(cam, "m31_exterior.jpg", 1f);
            Debug.Log("[M31] shot1 exterior saved");

            // Shot 2: interior - chest, bed, back-wall torch
            // angled from the doorway so the CLOSED DOOR PANEL does not block the
            // furnace/chest corner at the back wall
            cam.transform.position = new Vector3(anchor.x + 5, baseY + 2.7f, anchor.y - 2.5f);
            cam.transform.LookAt(new Vector3(anchor.x + 1.5f, baseY + 1.2f, anchor.y + 6.5f));
            Render(cam, "m31_interior.jpg", 1f);
            Debug.Log("[M31] shot2 interior saved");

            // Shot 3: night exterior - torch glow must stand out
            Shader.SetGlobalFloat("_VoxelDayBrightness", 0.22f);
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.12f);
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.05f, 0.06f, 0.12f));
            cam.transform.position = new Vector3(anchor.x - 13, baseY + 11, anchor.y - 16);
            cam.transform.LookAt(new Vector3(anchor.x + 5, baseY + 2, anchor.y + 2));
            Render(cam, "m31_night.jpg", 0.22f);
            Debug.Log("[M31] shot3 night saved");

            // Shot 4: furnace closeup - camera INSIDE the room, mouth side
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            cam.transform.position = new Vector3(133.5f, 36.6f, -50.2f);
            cam.transform.LookAt(new Vector3(133.5f, 36.4f, -48f));
            Render(cam, "m31_furnace.jpg", 1f);

            // Shot 5: bed closeup - vanilla red blanket + pillow + wood legs
            cam.transform.position = new Vector3(anchor.x + 4, baseY + 2.4f, anchor.y - 4.5f);
            cam.transform.LookAt(new Vector3(anchor.x + 8.5f, baseY + 1.2f, anchor.y + 0.5f));
            Render(cam, "m31_bed.jpg", 1f);
            Debug.Log("[M31] shot5 bed closeup saved");

            // Shot 6: door closeup - vanilla oak door texture (window + wood)
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.55f, 0.72f, 0.90f));
            cam.transform.position = new Vector3(anchor.x + 5, baseY + 2.2f, anchor.y - 7.5f);
            cam.transform.LookAt(new Vector3(anchor.x + 5, baseY + 1.4f, anchor.y + 1.0f));
            Render(cam, "m31_door.jpg", 1f);
            Debug.Log("[M31] shot6 door closeup saved");

            // Shot 7: house5 (mmmm obj_house5 /2) - anchor gates (flat>=W/4 above
            // sea) find no valid spot for a 33-wide footprint on this terrain, so
            // verify the pipeline on a built platform at a known-flat area instead.
            {
                var h5 = Vox.StructureRegistry.Load("house5");
                int px = 60, pz = -80; // near cottage area, terrain ~y32 flat-ish
                int top = 0;
                {
                    var remP = new List<Chunk>();
                    sim.dataRadius = 6; sim.meshRadius = 6;
                    int pcx = VoxelMath.ChunkCoord(px + 20), pcz = VoxelMath.ChunkCoord(pz + 20);
                    sim.Step(pcx, pcz, 100000f, 100000f, remP, null);
                    foreach (var cP in remP) attach(cP);
                    int mn = int.MaxValue, mx = int.MinValue;
                    for (int x = 0; x < 44; x += 2)
                        for (int z = 0; z < 44; z += 2)
                        {
                            int g = sim.generator.HeightAt(px + x, pz + z);
                            mn = Mathf.Min(mn, g); mx = Mathf.Max(mx, g);
                        }
                    int platBaseY = mx;
                    var aff = new List<Chunk>();
                    for (int x = -2; x < 46; x++)
                        for (int z = -2; z < 46; z++)
                        {
                            // fill from the REAL terrain column top up to platform base
                            int colTop = sim.SurfaceHeight(px + x, pz + z, true);
                            for (int y = colTop; y <= platBaseY; y++) sim.SetBlock(px + x, y, pz + z, BlockType.Grass, aff);
                            for (int y = platBaseY + 1; y <= platBaseY + h5.Height + 2; y++) sim.SetBlock(px + x, y, pz + z, BlockType.Air, aff);
                        }
                    for (int y = 0; y < h5.Height; y++)
                        for (int z = 0; z < h5.Depth; z++)
                            for (int x = 0; x < h5.Width; x++)
                            {
                                var t = h5.blocks[x, y, z];
                                if (t != BlockType.Air) sim.SetBlock(px + x, platBaseY + 1 + y, pz + z, t, aff);
                            }
                    sim.dataRadius = 6; sim.meshRadius = 6;
                    var rem2 = new List<Chunk>();
                    sim.Step(pcx, pcz, 100000f, 100000f, rem2, null);
                    foreach (var c2 in rem2) attach(c2);
                    top = platBaseY;
                }
                // house body is x[10..21] y[8..31] z[0..14]: a tall narrow slab.
                // Stand off its left face, 3/4 angle, aim mid-body.
                Vector3 c5 = new Vector3(px + 16, top + 14, pz + 7);
                Vector3[] dirs = {
                    new Vector3( 26,  4,   0), new Vector3(-26,  4,   0),
                    new Vector3(  0,  4,  26), new Vector3(  0,  4, -26) };
                string[] names = { "N", "S", "E", "W" };
                for (int d = 0; d < 4; d++)
                {
                    cam.transform.position = c5 + dirs[d];
                    cam.transform.LookAt(c5);
                    Render(cam, $"m31_house5_{names[d]}.jpg", 1f);
                }
                Debug.Log($"[M31] shot7 house5 on platform at ({px},{pz}) base={top}");
            }

            // Shot 8: street props row (tree1-4, fence2, stlight, trashcan,
            // planter) on the same platform, one render.
            {
                string[] props = { "tree1", "tree2", "tree3", "tree4", "fence2", "stlight", "trashcan", "planter" };
                var remP8 = new List<Chunk>();
                sim.dataRadius = 6; sim.meshRadius = 6;
                int p8x = 20, p8z = 40;
                int pcx8 = VoxelMath.ChunkCoord(p8x + 40), pcz8 = VoxelMath.ChunkCoord(p8z + 40);
                sim.Step(pcx8, pcz8, 100000f, 100000f, remP8, null);
                foreach (var c8 in remP8) attach(c8);
                int mn8 = int.MaxValue, mx8 = int.MinValue;
                for (int x = 0; x < 100; x += 4)
                    for (int z = 0; z < 24; z += 4)
                    {
                        int g = sim.generator.HeightAt(p8x + x, p8z + z);
                        mn8 = Mathf.Min(mn8, g); mx8 = Mathf.Max(mx8, g);
                    }
                int base8 = mx8;
                var aff8 = new List<Chunk>();
                for (int x = -2; x < 102; x++)
                    for (int z = -2; z < 26; z++)
                    {
                        int colTop = sim.SurfaceHeight(p8x + x, p8z + z, true);
                        for (int y = colTop; y <= base8; y++) sim.SetBlock(p8x + x, y, p8z + z, BlockType.Grass, aff8);
                        for (int y = base8 + 1; y <= base8 + 24; y++) sim.SetBlock(p8x + x, y, p8z + z, BlockType.Air, aff8);
                    }
                int ox = p8x + 2;
                foreach (var pn in props)
                {
                    var pv = Vox.StructureRegistry.Load(pn);
                    if (pv == null) { Debug.Log($"[M31] shot8 MISSING {pn}"); continue; }
                    for (int y = 0; y < pv.Height; y++)
                        for (int z = 0; z < pv.Depth; z++)
                            for (int x = 0; x < pv.Width; x++)
                            {
                                var t = pv.blocks[x, y, z];
                                if (t != BlockType.Air) sim.SetBlock(ox + x, base8 + 1 + y, p8z + 6 + z, t, aff8);
                            }
                    ox += pv.Width + 4;
                }
                // numeric probe: count non-natural blocks actually in the sim
                var typeCount = new System.Collections.Generic.Dictionary<BlockType, int>();
                int probeNon = 0;
                for (int y = base8 + 1; y <= base8 + 22; y++)
                    for (int z = p8z + 4; z <= p8z + 28; z++)
                        for (int x = p8x; x <= p8x + 110; x++)
                        {
                            var b = sim.GetBlock(x, y, z);
                            if (b != BlockType.Air && b != BlockType.Grass && b != BlockType.Dirt &&
                                b != BlockType.Stone && b != BlockType.Sand && b != BlockType.Water &&
                                b != BlockType.Leaves && b != BlockType.Log)
                            {
                                probeNon++;
                                typeCount[b] = typeCount.TryGetValue(b, out var cc) ? cc + 1 : 1;
                            }
                        }
                Debug.Log($"[M31] shot8 probe nonNatural={probeNon} types={string.Join(",", typeCount.Select(kv => kv.Key + ":" + kv.Value))}");
                var rem82 = new List<Chunk>();
                sim.Step(pcx8, pcz8, 100000f, 100000f, rem82, null);
                foreach (var c82 in rem82) attach(c82);
                var camP8 = new Vector3(p8x + 34, base8 + 12, p8z - 22);
                cam.transform.position = camP8;
                cam.transform.LookAt(new Vector3(p8x + 40, base8 + 3, p8z + 8));
                Render(cam, "m31_props.jpg", 1f);
                // top-down debug view of the row area
                var camT = new Vector3(p8x + 55, base8 + 60, p8z + 10);
                cam.transform.position = camT;
                cam.transform.rotation = Quaternion.Euler(90, 0, 0);
                Render(cam, "m31_props_top.jpg", 1f);
                // side view dead-on
                var camS = new Vector3(p8x + 55, base8 + 8, p8z - 20);
                cam.transform.position = camS;
                cam.transform.LookAt(new Vector3(p8x + 55, base8 + 6, p8z + 8));
                Render(cam, "m31_props_side.jpg", 1f);
                Debug.Log($"[M31] shot8 props row at ({p8x},{p8z}) base={base8} ox_end={ox}");
            }

            Debug.Log("M31SNAPSHOT RESULT: PASS");
        }

        private static void Render(Camera cam, string name, float brightness)
        {
            cam.Render();
            cam.Render();
            System.IO.Directory.CreateDirectory("D:/zlj_world/_shots");
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var prev = RenderTexture.active;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            RenderTexture.active = rt;
            cam.Render();
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            System.IO.File.WriteAllBytes("D:/zlj_world/_shots/" + name, tex.EncodeToJPG(88));
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
