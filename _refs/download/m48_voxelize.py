# -*- coding: utf-8 -*-
# M48 HXD3D: GLB 三角 → 体素网格(bullet_grid.pkl 同构)
import pickle, random, time, sys

tris = pickle.load(open('_refs/download/hxd3d_tris.pkl', 'rb'))
bx = [-285.0, 619.0]; by = [156.0, 296.0]; bz = [5.0, 257.0]
L = bx[1] - bx[0]
TARGET = 88
scale = L / TARGET
W = round((bz[1] - bz[0]) / scale); H = round((by[1] - by[0]) / scale)
print('grid:', TARGET, W, H, 'scale %.2f' % scale)

grid = {}
random.seed(42)
t0 = time.time()
for (v0, v1, v2, color) in tris:
    pts = []
    for (a, b) in ((v0, v1), (v1, v2), (v2, v0)):
        for f in (0.25, 0.75):
            pts.append((a[0] + (b[0] - a[0]) * f,
                        a[1] + (b[1] - a[1]) * f,
                        a[2] + (b[2] - a[2]) * f))
    pts.extend([v0, v1, v2])
    for p in pts:
        gx = int((p[0] - bx[0]) / scale)
        gy = int((p[1] - by[0]) / scale)
        gz = int((p[2] - bz[0]) / scale)
        if 0 <= gx < TARGET and 0 <= gy < H and 0 <= gz < W:
            key = (gx, gy, gz)
            if key not in grid or random.random() < 0.3:
                grid[key] = color
print('sampled -> %d voxels, %.1fs' % (len(grid), time.time() - t0))
pickle.dump({'dims': (TARGET, W, H), 'grid': grid, 'axes': 'x=len,y=h,z=w'},
            open('_refs/download/hxd3d_grid.pkl', 'wb'))

# 俯视投影(x=长 0..87, z=宽)
lines = []
for z in range(W - 1, -1, -1):
    row = ''.join('#' if any((x, y, z) in grid for y in range(H)) else '.' for x in range(TARGET))
    lines.append(row)
print('\n'.join(lines))
