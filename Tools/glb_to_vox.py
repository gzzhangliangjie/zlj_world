# steam GLB → .vox(MagicaVoxel):部件级采样 + 材质色,轨道剔除
# 用法: python glb_to_vox.py [RES] [OUT.vox]   RES=体素分辨率(长边格数)
import numpy as np, struct, trimesh, sys
from collections import deque
sys.path.insert(0, 'D:/zlj_world/Tools')
import glb_to_geo as G

GLB = 'D:/zlj_world/_refs/poly_pizza/trains/8si3z2CBUuc.glb'
RES = int(sys.argv[1]) if len(sys.argv) > 1 else 48
OUT = sys.argv[2] if len(sys.argv) > 2 else 'C:/Users/zlj10/AppData/Local/hermes/cache/scratch/steam_train.vox'

def fill_enclosed(occ):
    X, Y, Z = occ.shape
    p = np.pad(occ, 1); out = np.zeros(p.shape, bool); q = deque()
    sh = p.shape
    out[0,:,:]=out[-1,:,:]=True; out[:,0,:]=out[:,-1,:]=True; out[:,:,0]=out[:,:,-1]=True
    for x in range(sh[0]):
        for y in range(sh[1]):
            for z in range(sh[2]):
                if out[x,y,z]: q.append((x,y,z))
    while q:
        x,y,z = q.popleft()
        for d in ((1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1)):
            nx,ny,nz = x+d[0],y+d[1],z+d[2]
            if 0<=nx<sh[0] and 0<=ny<sh[1] and 0<=nz<sh[2] and not p[nx,ny,nz] and not out[nx,ny,nz]:
                out[nx,ny,nz]=True; q.append((nx,ny,nz))
    f = ~out[1:-1,1:-1,1:-1]
    return occ | (f & ~occ)

sc = trimesh.load(GLB, force='scene', process=False)
nodes = []
for n in sc.graph.nodes_geometry:
    T, gn = sc.graph.get(n); g = sc.geometry[gn]
    gv = np.asarray(g.vertices)
    w = (T @ np.c_[gv, np.ones(len(gv))].T).T[:, :3]
    m = getattr(g.visual, 'material', None); bcf = None
    if m is not None:
        try: bcf = [float(v) for v in np.atleast_1d(m.baseColorFactor)[:3]]
        except Exception: pass
    nodes.append(dict(name=gn, V=w, F=np.asarray(g.faces),
                      c=tuple(int(v) for v in (bcf or (150,150,155)))))
span = lambda p: p['V'].max(0) - p['V'].min(0)
rails = [p for p in nodes if span(p)[1] < 0.10 and span(p)[0] > 2.5]
keep = [p for p in nodes if p not in rails]
allV = np.vstack([p['V'] for p in keep]); lo, hi = allV.min(0), allV.max(0)
sp = hi - lo; sp[sp==0] = 1
s = RES / sp.max()
dims = np.rint(sp * s).astype(int); dims[dims==0] = 1
X, Y, Z = int(dims[2]), int(dims[1]), int(dims[0])    # 我方轴:x=宽 y=高 z=长

occ_total = np.zeros((X, Y, Z), bool)
col_total = {}
for p in keep:
    t = np.c_[np.clip(np.rint((p['V'][:,2]-lo[2])*s), 0, X-1),
              np.clip(np.rint((p['V'][:,1]-lo[1])*s), 0, Y-1),
              np.clip(np.rint((p['V'][:,0]-lo[0])*s), 0, Z-1)].astype(int)
    o = np.zeros((X, Y, Z), bool)
    for tri in t[p['F']]:
        n_ = int((tri.max(0)-tri.min(0)).max())*2+2
        a = np.arange(n_+1)/n_
        A, B = np.meshgrid(a, a); msk = A+B <= 1
        A, B = A[msk], B[msk]
        P = (tri[0]*A[:,None]+tri[1]*B[:,None]+tri[2]*(1-A-B)[:,None]).astype(int)
        o[P[:,0], P[:,1], P[:,2]] = True
    o = fill_enclosed(o)
    o |= o[::-1, :, :]
    for (x, y, z) in np.argwhere(o):
        if not occ_total[x, y, z] or (x, y, z) not in col_total:
            col_total[(int(x), int(y), int(z))] = p['c']
    occ_total |= o
print('grid %dx%dx%d  voxels %d  colored %d' % (X, Y, Z, occ_total.sum(), len(col_total)))

# ---- 写 .vox:SIZE=(宽,长,高)=(X,Z,Y),体素坐标 (x, z, y) ----
idx = np.argwhere(occ_total)
palette = {}
def pidx(c):
    if c not in palette:
        palette[c] = len(palette)+1
    return palette[c]
vox = []
for x, y, z in idx:
    c = col_total.get((int(x), int(y), int(z)), (150,150,155))
    vox.append((int(x), int(z), int(y), pidx(c)))   # 轴置换:(x, 长, 高)

def chunk(cid, payload):
    return struct.pack('<4sII', cid, len(payload), 0) + payload
xyz_i = struct.pack('<I', len(vox)) + b''.join(struct.pack('<BBBB', *v) for v in vox)
pal = b''
for i in range(256):
    # entry i (0-based) = colorIndex i+1
    found = [c for c, k in palette.items() if k == i+1]
    if found: pal += bytes((*found[0], 255))
    else: pal += bytes((255, 255, 255, 255))
main_body = chunk(b'SIZE', struct.pack('<III', X, Z, Y)) + chunk(b'XYZI', xyz_i) + chunk(b'RGBA', pal)
data = b'VOX ' + struct.pack('<I', 150) + chunk(b'MAIN', main_body)
open(OUT, 'wb').write(data)
print('wrote', OUT, len(data), 'bytes; palette colors', len(palette))
