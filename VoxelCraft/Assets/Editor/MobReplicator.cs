using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Mob Replicator v2: rebuilds a textured blocky mob from a REFERENCE
    /// SCREENSHOT using the game's OWN box model (BlockyAnimal.BuildModel), so
    /// the UV nets, box dims and 64x32 skin format are exactly what ships.
    ///
    /// Pipeline: load reference -> robust background mask (flood fill + alpha
    /// cutoff + largest connected component; wiki crops touch the image edges)
    /// -> harvest world quads + UV corners from the real meshes (no duplicated
    /// net/rot/row-flip conventions: every painted texel lands on the sheet
    /// texel the mesh UV actually names) -> fit ONLY the projection (yaw /
    /// pitch / scale / center via silhouette IoU; geometry stays vanilla,
    /// identical to the game) -> inverse-sample each visible face texel through
    /// the calibrated orthographic projector, global min-depth staging so
    /// shared nets (4 legs + 2 feet on one rect) resolve to the closest box ->
    /// render the real model with the painted skin through a camera that
    /// reproduces the software projector exactly (ortho size = h/(2*scale)) ->
    /// 3-panel comparison PNG (reference | replica | 50% blend) + render-based
    /// IoU -> apply the sheet as the live game skin (<species>_skin.png.bytes).
    /// Batch entry: MobReplicator.RunExperiment.
    /// </summary>
    public class MobReplicator : EditorWindow
    {
        const string RefDir = @"D:\zlj world\_refs";
        const string OutDir = @"D:\zlj world\_logs";
        const string SkinDir = @"D:\zlj world\VoxelCraft\Assets\Resources\Textures";
        const float PxPerUnit = 16f; // 1 world m = 16 skin px (game convention)

        static float yaw = 135f, pitch = 30f; // initial guess; fitted per mob
        static bool applyToGame = true;

        // ---------------- harvested game model ----------------
        class BoxFace
        {
            public string box;
            public Vector3 v0, v1, v2, v3;   // world quad, (a,b): v0=(0,0) v1=(1,0) v2=(1,1) v3=(0,1)
            public Vector2 uv0, uv1, uv2, uv3; // sheet UV at those corners
            public Vector3 n;                // outward world normal
            public int ga, gb;               // texel grid = face extents in skin px
        }

        class ModelGeom
        {
            public List<BoxFace> faces = new List<BoxFace>();
            public List<Vector3[]> boxCorners = new List<Vector3[]>(); // 8 unique world corners per box
            public List<string> boxNames = new List<string>();         // parallel to boxCorners
            public Vector3 center;
        }

        /// <summary>
        /// Builds the real game model (BlockyAnimal) and harvests per-face world
        /// quads + UVs straight from the produced meshes. Batch-safe (SelfTest
        /// builds the same models); the temporary hierarchy is destroyed after.
        /// </summary>
        static ModelGeom BuildGameModel(string species, float headYawDeg = 0f)
        {
            var go = new GameObject("ReplicaSrc_" + species);
            try
            {
                var animal = go.AddComponent<BlockyAnimal>();
                animal.species = species;
                animal.BuildModel();
                // BuildModel gives the root a random yaw; harvest needs model space.
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;

                if (Mathf.Abs(headYawDeg) > 0.01f)
                {
                    // Wiki-style references often have the head turned toward
                    // the camera relative to the body - no single rigid pose
                    // fits both. Turn the head about its neck pivot (back
                    // plane, centre height) before harvesting.
                    var head = go.transform.Find("Body/Head");
                    if (head != null)
                    {
                        var sz = head.localScale; // SkinnedBox: localScale = box size
                        var pivot = head.position + new Vector3(0f, 0f, -sz.z * 0.5f);
                        head.RotateAround(pivot, Vector3.up, headYawDeg);
                    }
                }

                var geom = new ModelGeom();
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null || mesh.vertexCount != 24) continue;
                    var t = mf.transform;
                    var verts = mesh.vertices;
                    var uvs = mesh.uv;
                    var corners = new List<Vector3>(8);
                    for (int f = 0; f < 6; f++)
                    {
                        int b = f * 4;
                        var F = new BoxFace { box = mf.gameObject.name };
                        F.v0 = t.TransformPoint(verts[b]);
                        F.v1 = t.TransformPoint(verts[b + 1]);
                        F.v2 = t.TransformPoint(verts[b + 2]);
                        F.v3 = t.TransformPoint(verts[b + 3]);
                        F.uv0 = uvs[b]; F.uv1 = uvs[b + 1]; F.uv2 = uvs[b + 2]; F.uv3 = uvs[b + 3];
                        // cornerUv order is CCW when seen from outside -> outward normal
                        F.n = Vector3.Cross(F.v1 - F.v0, F.v3 - F.v0).normalized;
                        F.ga = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(F.v0, F.v1) * PxPerUnit));
                        F.gb = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(F.v0, F.v3) * PxPerUnit));
                        geom.faces.Add(F);
                    }
                    foreach (var v in verts)
                    {
                        var w = t.TransformPoint(v);
                        if (!corners.Contains(w)) corners.Add(w);
                    }
                    geom.boxCorners.Add(corners.ToArray());
                    geom.boxNames.Add(mf.gameObject.name);
                }
                if (geom.faces.Count == 0)
                {
                    Debug.LogError($"REPLICA harvest failed for {species}: no skinned meshes (skin file missing?)");
                    return geom;
                }
                Vector3 lo = geom.faces[0].v0, hi = lo;
                foreach (var cs in geom.boxCorners)
                    foreach (var c in cs) { lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c); }
                geom.center = (lo + hi) * 0.5f;
                Debug.Log($"REPLICA harvest '{species}': {geom.boxCorners.Count} boxes, {geom.faces.Count} faces, center={geom.center}");
                return geom;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static Vector3 QuadPoint(BoxFace F, float a, float b)
        {
            var p0 = Vector3.Lerp(F.v0, F.v1, a);
            var p1 = Vector3.Lerp(F.v3, F.v2, a);
            return Vector3.Lerp(p0, p1, b);
        }

        static Vector2 QuadUv(BoxFace F, float a, float b)
        {
            var p0 = Vector2.Lerp(F.uv0, F.uv1, a);
            var p1 = Vector2.Lerp(F.uv3, F.uv2, a);
            return Vector2.Lerp(p0, p1, b);
        }

        // ---------------- projection ----------------
        static Quaternion camRot; static Vector3 camPos; static Vector2 projCenter; static float projScale = 1f;
        /// <summary>0 = orthographic; &gt;0 = perspective camera distance in m
        /// (wiki-style renders carry mild perspective an ortho fit can't match).</summary>
        static float projDist = 0f;

        static void SetCamera(Vector3 target, float rw, float rh)
        {
            camRot = Quaternion.Euler(pitch, yaw, 0f);
            camPos = target - camRot * Vector3.forward * (projDist > 0f ? projDist : 500f);
            projCenter = new Vector2(rw * 0.5f, rh * 0.5f);
            projScale = 1f;
        }

        /// <summary>
        /// Aligns the projected model bbox to the reference subject bbox
        /// (uniform scale = min of the axis ratios, centers matched).
        /// </summary>
        static void CalibrateCamera(ModelGeom geom, Ref r)
        {
            int minX = r.w, minY = r.h, maxX = -1, maxY = -1;
            for (int y = 0; y < r.h; y++)
                for (int x = 0; x < r.w; x++)
                    if (!r.bgTop[y * r.w + x])
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) { SetCamera(geom.center, r.w, r.h); return; }

            SetCamera(geom.center, r.w, r.h);
            Vector2 pmin = new Vector2(9999f, 9999f), pmax = new Vector2(-9999f, -9999f);
            foreach (var cs in geom.boxCorners)
                foreach (var c in cs)
                {
                    var sp = Project(c);
                    pmin = Vector2.Min(pmin, sp); pmax = Vector2.Max(pmax, sp);
                }
            float sw = (maxX - minX + 1) / Mathf.Max(pmax.x - pmin.x, 1e-3f);
            float sh = (maxY - minY + 1) / Mathf.Max(pmax.y - pmin.y, 1e-3f);
            projScale = Mathf.Min(sw, sh);
            Vector2 pc = Project(geom.center);
            projCenter += new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f) - pc;
        }

        static Vector2 Project(Vector3 p)
        {
            var c = Quaternion.Inverse(camRot) * (p - camPos);
            if (projDist > 0f)
            {
                float f = projScale * projDist; // focal length in px
                return new Vector2(projCenter.x + c.x / c.z * f, projCenter.y - c.y / c.z * f);
            }
            return new Vector2(projCenter.x + c.x * projScale, projCenter.y - c.y * projScale);
        }

        static float Depth(Vector3 p)
        {
            var c = Quaternion.Inverse(camRot) * (p - camPos);
            return c.z;
        }

        // ---------------- reference loading ----------------
        class Ref { public string name; public Color[] px, pxTop; public bool[] bg, bgTop; public int w, h; public Color bgColor; }

        static Ref LoadReference(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(path));
            var r = new Ref { name = Path.GetFileNameWithoutExtension(path), px = tex.GetPixels(), w = tex.width, h = tex.height };
            r.bg = new bool[r.w * r.h];
            Color c0 = r.px[0], c1 = r.px[r.w - 1], c2 = r.px[(r.h - 1) * r.w], c3 = r.px[r.h * r.w - 1];
            r.bgColor = (c0 + c1 + c2 + c3) * 0.25f;
            var stack = new Stack<int>();
            for (int x = 0; x < r.w; x++) { stack.Push(x); stack.Push((r.h - 1) * r.w + x); }
            for (int y = 0; y < r.h; y++) { stack.Push(y * r.w); stack.Push(y * r.w + r.w - 1); }
            float tol = 0.08f;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (i < 0 || i >= r.px.Length || r.bg[i]) continue;
                var c = r.px[i];
                // alpha 0.5 cutoff: wiki crops carry a 1-2 px AA halo of
                // half-transparent pixels around the subject; those are bg.
                bool isBg = c.a < 0.5f ||
                            (Mathf.Abs(c.r - r.bgColor.r) <= tol &&
                             Mathf.Abs(c.g - r.bgColor.g) <= tol &&
                             Mathf.Abs(c.b - r.bgColor.b) <= tol);
                if (!isBg) continue;
                r.bg[i] = true;
                int x = i % r.w, y = i / r.w;
                if (x > 0) stack.Push(i - 1);
                if (x < r.w - 1) stack.Push(i + 1);
                if (y > 0) stack.Push(i - r.w);
                if (y < r.h - 1) stack.Push(i + r.w);
            }
            Object.DestroyImmediate(tex);

            // Keep only the largest connected subject component: stray noise
            // specks (JPEG ringing, leftover halo islands) must not inflate
            // the bbox the projector is calibrated against.
            KeepLargestComponent(r);

            r.pxTop = new Color[r.w * r.h];
            r.bgTop = new bool[r.w * r.h];
            for (int y = 0; y < r.h; y++)
            {
                System.Array.Copy(r.px, (r.h - 1 - y) * r.w, r.pxTop, y * r.w, r.w);
                System.Array.Copy(r.bg, (r.h - 1 - y) * r.w, r.bgTop, y * r.w, r.w);
            }
            return r;
        }

        static void KeepLargestComponent(Ref r)
        {
            var comp = new int[r.bg.Length]; // 0 = bg/unassigned
            int next = 0, bestComp = 0, bestSize = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < r.bg.Length; i++)
            {
                if (r.bg[i] || comp[i] != 0) continue;
                next++; int size = 0;
                stack.Push(i); comp[i] = next;
                while (stack.Count > 0)
                {
                    int j = stack.Pop(); size++;
                    int x = j % r.w, y = j / r.w;
                    if (x > 0 && !r.bg[j - 1] && comp[j - 1] == 0) { comp[j - 1] = next; stack.Push(j - 1); }
                    if (x < r.w - 1 && !r.bg[j + 1] && comp[j + 1] == 0) { comp[j + 1] = next; stack.Push(j + 1); }
                    if (y > 0 && !r.bg[j - r.w] && comp[j - r.w] == 0) { comp[j - r.w] = next; stack.Push(j - r.w); }
                    if (y < r.h - 1 && !r.bg[j + r.w] && comp[j + r.w] == 0) { comp[j + r.w] = next; stack.Push(j + r.w); }
                }
                if (size > bestSize) { bestSize = size; bestComp = next; }
            }
            for (int i = 0; i < r.bg.Length; i++)
                if (!r.bg[i] && comp[i] != bestComp) r.bg[i] = true;
            Debug.Log($"REPLICA subject '{r.name}': largest component {bestSize}px of {r.bg.Length} ({bestSize * 100 / r.bg.Length}%), {next - 1} stray components removed");
        }

        static Color SampleBilinear(Ref r, float fx, float fy, out bool onBg)
        {
            fx = Mathf.Clamp(fx, 0f, r.w - 1f); fy = Mathf.Clamp(fy, 0f, r.h - 1f);
            int x0 = (int)fx, y0 = (int)fy, x1 = Mathf.Min(x0 + 1, r.w - 1), y1 = Mathf.Min(y0 + 1, r.h - 1);
            float dx = fx - x0, dy = fy - y0;
            int i00 = y0 * r.w + x0, i10 = y0 * r.w + x1, i01 = y1 * r.w + x0, i11 = y1 * r.w + x1;
            float bg = 0f;
            bg += r.bgTop[i00] ? 1f : 0f; bg += r.bgTop[i10] ? 1f : 0f; bg += r.bgTop[i01] ? 1f : 0f; bg += r.bgTop[i11] ? 1f : 0f;
            onBg = bg >= 1f; // any bg tap -> reject, avoids dark/AA edge bleed
            Color c = Color.Lerp(Color.Lerp(r.pxTop[i00], r.pxTop[i10], dx), Color.Lerp(r.pxTop[i01], r.pxTop[i11], dx), dy);
            return c;
        }

        // ---------------- silhouette rasterization ----------------
        /// <summary>Fills the projected convex hull of every box into a mask.</summary>
        static bool[] Rasterize(ModelGeom geom, int w, int h)
        {
            var m = new bool[w * h];
            foreach (var cs in geom.boxCorners)
            {
                var pts = new Vector2[cs.Length];
                for (int i = 0; i < cs.Length; i++) pts[i] = Project(cs[i]);
                FillConvexHull(pts, m, w, h);
            }
            return m;
        }

        static void FillConvexHull(Vector2[] pts, bool[] m, int w, int h)
        {
            // Andrew monotone chain.
            var sorted = (Vector2[])pts.Clone();
            System.Array.Sort(sorted, (p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
            var hull = new List<Vector2>();
            foreach (var p in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count + 1;
            for (int i = sorted.Length - 2; i >= 0; i--)
            {
                var p = sorted[i];
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            if (hull.Count < 3) return;
            hull.RemoveAt(hull.Count - 1);

            float minY = hull[0].y, maxY = hull[0].y;
            foreach (var p in hull) { minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
            int y0 = Mathf.Max(0, (int)Mathf.Floor(minY)), y1 = Mathf.Min(h - 1, (int)Mathf.Ceil(maxY));
            for (int y = y0; y <= y1; y++)
            {
                float ys = y + 0.5f;
                float minX = float.MaxValue, maxX = float.MinValue;
                for (int e = 0; e < hull.Count; e++)
                {
                    var p = hull[e]; var q = hull[(e + 1) % hull.Count];
                    if ((p.y <= ys && q.y > ys) || (q.y <= ys && p.y > ys))
                    {
                        float x = p.x + (ys - p.y) / (q.y - p.y) * (q.x - p.x);
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
                if (maxX < minX) continue;
                int ix0 = Mathf.Max(0, (int)Mathf.Ceil(minX - 0.5f));
                int ix1 = Mathf.Min(w - 1, (int)Mathf.Floor(maxX - 0.5f));
                for (int x = ix0; x <= ix1; x++) m[y * w + x] = true;
            }
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        static float Iou(bool[] a, bool[] b)
        {
            int inter = 0, uni = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] && b[i]) inter++;
                if (a[i] || b[i]) uni++;
            }
            return uni == 0 ? 0f : (float)inter / uni;
        }

        // ---------------- projection fitting ----------------
        /// <summary>
        /// Generous multiplicative-scale + px-center descent on silhouette IoU.
        /// bbox alignment is only the initializer (cropped refs clip the
        /// subject at the image edges, biasing the bbox-derived scale).
        /// Returns the improved score; projector statics hold the winner.
        /// </summary>
        static float OptimizeScaleCenter(ModelGeom geom, Ref r, bool[] subject, float cur)
        {
            float bestS = cur, bestScale = projScale;
            Vector2 bestCenter = projCenter;
            for (int sweep = 0; sweep < 8; sweep++)
            {
                bool improved = false;
                foreach (float f in new[] { 1.2f, 1.1f, 1.05f, 1.02f, 0.98f, 0.95f, 0.9f, 0.8f })
                {
                    projScale = bestScale * f; projCenter = bestCenter;
                    float s = Iou(Rasterize(geom, r.w, r.h), subject);
                    if (s > bestS + 1e-5f) { bestS = s; bestScale = projScale; bestCenter = projCenter; improved = true; break; }
                }
                if (!improved)
                {
                    float px = Mathf.Max(1f, 48f / (1 << sweep));
                    foreach (float d in new[] { px, -px })
                    {
                        for (int axis = 0; axis < 2 && !improved; axis++)
                        {
                            projScale = bestScale;
                            projCenter = bestCenter + (axis == 0 ? new Vector2(d, 0f) : new Vector2(0f, d));
                            float s = Iou(Rasterize(geom, r.w, r.h), subject);
                            if (s > bestS + 1e-5f) { bestS = s; bestScale = projScale; bestCenter = projCenter; improved = true; }
                        }
                        if (improved) break;
                    }
                }
                if (!improved) break;
            }
            projScale = bestScale; projCenter = bestCenter;
            return bestS;
        }

        /// <summary>
        /// The geometry is FIXED (vanilla game model), so only the projection is
        /// fitted: coarse quadrant+yaw+pitch sweep, coordinate descent, then a
        /// scale/center polish. Score = silhouette IoU against the reference.
        /// Returns the best IoU; when <paramref name="dump"/> is set the fit
        /// overlay PNG is written and the final calibration stays in the
        /// projector statics.
        /// </summary>
        static float FitProjection(ModelGeom geom, Ref r, string name, bool dump)
        {
            var subject = new bool[r.w * r.h];
            for (int i = 0; i < subject.Length; i++) subject[i] = !r.bgTop[i];

            float Score()
            {
                CalibrateCamera(geom, r);
                return Iou(Rasterize(geom, r.w, r.h), subject);
            }

            // Pre-select the projection kind: ortho vs perspective camera
            // distance (wiki-style refs render with mild perspective).
            float bestD = 0f, bestDScore = -1f;
            foreach (float d in new[] { 0f, 2.5f, 4f, 6f })
            {
                projDist = d;
                CalibrateCamera(geom, r);
                float s = OptimizeScaleCenter(geom, r, subject, Iou(Rasterize(geom, r.w, r.h), subject));
                if (s > bestDScore) { bestDScore = s; bestD = d; }
            }
            projDist = bestD;
            Debug.Log($"REPLICA projection dist '{name}' [{r.name}]: dist={bestD:F1} IoU={bestDScore:F3}");

            // Coarse quadrant+yaw+pitch ranking uses the same (bbox) basis for
            // every candidate; scale/center is re-optimized at the end.
            float bestS = -1f, bestYaw = yaw, bestPitch = pitch;
            foreach (float quad in new[] { 45f, 135f, 225f, 315f })
                foreach (float dy in new[] { -9f, 0f, 9f })
                    foreach (float dp in new[] { -4f, 0f, 4f })
                    {
                        yaw = quad + dy; pitch = Mathf.Clamp(30f + dp, 5f, 80f);
                        float s = Score();
                        if (s > bestS) { bestS = s; bestYaw = yaw; bestPitch = pitch; }
                    }
            yaw = bestYaw; pitch = bestPitch;

            for (float step = 2f; step >= 0.5f; step *= 0.5f)
            {
                bool improved = true;
                while (improved)
                {
                    improved = false;
                    foreach (var d in new[] { step, -step })
                    {
                        yaw += d;
                        float s = Score();
                        if (s > bestS + 1e-5f) { bestS = s; improved = true; }
                        else
                        {
                            yaw -= d;
                            pitch = Mathf.Clamp(pitch + d, 5f, 80f);
                            s = Score();
                            if (s > bestS + 1e-5f) { bestS = s; improved = true; }
                            else pitch -= d;
                        }
                    }
                }
            }

            // ---- generous scale/center descent, then alternating yaw/pitch
            // refinement on top of the optimized scale/center (no bbox
            // recalibration from here on).
            CalibrateCamera(geom, r);
            bestS = OptimizeScaleCenter(geom, r, subject, bestS);
            for (int sweep = 0; sweep < 4; sweep++)
            {
                bool improved = false;
                foreach (float d in new[] { 1f, -1f, 0.5f, -0.5f })
                {
                    yaw += d;
                    float s = Iou(Rasterize(geom, r.w, r.h), subject);
                    if (s > bestS + 1e-5f) { bestS = s; improved = true; continue; }
                    yaw -= d;
                    pitch = Mathf.Clamp(pitch + d, 5f, 80f);
                    s = Iou(Rasterize(geom, r.w, r.h), subject);
                    if (s > bestS + 1e-5f) { bestS = s; improved = true; }
                    else pitch -= d;
                }
                if (!improved) break;
            }
            bestS = OptimizeScaleCenter(geom, r, subject, bestS);

            // Face-aware polish: silhouette IoU is flat in yaw/pitch around
            // the true pose (the body-dominated plateau) while head features
            // misplace by texels - re-score with face terms and descend.
            RefineFaceAlignment(geom, r, subject);
            bestS = Iou(Rasterize(geom, r.w, r.h), subject);
            var fq = FaceQuality(geom, r);
            Debug.Log($"REPLICA face refine '{name}' [{r.name}]: yaw={yaw:F1} pitch={pitch:F1} scale={projScale:F2} center=({projCenter.x:F0},{projCenter.y:F0}) IoU={bestS:F3} faceFrac={fq.sampleFrac:F2} faceAsym={fq.asym:F2}");

            Debug.Log($"REPLICA projection fit '{name}' [{r.name}]: dist={projDist:F1} yaw={yaw:F1} pitch={pitch:F1} scale={projScale:F2} center=({projCenter.x:F0},{projCenter.y:F0}) IoU={bestS:F3}");
            if (dump) DumpFit(geom, r, name, subject);
            return bestS;
        }

        /// <summary>Vertically flipped copy (wiki refs are sometimes exported row-flipped).</summary>
        static Ref FlipVertical(Ref src)
        {
            var r = new Ref
            {
                name = src.name + "-flipped",
                px = src.px, bg = src.bg,
                w = src.w, h = src.h, bgColor = src.bgColor,
                pxTop = new Color[src.w * src.h],
                bgTop = new bool[src.w * src.h],
            };
            for (int y = 0; y < src.h; y++)
            {
                System.Array.Copy(src.pxTop, (src.h - 1 - y) * src.w, r.pxTop, y * src.w, src.w);
                System.Array.Copy(src.bgTop, (src.h - 1 - y) * src.w, r.bgTop, y * src.w, src.w);
            }
            return r;
        }

        static void DumpFit(ModelGeom geom, Ref r, string name, bool[] subject)
        {
            if (subject != null)
            {
                var model = Rasterize(geom, r.w, r.h);
                var tex = new Texture2D(r.w, r.h, TextureFormat.RGBA32, false);
                var px = new Color[r.w * r.h];
                for (int i = 0; i < px.Length; i++)
                    px[i] = new Color(subject[i] ? 1f : 0f, model[i] ? 1f : 0f, 0f, 1f);
                tex.SetPixels(px); tex.Apply();
                File.WriteAllBytes(Path.Combine(OutDir, $"fit_{name}_overlay.png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }

            // Per-box projected corner dots over the reference: shows exactly
            // where the fitted camera puts each box (head offset = the face
            // scramble). Head=green, snout/beak/wattle=yellow, body=cyan,
            // legs/feet/others=orange.
            var dot = new Texture2D(r.w, r.h, TextureFormat.RGBA32, false);
            var dp = new Color[r.w * r.h];
            for (int i = 0; i < dp.Length; i++) dp[i] = r.pxTop[i];
            for (int bi = 0; bi < geom.boxCorners.Count; bi++)
            {
                string bname = bi < geom.boxNames.Count ? geom.boxNames[bi] : "";
                var col = bname.Contains("Head") ? new Color(0f, 1f, 0f, 1f)
                        : bname.Contains("Snout") || bname.Contains("Beak") || bname.Contains("Wattle")
                            ? new Color(1f, 1f, 0f, 1f)
                        : bname.Contains("Body") ? new Color(0f, 1f, 1f, 1f)
                        : new Color(1f, 0.5f, 0f, 1f);
                foreach (var c in geom.boxCorners[bi])
                {
                    var sp = Project(c);
                    int cx = Mathf.Clamp((int)sp.x, 0, r.w - 1), cy = Mathf.Clamp((int)sp.y, 0, r.h - 1);
                    for (int oy = -2; oy <= 2; oy++)
                        for (int ox = -2; ox <= 2; ox++)
                        {
                            int xx = Mathf.Clamp(cx + ox, 0, r.w - 1), yy = Mathf.Clamp(cy + oy, 0, r.h - 1);
                            dp[yy * r.w + xx] = col;
                        }
                }
            }
            dot.SetPixels(dp); dot.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, $"fit_{name}_boxes.png"), dot.EncodeToPNG());
            Object.DestroyImmediate(dot);
        }

        // ---------------- skin painting ----------------
        class FacePaint
        {
            public BoxFace F; public Color[] c; public float[] d; public bool[] has; public int count;
            public FacePaint(BoxFace f)
            {
                F = f;
                int n = f.ga * f.gb;
                c = new Color[n]; d = new float[n]; has = new bool[n];
                for (int i = 0; i < n; i++) d[i] = float.PositiveInfinity;
            }
        }

        static readonly string[] FaceBoxes = { "Head", "Snout", "Beak", "Wattle" };
        static bool IsFaceBox(string name)
        {
            foreach (var fb in FaceBoxes) if (name.Contains(fb)) return true;
            return false;
        }

        /// <summary>
        /// Min view depth per reference pixel, remembering which box owns it.
        /// A convex box's camera-facing faces can never hide one another, so
        /// the occlusion test must exclude same-box hits - including them
        /// falsely rejected whole edge strips of a face (grey holes).
        /// </summary>
        static void BuildDepthBuf(ModelGeom geom, Ref r, out float[] depthBuf, out int[] boxIdBuf,
                                  Dictionary<string, int> boxIdx)
        {
            int n = r.w * r.h;
            depthBuf = new float[n];
            boxIdBuf = new int[n];
            for (int i = 0; i < n; i++) { depthBuf[i] = float.PositiveInfinity; boxIdBuf[i] = -1; }
            var camFwd = camRot * Vector3.forward;
            foreach (var F in geom.faces)
            {
                if (Vector3.Dot(F.n, camFwd) > -0.02f) continue;
                int bid = boxIdx[F.box];
                int sa = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(F.v0, F.v1) * projScale), 2, 2400);
                int sb = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(F.v0, F.v3) * projScale), 2, 2400);
                for (int j = 0; j < sb; j++)
                    for (int i = 0; i < sa; i++)
                    {
                        var P = QuadPoint(F, (i + 0.5f) / sa, (j + 0.5f) / sb);
                        var sp = Project(P);
                        int ix = Mathf.Clamp((int)sp.x, 0, r.w - 1);
                        int iy = Mathf.Clamp((int)sp.y, 0, r.h - 1);
                        int idx = iy * r.w + ix;
                        float d = Depth(P);
                        if (d < depthBuf[idx]) { depthBuf[idx] = d; boxIdBuf[idx] = bid; }
                    }
            }
        }

        /// <summary>
        /// Lateral mirror completion: a single reference view shows only one
        /// side of each box, so the hidden face of a lateral pair (+X/-X,
        /// +Z/-Z) inherits the visible face's texels mirrored. The mesh corner
        /// order gives the mapping a' = 1-a, b' = b. Animals are bilaterally
        /// symmetric, so the completed side reads correctly in-game.
        /// </summary>
        static void MirrorComplete(List<FacePaint> paints)
        {
            var byBox = new Dictionary<string, List<FacePaint>>();
            foreach (var p in paints)
            {
                if (!byBox.TryGetValue(p.F.box, out var l)) { l = new List<FacePaint>(); byBox[p.F.box] = l; }
                l.Add(p);
            }
            foreach (var kv in byBox)
            {
                for (int axis = 0; axis < 2; axis++)
                {
                    FacePaint a = null, b = null;
                    foreach (var p in kv.Value)
                    {
                        bool lateral = axis == 0 ? Mathf.Abs(p.F.n.x) > 0.9f : Mathf.Abs(p.F.n.z) > 0.9f;
                        if (!lateral) continue;
                        if (a == null) a = p; else if (b == null) b = p;
                    }
                    if (a == null || b == null) continue;
                    var rich = a.count >= b.count ? a : b;
                    var poor = rich == a ? b : a;
                    if (rich.count < 3 || rich.count <= poor.count) continue;
                    if (rich.F.ga != poor.F.ga || rich.F.gb != poor.F.gb) continue;
                    for (int j = 0; j < poor.F.gb; j++)
                        for (int i = 0; i < poor.F.ga; i++)
                        {
                            int pi = j * poor.F.ga + i;
                            if (poor.has[pi]) continue;
                            int ri = j * rich.F.ga + (rich.F.ga - 1 - i);
                            if (!rich.has[ri]) continue;
                            poor.c[pi] = rich.c[ri]; poor.d[pi] = rich.d[ri];
                            poor.has[pi] = true; poor.count++;
                        }
                }
            }
        }

        static Texture2D PaintSkin(ModelGeom geom, Ref r)
        {
            var sheet = new Texture2D(64, 32, TextureFormat.RGBA32, false);
            var clear = new Color[64 * 32];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color(0, 0, 0, 0);
            sheet.SetPixels(clear);

            var boxIdx = new Dictionary<string, int>();
            foreach (var F in geom.faces) if (!boxIdx.ContainsKey(F.box)) boxIdx[F.box] = boxIdx.Count;
            BuildDepthBuf(geom, r, out var depthBuf, out var boxIdBuf, boxIdx);

            var camFwd = camRot * Vector3.forward;
            const float OccEps = 0.02f; // view-space m; real occluders sit >= ~12mm nearer

            // Per-face texel buffers: occlusion (cross-box only) + bg reject.
            var paints = new List<FacePaint>();
            var accSum = new Dictionary<string, Color>();
            var accN = new Dictionary<string, int>();
            foreach (var F in geom.faces)
            {
                if (Vector3.Dot(F.n, camFwd) > -0.02f) continue; // faces away
                int bid = boxIdx[F.box];
                var fp = new FacePaint(F);
                var sum = new Color(0f, 0f, 0f, 0f); int n = 0;
                for (int j = 0; j < F.gb; j++)
                    for (int i = 0; i < F.ga; i++)
                    {
                        float a = (i + 0.5f) / F.ga, b = (j + 0.5f) / F.gb;
                        var P = QuadPoint(F, a, b);
                        var sp = Project(P);
                        float d = Depth(P);
                        int px = Mathf.Clamp((int)sp.x, 0, r.w - 1);
                        int py = Mathf.Clamp((int)sp.y, 0, r.h - 1);
                        int di = py * r.w + px;
                        if (boxIdBuf[di] != bid && depthBuf[di] < d - OccEps) continue; // hidden by ANOTHER box
                        bool onBg;
                        var c = SampleBilinear(r, sp.x, sp.y, out onBg);
                        if (onBg) continue;
                        int k = j * F.ga + i;
                        fp.c[k] = c; fp.d[k] = d; fp.has[k] = true; fp.count++;
                        sum += c; n++;
                    }
                if (n > 0)
                {
                    accSum[F.box] = accSum.TryGetValue(F.box, out var prev) ? prev + sum : sum;
                    accN[F.box] = (accN.TryGetValue(F.box, out var pn) ? pn : 0) + n;
                }
                paints.Add(fp);
            }

            MirrorComplete(paints);

            // Global min-depth staging: shared nets (4 legs, 2 feet, 2 wings
            // on one rect) are written by every box; the texel keeps the
            // sample from the box closest to the camera.
            var stageC = new Color[64 * 32];
            var stageD = new float[64 * 32];
            var stageHas = new bool[64 * 32];
            for (int i = 0; i < stageD.Length; i++) stageD[i] = float.PositiveInfinity;

            foreach (var fp in paints)
                for (int k = 0; k < fp.has.Length; k++)
                {
                    if (!fp.has[k]) continue;
                    int i = k % fp.F.ga, j = k / fp.F.ga;
                    var uv = QuadUv(fp.F, (i + 0.5f) / fp.F.ga, (j + 0.5f) / fp.F.gb);
                    int tx = Mathf.Clamp(Mathf.RoundToInt(uv.x * 64f - 0.5f), 0, 63);
                    int tyTop = Mathf.Clamp(Mathf.RoundToInt((1f - uv.y) * 32f - 0.5f), 0, 31);
                    int idx = tyTop * 64 + tx;
                    if (!stageHas[idx] || fp.d[k] < stageD[idx])
                    { stageC[idx] = fp.c[k]; stageD[idx] = fp.d[k]; stageHas[idx] = true; }
                }

            // Fallback: texels a box owns but nobody sampled (its own far side,
            // fully hidden faces) get that box's visible-face mean. Boxes with
            // NO accepted samples at all (fully occluded, e.g. the wattle)
            // are skipped here so the through-sampling pass below can fill
            // them with something better than a flat mean.
            foreach (var F in geom.faces)
            {
                if (accN.TryGetValue(F.box, out int bn) && bn == 0) continue;
                Color mean = Color.grey;
                bool hasMean = accN.TryGetValue(F.box, out int bn2) && bn2 > 0;
                if (hasMean) mean = accSum[F.box] / bn2;
                for (int j = 0; j < F.gb; j++)
                    for (int i = 0; i < F.ga; i++)
                    {
                        float a = (i + 0.5f) / F.ga, b = (j + 0.5f) / F.gb;
                        var uv = QuadUv(F, a, b);
                        int tx = Mathf.Clamp(Mathf.RoundToInt(uv.x * 64f - 0.5f), 0, 63);
                        int tyTop = Mathf.Clamp(Mathf.RoundToInt((1f - uv.y) * 32f - 0.5f), 0, 31);
                        int idx = tyTop * 64 + tx;
                        if (!stageHas[idx]) { stageC[idx] = mean; stageHas[idx] = true; }
                    }
            }

            // Last resort: still-unclaimed texels (fully occluded boxes) sample
            // straight through, ignoring occlusion - imperfect but far more
            // useful in-game than grey (the wattle keeps its red).
            foreach (var F in geom.faces)
            {
                if (Vector3.Dot(F.n, camFwd) > -0.02f) continue;
                for (int j = 0; j < F.gb; j++)
                    for (int i = 0; i < F.ga; i++)
                    {
                        float a = (i + 0.5f) / F.ga, b = (j + 0.5f) / F.gb;
                        var P = QuadPoint(F, a, b);
                        var sp = Project(P);
                        bool onBg;
                        var c = SampleBilinear(r, sp.x, sp.y, out onBg);
                        if (onBg) continue;
                        var uv = QuadUv(F, a, b);
                        int tx = Mathf.Clamp(Mathf.RoundToInt(uv.x * 64f - 0.5f), 0, 63);
                        int tyTop = Mathf.Clamp(Mathf.RoundToInt((1f - uv.y) * 32f - 0.5f), 0, 31);
                        int idx = tyTop * 64 + tx;
                        if (!stageHas[idx]) { stageC[idx] = c; stageHas[idx] = true; }
                    }
            }
            for (int i = 0; i < stageC.Length; i++)
                sheet.SetPixel(i % 64, 31 - i / 64, stageHas[i] ? stageC[i] : new Color(0.75f, 0.75f, 0.75f, 1f));

            var all = sheet.GetPixels();
            for (int i = 0; i < all.Length; i++) all[i].a = 1f; // transparent renders black on Unlit
            sheet.SetPixels(all);
            sheet.filterMode = FilterMode.Point;
            sheet.wrapMode = TextureWrapMode.Clamp;
            sheet.Apply();
            return sheet;
        }

        // ---------------- face-aware calibration polish ----------------
        class FaceQ { public float sampleFrac; public float asym; }

        /// <summary>
        /// Samples only the face-critical boxes (head/snout/beak/wattle) under
        /// the current calibration: (a) fraction of their texels landing on
        /// real reference pixels, (b) left/right mirror asymmetry of the
        /// head's front face. Both are optimal ONLY at the true pose, where
        /// silhouette IoU alone stays flat (body-dominated plateau) while
        /// face features still misplace by texels.
        /// </summary>
        static FaceQ FaceQuality(ModelGeom geom, Ref r)
        {
            var boxIdx = new Dictionary<string, int>();
            foreach (var F in geom.faces) if (!boxIdx.ContainsKey(F.box)) boxIdx[F.box] = boxIdx.Count;
            BuildDepthBuf(geom, r, out var depthBuf, out var boxIdBuf, boxIdx);
            var camFwd = camRot * Vector3.forward;
            const float OccEps = 0.02f;

            int total = 0, got = 0;
            FacePaint headFront = null;
            foreach (var F in geom.faces)
            {
                if (!IsFaceBox(F.box)) continue;
                if (Vector3.Dot(F.n, camFwd) > -0.02f) continue;
                int bid = boxIdx[F.box];
                var fp = new FacePaint(F);
                total += F.ga * F.gb;
                bool isFront = F.box.Contains("Head") && F.n.z > 0.9f;
                for (int j = 0; j < F.gb; j++)
                    for (int i = 0; i < F.ga; i++)
                    {
                        var P = QuadPoint(F, (i + 0.5f) / F.ga, (j + 0.5f) / F.gb);
                        var sp = Project(P);
                        int px = Mathf.Clamp((int)sp.x, 0, r.w - 1);
                        int py = Mathf.Clamp((int)sp.y, 0, r.h - 1);
                        int di = py * r.w + px;
                        if (boxIdBuf[di] != bid && depthBuf[di] < Depth(P) - OccEps) continue;
                        bool onBg;
                        var c = SampleBilinear(r, sp.x, sp.y, out onBg);
                        if (onBg) continue;
                        int k = j * F.ga + i;
                        fp.c[k] = c; fp.has[k] = true; fp.count++; got++;
                    }
                if (isFront && (headFront == null || fp.count > headFront.count)) headFront = fp;
            }

            var q = new FaceQ { sampleFrac = total > 0 ? (float)got / total : 0f, asym = 1f };
            if (headFront == null || headFront.count < 6) return q;

            int ga = headFront.F.ga, gb = headFront.F.gb;
            var mean = new Color(0f, 0f, 0f, 0f); int mn = 0;
            for (int k = 0; k < headFront.has.Length; k++)
                if (headFront.has[k]) { mean += headFront.c[k]; mn++; }
            mean /= mn;
            float LumDiff(Color x, Color y) => Mathf.Abs(x.r - y.r) + Mathf.Abs(x.g - y.g) + Mathf.Abs(x.b - y.b);
            float diffSum = 0f, contrastSum = 0f; int pairs = 0;
            for (int j = 0; j < gb; j++)
                for (int i = 0; i < ga; i++)
                {
                    int k = j * ga + i;
                    if (!headFront.has[k]) continue;
                    contrastSum += LumDiff(headFront.c[k], mean);
                    int km = j * ga + (ga - 1 - i);
                    if (headFront.has[km]) { diffSum += LumDiff(headFront.c[k], headFront.c[km]); pairs++; }
                }
            q.asym = pairs > 4 ? (diffSum / pairs) / (contrastSum / Mathf.Max(mn, 1) + 0.15f) : 1f;
            return q;
        }

        static float JointScore(ModelGeom geom, Ref r, bool[] subject)
        {
            float iou = Iou(Rasterize(geom, r.w, r.h), subject);
            var q = FaceQuality(geom, r);
            if (q.sampleFrac < 0.35f) return iou - 1f; // face entirely off the actual head
            return iou + 0.6f * q.sampleFrac - 0.8f * q.asym;
        }

        /// <summary>
        /// Coordinate descent over (yaw, pitch, scale, center) maximizing the
        /// joint silhouette + face quality score. Runs AFTER the IoU fit:
        /// it can only move the calibration within the flat silhouette
        /// plateau, onto the pose where the face actually reads.
        /// </summary>
        static void RefineFaceAlignment(ModelGeom geom, Ref r, bool[] subject)
        {
            float best = JointScore(geom, r, subject);
            float bYaw = yaw, bPitch = pitch, bScale = projScale;
            Vector2 bC = projCenter;
            for (int sweep = 0; sweep < 4; sweep++)
            {
                float yawStep = 2f * Mathf.Pow(0.5f, sweep);
                float pitchStep = 1.5f * Mathf.Pow(0.5f, sweep);
                float scaleStep = 0.015f * Mathf.Pow(0.5f, sweep);
                float cStep = 10f * Mathf.Pow(0.5f, sweep);
                bool improved = false;
                foreach (float d in new[] { 1f, -1f })
                {
                    yaw = bYaw + d * yawStep;
                    float s = JointScore(geom, r, subject);
                    if (s > best + 1e-5f) { best = s; bYaw = yaw; improved = true; }
                    yaw = bYaw;

                    pitch = Mathf.Clamp(bPitch + d * pitchStep, 5f, 80f);
                    s = JointScore(geom, r, subject);
                    if (s > best + 1e-5f) { best = s; bPitch = pitch; improved = true; }
                    pitch = bPitch;

                    projScale = bScale * (1f + d * scaleStep);
                    s = JointScore(geom, r, subject);
                    if (s > best + 1e-5f) { best = s; bScale = projScale; improved = true; }
                    projScale = bScale;

                    projCenter = bC + new Vector2(d * cStep, 0f);
                    s = JointScore(geom, r, subject);
                    if (s > best + 1e-5f) { best = s; bC = projCenter; improved = true; }
                    projCenter = bC;

                    projCenter = bC + new Vector2(0f, d * cStep);
                    s = JointScore(geom, r, subject);
                    if (s > best + 1e-5f) { best = s; bC = projCenter; improved = true; }
                    projCenter = bC;
                }
                yaw = bYaw; pitch = bPitch; projScale = bScale; projCenter = bC;
                if (!improved) break;
            }
            yaw = bYaw; pitch = bPitch; projScale = bScale; projCenter = bC;
        }

        // ---------------- feature anchoring (three-view-grade alignment) ----
        // Silhouette IoU is flat in yaw/pitch (body-dominated plateau) so the
        // face can end up texels off even at "best" fit. Anchors fix that:
        // feature positions are read from the game's CURRENT skin (where the
        // eyes/nostrils/wattle actually live, in texel coords) and matched to
        // same-type features detected in the reference near the projected
        // position; the projection is then re-solved so model features land
        // exactly on the reference features. Symmetry is audited afterwards
        // and a failing skin is NOT applied to the game.

        static float FeatureScore(Color c, string type)
        {
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            switch (type)
            {
                case "dark": return 0.9f - lum; // pupils: near-black
                case "nostril": return 0.75f - lum; // nostrils: darker pink
                case "pink": return (c.r > 0.55f && c.r - c.g > 0.12f && c.r - c.b > 0.12f)
                    ? (c.r - Mathf.Max(c.g, c.b)) * 2f + 0.3f : -1f; // pig snout disc: saturated pink
                case "red": return (c.r - Mathf.Max(c.g, c.b)) + (c.r > 0.35f ? 0.2f : -0.5f);
                case "orange": return (c.r > 0.5f && c.g > 0.28f && c.g < 0.78f && c.b < 0.5f && c.r - c.b > 0.25f)
                    ? (c.r - c.b) + 0.25f : -1f;
                default: return -1f;
            }
        }

        static float FeatureThreshold(string type) => type == "dark" ? 0.62f : type == "nostril" ? 0.32f : type == "red" ? 0.3f : type == "pink" ? 0.4f : 0.45f;

        /// <summary>Centroids (as face params a,b) of feature clusters within a
        /// face's UV rect on a sheet. Left/right clusters are split by column.</summary>
        static List<Vector2> SheetFeatureAbs(BoxFace F, Color[] px, string type)
        {
            float u0 = Mathf.Min(Mathf.Min(F.uv0.x, F.uv1.x), Mathf.Min(F.uv2.x, F.uv3.x));
            float u1 = Mathf.Max(Mathf.Max(F.uv0.x, F.uv1.x), Mathf.Max(F.uv2.x, F.uv3.x));
            float vLo = Mathf.Min(Mathf.Min(F.uv0.y, F.uv1.y), Mathf.Min(F.uv2.y, F.uv3.y));
            float vHi = Mathf.Max(Mathf.Max(F.uv0.y, F.uv1.y), Mathf.Max(F.uv2.y, F.uv3.y));
            int x0 = Mathf.CeilToInt(u0 * 64f + 0.5f), x1 = Mathf.FloorToInt(u1 * 64f - 0.5f);
            int ty0 = Mathf.CeilToInt((1f - vHi) * 32f + 0.5f), ty1 = Mathf.FloorToInt((1f - vLo) * 32f - 0.5f);
            float thr = FeatureThreshold(type);
            float sxL = 0, syL = 0, sxR = 0, syR = 0; int nL = 0, nR = 0;
            int midX = (x0 + x1) / 2;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = px[(31 - ty) * 64 + x];
                    if (FeatureScore(c, type) < thr) continue;
                    if (x < midX) { sxL += x; syL += ty; nL++; }
                    else { sxR += x; syR += ty; nR++; }
                }
            var res = new List<Vector2>();
            if (nL >= 1) res.Add(new Vector2((sxL / nL + 0.5f) / 64f, 1f - (syL / nL + 0.5f) / 32f));
            if (nR >= 1) res.Add(new Vector2((sxR / nR + 0.5f) / 64f, 1f - (syR / nR + 0.5f) / 32f));
            return res;
        }

        /// <summary>Collects the top local maxima of a feature score inside a
        /// biased window (see DetectRefFeature). Independent per-anchor argmax
        /// collapses onto the single strongest feature (both pig eyes matched
        /// the SAME pupil), so the caller assigns candidates globally.</summary>
        static List<Vector2> DetectRefFeatureCandidates(Ref r, Vector2 proj, string type, float radius, float bias, float yBias, int want)
        {
            var res = new List<Vector2>();
            float thr = FeatureThreshold(type);
            int x0 = Mathf.Max(0, Mathf.RoundToInt(proj.x + (bias < 0 ? -radius : -0.35f * radius)));
            int x1 = Mathf.Min(r.w - 1, Mathf.RoundToInt(proj.x + (bias > 0 ? radius : 0.35f * radius)));
            int y0 = Mathf.Max(0, Mathf.RoundToInt(proj.y - radius * (0.7f - 0.45f * yBias)));
            int y1 = Mathf.Min(r.h - 1, Mathf.RoundToInt(proj.y + radius * (0.7f + 0.45f * yBias)));
            var peaks = new List<(float score, int x, int y)>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = r.pxTop[y * r.w + x];
                    if (r.bgTop[y * r.w + x]) continue;
                    float s = FeatureScore(c, type);
                    if (s < thr) continue;
                    // local maximum within a 2px radius (noise-tolerant peak)
                    bool peak = true;
                    for (int oy = -2; peak && oy <= 2; oy++)
                        for (int ox = -2; ox <= 2; ox++)
                        {
                            int xx = Mathf.Clamp(x + ox, 0, r.w - 1), yy = Mathf.Clamp(y + oy, 0, r.h - 1);
                            if ((ox | oy) == 0) continue;
                            if (FeatureScore(r.pxTop[yy * r.w + xx], type) > s) { peak = false; break; }
                        }
                    if (peak) peaks.Add((s, x, y));
                }
            peaks.Sort((p, q) => q.score.CompareTo(p.score));
            foreach (var (score, px_, py_) in peaks)
            {
                if (res.Count >= want) break;
                bool dup = false;
                foreach (var f in res)
                    if ((f - new Vector2(px_, py_)).sqrMagnitude < 36f) { dup = true; break; }
                if (dup) continue;
                // centroid of near-peak pixels: sub-pixel position
                float sx = 0, sy = 0; int n = 0;
                for (int y = Mathf.Max(0, py_ - 3); y <= Mathf.Min(r.h - 1, py_ + 3); y++)
                    for (int x = Mathf.Max(0, px_ - 3); x <= Mathf.Min(r.w - 1, px_ + 3); x++)
                    {
                        if (FeatureScore(r.pxTop[y * r.w + x], type) >= score * 0.7f) { sx += x; sy += y; n++; }
                    }
                if (n >= 2) res.Add(new Vector2(sx / n, sy / n));
            }
            return res;
        }

        /// <summary>Single-argmax convenience wrapper around the candidate
        /// detector (used by tests/diagnosis).</summary>
        static bool DetectRefFeature(Ref r, Vector2 proj, string type, float radius, float bias, float yBias, out Vector2 found)
        {
            var cands = DetectRefFeatureCandidates(r, proj, type, radius, bias, yBias, 1);
            if (cands.Count == 0) { found = proj; return false; }
            found = cands[0];
            return true;
        }

        class AnchorPair { public Vector3 world; public Vector2 img; public string type; public float bias; public float yBias; }

        /// <summary>Reference copy with detected features (cyan) and model
        /// projections (magenta) drawn as dots - makes anchor mismatches
        /// directly visible instead of guessed from RMS numbers.</summary>
        static void DumpAnchors(Ref r, string species, List<AnchorPair> anchors)
        {
            var tex = new Texture2D(r.w, r.h, TextureFormat.RGBA32, false);
            var px = new Color[r.w * r.h];
            for (int i = 0; i < px.Length; i++) px[i] = r.pxTop[i] * 0.6f;
            void Dot(Vector2 p, Color c)
            {
                int cx = Mathf.Clamp((int)p.x, 0, r.w - 1), cy = Mathf.Clamp((int)p.y, 0, r.h - 1);
                for (int oy = -3; oy <= 3; oy++)
                    for (int ox = -3; ox <= 3; ox++)
                    {
                        if (Mathf.Abs(ox) + Mathf.Abs(oy) > 4) continue;
                        int xx = Mathf.Clamp(cx + ox, 0, r.w - 1), yy = Mathf.Clamp(cy + oy, 0, r.h - 1);
                        px[yy * r.w + xx] = c;
                    }
            }
            foreach (var a in anchors)
            {
                if (a.img.x >= 0f) Dot(a.img, new Color(0f, 1f, 1f, 1f));
                Dot(Project(a.world), new Color(1f, 0f, 1f, 1f));
            }
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, $"anchors_{species}.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Anchor texels measured on the VANILLA sheets (the references are
        /// vanilla renders, so features must land at vanilla texel positions):
        /// pig pupils at head-face cols 0/7 row 3, nostrils outer cols of the
        /// snout front; chicken eyes cols 0/3 row 1; sheep eyes outer cols.
        /// (a,b) = face params, bias = outward horizontal search bias so
        /// left/right anchors don't latch onto each other, yBias pushes the
        /// nostril windows below the eyes.
        /// </summary>
        static (string box, string type, float a, float b, float bias, float yBias)[] VanillaAnchors(string species)
        {
            if (species == "pig") return new[]
            {
                // Reference pig (wiki render, facing front-left): both pupils
                // visible, snout below with two near-black nostrils. All four
                // are strongly detectable; eyes alone underdetermined pitch.
                ("Head", "dark", 0.0625f, 0.5625f, -1f, 0f),
                ("Head", "dark", 0.9375f, 0.5625f, 1f, 0f),
                ("Snout", "nostril", 0.125f, 0.5f, -1f, 1f),
                ("Snout", "nostril", 0.875f, 0.5f, 1f, 1f),
            };
            if (species == "chicken") return new[]
            {
                ("Head", "dark", 0.125f, 0.75f, -1f, 0f),
                ("Head", "dark", 0.875f, 0.75f, 1f, 0f),
                ("Wattle", "red", 0.5f, 0.5f, 0f, 0f),
                ("Beak", "orange", 0.5f, 0.5f, 0f, 0f),
            };
            return new[]
            {
                ("Head", "dark", 0.083f, 0.583f, -1f, 0f),
                ("Head", "dark", 0.917f, 0.583f, 1f, 0f),
            };
        }

        /// <summary>
        /// Re-solves (yaw, pitch, scale, center) so the model's feature texels
        /// reproject exactly onto the same features detected in the reference.
        /// This is what breaks the silhouette-IoU plateau: IoU cannot see a
        /// few-degrees pose error, faces can. The reference heads are TURNED
        /// relative to the body (wiki renders), so the solve also picks a head
        /// yaw: each candidate gets its own harvested geometry, and the winner
        /// (best rms) is returned for painting. Returns true on sub-texel RMS.
        /// </summary>
        static bool SolveAnchors(System.Func<float, ModelGeom> buildGeom, Ref r, string species, out float headYawOut, out ModelGeom geomOut)
        {
            headYawOut = 0f;
            geomOut = null;
            var headYaws = new[] { 0f, 10f, -10f, 20f, -20f, 30f, -30f };
            float gBest = float.MaxValue;
            bool anyAccepted = false;
            float wYaw = 0f, wPitch = 0f, wScale = 0f; Vector2 wC = Vector2.zero;
            float sYaw0 = yaw, sPitch0 = pitch, sScale0 = projScale; Vector2 sC0 = projCenter;

            foreach (var hy in headYaws)
            {
                var geom = buildGeom(hy);
                var anchors = new List<AnchorPair>();
                foreach (var (box, type, a, b, bias, yBias) in VanillaAnchors(species))
                {
                    var F = geom.faces.FirstOrDefault(f => f.box.Contains(box) && f.n.z > 0.9f);
                    if (F == null) continue;
                    var P = QuadPoint(F, a, b) + F.n * 0.004f;
                    anchors.Add(new AnchorPair { world = P, type = type, bias = bias, yBias = yBias });
                }
                if (anchors.Count == 0) continue;

                // anchor failure must not leave a worse pose than the IoU fit
                float sYaw = yaw, sPitch = pitch, sScale = projScale; Vector2 sC = projCenter;

                float Error()
                {
                    float e = 0f; int n = 0;
                    foreach (var a in anchors)
                        if (a.img.x >= 0f) { e += (Project(a.world) - a.img).sqrMagnitude; n++; }
                    return n > 0 ? e / n : 1e9f;
                }

                const float R = 110f;
                for (int iter = 0; iter < 2; iter++)
                {
                    float rad = iter == 0 ? R : 55f;
                    // collect candidates per anchor, then pick the assignment
                    // (one reference point per anchor, points not shared) that
                    // minimises total reprojection error after the closed-form
                    // translation. Independent argmax collapses paired anchors
                    // onto the same feature - that was the scrambler.
                    var cand = new List<Vector2>[anchors.Count];
                    var proj0 = new Vector2[anchors.Count];
                    for (int i = 0; i < anchors.Count; i++)
                    {
                        proj0[i] = Project(anchors[i].world);
                        cand[i] = DetectRefFeatureCandidates(r, proj0[i], anchors[i].type, rad, anchors[i].bias, anchors[i].yBias, 5);
                    }
                    int comboCount = 1;
                    foreach (var c in cand) comboCount *= Mathf.Max(1, c.Count);
                    Vector2[] bestAssign = new Vector2[anchors.Count];
                    bool any = false;
                    float bestCombo = float.MaxValue;
                    for (int combo = 0; combo < comboCount; combo++)
                    {
                        int rem = combo;
                        bool skip = false;
                        var chosen = new Vector2[anchors.Count];
                        for (int i = 0; i < anchors.Count && !skip; i++)
                        {
                            if (cand[i].Count == 0) { skip = true; break; }
                            chosen[i] = cand[i][rem % cand[i].Count];
                            rem /= cand[i].Count;
                            for (int j = 0; j < i; j++)
                                if ((chosen[i] - chosen[j]).sqrMagnitude < 64f) { skip = true; break; } // shared point
                        }
                        if (skip) continue;
                        Vector2 mean = Vector2.zero;
                        for (int i = 0; i < anchors.Count; i++) mean += chosen[i] - proj0[i];
                        mean /= anchors.Count;
                        float e = 0f;
                        for (int i = 0; i < anchors.Count; i++)
                        {
                            var d = chosen[i] - (proj0[i] + mean);
                            e += d.sqrMagnitude;
                            // pairwise separation sanity: model distances must
                            // roughly survive in the reference (beats swapped pairs)
                            for (int j = 0; j < i; j++)
                            {
                                float dm = (proj0[i] - proj0[j]).magnitude;
                                float dr = (chosen[i] - chosen[j]).magnitude;
                                float diff = Mathf.Abs(dr - dm) / Mathf.Max(20f, dm);
                                e += diff * diff * 4000f;
                            }
                        }
                        if (e < bestCombo) { bestCombo = e; bestAssign = chosen; any = true; }
                    }
                    int matched = 0;
                    for (int i = 0; i < anchors.Count; i++)
                    {
                        anchors[i].img = any && cand[i].Count > 0 ? bestAssign[i] : new Vector2(-1f, -1f);
                        if (anchors[i].img.x >= 0f) matched++;
                    }
                    if (matched < 2)
                    {
                        Debug.Log($"REPLICA anchors '{species}' headYaw={hy}: only {matched} matched in reference, keeping IoU fit");
                        yaw = sYaw; pitch = sPitch; projScale = sScale; projCenter = sC;
                        continue;
                    }

                // Pose sweep with CLOSED-FORM optimal translation, over a WIDE
                // region: the silhouette IoU fit systematically lands too
                // lateral (all three wiki refs solved ~25 deg more frontal and
                // painted the sheep face perfectly there). Feature rms is the
                // acceptance criterion, not proximity to the IoU pose.
                float bestE = float.MaxValue;
                float bYaw = yaw, bPitch = pitch, bScale = projScale;
                Vector2 bC = projCenter;
                float y0 = yaw, p0 = pitch, s0 = projScale; Vector2 c0 = projCenter;
                for (float dyaw = -30f; dyaw <= 30f; dyaw += 2f)
                    for (float dpitch = -15f; dpitch <= 15f; dpitch += 2f)
                        foreach (float ds in new[] { -0.10f, -0.05f, 0f, 0.05f, 0.10f })
                        {
                            yaw = y0 + dyaw;
                            pitch = Mathf.Clamp(p0 + dpitch, 5f, 80f);
                            projScale = s0 * (1f + ds);
                            Vector2 mean = Vector2.zero; int n = 0;
                            foreach (var a in anchors)
                                if (a.img.x >= 0f) { mean += a.img - Project(a.world); n++; }
                            if (n == 0) continue;
                            projCenter = c0 + mean / n;
                            float e = Error();
                            if (e < bestE) { bestE = e; bYaw = yaw; bPitch = pitch; bScale = projScale; bC = projCenter; }
                        }
                yaw = bYaw; pitch = bPitch; projScale = bScale; projCenter = bC;

                // small-step polish on top of the swept pose
                for (int sweep = 0; sweep < 4; sweep++)
                {
                    float yawStep = 2f * Mathf.Pow(0.5f, sweep);
                    float pitchStep = 1.5f * Mathf.Pow(0.5f, sweep);
                    float scaleStep = 0.015f * Mathf.Pow(0.5f, sweep);
                    float cStep = 6f * Mathf.Pow(0.5f, sweep);
                    bool improved = false;
                    foreach (float d in new[] { 1f, -1f })
                    {
                        yaw = bYaw + d * yawStep;
                        float e = Error();
                        if (e < bestE - 1e-4f) { bestE = e; bYaw = yaw; improved = true; }
                        yaw = bYaw;
                        pitch = Mathf.Clamp(bPitch + d * pitchStep, 5f, 80f);
                        e = Error();
                        if (e < bestE - 1e-4f) { bestE = e; bPitch = pitch; improved = true; }
                        pitch = bPitch;
                        projScale = bScale * (1f + d * scaleStep);
                        e = Error();
                        if (e < bestE - 1e-4f) { bestE = e; bScale = projScale; improved = true; }
                        projScale = bScale;
                        projCenter = bC + new Vector2(d * cStep, 0f);
                        e = Error();
                        if (e < bestE - 1e-4f) { bestE = e; bC = projCenter; improved = true; }
                        projCenter = bC;
                        projCenter = bC + new Vector2(0f, d * cStep);
                        e = Error();
                        if (e < bestE - 1e-4f) { bestE = e; bC = projCenter; improved = true; }
                        projCenter = bC;
                    }
                    yaw = bYaw; pitch = bPitch; projScale = bScale; projCenter = bC;
                    if (!improved) break;
                }
                float rms = Mathf.Sqrt(bestE);
                Debug.Log($"REPLICA anchor solve '{species}' headYaw={hy}: matched={matched} rms={rms:F1}px yaw={yaw:F1} pitch={pitch:F1} scale={projScale:F0} center=({projCenter.x:F0},{projCenter.y:F0})");
                if (bestE < gBest)
                {
                    gBest = bestE;
                    headYawOut = hy;
                    geomOut = geom;
                    wYaw = yaw; wPitch = pitch; wScale = projScale; wC = projCenter;
                }
                float texelPx = projScale / 16f;
                if (rms < 12f && rms < 0.35f * texelPx) anyAccepted = true; // sub-texel pose; the symmetry audit makes the final call
                } // iter loop
            } // foreach headYaw

            // restore the winning pose/geometry for painting
            if (!anyAccepted)
            {
                // rejected: fall back to the IoU fit exactly as before
                yaw = sYaw0; pitch = sPitch0; projScale = sScale0; projCenter = sC0;
                return false;
            }
            if (geomOut != null)
            {
                yaw = wYaw; pitch = wPitch; projScale = wScale; projCenter = wC;
                var geom = geomOut;
                var anchors = new List<AnchorPair>();
                foreach (var (box, type, a, b, bias, yBias) in VanillaAnchors(species))
                {
                    var F = geom.faces.FirstOrDefault(f => f.box.Contains(box) && f.n.z > 0.9f);
                    if (F == null) continue;
                    anchors.Add(new AnchorPair { world = QuadPoint(F, a, b) + F.n * 0.004f, type = type, bias = bias, yBias = yBias });
                }
                foreach (var a in anchors)
                {
                    var pp = Project(a.world);
                    Debug.Log($"  winner '{species}' headYaw={headYawOut}: anchor {a.type} model=({pp.x:F0},{pp.y:F0})");
                }
                DumpAnchors(r, species, anchors);
            }
            return anyAccepted;
        }

        /// <summary>
        /// Acceptance gate: the head front face on the finished sheet must read
        /// bilaterally symmetric (left half vs mirrored right half, normalised
        /// by contrast). A scrambled face fails here instead of shipping.
        /// </summary>
        static float SheetFaceAsym(ModelGeom geom, Texture2D sheet)
        {
            var F = geom.faces.FirstOrDefault(f => f.box.Contains("Head") && f.n.z > 0.9f);
            if (F == null) return 0f;
            var px = sheet.GetPixels();
            float u0 = Mathf.Min(Mathf.Min(F.uv0.x, F.uv1.x), Mathf.Min(F.uv2.x, F.uv3.x));
            float u1 = Mathf.Max(Mathf.Max(F.uv0.x, F.uv1.x), Mathf.Max(F.uv2.x, F.uv3.x));
            float vLo = Mathf.Min(Mathf.Min(F.uv0.y, F.uv1.y), Mathf.Min(F.uv2.y, F.uv3.y));
            float vHi = Mathf.Max(Mathf.Max(F.uv0.y, F.uv1.y), Mathf.Max(F.uv2.y, F.uv3.y));
            int x0 = Mathf.CeilToInt(u0 * 64f), x1 = Mathf.FloorToInt(u1 * 64f) - 1;
            int ty0 = Mathf.CeilToInt((1f - vHi) * 32f), ty1 = Mathf.FloorToInt((1f - vLo) * 32f) - 1;
            var mean = new Color(0, 0, 0, 0); int n = 0;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int x = x0; x <= x1; x++) { mean += px[(31 - ty) * 64 + x]; n++; }
            if (n == 0) return 0f;
            mean /= n;
            float diff = 0f, contrast = 0f; int pairs = 0;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = (31 - ty) * 64 + x;
                    contrast += Mathf.Abs(px[i].r - mean.r) + Mathf.Abs(px[i].g - mean.g) + Mathf.Abs(px[i].b - mean.b);
                    int mx = x1 - (x - x0);
                    int j = (31 - ty) * 64 + mx;
                    diff += Mathf.Abs(px[i].r - px[j].r) + Mathf.Abs(px[i].g - px[j].g) + Mathf.Abs(px[i].b - px[j].b);
                    pairs++;
                }
            return pairs == 0 ? 0f : (diff / pairs) / (contrast / n + 0.15f);
        }

        // ---------------- model render + composite ----------------
        static void RenderAndCompare(ModelGeom geom, string species, Texture2D sheet, Ref r, string outPath)
        {
            Color bgCol = r.bgColor.a < 0.5f ? Color.white : r.bgColor;
            var mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = sheet };
            var root = new GameObject("ReplicaView_" + species);
            try
            {
                var animal = root.AddComponent<BlockyAnimal>();
                animal.species = species;
                animal.BuildModel();
                root.transform.position = Vector3.zero;
                root.transform.rotation = Quaternion.identity;
                foreach (var ren in root.GetComponentsInChildren<Renderer>())
                    ren.sharedMaterial = mat;

                // Camera exactly reproduces the software projector: ortho 1
                // world unit = projScale px, or perspective focal = scale*dist;
                // geom.center sits at projCenter either way.
                var rot = Quaternion.Euler(pitch, yaw, 0f);
                var camGo = new GameObject("Cam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = bgCol;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 500f;
                float rw = r.w, rh = r.h;
                cam.aspect = rw / rh;
                float camDist = 10f;
                if (projDist > 0f)
                {
                    cam.orthographic = false;
                    float focal = projScale * projDist;
                    cam.fieldOfView = 2f * Mathf.Atan(rh / (2f * focal)) * Mathf.Rad2Deg;
                    camDist = projDist;
                }
                else
                {
                    cam.orthographic = true;
                    cam.orthographicSize = rh / (2f * projScale);
                }
                camGo.transform.position = geom.center - rot * Vector3.forward * camDist;
                camGo.transform.rotation = rot;

                var rt = new RenderTexture((int)rw, (int)rh, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var shot = new Texture2D((int)rw, (int)rh, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, rw, rh), 0, 0);
                shot.Apply();
                cam.targetTexture = null;
                RenderTexture.active = null;
                Object.DestroyImmediate(rt);

                // ReadPixels is bottom-up; flip to top-down to match the ref.
                var shotPx = shot.GetPixels();
                Object.DestroyImmediate(shot);
                var repPx = new Color[shotPx.Length];
                for (int y = 0; y < (int)rh; y++)
                    System.Array.Copy(shotPx, y * (int)rw, repPx, ((int)rh - 1 - y) * (int)rw, (int)rw);

                // Render-based silhouette IoU: honest end-to-end metric.
                var repMask = new bool[repPx.Length];
                for (int i = 0; i < repPx.Length; i++)
                {
                    var c = repPx[i];
                    repMask[i] = c.a > 0.5f &&
                                 (Mathf.Abs(c.r - bgCol.r) > 0.08f ||
                                  Mathf.Abs(c.g - bgCol.g) > 0.08f ||
                                  Mathf.Abs(c.b - bgCol.b) > 0.08f);
                }
                var refMask = new bool[repPx.Length];
                for (int i = 0; i < refMask.Length; i++) refMask[i] = !r.bgTop[i];
                float iou = Iou(repMask, refMask);
                Debug.Log($"REPLICA render IoU '{species}': {iou:F3} (yaw={yaw:F1} pitch={pitch:F1} scale={projScale:F2})");

                // 3-panel composite: reference | replica | 50% blend, all over bg.
                int gap = 8, W = r.w * 3 + gap * 2, H = r.h;
                var comb = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var cbg = new Color[W * H];
                for (int i = 0; i < cbg.Length; i++) cbg[i] = new Color(0.15f, 0.15f, 0.18f, 1f);
                comb.SetPixels(cbg);
                var refOver = new Color[r.w * r.h];
                for (int i = 0; i < refOver.Length; i++)
                    refOver[i] = r.pxTop[i].a < 0.6f ? bgCol : Color.Lerp(bgCol, r.pxTop[i], r.pxTop[i].a);
                comb.SetPixels(0, 0, r.w, r.h, refOver);
                comb.SetPixels(r.w + gap, 0, (int)rw, (int)rh, repPx);
                var blend = new Color[r.w * r.h];
                for (int i = 0; i < blend.Length; i++) blend[i] = Color.Lerp(refOver[i], repPx[i], 0.5f);
                comb.SetPixels(2 * (r.w + gap), 0, r.w, r.h, blend);
                comb.Apply();
                File.WriteAllBytes(outPath, comb.EncodeToPNG());
                Object.DestroyImmediate(comb);
                Debug.Log($"REPLICA wrote {outPath}");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mat);
            }
        }

        static void SaveSheet(string species, Texture2D sheet, bool allowGame)
        {
            var png = sheet.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(OutDir, $"replica_{species}_sheet.png"), png);
            File.WriteAllBytes(Path.Combine(SkinDir, $"replica_{species}_skin.png.bytes"), png);
            if (allowGame)
            {
                File.WriteAllBytes(Path.Combine(SkinDir, $"{species}_skin.png.bytes"), png);
                Debug.Log($"REPLICA applied game skin -> {species}_skin.png.bytes");
            }
            else
            {
                Debug.Log($"REPLICA game skin NOT applied for {species} (acceptance gate)");
            }
            Debug.Log($"REPLICA skin sheet -> replica_{species}_skin.png.bytes");
        }

        // ---------------- synthetic fallback reference ----------------
        static void MakeSyntheticRef(ModelGeom geom, string path)
        {
            int w = 600, h = 600;
            yaw = 135f; pitch = 30f;
            SetCamera(geom.center, w, h);
            Vector2 pmin = new Vector2(9999f, 9999f), pmax = new Vector2(-9999f, -9999f);
            foreach (var cs in geom.boxCorners)
                foreach (var c in cs)
                {
                    var sp = Project(c);
                    pmin = Vector2.Min(pmin, sp); pmax = Vector2.Max(pmax, sp);
                }
            projScale = 0.7f * Mathf.Min(w / Mathf.Max(pmax.x - pmin.x, 1e-3f), h / Mathf.Max(pmax.y - pmin.y, 1e-3f));
            Vector2 pc = Project(geom.center);
            projCenter += new Vector2(w * 0.5f, h * 0.5f) - pc;

            var faces = new List<BoxFace>(geom.faces);
            faces.Sort((x, y) => Depth(QuadPoint(y, 0.5f, 0.5f)).CompareTo(Depth(QuadPoint(x, 0.5f, 0.5f))));
            var img = new Color[w * h];
            for (int i = 0; i < img.Length; i++) img[i] = Color.white;
            var m = new bool[w * h];
            foreach (var F in faces)
            {
                if (Vector3.Dot(F.n, camRot * Vector3.forward) > -0.02f) continue;
                float shade = F.n.y > 0.5f ? 1f : F.n.z > 0.5f ? 0.86f : F.n.x != 0f ? 0.78f : 0.7f;
                Color col = new Color(0.85f * shade, 0.83f * shade, 0.8f * shade, 1f);
                if (F.box.StartsWith("Beak") || F.box.StartsWith("Wattle") || F.box.StartsWith("Leg") || F.box.StartsWith("Foot")) col = new Color(0.87f * shade, 0.55f * shade, 0.3f * shade, 1f);
                if (F.box.StartsWith("Wattle")) col = new Color(0.7f * shade, 0.15f * shade, 0.12f * shade, 1f);
                if (F.box.StartsWith("Snout")) col = new Color(0.88f * shade, 0.6f * shade, 0.55f * shade, 1f);
                System.Array.Clear(m, 0, m.Length);
                for (int j = 0; j <= F.gb; j++)
                    for (int i2 = 0; i2 <= F.ga; i2++)
                    {
                        var sp = Project(QuadPoint(F, Mathf.Min(1f, (float)i2 / F.ga), Mathf.Min(1f, (float)j / F.gb)));
                        int ix = Mathf.Clamp((int)sp.x, 0, w - 1), iy = Mathf.Clamp((int)sp.y, 0, h - 1);
                        m[iy * w + ix] = true;
                    }
                for (int i = 0; i < m.Length; i++) if (m[i]) img[i] = col;
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels(img); tex.Apply();
            Directory.CreateDirectory(RefDir);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Debug.Log($"REPLICA synthetic stand-in reference -> {path}");
        }

        // ---------------- batch entry ----------------
        public static void RunExperiment()
        {
            Directory.CreateDirectory(RefDir);
            Directory.CreateDirectory(OutDir);
            RunOne("chicken"); RunOne("pig"); RunOne("sheep");
            Debug.Log("REPLICA EXPERIMENT DONE");
        }

        /// <summary>
        /// Diagnostic: sweeps projection kind (ortho / perspective distance)
        /// and (yaw, pitch), optimizing scale/center at each - tells a wrong
        /// projection apart from a geometry mismatch. Doesn't touch skins.
        /// </summary>
        public static void RunAngleProbe()
        {
            Directory.CreateDirectory(OutDir);
            foreach (string species in new[] { "pig", "chicken", "sheep" })
            {
                var geom = BuildGameModel(species);
                var r = LoadReference(Path.Combine(RefDir, species + ".png"));
                r.name = species;
                var subject = new bool[r.w * r.h];
                for (int i = 0; i < subject.Length; i++) subject[i] = !r.bgTop[i];
                foreach (float dist in new[] { 0f, 2.5f, 4f, 6f })
                {
                    projDist = dist;
                    foreach (var (ya, pi) in new[] { (135f, 30f), (135f, 35f), (135f, 40f), (140f, 38f), (145f, 33f) })
                    {
                        yaw = ya; pitch = pi;
                        CalibrateCamera(geom, r);
                        float s = OptimizeScaleCenter(geom, r, subject, Iou(Rasterize(geom, r.w, r.h), subject));
                        Debug.Log($"REPLICA probe {species} dist={dist:F1} yaw={ya:F0} pitch={pi:F0} IoU={s:F3}");
                    }
                }
                projDist = 0f;
            }
            Debug.Log("REPLICA ANGLE PROBE DONE");
        }

        static void RunOne(string species)
        {
            yaw = 135f; pitch = 30f; // reset shared state between mobs
            var geom = BuildGameModel(species);
            if (geom.faces.Count == 0) return;

            string refPath = Path.Combine(RefDir, species + ".png");
            if (!File.Exists(refPath)) MakeSyntheticRef(geom, refPath);
            var rOrig = LoadReference(refPath);

            // Auto-orient: chat-derived references are sometimes stored
            // vertically flipped (wattle above beak, hooves skyward). Fit both
            // orientations and keep the higher-IoU one.
            float sOrig = FitProjection(geom, rOrig, species, dump: false);
            var rFlip = FlipVertical(rOrig);
            float sFlip = FitProjection(geom, rFlip, species, dump: false);
            Ref r;
            if (sFlip > sOrig + 0.005f)
            {
                r = rFlip;
                Debug.Log($"REPLICA orientation '{species}': FLIPPED (asIs={sOrig:F3} flipped={sFlip:F3})");
                FitProjection(geom, r, species, dump: true);
            }
            else
            {
                r = rOrig;
                Debug.Log($"REPLICA orientation '{species}': as-is (asIs={sOrig:F3} flipped={sFlip:F3})");
                FitProjection(geom, r, species, dump: true);
            }

            // Feature anchoring: break the silhouette plateau by locking the
            // projection onto vanilla-layout features (eyes/snout/wattle)
            // detected in the reference. The reference head is often TURNED
            // relative to the body, so the solve also picks a head yaw and
            // returns matching geometry for painting.
            bool anchored = SolveAnchors(hy => BuildGameModel(species, hy), r, species, out float headYaw, out var aGeom);
            if (anchored)
            {
                geom = aGeom;
                Debug.Log($"REPLICA anchored '{species}': headYaw={headYaw:F0}deg");
                DumpFit(geom, r, species, subject: null);
            }

            var sheet = PaintSkin(geom, r);
            // Acceptance gates: feature anchoring quality is logged by
            // SolveAnchors; the finished face must be bilaterally symmetric
            // or the skin does NOT ship to the game.
            float faceAsym = SheetFaceAsym(geom, sheet);
            bool symPass = faceAsym < 0.45f;
            Debug.Log($"REPLICA symmetry '{species}': faceAsym={faceAsym:F2} {(symPass ? "PASS" : "FAIL")}");
            SaveSheet(species, sheet, applyToGame && symPass);
            RenderAndCompare(geom, species, sheet, r, Path.Combine(OutDir, $"replica_{species}.png"));
        }

        // ---------------- window ----------------
        [MenuItem("Tools/VoxelCraft/Mob Replicator")]
        static void Open() => GetWindow<MobReplicator>("Mob Replicator");

        void OnGUI()
        {
            EditorGUILayout.HelpBox("Put reference screenshots in D:\\zlj world\\_refs as chicken.png / pig.png / sheep.png, then Run Experiment. The model is the GAME's own (BlockyAnimal); only the projection is fitted. Output: _logs\\replica_<mob>.png (reference | replica | blend). Skins are written to <mob>_skin.png.bytes.", MessageType.Info);
            yaw = EditorGUILayout.Slider("Initial camera yaw", yaw, 0f, 360f);
            pitch = EditorGUILayout.Slider("Initial camera pitch", pitch, 0f, 80f);
            applyToGame = EditorGUILayout.Toggle("Apply skins to game", applyToGame);
            if (GUILayout.Button("Run Experiment")) RunExperiment();
            if (GUILayout.Button("Chicken only")) RunOne("chicken");
            if (GUILayout.Button("Pig only")) RunOne("pig");
            if (GUILayout.Button("Sheep only")) RunOne("sheep");
        }
    }
}
