import struct
from collections import Counter
import os

def load(path):
    d = open(path, 'rb').read()
    p = 8
    assert d[p:p+4] == b'MAIN'; p += 4
    mchlen, chlen = struct.unpack('<II', d[p:p+8])
    # mmmm uses the NESTED layout: contentLen==0, payload in the childrenLen
    # field (same convention Unity's VoxStructure.Parse demands on write).
    end = p + 8 + (mchlen if mchlen else chlen)
    p += 8
    size = None; vox = {}; pal = [None]; has_rgba = False
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
            has_rgba = True
            for i in range(clen // 4): pal.append(tuple(d[body+i*4:body+4+i*4]))
        p = body + clen + chlen
    if not has_rgba:
        pal = [None] + DEFAULT_PALETTE  # mmmm omits RGBA -> MagicaVoxel default palette
    return size, vox, pal

# MagicaVoxel default palette (256) — extracted verbatim from
# alien_bot1.vox (mmmm toolchain exports the stock MV palette in full);
# used when a source omits its RGBA chunk (mailbox2/grill/playgrnd1...).
DEFAULT_PALETTE = [
    (252, 252, 252, 255),
    (252, 252, 204, 255),
    (252, 252, 152, 255),
    (252, 252, 100, 255),
    (252, 252, 48, 255),
    (252, 252, 0, 255),
    (252, 204, 252, 255),
    (252, 204, 204, 255),
    (252, 204, 152, 255),
    (252, 204, 100, 255),
    (252, 204, 48, 255),
    (252, 204, 0, 255),
    (252, 152, 252, 255),
    (252, 152, 204, 255),
    (252, 152, 152, 255),
    (252, 152, 100, 255),
    (252, 152, 48, 255),
    (252, 152, 0, 255),
    (252, 100, 252, 255),
    (252, 100, 204, 255),
    (252, 100, 152, 255),
    (252, 100, 100, 255),
    (252, 100, 48, 255),
    (252, 100, 0, 255),
    (252, 48, 252, 255),
    (252, 48, 204, 255),
    (252, 48, 152, 255),
    (252, 48, 100, 255),
    (252, 48, 48, 255),
    (252, 48, 0, 255),
    (252, 0, 252, 255),
    (252, 0, 204, 255),
    (252, 0, 152, 255),
    (252, 0, 100, 255),
    (252, 0, 48, 255),
    (252, 0, 0, 255),
    (204, 252, 252, 255),
    (204, 252, 204, 255),
    (204, 252, 152, 255),
    (204, 252, 100, 255),
    (204, 252, 48, 255),
    (204, 252, 0, 255),
    (204, 204, 252, 255),
    (204, 204, 204, 255),
    (204, 204, 152, 255),
    (204, 204, 100, 255),
    (204, 204, 48, 255),
    (204, 204, 0, 255),
    (204, 152, 252, 255),
    (204, 152, 204, 255),
    (204, 152, 152, 255),
    (204, 152, 100, 255),
    (204, 152, 48, 255),
    (204, 152, 0, 255),
    (204, 100, 252, 255),
    (204, 100, 204, 255),
    (204, 100, 152, 255),
    (204, 100, 100, 255),
    (204, 100, 48, 255),
    (204, 100, 0, 255),
    (204, 48, 252, 255),
    (204, 48, 204, 255),
    (204, 48, 152, 255),
    (204, 48, 100, 255),
    (204, 48, 48, 255),
    (204, 48, 0, 255),
    (204, 0, 252, 255),
    (204, 0, 204, 255),
    (204, 0, 152, 255),
    (204, 0, 100, 255),
    (204, 0, 48, 255),
    (204, 0, 0, 255),
    (152, 252, 252, 255),
    (152, 252, 204, 255),
    (152, 252, 152, 255),
    (152, 252, 100, 255),
    (152, 252, 48, 255),
    (152, 252, 0, 255),
    (152, 204, 252, 255),
    (152, 204, 204, 255),
    (152, 204, 152, 255),
    (152, 204, 100, 255),
    (152, 204, 48, 255),
    (152, 204, 0, 255),
    (152, 152, 252, 255),
    (152, 152, 204, 255),
    (152, 152, 152, 255),
    (152, 152, 100, 255),
    (152, 152, 48, 255),
    (152, 152, 0, 255),
    (152, 100, 252, 255),
    (152, 100, 204, 255),
    (152, 100, 152, 255),
    (152, 100, 100, 255),
    (152, 100, 48, 255),
    (152, 100, 0, 255),
    (152, 48, 252, 255),
    (152, 48, 204, 255),
    (152, 48, 152, 255),
    (152, 48, 100, 255),
    (152, 48, 48, 255),
    (152, 48, 0, 255),
    (152, 0, 252, 255),
    (152, 0, 204, 255),
    (152, 0, 152, 255),
    (152, 0, 100, 255),
    (152, 0, 48, 255),
    (152, 0, 0, 255),
    (100, 252, 252, 255),
    (100, 252, 204, 255),
    (100, 252, 152, 255),
    (100, 252, 100, 255),
    (100, 252, 48, 255),
    (100, 252, 0, 255),
    (100, 204, 252, 255),
    (100, 204, 204, 255),
    (100, 204, 152, 255),
    (100, 204, 100, 255),
    (100, 204, 48, 255),
    (100, 204, 0, 255),
    (100, 152, 252, 255),
    (100, 152, 204, 255),
    (100, 152, 152, 255),
    (100, 152, 100, 255),
    (100, 152, 48, 255),
    (100, 152, 0, 255),
    (100, 100, 252, 255),
    (100, 100, 204, 255),
    (100, 100, 152, 255),
    (100, 100, 100, 255),
    (100, 100, 48, 255),
    (100, 100, 0, 255),
    (100, 48, 252, 255),
    (100, 48, 204, 255),
    (100, 48, 152, 255),
    (100, 48, 100, 255),
    (100, 48, 48, 255),
    (100, 48, 0, 255),
    (100, 0, 252, 255),
    (100, 0, 204, 255),
    (100, 0, 152, 255),
    (100, 0, 100, 255),
    (100, 0, 48, 255),
    (100, 0, 0, 255),
    (48, 252, 252, 255),
    (48, 252, 204, 255),
    (48, 252, 152, 255),
    (48, 252, 100, 255),
    (48, 252, 48, 255),
    (48, 252, 0, 255),
    (48, 204, 252, 255),
    (48, 204, 204, 255),
    (48, 204, 152, 255),
    (48, 204, 100, 255),
    (48, 204, 48, 255),
    (48, 204, 0, 255),
    (48, 152, 252, 255),
    (48, 152, 204, 255),
    (48, 152, 152, 255),
    (48, 152, 100, 255),
    (48, 152, 48, 255),
    (48, 152, 0, 255),
    (48, 100, 252, 255),
    (48, 100, 204, 255),
    (48, 100, 152, 255),
    (48, 100, 100, 255),
    (48, 100, 48, 255),
    (48, 100, 0, 255),
    (48, 48, 252, 255),
    (48, 48, 204, 255),
    (48, 48, 152, 255),
    (48, 48, 100, 255),
    (48, 48, 48, 255),
    (48, 48, 0, 255),
    (48, 0, 252, 255),
    (48, 0, 204, 255),
    (48, 0, 152, 255),
    (48, 0, 100, 255),
    (48, 0, 48, 255),
    (48, 0, 0, 255),
    (0, 252, 252, 255),
    (0, 252, 204, 255),
    (0, 252, 152, 255),
    (0, 252, 100, 255),
    (0, 252, 48, 255),
    (0, 252, 0, 255),
    (0, 204, 252, 255),
    (0, 204, 204, 255),
    (0, 204, 152, 255),
    (0, 204, 100, 255),
    (0, 204, 48, 255),
    (0, 204, 0, 255),
    (0, 152, 252, 255),
    (0, 152, 204, 255),
    (0, 152, 152, 255),
    (0, 152, 100, 255),
    (0, 152, 48, 255),
    (0, 152, 0, 255),
    (0, 100, 252, 255),
    (0, 100, 204, 255),
    (0, 100, 152, 255),
    (0, 100, 100, 255),
    (0, 100, 48, 255),
    (0, 100, 0, 255),
    (0, 48, 252, 255),
    (0, 48, 204, 255),
    (0, 48, 152, 255),
    (0, 48, 100, 255),
    (0, 48, 48, 255),
    (0, 48, 0, 255),
    (0, 0, 252, 255),
    (0, 0, 204, 255),
    (0, 0, 152, 255),
    (0, 0, 100, 255),
    (0, 0, 48, 255),
    (236, 0, 0, 255),
    (220, 0, 0, 255),
    (184, 0, 0, 255),
    (168, 0, 0, 255),
    (136, 0, 0, 255),
    (116, 0, 0, 255),
    (84, 0, 0, 255),
    (68, 0, 0, 255),
    (32, 0, 0, 255),
    (16, 0, 0, 255),
    (0, 236, 0, 255),
    (0, 220, 0, 255),
    (0, 184, 0, 255),
    (0, 168, 0, 255),
    (0, 136, 0, 255),
    (0, 116, 0, 255),
    (0, 84, 0, 255),
    (0, 68, 0, 255),
    (0, 32, 0, 255),
    (0, 16, 0, 255),
    (0, 0, 236, 255),
    (0, 0, 220, 255),
    (0, 0, 184, 255),
    (0, 0, 168, 255),
    (0, 0, 136, 255),
    (0, 0, 116, 255),
    (0, 0, 84, 255),
    (0, 0, 68, 255),
    (0, 0, 32, 255),
    (0, 0, 16, 255),
    (236, 236, 236, 255),
    (220, 220, 220, 255),
    (184, 184, 184, 255),
    (168, 168, 168, 255),
    (136, 136, 136, 255),
    (116, 116, 116, 255),
    (84, 84, 84, 255),
    (68, 68, 68, 255),
    (32, 32, 32, 255),
    (16, 16, 16, 255),
    (0, 0, 0, 255),
]

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
    # --- batch 3 (2026-10-02) ---
    'sidewalk2': {(136, 136, 136, 255): (125, 125, 125, 255)},     # pavement grey -> Stone
    'curb2':     {(0, 152, 48, 255):   (86, 128, 40, 255),         # green strip -> WoolGreen
                  (0, 100, 0, 255):    (86, 128, 40, 255)},
    'crosswalk': {(204, 204, 48, 255): (234, 195, 55, 255)},       # yellow stripes -> WoolYellow
    'sign1':     {(184, 184, 184, 255): (125, 125, 125, 255),      # pole -> Stone
                  (236, 236, 236, 255): (232, 236, 238, 255),      # plate -> WoolWhite
                  (252, 252, 48, 255):  (234, 195, 55, 255),
                  (0, 252, 152, 255):   (86, 128, 40, 255)},
    'sign5':     {(184, 184, 184, 255): (125, 125, 125, 255),
                  (236, 236, 236, 255): (232, 236, 238, 255),
                  (236, 0, 0, 255):     (176, 46, 38, 255)},       # red plate
    'chair1':    {(68, 68, 68, 255):    (32, 32, 38, 255)},        # dark chair -> WoolBlack
    'table1':    {(184, 184, 184, 255): (125, 125, 125, 255)},     # metal table -> Stone
    'cart1':     {(168, 168, 168, 255): (125, 125, 125, 255),
                  (168, 0, 0, 255):     (176, 46, 38, 255)},       # cart body grey + red accent
    'cart2':     {(168, 0, 0, 255):     (176, 46, 38, 255),        # red hot-dog cart
                  (236, 236, 236, 255): (232, 236, 238, 255)},
    'cart1a':    {(48, 152, 252, 255):  (53, 87, 178, 255),        # blue cart
                  (168, 0, 0, 255):     (176, 46, 38, 255)},
    'busstop':   {(48, 152, 204, 255):  (200, 220, 228, 255),      # glass panels -> Glass
                  (136, 136, 136, 255): (125, 125, 125, 255),
                  (220, 220, 220, 255): (232, 236, 238, 255)},
    'fountain':  {(168, 168, 168, 255): (125, 125, 125, 255),      # basin
                  (48, 152, 204, 255):  (93, 236, 245, 255),       # water -> DiamondOre? no -> Ice-ish
                  (152, 204, 252, 255): (93, 236, 245, 255)},      # light water
    'statue1':   {(136, 136, 136, 255): (125, 125, 125, 255),
                  (168, 168, 168, 255): (125, 125, 125, 255),
                  (116, 116, 116, 255): (110, 110, 110, 255)},     # Cobble base
    'mailbox2':  {(220, 220, 220, 255): (232, 236, 238, 255),      # white box
                  (116, 116, 116, 255): (125, 125, 125, 255),      # grey post -> Stone
                  (84, 84, 84, 255):    (110, 110, 110, 255)},     # dark trim -> Cobble
    'newsbox2':  {(252, 252, 48, 255):  (234, 195, 55, 255),       # yellow box
                  (68, 68, 68, 255):    (32, 32, 38, 255)},        # dark stand
    'trashcan2': {(168, 168, 168, 255): (125, 125, 125, 255),
                  (32, 32, 32, 255):    (32, 32, 38, 255),
                  (84, 0, 0, 255):      (176, 46, 38, 255)},       # red band
    'stlight1':  {(68, 68, 68, 255):    (32, 32, 38, 255),
                  (204, 204, 152, 255): (252, 224, 130, 255)},     # lamp -> Glowstone
    'trlight1':  {(68, 68, 68, 255):    (32, 32, 38, 255),
                  (204, 204, 152, 255): (252, 224, 130, 255),
                  (0, 252, 100, 255):   (86, 128, 40, 255),
                  (236, 0, 0, 255):     (176, 46, 38, 255)},
    'container1':{(252, 48, 48, 255):   (176, 46, 38, 255),        # red crate
                  (184, 0, 0, 255):     (176, 46, 38, 255)},
    'fence1':    {(136, 136, 136, 255): (125, 125, 125, 255)},     # grey fence
    'column1':   {(184, 184, 184, 255): (125, 125, 125, 255)},     # stone column
    'mushroom1': {(184, 0, 0, 255):     (176, 46, 38, 255),        # red cap
                  (220, 220, 220, 255): (232, 236, 238, 255)},     # stem
    'planter1':  {(100, 204, 0, 255):   (96, 168, 60, 255),        # leaves
                  (152, 152, 100, 255): (103, 82, 49, 255)},       # khaki -> Log
    'trellis':   {(236, 236, 236, 255): (232, 236, 238, 255),      # white arch
                  (0, 100, 0, 255):     (86, 128, 40, 255)},
    'stage':     {(168, 168, 168, 255): (125, 125, 125, 255),      # grey deck -> Stone
                  (184, 184, 184, 255): (232, 236, 238, 255),      # light trim
                  (204, 252, 252, 255): (200, 220, 228, 255)},     # cyan backdrop -> Glass
    'playgrnd1': {(152, 100, 0, 255):  (156, 127, 78, 255),        # timber frame -> Plank
                  (48, 152, 100, 255):  (86, 128, 40, 255),        # green -> WoolGreen
                  (100, 48, 0, 255):    (103, 82, 49, 255)},       # dark wood -> Log
    'grill':     {(68, 68, 68, 255):    (32, 32, 38, 255),         # dark BBQ -> WoolBlack
                  (116, 116, 116, 255): (110, 110, 110, 255)},     # legs -> Cobble
    'campfire':  {(152, 100, 0, 255):   (103, 82, 49, 255),        # logs -> Log
                  (100, 48, 0, 255):    (103, 82, 49, 255),
                  (136, 136, 136, 255): (125, 125, 125, 255)},
    'dogstand':  {(48, 152, 204, 255):  (200, 220, 228, 255),      # glass
                  (252, 252, 48, 255):  (234, 195, 55, 255)},      # yellow sign
    'rubbish1':  {(152, 100, 0, 255):   (103, 82, 49, 255)},       # brown litter
    'table3':    {(168, 1, 10, 255):    (176, 46, 38, 255)},       # red tabletop
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
    # --- batch 3 (2026-10-02, slice-audited) ---
    'sidewalk2': dict(swap=False, sub=1),  # 16x16x1 flat pavement
    'curb2':     dict(swap=False, sub=1),  # 16x16x2 green curb
    # crosswalk skipped: 46-long flat strip, X/Y ambiguous
    'sign1':     dict(swap=True,  sub=1),  # 4x1x19 road sign H=19 (y-guard exempt, like tree2)
    'sign5':     dict(swap=True,  sub=1),  # 5x2x19 H=19 (y-guard exempt)
    'chair1':    dict(swap=False, sub=1),  # 4x5x5 seat
    'table1':    dict(swap=False, sub=1),  # 10x10x7 table
    'cart1':     dict(swap=False, sub=1),  # 9x12x8 cart
    'cart2':     dict(swap=False, sub=1),  # 9x12x8 red cart
    'cart1a':    dict(swap=False, sub=1),  # 9x12x8 blue cart
    'busstop':   dict(swap=True,  sub=2),  # 40x20x20 shelter -> 20x10x10
    'fountain':  dict(swap=False, sub=2),  # 20x21x20 basin /2
    'statue1':   dict(swap=True,  sub=2),  # 16x16x17 figure -> 8x9x8
    'mailbox2':  dict(swap=True,  sub=1),  # 5x11x13 Z-up
    'newsbox2':  dict(swap=True,  sub=1),  # 8x7x11 Z-up
    'trashcan2': dict(swap=True,  sub=1),  # 6x5x12 Z-up
    'stlight1':  dict(swap=True,  sub=2),  # 38x4x32 like stlight
    'trlight1':  dict(swap=True,  sub=1),  # 6x6x8 lamp box
    'container1':dict(swap=False, sub=2),  # 60x15x15 shipping crate /2
    'fence1':    dict(swap=False, sub=1),  # 31x2x18 long fence (Y-up)
    'column1':   dict(swap=True,  sub=2),  # 20x21x20 column -> 10x10x10
    'mushroom1': dict(swap=True,  sub=1),  # tiny red mushroom
    'planter1':  dict(swap=True,  sub=1),  # like planter
    'trellis':   dict(swap=True,  sub=2),  # 18x17x19 arch /2
    'stage':     dict(swap=False, sub=1),  # 26x14x7 stage platform
    'playgrnd1': dict(swap=True,  sub=1),  # 35x7x16 climbing frame
    'grill':     dict(swap=False, sub=1),  # 7x5x7 BBQ
    'campfire':  dict(swap=False, sub=1),  # 8x8x7 logs+fire
    'dogstand':  dict(swap=True,  sub=2),  # 40x20x20 stand -> 20x10x10
    'rubbish1':  dict(swap=False, sub=1),  # 5x6x2 litter pile
    # table3 skipped: ambiguous tall profile
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
    # ground the model: drop empty bottom layers so base layer is non-empty
    if vox:
        minY = min(p[1] for p in vox)
        if minY > 0:
            vox = {(x, y - minY, z): c for (x, y, z), c in vox.items()}
    npal = [p0 if p0 is None else irole.get(p0, ROLE.get(p0, p0)) for p0 in pal]
    save(os.path.join(DST, n + '.bytes'), (W0, H0, D0), vox, npal)
    cc = Counter(npal[c] for c in vox.values() if c < len(npal) and npal[c])
    print(f'{n}: -> {W0}x{H0}x{D0} H<=16:{H0<=16} | ' + ', '.join(f'{c}:{k}' for c, k in cc.most_common(4)))
print('done')
