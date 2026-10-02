// M40 street-prop deliverable shots: stamp each converted prop on a flat
// platform, census its blocks, render a close-up, then remove it (one prop
// in frame at a time — empty-frame rule). Batch mode, no play mode.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    public static class M40PropsSnapshot
    {
        public static void Run()
        {
            var sim = new WorldSim(1337);
            // suppress worldgen structures during this run: random anchors
            // (30%/cell) stamp buildings INTO the platform area, contaminating
            // both the census and the shots (dogstand census showed +296 alien
            // voxels incl. 257 Brick)
            var savedSet = (string[])TerrainGenerator.StructureSet.Clone();
            for (int si = 0; si < TerrainGenerator.StructureSet.Length; si++)
            {
                TerrainGenerator.StructureSet[si] = "zz_disabled_" + si; // Load() returns null -> skip
            }
            var atlas = Art.TextureFactory.Build();
            var rects = new Rect[59];
            for (int i = 0; i < rects.Length; i++) { rects[i] = atlas.TileRect((TileId)i); }
            sim.tileRects = rects;

            var solidMat = new Material(Shader.Find("Voxel/Blocks")) { mainTexture = atlas.atlas };
            var root = new GameObject("M40Props");
            Action<Chunk> attach = c =>
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

            // batch-3 street props
            string[] names = {
                "sidewalk2", "sign1", "sign5", "chair1", "table1",
                "cart1", "cart2", "cart1a", "busstop", "fountain",
                "statue1", "mailbox2", "newsbox2", "trashcan2", "stlight1",
                "trlight1", "container1", "fence1", "column1", "mushroom1",
                "planter1", "trellis", "stage", "playgrnd1", "grill",
                "campfire", "dogstand", "rubbish1", "curb2",
            };
            names = names.Concat(new string[] {
                // batch 4
                "arcade1", "arcade2", "arcade3", "arcade4", "arcade5",
                "bench1", "bench2", "bench5", "boxingring",
                "cart1b", "cart2a", "cart2b", "celltower", "chair2",
                "christmas1", "column2", "column3",
                "container2", "container3", "container4",
                "cross", "curb1", "curb3", "curb4", "curb5", "curb6",
                "curb7", "curb7a", "curb8",
                "door1", "door2", "door3", "door4",
                "fence3", "fence4", "fence5", "fence6", "fence7",
                "grave1", "grave2", "grave3", "grave4", "guitarcase", "halo",
                "mailbox2a", "mailbox2b", "mushroom2", "mushroom3",
                "newsbox3", "newsbox4", "park_block", "path1", "pentagram",
                "planter2", "planter3a", "planter3b",
                "playgrnd2", "playgrnd3", "playgrnd4", "playgrnd5",
                "potty1", "potty2", "potty3",
                "rubbish2", "rubbish3", "rubbish4",
                "sidewalk1", "sidewalk3", "sidewalk4", "sidewalk5",
                "sign2", "sign3", "sign4", "sign6", "sign7", "sign8", "sign9",
                "statue2", "statue3", "stlight2", "stlight3",
                "street1", "street2", "stretcher",
                "table2", "table3", "table3a", "table3b",
                "tracks1", "tracks2",
                "trashcan1", "trashcan3", "trashcan4",
                "tree1a", "tree1b", "tree1c", "tree2a", "tree2b", "tree2c",
                "wall", "driveway1", "driveway2", "driveway3", "armgate2",
                // batch 5 misc props
                "armgate1", "candle", "crosswalk", "mailbox",
                "fire1", "fire2", "fire3", "fire4", "fire5",
                "policetape", "splatter1", "splatter2", "splatter3",
                // batch 6
                "train", "train2", "train3",
                "wagon1", "wagon2", "wagon3", "wagon4",
                "overpass1", "tunnel1",
                // batch 6b: road vehicles
                "ambulance", "bus", "cab1",
                "car1", "car2", "car3", "car4", "car5",
                "fire", "lunch1", "lunch2", "lunch3", "lunch4",
                "mini1", "mini2", "mini3", "mini4", "mini5",
                "police1", "suv1", "suv2", "suv3", "tank1",
                "truck1", "truck2", "truck3", "truck4", "truck5", "truck6", "truck7",
            }).ToArray();
            var vxs = names.Select(n => Vox.StructureRegistry.Load(n)).ToArray();

            int px = 60, pz = -80;
            var remP = new List<Chunk>();
            sim.dataRadius = 6; sim.meshRadius = 6;
            int pcx = VoxelMath.ChunkCoord(px + 20), pcz = VoxelMath.ChunkCoord(pz + 10);
            sim.Step(pcx, pcz, 100000f, 100000f, remP, null);
            foreach (var cP in remP) attach(cP);

            // flat 60x60 platform at a FIXED LOW height — max-of-terrain put tall
            // props (dogstand H=20 umbrella) past ChunkHeight=80, silently
            // dropping their top layers (59 voxels vanished)
            int platBaseY = 34;
            var affected = new List<Chunk>();
            for (int x = -4; x < 60; x++)
                for (int z = -4; z < 60; z++)
                {
                    int colTop = sim.SurfaceHeight(px + x, pz + z, true);
                    for (int y = colTop; y <= platBaseY; y++) sim.SetBlock(px + x, y, pz + z, BlockType.Grass, affected);
                    for (int y = platBaseY + 1; y <= platBaseY + 40; y++) sim.SetBlock(px + x, y, pz + z, BlockType.Air, affected);
                }

            // camera + render helper with the fog/brightness globals (M31 lesson)
            var camGo = new GameObject("M40PropsCam");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.5f; cam.farClipPlane = 400f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(140f / 255f, 184f / 255f, 229f / 255f);
            // fog/brightness globals BEFORE any render (M31 lesson: default fog
            // range 0 makes every pixel fog colour)
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(200f, 400f));

            var rem2 = new List<Chunk>();
            int scx = VoxelMath.ChunkCoord(px + 20), scz = VoxelMath.ChunkCoord(pz + 10);
            sim.dataRadius = 6; sim.meshRadius = 6;
            sim.Step(scx, scz, 100000f, 100000f, rem2, null);
            foreach (var c2 in rem2) attach(c2);

            for (int i = 0; i < names.Length; i++)
            {
                var v = vxs[i];
                int ox = px + 24, oz = pz + 24;   // centre of platform, one prop at a time
                int bY = platBaseY + 1;
                var touched = new List<Chunk>();
                for (int y = 0; y < v.Height; y++)
                    for (int z = 0; z < v.Depth; z++)
                        for (int x = 0; x < v.Width; x++)
                        {
                            var t = v.blocks[x, y, z];
                            if (t != BlockType.Air) sim.SetBlock(ox + x, bY + y, oz + z, t, touched);
                        }

                // remesh + census
                var rem3 = new List<Chunk>();
                sim.dataRadius = 6; sim.meshRadius = 6;
                sim.Step(scx, scz, 100000f, 100000f, rem3, null);
                foreach (var c3 in rem3) attach(c3);

                var census = new Dictionary<BlockType, int>();
                int solidCount = 0;
                for (int y = bY; y < bY + v.Height; y++)
                    for (int z = oz; z < oz + v.Depth; z++)
                        for (int x = ox; x < ox + v.Width; x++)
                        {
                            var b = sim.GetBlock(x, y, z);
                            if (b != BlockType.Air) { solidCount++; census[b] = census.TryGetValue(b, out var c4) ? c4 + 1 : 1; }
                        }
                string censusStr = string.Join(",", census.Select(kv => kv.Key + ":" + kv.Value).Take(5));
                Debug.Log("[M40P] census " + names[i] + " solid=" + solidCount + " " + censusStr);

                // close-up from SE, subject-centred
                float midX = ox + v.Width / 2f, midZ = oz + v.Depth / 2f, midY = bY + v.Height / 2f;
                float span = Mathf.Max(Mathf.Max(v.Width, v.Depth), 4f);
                float dist = span * 1.6f + 6f;
                cam.transform.position = new Vector3(midX + dist * 0.62f, midY + span * 0.5f + 3f, midZ + dist * 0.62f);
                cam.transform.LookAt(new Vector3(midX, midY, midZ));
                Render(cam, $"m40p_{names[i]}.jpg");

                // wipe the prop for the next shot
                for (int y = 0; y < v.Height + 2; y++)
                    for (int z = 0; z < v.Depth; z++)
                        for (int x = 0; x < v.Width; x++)
                            sim.SetBlock(ox + x, bY + y, oz + z, BlockType.Air, touched);
                var rem4 = new List<Chunk>();
                sim.dataRadius = 6; sim.meshRadius = 6;
                sim.Step(scx, scz, 100000f, 100000f, rem4, null);
                foreach (var c4 in rem4) attach(c4);
            }

            UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log("[M40P] all prop shots saved");
        }

        private static void Render(Camera cam, string name)
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
        }
    }
}
