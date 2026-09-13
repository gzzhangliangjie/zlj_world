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

        static float yaw = 135f, pitch = 30f; // MC wiki renders face LEFT -> mirrored vs yaw 225

        // ---------------- spec model ----------------
        public class MobBox
        {
            public string id; public string role; public Vector3 size; public Vector3 pos; public int tu, tv;
            public MobBox(string id, string role, float sx, float sy, float sz, float x, float y, float z, int tu, int tv)
            {
                this.id = id; this.role = role; size = new Vector3(sx, sy, sz); pos = new Vector3(x, y, z); this.tu = tu; this.tv = tv;
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
            s.boxes.Add(new MobBox("body", "body", 6, 6, 8, 0, 8, 0, 0, 8));
            s.boxes.Add(new MobBox("head", "head", 4, 6, 3, 0, 13.4f, 5.35f, 28, 0));
            s.boxes.Add(new MobBox("beak", "beak", 4, 3, 1, 0, 12.3f, 7.05f, 28, 9));
            s.boxes.Add(new MobBox("wattle", "wattle", 2, 2, 2, 0, 9.5f, 6.7f, 40, 9));
            s.boxes.Add(new MobBox("wing0", "wing", 1, 4, 6, -3.52f, 9, 0, 46, 0));
            s.boxes.Add(new MobBox("wing1", "wing", 1, 4, 6, 3.52f, 9, 0, 46, 0));
            s.boxes.Add(new MobBox("leg0", "leg", 2, 5, 2, -1, 2.69f, 0, 50, 16));
            s.boxes.Add(new MobBox("leg1", "leg", 2, 5, 2, 1, 2.69f, 0, 50, 16));
            s.boxes.Add(new MobBox("foot0", "foot", 3, 1, 2, -1, 0.5f, 1.5f, 52, 26));
            s.boxes.Add(new MobBox("foot1", "foot", 3, 1, 2, 1, 0.5f, 1.5f, 52, 26));
            return s;
        }

        public static MobSpec SpecPig()
        {
            var s = new MobSpec("pig");
            s.boxes.Add(new MobBox("body", "body", 10, 8, 16, 0, 10, 0, 0, 8));
            s.boxes.Add(new MobBox("head", "head", 8, 8, 8, 0, 17.2f, 9.8f, 32, 0));
            s.boxes.Add(new MobBox("snout", "snout", 8, 4, 1, 0, 15.2f, 11.9f, 0, 0));
            s.boxes.Add(new MobBox("leg0", "leg", 4, 6, 4, -3, 3.2f, 5, 40, 0));
            s.boxes.Add(new MobBox("leg1", "leg", 4, 6, 4, 3, 3.2f, 5, 40, 0));
            s.boxes.Add(new MobBox("leg2", "leg", 4, 6, 4, -3, 3.2f, -5, 40, 0));
            s.boxes.Add(new MobBox("leg3", "leg", 4, 6, 4, 3, 3.2f, -5, 40, 0));
            return s;
        }

        public static MobSpec SpecSheep()
        {
            var s = new MobSpec("sheep");
            s.boxes.Add(new MobBox("body", "body", 8, 6, 16, 0, 15, 0, 0, 8));
            s.boxes.Add(new MobBox("head", "head", 6, 6, 6, 0, 20.4f, 10.7f, 32, 0));
            s.boxes.Add(new MobBox("leg0", "leg", 4, 12, 4, -2.5f, 6.2f, 5, 0, 0));
            s.boxes.Add(new MobBox("leg1", "leg", 4, 12, 4, 2.5f, 6.2f, 5, 0, 0));
            s.boxes.Add(new MobBox("leg2", "leg", 4, 12, 4, -2.5f, 6.2f, -5, 0, 0));
            s.boxes.Add(new MobBox("leg3", "leg", 4, 12, 4, 2.5f, 6.2f, -5, 0, 0));
            return s;
        }

        // ---------------- face layout ----------------
        struct Face { public Rect rect; public Vector3 n, origin, uAxis, vAxis; public float uw, vh; }

        static IEnumerable<Face> FacesOf(MobBox b)
        {
            // Same net layout as BoxBuilder.McNet(u, v, W, H, D). uw/vh are the
            // face's WORLD extents along uAxis/vAxis (px units).
            int u = b.tu, v = b.tv; float W = b.size.x, H = b.size.y, D = b.size.z;
            Vector3 c = b.pos, h = b.size * 0.5f;
            yield return new Face { rect = new Rect(u, v + D, D, H), n = new Vector3(1, 0, 0), origin = c + new Vector3(h.x, h.y, h.z), uAxis = new Vector3(0, 0, -1), vAxis = new Vector3(0, -1, 0), uw = D, vh = H };
            yield return new Face { rect = new Rect(u + D + W, v + D, D, H), n = new Vector3(-1, 0, 0), origin = c + new Vector3(-h.x, h.y, -h.z), uAxis = new Vector3(0, 0, 1), vAxis = new Vector3(0, -1, 0), uw = D, vh = H };
            yield return new Face { rect = new Rect(u + D, v, W, D), n = new Vector3(0, 1, 0), origin = c + new Vector3(-h.x, h.y, h.z), uAxis = new Vector3(1, 0, 0), vAxis = new Vector3(0, 0, -1), uw = W, vh = D };
            yield return new Face { rect = new Rect(u + D + W, v, W, D), n = new Vector3(0, -1, 0), origin = c + new Vector3(-h.x, -h.y, -h.z), uAxis = new Vector3(1, 0, 0), vAxis = new Vector3(0, 0, 1), uw = W, vh = D };
            yield return new Face { rect = new Rect(u + D, v + D, W, H), n = new Vector3(0, 0, 1), origin = c + new Vector3(-h.x, h.y, h.z), uAxis = new Vector3(1, 0, 0), vAxis = new Vector3(0, -1, 0), uw = W, vh = H };
            yield return new Face { rect = new Rect(u + 2 * D + W, v + D, W, H), n = new Vector3(0, 0, -1), origin = c + new Vector3(h.x, h.y, -h.z), uAxis = new Vector3(-1, 0, 0), vAxis = new Vector3(0, -1, 0), uw = W, vh = H };
        }

        static Vector3 TexelPoint(Face f, int tx, int ty, int tw, int th)
        {
            float a = (tx + 0.5f) / tw, b = (ty + 0.5f) / th;
            return f.origin + f.uAxis * (a * f.uw) + f.vAxis * (b * f.vh);
        }

        // ---------------- projection ----------------
        static Quaternion camRot; static Vector3 camPos; static Vector2 projCenter; static float projScale = 1f;

        static void SetCamera(float rw, float rh, Vector3 target)
        {
            camRot = Quaternion.Euler(pitch, yaw, 0f);
            camPos = target - camRot * Vector3.forward * 500f;
            projCenter = new Vector2(rw * 0.5f, rh * 0.5f);
            projScale = 1f; // reset, or CalibrateCamera compounds the stale scale
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
            Debug.Log($"REPLICA calibrate: det=({maxX - minX + 1}x{maxY - minY + 1}) proj=({pmax.x - pmin.x:F1}x{pmax.y - pmin.y:F1}) scale={projScale:F2} center=({projCenter.x:F0},{projCenter.y:F0})");
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
        // px/bg are as Unity loads them (row 0 = BOTTOM); pxTop/bgTop are
        // TOP-DOWN copies used by the projector, sampler and silhouette fit,
        // which all work in image coordinates (y from the top).
        class Ref { public Color[] px, pxTop; public bool[] bg, bgTop; public int w, h; public Color bgColor; }

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
            float tol = 0.08f; // per channel; survives gradients/AA halos
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (i < 0 || i >= r.px.Length || r.bg[i]) continue;
                var c = r.px[i];
                bool isBg = c.a < 0.1f ||
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
            // Build top-down copies for projection-space sampling.
            r.pxTop = new Color[r.w * r.h];
            r.bgTop = new bool[r.w * r.h];
            for (int y = 0; y < r.h; y++)
            {
                System.Array.Copy(r.px, (r.h - 1 - y) * r.w, r.pxTop, y * r.w, r.w);
                System.Array.Copy(r.bg, (r.h - 1 - y) * r.w, r.bgTop, y * r.w, r.w);
            }
            return r;
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
                            var p = TexelPoint(f, tx, ty, tw, th);
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
                                var p = TexelPoint(f, tx, ty, tw, th);
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
                            var p = TexelPoint(f, tx, ty, tw, th);
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

        // ---------------- silhouette fitting ----------------
        /// <summary>Rasterizes the spec's camera-facing faces into a mask.</summary>
        static bool[] RasterizeMask(MobSpec spec, int w, int h)
        {
            var m = new bool[w * h];
            foreach (var b in spec.boxes)
                foreach (var f in FacesOf(b))
                {
                    if (Vector3.Dot(f.n, camPos - f.origin) <= 0.02f) continue;
                    // Sample density adapts to the current projection scale so
                    // the quad fills SOLID (2px spacing, 2x2 stamps).
                    int tw = Mathf.Max(1, (int)(f.uw * projScale / 2f));
                    int th = Mathf.Max(1, (int)(f.vh * projScale / 2f));
                    for (int ty = 0; ty < th; ty++)
                        for (int tx = 0; tx < tw; tx++)
                        {
                            var p = TexelPoint(f, tx, ty, tw, th);
                            var sp = Project(p);
                            for (int ox = 0; ox < 2; ox++)
                                for (int oy = 0; oy < 2; oy++)
                                {
                                    int ix = (int)sp.x + ox, iy = (int)sp.y + oy;
                                    if (ix >= 0 && ix < w && iy >= 0 && iy < h) m[iy * w + ix] = true;
                                }
                        }
                }
            return m;
        }

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

        /// <summary>
        /// Re-lays out a spec from 8 fitted parameters: body size, head size,
        /// leg height, leg thickness. Dependent parts (head/legs/wings/beak/
        /// wattle/snout/feet) follow their parent part by vanilla-style rules,
        /// so one silhouette drives the whole skeleton.
        /// </summary>
        static void LayoutSpec(MobSpec spec, float[] p)
        {
            MobBox body = null, head = null;
            float legH = p[6], legT = p[7];
            foreach (var b in spec.boxes)
            {
                if (b.role == "body") { body = b; body.size = new Vector3(p[0], p[1], p[2]); }
                if (b.role == "head") { head = b; head.size = new Vector3(p[3], p[4], p[5]); }
                if (b.role == "leg") b.size = new Vector3(legT, b.size.y, legT);
            }
            if (body == null) return;
            body.pos = new Vector3(0f, legH + body.size.y * 0.5f, 0f);
            if (head != null)
                head.pos = new Vector3(0f, legH + body.size.y + head.size.y * 0.4f,
                    body.size.z * 0.5f + head.size.z * 0.45f);
            foreach (var b in spec.boxes)
            {
                if (b.role == "leg") b.pos = new Vector3(b.pos.x, legH - b.size.y * 0.5f + 0.19f, b.pos.z);
                if (b.role == "wing") b.pos = new Vector3(Mathf.Sign(b.pos.x) * (body.size.x * 0.5f + 0.52f), legH + body.size.y - 2f, 0f);
                if (b.role == "beak") b.pos = head.pos + new Vector3(0f, -head.size.y * 0.18f, head.size.z * 0.5f + 0.21f);
                if (b.role == "wattle") b.pos = head.pos + new Vector3(0f, -head.size.y * 0.5f - 0.88f, head.size.z * 0.5f - 0.16f);
                if (b.role == "snout") b.pos = head.pos + new Vector3(0f, -head.size.y * 0.25f, head.size.z * 0.5f + 0.22f);
                if (b.role == "foot") b.pos = new Vector3(b.pos.x, 0.5f, b.pos.z);
            }
        }

        static float[] ParamsOf(MobSpec spec)
        {
            MobBox body = null, head = null; float legH = 5f, legT = 2f; bool hasLeg = false;
            foreach (var b in spec.boxes)
            {
                if (b.role == "body") body = b;
                if (b.role == "head") head = b;
                if (b.role == "leg" && !hasLeg) { legH = b.pos.y + b.size.y * 0.5f - 0.19f; legT = b.size.x; hasLeg = true; }
            }
            if (body == null || head == null) return null;
            return new float[] { body.size.x, body.size.y, body.size.z, head.size.x, head.size.y, head.size.z, legH, legT };
        }

        static MobSpec FitSpecToSilhouette(MobSpec preset, Ref r)
        {
            var spec = new MobSpec(preset.name);
            foreach (var b in preset.boxes)
                spec.boxes.Add(new MobBox(b.id, b.role, b.size.x, b.size.y, b.size.z, b.pos.x, b.pos.y, b.pos.z, b.tu, b.tv));
            float[] start = ParamsOf(spec);
            if (start == null) return spec;

            var mask = new bool[r.w * r.h];
            for (int i = 0; i < mask.Length; i++) mask[i] = !r.bgTop[i];

            System.Func<float[], float> score = delegate (float[] trial)
            {
                LayoutSpec(spec, trial);
                CalibrateCamera(spec, r);
                return Iou(RasterizeMask(spec, r.w, r.h), mask);
            };

            // Single-view silhouettes are front/back ambiguous: try the preset
            // facing and its 180-degree twin, keep whichever fits better.
            float baseYaw = yaw;
            float[] bestP = null; float best = -1f; float bestYaw = baseYaw;
            foreach (float cy in new[] { baseYaw, baseYaw + 180f })
            {
                yaw = cy;
                float[] p = (float[])start.Clone();
                float b0 = score(p);
                float step = 2f;
                for (int sweep = 0; sweep < 3; sweep++)
                {
                    for (int i = 0; i < p.Length; i++)
                    {
                        foreach (float d in new[] { step, -step, step * 0.5f, -step * 0.5f })
                        {
                            var trial = (float[])p.Clone();
                            trial[i] = Mathf.Max(1f, trial[i] + d);
                        // Vanilla prior: keep dims within 0.5x-2x of the preset
                        trial[i] = Mathf.Clamp(trial[i], start[i] * 0.5f, start[i] * 2f);
                            float s = score(trial);
                            if (s > b0 + 0.0005f) { b0 = s; p = trial; }
                        }
                    }
                    step *= 0.5f;
                }
                if (b0 > best) { best = b0; bestP = p; bestYaw = cy; }
            }

            yaw = bestYaw;
            LayoutSpec(spec, bestP);
            CalibrateCamera(spec, r);
            // Snap to whole texture pixels; keep if it does not hurt the fit.
            var snapped = (float[])bestP.Clone();
            for (int i = 0; i < snapped.Length; i++) snapped[i] = Mathf.Max(1f, Mathf.Round(snapped[i]));
            float sSnap = score(snapped);
            if (sSnap >= best - 0.01f) { LayoutSpec(spec, snapped); best = sSnap; bestP = snapped; }
            Debug.Log($"REPLICA fit '{spec.name}': yaw={yaw:F0} IoU={best:F3} body=({bestP[0]:F0},{bestP[1]:F0},{bestP[2]:F0}) head=({bestP[3]:F0},{bestP[4]:F0},{bestP[5]:F0}) legH={bestP[6]:F0} legT={bestP[7]:F0}");
            DumpFit(spec, r);
            return spec;
        }

        /// <summary>Writes reference/model/overlay masks for fit diagnosis.</summary>
        static void DumpFit(MobSpec spec, Ref r)
        {
            var model = RasterizeMask(spec, r.w, r.h);
            int filled = 0; foreach (var b in model) if (b) filled++;
            Debug.Log($"REPLICA rasterize '{spec.name}': {filled}px of {r.w * r.h}");
            var tex = new Texture2D(r.w, r.h, TextureFormat.RGBA32, false);
            var px = new Color[r.w * r.h];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color(r.bgTop[i] ? 0f : 1f, model[i] ? 1f : 0f, 0f, 1f);
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, $"fit_{spec.name}_overlay.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
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
            spec = FitSpecToSilhouette(spec, r);
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


