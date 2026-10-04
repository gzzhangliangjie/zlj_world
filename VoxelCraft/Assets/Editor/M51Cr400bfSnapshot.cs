using UnityEngine;
using System.IO;

namespace VoxelCraft.Editor
{
    /// <summary>Solo beauty shot of the CR400BF Fuxing head car (M51).</summary>
    public static class M51Cr400bfSnapshot
    {
        public static void Run()
        {
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.fieldOfView = 60f;

            var lightGo = new GameObject("Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            // fog globals the unlit shader needs (black-image guard, M48 lesson)
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.55f, 0.72f, 0.90f));
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);

            var skin = Art.CreatureTextureFactory.GetSkinMaterial("cr400bf_skin");
            var geo = Resources.Load<TextAsset>("Geo/cr400bf.geo");
            var root = new GameObject("cr400bf");
            Creatures.BedrockGeoImporter.Build(root.transform, geo, null, skin, 0f, null,
                out _, out _, out _, out _);
            root.transform.localScale = Vector3.one * 1.585f;
            // geo long axis is X; yaw 90 to point it down world Z (camera side view)
            root.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            var rends = root.GetComponentsInChildren<Renderer>();
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Debug.Log("[M51SHOT] bounds " + b.size.ToString("F2"));

            // ground slab under the train
            var gnd = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(gnd.GetComponent<BoxCollider>());
            gnd.transform.localScale = new Vector3(30f, 0.2f, 8f);
            gnd.transform.position = new Vector3(b.center.x, -0.1f, b.center.z);
            gnd.GetComponent<Renderer>().material.color = new Color(0.44f, 0.55f, 0.38f);

            // side view: camera on -Z looking at the body, train runs along X
            float d = b.size.x * 0.62f;
            cam.transform.position = new Vector3(b.center.x, b.size.y * 0.75f + 1.2f, b.center.z - d);
            cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
            Capture(cam, "_shots/m51_cr400bf_side.png");

            // 3/4 front
            cam.transform.position = new Vector3(b.center.x + b.size.x * 0.28f, b.size.y * 0.9f + 1.2f, b.center.z - d * 0.62f);
            cam.transform.rotation = Quaternion.Euler(8f, 38f, 0f);
            Capture(cam, "_shots/m51_cr400bf_q34.png");

            // top
            cam.transform.position = new Vector3(b.center.x, b.size.z * 2.2f + 4f, b.center.z - 2f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Capture(cam, "_shots/m51_cr400bf_top.png");
        }

        static void Capture(Camera cam, string path)
        {
            cam.Render();
            var rt = new RenderTexture(1280, 800, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 800, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 800), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[M51SHOT] saved " + path);
        }
    }
}
