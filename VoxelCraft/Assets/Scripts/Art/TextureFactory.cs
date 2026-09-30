using System;
using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;

namespace VoxelCraft.Art
{
    /// <summary>
    /// Builds the block texture atlas at runtime using a three-layer supply chain:
    ///  1. real textures loaded from Resources/Textures/&lt;name&gt;.png.bytes (TextAsset -&gt; LoadImage),
    ///  2. procedural pixel-art painting for any missing tile (project runs on any machine),
    ///  3. drop-in re-skin: supplying same-named .png.bytes files later replaces tiles.
    /// </summary>
    public static class TextureFactory
    {
        public const int TileSize = 16;
        public const int AtlasCols = 8;
        public const int AtlasRows = 8;

        /// <summary>Canonical tile file names, parallel to the TileId enum.</summary>
        private static readonly string[] TileNames =
        {
            "grass_top", "grass_side", "dirt", "stone", "sand", "log_side",
            "log_top", "leaves", "water", "plank", "cobble", "glass",
            "snow", "brick", "bedrock", "coal_ore", "iron_ore", "gold_ore",
            "diamond_ore", "gravel", "ice", "obsidian", "mossy", "stone_brick",
            "wheat0", "wheat1", "wheat2", "wheat3",
            "glowstone", "path", "wool_white", "wool_red", "wool_yellow",
            "wool_blue", "wool_green", "wool_black",
            "torch", "door", "door_top", "chest_side", "chest_top",
            "chest_front", "bed_head_top", "bed_foot_top", "bed_side", "fence_link",
            "furnace_side", "furnace_front", "furnace_lit",
            "bed_feet_end", "bed_head_end", "bed_head_side", "bed_feet_side", "furnace_top",
            "door_wood_lower", "door_wood_upper", "chest_front_v", "chest_side_v", "chest_top_v",
        };

        public sealed class AtlasResult
        {
            public Texture2D atlas;
            public Texture2D white;
            public Dictionary<BlockType, Texture2D> icons = new Dictionary<BlockType, Texture2D>();
            public List<string> externalTiles = new List<string>();
            public List<string> proceduralTiles = new List<string>();

            public Rect TileRect(TileId tile)
            {
                int col = (int)tile % AtlasCols;
                int row = (int)tile / AtlasCols;
                float eps = 0.25f / (AtlasCols * TileSize);
                float vEps = 0.25f / (AtlasRows * TileSize);
                float x = col / (float)AtlasCols + eps;
                float y = (AtlasRows - 1 - row) / (float)AtlasRows + vEps;
                return new Rect(x, y, 1f / AtlasCols - 2 * eps, 1f / AtlasRows - 2 * vEps);
            }
        }

        /// <summary>Build the atlas plus per-block UI icons. Call once at boot.</summary>
        public static AtlasResult Build()
        {
            var result = new AtlasResult();
            var atlas = new Texture2D(AtlasCols * TileSize, AtlasRows * TileSize, TextureFormat.RGBA32, false)
            {
                name = "VoxelAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            int count = TileNames.Length;
            for (int i = 0; i < count; i++)
            {
                var tile = (TileId)i;
                Color32[] canvas = LoadExternalTile(TileNames[i], out bool external);
                if (!external)
                {
                    canvas = PaintProcedural(tile);
                    result.proceduralTiles.Add(TileNames[i]);
                }
                else
                {
                    result.externalTiles.Add(TileNames[i]);
                }

                int col = i % AtlasCols;
                int row = i / AtlasCols;
                atlas.SetPixels32(col * TileSize, (AtlasRows - 1 - row) * TileSize, TileSize, TileSize, canvas);
            }

            atlas.Apply(false, false);
            result.atlas = atlas;
            result.white = MakeWhite();

            // Skip Wheat0..3 (no icons needed) but include all placeable blocks.
            for (int t = 1; t < 43; t++)
            {
                if (t >= (int)BlockType.Wheat0 && t <= (int)BlockType.Wheat3)
                {
                    continue;
                }
                var type = (BlockType)t;
                TileId iconTile = BlockDatabase.Get(type).top;
                var icon = new Texture2D(TileSize, TileSize, TextureFormat.RGBA32, false)
                {
                    name = "Icon_" + type,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                icon.SetPixels32(GetTilePixels(result, iconTile));
                icon.Apply(false, false);
                result.icons[type] = icon;
            }

            return result;
        }

        private static Color32[] GetTilePixels(AtlasResult result, TileId tile)
        {
            int col = (int)tile % AtlasCols;
            int row = (int)tile / AtlasCols;
            Color32[] all = result.atlas.GetPixels32();
            int aw = AtlasCols * TileSize;
            var canvas = new Color32[TileSize * TileSize];
            int x0 = col * TileSize;
            int y0 = (AtlasRows - 1 - row) * TileSize;
            for (int y = 0; y < TileSize; y++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    canvas[y * TileSize + x] = all[(y0 + y) * aw + x0 + x];
                }
            }
            return canvas;
        }

        private static Texture2D MakeWhite()
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "VoxelWhite",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(new Color32[4]
            {
                new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
                new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
            });
            tex.Apply(false, false);
            return tex;
        }

        // ------------------------------------------------------------------
        // Layer 1: external texture files
        // ------------------------------------------------------------------

        private static Color32[] LoadExternalTile(string name, out bool ok)
        {
            ok = false;
            var asset = Resources.Load<TextAsset>("Textures/" + name + ".png");
            if (asset == null || asset.bytes == null || asset.bytes.Length == 0)
            {
                return null;
            }

            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(asset.bytes, false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }

            Color32[] src = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;
            UnityEngine.Object.DestroyImmediate(tex);
            if (src == null || src.Length != w * h)
            {
                return null;
            }

            var canvas = new Color32[TileSize * TileSize];
            for (int y = 0; y < TileSize; y++)
            {
                int sy = y * h / TileSize;
                for (int x = 0; x < TileSize; x++)
                {
                    int sx = x * w / TileSize;
                    // Unity GetPixels32 rows are already bottom-up, matching the canvas convention.
                    canvas[y * TileSize + x] = src[sy * w + sx];
                }
            }

            ok = true;
            return canvas;
        }

        // ------------------------------------------------------------------
        // Layer 2: procedural pixel art
        // ------------------------------------------------------------------

        private static Color32[] PaintProcedural(TileId id)
        {
            var px = new Color32[TileSize * TileSize];
            var rng = new System.Random(unchecked((int)id * 7919 + 0x5eed));

            switch (id)
            {
                case TileId.GrassTop: Speckle(px, rng, 116, 169, 62, 22); break;
                case TileId.GrassSide:
                    Speckle(px, rng, 134, 96, 67, 22);
                    Strip(px, rng, 116, 169, 62, 3, 1);
                    break;
                case TileId.Dirt: Speckle(px, rng, 134, 96, 67, 22); break;
                case TileId.Stone: Speckle(px, rng, 125, 125, 125, 14); break;
                case TileId.Sand: Speckle(px, rng, 219, 207, 163, 14); break;
                case TileId.LogSide: Stripes(px, rng, 102, 81, 50, 18, true); break;
                case TileId.LogTop: Rings(px, rng); break;
                case TileId.Leaves: Speckle(px, rng, 60, 124, 48, 26); break;
                case TileId.Water: Water(px, rng); break;
                case TileId.Plank: Planks(px, rng); break;
                case TileId.Cobble: Cobble(px, rng, 118, 118, 118); break;
                case TileId.Glass: Glass(px); break;
                case TileId.Snow: Speckle(px, rng, 240, 246, 246, 8); break;
                case TileId.Brick: Bricks(px, rng, 150, 97, 83, 161, 58, 49); break;
                case TileId.Bedrock: Cobble(px, rng, 55, 55, 60); break;
                case TileId.CoalOre: Ore(px, rng, 35, 35, 35); break;
                case TileId.IronOre: Ore(px, rng, 216, 175, 147); break;
                case TileId.GoldOre: Ore(px, rng, 252, 238, 75); break;
                case TileId.DiamondOre: Ore(px, rng, 93, 236, 245); break;
                case TileId.Gravel: Pebbles(px, rng); break;
                case TileId.Ice: Ice(px, rng); break;
                case TileId.Obsidian: Speckle(px, rng, 34, 26, 48, 16); break;
                case TileId.MossyCobble: Cobble(px, rng, 110, 118, 96); break;
                case TileId.StoneBrick: Bricks(px, rng, 122, 122, 122, 100, 100, 100); break;
                case TileId.Wheat0: WheatStage(px, rng, 0); break;
                case TileId.Wheat1: WheatStage(px, rng, 1); break;
                case TileId.Wheat2: WheatStage(px, rng, 2); break;
                case TileId.Wheat3: WheatStage(px, rng, 3); break;
                case TileId.Glowstone: GlowstoneTile(px, rng); break;
                case TileId.Path: PathTile(px, rng); break;
                case TileId.WoolWhite: Wool(px, rng, 232, 236, 238); break;
                case TileId.WoolRed: Wool(px, rng, 176, 46, 38); break;
                case TileId.WoolYellow: Wool(px, rng, 234, 195, 55); break;
                case TileId.WoolBlue: Wool(px, rng, 53, 87, 178); break;
                case TileId.WoolGreen: Wool(px, rng, 86, 128, 40); break;
                case TileId.WoolBlack: Wool(px, rng, 32, 32, 38); break;
                case TileId.Torch: TorchTile(px, rng); break;
                case TileId.DoorTile: VanillaTile(px, _vanilla_door_wood_lower); break;
                case TileId.DoorTileTop: VanillaTile(px, _vanilla_door_wood_upper); break;
                case TileId.ChestSide: VanillaTile(px, _vanilla_chest_side_v); break;
                case TileId.ChestTop: VanillaTile(px, _vanilla_chest_top_v); break;
                case TileId.ChestFront: VanillaTile(px, _vanilla_chest_front_v); break;
                case TileId.BedHeadTop: VanillaTile(px, _vanilla_bed_head_top); break;
                case TileId.BedFootTop: VanillaTile(px, _vanilla_bed_feet_top); break;
                case TileId.BedHeadSide: VanillaTile(px, _vanilla_bed_head_side); break;
                case TileId.BedFeetSide: VanillaTile(px, _vanilla_bed_feet_side); break;
                case TileId.BedHeadEnd: VanillaTile(px, _vanilla_bed_head_end); break;
                case TileId.BedFeetEnd: VanillaTile(px, _vanilla_bed_feet_end); break;
                case TileId.FurnaceTop: VanillaTile(px, _vanilla_furnace_top); break;
                case TileId.DoorWoodLower: VanillaTile(px, _vanilla_door_wood_lower); break;
                case TileId.DoorWoodUpper: VanillaTile(px, _vanilla_door_wood_upper); break;
                case TileId.ChestFrontV: VanillaTile(px, _vanilla_chest_front_v); break;
                case TileId.ChestSideV: VanillaTile(px, _vanilla_chest_side_v); break;
                case TileId.ChestTopV: VanillaTile(px, _vanilla_chest_top_v); break;
                case TileId.BedSide: BedSideTile(px, rng); break;
                case TileId.FenceLink: FenceLinkTile(px, rng); break;
                case TileId.FurnaceSide: FurnaceTile(px, rng, false, false); break;
                case TileId.FurnaceFront: VanillaTile(px, _vanilla_furnace_front_off); break;
                case TileId.FurnaceLit: VanillaTile(px, _vanilla_furnace_front_on); break;
            }

            return px;
        }

        private static void GlowstoneTile(Color32[] px, System.Random rng)
        {
            // warm amber base with brighter crystalline blobs
            Speckle(px, rng, 196, 154, 92, 18);
            for (int i = 0; i < 5; i++)
            {
                int cx = rng.Next(2, TileSize - 2);
                int cy = rng.Next(2, TileSize - 2);
                int rad = rng.Next(1, 3);
                for (int y = cy - rad; y <= cy + rad; y++)
                {
                    for (int x = cx - rad; x <= cx + rad; x++)
                    {
                        if (x < 0 || x >= TileSize || y < 0 || y >= TileSize) continue;
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > rad * rad) continue;
                        px[y * TileSize + x] = new Color32(252, 224, 130, 255);
                    }
                }
            }
        }

        private static void PathTile(Color32[] px, System.Random rng)
        {
            // trodden dirt path: lighter and more compact than farmland-style dirt
            Speckle(px, rng, 152, 121, 85, 16);
            for (int i = 0; i < 6; i++)
            {
                int x = rng.Next(0, TileSize);
                int y = rng.Next(0, TileSize);
                px[y * TileSize + x] = new Color32(170, 140, 100, 255);
            }
        }

        private static void Wool(Color32[] px, System.Random rng, int r, int g, int b)
        {
            // soft fabric weave: base colour with faint horizontal/vertical banding
            for (int y = 0; y < TileSize; y++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    int d = rng.Next(-8, 9);
                    int band = (x + y) % 4 == 0 ? -10 : 0;
                    px[y * TileSize + x] = new Color32(
                        (byte)Clamp(r + d + band), (byte)Clamp(g + d + band), (byte)Clamp(b + d + band), 255);
                }
            }
        }

        private static void WheatStage(Color32[] px, System.Random rng, int stage)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(0, 0, 0, 0); // transparent background
            }
            // stage 0: sparse short sprouts, stage 3: dense tall golden stalks
            int columns = stage == 0 ? 4 : stage == 1 ? 5 : 6;
            int maxH = 3 + stage * 3;                       // 3..12 rows tall
            byte stemR = (byte)(90 + stage * 40);
            byte stemG = (byte)(170 - stage * 15);
            byte stemB = (byte)(60 + stage * 10);
            for (int cIdx = 0; cIdx < columns; cIdx++)
            {
                int x = 2 + cIdx * (16 - 3) / columns + rng.Next(0, 2);
                int h = maxH - rng.Next(0, 3);
                for (int y = 15; y > 15 - h && y >= 0; y--)
                {
                    px[y * 16 + Mathf.Clamp(x, 0, 15)] = new Color32(stemR, stemG, stemB, 255);
                }
                // grain heads on mature stages
                if (stage >= 2 && h > 6)
                {
                    px[(15 - h + 1) * 16 + Mathf.Clamp(x, 0, 15)] = new Color32(228, 190, 90, 255);
                    px[(15 - h) * 16 + Mathf.Clamp(x + 1, 0, 15)] = new Color32(228, 190, 90, 255);
                }
            }
        }

        private static void Speckle(Color32[] px, System.Random rng, int r, int g, int b, int spread)
        {
            for (int i = 0; i < px.Length; i++)
            {
                int d = rng.Next(-spread, spread + 1);
                px[i] = new Color32((byte)Clamp(r + d), (byte)Clamp(g + d), (byte)Clamp(b + d), 255);
            }
        }

        private static void Strip(Color32[] px, System.Random rng, int r, int g, int b, int rows, int ragged)
        {
            for (int x = 0; x < TileSize; x++)
            {
                int depth = rows + (rng.NextDouble() < 0.4 ? 1 : 0) * ragged;
                for (int y = TileSize - 1; y >= TileSize - depth; y--)
                {
                    int d = rng.Next(-16, 17);
                    px[y * TileSize + x] = new Color32((byte)Clamp(r + d), (byte)Clamp(g + d), (byte)Clamp(b + d), 255);
                }
            }
        }

        private static void Stripes(Color32[] px, System.Random rng, int r, int g, int b, int spread, bool vertical)
        {
            for (int a = 0; a < TileSize; a++)
            {
                int columnShade = rng.Next(-spread, spread + 1) / 2;
                for (int o = 0; o < TileSize; o++)
                {
                    int d = columnShade + rng.Next(-6, 7);
                    int x = vertical ? a : o;
                    int y = vertical ? o : a;
                    px[y * TileSize + x] = new Color32((byte)Clamp(r + d), (byte)Clamp(g + d), (byte)Clamp(b + d), 255);
                }
            }
        }

        private static void Rings(Color32[] px, System.Random rng)
        {
            for (int y = 0; y < TileSize; y++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    float dx = x - 7.5f;
                    float dy = y - 7.5f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    int shade = Mathf.RoundToInt(Mathf.PingPong(dist * 1.6f, 1f) * 26);
                    int d = shade + rng.Next(-6, 7);
                    px[y * TileSize + x] = new Color32((byte)Clamp(151 + d - 13), (byte)Clamp(122 + d - 10), (byte)Clamp(78 + d - 6), 255);
                }
            }
        }

        private static void Water(Color32[] px, System.Random rng)
        {
            for (int i = 0; i < px.Length; i++)
            {
                int d = rng.Next(-12, 13);
                px[i] = new Color32((byte)Clamp(52 + d), (byte)Clamp(95 + d), (byte)Clamp(218 + d / 2), 255);
            }
        }

        private static void Planks(Color32[] px, System.Random rng)
        {
            for (int y = 0; y < TileSize; y++)
            {
                int board = y / 4;
                bool seam = y % 4 == 0;
                for (int x = 0; x < TileSize; x++)
                {
                    int d = rng.Next(-10, 11) + (seam ? -60 : 0) + ((x + board * 5) % 8 == 0 && !seam ? -35 : 0);
                    px[y * TileSize + x] = new Color32((byte)Clamp(160 + d), (byte)Clamp(130 + d), (byte)Clamp(78 + d), 255);
                }
            }
        }

        private static void Cobble(Color32[] px, System.Random rng, int r, int g, int b)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32((byte)(r / 2), (byte)(g / 2), (byte)(b / 2), 255);
            }
            for (int blob = 0; blob < 9; blob++)
            {
                int cx = rng.Next(2, TileSize - 2);
                int cy = rng.Next(2, TileSize - 2);
                int rad = rng.Next(1, 3);
                int shade = rng.Next(-20, 21);
                for (int y = cy - rad - 1; y <= cy + rad + 1; y++)
                {
                    for (int x = cx - rad - 1; x <= cx + rad + 1; x++)
                    {
                        int xi = Mathf.Clamp(x, 0, TileSize - 1);
                        int yi = Mathf.Clamp(y, 0, TileSize - 1);
                        float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        if (dist <= rad + rng.Next(0, 2))
                        {
                            int d = shade + rng.Next(-8, 9);
                            px[yi * TileSize + xi] = new Color32((byte)Clamp(r + d), (byte)Clamp(g + d), (byte)Clamp(b + d), 255);
                        }
                    }
                }
            }
        }

        private static void Glass(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(0, 0, 0, 0);
            }
            for (int a = 0; a < TileSize; a++)
            {
                byte edge = 230;
                px[a] = new Color32(edge, 244, 250, 255);                                     // bottom
                px[(TileSize - 1) * TileSize + a] = new Color32(edge, 244, 250, 255);         // top
                px[a * TileSize] = new Color32(edge, 244, 250, 255);                          // left
                px[a * TileSize + TileSize - 1] = new Color32(edge, 244, 250, 255);           // right
            }
            for (int i = 0; i < 5; i++)
            {
                int x = 3 + i;
                px[(TileSize - 3 - i) * TileSize + x] = new Color32(255, 255, 255, 150);
            }
        }

        private static void Bricks(Color32[] px, System.Random rng, int br, int bg, int bb, int mr, int mg, int mb)
        {
            for (int y = 0; y < TileSize; y++)
            {
                bool mortarRow = y % 4 == 3;
                int row = y / 4;
                for (int x = 0; x < TileSize; x++)
                {
                    bool mortarCol = (x + (row % 2) * 4) % 8 == 7;
                    bool mortar = mortarRow || mortarCol;
                    int d = rng.Next(-10, 11);
                    if (mortar)
                    {
                        px[y * TileSize + x] = new Color32((byte)Clamp(mr + d), (byte)Clamp(mg + d), (byte)Clamp(mb + d), 255);
                    }
                    else
                    {
                        px[y * TileSize + x] = new Color32((byte)Clamp(br + d), (byte)Clamp(bg + d), (byte)Clamp(bb + d), 255);
                    }
                }
            }
        }

        private static void Ore(Color32[] px, System.Random rng, int r, int g, int b)
        {
            Speckle(px, rng, 125, 125, 125, 14);
            for (int cluster = 0; cluster < 4; cluster++)
            {
                int cx = rng.Next(2, TileSize - 2);
                int cy = rng.Next(2, TileSize - 2);
                for (int k = 0; k < 4; k++)
                {
                    int x = Mathf.Clamp(cx + rng.Next(-1, 2), 0, TileSize - 1);
                    int y = Mathf.Clamp(cy + rng.Next(-1, 2), 0, TileSize - 1);
                    int d = rng.Next(-20, 21);
                    px[y * TileSize + x] = new Color32((byte)Clamp(r + d), (byte)Clamp(g + d), (byte)Clamp(b + d), 255);
                }
            }
        }

        private static void Pebbles(Color32[] px, System.Random rng)
        {
            Speckle(px, rng, 136, 126, 126, 12);
            for (int k = 0; k < 14; k++)
            {
                int x = rng.Next(0, TileSize);
                int y = rng.Next(0, TileSize);
                int shade = rng.Next(-25, 26);
                px[y * TileSize + x] = new Color32((byte)Clamp(160 + shade), (byte)Clamp(152 + shade), (byte)Clamp(150 + shade), 255);
            }
        }

        private static void Ice(Color32[] px, System.Random rng)
        {
            Speckle(px, rng, 160, 205, 245, 10);
            for (int crack = 0; crack < 3; crack++)
            {
                int x = rng.Next(0, TileSize);
                for (int y = 0; y < TileSize; y++)
                {
                    px[y * TileSize + Mathf.Clamp(x, 0, TileSize - 1)] = new Color32(210, 235, 252, 255);
                    if (rng.NextDouble() < 0.5)
                    {
                        x += rng.Next(-1, 2);
                    }
                }
            }
        }

        private static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        // ---- M31 furniture tile painters ----

        private static int Idx(int x, int y) { return y * TileSize + x; }

        private static void Fill(Color32[] px, Color32 c)
        {
            for (int i = 0; i < px.Length; i++) { px[i] = c; }
        }

        private static void TorchTile(Color32[] px, System.Random rng)
        {
            // FULLY OPAQUE tile (no cutout): the torch geometry maps sub-rects of
            // this tile; transparent texels anywhere on those rects got alpha-
            // clipped and erased whole faces. Stick = full-height wood column,
            // ember = bright head band at the top rows.
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    px[Idx(x, y)] = new Color32(104, 76, 46, 255); // stick base wood
                }
            }
            // ember band rows 3..7 across the width
            for (int y = 3; y <= 7; y++)
            {
                for (int x = 3; x <= 12; x++)
                {
                    bool core = x >= 6 && x <= 9 && y >= 4 && y <= 6;
                    px[Idx(x, y)] = core ? new Color32(255, 231, 128, 255) : new Color32(228, 148, 56, 255);
                }
            }
            for (int x = 5; x <= 10; x++) { px[Idx(x, 3)] = new Color32(255, 246, 180, 255); }
        }

        private static void DoorTile(Color32[] px, System.Random rng, bool top)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int plank = x / 4;
                    int shade = plank % 2 == 0 ? 0 : -14;
                    Color32 c = new Color32((byte)(146 + shade), (byte)(114 + shade), (byte)(70 + shade), 255);
                    if (x % 4 == 3) { c = new Color32(104, 78, 46, 255); }
                    if (rng.Next(8) == 0) { c = new Color32((byte)(c.r - 10), (byte)(c.g - 8), (byte)(c.b - 6), 255); }
                    px[Idx(x, y)] = c;
                }
            }
            if (top)
            {
                for (int y = 3; y < 7; y++)
                {
                    for (int x = 4; x < 12; x++)
                    {
                        px[Idx(x, y)] = new Color32(58, 64, 74, 255);
                    }
                }
                px[Idx(7, 3)] = new Color32(120, 130, 142, 255);
                px[Idx(8, 4)] = new Color32(120, 130, 142, 255);
            }
            else
            {
                for (int x = 2; x < 14; x++)
                {
                    px[Idx(x, 4)] = new Color32(96, 72, 44, 255);
                    px[Idx(x, 5)] = new Color32(96, 72, 44, 255);
                }
            }
            for (int y = top ? 3 : 11; y < (top ? 6 : 14); y++)
            {
                px[Idx(1, y)] = new Color32(70, 70, 74, 255);
            }
        }

        private static byte[] BytesFromHex(params string[] chunks)
        {
            int total = 0;
            foreach (var c in chunks) { total += c.Length / 2; }
            var b = new byte[total];
            int o = 0;
            foreach (var c in chunks)
            {
                for (int i = 0; i < c.Length; i += 2)
                {
                    b[o++] = System.Convert.ToByte(c.Substring(i, 2), 16);
                }
            }
            return b;
        }

        private static void VanillaTile(Color32[] px, byte[] rgba)
        {
            for (int i = 0; i < 256; i++)
            {
                px[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
            }
        }

        // vanilla bedrock-samples v1.21.80.3: bed_feet_end.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_bed_feet_end = BytesFromHex(
            "69451eff926e3affaa8e4aff000000000000000000000000000000000000000000000000000000000000000000000000000000006c431fff986d35ffaa8a4bff6e461dff916d3affb19651ff00000000000000000000000000000000000000000000000000000000000000000000000000000000593c17ff8c6e3cffae9952ff563d1eff936d3affaa9148ff00000000000000000000000000000000000000000000000000000000000000000000000000000000563c1aff8c6331ffac8f45ff58401bff8e6f38ff917447ff5b3f19ff6a4421ff6b421eff563c22ff573a17ff6d4020ff69411dff6d4120ff6a4320ff6c4420ff674322ff886731ffb19655ff53411bff917039ff936f35ff916e36ff8b6b32ff876931ff916b36ff946e34ff8b6933ff8d682eff936f3bff906d38ff946d38ff8c6932ff916d36ffa7904aff6d0000ff6e0200ff6c0000ff6f0001ff6c0000ff6d0201ff6d0000ff6d0300ff6c0100ff6f0000ff6a0100ff6c0000ff680500ff6a0401ff680000ff6d0000ff8e1616ff8c1517ff820e0cff811010ff810c13ff8a1a15ff8e1413ff891413ff9c231fff981f20ff9b2425ff9e2022ff9b2126ff9a2022ff8a1114ff83070aff800c11ff8c1415ff881815ff8c0f15ff811210ff810b11ff800f10ff811011ff81130dff8c1513ff881716ff8a1616ff891412ff8b1112ff8e1811ff840909ff810f10ff7f1010ff82090bff810e0fff810f0fff83130fff7c1310ff850f11ff831010ff860909ff800a0dff840409ff810906ff83060bff840a0bff870d09ff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );

        // vanilla bedrock-samples v1.21.80.3: bed_feet_side.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_bed_feet_side = BytesFromHex(
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006b441dff946c38ffad8f4aff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000553d1fff8f6937ffb09651ff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000583c1dff89682dffaa8d49ff69421fff68431eff6a3e20ff573919ff6a441fff6a441bff563c1cff553f19ff684520ff69411eff69431eff6a421eff6b411fff6b3f21ff896a34ffae9750ff886834ff916c39ff907335ff916b39ff876432ff886833ff936c38ff916f36ff896830ff896732ff936c39ff8f6a3bff927038ff876a32ff916e38ffaa8f4cff6d0200ff6f0103ff6b0000ff6d0004ff6b0000ff6b0001ff690100ff6c0102ff690000ff6d0001ff6c0002ff6d0000ff700000ff6f0000ff710100ff6d0301ff8c1515ff861610ff811110ff80110fff800f0eff8f1518ff8e1313ff8d1517ff991e23ff9a2324ff9c231eff972225ff9c2323ff9b2221ff8c1217ff800a09ff83110dff8b1615ff8b1411ff90171aff7d1210ff821012ff7f1410ff7e1110ff821010ff8c1615ff8a1514ff8e1113ff8a1513ff8c1815ff8d1715ff820909ff810d12ff811411ff7d0909ff7e0b0fff801110ff810d10ff7f1010ff831212ff821110ff850a09ff810906ff810b0eff830708ff7a0a0cff840609ff82080cff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );

        // vanilla bedrock-samples v1.21.80.3: bed_feet_top.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_bed_feet_top = BytesFromHex(
            "820908ff80090aff830909ff860705ff840609ff820804ff820a08ff830b0cff850a09ff820b0dff810b0dff7a0904ff810b0aff820a09ff810b06ff800e0cff810a09ff7d080aff7f0a07ff7b0504ff7b0709ff81150fff801013ff7f0f12ff851011ff80120eff810c07ff820a09ff82090aff7e0409ff820a08ff810a0aff831015ff81100fff7f1210ff851012ff800f10ff7e0c11ff80100dff811111ff830f15ff7f1313ff8d1615ff8b1516ff881213ff8c1414ff8b0a07ff84050bff830c0fff81120cff820e12ff810d10ff7f1416ff7d0c10ff800f10ff7f110fff7e110fff7e0f10ff821111ff891412ff891613ff951e1aff920e0eff7f090aff7b0e10ff7e130dff871314ff860d12ff881112ff871011ff810e0dff7f0d13ff800f0eff801114ff841216ff82120eff8e1617ff991a1eff91110fff830b06ff8c1716ff911514ff8c1515ff8b1415ff881719ff8a1415ff8d1518ff871110ff881212ff871212ff831611ff871217ff901d1bff9d2324ff8d1011ff840a08ff8c1516ff901815ff8e140fff8c1114ff891416ff8b1414ff8a1510ff8d1516ff851518ff8b1716ff8c1515ff8a1615ff91191bff9e201fff921011ff82070bff8b1215ff8d1a15ff8c1616ff8c1515ff8c1617ff8a1512ff8c1613ff8a1515ff911517ff8c1416ff8e1215ff8a1418ff8b1518ff98201dff950e11ff81080bff8c1516ff8d1617ff8d1515ff8d1418ff8d1413ff8c1515ff8d1514ff8b1318ff871612ff871310ff8b1412ff87160eff8c1516ff9a2023ff900f10ff810a09ff881215ff8a1415ff891814ff8a1110ff811312ff8a1512ff88120eff891013ff8b1414ff911719ff921a17ff93191cff9d2c2affb13935ff941115ff860d09ff8f1615ff881718ff861212ff821912ff87130fff890f12ff8c1618ff8f1919ff94171bff991e1eff9b2122ff9c1d22ffb23738ffb63c38ff9b1d1aff840b0dff9b2628ff9e2525ff9a2122ff992022ff9b2020ff9f2428ffa22b29ffaa2e2cffa42b2effa92f2dffa62928ff9d2224ffab2f2cffac2f31ff9c1319ff810b07ffb13334ffb23536ffb03231ffad2e31ffa72829ffa92c2bffab3030ffae3335ffb03437ffad3632ffad3737ffac3736ffae3635ffa9302fff930f11ff840908ffb63e3fffb6383fffb63a3dffb2383affab2e2effa52828ffa32928ffac3031ffb13334ffb03935ffad3333ffad3337ffb23637ffa92b2fff890e0fff820908ffa41f20ffa3171bffa01e1dffa01a1cff991619ff880a0cff8f0f13ff991215ff9a1519ff971416ff971416ff941510ff971616ff8f1110ff840a09ff810909ff870d0fff880e12ff8b0d0eff820a09ff820a09ff800609ff82090aff8b100dff900d0dff890c0eff820b08ff85060cff8a0f0eff870c0eff820809ff820808ff"
        );

        // vanilla bedrock-samples v1.21.80.3: bed_head_end.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_bed_head_end = BytesFromHex(
            "6a4223ff916e3cffaa9045ff000000000000000000000000000000000000000000000000000000000000000000000000000000006a401fff917037ffa88e47ff6b4021ff8f6e39ffb09752ff00000000000000000000000000000000000000000000000000000000000000000000000000000000553e1bff8f6d3bffb49752ff573c1aff916c36ffa98a46ff00000000000000000000000000000000000000000000000000000000000000000000000000000000543c1cff8d6835ffac9049ff5b3b1dff8f6d37ff9a7846ff59391aff69411fff69431fff5a3c1cff553c1bff6c401dff6a431cff664020ff63451dff6c4021ff68431fff886431ffb2994fff583c1cff92703eff906d35ff916c36ff8a6b31ff876631ff916d3cff947539ff8d6733ff8b6836ff916d36ff946d39ff946d38ff866733ff916939ffac9548ffb7b6b8ffb7b9b9ffb9b8b4ffcbccccffcdcac9ffd0cfd1ffb9babaffb6b8b9ffb3b6b6ffb6b6b4ffb6b6b4ffcacbc8ffcbc6cbffcfcbcdffb7b9b6ffb5b3b5ffb7b4b4ffced3d2fff0ecefffeff0eefff7f7f6ffecf0ecfff1eeeefffafaf8fff3ebefffeeefeeffebeeebfff0ede9ffedeef0ffe8eef3ffd4d0cfffb7b5b6ffb6b7b5ffcfcdd1ffefebefffebf2f2fff1efebffefefeefffaf6f8fffaf7f7fff7f8f6fff8f8fafff8fafafff9fbf8ffefeceeffedeaecffcecfcfffb5b8b9ffb6b8b6ffb6b6b5ffbbb7bcffbbb7b4ffcdcdcbffcccfd2ffced0d1ffb4b5b7ffb8b3b5ffb5b3b1ffbab9b3ffb6b8b8ffc9c9c9ffcfcfcfffced2cfffbbb3b6ff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );

        // vanilla bedrock-samples v1.21.80.3: bed_head_side.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_bed_head_side = BytesFromHex(
            "6a421fff946d38ffaa8e44ff000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006a4222ff916d38ffb19651ff000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000005a3f1bff926f39ffaa914cff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000573a1bff8d6c3dff927547ff65411dff6b441eff6b4120ff68421fff68421dff6d451fff6a4423ff6a441fff563b1bff6a421fff6f431eff583f1aff674520ff5b3b1bff8f6d36ff916e35ff8b6e37ff926d39ff896735ff916a34ff8c6832ff8e6a35ff916b37ff936f3bff8f703aff916c37ff916c38ff8b682fff896931ffb6b6b7ffb4b8b8ffb3b6b6ffc8c8ccffc9cacdffd0d6ceffb8bab9ffb5b6b6ff6e0000ff6a0000ff6e0101ff6c0200ff6d0500ff6a0300ff6c0103ff690000ffb6b6b5ffd1cfcfffe8e7e9ffeeeeeeffefeaecffeee9eaffcacfd0ffb5b5b6ff6b0002ff8c121aff891617ff81120affa22829ff730000ff7e1210ff8f1315ffb6b5b8ffcdd2cfffeeefeeffedf0eeffe9eef1ffeef1eeffedeeeeffb9b6b4ff6c0000ff840f12ff8c1511ffa3272affb13534ff7a0401ff8c1515ff891413ffb5b0b6ffb7b6bbffbbbab7ffb5b8b9ffcccbcbffcdd3cfffcecdd0ffb6b6b6ff6b0003ff840809ff850c09ff9f2d2aff840906ff790404ff840506ff830f09ff00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );

        // vanilla bedrock-samples v1.21.80.3: bed_head_top.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_bed_head_top = BytesFromHex(
            "bab5b9ffb5b6b3ffb7b6b4ffafafaeffa2ada7ffb4b6afffb0b5b5ffb7b6bcff6f0002ff720708ff790504ff850a08ff810609ff7c0806ff840909ff84060bffb6b8b6ffd2d0d4ffc9cacbffb4b7bbffb4b4b2ffc0bec2ffd1cfcfffd1d2ccff740000ff75070bff7c0609ff8e1010ff981413ff780507ff800709ff83060affb6b7b2ffc8c7c9ffd8d7d8ffc7d0cdffcfceceffd8d7d5ffdfdcddffcfd1ceff6f0000ff770600ff841310ff9a1e1dffab302eff7f0607ff841013ff851312ffacabacffb3b3b7ffcececeffd7d5d5ffdddfdeffd8d9deffdad7d9ffc4c4c7ff6e0300ff770409ff841214ffa52a2fffaa312fff750704ff80110eff7c1311ffacaaabffb0b2b4ffd2cec7ffdddcdcffedebe7ffd9d7ddffdbdbdbffc1c3c5ff680000ff770906ff8f1716ff951c1dffac3434ff760504ff85100fff7f0f0dffafacafffc1c1c1ffdce1e3ffe9e9ecffe9e9ecffebe9e9ffe1dce3ffc6c3c3ff6d0000ff790602ff8c1519ff94211cffb43e3cff860d0cff8b1012ff8b1715ffb7b3b2ffd0cfcbffece9ebffedeaebffeeeaf0ffedeef1fff1eff2ffd3d2d2ff6c0000ff740405ff891410ff9a1b18ffb63e3eff890f0eff871415ff8f1513ffb2aeb4ffcccfd1ffe8eaebffeceae8ffeeeef0fff5f1effff1f4f0ffd6d6d6ff6d0000ff74050bff851210ff931c1cffb73b3fff8e0f0bff8a1515ff8b1514ffb5b6b5ffccd1cfffebeeebffebebecffeceef1fff1f1f1ffeef1efffd7dadcff6a0200ff740006ff861413ff951d1bffbe4a4bff8d0f0eff8b1219ff8c1515ffb6b5bbffd1ced2ffe5e9f1ffebeaecffebedf0fff5f1f1fff0eff1ffd5d5d5ff6e0000ff770206ff85100eff9c2624ffcc6362ff961312ff901714ff8d1515ffb2b3b0ffd3d1d1ffedeaeaffedeaebfff1f3f1fff9f4f6fff7f6f3ffd5d5daff6f0000ff79080aff811414ffa2262effc45a57ff911215ff8d1214ff89170fffa7a1a3ffcbc5c9fff1e9efffede7eafffef5f8fffdfdfcfffcfefeffd1d7d3ff720001ff7e0a09ff96201bffb74243ffbe5453ff931110ff971e1fff9d2b2bff9c9ea0ffb9babdffe5e7e7fff4f3f0fffcfbfefffffdfffff9f9faffcecbc9ff730003ff840909ff9c2529ffb64243ffc25654ff971619ffaa302cffaa3232ffa09c9dffb1b1aeffd1ced0fff3f3f9fffffffffffffcfffff2f2f3ffc6c8c5ff6c0000ff7f0709ffa12724ffb44041ffbe4c50ff921917ffaf3635ffb43d3dff9fa4a1ffb0aaabffb8b3b5ffd2d7d6fff6f4f7ffe6ebeaffd0d3d0ffc6c4cbff760401ff840709ff890d0fff9e2925ff9e2426ff89050bff9b1916ffa72224ffb1b0afff9f9c9dff9da09fffb0b1b2ffc9c7c7ffbcc0beffadadadffb2b3afff710000ff83090cff8a1413ff931f1cff961e20ff860a07ff7e0904ff810908ff"
        );

        // vanilla bedrock-samples v1.21.80.3: furnace_front_off.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_furnace_front_off = BytesFromHex(
            "3c3b3bff686868ff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff3c3b3bff686868ff3c3b3bff3c3b3bff9d9d9dff777777ff111111ff212121ff212121ff212121ff212121ff212121ff212121ff212121ff212121ff111111ff777777ff919191ff3c3b3bff504e4eff919191ff9d9d9dff111111ff111111ff111111ff212121ff212121ff212121ff212121ff111111ff111111ff111111ff919191ff777777ff504e4eff504e4eff9d9d9dff777777ff504e4eff111111ff111111ff111111ff111111ff111111ff111111ff111111ff111111ff504e4eff919191ff919191ff504e4eff504e4eff9d9d9dffa8a8a8ff919191ff5d5b5bff212121ff111111ff111111ff111111ff111111ff212121ff5d5b5bff919191ffa8a8a8ff9d9d9dff504e4eff3c3b3bff9d9d9dffb0b0b0ffa8a8a8ffb0b0b0ffa8a8a8ff858585ff858585ff858585ff858585ffa8a8a8ffb0b0b0ffa8a8a8ffa8a8a8ffa8a8a8ff504e4eff504e4effa8a8a8ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffb0b0b0ffc5c5c5ff504e4eff504e4eff686868ff686868ff777777ff686868ff777777ff686868ff5d5b5bff686868ff777777ff5d5b5bff686868ff777777ff858585ff777777ff3c3b3bff3c3b3bff777777ff919191ffa8a8a8ffa8a8a8ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffa8a8a8ffa8a8a8ff919191ff777777ff3c3b3bff504e4eff5d5b5bff919191ff111111ff111111ff3c3b3bff3c3b3bff3c3b3bff3c3b3bff3c3b3bff3c3b3bff111111ff111111ff686868ff777777ff504e4eff3c3b3bff5d5b5bff919191ff111111ff111111ff111111ff212121ff212121ff212121ff212121ff111111ff111111ff111111ff686868ff777777ff3c3b3bff3c3b3bff5d5b5bff777777ff3c3b3bff111111ff111111ff111111ff111111ff111111ff111111ff111111ff111111ff3c3b3bff858585ff777777ff504e4eff504e4eff686868ff777777ff858585ff3c3b3bff212121ff212121ff212121ff212121ff212121ff212121ff3c3b3bff858585ff858585ff686868ff504e4eff504e4eff777777ff686868ff777777ff919191ff858585ff919191ff858585ff777777ff686868ff858585ff777777ff919191ff777777ff5d5b5bff3c3b3bff504e4eff5d5b5bff777777ff686868ff777777ff686868ff777777ff777777ff686868ff777777ff686868ff777777ff777777ff686868ff686868ff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff504e4eff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff3c3b3bff504e4eff504e4eff504e4eff504e4eff"
        );

        // vanilla bedrock-samples v1.21.80.3: furnace_front_on.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_furnace_front_on = BytesFromHex(
            "3c3b3bff686868ff3c3b3bffffd800ffff8f00ffffd800ffffff97ffff8f00ffffffffffffff97ffffffffffff8f00ffff8f00ff3c3b3bff686868ff3c3b3bff3c3b3bff9d9d9dff777777ffc35d1bffffd800ffffff97ffffffffffffd800ffffffffffffd800ffffff97ffffd800ffc35d1bff777777ff919191ff3c3b3bff504e4eff919191ff9d9d9dff111111ffff8f00ffffff97ffffd800ffffd800ffffff97ffff8f00ffffd800ffffd800ff111111ff919191ff777777ff504e4eff504e4eff9d9d9dff777777ff504e4effffd800ffffd800ffc35d1bffff8f00ffff8f00ffc35d1bff111111ffff8f00ff504e4eff919191ff919191ff504e4eff504e4eff9d9d9dffa8a8a8ff919191ffff8f00ff212121ff111111ff111111ff111111ff111111ff212121ff5d5b5bff919191ffa8a8a8ff9d9d9dff504e4eff3c3b3bff9d9d9dffb0b0b0ffa8a8a8ffb0b0b0ffa8a8a8ff858585ff858585ff858585ff858585ffa8a8a8ffb0b0b0ffa8a8a8ffa8a8a8ffa8a8a8ff504e4eff504e4effa8a8a8ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffb0b0b0ffc5c5c5ff504e4eff504e4eff686868ff686868ff777777ff686868ff777777ff686868ff5d5b5bff686868ff777777ff5d5b5bff686868ff777777ff858585ff777777ff3c3b3bff3c3b3bff777777ff919191ffa8a8a8ffa8a8a8ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffa8a8a8ffa8a8a8ff919191ff777777ff3c3b3bff504e4eff5d5b5bff919191ff111111ff111111ff3c3b3bff3c3b3bff3c3b3bff3c3b3bff3c3b3bff3c3b3bff111111ff111111ff686868ff777777ff504e4eff3c3b3bff5d5b5bff919191ff111111ff111111ff111111ff212121ff212121ff212121ff212121ff111111ff111111ff111111ff686868ff777777ff3c3b3bff3c3b3bff5d5b5bff777777ff3c3b3bff111111ff111111ff111111ff111111ff111111ff111111ff111111ff111111ff3c3b3bff858585ff777777ff504e4eff504e4eff686868ff777777ff858585ff3c3b3bff212121ff212121ff212121ff212121ff212121ff212121ff3c3b3bff858585ff858585ff686868ff504e4eff504e4eff777777ff686868ff777777ff919191ff858585ff919191ff858585ff777777ff686868ff858585ff777777ff919191ff777777ff5d5b5bff3c3b3bff504e4eff5d5b5bff777777ff686868ff777777ff686868ff777777ff777777ff686868ff777777ff686868ff777777ff777777ff686868ff686868ff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff504e4eff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff3c3b3bff504e4eff504e4eff504e4eff504e4eff"
        );

        // vanilla bedrock-samples v1.21.80.3: furnace_side.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_furnace_side = BytesFromHex(
            "504e4eff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff3c3b3bff504e4eff504e4eff504e4eff504e4eff504e4eff686868ff686868ff686868ff777777ff777777ff777777ff777777ff777777ff777777ff686868ff777777ff777777ff686868ff686868ff504e4eff504e4eff7f7f7fff919191ff9d9d9dff919191ff9d9d9dffa8a8a8ff9d9d9dff9d9d9dff9d9d9dff9d9d9dff919191ff9d9d9dff919191ff7f7f7fff504e4eff504e4eff919191ff9d9d9dffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ff9d9d9dffa8a8a8ff9d9d9dff9d9d9dff9d9d9dff9d9d9dff919191ff3c3b3bff504e4eff919191ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ffa8a8a8ff9d9d9dff9d9d9dff3c3b3bff3c3b3bff9d9d9dffb0b0b0ff9d9d9dffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffb0b0b0ffa8a8a8ffb0b0b0ffa8a8a8ffa8a8a8ff9d9d9dff504e4eff504e4effa8a8a8ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffc5c5c5ffb0b0b0ffc5c5c5ff504e4eff504e4eff686868ff777777ff858585ff858585ff858585ff919191ff5d5b5bff777777ff5d5b5bff5d5b5bff686868ff777777ff777777ff686868ff3c3b3bff3c3b3bff777777ff858585ff858585ff858585ff919191ff5d5b5bff777777ff858585ff858585ff686868ff919191ff858585ff858585ff777777ff3c3b3bff504e4eff5d5b5bff777777ff777777ff686868ff686868ff858585ff919191ff858585ff858585ff686868ff777777ff919191ff919191ff686868ff504e4eff3c3b3bff504e4eff504e4eff686868ff858585ff777777ff686868ff858585ff919191ff858585ff5d5b5bff686868ff686868ff5d5b5bff5d5b5bff3c3b3bff3c3b3bff686868ff777777ff5d5b5bff858585ff919191ff858585ff686868ff777777ff5d5b5bff777777ff858585ff858585ff777777ff5d5b5bff504e4eff504e4eff858585ff858585ff686868ff858585ff858585ff919191ff777777ff686868ff5d5b5bff919191ff858585ff858585ff858585ff686868ff504e4eff504e4eff777777ff858585ff858585ff686868ff777777ff858585ff858585ff5d5b5bff686868ff5d5b5bff919191ff919191ff777777ff5d5b5bff504e4eff504e4eff5d5b5bff686868ff686868ff777777ff686868ff686868ff686868ff777777ff858585ff686868ff777777ff686868ff686868ff5d5b5bff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff504e4eff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff3c3b3bff504e4eff504e4eff504e4eff504e4eff"
        );

        // vanilla bedrock-samples v1.21.80.3: furnace_top.png (rows flipped to bottom-origin)
        private static byte[] _vanilla_furnace_top = BytesFromHex(
            "504e4eff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff3c3b3bff504e4eff504e4eff504e4eff504e4eff504e4eff686868ff777777ff686868ff5d5b5bff686868ff686868ff5d5b5bff504e4eff777777ff858585ff777777ff686868ff777777ff5d5b5bff504e4eff504e4eff777777ff858585ff858585ff686868ff777777ff777777ff777777ff686868ff5d5b5bff777777ff5d5b5bff5d5b5bff919191ff777777ff504e4eff504e4eff858585ff858585ff858585ff777777ff919191ff919191ff919191ff858585ff777777ff5d5b5bff686868ff5d5b5bff5d5b5bff858585ff3c3b3bff504e4eff777777ff919191ff686868ff858585ff919191ff919191ff919191ff919191ff919191ff686868ff777777ff858585ff777777ff5d5b5bff3c3b3bff3c3b3bff686868ff5d5b5bff5d5b5bff686868ff858585ff919191ff919191ff919191ff858585ff777777ff919191ff919191ff919191ff777777ff504e4eff504e4eff504e4eff858585ff858585ff858585ff777777ff686868ff858585ff686868ff686868ff858585ff919191ff919191ff919191ff919191ff504e4eff504e4eff858585ff777777ff919191ff919191ff919191ff858585ff686868ff858585ff777777ff777777ff919191ff919191ff919191ff919191ff3c3b3bff3c3b3bff777777ff919191ff919191ff919191ff858585ff686868ff919191ff919191ff919191ff686868ff858585ff919191ff919191ff777777ff3c3b3bff504e4eff686868ff858585ff858585ff5d5b5bff5d5b5bff858585ff919191ff919191ff919191ff858585ff686868ff919191ff858585ff5d5b5bff504e4eff3c3b3bff504e4eff5d5b5bff5d5b5bff858585ff777777ff686868ff686868ff777777ff858585ff5d5b5bff686868ff686868ff5d5b5bff686868ff3c3b3bff3c3b3bff686868ff777777ff5d5b5bff919191ff919191ff919191ff777777ff686868ff5d5b5bff777777ff858585ff919191ff858585ff777777ff504e4eff504e4eff777777ff777777ff686868ff858585ff919191ff919191ff919191ff777777ff5d5b5bff919191ff919191ff858585ff919191ff686868ff504e4eff504e4eff777777ff858585ff858585ff686868ff858585ff919191ff858585ff5d5b5bff777777ff777777ff919191ff919191ff777777ff5d5b5bff504e4eff504e4eff5d5b5bff686868ff858585ff777777ff686868ff686868ff5d5b5bff777777ff858585ff858585ff686868ff5d5b5bff5d5b5bff5d5b5bff504e4eff504e4eff504e4eff3c3b3bff3c3b3bff504e4eff3c3b3bff3c3b3bff3c3b3bff504e4eff504e4eff504e4eff3c3b3bff504e4eff504e4eff504e4eff504e4eff"
        );
        // vanilla bedrock-samples v1.21.80.3: door_wood_lower.png, rows flipped to bottom-origin
        private static byte[] _vanilla_door_wood_lower = BytesFromHex(
            "513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff513d24ff67502cff67502cff7e6237ff7e6237ff967441ffa6824dffa6824dff967441ff7e6237ff7e6237ff967441ffa6824dffa6824dff967441ff7e6237ff7e6237ffb8945fff67502cff7e6237ffa6824dffb58d50ffb58d50ffb58d50ffa6824dff7e6237ffa6824dffb58d50ffb58d50ffb58d50ffa6824dff7e6237ff967441ffa6824dff7e6237ff967441ffb58d50ff7e6237ff7e6237ff967441ff967441ff67502cffb58d50ff7e6237ff7e6237ff967441ff967441ff67502cff967441ffa6824dff513d24ff967441ffb58d50ff7e6237ff967441ff967441ff967441ff67502cffb58d50ff7e6237ff967441ff967441ff967441ff67502cff967441ff6b6f7aff67502cff967441ffa6824dff967441ff967441ff967441ff967441ff67502cffa6824dff967441ff967441ff967441ff967441ff67502cff967441ff808b95ff67502cff967441ffa6824dff67502cff67502cff67502cff67502cff7e6237ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ffb8945fff7e6237ff7e6237ff7e6237ff7e6237ff967441ffa6824dffa6824dff967441ff7e6237ff7e6237ff967441ffa6824dffa6824dffa6824dff967441ffa6824dff513d24ff7e6237ffa6824dffb58d50ffb58d50ffb58d50ffa6824dff7e6237ffa6824dffb58d50ffb58d50ffb58d50ffa6824dff7e6237ff967441ffb8945fff67502cff967441ffb58d50ff7e6237ff7e6237ff967441ff967441ff67502cffb58d50ff7e6237ff7e6237ff967441ff967441ff67502cff967441ffb8945fff67502cff967441ffb58d50ff7e6237ff967441ff967441ff967441ff67502cffb58d50ff7e6237ff967441ff967441ff967441ff67502cff967441ffb8945fff7e6237ff967441ffa6824dff967441ff967441ff967441ff967441ff67502cffa6824dff967441ff967441ff967441ff967441ff67502cff967441ffb8945fff513d24ff967441ffa6824dff67502cff67502cff67502cff67502cff513d24ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ffb8945fff67502cff967441ff967441ff7e6237ff7e6237ff967441ff967441ff967441ff7e6237ff7e6237ff967441ff967441ffa6824dffa6824dff967441ffa6824dff67502cff967441ffa6824dffb58d50ffb58d50ffb58d50ffa6824dff7e6237ffa6824dffb58d50ffb58d50ffb58d50ffa6824dff7e6237ff967441ffa6824dff7e6237ff967441ffb58d50ff513d24ff513d24ff967441ff967441ff67502cffb58d50ff7e6237ff7e6237ff967441ff967441ff67502cff967441ff6b6f7aff"
        );

        // vanilla bedrock-samples v1.21.80.3: door_wood_upper.png, rows flipped to bottom-origin
        private static byte[] _vanilla_door_wood_upper = BytesFromHex(
            "513d24ff967441ff7e6237ff513d24ff6b6f7aff967441ff967441ff67502cffa6824dff7e6237ff967441ff967441ff967441ff67502cff967441ff808b95ff67502cff967441ff6b6f7aff808b95ff808b95ff967441ff967441ff67502cffa6824dff967441ff967441ff967441ff967441ff67502cff967441ffa6824dff67502cff967441ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ffb8945fff7e6237ff967441ff7e6237ff7e6237ff967441ffa6824dffa6824dffa6824dff7e6237ff7e6237ff967441ffa6824dffa6824dffa6824dff967441ffb8945fff513d24ff967441ffa6824dffa6824dffb58d50ffb58d50ffa6824dff7e6237ffa6824dffa6824dffb58d50ffb58d50ffa6824dff7e6237ff967441ffb8945fff67502cff967441ffa6824dff0000000000000000000000000000000067502cffa6824dff0000000000000000000000000000000067502cff967441ffb8945fff67502cff967441ffb58d50ff0000000000000000000000000000000067502cffb58d50ff0000000000000000000000000000000067502cff967441ffa6824dff7e6237ff967441ffa6824dff0000000000000000000000000000000067502cffa6824dff0000000000000000000000000000000067502cff967441ffb8945fff513d24ff967441ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ffb8945fff67502cff967441ffa6824dffa6824dffb58d50ffb58d50ffa6824dff7e6237ffa6824dffa6824dffb58d50ffb58d50ffa6824dff7e6237ff967441ffa6824dff67502cff967441ffa6824dff0000000000000000000000000000000067502cffa6824dff0000000000000000000000000000000067502cff967441ff6f6f6fff7e6237ff967441ffb58d50ff0000000000000000000000000000000067502cffb58d50ff0000000000000000000000000000000067502cff967441ff808b95ff513d24ff967441ffa6824dff0000000000000000000000000000000067502cffa6824dff0000000000000000000000000000000067502cff967441ffb8945fff67502cff967441ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ff67502cff67502cff67502cff67502cff513d24ff967441ffb8945fff67502cff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ff967441ffa6824dff967441ffb8945fffb8945fffa6824dffb8945fffb8945fffb8945fffb8945fffa6824dffb8945fffb8945fffb8945fffb8945fffa6824dffa6824dffb8945fff"
        );

        // vanilla bedrock-samples v1.21.80.3: chest_front_v.png (chest atlas UV-extracted), rows flipped to bottom-origin
        private static byte[] _vanilla_chest_front_v = BytesFromHex(
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002b261fff84622fff956c2eff90662aff7f5f22ff956c2eff956c2eff7f5f22ff956c2eff956c2eff7f5f22ff7f5f22ff956c2eff463e32ff00000000000000002b261fff956c2effa47227ff8f691dff8f691dff8f691dff8f691dff8f691dffab792dff8f691dffab792dffab792dffab792dff463e32ff00000000000000002a251dff8e6126ffa26b23ffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa26b23ffa26b23ffa26b23ffa76e1fffa26b23ff453c2fff000000000000000028241dff90662affa47227ffa47227ffa47227ff8f691dff8f691dff8f691dff8f691dffab792dffab792dffa47227ffa47227ff463e32ff000000000000000028241dff7f5f22ffab792dffa76e1fff8f691dff8f691dff8f691dffa47227ffa47227ffab792dff8f691dff8f691dff8f691dff413b2fff000000000000000028241dff956c2effab792dffa47227ffa47227ffa47227ffab792dffab792dff8f691dffa76e1fffab792dffab792dffab792dff463e32ff00000000000000002a251dff825b26ff8e6126ff8e6126ff8e6126ff77501cff614116ff614116ff734f1eff8e6126ff8e6126ff8e6126ff926323ff453c2fff00000000000000002b261fff775a30ff775a30ff775a30ff755429ff45351aff45351aff45351aff614927ff775a30ff775a30ff73562eff775a30ff463e32ff00000000000000001f1c17ff332e25ff332e25ff363026ff363026ff2c271eff2c271eff2c271eff2c271fff363026ff363026ff363026ff332e25ff413b2fff00000000000000001f1c17ff332e25ff332e25ff363026ff363026ff2c271eff2c271eff2c271eff2c271fff363026ff363026ff363026ff332e25ff413b2fff00000000000000002b261fff90662aff90662aff926323ff7f5f22ff543e16ff614116ff543e16ff795825ff956c2eff7f5f22ff7f5f22ff7f5f22ff463e32ff00000000000000002a251dffa76e1fffa26b23ffa26b23ffa26b23ff885919ff543e16ff543e16ff885919ffa76e1fffa76e1fffa76e1fffa26b23ff443c30ff000000000000000028241dffab792dffa76e1fffa47227ffab792dffab792dff8b6224ff855c1fffa47227ff8f691dff8f691dff8f691dffab792dff463e32ff0000000000000000332e25ff463e32ff463e32ff463e32ff463e32ff463e32ff413b2fff453d31ff463e32ff463e32ff413b2fff413b2fff413b2fff463e32ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );

        // vanilla bedrock-samples v1.21.80.3: chest_side_v.png (chest atlas UV-extracted), rows flipped to bottom-origin
        private static byte[] _vanilla_chest_side_v = BytesFromHex(
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002b261fff84622fff956c2eff90662aff7f5f22ff956c2eff956c2eff7f5f22ff956c2eff956c2eff7f5f22ff7f5f22ff7f5f22ff463e32ff00000000000000002b261fff956c2effa47227ff8f691dff8f691dff8f691dff8f691dff8f691dffab792dff8f691dffab792dffab792dffab792dff463e32ff00000000000000002a251dff8e6126ffa26b23ffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa26b23ffa26b23ffa26b23ffa76e1fffa76e1fff453c2fff000000000000000028241dff90662affa47227ffa47227ffa47227ff8f691dff8f691dff8f691dff8f691dffab792dffab792dffa47227ffa47227ff463e32ff000000000000000028241dff7f5f22ffab792dffa76e1fff8f691dff8f691dff8f691dffa47227ffa47227ffab792dff8f691dff8f691dff8f691dff413b2fff000000000000000028241dff956c2effab792dffa47227ffa47227ffa47227ffab792dffab792dff8f691dffa76e1fffab792dffab792dffab792dff463e32ff00000000000000002a251dff825b26ff8e6126ff8e6126ff8e6126ff926323ff926323ff926323ff8e6126ff8e6126ff8e6126ff8e6126ff8e6126ff453c2fff00000000000000002b261fff775a30ff775a30ff775a30ff755429ff695229ff695229ff695229ff775a30ff775a30ff775a30ff73562eff775a30ff463e32ff00000000000000001f1c17ff332e25ff332e25ff363026ff363026ff363026ff363026ff332e25ff373127ff363026ff363026ff363026ff362f25ff413b2fff00000000000000001f1c17ff332e25ff332e25ff363026ff363026ff363026ff363026ff332e25ff373127ff363026ff363026ff363026ff362f25ff413b2fff00000000000000002b261fff90662aff90662aff926323ff7f5f22ff7f5f22ff956c2eff7f5f22ff956c2eff956c2eff7f5f22ff7f5f22ff7f5f22ff463e32ff00000000000000002a251dffa76e1fffa26b23ffa26b23ffa26b23ffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa26b23ff443c30ff000000000000000028241dffab792dffa76e1fffa47227ffab792dffab792dffab792dffa47227ffa47227ff8f691dff8f691dff8f691dff8f691dff463e32ff0000000000000000332e25ff463e32ff463e32ff463e32ff463e32ff463e32ff413b2fff453d31ff463e32ff463e32ff413b2fff413b2fff413b2fff463e32ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );

        // vanilla bedrock-samples v1.21.80.3: chest_top_v.png (chest atlas UV-extracted), rows flipped to bottom-origin
        private static byte[] _vanilla_chest_top_v = BytesFromHex(
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000332e25ff352f26ff322d26ff352f26ff362f25ff362f25ff362f25ff352f26ff382f25ff352f26ff352f26ff382f25ff362f25ff2a251dff0000000000000000373127ff84622fff956c2eff90662aff7f5f22ff956c2eff956c2eff7f5f22ff956c2eff956c2eff7f5f22ff7f5f22ff84622fff373127ff0000000000000000373127ff956c2effa47227ff8f691dff8f691dff8f691dff8f691dff8f691dffab792dff8f691dffab792dffab792dff956c2eff373127ff0000000000000000362f25ff8e6126ffa26b23ffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa26b23ffa26b23ffa26b23ffa76e1fff8e6126ff362f25ff0000000000000000332e25ff90662affa47227ffa47227ffa47227ff8f691dff8f691dff8f691dff8f691dffab792dffab792dffa47227ff90662aff373127ff0000000000000000332e25ff7f5f22ffab792dffa76e1fff8f691dff8f691dff8f691dffa47227ffa47227ffab792dff8f691dff8f691dff7f5f22ff332e25ff0000000000000000332e25ff956c2effab792dffa47227ffa47227ffa47227ffab792dffab792dff8f691dffa76e1fffab792dffab792dff956c2eff373127ff0000000000000000362f25ff926323ffa26b23ffa26b23ffa26b23ffa76e1fffa76e1fffa76e1fffa26b23ffa26b23ffa26b23ffa26b23ff926323ff362f25ff0000000000000000373127ff956c2effab792dffab792dffa76e1fff8f691dff8f691dff8f691dffab792dffab792dffab792dffa47227ff956c2eff373127ff0000000000000000332e25ff7f5f22ff8f691dffa47227ffa47227ffa47227ffa47227ff8f691dffab792dffa47227ffa47227ffa47227ff7f5f22ff332e25ff0000000000000000373127ff90662affa47227ffa76e1fff8f691dff8f691dffab792dff8f691dffab792dffab792dff8f691dff8f691dff7f5f22ff373127ff0000000000000000362f25ff926323ffa26b23ffa26b23ffa26b23ffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fffa76e1fff8e6126ff352f26ff0000000000000000332e25ff84622fff7f5f22ff90662aff7f5f22ff7f5f22ff7f5f22ff7f5f22ff7f5f22ff90662aff90662aff926323ff84622fff373127ff000000000000000028241dff373127ff373127ff373127ff373127ff373127ff332e25ff363026ff373127ff373127ff332e25ff332e25ff332e25ff2b261fff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        );
        private static void ChestTile(Color32[] px, System.Random rng, string kind)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    Color32 c = new Color32(158, 116, 68, 255);
                    if (y == 0 || y == 15 || x == 0 || x == 15) { c = new Color32(104, 74, 42, 255); }
                    else if (rng.Next(6) == 0) { c = new Color32(146, 106, 62, 255); }
                    px[Idx(x, y)] = c;
                }
            }
            if (kind == "side" || kind == "top")
            {
                for (int x = 1; x < 15; x++)
                {
                    int bandY = kind == "top" ? 7 : 11;
                    px[Idx(x, bandY)] = new Color32(88, 88, 92, 255);
                    px[Idx(x, bandY + 1)] = new Color32(88, 88, 92, 255);
                }
            }
            if (kind == "front")
            {
                for (int y = 5; y < 11; y++)
                {
                    px[Idx(7, y)] = new Color32(88, 88, 92, 255);
                    px[Idx(8, y)] = new Color32(88, 88, 92, 255);
                }
                px[Idx(7, 7)] = new Color32(226, 176, 88, 255);
                px[Idx(8, 7)] = new Color32(226, 176, 88, 255);
            }
        }

        private static void BedTile(Color32[] px, System.Random rng, bool head)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    Color32 c = new Color32(168, 42, 40, 255);
                    if (y < 3 || y > 12 || x < 2 || x > 13) { c = new Color32(126, 30, 28, 255); }
                    px[Idx(x, y)] = c;
                }
            }
            if (head)
            {
                for (int y = 4; y < 12; y++)
                {
                    for (int x = 3; x < 13; x++)
                    {
                        px[Idx(x, y)] = new Color32(236, 240, 242, 255);
                    }
                }
                for (int x = 3; x < 13; x++) { px[Idx(x, 4)] = new Color32(214, 218, 222, 255); }
            }
            else
            {
                for (int x = 2; x < 14; x += 3)
                {
                    for (int y = 3; y < 13; y++)
                    {
                        px[Idx(x, y)] = new Color32(142, 34, 32, 255);
                    }
                }
            }
        }

        private static void BedSideTile(Color32[] px, System.Random rng)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    Color32 c;
                    if (y >= 10) { c = new Color32(110, 84, 52, 255); }
                    else if (y >= 7) { c = new Color32(236, 240, 242, 255); }
                    else { c = new Color32(168, 42, 40, 255); }
                    px[Idx(x, y)] = c;
                }
            }
            for (int x = 0; x < 16; x++) { px[Idx(x, 7)] = new Color32(214, 218, 222, 255); }
        }

        private static void FenceLinkTile(Color32[] px, System.Random rng)
        {
            Stripes(px, rng, 102, 81, 50, 18, true);
        }

        private static void FurnaceTile(Color32[] px, System.Random rng, bool front, bool lit)
        {
            // dark iron-stone body so it reads instantly against wood/plank;
            // front carries a BIG black firemouth with an iron rim (glowing
            // orange fire + fullbright glow bit when lit).
            Speckle(px, rng, 96, 96, 100, 10);
            // iron band across the top
            for (int x = 0; x < 16; x++)
            {
                px[Idx(x, 13)] = new Color32(130, 130, 138, 255);
                px[Idx(x, 14)] = new Color32(130, 130, 138, 255);
            }
            if (front)
            {
                // firemouth: wide black arch with bright iron rim
                for (int y = 2; y <= 10; y++)
                {
                    for (int x = 2; x <= 13; x++)
                    {
                        bool rim = x == 2 || x == 13 || y == 2 || y == 10;
                        px[Idx(x, y)] = rim
                            ? new Color32(150, 150, 158, 255)
                            : (lit ? new Color32(255, 150, 30, 255) : new Color32(18, 16, 16, 255));
                    }
                }
                if (lit)
                {
                    for (int y = 4; y <= 8; y++)
                        for (int x = 4; x <= 11; x++)
                        {
                            px[Idx(x, y)] = new Color32(255, 220, 120, 255);
                        }
                }
            }
        }
    }
}
