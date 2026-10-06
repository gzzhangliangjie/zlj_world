# 批量:火车 GLB → res32 .vox(轨道重采样:横跨全长+贴地薄层剔除)
import numpy as np, struct, trimesh, sys, os, json
from collections import deque

RES = int(__import__('os').environ.get('VOXRES', '32'))


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


def load_nodes(glb):
    sc = trimesh.load(glb, force='scene', process=False)
    nodes = []
    for n in sc.graph.nodes_geometry:
        _b = None
        T, gn = sc.graph.get(n); g = sc.geometry[gn]
        gv = np.asarray(g.vertices)
        w = (T @ np.c_[gv, np.ones(len(gv))].T).T[:, :3]
        m = getattr(g.visual, 'material', None); bcf = None
        if m is not None:
            try:
                bcf = [float(v) for v in np.atleast_1d(m.baseColorFactor)[:3]]
                if max(bcf) > 1.001:      # 已经是 0-255 字节值,别再乘
                    bcf = [v / 255.0 for v in bcf]
            except Exception: pass
        c0 = tuple(int(np.clip(round(v*255), 0, 255)) for v in (bcf or (0.59,0.59,0.61)))
        if 'wheel' in gn.lower():
            c0 = (52, 58, 64)          # 轮子统一深灰,与车身分离
        if __import__('os').environ.get('SKIP_SLATS') == '1':
            # 车站格栅:深棕通高窄件(宽<0.15 高>0.5)是密百叶,量化后糊成墙,跳过
            try:
                bb = w.min(0), w.max(0)
                if c0 == (48,23,15) and (bb[1][0]-bb[0][0]) < 0.15 and (bb[1][1]-bb[0][1]) > 0.5:
                    continue
            except Exception:
                pass
        nodes.append(dict(name=gn, V=w, F=np.asarray(g.faces), c=c0))
    return nodes


def voxelize(nodes, res=RES):
    nodes = sorted(nodes, key=lambda p: 0 if 'wheel' in p['name'].lower() else 1)  # 轮子先画
    body = [p for p in nodes if 'wheel' not in p['name'].lower()]
    if not body:
        body = nodes
    allV = np.vstack([p['V'] for p in body]); lo, hi = allV.min(0), allV.max(0)
    sp = hi - lo; sp[sp==0] = 1
    s = res / sp.max()
    dims = np.rint(sp * s).astype(int); dims[dims==0] = 1
    X, Y, Z = int(dims[2]), int(dims[1]), int(dims[0])
    occ = np.zeros((X, Y, Z), bool); col = {}
    for p in nodes:
        t = np.c_[np.clip(np.rint((p['V'][:,2]-lo[2])*s), 0, X-1),
                  np.clip(np.rint((p['V'][:,1]-lo[1])*s), 0, Y-1),
                  np.clip(np.rint((p['V'][:,0]-lo[0])*s), 0, Z-1)].astype(int)
        o = np.zeros((X, Y, Z), bool)
        for tri in t[p['F']]:
            n_ = int((tri.max(0)-tri.min(0)).max())*2+2
            if n_ > 96: n_ = 96   # 车站等大模型:三角形巨大时采样上限,防 O(n^2) 爆炸
            a = np.arange(n_+1)/n_
            A, B = np.meshgrid(a, a); msk = A+B <= 1
            A, B = A[msk], B[msk]
            P = (tri[0]*A[:,None]+tri[1]*B[:,None]+tri[2]*(1-A-B)[:,None]).astype(int)
            o[P[:,0], P[:,1], P[:,2]] = True
        # 性能:fill_enclosed 只在节点局部包围盒内跑(375 节点全网格 BFS = 300s;局部 <2s)
        idx = np.argwhere(o)
        if len(idx):
            x0,x1 = idx[:,0].min(), idx[:,0].max()+1
            y0,y1 = idx[:,1].min(), idx[:,1].max()+1
            z0,z1 = idx[:,2].min(), idx[:,2].max()+1
            o[x0:x1, y0:y1, z0:z1] = fill_enclosed(o[x0:x1, y0:y1, z0:z1])
        for (x, y, z) in np.argwhere(o):
            if not occ[x, y, z] or (x, y, z) not in col:
                col[(int(x), int(y), int(z))] = p['c']
        occ |= o
    return occ, col, (X, Y, Z)


def strip_rails(occ):
    """贴地薄层 + 横跨全长且薄的 z 段落 = 轨道,剔除"""
    X, Y, Z = occ.shape
    rm = np.zeros_like(occ, bool)
    # 规则1: y=0 的薄层(y<=1 且该 z 段全高<=2)
    colh = np.zeros((X, Z), int)
    for x in range(X):
        for z in range(Z):
            colh[x, z] = occ[x, :, z].sum()
    # 规则2: 整个 z 列高度<=2 且贴地(y 从 0 起)
    for x in range(X):
        for z in range(Z):
            if colh[x, z] <= 2 and occ[x, 0, z]:
                rm[x, :, z] = True
    return rm


def write_vox(path, occ, col):
    X, Y, Z = occ.shape
    idx = np.argwhere(occ)
    palette = {}
    def pidx(c):
        if c not in palette:
            palette[c] = len(palette)+1
        return palette[c]
    vox = []
    for x, y, z in idx:
        c = col.get((int(x), int(y), int(z)), (150,150,155))
        vox.append((int(x), int(z), int(y), pidx(c)))
    def chunk(cid, payload):
        return struct.pack('<4sII', cid, len(payload), 0) + payload
    xyz_i = struct.pack('<I', len(vox)) + b''.join(struct.pack('<BBBB', *v) for v in vox)
    pal = b''
    for i in range(256):
        found = [c for c, k in palette.items() if k == i+1]
        if found: pal += bytes((*found[0], 255))
        else: pal += bytes((255, 255, 255, 255))
    main_body = chunk(b'SIZE', struct.pack('<III', X, Z, Y)) + chunk(b'XYZI', xyz_i) + chunk(b'RGBA', pal)
    data = b'VOX ' + struct.pack('<I', 150) + chunk(b'MAIN', main_body)
    open(path, 'wb').write(data)
    return len(vox), len(palette)


if __name__ == '__main__':
    uid = sys.argv[1]
    meta = json.load(open('D:/zlj_world/_refs/poly_pizza/trains/_meta.json'))
    info = meta[uid]
    glb = 'D:/zlj_world/_refs/poly_pizza/trains/%s.glb' % uid
    nodes = load_nodes(glb)
    occ, col, dims = voxelize(nodes)
    # 轨道剔除只对车类模型;山/车站等地形件跳过(山脚薄层会被误删)
    if any(k in info['name'].lower() for k in ('train','locomotive','tram','wagon','car','carriage','railway')):
        rm = strip_rails(occ)
    else:
        rm = np.zeros_like(occ, bool)
    occ2 = occ & ~rm
    nrm = int(rm.sum())
    out = 'C:/Users/zlj10/AppData/Local/hermes/cache/scratch/batch_%s_r%d.vox' % (uid, RES)
    nv, ncol = write_vox(out, occ2, col)
    print('%s (%s): grid %s vox %d -rails %d -> %d, colors %d' % (uid, info['name'], dims, occ.sum(), nrm, nv, ncol))
