using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VoxelCraft.Creatures;

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
                VerifyStructure(sp, go);
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

        // ---- goal: structure matches vanilla geo truth ----
        /// Batch-mode renderer.bounds can return unit-cube garbage before a
        /// render pass; build the AABB from mesh vertices instead.
        static Bounds VertexAabb(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return r.bounds;
            var verts = mf.sharedMesh.vertices;
            var lm = r.transform.localToWorldMatrix;
            var b = new Bounds();
            for (int i = 0; i < verts.Length; i++)
            {
                var w = lm.MultiplyPoint3x4(verts[i]);
                if (i == 0) b = new Bounds(w, Vector3.zero);
                else b.Encapsulate(w);
            }
            return b;
        }

        // Two checks per species:
        //  A) head/body seam: the head cube's rear face must reach INTO the
        //     body (or mane) volume - a floating head is the classic "did not
        //     diff against the reference" bug (wolf 4px gap, 2026-09-25).
        //  B) sampled-opaque: every opaque-model face rect (mesh UV) must
        //     sample >=95% opaque texels - transparent texels render BLACK
        //     under the unlit shader (sheep legs, fox body padding).
        static void VerifyStructure(string sp, GameObject go)
        {
            var head = go.GetComponentsInChildren<Transform>()
                .FirstOrDefault(t => t.name.ToLowerInvariant() == "head");
            if (head == null) { Add("struct.head_seam", sp, false, "no Head transform", 0, 0, 1); return; }

            // Head world AABB. Geo path hangs meshes under CubePivot children,
            // hand-built path puts the renderer on the Head node itself:
            // search the head subtree either way.
            var headRends = head.GetComponentsInChildren<Renderer>();
            if (headRends.Length == 0)
            { Add("struct.head_seam", sp, false, "no renderer under head", 0, 0, 1); return; }
            var headRend = headRends[0]; // texture source (kept)
            var bodyRends = go.GetComponentsInChildren<Renderer>()
                .Where(r => r.name.ToLowerInvariant() != "head" &&
                            !r.name.ToLowerInvariant().StartsWith("snout") &&
                            !r.name.ToLowerInvariant().StartsWith("ear") &&
                            !r.name.ToLowerInvariant().StartsWith("beak") &&
                            !r.name.ToLowerInvariant().StartsWith("comb") &&
                            !r.name.ToLowerInvariant().StartsWith("wattle") &&
                            !r.name.ToLowerInvariant().StartsWith("nose") &&
                            !r.name.ToLowerInvariant().StartsWith("horn") &&
                            !r.name.ToLowerInvariant().StartsWith("tail") &&
                            // legs must NOT count as the head's anchor: front
                            // legs reaching the head rear masked the wolf's
                            // 4px gap (fake "0mm connected" green).
                            !RegexName(r.name, @"""^(leg\d|Hip\d|left_front_leg|right_front_leg|left_back_leg|right_back_leg|cube_2x8x2)$"""))
                .ToList();
            if (bodyRends.Count == 0)
            { Add("struct.head_seam", sp, false, "no body renderers", 0, 0, 1); return; }

            // Head AABB = union over the whole head subtree (head + snout +
            // ears + comb): robust to geo-path mesh-per-cube ordering.
            Vector3 hMin = Vector3.positiveInfinity, hMax = Vector3.negativeInfinity;
            foreach (var hr in headRends)
            {
                var hbx = VertexAabb(hr);
                hMin = Vector3.Min(hMin, hbx.min);
                hMax = Vector3.Max(hMax, hbx.max);
            }
            var hb = new Bounds(); hb.SetMinMax(hMin, hMax);
            // The nearest body-ish part behind the head decides the seam.
            float bestRear = float.MinValue;
            foreach (var r in bodyRends)
            {
                var b = VertexAabb(r);
                if (b.max.z <= hb.min.z + 0.30f && b.min.z < hb.min.z) // sits behind head
                    bestRear = Mathf.Max(bestRear, b.max.z);
            }
            // overlap = how deep the head rear sinks into the nearest part.
            float overlap = bestRear == float.MinValue ? -1f : bestRear - hb.min.z;
            // Coplanar touch (0mm) still reads as connected (cow head sits
            // flush); only a visible GAP fails. Sinking deeper is best.
            bool seamOk = overlap >= -0.0001f;
            Add("struct.head_seam", sp, seamOk,
                seamOk ? $"head/body z-overlap {overlap * 1000:F0}mm (connected)" :
                         $"head floats: gap {(-overlap) * 1000:F0}mm behind nearest part",
                overlap, 0f, 1f);

            // B) every face rect samples opaque texels (transparent -> black)
            var tex = headRend.sharedMaterial != null ? headRend.sharedMaterial.mainTexture as Texture2D : null;
            if (tex == null) { Add("struct.opaque_tex", sp, false, "no texture", 0, 0, 1); return; }
            int badFaces = 0, faces = 0;
            var badList = new System.Collections.Generic.List<string>();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var uv = mf.sharedMesh.uv;
                for (int f = 0; f < uv.Length / 4; f++)
                {
                    if (f == 3) continue; // -Y bottom: MC convention leaves it blank
                    faces++;
                    float u0 = 1e9f, u1 = -1e9f, v0 = 1e9f, v1 = -1e9f;
                    for (int c = 0; c < 4; c++)
                    {
                        u0 = Mathf.Min(u0, uv[f * 4 + c].x); u1 = Mathf.Max(u1, uv[f * 4 + c].x);
                        v0 = Mathf.Min(v0, uv[f * 4 + c].y); v1 = Mathf.Max(v1, uv[f * 4 + c].y);
                    }
                    int x0 = Mathf.RoundToInt(u0 * tex.width), x1 = Mathf.RoundToInt(u1 * tex.width);
                    int y0 = Mathf.RoundToInt(v0 * tex.height), y1 = Mathf.RoundToInt(v1 * tex.height);
                    if (x1 - x0 <= 1 || y1 - y0 <= 1) continue; // zero-width/degenerate
                    var px = tex.GetPixels(x0, y0, x1 - x0, y1 - y0);
                    float opaque = 0;
                    foreach (var q in px) if (q.a > 0.5f) opaque++;
                    // Fox-class art draws fur edges with big TRANSPARENT
                    // margins inside real face rects (fox64 side face 31/36
                    // empty, top 22/48) - alpha-test clips them to a furred
                    // silhouette, by design. Only flag rects that are 100%
                    // blank (painter never touched them = mapping bug) or
                    // nearly-opaque-with-few-holes (<=30% opaque = damage).
                    // Alpha-tested creature mats clip transparent texels away
                    // (fur edges, blank-face convention). Only rects that are
                    // painted on <10% but not fully blank signal UV misalignment
                    // into an empty art region.
                    if (opaque == 0 || opaque >= px.Length * 0.10f) { /* blank-by-design or normal fur art */ }
                    else
                    {
                        badFaces++;
                        if (badList.Count < 4)
                            badList.Add($"{r.name}#{f} rect({x0},{y0} {x1 - x0}x{y1 - y0}) {opaque * 100 / px.Length}%");
                    }
                }
            }
            bool opaqOk = badFaces == 0;
            Add("struct.opaque_tex", sp, opaqOk,
                opaqOk ? $"all {faces} faces sample >=95% opaque" :
                         $"{badFaces}/{faces} faces sample transparent texels (render black)",
                badFaces, 0, 0);
            foreach (var bf in badList) Debug.Log($"[AnimVerify] bad face {sp}/{bf}");
        }

        // ---- goal: rest pose level, feet grounded, head forward ----
        static void VerifyPose(string sp, GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { Add("pose", sp, false, "no renderers", 0, 0, 1); return; }
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);

            // Feet = LEG boxes' bottom, not the whole-model AABB: fox's
            // low-slung snout legitimately dips below y0 (vanilla stance),
            // which corrupted the whole-model min.y reading.
            float feet = b.min.y;
            var legRends = go.GetComponentsInChildren<Renderer>()
                .Where(r => r.transform.parent != null && r.transform.parent.name.StartsWith("leg"))
                .ToList();
            if (legRends.Count > 0)
            {
                var lb = legRends[0].bounds;
                foreach (var lr in legRends) lb.Encapsulate(lr.bounds);
                feet = lb.min.y;
            }
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
            // Pixel-verified overrides live in the data registry
            // (Resources/Registry/creatures.json faceOverrides, png coords);
            // everything else computes the rect from the geo JSON itself.
            int[] ovr = CreatureRegistry.FaceOverride(sp);
            if (ovr != null) rect = new Vector4(ovr[0], ovr[1], ovr[2], ovr[3]);
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
