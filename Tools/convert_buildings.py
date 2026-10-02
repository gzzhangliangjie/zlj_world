#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""convert_buildings.py — mmmm obj_ 建筑源(.vox, z-up, 64x64 底) → VoxStructures/*.bytes

Pipeline (mirrors M32/M33 props pipeline, works for large buildings):
  1. load source .vox (XYZI + RGBA palette)
  2. /2 subsample with majority color per output cell
  3. bbox crop, then center-crop/clip to MAX_W x MAX_H x MAX_D (see LIMITS)
  4. palette remap to block anchor colors (same table as vox_lint.BLOCK /
     VoxStructure.BlockForColor) so runtime + lint see clean roles
  5. emit a minimal standard .vox (VOX 150, MAIN/SIZE/XYZI/RGBA) named <name>.bytes

Usage: python convert_buildings.py obj_store01 obj_house1 ...
Writes to VoxelCraft/Assets/Resources/VoxStructures/<name>.bytes
"""
import struct, sys, os
from collections import Counter

SRC_DIR = 'D:/zlj_world/_refs/mmmm/vox'
DST_DIR = 'D:/zlj_world/VoxelCraft/Assets/Resources/VoxStructures'

# per-name output limits: (max_w, max_h, max_d) after /2 subsample + bbox crop.
# mmmm obj_ sources are full STREET BLOCKS (64x64 with sidewalk + multiple
# facades), not single houses — cropping to the center 24 cut away shop
# windows and sign strips (the M40 "monochrome, broken windows" review).
# Keep the whole /2 footprint (32x32) and only clip height.
LIMITS = {
    'default': (32, 14, 32),
}

# block anchor colors — MUST stay in sync with vox_lint.BLOCK and
# VoxStructure.BlockForColor
BLOCK = {
    (96,168,60):'Leaves',(103,82,49):'Log',(125,125,125):'Stone',
    (252,224,130):'Glowstone',(32,32,38):'WoolBlack',(176,46,38):'WoolRed',
    (219,207,163):'Sand',(150,97,83):'Brick',(156,127,78):'Plank',
    (200,220,228):'Glass',(106,170,64):'Grass',(232,236,238):'WoolWhite',
    (234,195,55):'WoolYellow',(53,87,178):'WoolBlue',(86,128,40):'WoolGreen',
}

def load(path):
    d = open(path,'rb').read()
    i = d.find(b'SIZE'); X,Y,Z = struct.unpack('<3i', d[i+12:i+24])
    j = d.find(b'XYZI'); n = struct.unpack('<I', d[j+12:j+16])[0]
    vs = {}
    for k in range(n):
        x,y,z,c = d[j+16+k*4:j+20+k*4]
        vs[(x,y,z)] = c
    pal = [None]
    a = d.find(b'RGBA')
    for k in range(255):
        r,g,b,al = d[a+12+4*k:a+16+4*k]
        pal.append((r,g,b,al))
    return (X,Y,Z), vs, pal

# semantic role overrides (file -> {source RGB -> block}), applied BEFORE
# nearest-anchor matching so windows/doors/signs keep their intended material.
# Catches the two M40 review bugs: greyscale walls collapsing everything to
# Stone (all-grey monotony) and wall whites/off-blues falling to Glass/white
# wool (transparent-hole / dead-panel "windows").
ROLE = {
    'obj_store01': {
        (32, 32, 32):   'WoolBlack',    # storefront band + dark trim
        (204, 252, 252):'Glass',        # display windows / glass door
        (204, 152, 48): 'WoolYellow',   # awning orange
        (116, 0, 0):    'WoolRed',      # red sign strip
        (136, 136, 136):'Stone',        # main wall
        (168, 168, 168):'Stone',        # light trim
        (84, 84, 84):   'Cobble',       # dark base
        (116, 116, 116):'Cobble',
        (220, 220, 220):'WoolWhite',    # door frame
    },
    'obj_house1': {
        (48, 152, 204): 'Glass', (152, 204, 252): 'Glass', (48, 100, 152): 'Glass',  # window family
        (100, 48, 0):   'Plank',       # wooden door
        (136, 136, 136):'Stone',
        (168, 168, 168):'Stone',
        (236, 236, 236):'WoolWhite',
        (184, 184, 184):'Cobble',
    },
    'obj_house2': {
        (152, 204, 252):'Glass',        # windows
        (236, 236, 236):'WoolWhite',    # main white wall
        (168, 168, 168):'Cobble',       # base band
    },
    'obj_house6': {
        (48, 152, 252): 'Glass',        # bright windows (z16-22, 164 vox)
        (100, 48, 0):   'Plank', (152, 100, 0): 'Plank',  # wood door/trim
        (168, 168, 168):'Sand',         # main light-grey wall -> warm sand render
        (220, 220, 220):'WoolWhite',    # white base slab + trim (NOT glass)
        (136, 136, 136):'Stone', (184, 184, 184):'Cobble', (116, 116, 116):'Cobble',
        (236, 236, 236):'WoolWhite',
    },
}

# anchor colors by block name — role targets (keep in sync with vox_lint/BlockForColor)
ANCHOR = {
    'Leaves':(96,168,60),'Log':(103,82,49),'Stone':(125,125,125),
    'Glowstone':(252,224,130),'WoolBlack':(32,32,38),'WoolRed':(176,46,38),
    'Sand':(219,207,163),'Brick':(150,97,83),'Plank':(156,127,78),
    'Glass':(200,220,228),'Grass':(106,170,64),'WoolWhite':(232,236,238),
    'WoolYellow':(234,195,55),'WoolBlue':(53,87,178),'WoolGreen':(86,128,40),
    'Dirt':(121,85,58),'Cobble':(110,110,110),'Snow':(240,246,246),
}

def nearest_block(rgb):
    best, bd = None, 1e9
    for k in BLOCK:
        d = sum((a-b)**2 for a,b in zip(rgb,k))
        if d < bd: bd, best = d, k
    return best

def convert(name):
    size, vs, pal = load(os.path.join(SRC_DIR, name + '.vox'))
    # 1) /2 subsample, majority color; ROLE colors get weighted votes so thin
    #    window bands / sign strips survive downsampling instead of being
    #    outvoted by the surrounding wall greys
    roles = ROLE.get(name, {})
    def w(ci):
        rgb = tuple(pal[ci][:3])
        return 4 if rgb in roles else 1
    acc = {}
    for (x,y,z),c in vs.items():
        acc.setdefault((x//2,y//2,z//2), []).append(c)
    sub = {}
    for k, cs in acc.items():
        cnt = Counter()
        for c in cs: cnt[c] += w(c)
        sub[k] = cnt.most_common(1)[0][0]
    # 2) bbox crop
    xs=[p[0] for p in sub]; ys=[p[1] for p in sub]; zs=[p[2] for p in sub]
    x0,x1=min(xs),max(xs); y0,y1=min(ys),max(ys); z0,z1=min(zs),max(zs)
    # footprint = x,y (plan); height axis = z (z-up source)
    W,H,D = LIMITS.get(name, LIMITS['default'])
    # center crop to limits, keeping the denser/central part
    def cc(v0, v1, keep):
        span = v1-v0+1
        s = (span-keep)//2
        s = max(0, s)
        return v0+s, v0+s+min(keep,span)-1
    x0,x1 = cc(x0,x1,W); y0,y1 = cc(y0,y1,D); z0,z1 = cc(z0,z1,H)
    out = {}
    for (x,y,z),c in sub.items():
        if x0<=x<=x1 and y0<=y<=y1 and z0<=z<=z1:
            out[(x-x0, z-z0, y-y0)] = c   # emit y-up: (X, height, depth)
    # 3) palette remap to anchors
    # build the output palette: index 1..N distinct remapped colors
    remap = {}
    roles = ROLE.get(name, {})
    for (x,h,dpt),c in out.items():
        rgb = tuple(pal[c][:3])
        blk = roles.get(rgb)
        remap[c] = ANCHOR[blk] if blk else nearest_block(rgb)
    # (re)assign: palette = sorted distinct anchor colors
    anchors = sorted(set(remap.values()))
    aidx = {a:i+1 for i,a in enumerate(anchors)}
    voxels = [(x,h,dpt,aidx[remap[c]]) for (x,h,dpt),c in out.items()]
    X = max(v[0] for v in voxels)+1; Hh = max(v[1] for v in voxels)+1; Dd = max(v[2] for v in voxels)+1
    # 4) write minimal .vox
    def chunk(cid, body):
        return cid + struct.pack('<II', len(body), 0) + body
    core = chunk(b'SIZE', struct.pack('<3I', X, Hh, Dd))
    xyzi = struct.pack('<I', len(voxels)) + b''.join(struct.pack('<4B', *v) for v in voxels)
    core += chunk(b'XYZI', xyzi)
    # palette: fill 255 entries; anchor i at index i+1
    palb = b''
    for i in range(255):
        if i < len(anchors):
            r,g,b = anchors[i]
            palb += struct.pack('<4B', r,g,b,255)
        else:
            palb += struct.pack('<4B', 0,0,0,255)
    core += chunk(b'RGBA', palb)
    # Unity VoxStructure.Parse requires MAIN contentLen == 0 and puts the
    # children byte count in MAIN's childrenLen field (M31 house5 layout).
    main = b'MAIN' + struct.pack('<II', 0, len(core)) + core
    data = b'VOX ' + struct.pack('<I', 150) + main
    out_path = os.path.join(DST_DIR, name + '.bytes')
    open(out_path,'wb').write(data)
    # report
    INV = {v: k for k, v in ANCHOR.items()}
    roles = Counter(INV.get(a, '@' + str(a)) for a in anchors)
    print(f'{name}: {X}x{Hh}x{Dd} vox={len(voxels)} -> {out_path}')
    print('   roles:', ', '.join(f'{r}:{n}' for r,n in roles.most_common()))
    return True

if __name__ == '__main__':
    for name in sys.argv[1:]:
        convert(name)
