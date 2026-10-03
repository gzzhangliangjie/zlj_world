using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;
using VoxelCraft.Core;

namespace VoxelCraft.Editor
{
    /// <summary>M42 audit: top-down view of ONE minecart at 4x render scale —
    /// an open tub must show the dark interior through the top opening; a
    /// wrongly-closed model shows a flat textured lid. Plus a side view.</summary>
    public static class M42TopAudit
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));

            var lightGo = new GameObject("L");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var go = new GameObject("Cart");
            var skin = Art.CreatureTextureFactory.GetSkinMaterial("minecart_skin");
            var geo = Resources.Load<TextAsset>("Geo/minecart.geo");
            BedrockGeoImporter.Build(go.transform, geo, null, skin, 0f, null,
                out _, out _, out _, out _);
            go.transform.localScale = Vector3.one * 4f;  // 4x zoom for clarity

            // numeric audit: dump every cube's world centre / rotation / size
            var rends = go.GetComponentsInChildren<Renderer>();
            var all = rends[0].bounds;
            foreach (var r in rends)
            {
                all.Encapsulate(r.bounds);
                Debug.Log($"[M42AUD] {r.name} centre={r.bounds.center:F2} size={r.bounds.size:F2} rot={r.transform.rotation.eulerAngles:F0}");
            }
            Debug.Log($"[M42AUD] TOTAL bounds {all}");

            var gnd = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(gnd.GetComponent<BoxCollider>());
            gnd.transform.localScale = new Vector3(20f, 0.2f, 20f);
            gnd.transform.position = new Vector3(0f, -0.1f, 0f);
            gnd.GetComponent<Renderer>().material.color = new Color(0.44f, 0.55f, 0.38f);

            // top-down: 80 degrees, straight down the opening
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.transform.position = new Vector3(0f, 6f, 1.2f);
            cam.transform.rotation = Quaternion.Euler(80f, 180f, 0f);
            Shot(cam, "m42_top.png");

            // side view at eye level
            cam.transform.position = new Vector3(0f, 1.4f, 4.5f);
            cam.transform.rotation = Quaternion.Euler(10f, 180f, 0f);
            Shot(cam, "m42_side.png");

            EditorApplication.Exit(0);
        }

        static void Shot(Camera cam, string name)
        {
            var rt = new RenderTexture(900, 700, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(900, 700, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 900, 700), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes("D:/zlj_world/_shots/" + name,
                ImageConversion.EncodeToPNG(tex));
            Debug.Log("[M42AUD] saved " + name);
        }
    }
}
