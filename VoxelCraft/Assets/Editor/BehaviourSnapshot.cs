using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Snapshots each species in its AMBIENT BEHAVIOUR pose
    /// (wolf sitting/shaking, sheep grazing, fox sit/sleep) for visual
    /// review. Batch-safe: drives BehaviourBrain.Tick + player.Tick
    /// manually, then renders orthographic side views.</summary>
    public static class BehaviourSnapshot
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

            foreach (var sp in new[] { "wolf", "sheep", "fox" })
            {
                var go = new GameObject("bs_" + sp);
                try
                {
                    var ani = go.AddComponent<BlockyAnimal>();
                    ani.species = sp;
                    ani.BuildModel();
                    var brain = go.GetComponent<BehaviourBrain>();
                    var player = go.GetComponent<BedrockAnimationPlayer>();
                    ani.walking = false;

                    // Force-start the FIRST behaviour deterministically.
                    int guard = 0;
                    while (!brain.InBehaviour && guard++ < 400)
                    { brain.Tick(1f / 60f); player.Tick(1f / 60f); }
                    for (int i = 0; i < 300; i++) { brain.Tick(1f / 60f); player.Tick(1f / 60f); }

                    string clip = brain.ActiveClip;
                    string label = clip.Replace("animation." + sp + ".", "");
                    // settle the absolute clip fully (no blend residue)
                    for (int i = 0; i < 120; i++) { brain.Tick(1f / 60f); player.Tick(1f / 60f); }

                    // Bounds-aware framing: sitting/sleeping poses change the
                    // silhouette drastically; frame the actual model bounds.
                    var rends = go.GetComponentsInChildren<Renderer>();
                    bool hasRend = rends != null && rends.Length > 0;
                    if (!hasRend) { Debug.Log($"[BehaviourSnapshot] {sp}: NO RENDERERS, skip"); continue; }
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    Vector3 center = b.center;
                    float radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
                    float dist = radius / Mathf.Tan(Mathf.Deg2Rad * cam.fieldOfView * 0.5f) * 1.35f;
                    camGo.transform.position = center + new Vector3(dist, 0f, 0f);
                    camGo.transform.LookAt(center, Vector3.up);
                    var rt = new RenderTexture(700, 700, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    cam.Render(); cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(700, 700, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0);
                    tex.Apply();
                    string path = System.IO.Path.Combine(dir, $"snapshot_{sp}_behaviour_{label}.png");
                    System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                    Debug.Log($"[BehaviourSnapshot] {sp}: {clip} -> {path}");
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    rt.Release();
                    UnityEngine.Object.Destroy(tex);
                }
                finally { Object.DestroyImmediate(go); }
            }
            Object.DestroyImmediate(camGo);
            Debug.Log("[BehaviourSnapshot] RESULT: DONE");
        }
    }
}
