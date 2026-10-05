# r96 .vox → 多 bone .bbmodel:去烟、提亮黑、轮/杆独立 bone
import json, base64, io, uuid as _u, struct, sys
from collections import deque
from PIL import Image
import numpy as np


def _uuid(tag):
    return str(_u.uuid5(_u.NAMESPACE_DNS, 'steamvox96:' + tag))


def img_to_b64(img):
    buf = io.BytesIO()
    img.save(buf, format='PNG')
    return base64.b64encode(buf.getvalue()).decode()


def parse_vox(path):
    data = open(path, 'rb').read()
    off = 8
    assert data[off:off+4] == b'MAIN'
    off += 12
    size = None; voxels = []; palette = []
    while off + 12 <= len(data):
        cid = data[off:off+4].decode('latin1')
        clen, ch = struct.unpack('<II', data[off+4:off+12])
        body = off + 12
        if cid == 'SIZE':
            size = struct.unpack('<III', data[body:body+12])
        elif cid == 'XYZI':
            n = struct.unpack('<I', data[body:body+4])[0]
            for i in range(n):
                x, y, z, ci = data[body+4+i*4:body+4+i*4+4]
                voxels.append((x, y, z, ci))
        elif cid == 'RGBA':
            for i in range(256):
                palette.append(tuple(data[body+i*4:body+i*4+4]))
        off = body + clen
    return size, voxels, palette


def domains_of(mask):
    X, Y, Z = mask.shape
    seen = np.zeros_like(mask, bool)
    doms = []
    D = [(1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1),
         (1,1,0),(1,-1,0),(-1,1,0),(-1,-1,0),(1,0,1),(1,0,-1),
         (-1,0,1),(-1,0,-1),(0,1,1),(0,1,-1),(0,-1,1),(0,-1,-1)]
    for sx, sy, sz in zip(*np.nonzero(mask)):
        if seen[sx, sy, sz]:
            continue
        q = deque([(sx, sy, sz)]); seen[sx, sy, sz] = True; cells = []
        while q:
            x, y, z = q.popleft(); cells.append((x, y, z))
            for dx, dy, dz in D:
                nx, ny, nz = x+dx, y+dy, z+dz
                if 0 <= nx < X and 0 <= ny < Y and 0 <= nz < Z and mask[nx, ny, nz] and not seen[nx, ny, nz]:
                    seen[nx, ny, nz] = True; q.append((nx, ny, nz))
        doms.append(cells)
    return doms


def greedy_boxes(bone_mask, color):
    """bone_mask 内指定颜色的贪心同色盒"""
    X, Y, Z = bone_mask.shape
    m = bone_mask & (color_arr == color)
    visited = np.zeros_like(m, bool)
    boxes = []
    for x, y, z in zip(*np.nonzero(m)):
        if visited[x, y, z]:
            continue
        x2 = x
        while x2+1 < X and m[x2+1, y, z] and not visited[x2+1, y, z]:
            x2 += 1
        y2 = y
        while y2+1 < Y and m[x:x2+1, y2+1, z].all() and not visited[x:x2+1, y2+1, z].any():
            y2 += 1
        z2 = z
        while z2+1 < Z and m[x:x2+1, y:y2+1, z2+1].all() and not visited[x:x2+1, y:y2+1, z2+1].any():
            z2 += 1
        visited[x:x2+1, y:y2+1, z:z2+1] = True
        boxes.append((x, y, z, x2, y2, z2))
    return boxes


size, voxels, palette = parse_vox('C:/Users/zlj10/AppData/Local/hermes/cache/scratch/steam_r96.vox')
X, Z, Y = size
color_arr = np.zeros((X, Y, Z), np.int16)
for x, z, y, ci in voxels:
    color_arr[x, y, z] = ci
print('grid', X, Y, Z, 'vox', len(voxels))

# 颜色重映射:黑提亮,烟删除
SMOKE = 5
REMAP = {1: (38, 38, 43)}     # 030303 -> 26262b
keep_colors = [c for c in sorted(set(v[3] for v in voxels)) if c != SMOKE]
slots = {c: i for i, c in enumerate(keep_colors)}
img = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
for c, i in slots.items():
    rgb = REMAP.get(c, palette[c-1][:3])
    img.putpixel((i % 16, i // 16), (*rgb, 255))
print('colors after remap:', ['#%02x%02x%02x' % (REMAP.get(c, palette[c-1][:3])) for c in keep_colors])

# bone 划分
bone_of = {}          # voxel -> bone name
# 车轮:红色(color2)低位的连通域,按 z 分前/中/煤水
red_dom = domains_of(color_arr == 2)
wheels = {'wheels_front': [], 'wheels_mid': [], 'wheels_tender': []}
for cells in red_dom:
    ys = [c[1] for c in cells]; zs = [c[2] for c in cells]
    if max(ys) <= 18:                       # 低位=轮
        zc = sum(zs)/len(zs)
        name = 'wheels_front' if zc < 30 else ('wheels_mid' if zc < 55 else 'wheels_tender')
        wheels[name].extend(cells)
    else:                                    # 高位红件=饰条归 body
        pass
for n, cells in wheels.items():
    if cells:
        print(n, len(cells))
    for c in cells:
        bone_of[c] = n
# 连杆:灰 color4
for c in zip(*np.nonzero(color_arr == 4)):
    bone_of[(int(c[0]), int(c[1]), int(c[2]))] = 'rods'
print('rods', int((color_arr == 4).sum()))

# bone -> bool mask
bone_masks = {}
allvox = set()
for x, y, z in zip(*np.nonzero(color_arr)):
    if color_arr[x, y, z] == SMOKE:
        continue
    allvox.add((int(x), int(y), int(z)))
    b = bone_of.get((int(x), int(y), int(z)), 'body')
    bone_masks.setdefault(b, []).append((int(x), int(y), int(z)))
print('smoke removed', int((color_arr == 5).sum()), '; bones:', {k: len(v) for k, v in bone_masks.items()})

# 每个 bone 独立贪心盒
TEX_UUID = _uuid('tex0')
elements = []
outliner = []
for bname, cells in bone_masks.items():
    bm = np.zeros((X, Y, Z), bool)
    for (x, y, z) in cells:
        bm[x, y, z] = True
    cnt = 0
    for ci in keep_colors:
        for (x, y, z, x2, y2, z2) in greedy_boxes(bm, ci):
            u = (slots[ci] % 16) + 0.5
            v = (slots[ci] // 16) + 0.5
            elements.append({
                'name': '%s_%03d' % (bname, cnt),
                'box_uv': False, 'type': 'cube',
                'uuid': _uuid('el_%s_%03d' % (bname, cnt)),
                'from': [int(x - X//2), int(y), int(z)],
                'to': [int(x2+1 - X//2), int(y2+1), int(z2+1)],
                'faces': {d: {'uv': [u, v, u, v], 'texture': TEX_UUID}
                          for d in ('north', 'east', 'south', 'west', 'up', 'down')},
            })
            cnt += 1
    outliner.append({'name': bname, 'uuid': _uuid('grp_' + bname), 'origin': [0, 0, 0], 'children': []})
    print(bname, 'cubes', cnt)

# 填 children
name2out = {g['name']: g for g in outliner}
for e in elements:
    name2out[e['name'].rsplit('_', 1)[0]]['children'].append(e['uuid'])

model = {
    'meta': {'format_version': '4.5', 'model_format': 'bedrock', 'box_uv': False},
    'name': 'steam_r96', 'geometry_name': 'steam_r96',
    'visible_box': [1, 1, 0],
    'resolution': {'width': 16, 'height': 16},
    'elements': elements,
    'outliner': outliner,
    'textures': [{'name': 'voxel_palette.png', 'id': '0', 'particle': False,
                  'source': 'data:image/png;base64,' + img_to_b64(img),
                  'uuid': TEX_UUID, 'saved': False}],
}
json.dump(model, open('C:/Users/zlj10/AppData/Local/hermes/cache/scratch/steam_r96_bones.bbmodel', 'w'))
print('total cubes', len(elements), 'bones', len(outliner))
