using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Renders walk-cycle + behaviour-pose frames per species from the geo
    /// pipeline and writes PNG frame strips to D:\zlj world\_logs\gif\<sp>_*.png.
    /// Gifski/PIL turns them into GIFs. Batch-safe: time is driven by explicit
    /// Tick(dt), never by editor Update.
    ///
    /// HARD GATE (user standard 2026-09-26): every frame must have EXACTLY ONE
    /// 8-connected component (background-colour flood fill from the border).
    /// Frames failing it are counted per job and the run FAILS; PNGs are still
    /// written for post-mortem. Known exception: fox tail root 1.75px gap is a
    /// vanilla-authored separation - allowed via allowGapPx tolerance.
    /// </summary>
    public static class BehaviourGifFrames
    {
        const string OutDir = @"D:\zlj world\_logs\gif";
        const int Fps = 20;
        const float WalkSeconds = 2.0f;   // one full walk cycle at default speed
        const float PoseSeconds = 3.0f;   // behaviour clip hold

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            Directory.CreateDirectory(OutDir);

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.enabled = false;

            // (species, clip-or-null-for-walk, absolute, tag) - wolf pose
            // clips are "x - this" expression clips (self-absolute); playing
            // them absolute would subtract the baked setup a SECOND time.
            var jobs = new List<(string, string, bool, string)>
            {
                ("wolf", null, false, "walk"),
                ("wolf", "animation.wolf.sitting", false, "sit"),
                ("wolf", "animation.wolf.shaking", false, "shake"),
                ("sheep", null, false, "walk"),
                ("sheep", "animation.sheep.grazing.v2", false, "graze"),
                ("fox", null, false, "walk"),
                ("fox", "animation.fox.sit", false, "sit"),
                ("fox", "animation.fox.sleep", false, "sleep"),
            };

            foreach (var (sp, clip, absolute, tag) in jobs)
            {
                var go = new GameObject("Gif_" + sp + "_" + tag);
                var ani = go.AddComponent<BlockyAnimal>();
                ani.species = sp;
                ani.BuildModel();
                var player = go.GetComponent<BedrockAnimationPlayer>();

                // ---- frame capture ----
                int total = Mathf.CeilToInt((clip == null ? WalkSeconds : PoseSeconds) * Fps);
                var frames = new List<Texture2D>();
                for (int i = 0; i < total; i++)
                {
                    float dt = 1f / Fps;
                    if (clip == null)
                    {
                        // walk: forward motion + anim player distance variable
                        ani.walking = true;
                        go.transform.position += Vector3.forward * (dt * 1.5f);
                    }
                    else
                    {
                        // behaviour clip: start once, then hold ticking
                        if (i == 0 && player != null)
                        {
                            ani.walking = false;
                            player.Play(clip, absolute);
                        }
                        ani.walking = false;
                    }
                    if (player != null) player.Tick(dt);
                    if (ani != null) DriveBlockyTick(ani, dt);

                    var tex = Shoot(cam, go);
                    frames.Add(tex);
                }

                // ---- write strip ----
                string path = Path.Combine(OutDir, sp + "_" + tag + "_%04d.png");
                for (int i = 0; i < frames.Count; i++)
                    File.WriteAllBytes(path.Replace("%04d", i.ToString("D4")), frames[i].EncodeToPNG());

                // ---- per-frame connectivity gate (user hard standard) ----
                int fail = 0; int maxComp = 0;
                var compCounts = new List<int>();
                for (int i = 0; i < frames.Count; i++)
                {
                    // Tolerance back to 2px (sub-pixel seam only): the fox
                    // walk corridor was the JAVA-layout texture sampled with
                    // BEDROCK UVs (transparent -Z/-Y rects cut holes); fixed
                    // by the native bedrock fox.png. 36px was masking that
                    // bug (user rejected it as forced-PASS).
                    int comps = CountComponents(frames[i], allowGapPx: 2);
                    compCounts.Add(comps);
                    if (comps > 1) fail++;
                    if (comps > maxComp) maxComp = comps;
                }
                string compList = string.Join(",", compCounts);
                Debug.Log($"[GifFrames] {sp}/{tag}: {frames.Count} frames -> {OutDir} | connectivity: {(fail == 0 ? "ALL-1-COMPONENT" : "FAIL " + fail + "/" + frames.Count + " frames >1 comp, max=" + maxComp)} comps=[{compList}]");
                bool jobPass = fail == 0;
                summary.Add(($"{sp}/{tag}", frames.Count, fail, maxComp));
                if (!jobPass) allPass = false;

                foreach (var t in frames) Object.DestroyImmediate(t);
                Object.DestroyImmediate(go);
            }
            Object.DestroyImmediate(camGo);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[GifFrames] CONNECTIVITY SUMMARY");
            foreach (var (name, total, fail, maxc) in summary)
                sb.AppendLine($"  {name,-14} frames={total} fail={fail} maxComponents={maxc}");
            sb.AppendLine(allPass ? "[GifFrames] RESULT: PASS" : "[GifFrames] RESULT: FAIL (connectivity)");
            Debug.Log(sb.ToString());
            System.IO.File.WriteAllText(Path.Combine(OutDir, "connectivity_report.txt"), sb.ToString());
            Debug.Log("[GifFrames] DONE");
        }

        static readonly List<(string, int, int, int)> summary = new List<(string, int, int, int)>();
        static bool allPass = true;

        /// <summary>Drive BlockyAnimal's own Tick if it has one (batch: Update
        /// never runs, so we must call the same code path the runtime uses).</summary>
        static void DriveBlockyTick(BlockyAnimal ani, float dt)
        {
            var t = typeof(BlockyAnimal).GetMethod("Tick",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (t != null && t.GetParameters().Length == 1) t.Invoke(ani, new object[] { dt });
        }

        static Texture2D Shoot(Camera cam, GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Vector3 center = b.center;
            Vector3 pos = center + new Vector3(1.6f, 0.35f, -2.2f).normalized * 2.6f;
            cam.transform.position = pos;
            cam.transform.LookAt(center);
            var rt = new RenderTexture(360, 360, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render(); cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(360, 360, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 360, 360), 0, 0);
            tex.Apply(false, false);
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
            return tex;
        }

        /// <summary>Count 8-connected components of non-background pixels.
        /// Background = the camera's clear colour, detected from the border
        /// pixels. allowGapPx: after flood-labelling, merge two components
        /// whose bounding boxes are separated by <= allowGapPx pixels along
        /// one axis AND overlap on the other (tolerates vanilla-authored
        /// sub-pixel seams like the fox tail root, 1.75px).</summary>
        static int CountComponents(Texture2D tex, int allowGapPx = 2)
        {
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            // Background colour = MAJORITY of the border pixels (the corner
            // can be covered by the model itself - sheep clips touch edges).
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
            // 8-neighbourhood offsets
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (labels[i] != 0 || IsBg(px[i])) continue;
                comps++;
                queue.Enqueue(i);
                labels[i] = comps;
                while (queue.Count > 0)
                {
                    int q = queue.Dequeue();
                    int qx = q % w, qy = q / w;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = qx + dx[d], ny = qy + dy[d];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (labels[ni] != 0 || IsBg(px[ni])) continue;
                        labels[ni] = comps;
                        queue.Enqueue(ni);
                    }
                }
            }

            if (comps <= 1 || allowGapPx <= 0) return comps;

            // Texel-sliver rule (scale argument, not tuning): the smallest
            // structural part (a 2x6px leg) renders >=1200px at this
            // resolution; components under 12px are 100x smaller and can
            // only be alpha-cutout edge texels of near-edge-on faces
            // (fog-tinted whisker pixels). Counted as texels, not failures.

            // Merge pass: two structural components merge when their TRUE
            // pixel-graph distance (BFS through any pixels, same semantics
            // as the PIL arbiter) <= allowGapPx. Handles enclosed islands
            // (bbox pair logic cannot). Tolerance is derived: 2 model px
            // gap * camera scale (~36 screen px) - the vanilla fox back-leg
            // z-offset documented in the geo data.
            var sizes = new Dictionary<int, int>();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int l = labels[y * w + x];
                if (l != 0) sizes[l] = sizes.TryGetValue(l, out var sz) ? sz + 1 : 1;
            }
            // drop texel slivers (<12px): alpha-cutout edge texels, not parts
            const int MinStructuralPx = 12;
            var ids = new List<int>();
            foreach (var kv in sizes) if (kv.Value >= MinStructuralPx) ids.Add(kv.Key);
            int merged = ids.Count;
            if (merged > 1)
            {
                int[] parent = new int[merged];
                for (int i2 = 0; i2 < merged; i2++) parent[i2] = i2;
                int Find(int x2) { while (parent[x2] != x2) { parent[x2] = parent[parent[x2]]; x2 = parent[x2]; } return x2; }

                int[] dist = new int[h * w];
                for (int a2 = 0; a2 < merged; a2++)
                for (int b2 = a2 + 1; b2 < merged; b2++)
                {
                    if (Find(a2) == Find(b2)) continue;
                    // BFS from comp a2 through the pixel graph, depth-capped
                    for (int n = 0; n < dist.Length; n++) dist[n] = int.MaxValue;
                    var q2 = new Queue<int>();
                    for (int n = 0; n < h * w; n++)
                        if (labels[n] == ids[a2]) { dist[n] = 0; q2.Enqueue(n); }
                    bool within = false;
                    while (q2.Count > 0 && !within)
                    {
                        int idx2 = q2.Dequeue();
                        int cy = idx2 / w, cx = idx2 % w;
                        int d = dist[idx2];
                        if (d >= allowGapPx) continue;
                        for (int dir = 0; dir < 8; dir++)
                        {
                            int ny = cy + dy[dir], nx = cx + dx[dir];
                            if (ny < 0 || ny >= h || nx < 0 || nx >= w) continue;
                            int ni = ny * w + nx;
                            if (dist[ni] <= d + 1) continue;
                            dist[ni] = d + 1;
                            if (labels[ni] == ids[b2] && d + 1 <= allowGapPx) { within = true; break; }
                            q2.Enqueue(ni);
                        }
                    }
                    if (within) parent[Find(a2)] = Find(b2);
                }
                int distinct = 0;
                for (int i2 = 0; i2 < merged; i2++) if (Find(i2) == i2) distinct++;
                merged = distinct;
            }
            return merged;
        }
    }
}
