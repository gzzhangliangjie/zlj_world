# 批量 vox → bbmodel:转轴(x↔z)、去悬空烟、提亮近黑、贴地异色件拆 wheels bone
import json, base64, io, uuid as _u, struct, sys, glob, os
from collections import deque
from PIL import Image
import numpy as np


def _uuid(tag):
    return str(_u.uuid5(_u.NAMESPACE_DNS, 'batchvox:' + tag))


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


def process(uid):
    import os as _os
    vox_path = 'C:/Users/zlj10/AppData/Local/hermes/cache/scratch/batch_%s_r%s.vox' % (uid, _os.environ.get('VOXRES','32'))
    size, voxels, pal = parse_vox(vox_path)
    X, Z, Y = size
    col = np.zeros((X, Y, Z), np.int16)
    for x, z, y, ci in voxels:
        col[x, y, z] = ci
    # 转轴:长边应在 z
    if X > Z:
        col = np.transpose(col, (2, 1, 0))
        X, Z = Z, X
    # mirror-fill:x(宽)轴镜像补全,车左右对称
    col = np.maximum(col, col[::-1, :, :])
    # 去悬空烟:与主体有明确气隙的上方独立域
    Yh = col.shape[1]
    occ_any = col > 0
    # 主体 = 最大连通域
    doms = domains_of(occ_any)
    main_dom = max(doms, key=len)
    main_set = set(main_dom)
    main_top = max(c[1] for c in main_dom)
    smoke_mask = np.zeros_like(occ_any)
    for cells in doms:
        if cells is main_dom or set(cells) == main_set:
            continue
        ys = [c[1] for c in cells]
        if min(ys) > main_top:            # 完全悬空于主体顶之上
            for c in cells:
                smoke_mask[c] = True
    # 提亮近黑 & 重映射颜色
    colors = sorted(set(int(v) for v in col.flatten() if v > 0))
    REMAP = {}
    for ci in colors:
        r, g, b = pal[ci-1][:3]
        if max(r, g, b) <= 20:              # 近黑
            REMAP[ci] = (38, 38, 43)
    keep = [ci for ci in colors if not smoke_mask.any() or True]
    # 烟色=仅存在于 smoke_mask 的颜色
    smoke_colors = set()
    if smoke_mask.any():
        for ci in colors:
            m = (col == ci) & smoke_mask
            m_all = (col == ci)
            if m.sum() > 0 and m.sum() == m_all.sum():
                smoke_colors.add(ci)
    keep = [ci for ci in colors if ci not in smoke_colors]
    slots = {ci: i for i, ci in enumerate(keep)}
    img = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
    for ci, i in slots.items():
        rgb = REMAP.get(ci, pal[ci-1][:3])
        img.putpixel((i % 16, i // 16), (*rgb, 255))
    col_kept = np.where(np.isin(col, keep) & ~smoke_mask, col, 0)
    # wheels bone:贴地(域内 min_y<=2)且非最大色的连通域
    main_color = max(keep, key=lambda ci: int((col_kept == ci).sum()))
    bone_of = {}
    for cells in domains_of(col_kept > 0):
        ys = [c[1] for c in cells]
        cs = [col_kept[c] for c in cells]
        from collections import Counter
        dom_color = Counter(cs).most_common(1)[0][0]
        if min(ys) <= 2 and dom_color != main_color and len(cells) >= 8:
            for c in cells:
                bone_of[c] = 'wheels'
    bones = {}
    for x, y, z in zip(*np.nonzero(col_kept)):
        b = bone_of.get((int(x), int(y), int(z)), 'body')
        bones.setdefault(b, np.zeros(col_kept.shape, bool))[x, y, z] = True
    # 贪心盒 + bbmodel
    X2, Y2, Z2 = col_kept.shape
    TEX_UUID = _uuid(uid + '_tex')
    elements = []
    outliner = []
    for bname, bm in bones.items():
        cnt = 0
        for ci in keep:
            m = bm & (col_kept == ci)
            visited = np.zeros_like(m, bool)
            for x, y, z in zip(*np.nonzero(m)):
                if visited[x, y, z]:
                    continue
                x2 = x
                while x2+1 < X2 and m[x2+1, y, z] and not visited[x2+1, y, z]:
                    x2 += 1
                y2 = y
                while y2+1 < Y2 and m[x:x2+1, y2+1, z].all() and not visited[x:x2+1, y2+1, z].any():
                    y2 += 1
                z2 = z
                while z2+1 < Z2 and m[x:x2+1, y:y2+1, z2+1].all() and not visited[x:x2+1, y:y2+1, z2+1].any():
                    z2 += 1
                visited[x:x2+1, y:y2+1, z:z2+1] = True
                u = (slots[ci] % 16) + 0.5
                v = (slots[ci] // 16) + 0.5
                elements.append({
                    'name': '%s_%03d' % (bname, cnt),
                    'box_uv': False, 'type': 'cube',
                    'uuid': _uuid('%s_%s_%03d' % (uid, bname, cnt)),
                    'from': [int(x - X2//2), int(y), int(z)],
                    'to': [int(x2+1 - X2//2), int(y2+1), int(z2+1)],
                    'faces': {d: {'uv': [u, v, u, v], 'texture': TEX_UUID}
                              for d in ('north', 'east', 'south', 'west', 'up', 'down')},
                })
                cnt += 1
        outliner.append({'name': bname, 'uuid': _uuid('%s_%s' % (uid, bname)), 'origin': [0, 0, 0], 'children': [e['uuid'] for e in elements if e['name'].startswith(bname + '_')]})
    model = {
        'meta': {'format_version': '4.5', 'model_format': 'bedrock', 'box_uv': False},
        'name': uid, 'geometry_name': uid,
        'visible_box': [1, 1, 0],
        'resolution': {'width': 16, 'height': 16},
        'elements': elements,
        'outliner': outliner,
        'textures': [{'name': 'voxel_palette.png', 'id': '0', 'particle': False,
                      'source': 'data:image/png;base64,' + img_to_b64(img),
                      'uuid': TEX_UUID, 'saved': False}],
    }
    out = 'C:/Users/zlj10/AppData/Local/hermes/cache/scratch/bb_%s_r%s.bbmodel' % (uid, _os.environ.get('VOXRES','32'))
    json.dump(model, open(out, 'w'))
    n_smoke = int((smoke_mask & (col > 0)).sum())
    n_wheel = int(sum((v).sum() for k, v in bones.items() if k == 'wheels'))
    print('%s: grid %dx%dx%d cubes %d bones %s smoke_removed %d wheels_vox %d colors %d' % (
        uid, X2, Y2, Z2, len(elements), list(bones.keys()), n_smoke, n_wheel, len(keep)))
    return out


if __name__ == '__main__':
    for uid in sys.argv[1:]:
        process(uid)
