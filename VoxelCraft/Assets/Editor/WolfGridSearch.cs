using UnityEditor;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Grid-search wolf geo bind_pose_rotation via MiniJson (no
    /// string hacks). 8 combos, hard vanilla-layout criteria, one run.</summary>
    public static class WolfGridSearch
    {
        public static void Run()
        {
            var asset = Resources.Load<TextAsset>("Geo/wolf.geo");
            var sb = new System.Text.StringBuilder();
            string best = null; float bestScore = -1e9f;

            foreach (float bodySign in new[] { 90f })
            foreach (float maneSign in new[] { 90f, -90f })
            foreach (float manePivZ in new[] { -6f,-5f,-4f,-3f,-2f,-1f,0f,1f,2f,3f,4f,5f,6f })
            foreach (float manePivY in new[] { 6f, 8f, 10f })
            {
                // -- load pre-generated variant (python wrote 8 files) -----
                string variantName = $"wolf_gs_{(int)bodySign}_{(int)maneSign}_{(int)manePivZ}_{(int)manePivY}";
                var ta = Resources.Load<TextAsset>("GeoTest/" + variantName);
                if (ta == null) { sb.AppendLine(variantName + ": variant asset missing"); continue; }

                var root = new GameObject("gs");
                var bRoot = new GameObject("Body").transform;
                bRoot.SetParent(root.transform, false);
                var skin = Art.CreatureTextureFactory.GetSkinMaterial("wolf_skin");
                bool ok = BedrockGeoImporter.Build(bRoot, ta, null, skin, 0f,
                    out var legs, out var tail, out var head, out var wings);
                if (!ok) { sb.AppendLine($"body{bodySign} mane{maneSign} pivZ{manePivZ}: BUILD FAIL"); Destroy(root); continue; }

                // -- measure by BONE-NAME subtree AABBs --------------------
                var boneT = root.GetComponentsInChildren<Transform>()
                    .Where(t => t.name == "body" || t.name == "upperBody" || t.name == "head")
                    .ToList();
                Bounds body = new Bounds(), mane = new Bounds(), headB = new Bounds();
                foreach (var bt in boneT)
                {
                    var b = SubtreeAabb(bt);
                    if (bt.name == "body") body = b;
                    else if (bt.name == "upperBody") mane = b;
                    else headB = b;
                }
                float px = 0.0625f;
                float coverLegs = body.min.z <= -0.37f ? 1f : 0f;
                float capTop = (mane.min.y >= body.min.y - 2 * px && mane.max.y <= body.max.y + 2 * px) ? 1f : 0f;
                float bridge = mane.max.z >= headB.min.z - 1.5f * px ? 1f : 0f;
                float overlapBody = (mane.min.z <= body.max.z + 0.5f * px) ? 1f : 0f; // mane sits ON body
                float score = coverLegs + capTop + bridge + overlapBody;
                sb.AppendLine($"body{bodySign:+0;-#} mane{maneSign:+0;-#} pivZ{manePivZ:+0;-#} pivY{manePivY:+0;-#}: " +
                    $"body z {body.min.z:F3}..{body.max.z:F3} y {body.min.y:F3}..{body.max.y:F3} | " +
                    $"mane z {mane.min.z:F3}..{mane.max.z:F3} y {mane.min.y:F3}..{mane.max.y:F3} | " +
                    $"headRear {headB.min.z:F3} | cover={coverLegs} cap={capTop} bridge={bridge} onBody={overlapBody} SCORE={score}");
                if (score > bestScore) { bestScore = score; best = $"body{bodySign} mane{maneSign} pivZ{manePivZ} pivY{manePivY}"; }
                Destroy(root);
            }
            sb.AppendLine("BEST: " + best + " score " + bestScore);
            Debug.Log("[WolfGridSearch]\n" + sb);
            System.IO.File.WriteAllText(@"D:\zlj world\_logs\wolf_grid_search.txt", sb.ToString());
        }

        static Bounds SubtreeAabb(Transform bone)
        {
            Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
            foreach (var r in bone.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var w = r.transform.localToWorldMatrix.MultiplyPoint3x4(v);
                    mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
                }
            }
            var b = new Bounds(); b.SetMinMax(mn, mx); return b;
        }

        static void Destroy(Object o) { Object.DestroyImmediate(o); }
    }
}
