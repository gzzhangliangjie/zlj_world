using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Solo close-up snapshot of one species' head, framed tight, so
    /// face rendering can be judged without neighbouring animals.</summary>
    public static class SoloHeadSnapshot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var go = new GameObject("SoloFox");
            var ani = go.AddComponent<BlockyAnimal>();
            ani.species = "fox";
            ani.BuildModel();

            // frame the head: head sits ~0.6m up, 0.4m forward of origin
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.transform.position = new Vector3(0f, 0.65f, 2.2f);
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // look back toward origin
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 10f;
            cam.enabled = false;

            var rt = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render(); // warm pass - shader variants compile on first draw
            cam.Render(); // second pass actually renders with compiled shaders
            RenderTexture.active = rt;
            var tex = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            tex.Apply();
            var png = tex.EncodeToPNG();
            string outPath = @"D:\zlj world\_logs\snapshot_fox_solohead.png";
            System.IO.File.WriteAllBytes(outPath, png);
            Debug.Log($"[SoloHeadSnapshot] wrote {outPath} ({png.Length} bytes)");
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(camGo);
            Debug.Log("[SoloHeadSnapshot] RESULT: DONE");
        }
    }
}
