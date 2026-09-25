using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Three orthographic views (front/side/top) of one species for
    /// structural review - seams, proportions, texture placement.</summary>
    public static class TripleViewSnapshot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var go = new GameObject("TripleWolf");
            var ani = go.AddComponent<BlockyAnimal>();
            ani.species = "wolf";
            ani.BuildModel();

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.enabled = false;

            string dir = @"D:\zlj world\_logs";
            // (name, camPos, lookAt): front = +Z, side = +X, top = +Y
            var views = new (string, Vector3, Vector3)[] {
                ("front", new Vector3(0f, 0.55f, 2.6f),  new Vector3(0f, 0.55f, 0f)),
                ("side",  new Vector3(2.6f, 0.55f, 0f),  new Vector3(0f, 0.55f, 0f)),
                ("top",   new Vector3(0.01f, 2.6f, 0f),  new Vector3(0f, 0f, 0f)),
            };
            foreach (var (name, pos, look) in views)
            {
                camGo.transform.position = pos;
                camGo.transform.LookAt(look, pos.y > 1f ? Vector3.forward : Vector3.up);
                var rt = new RenderTexture(700, 700, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render(); // warm pass (shader variants)
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(700, 700, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0);
                tex.Apply();
                string path = System.IO.Path.Combine(dir, $"snapshot_wolf_{name}.png");
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[TripleViewSnapshot] wrote {path}");
                RenderTexture.active = null;
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.Destroy(tex);
            }
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(camGo);
            Debug.Log("[TripleViewSnapshot] RESULT: DONE");
        }
    }
}
