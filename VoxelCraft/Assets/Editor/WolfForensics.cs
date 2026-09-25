using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Same-process forensics: dump world AABBs AND render the side
    /// view in ONE BuildModel call - catches transform mutations that happen
    /// between build and render (Update(), animation, editor hooks).</summary>
    public static class WolfForensics
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var go = new GameObject("ForensicsWolf");
            var ani = go.AddComponent<BlockyAnimal>();
            ani.species = "wolf";
            ani.BuildModel();
            go.transform.rotation = Quaternion.identity;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== T0 (right after BuildModel)");
            Dump(sb, go);

            // What Update() does before the first render frame (batch mode
            // never runs MonoBehaviour.Update, so drive the public gait API
            // the same way AnimVerify does):
            ani.walking = false; // idle pose first
            ani.ApplyLegacyGait(0f, 1f / 60f, 0f);
            sb.AppendLine("== T1 (after 5 x Tick)");
            Dump(sb, go);

            // Render side view right here, same process, same object.
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 30f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 20f;
            cam.enabled = false;
            camGo.transform.position = new Vector3(2.6f, 0.55f, 0f);
            camGo.transform.LookAt(new Vector3(0f, 0.55f, 0f));
            var rt = new RenderTexture(700, 700, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render(); cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(700, 700, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(@"D:\zlj world\_logs\snapshot_wolf_forensic_side.png",
                tex.EncodeToPNG());
            sb.AppendLine("== T2 (after render) - wrote forensic_side.png");
            Dump(sb, go);

            Debug.Log("[WolfForensics]\n" + sb);
            System.IO.File.WriteAllText(@"D:\zlj world\_logs\wolf_forensics.txt", sb.ToString());
        }

        static void Dump(System.Text.StringBuilder sb, GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mn = Vector3.positiveInfinity; var mx = Vector3.negativeInfinity;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var w = r.transform.localToWorldMatrix.MultiplyPoint3x4(v);
                    mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
                }
                string chain = r.transform.parent != null && r.transform.parent.parent != null
                    ? r.transform.parent.parent.name + "/" + r.transform.parent.name
                    : (r.transform.parent != null ? r.transform.parent.name : "-");
                sb.AppendLine(string.Format("{0,-14} {1,-16} y {2:F3}..{3:F3}  z {4:F3}..{5:F3}",
                    r.name, chain, mn.y, mx.y, mn.z, mx.z));
            }
            // bone local rotations of interest
            foreach (var t in go.GetComponentsInChildren<Transform>())
            {
                if (t.name == "body" || t.name == "upperBody" || t.name == "head")
                {
                    var e = t.localRotation.eulerAngles;
                    sb.AppendLine($"  bone {t.name}: localEuler ({e.x:F1},{e.y:F1},{e.z:F1}) localPos {t.localPosition}");
                }
            }
        }
    }
}
