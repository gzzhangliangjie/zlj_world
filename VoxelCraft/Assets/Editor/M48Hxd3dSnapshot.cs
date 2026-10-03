using UnityEngine;
using UnityEditor;
using VoxelCraft.Creatures;
using VoxelCraft.Core;

namespace VoxelCraft.Editor
{
    /// <summary>M48 solo shot: the HXD3D electric locomotive alone, three
    /// angles (top / side / 3-4), numeric pixel audit source.</summary>
    public static class M48Hxd3dSnapshot
    {
        public static void Run()
        {
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.55f, 0.72f, 0.90f));
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;

            var go = new GameObject("Hxd3d");
            var skin = Art.CreatureTextureFactory.GetSkinMaterial("hxd3d_skin");
            var geo = Resources.Load<TextAsset>("Geo/hxd3d.geo");
            if (skin == null || geo == null)
            {
                Debug.LogError("[M48SHOT] missing asset skin=" + (skin == null) + " geo=" + (geo == null));
                EditorApplication.Exit(1);
                return;
            }
            var texDebug = skin.mainTexture as Texture2D;
            Debug.Log("[M48SHOT] mat tex " + (texDebug == null ? "NULL" : texDebug.width + "x" + texDebug.height));
            if (texDebug != null)
            {
                var c10 = texDebug.GetPixel(11, texDebug.height - 1 - 10);
                var c100 = texDebug.GetPixel(64, texDebug.height - 1 - 100);
                Debug.Log("[M48SHOT] tex(11,10)=" + c10 + " tex(64,100)=" + c100);
            }
            BedrockGeoImporter.Build(go.transform, geo, null, skin, 0f, null,
                out _, out _, out _, out _);
            // centre + ground the model by its bounds
            var rends0 = go.GetComponentsInChildren<Renderer>();
            var b = rends0[0].bounds;
            foreach (var r in rends0) b.Encapsulate(r.bounds);
            go.transform.Rotate(0f, 90f, 0f, Space.World);
            var rends2 = go.GetComponentsInChildren<Renderer>();
            b = rends2[0].bounds;
            foreach (var r in rends2) b.Encapsulate(r.bounds);
            go.transform.position += new Vector3(-b.center.x, -b.min.y, -b.center.z);
            var size = b.size;
            Debug.Log("[M48SHOT] bounds " + size.ToString("F2"));

            // side view (long axis horizontal)
            float d = Mathf.Max(size.x, size.z) * 1.15f + 4f;
            var rt = new RenderTexture(1280, 460, 24);
            cam.targetTexture = rt;
            cam.Render();

            // --- side elevation (looking -Z): long axis = X ---
            cam.transform.position = new Vector3(0f, size.y * 0.55f, -d);
            cam.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            Shot(cam, rt, "D:/zlj_world/_shots/m48_hxd3d_side.png");

            // --- top view ---
            cam.transform.position = new Vector3(0f, size.y + d * 0.9f, -0.01f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Shot(cam, rt, "D:/zlj_world/_shots/m48_hxd3d_top.png");

            // --- front (nose) view: looking +X ---
            cam.transform.position = new Vector3(-d, size.y * 0.5f, 0f);
            cam.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            Shot(cam, rt, "D:/zlj_world/_shots/m48_hxd3d_front.png");

            // --- 3/4 view ---
            float q = d * 0.72f;
            cam.transform.position = new Vector3(-q, size.y * 0.9f + 2f, -q);
            cam.transform.rotation = Quaternion.Euler(24f, 45f, 0f);
            Shot(cam, rt, "D:/zlj_world/_shots/m48_hxd3d_q34.png");

            EditorApplication.Exit(0);
        }

        static void Shot(Camera cam, RenderTexture rt, string path)
        {
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 460, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 460), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Debug.Log("[M48SHOT] saved " + path);
        }
    }
}
