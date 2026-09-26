using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Dump the runtime bone hierarchy (parenting + local pose) of one
    /// species' geo model, plus leg/body world positions - to debug mid-
    /// animation splits without guessing from screenshots.</summary>
    public static class HierarchyDump
    {
        public static void Run()
        {
            foreach (var sp in new[] { "fox", "wolf" })
            {
                var go = new GameObject("Dump_" + sp);
                var ani = go.AddComponent<BlockyAnimal>();
                ani.species = sp;
                ani.BuildModel();
                var player = go.GetComponent<BedrockAnimationPlayer>();

                Debug.Log($"[HD] ==== {sp} bind pose ====");
                DumpTree(go.transform, 0);

                if (sp == "fox")
                {
                // drive one walk frame at 80-deg swing peak
                ani.walking = true;
                player?.Tick(1f / 20f);
                Debug.Log($"[HD] ==== {sp} after 1 walk tick ====");
                DumpTree(go.transform, 0);

                // sit clip
                if (player != null) { ani.walking = false; player.Play("animation.fox.sit", false); player.Tick(0.1f); player.Tick(1f / 20f); }
                Debug.Log($"[HD] ==== {sp} sit clip @0.15s ====");
                DumpTree(go.transform, 0);

                // sleep clip (part_visibility check: legs hidden, head swapped)
                if (player != null) { player.StopAll(); player.Play("animation.fox.sleep", false); player.Tick(1f); }
                Debug.Log($"[HD] ==== {sp} sleep clip @1s ====");
                DumpTree(go.transform, 0);
                player?.StopAll();
                Debug.Log($"[HD] ==== {sp} after StopAll (restore) ====");
                DumpTree(go.transform, 0);
                }
                else
                {
                    // wolf: play the registry behaviour clip exactly as the
                    // BehaviourBrain/GIF harness do (relative, "45-this").
                    if (player != null) { ani.walking = false; player.Play("animation.wolf.sitting", false); player.Tick(0.1f); player.Tick(1f / 20f); }
                    Debug.Log($"[HD] ==== {sp} sitting clip @0.15s ====");
                    DumpTree(go.transform, 0);
                    if (player != null) { player.Tick(2f); }
                    Debug.Log($"[HD] ==== {sp} sitting clip settled @2.15s ====");
                    DumpTree(go.transform, 0);
                }

                Object.DestroyImmediate(go);
            }
        }

        static void DumpTree(Transform t, int depth)
        {
            if (depth > 0 && (t.name.Contains("cube_"))) return; // skip cubes
            string pad = new string(' ', depth * 2);
            Vector3 lp = t.localPosition, wp = t.position;
            Vector3 le = t.localEulerAngles;
            Debug.Log($"[HD] {pad}{t.name}  local({lp.x:F3},{lp.y:F3},{lp.z:F3}) euler({le.x:F0},{le.y:F0},{le.z:F0}) world({wp.x:F3},{wp.y:F3},{wp.z:F3}) active={t.gameObject.activeSelf}");
            for (int i = 0; i < t.childCount; i++) DumpTree(t.GetChild(i), depth + 1);
        }
    }
}
