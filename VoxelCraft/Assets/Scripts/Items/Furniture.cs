using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;

namespace VoxelCraft.Items
{
    /// <summary>
    /// M31 furniture state: doors (open/closed per lower block), chests (item
    /// storage keyed by world position), beds (spawn binding) and fence links.
    /// Static registry like Items.Crops: pure data, no scene objects, so the
    /// SelfTest can exercise it in batch mode. Chunk regeneration replays the
    /// same lookups deterministically (a door re-reads its open flag, a chest
    /// re-reads its contents).
    /// </summary>
    public static class Furniture
    {
        // ---- doors ----
        // Key = lower block position; value = open state. Upper block is always
        // lower.y + 1 and shares the flag.
        private static readonly Dictionary<Vector3Int, bool> openDoors = new Dictionary<Vector3Int, bool>();

        // Door facing (which wall plane it sits in) so the mesher knows how to
        // rotate the panel: 0 = on a X wall (panel spans Z), 1 = on a Z wall.
        private static readonly Dictionary<Vector3Int, byte> doorFacing = new Dictionary<Vector3Int, byte>();

        // ---- chests ----
        private static readonly Dictionary<Vector3Int, List<BlockType>> chests = new Dictionary<Vector3Int, List<BlockType>>();

        // ---- beds ----
        // Head position -> true when this bed is the player's spawn bed.
        private static readonly Dictionary<Vector3Int, bool> bedSpawn = new Dictionary<Vector3Int, bool>();
        public static Vector3Int? spawnBedHead;

        // ---- torches: purely visual (light baked at mesh time), no state ----

        // ---- furnaces ----
        // Key = furnace position; value = lit state. The mesher reads the lit
        // flag to swap the front face to the glowing firemouth tile.
        private static readonly Dictionary<Vector3Int, bool> litFurnaces = new Dictionary<Vector3Int, bool>();

        /// <summary>Smelting recipe: raw -> cooked. Anything else is not smeltable.</summary>
        public static bool TryGetSmelt(BlockType raw, out BlockType cooked)
        {
            switch (raw)
            {
                case BlockType.Sand: cooked = BlockType.Glass; return true;
                case BlockType.Cobble: cooked = BlockType.Stone; return true;
                case BlockType.IronOre: cooked = BlockType.IronOre; return true; // placeholder, no ingot item
                default: cooked = BlockType.Air; return false;
            }
        }

        public static void SetFurnaceLit(Vector3Int p, bool lit)
        {
            if (lit) { litFurnaces[p] = true; }
            else { litFurnaces.Remove(p); }
        }

        public static bool IsFurnaceLit(Vector3Int p)
        {
            return litFurnaces.TryGetValue(p, out bool lit) && lit;
        }

        public static void UnregisterFurnace(Vector3Int p) => litFurnaces.Remove(p);

        public static void Reset()
        {
            openDoors.Clear();
            doorFacing.Clear();
            chests.Clear();
            bedSpawn.Clear();
            litFurnaces.Clear();
            spawnBedHead = null;
        }

        public static bool IsDoor(BlockType t) =>
            t == BlockType.DoorClosed || t == BlockType.DoorOpen;
        public static bool IsDoorLower(Vector3Int p) => openDoors.ContainsKey(p);
        public static bool IsChest(BlockType t) => t == BlockType.Chest;
        public static bool IsBed(BlockType t) => t == BlockType.BedFoot || t == BlockType.BedHead;

        /// <summary>Register a door at its lower block. facing: 0 = panel spans Z (hinge on X wall), 1 = spans X.</summary>
        public static void RegisterDoor(Vector3Int lower, byte facing)
        {
            openDoors[lower] = false;
            doorFacing[lower] = facing;
        }

        public static void UnregisterDoor(Vector3Int lower)
        {
            openDoors.Remove(lower);
            doorFacing.Remove(lower);
        }

        public static bool TryGetDoorFacing(Vector3Int lower, out byte facing)
        {
            return doorFacing.TryGetValue(lower, out facing);
        }

        /// <summary>Toggle a door; the caller re-meshes the affected chunks.</summary>
        public static bool ToggleDoor(Vector3Int lower)
        {
            if (!openDoors.TryGetValue(lower, out bool open))
            {
                return false;
            }
            openDoors[lower] = !open;
            return true;
        }

        public static bool IsDoorOpen(Vector3Int lower)
        {
            return openDoors.TryGetValue(lower, out bool open) && open;
        }

        // ---- chest API ----
        public const int ChestSlots = 9;

        public static void RegisterChest(Vector3Int p) => RegisterChest(p, null);
        public static void RegisterChest(Vector3Int p, IEnumerable<BlockType> initial)
        {
            var list = new List<BlockType>(ChestSlots);
            if (initial != null)
            {
                list.AddRange(initial);
            }
            chests[p] = list;
        }

        public static void UnregisterChest(Vector3Int p) => chests.Remove(p);

        public static bool HasChest(Vector3Int p) => chests.ContainsKey(p);

        /// <summary>Chest contents snapshot (never null once registered).</summary>
        public static List<BlockType> ChestContents(Vector3Int p)
        {
            return chests.TryGetValue(p, out var list) ? list : null;
        }

        public static bool ChestAdd(Vector3Int p, BlockType item)
        {
            if (!chests.TryGetValue(p, out var list) || list.Count >= ChestSlots)
            {
                return false;
            }
            list.Add(item);
            return true;
        }

        /// <summary>Pop the most recently added item (LMB take in the chest UI).</summary>
        public static bool ChestTake(Vector3Int p, out BlockType item)
        {
            item = BlockType.Air;
            if (!chests.TryGetValue(p, out var list) || list.Count == 0)
            {
                return false;
            }
            item = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            return true;
        }

        // ---- bed API ----

        public static void SetSpawnBed(Vector3Int headPos)
        {
            bedSpawn.Clear();
            bedSpawn[headPos] = true;
            spawnBedHead = headPos;
        }

        public static bool IsSpawnBed(Vector3Int headPos)
        {
            return bedSpawn.TryGetValue(headPos, out bool isSpawn) && isSpawn;
        }
    }
}
