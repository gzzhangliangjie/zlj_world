using System;
using System.Collections.Generic;
using VoxelCraft.Core;

namespace VoxelCraft.Items
{
    public enum ToolType : byte
    {
        Hand = 0,
        Sword = 1,
        Axe = 2,
        Pickaxe = 3,
        Clock = 4,
    }

    /// <summary>
    /// Tool-gated block breaking rules:
    /// stone-family blocks need the pickaxe, wood is much faster with the axe,
    /// soft blocks break by hand. Returns (allowed, seconds between breaks).
    /// </summary>
    public static class ToolRules
    {
        public static (bool allowed, float interval) BreakRule(ToolType tool, BlockType block)
        {
            var def = BlockDatabase.Get(block);
            if (def.unbreakable)
            {
                return (false, 0f);
            }
            bool stoneFamily = def.soundGroup == "stone";
            bool woodFamily = def.soundGroup == "wood";

            switch (tool)
            {
                case ToolType.Axe:
                    if (stoneFamily) return (false, 0f);
                    return (true, woodFamily ? 0.2f : 0.3f);
                case ToolType.Sword:
                    if (stoneFamily) return (false, 0f);
                    return (true, woodFamily ? 0.7f : 0.5f);
                case ToolType.Pickaxe:
                    if (stoneFamily) return (true, 0.22f);
                    return (true, woodFamily ? 0.6f : 0.3f);
                case ToolType.Clock:
                    return (false, 0f); // the clock never breaks blocks; it bends time
                default: // Hand
                    if (stoneFamily) return (false, 0f);
                    return (true, woodFamily ? 0.8f : 0.35f);
            }
        }

        public static string DisplayName(ToolType tool)
        {
            switch (tool)
            {
                case ToolType.Axe: return "Axe";
                case ToolType.Sword: return "Sword";
                case ToolType.Pickaxe: return "Pickaxe";
                case ToolType.Clock: return "Clock";
                default: return "Hand";
            }
        }
    }

    /// <summary>
    /// Survival-lite inventory: broken blocks drop into per-type counts, placing
    /// consumes one. Creative mode (G) makes placement free.
    /// </summary>
    public static class Inventory
    {
        public static readonly Dictionary<BlockType, int> counts = new Dictionary<BlockType, int>();
        public static int meat;
        public static int seeds;
        public static int carrots;
        public static bool creative;

        public static void AddSeeds(int n)
        {
            if (n > 0)
            {
                seeds += n;
                Changed?.Invoke();
            }
        }

        public static void AddCarrot(int n)
        {
            if (n > 0)
            {
                carrots += n;
                Changed?.Invoke();
            }
        }

        public static bool TryConsumeSeeds(int n)
        {
            if (creative)
            {
                return true;
            }
            if (seeds < n)
            {
                return false;
            }
            seeds -= n;
            Changed?.Invoke();
            return true;
        }

        public static event Action Changed;

        public static int Get(BlockType type)
        {
            return counts.TryGetValue(type, out int v) ? v : 0;
        }

        public static void Add(BlockType type)
        {
            if (type == BlockType.Air)
            {
                return;
            }
            counts[type] = Get(type) + 1;
            Changed?.Invoke();
        }

        public static bool TryConsume(BlockType type)
        {
            if (creative)
            {
                return true;
            }
            int have = Get(type);
            if (have <= 0)
            {
                return false;
            }
            counts[type] = have - 1;
            Changed?.Invoke();
            return true;
        }

        /// <summary>What a broken block yields (Grass->Dirt, Stone->Cobble, Leaves->nothing).</summary>
        public static BlockType DropFor(BlockType broken)
        {
            switch (broken)
            {
                case BlockType.Grass: return BlockType.Dirt;
                case BlockType.Stone: return BlockType.Cobble;
                case BlockType.Leaves: return BlockType.Air;
                case BlockType.Ice: return BlockType.Air;
                default: return broken;
            }
        }

        public static void AddMeat(int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            meat += amount;
            Changed?.Invoke();
        }

        public static void Reset()
        {
            counts.Clear();
            meat = 0;
            seeds = 0;
            carrots = 0;
            creative = false;
            Changed?.Invoke();
        }
    }
}
