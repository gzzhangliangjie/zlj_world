#!/usr/bin/env python
# veh_to_geo.py — batch: mmmm veh_*.vox -> drivable geo creatures (M34 pipeline).
# scale=2 geo px + DrivableVehicle.modelScale=1.585 => 1 vox = 0.198u, exactly
# the chr_base 10-vox = 1.98u player scale (M34 ground truth).
# Wheels auto-detected: ground-starting DARK-gray runs (<110 max channel, so
# white bodies / light trim never read as tires) clustered into left/right
# axle columns. Trains + tank1 keep voxel wheels (rails/treads -> no discs).
import os, sys, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vox_to_creature import convert, load

BASE = 'D:/zlj_world/_refs/mmmm/vox'
GEO = 'D:/zlj_world/VoxelCraft/Assets/Resources/Geo'
TEX = 'D:/zlj_world/VoxelCraft/Assets/Resources/Textures'

NO_WHEELS = {'train', 'train2', 'train3', 'tank1'}   # rails / treads

def detect_wheels(name, path):
    size, vox, pal = load(path)
    if size is None or not vox:
        return None
    X, Y, Z = size
    v2 = {(x, z, y): pal[c][:3] for (x, y, z), c in vox.items() if c < len(pal) and pal[c]}
    W = max(p[0] for p in v2) + 1
    D = max(p[2] for p in v2) + 1
    midx = W / 2.0

    def tire_gray(c):
        return abs(c[0]-c[1]) < 24 and abs(c[1]-c[2]) < 24 and max(c) < 110

    # per (x,z) pillar: longest ground-starting tire run
    pillar_run = {}   # (x,z) -> set of (x,y,z)
    for x in range(W):
        for z in range(D):
            run, best = [], []
            for y in range(0, max(p[1] for p in v2) + 1):
                if (x, y, z) in v2 and tire_gray(v2[(x, y, z)]):
                    run.append((x, y, z))
                else:
                    if run and (not best or len(run) > len(best)): best = run
                    run = []
            if run and (not best or len(run) > len(best)): best = run
            if len(best) >= 3:
                pillar_run[(x, z)] = set(best)

    if not pillar_run:
        return None

    def axles_for(side_cols):
        zs = sorted({z for (x, z) in pillar_run if side_cols(x)})
        clusters = []
        for z in zs:
            if clusters and z - clusters[-1][-1] <= 2:
                clusters[-1].append(z)
            else:
                clusters.append([z])
        clusters.sort(key=len, reverse=True)
        return clusters[:2]

    left = axles_for(lambda x: x < midx)
    right = axles_for(lambda x: x >= midx)
    if not left or not right or len(left) < 2 or len(right) < 2:
        return None
    # pair by z-center proximity
    def ctr(cl): return sum(cl) / len(cl)
    pairs = []
    used = set()
    for lc in sorted(left, key=ctr):
        best_r, bd = None, 1e9
        for rc in right:
            d = abs(ctr(lc) - ctr(rc))
            if d < bd and id(rc) not in used:
                bd, best_r = d, rc
        if best_r is not None and bd <= 3:
            used.add(id(best_r))
            pairs.append((lc, best_r))
    if len(pairs) < 2:
        return None

    lefts = [x for x in range(W) if x < midx and any(k[0] == x for k in pillar_run)]
    rights = [x for x in range(W) if x >= midx and any(k[0] == x for k in pillar_run)]
    lx, rx = (min(lefts), max(rights)) if lefts and rights else (0, W - 1)
    specs = []
    for lc, rc in pairs:
        z0, z1 = min(lc + rc), max(lc + rc)
        specs.append((lx, z0, z1))
        specs.append((rx, z0, z1))
    return specs

def main():
    names = ['train', 'train2', 'train3', 'wagon1', 'wagon2', 'wagon3', 'wagon4',
             'ambulance', 'bus', 'cab1', 'car1', 'car2', 'car3', 'car4', 'car5',
             'fire', 'lunch1', 'lunch2', 'lunch3', 'lunch4',
             'mini1', 'mini2', 'mini3', 'mini4', 'mini5',
             'police1', 'suv1', 'suv2', 'suv3', 'tank1',
             'truck1', 'truck2', 'truck3', 'truck4', 'truck5', 'truck6', 'truck7']
    report = {}
    for n in names:
        src = f'{BASE}/veh_{n}.vox'
        wheels = None if n in NO_WHEELS else detect_wheels(n, src)
        boxes = convert(n, src, GEO, TEX, scale=2.0, wheels=wheels)
        # delivery chain rule: png -> png.bytes (Unity reads .bytes)
        import shutil
        shutil.copyfile(f'{TEX}/{n}_skin.png', f'{TEX}/{n}_skin.png.bytes')
        # geo bbox from the json for the report
        g = json.load(open(f'{GEO}/{n}.geo.json'))
        xs, ys, zs = [], [], []
        for b in g['minecraft:geometry'][0]['bones']:
            for c in b.get('cubes', []):
                o, s = c['origin'], c['size']
                xs += [o[0], o[0]+s[0]]; ys += [o[1], o[1]+s[1]]; zs += [o[2], o[2]+s[2]]
        W, H, D = max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs)
        u = 1/16*1.585   # geo px -> world u
        wb = [b['name'] for b in g['minecraft:geometry'][0]['bones'] if b['name'].startswith('wheel')]
        report[n] = dict(vox=boxes, geo=f'{W:.0f}x{H:.0f}x{D:.0f}px',
                         world=f'{W*u:.2f}x{H*u:.2f}x{D*u:.2f}u', wheels=len(wb))
        print(f"{n}: wheels={len(wb)} geo={W:.0f}x{H:.0f}x{D:.0f}px world={W*u:.2f}x{H*u:.2f}x{D*u:.2f}u")
    json.dump(report, open('D:/zlj_world/_logs/veh_geo_report.json', 'w'), indent=1)

if __name__ == '__main__':
    main()
