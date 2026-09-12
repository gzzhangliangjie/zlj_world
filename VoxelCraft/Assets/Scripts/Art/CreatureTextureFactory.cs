using System.Collections.Generic;
using UnityEngine;

namespace VoxelCraft.Art
{
    /// <summary>
    /// Paints procedural 16x16 pixel skins for blocky creatures (animals and the
    /// player model) and hands out unlit materials keyed by species + part.
    /// Matches the game's pixel-art voxel style with zero external assets.
    /// </summary>
    public static class CreatureTextureFactory
    {
        private static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();

        public static Material Get(string species, string part)
        {
            string key = species + ":" + part;
            if (cache.TryGetValue(key, out var mat) && mat != null)
            {
                return mat;
            }
            var tex = Paint(species, part);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            var shader = Resources.Load<Shader>("Shaders/UnlitTextureShader");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Texture"); // editor-only fallback
            }
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Diffuse");
            }
            mat = new Material(shader) { mainTexture = tex };
            cache[key] = mat;
            return mat;
        }

        private static Texture2D Paint(string species, string part)
        {
            var px = new Color32[256];
            int seed = species.GetHashCode() ^ (part.GetHashCode() * 7);
            var rng = new System.Random(seed);

            switch (species)
            {
                case "pig":
                    Fill(px, rng, 235, 163, 158, 10);
                    if (part == "face") PaintFace(px, 235, 163, 158, 20, 60, 40);
                    if (part == "leg") Fill(px, rng, 228, 150, 145, 8);
                    break;
                case "cow":
                    Fill(px, rng, 96, 64, 48, 12);
                    if (part == "body") Patches(px, rng, 235, 232, 225, 4);
                    if (part == "face") { PaintFace(px, 96, 64, 48, 0, 0, 0); Fill(px, rng, 235, 232, 225, 6, 6, 10, 3); }
                    if (part == "leg") Fill(px, rng, 70, 50, 40, 8);
                    break;
                case "sheep":
                    Fill(px, rng, 232, 230, 225, 14);
                    if (part == "face") { PaintFace(px, 220, 205, 195, 0, 0, 0); }
                    if (part == "leg") Fill(px, rng, 222, 216, 208, 10);
                    break;
                case "chicken":
                    Fill(px, rng, 242, 242, 238, 8);
                    if (part == "face") { PaintFace(px, 242, 242, 238, 15, 90, 60); Fill(px, rng, 226, 160, 40, 8, 11, 5, 9); }
                    if (part == "leg") Fill(px, rng, 226, 160, 40, 10);
                    break;
                default: // player
                    if (part == "body") Fill(px, rng, 42, 96, 178, 10);
                    else if (part == "legs") Fill(px, rng, 62, 58, 138, 10);
                    else if (part == "arm") Fill(px, rng, 42, 96, 178, 10);
                    else if (part == "face") { Fill(px, rng, 232, 178, 131, 8); PaintFace(px, 232, 178, 131, 0, 0, 0); Fill(px, rng, 62, 42, 28, 6, 6, 10, 0); }
                    else Fill(px, rng, 232, 178, 131, 8);
                    break;
            }
            return Build(px);
        }

        private static void PaintFace(Color32[] px, int r, int g, int b, int snoutR, int snoutG, int snoutB)
        {
            // two eyes
            Rect(px, 3, 5, 2, 2, 30, 30, 40);
            Rect(px, 11, 5, 2, 2, 30, 30, 40);
            if (snoutR > 0)
            {
                Rect(px, 5, 9, 6, 3, snoutR, snoutG, snoutB);
                Rect(px, 6, 10, 1, 1, 20, 20, 20);
                Rect(px, 9, 10, 1, 1, 20, 20, 20);
            }
        }

        private static void Fill(Color32[] px, System.Random rng, int r, int g, int b, int spread)
        {
            for (int i = 0; i < px.Length; i++)
            {
                int d = rng.Next(-spread, spread + 1);
                px[i] = new Color32(Cl(r + d), Cl(g + d), Cl(b + d), 255);
            }
        }

        private static void Fill(Color32[] px, System.Random rng, int r, int g, int b, int spread, int ox, int oy, int size)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    if (x < ox || y < oy || x >= ox + size || y >= oy + size)
                    {
                        continue;
                    }
                    int d = rng.Next(-spread, spread + 1);
                    px[y * 16 + x] = new Color32(Cl(r + d), Cl(g + d), Cl(b + d), 255);
                }
            }
        }

        private static void Patches(Color32[] px, System.Random rng, int r, int g, int b, int count)
        {
            for (int p = 0; p < count; p++)
            {
                int cx = rng.Next(2, 14);
                int cy = rng.Next(2, 14);
                int w = rng.Next(2, 5);
                int h = rng.Next(2, 5);
                for (int y = cy; y < cy + h && y < 16; y++)
                {
                    for (int x = cx; x < cx + w && x < 16; x++)
                    {
                        px[y * 16 + x] = new Color32((byte)r, (byte)g, (byte)b, 255);
                    }
                }
            }
        }

        private static void Rect(Color32[] px, int x0, int y0, int w, int h, int r, int g, int b)
        {
            for (int y = y0; y < y0 + h && y < 16; y++)
            {
                for (int x = x0; x < x0 + w && x < 16; x++)
                {
                    px[y * 16 + x] = new Color32((byte)r, (byte)g, (byte)b, 255);
                }
            }
        }

        private static Texture2D Build(Color32[] px)
        {
            var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            return tex;
        }

        private static byte Cl(int v)
        {
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
    }
}
