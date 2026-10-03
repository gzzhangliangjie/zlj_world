using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;
using VoxelCraft.Core;

namespace VoxelCraft.Editor
{
    /// <summary>M42 delivery shot: official minecart consist on rails next to
    /// the player-height reference bar and one mmmm wagon for scale.</summary>
    public static class M42DeliverySnapshot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));

            var lightGo = new GameObject("L");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            // minecart consist: 3 carts along X on rails
            var railTie = new Color(0.36f, 0.27f, 0.16f);
            for (int i = 0; i < 14; i++)
            {
                var tie = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(tie.GetComponent<BoxCollider>());
                tie.transform.localScale = new Vector3(1.3f, 0.1f, 0.28f);
                tie.transform.position = new Vector3(-6.5f + i * 1.0f, 0.02f, 0f);
                tie.GetComponent<Renderer>().material.color = railTie;
            }

            System.Action<string, Vector3, float> place = (n, target, scale) =>
            {
                var go = new GameObject("S_" + n);
                var skin = Art.CreatureTextureFactory.GetSkinMaterial(n + "_skin");
                var geo = Resources.Load<TextAsset>("Geo/" + n + ".geo");
                if (skin == null || geo == null) { Debug.LogError("[M42SHOT] missing " + n); return; }
                BedrockGeoImporter.Build(go.transform, geo, null, skin, 0f, null,
                    out _, out _, out _, out _);
                go.transform.localScale = Vector3.one * scale;
                go.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                var rends = go.GetComponentsInChildren<Renderer>();
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                go.transform.position += new Vector3(
                    target.x - b.center.x, target.y - b.min.y, target.z - b.center.z);
            };

            // 3 minecarts (official, 0.5 scale), spaced by ~0.9u
            place("minecart", new Vector3(-4.5f, 0.1f, 0f), 0.5f);
            place("minecart", new Vector3(-3.4f, 0.1f, 0f), 0.5f);
            place("minecart", new Vector3(-2.3f, 0.1f, 0f), 0.5f);
            // one mmmm wagon for scale comparison
            place("wagon1", new Vector3(2.8f, 0.1f, 0f), 1.585f);

            // player reference (1.98u)
            var refGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(refGo.GetComponent<BoxCollider>());
            refGo.transform.localScale = new Vector3(0.28f, 1.98f, 0.28f);
            refGo.transform.position = new Vector3(6.5f, 1.0f, 0f);
            refGo.GetComponent<Renderer>().material.color = new Color(0.95f, 0.78f, 0.15f);

            var gnd = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(gnd.GetComponent<BoxCollider>());
            gnd.transform.localScale = new Vector3(30f, 0.2f, 8f);
            gnd.transform.position = new Vector3(0f, -0.05f, 0f);
            gnd.GetComponent<Renderer>().material.color = new Color(0.44f, 0.55f, 0.38f);

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.transform.position = new Vector3(1f, 5.5f, 9.5f);
            cam.transform.rotation = Quaternion.Euler(22f, 200f, 0f);

            var rt = new RenderTexture(1280, 640, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 640, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 640), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes("D:/zlj_world/_shots/m42_delivery.png",
                ImageConversion.EncodeToPNG(tex));
            Debug.Log("[M42SHOT] saved");
            EditorApplication.Exit(0);
        }
    }
}
