using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Items
{
    /// <summary>
    /// Farming: seed placement on soil, timed growth through 4 wheat stages, and
    /// pickaxe harvesting that yields carrots (+ seeds back). Planted crops
    /// survive chunk reloads via replay when their chunk regenerates.
    /// </summary>
    public static class Crops
    {
        public const int GrowthStages = 4;         // Wheat0..Wheat3
        public const float StageSeconds = 30f;     // seconds per growth stage
        public const float SeedChanceFromGrass = 0.35f;

        private static readonly List<Vector3Int> planted = new List<Vector3Int>();
        private static readonly List<float> stageStart = new List<float>();
        private static float clock;

        public static bool IsWheat(BlockType t)
        {
            return t >= BlockType.Wheat0 && t <= BlockType.Wheat3;
        }

        public static bool IsMature(BlockType t)
        {
            return t == BlockType.Wheat3;
        }

        public static bool IsSoil(BlockType t)
        {
            return t == BlockType.Grass || t == BlockType.Dirt;
        }

        /// <summary>Registers a freshly planted crop (block already set by the caller).</summary>
        public static void Track(Vector3Int pos)
        {
            if (!planted.Contains(pos))
            {
                planted.Add(pos);
                stageStart.Add(Time.time);
            }
        }

        /// <summary>Forgets a crop (harvested or overwritten).</summary>
        public static void Forget(Vector3Int pos)
        {
            int i = planted.IndexOf(pos);
            if (i >= 0)
            {
                planted.RemoveAt(i);
                stageStart.RemoveAt(i);
            }
        }

        /// <summary>
        /// Called once per second by WorldRoot: advances mature-time crops and
        /// replants crops whose chunk regenerated without them.
        /// Returns the list of (pos, newBlock) edits to apply.
        /// </summary>
        public static List<(Vector3Int pos, BlockType block)> Tick(WorldSim sim)
        {
            var edits = new List<(Vector3Int, BlockType)>();
            clock += 1f;

            for (int i = planted.Count - 1; i >= 0; i--)
            {
                var pos = planted[i];
                var current = sim.GetBlock(pos.x, pos.y, pos.z);
                if (!IsWheat(current))
                {
                    // chunk regenerated and wiped the crop -> replant if soil below remains
                    var below = sim.GetBlock(pos.x, pos.y - 1, pos.z);
                    if (IsSoil(below) && sim.GetBlock(pos.x, pos.y + 1, pos.z) == BlockType.Air)
                    {
                        edits.Add((pos, BlockType.Wheat0));
                        stageStart[i] = Time.time;
                    }
                    else
                    {
                        Forget(pos);
                    }
                    continue;
                }

                int stage = current - BlockType.Wheat0;
                if (stage >= GrowthStages - 1 || Time.time - stageStart[i] < StageSeconds)
                {
                    continue;
                }
                var next = (BlockType)(current + 1);
                edits.Add((pos, next));
                if (next == BlockType.Wheat3)
                {
                    stageStart[i] = Time.time; // stays mature
                }
                else
                {
                    stageStart[i] = Time.time;
                }
            }
            return edits;
        }

        /// <summary>Harvest yields: mature wheat gives carrots + a seed back, immature only a seed.</summary>
        public static void Harvest(BlockType wheat)
        {
            if (IsMature(wheat))
            {
                Inventory.AddCarrot(1 + (Random.value < 0.5f ? 1 : 0));
            }
            Inventory.AddSeeds(1);
        }
    }
}
