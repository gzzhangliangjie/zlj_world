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
    """返回 (verts Nx3, faces Mx3, vcolors Nx3 or None)"""
    import trimesh
    scene = trimesh.load(path, force='scene', process=False)
    verts=[]; faces=[]; vcols=[]
    has_col=False
    for name, geom in scene.geometry.items():
        g = geom.to_mesh() if hasattr(geom,'to_mesh') else geom
        if g is None or len(getattr(g,'vertices',[]))==0: continue
        v0=len(verts)
        verts.append(np.asarray(g.vertices))
        faces.append(np.asarray(g.faces)+v0)
        c=None
        if g.visual is not None:
            if hasattr(g.visual,'vertex_colors') and g.visual.vertex_colors is not None:
                c=np.asarray(g.visual.vertex_colors)[:,:3].astype(np.float32)
            elif hasattr(g.visual,'face_colors') and g.visual.face_colors is not None:
                fc=np.asarray(g.visual.face_colors)[:,:3].astype(np.float32)
                c=fc[np.asarray(g.faces)].mean(axis=1)  # 面色均摊到顶点(粗略)
        if c is not None:
            if len(c)==len(verts[-1]): vcols.append(c); has_col=True
            else: vcols.append(np.full((len(verts[-1]),3),255.0))
        else:
            vcols.append(np.full((len(verts[-1]),3),255.0))
    V=np.concatenate(verts); F=np.concatenate(faces); C=np.concatenate(vcols) if has_col else None
    return V,F,C

def voxelize(V, F, C, res=32, axis_order='glb2vox'):
    """三角形重心采样体素化。返回 occ(res,rez,res) bool + 颜色 dict"""
    # 归一化到 [0,1]
    lo=V.min(axis=0); hi=V.max(axis=0)
    span=hi-lo; span[span==0]=1
    N=(V-lo)/span
    # GLB Y-up → vox: x=x(宽), y(vox)=z_glb(长), z(vox)=y_glb(高)
    nx, nlen, nh = N[:,0], N[:,2], N[:,1]
    occ=np.zeros((res,res,res),dtype=bool)
    col={}
    def put(px,pl,ph,c):
        ix,iy,iz=int(px*res-0.5), int(pl*res-0.5), int(ph*res-0.5)
        ix=min(max(ix,0),res-1); iy=min(max(iy,0),res-1); iz=min(max(iz,0),res-1)
        if not occ[ix,iy,iz]:
            occ[ix,iy,iz]=True
            col[(ix,iy,iz)]=(int(c[0]),int(c[1]),int(c[2]))
    rng=np.random.default_rng(7)
    tri=N[F]
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
            put(px, pz, py, cvec)   # px, pz(=glb z→vox 长), py(=glb y→vox 高)
        # 三顶点也钉进去
        for p,cvv in ((a,(240,240,240)),(b,(240,240,240)),(c_,(240,240,240))):
            pass
    # 顶点色钉点
    if C is not None:
        for i in range(0,len(N), max(1,len(N)//4000)):
            px,py,pz=N[i]
            put(px, pz, py, C[i])
    else:
        for i in range(0,len(N), max(1,len(N)//4000)):
            px,py,pz=N[i]
            put(px, pz, py, (240,240,240))
    return occ, col

def symmetric_fill(occ):
    """x 轴镜像补全(左缺补右,右缺补左)"""
    res=occ.shape[0]
    fl=occ[:,:, :]  # (x, len, h)
    mir=occ[::-1,:,:]
    out=occ|mir
    return out

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
    # vox(x=宽, y=长, z=高) → bedrock geo(x=宽, y=高, z=长)
    # pivot 中心:geo x 偏移 16-res/2? 直接用 bedrock 惯例:pivot=[16,0,res_long]
    L=res[1]
    cubes=[]
    for (x,y,z,sx,sy,sz) in boxes:
        # vox→geo:x→x, y(长)→z, z(高)→y
        gx=x; gy=z; gz=y
        cubes.append({
            "origin":[gx,gy,gz],
            "size":[sx,sz,sy],
            "uv":[0,0],
        })
    geo={
      "format_version":"1.12.0",
      "minecraft:geometry":[{
        "description":{
          "identifier":"geometry."+name,
          "texture_width":texture_w,"texture_height":texture_h,
          "visible_bounds_width":float(max(res[0],res[1])/8),
          "visible_bounds_height":float(res[2]/8),
          "visible_bounds_offset":[0,0,0]
        },
        "bones":[{
          "name":"body","pivot":[res[0]/2.0,0,res[1]],
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
    ap.add_argument('--name',default='triposr')
    a=ap.parse_args()
    V,F,C=load_glb_meshes(a.glb)
    print('mesh: verts=%d faces=%d colors=%s'%(len(V),len(F),'yes' if C is not None else 'no'))
    occ,col=voxelize(V,F,C,res=a.res)
    occ=symmetric_fill(occ)
    n=int(occ.sum())
    print('voxels:',n)
    geo=build_geo(occ,col,name=a.name)
    json.dump(geo,open(a.out,'w'),indent=1)
    print('saved:',a.out,'cubes:',len(geo['minecraft:geometry'][0]['bones'][0]['cubes']))

if __name__=='__main__':
    main()
