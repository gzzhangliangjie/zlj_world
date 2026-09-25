using UnityEditor;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>Dumps the fox Head mesh face rects (pixel coords) so we can
    /// see exactly which texture region each face samples.</summary>
    public static class FoxFaceDebug
    {
        public static void Run()
        {
            var go = new GameObject("FoxDebug");
            var ani = go.AddComponent<BlockyAnimal>();
            ani.species = "fox";
            ani.BuildModel();

            foreach (var name in new[] { "Head", "Snout", "Ear0", "Ear1", "Body" })
            {
                var t = go.transform.Find("Body/" + name);
                if (t == null) { Debug.Log($"[FoxFaceDebug] {name}: NOT FOUND under Body/"); continue; }
                var mf = t.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) { Debug.Log($"[FoxFaceDebug] {name}: no mesh"); continue; }
                var m = mf.sharedMesh;
                var uv = m.uv;
                // 24 verts: 6 faces x 4 corners, face order +X,-X,+Y,-Y,+Z,-Z
                string[] fn = { "+X", "-X", "+Y(top)", "-Y(bot)", "+Z(front)", "-Z(back)" };
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[FoxFaceDebug] {name} (verts {m.vertexCount}):");
                for (int f = 0; f < 6; f++)
                {
                    float minU = 1e9f, maxU = -1e9f, minV = 1e9f, maxV = -1e9f;
                    for (int c = 0; c < 4; c++)
                    {
                        var p = uv[f * 4 + c];
                        minU = Mathf.Min(minU, p.x); maxU = Mathf.Max(maxU, p.x);
                        minV = Mathf.Min(minV, p.y); maxV = Mathf.Max(maxV, p.y);
                    }
                    // unity uv -> png pixel coords (64x32, v origin bottom)
                    float px0 = minU * 64f, px1 = maxU * 64f;
                    float py0 = (1f - maxV) * 32f, py1 = (1f - minV) * 32f;
                    sb.AppendLine($"  {fn[f]}: png x {px0:F1}..{px1:F1}  y {py0:F1}..{py1:F1}");
                }
                Debug.Log(sb.ToString());
            }
            // dump every renderer's material + texture stats
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var m = r.sharedMaterial;
                if (m == null) { Debug.Log($"[FoxFaceDebug] {r.name}: MATERIAL NULL"); continue; }
                var t = m.mainTexture;
                if (t == null) { Debug.Log($"[FoxFaceDebug] {r.name}: shader={m.shader?.name} TEX NULL"); continue; }
                var t2 = t as Texture2D;
                string avg = "n/a";
                if (t2 != null)
                {
                    var p = t2.GetPixels();
                    float rr = 0, gg = 0, bb = 0; int n = 0;
                    foreach (var q in p) { rr += q.r; gg += q.g; bb += q.b; n++; }
                    avg = $"avg({rr / n:F2},{gg / n:F2},{bb / n:F2}) {t2.width}x{t2.height}";
                }
                Debug.Log($"[FoxFaceDebug] {r.name}: shader={m.shader?.name} tex={t.name} {avg}");
            }
            Object.DestroyImmediate(go);
            Debug.Log("[FoxFaceDebug] RESULT: DONE");
        }
    }
}
