# VOX 结构处理管线（mmmm → VoxelCraft .bytes）

> 2026-10-01 建立。血泪教训：树叶绿被映射到 Grass（土色树）、Z-up 模型没换轴（栅栏平铺成地砖、路灯躺倒）、38 高路灯戳穿区块顶（IndexOutOfRange 崩溃）。

## 管线总览

```
mmmm 原件 (.vox, D:/zlj_world/_refs/mmmm/vox/obj_*.vox)
  → Python 转换 (scratch/remap_props.py, 逐件 CFG)
  → Resources/VoxStructures/<name>.bytes (仍是 .vox 格式, TextAsset)
  → 运行时 VoxStructure.Parse() 解析
  → BlockForColor() 最近邻锚色 → BlockType
  → TerrainGenerator.StampStructures() 撒入世界
```

## 转换规则（每件必过）

1. **up 轴逐件判定，不许全局猜**。mmmm 混用 Y-up 和 Z-up：
   - 逐层打印切片（y 扫描 z 投影），看模型"立着"还是"躺着"
   - 立着的竖轴在 Z → swap(y,z)：tree1-4、planter、stlight、house 系列
   - 竖轴本来就在 Y → 不换：fence2（16×2×18 的 2 格高绿篱）、trashcan（6×5×6 方桶）
   - **换轴判据是形状语义（树干细、灯杆细），不是"z 跨度 > y 跨度"这种数值规则**——fence2 的 z 跨度 18 > y 跨度 2，但它本来就是 Y-up 的
2. **高度硬上限 16**（ChunkHeight=80，山顶 baseY 可到 ~60，baseY+H+1 必须 ≤79）。超了就 sub=2 减半（stlight 38×4×32 → swap → 19×16×2）。tree2 H=19 是已知例外，靠 StampStructures 的 y 越界 guard 兜底
3. **调色板按角色重映射，不是最近邻碰运气**（锚色 = BlockForColor 表里的 Color32，改表必须两边同步）：
   - 叶绿 (100,204,0) → Leaves (96,168,60)
   - 树干卡其 (152,152,100) → Log (103,82,49)
   - 中灰 (136/168) → Stone (125,125,125)；深灰 (68/70) → **按件决定**（0d51d97）：trashcan/stlight 的深灰 → WoolBlack(32,32,38) 保层次，别全塌成 Stone
   - 灯头浅色 (204,204,152) → Glowstone (252,224,130)
   - 红 (252,0,0) → WoolRed (176,46,38)
   - **语义配色 > 原件配色**：fence2 原件整件纯灰 (136,136,136)×130，按"绿篱"语义整件重染 Leaves——原件颜色不表达语义时就别透传
   - **ROLE.get(p0,p0) 只做精确匹配**：源调色板对不上就整个透传再被最近邻全落 Stone（"全是一个颜色"的根因）。每换一批源文件必须先 dump 源调色板（remap_props.py 里的 load()），对着实际 RGB 写映射，不许沿用上一批的 ROLE
4. **M40 教训:灰阶主墙必须逐楼分色,不许全落 Stone**(35cd98c):
   - mmmm 店铺/住宅源**每栋 ~4 万体素的墙共用一个 (136,136,136)**,最近邻=Stone,
     ROLE 只保特征色时整条街仍是灰盒子(vision 量化:灰墙 80-90%,彩色只剩细条)
   - 修法=**per-building MAIN WALL 映射**:同一灰按楼指定 Brick/Plank/Sand/WoolWhite,
     留一栋 Stone 作对照;屋顶/饰线灰阶 (168/116/84) 拆到白饰条/Cobble 与墙拉开
   - vox_lint EXPECT 门禁同步改墙色角色(砖楼必须出 Brick),单色塌缩直接 FAIL
   - 一批里每栋楼的主墙色不许重复超过 2 栋,否则街景俯视仍单调
5. **sub=2 减半用多数决投票，但稀有重点色（灯头 12 voxels）必须优先保留**（PRIORITY 集合），否则 Glowstone 被灰色票数吞没、census 里直接消失
6. **mmmm 的 house/store 全是 64×64 排屋模块**（小身子 + 巨型拼楼盖板），单放=蘑菇形。要么裁身子+自建坡顶（house5：裁 x20..43/z0..23 身子 /2 → 12×8×12 + 程序砖顶），要么不用

## 撒入规则（TerrainGenerator.StampStructures）

- 锚点哈希必须掺结构名，否则所有结构共用同一批 30% 格子互相叠
- 门禁（平地容差）= max(6, W/2)；整地平台取足迹最高点为 base
- 泥土裙边逐级收窄（每格降 1，最多 5 宽）
- 泥土/空气带只碰自然方块（Stone/Dirt/Grass/Sand/Gravel/Water/Snow/Leaves/Log），不埋先撒结构的家具
- 所有对 chunk.blocks 的索引必须带 `y < 0 || y >= ChunkHeight` guard

## 验证清单（改完任何 .bytes 必须）

0. **`python Tools/vox_lint.py`（离线门禁，Unity 之前跑）**：尺寸/高度≤16、底面非空、
   每件期望角色齐（stlight 必须有 Glowstone、trashcan 必须 Stone+WoolBlack……）、
   多件道具禁单色塌缩。退出码非 0 = 禁止进 Unity。新增道具必须同步 EXPECT 表
1. Python 解析 .bytes：尺寸、每色 voxel 数、逐层切片形状（确认立着、有干有冠）
2. SelfTest 58/58 全绿；M31Snapshot 内建 ROLE-GATE（census 缺角色直接 FAIL）
3. M31Snapshot 单拍特写（一物一镜：放置→视锥公式相机→渲染→移除）：
   `dist = max(extV/tan(fovV/2), 对角/2/tan(fovH/2)) × 1.25`
4. PIL 像素仲裁（vision 超时/可疑时）：非天空像素 >12% 且质心在画面中央带
5. 视觉复核四项：主体完整、颜色对、落地贴合、无悬空无深埋

## 截图台铁律（M32 排查 5 轮的血泪）

1. **attach 必须幂等 + 销毁旧 mesh**（ccf625b）：`root.transform.Find(C{cx}_{cz})`
   复用同一 GameObject、替换 mesh 前把旧 mesh `DestroyImmediate` 掉。否则每次
   remesh 叠一个新 GameObject，**初代网格（带世界树）永远留在场景里**——表现为
   "怎么 wipe 都有树穿出来、census 干净但画面脏"（活数据干净、渲染画的是旧层）
2. **天空摄影棚**：平台抬到 base+55（受 `ChunkHeight-20` 封顶），整柱 wipe
   （studioY-16..79）后再铺 Grass，背景纯天空；相机 dir=(-0.75,0.45,-0.75)
   下压 45°，地平线/远处地形出画
3. **PIL 仲裁门槛**：上部 1/3 绿像素 <3%（树道具除外）= 无穿出的叶柱
4. 道具 census（画面判定前的数据判定）：fence2=Stone:130、stlight=Stone:88、
   trashcan=Stone:120、planter=Stone:102+Log:24+Leaves:422（含绿植本体）

## 当前清单（.bytes 尺寸 × 换轴 × 配色）

| 名字 | 尺寸 | 轴 | 主要方块 |
|---|---|---|---|
| tree1 | 8×15×8 | swap | Leaves 544 + Log 24 |
| tree2 | 10×19×10 | swap | Leaves 894 + Log 24 |
| tree3 | 10×16×10 | swap | Leaves 544 + Log 24 + WoolRed 9 |
| tree4 | 6×10×6 | swap | Leaves 266 + Log 8 |
| fence2 | 16×2×18 | 原生 Y-up | Stone 130（2 格高绿篱） |
| stlight | 19×16×2 | swap+减半 | Stone 88 + Glowstone 12 |
| trashcan | 6×5×6 | 原生 | Stone 120 |
| planter | 8×13×8 | swap | Leaves 422 + Stone 102 + Log 24 |
| cottage | 11×8×9 | — | Plank/Glass（M31 手建，非 mmmm） |
| house5 | 12×12×12 | — | Sand/Brick/Log（裁 mmmm 身子+程序坡顶） |
| obj_store01 | 32×12×32 | z-up | Brick 墙+Glass 橱窗+黑招牌带+黄雨棚 |
| obj_store02 | 32×12×32 | z-up | Plank 墙+WoolGreen 绿带+Glass |
| obj_store03 | 32×12×32 | z-up | Sand 墙+WoolRed 粉招牌带+WoolBlue 门面+Glass |
| obj_store04 | 32×12×32 | z-up | WoolWhite 墙+通高蓝 Glass 带+Glowstone 灯箱 |
| obj_store05 | 32×12×32 | z-up | Brick 墙+WoolWhite 饰+Glass |
| obj_house1 | 32×12×32 | z-up | Stone 墙(对照楼)+Glass |
| obj_house2 | 32×12×32 | z-up | WoolWhite 墙+Cobble 基座+Glass |
| obj_house6 | 32×12×26 | z-up | Sand 墙+WoolWhite 饰+Plank 木+Glass |
| obj_story01 | 16×10×30 | z-up | Plank 墙+Glass+WoolBlack |
| obj_story02 | 16×10×31 | z-up | Brick 墙+Glass+WoolYellow 橙雨棚 |

## 批4清单（2026-10-02，104 件道具 + store16 楼，d56668c）

- **store16**： 32×12×32，Sand 墙+Glass+Stone 勾边（convert_buildings 管线；顺带修 ANCHOR[nearest_block(rgb)] 元组键错 → BLOCK 补 Dirt/Cobble/Snow 锚色）
- **变体件 ROLE 铁律**： 字母后缀变体的颜色映射**必须回源 dump 真实调色板**再写 RGB 键——猜色值=静默 no-op（arcade2-5/container3-4/tree1b-c/tree2b-c/table3a/table3b/mailbox2b/mushroom3/curb7a 全中招）。dump_pal.py 一把出全部变体色
- **贴地平面件豁免**： driveway/fence3/path1/tracks2/wall/curb/sidewalk/street 类 D==1 或 H 大的地面漆件，flat_ok 时 H 上限放宽到 64（长条路面躺着是对的）；竖立件仍走 HEIGHT_WHITELIST
- 104 件明细： arcade1-5 / bench1,2,5 / boxingring / cart1b,cart2a,cart2b / celltower / chair2 / christmas1 / column2-3 / container2-4 / cross / curb1,3-8,7a / door1-4 / fence3-7 / grave1-4 / guitarcase / halo / mailbox2a,2b / mushroom2,3 / newsbox3-4 / park_block / path1 / pentagram / planter2,3a,3b / playgrnd2-5 / potty1-3 / rubbish2-4 / sidewalk1,3-5 / sign2-9 / statue2-3 / stlight2-3 / street1-2 / stretcher / table2,3,3a,3b / tracks1-2 / trashcan1,3,4 / tree1a-c,2a-c / wall / driveway1-3 / armgate2
- lint 180/180 exit=0；SelfTest 31 楼 PASS；M40PropsSnapshot 133 件 census 无 ZERO；vision 疑点 15 处全数值排除（空格候选=贴地平面件、倒伏候选=低矮/散落设计）
- **批5（2026-10-02 终批，46 变体 + 13 零散，全部 obj_ 接入完毕）**：
  - 变体源（字母后缀）**不带 RGBA 块**→convert_buildings 原 load() 用 `d.find(b'RGBA')` 返回 -1 读到垃圾调色板（house7b 曾整栋 WoolBlack）→load() 补默认 MV 256 色表 fallback（复用 remap_props.DEFAULT_PALETTE）
  - 变体 ROLE 按本体模式+dump 真调色板写，46 栋主墙互异（house1a Brick/1b Stone/1c Plank…house7a WoolBlue/7b WoolYellow/7c WoolRed）
  - 零散件：armgate1/candle/crosswalk/fire1-5/mailbox/policetape/splatter1-3；fire4/5 烟雾→Cobble+CoalOre（lint BLOCK 表补 (70,70,70):CoalOre）
  - FLAT_OK 再扩：candle/fire1-5（H<2 微型件 W 下限放宽）、policetape/splatter1-3（1 高贴地贴花）
  - lint 239/239 exit=0；SelfTest 78 楼 PASS（31 本体+46 变体）；M40PropsSnapshot 146 件 census 无 ZERO；`comm` 复核 _refs obj_ 全集 vs done = **0 件剩余**
- **批6/6b（2026-10-02，交通全量：火车 7 + 公路车 30 + 立交桥/隧道手建 2）**：
  - veh_ 源接入：remap_props.cands 放宽支持 veh_ 前缀，但**必须精确名优先**（`bus` 前缀碰撞匹配到 obj_busstop、`fire` 碰到 obj_fire1，census 全错才暴露）→ exact-first 修复
  - chr_bridget 数值拆穿=人物模型（肤色 138+棕衣 100），不是桥；scene_depot1-3 竖直分层证明无架空层/下穿 → **mmmm 无立交/隧道源**
  - overpass1/tunnel1 手建（官方方块程序化，不仿制贴图）：32×10×16 双墩+双侧引道+铁艺栏杆；32×12×16 山体穿洞+砖砌门脸。数值验收：隧道 6 中轴全贯通、桥跨中 y<6 净空=0
  - lint BLOCK 补 (136,126,126)Gravel/(216,175,147)IronOre/(120,120,120)StoneBrick/(121,85,58)Dirt
  - lint 278/278 exit=0；SelfTest PASS（80 件大名单）；M40PropsSnapshot 185 件 census 无 ZERO



## BlockForColor 锚色表（VoxStructure.cs，转换脚本必须同步）

Grass(106,170,64) / **Leaves(96,168,60)** / Dirt(121,85,58) / Stone(125,125,125) / Sand(219,207,163) / Log(103,82,49) / Plank(156,127,78) / Cobble(110,110,110) / Glass(200,220,228) / Snow(240,246,246) / Brick(150,97,83) / CoalOre(70,70,70) / IronOre(216,175,147) / GoldOre(250,238,77) / DiamondOre(93,236,245) / Gravel(136,126,126) / Ice(160,210,255) / Obsidian(20,18,30) / MossyCobble(110,130,100) / StoneBrick(120,120,120) / Glowstone(252,224,130) / Path(152,121,85) / WoolWhite(232,236,238) / WoolRed(176,46,38) / WoolYellow(234,195,55) / WoolBlue(53,87,178) / WoolGreen(86,128,40) / WoolBlack(32,32,38)
