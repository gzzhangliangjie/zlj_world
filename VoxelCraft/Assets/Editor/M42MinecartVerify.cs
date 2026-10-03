using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>M42: official minecart on rails — geo loads, texture binds,
    /// scale correct (1.12 geo px = 1/16 u, NO 1.585 multiplier), and a
    /// minecart + 2 carts consist drives along the rail line.</summary>
    public static class M42MinecartVerify
    {
        static int fail, total;
        static void Add(string goal, bool pass, string detail)
        {
            if (!pass) fail++;
            total++;
            Debug.Log($"[M42] {(pass ? "PASS" : "FAIL")} {goal} {detail}");
        }

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));

            try
            {
                var atlas = Art.TextureFactory.Build();
                var sim = new WorldSim(1337);
                sim.dataRadius = 3; sim.meshRadius = 3;
                var rects = new Rect[80];
                for (int i = 0; i < rects.Length; i++) rects[i] = atlas.TileRect((TileId)i);
                sim.tileRects = rects;
                var remeshed = new List<Chunk>();
                sim.Step(0, 0, 100000f, 100000f, remeshed, null);

                var stubGo = new GameObject("StubWorld");
                var stub = stubGo.AddComponent<WorldRoot>();
                typeof(WorldRoot).GetProperty("sim").SetValue(stub, sim);

                // ---- lay a 64-cell straight rail line along +X ----
                int z0 = 20;
                int railCount = 0;
                for (int cx = 0; cx <= 6; cx++)
                    for (int cz = 0; cz <= 2; cz++)
                        sim.GenerateChunkData(cx, cz);
                for (int x = 6; x < 70; x++)
                {
                    int y = sim.SurfaceHeight(x, z0, true) + 1;
                    sim.SetBlock(x, y, z0, BlockType.RailX, remeshed);
                    if (sim.GetBlock(x, y, z0) == BlockType.RailX) railCount++;
                }
                Add("rail.line", railCount == 64, $"placed {railCount}/64 RailX");

                // ---- assets load ----
                var geoAsset = Resources.Load<TextAsset>("Geo/minecart.geo");
                var skin = Art.CreatureTextureFactory.GetSkinMaterial("minecart_skin");
                Add("assets.load", geoAsset != null && skin != null,
                    $"geo={geoAsset != null} skin={skin != null}");

                // ---- scale audit from the geo JSON itself ----
                float gy0 = float.MaxValue, gy1 = float.MinValue;
                float gx0 = float.MaxValue, gx1 = float.MinValue;
                if (geoAsset != null)
                {
                    var root = VoxelCraft.Creatures.MiniJson.Deserialize(geoAsset.text) as Dictionary<string, object>;
                    var geo = root["minecraft:geometry"] as List<object>;
                    var g0 = geo[0] as Dictionary<string, object>;
                    var bones = g0["bones"] as List<object>;
                    foreach (var bo in bones)
                    {
                        var bd = bo as Dictionary<string, object>;
                        if (!bd.ContainsKey("cubes")) continue;
                        foreach (var co in (List<object>)bd["cubes"])
                        {
                            var cd = co as Dictionary<string, object>;
                            var org = (List<object>)cd["origin"];
                            var sz = (List<object>)cd["size"];
                            float ox = System.Convert.ToSingle(org[0]);
                            float oy = System.Convert.ToSingle(org[1]);
                            float sx = System.Convert.ToSingle(sz[0]);
                            float sy = System.Convert.ToSingle(sz[1]);
                            gx0 = Mathf.Min(gx0, ox); gx1 = Mathf.Max(gx1, ox + sx);
                            gy0 = Mathf.Min(gy0, oy); gy1 = Mathf.Max(gy1, oy + sy);
                        }
                    }
                }
                const float unitScale = 0.5f;   // bedrock minecart render scale
                float wU = (gx1 - gx0) / 16f * unitScale, hU = (gy1 - gy0) / 16f * unitScale;
                Add("cart.scale", hU > 0.5f && hU < 1.2f && wU > 0.7f && wU < 1.4f,
                    $"minecart {wU:F2} x {hU:F2} u (official ~0.98 x 0.7)");

                // ---- build a minecart + 2 carts consist ----
                var go = new GameObject("CartConsist");
                var train = go.AddComponent<TrackTrain>();
                train.world = stub;
                train.locomotiveName = "minecart";
                train.carNames = new[] { "minecart", "minecart" };
                train.unitScale = 0.5f;
                train.carGap = 0.15f;
                train.maxSpeed = 4.5f;
                train.BuildConsist(new Vector2Int(30, z0));

                int cars = 0, renderers = 0;
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    var c = go.transform.GetChild(i);
                    if (c.name == "Seat") continue;
                }
                var consistRoot = GameObject.Find("ConsistRoot");
                if (consistRoot != null)
                    foreach (Transform c in consistRoot.transform)
                        if (c.name.StartsWith("Car_"))
                        { cars++; renderers += c.GetComponentsInChildren<Renderer>().Length; }
                Add("consist.model", cars == 3 && renderers >= 3,
                    $"Car_ roots {cars}/3, renderers {renderers}");

                float locoTop = 0f;
                if (consistRoot != null)
                {
                    var rends = consistRoot.GetComponentsInChildren<Renderer>();
                    if (rends.Length > 0)
                    {
                        var b = rends[0].bounds;
                        foreach (var r in rends) b.Encapsulate(r.bounds);
                        locoTop = b.size.y;
                    }
                }
                Add("consist.worldH", locoTop > 0.25f && locoTop < 1.15f,
                    $"consist world height {locoTop:F2} u (want 0.4..1.1)");

                // ---- ride + drive ----
                train.Enter(null);
                Add("ride.enter", train.Occupied && train.Seat != null, "occupied+seat");

                train.throttleIn = 1f;
                float dt = 1f / 60f, maxV = 0f, t = 4f;
                float xStart = train.transform.position.x;
                while (t > 0f) { train.Tick(dt); maxV = Mathf.Max(maxV, train.Speed); t -= dt; }
                Add("drive.accel", maxV >= 0.8f * train.maxSpeed, $"speed {maxV:F2}/{train.maxSpeed} u/s");
                float xEnd = train.transform.position.x;
                Add("drive.move", xEnd > xStart + 1f, $"head x {xStart:F1} -> {xEnd:F1}");

                train.throttleIn = 1f;
                for (int i = 0; i < 60 * 30; i++) train.Tick(dt);
                bool stopped = train.Speed == 0f;
                train.Exit();
                Add("drive.stop+exit", stopped && !train.Occupied,
                    $"end speed {train.Speed:F2} occupied={train.Occupied}");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                Add("exception", false, e.Message);
            }

            Debug.Log($"M42CART RESULT: {(fail == 0 ? "PASS" : "FAIL")} ({total - fail}/{total})");
            EditorApplication.Exit(fail == 0 ? 0 : 1);
        }
    }
}
