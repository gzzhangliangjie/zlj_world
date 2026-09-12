using UnityEngine;
using VoxelCraft.Core;

namespace VoxelCraft.World
{
    /// <summary>
    /// Turns chunk voxel data into renderable mesh buffers:
    /// hidden-face culling, per-corner ambient occlusion baked into vertex colors,
    /// directional face shading, atlas UVs, lowered water surface, quad-flip for AO.
    /// Pure C# (no GameObjects) so it can be regression-tested in batch mode.
    /// </summary>
    public static class ChunkMesher
    {
        private static readonly Vector3Int[] Normals =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        // Corner order per face: counter-clockwise seen from outside, first two at the
        // face's "bottom" so the shared UV table keeps textures upright on side faces.
        private static readonly Vector3Int[][] Corners =
        {
            new[] { new Vector3Int(1,0,1), new Vector3Int(1,0,0), new Vector3Int(1,1,0), new Vector3Int(1,1,1) }, // +X
            new[] { new Vector3Int(0,0,0), new Vector3Int(0,0,1), new Vector3Int(0,1,1), new Vector3Int(0,1,0) }, // -X
            new[] { new Vector3Int(0,1,1), new Vector3Int(1,1,1), new Vector3Int(1,1,0), new Vector3Int(0,1,0) }, // +Y
            new[] { new Vector3Int(0,0,0), new Vector3Int(1,0,0), new Vector3Int(1,0,1), new Vector3Int(0,0,1) }, // -Y
            new[] { new Vector3Int(0,0,1), new Vector3Int(1,0,1), new Vector3Int(1,1,1), new Vector3Int(0,1,1) }, // +Z
            new[] { new Vector3Int(1,0,0), new Vector3Int(0,0,0), new Vector3Int(0,1,0), new Vector3Int(1,1,0) }, // -Z
        };

        private static readonly float[] Shade = { 0.80f, 0.80f, 1.00f, 0.55f, 0.70f, 0.70f };
        private static readonly Vector2[] CornerUv = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };

        public static void Build(Chunk chunk, WorldSim sim, Rect[] tileRects, MeshData solid, MeshData water)
        {
            solid.Clear();
            water.Clear();

            int baseX = chunk.cx * VoxelMath.ChunkSize;
            int baseZ = chunk.cz * VoxelMath.ChunkSize;
            int size = VoxelMath.ChunkSize;
            int height = VoxelMath.ChunkHeight;

            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var block = chunk.GetLocal(x, y, z);
                        if (block == BlockType.Air)
                        {
                            continue;
                        }

                        var def = BlockDatabase.Get(block);
                        bool isWater = block == BlockType.Water;
                        MeshData target = isWater ? water : solid;
                        int wx = baseX + x;
                        int wz = baseZ + z;
                        bool lowerWaterTop = isWater && sim.GetBlock(wx, y + 1, wz) != BlockType.Water;

                        for (int f = 0; f < 6; f++)
                        {
                            var n = Normals[f];
                            var neighbour = sim.GetBlock(wx + n.x, y + n.y, wz + n.z);
                            if (!FaceVisible(block, def.opaque, neighbour))
                            {
                                continue;
                            }

                            var rect = tileRects[(int)def.FaceTile(n)];
                            int axis = f < 2 ? 0 : f < 4 ? 1 : 2;
                            int t1 = (axis + 1) % 3;
                            int t2 = (axis + 2) % 3;
                            int nx = wx + n.x;
                            int ny = y + n.y;
                            int nz = wz + n.z;

                            int vertexBase = target.Vertices.Count;
                            int ao0 = 3, ao1 = 3, ao2 = 3, ao3 = 3;
                            float alpha = isWater ? 0.72f : 1f;

                            for (int c = 0; c < 4; c++)
                            {
                                var corner = Corners[f][c];
                                float vy = corner.y;
                                if (lowerWaterTop && corner.y == 1)
                                {
                                    vy = 0.9f;
                                }
                                target.Vertices.Add(new Vector3(x + corner.x, y + vy, z + corner.z));
                                target.Uvs.Add(new Vector2(
                                    rect.x + CornerUv[c].x * rect.width,
                                    rect.y + CornerUv[c].y * rect.height));

                                float light = Shade[f];
                                if (def.opaque)
                                {
                                    int ao = CornerAo(sim, nx, ny, nz, axis, t1, t2, corner);
                                    light *= 0.55f + 0.15f * ao;
                                    if (c == 0) ao0 = ao;
                                    else if (c == 1) ao1 = ao;
                                    else if (c == 2) ao2 = ao;
                                    else ao3 = ao;
                                }
                                byte l = (byte)(Mathf.Clamp01(light) * 255f);
                                target.Colors.Add(new Color32(l, l, l, (byte)(alpha * 255f)));
                            }

                            // Flip the quad diagonal when it produces cleaner AO gradients.
                            if (ao0 + ao2 > ao1 + ao3)
                            {
                                target.Indices.Add(vertexBase);
                                target.Indices.Add(vertexBase + 1);
                                target.Indices.Add(vertexBase + 2);
                                target.Indices.Add(vertexBase);
                                target.Indices.Add(vertexBase + 2);
                                target.Indices.Add(vertexBase + 3);
                            }
                            else
                            {
                                target.Indices.Add(vertexBase + 1);
                                target.Indices.Add(vertexBase + 2);
                                target.Indices.Add(vertexBase + 3);
                                target.Indices.Add(vertexBase + 1);
                                target.Indices.Add(vertexBase + 3);
                                target.Indices.Add(vertexBase);
                            }
                        }
                    }
                }
            }
        }

        private static bool FaceVisible(BlockType block, bool blockOpaque, BlockType neighbour)
        {
            if (block == BlockType.Water)
            {
                if (neighbour == BlockType.Air)
                {
                    return true;
                }
                return !BlockDatabase.IsOpaque(neighbour) && neighbour != BlockType.Water && neighbour != BlockType.Glass;
            }
            if (blockOpaque)
            {
                return !BlockDatabase.IsOpaque(neighbour);
            }
            // Transparent solids (glass): cull against own type, show otherwise.
            return !BlockDatabase.IsOpaque(neighbour) && neighbour != block;
        }

        /// <summary>Classic 3-neighbour corner AO. Returns occlusion level 0 (darkest) to 3 (open).</summary>
        private static int CornerAo(WorldSim sim, int nx, int ny, int nz, int axis, int t1, int t2, Vector3Int corner)
        {
            int d1 = Comp(corner, t1) == 1 ? 1 : -1;
            int d2 = Comp(corner, t2) == 1 ? 1 : -1;
            var o1 = Offset(t1, d1);
            var o2 = Offset(t2, d2);
            bool s1 = BlockDatabase.IsOpaque(sim.GetBlock(nx + o1.x, ny + o1.y, nz + o1.z));
            bool s2 = BlockDatabase.IsOpaque(sim.GetBlock(nx + o2.x, ny + o2.y, nz + o2.z));
            if (s1 && s2)
            {
                return 0;
            }
            bool c = BlockDatabase.IsOpaque(sim.GetBlock(nx + o1.x + o2.x, ny + o1.y + o2.y, nz + o1.z + o2.z));
            return 3 - ((s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0));
        }

        private static int Comp(Vector3Int v, int axis)
        {
            return axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        }

        private static Vector3Int Offset(int axis, int dir)
        {
            return axis == 0 ? new Vector3Int(dir, 0, 0) : axis == 1 ? new Vector3Int(0, dir, 0) : new Vector3Int(0, 0, dir);
        }
    }
}
