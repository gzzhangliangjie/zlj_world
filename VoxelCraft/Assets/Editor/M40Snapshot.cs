using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// M40 snapshot: render the four mmmm obj_ buildings in-world on a platform,
    /// 3/4 view each + one group shot. Mirrors the M31 house5 platform approach.
    /// Unity.exe -batchmode -quit -executeMethod VoxelCraft.Editor.M40Snapshot.Run
    /// </summary>
    public static class M40Snapshot
    {
        public static void Run()
        {
            var sim = new WorldSim(1337);
            var atlas = Art.TextureFactory.Build();
            var rects = new Rect[59];
            for (int i = 0; i < rects.Length; i++) { rects[i] = atlas.TileRect((TileId)i); }
            sim.tileRects = rects;
            sim.unloadRadius = 64;

            var camGo = new GameObject("M40Cam");
            var cam = camGo.AddComponent<Camera>();
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(200f, 400f));
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.fieldOfView = 60f;
            cam.farClipPlane = 400f;
            cam.nearClipPlane = 0.3f;
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.55f, 0.72f, 0.90f));

            var root = new GameObject("M40World");
            var solidMat = new Material(Shader.Find("Voxel/Blocks")) { mainTexture = atlas.atlas };
            System.Action<Chunk> attach = (c) =>
            {
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
                if (stale != null) UnityEngine.Object.DestroyImmediate(stale);
            };

            string[] names = { "obj_store02", "obj_store03", "obj_store04", "obj_store05",
                "obj_story01", "obj_story02" };
            var vxs = names.Select(n => Vox.StructureRegistry.Load(n)).ToArray();

            // Platform: terrain around (60,-80), flattened like M31 shot7.
            int px = 60, pz = -80;
            var remP = new List<Chunk>();
            sim.dataRadius = 8; sim.meshRadius = 8;
            int pcx = VoxelMath.ChunkCoord(px + 70), pcz = VoxelMath.ChunkCoord(pz + 16);
            sim.Step(pcx, pcz, 100000f, 100000f, remP, null);
            foreach (var cP in remP) attach(cP);

            int mn = int.MaxValue, mx = int.MinValue;
            for (int x = -4; x < 146; x += 2)
                for (int z = -4; z < 40; z += 2)
                {
                    int g = sim.generator.HeightAt(px + x, pz + z);
                    mn = Mathf.Min(mn, g); mx = Mathf.Max(mx, g);
                }
            int platBaseY = mx;
            var affected = new List<Chunk>();
            for (int x = -4; x < 146; x++)
                for (int z = -4; z < 40; z++)
                {
                    int colTop = sim.SurfaceHeight(px + x, pz + z, true);
                    for (int y = colTop; y <= platBaseY; y++) sim.SetBlock(px + x, y, pz + z, BlockType.Grass, affected);
                    for (int y = platBaseY + 1; y <= platBaseY + 30; y++) sim.SetBlock(px + x, y, pz + z, BlockType.Air, affected);
                }

            // Stamp the four buildings in a row with 6-block gaps.
            int ox = px, oz = pz;
            var bases = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var v = vxs[i];
                int bY = platBaseY + 1;
                for (int y = 0; y < v.Height; y++)
                    for (int z = 0; z < v.Depth; z++)
                        for (int x = 0; x < v.Width; x++)
                        {
                            var t = v.blocks[x, y, z];
                            if (t != BlockType.Air) sim.SetBlock(ox + x, bY + y, oz + z, t, affected);
                        }
                bases[i] = bY;
                Debug.Log($"[M40] {names[i]} stamped at ({ox},{oz}) base={bY} {v.Width}x{v.Height}x{v.Depth}");
                ox += v.Width + 6;
            }

            var rem2 = new List<Chunk>();
            sim.dataRadius = 8; sim.meshRadius = 8;
            // center on the STAMPED area so every touched chunk is inside
            // meshRadius (stamp spans z=-80..-56 → cz -5..-4; center (5,5) missed them)
            int scx = VoxelMath.ChunkCoord(px + 70), scz = VoxelMath.ChunkCoord(pz + 16);
            sim.Step(scx, scz, 100000f, 100000f, rem2, null);
            foreach (var c2 in rem2) attach(c2);

            // numeric census of each building's blocks in the sim (readback audit)
            for (int i = 0; i < names.Length; i++)
            {
                var v = vxs[i];
                var bx0 = px + (v.Width + 6) * i;
                var census = new Dictionary<BlockType, int>();
                int solidCount = 0;
                for (int y = bases[i]; y < bases[i] + v.Height; y++)
                    for (int z = oz; z < oz + v.Depth; z++)
                        for (int x = bx0; x < bx0 + v.Width; x++)
                        {
                            var b = sim.GetBlock(x, y, z);
                            if (b != BlockType.Air) { solidCount++; census[b] = census.TryGetValue(b, out var c4) ? c4 + 1 : 1; }
                        }
                Debug.Log($"[M40] census {names[i]} solid={solidCount} {string.Join(",", census.Select(kv => kv.Key + ":" + kv.Value).Take(6))}");
            }

            // 3/4 view of each building
            int cx0 = px;
            for (int i = 0; i < names.Length; i++)
            {
                var v = vxs[i];
                float midX = cx0 + v.Width / 2f, midZ = oz + v.Depth / 2f, midY = bases[i] + v.Height / 2f;
                float dist = Mathf.Max(v.Width, v.Depth) * 0.9f + 8f;
                cam.transform.position = new Vector3(midX - dist * 0.7f, midY + v.Height * 0.7f + 4f, midZ - dist * 0.7f);
                cam.transform.LookAt(new Vector3(midX, midY, midZ));
                Render(cam, $"m40_{names[i]}.jpg", 1f);
                cx0 += v.Width + 6;
            }

            // mesh state probe for the stamped chunks
            for (int czP = -6; czP <= -3; czP++)
                for (int cxP = 3; cxP <= 10; cxP++)
                {
                    var ch = sim.GetChunk(cxP, czP);
                    if (ch != null)
                        Debug.Log($"[M40] chunk({cxP},{czP}) dataReady={ch.dataReady} meshDirty={ch.meshDirty} built={ch.meshBuilt} verts={(ch.solidMeshData != null ? ch.solidMeshData.Vertices.Count : -1)}");
                }

            // Diagnostic: top-down view of the first building
            {
                var v0 = vxs[0];
                float cxT = px + v0.Width / 2f, czT = oz + v0.Depth / 2f;
                cam.transform.position = new Vector3(cxT, bases[0] + v0.Height + 30f, czT);
                cam.transform.LookAt(new Vector3(cxT, bases[0], czT));
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                Render(cam, "m40_topdown.jpg", 1f);
                Debug.Log($"[M40] topdown at ({cxT},{czT})");
            }

            // Group shot from the front-left
            float gMidX = px + 66f, gMidZ = oz + 13f, gMidY = platBaseY + 8f;
            cam.transform.position = new Vector3(gMidX - 55f, platBaseY + 38f, gMidZ - 75f);
            cam.transform.LookAt(new Vector3(gMidX, gMidY, gMidZ + 4f));
            Render(cam, "m40_group.jpg", 1f);
            Debug.Log("[M40] all shots saved");

            Object.DestroyImmediate(camGo);
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
