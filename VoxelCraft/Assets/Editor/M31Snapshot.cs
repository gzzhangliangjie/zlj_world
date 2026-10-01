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
                // idempotent: reuse the same GameObject per chunk so remeshes
                // REPLACE the mesh instead of stacking duplicate overlays (a
                // stale first-generation mesh with world trees would otherwise
                // stay in the scene forever and photobomb every later shot).
                var go = root.transform.Find($"C{c.cx}_{c.cz}");
                if (go == null)
                {
                    var g = new GameObject($"C{c.cx}_{c.cz}");
                    go = g.transform;
                    go.SetParent(root.transform, false);
                    go.localPosition = new Vector3(c.cx * 16, 0, c.cz * 16);
                    g.AddComponent<MeshFilter>();
                    var mr = g.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = solidMat;
                }
                var mf2 = go.GetComponent<MeshFilter>();
                var stale = mf2.sharedMesh;
                mf2.sharedMesh = c.solidMeshData != null ? c.solidMeshData.ToMesh(null) : null;
                if (stale != null) UnityEngine.Object.DestroyImmediate(stale); // free the replaced mesh NOW, not at domain reload
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

            // Shot 7: house5 - engine tol now max(6, W/2)=16 so real anchors pass.
            // Try a natural anchor first; fall back to the platform.
            {
                var h5 = Vox.StructureRegistry.Load("house5");
                Vector2Int? nat = null;
                foreach (var a in Vox.StructureRegistry.AnchorsNear("house5", -320, -320, 320, 320, 1337))
                {
                    if (sim.generator.IsDesert(a.x, a.y)) continue;
                    int mnA = int.MaxValue, mxA = int.MinValue;
                    for (int x = 0; x < h5.Width; x += 3)
                        for (int z = 0; z < h5.Depth; z += 3)
                        {
                            int g = sim.generator.HeightAt(a.x + x, a.y + z);
                            mnA = Mathf.Min(mnA, g); mxA = Mathf.Max(mxA, g);
                        }
                    if (mxA - mnA <= 16 && mnA > VoxelMath.SeaLevel + 1 && mxA < TerrainGenerator.SnowLine - 2)
                    {
                        nat = a;
                        break;
                    }
                }
                Debug.Log($"[M31] shot7 natural anchor={(nat.HasValue ? nat.Value.ToString() : "NONE")}");
                if (nat.HasValue)
                {
                    var an = nat.Value;
                    int hcx = VoxelMath.ChunkCoord(an.x + 16), hcz = VoxelMath.ChunkCoord(an.y + 16);
                    sim.dataRadius = 5; sim.meshRadius = 5;
                    var remN = new List<Chunk>();
                    sim.Step(hcx, hcz, 100000f, 100000f, remN, null);
                    foreach (var cN in remN) attach(cN);
                    int bn = 0;
                    for (int x = 0; x < h5.Width; x += 2)
                        for (int z = 0; z < h5.Depth; z += 2)
                            bn = Mathf.Max(bn, sim.generator.HeightAt(an.x + x, an.y + z));
                    // numeric probe of the natural house: block census + wall heights
                    var census = new System.Collections.Generic.Dictionary<BlockType, int>();
                    int probeCount = 0;
                    for (int y = bn; y < bn + 40; y++)
                        for (int z = an.y - 2; z <= an.y + 34; z++)
                            for (int x = an.x - 2; x <= an.x + 34; x++)
                            {
                                var b = sim.GetBlock(x, y, z);
                                if (b != BlockType.Air && b != BlockType.Grass && b != BlockType.Dirt &&
                                    b != BlockType.Stone && b != BlockType.Sand && b != BlockType.Water &&
                                    b != BlockType.Snow && b != BlockType.Leaves && b != BlockType.Log)
                                {
                                    probeCount++;
                                    census[b] = census.TryGetValue(b, out var ccc) ? ccc + 1 : 1;
                                }
                            }
                    Debug.Log($"[M31] shot7 natural census n={probeCount} {string.Join(",", census.Select(kv => kv.Key + ":" + kv.Value))}");
                    cam.transform.position = new Vector3(an.x - 26, bn + 30, an.y + 46);
                    cam.transform.LookAt(new Vector3(an.x + 16, bn + 10, an.y + 14));
                    Render(cam, "m31_house5_natural.jpg", 1f);
                    Debug.Log($"[M31] shot7 NATURAL house5 at ({an.x},{an.y}) base~{bn}");
                }
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
                // Per-item SOLO shots: one prop alone on a SKY STUDIO platform
                // lifted high above terrain (base8+40) so the backdrop is pure
                // sky - no world trees photobombing 2-tall props. Camera fitted
                // from the prop's known WxHxD via the frustum formula.
                string[] propsOrder = { "tree1", "tree2", "tree3", "tree4", "fence2", "stlight", "trashcan", "planter", "bench3", "bench4", "hydrant", "pumpkin", "trlight2", "newsbox1", "cone1" };
                int spotX = p8x + 40, spotZ = p8z + 12;
                int studioY = Mathf.Min(base8 + 55, VoxelMath.ChunkHeight - 20); // high but INSIDE the chunk (props top out at +19)
                var studioAff = new List<Chunk>();
                for (int x = -40; x < 70; x++)
                    for (int z = -40; z < 70; z++)
                    {
                        // wipe the whole column first so world trees can't poke
                        // through the studio platform, THEN lay floor + air.
                        for (int y = studioY - 16; y < VoxelMath.ChunkHeight; y++) sim.SetBlock(spotX + x, y, spotZ + z, BlockType.Air, studioAff);
                        sim.SetBlock(spotX + x, studioY, spotZ + z, BlockType.Grass, studioAff);
                    }
                foreach (var pn in propsOrder)
                {
                    var pv = Vox.StructureRegistry.Load(pn);
                    if (pv == null) { Debug.Log($"[M31] shot8 MISSING {pn}"); continue; }
                    var soloAff = new List<Chunk>();
                    int placed = 0;
                    for (int y = 0; y < pv.Height; y++)
                        for (int z = 0; z < pv.Depth; z++)
                            for (int x = 0; x < pv.Width; x++)
                            {
                                var t = pv.blocks[x, y, z];
                                if (t != BlockType.Air) { sim.SetBlock(spotX + 6 + x, studioY + 1 + y, spotZ + 6 + z, t, soloAff); placed++; }
                            }
                    // Camera from the frustum formula on the KNOWN box.
                    float fovV = cam.fieldOfView * Mathf.Deg2Rad;
                    float fovH = 2f * Mathf.Atan(Mathf.Tan(fovV * 0.5f) * cam.aspect);
                    float extV = (pv.Height + 6f) / 2f; // + clearance so feet aren't cropped
                    float extHn = (Mathf.Sqrt(pv.Width * pv.Width + pv.Depth * pv.Depth) + 6f) / 2f;
                    float dist = Mathf.Max(extV / Mathf.Tan(fovV * 0.5f), extHn / Mathf.Tan(fovH * 0.5f)) * 1.25f;
                    var center = new Vector3(spotX + 6 + pv.Width / 2f, studioY + 1 + pv.Height / 2f, spotZ + 6 + pv.Depth / 2f);
                    var dir = new Vector3(-0.75f, 0.45f, -0.75f).normalized;
                    cam.transform.position = center + dir * dist;
                    cam.transform.LookAt(center);
                    var remS = new List<Chunk>();
                    sim.Step(pcx8, pcz8, 100000f, 100000f, remS, null);
                    foreach (var cs in remS) attach(cs);
                    // census: what block types is THIS prop made of in-world?
                    var cens = new System.Collections.Generic.Dictionary<BlockType, int>();
                    for (int y = 0; y < pv.Height; y++)
                        for (int z = 0; z < pv.Depth; z++)
                            for (int x = 0; x < pv.Width; x++)
                            {
                                var b = sim.GetBlock(spotX + 6 + x, studioY + 1 + y, spotZ + 6 + z);
                                if (b != BlockType.Air) cens[b] = cens.TryGetValue(b, out var n0) ? n0 + 1 : 1;
                            }
                    Debug.Log($"[M31] shot8 census {pn}: {string.Join(",", cens.Select(kv => kv.Key + ":" + kv.Value))}");
                    // palette-collapse gate (0d51d97 lesson): expected roles per
                    // prop; single-color or missing role = FAIL, not PASS.
                    var expect = new System.Collections.Generic.Dictionary<string, BlockType[]>
                    {
                        { "tree1", new[]{ BlockType.Leaves, BlockType.Log } },
                        { "tree2", new[]{ BlockType.Leaves, BlockType.Log } },
                        { "tree3", new[]{ BlockType.Leaves, BlockType.Log } },
                        { "tree4", new[]{ BlockType.Leaves, BlockType.Log } },
                        { "fence2", new[]{ BlockType.Leaves } },
                        { "stlight", new[]{ BlockType.Glowstone, BlockType.Stone } },
                        { "trashcan", new[]{ BlockType.Stone, BlockType.WoolBlack } },
                        { "planter", new[]{ BlockType.Leaves, BlockType.Stone } },
                        { "bench3", new[]{ BlockType.WoolWhite } },
                        { "bench4", new[]{ BlockType.Stone, BlockType.WoolBlack } },
                        { "hydrant", new[]{ BlockType.WoolRed } },
                        { "pumpkin", new[]{ BlockType.WoolYellow } },
                        { "trlight2", new[]{ BlockType.WoolBlack, BlockType.Glowstone } },
                        { "newsbox1", new[]{ BlockType.Stone, BlockType.WoolBlack, BlockType.WoolBlue } },
                        { "cone1", new[]{ BlockType.WoolYellow, BlockType.WoolWhite } },
                    };
                    if (expect.TryGetValue(pn, out var need))
                    {
                        var missing = need.Where(t => !cens.ContainsKey(t)).ToList();
                        if (missing.Count > 0)
                        {
                            Debug.LogError($"[M31] shot8 ROLE-GATE FAIL {pn}: missing {string.Join(",", missing)} in census");
                            Debug.Log("M31SNAPSHOT RESULT: FAIL");
                            return;
                        }
                    }
                    Render(cam, $"m31_prop_{pn}.jpg", 1f);
                    Debug.Log($"[M31] shot8 solo {pn} box={pv.Width}x{pv.Height}x{pv.Depth} blocks={placed} dist={dist:F1}");
                    // remove this item so the next one renders alone
                    for (int y = 0; y < pv.Height; y++)
                        for (int z = 0; z < pv.Depth; z++)
                            for (int x = 0; x < pv.Width; x++)
                                if (pv.blocks[x, y, z] != BlockType.Air) sim.SetBlock(spotX + 6 + x, studioY + 1 + y, spotZ + 6 + z, BlockType.Air, soloAff);
                    var remS2 = new List<Chunk>();
                    sim.Step(pcx8, pcz8, 100000f, 100000f, remS2, null);
                    foreach (var cs2 in remS2) attach(cs2);
                }
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
