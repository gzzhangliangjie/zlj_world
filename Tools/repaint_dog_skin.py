# -*- coding: utf-8 -*-
"""按 vox 逐体素重画狗贴图集 + 重新分骨。

vox 网格轴:grid[vx, vy, vz],尺寸 (X, Y, Z) = (6, 13, 7)
geo 轴映射(由眼睛/鼻尖位置实证):geo(x,y,z) = vox(x, z, y)
  - geo x = vox x(宽 6)
  - geo y = vox z(高 7)
  - geo z = vox y(身长 13)
"""
import struct
import json
import numpy as np
from PIL import Image


def load_vox(path):
    d = open(path, 'rb').read()
    j = d.find(b'XYZI')
    i = d.find(b'RGBA')
    s = d.find(b'SIZE')
    X, Y, Z = struct.unpack('<iii', d[s + 12:s + 24])
    nv = struct.unpack('<I', d[j + 12:j + 16])[0]
    pal = {}
    for k in range(256):
        r, g, b, a = list(d[i + 12 + 4 * k:i + 16 + 4 * k])
        if a > 0:
            pal[k + 1] = (r, g, b)
    grid = np.zeros((X, Y, Z, 3), dtype=np.uint8)
    for k in range(nv):
        x, y, z, c = d[j + 16 + 4 * k], d[j + 16 + 4 * k + 1], d[j + 16 + 4 * k + 2], d[j + 16 + 4 * k + 3]
        col = pal.get(c)
        if col:
            grid[x, y, z] = col
    return grid


def make_sampler(grid):
    X, Y, Z = grid.shape[0], grid.shape[1], grid.shape[2]

    def sample(gx, gy, gz):
        vx, vy, vz = gx, gz, gy  # geo(x,y,z) = vox(x, z, y)
        if 0 <= vx < X and 0 <= vy < Y and 0 <= vz < Z:
            t = grid[vx, vy, vz]
            return (int(t[0]), int(t[1]), int(t[2]))
        return None

    return sample


def regroup_and_paint(species, voxfile):
    grid = load_vox(voxfile)
    sample = make_sampler(grid)
    gp = f'VoxelCraft/Assets/Resources/Geo/{species}.geo.json'
    g = json.load(open(gp, encoding='utf-8'))
    geo = g['minecraft:geometry']
    if isinstance(geo, list):
        geo = geo[0]
    cubes = [c for b in geo['bones'] for c in b.get('cubes', [])]
    for c in cubes:
        c['origin'] = [float(v) for v in c['origin']]
        c['size'] = [float(v) for v in c['size']]

    # ---- regroup bones: body/head/leg0..3 (七骨去 tail,鼻尖/耳归 head) ----
    groups = {'body': [], 'head': [], 'leg0': [], 'leg1': [], 'leg2': [], 'leg3': [], 'tail': []}
    for c in cubes:
        x0, y0, z0 = c['origin']
        sx, sy, sz = c['size']
        z1 = z0 + sz
        if z0 < 1 and y0 >= 2.5 and sy <= 1.5 and sz <= 1.5:
            groups['head'].append(c)          # 鼻尖(z0,高位,小)
        elif y0 >= 5.5 and z0 <= 3.5:
            groups['head'].append(c)          # 耳朵(高位,前段)
        elif z1 <= 3.5 and y0 >= 1.5 and z0 >= 0.5:
            groups['head'].append(c)          # 头部主体
        elif z0 >= 11.5:
            groups['tail'].append(c)          # 尾(z 末端)
        elif y0 <= 1 and sx <= 1.01:
            left = x0 < 3
            front = z0 <= 6.5
            key = ('leg0' if left else 'leg1') if front else ('leg2' if left else 'leg3')
            groups[key].append(c)             # 脚片+腿柱
        else:
            groups['body'].append(c)
    pivots = {'head': [0, 4, 2], 'leg0': [0.5, 2, 5], 'leg1': [5.5, 2, 5],
              'leg2': [0.5, 2, 10], 'leg3': [5.5, 2, 10], 'body': [0, 4, 7], 'tail': [0, 4, 12]}
    geo['bones'] = [{'name': n, 'pivot': pivots[n], 'cubes': groups[n]}
                    for n in ('body', 'head', 'leg0', 'leg1', 'leg2', 'leg3', 'tail') if groups[n]]
    json.dump(g, open(gp, 'w', encoding='utf-8'), indent=1)

    # ---- repaint atlas: 每 texel 按其覆盖的体素真实颜色 ----
    img = Image.new('RGB', (128, 128), (154, 154, 154))
    px = img.load()
    painted = faces = 0
    for b in geo['bones']:
        for c in b.get('cubes', []):
            x0, y0, z0 = [int(v) for v in c['origin']]
            sx, sy, sz = [int(v) for v in c['size']]
            uvf = c.get('uv')
            if not isinstance(uvf, dict):
                continue
            for face, (axis, fixed, ulen, vlen) in {
                'up':    ('y', y0 + sy - 1, sx, sz),
                'down':  ('y', y0, sx, sz),
                'north': ('z', z0, sx, sy),
                'south': ('z', z0 + sz - 1, sx, sy),
                'east':  ('x', x0 + sx - 1, sz, sy),
                'west':  ('x', x0, sz, sy),
            }.items():
                fd = uvf.get(face)
                if not fd:
                    continue
                u, v = int(fd['uv'][0]), int(fd['uv'][1])
                w, h = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                faces += 1
                for vv in range(h):
                    for uu in range(w):
                        fu = uu / (w - 1) if w > 1 else 0.0
                        fv = vv / (h - 1) if h > 1 else 0.0
                        if axis == 'y':
                            gx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                            gz = z0 + int(round(fv * (vlen - 1))) if vlen > 1 else z0
                            col = sample(gx, fixed, gz)
                        elif axis == 'z':
                            gx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                            gy = y0 + sy - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                            col = sample(gx, gy, fixed)
                        else:
                            gz = z0 + int(round(fu * (ulen - 1))) if ulen > 1 else z0
                            gy = y0 + sy - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                            col = sample(fixed, gy, gz)
                        if col is not None and col != (0, 0, 0):
                            px[u + uu, v + vv] = col
                            painted += 1
    import io
    buf = io.BytesIO()
    img.save(buf, format='PNG')
    open(f'VoxelCraft/Assets/Resources/Textures/{species}_skin.png.bytes', 'wb').write(buf.getvalue())
    img.save(f'_logs/{species}_skin_preview.png')
    print(species, 'bones:', {k: len(v) for k, v in groups.items() if v},
          '| painted', painted, 'texels /', faces, 'faces')


if __name__ == '__main__':
    # 默认狗(棕):geo 来自 mob_dog2
    regroup_and_paint('dog', '_refs/mmmm/vox/mob_dog2.vox')
    # urban 变体:同一 dog.geo.json 的 UV,配 mob_dog1 的配色
    grid = load_vox('_refs/mmmm/vox/mob_dog1.vox')
    sample = make_sampler(grid)
    g = json.load(open('VoxelCraft/Assets/Resources/Geo/dog.geo.json', encoding='utf-8'))
    geo = g['minecraft:geometry'][0] if isinstance(g['minecraft:geometry'], list) else g['minecraft:geometry']
    import io
    img = Image.new('RGB', (128, 128), (154, 154, 154))
    px = img.load()
    painted = 0
    for b in geo['bones']:
        for c in b.get('cubes', []):
            x0, y0, z0 = [int(v) for v in c['origin']]
            sx, sy, sz = [int(v) for v in c['size']]
            uvf = c.get('uv')
            if not isinstance(uvf, dict):
                continue
            for face, (axis, fixed, ulen, vlen) in {
                'up':    ('y', y0 + sy - 1, sx, sz),
                'down':  ('y', y0, sx, sz),
                'north': ('z', z0, sx, sy),
                'south': ('z', z0 + sz - 1, sx, sy),
                'east':  ('x', x0 + sx - 1, sz, sy),
                'west':  ('x', x0, sz, sy),
            }.items():
                fd = uvf.get(face)
                if not fd:
                    continue
                u, v = int(fd['uv'][0]), int(fd['uv'][1])
                w, h = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                for vv in range(h):
                    for uu in range(w):
                        fu = uu / (w - 1) if w > 1 else 0.0
                        fv = vv / (h - 1) if h > 1 else 0.0
                        if axis == 'y':
                            gx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                            gz = z0 + int(round(fv * (vlen - 1))) if vlen > 1 else z0
                            col = sample(gx, fixed, gz)
                        elif axis == 'z':
                            gx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                            gy = y0 + sy - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                            col = sample(gx, gy, fixed)
                        else:
                            gz = z0 + int(round(fu * (ulen - 1))) if ulen > 1 else z0
                            gy = y0 + sy - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                            col = sample(fixed, gy, gz)
                        if col is not None and col != (0, 0, 0):
                            px[u + uu, v + vv] = col
                            painted += 1
    buf = io.BytesIO()
    img.save(buf, format='PNG')
    open('VoxelCraft/Assets/Resources/Textures/dog_urban_skin.png.bytes', 'wb').write(buf.getvalue())
    img.save('_logs/dog_urban_skin_preview.png')
    print('dog urban variant painted', painted, 'texels')
