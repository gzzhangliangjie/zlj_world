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
                case TileId.DoorTile: DoorTile(px, rng, false); break;
                case TileId.DoorTileTop: DoorTile(px, rng, true); break;
                case TileId.ChestSide: ChestTile(px, rng, "side"); break;
                case TileId.ChestTop: ChestTile(px, rng, "top"); break;
                case TileId.ChestFront: ChestTile(px, rng, "front"); break;
                case TileId.BedHeadTop: BedTile(px, rng, true); break;
                case TileId.BedFootTop: BedTile(px, rng, false); break;
                case TileId.BedSide: BedSideTile(px, rng); break;
                case TileId.FenceLink: FenceLinkTile(px, rng); break;
                case TileId.FurnaceSide: FurnaceTile(px, rng, false, false); break;
                case TileId.FurnaceFront: FurnaceTile(px, rng, true, false); break;
                case TileId.FurnaceLit: FurnaceTile(px, rng, true, true); break;
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
