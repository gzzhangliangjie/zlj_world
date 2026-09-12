using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Items;

namespace VoxelCraft.Art
{
    /// <summary>Procedural 24x24 pixel icons for the tool bar (hand/axe/pick/clock).</summary>
    public static class ToolIcons
    {
        private static readonly Dictionary<ToolType, Texture2D> cache = new Dictionary<ToolType, Texture2D>();

        public static Texture2D Get(ToolType tool)
        {
            if (cache.TryGetValue(tool, out var tex) && tex != null)
            {
                return tex;
            }
            tex = Paint(tool);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            cache[tool] = tex;
            return tex;
        }

        private static Texture2D Paint(ToolType tool)
        {
            var px = new Color32[Size * Size];
            switch (tool)
            {
                case ToolType.Axe: PaintAxe(px); break;
                case ToolType.Sword: PaintSword(px); break;
                case ToolType.Pickaxe: PaintPickaxe(px); break;
                case ToolType.Clock: PaintClock(px); break;
                default: PaintHand(px); break;
            }
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            return tex;
        }

        private static Texture2D itemMeat;
        private static Texture2D itemSeeds;
        private static Texture2D itemCarrot;

        /// <summary>Icon for non-tool items carried in the backpack.</summary>
        public static Texture2D GetItem(string item)
        {
            if (item == "meat")
            {
                if (itemMeat == null)
                {
                    var px = new Color32[Size * Size];
                    FillAll(px, 0, 0, 0, 0);
                    // Drumstick: meat blob + bone
                    for (int y = 6; y < 18; y++)
                    {
                        for (int x = 5; x < 16; x++)
                        {
                            float dx = x - 10f, dy = y - 11.5f;
                            if (dx * dx + dy * dy * 1.15f < 20f)
                            {
                                px[y * Size + x] = new Color32(178, 68, 58, 255);
                            }
                        }
                    }
                    Rect(px, 10, 4, 4, 3, 214, 140, 120);
                    Rect(px, 15, 3, 5, 2, 235, 228, 215);
                    Rect(px, 14, 2, 3, 3, 245, 240, 230);
                    Rect(px, 14, 8, 3, 3, 245, 240, 230);
                    itemMeat = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                    itemMeat.SetPixels32(px);
                    itemMeat.filterMode = FilterMode.Point;
                    itemMeat.wrapMode = TextureWrapMode.Clamp;
                    itemMeat.Apply(false, false);
                }
                return itemMeat;
            }
            if (item == "seeds")
            {
                if (itemSeeds == null)
                {
                    var px = new Color32[Size * Size];
                    FillAll(px, 0, 0, 0, 0);
                    // A small pouch of green seeds
                    Rect(px, 6, 8, 12, 10, 120, 84, 50);
                    Rect(px, 7, 9, 10, 8, 150, 108, 62);
                    for (int i = 0; i < 7; i++)
                    {
                        int x = 8 + (i * 3) % 9;
                        int y = 10 + (i * 2) % 6;
                        px[y * Size + x] = new Color32(110, 178, 60, 255);
                        px[(y + 1) * Size + x] = new Color32(88, 150, 48, 255);
                    }
                    itemSeeds = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                    itemSeeds.SetPixels32(px);
                    itemSeeds.filterMode = FilterMode.Point;
                    itemSeeds.wrapMode = TextureWrapMode.Clamp;
                    itemSeeds.Apply(false, false);
                }
                return itemSeeds;
            }
            if (item == "carrot")
            {
                if (itemCarrot == null)
                {
                    var px = new Color32[Size * Size];
                    FillAll(px, 0, 0, 0, 0);
                    // Orange carrot, diagonal, with green top
                    for (int i = 0; i < 11; i++)
                    {
                        Rect(px, 4 + i / 2, 6 + i, 4 - i / 6, 2, 236, 140, 40);
                    }
                    Rect(px, 12, 2, 2, 4, 90, 160, 60);
                    Rect(px, 15, 3, 2, 4, 70, 140, 50);
                    Rect(px, 10, 1, 2, 4, 90, 160, 60);
                    itemCarrot = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                    itemCarrot.SetPixels32(px);
                    itemCarrot.filterMode = FilterMode.Point;
                    itemCarrot.wrapMode = TextureWrapMode.Clamp;
                    itemCarrot.Apply(false, false);
                }
                return itemCarrot;
            }
            return null;
        }

        private static void PaintSword(Color32[] px)
        {
            FillAll(px, 0, 0, 0, 0);
            // Blade: diagonal steel edge
            for (int i = 0; i < 13; i++)
            {
                int x = 17 - i;
                int y = 4 + i;
                Rect(px, x, y, 2, 2, 208, 212, 220);
                Rect(px, x + 1, y, 1, 2, 160, 165, 175);
            }
            Rect(px, 17, 3, 2, 2, 230, 234, 240);
            // Guard
            Rect(px, 3, 15, 6, 2, 120, 88, 40);
            Rect(px, 5, 14, 2, 4, 140, 104, 50);
            // Grip + pommel
            for (int i = 0; i < 4; i++)
            {
                Rect(px, 5 - i, 16 + i, 2, 2, 90, 62, 36);
            }
            Rect(px, 1, 20, 3, 3, 120, 88, 40);
        }

        private const int Size = 24;

        private static void PaintHand(Color32[] px)
        {
            FillAll(px, 0, 0, 0, 0);
            // Palm
            Rect(px, 8, 6, 8, 12, 232, 178, 131);
            Rect(px, 9, 7, 6, 10, 240, 194, 148);
            // Fingers
            Rect(px, 8, 3, 2, 5, 240, 194, 148);
            Rect(px, 11, 2, 2, 6, 240, 194, 148);
            Rect(px, 14, 3, 2, 5, 232, 186, 140);
            // Thumb
            Rect(px, 5, 9, 3, 2, 236, 190, 144);
            // Sleeve
            Rect(px, 8, 18, 8, 4, 42, 96, 178);
        }

        private static void PaintAxe(Color32[] px)
        {
            FillAll(px, 0, 0, 0, 0);
            // Handle: diagonal from bottom-right to top-left
            for (int i = 0; i < 14; i++)
            {
                int x = 16 - i;
                int y = 5 + i;
                Rect(px, x, y, 2, 2, 139, 105, 62);
                Rect(px, x + 1, y, 1, 2, 168, 130, 80);
            }
            // Blade head at top-left
            Rect(px, 3, 2, 8, 6, 160, 162, 166);
            Rect(px, 4, 3, 6, 4, 196, 198, 202);
            Rect(px, 3, 8, 3, 3, 140, 142, 146);
        }

        private static void PaintPickaxe(Color32[] px)
        {
            FillAll(px, 0, 0, 0, 0);
            for (int i = 0; i < 14; i++)
            {
                int x = 16 - i;
                int y = 5 + i;
                Rect(px, x, y, 2, 2, 139, 105, 62);
                Rect(px, x + 1, y, 1, 2, 168, 130, 80);
            }
            // Curved head: two prongs
            Rect(px, 2, 3, 9, 3, 160, 162, 166);
            Rect(px, 2, 6, 4, 3, 176, 178, 182);
            Rect(px, 9, 6, 4, 2, 176, 178, 182);
            Rect(px, 3, 4, 7, 1, 200, 202, 206);
        }

        private static void PaintClock(Color32[] px)
        {
            FillAll(px, 0, 0, 0, 0);
            float c = (Size - 1) * 0.5f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    if (d > 10.5f)
                    {
                        continue;
                    }
                    if (d > 9f)
                    {
                        px[y * Size + x] = new Color32(150, 110, 50, 255); // gold rim
                    }
                    else
                    {
                        px[y * Size + x] = new Color32(232, 240, 248, 255); // face
                    }
                }
            }
            // Ticks
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6f;
                int x = Mathf.RoundToInt(c + Mathf.Cos(a) * 7.2f);
                int y = Mathf.RoundToInt(c + Mathf.Sin(a) * 7.2f);
                px[y * Size + x] = new Color32(70, 70, 80, 255);
            }
            // Hands (10:10)
            Line(px, (int)c, (int)c, (int)c + 4, (int)c - 3, 40, 40, 50);
            Line(px, (int)c, (int)c, (int)c - 2, (int)c - 6, 40, 40, 50);
            px[(int)c * Size + (int)c] = new Color32(160, 60, 50, 255);
        }

        private static void Line(Color32[] px, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) * 2 + 1;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
                int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
                if (x >= 0 && y >= 0 && x < Size && y < Size)
                {
                    px[y * Size + x] = new Color32(r, g, b, 255);
                }
            }
        }

        private static void Rect(Color32[] px, int x0, int y0, int w, int h, int r, int g, int b)
        {
            for (int y = y0; y < y0 + h && y < Size; y++)
            {
                for (int x = x0; x < x0 + w && x < Size; x++)
                {
                    if (x < 0 || y < 0)
                    {
                        continue;
                    }
                    px[y * Size + x] = new Color32((byte)r, (byte)g, (byte)b, 255);
                }
            }
        }

        private static void FillAll(Color32[] px, int r, int g, int b, int a)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }
    }
}
