using UnityEngine;
using UnityEditor;
using System.Text;

namespace VoxelCraft.Editor
{
    /// <summary>M23c: FRONT-view render of horse/donkey head+neck. The user
    /// reports a crooked head ("歪的") that a side view cannot show. Front
    /// view exposes yaw/roll asymmetry directly.</summary>
    public static class HeadFrontProbe
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var camGo = new GameObject("HFcam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.68f, 0.80f, 0.92f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 60f;
            cam.fieldOfView = 30f;
            cam.enabled = false;

            foreach (string sp in new[] { "horse", "donkey" })
            {
                // REST pose (no walk) AND walk f19 - two renders stacked
                for (int mode = 0; mode < 2; mode++)
                {
                    var go = new GameObject("HF_" + sp + mode);
                    var ani = go.AddComponent<Creatures.BlockyAnimal>();
                    ani.species = sp;
                    ani.BuildModel();
                    var player = go.GetComponent<Creatures.BedrockAnimationPlayer>();
                    ani.walking = mode == 1;
                    float dt = 1f / 30f;
                    if (mode == 1)
                        for (int i = 0; i < 19; i++)
                        {
                            go.transform.position += Vector3.forward * (dt * 1.5f);
                            if (player != null) player.Tick(dt);
                            ani.TickControllers(dt, ani.walking);
                        }
                    else if (player != null) player.Tick(dt);

                    var rends = go.GetComponentsInChildren<Renderer>();
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    // head subtree bounds only
                    var hb = new Bounds();
                    bool first = true;
                    foreach (var r in rends)
                    {
                        bool inHead = false;
                        for (var t = r.transform; t != null; t = t.parent)
                            if (t.name == "Neck") { inHead = true; break; }
                        if (!inHead) continue;
                        if (first) { hb = r.bounds; first = false; }
                        else hb.Encapsulate(r.bounds);
                    }
                    // FRONT view: dead-ahead -Z looking at +Z-facing animal
                    cam.transform.position = new Vector3(hb.center.x, hb.center.y, hb.center.z - hb.size.z * 3f - 0.8f);
                    cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                    var rt = new RenderTexture(240, 240, 24);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(240, 240, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 240, 240), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    Object.DestroyImmediate(rt);
                    System.IO.File.WriteAllBytes(@"C:\Users\zlj10\AppData\Local\hermes\cache\scratch\hf_" + sp + (mode == 0 ? "_rest" : "_walk") + ".png", tex.EncodeToPNG());
                    Debug.Log($"[HF] saved {sp} mode={mode} headBounds=({hb.center.x:F3},{hb.center.y:F3},{hb.center.z:F3}) size=({hb.size.x:F3},{hb.size.y:F3},{hb.size.z:F3})");
                    Object.DestroyImmediate(go);
                }
            }
            Object.DestroyImmediate(camGo);
            Debug.Log("[HF] DONE");
        }
    }
}
