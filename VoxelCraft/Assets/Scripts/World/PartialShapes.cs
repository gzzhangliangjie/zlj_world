using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Items;

namespace VoxelCraft.World
{
    /// <summary>
    /// M31 partial-block geometry: custom quads for torch / door / chest / bed /
    /// fence emitted instead of the full cube. Pure static like the rest of the
    /// mesher so batch tests can call it directly.
    /// </summary>
    public static class PartialShapes
    {
        // Emit geometry for a partial block at local (x,y,z). Returns true when the
        // block was fully handled (caller skips the standard cube path).
        public static bool Emit(
            BlockType block, int x, int y, int z, int wx, int wz,
            WorldSim sim, Rect[] tileRects, MeshData target)
        {
            switch (block)
            {
                case BlockType.Torch: Torch(x, y, z, tileRects, target); return true;
                case BlockType.Furnace: Furnace(x, y, z, wx, wz, sim, tileRects, target); return true;
                case BlockType.DoorClosed:
                case BlockType.DoorOpen: Door(block, x, y, z, wx, wz, sim, tileRects, target); return true;
                case BlockType.Chest: Chest(x, y, z, wx, wz, sim, tileRects, target); return true;
                case BlockType.BedFoot:
                case BlockType.BedHead: Bed(block, x, y, z, wx, wz, sim, tileRects, target); return true;
                case BlockType.Fence: Fence(x, y, z, wx, wz, sim, tileRects, target); return true;
                default: return false;
            }
        }

        // ---- low-level quad helper: axis-aligned quad with a tile and flat shade ----
        private static void Quad(
            MeshData t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Rect rect, float shade, bool glow = false)
        {
            int vb = t.Vertices.Count;
            t.Vertices.Add(a); t.Vertices.Add(b); t.Vertices.Add(c); t.Vertices.Add(d);
            var uv0 = new Vector2(rect.x, rect.y);
            var uv1 = new Vector2(rect.x + rect.width, rect.y);
            var uv2 = new Vector2(rect.x + rect.width, rect.y + rect.height);
            var uv3 = new Vector2(rect.x, rect.y + rect.height);
            t.Uvs.Add(uv0); t.Uvs.Add(uv1); t.Uvs.Add(uv2); t.Uvs.Add(uv3);
            byte l = (byte)(Mathf.Clamp01(shade) * 255f);
            // glow bit: alpha 250 = fullbright (shader skips day dimming);
            // 250 vs 255 differs by 5/255 so fixed-precision interpolation
            // cannot confuse them (254 failed through precision loss)
            byte alpha = glow ? (byte)250 : (byte)255;
            var col = new Color32(l, l, l, alpha);
            t.Colors.Add(col); t.Colors.Add(col); t.Colors.Add(col); t.Colors.Add(col);
            t.Indices.Add(vb); t.Indices.Add(vb + 1); t.Indices.Add(vb + 2);
            t.Indices.Add(vb); t.Indices.Add(vb + 2); t.Indices.Add(vb + 3);
        }

        private static void Box(
            MeshData t, float x0, float y0, float z0, float x1, float y1, float z1,
            Rect[] tileRects, TileId tile, bool glow, float shade = 1f)
        {
            var r = tileRects[(int)tile];
            // +X / -X
            Quad(t, new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), r, 0.80f * shade, glow);
            Quad(t, new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), r, 0.80f * shade, glow);
            // +Y / -Y
            Quad(t, new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), r, 1f * shade, glow);
            Quad(t, new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), r, 0.55f * shade, glow);
            // +Z / -Z
            Quad(t, new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), r, 0.70f * shade, glow);
            Quad(t, new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), r, 0.70f * shade, glow);
        }

        // ---- torch: 2/16 stick + ember head, fullbright ----
        private static void Furnace(int x, int y, int z, int wx, int wz, WorldSim sim, Rect[] tileRects, MeshData target)
        {
            // Official furnace: full stone cube; the -Z face carries the
            // vanilla front tile (mouth baked into the texture, lit version
            // glows). Faces the cottage doorway (-Z).
            var lit = Items.Furniture.IsFurnaceLit(new Vector3Int(wx, y, wz));
            var front = lit ? tileRects[(int)TileId.FurnaceLit] : tileRects[(int)TileId.FurnaceFront];
            var side = tileRects[(int)TileId.FurnaceSide];
            var topT = tileRects[(int)TileId.FurnaceTop];
            // +Y top / -Y bottom
            Quad(target, new Vector3(x, y + 1, z), new Vector3(x + 1, y + 1, z), new Vector3(x + 1, y + 1, z + 1), new Vector3(x, y + 1, z + 1), topT, 1f, false);
            Quad(target, new Vector3(x, y, z + 1), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z), new Vector3(x, y, z), side, 0.55f, false);
            // +X / -X sides
            Quad(target, new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z), new Vector3(x + 1, y + 1, z), new Vector3(x + 1, y + 1, z + 1), side, 0.80f, false);
            Quad(target, new Vector3(x, y, z), new Vector3(x, y, z + 1), new Vector3(x, y + 1, z + 1), new Vector3(x, y + 1, z), side, 0.80f, false);
            // +Z back
            Quad(target, new Vector3(x, y, z + 1), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y + 1, z + 1), new Vector3(x, y + 1, z + 1), side, 0.70f, false);
            // -Z FRONT toward doorway (winding as proven for torch)
            Quad(target,
                new Vector3(x + 1, y, z), new Vector3(x, y, z),
                new Vector3(x, y + 1, z), new Vector3(x + 1, y + 1, z),
                front, 1f, lit);
        }

        private static void Torch(int x, int y, int z, Rect[] tileRects, MeshData target)
        {
            float cx = x + 0.5f;
            float cz = z + 0.5f;
            var r = tileRects[(int)TileId.Torch];
            // The torch tile paints a 2px stick + ember on a TRANSPARENT tile; the
            // full-tile UVs mapped transparent texels onto the whole box and the
            // cutout shader clipped the torch away entirely. Sub-rect UVs map every
            // face to the opaque middle strip (x 6..10 of 16) instead.
            Rect strip = new Rect(
                r.x + r.width * (6f / 16f), r.y + r.height * (6f / 16f), r.width * (4f / 16f), r.height * (10f / 16f));
            Quad(t: target,
                a: new Vector3(x + 7 / 16f, y, z + 7 / 16f), b: new Vector3(x + 7 / 16f, y, z + 9 / 16f),
                c: new Vector3(x + 7 / 16f, y + 10 / 16f, z + 9 / 16f), d: new Vector3(x + 7 / 16f, y + 10 / 16f, z + 7 / 16f),
                rect: strip, shade: 0.8f);
            Quad(t: target,
                a: new Vector3(x + 9 / 16f, y, z + 9 / 16f), b: new Vector3(x + 9 / 16f, y, z + 7 / 16f),
                c: new Vector3(x + 9 / 16f, y + 10 / 16f, z + 7 / 16f), d: new Vector3(x + 9 / 16f, y + 10 / 16f, z + 9 / 16f),
                rect: strip, shade: 0.8f);
            Quad(t: target,
                a: new Vector3(x + 7 / 16f, y, z + 9 / 16f), b: new Vector3(x + 9 / 16f, y, z + 9 / 16f),
                c: new Vector3(x + 9 / 16f, y + 10 / 16f, z + 9 / 16f), d: new Vector3(x + 7 / 16f, y + 10 / 16f, z + 9 / 16f),
                rect: strip, shade: 0.7f);
            Quad(t: target,
                a: new Vector3(x + 9 / 16f, y, z + 7 / 16f), b: new Vector3(x +7 / 16f, y, z + 7 / 16f),
                c: new Vector3(x + 7 / 16f, y + 10 / 16f, z + 7 / 16f), d: new Vector3(x + 9 / 16f, y + 10 / 16f, z + 7 / 16f),
                rect: strip, shade: 0.7f);
            Quad(t: target,
                a: new Vector3(x + 7 / 16f, y + 10 / 16f, z + 7 / 16f), b: new Vector3(x + 9 / 16f, y + 10 / 16f, z + 7 / 16f),
                c: new Vector3(x + 9 / 16f, y + 10 / 16f, z + 9 / 16f), d: new Vector3(x + 7 / 16f, y + 10 / 16f, z + 9 / 16f),
                rect: strip, shade: 1f);
            // ember head: small glowing cube sampling the opaque ember rows
            // (tile rows y 3..7 of 16 hold the ember pixels) so cutout keeps them.
            Rect ember = new Rect(
                r.x + r.width * (6f / 16f), r.y + r.height * (3f / 16f), r.width * (4f / 16f), r.height * (4f / 16f));
            float e0 = 6 / 16f, e1 = 10 / 16f, ey0 = 10 / 16f, ey1 = 13 / 16f;
            Quad(target,
                new Vector3(x + e1, y + ey0, z + e0), new Vector3(x + e1, y + ey0, z + e1),
                new Vector3(x + e1, y + ey1, z + e1), new Vector3(x + e1, y + ey1, z + e0), ember, 0.8f, true);
            Quad(target,
                new Vector3(x + e0, y + ey0, z + e1), new Vector3(x + e0, y + ey0, z + e0),
                new Vector3(x + e0, y + ey1, z + e0), new Vector3(x + e0, y + ey1, z + e1), ember, 0.8f, true);
            Quad(target,
                new Vector3(x + e0, y + ey1, z + e1), new Vector3(x + e1, y + ey1, z + e1),
                new Vector3(x + e1, y + ey1, z + e0), new Vector3(x + e0, y + ey1, z + e0), ember, 1f, true);
            Quad(target,
                new Vector3(x + e0, y + ey0, z + e0), new Vector3(x + e1, y + ey0, z + e0),
                new Vector3(x + e1, y + ey0, z + e1), new Vector3(x + e0, y + ey0, z + e1), ember, 0.6f, true);
            Quad(target,
                new Vector3(x + e0, y + ey0, z + e1), new Vector3(x + e1, y + ey0, z + e1),
                new Vector3(x + e1, y + ey1, z + e1), new Vector3(x + e0, y + ey1, z + e1), ember, 0.7f, true);
            Quad(target,
                new Vector3(x + e1, y + ey0, z + e0), new Vector3(x + e0, y + ey0, z + e0),
                new Vector3(x + e0, y + ey1, z + e0), new Vector3(x + e1, y + ey1, z + e0), ember, 0.7f, true);
        }

        // ---- door: thin panel in the wall plane; rotates 90° when open ----
        private static void Door(BlockType block, int x, int y, int z, int wx, int wz, WorldSim sim, Rect[] tileRects, MeshData target)
        {
            bool open = block == BlockType.DoorOpen;
            // Lower block carries the panel; the upper block mirrors the panel
            // shifted up 1 (we simply emit the half for this y).
            bool lower = !IsDoorUpper(x, y, z, wx, wz, sim);

            byte facing = DoorFacing(x, y, z, wx, wz, sim);
            TileId tile = y % 2 == 1 ? TileId.DoorTileTop : TileId.DoorTile;
            // door texture spans both blocks: emit per-y half via V offset is overkill;
            // use DoorTile for lower and DoorTileTop for upper.
            float t = 0f; // panel thickness 3/16
            const float th = 3 / 16f;
            float y0 = y, y1 = y + 1f;
            if (facing == 0)
            {
                // wall runs along X => panel in Z plane at the block's -Z face, spans X
                float px0 = open ? x : x;
                if (open)
                {
                    // swing: panel along Z at the hinge (west) edge
                    float z0 = z, z1 = z + 1f;
                    float hx = x; // hinge at x
                    Box(target, hx, y0, z0, hx + th, y1, z1, tileRects, tile, false);
                }
                else
                {
                    Box(target, x, y0, z, x + 1f, y1, z + th, tileRects, tile, false);
                }
            }
            else
            {
                if (open)
                {
                    float hx = x;
                    Box(target, hx, y0, z, hx + 1f, y1, z + th, tileRects, tile, false);
                }
                else
                {
                    Box(target, x, y0, z, x + th, y1, z + 1f, tileRects, tile, false);
                }
            }
        }

        private static bool IsDoorUpper(int x, int y, int z, int wx, int wz, WorldSim sim)
        {
            return sim.GetBlock(wx, y - 1, wz) == BlockType.DoorClosed ||
                   sim.GetBlock(wx, y - 1, wz) == BlockType.DoorOpen;
        }

        private static byte DoorFacing(int x, int y, int z, int wx, int wz, WorldSim sim)
        {
            // facing from registry if registered, else infer from neighbours:
            // solid neighbour left/right => panel spans Z (facing 0); front/back => spans X.
            Vector3Int lower = new Vector3Int(wx, y, wz);
            if (y > 0 && (sim.GetBlock(wx, y - 1, wz) == BlockType.DoorClosed || sim.GetBlock(wx, y - 1, wz) == BlockType.DoorOpen))
            {
                lower = new Vector3Int(wx, y - 1, wz);
            }
            if (Furniture.TryGetDoorFacing(lower, out byte f)) { return f; }
            bool solidX = BlockDatabase.IsSolid(sim.GetBlock(wx - 1, lower.y, wz)) ||
                          BlockDatabase.IsSolid(sim.GetBlock(wx + 1, lower.y, wz));
            return solidX ? (byte)0 : (byte)1;
        }

        // ---- chest: 14/16 box with lid lip ----
        private static void Chest(int x, int y, int z, int wx, int wz, WorldSim sim, Rect[] tileRects, MeshData target)
        {
            float m = 1 / 16f;
            // base body 14/16 wide, 10/16 tall; lid 12/16 wide, 6/16 tall on top
            Box(target, x + m, y, z + m, x + 15 / 16f, y + 10 / 16f, z + 15 / 16f, tileRects, TileId.ChestSide, false);
            Box(target, x + m, y + 10 / 16f, z + m, x + 15 / 16f, y + 14 / 16f, z + 15 / 16f, tileRects, TileId.ChestTop, false);
        }

        // ---- bed: 16/16 x 9/16 tall slab; head half has the pillow top tile ----
        private static void Bed(BlockType block, int x, int y, int z, int wx, int wz, WorldSim sim, Rect[] tileRects, MeshData target)
        {
            // Official bed (bedrock-samples): 1x1 footprint, 9/16 tall blanket
            // layer over a 3/16 leg frame; head/foot halves get their own
            // per-face vanilla tiles. Cottage beds run along +X (foot, head).
            bool head = block == BlockType.BedHead;
            float h = 9 / 16f;
            TileId top = head ? TileId.BedHeadTop : TileId.BedFootTop;
            TileId side = head ? TileId.BedHeadSide : TileId.BedFeetSide;
            TileId endF = head ? TileId.BedHeadEnd : TileId.BedFeetEnd;
            var rt = tileRects[(int)top];
            var rs = tileRects[(int)side];
            var re = tileRects[(int)endF];
            var rb = tileRects[(int)TileId.Plank];
            // top (blanket) face
            Quad(target, new Vector3(x, y + h, z + 1), new Vector3(x + 1, y + h, z + 1), new Vector3(x + 1, y + h, z), new Vector3(x, y + h, z), rt, 1f, false);
            // bottom (frame underside)
            Quad(target, new Vector3(x, y, z), new Vector3(x + 1, y, z), new Vector3(x + 1, y, z + 1), new Vector3(x, y, z + 1), rb, 0.55f, false);
            // -X / +X end faces (footboard / headboard)
            Quad(target, new Vector3(x, y, z), new Vector3(x, y, z + 1), new Vector3(x, y + h, z + 1), new Vector3(x, y + h, z), re, 0.80f, false);
            Quad(target, new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z), new Vector3(x + 1, y + h, z), new Vector3(x + 1, y + h, z + 1), re, 0.80f, false);
            // -Z / +Z long sides
            Quad(target, new Vector3(x, y, z + 1), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y + h, z + 1), new Vector3(x, y + h, z + 1), rs, 0.70f, false);
            Quad(target, new Vector3(x + 1, y, z), new Vector3(x, y, z), new Vector3(x, y + h, z), new Vector3(x + 1, y + h, z), rs, 0.70f, false);
        }

        // ---- fence: 4/16 post + arms toward neighbouring fences ----
        private static void Fence(int x, int y, int z, int wx, int wz, WorldSim sim, Rect[] tileRects, MeshData target)
        {
            float p0 = 6 / 16f, p1 = 10 / 16f;
            // post
            Box(target, x + p0, y, z + p0, x + p1, y + 1f, z + p1, tileRects, TileId.LogSide, false);
            // arms: two rails (y+15/16..13/16 and y+6/16..8/16) toward each fence neighbour
            bool nX = IsFenceOrSolid(sim.GetBlock(wx + 1, y, wz));
            bool pX = IsFenceOrSolid(sim.GetBlock(wx - 1, y, wz));
            bool nZ = IsFenceOrSolid(sim.GetBlock(wx, y, wz + 1));
            bool pZ = IsFenceOrSolid(sim.GetBlock(wx, y, wz - 1));
            foreach (float ry in new[] { 12 / 16f, 6 / 16f })
            {
                float rh = 2 / 16f;
                if (nX) Box(target, x + p1, y + ry, z + p0, x + 1f, y + ry + rh, z + p1, tileRects, TileId.FenceLink, false);
                if (pX) Box(target, x, y + ry, z + p0, x + p0, y + ry + rh, z + p1, tileRects, TileId.FenceLink, false);
                if (nZ) Box(target, x + p0, y + ry, z + p1, x + p1, y + ry + rh, z + 1f, tileRects, TileId.FenceLink, false);
                if (pZ) Box(target, x + p0, y + ry, z, x + p1, y + ry + rh, z + p0, tileRects, TileId.FenceLink, false);
            }
        }

        private static bool IsFenceOrSolid(BlockType t)
        {
            return t == BlockType.Fence || BlockDatabase.IsOpaque(t);
        }
    }
}
