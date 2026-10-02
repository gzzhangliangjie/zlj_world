#!/usr/bin/env python
# vox_lint.py — offline gate for converted VoxStructures (.bytes = MagicaVoxel).
# Run AFTER remap_props.py, BEFORE any Unity snapshot. Exit 1 on any failure.
#
# Checks (each catches a real bug that shipped in M30-M32):
#   1. parse + size sane (W,D >=2, 2 <= H <= 16; tree2 H=19 whitelisted)
#   2. census by RGB -> must NOT be single-color for multi-part props
#      (palette-collapse bug: ROLE exact-match miss -> all-Nearest-Neighbor-Stone)
#   3. expected roles per prop (Glowstone must exist in stlight, Leaves in
#      hedge/tree/planter...) — the "lamp with no lamp" bug
#   4. non-empty and no NaN-ish sizes
#   5. footprint sanity: prop must stand on its base layer (base layer >0 voxels)
import struct, sys, os
from collections import Counter

# anchor colors from VoxStructure.BlockForColor (keep both in sync!)
BLOCK = {
    (96, 168, 60): 'Leaves', (103, 82, 49): 'Log', (125, 125, 125): 'Stone',
    (252, 224, 130): 'Glowstone', (32, 32, 38): 'WoolBlack',
    (176, 46, 38): 'WoolRed', (219, 207, 163): 'Sand', (150, 97, 83): 'Brick',
    (156, 127, 78): 'Plank', (200, 220, 228): 'Glass', (106, 170, 64): 'Grass',
    (232, 236, 238): 'WoolWhite', (234, 195, 55): 'WoolYellow',
    (53, 87, 178): 'WoolBlue', (86, 128, 40): 'WoolGreen',
    (110, 110, 110): 'Cobble', (160, 210, 255): 'Ice', (93, 236, 245): 'DiamondOre',
    (70, 70, 70): 'CoalOre',
    (136, 126, 126): 'Gravel', (216, 175, 147): 'IronOre', (120, 120, 120): 'StoneBrick',
}
# per-prop expectations: (min distinct colors, required roles)
EXPECT = {
    'tree1':    (2, {'Leaves', 'Log'}),
    'tree2':    (2, {'Leaves', 'Log'}),
    'tree3':    (2, {'Leaves', 'Log'}),          # WoolRed optional accent
    'tree4':    (2, {'Leaves', 'Log'}),
    'fence2':   (1, {'Leaves'}),                 # semantic green hedge
    'stlight':  (3, {'Glowstone', 'Stone'}),     # lamp head + pole + contrast
    'trashcan': (2, {'Stone', 'WoolBlack'}),     # body + dark rim
    'planter':  (3, {'Leaves', 'Stone'}),        # plant + pot (+Log trunk)
    'cottage':  (2, {'Plank'}),                  # hand-built, lenient
    'house5':   (2, {'Brick'}),                  # hand-built, lenient
    # batch 2
    'bench3':   (1, {'WoolWhite'}),
    'bench4':   (2, {'Stone', 'WoolBlack'}),
    'hydrant':  (1, {'WoolRed'}),
    'pumpkin':  (1, {'WoolYellow'}),             # +WoolGreen stem (1 voxel)
    'trlight2': (3, {'WoolBlack', 'Glowstone'}), # traffic light w/ lamp
    'newsbox1': (3, {'Stone', 'WoolBlack', 'WoolBlue'}),
    'cone1':    (2, {'WoolYellow', 'WoolWhite'}),
    # M40 buildings (street blocks): window glass + per-building wall role gates
    'obj_store01': (4, {'Glass', 'Brick', 'WoolBlack', 'WoolYellow'}),
    'obj_house1':  (3, {'Glass', 'Stone'}),
    'obj_house2':  (3, {'Glass', 'WoolWhite'}),
    'obj_house6':  (3, {'Glass', 'Sand', 'Plank'}),
    'obj_store02': (3, {'Glass', 'Plank', 'WoolGreen'}),
    'obj_store03': (4, {'Glass', 'Sand', 'WoolRed', 'WoolBlue'}),
    'obj_store04': (3, {'Glass', 'Stone'}),
    'obj_store05': (3, {'Glass', 'Brick', 'WoolBlack'}),
    'obj_story01': (2, {'Glass', 'Stone'}),
    'obj_story02': (3, {'Glass', 'Brick', 'WoolYellow'}),
    # batch 3 buildings
    'obj_store06': (3, {'Brick', 'Cobble', 'Glass'}),
    'obj_store07': (3, {'Plank', 'Glass'}),
    'obj_store08': (3, {'Sand', 'WoolWhite'}),
    'obj_store09': (3, {'WoolWhite', 'Stone'}),
    'obj_store10': (3, {'Brick', 'WoolWhite'}),
    'obj_store11': (3, {'Plank', 'Glass'}),
    'obj_store12': (3, {'Sand', 'Glass'}),
    'obj_store13': (3, {'WoolWhite', 'Glass'}),
    'obj_store14': (3, {'Plank', 'Glass', 'WoolWhite'}),
    'obj_store15': (3, {'Brick', 'WoolYellow', 'WoolWhite'}),
    'obj_store16': (3, {'Sand', 'Glass'}),
    'obj_store17': (3, {'Plank', 'WoolWhite'}),
    'obj_house3':  (4, {'WoolWhite', 'Plank', 'Cobble', 'Glass'}),
    'obj_house4':  (3, {'WoolWhite', 'Glass', 'WoolBlack'}),
    'obj_house7':  (2, {'Brick'}),
    'obj_house8':  (3, {'WoolWhite', 'Stone', 'Cobble'}),
    'obj_story03': (3, {'Plank', 'Glass', 'WoolYellow'}),
    'obj_story04': (3, {'Sand', 'Plank', 'WoolYellow'}),
    'obj_story05': (3, {'Brick', 'Glass'}),
    'obj_story06': (3, {'Plank', 'Glass', 'WoolYellow'}),
    # batch 3 props
    'sign1':     (2, {'Stone', 'WoolWhite'}),
    'sign5':     (2, {'Stone', 'WoolRed'}),
    'chair1':    (2, {'Plank', 'Log'}),          # timber chair
    'table1':    (1, {'Stone'}),
    'cart1':     (2, {'Stone', 'WoolRed'}),
    'cart2':     (2, {'WoolRed', 'WoolWhite'}),
    'cart1a':    (2, {'WoolBlue', 'WoolRed'}),
    'busstop':   (2, {'Glass', 'Stone'}),
    'fountain':  (2, {'DiamondOre', 'Stone'}),  # water renders as DiamondOre anchor
    'statue1':   (2, {'Stone', 'Cobble'}),
    'mailbox2':  (2, {'Stone', 'WoolWhite'}),   # white pillar box (default-palette source)
    'newsbox2':  (2, {'WoolYellow', 'WoolBlack'}),
    'trashcan2': (3, {'Stone', 'WoolBlack', 'WoolRed'}),
    'stlight1':  (2, {'Stone', 'Glowstone'}),
    'trlight1':  (2, {'WoolBlack', 'Glowstone'}),
    'container1':(2, {'WoolRed', 'Stone'}),
    'fence1':    (1, {'Stone'}),
    'column1':   (1, {'Stone'}),
    'mushroom1': (2, {'WoolRed', 'WoolWhite'}),
    'planter1':  (3, {'Leaves', 'Stone', 'Log'}),
    'trellis':   (2, {'WoolWhite', 'WoolGreen'}),
    'stage':     (2, {'Stone', 'Glass'}),        # grey stage + cyan backdrop
    'playgrnd1': (2, {'Plank', 'WoolGreen'}),   # timber frame + green
    'grill':     (1, {'WoolBlack'}),
    'campfire':  (3, {'Log', 'Stone'}),
    'dogstand':  (3, {'Glass', 'WoolYellow', 'WoolWhite'}),
    'rubbish1':  (1, {'Log'}),
    # ---- batch 4 props (2026-10-02) ----
    'arcade1':   (3, {'WoolWhite', 'WoolRed', 'WoolBlack'}),
    'arcade2':   (3, {'WoolWhite', 'Glass', 'WoolRed'}),
    'arcade3':   (2, {'WoolWhite', 'Cobble'}),
    'arcade4':   (3, {'WoolWhite', 'WoolYellow', 'Cobble'}),
    'arcade5':   (3, {'WoolWhite', 'Leaves', 'WoolRed'}),
    'bench1':    (2, {'WoolWhite', 'Glass'}),
    'bench2':    (3, {'WoolWhite', 'Glass', 'Plank'}),
    'bench5':    (1, {'Plank'}),
    'boxingring':(3, {'Glass', 'Cobble', 'WoolBlue'}),
    'cart1b':    (3, {'Stone', 'Plank', 'WoolYellow'}),
    'cart2a':    (2, {'WoolYellow', 'WoolWhite'}),
    'cart2b':    (3, {'WoolRed', 'WoolYellow', 'WoolWhite'}),
    'celltower': (2, {'Cobble', 'Stone'}),
    'chair2':    (2, {'Plank', 'Log'}),
    'christmas1':(2, {'WoolGreen', 'WoolRed'}),
    'column2':   (1, {'WoolWhite'}),
    'column3':   (1, {'WoolWhite'}),
    'container2':(1, {'WoolGreen'}),
    'container3':(2, {'Stone', 'Cobble'}),
    'container4':(2, {'Glass', 'WoolBlue'}),
    'cross':     (1, {'WoolYellow'}),
    'curb1':     (2, {'WoolGreen', 'Stone'}),
    'curb3':     (2, {'WoolGreen', 'Stone'}),
    'curb4':     (2, {'WoolGreen', 'Stone'}),
    'curb5':     (1, {'Stone'}),
    'curb6':     (2, {'WoolGreen', 'Stone'}),
    'curb7':     (2, {'WoolGreen', 'Stone'}),
    'curb7a':    (1, {'WoolGreen'}),
    'curb8':     (1, {'Stone'}),
    'door1':     (3, {'WoolWhite', 'Glass', 'Plank'}),
    'door2':     (3, {'Log', 'WoolWhite', 'Glass'}),
    'door3':     (2, {'Glass', 'WoolWhite'}),
    'door4':     (3, {'Plank', 'WoolWhite', 'Glass'}),
    'fence3':    (2, {'Stone', 'Cobble'}),
    'fence4':    (1, {'Stone'}),
    'fence5':    (2, {'Stone', 'Cobble'}),
    'fence6':    (2, {'Stone', 'Cobble'}),
    'fence7':    (1, {'WoolWhite'}),
    'grave1':    (1, {'Cobble'}),
    'grave2':    (2, {'Stone', 'Cobble'}),
    'grave3':    (2, {'Stone', 'Cobble'}),
    'grave4':    (2, {'Cobble', 'Stone'}),
    'guitarcase':(1, {'Log'}),
    'halo':      (1, {'WoolYellow'}),
    'mailbox2a': (2, {'WoolWhite', 'WoolRed'}),
    'mailbox2b': (2, {'WoolBlue', 'WoolWhite'}),
    'mushroom2': (2, {'Plank', 'WoolYellow'}),
    'mushroom3': (1, {'Cobble'}),
    'newsbox3':  (3, {'Glass', 'Cobble', 'WoolWhite'}),
    'newsbox4':  (3, {'WoolYellow', 'Cobble', 'Log'}),
    'park_block':(1, {'Stone'}),
    'path1':     (1, {'Cobble'}),
    'pentagram': (2, {'WoolRed', 'WoolYellow'}),
    'planter2':  (3, {'Leaves', 'Stone', 'Log'}),
    'planter3a': (3, {'Stone', 'Log', 'Cobble'}),
    'planter3b': (3, {'Stone', 'Log', 'Cobble'}),
    'playgrnd2': (3, {'Plank', 'WoolYellow', 'Log'}),
    'playgrnd3': (3, {'WoolGreen', 'Plank', 'Log'}),
    'playgrnd4': (3, {'Plank', 'Log', 'WoolGreen'}),
    'playgrnd5': (3, {'Plank', 'Log', 'WoolGreen'}),
    'potty1':    (2, {'WoolGreen', 'WoolWhite'}),
    'potty2':    (2, {'WoolBlue', 'WoolWhite'}),
    'potty3':    (3, {'WoolBlue', 'Cobble', 'WoolWhite'}),
    'rubbish2':  (1, {'Log'}),
    'rubbish3':  (1, {'Glass'}),
    'rubbish4':  (1, {'WoolRed'}),
    'sidewalk1': (1, {'Stone'}),
    'sidewalk3': (1, {'Stone'}),
    'sidewalk4': (1, {'Stone'}),
    'sidewalk5': (1, {'Stone'}),
    'sign2':     (3, {'WoolWhite', 'Plank', 'Glass'}),
    'sign3':     (3, {'WoolWhite', 'Plank', 'WoolRed'}),
    'sign4':     (3, {'WoolWhite', 'WoolRed', 'WoolGreen'}),
    'sign6':     (2, {'WoolWhite', 'Glass'}),
    'sign7':     (3, {'WoolBlack', 'WoolYellow', 'Stone'}),
    'sign8':     (2, {'WoolWhite', 'WoolRed'}),         # grey 116 lands nearest-neighbour, not Stone anchor
    'sign9':     (2, {'WoolWhite', 'WoolYellow'}),
    'statue2':   (2, {'Stone', 'Cobble'}),
    'statue3':   (2, {'Stone', 'Cobble'}),
    'stlight2':  (3, {'Stone', 'Cobble', 'Glowstone'}),
    'stlight3':  (3, {'WoolWhite', 'Stone', 'Glowstone'}),
    'street1':   (1, {'WoolYellow'}),
    'street2':   (1, {'WoolWhite'}),
    'stretcher': (2, {'WoolYellow', 'WoolBlack'}),
    'table2':    (2, {'Plank', 'Stone'}),
    'table3':    (1, {'Log'}),
    'table3a':   (2, {'Plank', 'Log'}),
    'table3b':   (2, {'Log', 'Stone'}),
    'tracks1':   (1, {'Cobble'}),
    'tracks2':   (2, {'Log', 'Cobble'}),
    'trashcan1': (2, {'Stone', 'Cobble'}),
    'trashcan3': (3, {'Stone', 'WoolBlack', 'Plank'}),
    'trashcan4': (2, {'WoolGreen', 'Stone'}),
    'tree1a':    (2, {'WoolRed', 'Log'}),
    'tree1b':    (2, {'WoolYellow', 'Log'}),
    'tree1c':    (2, {'WoolRed', 'Log'}),
    'tree2a':    (2, {'WoolRed', 'Log'}),
    'tree2b':    (2, {'WoolYellow', 'Log'}),
    'tree2c':    (2, {'WoolRed', 'Log'}),
    'wall':      (2, {'Stone', 'WoolWhite'}),
    'driveway1': (1, {'Stone'}),
    'driveway2': (2, {'Stone', 'Cobble'}),
    'driveway3': (1, {'Stone'}),
    'armgate2':  (1, {'Stone'}),
    'obj_store16': (3, {'Sand', 'Stone', 'Glass'}),
    # ---- batch 5 (2026-10-02 final: 46 variants + 13 misc) ----
    'armgate1': (2, {'WoolYellow', 'WoolRed'}),
    'candle': (2, {'Log', 'Glowstone'}),
    'crosswalk': (1, {'WoolYellow'}),
    'fire1': (2, {'Glowstone', 'WoolRed'}),
    'fire2': (2, {'Glowstone', 'WoolRed'}),
    'fire3': (2, {'Glowstone', 'WoolRed'}),
    'fire4': (2, {'Cobble', 'CoalOre'}),
    'fire5': (2, {'Cobble', 'CoalOre'}),
    'mailbox': (2, {'WoolBlue', 'WoolWhite'}),
    'obj_house1a': (3, {'Brick', 'Sand', 'WoolWhite'}),
    'obj_house1b': (3, {'Stone', 'WoolWhite', 'WoolYellow'}),
    'obj_house1c': (3, {'Plank', 'WoolWhite', 'WoolRed'}),
    'obj_house2a': (3, {'WoolWhite', 'Cobble', 'Glass'}),
    'obj_house2b': (3, {'WoolWhite', 'Cobble', 'Glass'}),
    'obj_house2c': (3, {'WoolWhite', 'Cobble', 'Glass'}),
    'obj_house2d': (3, {'WoolWhite', 'Cobble', 'Glass'}),
    'obj_house3a': (3, {'WoolWhite', 'WoolBlack', 'Cobble'}),
    'obj_house3b': (3, {'WoolWhite', 'WoolGreen', 'Cobble'}),
    'obj_house3c': (3, {'WoolWhite', 'WoolBlue', 'Cobble'}),
    'obj_house4a': (3, {'WoolWhite', 'Glass', 'WoolYellow'}),
    'obj_house4b': (3, {'WoolWhite', 'WoolGreen', 'WoolBlack'}),
    'obj_house4c': (3, {'WoolWhite', 'Glass', 'WoolBlue'}),
    'obj_house4d': (3, {'WoolWhite', 'Brick', 'WoolBlack'}),
    'obj_house5a': (3, {'Sand', 'Stone', 'Log'}),
    'obj_house5b': (3, {'Plank', 'Log', 'Stone'}),
    'obj_house5c': (3, {'Brick', 'Stone', 'Log'}),
    'obj_house6a': (3, {'Stone', 'WoolWhite', 'WoolYellow'}),
    'obj_house6b': (3, {'Stone', 'WoolWhite', 'WoolRed'}),
    'obj_house6c': (3, {'Stone', 'WoolWhite', 'Glass'}),
    'obj_house6d': (3, {'Stone', 'WoolWhite', 'WoolGreen'}),
    'obj_house7a': (3, {'WoolBlue', 'Glass', 'Cobble'}),
    'obj_house7b': (3, {'WoolYellow', 'Cobble', 'WoolWhite'}),
    'obj_house7c': (3, {'WoolRed', 'WoolYellow', 'Cobble'}),
    'obj_house8a': (3, {'WoolWhite', 'WoolYellow', 'Cobble'}),
    'obj_house8b': (3, {'WoolWhite', 'WoolRed', 'Cobble'}),
    'obj_house8c': (3, {'WoolWhite', 'WoolBlue', 'Cobble'}),
    'obj_store03a': (3, {'Brick', 'Cobble', 'WoolBlack'}),
    'obj_store16a': (3, {'Brick', 'Cobble', 'WoolBlue'}),
    'obj_store16b': (3, {'Sand', 'WoolWhite', 'Cobble'}),
    'obj_store17a': (3, {'Brick', 'Glass', 'Stone'}),
    'obj_story01a': (3, {'Plank', 'Stone', 'Glass'}),
    'obj_story01b': (3, {'Plank', 'Stone', 'Glass'}),
    'obj_story03a': (3, {'Plank', 'Stone', 'Glass'}),
    'obj_story03b': (3, {'Plank', 'Stone', 'WoolRed'}),
    'obj_story03c': (3, {'Plank', 'Stone', 'WoolGreen'}),
    'obj_story03d': (3, {'Plank', 'Stone', 'WoolYellow'}),
    'obj_story04a': (3, {'Sand', 'Plank', 'Cobble'}),
    'obj_story04b': (3, {'Sand', 'Plank', 'WoolRed'}),
    'obj_story04c': (3, {'Sand', 'Plank', 'WoolGreen'}),
    'obj_story04d': (3, {'Sand', 'Plank', 'WoolYellow'}),
    'obj_story05a': (3, {'Brick', 'Stone', 'Glass'}),
    'obj_story06a': (3, {'Plank', 'Stone', 'Glass'}),
    'obj_story06b': (3, {'Plank', 'Stone', 'Glass'}),
    'obj_story06c': (3, {'Plank', 'Stone', 'Glass'}),
    'obj_story06d': (3, {'Plank', 'Stone', 'Glass'}),
    'policetape': (1, {'WoolYellow'}),
    'splatter1': (1, {'Sand'}),
    'splatter2': (1, {'WoolRed'}),
    'splatter3': (1, {'WoolRed'}),
    # ---- batch 6: trains + road structures ----
    'train':  (3, {'Cobble', 'WoolRed', 'Glass'}),
    'train2': (3, {'CoalOre', 'WoolYellow', 'Cobble'}),
    'train3': (2, {'Cobble', 'CoalOre'}),
    'wagon1': (3, {'WoolBlue', 'WoolWhite', 'WoolBlack'}),
    'wagon2': (2, {'WoolGreen', 'WoolWhite'}),
    'wagon3': (2, {'WoolYellow', 'WoolWhite'}),
    'wagon4': (2, {'WoolRed', 'WoolWhite'}),
    'overpass1': (3, {'Stone', 'Cobble', 'WoolYellow'}),
    'tunnel1': (3, {'Stone', 'StoneBrick', 'Glass'}),

    # ---- batch 6b: road vehicles ----
    'ambulance': (3, {'WoolWhite', 'CoalOre', 'Stone'}),
    'bus': (3, {'WoolWhite', 'WoolRed', 'CoalOre'}),
    'cab1': (3, {'WoolYellow', 'Glass', 'WoolWhite'}),
    'car1': (3, {'WoolBlue', 'WoolWhite', 'WoolBlack'}),
    'car2': (3, {'WoolRed', 'Glass', 'WoolBlack'}),
    'car3': (3, {'CoalOre', 'Stone', 'WoolBlack'}),
    'car4': (3, {'WoolWhite', 'WoolRed', 'WoolGreen'}),
    'car5': (3, {'WoolGreen', 'WoolWhite', 'WoolBlack'}),
    'fire': (3, {'WoolRed', 'Stone', 'CoalOre'}),
    'lunch1': (3, {'WoolRed', 'CoalOre', 'Stone'}),
    'lunch2': (3, {'WoolWhite', 'CoalOre', 'Stone'}),
    'lunch3': (3, {'WoolGreen', 'CoalOre', 'Stone'}),
    'lunch4': (3, {'CoalOre', 'Stone', 'WoolBlack'}),
    'mini1': (3, {'WoolRed', 'WoolBlack', 'WoolWhite'}),
    'mini2': (3, {'WoolWhite', 'WoolBlack', 'WoolGreen'}),
    'mini3': (3, {'WoolYellow', 'WoolWhite', 'WoolBlack'}),
    'mini4': (3, {'WoolGreen', 'WoolBlack', 'WoolWhite'}),
    'mini5': (3, {'WoolBlue', 'WoolBlack', 'WoolWhite'}),
    'police1': (3, {'WoolBlue', 'WoolWhite', 'WoolBlack'}),
    'suv1': (3, {'Stone', 'CoalOre', 'WoolBlack'}),
    'suv2': (3, {'CoalOre', 'Stone', 'WoolBlue'}),
    'suv3': (3, {'CoalOre', 'Stone', 'WoolYellow'}),
    'tank1': (3, {'Sand', 'CoalOre', 'Cobble'}),
    'truck1': (3, {'WoolWhite', 'WoolRed', 'Stone'}),
    'truck2': (3, {'Stone', 'CoalOre', 'WoolWhite'}),
    'truck3': (3, {'WoolWhite', 'Stone', 'CoalOre'}),
    'truck4': (3, {'Sand', 'Stone', 'CoalOre'}),
    'truck5': (3, {'WoolBlue', 'Stone', 'CoalOre'}),
    'truck6': (3, {'WoolBlue', 'Stone', 'CoalOre'}),
    'truck7': (3, {'WoolRed', 'Stone', 'CoalOre'}),
}
HEIGHT_WHITELIST = {'tree2': 19, 'sign1': 19, 'sign5': 19, 'dogstand': 20, 'fence1': 18,
                  'celltower': 19, 'cross': 20, 'sign4': 19, 'sign9': 22, 'fence5': 19, 'stlight3': 32,
                  'playgrnd2': 20, 'sign6': 19, 'stlight2': 32, 'tree2a': 19, 'tree2b': 19, 'tree2c': 19}   # tall thin poles, y-guard in stamper
FLAT_OK = {'sidewalk2', 'sign1', 'sign5', 'curb2',             # flat pavement tiles & 1-deep sign plates
           'sidewalk1', 'sidewalk3', 'sidewalk4', 'sidewalk5',    # batch4 pavement tiles
           'curb3', 'curb5', 'curb8', 'park_block', 'path1',      # thin ground strips
           'street1', 'street2', 'crosswalk',                     # flat road paint
           'tracks1', 'tracks2', 'fence3', 'fence4',              # rails / thin pickets
           'driveway1', 'driveway2', 'driveway3', 'wall',         # driveway paint / thin wall copings
           'candle', 'fire1', 'fire2', 'fire3', 'fire4', 'fire5',  # batch5: tiny flames/smoke H<2
           'policetape', 'splatter1', 'splatter2', 'splatter3'}    # batch5: 1-tall tape/decals

def load(path):
    d = open(path, 'rb').read()
    p = 8
    assert d[p:p+4] == b'MAIN'; p += 4
    mchlen = struct.unpack('<I', d[p+4:p+8])[0]
    p += 8; end = p + mchlen
    size = None; vox = {}; pal = [None]
    while p + 12 <= end:
        cid = d[p:p+4]; clen = struct.unpack('<I', d[p+4:p+8])[0]; chlen = struct.unpack('<I', d[p+8:p+12])[0]
        body = p + 12
        if cid == b'SIZE': size = struct.unpack('<3I', d[body:body+12])
        elif cid == b'XYZI':
            n = struct.unpack('<I', d[body:body+4])[0]
            for i in range(n):
                x, y, z, c = d[body+4+i*4:body+8+i*4]
                vox[(x, y, z)] = c
        elif cid == b'RGBA':
            for i in range(clen // 4): pal.append(tuple(d[body+i*4:body+4+i*4]))
        p = body + clen + chlen
    return size, vox, pal

def main():
    dst = 'D:/zlj_world/VoxelCraft/Assets/Resources/VoxStructures'
    fails = []
    for fn in sorted(os.listdir(dst)):
        if not fn.endswith('.bytes'): continue
        name = fn[:-6]
        size, vox, pal = load(os.path.join(dst, fn))
        W, H, D = size
        # 1. size sanity
        flat_ok = name in FLAT_OK  # thin/flat pieces: 1-deep signs, 1-tall pavement
        hmax = 64 if flat_ok else HEIGHT_WHITELIST.get(name, 16)  # flat strips may be long (road paint)
        if W < (1 if flat_ok else 2) or D < (1 if flat_ok else 2) or H < (1 if flat_ok else 2) or H > hmax:
            fails.append(f'{name}: bad size {W}x{H}x{D}')
        if len(vox) == 0:
            fails.append(f'{name}: EMPTY')
            continue
        # 5. base layer non-empty (stands on ground)
        base_n = sum(1 for (x, y, z) in vox if y == 0)
        if base_n == 0:
            fails.append(f'{name}: base layer empty (floats)')
        # 2+3. color census + roles
        cc = Counter(pal[c][:3] for c in vox.values() if c < len(pal) and pal[c])
        roles = Counter(BLOCK.get(rgb, f'@{rgb}') for rgb, n in cc.items() for _ in [n])
        distinct = len(cc)
        min_colors, required = EXPECT.get(name, (1, set()))
        summary = ', '.join(f'{BLOCK.get(rgb, "@"+str(rgb))}:{n}' for rgb, n in cc.most_common(5))
        if name in EXPECT and distinct < min_colors:
            fails.append(f'{name}: SINGLE-COLOR COLLAPSE ({summary})')
        missing = required - set(roles)
        if missing:
            fails.append(f'{name}: missing roles {sorted(missing)} ({summary})')
        print(f'ok  {name}: {W}x{H}x{D} vox={len(vox)} base={base_n} | {summary}')
    if fails:
        print('\nFAIL:')
        for f in fails: print(' -', f)
        sys.exit(1)
    print('\nVOXLINT PASS')

if __name__ == '__main__':
    main()
