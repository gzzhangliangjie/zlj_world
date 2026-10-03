using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;
using VoxelCraft.Core;

namespace VoxelCraft.Editor
{
    /// <summary>M41 delivery shot: the 5-car train on rails + the newly
    /// converted tank/truck geo at player scale, one contact sheet.</summary>
    public static class M41DeliverySnapshot
    {
        public static void Run()
        {
            // voxel shader globals (same as M34: without these everything renders black)
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));
            var cam = new GameObject("M41Cam").AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.transform.position = new Vector3(0f, 6f, 14f);
            cam.transform.rotation = Quaternion.Euler(12f, 180f, 0f);

            var lightGo = new GameObject("M41Light");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            // ---- rows: place every model by its combined BOUNDS CENTRE ----
            // (geo roots are not centred: children are offset from the root, so
            // positioning the root directly accumulates drift — M41 shot round 2)
            System.Action<string, Vector3, float> place = (n, target, yaw) =>
            {
                var go = new GameObject("S_" + n);
                var skin = Art.CreatureTextureFactory.GetSkinMaterial(n + "_skin");
                var geo = Resources.Load<TextAsset>("Geo/" + n + ".geo");
                if (skin == null || geo == null) { Debug.LogError("[M41SHOT] missing " + n); return; }
                BedrockGeoImporter.Build(go.transform, geo, null, skin, 0f, null,
                    out _, out _, out _, out _);
                go.transform.localScale = Vector3.one * 1.585f;
                go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                var rends = go.GetComponentsInChildren<Renderer>();
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                // shift so bounds centre = target and bounds.min.y = target.y ground
                go.transform.position += new Vector3(
                    target.x - b.center.x, target.y - b.min.y, target.z - b.center.z);
            };

            // row 1 (z=0): coupled consist — train 9.7u + 2 coaches 9.1u
            // (46-vox carved from scene_train, full 27..70 span), 0.15u gaps
            float[] row1x = { -20f, -10.35f, -0.9f };
            string[] consist = { "train", "coach3", "coach3" };
            for (int i = 0; i < consist.Length; i++)
                place(consist[i], new Vector3(row1x[i], 0f, 0f), 90f);

            // row 2 (z=-10): tank+trucks+ambulance+bus, each ~6.7-12.9u long
            string[] row2 = { "tank1", "truck1", "truck4", "truck6", "ambulance", "bus" };
            float[] row2x = { -20f, -11.5f, -4.4f, 2.7f, 9.8f, 17.2f };
            for (int i = 0; i < row2.Length; i++)
                place(row2[i], new Vector3(row2x[i], 0f, -10f), 90f);

            // row 3 (z=-20): diesel train2 + coach + flatcar train3 (M45/M46)
            float[] row3x = { -20f, -9.6f, -1.4f };
            string[] row3 = { "train2", "coach3", "train3" };
            for (int i = 0; i < row3.Length; i++)
                place(row3[i], new Vector3(row3x[i], 0f, -20f), 90f);

            // row 4 (z=-30): bullet train + jeremy trailer (same CC-BY family)
            place("bullettrain", new Vector3(1.5f, 0f, -32f), 90f);
            place("traincar", new Vector3(14f, 0f, -32f), 90f);

            // row 5 (z=-40): HXD3D electric loco (M48, Sketchfab CC-BY rip)
            place("hxd3d", new Vector3(6f, 0f, -42f), 90f);

            // player-height reference bar (1.98u) at the row-2 end
            var refGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            refGo.name = "PlayerRef";
            Object.DestroyImmediate(refGo.GetComponent<BoxCollider>());
            refGo.transform.localScale = new Vector3(0.6f, 1.98f, 0.6f);
            refGo.transform.position = new Vector3(27f, 0.99f, -5f);
            refGo.GetComponent<Renderer>().material.color = new Color(0.95f, 0.78f, 0.15f);

            // yellow ground plane so subjects are grounded
            var gnd = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(gnd.GetComponent<BoxCollider>());
            gnd.name = "Ground";
            gnd.transform.localScale = new Vector3(90f, 0.2f, 74f);
            gnd.transform.position = new Vector3(2f, -0.1f, -21f);
            gnd.GetComponent<Renderer>().material.color = new Color(0.44f, 0.55f, 0.38f);

            // rails under row 1
            for (int i = 0; i < 40; i++)
            {
                var tie = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(tie.GetComponent<BoxCollider>());
                tie.transform.localScale = new Vector3(1.6f, 0.12f, 0.3f);
                tie.transform.position = new Vector3(0f, -0.06f, -13f + i * 0.9f);
                tie.GetComponent<Renderer>().material.color = new Color(0.35f, 0.25f, 0.15f);
            }

            cam.transform.position = new Vector3(2f, 24f, 28f);
            cam.transform.rotation = Quaternion.Euler(42f, 180f, 0f);

            var rt = new RenderTexture(1280, 800, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 800, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 800), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes("D:/zlj_world/_shots/m41_delivery.png",
                ImageConversion.EncodeToPNG(tex));
            Debug.Log("[M41SHOT] saved _shots/m41_delivery.png");
            EditorApplication.Exit(0);
        }
    }
}
