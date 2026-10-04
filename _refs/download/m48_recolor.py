# -*- coding: utf-8 -*-
# M48c: 涂装方向修正。GLB Y-up: y0=车底(底架), y13=车顶(受电弓/设备)。
# 之前按 y0=车顶画反了(用户: 车头倒下/看到底部)。正确(底→顶):
#   y0 黑底架 y1 灰裙板 y2-3 蓝 y4 白带 y5 红条 y6 蓝 y7 黑窗带
#   y8-9 蓝 y10 银灰上带 y11 蓝 y12 银灰车顶 y13 黑色车顶设备
import pickle

d = pickle.load(open('_refs/download/hxd3d_grid2.pkl', 'rb'))
grid = d['grid']
(T, W, H) = d['dims']

BLUE = (22, 44, 88); WHITE = (240, 240, 240); RED = (200, 16, 16)
BLACK = (12, 12, 16); DARK = (25, 25, 28); SKIRT = (70, 72, 78)
SILVER = (200, 203, 216)

def paint(x, y, z):
    if x <= 4 or x >= T - 5:
        if y >= 13: return DARK
        if y == 12: return SILVER
        if y in (7, 8): return BLACK
        if y in (4, 5): return RED
        if y <= 1: return DARK
        return BLUE
    table = {0: DARK, 1: SKIRT, 2: BLUE, 3: BLUE, 4: WHITE, 5: RED,
             6: BLUE, 7: BLACK, 8: BLUE, 9: BLUE, 10: SILVER, 11: BLUE,
             12: SILVER, 13: DARK}
    c = table[y]
    if z in (0, W - 1) and y == 7:
        if 8 <= x <= 16 or 24 <= x <= 32 or 40 <= x <= 48 or 56 <= x <= 64 or 70 <= x <= 78:
            c = BLACK
    return c

grid3 = {k: paint(*k) for k in grid}
pickle.dump({'dims': (T, W, H), 'grid': grid3, 'axes': 'x=len,y=h,z=w'},
            open('_refs/download/hxd3d_grid3.pkl', 'wb'))

for y in range(H - 1, -1, -1):
    row = ''
    for x in range(T):
        col = grid3.get((x, y, 0))
        row += ('.' if col is None else
                'B' if col == BLUE else
                'W' if col == WHITE else
                'R' if col == RED else
                'k' if col == BLACK else
                'S' if col == SILVER else
                'g' if col == SKIRT else 'D')
    print('%2d %s' % (y, row))
