from PIL import Image
import numpy as np
a=np.asarray(Image.open('D:/zlj_world/VoxelCraft/_shots/m51_cr400bf_front.png').convert('RGB'),dtype=int)
r,g,b=a[:,:,0],a[:,:,1],a[:,:,2]
Vm=(np.abs(r-30)<12)&(np.abs(g-32)<12)&(np.abs(b-38)<12); Vm[545:]=False
ys,xs=np.where(Vm)
x0,x1,y0,y1=xs.min(),xs.max(),ys.min(),ys.max()
print('V 区: x%d..%d y%d..%d'%(x0,x1,y0,y1))
print('== 每行 V 最左像素左邻色:')
for y in range(int(y0),int(y1)+1,12):
    xr=np.where(Vm[y])[0]
    if not len(xr): continue
    xl=int(xr.min())
    print(y, xl, [tuple(a[y, max(0,xl-i)]) for i in (1,3,6)])
print('== 每行 V 最右像素右邻色:')
for y in range(int(y0),int(y1)+1,12):
    xr=np.where(Vm[y])[0]
    if not len(xr): continue
    xr_=int(xr.max())
    print(y, xr_, [tuple(a[y, min(a.shape[1]-1,xr_+i)]) for i in (1,3,6)])
print('== 每列 V 最底像素下方色:')
for x in range(int(x0),int(x1)+1,12):
    yc=np.where(Vm[:,x])[0]
    if not len(yc): continue
    yb=int(yc.max())
    print(x, yb, [tuple(a[min(a.shape[0]-1,yb+i),x]) for i in (1,3,6)])
