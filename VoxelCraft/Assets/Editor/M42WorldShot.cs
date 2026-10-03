using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;
using VoxelCraft.Core;
using System.Collections.Generic;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>M42 real-world shot: generated terrain + REAL Rail blocks
    /// (official 16x16 texture, real chunk mesher) + minecart consist at
    /// player scale. Replaces the synthetic-tie snapshot — the user never saw
    /// actual rails because delivery shots were hand-built scenes.</summary>
    public static class M42WorldShot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(500f, 900f, 0f, 0f));

            var atlas = Art.TextureFactory.Build();
            var solidMat = new Material(Resources.Load<Shader>("Shaders/BlocksShader"))
            {
                mainTexture = atlas.atlas,
            };
            var waterMat = new Material(Resources.Load<Shader>("Shaders/WaterShader"))
            {
                mainTexture = atlas.atlas,
            };

            var stubGo = new GameObject("StubWorld");
            var stub = stubGo.AddComponent<WorldRoot>();
            stub.Init(20261003, 8, atlas, solidMat, waterMat, stubGo.transform);
            var sim = (WorldSim)typeof(WorldRoot).GetProperty("sim").GetValue(stub);
            int z0 = 20;

            // generate chunk data first so the flatness scan sees real terrain
            for (int gx = 0; gx <= 6; gx++)
                for (int gz = 0; gz <= 8; gz++)
                    sim.GenerateChunkData(gx, gz);

            // ---- find the FLATTEST 70-block strip (scan z lines in generated chunks) ----
            int bestZ = -1, bestFlat = -1;
            for (int zt = 8; zt < 120; zt++)
            {
                int flat = 0, run = 0;
                int h0 = sim.SurfaceHeight(8, zt, true);
                if (h0 < 2) continue;
                for (int xt = 8; xt < 90; xt++)
                {
                    int h = sim.SurfaceHeight(xt, zt, true);
                    run = (h == h0) ? run + 1 : 0;
                    if (run > flat) flat = run;
                }
                if (flat > bestFlat) { bestFlat = flat; bestZ = zt; }
            }
            z0 = bestZ >= 0 ? bestZ : 20;
            int targetY = sim.SurfaceHeight(8, z0, true);
            Debug.Log($"[M42W] flat strip z={z0} run={bestFlat} y={targetY}");

            // build meshes for the whole strip synchronously
            var prewarmed = new List<World.Chunk>();
            sim.Step(3, 1, 100000f, 100000f, prewarmed, null);
            foreach (var ch in prewarmed) stub.ApplyMeshes(ch);

            // ---- terraform: flatten + clear trees over a 3-wide corridor ----
            for (int x = 6; x < 80; x++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    int zz = z0 + dz;
                    int h = sim.SurfaceHeight(x, zz, true);
                    if (h < 0) continue;
                    // clear everything above target surface (trees, bumps)
                    for (int y = h; y > targetY; y--)
                        stub.SetBlockAndApply(new Vector3Int(x, y, zz), BlockType.Air);
                    // fill dips with dirt, cap with grass
                    int h2 = sim.SurfaceHeight(x, zz, true);
                    for (int y = h2 + 1; y < targetY; y++)
                        stub.SetBlockAndApply(new Vector3Int(x, y, zz), BlockType.Dirt);
                    var cap = sim.GetBlock(x, targetY, zz);
                    if (cap != BlockType.Grass && cap != BlockType.Sand)
                        stub.SetBlockAndApply(new Vector3Int(x, targetY, zz), BlockType.Grass);
                }
            }

            // lay REAL rail blocks along X on the flattened corridor
            int placed = 0;
            for (int x = 6; x < 80; x++)
            {
                stub.SetBlockAndApply(new Vector3Int(x, targetY + 1, z0), BlockType.RailX);
                if (sim.GetBlock(x, targetY + 1, z0) == BlockType.RailX) placed++;
            }
            Debug.Log($"[M42W] rails placed {placed}/74 at y={targetY + 1}");

            // flatten confirms
            int yA = sim.SurfaceHeight(10, z0, true), yB = sim.SurfaceHeight(70, z0, true);
            Debug.Log($"[M42W] flat check y10={yA} y70={yB}");

            // spawn the consist on the flattened corridor
            var go = new GameObject("M42WorldConsist");
            var train = go.AddComponent<TrackTrain>();
            train.world = stub;
            train.locomotiveName = "minecart";
            train.carNames = new[] { "minecart", "minecart" };
            train.unitScale = 0.5f;
            train.carGap = 0.85f;
            train.BuildConsist(new Vector2Int(14, z0));

            // log consist so empty-frame bugs are caught numerically
            var consistRoot = GameObject.Find("ConsistRoot");
            if (consistRoot != null)
                Debug.Log($"[M42W] consist cars={consistRoot.transform.childCount} pos={consistRoot.transform.position}");
            else
                Debug.LogWarning("[M42W] ConsistRoot NOT FOUND");

            // camera: behind-left of the consist, looking down the rails
            var camGo = new GameObject("cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.75f, 0.95f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.fieldOfView = 46f;
            // camera: player eye height ON the rails, carts ahead — like the
            // reference screenshot (flat grass, ties receding, cart dominant)
            float eyeY = (float)targetY + 1.9f;
            // 3/4 view: stand off the track's left side, carts on the near
            // track receding to the right — reads instantly as "minecart on rail"
            cam.transform.position = new Vector3(9.5f, eyeY, z0 + 3.6f);
            cam.transform.LookAt(new Vector3(16.5f, eyeY - 1.1f, z0 + 0.1f));

            // project each CART to screen — the crop will follow these numbers
            var consist = GameObject.Find("ConsistRoot");
            if (consist != null)
                foreach (Transform car in consist.transform)
                {
                    var cp = cam.WorldToScreenPoint(car.position);
                    Debug.Log($"[M42W] cart {car.name} world={car.position} screen=({cp.x:F0},{cp.y:F0}) w={cp.z:F1}");
                }

            // numeric screen-projection audit of rail pixels
            for (int ax = 8; ax <= 24; ax += 4)
            {
                float ay = sim.SurfaceHeight(ax, z0, true) + 1f + 1f / 16f;
                var sp = cam.WorldToScreenPoint(new Vector3(ax + 0.5f, ay, z0 + 0.5f));
                Debug.Log($"[M42W] rail x={ax} screen=({sp.x:F0},{sp.y:F0}) w={sp.z:F1}");
            }

            // marker pass: render a frame, then stamp each car's bounds corners red
            var rtM = new RenderTexture(1520, 960, 24);
            cam.targetTexture = rtM;
            cam.Render();
            RenderTexture.active = rtM;
            var markerTex = new Texture2D(1520, 960, TextureFormat.RGB24, false);
            markerTex.ReadPixels(new Rect(0, 0, 1520, 960), 0, 0);
            markerTex.Apply();
            var px = markerTex.GetPixels32();
            void Dot(int sx, int sy)
            {
                for (int oy = -3; oy <= 3; oy++)
                    for (int ox = -3; ox <= 3; ox++)
                    {
                        int xx = sx + ox, yy = sy + oy;
                        if (xx < 0 || yy < 0 || xx >= 1520 || yy >= 960) continue;
                        px[yy * 1520 + xx] = new Color32(255, 0, 0, 255);
                    }
            }
            var cons = GameObject.Find("ConsistRoot");
            if (cons != null)
                foreach (Transform car in cons.transform)
                {
                    var rs = car.GetComponentsInChildren<Renderer>();
                    if (rs.Length == 0) continue;
                    var b = rs[0].bounds;
                    foreach (var r2 in rs) b.Encapsulate(r2.bounds);
                    Vector3[] corners =
                    {
                        new Vector3(b.min.x, b.min.y, b.min.z), new Vector3(b.max.x, b.min.y, b.min.z),
                        new Vector3(b.min.x, b.max.y, b.min.z), new Vector3(b.max.x, b.max.y, b.min.z),
                        new Vector3(b.min.x, b.min.y, b.max.z), new Vector3(b.max.x, b.min.y, b.max.z),
                        new Vector3(b.min.x, b.max.y, b.max.z), new Vector3(b.max.x, b.max.y, b.max.z),
                    };
                    foreach (var c3 in corners)
                    {
                        var sp = cam.WorldToScreenPoint(c3);
                        if (sp.z > 0) Dot((int)sp.x, (int)sp.y);
                    }
                    Debug.Log($"[M42W] MARK {car.name} world b={b}");
                    var ce = car.rotation.eulerAngles;
                    var be = car.Find("Body") != null ? car.Find("Body").rotation.eulerAngles : Vector3.zero;
                    Debug.Log($"[M42W] CAR {car.name} rot=({ce.x:F1},{ce.y:F1},{ce.z:F1}) body=({be.x:F1},{be.y:F1},{be.z:F1})");
                    foreach (var rr in car.GetComponentsInChildren<Renderer>())
                    {
                        var ee = rr.transform.rotation.eulerAngles;
                        Debug.Log($"[M42W] PLATE {rr.name} rot=({ee.x:F1},{ee.y:F1},{ee.z:F1})");
                    }
                }
            markerTex.SetPixels32(px);
            markerTex.Apply();
            System.IO.File.WriteAllBytes("D:/zlj_world/_shots/m42_marked.png", markerTex.EncodeToPNG());
            Debug.Log("[M42W] saved m42_marked.png");

            // render
            var rt = new RenderTexture(1520, 960, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1520, 960, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1520, 960), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(
                "D:/zlj_world/_shots/m42_world.png", tex.EncodeToPNG());
            Debug.Log("[M42W] saved m42_world.png");
        }
    }
}
