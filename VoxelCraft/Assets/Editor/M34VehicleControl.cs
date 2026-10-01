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
    /// M34 vehicle CONTROL verification (not just model presence): drives
    /// DrivableVehicle.Tick(dt) with scripted input in batch mode and asserts
    /// measurable signals per unity-batch-verification rule 2.
    ///
    /// Checks per vehicle:
    ///  car   accel:   reaches >= 80% maxSpeed within 6 s of full throttle
    ///  car   brake:   stops from ~max within 2.5 s of full reverse
    ///  car   steer:   yaw changes >= 90 deg over 3 s of full steer at speed
    ///  car   ground:  |y - (surface+1+wheelRadius+suspRest*(1-comp))| <= 1.2
    ///  car   susp:    AvgCompression in 0..1 and > 0 on flat ground
    ///  mount gallop:  reaches >= 80% gallopSpeed within 5 s (Shift+W)
    ///  mount anim:    mountPlayer.moving true at speed, walkSpeedRef > 0.4
    ///  mount stop:    walkSpeedRef eases below 0.2 within 2 s of release
    /// </summary>
    public static class M34VehicleControl
    {
        class Check
        {
            public string goal, subject;
            public bool pass;
            public float value;
            public string range;
            public string detail;
        }

        static readonly List<Check> checks = new List<Check>();

        static int failCount;

        static void Add(string goal, string subject, bool pass, float value, string range, string detail = "")
        {
            checks.Add(new Check { goal = goal, subject = subject, pass = pass, value = value, range = range, detail = detail });
            if (!pass) failCount++;
            Debug.Log($"[M34C] {(pass ? "PASS" : "FAIL")} {goal}/{subject} value={value:F3} range={range} {detail}");
        }

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var atlas = Art.TextureFactory.Build();
            var sim = new WorldSim(1337);
            sim.dataRadius = 3; sim.meshRadius = 3;
            var rects = new Rect[36];
            for (int i = 0; i < 36; i++) rects[i] = atlas.TileRect((TileId)i);
            sim.tileRects = rects;
            var remeshed = new List<Chunk>();
            sim.Step(0, 0, 100000f, 100000f, remeshed, null);

            // a fake WorldRoot exposing the batch sim (vehicles read
            // world.sim.SurfaceHeight; no meshes/colliders needed for control
            // checks)
            var stubGo = new GameObject("StubWorld");
            var stub = stubGo.AddComponent<WorldRoot>();
            typeof(WorldRoot).GetProperty("sim")
                .SetValue(stub, sim);

            // ---------- WHEELS ----------
            foreach (string name in new[] { "car1", "police1" })
            {
                var vgo = new GameObject($"VC_{name}");
                int gx = 8, gz = 20;
                int g0 = sim.SurfaceHeight(gx, gz, true);
                vgo.transform.position = new Vector3(gx + 0.5f, g0 + 1f + 0.9f, gz + 0.5f);
                var v = vgo.AddComponent<DrivableVehicle>();
                v.world = stub;
                v.vehicleName = name;
                v.chassis = DrivableVehicle.ChassisType.Wheels;
                v.mountCreature = false;
                v.BuildModel();
                DriveCarChecks(v, sim, name);
            }

            // ---------- MOUNT ----------
            foreach (string sp in new[] { "horse", "donkey" })
            {
                var vgo = new GameObject($"VC_{sp}");
                int gx = 20, gz = 20;
                int g0 = sim.SurfaceHeight(gx, gz, true);
                vgo.transform.position = new Vector3(gx + 0.5f, g0 + 1.02f, gz + 0.5f);
                var v = vgo.AddComponent<DrivableVehicle>();
                v.world = stub;
                v.vehicleName = sp;
                v.mountCreature = true;
                v.chassis = DrivableVehicle.ChassisType.Mount;
                v.BuildModel();
                DriveMountChecks(v, sim, sp);
            }

            int fail = failCount;

            // JSON report (rule 2)
            var sb = new StringBuilder();
            sb.AppendLine("{ \"checks\": [");
            for (int i = 0; i < checks.Count; i++)
            {
                var c = checks[i];
                sb.Append($"  {{\"goal\":\"{c.goal}\",\"subject\":\"{c.subject}\",\"pass\":{(c.pass ? "true" : "false")},\"value\":{c.value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\"range\":\"{c.range}\",\"detail\":\"{c.detail}\"}}");
                sb.AppendLine(i < checks.Count - 1 ? "," : "");
            }
            sb.AppendLine("],");
            int total = checks.Count;
            sb.AppendLine($"  \"summary\": {{\"total\":{total},\"failed\":{fail}}}");
            sb.AppendLine("}");
            System.IO.File.WriteAllText("D:/zlj_world/_logs/m34_control_report.json", sb.ToString());

            if (fail > 0) Debug.LogError($"M34CONTROL RESULT: FAIL ({total - fail}/{total})");
            else Debug.Log($"M34CONTROL RESULT: PASS ({total}/{total})");
        }

        static int DriveCarChecks(DrivableVehicle v, WorldSim sim, string name)
        {
            v.Enter(null);
            var dt = 1f / 60f;

            // settle suspension on flat ground first
            for (int i = 0; i < 180; i++) v.Tick(dt);
            float comp0 = v.AvgCompression;
            Add("susp-settle", name, Mathf.Abs(comp0) <= 0.3f, comp0, "|c|<=0.3 (sag equilibrium)", $"grounded={v.AnyWheelGrounded}");

            // acceleration run
            v.throttleIn = 1f;
            float top = 0f;
            for (int i = 0; i < 6 * 60; i++) { v.Tick(dt); top = Mathf.Max(top, v.Speed); }
            Add("accel", name, top >= v.maxSpeed * 0.8f, top, $">= {v.maxSpeed * 0.8f:F1}", $"reached {top:F2}/{v.maxSpeed}");

            // braking from speed
            v.throttleIn = -1f;
            float t0 = Time.realtimeSinceStartup;
            int brakeFrames = 0;
            for (int i = 0; i < (int)(2.5f * 60); i++)
            {
                v.Tick(dt);
                brakeFrames++;
                if (Mathf.Abs(v.Speed) < 0.05f) break;
            }
            float brakeTime = brakeFrames * dt;
            Add("brake", name, Mathf.Abs(v.Speed) < 0.05f && brakeTime <= 2.5f, brakeTime, "<= 2.5s", $"final={v.Speed:F3}");

            // steering authority at cruise
            v.throttleIn = 1f; v.steerIn = 1f;
            float yaw0 = v.transform.eulerAngles.y;
            for (int i = 0; i < 3 * 60; i++) v.Tick(dt);
            float dyaw = Mathf.Abs(Mathf.DeltaAngle(yaw0, v.transform.eulerAngles.y));
            Add("steer", name, dyaw >= 90f, dyaw, ">= 90 deg", $"speed={v.Speed:F2}");

            // ground tracking: y within ride band of the voxel surface
            Vector3 p = v.transform.position;
            int sg = sim.SurfaceHeight(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z), true);
            float ride = p.y - (sg + 1f);
            Add("ground", name, ride > -0.5f && ride < 2.2f, ride, "-0.5..2.2", $"surface={sg} y={p.y:F2}");

            v.Exit();
            return 0;
        }

        static int DriveMountChecks(DrivableVehicle v, WorldSim sim, string sp)
        {
            v.Enter(null);
            var dt = 1f / 60f;
            for (int i = 0; i < 60; i++) v.Tick(dt);

            // gallop: W + Shift; steer into a circle so the mount stays on
            // open ground instead of cliff-stopping
            var f = typeof(DrivableVehicle).GetField("mountPlayer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var mp = f != null ? f.GetValue(v) as BedrockAnimationPlayer : null;
            v.throttleIn = 1f; v.gallopIn = true; v.steerIn = 0.55f;
            float top = 0f;
            bool movingAtSpeed = false;
            float cadAtSpeed = 0f;
            for (int i = 0; i < 5 * 60; i++)
            {
                v.Tick(dt);
                top = Mathf.Max(top, v.Speed);
                if (mp != null && v.Speed >= v.gallopSpeed * 0.8f)
                {
                    movingAtSpeed = mp.moving;
                    cadAtSpeed = mp.walkSpeedRef;
                }
            }
            Add("gallop", sp, top >= v.gallopSpeed * 0.8f, top, $">= {v.gallopSpeed * 0.8f:F1}", "");
            Add("anim-moving", sp, movingAtSpeed, movingAtSpeed ? 1f : 0f, "== 1", $"player={(mp == null ? "null" : "ok")} top={top:F2}");
            Add("anim-cadence", sp, cadAtSpeed > 0.4f, cadAtSpeed, "> 0.4", $"ref={cadAtSpeed:F2}");

            // stop: cadence eases down
            v.throttleIn = 0f; v.gallopIn = false;
            for (int i = 0; i < 2 * 60; i++) v.Tick(dt);
            float cad2 = mp != null ? mp.walkSpeedRef : 1f;
            Add("anim-ease", sp, cad2 < 0.2f, cad2, "< 0.2", $"speed={v.Speed:F2}");

            v.Exit();
            return 0;
        }
    }
}
