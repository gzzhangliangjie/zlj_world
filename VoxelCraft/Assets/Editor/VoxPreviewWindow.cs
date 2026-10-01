using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Vox;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Static .vox model viewer (M36). Window/VoxelCraft/Vox Preview.
    /// Left: structure list (Resources/VoxStructures) + external .vox picker.
    /// Right: in-window orbit preview. Two modes: quantized blocks (exactly
    /// what the world generator stamps) and true palette colors (the raw
    /// MagicaVoxel art, pre-quantisation).
    /// </summary>
    public class VoxPreviewWindow : EditorWindow
    {
        [MenuItem("Window/VoxelCraft/Vox Preview")]
        static void Open() => GetWindow<VoxPreviewWindow>("Vox Preview");

        // stage (same convention as AnimPreviewWindow: y=-500 away from scene)
        GameObject stage;
        Mesh mesh;
        Material blockMat;
        TextureFactory.AtlasResult atlas;
        Rect[] tileRects;

        // in-window camera
        Camera cam;
        RenderTexture rt;

        string cur = "";
        bool trueColor;
        bool autoRot = true;
        float yaw = 35f, pitch = 22f, dist = 24f;
        string stats = "pick a structure";
        Vector2 listScroll;

        static readonly Vector3Int[] Normals =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };
        static readonly Vector3Int[][] Corners =
        {
            new[] { new Vector3Int(1,0,1), new Vector3Int(1,0,0), new Vector3Int(1,1,0), new Vector3Int(1,1,1) }, // +X
            new[] { new Vector3Int(0,0,0), new Vector3Int(0,0,1), new Vector3Int(0,1,1), new Vector3Int(0,1,0) }, // -X
            new[] { new Vector3Int(0,1,1), new Vector3Int(1,1,1), new Vector3Int(1,1,0), new Vector3Int(0,1,0) }, // +Y
            new[] { new Vector3Int(0,0,0), new Vector3Int(1,0,0), new Vector3Int(1,0,1), new Vector3Int(0,0,1) }, // -Y
            new[] { new Vector3Int(0,0,1), new Vector3Int(1,0,1), new Vector3Int(1,1,1), new Vector3Int(0,1,1) }, // +Z
            new[] { new Vector3Int(1,0,0), new Vector3Int(0,0,0), new Vector3Int(0,1,0), new Vector3Int(1,1,0) }, // -Z
        };
        static readonly float[] Shade = { 0.80f, 0.80f, 1.00f, 0.55f, 0.70f, 0.70f };
        static readonly Vector2[] CornerUv = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };

        void OnEnable() { EnsureAtlas(); autoRot = true; }
        void OnDisable()
        {
            if (stage != null) DestroyImmediate(stage);
            if (rt != null) { rt.Release(); DestroyImmediate(rt); }
            if (cam != null) DestroyImmediate(cam.gameObject);
            if (mesh != null) DestroyImmediate(mesh);
        }

        void EnsureAtlas()
        {
            if (atlas != null) return;
            atlas = TextureFactory.Build();
            var count = TextureFactory.AtlasCols * TextureFactory.AtlasRows;
            tileRects = new Rect[count];
            for (int i = 0; i < count; i++) tileRects[i] = atlas.TileRect((TileId)i);
            blockMat = new Material(Resources.Load<Shader>("Shaders/BlocksShader")) { mainTexture = atlas.atlas };
        }

        /// <summary>Probe access to the structure list.</summary>
        public static string[] ListStructuresPublic() => ListStructures();

        static string[] ListStructures()
        {
            var dir = Path.Combine(Application.dataPath, "Resources/VoxStructures");
            if (!Directory.Exists(dir)) return new string[0];
            return Directory.GetFiles(dir, "*.bytes")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(s => s).ToArray();
        }

        void Load(string name, byte[] data)
        {
            cur = name;
            VoxStructure vox;
            try { vox = VoxStructure.Parse(data); }
            catch (System.Exception ex) { stats = "parse failed: " + ex.Message; return; }

            EnsureAtlas();
            var hist = new Dictionary<BlockType, int>();
            mesh = BuildPreviewMesh(vox, trueColor, tileRects, hist);
            var sb = new StringBuilder();
            sb.AppendLine($"{name}: {vox.Width}x{vox.Height}x{vox.Depth} voxels");
            int solid = hist.Values.Sum();
            sb.Append($"solid {solid} blocks, {hist.Count} block types | top: ");
            sb.Append(string.Join(", ", hist.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"{BlockDatabase.Get(kv.Key).name} x{kv.Value}")));
            stats = sb.ToString();

            if (stage != null) DestroyImmediate(stage);
            stage = new GameObject("VoxPreviewStage") { hideFlags = HideFlags.HideAndDontSave };
            stage.transform.position = new Vector3(0f, -500f, 0f);
            var view = new GameObject("VoxPreviewModel") { hideFlags = HideFlags.HideAndDontSave };
            view.transform.SetParent(stage.transform, false);
            var mf = view.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = view.AddComponent<MeshRenderer>();
            mr.sharedMaterial = trueColor ? PaletteMaterial(vox) : blockMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // frame: pivot center-bottom, camera distance from bounding sphere
            var maxDim = Mathf.Max(vox.Width, vox.Height, vox.Depth);
            dist = Mathf.Max(4f, maxDim * 1.7f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(10000f, 20000f, 0f, 0f)); // disable fog at the stage
        }

        static Material palMatCache;
        static Material PaletteMaterial(VoxStructure vox)
        {
            var tex = new Texture2D(256, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[256];
            for (int i = 0; i < vox.palette.Length && i < 256; i++) px[i] = vox.palette[i];
            tex.SetPixels32(px); tex.Apply(false, false);
            if (palMatCache == null)
                palMatCache = new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex };
            palMatCache.mainTexture = tex;
            return palMatCache;
        }

        /// <summary>Greedy-less cube mesh for the preview (1 unit = 1 block).
        /// Also used by the batch self-check probe.</summary>
        public static Mesh BuildPreviewMesh(VoxStructure vox, bool raw, Rect[] rects,
            Dictionary<BlockType, int> histOut = null)
        {
            if (rects == null) { rects = new Rect[64]; for (int i = 0; i < 64; i++) rects[i] = new Rect(0f, 0f, 1f, 1f); }
            var verts = new List<Vector3>(4096);
            var uvs = new List<Vector2>(4096);
            var cols = new List<Color32>(4096);
            var idx = new List<int>(6144);
            bool Solid(int x, int y, int z)
            {
                if (x < 0 || y < 0 || z < 0 || x >= vox.Width || y >= vox.Height || z >= vox.Depth) return false;
                return raw ? vox.indices[(y * vox.Depth + z) * vox.Width + x] != 0
                           : vox.blocks[x, y, z] != BlockType.Air;
            }
            for (int y = 0; y < vox.Height; y++)
                for (int z = 0; z < vox.Depth; z++)
                    for (int x = 0; x < vox.Width; x++)
                    {
                        byte ci = vox.indices[(y * vox.Depth + z) * vox.Width + x];
                        var type = vox.blocks[x, y, z];
                        if (ci == 0) continue;
                        if (histOut != null && !raw)
                        {
                            histOut.TryGetValue(type, out int c);
                            histOut[type] = c + 1;
                        }
                        for (int f = 0; f < 6; f++)
                        {
                            var n = Normals[f];
                            if (Solid(x + n.x, y + n.y, z + n.z)) continue;
                            var rect = raw
                                ? new Rect((ci - 0.5f) / 256f, 0f, 0.9f / 256f, 0.9f)
                                : rects[(int)BlockDatabase.Get(type).FaceTile(n)];
                            int b = verts.Count;
                            for (int c = 0; c < 4; c++)
                            {
                                var corner = Corners[f][c];
                                verts.Add(new Vector3(x + corner.x, y + corner.y, z + corner.z));
                                uvs.Add(new Vector2(rect.x + CornerUv[c].x * rect.width,
                                                    rect.y + CornerUv[c].y * rect.height));
                                byte l = (byte)(Shade[f] * 255f);
                                cols.Add(new Color32(l, l, l, 255));
                            }
                            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
                            idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
                        }
                    }
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetColors(cols); m.SetIndices(idx, MeshTopology.Triangles, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        void OnGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(280));
            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(300));
            foreach (var s in ListStructures())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(s, GUILayout.Width(190));
                bool sel = s == cur;
                if (GUILayout.Button(sel ? "shown" : "view", GUILayout.Width(56)) && !sel)
                    Load(s, File.ReadAllBytes(Path.Combine(Application.dataPath, "Resources/VoxStructures", s + ".bytes")));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Open external .vox..."))
            {
                var path = EditorUtility.OpenFilePanel("Pick a .vox", Application.dataPath, "vox");
                if (!string.IsNullOrEmpty(path))
                    Load(Path.GetFileNameWithoutExtension(path), File.ReadAllBytes(path));
            }
            var nextTrue = GUILayout.Toggle(trueColor, "true palette colors (raw art)");
            if (nextTrue != trueColor && !string.IsNullOrEmpty(cur))
            {
                trueColor = nextTrue;
                var p = FindOnDisk(cur);
                if (p != null) Load(cur, File.ReadAllBytes(p));
            }
            autoRot = GUILayout.Toggle(autoRot, "auto-rotate");
            if (GUILayout.Button("reset view")) { yaw = 35f; pitch = 22f; }
            GUILayout.Space(6);
            EditorGUILayout.HelpBox(stats, MessageType.None);
            GUILayout.EndVertical();

            // preview viewport
            var r = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var e = Event.current;
            if (r.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDrag)
                {
                    yaw -= e.delta.x * 0.6f;
                    pitch = Mathf.Clamp(pitch + e.delta.y * 0.6f, -89f, 89f);
                    e.Use(); Repaint();
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    dist *= 1f + Mathf.Sign(e.delta.y) * 0.12f;
                    dist = Mathf.Clamp(dist, 2f, 400f);
                    e.Use(); Repaint();
                }
            }
            if (e.type == EventType.Repaint && stage != null)
            {
                if (autoRot) { yaw += 0.4f; }
                DrawPreview(r);
            }
            else if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, new Color(0.16f, 0.17f, 0.19f));
                GUI.Label(r, "no structure loaded", EditorStyles.centeredGreyMiniLabel);
            }
            GUILayout.EndHorizontal();
        }

        static string FindOnDisk(string name)
        {
            var p = Path.Combine(Application.dataPath, "Resources/VoxStructures", name + ".bytes");
            return File.Exists(p) ? p : null;
        }

        void DrawPreview(Rect r)
        {
            int w = Mathf.Max(64, (int)r.width), h = Mathf.Max(64, (int)r.height);
            if (rt == null || rt.width != w || rt.height != h)
            {
                if (rt != null) { rt.Release(); DestroyImmediate(rt); }
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            }
            if (cam == null)
            {
                var camGo = new GameObject("VoxPreviewCam") { hideFlags = HideFlags.HideAndDontSave };
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.35f, 0.47f, 0.60f);
                cam.nearClipPlane = 0.1f; cam.farClipPlane = 2000f;
                cam.enabled = false;
            }
            var center = new Vector3(0f, -500f + mesh.bounds.size.y * 0.5f, 0f);
            float rad = Mathf.Max(mesh.bounds.size.magnitude * 0.5f, 0.5f);
            var dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            cam.transform.position = center + dir * Mathf.Max(dist, rad * 1.2f);
            cam.transform.LookAt(center);
            cam.fieldOfView = 45f;
            cam.targetTexture = rt;
            cam.Render();
            Graphics.DrawTexture(r, rt);
        }
    }
}
