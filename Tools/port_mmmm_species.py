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


def enhance(c, face, bone, sx, sy, sz, uu, vv, belly=True):
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
    if belly and bone in ('body', 'neck') and face in ('north', 'south', 'east', 'west') and sy <= 3:
        r, g, b = min(255, r + 26), min(255, g + 24), min(255, b + 18)
    if belly and bone.startswith('leg') and face in ('north', 'south', 'east', 'west') and sy == 0:
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
        regions={'head': ('y', 0, 5), 'neck': ('y', 5, 5), 'body': ('y', 5, 13)},
        leg_geo_y_max=0, front_split=6,
        tail_rule=lambda vx, vy, vz: vy >= 13 or (vy == 12 and vz >= 8),
        leg_y_range=(0, 3, 11, 14),      # 腿只在前后肢段(vox y),身体底层不吃
        leg_lift=2,
    ),
    'bear': dict(
        voxfile='mob_bear.vox',
        regions={'head': ('y', 0, 8), 'neck': ('y', 8, 9), 'body': ('y', 9, 19)},
        leg_geo_y_max=2, front_split=13,
        tail_rule=lambda vx, vy, vz: vy >= 19,
        leg_lift=3,
    ),
    'penguin': dict(
        voxfile='mob_penguin.vox',
        regions={'head': ('z', 6, 9), 'body': ('z', 0, 6)},
        leg_geo_y_max=0, front_split=3,
        tail_rule=lambda vx, vy, vz: vy >= 5 and vz <= 1 and 2 <= vx <= 3,
        leg_x=lambda vx: vx <= 1 or vx >= 4,     # 只有两侧才是脚,身体底层不进腿
        wing_rule=lambda vx, vy, vz: (vx == 0 or vx == 5) and vy <= 2 and vz >= 3,
        ns_swap=True,                      # 白肚/喙/眼在引擎 +Z(前脸):north/south 互换
        leg_lift=0,                        # 腿短:身体落 1,腿只露 1 格短粗蹼
        leg_h=1,                           # 腿=1 格脚掌,贴身下沿,旋转不入体
        emperor=True,
        belly_light=False,
        no_head_bump=True,
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

    # 翅膀规则:x 两侧黑鳍列独立成骨(左右拍动)
    wing_mask = np.zeros_like(occ)
    wr = cfg.get('wing_rule')
    if wr:
        for (gx, gy, gz), c in col.items():
            if wr(gx, gz, gy):
                wing_mask[gx, gy, gz] = True

    # 腿规则:geo y(vox z) <= leg_geo_y_max,排除尾和翅;企鹅仅两侧 x 是脚
    leg_mask = (occ & (np.arange(GY)[None, :, None] <= cfg['leg_geo_y_max'])) & ~tail_extra & ~wing_mask
    lx = cfg.get('leg_x')
    if lx:
        keep = np.zeros_like(leg_mask)
        for gx in range(GX):
            if lx(gx):
                keep[gx, :, :] = leg_mask[gx, :, :]
        leg_mask = keep
    lyr = cfg.get('leg_y_range')            # 腿只在前/后肢段(vox y)
    if lyr:
        lo1, hi1, lo2, hi2 = lyr
        vy_ok = ((np.arange(GZ) >= lo1) & (np.arange(GZ) < hi1)) |                 ((np.arange(GZ) >= lo2) & (np.arange(GZ) < hi2))
        leg_mask &= vy_ok[None, None, :]

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
            # 盒子跨 front_split(geo z)时切前后两半(猫前爪与头底连通的 z 长条)
            zparts = []
            if z0 < cfg['front_split'] < z0 + d:
                zs = int(cfg['front_split'] - z0)
                if 0 < zs < d:
                    zparts = [(z0, zs), (z0 + zs, d - zs)]
            if not zparts:
                zparts = [(z0, d)]
            for (pz0, pd) in zparts:
                front = pz0 + pd / 2 <= cfg['front_split']
                lk = ('leg0' if left else 'leg1') if front else ('leg2' if left else 'leg3')
                bones.setdefault(lk, []).append((px0, y0, pz0, pw, h, pd))

    for name, (axis, lo, hi) in cfg['regions'].items():
        m = occ & ~leg_mask & ~tail_extra & ~wing_mask
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
    if cfg.get('no_head_bump'):               # 去掉头后凸块(喙层 z4 的小薄片)
        bones['head'] = [b for b in bones.get('head', [])
                         if not (b[2] >= 4 and b[5] == 1 and b[0] >= 1)]
    if wing_mask.any():
        for b in greedy_boxes(occ, wing_mask & occ):
            x0, y0, z0, w, h, d = b
            bones.setdefault('wing0' if x0 < GX // 2 else 'wing1', []).append(b)


    packer = ShelfPacker()
    front_split = cfg['front_split']
    zmid = GZ / 2
    xmid = GX / 2
    leg_y = cfg['leg_geo_y_max']
    pivots = {'head': [xmid, GY - 1, 0.5], 'body': [xmid, GY - 1, zmid],
              'tail': [xmid, GY - 2, GZ - 1.5],
              'wing0': [1, GY - 3, 1.5], 'wing1': [GX - 1, GY - 3, 1.5],
              'leg0': [0.5, leg_y + 1, front_split - 2], 'leg1': [GX - 0.5, leg_y + 1, front_split - 2],
              'leg2': [0.5, leg_y + 1, front_split + 2], 'leg3': [GX - 0.5, leg_y + 1, front_split + 2]}
    order = ['body', 'head', 'tail', 'wing0', 'wing1', 'leg0', 'leg1', 'leg2', 'leg3']
    bone_json, cube_all = [], []
    for bn in order:
        cubes = bones.get(bn)
        if not cubes:
            continue
        cjson = []
        lift = cfg.get('leg_lift', 0)
        for (x0, y0, z0, w, h, d) in cubes:
            oy0 = y0                            # 采样基准(未抬升)
            if bn.startswith('leg'):
                y0, h = 0, max(h + y0, cfg.get('leg_h', 3))   # 埋体防断连(企鹅 2=短腿)
                oy0 = 0
            elif lift:
                y0 += lift                     # 身体抬升,腿撑地(站立感)
            faces = {'north': (w, h), 'south': (w, h), 'up': (w, d), 'down': (w, d),
                     'east': (d, h), 'west': (d, h)}
            uv = {}
            for f, (fw, fh) in faces.items():
                u, v = packer.alloc(fw, fh)
                uv[f] = {'uv': [u, v], 'uv_size': [fw, fh]}

            cjson.append({'origin': [x0, y0, z0], 'size': [w, h, d], 'uv': uv})
            cube_all.append((bn, x0, y0, z0, w, h, d, uv, oy0))
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
    for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
        for face, (axis, fixed, ulen, vlen) in {
            'up':    ('y', oy0 + h - 1, w, d),
            'down':  ('y', oy0, w, d),
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
                        sy = oy0 + h - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                        sz = fixed
                    else:
                        sx = fixed
                        sy = oy0 + h - 1 - (int(round(fv * (vlen - 1))) if vlen > 1 else 0)
                        sz = z0 + int(round(fu * (ulen - 1))) if ulen > 1 else z0
                    c = col.get((sx, sy, sz))
                    # 侧面(脸)采色:固定层为空或非腿骨时改射线投影,
                    # 取该 texel 向内第一个非空体素(眼在内层也能投到脸上)
                    # x 侧面强制投影:骨盒边界≠模型边缘(翼列挖走后盒面
                    # 采到内部浅色),采满到真实模型侧缘的黑背

                    if c is None and axis in ('z', 'x'):
                        if axis == 'z':
                            rz = z0 if face == 'north' else z0 + d - 1
                            step = 1 if face == 'north' else -1
                            rz += step
                            while z0 <= rz < z0 + d:
                                c = col.get((sx, sy, rz))
                                if c is not None:
                                    break
                                rz += step
                        else:
                            rx = x0 if face == 'west' else x0 + w - 1
                            step = 1 if face == 'west' else -1
                            rx += step
                            while x0 <= rx < x0 + w:
                                c = col.get((rx, sy, sz))
                                if c is not None:
                                    break
                                rx += step
                    if c is not None:
                        px[u + uu, v + vv] = enhance(c, face, bn, sx, sy, sz, uu, vv, belly=cfg.get('belly_light', True))
                        painted += 1
    if cfg.get('tuft'):
        for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
            if bn == 'head' and oy0 >= 8:
                for face, fd in uv.items():
                    u, v = int(fd['uv'][0]), int(fd['uv'][1])
                    tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                    for vv in range(th):
                        for uu in range(tw):
                            px[u+uu, v+vv] = (32, 32, 32)
    # 企鹅背黑:body/head 侧面+顶面涂黑(帝企鹅黑背,白肚只在前脸)
    if cfg.get('emperor'):
        BLACK = (32, 32, 32)
        for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
            if bn in ('body', 'head', 'tail'):
                faces_bk = ('east', 'west', 'up')
                if bn == 'head':
                    faces_bk = ('east', 'west', 'up', 'north')   # 黑帽兜:顶+侧+后脑,south=白脸
                for face in faces_bk:
                    fd = uv[face]
                    u, v = int(fd['uv'][0]), int(fd['uv'][1])
                    tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                    for vv in range(th):
                        for uu in range(tw):
                            hsh = (uu * 2654435761) ^ (vv * 40503) ^ (FACE_ID[face] * 918731)
                            n = (-10, -5, 0, 5, 8)[hsh % 5]
                            c0 = BLACK
                            px[u+uu, v+vv] = tuple(max(0, min(255, cc + n)) for cc in c0)
    # ns_swap:引擎 +Z(脸)采 south 矩形,而脚本把脸(z0)画进 north——
    # 画完后把每盒 north/south 矩形内容互换,脸内容即落到引擎 +Z
    if cfg.get('ns_swap'):
        for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
            un, vn = int(uv['north']['uv'][0]), int(uv['north']['uv'][1])
            us, vs = int(uv['south']['uv'][0]), int(uv['south']['uv'][1])
            tw, th = int(uv['north']['uv_size'][0]), int(uv['north']['uv_size'][1])
            a = img.crop((un, vn, un+tw, vn+th))
            b = img.crop((us, vs, us+tw, vs+th))
            img.paste(b, (un, vn))
            img.paste(a, (us, vs))
    # 企鹅深色蹼(帝企鹅 dark webbed feet)+ 帝企鹅喙(深灰+下颌粉橙条)
    if cfg.get('emperor'):
        FEET = (248, 148, 122)        # 参考图:粉橙蹼
        BEAK = (242, 140, 40)          # 橙嘴
        STRIPE = (242, 100, 30)        # 下颌深橙条
        for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
            if bn.startswith('leg'):
                for face in ('down', 'north', 'south', 'east', 'west', 'up'):   # 短腿整体橙,露出即蹼
                    fd = uv[face]
                    u, v = int(fd['uv'][0]), int(fd['uv'][1])
                    tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                    for vv in range(th):
                        for uu in range(tw):
                            px[u + uu, v + vv] = FEET
            elif bn == 'head' and d == 1 and w <= 2 and oy0 < 8:
                # 喙盒(前端 2 宽 1 深小盒):面橙,底面=下颌深橙条
                # (ns_swap 后引擎+Z 脸读 south 键,喙面必须涂 south)
                for face in ('south', 'east', 'west', 'up'):
                    fd = uv[face]
                    u, v = int(fd['uv'][0]), int(fd['uv'][1])
                    tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                    for vv in range(th):
                        for uu in range(tw):
                            px[u + uu, v + vv] = BEAK
                fd = uv['down']
                u, v = int(fd['uv'][0]), int(fd['uv'][1])
                tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
                for vv in range(th):
                    for uu in range(tw):
                        px[u + uu, v + vv] = STRIPE
    # 企鹅无眼(原作黑头无眼点):头盒 north face 手工补白眼
    if sp == 'penguin':
        WHITE = (255, 255, 255)
        SOFT = (240, 240, 240)
        for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
            if bn != 'head' or w < 4:
                continue
            faces_e = ('south', 'east', 'west') if cfg.get('ns_swap') else ('north', 'east', 'west')
            for FACE in faces_e:                # 正脸+两侧面都放眼(绕行可见)
                u, v = int(uv[FACE]['uv'][0]), int(uv[FACE]['uv'][1])
                fw_, fh_ = int(uv[FACE]['uv_size'][0]), int(uv[FACE]['uv_size'][1])
                # 超大眼(竖 2 格满白)+粉腮红(参考图)
                BLUSH = (250, 165, 165)
                if FACE == 'south' and fw_ >= 4 and fh_ >= 3:
                    # 眼位=喙旁两端(锁死勿动);其余全白,仅眼上方顶行留白
                    for cc in range(fw_):
                        for rr in range(fh_):
                            px[u+cc, v+rr] = WHITE
                    px[u, v+1] = (15, 15, 15); px[u+fw_-1, v+1] = (15, 15, 15)
                    for cc in range(1, fw_-1):
                        px[u+cc, v] = (32, 32, 32)   # 顶行中间黑帽,眼正上方留白
                elif FACE in ('east', 'west') and fw_ >= 3 and fh_ >= 3:
                    # 侧脸靠眼端白斑(u 方向哪端朝脸由 swap 定,两端都试:先远后近)
                    px[u + fw_-1, v] = WHITE; px[u + fw_-1, v+1] = WHITE


    # 眼睛放大:头骨脸面上的眼白/深眼底色向邻 texel 膨胀 1 格(vox 原作眼只有
    # 1-2 texel,渲染太小看不见)
    for (bn, x0, y0, z0, w, h, d, uv, oy0) in cube_all:
        if bn != 'head' or sp == 'penguin':
            continue
        for face in ('north', 'south'):
            fd = uv[face]
            u, v = int(fd['uv'][0]), int(fd['uv'][1])
            tw, th = int(fd['uv_size'][0]), int(fd['uv_size'][1])
            grow = []
            for vv in range(th):
                for uu in range(tw):
                    c = px[u + uu, v + vv]
                    if c[0] > 200 and c[1] > 200 and c[2] > 200:      # 白眼
                        grow.append((uu, vv))
                    elif c[0] < 60 and c[1] < 60 and c[2] < 60:       # 深眼/鼻
                        grow.append((uu, vv))
            for (uu, vv) in grow:
                for du, dv in ((1,0),(-1,0),(0,1),(0,-1)):
                    nu, nv = uu+du, vv+dv
                    if 0 <= nu < tw and 0 <= nv < th:
                        cc = px[u+nu, v+nv]
                        # 只覆盖非特征区(不吞喙橙/别的眼)
                        if not (cc[0] < 60 and cc[1] < 60) and not (cc[0] > 200 and cc[1] > 200 and cc[2] > 200):
                            px[u+nu, v+nv] = px[u+uu, v+vv]
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
