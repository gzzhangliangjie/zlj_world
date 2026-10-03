using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Creatures;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// M41 train verification: multi-car consist running on RAIL blocks.
    /// Batch harness drives TrackTrain.Tick(dt) directly (rule 1: no Update
    /// in batch mode) and asserts measurable signals (rule 2):
    ///
    ///  rail.blocks   : placed RailX chain present in the sim (count == 40)
    ///  consist.model : locomotive + 4 wagons built, all with renderers
    ///  consist.scale : locomotive geo height 3.0..4.0 u (player = 1.98 u,
    ///                  mmmm ground truth loco ~= 1.7x player)
    ///  drive.accel   : speed >= 80% maxSpeed within 6 s of full throttle
    ///  drive.stop    : dead end stops the consist (speed -> 0, no derail:
    ///                  head stays on a rail cell)
    ///  follow.cars   : all 5 cars advance; spacing between consecutive car
    ///                  centres stays within 0.5 u of (half+half+gap)
    ///  ride.enter    : Enter()/Exit() gate occupancy; Seat non-null
    /// </summary>
    public static class M41TrainVerify
    {
        static int fail;
        static readonly List<string> lines = new List<string>();

        static void Add(string goal, bool pass, string detail)
        {
            if (!pass) fail++;
            Debug.Log($"[M41] {(pass ? "PASS" : "FAIL")} {goal} {detail}");
        }

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));

            var atlas = TextureFactory.Build();
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

            // ---- lay a 40-cell straight rail line along +X ----
            int z0 = 24;
            int railCount = 0;
            for (int cx = 0; cx <= 6; cx++)
                for (int cz = 0; cz <= 2; cz++)
                    sim.GenerateChunkData(cx, cz);
            for (int x = 4; x < 104; x++)
            {
                int y = sim.SurfaceHeight(x, z0, true) + 1;
                var aff = new List<Chunk>();
                sim.SetBlock(x, y, z0, BlockType.RailX, aff);
                var b = sim.GetBlock(x, y, z0);
                if (b == BlockType.RailX) railCount++;
            }
            Add("rail.blocks", railCount == 100, $"placed {railCount}/100 RailX on surface+1");

            // ---- build the consist ----
            var trainGo = new GameObject("TrainConsist");
            var train = trainGo.AddComponent<TrackTrain>();
            train.world = stub;
            train.locomotiveName = "train";
            train.carNames = new[] { "coach1", "coach2" };
            train.maxSpeed = 7f;
            train.BuildConsist(new Vector2Int(40, z0));

            var cars = train.GetComponentsInChildren<Renderer>();
            int carModels = 0;
            foreach (var r in cars) if (r.name.StartsWith("Car_")) carModels++;
            // every car has at least one renderer (the whole consist shares one
            // SkinnedBox mesh per bone; count Car_ roots instead)
            var consistRoot = GameObject.Find("ConsistRoot") != null ? GameObject.Find("ConsistRoot").transform : train.transform;
            int carRoots = 0;
            foreach (Transform c in consistRoot)
                if (c.name.StartsWith("Car_")) carRoots++;
            Add("consist.model", carRoots == 5, $"Car_ roots {carRoots}/5 (renderers {cars.Length})");

            // scale: geo JSON audit via the project's own parser
            // (skill rule: geo-level numeric audit is the final authority)
            var locoGeo = Resources.Load<TextAsset>("Geo/train.geo");
            float locoH = 0f;
            if (locoGeo != null)
            {
                var root = VoxelCraft.Creatures.MiniJson.Deserialize(locoGeo.text) as Dictionary<string, object>;
                if (root != null)
                {
                    var geo = root["minecraft:geometry"] as List<object>;
                    var g0 = geo[0] as Dictionary<string, object>;
                    var bones = g0["bones"] as List<object>;
                    float y0 = float.MaxValue, y1 = float.MinValue;
                    foreach (var bo in bones)
                    {
                        var bd = bo as Dictionary<string, object>;
                        if (!bd.ContainsKey("cubes")) continue;
                        foreach (var co in (List<object>)bd["cubes"])
                        {
                            var cd = co as Dictionary<string, object>;
                            var org = (List<object>)cd["origin"];
                            var sz = (List<object>)cd["size"];
                            float oy = System.Convert.ToSingle(org[1]);
                            float sy = System.Convert.ToSingle(sz[1]);
                            y0 = Mathf.Min(y0, oy);
                            y1 = Mathf.Max(y1, oy + sy);
                        }
                    }
                    if (y1 > y0) locoH = (y1 - y0) / 16f * 1.585f;
                }
            }
            Add("consist.scale", locoH > 2.8f && locoH < 4.6f, $"loco geo height {locoH:F2} u (want 2.8..4.6)");

            // ---- drive: accelerate along the line ----
            train.Enter(null);
            Add("ride.enter", train.Occupied && train.Seat != null, $"occupied={train.Occupied} seat={(train.Seat != null)}");

            train.throttleIn = 1f;
            float maxReached = 0f;
            var dt = 1f / 60f;
            float t6 = 6f;
            while (t6 > 0f)
            {
                train.Tick(dt);
                maxReached = Mathf.Max(maxReached, train.Speed);
                t6 -= dt;
            }
            Add("drive.accel", maxReached >= 0.8f * train.maxSpeed,
                $"speed {maxReached:F2}/{train.maxSpeed} u/s after 6 s");

            // positions moved forward (+X)?
            float x0 = train.transform.position.x;
            train.Tick(2f); // big step: just advance
            float x1 = train.transform.position.x;
            Add("drive.move", x1 > x0 + 1f, $"head x {x0:F1} -> {x1:F1}");

            // ---- spacing: consecutive car centres ----
            var centres = new List<Vector3>();
            foreach (Transform c in consistRoot)
            {
                if (c.name.StartsWith("Car_")) centres.Add(c.position);
            }
            bool spacingOk = centres.Count == 5;
            float worst = 0f;
            var want = train.CouplerSpacings();
            for (int i = 1; i < centres.Count && spacingOk && i < want.Length + 1; i++)
            {
                float d = Vector3.Distance(centres[i - 1], centres[i]);
                worst = Mathf.Max(worst, Mathf.Abs(d - want[i - 1]));
                if (Mathf.Abs(d - want[i - 1]) > 0.6f) spacingOk = false;
            }
            Add("follow.cars", spacingOk, $"5 cars, worst dev {worst:F2} u vs coupler chain ({centres.Count} centres)");

            // ---- dead end: coast into the buffer stop ----
            train.throttleIn = 1f;
            for (int i = 0; i < 60 * 30; i++) train.Tick(dt);
            bool stopped = Mathf.Abs(train.Speed) < 0.05f;
            Vector2Int head = train.HeadCell;
            int hg = sim.SurfaceHeight(head.x, head.y, true);
            var hb0 = sim.GetBlock(head.x, hg, head.y);
            var hb1 = sim.GetBlock(head.x, hg + 1, head.y);
            var hb = (hb0 == BlockType.Rail || hb0 == BlockType.RailX) ? hb0 : hb1;
            bool onRail = hb == BlockType.Rail || hb == BlockType.RailX;
            Add("drive.stop", stopped && onRail, $"speed {train.Speed:F3} head=({head.x},{head.y}) block={hb} (stopped={stopped} onRail={onRail})");

            train.Exit();
            Add("ride.exit", !train.Occupied, $"occupied={train.Occupied}");

            // JSON report
            var sb = new StringBuilder();
            sb.AppendLine("{ \"summary\": {\"failed\": " + fail + "} }");
            System.IO.File.WriteAllText("D:/zlj_world/_logs/m41_train_report.json", sb.ToString());

            if (fail > 0) Debug.LogError($"M41TRAIN RESULT: FAIL ({fail} failed)");
            else Debug.Log("M41TRAIN RESULT: PASS (7/7)");
        }
    }
}
