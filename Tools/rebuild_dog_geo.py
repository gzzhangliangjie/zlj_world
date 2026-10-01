# -*- coding: utf-8 -*-
"""从 vox 源按解剖区域重建狗的 geo(含头/耳/鼻/颈/身/腿/尾)+ UV 打包 + 双变体贴图。

vox 网格:grid[vx, vy, vz],尺寸 (6, 13, 7)
geo 轴映射(实证):geo(x,y,z) = vox(x, z, y)
解剖分区(geo z = vox y 身长方向):
  snout z0 / head z1-2 / neck z3 / body z4-10 / tail z11-12
  腿 = body 区内 geo y<=1 的体素,按 x/z 象限分 leg0..3
"""
import struct
import json
import io
import numpy as np
from PIL import Image

ATLAS = 128


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


# ---- geo 坐标网格 (gx 0..5, gy 0..6, gz 0..12) ----
def to_geo_grid(grid):
    X, Y, Z = grid.shape[:3]          # vox (6,13,7)
    occ = np.zeros((X, Z, Y), dtype=bool)   # geo (x, y=vox z, z=vox y)
    col = {}
    for vx in range(X):
        for vy in range(Y):
            for vz in range(Z):
                c = tuple(int(v) for v in grid[vx, vy, vz])
                if c != (0, 0, 0):
                    occ[vx, vz, vy] = True
                    col[(vx, vz, vy)] = c
    return occ, col


def greedy_boxes(occ, mask):
    """mask 内做 3D 贪婪盒分解,返回 [(x0,y0,z0,w,h,d)]"""
    seen = np.zeros_like(mask, dtype=bool)
    boxes = []
    xs, ys, zs = np.where(mask & ~seen)
    coords = [(int(a), int(b), int(c)) for a, b, c in zip(xs, ys, zs)]
    for x0, y0, z0 in sorted(coords):
        if seen[x0, y0, z0]:
            continue
        w = 1
        while (x0 + w < mask.shape[0] and mask[x0 + w, y0, z0] and not seen[x0 + w, y0, z0]):
            w += 1
        h = 1
        while (y0 + h < mask.shape[1] and all(
                mask[x0 + dx, y0 + h, z0] and not seen[x0 + dx, y0 + h, z0] for dx in range(w))):
            h += 1
        d = 1
        while (z0 + d < mask.shape[2] and all(
                mask[x0 + dx, y0 + dy, z0 + d] and not seen[x0 + dx, y0 + dy, z0 + d]
                for dx in range(w) for dy in range(h))):
            d += 1
        for dx in range(w):
            for dy in range(h):
                for dz in range(d):
                    seen[x0 + dx, y0 + dy, z0 + dz] = True
        boxes.append((x0, y0, z0, w, h, d))
    return boxes


class ShelfPacker:
    def __init__(self, limit=ATLAS):
        self.limit = limit
        self.x = self.y = self.h = 0

    def alloc(self, w, h):
        if w > self.limit or h > self.limit:
            raise ValueError('face too big')
        if self.x + w > self.limit:
            self.y += self.h
            self.x = 0
            self.h = 0
        r = (self.x, self.y)
        self.x += w
        self.h = max(self.h, h)
        if self.y + self.h > self.limit:
            raise ValueError('atlas overflow')
        return r


def build():
    brown = load_vox('_refs/mmmm/vox/mob_dog2.vox')
    urban = load_vox('_refs/mmmm/vox/mob_dog1.vox')
    occ, _ = to_geo_grid(brown)          # 主形状用棕狗(212 体素)
    GX, GY, GZ = occ.shape
    assert (GX, GY, GZ) == (6, 7, 13), occ.shape

    regions = {                          # geo z 分区
        'head': (0, 3), 'neck': (3, 4), 'body': (4, 11), 'tail': (11, 13),
    }
    bones = {}                           # name -> [(x0,y0,z0,w,h,d)]
    for name, (z0, z1) in regions.items():
        m = np.zeros_like(occ)
        m[:, :, z0:z1] = occ[:, :, z0:z1]
        for b in greedy_boxes(occ, m):
            x0, y0, zb0, w, h, d = b
            if name == 'body' and y0 <= 1:            # 腿:体区内的低位柱
                left = x0 < GX // 2
                front = z0 + d / 2 <= 7
                lk = ('leg0' if left else 'leg1') if front else ('leg2' if left else 'leg3')
                bones.setdefault(lk, []).append(b)
            else:
                bones.setdefault(name, []).append(b)

    # ---- UV 打包 + geo 写出 ----
    packer = ShelfPacker()
    bone_json = []
    pivots = {'head': [3, 3, 3], 'neck': None, 'body': [3, 4, 7],
              'tail': [3, 4, 11],
              'leg0': [0.5, 2, 5], 'leg1': [5.5, 2, 5],
              'leg2': [0.5, 2, 10], 'leg3': [5.5, 2, 10]}
    order = ['body', 'head', 'neck', 'tail', 'leg0', 'leg1', 'leg2', 'leg3']
    cube_all = []
    for bn in order:
        cubes = bones.get(bn)
        if not cubes:
            continue
        cjson = []
        for (x0, y0, z0, w, h, d) in cubes:
            faces = {'north': (w, h), 'south': (w, h), 'up': (w, d), 'down': (w, d),
                     'east': (d, h), 'west': (d, h)}
            uv = {}
            for f, (fw, fh) in faces.items():
                u, v = packer.alloc(fw, fh)
                uv[f] = {'uv': [u, v], 'uv_size': [fw, fh]}
            cjson.append({'origin': [x0, y0, z0], 'size': [w, h, d], 'uv': uv})
            cube_all.append((x0, y0, z0, w, h, d, uv))
        bone = {'name': bn, 'pivot': pivots[bn], 'cubes': cjson}
        if bn == 'neck':                 # 颈并入 body 骨(不单独动)
            bone_json.insert(0, bone)
        else:
            bone_json.append(bone)
    geo = {'format_version': '1.16.0',
           'minecraft:geometry': [{'description': {
               'identifier': 'geometry.dog', 'texture_width': ATLAS, 'texture_height': ATLAS,
               'visible_bounds_width': 4, 'visible_bounds_height': 3, 'visible_bounds_offset': [0, 1, 0]},
               'bones': bone_json}]}
    json.dump(geo, open('VoxelCraft/Assets/Resources/Geo/dog.geo.json', 'w', encoding='utf-8'), indent=1)

    # ---- 双变体逐体素上色 ----
    for variant, grid in [('default', brown), ('urban', urban)]:
        occ_v, col_v = to_geo_grid(grid)
        img = Image.new('RGB', (ATLAS, ATLAS), (154, 154, 154))
        px = img.load()

        def sample(gx, gy, gz):
            # geo -> vox: vx=gx, vy=gz, vz=gy
            return col_v.get((gx, gy, gz))

        painted = 0
        for (x0, y0, z0, w, h, d, uv) in cube_all:
            for face, (axis, fixed, ulen, vlen) in {
                'up':    ('y', y0 + h - 1, w, d),
                'down':  ('y', y0, w, d),
                'north': ('z', z0, w, h),
                'south': ('z', z0 + d - 1, w, h),
                'east':  ('x', x0 + w - 1, d, h),
                'west':  ('x', x0, d, h),
            }.items():
                fd = uv[face]
                u, v = int(fd['uv'][0]), int(fd['uv'][1])
                tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                for vv in range(th):
                    for uu in range(tw):
                        fu = uu / (tw - 1) if tw > 1 else 0.0
                        fv = vv / (th - 1) if th > 1 else 0.0
                        if axis == 'y':
                            gx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                            gz = z0 + int(round(fv * (vlen - 1))) if vlen > 1 else z0
                            c = sample(gx, fixed, gz)
                        elif axis == 'z':
                            gx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                            gy = y0 + h - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                            c = sample(gx, gy, fixed)
                        else:
                            gz = z0 + int(round(fu * (ulen - 1))) if ulen > 1 else z0
                            gy = y0 + h - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                            c = sample(fixed, gy, gz)
                        if c is not None:
                            px[u + uu, v + vv] = c
                            painted += 1
        name = 'dog_skin' if variant == 'default' else 'dog_urban_skin'
        buf = io.BytesIO()
        img.save(buf, format='PNG')
        open(f'VoxelCraft/Assets/Resources/Textures/{name}.png.bytes', 'wb').write(buf.getvalue())
        img.save(f'_logs/{name}_preview.png')
        print(variant, 'painted', painted, 'texels; cubes/bone:',
              {b: len(c) for b, c in bones.items() if c})


if __name__ == '__main__':
    build()
