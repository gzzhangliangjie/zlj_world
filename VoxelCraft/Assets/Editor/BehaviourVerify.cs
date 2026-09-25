using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Runtime verification of ambient behaviours: force each
    /// species' behaviour state, drive Tick(dt), and measure the official
    /// clip's effect on the assembled model (bone rotations vs targets).
    /// Batch-safe: manual Tick, no MonoBehaviour.Update dependence.</summary>
    public static class BehaviourVerify
    {
        static System.Collections.Generic.List<Json> results;

        class Json
        {
            public string goal, subject, detail;
            public bool pass;
        }

        [MenuItem("VoxelCraft/Verify Behaviours")]
        public static void Run()
        {
            results = new System.Collections.Generic.List<Json>();
            foreach (var sp in new[] { "wolf", "sheep", "fox" })
            {
                var go = new GameObject("bv_" + sp);
                try
                {
                    var ani = go.AddComponent<BlockyAnimal>();
                    ani.species = sp;
                    ani.BuildModel();
                    var brain = go.GetComponent<BehaviourBrain>();
                    var player = go.GetComponent<BedrockAnimationPlayer>();
                    Check(sp + ".brain", brain != null, "BehaviourBrain attached");
                    if (brain == null || player == null) continue;

                    // Force idle then drive the brain until a behaviour starts.
                    ani.walking = false;
                    float t = 0f; bool started = false;
                    for (int i = 0; i < 600 && !started; i++) // max 10 s
                    {
                        brain.Tick(1f / 60f);
                        player.Tick(1f / 60f);
                        t += 1f / 60f;
                        started = brain.InBehaviour;
                    }
                    Check(sp + ".starts", started,
                        $"behaviour started after {t:F1}s idle (clip={brain.ActiveClip}) " +
                        $"playerClips={player.ClipCount} walking={ani.walking}");

                    if (started)
                    {
                        // Let the pose settle fully.
                        for (int i = 0; i < 180; i++) { brain.Tick(1f / 60f); player.Tick(1f / 60f); }

                        string clip = brain.ActiveClip;
                        float maxBoneDelta = 0f; string movedBone = "";
                        foreach (var tr in go.GetComponentsInChildren<Transform>())
                        {
                            var e = tr.localRotation.eulerAngles;
                            float d = Mathf.Min(Mathf.Abs(e.x), Mathf.Abs(e.x > 180f ? 360f - e.x : e.x)) +
                                      Mathf.Min(Mathf.Abs(e.y), Mathf.Abs(e.y > 180f ? 360f - e.y : e.y));
                            if (d > maxBoneDelta) { maxBoneDelta = d; movedBone = tr.name; }
                        }
                        // Pose clips move bones a LOT (sitting = 45-270 deg).
                        Check(sp + ".pose_effect", maxBoneDelta > 30f,
                            $"{clip}: max bone delta {maxBoneDelta:F0}deg on {movedBone}");

                        // Interrupt: walking must drop the pose.
                        ani.walking = true;
                        brain.Tick(1f / 60f);
                        Check(sp + ".interrupt", !brain.InBehaviour,
                            "walking interrupts behaviour");
                    }
                }
                finally { Object.DestroyImmediate(go); }
            }
            Write();
        }

        static void Check(string goal, bool pass, string detail)
        {
            results.Add(new Json { goal = goal, pass = pass, detail = detail });
            Debug.Log($"[BehaviourVerify] {(pass ? "PASS" : "FAIL")} {goal}: {detail}");
        }

        static void Write()
        {
            int pass = 0;
            foreach (var r in results) if (r.pass) pass++;
            var sb = new System.Text.StringBuilder("{\"checks\":[");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"goal\":\"{r.goal}\",\"pass\":{r.pass.ToString().ToLower()},\"detail\":\"{r.detail}\"}}");
            }
            sb.Append($"],\"summary\":{{\"pass\":{pass},\"fail\":{results.Count - pass}}}}}");
            System.IO.File.WriteAllText(@"D:\zlj world\_logs\behaviour_verify.json", sb.ToString());
            Debug.Log($"[BehaviourVerify] SUMMARY pass={pass} fail={results.Count - pass}");
            Debug.Log($"[BehaviourVerify] RESULT: {(pass == results.Count ? "PASS" : "FAIL")}");
        }
    }
}
