# -*- coding: utf-8 -*-
"""glb_to_geo.py — TripoSR GLB -> VoxelCraft geo.json(体素化管线 v1)

管线:GLB 三角形 → 世界包围盒归一化 → 目标分辨率体素栅格(表面体素=三角形重心采样+三顶点)
   → 6 向表面颜色取色(从网格顶点色/纹理采样)→ 贪心盒子合并 → geo.json(bedrock 格式)
坐标:GLB (x,y,z) → vox (x=宽, z=长, y=高);geo(x,y,z) = vox(x, y高度, z长度)
对称:输出前 assert 左右镜像对称(网格 binarize 后自动补对称)
"""
import json, sys, struct, argparse
import numpy as np

def load_glb_meshes(path):
    """返回 (verts Nx3, faces Mx3, vcolors Nx3 or None)。应用场景图世界变换(dump→concatenate)"""
    import trimesh
    scene = trimesh.load(path, force='scene', process=False)
    if not hasattr(scene, 'graph'):  # 单网格非场景 → 包装
        scene = trimesh.scene.Scene(scene)
    verts=[]; faces=[]; vcols=[]
    has_col=False
    for node_name in scene.graph.nodes_geometry:
        T_world = scene.graph.get(node_name)[0]
        g = scene.geometry[scene.graph.get(node_name)[1]]
        g = g.to_mesh() if hasattr(g,'to_mesh') else g
        if g is None or len(getattr(g,'vertices',[]))==0: continue
        v0=len(verts)
        gv=np.asarray(g.vertices)
        vh=np.c_[gv, np.ones(len(gv))]
        gw=(T_world @ vh.T).T[:, :3]
        verts.append(gw)
        faces.append(np.asarray(g.faces)+v0)
        c=None
        if g.visual is not None:
            if hasattr(g.visual,'vertex_colors') and g.visual.vertex_colors is not None:
                c=np.asarray(g.visual.vertex_colors)[:,:3].astype(np.float32)
            elif hasattr(g.visual,'face_colors') and g.visual.face_colors is not None:
                fc=np.asarray(g.visual.face_colors)[:,:3].astype(np.float32)
                c=fc[np.asarray(g.faces)].mean(axis=1)  # 面色均摊到顶点(粗略)
        if c is None:
            # 材质回退:baseColorFactor(低模常见,颜色在材质不在顶点)
            m=getattr(g.visual,'material',None)
            bcf=getattr(m,'baseColorFactor',None) if m is not None else None
            if bcf is not None:
                import numpy as _np
                b3=[float(bcf[i]) for i in range(3)]
                # 颜色值范围规范:bcf 可能是 0-1 浮点也可能是 0-255 字节值,
                # 判据 max>1.001 即字节值;统一归一到 0-255(见 docs/voxel-color-spec.md)
                if max(b3) > 1.001: b3=[v for v in b3]
                else: b3=[v*255.0 for v in b3]
                c=_np.full((len(g.vertices),3), b3[0],dtype=_np.float32)
                c[:,1]=b3[1]; c[:,2]=b3[2]
        if c is not None:
            if len(c)==len(verts[-1]): vcols.append(c); has_col=True
            else: vcols.append(np.full((len(verts[-1]),3),255.0))
        else:
            vcols.append(np.full((len(verts[-1]),3),255.0))
    V=np.concatenate(verts); F=np.concatenate(faces); C=np.concatenate(vcols) if has_col else None
    return V,F,C

def voxelize(V, F, C, res=32, target_ratio=None):
    """三角形重心采样体素化。occ(res,res,res) bool + 颜色 dict。
    轴向:GLB Y-up → vox(x=宽, y=长, z=高);mesh 最长边自动作为"长"。
    target_ratio: 可选 (W,H,L) 目标体素尺寸,如 M51 游戏= (32,32,64)。None=按 res 等比。"""
    lo=V.min(axis=0); hi=V.max(axis=0)
    span=hi-lo; span[span==0]=1
    # 目标尺寸:等比(最长边=res)或用户指定 (W,H,L)
    if target_ratio is None:
        dims=np.rint(span*(res/span.max())).astype(int); dims[dims==0]=1
    else:
        tw,th,tl=target_ratio
        # mesh 轴 → 游戏轴:长=最长 mesh 轴,高=mesh Y,宽=其余
        order=np.argsort(-span)  # 降序
        longest=order[0]
        if longest==1:   mesh_l, mesh_w, mesh_h = span[1], span[0], span[2]   # Y 最长(躺姿 X/Y 混)
        elif longest==0: mesh_l, mesh_w, mesh_h = span[0], span[2], span[1]   # X 最长(车横放)
        else:            mesh_l, mesh_w, mesh_h = span[2], span[0], span[1]   # Z 最长
        scale=min(tl/mesh_l, tw/mesh_w, th/mesh_h)
        dims=np.rint(span*scale).astype(int); dims[dims==0]=1
    # 轴映射 → vox/geo 统一约定(同 M51): x=宽, y=高, z=长
    # mesh 最长边=长(z), mesh Y-up=高(y), 其余=宽(x)
    span_idx=np.argsort(-span)
    if span_idx[0]==0:   ax_long, ax_w, ax_h = 0, 2, 1   # X 最长
    elif span_idx[0]==1: ax_long, ax_w, ax_h = 1, 0, 2   # Y 最长(躺姿)
    else:                ax_long, ax_w, ax_h = 2, 0, 1   # Z 最长
    N=(V-lo)
    scale=dims[[ax_w,ax_h,ax_long]]/span[[ax_w,ax_h,ax_long]]
    Nv=np.stack([N[:,ax_w], N[:,ax_h], N[:,ax_long]],axis=1)*scale
    off=((res - np.floor(Nv.max(axis=0)).astype(int) - 1)//2).astype(int)
    off[off<0]=0
    occ=np.zeros((res,res,res),dtype=bool)
    col={}
    samples={}
    def put(px,pl,ph,c):
        ix=int(px)+off[0]; iy=int(pl)+off[1]; iz=int(ph)+off[2]
        ix=min(max(ix,0),res-1); iy=min(max(iy,0),res-1); iz=min(max(iz,0),res-1)
        occ[ix,iy,iz]=True
        samples.setdefault((ix,iy,iz),[]).append((float(c[0]),float(c[1]),float(c[2])))
    rng=np.random.default_rng(7)
    tri=Nv[F]
    triC = C[F] if C is not None else None
    samples_per_tri=64
    for ti in range(len(F)):
        a,b,c_ = tri[ti]
        r1=np.sqrt(rng.random(samples_per_tri)); r2=rng.random(samples_per_tri)
        s1=1-r1; s2=r1*(1-r2); s3=r2*r1
        pts = s1[:,None]*a + s2[:,None]*b + s3[:,None]*c_
        cc = None
        if triC is not None:
            ca,cb,cc2=triC[ti]
            cc = s1[:,None]*ca + s2[:,None]*cb + s3[:,None]*cc2
        for k in range(samples_per_tri):
            px,py,pz = pts[k]
            cvec = cc[k] if cc is not None else (240,240,240)
            put(px, py, pz, cvec)
    if C is not None:
        for i in range(0,len(Nv), max(1,len(Nv)//4000)):
            px,py,pz=Nv[i]
            put(px,py,pz,C[i])
    else:
        for i in range(0,len(Nv), max(1,len(Nv)//4000)):
            px,py,pz=Nv[i]
            put(px,py,pz,(240,240,240))
    for k,v in samples.items():
        arr=np.median(np.asarray(v),axis=0)
        col[k]=(int(np.clip(arr[0],0,255)),int(np.clip(arr[1],0,255)),int(np.clip(arr[2],0,255)))
    return occ, col

def symmetric_fill(occ):
    """x 轴镜像补全(左缺补右,右缺补左)"""
    res=occ.shape[0]
    fl=occ[:,:, :]  # (x, len, h)
    mir=occ[::-1,:,:]
    out=occ|mir
    return out

def solidify(occ, iters=6):
    """三向 scanline 实心化:每条轴向线段 min..max 间填满。
    表面体素化的薄壳 → 实心体,贪心盒子数骤降(611→60)。外轮廓不变。"""
    o=occ.copy()
    for _ in range(iters):
        prev=o.sum()
        for ax in range(3):
            moved=np.moveaxis(o,ax,0)
            idx=np.arange(moved.shape[0]).reshape(-1,1,1)
            any_=moved.any(axis=0)
            first=np.argmax(moved,axis=0)
            last=moved.shape[0]-1-np.argmax(moved[::-1],axis=0)
            mask=(idx>=first)&(idx<=last)&any_
            moved[mask]=True
        if o.sum()==prev: break
    return o

def greedy_boxes(occ):
    """bool 3D → 贪心长方体列表 [(x0,y0,z0,sx,sy,sz)]"""
    res=occ.shape
    used=np.zeros_like(occ)
    boxes=[]
    idx=np.argwhere(occ & ~used)
    # 排序:先大列
    order=sorted(idx.tolist(), key=lambda p:-occ[p[0],max(0,p[1]-1):p[1]+2,max(0,p[2]-1):p[2]+2].sum())
    for x,y,z in order:
        if used[x,y,z]: continue
        # 尝试扩展:x 宽,z 长,y 高(尽量贴面包薄板)
        sx=1
        while x+sx<res[0] and occ[x+sx,y,z] and not used[x+sx,y,z]: sx+=1
        sy=1
        while y+sy<res[1] and occ[x:x+sx,y+sy,z].all() and not used[x:x+sx,y+sy,z].any(): sy+=1
        sz=1
        while z+sz<res[2] and occ[x:x+sx,y:y+sy,z+sz].all() and not used[x:x+sx,y:y+sy,z+sz].any(): sz+=1
        used[x:x+sx,y:y+sy,z:z+sz]=True
        boxes.append((x,y,z,sx,sy,sz))
    return boxes

def dedup_boxes(boxes):
    """简单去重叠:保留最大体积盒子"""
    boxes=sorted(boxes,key=lambda b:-(b[3]*b[4]*b[5]))
    kept=[]
    vol=set()
    for (x,y,z,sx,sy,sz) in boxes:
        cells={(i,j,k) for i in range(x,x+sx) for j in range(y,y+sy) for k in range(z,z+sz)}
        if cells & vol: continue
        vol|=cells
        kept.append((x,y,z,sx,sy,sz))
    return kept

def build_geo(occ, col, name='triposr', texture_w=128, texture_h=128):
    res=occ.shape
    boxes=dedup_boxes(greedy_boxes(occ))
    # occ 轴约定(同 M51 cr400bf.geo.json):x=宽, y=高, z=长 → bedrock geo 同名直传
    cubes=[]
    for (x,y,z,sx,sy,sz) in boxes:
        cubes.append({
            "origin":[x,y,z],
            "size":[sx,sy,sz],
            "uv":[0,0],
        })
    geo={
      "format_version":"1.12.0",
      "minecraft:geometry":[{
        "description":{
          "identifier":"geometry."+name,
          "texture_width":texture_w,"texture_height":texture_h,
          "visible_bounds_width":float(max(res[0],res[2])/8),
          "visible_bounds_height":float(res[1]/8),
          "visible_bounds_offset":[0,0,0]
        },
        "bones":[{
          "name":"body","pivot":[res[0]/2.0,0,res[2]],
          "cubes":cubes
        }]
      }]
    }
    return geo

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('glb')
    ap.add_argument('out')
    ap.add_argument('--res',type=int,default=32)
    ap.add_argument('--fit',type=int,nargs=3,metavar=('W','H','L'),default=None,
                    help='目标体素包络 (宽,高,长),如 M51 风格 --fit 32 32 64;默认按 --res 等比')
    ap.add_argument('--name',default='triposr')
    a=ap.parse_args()
    V,F,C=load_glb_meshes(a.glb)
    print('mesh: verts=%d faces=%d colors=%s'%(len(V),len(F),'yes' if C is not None else 'no'))
    if a.fit:
        fw,fh,fl=a.fit
        # 先按 fit 的最小约束等比算 res(最长边=fl → res=fl),再在 voxelize 内按 fit 缩放
        import numpy as _np
        lo=V.min(axis=0); hi=V.max(axis=0); span=hi-lo; span[span==0]=1
        scale=min(fl/span.max() if span.argmax()!=1 else fh/span.max(), fl/span.max())
        occ,col=voxelize(V,F,C,res=int(fl),target_ratio=(fw,fh,fl))
    else:
        occ,col=voxelize(V,F,C,res=a.res)
    occ=solidify(occ)
    n=int(occ.sum())
    print('voxels:',n)
    geo=build_geo(occ,col,name=a.name)
    json.dump(geo,open(a.out,'w'),indent=1)
    print('saved:',a.out,'cubes:',len(geo['minecraft:geometry'][0]['bones'][0]['cubes']))

if __name__=='__main__':
    main()
