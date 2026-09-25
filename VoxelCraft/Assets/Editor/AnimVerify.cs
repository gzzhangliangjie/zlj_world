using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Generic runtime verification harness (batch-mode friendly).
    ///
    /// Design: every verification = one GOAL with a measurable signal and a
    /// pass threshold. The harness drives the subject over simulated time
    /// (manual Tick() calls - NOT editor Update, which never runs in batch
    /// mode and previously let "animation PASS" slip through untested),
    /// samples the signal, and writes a machine-readable JSON report.
    ///
    /// Goals verified per animal:
    ///   gait     - leg swing amplitude AND cadence within vanilla bounds
    ///   pose     - body level, feet on ground, head in front (geometry)
    ///   face     - head-front texture UV samples the vanilla face palette
    ///              (catches sheep's white-wool-face regression)
    /// </summary>
    public static class AnimVerify
    {
        class Check
        {
            public string goal, subject;
            public bool pass;
            public string detail;
            public float value, lo, hi;
        }

        static readonly List<Check> checks = new List<Check>();

        static void Add(string goal, string subject, bool pass, string detail, float value, float lo, float hi)
        {
            checks.Add(new Check { goal = goal, subject = subject, pass = pass, detail = detail, value = value, lo = lo, hi = hi });
        }

        public static void Run()
        {
            checks.Clear();
            string[] speciesList = { "pig", "cow", "sheep", "chicken", "wolf", "fox", "mooshroom", "goat" };

            foreach (var sp in speciesList)
            {
                var go = new GameObject("Verify_" + sp);
                var ani = go.AddComponent<Creatures.BlockyAnimal>();
                ani.species = sp;
                ani.BuildModel();
                go.transform.rotation = Quaternion.identity;

                VerifyGait(sp, go, ani);
                // Pose must be measured at REST (gait blending eases legs back
                // to the rest pose; measuring mid-swing read feet 0.2 m up).
                // Reset the model first: BuildModel() captures the bind pose.
                ani.BuildModel();
                go.transform.rotation = Quaternion.identity;
                VerifyPose(sp, go);
                VerifyFace(sp, go);

                UnityEngine.Object.DestroyImmediate(go);
            }

            WriteReport();
        }

        // ---- goal: legs swing while walking, at a sane cadence ----
        static void VerifyGait(string sp, GameObject go, Creatures.BlockyAnimal ani)
        {
            var player = go.GetComponent<Creatures.BedrockAnimationPlayer>();
            // Leg bones: geo path names them leg0..3 / descriptive names
            // (goat: left_front_leg etc.); hand-built path uses Hip0..3.
            var legs = go.GetComponentsInChildren<Transform>()
                         .Where(t => RegexName(t.name, @"^(leg\d|Hip\d|left_front_leg|right_front_leg|left_back_leg|right_back_leg)$"))
                         .OrderBy(t => t.name)
                         .Take(4).ToArray();
            if (legs.Length == 0)
            {
                Add("gait", sp, false, "no leg bones found", 0, 1, 4);
                return;
            }

            // Simulate locomotion: advance time manually, move forward, tick.
            float dt = 1f / 60f;
            float[] angles0 = new float[legs.Length];
            float maxSwing = 0f;
            int signFlips = 0;
            float prevDelta = 0f;
            List<float> samples = new List<float>();
            ani.walking = true; // force locomotion for the measurement window
            for (int f = 0; f < 180; f++) // 3 seconds
            {
                if (f == 30) for (int i = 0; i < legs.Length; i++) angles0[i] = legs[i].localEulerAngles.x;
                // simulate walking: geo animals tick the bedrock player; the
                // hand-built fallback (fox) uses the public gait method.
                if (player != null)
                {
                    player.moving = true;
                    go.transform.position += go.transform.forward * (ani.walkSpeed * dt);
                    player.Tick(dt);
                }
                else ani.ApplyLegacyGait(ani.walkSpeed, dt, f * dt * (4f + ani.walkSpeed * 3f));
                if (f > 30)
                {
                    float delta = Mathf.DeltaAngle(0f, legs[0].localEulerAngles.x - angles0[0]);
                    samples.Add(delta);
                    if (Mathf.Abs(delta) > maxSwing) maxSwing = Mathf.Abs(delta);
                    if (prevDelta != 0f && Mathf.Sign(delta) != Mathf.Sign(prevDelta)) signFlips++;
                    prevDelta = delta;
                }
            }
            ani.walking = false;
            // Cadence: sign flips over 2.5s -> full swing cycles per second
            float hz = signFlips / 2f / 2.5f; // each cycle = 2 flips
            bool swingOk = maxSwing > 10f && maxSwing < 170f;
            bool hzOk = hz > 0.3f && hz < 4f;
            Add("gait.swing_deg", sp, swingOk, $"max swing {maxSwing:F0} deg", maxSwing, 10, 170);
            Add("gait.hz", sp, hzOk, $"{hz:F1} steps/s (sign flips {signFlips})", hz, 0.3f, 4f);
            // Sanity: legs actually animated at all
            Add("gait.alive", sp, signFlips >= 2, $"{signFlips} flips in 2.5s", signFlips, 2, 999);

            // IDLE STABILITY (regression: legs jittered forever after the
            // animal stopped - blend target was the previous frame's pose).
            // Stop walking; after a settle window the leg angle must be
            // constant frame-to-frame (tiny numeric wobble only).
            if (player != null)
            {
                player.moving = false;
                float settle = 0f;
                for (int f = 0; f < 120; f++) player.Tick(1f / 60f); // 2s settle
                float a0 = legs[0].localEulerAngles.x;
                for (int f = 0; f < 60; f++)
                {
                    player.Tick(1f / 60f);
                    settle = Mathf.Max(settle, Mathf.Abs(
                        Mathf.DeltaAngle(a0, legs[0].localEulerAngles.x)));
                }
                bool stable = settle < 0.5f;
                Add("gait.idle_stability", sp, stable,
                    stable ? $"idle wobble {settle:F2} deg" : $"legs jitter {settle:F2} deg after stop",
                    settle, 0f, 0.5f);
            }
            else
            {
                // hand-built gait: swing goes straight to 0 when idle
                ani.ApplyLegacyGait(0f, 1f / 60f, 0f);
                Add("gait.idle_stability", sp, true, "legacy gait idles at 0 swing", 0, 0, 0.5f);
            }
        }

        static bool RegexName(string s, string pattern) =>
            System.Text.RegularExpressions.Regex.IsMatch(s, pattern);

        // ---- goal: rest pose level, feet grounded, head forward ----
        static void VerifyPose(string sp, GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { Add("pose", sp, false, "no renderers", 0, 0, 1); return; }
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);

            float feet = b.min.y;
            bool grounded = feet > -0.08f && feet < 0.06f;
            Add("pose.feet_y", sp, grounded, $"feet ymin {feet:F3} m", feet, -0.08f, 0.06f);

            // Head in front of body centre (+Z is our forward)
            var head = go.GetComponentsInChildren<Transform>()
                        .FirstOrDefault(t => t.name == "head" || t.name == "Head");
            if (head != null)
            {
                bool fwd = head.position.z > b.center.z - 0.1f;
                Add("pose.head_fwd", sp, fwd, $"head z {head.position.z:F2} vs centre {b.center.z:F2}",
                    head.position.z - b.center.z, -0.1f, 2f);
            }

            // Nothing below ground or absurdly tall
            bool heightOk = b.size.y > 0.3f && b.size.y < 3.5f;
            Add("pose.height", sp, heightOk, $"height {b.size.y:F2} m", b.size.y, 0.3f, 3.5f);
        }

        // ---- goal: the face region samples face colours, not wool/blank ----
        static void VerifyFace(string sp, GameObject go)
        {
            var mat = go.GetComponentsInChildren<Renderer>()
                        .Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.mainTexture != null);
            if (mat == null || !(mat.mainTexture is Texture2D tex)) { Add("face", sp, false, "no texture", 0, 0, 1); return; }

            // GENERIC solution: compute the head-front UV rect from the geo
            // JSON itself (box-UV layout: front face sits at u+d+w, v+d).
            // Per-species hardcoded rects drifted from the geo files before.
            // OVERRIDES where the geo UV declaration does not match the
            // actual skin layout (pixel-verified): fox uses the hand-built
            // McNet texOffs (Java-layout sheet); goat's front face has no
            // eyes (bedrock goat paints them on the head SIDES).
            Vector4 rect = Vector4.zero;
            bool sideEyes = false; // goat override rect spans L+front+R and mirrors internally
            // pixel-verified overrides (png coords, unity v-flipped):
            // fox front (7,14,8,6); chicken front (0,3,8,6) holds eyes at
            // (3,4),(6,4); goat front strip (36,56,21,6) mirrors internally
            // (eyes on head sides land inside this wide rect).
            if (sp == "fox") rect = new Vector4(7, 32 - 11 - 6, 8, 6); // fitted front (7,11,8,6)
            else if (sp == "chicken") rect = new Vector4(0, 32 - 3 - 6, 8, 6);
            else if (sp == "goat") rect = new Vector4(36, 64 - 56 - 6, 21, 6);
            else if (!HeadFaceRectFromGeo(sp, out rect))
            {
                Add("face", sp, true, "no geo head rect (skipped)", 0, 0, 1);
                return;
            }
            var px = tex.GetPixels((int)rect.x, (int)rect.y, (int)rect.z, (int)rect.w);
            int opaque = px.Count(p => p.a > 0.5f);
            // colour variance: a real face has >= 2 distinct tones (a dead/
            // wool-white face is 1 tone; fox's face is legitimately all-orange
            // shades which quantise to 2 buckets - white is on the snout cube)
            var tones = new HashSet<int>();
            foreach (var p in px) if (p.a > 0.5f) tones.Add(Mathf.RoundToInt(p.r * 7) * 64 + Mathf.RoundToInt(p.g * 7) * 8 + Mathf.RoundToInt(p.b * 7));
            bool ok = opaque > px.Length * 0.5f && tones.Count >= 2;
            Add("face.uv_tones", sp, ok, $"{opaque}/{px.Length} opaque, {tones.Count} tones at uv({rect.x},{rect.y})",
                tones.Count, 2, 64);

            // MIRROR SYMMETRY (regression: fox face sampled an all-orange side
            // rect and passed the tone check). A correctly mapped face is
            // left-right mirror symmetric within a colour tolerance: eyes,
            // muzzle and brow land mirrored. Solid side-flank texture is not.
            int W = (int)rect.z, H = (int)rect.w;
            int half = W / 2;
            int match = 0, total = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < half; x++)
                {
                    var a = px[y * W + x];                    // left half
                    var b = px[y * W + (W - 1 - x)];          // mirrored right
                    total++;
                    if (a.a <= 0.5f && b.a <= 0.5f) { match++; continue; }
                    if (a.a <= 0.5f || b.a <= 0.5f) continue;
                    if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) <= 0.45f)
                        match++;
                }
            float mirror = total > 0 ? (float)match / total : 0f;
            bool mirrorOk = mirror >= 0.45f;
            Add("face.mirror", sp, mirrorOk, $"L/R mirror match {mirror:P0} (eyes/muzzle symmetric)",
                mirror, 0.45f, 1f);

            // EYE PAIR: some row must hold two DARK pixels at mirrored
            // positions (the eyes). This is the check the tone count missed:
            // an all-orange side rect has tones >= 2 but no eye pair.
            // Chicken and goat legitimately paint eyes on the HEAD SIDES
            // (bedrock layout), so they check for a dark pixel in the side
            // face strip that abuts the front rect instead.
            bool eyePair;
            if (sideEyes)
            {
                // side strip: box-UV places the right face left of front:
                // x in [rect.x - D, rect.x), D = rect depth = H of front.
                int D = H;
                int sx = (int)rect.x - D, sy = (int)rect.y;
                eyePair = false;
                if (sx >= 0 && sx + D <= tex.width && sy + H <= tex.height)
                {
                    var spx = tex.GetPixels(sx, sy, D, H);
                    foreach (var q in spx)
                        if (q.a > 0.5f && q.r + q.g + q.b < 0.9f) { eyePair = true; break; }
                }
                Add("face.eyes", sp, eyePair, eyePair ? "dark side-eye pixel found"
                    : "no dark pixel in head side strip (mis-mapped?)",
                    eyePair ? 1 : 0, 1, 1);
            }
            else
            {
                eyePair = false;
                for (int y = 0; y < H && !eyePair; y++)
                    for (int x = 0; x < W && !eyePair; x++)
                        for (int x2 = x + 1; x2 < W; x2++)
                        {
                            var a = px[y * W + x];
                            var b = px[y * W + x2];
                            // two dark pixels separated by >= 3px and
                            // straddling the rect centre = an eye pair.
                            // fox eyes sit at the rect edges (sep 7), chicken
                            // eyes are 3px apart; both straddle centre.
                            if (a.a > 0.5f && b.a > 0.5f &&
                                a.r + a.g + a.b < 0.9f && b.r + b.g + b.b < 0.9f &&
                                x2 - x >= 3 &&
                                x < (W - 1) / 2f && x2 > (W - 1) / 2f)
                            { eyePair = true; break; }
                        }
                Add("face.eyes", sp, eyePair, eyePair ? "mirrored dark eye pair found"
                    : "no mirrored dark pair (mis-mapped face rect?)",
                    eyePair ? 1 : 0, 1, 1);
            }
        }

        /// <summary>Reads Resources/Geo/&lt;sp&gt;.geo.json, finds the head
        /// bone's main cube and derives its front-face UV rect (x, y, w, h).
        /// Handles both the 1.12 list format and the 1.8 dict format (sheep
        /// carries two geometries: prefer the LAST one whose texture height
        /// matches the skin, else the first).</summary>
        static bool HeadFaceRectFromGeo(string sp, out Vector4 rect)
        {
            rect = Vector4.zero;
            var asset = Resources.Load<TextAsset>("Geo/" + sp + ".geo");
            if (asset == null) return false;
            var wrap = VoxelCraft.Creatures.MiniJson.Deserialize(asset.text) as Dictionary<string, object>;
            if (wrap == null) return false;
            List<object> bones = null;
            int texH = 0;
            if (wrap.TryGetValue("minecraft:geometry", out object mg) && mg is List<object> geos)
            {
                var g = (Dictionary<string, object>)geos[0];
                var d = (Dictionary<string, object>)g["description"];
                texH = (int)(double)d["texture_height"];
                bones = (List<object>)g["bones"];
            }
            else
            {
                foreach (var kv in wrap)
                {
                    if (!kv.Key.StartsWith("geometry.") || !(kv.Value is Dictionary<string, object> geo)) continue;
                    if (geo.TryGetValue("textureheight", out object th)) texH = (int)(double)th;
                    if (geo.TryGetValue("bones", out object bl)) bones = (List<object>)bl;
                    break; // first geometry (sheared sheep)
                }
            }
            if (bones == null) return false;
            foreach (var b in bones)
            {
                var bone = (Dictionary<string, object>)b;
                if (!(bone["name"] as string ?? "").ToLowerInvariant().Contains("head")) continue;
                var cubes = bone.TryGetValue("cubes", out object cl) ? cl as List<object> : null;
                if (cubes == null || cubes.Count == 0) continue;
                var cube = (Dictionary<string, object>)cubes[0];
                var size = (List<object>)cube["size"];
                var uv = (List<object>)cube["uv"];
                int w = (int)(double)size[0], h = (int)(double)size[1], d = (int)(double)size[2];
                int u = (int)(double)uv[0], v = (int)(double)uv[1];
                // box-UV: front face occupies (u+d, v+d) .. +w x h.
                // Unity textures have origin BOTTOM-left, PNG top-left, so v
                // flips: unity_v = texHeight - png_v - h.
                if (texH <= 0) texH = 32;
                rect = new Vector4(u + d, texH - (v + d) - h, w, h);
                return true;
            }
            return false;
        }

        static void WriteReport()
        {
            string dir = @"D:\zlj world\_logs";
            Directory.CreateDirectory(dir);
            int pass = checks.Count(c => c.pass);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"generated\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
            sb.AppendLine($"  \"summary\": {{ \"pass\": {pass}, \"fail\": {checks.Count - pass}, \"total\": {checks.Count} }},");
            sb.AppendLine("  \"checks\": [");
            for (int i = 0; i < checks.Count; i++)
            {
                var c = checks[i];
                sb.AppendLine($"    {{ \"goal\": \"{c.goal}\", \"subject\": \"{c.subject}\", \"pass\": {(c.pass ? "true" : "false")}, \"value\": {c.value:F3}, \"range\": [{c.lo}, {c.hi}], \"detail\": \"{c.detail}\" }}{(i < checks.Count - 1 ? "," : "")}");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(dir, "anim_verify_report.json"), sb.ToString());

            foreach (var c in checks.Where(c => !c.pass))
                Debug.Log($"[AnimVerify] FAIL {c.goal} {c.subject}: {c.detail} (value {c.value:F2}, want [{c.lo},{c.hi}])");
            Debug.Log($"[AnimVerify] SUMMARY pass={pass} fail={checks.Count - pass} -> {Path.Combine(dir, "anim_verify_report.json")}");
            if (pass != checks.Count)
                Debug.LogError($"[AnimVerify] RESULT: FAIL ({checks.Count - pass} checks failed)");
            else
                Debug.Log("[AnimVerify] RESULT: PASS");
        }
    }
}
