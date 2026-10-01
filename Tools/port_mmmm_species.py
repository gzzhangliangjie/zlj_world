# -*- coding: utf-8 -*-
"""mmmm vox 生物 -> VoxelCraft 物种(geo 骨骼 + UV + 增强贴图)。

解剖分区管线(狗/猫/熊/企鹅实证):
  vox (x=宽, y=长, z=高);geo(x,y,z) = vox(x, z, y)。
  水平四足(猫/熊/狗):分区按 vox y(geo z);垂直鸟形(企鹅):分区按 vox z(geo y)。
  规则优先级:tail_rule(vox 谓词)> leg(geo y<=max)> 区域分区;腿埋进身体底层防断连。
  x 归中:模型偏置网格时平移到中央(猫实证 x0-3 -> 网格 8)。
增强贴图:五阶噪点 + 顶亮底暗 + 浅肚 + 深爪;眼/鼻深色(<60)原样保留。
"""
import struct
import json
import io
import sys
import numpy as np
from PIL import Image

ATLAS = 128
FACE_ID = {'up': 1, 'down': 2, 'north': 3, 'south': 4, 'east': 5, 'west': 6}


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


def to_geo_grid(grid):
    """vox(x,y,z) -> geo(gx=x, gy=vox z, gz=vox y)。"""
    X, Y, Z = grid.shape[:3]
    occ = np.zeros((X, Z, Y), dtype=bool)
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
    seen = np.zeros_like(mask, dtype=bool)
    boxes = []
    xs, ys, zs = np.where(mask & ~seen)
    coords = [(int(a), int(b), int(c)) for a, b, c in zip(xs, ys, zs)]
    for x0, y0, z0 in sorted(coords):
        if seen[x0, y0, z0]:
            continue
        w = 1
        while x0 + w < mask.shape[0] and mask[x0 + w, y0, z0] and not seen[x0 + w, y0, z0]:
            w += 1
        h = 1
        while y0 + h < mask.shape[1] and all(
                mask[x0 + dx, y0 + h, z0] and not seen[x0 + dx, y0 + h, z0] for dx in range(w)):
            h += 1
        d = 1
        while z0 + d < mask.shape[2] and all(
                mask[x0 + dx, y0 + dy, z0 + d] and not seen[x0 + dx, y0 + dy, z0 + d]
                for dx in range(w) for dy in range(h)):
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


def enhance(c, face, bone, sx, sy, sz, uu, vv):
    r, g, b = c
    if r < 60 and g < 60 and b < 60:
        return c                      # 眼/鼻/爪深色保留
    h = (sx * 73856093) ^ (sz * 19349663) ^ (sy * 83492791) \
        ^ (uu * 2654435761) ^ (vv * 40503) ^ (FACE_ID[face] * 918731)
    noise = (-14, -7, 0, 7, 12)[h % 5]
    if face == 'up':
        noise += 10
    elif face == 'down':
        noise -= 22
    if bone in ('body', 'neck') and face in ('north', 'south', 'east', 'west') and sy <= 3:
        r, g, b = min(255, r + 26), min(255, g + 24), min(255, b + 18)
    if bone.startswith('leg') and face in ('north', 'south', 'east', 'west') and sy == 0:
        noise -= 20
    return (max(0, min(255, r + noise)),
            max(0, min(255, g + noise)),
            max(0, min(255, b + noise)))


# ------------- 每物种解剖配置(vox 切片实证) -------------
# regions: name -> (axis, lo, hi)  axis='y' 水平四足按身长 / 'z' 垂直鸟形按高度
# leg_geo_y_max: 腿柱最高 geo y(vox z <= 此值为腿)
# front_split: 前腿 geo z(vox y)< 此值
# tail_rule(vox 坐标谓词 vx,vy,vz): 优先于腿规则划入尾
SPECIES = {
    'cat': dict(
        voxfile='mob_cat3.vox',
        regions={'head': ('y', 0, 5), 'neck': ('y', 5, 5), 'body': ('y', 5, 12)},
        leg_geo_y_max=0, front_split=6,
        tail_rule=lambda vx, vy, vz: vy >= 13 or (vy == 12 and vz >= 8),
    ),
    'bear': dict(
        voxfile='mob_bear.vox',
        regions={'head': ('y', 0, 8), 'neck': ('y', 8, 9), 'body': ('y', 9, 19)},
        leg_geo_y_max=2, front_split=13,
        tail_rule=lambda vx, vy, vz: vy >= 19,
    ),
    'penguin': dict(
        voxfile='mob_penguin.vox',
        regions={'head': ('z', 6, 9), 'body': ('z', 1, 6)},
        leg_geo_y_max=1, front_split=3,
        tail_rule=lambda vx, vy, vz: vy >= 5 and vz <= 1 and 2 <= vx <= 3,
    ),
}


def build_species(sp, cfg):
    grid = load_vox(f'_refs/mmmm/vox/{cfg["voxfile"]}')
    occ, col = to_geo_grid(grid)
    GX, GY, GZ = occ.shape

    # x 归中(猫模型偏置网格左半)
    xs = np.where(occ.any(axis=(1, 2)))[0]
    shift = int((GX - (xs.max() + 1 - xs.min())) // 2 - xs.min())
    if shift:
        occ = np.roll(occ, shift, axis=0)
        col = {(gx + shift, gy, gz): c for (gx, gy, gz), c in col.items()}
        xs = np.where(occ.any(axis=(1, 2)))[0]
    print(f'{sp}: x recenter {shift:+d} -> [{xs.min()},{xs.max()}]')

    # 尾规则优先(vox 坐标):tail_extra 在 geo 坐标
    tail_extra = np.zeros_like(occ)
    tr = cfg.get('tail_rule')
    if tr:
        for (gx, gy, gz), c in col.items():      # gz=vox y, gy=vox z, gx=vox x
            if tr(gx, gz, gy):
                tail_extra[gx, gy, gz] = True

    # 腿规则:geo y(vox z) <= leg_geo_y_max,排除尾
    leg_mask = (occ & (np.arange(GY)[None, :, None] <= cfg['leg_geo_y_max'])) & ~tail_extra

    bones = {}
    xmid = GX / 2
    for b in greedy_boxes(occ, leg_mask):
        x0, y0, z0, w, h, d = b
        front = z0 + d / 2 <= cfg['front_split']
        # 盒子跨中线(x0 < xmid < x0+w)时拆成左右两半,避免后腿并成一条
        parts = []
        if x0 < xmid < x0 + w:
            split = int(xmid - x0) if xmid > x0 else 1
            if 0 < split < w:
                parts = [(x0, split), (x0 + split, w - split)]
        if not parts:
            parts = [(x0, w)]
        for (px0, pw) in parts:
            left = px0 < xmid
            lk = ('leg0' if left else 'leg1') if front else ('leg2' if left else 'leg3')
            bones.setdefault(lk, []).append((px0, y0, z0, pw, h, d))

    for name, (axis, lo, hi) in cfg['regions'].items():
        m = occ & ~leg_mask & ~tail_extra
        if axis == 'y':                      # 按 vox y = geo z
            m[:, :, :lo] = False
            m[:, :, hi:] = False
        else:                                # 按 vox z = geo y
            m[:, :lo, :] = False
            m[:, hi:, :] = False
        if not m.any():
            continue
        tgt = 'body' if name == 'neck' else name
        for b in greedy_boxes(occ, m):
            bones.setdefault(tgt, []).append(b)
    if tail_extra.any():
        for b in greedy_boxes(occ, tail_extra & occ):
            bones.setdefault('tail', []).append(b)

    packer = ShelfPacker()
    front_split = cfg['front_split']
    zmid = GZ / 2
    xmid = GX / 2
    leg_y = cfg['leg_geo_y_max']
    pivots = {'head': [xmid, GY - 1, 0.5], 'body': [xmid, GY - 1, zmid],
              'tail': [xmid, GY - 2, GZ - 1.5],
              'leg0': [0.5, leg_y + 1, front_split - 2], 'leg1': [GX - 0.5, leg_y + 1, front_split - 2],
              'leg2': [0.5, leg_y + 1, front_split + 2], 'leg3': [GX - 0.5, leg_y + 1, front_split + 2]}
    order = ['body', 'head', 'tail', 'leg0', 'leg1', 'leg2', 'leg3']
    bone_json, cube_all = [], []
    for bn in order:
        cubes = bones.get(bn)
        if not cubes:
            continue
        cjson = []
        for (x0, y0, z0, w, h, d) in cubes:
            if bn.startswith('leg'):
                y0, h = 0, max(h + y0, 3)     # 埋进身体底层,防摆动断连
            faces = {'north': (w, h), 'south': (w, h), 'up': (w, d), 'down': (w, d),
                     'east': (d, h), 'west': (d, h)}
            uv = {}
            for f, (fw, fh) in faces.items():
                u, v = packer.alloc(fw, fh)
                uv[f] = {'uv': [u, v], 'uv_size': [fw, fh]}
            cjson.append({'origin': [x0, y0, z0], 'size': [w, h, d], 'uv': uv})
            cube_all.append((bn, x0, y0, z0, w, h, d, uv))
        bone_json.append({'name': bn, 'pivot': pivots[bn], 'cubes': cjson})

    geo = {'format_version': '1.16.0',
           'minecraft:geometry': [{'description': {
               'identifier': f'geometry.{sp}', 'texture_width': ATLAS, 'texture_height': ATLAS,
               'visible_bounds_width': 4, 'visible_bounds_height': 3, 'visible_bounds_offset': [0, 1, 0]},
               'bones': bone_json}]}
    json.dump(geo, open(f'VoxelCraft/Assets/Resources/Geo/{sp}.geo.json', 'w', encoding='utf-8'), indent=1)

    img = Image.new('RGB', (ATLAS, ATLAS), (154, 154, 154))
    px = img.load()
    painted = 0
    for (bn, x0, y0, z0, w, h, d, uv) in cube_all:
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
                        sx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                        sy = fixed
                        sz = z0 + int(round(fv * (vlen - 1))) if vlen > 1 else z0
                    elif axis == 'z':
                        sx = x0 + int(round(fu * (ulen - 1))) if ulen > 1 else x0
                        sy = y0 + h - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                        sz = fixed
                    else:
                        sx = fixed
                        sy = y0 + h - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                        sz = z0 + int(round(fu * (ulen - 1))) if ulen > 1 else z0
                    c = col.get((sx, sy, sz))
                    if c is not None:
                        px[u + uu, v + vv] = enhance(c, face, bn, sx, sy, sz, uu, vv)
                        painted += 1
    buf = io.BytesIO()
    img.save(buf, format='PNG')
    open(f'VoxelCraft/Assets/Resources/Textures/{sp}_skin.png.bytes', 'wb').write(buf.getvalue())
    img.save(f'_logs/{sp}_skin_preview.png')
    print(sp, f'geo {GX}x{GY}x{GZ} painted={painted} bones:',
          {b: len(c) for b, c in bones.items() if c})


if __name__ == '__main__':
    only = sys.argv[1:] or list(SPECIES)
    for sp in only:
        build_species(sp, SPECIES[sp])
