using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Gen
{
    /// <summary>
    /// Turns noise into chunk voxel data: height field with continents, ridged mountains
    /// and roughness; biome selection (desert / plains / snow); water fill up to sea level;
    /// deterministic trees whose canopies safely cross chunk borders (margin scan).
    /// </summary>
    public class TerrainGenerator
    {
        public const int SnowLine = 46;
        public const float DesertThreshold = 0.66f;
        public const float ColdWaterThreshold = 0.30f;
        public const int MaxTerrainHeight = 68; // leaves headroom for trees below ChunkHeight
        public const float TreeThreshold = 0.02f;

        private readonly int seed;
        private readonly Noise continentNoise;
        private readonly Noise mountainNoise;
        private readonly Noise roughnessNoise;
        private readonly Noise biomeNoise;

        public TerrainGenerator(int seed)
        {
            this.seed = seed;
            continentNoise = new Noise(seed ^ 0x1a2b3c);
            mountainNoise = new Noise(seed ^ 0x4d5e6f);
            roughnessNoise = new Noise(seed ^ 0x778899);
            biomeNoise = new Noise(seed ^ 0xaabbcc);
        }

        /// <summary>Terrain surface height (top solid block Y) at a world column.</summary>
        public int HeightAt(int wx, int wz)
        {
            float c = continentNoise.Fbm(wx * 0.0015f, wz * 0.0015f, 4);
            float m = mountainNoise.Fbm(wx * 0.004f, wz * 0.004f, 4);
            float ridge = 1f - Mathf.Abs(2f * m - 1f);
            ridge *= ridge;
            float r = roughnessNoise.Fbm(wx * 0.02f, wz * 0.02f, 3);
            float mountainMask = Mathf.Clamp01((c - 0.50f) * 5f);
            float h = 20f + c * 14f + ridge * 40f * mountainMask + r * 4f;
            return Mathf.Clamp(Mathf.FloorToInt(h), 1, MaxTerrainHeight);
        }

        /// <summary>Temperature/biome field in [0, 1]; high values are desert.</summary>
        public float BiomeAt(int wx, int wz)
        {
            return biomeNoise.Fbm(wx * 0.0025f, wz * 0.0025f, 3);
        }

        public bool IsDesert(int wx, int wz)
        {
            return BiomeAt(wx, wz) > DesertThreshold;
        }

        /// <summary>True when a tree anchor exists at this column (surface rules included).</summary>
        public bool TreeAt(int wx, int wz)
        {
            if (Noise.Hash01(wx, wz, seed ^ 0x51ab) >= TreeThreshold)
            {
                return false;
            }
            int h = HeightAt(wx, wz);
            if (h <= VoxelMath.SeaLevel + 1 || h >= SnowLine - 2)
            {
                return false;
            }
            return !IsDesert(wx, wz);
        }

        /// <summary>Fills the chunk voxel array with terrain, water and trees.</summary>
        public void Generate(Chunk chunk)
        {
            var blocks = chunk.blocks;
            int baseX = chunk.cx * VoxelMath.ChunkSize;
            int baseZ = chunk.cz * VoxelMath.ChunkSize;
            int sea = VoxelMath.SeaLevel;

            for (int lz = 0; lz < VoxelMath.ChunkSize; lz++)
            {
                for (int lx = 0; lx < VoxelMath.ChunkSize; lx++)
                {
                    int wx = baseX + lx;
                    int wz = baseZ + lz;
                    int h = HeightAt(wx, wz);
                    float temp = BiomeAt(wx, wz);
                    bool desert = temp > DesertThreshold;
                    bool beach = h <= sea + 1;
                    bool snowy = h >= SnowLine;

                    for (int y = 0; y <= h; y++)
                    {
                        byte block;
                        if (y == 0)
                        {
                            block = (byte)BlockType.Bedrock;
                        }
                        else if (y == h)
                        {
                            block = (byte)SurfaceBlock(beach, desert, snowy);
                        }
                        else if (y >= h - 3)
                        {
                            byte subsurface = (byte)(beach || desert ? BlockType.Sand : BlockType.Dirt);
                            block = subsurface;
                        }
                        else
                        {
                            block = SelectDeepBlock(wx, y, wz, h);
                        }
                        blocks[VoxelMath.LocalIndex(lx, y, lz)] = block;
                    }

                    for (int y = h + 1; y <= sea; y++)
                    {
                        bool surfaceIce = y == sea && temp < ColdWaterThreshold;
                        blocks[VoxelMath.LocalIndex(lx, y, lz)] =
                            (byte)(surfaceIce ? BlockType.Ice : BlockType.Water);
                    }
                }
            }

            PlantTrees(chunk);
        }

        private static byte SurfaceBlock(bool beach, bool desert, bool snowy)
        {
            if (beach || desert)
            {
                return (byte)BlockType.Sand;
            }
            if (snowy)
            {
                return (byte)BlockType.Snow;
            }
            return (byte)BlockType.Grass;
        }

        /// <summary>Deep stone with deterministic ores, gravel pockets and rare obsidian.</summary>
        private byte SelectDeepBlock(int wx, int y, int wz, int h)
        {
            float roll = Noise.Hash01(wx, wz, seed ^ (y * 668265263));
            if (roll < 0.0015f && y < 16)
            {
                return (byte)BlockType.DiamondOre;
            }
            if (roll < 0.0025f && y < 25)
            {
                return (byte)BlockType.GoldOre;
            }
            if (roll < 0.008f && y < 42)
            {
                return (byte)BlockType.IronOre;
            }
            if (roll < 0.016f && y < 52)
            {
                return (byte)BlockType.CoalOre;
            }
            if (roll > 0.9993f && y < 12)
            {
                return (byte)BlockType.Obsidian;
            }
            if (roll > 0.984f && y < h - 6)
            {
                return (byte)BlockType.Gravel;
            }
            return (byte)BlockType.Stone;
        }

        /// <summary>
        /// Scans tree anchors in a margin around the chunk so canopies crossing chunk
        /// borders appear identically from whichever chunk builds them.
        /// </summary>
        private void PlantTrees(Chunk chunk)
        {
            int baseX = chunk.cx * VoxelMath.ChunkSize;
            int baseZ = chunk.cz * VoxelMath.ChunkSize;
            const int margin = 3;

            for (int tz = baseZ - margin; tz < baseZ + VoxelMath.ChunkSize + margin; tz++)
            {
                for (int tx = baseX - margin; tx < baseX + VoxelMath.ChunkSize + margin; tx++)
                {
                    if (!TreeAt(tx, tz))
                    {
                        continue;
                    }

                    int h = HeightAt(tx, tz);
                    int trunk = 4 + (int)(Noise.Hash01(tx, tz, seed ^ 0x77aa) * 3f); // 4..6
                    int topLog = h + trunk;
                    if (topLog + 3 >= VoxelMath.ChunkHeight)
                    {
                        continue;
                    }

                    for (int dy = 1; dy <= trunk; dy++)
                    {
                        TrySet(chunk, tx, h + dy, tz, BlockType.Log, overwrite: true);
                    }

                    for (int dy = -2; dy <= 1; dy++)
                    {
                        int y = topLog + dy;
                        int radius = dy <= -1 ? 2 : 1;
                        bool cross = dy == 1;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            for (int dz = -radius; dz <= radius; dz++)
                            {
                                if (dx == 0 && dz == 0 && dy <= 0)
                                {
                                    continue; // trunk column
                                }
                                if (cross && Mathf.Abs(dx) + Mathf.Abs(dz) > 1)
                                {
                                    continue;
                                }
                                if (radius == 2 && Mathf.Abs(dx) == 2 && Mathf.Abs(dz) == 2)
                                {
                                    // deterministic corner trim
                                    if (Noise.Hash01(tx * 31 + dx, tz * 17 + dz, seed ^ 0xc0de) < 0.5f)
                                    {
                                        continue;
                                    }
                                }
                                TrySet(chunk, tx + dx, y, tz + dz, BlockType.Leaves, overwrite: false);
                            }
                        }
                    }
                }
            }
        }

        private static void TrySet(Chunk chunk, int wx, int y, int wz, BlockType type, bool overwrite)
        {
            int lx = wx - chunk.cx * VoxelMath.ChunkSize;
            int lz = wz - chunk.cz * VoxelMath.ChunkSize;
            if (lx < 0 || lx >= VoxelMath.ChunkSize || lz < 0 || lz >= VoxelMath.ChunkSize ||
                y < 0 || y >= VoxelMath.ChunkHeight)
            {
                return;
            }
            int idx = VoxelMath.LocalIndex(lx, y, lz);
            if (!overwrite && chunk.blocks[idx] != (byte)BlockType.Air)
            {
                return;
            }
            chunk.blocks[idx] = (byte)type;
        }
    }
}
