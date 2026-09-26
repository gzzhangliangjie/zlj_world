using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace VoxelCraft.Editor
{
    /// <summary>Front-view single frames for the 3 new humanoids, mirroring
    /// BehaviourGifFrames' instantiation (AddComponent + BuildModel + Tick),
    /// camera yaw 180 so faces show. PNGs to _logs/front for PIL.</summary>
    public static class FrontSheet
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));
            var camGo = new GameObject("FS_cam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.backgroundColor = new Color(0.68f, 0.80f, 0.92f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            try
            {
                foreach (string sp in new[] { "zombie", "skeleton", "villager", "bee", "donkey", "armadillo" })
                {
                    float yaw = sp == "bee" ? -144f : 180f;
                    var go = new GameObject("FS_" + sp);
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
                    }
                    var rends = go.GetComponentsInChildren<Renderer>();
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                    float dist = maxDim * 2.2f + 0.5f;
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * new Vector3(1.6f, 0.35f, -2.2f).normalized;
                    cam.transform.position = b.center + dir * dist;
                    cam.transform.LookAt(b.center);
                    cam.orthographicSize = maxDim * 0.62f;
                    var rt = new RenderTexture(360, 360, 24);
                    cam.targetTexture = rt;
                    cam.Render(); cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(360, 360, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 360, 360), 0, 0);
                    tex.Apply(false, false);
                    System.IO.Directory.CreateDirectory(@"D:\zlj world\_logs\front");
                    System.IO.File.WriteAllBytes(@"D:\zlj world\_logs\front\" + sp + "_front.png", tex.EncodeToPNG());
                    Debug.Log("[FrontSheet] saved " + sp);
                    cam.targetTexture = null;
                    rt.Release();
                    Object.DestroyImmediate(go);
                }
            }
            finally { Object.DestroyImmediate(camGo); }
            Debug.Log("[FrontSheet] DONE");
        }
    }
}
