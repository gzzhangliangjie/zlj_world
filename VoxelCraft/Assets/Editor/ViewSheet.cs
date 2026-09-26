using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Render a static 3-view (side / front / top) contact sheet PNG for the
    /// given species (-species=fox) at rest pose. Writes one PNG per view
    /// plus a combined sheet to _logs/gif/views/.
    /// </summary>
    public static class ViewSheet
    {
        public static void Run()
        {
            string species = "fox";
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.StartsWith("-species=")) species = arg.Substring(9);

            var go = new GameObject("sheet_" + species);
            var ani = go.AddComponent<BlockyAnimal>();
            ani.species = species;
            ani.BuildModel();

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.enabled = false;

            // shader globals required by the voxel shader (SideViewCheck lesson)
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            string dir = @"D:\zlj world\_logs\gif\views";
            Directory.CreateDirectory(dir);

            // (name, direction offset from center) - enough distance to fit
            var views = new (string, Vector3)[]
            {
                ("side",  new Vector3(0f, 0.05f, -3.2f)),
                ("front", new Vector3(3.2f, 0.05f, 0f)),
                ("top",   new Vector3(0f, 3.2f, -0.001f)),
            };

            var rends = go.GetComponentsInChildren<Renderer>();
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Vector3 center = b.center;

            var files = new List<string>();
            foreach (var (name, off) in views)
            {
                cam.transform.position = center + off;
                cam.transform.LookAt(center);
                var rt = new RenderTexture(420, 420, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render(); cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(420, 420, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 420, 420), 0, 0);
                tex.Apply(false, false);
                string p = Path.Combine(dir, $"{species}_{name}.png");
                File.WriteAllBytes(p, tex.EncodeToPNG());
                files.Add(p);
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            Debug.Log($"[ViewSheet] wrote {files.Count} views for {species} -> {dir}");
        }
    }
}
