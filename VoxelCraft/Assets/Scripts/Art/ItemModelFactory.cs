using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Items;

namespace VoxelCraft.Art
{
    /// <summary>
    /// Builds and caches 3D models for held items. Tools and food use
    /// Minecraft-style flat items: their 24x24 pixel icon extruded into a solid
    /// mesh. Block items become a small cube textured from the block atlas with
    /// the block's real top/side/bottom tiles. Instances are cached per item id
    /// and reused by the held-item view.
    /// </summary>
    public static class ItemModelFactory
    {
        private static Shader unlit;
        private static readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>();

        public static GameObject Get(string id)
        {
            return cache.TryGetValue(id, out var go) && go != null ? go : null;
        }

        public static GameObject BuildTool(ToolType tool)
        {
            return BuildFlat("tool:" + (int)tool, ToolIcons.Get(tool));
        }

        public static GameObject BuildFood(string item)
        {
            return BuildFlat("item:" + item, ToolIcons.GetItem(item));
        }

        /// <summary>Small cube of a real block, textured from the atlas tiles.</summary>
        public static GameObject BuildBlock(TextureFactory.AtlasResult atlas, BlockType type)
        {
            string id = "block:" + (int)type;
            if (cache.TryGetValue(id, out var cached) && cached != null)
            {
                return cached;
            }
            if (atlas == null || atlas.atlas == null)
            {
                return null;
            }
            var def = BlockDatabase.Get(type);
            var full = new Vector4[6];
            full[0] = AtlasPxRect(def.side);
            full[1] = AtlasPxRect(def.side);
            full[2] = AtlasPxRect(def.top);
            full[3] = AtlasPxRect(def.bottom);
            full[4] = AtlasPxRect(def.side);
            full[5] = AtlasPxRect(def.side);
            var go = BoxBuilder.SkinnedBox(null, "Held_" + type, Vector3.zero, Vector3.one * 0.3f,
                MaterialFor(atlas.atlas), full,
                TextureFactory.AtlasCols * TextureFactory.TileSize,
                TextureFactory.AtlasRows * TextureFactory.TileSize);
            go.SetActive(false);
            cache[id] = go;
            return go;
        }

        private static Vector4 AtlasPxRect(TileId tile)
        {
            int col = (int)tile % TextureFactory.AtlasCols;
            int row = (int)tile / TextureFactory.AtlasCols;
            return new Vector4(
                col * TextureFactory.TileSize,
                (TextureFactory.AtlasRows - 1 - row) * TextureFactory.TileSize,
                TextureFactory.TileSize, TextureFactory.TileSize);
        }

        /// <summary>
        /// Extrudes an alpha icon into a solid double-sided pixel mesh:
        /// front + back quads per opaque pixel, plus side quads on exposed
        /// edges (the classic flat-item look, one draw call).
        /// </summary>
        private static GameObject BuildFlat(string id, Texture2D icon)
        {
            if (cache.TryGetValue(id, out var cached) && cached != null)
            {
                return cached;
            }
            if (icon == null)
            {
                return null;
            }
            int w = icon.width;
            int h = icon.height;
            var px = icon.GetPixels32();
            float cell = 0.02f;   // world size of one icon pixel
            float halfT = 0.02f;  // half thickness
            float ox = -w * cell * 0.5f;
            float oy = -h * cell * 0.5f;

            var verts = new List<Vector3>(256);
            var uvs = new List<Vector2>(256);
            var tris = new List<int>(512);

            bool Solid(int x, int y)
            {
                return x >= 0 && y >= 0 && x < w && y < h && px[y * w + x].a > 127;
            }
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int b0 = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
                tris.Add(b0); tris.Add(b0 + 1); tris.Add(b0 + 2);
                tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 3);
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!Solid(x, y))
                    {
                        continue;
                    }
                    float x0 = ox + x * cell, x1 = x0 + cell;
                    float y0 = oy + y * cell, y1 = y0 + cell;
                    float u0 = x / (float)w, u1 = (x + 1) / (float)w;
                    float v0 = y / (float)h, v1 = (y + 1) / (float)h;
                    var uvA = new Vector2(u0, v0);
                    var uvB = new Vector2(u1, v0);
                    var uvC = new Vector2(u1, v1);
                    var uvD = new Vector2(u0, v1);
                    // front (+Z)
                    Quad(new Vector3(x0, y0, halfT), new Vector3(x1, y0, halfT),
                        new Vector3(x1, y1, halfT), new Vector3(x0, y1, halfT), uvA, uvB, uvC, uvD);
                    // back (-Z), reversed winding, mirrored U
                    Quad(new Vector3(x1, y0, -halfT), new Vector3(x0, y0, -halfT),
                        new Vector3(x0, y1, -halfT), new Vector3(x1, y1, -halfT), uvB, uvA, uvD, uvC);
                    if (!Solid(x + 1, y))
                    {
                        Quad(new Vector3(x1, y0, halfT), new Vector3(x1, y0, -halfT),
                            new Vector3(x1, y1, -halfT), new Vector3(x1, y1, halfT), uvB, uvB, uvC, uvC);
                    }
                    if (!Solid(x - 1, y))
                    {
                        Quad(new Vector3(x0, y0, -halfT), new Vector3(x0, y0, halfT),
                            new Vector3(x0, y1, halfT), new Vector3(x0, y1, -halfT), uvA, uvA, uvD, uvD);
                    }
                    if (!Solid(x, y + 1))
                    {
                        Quad(new Vector3(x0, y1, halfT), new Vector3(x1, y1, halfT),
                            new Vector3(x1, y1, -halfT), new Vector3(x0, y1, -halfT), uvD, uvC, uvC, uvD);
                    }
                    if (!Solid(x, y - 1))
                    {
                        Quad(new Vector3(x0, y0, -halfT), new Vector3(x1, y0, -halfT),
                            new Vector3(x1, y0, halfT), new Vector3(x0, y0, halfT), uvA, uvB, uvB, uvA);
                    }
                }
            }

            var mesh = new Mesh { name = "Held_" + id };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject("Held_" + id);
            go.transform.localPosition = Vector3.zero;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = MaterialFor(icon);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            cache[id] = go;
            return go;
        }

        private static Material MaterialFor(Texture2D tex)
        {
            if (unlit == null)
            {
                unlit = Resources.Load<Shader>("Shaders/UnlitTextureShader");
                if (unlit == null)
                {
                    unlit = Shader.Find("Unlit/Texture"); // editor-only fallback
                }
            }
            return new Material(unlit) { mainTexture = tex };
        }
    }
}
