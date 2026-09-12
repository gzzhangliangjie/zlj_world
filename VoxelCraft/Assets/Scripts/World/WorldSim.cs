using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Gen;

namespace VoxelCraft.World
{
    /// <summary>
    /// Pure world simulation: chunk storage, deterministic generation, budgeted
    /// streaming (data ring wider than mesh ring), 8-neighbour readiness gating,
    /// block edits with neighbour invalidation, and far-chunk unloading.
    /// No Unity GameObjects live here; WorldRoot applies mesh data to the scene.
    /// </summary>
    public class WorldSim
    {
        public readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        public readonly TerrainGenerator generator;
        public readonly int seed;

        public int dataRadius = 8;
        public int meshRadius = 7;
        public int unloadRadius = 10;

        /// <summary>Atlas tile rectangles indexed by TileId; set by WorldRoot or tests before Step.</summary>
        public Rect[] tileRects;

        private readonly List<(int cx, int cz, int dist)> wanted = new List<(int, int, int)>(256);
        private readonly List<Chunk> meshable = new List<Chunk>(256);

        public WorldSim(int seed)
        {
            this.seed = seed;
            generator = new TerrainGenerator(seed);
        }

        public Chunk GetChunk(int cx, int cz)
        {
            chunks.TryGetValue(Chunk.Key(cx, cz), out Chunk c);
            return c;
        }

        public void GenerateChunkData(int cx, int cz)
        {
            long key = Chunk.Key(cx, cz);
            if (chunks.ContainsKey(key))
            {
                return;
            }
            var chunk = new Chunk(cx, cz);
            generator.Generate(chunk);
            chunk.dataReady = true;
            chunks[key] = chunk;
        }

        /// <summary>Global block getter; missing chunks or out-of-range Y read as Air.</summary>
        public BlockType GetBlock(int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= VoxelMath.ChunkHeight)
            {
                return BlockType.Air;
            }
            var chunk = GetChunk(VoxelMath.ChunkCoord(wx), VoxelMath.ChunkCoord(wz));
            if (chunk == null || !chunk.dataReady)
            {
                return BlockType.Air;
            }
            return chunk.GetLocal(
                VoxelMath.Mod(wx, VoxelMath.ChunkSize),
                wy,
                VoxelMath.Mod(wz, VoxelMath.ChunkSize));
        }

        /// <summary>Sets a block and marks every mesh whose geometry can change for rebuild.</summary>
        public void SetBlock(int wx, int wy, int wz, BlockType type, List<Chunk> affected)
        {
            if (wy < 0 || wy >= VoxelMath.ChunkHeight)
            {
                return;
            }
            int cx = VoxelMath.ChunkCoord(wx);
            int cz = VoxelMath.ChunkCoord(wz);
            var chunk = GetChunk(cx, cz);
            if (chunk == null)
            {
                return;
            }
            int lx = VoxelMath.Mod(wx, VoxelMath.ChunkSize);
            int lz = VoxelMath.Mod(wz, VoxelMath.ChunkSize);
            chunk.SetLocal(lx, wy, lz, type);
            MarkDirty(chunk, affected);

            // Only blocks on a border (or its corners) can change neighbour geometry.
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }
                    bool xOk = dx == 0 || (dx < 0 ? lx == 0 : lx == VoxelMath.ChunkSize - 1);
                    bool zOk = dz == 0 || (dz < 0 ? lz == 0 : lz == VoxelMath.ChunkSize - 1);
                    if (xOk && zOk)
                    {
                        MarkDirty(GetChunk(cx + dx, cz + dz), affected);
                    }
                }
            }
        }

        private static void MarkDirty(Chunk chunk, List<Chunk> affected)
        {
            if (chunk == null)
            {
                return;
            }
            chunk.meshDirty = true;
            if (affected != null && !affected.Contains(chunk))
            {
                affected.Add(chunk);
            }
        }

        public bool NeighborsReady(int cx, int cz)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }
                    var c = GetChunk(cx + dx, cz + dz);
                    if (c == null || !c.dataReady)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Advances streaming around a center chunk with per-phase millisecond budgets.
        /// Remeshed chunks carry fresh MeshData; unloaded chunks must be torn down by the caller.
        /// </summary>
        public void Step(int centerCx, int centerCz, float dataBudgetMs, float meshBudgetMs, List<Chunk> remeshed, List<Chunk> unloaded)
        {
            // --- data generation pass (missing chunks, nearest first) ---
            wanted.Clear();
            for (int dz = -dataRadius; dz <= dataRadius; dz++)
            {
                for (int dx = -dataRadius; dx <= dataRadius; dx++)
                {
                    int d = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
                    if (d > dataRadius)
                    {
                        continue;
                    }
                    int cx = centerCx + dx;
                    int cz = centerCz + dz;
                    if (!chunks.ContainsKey(Chunk.Key(cx, cz)))
                    {
                        wanted.Add((cx, cz, d));
                    }
                }
            }
            if (wanted.Count > 0)
            {
                wanted.Sort((a, b) => a.dist.CompareTo(b.dist));
                long budget = TicksOf(dataBudgetMs);
                var sw = Stopwatch.StartNew();
                foreach (var (cx, cz, _) in wanted)
                {
                    if (sw.ElapsedTicks > budget)
                    {
                        break;
                    }
                    GenerateChunkData(cx, cz);
                }
            }

            // --- mesh building pass (dirty, ready, inside mesh radius, nearest first) ---
            meshable.Clear();
            foreach (var kv in chunks)
            {
                var c = kv.Value;
                if (!c.dataReady || !c.meshDirty)
                {
                    continue;
                }
                int d = Mathf.Max(Mathf.Abs(c.cx - centerCx), Mathf.Abs(c.cz - centerCz));
                if (d > meshRadius || !NeighborsReady(c.cx, c.cz))
                {
                    continue;
                }
                meshable.Add(c);
            }
            if (meshable.Count > 0)
            {
                meshable.Sort((a, b) =>
                    Mathf.Max(Mathf.Abs(a.cx - centerCx), Mathf.Abs(a.cz - centerCz))
                    .CompareTo(Mathf.Max(Mathf.Abs(b.cx - centerCx), Mathf.Abs(b.cz - centerCz))));
                long budget = TicksOf(meshBudgetMs);
                var sw = Stopwatch.StartNew();
                foreach (var chunk in meshable)
                {
                    if (sw.ElapsedTicks > budget)
                    {
                        break;
                    }
                    if (!chunk.meshDirty)
                    {
                        continue;
                    }
                    if (chunk.solidMeshData == null)
                    {
                        chunk.solidMeshData = new MeshData();
                        chunk.waterMeshData = new MeshData();
                    }
                    ChunkMesher.Build(chunk, this, tileRects, chunk.solidMeshData, chunk.waterMeshData);
                    chunk.meshDirty = false;
                    chunk.meshBuilt = true;
                    remeshed?.Add(chunk);
                }
            }

            // --- unload pass ---
            if (unloaded != null)
            {
                List<long> removeKeys = null;
                foreach (var kv in chunks)
                {
                    var c = kv.Value;
                    int d = Mathf.Max(Mathf.Abs(c.cx - centerCx), Mathf.Abs(c.cz - centerCz));
                    if (d > unloadRadius)
                    {
                        (removeKeys ?? (removeKeys = new List<long>())).Add(kv.Key);
                        unloaded.Add(c);
                    }
                }
                if (removeKeys != null)
                {
                    foreach (long key in removeKeys)
                    {
                        chunks.Remove(key);
                    }
                }
            }
        }

        /// <summary>
        /// Amanatides-Woo voxel DDA raycast. Returns the first non-liquid solid voxel
        /// within maxDistance plus the adjacent (empty) voxel for placement.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3Int hitVoxel, out Vector3Int placeVoxel)
        {
            hitVoxel = default;
            placeVoxel = default;
            Vector3 dir = direction.normalized;

            int x = Mathf.FloorToInt(origin.x);
            int y = Mathf.FloorToInt(origin.y);
            int z = Mathf.FloorToInt(origin.z);
            int px = x, py = y, pz = z;

            int stepX = dir.x >= 0 ? 1 : -1;
            int stepY = dir.y >= 0 ? 1 : -1;
            int stepZ = dir.z >= 0 ? 1 : -1;

            float tMaxX = Mathf.Abs(dir.x) > 1e-8f ? (stepX > 0 ? x + 1f - origin.x : origin.x - x) / Mathf.Abs(dir.x) : float.PositiveInfinity;
            float tMaxY = Mathf.Abs(dir.y) > 1e-8f ? (stepY > 0 ? y + 1f - origin.y : origin.y - y) / Mathf.Abs(dir.y) : float.PositiveInfinity;
            float tMaxZ = Mathf.Abs(dir.z) > 1e-8f ? (stepZ > 0 ? z + 1f - origin.z : origin.z - z) / Mathf.Abs(dir.z) : float.PositiveInfinity;
            float tDeltaX = Mathf.Abs(dir.x) > 1e-8f ? 1f / Mathf.Abs(dir.x) : float.PositiveInfinity;
            float tDeltaY = Mathf.Abs(dir.y) > 1e-8f ? 1f / Mathf.Abs(dir.y) : float.PositiveInfinity;
            float tDeltaZ = Mathf.Abs(dir.z) > 1e-8f ? 1f / Mathf.Abs(dir.z) : float.PositiveInfinity;

            BlockType startBlock = GetBlock(x, y, z);
            if (startBlock != BlockType.Air && !BlockDatabase.IsLiquid(startBlock))
            {
                // Head inside a block: hit it, placement is invalid (same voxel).
                hitVoxel = new Vector3Int(x, y, z);
                placeVoxel = new Vector3Int(x, y, z);
                return true;
            }

            float t = 0f;
            for (int i = 0; i < 512; i++)
            {
                px = x;
                py = y;
                pz = z;
                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    x += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY < tMaxZ)
                {
                    y += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                }

                if (t > maxDistance)
                {
                    return false;
                }

                var block = GetBlock(x, y, z);
                if (block != BlockType.Air && !BlockDatabase.IsLiquid(block))
                {
                    hitVoxel = new Vector3Int(x, y, z);
                    placeVoxel = new Vector3Int(px, py, pz);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Y of the highest solid non-liquid block in a column, or -1 if unknown.</summary>
        public int SurfaceHeight(int wx, int wz)
        {
            var chunk = GetChunk(VoxelMath.ChunkCoord(wx), VoxelMath.ChunkCoord(wz));
            if (chunk == null || !chunk.dataReady)
            {
                return -1;
            }
            int lx = VoxelMath.Mod(wx, VoxelMath.ChunkSize);
            int lz = VoxelMath.Mod(wz, VoxelMath.ChunkSize);
            for (int y = VoxelMath.ChunkHeight - 1; y >= 0; y--)
            {
                var b = chunk.GetLocal(lx, y, lz);
                if (b != BlockType.Air && !BlockDatabase.IsLiquid(b))
                {
                    return y;
                }
            }
            return -1;
        }

        private static long TicksOf(float ms)
        {
            return (long)(ms * Stopwatch.Frequency / 1000f);
        }
    }
}
