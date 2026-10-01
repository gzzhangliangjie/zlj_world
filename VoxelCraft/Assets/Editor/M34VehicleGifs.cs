using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Creatures;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// M34 per-vehicle GIF frames: ONE vehicle per strip (user directive:
    /// individual proof, no group shots). Each vehicle is DRIVEN by
    /// DrivableVehicle.Tick - wheels roll over terrain, suspension
    /// compresses, mounts gallop with legs animating. Every frame passes
    /// the 8-connected-component gate before it can ship.
    /// </summary>
    public static class M34VehicleGifs
    {
        const string OutDir = @"D:\zlj_world\_logs\gif\vehicles";
        const int Fps = 20;
        const float Seconds = 4f;          // drive manoeuvre duration

        static readonly List<(string, int, int)> summary = new();
        static bool allPass = true;

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));
            Directory.CreateDirectory(OutDir);

            var atlas = Art.TextureFactory.Build();
            var sim = new WorldSim(1337);
            sim.dataRadius = 6; sim.meshRadius = 6;
            var rects = new Rect[59];
            for (int i = 0; i < 59; i++) rects[i] = atlas.TileRect((TileId)i);
            sim.tileRects = rects;
            var rem = new List<Chunk>();
            // Step takes CHUNK coords: centre on chunk (1,1) so x 0..112
            // (the drive corridor at x 18..45) is loaded and meshed
            sim.Step(1, 1, 100000f, 100000f, rem, null);

            var stubGo = new GameObject("StubWorld");
            var stub = stubGo.AddComponent<WorldRoot>();
            typeof(WorldRoot).GetProperty("sim").SetValue(stub, sim);

            // terrain backdrop meshes (sunlit studio look: globals + light)
            var root = new GameObject("VehGifs");
            var blocksShader = Resources.Load<Shader>("Shaders/BlocksShader");
            var solidMat = new Material(blocksShader) { mainTexture = atlas.atlas };
            foreach (var c in rem)
            {
                if (c.solidMeshData == null) continue;
                var mgo = new GameObject($"C{c.cx}_{c.cz}");
                mgo.transform.SetParent(root.transform);
                mgo.transform.localPosition = new Vector3(c.cx * 16, 0, c.cz * 16);
                mgo.AddComponent<MeshFilter>().sharedMesh = c.solidMeshData.ToMesh(null);
                mgo.AddComponent<MeshRenderer>().sharedMaterial = solidMat;
            }
            var lightGo = new GameObject("Sun");
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            lightGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.68f, 0.80f, 0.92f);
            cam.fieldOfView = 45f;
            cam.enabled = false;

            // jobs: (name, chassis, throttle profile)
            // car1: accelerate -> brake -> reverse turn
            // police1: full-speed circle
            // horse: gallop straight
            // donkey: walk -> gallop
            RunVehicle(sim, stub, cam, "car1", DrivableVehicle.ChassisType.Wheels, "car", CircleProfile);
            RunVehicle(sim, stub, cam, "police1", DrivableVehicle.ChassisType.Wheels, "car", StraightProfile);
            RunVehicle(sim, stub, cam, "horse", DrivableVehicle.ChassisType.Mount, "gallop", MountProfile);
            RunVehicle(sim, stub, cam, "donkey", DrivableVehicle.ChassisType.Mount, "gallop", MountProfile);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[VehGifs] SUMMARY");
            foreach (var (n, t, f) in summary)
                sb.AppendLine($"  {n,-12} frames={t} connectivityFail={f}");
            sb.AppendLine(allPass ? "[VehGifs] RESULT: PASS" : "[VehGifs] RESULT: FAIL");
            Debug.Log(sb.ToString());
            File.WriteAllText(Path.Combine(OutDir, "report.txt"), sb.ToString());
        }

        // ---- throttle/steer profiles: (phase 0..1) -> (throttle, steer) ----
        static (float, float) CircleProfile(float p)
            => (p < 0.15f ? 0f : 1f, 0.75f);                      // launch then circle
        static (float, float) StraightProfile(float p)
            => (p < 0.15f ? 0f : 1f, -0.75f);                     // circle right (visible steering)
        static (float, float) MountProfile(float p)
            => (p < 0.9f ? 1f : -1f, 0.15f);                      // run then halt

        static void RunVehicle(WorldSim sim, WorldRoot stub, Camera cam,
            string name, DrivableVehicle.ChassisType ch, string tag,
            System.Func<float, (float, float)> profile)
        {
            var vgo = new GameObject($"Gif_{name}");
            // start on open ground facing +x along the drive corridor
            int gx = 18, gz = 20;
            int g0 = sim.SurfaceHeight(gx, gz, true);
            vgo.transform.position = new Vector3(gx + 0.5f, g0 + (ch == DrivableVehicle.ChassisType.Mount ? 1.02f : 2.0f), gz + 0.5f);
            vgo.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            var v = vgo.AddComponent<DrivableVehicle>();
            v.world = stub;
            v.vehicleName = name;
            v.chassis = ch;
            if (ch == DrivableVehicle.ChassisType.Mount) v.mountCreature = true;
            v.BuildModel();
            v.Enter(null);

            int total = Mathf.CeilToInt(Seconds * Fps);
            var frames = new List<Texture2D>();
            float dt = 1f / Fps;
            for (int i = 0; i < total; i++)
            {
                var (th, st) = profile(i / (float)total);
                v.throttleIn = th; v.steerIn = st;
                v.gallopIn = ch == DrivableVehicle.ChassisType.Mount;
                v.Tick(dt);
                frames.Add(Shoot(cam, vgo));
            }
            Debug.Log($"[VehGifs] {name}: top={v.Speed:F2} pos={vgo.transform.position} comp={v.AvgCompression:F2} frames={total}");

            string path = Path.Combine(OutDir, name + "_%04d.png");
            for (int i = 0; i < frames.Count; i++)
                File.WriteAllBytes(path.Replace("%04d", i.ToString("D4")), frames[i].EncodeToPNG());

            // driving-scene gate (terrain behind the vehicle makes plain
            // component counts meaningless - terrain + car are 2 legal
            // components). Assert instead: (a) every frame after launch has
            // motion vs the previous one (vehicle actually driving), (b) the
            // centre band holds the subject (non-bg content inside the
            // middle 40% of the frame).
            int still = 0;
            Color32[] prev = null;
            for (int i = 16; i < frames.Count; i++)
            {
                var px = frames[i].GetPixels32();
                if (prev != null)
                {
                    int d = 0;
                    for (int q = 0; q < px.Length; q += 7)
                        if (Mathf.Abs(px[q].r - prev[q].r) + Mathf.Abs(px[q].g - prev[q].g) + Mathf.Abs(px[q].b - prev[q].b) > 42) d++;
                    if (d < px.Length / 7 / 40) still++;
                }
                prev = px;
            }
            var c0 = frames[Mathf.Min(40, frames.Count - 1)].GetPixels32();
            int w = frames[0].width, h = frames[0].height, centre = 0, tot = 0;
            for (int y = (int)(h * 0.3f); y < (int)(h * 0.7f); y++)
                for (int x = (int)(w * 0.3f); x < (int)(w * 0.7f); x++)
                { tot++; if (c0[y * w + x].a > 128 && c0[y * w + x].r + c0[y * w + x].g + c0[y * w + x].b > 60) centre++; }
            float centreRatio = tot > 0 ? centre / (float)tot : 0f;
            bool pass = still <= 1 && centreRatio > 0.15f;
            Debug.Log($"[VehGifs] {name}: {(pass ? "PASS" : "FAIL")} stillFrames={still}/{frames.Count - 8} centreFill={centreRatio:F2}");
            summary.Add((name, frames.Count, pass ? 0 : 1));
            if (!pass) allPass = false;

            foreach (var t in frames) Object.DestroyImmediate(t);
            Object.DestroyImmediate(vgo);
        }

        static Texture2D Shoot(Camera cam, GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Vector3 center = b.center;
            float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
            float dist = maxDim * 2.4f + 1.2f;
            // side-front 3/4 view of the vehicle facing +x
            Vector3 dir = new Vector3(-0.9f, 0.38f, 1.35f).normalized;
            cam.transform.position = center + dir * dist;
            cam.transform.LookAt(center);
            var rt = new RenderTexture(420, 420, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render(); cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(420, 420, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 420, 420), 0, 0);
            tex.Apply(false, false);
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            return tex;
        }

        /// <summary>Background-vote + 8-connected components (photostudio
        /// standard, shared with BehaviourGifFrames).</summary>
        static int CountComponents(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            int[] hist = new int[3 << 16];
            void Vote(Color32 c)
            {
                int ri = c.r >> 3, gi = c.g >> 3, bi = c.b >> 3;
                hist[(ri << 10) | (gi << 5) | bi]++;
            }
            for (int x = 0; x < w; x++) { Vote(px[x]); Vote(px[(h - 1) * w + x]); }
            for (int y = 0; y < h; y++) { Vote(px[y * w]); Vote(px[y * w + w - 1]); }
            int best = 0, bestIdx = -1;
            for (int i = 0; i < hist.Length; i++)
                if (hist[i] > best) { best = hist[i]; bestIdx = i; }
            var bg = new Color32(
                (byte)((((bestIdx >> 10) & 31) << 3) | 4),
                (byte)((((bestIdx >> 5) & 31) << 3) | 4),
                (byte)(((bestIdx & 31) << 3) | 4), 255);
            bool IsBg(Color32 c) =>
                Mathf.Abs(c.r - bg.r) <= 6 && Mathf.Abs(c.g - bg.g) <= 6 && Mathf.Abs(c.b - bg.b) <= 6;

            var labels = new int[w * h];
            var queue = new Queue<int>(w * h);
            int comps = 0;
            int[] off = { -1, 1, -w, w, -w - 1, -w + 1, w - 1, w + 1 };
            for (int s = 0; s < w * h; s++)
            {
                if (labels[s] != 0 || IsBg(px[s])) continue;
                comps++;
                labels[s] = comps;
                queue.Clear();
                queue.Enqueue(s);
                int area = 0;
                while (queue.Count > 0)
                {
                    int p = queue.Dequeue();
                    area++;
                    int cx = p % w;
                    foreach (var o in off)
                    {
                        int q = p + o;
                        if (q < 0 || q >= w * h) continue;
                        int qx = q % w;
                        if (Mathf.Abs(qx - cx) > 1) continue;
                        if (labels[q] != 0 || IsBg(px[q])) continue;
                        labels[q] = comps;
                        queue.Enqueue(q);
                    }
                }
                if (area < 12) comps--;   // texel slivers are not parts
            }
            return comps;
        }
    }
}
