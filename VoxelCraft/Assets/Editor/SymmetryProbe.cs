using UnityEngine;
using UnityEditor;
using System.Text;

namespace VoxelCraft.Editor
{
    /// <summary>Symmetry probe: for horse/donkey, dump every renderer's
    /// world bounds centre + the head subtree bone frames. A symmetric
    /// quadruped must have every cube centre x paired ±; an unpaired |x|
    /// or head-namespace yaw is the "crooked head" the user sees.</summary>
    public static class SymmetryProbe
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            foreach (string sp in new[] { "horse", "donkey" })
            {
                var go = new GameObject("SP_" + sp);
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
                var sb = new StringBuilder();
                sb.AppendLine($"[SP] ===== {sp} walk f19 =====");
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    // only the head/neck subtree + whole-model lateral census
                    bool inHead = false;
                    for (var t = r.transform; t != null; t = t.parent)
                        if (t.name == "Neck") { inHead = true; break; }
                    var c = r.bounds.center;
                    var s = r.bounds.size;
                    string tag = inHead ? "HEAD" : "body";
                    if (inHead || Mathf.Abs(c.x) > 0.02f)
                        sb.AppendLine($"[SP] {tag} {r.transform.name} centre=({c.x:F3},{c.y:F3},{c.z:F3}) size=({s.x:F2},{s.y:F2},{s.z:F2})");
                }
                // head forward axis: Head local +Z in world (animals face +Z)
                foreach (var t in go.GetComponentsInChildren<Transform>())
                {
                    if (t.name != "Head" && t.name != "Neck" && !t.name.Contains("Ear")) continue;
                    Vector3 fwd = t.rotation * Vector3.forward;
                    Vector3 up = t.rotation * Vector3.up;
                    sb.AppendLine($"[SP] bone {t.name} localEuler={t.localEulerAngles} fwd=({fwd.x:F3},{fwd.y:F3},{fwd.z:F3}) up=({up.x:F3},{up.y:F3},{up.z:F3})");
                }
                Debug.Log(sb.ToString());
                Object.DestroyImmediate(go);
            }
            Debug.Log("[SP] DONE");
        }
    }
}
