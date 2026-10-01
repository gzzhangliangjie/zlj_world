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

def convert(name, src, geo_dir, tex_dir, scale=1.0):
    size, vox, pal = load(src)
    # mmmm is Z-up: (x, y, z)_vox -> creature frame (x, z, y): new y = old z
    v2 = {(x, z, y): pal[c][:3] for (x,y,z),c in vox.items()}
    boxes = greedy_boxes(v2)
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
            'origin': [x0, y0, z0],
            'size': [w, h, d],
            'uv': uv12,
        })
    img.save(f'{tex_dir}/{name}_skin.png')

    geo = {
        'format_version': '1.12.0',
        'minecraft:geometry': [{
            'description': {
                'identifier': f'geometry.{name}',
                'texture_width': 128, 'texture_height': texH,
                'visible_bounds_width': max(W, D) / 16 + 1,
                'visible_bounds_height': H / 16 + 0.5,
                'visible_bounds_offset': [0, H / 32, 0],
            },
            'bones': [{
                'name': 'body',
                'pivot': [W/2, 0, D/2],
                'cubes': cubes,
            }],
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
