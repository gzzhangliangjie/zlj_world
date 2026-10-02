
import sys
from collections import Counter
sys.path.insert(0, 'Tools')
from remap_props import load   # proven nested-layout loader
for name in sys.argv[1:]:
    sz, vox, pal = load(f'_refs/mmmm/vox/obj_{name}.vox')
    X,Y,Z = sz
    cc = Counter(pal[ci] for ci in vox.values())
    zmax = max(v[2] for v in vox)
    top = Counter(pal[ci] for ci, v2 in [(ci, k) for k, ci in vox.items()] if v2[2] >= zmax*2//3)
    fmt = lambda c: f"{c[0]},{c[1]},{c[2]}" if isinstance(c[0], int) else f"{c}"
    print(f"{name:12s} {X}x{Y}x{Z} n={len(vox):5d} | all: " + ' '.join(f"{fmt(c)}:{k2}" for c,k2 in cc.most_common(5)) + " | top: " + ' '.join(f"{fmt(c)}:{k2}" for c,k2 in top.most_common(3)))
