using System.Linq;
using UnityEngine;
using UnityEditor;
using System.Text;

namespace VoxelCraft.Editor
{
    /// <summary>Debug: build a species, play a behaviour clip, tick N frames,
    /// dump per-bone local transform + mesh world AABB. Args via env:
    /// SITDUMP_SPECIES / SITDUMP_CLIP.</summary>
    public static class SitDump
    {
        public static void Run()
        {
            string sp = System.Environment.GetEnvironmentVariable("SITDUMP_SPECIES") ?? "ocelot";
            string clip = System.Environment.GetEnvironmentVariable("SITDUMP_CLIP") ?? "animation.ocelot.sit";
            var go = new GameObject("SitDump_" + sp);
            var ani = go.AddComponent<Creatures.BlockyAnimal>();
            ani.species = sp;
            ani.BuildModel();
            var player = go.GetComponent<Creatures.BedrockAnimationPlayer>();
            if (clip == "wingtrace")
            {
                player.moving = true;
                var wn = go.GetComponentsInChildren<Transform>()
                    .Where(t => t.name.ToLowerInvariant().Contains("wing"))
                    .OrderBy(t => t.name).ToArray();
                var sbw = new System.Text.StringBuilder();
                sbw.AppendLine("frame " + string.Join(" ", wn.Select(w => w.name)));
                for (int f = 0; f < 40; f++)
                {
                    player.Tick(1f / 30f);
                    sbw.Append($"f{f}");
                    foreach (var w in wn)
                    {
                        var e = w.localEulerAngles;
                        // World-space pivot (px) of each wing bone: a detached
                        // or hierarchy-broken tip only shows in world space.
                        var wp = w.position;
                        sbw.Append($" {w.name}({e.x:0.0},{e.y:0.0},{e.z:0.0})@({wp.x*16f:0.00},{wp.y*16f:0.00},{wp.z*16f:0.00})");
                    }
                    sbw.AppendLine();
                }
                System.IO.File.WriteAllText(@"D:\zlj world\_logs\wingtrace_" + sp + ".txt", sbw.ToString());
                UnityEngine.Object.DestroyImmediate(go);
                return;
            }
            if (clip == "walktrace")
            {
                player.moving = true;
                var leg = go.GetComponentsInChildren<Transform>()
                    .First(t => t.name == "leftLeg" || t.name == "leg0");
                var sb2 = new StringBuilder();
                for (int f = 0; f < 180; f++)
                {
                    go.transform.position += go.transform.forward * (ani.walkSpeed / 60f);
                    player.Tick(1f / 60f);
                    float tv = player.variables.TryGetValue("tcos0", out var tv2) ? tv2 : 0f;
                    sb2.AppendLine($"f{f} tcos0={tv:F2} legX={leg.localEulerAngles.x:F2}");
                }
                System.IO.File.WriteAllText($@"D:\zlj world\_logs\walktrace_{sp}.txt", sb2.ToString());
                Debug.Log("[SitDump] walktrace written");
                return;
            }
            if (clip != "none")
            {
                player.Play(clip);
                for (int f = 0; f < 30; f++) player.Tick(1f / 60f);
            }
            var sb = new StringBuilder();
            sb.AppendLine($"== {sp} / {clip} (after 30 ticks)");
            foreach (var tr in go.GetComponentsInChildren<Transform>())
            {
                if (tr == go.transform) continue;
                Vector3 lp = tr.localPosition, lr = tr.localEulerAngles;
                sb.AppendLine($"bone {tr.name,16} lpos=({lp.x:F3},{lp.y:F3},{lp.z:F3}) leul=({lr.x:F2},{lr.y:F2},{lr.z:F2})");
            }
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                var r = mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds();
                Vector3 c = mf.transform.TransformPoint(r.center);
                Vector3 ex = mf.transform.TransformVector(r.extents);
                sb.AppendLine($"cube {mf.name,14} center=({c.x:F3},{c.y:F3},{c.z:F3}) ext=({Mathf.Abs(ex.x):F3},{Mathf.Abs(ex.y):F3},{Mathf.Abs(ex.z):F3})");
            }
            System.IO.File.WriteAllText($@"D:\zlj world\_logs\sitdump_{sp}.txt", sb.ToString());
            Debug.Log("[SitDump] written sitdump_" + sp);
        }
    }
}
