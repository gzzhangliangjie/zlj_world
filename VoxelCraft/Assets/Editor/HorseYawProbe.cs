using UnityEngine;
using UnityEditor;

namespace VoxelCraft.Editor
{
    /// <summary>Why is horse/donkey head YAWED during walk? Probe the live
    /// neck/head euler per tick plus the molang variables.</summary>
    public static class HorseYawProbe
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var go = new GameObject("HY_horse");
            var ani = go.AddComponent<Creatures.BlockyAnimal>();
            ani.species = "horse";
            ani.BuildModel();
            var player = go.GetComponent<Creatures.BedrockAnimationPlayer>();
            ani.walking = true;
            float dt = 1f / 30f;
            Transform neck = null, head = null, body = null;
            foreach (var t in go.GetComponentsInChildren<Transform>())
            {
                if (t.name == "Neck") neck = t;
                if (t.name == "Head") head = t;
                if (t.name == "Body") body = t;
            }
            Debug.Log($"[HY] REST Neck={neck.localEulerAngles} Head={head.localEulerAngles} Body={body.localEulerAngles}");
            for (int i = 0; i < 20; i++)
            {
                go.transform.position += Vector3.forward * (dt * 1.5f);
                if (player != null) player.Tick(dt);
                ani.TickControllers(dt, ani.walking);
            }
            Debug.Log($"[HY] f19 Neck={neck.localEulerAngles} Head={head.localEulerAngles} Body={body.localEulerAngles}");
            Debug.Log($"[HY] headYawDeg={player.headYawDeg}");
            if (player != null)
            {
                foreach (var kv in player.variables)
                    if (kv.Key.Contains("stand") || kv.Key.Contains("eat") || kv.Key.Contains("head"))
                        Debug.Log($"[HY] var {kv.Key}={kv.Value}");
            }
            Object.DestroyImmediate(go);
            Debug.Log("[HY] DONE");
        }
    }
}
