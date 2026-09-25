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
            string[] species = { "pig", "cow", "sheep", "chicken", "wolf", "fox", "mooshroom" };
            float[] xs = { 0f, 1.7f, 3.4f, 4.6f, -1.8f, -3.6f, 8.1f };
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
            foreach (Transform kid in root.transform)
                Debug.Log($"MODEL SLOT {kid.name}: pos=({kid.position.x:F2},{kid.position.y:F2},{kid.position.z:F2}) yaw={kid.eulerAngles.y:F0}");
            Shot(camGo, cam, new Vector3(2.2f, 1.6f, -10.5f), new Vector3(2.2f, 0.7f, 0f),
                Path.Combine(dir, "snapshot_all_front.png"));
            Shot(camGo, cam, new Vector3(-1.0f, 2.6f, -8.0f), new Vector3(3.0f, 0.5f, 0.6f),
                Path.Combine(dir, "snapshot_all_threequarter.png"));
            Shot(camGo, cam, new Vector3(5.8f, 0.9f, -2.4f), new Vector3(4.6f, 0.45f, 0f),
                Path.Combine(dir, "snapshot_chicken_close.png"));
            Shot(camGo, cam, new Vector3(6.3f, 1.15f, -3.2f), new Vector3(6.3f, 1.0f, 0f),
                Path.Combine(dir, "snapshot_player_close.png"));
            // Close-ups with HARDCODED camera positions: renderer/mesh bounds
            // are unreliable in batch mode, so aim at the known spawn slots.
            // (pig x=0, cow 1.7, sheep 3.4, chicken 4.6, player 6.3; yaw 210.)
            CloseUp(camGo, cam, 0f, 1.0f, "pig");
            CloseUp(camGo, cam, 4.6f, 0.85f, "chicken");
            CloseUp(camGo, cam, 3.4f, 1.0f, "sheep");
            CloseUp(camGo, cam, 1.7f, 1.3f, "cow");
            CloseUp(camGo, cam, -1.8f, 0.9f, "wolf");
            CloseUp(camGo, cam, -3.6f, 0.7f, "fox");
            CloseUp(camGo, cam, 8.1f, 1.3f, "mooshroom");
            // Player sanity: the rig hides the model in first person by design
            // (SetActive(false)); force it visible for the snapshot only.
            foreach (var t in playerGo.GetComponentsInChildren<Transform>(true)) t.gameObject.SetActive(true);
            Shot(camGo, cam, new Vector3(6.3f, 1.3f, -3.6f), new Vector3(6.3f, 1.05f, 0f),
                Path.Combine(dir, "snapshot_player_solo.png"));
            Debug.Log("MODEL SNAPSHOT OK");
        }

        private static void CloseUp(GameObject camGo, Camera cam, float x, float aimY, string name)
        {
            string dir = @"D:\zlj world\_logs";
            // Face-on (animals yaw 210 -> face toward front-left) and side.
            Shot(camGo, cam, new Vector3(x - 0.9f, aimY + 0.25f, -1.9f), new Vector3(x, aimY, 0f),
                Path.Combine(dir, $"snapshot_{name}_front.png"));
            Shot(camGo, cam, new Vector3(x + 1.7f, aimY + 0.3f, -1.5f), new Vector3(x, aimY, 0f),
                Path.Combine(dir, $"snapshot_{name}_side.png"));
        }

        private static Bounds CombinedBounds(GameObject go)
        {
            var b = new Bounds(go.transform.position, Vector3.zero);
            bool first = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private static void Shot(GameObject camGo, Camera cam, Vector3 pos, Vector3 lookAt, string path)
        {
            camGo.transform.position = pos;
            camGo.transform.LookAt(lookAt);
            cam.Render(); // warm the matrices so the logged matrix is the render one
            var c = cam.worldToCameraMatrix.GetColumn(3);
            Debug.Log($"SHOT {Path.GetFileName(path)}: requested pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) " +
                      $"matrixPos=({c.x:F2},{c.y:F2},{c.z:F2}) fwd={camGo.transform.forward}");
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
