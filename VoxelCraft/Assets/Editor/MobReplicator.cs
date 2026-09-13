using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Art;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Mob Replicator: rebuilds a textured blocky mob from a REFERENCE
    /// SCREENSHOT. Pipeline: load reference -> detect background (flood fill
    /// from borders) -> for every box face in the model spec, inverse-map each
    /// texel through the isometric camera and bilinearly sample the reference
    /// -> paint a fresh 64x32 skin sheet -> build the boxes with that skin ->
    /// render at the reference angle/resolution -> save a side-by-side
    /// comparison PNG. Batch entry: MobReplicator.RunExperiment.
    /// </summary>
    public class MobReplicator : EditorWindow
    {
        const string RefDir = @"D:\zlj world\_refs";
        const string OutDir = @"D:\zlj world\_logs";
        const string SkinDir = @"D:\zlj world\VoxelCraft\Assets\Resources\Textures";

        static float yaw = 225f, pitch = 30f;

        // ---------------- spec model ----------------
        public class MobBox
        {
            public string id; public Vector3 size; public Vector3 pos; public int tu, tv;
            public MobBox(string id, float sx, float sy, float sz, float x, float y, float z, int tu, int tv)
            {
                this.id = id; size = new Vector3(sx, sy, sz); pos = new Vector3(x, y, z); this.tu = tu; this.tv = tv;
            }
        }

        public class MobSpec
        {
            public string name; public List<MobBox> boxes = new List<MobBox>();
            public MobSpec(string name) { this.name = name; }
        }

        // Sizes/positions in texture pixels; y up, +z = face, origin on the ground.
        public static MobSpec SpecChicken()
        {
            var s = new MobSpec("chicken");
            s.boxes.Add(new MobBox("body", 6, 6, 8, 0, 8, 0, 0, 8));
            s.boxes.Add(new MobBox("head", 4, 6, 3, 0, 13.4f, 5.35f, 28, 0));
            s.boxes.Add(new MobBox("beak", 4, 3, 1, 0, 12.3f, 7.05f, 28, 9));
            s.boxes.Add(new MobBox("wattle", 2, 2, 2, 0, 9.5f, 6.7f, 40, 9));
            s.boxes.Add(new MobBox("wing0", 1, 4, 6, -3.52f, 9, 0, 46, 0));
            s.boxes.Add(new MobBox("wing1", 1, 4, 6, 3.52f, 9, 0, 46, 0));
            s.boxes.Add(new MobBox("leg0", 2, 5, 2, -1, 2.69f, 0, 50, 16));
            s.boxes.Add(new MobBox("leg1", 2, 5, 2, 1, 2.69f, 0, 50, 16));
            s.boxes.Add(new MobBox("foot0", 3, 1, 2, -1, 0.5f, 1.5f, 52, 26));
            s.boxes.Add(new MobBox("foot1", 3, 1, 2, 1, 0.5f, 1.5f, 52, 26));
            return s;
        }

        public static MobSpec SpecPig()
        {
            var s = new MobSpec("pig");
            s.boxes.Add(new MobBox("body", 10, 8, 16, 0, 10, 0, 0, 8));
            s.boxes.Add(new MobBox("head", 8, 8, 8, 0, 17.2f, 9.8f, 32, 0));
            s.boxes.Add(new MobBox("snout", 8, 4, 1, 0, 15.2f, 11.9f, 0, 0));
            s.boxes.Add(new MobBox("leg0", 4, 6, 4, -3, 3.2f, 5, 40, 0));
            s.boxes.Add(new MobBox("leg1", 4, 6, 4, 3, 3.2f, 5, 40, 0));
            s.boxes.Add(new MobBox("leg2", 4, 6, 4, -3, 3.2f, -5, 40, 0));
            s.boxes.Add(new MobBox("leg3", 4, 6, 4, 3, 3.2f, -5, 40, 0));
            return s;
        }

        public static MobSpec SpecSheep()
        {
            var s = new MobSpec("sheep");
            s.boxes.Add(new MobBox("body", 8, 6, 16, 0, 15, 0, 0, 8));
            s.boxes.Add(new MobBox("head", 6, 6, 6, 0, 20.4f, 10.7f, 32, 0));
            s.boxes.Add(new MobBox("leg0", 4, 12, 4, -2.5f, 6.2f, 5, 0, 0));
            s.boxes.Add(new MobBox("leg1", 4, 12, 4, 2.5f, 6.2f, 5, 0, 0));
            s.boxes.Add(new MobBox("leg2", 4, 12, 4, -2.5f, 6.2f, -5, 0, 0));
            s.boxes.Add(new MobBox("leg3", 4, 12, 4, 2.5f, 6.2f, -5, 0, 0));
            return s;
        }

        // ---------------- face layout ----------------
        struct Face { public Rect rect; public Vector3 n, origin, uAxis, vAxis; }

        static IEnumerable<Face> FacesOf(MobBox b)
        {
            // Same net layout as BoxBuilder.McNet(u, v, W, H, D).
            int u = b.tu, v = b.tv; float W = b.size.x, H = b.size.y, D = b.size.z;
            Vector3 c = b.pos, h = b.size * 0.5f;
            yield return new Face { rect = new Rect(u, v + D, D, H), n = new Vector3(1, 0, 0), origin = c + new Vector3(h.x, h.y, h.z), uAxis = new Vector3(0, 0, -1), vAxis = new Vector3(0, -1, 0) };
            yield return new Face { rect = new Rect(u + D + W, v + D, D, H), n = new Vector3(-1, 0, 0), origin = c + new Vector3(-h.x, h.y, -h.z), uAxis = new Vector3(0, 0, 1), vAxis = new Vector3(0, -1, 0) };
            yield return new Face { rect = new Rect(u + D, v, W, D), n = new Vector3(0, 1, 0), origin = c + new Vector3(-h.x, h.y, h.z), uAxis = new Vector3(1, 0, 0), vAxis = new Vector3(0, 0, -1) };
            yield return new Face { rect = new Rect(u + D + W, v, W, D), n = new Vector3(0, -1, 0), origin = c + new Vector3(-h.x, -h.y, -h.z), uAxis = new Vector3(1, 0, 0), vAxis = new Vector3(0, 0, 1) };
            yield return new Face { rect = new Rect(u + D, v + D, W, H), n = new Vector3(0, 0, 1), origin = c + new Vector3(-h.x, h.y, h.z), uAxis = new Vector3(1, 0, 0), vAxis = new Vector3(0, -1, 0) };
            yield return new Face { rect = new Rect(u + 2 * D + W, v + D, W, H), n = new Vector3(0, 0, -1), origin = c + new Vector3(h.x, h.y, -h.z), uAxis = new Vector3(-1, 0, 0), vAxis = new Vector3(0, -1, 0) };
        }

        static Vector3 TexelPoint(Face f, Vector3 size, int tx, int ty, int tw, int th)
        {
            float a = (tx + 0.5f) / tw, b = (ty + 0.5f) / th;
            return f.origin + f.uAxis * (a * size.x) + f.vAxis * (b * size.y);
        }

        // ---------------- projection ----------------
        static Quaternion camRot; static Vector3 camPos; static Vector2 projCenter; static float projScale = 1f;

        static void SetCamera(float rw, float rh, Vector3 target)
        {
            camRot = Quaternion.Euler(pitch, yaw, 0f);
            camPos = target - camRot * Vector3.forward * 500f;
            projCenter = new Vector2(rw * 0.5f, rh * 0.5f);
        }

        /// <summary>
        /// Auto-calibrates the painter's projection against the reference:
        /// finds the non-background bounding box in the image and scales/shifts
        /// our projected spec bounds onto it, so arbitrary screenshots with
        /// unknown framing sample correctly.
        /// </summary>
        static void CalibrateCamera(MobSpec spec, Ref r)
        {
            int minX = r.w, minY = r.h, maxX = -1, maxY = -1;
            for (int y = 0; y < r.h; y++)
                for (int x = 0; x < r.w; x++)
                    if (!r.bg[y * r.w + x])
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) { SetCamera(r.w, r.h, Vector3.zero); return; }

            Vector3 lo = new Vector3(999f, 999f, 999f), hi = new Vector3(-999f, -999f, -999f);
            foreach (var b in spec.boxes)
                for (int i = 0; i < 8; i++)
                {
                    var c = b.pos + Vector3.Scale(
                        new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1),
                        b.size * 0.5f);
                    lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c);
                }
            var center = (lo + hi) * 0.5f;
            SetCamera(r.w, r.h, center);
            Vector2 pmin = new Vector2(9999f, 9999f), pmax = new Vector2(-9999f, -9999f);
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
                var sp = Project(c);
                pmin = Vector2.Min(pmin, sp); pmax = Vector2.Max(pmax, sp);
            }
            float sw = (maxX - minX + 1) / Mathf.Max(pmax.x - pmin.x, 1f);
            float sh = (maxY - minY + 1) / Mathf.Max(pmax.y - pmin.y, 1f);
            projScale = Mathf.Min(sw, sh);
            Vector2 pc = Project(center);
            projCenter += new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f) - pc;
        }

        static Vector2 Project(Vector3 p)
        {
            var c = Quaternion.Inverse(camRot) * (p - camPos);
            return new Vector2(projCenter.x + c.x * projScale, projCenter.y - c.y * projScale);
        }

        static float Depth(Vector3 p)
        {
            var c = Quaternion.Inverse(camRot) * (p - camPos);
            return c.z;
        }

        // ---------------- reference loading ----------------
        class Ref { public Color[] px; public bool[] bg; public int w, h; public Color bgColor; }

        static Ref LoadReference(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(path));
            var r = new Ref { px = tex.GetPixels(), w = tex.width, h = tex.height };
            r.bg = new bool[r.w * r.h];
            // Border seed color = mean of the 4 corners.
            Color c0 = r.px[0], c1 = r.px[r.w - 1], c2 = r.px[(r.h - 1) * r.w], c3 = r.px[r.h * r.w - 1];
            r.bgColor = (c0 + c1 + c2 + c3) * 0.25f;
            // Flood fill from all border pixels within tolerance of the seed.
            var stack = new Stack<int>();
            for (int x = 0; x < r.w; x++) { stack.Push(x); stack.Push((r.h - 1) * r.w + x); }
            for (int y = 0; y < r.h; y++) { stack.Push(y * r.w); stack.Push(y * r.w + r.w - 1); }
            float tol = 0.06f;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (i < 0 || i >= r.px.Length || r.bg[i]) continue;
                var c = r.px[i];
                if (Mathf.Abs(c.r - r.bgColor.r) + Mathf.Abs(c.g - r.bgColor.g) + Mathf.Abs(c.b - r.bgColor.b) > tol) continue;
                r.bg[i] = true;
                int x = i % r.w, y = i / r.w;
                if (x > 0) stack.Push(i - 1);
                if (x < r.w - 1) stack.Push(i + 1);
                if (y > 0) stack.Push(i - r.w);
                if (y < r.h - 1) stack.Push(i + r.w);
            }
            Object.DestroyImmediate(tex);
            return r;
        }

        static Color SampleBilinear(Ref r, float fx, float fy, out bool onBg)
        {
            fx = Mathf.Clamp(fx, 0f, r.w - 1f); fy = Mathf.Clamp(fy, 0f, r.h - 1f);
            int x0 = (int)fx, y0 = (int)fy, x1 = Mathf.Min(x0 + 1, r.w - 1), y1 = Mathf.Min(y0 + 1, r.h - 1);
            float dx = fx - x0, dy = fy - y0;
            int i00 = y0 * r.w + x0, i10 = y0 * r.w + x1, i01 = y1 * r.w + x0, i11 = y1 * r.w + x1;
            float bg = 0f;
            bg += r.bg[i00] ? 1f : 0f; bg += r.bg[i10] ? 1f : 0f; bg += r.bg[i01] ? 1f : 0f; bg += r.bg[i11] ? 1f : 0f;
            onBg = bg >= 3f;
            Color c = Color.Lerp(Color.Lerp(r.px[i00], r.px[i10], dx), Color.Lerp(r.px[i01], r.px[i11], dx), dy);
            return c;
        }

        // ---------------- skin painting ----------------
        static Texture2D PaintSkin(MobSpec spec, Ref r)
        {
            var sheet = new Texture2D(64, 32, TextureFormat.RGBA32, false);
            var clear = new Color[64 * 32]; for (int i = 0; i < clear.Length; i++) clear[i] = new Color(0, 0, 0, 0);
            sheet.SetPixels(clear);

            foreach (var b in spec.boxes)
            {
                // Per-face mean from non-background samples (pass 1), then paint
                // (pass 2): real sample where available, face mean elsewhere,
                // box mean on faces that never face the camera.
                var faceMeans = new Color[6]; var faceHas = new bool[6];
                var faces = new List<Face>(FacesOf(b));
                var dims = new[] { (int)b.size.z, (int)b.size.x }; // side rect w,h
                for (int fi = 0; fi < 6; fi++)
                {
                    var f = faces[fi];
                    if (Vector3.Dot(f.n, camPos - f.origin) <= 0.02f) continue;
                    int tw = (int)f.rect.width, th = (int)f.rect.height;
                    Color acc = new Color(0, 0, 0, 0); int n = 0;
                    for (int ty = 0; ty < th; ty++)
                        for (int tx = 0; tx < tw; tx++)
                        {
                            var p = TexelPoint(f, b.size, tx, ty, tw, th);
                            var sp = Project(p);
                            bool onBg; var c = SampleBilinear(r, sp.x, sp.y, out onBg);
                            if (!onBg) { acc += c; n++; }
                        }
                    if (n > 0) { faceMeans[fi] = acc / n; faceHas[fi] = true; }
                }
                Color boxMean = new Color(0.8f, 0.8f, 0.8f, 1f); int bn = 0;
                for (int fi = 0; fi < 6; fi++) if (faceHas[fi]) { boxMean += faceMeans[fi]; bn++; }
                if (bn > 0) boxMean = (boxMean - new Color(0.8f, 0.8f, 0.8f, 1f) * 0f) / bn;

                for (int fi = 0; fi < 6; fi++)
                {
                    var f = faces[fi];
                    int tw = (int)f.rect.width, th = (int)f.rect.height;
                    bool visible = Vector3.Dot(f.n, camPos - f.origin) > 0.02f;
                    Color fallback = faceHas[fi] ? faceMeans[fi] : boxMean;
                    for (int ty = 0; ty < th; ty++)
                        for (int tx = 0; tx < tw; tx++)
                        {
                            Color c = fallback;
                            if (visible)
                            {
                                var p = TexelPoint(f, b.size, tx, ty, tw, th);
                                var sp = Project(p);
                                bool onBg; var s = SampleBilinear(r, sp.x, sp.y, out onBg);
                                if (!onBg) c = s;
                            }
                            int sx = (int)f.rect.x + tx, sy = (int)f.rect.y + ty;
                            // Net rect y counts from the TOP (SkinnedBox does
                            // 1 - y/texH), Texture2D.SetPixel counts from the
                            // BOTTOM -> flip the row.
                            if (sx >= 0 && sx < 64 && sy >= 0 && sy < 32) sheet.SetPixel(sx, 31 - sy, c);
                        }
                }
            }
            // Opaque everywhere: transparent texels render black on Unlit/Texture.
            var all = sheet.GetPixels();
            for (int i = 0; i < all.Length; i++) all[i].a = 1f;
            sheet.SetPixels(all);
            sheet.Apply();
            return sheet;
        }

        // ---------------- model render + composite ----------------
        static void RenderAndCompare(MobSpec spec, Texture2D sheet, Ref r, string outPath)
        {
            var mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = sheet };
            var root = new GameObject("Replica_" + spec.name);
            try
            {
                foreach (var b in spec.boxes)
                {
                    var go = BoxBuilder.SkinnedBox(root.transform, b.id, b.pos / 16f, b.size / 16f, mat,
                        BoxBuilder.McNet(b.tu, b.tv, (int)b.size.x, (int)b.size.y, (int)b.size.z), 64, 32);
                }

                Bounds bounds = new Bounds(root.transform.position, Vector3.zero); bool first = true;
                foreach (var ren in root.GetComponentsInChildren<Renderer>())
                {
                    if (first) { bounds = ren.bounds; first = false; } else bounds.Encapsulate(ren.bounds);
                }
                Vector3 center = bounds.center;
                float height = bounds.size.y;

                var camGo = new GameObject("Cam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = r.bgColor;
                cam.orthographic = true;
                float rw = r.w, rh = r.h; // render at full reference resolution
                float aspect = (float)r.w / r.h;
                cam.aspect = aspect;
                float halfH = height * 0.5f * 1.3f + 0.02f;
                cam.orthographicSize = halfH;
                var rot = Quaternion.Euler(pitch, yaw, 0f);
                camGo.transform.position = center - rot * Vector3.forward * 10f;
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

                // ReadPixels is bottom-up; flip to top-down to match the reference.
                var flipped = new Texture2D((int)rw, (int)rh, TextureFormat.RGBA32, false);
                var sp2 = shot.GetPixels();
                var fp = new Color[sp2.Length];
                for (int y = 0; y < (int)rh; y++)
                    System.Array.Copy(sp2, y * (int)rw, fp, ((int)rh - 1 - y) * (int)rw, (int)rw);
                flipped.SetPixels(fp);
                flipped.Apply();
                Object.DestroyImmediate(shot);

                // Side-by-side: reference | ours.
                int gap = 8;
                var comb = new Texture2D(r.w + gap + (int)rw, Mathf.Max(r.h, (int)rh), TextureFormat.RGBA32, false);
                var cbg = new Color[Mathf.Max(r.h, (int)rh) * (r.w + gap + (int)rw)];
                for (int i = 0; i < cbg.Length; i++) cbg[i] = new Color(0.15f, 0.15f, 0.18f, 1f);
                comb.SetPixels(cbg);
                comb.SetPixels(0, 0, r.w, r.h, r.px);
                comb.SetPixels(r.w + gap, 0, (int)rw, (int)rh, flipped.GetPixels());
                comb.Apply();
                File.WriteAllBytes(outPath, comb.EncodeToPNG());
                Object.DestroyImmediate(flipped); Object.DestroyImmediate(comb);
                Debug.Log($"REPLICA wrote {outPath}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void SaveSheet(MobSpec spec, Texture2D sheet)
        {
            var png = sheet.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(SkinDir, $"replica_{spec.name}_skin.png.bytes"), png);
            File.WriteAllBytes(Path.Combine(OutDir, $"replica_{spec.name}_sheet.png"), png);
            Debug.Log($"REPLICA skin sheet -> replica_{spec.name}_skin.png.bytes");
        }

        // Synthetic fallback reference: flat-shaded projection of the spec
        // itself, so the pipeline can be exercised without a real screenshot.
        static void MakeSyntheticRef(MobSpec spec, string path)
        {
            int w = 600, h = 600;
            var img = new Color[w * h];
            for (int i = 0; i < img.Length; i++) img[i] = new Color(1f, 1f, 1f, 1f);
            SetCamera(w, h, new Vector3(0f, 12f, 0f));
            projScale = 13f;
            var ordered = new List<MobBox>(spec.boxes);
            ordered.Sort((a, b2) => Depth(b2.pos).CompareTo(Depth(a.pos))); // far first
            foreach (var b in ordered)
                foreach (var f in FacesOf(b))
                {
                    if (Vector3.Dot(f.n, camPos - f.origin) <= 0.02f) continue;
                    float shade = f.n.y > 0.5f ? 1f : f.n.z > 0.5f ? 0.86f : 0.72f;
                    Color col = new Color(0.85f * shade, 0.83f * shade, 0.8f * shade, 1f);
                    if (b.id.StartsWith("beak") || b.id.StartsWith("leg") || b.id.StartsWith("foot")) col = new Color(0.87f * shade, 0.55f * shade, 0.3f * shade, 1f);
                    if (b.id.StartsWith("wattle")) col = new Color(0.7f * shade, 0.15f * shade, 0.12f * shade, 1f);
                    if (b.id.StartsWith("snout")) col = new Color(0.88f * shade, 0.6f * shade, 0.55f * shade, 1f);
                    int tw = (int)f.rect.width * 8, th = (int)f.rect.height * 8;
                    for (int ty = 0; ty < th; ty++)
                        for (int tx = 0; tx < tw; tx++)
                        {
                            var p = TexelPoint(f, b.size, tx, ty, tw, th);
                            var sp2 = Project(p);
                            for (int ox = 0; ox < 3; ox++)
                                for (int oy = 0; oy < 3; oy++)
                                {
                                    int ix = (int)sp2.x + ox, iy = (int)sp2.y + oy;
                                    if (ix < 0 || ix >= w || iy < 0 || iy >= h) continue;
                                    img[iy * w + ix] = col;
                                }
                        }
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
            RunOne(SpecChicken()); RunOne(SpecPig()); RunOne(SpecSheep());
            Debug.Log("REPLICA EXPERIMENT DONE");
        }

        static void RunOne(MobSpec spec)
        {
            string refPath = Path.Combine(RefDir, spec.name + ".png");
            if (!File.Exists(refPath)) MakeSyntheticRef(spec, refPath);
            var r = LoadReference(refPath);
            CalibrateCamera(spec, r);
            var sheet = PaintSkin(spec, r);
            SaveSheet(spec, sheet);
            RenderAndCompare(spec, sheet, r, Path.Combine(OutDir, $"replica_{spec.name}.png"));
        }

        // ---------------- window ----------------
        [MenuItem("Tools/VoxelCraft/Mob Replicator")]
        static void Open() => GetWindow<MobReplicator>("Mob Replicator");

        void OnGUI()
        {
            EditorGUILayout.HelpBox("Put reference screenshots in D:\\zlj world\\_refs as chicken.png / pig.png / sheep.png, then Run Experiment. Missing files fall back to synthetic stand-ins. Output: _logs\\replica_<mob>.png", MessageType.Info);
            yaw = EditorGUILayout.Slider("Camera yaw", yaw, 0f, 360f);
            pitch = EditorGUILayout.Slider("Camera pitch", pitch, 0f, 80f);
            if (GUILayout.Button("Run Experiment")) RunExperiment();
            if (GUILayout.Button("Chicken only")) RunOne(SpecChicken());
            if (GUILayout.Button("Pig only")) RunOne(SpecPig());
            if (GUILayout.Button("Sheep only")) RunOne(SpecSheep());
        }
    }
}
