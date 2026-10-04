#!/usr/bin/env python
# vox_to_creature.py — mmmm mob_*.vox / veh_*.vox -> bedrock creature assets.
#
# Pipeline per model:
#   .vox (Z-up) -> greedy box decomposition (Y-up frame)
#   -> single "body" bone geo.json (cubes = boxes, per-face UV)
#   -> skin png painted from the vox palette (per-face solid rects)
#   -> creatures.json entry (archetype/locomotion/walkClip default)
#
# Animations: no per-bone anims authored (single bone). The engine's
# procedural fallback gait drives BlockyAnimal legs; walkClip stays the
# generic quadruped clip which expects leg bones - with no leg bones it
# simply animates nothing (static body), which is acceptable for props-on-
# legs style. Vehicles get locomotion walk + walkSpeed low (parked look).
#
# Reuses the parser + greedy_boxes from the audited prototype.
import struct, os, sys, json
from PIL import Image

def load(path):
    d = open(path, 'rb').read()
    p = 8
    assert d[p:p+4] == b'MAIN'; p += 4
    mchlen = struct.unpack('<I', d[p+4:p+8])[0]
    p += 8; end = p + mchlen
    size = None; vox = {}; pal = [None]
    while p + 12 <= end:
        cid = d[p:p+4]; clen = struct.unpack('<I', d[p+4:p+8])[0]; chlen = struct.unpack('<I', d[p+8:p+12])[0]
        body = p + 12
        if cid == b'SIZE': size = struct.unpack('<3I', d[body:body+12])
        elif cid == b'XYZI':
            n = struct.unpack('<I', d[body:body+4])[0]
            for i in range(n):
                x, y, z, c = d[body+4+i*4:body+8+i*4]
                vox[(x, y, z)] = c
        elif cid == b'RGBA':
            for i in range(clen // 4): pal.append(tuple(d[body+i*4:body+4+i*4]))
        p = body + clen + chlen
    return size, vox, pal

def greedy_boxes(vox):
    # Fast path: identical semantics to the set-based version below, but the
    # "is this box full?" test is a numpy boolean-slice AND-reduce instead of a
    # per-voxel set probe. Large models (tank1 ~50k vox) went from hours to
    # seconds. Falls back to the original loop when numpy is unavailable.
    try:
        import numpy as np
    except ImportError:
        np = None
    remaining = set(vox)
    boxes = []
    if np is not None:
        if not remaining:
            return boxes
        xs = [p[0] for p in remaining]; ys = [p[1] for p in remaining]; zs = [p[2] for p in remaining]
        X0, X1, Y0, Y1, Z0, Z1 = min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)
        grid = np.zeros((X1 - X0 + 1, Y1 - Y0 + 1, Z1 - Z0 + 1), dtype=bool)
        for (x, y, z) in remaining:
            grid[x - X0, y - Y0, z - Z0] = True
        order = sorted(remaining, key=lambda p: (p[1], p[2], p[0]))  # same seed order
        ptr = 0
        while True:
            while ptr < len(order) and not grid[order[ptr][0] - X0, order[ptr][1] - Y0, order[ptr][2] - Z0]:
                ptr += 1
            if ptr >= len(order):
                break
            s = order[ptr]
            bx0, by0, bz0 = s[0] - X0, s[1] - Y0, s[2] - Z0
            bx1, by1, bz1 = bx0, by0, bz0
            improved = True
            while improved:
                improved = False
                for dx, dy, dz in ((1, 0, 0), (0, 1, 0), (0, 0, 1), (0, 0, -1)):
                    nx1 = bx1 + 1 if dx else bx1
                    ny1 = by1 + 1 if dy else by1
                    nz0 = bz0 - 1 if dz == -1 else bz0   # single-step growth
                    nz1 = bz1 + 1 if dz == 1 else bz1
                    if bx0 < 0 or by0 < 0 or nz0 < 0:
                        continue
                    if nx1 >= grid.shape[0] or ny1 >= grid.shape[1] or nz1 >= grid.shape[2]:
                        continue
                    if grid[bx0:nx1 + 1, by0:ny1 + 1, nz0:nz1 + 1].all():
                        bx1, by1, bz1 = nx1, ny1, nz1
                        if dz == -1:
                            bz0 = nz0
                        improved = True
            boxes.append((bx0 + X0, by0 + Y0, bz0 + Z0, bx1 + X0, by1 + Y0, bz1 + Z0))
            grid[bx0:bx1 + 1, by0:by1 + 1, bz0:bz1 + 1] = False
            for x in range(bx0, bx1 + 1):
                for y in range(by0, by1 + 1):
                    for z in range(bz0, bz1 + 1):
                        remaining.discard((x + X0, y + Y0, z + Z0))
        return boxes
    def full(x0,y0,z0,x1,y1,z1):
        for x in range(x0,x1+1):
            for y in range(y0,y1+1):
                for z in range(z0,z1+1):
                    if (x,y,z) not in remaining: return False
        return True
    while remaining:
        s = min(remaining, key=lambda p:(p[1], p[2], p[0]))
        x0,y0,z0 = s; x1,y1,z1 = s
        improved = True
        while improved:
            improved = False
            for dx,dy,dz in ((1,0,0),(0,1,0),(0,0,1),(0,0,-1)):
                nx1 = x1+1 if dx else x1
                ny1 = y1+1 if dy else y1
                nz0 = z0-1 if dz==-1 else z0
                nz1 = z1+1 if dz==1 else z1
                if nz0 < 0: continue
                if full(x0,y0,nz0,nx1,ny1,nz1):
                    x1,y1,z1 = nx1,ny1,nz1
                    if dz==-1: z0 = nz0
                    improved = True
        boxes.append((x0,y0,z0,x1,y1,z1))
        for x in range(x0,x1+1):
            for y in range(y0,y1+1):
                for z in range(z0,z1+1): remaining.discard((x,y,z))
    return boxes

def W_mid(v2):
    xs = {p[0] for p in v2}
    return (min(xs) + max(xs)) / 2

def D_mid(v2):
    zs = {p[2] for p in v2}
    return (min(zs) + max(zs)) / 2

def convert(name, src, geo_dir, tex_dir, scale=1.0, wheels=None, wheel_radius=2.5):
    """wheels: optional [(x,z0,z1), ...] wheel column specs (voxel coords);
    those columns become wheelFL/FR/RL/RR bones (rotatable around X for
    rolling) instead of static body cubes."""
    size, vox, pal = load(src)
    # mmmm is Z-up: (x, y, z)_vox -> creature frame (x, z, y): new y = old z
    v2 = {(x, z, y): pal[c][:3] for (x,y,z),c in vox.items()}
    wheel_cells = set()
    wheel_specs = []   # (name, cells) in creature coords
    if wheels:
        for wi, (wx, z0, z1) in enumerate(wheels):
            side = 'L' if wx < W_mid(v2) else 'R'
            fore = 'F' if (z0 + z1) / 2 < D_mid(v2) else 'R'
            cells = {(x, y, z) for (x, y, z) in v2
                     if x == wx and z0 <= z <= z1}
            # tire only: contiguous gray run from the bottom (body-coloured
            # voxels above the tire stay with the body)
            keep = set()
            maxY = max(c[1] for c in cells) if cells else 0
            for z in {c[2] for c in cells}:
                for x in {c[0] for c in cells}:
                    # ALL contiguous gray runs in this pillar (creature y =
                    # height). A car has TWO kinds of gray band: the tire
                    # (starts at the ground, y<=1) and the window/trim band
                    # higher up (y>=6). Picking the globally-longest run let
                    # the window band win on some pillars and the wheel bbox
                    # blew up to nearly full car height (M34 round-3: police1
                    # disc Ø11 vox on a 13-vox car). Rule: tire = longest
                    # GROUND-STARTING run; fall back to longest overall.
                    runs = []
                    run = []
                    for y in range(0, maxY + 1):
                        c0 = v2.get((x, y, z))
                        if c0 is None: continue
                        gray = abs(c0[0] - c0[1]) < 24 and abs(c0[1] - c0[2]) < 24
                        if gray: run.append((x, y, z))
                        else:
                            if run: runs.append(run)
                            run = []
                    if run: runs.append(run)
                    grounded = [r for r in runs if r[0][1] <= 1]
                    best = max(grounded or runs, key=len) if runs else []
                    keep.update(best)
            wheel_cells |= keep
            wheel_specs.append((f'wheel{fore}{side}', keep))
    body_v2 = {k: v for k, v in v2.items() if k not in wheel_cells}
    boxes = greedy_boxes(body_v2)
    W = max(p[0] for p in v2)+1; H = max(p[1] for p in v2)+1; D = max(p[2] for p in v2)+1

    # --- skin layout: shelf-pack box nets, 1 px per voxel-face ---
    regions = []  # (x,y,w,h) allocated
    shelves = []  # (y, h, x_cursor) shelf packer state
    def alloc(w, h):
        # Shelf packer: first shelf tall enough with remaining width, else a
        # new shelf on top. Same first-fit spirit as the old row scan but
        # O(shelves) instead of O(tex_height * regions) — tank1 (261 boxes)
        # went from minutes/hours to milliseconds.
        Wt = 128
        if w > Wt:
            w = Wt  # clamp pathological nets; UVs stay self-consistent
            raise RuntimeError(f'net wider than canvas: w={w} — rotate/limit the box')
        for si, (sy, sh, sx) in enumerate(shelves):
            if sh >= h and sx + w <= Wt:
                shelves[si] = (sy, sh, sx + w)
                regions.append((sx, sy, w, h))
                return sx, sy
        top = max((s[0] + s[1] for s in shelves), default=0)
        shelves.append((top, h, w))
        regions.append((0, top, w, h))
        if top + h > texH[0]: texH[0] = top + h
        return 0, top
    texH = [128]   # grown on demand inside alloc (closure)
    # tall scratch canvas: the shelf packer may exceed 1024 on huge models
    # (tank1 261 boxes); cropped to pow2 at save time
    img = Image.new('RGBA', (128, 16384), (0,0,0,255))

    cubes = []
    face_cols = []  # per cube: dict face->color for painting
    # split over-wide boxes along Z so every net fits the 128-wide canvas
    # (tank1 hull: w20+d63 -> net 166). Split parts are stacked end-to-end
    # so the union keeps the exact same volume and colours.
    expanded = []
    for (x0, y0, z0, x1, y1, z1) in boxes:
        w_, h_, d_ = x1-x0+1, y1-y0+1, z1-z0+1
        max_d = (128 // 2) - w_
        if d_ <= max_d:
            expanded.append((x0, y0, z0, x1, y1, z1))
            continue
        z = z0
        while z <= z1:
            zd = min(max_d, z1 - z + 1)
            expanded.append((x0, y0, z, x1, y1, z + zd - 1))
            z += zd
    boxes = expanded
    for (x0,y0,z0,x1,y1,z1) in boxes:
        w, h, d = x1-x0+1, y1-y0+1, z1-z0+1
        # net dims: total width 2*(w+d), height d+h
        nu, nv = alloc(2*(w+d), d+h)
        # sample colors: center voxel of each face
        def col(fx, fy, fz):
            c = v2.get((fx, fy, fz))
            return c if c else (255, 0, 255)
        # Per-TEXEL sampling: a merged box can span livery boundaries
        # (police1 cabin: white roof band + blue hood/trunk). Sampling one
        # center colour flattens the whole face to a single colour - the
        # "scrambled texture" bug. Each net texel now reads the voxel that
        # maps to it (M34 round-2 fix).
        def texel(face, px, py):
            # px,py in 0..face_w-1 / 0..face_h-1 net coords
            if face == 'up':    return col(x0+px, y1, z0+py)
            if face == 'down':  return col(x0+px, y0, z0+py)
            # flipped frame: the +Z' cap sits at geo z0 - it must paint
            # the z0 column (and -Z' cap paints z1). Swap north/south
            # sampling or the nose cap shows the far end's colors (M51r).
            if face == 'north': return col(x0+px, y1-py, z1)
            if face == 'south': return col(x0+px, y1-py, z0)
            if face == 'west':  return col(x0, y1-py, z0+px)
            return               col(x1, y1-py, z0+px)   # east
        uv = {
            'east':  [nu + d + w, nv + d, d, h],
            'west':  [nu, nv + d, d, h],
            'up':    [nu + d, nv, w, d],
            'down':  [nu + d + w, nv, w, d],
            'north': [nu + d, nv + d, w, h],
            'south': [nu + 2*d + w, nv + d, w, h],
        }
        for f, (ux, uy, uw, uh) in [(k, v) for k, v in uv.items()]:
            for py in range(uh):
                for px in range(uw):
                    c = texel(f, px, py)
                    img.putpixel((ux+px, uy+py), (c[0], c[1], c[2], 255))
        # 1.12 per-face UV dict format (what BedrockGeoImporter's
        # BuildNetPerFace actually parses): {"north": {"uv":[u,v],
        # "uv_size":[w,h]}, ...}
        uv12 = {f: {'uv': [ux, uy], 'uv_size': [uw, uh]}
                for f, (ux, uy, uw, uh) in uv.items()}
        cubes.append({
            'origin': [x0 * scale, y0 * scale, z0 * scale],
            'size': [w * scale, h * scale, d * scale],
            'uv': uv12,
        })
    imgSkinHolder = img  # saved after wheel nets are painted below

    bones_out = [{
        'name': 'body',
        'pivot': [W/2 * scale, 0, D/2 * scale],
        'cubes': cubes,
    }]
    # ---- ONE shared wheel template (M34 round-5) ----
    # Detection varies pillar to pillar (window bands vs tire runs), which
    # produced asymmetric/oversized wheels. Now the SIZE comes from the CAR:
    # D = round(0.45 * body_height) (mmmm ground truth: Ø5-6 on 11-13 vox
    # cars), the disc bottom sits at y=0 (flush with the ground), and all
    # four wheels are stamped from the same template. Detection only picks
    # the AXLE z positions.
    body_ymax = max(k[1] for k in v2.keys()) + 1   # k = (x, y_up, z_len)
    DISC_D = max(4, min(7, round(0.45 * body_ymax)))
    DISC_DEPTH = 2                              # axle-line thickness (x)

    for wname, cells in wheel_specs:
        if not cells: continue
        xs = [c[0] for c in cells]; ys = [c[1] for c in cells]; zs = [c[2] for c in cells]
        wx0, wx1 = min(xs), max(xs); wy0, wy1 = min(ys), max(ys); wz0, wz1 = min(zs), max(zs)
        is_left = wx0 < (max(p[0] for p in v2) + 1) / 2.0

        depth = DISC_DEPTH
        R = DISC_D // 2
        zw = max(1, R)
        # mirrored placement about the car centre: both discs protrude
        # past the body flanks by the same amount
        ax = (1 - depth) if is_left else (W - 1)
        cy = DISC_D / 2.0                       # hub height = D/2 (bottom at 0)
        cz = (wz0 + wz1) // 2
        TIRE = (40, 40, 44); HUB = (170, 170, 175); SPOKE = (228, 228, 235)

        def paint_slab(sy, sw_y, sz0, sw_z, paint):
            # allocate a net and paint it; return per-face uv dict
            nu, nv = alloc(2*(depth + sw_z), depth + sw_y)
            uvw = {
                'east':  [nu + depth + sw_z, nv + depth, depth, sw_y],
                'west':  [nu, nv + depth, depth, sw_y],
                'up':    [nu + depth, nv, sw_z, depth],
                'down':  [nu + depth + sw_z, nv, sw_z, depth],
                'north': [nu + depth, nv + depth, sw_z, sw_y],
                'south': [nu + 2*depth + sw_z, nv + depth, sw_z, sw_y],
            }
            for f, (ux, uy, uw, uh) in [(k, v) for k, v in uvw.items()]:
                c = paint if f in ('east', 'west') else TIRE
                for py in range(uh):
                    for px in range(uw):
                        img.putpixel((ux+px, uy+py), (c[0], c[1], c[2], 255))
            return uvw

        # ROTATION-SYMMETRIC octagon inside the D x D bounding box, grounded
        # at y=0: centre band (D-2 tall, full width) + 1-vox chamfers at
        # top AND bottom (D-2 wide). A chamfer on one side only made the
        # disc wobble as it spun (M34 round-5: "rear wheel bigger than
        # front" at different spin phases).
        rows = []
        if DISC_D >= 4:
            rows.append((0, 1, cz - zw + 1, 2 * zw - 1))           # bottom chamfer
            rows.append((1, DISC_D - 2, cz - zw, 2 * zw + 1))      # centre band
            rows.append((DISC_D - 1, 1, cz - zw + 1, 2 * zw - 1))  # top chamfer
        else:
            rows.append((0, DISC_D, cz - zw, 2 * zw + 1))

        wheel_cubes = []
        centre_uv = None
        for (sy, swy, sz0, swz) in rows:
            if swy <= 0 or swz <= 0: continue
            uvw = paint_slab(sy, swy, sz0, swz, HUB)
            if swy > 1: centre_uv = uvw                      # centre slab
            wheel_cubes.append({
                'origin': [ax * scale, sy * scale, sz0 * scale],
                'size': [depth * scale, swy * scale, swz * scale],
                'uv': {f: {'uv': [r[0], r[1]], 'uv_size': [r[2], r[3]]}
                       for f, r in uvw.items()},
            })
        # bold two-tone hub on the OUTWARD face: bright upper-left quadrant
        # + dark lower-right quadrant => rotation reads clearly at distance
        if centre_uv is not None:
            oface = 'west' if is_left else 'east'
            ux, uy = centre_uv[oface][0], centre_uv[oface][1]
            uw, uh2 = centre_uv[oface][2], centre_uv[oface][3]
            hx, hy = ux + uw // 2, uy + uh2 // 2
            half = max(1, min(uw, uh2) // 4)
            for py in range(-half, half + 1):
                for px in range(-half, half + 1):
                    bright = (px <= 0) != (py > 0)     # diagonal split
                    col = SPOKE if bright else (90, 90, 96)
                    img.putpixel((hx + px, hy + py), (col[0], col[1], col[2], 255))
        bones_out.append({
            'name': wname,
            'parent': 'body',
            'pivot': [(ax + depth / 2.0) * scale,   # disc centre (wobble-free)
                      (DISC_D / 2.0) * scale,
                      (cz + 0.5) * scale],
            'cubes': wheel_cubes,
        })

    finalH = max(16, 1 << (texH[0] - 1).bit_length())   # pow2 for the importer
    imgSkinHolder.crop((0, 0, 128, finalH)).save(f'{tex_dir}/{name}_skin.png')

    geo = {
        'format_version': '1.12.0',
        'minecraft:geometry': [{
            'description': {
                'identifier': f'geometry.{name}',
                # must equal the SAVED png height (pow2 finalH): UVs are
                # normalized by this number — texH[0] (unpadded content
                # height) made lower faces sample the black pad below
                'texture_width': 128, 'texture_height': finalH,
                'visible_bounds_width': (max(W, D) * scale) / 16 + 1,
                'visible_bounds_height': (H * scale) / 16 + 0.5,
                'visible_bounds_offset': [0, (H * scale) / 32, 0],
            },
            'bones': bones_out,
        }],
    }
    with open(f'{geo_dir}/{name}.geo.json', 'w') as f:
        json.dump(geo, f, indent=1)
    print(f'{name}: vox={len(vox)} boxes={len(boxes)} size={W}x{H}x{D} skin regions={len(regions)}')
    return len(boxes)

if __name__ == '__main__':
    base = 'D:/zlj_world/_refs/mmmm/vox'
    geo_dir = 'D:/zlj_world/VoxelCraft/Assets/Resources/Geo'
    tex_dir = 'D:/zlj_world/VoxelCraft/Assets/Resources/Textures'
    jobs = {
        'mob_dog1': 'dog_urban', 'mob_cat1': 'cat_urban', 'mob_penguin': 'penguin',
        'mob_bear': 'bear', 'mob_cat3': 'cat_orange', 'mob_dog2': 'dog_brown',
    }
    for src, name in jobs.items():
        convert(name, f'{base}/{src}.vox', geo_dir, tex_dir)
