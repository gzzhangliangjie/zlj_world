import struct
from collections import Counter
import os

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

def save(path, size, vox, pal):
    sx, sy, sz = size
    out = bytearray(b'VOX ' + struct.pack('<I', 150))
    voxels = [(x, y, z, c) for (x, y, z), c in vox.items()]
    body = struct.pack('<I', len(voxels)) + b''.join(struct.pack('<4B', *v) for v in voxels)
    children = (b'SIZE' + struct.pack('<II', 12, 0) + struct.pack('<3I', sx, sy, sz) +
                b'XYZI' + struct.pack('<II', len(body), 0) + body +
                b'RGBA' + struct.pack('<II', (len(pal)-1)*4, 0) + b''.join(struct.pack('<4B', *c) for c in pal[1:]))
    out += b'MAIN' + struct.pack('<II', 0, len(children)) + children
    open(path, 'wb').write(bytes(out))

# Role remap -> block anchor colors (VoxStructure.BlockForColor)
ROLE = {
    (100, 204, 0, 255):   (96, 168, 60, 255),    # leaf -> Leaves
    (100, 204, 48, 255):  (96, 168, 60, 255),
    (84, 168, 0, 255):    (96, 168, 60, 255),
    (152, 152, 100, 255): (103, 82, 49, 255),    # trunk -> Log
    (136, 136, 136, 255): (125, 125, 125, 255),  # grey -> Stone
    (168, 168, 168, 255): (125, 125, 125, 255),
    (204, 204, 152, 255): (252, 224, 130, 255),  # lamp pale -> Glowstone
    (236, 236, 180, 255): (252, 224, 130, 255),
    (68, 68, 68, 255):    (125, 125, 125, 255),  # dark grey -> Stone
    (70, 70, 70, 255):    (125, 125, 125, 255),
    (84, 84, 84, 255):    (125, 125, 125, 255),
    (100, 100, 100, 255): (125, 125, 125, 255),
    (103, 82, 49, 255):   (103, 82, 49, 255),
    (252, 0, 0, 255):     (176, 46, 38, 255),    # red accents -> WoolRed
}

# Per-item axis + scale, decided from per-layer shape dumps of the ORIGINALS:
#   swap=True  : mmmm stores the model Z-up (vertical axis is Z)
#   sub=2      : halve every axis (mm master scale is 2x the MC block scale)
# Height must stay <= 16 so baseY+H+1 <= 79 on mountain terrain (chunk H=80).
# Per-item extra role remaps applied AFTER the global ROLE (item wins).
# Source palettes dumped 2026-10-01: fence2 is ALL (136,136,136) - a grey
# lattice, recolor -> green hedge to match its semantic; trashcan1 has a dark
# (84,84,84) detail layer -> WoolBlack rim; stlight1 dark (68,68,68) pole ->
# WoolBlack for contrast, pale lamp stays Glowstone.
ITEM_ROLE = {
    'fence2':   {(136, 136, 136, 255): (96, 168, 60, 255)},          # grey lattice -> Leaves hedge
    'trashcan': {(84, 84, 84, 255): (32, 32, 38, 255)},              # dark detail -> WoolBlack
    'stlight':  {(68, 68, 68, 255): (32, 32, 38, 255)},              # pole -> WoolBlack contrast
    # --- batch 2 (verified Z-up swap unless noted; slice profiles dumped) ---
    'bench3':   {(220, 220, 220, 255): (232, 236, 238, 255)},        # white bench -> WoolWhite (Y-up, no swap)
    'bench4':   {(116, 116, 116, 255): (125, 125, 125, 255),
                 (84, 84, 84, 255):    (32, 32, 38, 255)},           # grey+dark -> Stone+WoolBlack (Y-up)
    'hydrant':  {(184, 0, 0, 255):    (176, 46, 38, 255),            # red body -> WoolRed
                 (136, 0, 0, 255):    (176, 46, 38, 255)},           # dark red caps -> WoolRed (keep silhouette)
    'pumpkin':  {(252, 152, 0, 255):  (234, 195, 55, 255),           # orange -> WoolYellow (no Pumpkin block)
                 (252, 252, 48, 255): (234, 195, 55, 255),
                 (48, 100, 0, 255):   (86, 128, 40, 255)},           # green stem -> WoolGreen
    'mailbox':  {(48, 100, 252, 255): (53, 87, 178, 255),            # blue box -> WoolBlue
                 (220, 220, 220, 255): (232, 236, 238, 255)},        # white flag -> WoolWhite
    'trlight2': {(68, 68, 68, 255):   (32, 32, 38, 255),             # pole -> WoolBlack
                 (168, 168, 168, 255): (125, 125, 125, 255),
                 (204, 204, 152, 255): (252, 224, 130, 255)},        # lamp head -> Glowstone
    'newsbox1': {(168, 168, 168, 255): (125, 125, 125, 255),         # grey body -> Stone
                 (68, 68, 68, 255):    (32, 32, 38, 255),            # dark stand -> WoolBlack
                 (48, 152, 204, 255):  (53, 87, 178, 255)},          # blue front -> WoolBlue
    'cone1':    {(252, 100, 0, 255):  (234, 195, 55, 255),           # orange -> WoolYellow
                 (236, 236, 236, 255): (232, 236, 238, 255)},        # white stripe -> WoolWhite
}
# Rare-but-critical colors that MUST survive sub=2 majority voting
# (the stlight lamp head is 12 voxels and got outvoted to zero before).
PRIORITY = {(204, 204, 152, 255), (236, 236, 180, 255), (252, 204, 100, 255)}

CFG = {
    'tree1':    dict(swap=True,  sub=1),  # 8x8x15  -> 8x15x8
    'tree2':    dict(swap=True,  sub=1),  # 10x10x19 -> 10x19x10
    'tree3':    dict(swap=True,  sub=1),  # 10x10x16 -> 10x16x10
    'tree4':    dict(swap=True,  sub=1),  # 6x6x10  -> 6x10x6
    'fence2':   dict(swap=False, sub=1),  # 16x2x18 ALREADY Y-up (2-tall hedge row)
    'stlight':  dict(swap=True,  sub=2),  # 38x4x32 -> 19x16x2 (lamp; /2 or it pokes past chunk top)
    'trashcan': dict(swap=False, sub=1),  # 6x5x6   cube, Y-up already
    'planter':  dict(swap=True,  sub=1),  # 8x8x13  -> 8x13x8
    # --- batch 2 (slice-profile-audited 2026-10-01) ---
    'bench3':   dict(swap=False, sub=1),  # 15x6x7  Y-up bench (seat at z3)
    'bench4':   dict(swap=False, sub=1),  # 15x6x7  Y-up bench
    'hydrant':  dict(swap=True,  sub=1),  # 4x5x7 -> 4x7x5? Z-up, vertical=z(7)
    'pumpkin':  dict(swap=True,  sub=1),  # 7x5x6 -> Z-up
    'trlight2': dict(swap=True,  sub=2),  # 6x6x25 -> 6x13x6 traffic light (/2 to fit H<=16)
    'newsbox1': dict(swap=True,  sub=1),  # 8x7x11 -> Z-up
    'cone1':    dict(swap=True,  sub=1),  # 3x3x7 -> Z-up cone
}
SRC = 'D:/zlj_world/_refs/mmmm/vox'
DST = 'D:/zlj_world/VoxelCraft/Assets/Resources/VoxStructures'
for n, cfg in CFG.items():
    cands = [f for f in os.listdir(SRC) if f.startswith('obj_' + n)]
    src = os.path.join(SRC, sorted(cands)[0])
    size, vox, pal = load(src)
    W0, H0, D0 = size
    if cfg['swap']:
        vox = {(x, z, y): c for (x, y, z), c in vox.items()}
        W0, H0, D0 = W0, D0, H0
    s = cfg['sub']
    irole = ITEM_ROLE.get(n, {})
    if s > 1:
        acc = {}
        for (x, y, z), c in vox.items():
            acc.setdefault((x // s, y // s, z // s), []).append(c)
        out = {}
        for k, v in acc.items():
            # priority colors (lamp heads) beat the majority; else majority vote
            pri = [c for c in v if pal[c] in PRIORITY] if max(c for c in v) < len(pal) else []
            out[k] = pri[0] if pri else Counter(v).most_common(1)[0][0]
        vox = out
        W0, H0, D0 = W0 // s, H0 // s, D0 // s
    npal = [p0 if p0 is None else irole.get(p0, ROLE.get(p0, p0)) for p0 in pal]
    save(os.path.join(DST, n + '.bytes'), (W0, H0, D0), vox, npal)
    cc = Counter(npal[c] for c in vox.values() if c < len(npal) and npal[c])
    print(f'{n}: -> {W0}x{H0}x{D0} H<=16:{H0<=16} | ' + ', '.join(f'{c}:{k}' for c, k in cc.most_common(4)))
print('done')
