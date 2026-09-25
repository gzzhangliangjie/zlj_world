using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using System.Linq;
using System.Text;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Dump every mesh AABB of the assembled wolf (or given species)
    /// in world space - exact numbers for seam analysis.</summary>
    public static class WolfStructDump
    {
        [MenuItem("VoxelCraft/Dump Wolf Structure")]
        public static void Run()
        {
            var sb = new StringBuilder();
            foreach (var sp in new[] { "sheep", "fox" })
            {
                var go = new GameObject("dump_" + sp);
                var ani = go.AddComponent<BlockyAnimal>();
                ani.species = sp;
                ani.BuildModel();
                go.transform.rotation = Quaternion.identity;

                sb.AppendLine("== " + sp + " path=" + (go.GetComponentsInChildren<Transform>().Any(t => t.name == "leg0") ? "GEO" : "HANDBUILT"));
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var m = mf.sharedMesh;
                    var mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    foreach (var v in m.vertices)
                    {
                        var w = r.transform.localToWorldMatrix.MultiplyPoint3x4(v);
                        mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
                    }
                    sb.AppendLine(string.Format("{0,-18} chain={1} min=({2:F4},{3:F4},{4:F4}) max=({5:F4},{6:F4},{7:F4})",
                        r.name, string.Join("/", Ancestry(r.transform)), mn.x, mn.y, mn.z, mx.x, mx.y, mx.z));
                }
                Object.DestroyImmediate(go);
            }
            Debug.Log("[WolfStructDump]\n" + sb.ToString());
            System.IO.File.WriteAllText(@"D:\zlj world\_logs\wolf_struct_dump.txt", sb.ToString());
        }

        static string[] Ancestry(Transform t)
        {
            var names = new System.Collections.Generic.List<string>();
            var cur = t;
            while (cur != null && cur.name != "dump_wolf") { names.Add(cur.name); cur = cur.parent; }
            names.Reverse();
            return names.ToArray();
        }
    }
}
