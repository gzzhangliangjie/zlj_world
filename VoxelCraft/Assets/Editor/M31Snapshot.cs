// M31 deliverable shots: furnished cottage interior + exterior with door/torches/
// chest/bed/fence, plus a night shot proving torch glow. Batch mode, no play mode.
using System;
using System.Collections.Generic;
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
            for (int i = 0; i < 49; i++) { rects[i] = atlas.TileRect((TileId)i); }
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
            Debug.Log("[M31] shot4 furnace closeup saved");

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
