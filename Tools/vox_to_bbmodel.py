# .vox → .bbmodel(贪心同色盒+调色板贴图)→ 载入 Blockbench,可重复任意 RES
import json, base64, io, uuid as _u, struct, sys
from PIL import Image
import numpy as np


def _uuid(tag):
    return str(_u.uuid5(_u.NAMESPACE_DNS, 'steamvox.local:' + tag))


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


def greedy_boxes(occ, colors):
    X, Y, Z = occ.shape
    visited = np.zeros_like(occ, bool)
    boxes = []
    for ci in colors:
        m = (occ == ci)
        for x, y, z in zip(*np.nonzero(m)):
            if visited[x, y, z]:
                continue
            x2 = x
            while x2 + 1 < X and m[x2+1, y, z] and not visited[x2+1, y, z]:
                x2 += 1
            y2 = y
            while y2 + 1 < Y and m[x:x2+1, y2+1, z].all() and not visited[x:x2+1, y2+1, z].any():
                y2 += 1
            z2 = z
            while z2 + 1 < Z and m[x:x2+1, y:y2+1, z2+1].all() and not visited[x:x2+1, y:y2+1, z2+1].any():
                z2 += 1
            visited[x:x2+1, y:y2+1, z:z2+1] = True
            boxes.append((x, y, z, x2, y2, z2, ci))
    return boxes


def vox_to_bbmodel(vox_path, bbmodel_path):
    size, voxels, pal = parse_vox(vox_path)
    X, Z, Y = size          # SIZE=(宽,长,高);体素坐标 (x, z, y)
    occ = np.zeros((X, Y, Z), np.int16)
    for x, z, y, ci in voxels:
        occ[x, y, z] = ci
    colors = sorted(set(int(v) for v in occ.flatten() if v > 0))
    slots = {ci: i for i, ci in enumerate(colors)}
    img = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
    for ci, i in slots.items():
        r, g, b, a = pal[ci-1]
        img.putpixel((i % 16, i // 16), (r, g, b, 255))
    boxes = greedy_boxes(occ, colors)
    TEX_UUID = _uuid('tex0')
    elements = []
    for (x, y, z, x2, y2, z2, ci) in boxes:
        u = (slots[ci] % 16) + 0.5
        v = (slots[ci] // 16) + 0.5
        elements.append({
            'name': 'vox_%s_%03d' % (''.join('%02x' % v_ for v_ in pal[ci-1][:3]), len(elements)),
            'box_uv': False,
            'type': 'cube',
            'uuid': _uuid('el%03d' % len(elements)),
            'from': [int(x - X // 2), int(y), int(z)],
            'to': [int(x2 + 1 - X // 2), int(y2 + 1), int(z2 + 1)],
            'faces': {d_: {'uv': [u, v, u, v], 'texture': TEX_UUID}
                      for d_ in ('north', 'east', 'south', 'west', 'up', 'down')},
        })
    model = {
        'meta': {'format_version': '4.5', 'model_format': 'bedrock', 'box_uv': False},
        'name': 'steam_vox', 'geometry_name': 'steam_vox',
        'visible_box': [1, 1, 0],
        'resolution': {'width': 16, 'height': 16},
        'elements': elements,
        'outliner': [{'name': 'steam_body', 'from': [-8, -8, -8], 'to': [8, 8, 8],
                      'uuid': _uuid('grp_root'), 'origin': [0, 0, 0],
                      'children': [e['uuid'] for e in elements]}],
        'textures': [{'name': 'voxel_palette.png', 'id': '0', 'particle': False,
                      'source': 'data:image/png;base64,' + img_to_b64(img),
                      'uuid': TEX_UUID, 'saved': False}],
    }
    json.dump(model, open(bbmodel_path, 'w'))
    return X, Y, Z, len(boxes), len(colors)


if __name__ == '__main__':
    vp = sys.argv[1]
    bp = sys.argv[2]
    X, Y, Z, nb, nc = vox_to_bbmodel(vp, bp)
    print('grid %dx%dx%d boxes %d colors %d -> %s' % (X, Y, Z, nb, nc, bp))
