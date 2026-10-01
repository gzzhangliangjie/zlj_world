#!/usr/bin/env python
# vox_lint.py — offline gate for converted VoxStructures (.bytes = MagicaVoxel).
# Run AFTER remap_props.py, BEFORE any Unity snapshot. Exit 1 on any failure.
#
# Checks (each catches a real bug that shipped in M30-M32):
#   1. parse + size sane (W,D >=2, 2 <= H <= 16; tree2 H=19 whitelisted)
#   2. census by RGB -> must NOT be single-color for multi-part props
#      (palette-collapse bug: ROLE exact-match miss -> all-Nearest-Neighbor-Stone)
#   3. expected roles per prop (Glowstone must exist in stlight, Leaves in
#      hedge/tree/planter...) — the "lamp with no lamp" bug
#   4. non-empty and no NaN-ish sizes
#   5. footprint sanity: prop must stand on its base layer (base layer >0 voxels)
import struct, sys, os
from collections import Counter

# anchor colors from VoxStructure.BlockForColor (keep both in sync!)
BLOCK = {
    (96, 168, 60): 'Leaves', (103, 82, 49): 'Log', (125, 125, 125): 'Stone',
    (252, 224, 130): 'Glowstone', (32, 32, 38): 'WoolBlack',
    (176, 46, 38): 'WoolRed', (219, 207, 163): 'Sand', (150, 97, 83): 'Brick',
    (156, 127, 78): 'Plank', (200, 220, 228): 'Glass', (106, 170, 64): 'Grass',
    (232, 236, 238): 'WoolWhite', (234, 195, 55): 'WoolYellow',
    (53, 87, 178): 'WoolBlue', (86, 128, 40): 'WoolGreen',
}
# per-prop expectations: (min distinct colors, required roles)
EXPECT = {
    'tree1':    (2, {'Leaves', 'Log'}),
    'tree2':    (2, {'Leaves', 'Log'}),
    'tree3':    (2, {'Leaves', 'Log'}),          # WoolRed optional accent
    'tree4':    (2, {'Leaves', 'Log'}),
    'fence2':   (1, {'Leaves'}),                 # semantic green hedge
    'stlight':  (3, {'Glowstone', 'Stone'}),     # lamp head + pole + contrast
    'trashcan': (2, {'Stone', 'WoolBlack'}),     # body + dark rim
    'planter':  (3, {'Leaves', 'Stone'}),        # plant + pot (+Log trunk)
    'cottage':  (2, {'Plank'}),                  # hand-built, lenient
    'house5':   (2, {'Brick'}),                  # hand-built, lenient
    # batch 2
    'bench3':   (1, {'WoolWhite'}),
    'bench4':   (2, {'Stone', 'WoolBlack'}),
    'hydrant':  (1, {'WoolRed'}),
    'pumpkin':  (1, {'WoolYellow'}),             # +WoolGreen stem (1 voxel)
    'trlight2': (3, {'WoolBlack', 'Glowstone'}), # traffic light w/ lamp
    'newsbox1': (3, {'Stone', 'WoolBlack', 'WoolBlue'}),
    'cone1':    (2, {'WoolYellow', 'WoolWhite'}),
}
HEIGHT_WHITELIST = {'tree2': 19}

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

def main():
    dst = 'D:/zlj_world/VoxelCraft/Assets/Resources/VoxStructures'
    fails = []
    for fn in sorted(os.listdir(dst)):
        if not fn.endswith('.bytes'): continue
        name = fn[:-6]
        size, vox, pal = load(os.path.join(dst, fn))
        W, H, D = size
        # 1. size sanity
        if W < 2 or D < 2 or H < 2 or H > HEIGHT_WHITELIST.get(name, 16):
            fails.append(f'{name}: bad size {W}x{H}x{D}')
        if len(vox) == 0:
            fails.append(f'{name}: EMPTY')
            continue
        # 5. base layer non-empty (stands on ground)
        base_n = sum(1 for (x, y, z) in vox if y == 0)
        if base_n == 0:
            fails.append(f'{name}: base layer empty (floats)')
        # 2+3. color census + roles
        cc = Counter(pal[c][:3] for c in vox.values() if c < len(pal) and pal[c])
        roles = Counter(BLOCK.get(rgb, f'@{rgb}') for rgb, n in cc.items() for _ in [n])
        distinct = len(cc)
        min_colors, required = EXPECT.get(name, (1, set()))
        summary = ', '.join(f'{BLOCK.get(rgb, "@"+str(rgb))}:{n}' for rgb, n in cc.most_common(5))
        if name in EXPECT and distinct < min_colors:
            fails.append(f'{name}: SINGLE-COLOR COLLAPSE ({summary})')
        missing = required - set(roles)
        if missing:
            fails.append(f'{name}: missing roles {sorted(missing)} ({summary})')
        print(f'ok  {name}: {W}x{H}x{D} vox={len(vox)} base={base_n} | {summary}')
    if fails:
        print('\nFAIL:')
        for f in fails: print(' -', f)
        sys.exit(1)
    print('\nVOXLINT PASS')

if __name__ == '__main__':
    main()
