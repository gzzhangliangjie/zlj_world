using UnityEngine;

namespace VoxelCraft.Art
{
    /// <summary>Small helper for building box-model creatures out of Unity cubes.</summary>
    public static class BoxBuilder
    {
        /// <summary>Creates a collider-free unit cube scaled to size at a local offset.</summary>
        public static GameObject Box(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        /// <summary>
        /// Minecraft-style skinned box: a mesh whose six faces map to per-face UV
        /// rectangles (texture pixels) of the supplied material. Face order:
        /// +X, -X, +Y, -Y, +Z, -Z; each Vector4 = (u, v, w, h) with v from TOP.
        /// faceRot (optional, per face) quarter-turns the rect content in 90
        /// degree steps counter-clockwise, for boxes stored rotated in the skin.
        /// </summary>
        public static GameObject SkinnedBox(Transform parent, string name, Vector3 localPosition, Vector3 size,
            Material material, Vector4[] faceUvPx, int texW, int texH, int[] faceRot = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;

            var verts = new Vector3[24];
            var uvs = new Vector2[24];
            var indices = new int[36];

            Vector3[][] corners =
            {
                new[] { new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1) }, // +X
                new[] { new Vector3(0,0,0), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0) }, // -X
                new[] { new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(0,1,0) }, // +Y
                new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(0,0,1) }, // -Y
                new[] { new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1) }, // +Z
                new[] { new Vector3(1,0,0), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0) }, // -Z
            };
            Vector2[] cornerUv = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };

            const float inset = 0.25f;
            for (int f = 0; f < 6; f++)
            {
                var r = faceUvPx[f];
                int rot = faceRot != null && f < faceRot.Length ? faceRot[f] & 3 : 0;
                float u0 = (r.x + inset) / texW;
                float u1 = (r.x + r.z - inset) / texW;
                float v1 = 1f - (r.y + inset) / texH;
                float v0 = 1f - (r.y + r.w - inset) / texH;
                for (int c = 0; c < 4; c++)
                {
                    int vi = f * 4 + c;
                    // Center the unit cube on the local origin so localScale=size
                    // grows the box symmetrically around localPosition (matches
                    // the CreatePrimitive convention Box() relies on).
                    verts[vi] = corners[f][c] - new Vector3(0.5f, 0.5f, 0.5f);
                    Vector2 s = RotateUvQuarter(rot, cornerUv[c].x, cornerUv[c].y);
                    uvs[vi] = new Vector2(
                        Mathf.Lerp(u0, u1, s.x),
                        Mathf.Lerp(v0, v1, s.y));
                }
                int b = f * 4;
                int o = f * 6;
                indices[o] = b; indices[o + 1] = b + 1; indices[o + 2] = b + 2;
                indices[o + 3] = b; indices[o + 4] = b + 2; indices[o + 5] = b + 3;
            }

            var mesh = new Mesh { name = name + "_mesh" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        /// <summary>
        /// Standard Minecraft box net: returns 6 UV rects for a W x H x D box whose
        /// net starts at pixel (u, v). Order: +X, -X, +Y, -Y, +Z, -Z.
        /// </summary>
        public static Vector4[] McNet(int u, int v, int W, int H, int D)
        {
            return new Vector4[]
            {
                new Vector4(u, v + D, D, H),                 // +X right side
                new Vector4(u + D + W, v + D, D, H),         // -X left side
                new Vector4(u + D, v, W, D),                 // +Y top
                new Vector4(u + D + W, v, W, D),             // -Y bottom
                new Vector4(u + D, v + D, W, H),             // +Z front
                new Vector4(u + 2 * D + W, v + D, W, H),     // -Z back
            };
        }

        /// <summary>
        /// UV net for a Minecraft quadruped torso. Those boxes are stored in the
        /// skin rotated +90 degrees about X (the long axis runs vertically in the
        /// texture), so the world top/bottom come from the MC north/south rects
        /// and the sides need a quarter turn. Pair with <see cref="QuadrupedBodyRots"/>.
        /// </summary>
        public static Vector4[] QuadrupedBodyNet(int u, int v, int W, int H, int D)
        {
            return new Vector4[]
            {
                new Vector4(u, v + D, D, H),                 // +X (mc east, rotated)
                new Vector4(u + D + W, v + D, D, H),         // -X (mc west, rotated)
                new Vector4(u + 2 * D + W, v + D, W, H),     // +Y (mc north -> world top)
                new Vector4(u + D, v + D, W, H),             // -Y (mc south -> world bottom)
                new Vector4(u + D, v, W, D),                 // +Z (mc top -> world front)
                new Vector4(u + D + W, v, W, D),             // -Z (mc bottom -> world back)
            };
        }

        /// <summary>Quarter-turns that keep a <see cref="QuadrupedBodyNet"/> upright.</summary>
        public static readonly int[] QuadrupedBodyRots = { 1, 3, 2, 0, 0, 0 };

        /// <summary>
        /// Maps face-local coords (a: horizontal, b: vertical up) into rect space
        /// so the rect content reads upright after rot quarter-turns.
        /// </summary>
        private static Vector2 RotateUvQuarter(int rot, float a, float b)
        {
            switch (rot)
            {
                case 1: return new Vector2(b, 1f - a);
                case 2: return new Vector2(1f - a, 1f - b);
                case 3: return new Vector2(1f - b, a);
                default: return new Vector2(a, b);
            }
        }
    }
}
