// M30 deliverable shots: batch-mode friendly (no play mode). Build a minimal
// world sim + meshes by hand, render three cameras to JPEGs:
//   1. m30_terrain.jpg  — FastNoiseLite landscape panorama
//   2. m30_cottage.jpg  — .vox cottage stamped into the world
//   3. m30_buildsel.jpg — build-tool selection box around the cottage
using System;
using System.IO;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    public static class M30Snapshot
    {
        const string OutDir = @"D:\zlj_world\_shots";

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));
            Directory.CreateDirectory(OutDir);

            var atlas = TextureFactory.Build();
            var blocksShader = Resources.Load<Shader>("Shaders/BlocksShader");
            var waterShader = Resources.Load<Shader>("Shaders/WaterShader");
            var solidMat = new Material(blocksShader) { mainTexture = atlas.atlas };
            var waterMat = new Material(waterShader) { mainTexture = atlas.atlas };

            // Camera rig.
            var camGo = new GameObject("M30Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 600f;
            cam.enabled = false;

            // Sun.
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            sun.shadows = LightShadows.None;
            sunGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            var sim = new WorldSim(1337);
            var rects = new Rect[36];
            for (int i = 0; i < 36; i++) rects[i] = atlas.TileRect((TileId)i);
            sim.tileRects = rects;

            // Find a valid cottage anchor near origin: (141,-66) works per hash walk.
            Vector2Int? anchorFound = null;
            foreach (var a in Vox.StructureRegistry.AnchorsNear("probe", -400, -400, 400, 400, 1337))
            {
                int g = sim.generator.HeightAt(a.x, a.y);
                bool flat = true;
                for (int zz = 0; zz < 12 && flat; zz++)
                    for (int xx = 0; xx < 12 && flat; xx++)
                    {
                        int hh = sim.generator.HeightAt(a.x + xx, a.y + zz);
                        if (Mathf.Abs(hh - g) > 6) { flat = false; }
                    }
                if (flat && g > VoxelMath.SeaLevel + 2 && g < TerrainGenerator.SnowLine - 3 &&
                    !sim.generator.IsDesert(a.x, a.y))
                {
                    anchorFound = a;
                    break;
                }
            }
            Vector2Int anchor = anchorFound ?? new Vector2Int(141, -66);
            Debug.Log($"[M30] anchor=({anchor.x},{anchor.y}) h={sim.generator.HeightAt(anchor.x, anchor.y)}");

            // Highest peak in +/-240 for the panorama.
            int bh = 1, bx = 0, bz = 0;
            for (int z = -240; z <= 240; z += 8)
                for (int x = -240; x <= 240; x += 8)
                {
                    int h = sim.generator.HeightAt(x, z);
                    if (h > bh) { bh = h; bx = x; bz = z; }
                }
            Debug.Log($"[M30] peak h={bh} at ({bx},{bz})");

            // Generate + mesh a wide disc of chunks around the anchor & peak.
            var root = new GameObject("M30World").transform;
            Action<Chunk> attach = null;
            attach = (c) =>
            {
                var go = new GameObject($"C{c.cx}_{c.cz}");
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(c.cx * VoxelMath.ChunkSize, 0f, c.cz * VoxelMath.ChunkSize);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = solidMat;
                mf.sharedMesh = c.solidMeshData != null ? c.solidMeshData.ToMesh(null) : null;
            };
            Action<int, int, int> meshChunk = (cx, cz, r) =>
            {
                var remeshed = new System.Collections.Generic.List<Chunk>();
                sim.unloadRadius = 64; // never unload during capture
                sim.dataRadius = r; sim.meshRadius = r;
                sim.Step(cx, cz, 100000f, 100000f, remeshed, null);
                foreach (var c in remeshed) attach(c);
            };

            int acx = VoxelMath.ChunkCoord(anchor.x);
            int acz = VoxelMath.ChunkCoord(anchor.y);
            meshChunk(0, 0, 8); // one big disc centred at origin covers peak & anchor
            if (Mathf.Max(Mathf.Abs(acx), Mathf.Abs(acz)) > 8 || true)
            {
                // top up the two POI discs without unloading the origin disc:
                // bump unloadRadius far out so nothing gets dropped.
                sim.unloadRadius = 64;
                var remeshedA = new System.Collections.Generic.List<Chunk>();
                sim.dataRadius = 5; sim.meshRadius = 5;
                sim.Step(acx, acz, 100000f, 100000f, remeshedA, null);
                foreach (var c in remeshedA) attach(c);
                var remeshedB = new System.Collections.Generic.List<Chunk>();
                sim.Step(VoxelMath.ChunkCoord(bx), VoxelMath.ChunkCoord(bz), 100000f, 100000f, remeshedB, null);
                foreach (var c in remeshedB) attach(c);
            }

            int gy = sim.generator.HeightAt(anchor.x, anchor.y);

            // Shot 1: panorama from above the peak.
            cam.transform.position = new Vector3(bx - 60, bh + 34, bz - 60);
            cam.transform.LookAt(new Vector3(bx, bh - 6, bz));
            RenderTo(cam, "m30_terrain.jpg");
            Debug.Log("[M30] shot1 terrain saved");

            // Shot 2: cottage front-left quarter view.
            cam.transform.position = new Vector3(anchor.x - 13, gy + 11, anchor.y - 13);
            cam.transform.LookAt(new Vector3(anchor.x + 5, gy + 4, anchor.y + 5));
            RenderTo(cam, "m30_cottage.jpg");
            Debug.Log("[M30] shot2 cottage saved");

            // Shot 3: same view + build-tool selection box around the cottage.
            var btGo = new GameObject("M30BuildTool");
            var bt = btGo.AddComponent<BuildTools.BuildToolController>();
            bt.viewCamera = cam;
            var f_active = typeof(BuildTools.BuildToolController).GetField("active",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var f_ca = typeof(BuildTools.BuildToolController).GetField("cornerA",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var f_cb = typeof(BuildTools.BuildToolController).GetField("cornerB",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f_active.SetValue(bt, true);
            f_ca.SetValue(bt, (Vector3Int?)new Vector3Int(anchor.x - 1, gy, anchor.y - 1));
            f_cb.SetValue(bt, (Vector3Int?)new Vector3Int(anchor.x + 11, gy + 9, anchor.y + 9));
            bt.BuildBoxForCapture(); // public hook: builds box lines without Update loop
            cam.transform.position = new Vector3(anchor.x - 14, gy + 9, anchor.y - 14);
            cam.transform.LookAt(new Vector3(anchor.x + 5, gy + 5, anchor.y + 5));
            RenderTo(cam, "m30_buildsel.jpg");
            Debug.Log("[M30] shot3 buildsel saved");

            Debug.Log("M30SNAPSHOT RESULT: PASS");
        }

        private static void RenderTo(Camera cam, string name)
        {
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            File.WriteAllBytes(Path.Combine(OutDir, name), tex.EncodeToJPG(88));
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
