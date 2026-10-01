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
    remaining = set(vox)
    boxes = []
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
                    run = []          # longest contiguous gray run in this pillar
                    best = []
                    for y in range(0, maxY + 1):
                        c0 = v2.get((x, y, z))
                        if c0 is None: continue
                        gray = abs(c0[0] - c0[1]) < 24 and abs(c0[1] - c0[2]) < 24
                        if gray: run.append((x, y, z))
                        else:
                            if len(run) > len(best): best = run
                            run = []
                    if len(run) > len(best): best = run
                    keep.update(best)
            wheel_cells |= keep
            wheel_specs.append((f'wheel{fore}{side}', keep))
    body_v2 = {k: v for k, v in v2.items() if k not in wheel_cells}
    boxes = greedy_boxes(body_v2)
    W = max(p[0] for p in v2)+1; H = max(p[1] for p in v2)+1; D = max(p[2] for p in v2)+1

    # --- skin layout: shelf-pack box nets, 1 px per voxel-face ---
    regions = []  # (x,y,w,h) allocated
    def alloc(w, h):
        Wt = 128
        y = 0
        while True:
            row = [r for r in regions if r[1] <= y < r[1]+r[3] and r[1]+r[3] > y]
            # simple first-fit scan
            for x in range(0, Wt - w + 1, 1):
                ok = all(not (x < r[0]+r[2] and x+w > r[0] and y < r[1]+r[3] and y+h > r[1]) for r in regions)
                if ok:
                    regions.append((x,y,w,h)); return x, y
            y += 1
    texH = 128
    img = Image.new('RGBA', (128, texH), (0,0,0,255))

    cubes = []
    face_cols = []  # per cube: dict face->color for painting
    for (x0,y0,z0,x1,y1,z1) in boxes:
        w, h, d = x1-x0+1, y1-y0+1, z1-z0+1
        # net dims: total width 2*(w+d), height d+h
        nu, nv = alloc(2*(w+d), d+h)
        # sample colors: center voxel of each face
        def col(fx, fy, fz):
            c = v2.get((fx, fy, fz))
            return c if c else (255, 0, 255)
        cx, cy, cz = (x0+x1)//2, (y0+y1)//2, (z0+z1)//2
        faces = {
            'east':  col(x1, cy, cz) if w>1 or True else None,
            'west':  col(x0, cy, cz),
            'up':    col(cx, y1, cz),
            'down':  col(cx, y0, cz),
            'north': col(cx, cy, z0),
            'south': col(cx, cy, z1),
        }
        # bedrock per-face uv rects (see BuildNetPerFace):
        # east/west: [d+w, d, d, h]/[0, d, d, h]... simplified: allocate 6 rects in net layout
        uv = {
            'east':  [nu + d + w, nv + d, d, h],
            'west':  [nu, nv + d, d, h],
            'up':    [nu + d, nv, w, d],
            'down':  [nu + d + w, nv, w, d],
            'north': [nu + d, nv + d, w, h],
            'south': [nu + 2*d + w, nv + d, w, h],
        }
        for f, (ux, uy, uw, uh) in [(k, v) for k, v in uv.items()]:
            c = faces[f]
            for py in range(uh):
                for px in range(uw):
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
    for wname, cells in wheel_specs:
        if not cells: continue
        xs = [c[0] for c in cells]; ys = [c[1] for c in cells]; zs = [c[2] for c in cells]
        wx0, wx1 = min(xs), max(xs); wy0, wy1 = min(ys), max(ys); wz0, wz1 = min(zs), max(zs)
        w, h, d = wx1-wx0+1, wy1-wy0+1, wz1-wz0+1
        is_left = wx0 < (max(p[0] for p in v2) + 1) / 2.0

        # ---- procedural disc wheel (octagon profile) ----
        # mmmm source wheels are 1-voxel flush slabs: rotation can't read.
        # Build a proper disc wheel in the y-z plane around the x axle:
        # centre band (full width) + chamfered top/bottom rows, extruded
        # past the body side so it protrudes.
        depth = max(2, min(3, d))              # axle-line thickness (x)
        R = (wy1 - wy0 + 1) // 2               # ~half height
        zw = max(1, R)                          # z half-width of centre band
        ax = (wx0 - 1) if is_left else (wx1 + 1)   # outward slab plane
        cy = wy0 + (wy1 - wy0) / 2.0            # hub y
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

        # octagon rows: bottom chamfer, centre band, top chamfer
        rows = []
        if wy1 - wy0 + 1 >= 3:
            rows.append((wy0 - 1, 1, cz - zw + 1, 2 * zw - 1))   # bottom (dropped 1)
            rows.append((wy1 + 1, 1, cz - zw + 1, 2 * zw - 1))   # top (raised 1)
            rows.append((wy0, wy1 - wy0 + 1, cz - zw, 2 * zw + 1))     # centre
        else:
            rows.append((wy0, wy1 - wy0 + 1, cz - zw, 2 * zw + 1))

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
            'pivot': [((ax + 0.5) if not is_left else (ax + 0.5)) * scale,
                      (wy0 + (wy1 - wy0 + 1) / 2.0) * scale,
                      (cz + 0.5) * scale],
            'cubes': wheel_cubes,
        })

    imgSkinHolder.save(f'{tex_dir}/{name}_skin.png')

    geo = {
        'format_version': '1.12.0',
        'minecraft:geometry': [{
            'description': {
                'identifier': f'geometry.{name}',
                'texture_width': 128, 'texture_height': texH,
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
