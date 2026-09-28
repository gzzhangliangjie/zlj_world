using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace VoxelCraft.Editor
{
    /// <summary>All-26-species front sheet (walk pose frame, yaw 180 so
    /// faces show) - full-delivery confirmation grid. PNGs to
    /// _logs/allfront for PIL contact-sheet assembly.</summary>
    public static class AllFrontSheet
    {
        static readonly string[] All = {
            "pig", "cow", "sheep", "mooshroom", "chicken", "wolf", "fox",
            "goat", "horse", "donkey", "rabbit", "panda", "armadillo",
            "ocelot", "creeper", "hoglin", "polar_bear", "llama", "bee",
            "spider", "parrot", "bat", "steve", "zombie", "skeleton",
            "villager",
        };

        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));
            var camGo = new GameObject("AFS_cam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            string outDir = Path.Combine(Application.dataPath, "../../_logs/allfront");
            Directory.CreateDirectory(outDir);
            int done = 0;
            try
            {
                foreach (string sp in All)
                {
                    float yaw = sp == "bee" ? -144f : 180f;
                    var go = new GameObject("AFS_" + sp);
                    var ani = go.AddComponent<Creatures.BlockyAnimal>();
                    ani.species = sp;
                    ani.BuildModel();
                    var player = go.GetComponent<Creatures.BedrockAnimationPlayer>();
                    ani.walking = true;
                    float dt = 1f / 30f;
                    for (int i = 0; i < 20; i++)
                    {
                        go.transform.position += Vector3.forward * (dt * 1.5f);
                        if (player != null) player.Tick(dt);
                        // controller-driven species need the vanilla state
                        // machine advanced manually in batch mode (GIF
                        // pipeline's DriveBlockyTick lesson): without it the
                        // walk clip is never scheduled (frozen legs - chicken
                        // showed as one solid yellow column).
                        ani.TickControllers(dt, ani.walking);
                    }
                    var rends = go.GetComponentsInChildren<Renderer>();
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                    float dist = maxDim * 2.2f + 0.5f;
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * new Vector3(1.6f, 0.35f, -2.2f).normalized;
                    cam.transform.position = b.center + dir * dist;
                    cam.transform.LookAt(b.center);
                    cam.orthographicSize = maxDim * 0.62f;
                    var rt = new RenderTexture(360, 360, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    cam.Render(); cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(360, 360, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 360, 360), 0, 0);
                    tex.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(outDir, sp + "_front.png"), tex.EncodeToPNG());
                    done++;
                    Debug.Log($"[AFS] {sp} written");
                    cam.targetTexture = null;
                    rt.Release();
                    Object.DestroyImmediate(tex);
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(go);
                }
            }
            finally { Object.DestroyImmediate(camGo); }
            Debug.Log($"[AFS] RESULT: {done}/{All.Length} species front frames -> {outDir}");
        }
    }
}
