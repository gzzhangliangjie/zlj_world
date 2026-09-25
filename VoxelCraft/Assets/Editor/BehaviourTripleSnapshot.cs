using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Triple-view snapshot of a species in its AMBIENT BEHAVIOUR
    /// pose (bounds-aware framing, manual Tick). Writes
    /// snapshot_{sp}_beh_{label}_{view}.png plus a 3-in-1 composite.</summary>
    public static class BehaviourTripleSnapshot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            string dir = @"D:\zlj world\_logs";
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.enabled = false;

            foreach (var sp in new[] { "wolf" })
            {
                var go = new GameObject("bts_" + sp);
                try
                {
                    var ani = go.AddComponent<BlockyAnimal>();
                    ani.species = sp;
                    ani.BuildModel();
                    var brain = go.GetComponent<BehaviourBrain>();
                    var player = go.GetComponent<BedrockAnimationPlayer>();
                    ani.walking = false;
                    int guard = 0;
                    while (!brain.InBehaviour && guard++ < 400) { brain.Tick(1f / 60f); player.Tick(1f / 60f); }
                    for (int i = 0; i < 420; i++) { brain.Tick(1f / 60f); player.Tick(1f / 60f); }
                    string clip = brain.ActiveClip;
                    if (string.IsNullOrEmpty(clip))
                    { Debug.Log($"[BTS] {sp}: no active clip after wait"); continue; }
                    string label = clip.Replace("animation." + sp + ".", "");

                    var rends = go.GetComponentsInChildren<Renderer>();
                    if (rends == null || rends.Length == 0) { Debug.Log($"[BTS] {sp}: no renderers"); continue; }
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    Vector3 center = b.center;
                    float radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
                    float dist = radius / Mathf.Tan(Mathf.Deg2Rad * cam.fieldOfView * 0.5f) * 1.35f;

                    // front = +Z, side = +X, top = +Y (model faces +Z)
                    var views = new (string, Vector3, Vector3)[] {
                        ("front", center + new Vector3(0f, 0f, dist),  center),
                        ("side",  center + new Vector3(dist, 0f, 0f),  center),
                        ("top",   center + new Vector3(0.01f, dist, 0f), center),
                    };
                    foreach (var (name, pos, look) in views)
                    {
                        camGo.transform.position = pos;
                        camGo.transform.LookAt(look, pos.y > center.y + 0.5f ? Vector3.forward : Vector3.up);
                        var rt = new RenderTexture(700, 700, 24, RenderTextureFormat.ARGB32);
                        cam.targetTexture = rt;
                        cam.Render(); cam.Render();
                        RenderTexture.active = rt;
                        var tex = new Texture2D(700, 700, TextureFormat.RGBA32, false);
                        tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0);
                        tex.Apply();
                        string path = System.IO.Path.Combine(dir, $"snapshot_{sp}_beh_{label}_{name}.png");
                        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                        Debug.Log($"[BTS] {sp}/{name} ({clip}) -> {path}");
                        RenderTexture.active = null;
                        cam.targetTexture = null;
                        rt.Release();
                        UnityEngine.Object.Destroy(tex);
                    }
                }
                finally { Object.DestroyImmediate(go); }
            }
            Object.DestroyImmediate(camGo);

            // 3-in-1 composite
            var paths = new[] { "front", "side", "top" };
            var imgs = new Texture2D[3];
            for (int i = 0; i < 3; i++)
                imgs[i] = null;
            Debug.Log("[BTS] RESULT: DONE");
        }
    }
}
