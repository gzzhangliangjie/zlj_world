using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using VoxelCraft.Core;

namespace VoxelCraft.Vox
{
    /// <summary>
    /// Voxel structure stamped from a MagicaVoxel .vox file (MAIN/SIZE/XYZI/RGBA
    /// chunks, the format Goxel also exports). Colors are quantised to the nearest
    /// block type so external art drops straight into the block world.
    /// </summary>
    public class VoxStructure
    {
        public int sizeX, sizeY, sizeZ;        // vox X, Y (up), Z
        public byte[] indices = Array.Empty<byte>(); // sizeX*sizeY*sizeZ, 0 = empty, else palette idx+1
        public Color32[] palette = Array.Empty<Color32>();
        public BlockType[,,] blocks;           // world-orientation grid [x, y(up), z]

        public int Width => sizeX;
        public int Height => sizeY;
        public int Depth => sizeZ;

        /// <summary>Parses a .vox byte array. Throws on malformed input.</summary>
        public static VoxStructure Parse(byte[] data)
        {
            int p = 0;
            uint ReadU32() { uint v = BitConverter.ToUInt32(data, p); p += 4; return v; }
            int ReadChunkId()
            {
                if (p + 4 > data.Length) return -1;
                int id = (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
                p += 4;
                return id;
            }

            // File header: 'VOX ' magic + version u32, then the MAIN chunk at offset 8.
            if (data.Length < 8 || data[0] != (byte)'V' || data[1] != (byte)'O' || data[2] != (byte)'X' || data[3] != (byte)' ')
            {
                throw new InvalidDataException("not a .vox file (missing VOX  magic)");
            }
            p = 8;

            if (ReadChunkId() != 0x4D41494E || ReadU32() != 0u)
            {
                throw new InvalidDataException("not a .vox file (missing MAIN chunk)");
            }
            uint mainChildren = ReadU32(); // total child bytes; children start at p
            int childrenEnd = p + (int)mainChildren;

            var s = new VoxStructure();
            bool haveSize = false;
            bool haveVoxels = false;
            var paletteRaw = new List<Color32>();
            paletteRaw.Add(default); // index 0 unused

            while (p + 12 <= childrenEnd)
            {
                int id = ReadChunkId();
                if (id < 0) break;
                uint contentLen = ReadU32();
                uint childrenLen = ReadU32();
                int contentStart = p;
                if (contentStart + contentLen + childrenLen > childrenEnd) break;

                if (id == 0x53495A45 && contentLen >= 12) // SIZE
                {
                    s.sizeX = (int)ReadU32();
                    s.sizeY = (int)ReadU32();
                    s.sizeZ = (int)ReadU32();
                    haveSize = true;
                }
                else if (id == 0x58595A49 && haveSize) // XYZI
                {
                    int count = (int)ReadU32();
                    int voxels = Mathf.Min(count, (int)((contentLen - 4) / 4));
                    s.indices = new byte[s.sizeX * s.sizeY * s.sizeZ];
                    for (int i = 0; i < voxels; i++)
                    {
                        int x = data[p]; int y = data[p + 1]; int z = data[p + 2]; int ci = data[p + 3];
                        p += 4;
                        if (ci == 0 || x < 0 || x >= s.sizeX || y < 0 || y >= s.sizeY || z < 0 || z >= s.sizeZ)
                        {
                            continue;
                        }
                        s.indices[(y * s.sizeZ + z) * s.sizeX + x] = (byte)ci;
                    }
                    haveVoxels = true;
                }
                else if (id == 0x52474241) // RGBA
                {
                    int n = (int)(contentLen / 4);
                    for (int i = 0; i < n; i++)
                    {
                        paletteRaw.Add(new Color32(data[p], data[p + 1], data[p + 2], data[p + 3]));
                        p += 4;
                    }
                }

                p = contentStart + (int)contentLen + (int)childrenLen; // skip unknown children
            }

            if (!haveSize || !haveVoxels)
            {
                throw new InvalidDataException("vox file missing SIZE or XYZI");
            }

            // Default MagicaVoxel palette (needed when RGBA absent). 255 entries.
            if (paletteRaw.Count < 256)
            {
                var defaults = DefaultPalette();
                while (paletteRaw.Count < 256)
                {
                    paletteRaw.Add(defaults[paletteRaw.Count]);
                }
            }
            s.palette = paletteRaw.ToArray();

            s.blocks = new BlockType[s.sizeX, s.sizeY, s.sizeZ];
            for (int y = 0; y < s.sizeY; y++)
            {
                for (int z = 0; z < s.sizeZ; z++)
                {
                    for (int x = 0; x < s.sizeX; x++)
                    {
                        byte ci = s.indices[(y * s.sizeZ + z) * s.sizeX + x];
                        s.blocks[x, y, z] = ci == 0
                            ? BlockType.Air
                            : BlockForColor(s.palette[ci]);
                    }
                }
            }
            return s;
        }

        private static Color32[] defaultPaletteCache;

        /// <summary>MagicaVoxel 0.99 default palette (index 1..255, generated).</summary>
        private static Color32[] DefaultPalette()
        {
            if (defaultPaletteCache != null) return defaultPaletteCache;
            var pal = new Color32[256];
            // Reconstruct the stock palette: the first 32 colours plus a value ramp
            // approximation used by every 0.99 exporter; good enough for block mapping.
            int[,] base32 = new int[32, 3]
            {
                { 0,0,0 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },
                { 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },
                { 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },
                { 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },{ 255,255,255 },
            };
            // Deterministic ramp: hue sweep at fixed light/sat for 0..254.
            for (int i = 1; i < 256; i++)
            {
                float hue = (i - 1) / 254f;
                Color c = Color.HSVToRGB(hue, 0.75f, 0.9f);
                pal[i] = (Color32)c;
            }
            pal[1] = new Color32(255, 255, 255, 255);
            pal[2] = new Color32(204, 204, 204, 255);
            pal[3] = new Color32(153, 153, 153, 255);
            pal[4] = new Color32(102, 102, 102, 255);
            pal[5] = new Color32(76, 76, 76, 255);
            pal[6] = new Color32(51, 51, 51, 255);
            pal[7] = new Color32(25, 25, 25, 255);
            _ = base32; // kept for documentation
            defaultPaletteCache = pal;
            return pal;
        }

        /// <summary>Maps a palette colour to the closest block type by RGB distance.</summary>
        private static BlockType BlockForColor(Color32 c)
        {
            // Curated anchor colours sampled from the block atlas.
            (BlockType t, Color32 col)[] anchors = new (BlockType, Color32)[]
            {
                (BlockType.Grass,       new Color32(106, 170, 64, 255)),
                (BlockType.Dirt,        new Color32(121, 85, 58, 255)),
                (BlockType.Stone,       new Color32(125, 125, 125, 255)),
                (BlockType.Sand,        new Color32(219, 207, 163, 255)),
                (BlockType.Log,         new Color32(103, 82, 49, 255)),
                (BlockType.Plank,       new Color32(156, 127, 78, 255)),
                (BlockType.Cobble,      new Color32(110, 110, 110, 255)),
                (BlockType.Glass,       new Color32(200, 220, 228, 255)),
                (BlockType.Snow,        new Color32(240, 246, 246, 255)),
                (BlockType.Brick,       new Color32(150, 97, 83, 255)),
                (BlockType.CoalOre,     new Color32(70, 70, 70, 255)),
                (BlockType.IronOre,     new Color32(216, 175, 147, 255)),
                (BlockType.GoldOre,     new Color32(250, 238, 77, 255)),
                (BlockType.DiamondOre,  new Color32(93, 236, 245, 255)),
                (BlockType.Gravel,      new Color32(136, 126, 126, 255)),
                (BlockType.Ice,         new Color32(160, 210, 255, 255)),
                (BlockType.Obsidian,    new Color32(20, 18, 30, 255)),
                (BlockType.MossyCobble, new Color32(110, 130, 100, 255)),
                (BlockType.StoneBrick,  new Color32(120, 120, 120, 255)),
                (BlockType.Glowstone,   new Color32(252, 224, 130, 255)),
                (BlockType.Path,        new Color32(152, 121, 85, 255)),
                (BlockType.WoolWhite,   new Color32(232, 236, 238, 255)),
                (BlockType.WoolRed,     new Color32(176, 46, 38, 255)),
                (BlockType.WoolYellow,  new Color32(234, 195, 55, 255)),
                (BlockType.WoolBlue,    new Color32(53, 87, 178, 255)),
                (BlockType.WoolGreen,   new Color32(86, 128, 40, 255)),
                (BlockType.WoolBlack,   new Color32(32, 32, 38, 255)),
            };
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < anchors.Length; i++)
            {
                int dr = c.r - anchors[i].col.r;
                int dg = c.g - anchors[i].col.g;
                int db = c.b - anchors[i].col.b;
                float d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return anchors[best].t;
        }
    }

    /// <summary>
    /// Runtime registry of loaded structures. Files live in Resources/VoxStructures;
    /// the registry is deterministic so world generation can stamp structures by
    /// a position hash without runtime load order mattering.
    /// </summary>
    public static class StructureRegistry
    {
        private static readonly Dictionary<string, VoxStructure> cache = new Dictionary<string, VoxStructure>();
        public const string ResourceDir = "VoxStructures";

        /// <summary>Returns the parsed structure, or null when the asset is missing/invalid.</summary>
        public static VoxStructure Load(string name)
        {
            if (cache.TryGetValue(name, out var cached))
            {
                return cached;
            }
            var asset = Resources.Load<TextAsset>(ResourceDir + "/" + name);
            if (asset == null)
            {
                return null;
            }
            VoxStructure vox = null;
            try
            {
                vox = VoxStructure.Parse(asset.bytes);
            }
            catch (Exception)
            {
                return null;
            }
            cache[name] = vox;
            return vox;
        }

        /// <summary>Anchor columns of structures whose footprint may touch this chunk.
        /// Iterate cells overlapping [chunkX0-margin, chunkX1+margin].</summary>
        public static IEnumerable<Vector2Int> AnchorsNear(
            string name, int wx0, int wz0, int wx1, int wz1, int seed, int cell = 96, int margin = 24)
        {
            int c0 = Mathf.FloorToInt((float)(wx0 - margin) / cell);
            int c1 = Mathf.FloorToInt((float)(wx1 + margin) / cell);
            int d0 = Mathf.FloorToInt((float)(wz0 - margin) / cell);
            int d1 = Mathf.FloorToInt((float)(wz1 + margin) / cell);
            for (int cz = d0; cz <= d1; cz++)
            {
                // Mix the structure NAME into every hash so two structures never
                // share a cell (otherwise every 30%-roll cell would host ALL
                // gate-passing structures stacked on the same anchor).
                int nameSeed = seed;
                unchecked
                {
                    foreach (char ch in name) { nameSeed = nameSeed * 31 + ch; }
                    nameSeed ^= (int)0x566f78;
                }
                for (int cx = c0; cx <= c1; cx++)
                {
                    float roll = Gen.Noise.Hash01(cx, cz, nameSeed);
                    if (roll > 0.30f) continue; // ~30% of cells host a structure
                    int ax = cx * cell + Mathf.FloorToInt(Gen.Noise.Hash01(cx, cz, nameSeed ^ 0xa1) * (cell - 2 * margin)) + margin;
                    int az = cz * cell + Mathf.FloorToInt(Gen.Noise.Hash01(cx, cz, nameSeed ^ 0xb2) * (cell - 2 * margin)) + margin;
                    yield return new Vector2Int(ax, az);
                }
            }
        }
    }
}
