using System;
using System.IO;
using UnityEngine;
using VoxelCraft.Creatures;
using VoxelCraft.Player;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Renders the actual creature/player box models to PNG so skin/UV issues
    /// can be verified by looking at real output instead of reasoning:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; `
    ///     -executeMethod VoxelCraft.Editor.ModelSnapshot.Run -logFile &lt;log&gt;
    /// Writes _logs/snapshot_*.png and logs "MODEL SNAPSHOT OK".
    /// </summary>
    public static class ModelSnapshot
    {
        public static void Run()
        {
            // Same globals DayNightCycle drives in-game; without them the unlit
            // skin shader renders black.
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var root = new GameObject("Models");
            string[] species = { "pig", "cow", "sheep", "chicken" };
            float[] xs = { 0f, 1.7f, 3.4f, 4.6f };
            for (int i = 0; i < species.Length; i++)
            {
                var go = new GameObject(species[i]);
                go.transform.SetParent(root.transform, false);
                go.transform.position = new Vector3(xs[i], 0f, 0f);
                var animal = go.AddComponent<BlockyAnimal>();
                animal.species = species[i];
                animal.BuildModel();
                go.transform.rotation = Quaternion.Euler(0f, 210f, 0f);
            }

            var playerGo = new GameObject("Player");
            playerGo.transform.SetParent(root.transform, false);
            playerGo.transform.position = new Vector3(6.3f, 0f, 0f);
            var rig = playerGo.AddComponent<ThirdPersonRig>();
            rig.BuildModel();
            playerGo.transform.rotation = Quaternion.Euler(0f, 210f, 0f);

            var camGo = new GameObject("SnapCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 500f;

            string dir = @"D:\zlj world\_logs";
            Directory.CreateDirectory(dir);
            Shot(camGo, cam, new Vector3(3.1f, 1.5f, -6.5f), new Vector3(3.1f, 0.7f, 0f),
                Path.Combine(dir, "snapshot_all_front.png"));
            Shot(camGo, cam, new Vector3(-0.6f, 2.4f, -4.6f), new Vector3(3.6f, 0.5f, 0.6f),
                Path.Combine(dir, "snapshot_all_threequarter.png"));
            Shot(camGo, cam, new Vector3(5.8f, 0.9f, -2.4f), new Vector3(4.6f, 0.45f, 0f),
                Path.Combine(dir, "snapshot_chicken_close.png"));
            Shot(camGo, cam, new Vector3(6.3f, 1.15f, -3.2f), new Vector3(6.3f, 1.0f, 0f),
                Path.Combine(dir, "snapshot_player_close.png"));
            // Diagnostic: isolate each chicken renderer, frame it by its own
            // bounds, and log name/mesh/bounds so sizes are data, not guesses.
            var chickenGo = root.transform.GetChild(3).gameObject;
            var parts = chickenGo.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < parts.Length; i++)
            {
                var b = parts[i].bounds;
                var mf = parts[i].GetComponent<MeshFilter>();
                var mb = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.size : Vector3.zero;
                var lp = parts[i].transform.localPosition;
                Debug.Log($"CHICKEN PART {i}: '{parts[i].gameObject.name}' mesh='{(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "?")}' " +
                          $"meshBounds=({mb.x:F3},{mb.y:F3},{mb.z:F3}) localPos=({lp.x:F3},{lp.y:F3},{lp.z:F3}) " +
                          $"worldCenter=({b.center.x:F3},{b.center.y:F3},{b.center.z:F3}) worldSize=({b.size.x:F3},{b.size.y:F3},{b.size.z:F3})");
            }
            for (int k = 0; k < parts.Length; k++)
            {
                for (int i = 0; i < parts.Length; i++) parts[i].enabled = i == k;
                var b = parts[k].bounds;
                float dist = Mathf.Max(b.size.x, b.size.y, b.size.z) * 3.5f + 0.35f;
                Shot(camGo, cam, b.center + new Vector3(0.25f, 0.1f, -dist), b.center,
                    Path.Combine(dir, $"snapshot_chicken_part{k}_{parts[k].gameObject.name}.png"));
            }
            for (int i = 0; i < parts.Length; i++) parts[i].enabled = true;
            // Player sanity: the rig hides the model in first person by design
            // (SetActive(false)); force it visible for the snapshot only.
            foreach (var t in playerGo.GetComponentsInChildren<Transform>(true)) t.gameObject.SetActive(true);
            var playerParts = playerGo.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < playerParts.Length; i++)
            {
                var mf = playerParts[i].GetComponent<MeshFilter>();
                var mb = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.size : Vector3.zero;
                var wc = playerParts[i].bounds.center;
                Debug.Log($"PLAYER PART {i}: '{playerParts[i].gameObject.name}' meshBounds=({mb.x:F3},{mb.y:F3},{mb.z:F3}) worldCenter=({wc.x:F3},{wc.y:F3},{wc.z:F3})");
            }
            Shot(camGo, cam, new Vector3(6.3f, 1.3f, -3.6f), new Vector3(6.3f, 1.05f, 0f),
                Path.Combine(dir, "snapshot_player_solo.png"));
            Debug.Log("MODEL SNAPSHOT OK");
        }

        private static void Shot(GameObject camGo, Camera cam, Vector3 pos, Vector3 lookAt, string path)
        {
            camGo.transform.position = pos;
            camGo.transform.LookAt(lookAt);
            int w = 1100, h = 560;
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Debug.Log("SNAPSHOT WROTE " + path);
        }
    }
}
