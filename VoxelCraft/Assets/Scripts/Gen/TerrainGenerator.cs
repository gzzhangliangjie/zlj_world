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
        private readonly FastNoiseLite continentNoise;
        private readonly FastNoiseLite mountainNoise;
        private readonly FastNoiseLite roughnessNoise;
        private readonly FastNoiseLite biomeNoise;
        private readonly FastNoiseLite biomeRegions;

        public TerrainGenerator(int seed)
        {
            this.seed = seed;
            continentNoise = BuildSimplex(seed ^ 0x1a2b3c, 0.0015f, 4, warpAmp: 90f);
            mountainNoise = BuildSimplex(seed ^ 0x4d5e6f, 0.004f, 4, warpAmp: 25f);
            roughnessNoise = BuildSimplex(seed ^ 0x778899, 0.02f, 3, warpAmp: 0f);
            biomeNoise = BuildSimplex(seed ^ 0xaabbcc, 0.0025f, 3, warpAmp: 0f);
            biomeRegions = BuildCellular(seed ^ 0x33ccdd, 0.0018f);
        }

        private static FastNoiseLite BuildSimplex(int seed, float frequency, int octaves, float warpAmp)
        {
            var n = new FastNoiseLite();
            n.SetSeed(seed);
            n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            n.SetFrequency(frequency);
            n.SetFractalType(FastNoiseLite.FractalType.FBm);
            n.SetFractalOctaves(octaves);
            n.SetFractalLacunarity(2f);
            n.SetFractalGain(0.5f);
            if (warpAmp > 0f)
            {
                n.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
                n.SetDomainWarpAmp(warpAmp);
            }
            return n;
        }

        private static FastNoiseLite BuildCellular(int seed, float frequency)
        {
            var n = new FastNoiseLite();
            n.SetSeed(seed);
            n.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
            n.SetFrequency(frequency);
            n.SetCellularDistanceFunction(FastNoiseLite.CellularDistanceFunction.EuclideanSq);
            n.SetCellularReturnType(FastNoiseLite.CellularReturnType.CellValue);
            return n;
        }

        /// <summary>Terrain surface height (top solid block Y) at a world column.</summary>
        public int HeightAt(int wx, int wz)
        {
            float xf = wx, zf = wz;
            continentNoise.DomainWarp(ref xf, ref zf);
            float c = continentNoise.GetNoise(xf, zf) * 0.5f + 0.5f;

            float xm = wx, zm = wz;
            mountainNoise.DomainWarp(ref xm, ref zm);
            float m = mountainNoise.GetNoise(xm, zm) * 0.5f + 0.5f;
            float ridge = 1f - Mathf.Abs(2f * m - 1f);
            ridge *= ridge;

            float r = roughnessNoise.GetNoise(wx, wz) * 0.5f + 0.5f;

            float mountainMask = Mathf.Clamp01((c - 0.50f) * 5f);
            float h = 20f + c * 14f + ridge * 40f * mountainMask + r * 4f;
            return Mathf.Clamp(Mathf.FloorToInt(h), 1, MaxTerrainHeight);
        }

        /// <summary>Temperature/biome field in [0, 1]; high values are desert.
        /// Half smooth large-scale drift, half cellular regions so biome borders
        /// read as distinct zones instead of noise contours.</summary>
        public float BiomeAt(int wx, int wz)
        {
            float drift = biomeNoise.GetNoise(wx, wz) * 0.5f + 0.5f;
            float region = biomeRegions.GetNoise(wx, wz) * 0.5f + 0.5f;
            return drift * 0.6f + region * 0.4f;
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

        /// <summary>
        /// Fills the chunk voxel array with terrain, water, trees and .vox structures.
        /// </summary>
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
            StampStructures(chunk);
        }

        /// <summary>Structure names stamped during world gen (Resources/VoxStructures/*.bytes).</summary>
        public static readonly string[] StructureSet = { "cottage" };

        /// <summary>
        /// Stamps .vox structures near chunk borders deterministically: anchors come
        /// from a per-cell hash, the structure sits on the terrain surface at its
        /// anchor column, footprint clipped to this chunk.
        /// </summary>
        private void StampStructures(Chunk chunk)
        {
            int baseX = chunk.cx * VoxelMath.ChunkSize;
            int baseZ = chunk.cz * VoxelMath.ChunkSize;

            foreach (string name in StructureSet)
            {
                var vox = Vox.StructureRegistry.Load(name);
                if (vox == null)
                {
                    continue;
                }
                foreach (var anchor in Vox.StructureRegistry.AnchorsNear(
                    name, baseX, baseZ, baseX + VoxelMath.ChunkSize - 1, baseZ + VoxelMath.ChunkSize - 1, seed))
                {
                    int groundY = HeightAt(anchor.x, anchor.y);
                    if (groundY <= VoxelMath.SeaLevel + 1 || groundY >= SnowLine - 2 || IsDesert(anchor.x, anchor.y))
                    {
                        continue; // no cottages on beaches, underwater, on snow or in deserts
                    }
                    // flatness gate: if terrain rises more than 3 blocks across the
                    // footprint the walls would be buried — skip this anchor entirely
                    // (stays deterministic: same seed, same skip)
                    int maxH = groundY, minH = groundY;
                    for (int z = 0; z < vox.Depth; z++)
                    {
                        for (int x = 0; x < vox.Width; x++)
                        {
                            int h = HeightAt(anchor.x + x, anchor.y + z);
                            if (h > maxH) { maxH = h; }
                            if (h < minH) { minH = h; }
                        }
                    }
                    if (maxH - minH > 6)
                    {
                        continue;
                    }
                    // Flatten under the whole footprint: base at the HIGHEST terrain
                    // column of the footprint so no wall is buried on slopes; fill
                    // gaps below with dirt and clear any terrain bump above the base
                    // up past the roof so the full structure shows.
                    int baseY = groundY;
                    for (int z = -1; z <= vox.Depth; z++)
                    {
                        for (int x = -1; x <= vox.Width; x++)
                        {
                            baseY = Mathf.Max(baseY, HeightAt(anchor.x + x, anchor.y + z));
                        }
                    }
                    for (int z = -1; z <= vox.Depth; z++)
                    {
                        for (int x = -1; x <= vox.Width; x++)
                        {
                            int colTop = baseY - 1; // one-block lip around the structure
                            for (int y = groundY - 3; y < colTop; y++)
                            {
                                TrySet(chunk, anchor.x + x, y, anchor.y + z, BlockType.Dirt, overwrite: true);
                            }
                            for (int y = colTop; y <= baseY + vox.Height + 1; y++)
                            {
                                TrySet(chunk, anchor.x + x, y, anchor.y + z, BlockType.Air, overwrite: true);
                            }
                        }
                    }
                    for (int y = 0; y < vox.Height; y++)
                    {
                        for (int z = 0; z < vox.Depth; z++)
                        {
                            for (int x = 0; x < vox.Width; x++)
                            {
                                var type = vox.blocks[x, y, z];
                                if (type == BlockType.Air)
                                {
                                    continue;
                                }
                                TrySet(chunk, anchor.x + x, baseY + y, anchor.y + z, type, overwrite: true);
                            }
                        }
                    }

                    // M31 furniture pass: door in the doorway, torches on the front
                    // wall, a chest, a bed, and a fence patch by the entrance.
                    int fx = anchor.x, fz = anchor.y, fy = baseY;
                    // cottage layout: doorway at (x=4..6, z=0), windows z=3..5,
                    // interior floor y=1. All offsets mirror cottage.bytes.
                    // door (lower + upper) at x=5, z=0
                    TrySet(chunk, fx + 5, fy + 1, fz + 0, BlockType.DoorClosed, overwrite: true);
                    TrySet(chunk, fx + 5, fy + 2, fz + 0, BlockType.DoorClosed, overwrite: true);
                    // torches: two standing on the lip in front of the door (z=-1,
                    // OUT of the wall so they no longer punch holes in it), one on
                    // the interior floor next to the back wall
                    TrySet(chunk, fx + 2, fy + 0, fz - 1, BlockType.Torch, overwrite: false);
                    TrySet(chunk, fx + 8, fy + 0, fz - 1, BlockType.Torch, overwrite: false);
                    TrySet(chunk, fx + 5, fy + 1, fz + 6, BlockType.Torch, overwrite: false);
                    // chest against the back wall
                    TrySet(chunk, fx + 2, fy + 1, fz + 6, BlockType.Chest, overwrite: true);
                    // furnace beside the chest, facing the doorway (-Z)
                    TrySet(chunk, fx + 1, fy + 1, fz + 6, BlockType.Furnace, overwrite: true);
                    // bed in the back-right corner (foot toward the wall opening)
                    TrySet(chunk, fx + 8, fy + 1, fz + 6, BlockType.BedHead, overwrite: true);
                    TrySet(chunk, fx + 8, fy + 1, fz + 5, BlockType.BedFoot, overwrite: true);
                    // fence pen in front of the house (3x3 with a gap at the door path)
                    for (int pz = -3; pz <= -1; pz++)
                    {
                        for (int px = 3; px <= 7; px++)
                        {
                            if (px == 5 && pz == -3) { continue; } // gate gap
                            TrySet(chunk, fx + px, fy + 1, fz + pz, BlockType.Fence, overwrite: false);
                        }
                    }
                }
            }
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
