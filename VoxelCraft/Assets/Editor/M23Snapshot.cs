using UnityEngine;
using UnityEditor;
using System.IO;

namespace VoxelCraft.Editor
{
    /// <summary>M23 verification: screenshot the 4 fixed species (hoglin,
    /// horse, donkey, chicken) front+quarter view at walk frame 19.</summary>
    public static class M23Snapshot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var camGo = new GameObject("M23Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.68f, 0.80f, 0.92f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.fieldOfView = 40f;
            cam.enabled = false;

            try
            {
                foreach (string sp in new[] { "hoglin", "horse", "donkey", "chicken" })
                {
                    var go = new GameObject("M23_" + sp);
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
                        ani.TickControllers(dt, ani.walking);
                    }
                    var rends = go.GetComponentsInChildren<Renderer>();
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                    float dist = maxDim * 2.4f + 0.5f;
                    // walk faces +Z; PROFILE view from the right side so
                    // head pitch reads unambiguously (a front view fore-
                    // shortens a down pitched snout into "level").
                    Vector3 dir = Quaternion.Euler(0f, 8f, 0f) * new Vector3(1f, 0.32f, 0f).normalized;
                    cam.transform.position = b.center + dir * dist;
                    cam.transform.LookAt(b.center);
                    cam.Render();
                    var rt = new RenderTexture(360, 360, 24);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(360, 360, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 360, 360), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    Object.DestroyImmediate(rt);
                    File.WriteAllBytes(@"D:\zlj world\_logs\front\m23_" + sp + ".png", tex.EncodeToPNG());
                    Debug.Log("[M23] saved " + sp);
                    Object.DestroyImmediate(go);
                }
            }
            finally { Object.DestroyImmediate(camGo); }
            Debug.Log("[M23] DONE");
        }
    }
}
