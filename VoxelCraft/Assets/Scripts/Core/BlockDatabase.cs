using UnityEngine;

namespace VoxelCraft.Core
{
    /// <summary>Atlas tile slot ids. The tile table lives in TextureFactory.</summary>
    public enum TileId
    {
        GrassTop = 0,
        GrassSide = 1,
        Dirt = 2,
        Stone = 3,
        Sand = 4,
        LogSide = 5,
        LogTop = 6,
        Leaves = 7,
        Water = 8,
        Plank = 9,
        Cobble = 10,
        Glass = 11,
        Snow = 12,
        Brick = 13,
        Bedrock = 14,
        CoalOre = 15,
        IronOre = 16,
        GoldOre = 17,
        DiamondOre = 18,
        Gravel = 19,
        Ice = 20,
        Obsidian = 21,
        MossyCobble = 22,
        StoneBrick = 23,
        Wheat0 = 24,
        Wheat1 = 25,
        Wheat2 = 26,
        Wheat3 = 27,
    }

    /// <summary>Static definition of every block: rendering, physics and interaction rules.</summary>
    public static class BlockDatabase
    {
        public struct BlockDef
        {
            public string name;
            public bool opaque;        // hides neighbour faces / blocks light baking
            public bool solid;         // collides with the player
            public bool liquid;        // water-like: non-solid, swimmable
            public bool placeable;     // allowed on the hotbar
            public bool unbreakable;   // cannot be destroyed by the player
            public TileId top;
            public TileId side;
            public TileId bottom;
            public string soundGroup;  // dig/step sound family used by the audio layer

            public TileId FaceTile(Vector3Int normal)
            {
                if (normal.y > 0) return top;
                if (normal.y < 0) return bottom;
                return side;
            }
        }

        private static BlockDef[] defs;

        public static BlockDef Get(BlockType type) { return defs[(int)type]; }
        public static bool IsOpaque(BlockType type) { return type != BlockType.Air && defs[(int)type].opaque; }
        public static bool IsSolid(BlockType type) { return type != BlockType.Air && defs[(int)type].solid; }
        public static bool IsLiquid(BlockType type) { return type != BlockType.Air && defs[(int)type].liquid; }

        static BlockDatabase()
        {
            defs = new BlockDef[27];

            defs[(int)BlockType.Air] = new BlockDef
            {
                name = "Air", opaque = false, solid = false, liquid = false,
                placeable = false, unbreakable = true,
                top = TileId.Stone, side = TileId.Stone, bottom = TileId.Stone, soundGroup = "none",
            };

            defs[(int)BlockType.Grass] = new BlockDef
            {
                name = "Grass", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.GrassTop, side = TileId.GrassSide, bottom = TileId.Dirt, soundGroup = "grass",
            };

            defs[(int)BlockType.Dirt] = new BlockDef
            {
                name = "Dirt", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Dirt, side = TileId.Dirt, bottom = TileId.Dirt, soundGroup = "dirt",
            };

            defs[(int)BlockType.Stone] = new BlockDef
            {
                name = "Stone", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Stone, side = TileId.Stone, bottom = TileId.Stone, soundGroup = "stone",
            };

            defs[(int)BlockType.Sand] = new BlockDef
            {
                name = "Sand", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Sand, side = TileId.Sand, bottom = TileId.Sand, soundGroup = "sand",
            };

            defs[(int)BlockType.Log] = new BlockDef
            {
                name = "Log", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.LogTop, side = TileId.LogSide, bottom = TileId.LogTop, soundGroup = "wood",
            };

            defs[(int)BlockType.Leaves] = new BlockDef
            {
                name = "Leaves", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Leaves, side = TileId.Leaves, bottom = TileId.Leaves, soundGroup = "grass",
            };

            defs[(int)BlockType.Water] = new BlockDef
            {
                name = "Water", opaque = false, solid = false, liquid = true,
                placeable = false, unbreakable = false,
                top = TileId.Water, side = TileId.Water, bottom = TileId.Water, soundGroup = "none",
            };

            defs[(int)BlockType.Plank] = new BlockDef
            {
                name = "Plank", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Plank, side = TileId.Plank, bottom = TileId.Plank, soundGroup = "wood",
            };

            defs[(int)BlockType.Cobble] = new BlockDef
            {
                name = "Cobble", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Cobble, side = TileId.Cobble, bottom = TileId.Cobble, soundGroup = "stone",
            };

            defs[(int)BlockType.Glass] = new BlockDef
            {
                name = "Glass", opaque = false, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Glass, side = TileId.Glass, bottom = TileId.Glass, soundGroup = "glass",
            };

            defs[(int)BlockType.Snow] = new BlockDef
            {
                name = "Snow", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Snow, side = TileId.Snow, bottom = TileId.Snow, soundGroup = "snow",
            };

            defs[(int)BlockType.Brick] = new BlockDef
            {
                name = "Brick", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Brick, side = TileId.Brick, bottom = TileId.Brick, soundGroup = "stone",
            };

            defs[(int)BlockType.Bedrock] = new BlockDef
            {
                name = "Bedrock", opaque = true, solid = true, liquid = false,
                placeable = false, unbreakable = true,
                top = TileId.Bedrock, side = TileId.Bedrock, bottom = TileId.Bedrock, soundGroup = "stone",
            };

            TileId OreTiles(BlockType t)
            {
                switch (t)
                {
                    case BlockType.CoalOre: return TileId.CoalOre;
                    case BlockType.IronOre: return TileId.IronOre;
                    case BlockType.GoldOre: return TileId.GoldOre;
                    default: return TileId.DiamondOre;
                }
            }
            foreach (BlockType ore in new[] { BlockType.CoalOre, BlockType.IronOre, BlockType.GoldOre, BlockType.DiamondOre })
            {
                defs[(int)ore] = new BlockDef
                {
                    name = ore.ToString(), opaque = true, solid = true, liquid = false,
                    placeable = true, unbreakable = false,
                    top = OreTiles(ore), side = OreTiles(ore), bottom = OreTiles(ore), soundGroup = "stone",
                };
            }

            defs[(int)BlockType.Gravel] = new BlockDef
            {
                name = "Gravel", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Gravel, side = TileId.Gravel, bottom = TileId.Gravel, soundGroup = "dirt",
            };

            defs[(int)BlockType.Ice] = new BlockDef
            {
                name = "Ice", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Ice, side = TileId.Ice, bottom = TileId.Ice, soundGroup = "glass",
            };

            defs[(int)BlockType.Obsidian] = new BlockDef
            {
                name = "Obsidian", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.Obsidian, side = TileId.Obsidian, bottom = TileId.Obsidian, soundGroup = "stone",
            };

            defs[(int)BlockType.MossyCobble] = new BlockDef
            {
                name = "Mossy Cobble", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.MossyCobble, side = TileId.MossyCobble, bottom = TileId.MossyCobble, soundGroup = "stone",
            };

            defs[(int)BlockType.StoneBrick] = new BlockDef
            {
                name = "Stone Brick", opaque = true, solid = true, liquid = false,
                placeable = true, unbreakable = false,
                top = TileId.StoneBrick, side = TileId.StoneBrick, bottom = TileId.StoneBrick, soundGroup = "stone",
            };

            TileId wheatTile(BlockType t)
            {
                switch (t)
                {
                    case BlockType.Wheat0: return TileId.Wheat0;
                    case BlockType.Wheat1: return TileId.Wheat1;
                    case BlockType.Wheat2: return TileId.Wheat2;
                    default: return TileId.Wheat3;
                }
            }
            foreach (BlockType wheat in new[] { BlockType.Wheat0, BlockType.Wheat1, BlockType.Wheat2, BlockType.Wheat3 })
            {
                defs[(int)wheat] = new BlockDef
                {
                    name = "Wheat (" + wheat.ToString().Substring(5) + ")",
                    opaque = false, solid = false, liquid = false,
                    placeable = false, unbreakable = false,
                    top = wheatTile(wheat), side = wheatTile(wheat), bottom = wheatTile(wheat), soundGroup = "grass",
                };
            }
        }
    }
}
