using UnityEngine;

namespace VoxelCraft.Gen
{
    /// <summary>
    /// Deterministic value utilities: seeded classic 2D Perlin noise, fractal Brownian
    /// motion, and an integer avalanche hash for stable per-coordinate decisions (trees).
    /// </summary>
    public class Noise
    {
        private readonly int[] perm = new int[512];

        public Noise(int seed)
        {
            var rng = new System.Random(seed);
            var p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                p[i] = i;
            }
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++)
            {
                perm[i] = p[i & 255];
            }
        }

        /// <summary>Classic 2D Perlin gradient noise, range about [-1, 1].</summary>
        public float Perlin(float x, float y)
        {
            float fx = Mathf.Floor(x);
            float fy = Mathf.Floor(y);
            int X = ((int)fx) & 255;
            int Y = ((int)fy) & 255;
            float xf = x - fx;
            float yf = y - fy;

            float u = Fade(xf);
            float v = Fade(yf);

            int aa = perm[perm[X] + Y];
            int ab = perm[perm[X] + Y + 1];
            int ba = perm[perm[X + 1] + Y];
            int bb = perm[perm[X + 1] + Y + 1];

            float x1 = Mathf.Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
            float x2 = Mathf.Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
            return Mathf.Lerp(x1, x2, v);
        }

        /// <summary>fBm over Perlin noise, normalised to [0, 1].</summary>
        public float Fbm(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Perlin(x, y) * amplitude;
                total += amplitude;
                x *= lacunarity;
                y *= lacunarity;
                amplitude *= gain;
            }
            return sum / total * 0.5f + 0.5f;
        }

        /// <summary>Deterministic hash for integer coordinates, result in [0, 1).</summary>
        public static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                int h = seed;
                h = h * 374761393 + x * 668265263;
                // -2048144777 is (int)2246822519: same wrap-around bits, stays int arithmetic.
                h = h * 374761393 + y * -2048144777;
                h ^= h >> 13;
                h *= 1274126177;
                h ^= h >> 16;
                return (h & 0x7fffffff) / 2147483647f;
            }
        }

        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private static float Grad(int hash, float x, float y)
        {
            switch (hash & 7)
            {
                case 0: return x + y;
                case 1: return -x + y;
                case 2: return x - y;
                case 3: return -x - y;
                case 4: return x;
                case 5: return -x;
                case 6: return y;
                default: return -y;
            }
        }
    }
}
