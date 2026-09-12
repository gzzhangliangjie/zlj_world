# VoxelCraft 技术实现文档

> 用途：开发查阅手册。所有内容与代码一一对应，标注了文件与方法名。
> 配套：架构地图与规范见 `.dsh\skills\voxelcraft-guide`；操作与调参见 `README.md`。
> 环境：Unity 2022.3.57f1c2（Built-in 管线 / 旧版 Input / IMGUI / 零 UPM 包）

## 目录

1. [坐标系统与常量](#1-坐标系统与常量)
2. [方块注册表与可见性规则](#2-方块注册表与可见性规则)
3. [噪声与地形生成](#3-噪声与地形生成)
4. [网格生成（面剔除 / AO / 水面）](#4-网格生成)
5. [流式世界调度](#5-流式世界调度)
6. [DDA 体素射线](#6-dda-体素射线)
7. [渲染管线（着色器 / 雾 / 材质）](#7-渲染管线)
8. [玩家控制与出生点](#8-玩家控制与出生点)
9. [方块交互与热栏](#9-方块交互与热栏)
10. [音频系统](#10-音频系统)
11. [生物与第三人称（盒体建模）](#11-生物与第三人称)
12. [贴图三层供给链](#12-贴图三层供给链)
13. [自动化测试基建](#13-自动化测试基建)
14. [性能特征与调优旋钮](#14-性能特征与调优旋钮)
15. [踩坑记录（API / 环境）](#15-踩坑记录)
16. [扩展配方](#16-扩展配方)

---

## 1. 坐标系统与常量

文件：`Scripts/Core/VoxelMath.cs`、`Scripts/Gen/TerrainGenerator.cs`

| 常量 | 值 | 说明 |
|---|---|---|
| `ChunkSize` | 16 | 区块 X/Z 尺寸 |
| `ChunkHeight` | 80 | 区块 Y 高度（单柱区块，无竖切分） |
| `SeaLevel` | 30 | 海平面（水体填充上限，含） |
| `SnowLine` | 46 | 地表起积雪高度 |
| `DesertThreshold` | 0.66 | 生物群系温度>此值为沙漠 |
| `ColdWaterThreshold` | 0.30 | 温度<此值湖面结冰 |
| `MaxTerrainHeight` | 68 | 地形上限（为树冠留 12 格净空） |
| `TreeThreshold` | 0.02 | 树锚点哈希概率（≈0.37 棵/列·栅格采样实测） |

- **体素约定**：方块 `(x,y,z)` 占据世界 AABB `[x,x+1)×[y,y+1)×[z,z+1)`，中心在 `+0.5`。
- **局部索引**：`LocalIndex(x,y,z) = (y*16+z)*16+x`（Y 主序：同柱纵向连续，利于扫描列）。
- **负坐标**：`FloorDiv` / `Mod`（结果恒非负）配套 `ChunkCoord(wx)`；永远不要用 `%` 处理负世界坐标。
- **区块键**：`Chunk.Key = ((long)cx<<32)|(uint)cz`。

## 2. 方块注册表与可见性规则

文件：`Scripts/Core/BlockType.cs`、`BlockDatabase.cs`

- `BlockType` 为 **byte 枚举 0~22**，顺序已冻结（未来存档兼容依赖它，只能追加不能重排）。
- `BlockDef` 字段：`opaque`（遮挡邻面/参与AO）、`solid`（碰撞）、`liquid`、`placeable`、`unbreakable`、`top/side/bottom`（TileId 槽位）、`soundGroup`。
- `FaceTile(normal)`：法线 y>0→top，y<0→bottom，其余→side。

**面可见性判定**（`ChunkMesher.FaceVisible`，交互与测试复用同一规则）：

```
水：    nb==Air || (!nb不透明 && nb!=Water && nb!=Glass)
不透明块：!nb不透明
玻璃类： !nb不透明 && nb!=自身类型     // 同类相邻互剔（玻璃幕墙无内面）
```

22 种方块速查：Grass(草顶/草侧/土底)、Dirt、Stone、Sand、Log(顶底年轮)、Leaves(按不透明处理=快速渲染)、Water(非固体非不透明)、Plank、Cobble、Glass(镂空)、Snow、Brick、Bedrock(不可破坏)、Coal/Iron/Gold/DiamondOre、Gravel、Ice(按不透明)、Obsidian、MossyCobble、StoneBrick。

## 3. 噪声与地形生成

文件：`Scripts/Gen/Noise.cs`、`TerrainGenerator.cs`

### 3.1 Perlin 2D + fBm
- 种子洗牌 256 置换表，复制到 512 避免越界；五次 Fade `t³(t(6t-15)+10)`；8 方向梯度（`hash&7`）；输出约 `[-1,1]`。
- `Fbm(x,y,octaves,lac=2,gain=0.5)`：按振幅加权求和后归一化到 `[0,1]`。

### 3.2 整数哈希（树/矿物等逐格决策）
`Hash01(x,y,seed)`：乘法雪崩链 `374761393 / 668265263 / -2048144777(=0x85EBCA77 位模式)` + `异或>>13` + `×1274126177` + `异或>>16`，取 `&0x7fffffff / 2147483647f`。**Y 维打包进种子**：`seed ^ (y*668265263)` 得到三维确定性。

### 3.3 高度场（`HeightAt`）
```
c  = fBm大陆   (0.0015, 4oct)          // 大尺度陆海
m  = fBm山脉   (0.004, 4oct)
ridge = (1-|2m-1|)²                     // 山脊化
mask = clamp01((c-0.50)*5)              // 只在内陆隆起
r  = fBm粗糙  (0.02, 3oct)
h  = 20 + c*14 + ridge*40*mask + r*4    // clamp [1,68]
```
实测分布（seed 1337，±800m 采样 10101 列）：水面列 24%、雪山列 6.3%、沙漠列 2.3%、草原 31%，min24/max67。

### 3.4 表层与深层规则（`Generate` / `SelectDeepBlock`）
- 表层：`y==0` 基岩；`y==h`：沙滩(h≤31)/沙漠→沙、雪山(h≥46)→雪、否则草；`y∈[h-3,h)`：沙区沙/其它土；再往下进入深层判定。
- 深层单掷哈希（**判定顺序敏感**，黑曜石必须先于砾石，否则被 `roll>0.984` 吞掉——实测踩过）：

| 条件 | 结果 | 概率 |
|---|---|---|
| `roll<0.0015 && y<16` | 钻石矿 | 0.15% |
| `roll<0.0025 && y<25` | 金矿 | 0.25% |
| `roll<0.008 && y<42` | 铁矿 | 0.8% |
| `roll<0.016 && y<52` | 煤矿 | 1.6% |
| `roll>0.9993 && y<12` | 黑曜石 | 0.07% |
| `roll>0.984 && y<h-6` | 砾石团 | 1.6% |

- 水体：`y∈(h,30]` 填水；`y==30 && temp<0.30` 改冰（仅表面层）。

### 3.5 树（跨区块安全）
- 锚点：`Hash01<0.02 && h∈(31,44) && !沙漠`（雪山/沙滩不长）。
- 生成时对区块外扩 **margin=3** 扫描锚点，`TrySet` 只写落在本区内的方块 → 树冠跨界无缝且两侧区块独立生成结果一致。
- 形状：主干 4~6（哈希定高）；叶层在顶端 `dy∈[-2,1]`，半径 2/2/1/1（顶层十字 `|dx|+|dz|≤1`）；外角按 `Hash01<0.5` 确定性修剪；叶子只写 Air，主干可覆盖。

## 4. 网格生成

文件：`Scripts/World/ChunkMesher.cs`（纯 C#，无 Unity 对象，可离线回归）

### 4.1 面表（已验证 CCW 外向）
6 面法线 + 每面 4 角点（前两角为面"底部"，保证侧贴图直立）；UV 固定 `(0,0)(1,0)(1,1)(0,1)` 映射到图集槽位矩形。

```
+X: (1,0,1)(1,0,0)(1,1,0)(1,1,1)   -X: (0,0,0)(0,0,1)(0,1,1)(0,1,0)
+Y: (0,1,1)(1,1,1)(1,1,0)(0,1,0)   -Y: (0,0,0)(1,0,0)(1,0,1)(0,0,1)
+Z: (0,0,1)(1,0,1)(1,1,1)(0,1,1)   -Z: (1,0,0)(0,0,0)(0,1,0)(1,1,0)
```

### 4.2 光照烘焙（顶点色）
- 面向底色：+Y 1.00 / -Y 0.55 / ±X 0.80 / ±Z 0.70。
- **角点 AO**（经典三邻居法）：对面上切向轴 `t1,t2`，按角点 0/1 取 `±1` 方向，采样 `n+t1'`、`n+t2'`、`n+t1'+t2'`（n=面外侧格）；`s1&&s2 → 0`，否则 `3-(s1+s2+c)`；亮度系数 `0.55+0.15*ao`。乘进顶点色 RGB。
- **四边形翻转**：`ao0+ao2 > ao1+ao3` 时用对角线 0-2 三角化，否则 1-3（消除 AO 各向异性暗斑）。
- 水不做 AO，顶点 alpha 固定 0.72。

### 4.3 水面下沉
块上方非水时：+Y 面四角与侧面的 `corner.y==1` 顶点统一降到 `y+0.9`（本体在 `ChunkMesher.Build` 的 `lowerWaterTop` 分支）。自测断言：水区块必含 `frac(y)==0.9` 顶点。

### 4.4 图集与 UV
- 图集 6×4 槽 × 16px = **96×64**，RGBA32、Point 过滤、Clamp、无 mipmap。
- `TileRect` 做 **0.25 纹素内缩**，点采样下杜绝边缘串色。
- Mesh：`IndexFormat.UInt32`（单区块顶点可超 65535）；`MeshData` 缓冲池复用，`ToMesh(mesh)` 原地 Clear+重灌，不新建 Mesh 对象。

## 5. 流式世界调度

文件：`Scripts/World/WorldSim.cs`（纯 C# 核心）+ `WorldRoot.cs`（Unity 胶水）

### 5.1 三环半径
`dataRadius=view+1`（数据环）> `meshRadius=view`（网格环）> `unloadRadius=view+3`。数据环比网格环大一圈是为了**8 邻块就绪门控**（`NeighborsReady`：8 邻 dataReady 才允许构建网格，保证边界剔除与 AO 角点查询跨块正确）。

### 5.2 `Step(centerCx, centerCz, dataBudgetMs, meshBudgetMs, remeshed, unloaded)`
三阶段，各用 `Stopwatch` 预算（WorldRoot 每帧给 **8ms+8ms**）：
1. **数据**：切比雪夫半径内缺失区块按距离排序，预算内逐个 `GenerateChunkData`；
2. **网格**：`dataReady && meshDirty && 在meshRadius && 8邻就绪` 按距离排序，预算内构建 `MeshData` 挂到 chunk，置 `meshDirty=false`；
3. **卸载**：距离>unloadRadius 的区块移出字典，交由 WorldRoot 销毁 GO/Mesh。

排序保证出生方向优先加载；预算保证帧率平滑（首帧同步预热例外，见 8.3）。

### 5.3 编辑联动（`SetBlock`）
写入后本块置脏；邻块 `(dx,dz)∈{-1,0,1}²\{0,0}` 被置脏当且仅当
`(dx==0 || lx 命中对应边) && (dz==0 || lz 命中对应边)`
——即只有贴边的方块才会改变邻块的面/AO，含对角块。`WorldRoot.SetBlockAndApply` 同步重建所有受影响区块（编辑即时反馈，实测单次<10ms）。

### 5.4 地表查询
`SurfaceHeight(wx,wz)`：列内自顶向下第一个非空非液体方块 Y（生物贴地、出生点用）。缺数据返回 -1。

## 6. DDA 体素射线

文件：`WorldSim.Raycast`（Amanatides-Woo 算法）

```
step  = dir≥0 ? +1 : -1（分量为0时 tMax=+∞，步进仍定义但不触发）
tMax  = 到下一格边界的距离 / |dir|
tDelta= 穿越一格的 t 步长 = 1/|dir|
每迭代：px/py/pz 记旧格 → 步进最小 tMax 轴（并列时 X>Y>Z 顺序）→ t>max 则失败
       → 命中非空非液体格：hit=当前格，place=旧格（空气格）
```
- 起点在实体内：直接返回 hit=起点格、place=同格（非法放置位，交互侧自然忽略）。
- 液体穿透（水不可被选中）；512 次迭代上限防死循环。
- 自测 5 用例：轴向命中+放置位 / 空射 / 穿水命中 / 45° 斜射 / 射程裁断。

## 7. 渲染管线

文件：`Assets/Resources/Shaders/BlocksShader.shader`、`WaterShader.shader`、`Core/Game.cs`

### 7.1 BlocksShader（不透明+镂空）
- Unlit vert/frag：`tex2D(_MainTex, uv) * 顶点色`；`clip(a-0.5)` 镂空（玻璃边框保留、中心裁掉——所有不透明块 alpha=1 不受影响）。
- **手动距离雾**：vert 里算世界坐标到 `_WorldSpaceCameraPos` 距离 → frag `lerp(rgb, _VoxelFogColor.rgb, saturate((d-start)/(end-start)))`；参数由 C# 每次启动 `Shader.SetGlobalVector("_VoxelFogRange")/SetGlobalColor("_VoxelFogColor")` 注入。
- `fogEnd = viewRadius*16-8`，`fogStart = fogEnd*0.55`；相机背景色=雾色 → 地平线无缝。

### 7.2 WaterShader
同上 + `Blend SrcAlpha OneMinusSrcAlpha`、`ZWrite Off`、Queue=Transparent；`a = min(纹理a, 顶点a=0.72)`。

### 7.3 材质与光照模型
- 着色器**必须**放 `Resources/` 用 `Resources.Load<Shader>` 加载（`Shader.Find` 在打包时会被裁剪）。
- 光照全部烘焙在顶点色（面向×AO），场景平行光只影响盒体生物/角色（Unlit/Texture 材质的它们也不受影响，风格统一为"全亮卡通"）；实时阴影关闭。

## 8. 玩家控制与出生点

文件：`Scripts/Player/PlayerMotor.cs`、`MouseLook.cs`、`Core/Game.cs`

### 8.1 CharacterController 配置
身高 1.8 / 半径 0.35 / center(0,0.9) / 坡度 50° / **stepOffset 0.4**（方块高 1.0 → 需跳跃上台阶，MC 手感）/ skinWidth 0.03。

### 8.2 运动模型
| 参数 | 值 | 备注 |
|---|---|---|
| walk/sprint/fly | 4.3 / 7 / 11 m/s | 水中×0.6 |
| jump / gravity | 8 / 25 | 跳高 v²/2g≈**1.28 格** |
| 游泳 | 浮力重力 4 / 上浮 4.5 / 最大下沉 3 | 头部入水判定 `feet+0.4` |
- 竖直速度独立持久；地面吸附 `isGrounded→v=max(跳,-2)`；按住空格自动连跳。
- **指针未锁定时整体冻结**（暂停语义）；飞行时空格升 / Ctrl 降。

### 8.3 出生点（`Game.Start`）
- 环形搜索 r≤96 步长 8：找 `h∈(31,44)` 且非沙漠的草列 → 落点 `(sx+0.5, h+2.2, sz+0.5)`。
- `WorldRoot.PrewarmAround`：临时把半径压到 data=1/mesh=0，**同步**生成+建网格 3×3 区块（预算 60s 实际 <100ms），玩家落地即有碰撞体。恢复半径后交给常规流式。

## 9. 方块交互与热栏

文件：`Scripts/Player/BlockInteraction.cs`

- **射线**：相机位置+forward，`reach=6`；命中显示高亮框。
- **高亮框**：12 条 `LineRenderer`（立方体 12 棱），`Sprites/Default` 黑色 α0.85、宽 0.02、盒体 ±0.502 防 z-fight。
- **破坏**：左键按住，`breakInterval=0.22s` 连发；`unbreakable`（基岩）跳过；触发 `OnBreak(type)` 事件。
- **放置**：右键单击；目标格须为 Air/Water 且 `!OverlapsPlayer`（CC.bounds ∩ 体素 Bounds(0.98)）；触发 `OnPlace(type)`。
- **热栏**：2 页×9 槽（页1 经典 / 页2 雪·砾石·冰·黑曜石·苔石·石砖·煤铁金矿），`Tab` 翻页，`1-9`+滚轮选择。
- ⚠ 组件时序：`playerBody` 在 AddComponent 之后才赋值 → `Controller` 属性**惰性解析**，不要在 Awake 缓存。

## 10. 音频系统

文件：`Scripts/Art/AudioLibrary.cs`、`Player/BlockAudio.cs`

- `AudioLibrary.Build()`：`Resources.LoadAll<AudioClip>("Sounds")` 一次载入 77 个 ogg，按 `soundGroup` 映射成 8 组 `{dig[], place[], step[]}`：
  grass/dirt/sand→crumbly；stone→cracky+硬放置音；wood→choppy；glass→break_glass；snow→snappy；ice→ice_dig 全套。
- `BlockAudio`： AudioSource（2D，volume 0.45）；事件**惰性订阅**（同上时序坑，`Update` 里 `!hooked && interaction!=null` 时挂接）；脚步由"脚下方块 soundGroup"驱动，间隔 0.42s 按实际移速缩放（0.5~2.5 倍率）。

## 11. 生物与第三人称

文件：`Scripts/Creatures/BlockyAnimal.cs`、`CreatureSpawner.cs`、`Art/BoxBuilder.cs`、`CreatureTextureFactory.cs`、`Player/ThirdPersonRig.cs`

### 11.1 盒体建模法（与体素世界风格统一的关键）
- `BoxBuilder.Box`：`CreatePrimitive(Cube)` → **销毁 Collider** → 缩放到目标尺寸 → Unlit 材质（`Shader.Find("Unlit/Texture")`，失败回退 Legacy Diffuse）。
- `CreatureTextureFactory`：按 `species:part` 缓存材质；16×16 程序像素画（底色+噪点+白斑/脸谱：双眼/猪鼻/鸡喙/刘海），Point 过滤。
- 四肢用**髋/肩枢轴**（空物体）+ 偏移子盒实现摆动，旋转 pivot 不在盒心。

### 11.2 动物 AI（`BlockyAnimal.Update`）
- 状态机：idle(2~5s) ↔ walk(3~7s，随机目标朝向)；朝向 `Slerp 3/s`。
- 贴地：前进前查 `SurfaceHeight(前视格)`；`ground<SeaLevel` 或前方水 → 随机转向（避水避崖）；y=ground+1.02。
- 动画：`phase += dt*(4+move*3)`；腿摆 `sin(phase)*0.55rad`，对角同步 `(i==0||i==3)?+1:-1`；头微摆。
- 尺寸表：pig 0.8×0.55×1.05/leg0.36；cow 0.9×0.65×1.15/0.45；sheep 略小；chicken 0.42 系。

### 11.3 种群管理（`CreatureSpawner`）
每 1s 扫描：距离>70m 销毁；数量<10 时在玩家周围 18~42m 环上试 12 次找"草地 + h∈(31,44)"生成随机物种。

### 11.4 第三人称（`ThirdPersonRig`）
- Steve 式盒体（身0.5×0.72×0.26 / 头0.46 / 四肢枢轴摆动），**默认隐藏**（第一人称）。
- `V` 切换：相机在 `Head` 子节点本地坐标 `(0,0,0)↔(0,0.38,-3.4)` Lerp（10/s）；模型仅 TP 时 SetActive。
- 摆幅由**实际水平速度**驱动（`0.7rad·sin`，双臂双腿交叉相位）。

## 12. 贴图三层供给链

文件：`Scripts/Art/TextureFactory.cs`

```
① Resources/Textures/<name>.png.bytes   TextAsset→LoadImage→GetPixels32→最近邻重采样16×16
② 程序化绘制（PaintProcedural 24 种：噪点/条纹/年轮/砖砌/圆石团/矿斑/冰裂/玻璃框…）
③ 换装：同名 .bytes 覆盖即换，删除自动回退②
```
- **行序坑**：`GetPixels32` 本身就是**自底向上**行序，与画布约定一致——加载时**不要再翻转**（曾翻转导致草侧草皮条朝下，已修）。
- 图集灌入用 `SetPixels32(x,y,w,h,canvas)`（该 API 支持块写）；**读取无块重载**（`GetPixels32(x,y,w,h)` 不存在，只能整读手裁——图标走 `GetPixels32()` 全图+行拷贝）。
- 图标：每方块 16×16 独立 Texture2D（HUD 用）；`white` 2×2 白图（准星）。
- 外部命中数在自测断言（当前 24/24），缺失自动降级并留 `proceduralTiles` 清单。

## 13. 自动化测试基建

文件：`Assets/Editor/SelfTest.cs`（30 用例）+ 技能 `unity-batchmode`

```powershell
& "D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -quit `
  -projectPath "<proj>" -executeMethod VoxelCraft.Editor.SelfTest.RunAll -logFile "<log>"
# 判据 = 日志行 "SELFTEST PASS/FAIL <case>"，绝不只看进程退出码
```
- 用例矩阵：M0 编译骨架 / M1 图集(24外部·尺寸·图标·槽位·像素)·着色器 / M2 确定性(逐字节)·树·群系·高度界 / M3 面剔除(**与独立复算逐面相等**——SelfTest 内置一份规则复制品交叉验证)·AO 多级·水面下沉·编辑联动·卸载 / M5 射线 5 例 / M7A 音频·M7B 矿物·冻湖·热栏·M7C/D 模型。
- 纯 C# 路径（WorldSim/Chunk/TerrainGenerator）可完整离线回归；MonoBehaviour 运行时行为靠 `AddComponent`+结构断言（如盒体子节点数）覆盖。

## 14. 性能特征与调优旋钮

- **实测参考**（seed1333 原点 3×3）：固体面 5471 + 水面 1874 / 9 区块（平均 ~820 面/区块）；AO 使顶点色出现 11 档亮度。
- 单区块体素 20,480 字节；网格重构建毫秒级；`Step` 双 8ms 预算下常规帧无尖峰。
- 旋钮：`Game.viewRadius`（5/7/9）；`WorldRoot.Update` 的预算参数；`PlayerMotor` 全参数；`TerrainGenerator` 常量区（群系/矿率/树密度）；雾距自动随 viewRadius。
- 内存注意：`wanted/meshable` 列表为字段级复用零分配；卸载路径 Destroy GO+Mesh 防泄漏。

## 15. 踩坑记录

**Unity API（2022.3）**
- `GetPixels32` **无** (x,y,w,h) 块重载（`SetPixels32` 有）。
- `Vector3.Horizontal/Vertical` 是 Unity 6 API，2022.3 没有（手写 y=0）。
- `Texture2D.GetPixel` 只收 int；浮点用 `GetPixelBilinear`。
- C# 字面量超 int.MaxValue 会升级成 uint/long 破坏 unchecked 链（`2246822519`→写 `-2048144777`）。
- 嵌套类引用要全名（`TextureFactory.AtlasResult`）。
- `AddComponent` 当帧即触发 Awake/OnEnable → **引用字段在 AddComponent 后赋值的一律惰性获取**（事件订阅/组件缓存两处真实踩过）。
- `GetComponentsInChildren` 默认**不含未激活**对象（玩家模型测试），要 `(true)`。
- 着色器资源必须 `Resources.Load`（`Shader.Find` 打包被裁）；`Sprites/Default`、`Unlit/Texture` 为内置常驻，编辑器/开发版无碍，正式打包建议加入 Always Included Shaders。

**环境/流程**
- ProjectVersion.txt 版本串必须 `(hash)` 括号格式（写成 `2022.3.57f1c2_hash` 会让 Unity 秒退 exit 140063 且不留日志）；从 `(Get-Item Unity.exe).VersionInfo.ProductVersion` 取真值。
- 批处理前先"裸跑"一次预热许可客户端；`Start-Process -Wait` 可能被 LicensingClient 句柄卡死 → 用**限时轮询 HasExited** 模式。
- 沙箱阻断 PowerShell HTTP 栈与输出重定向，但 git 自带 HTTP 栈可用（GitHub 资源获取通道）；`Select-Object -First N` 截断原生命令管道会产生非零退出码误报。
- unitypackage 的 pathname 文件内容为"路径\n00"两行，按行拆分后再统计。
- 代码内禁用中文字符串（IMGUI 默认字体/编码安全），文档中文写在 md。

**逻辑类**
- 矿物 else-if 阈值区间重叠会吞分支（黑曜石被砾石前件吞掉，靠自测 obsidian=0 暴露）。
- 玻璃/水互邻的面规则要显式互剔，否则透明面叠加闪面。
- 树冠跨界必须 margin 扫描 + 本块裁剪写入，否则边界树缺半边。

## 16. 扩展配方

**加方块**：`BlockType` 追加枚举 → `BlockDatabase` 加行（贴图槽/透明/碰撞/音组）→ `TextureFactory` 槽位表加名（或放 .bytes）→ 热栏数组加槽 → （需要自然生成才动 TerrainGenerator）。

**加生物**：`CreatureTextureFactory.Paint` 加 case → `BlockyAnimal.GetDims` 加尺寸 → `CreatureSpawner.Species` 加名。

**换世界风格**：整体替换 `Resources/Textures/*.png.bytes`（24 个标准名，README 有清单）。

**存档（未实现，设计建议）**：`WorldSim.SetBlock` 处挂增量字典 `(chunkKey → List<(idx,block)>)`，JSON 序列化到 `Application.persistentDataPath/world_<seed>.json`；加载时在 `GenerateChunkData` 后回放。

**Job/Burst 多线程**：`ChunkMesher.Build` 已是纯函数（Chunk+邻居只读），可平移到 IJob；需联网装包，当前离线环境不可用。

**平滑光照/昼夜**：顶点色已承载光照，可扩展为"天空光列传播 + 面亮度表"；雾色/天色改随时间插值即可出昼夜。

## 17. WebGL 网页发布

### 17.1 前置：模块与安装
- WebGL 构建依赖编辑器模块 `WebGL Build Support`（本机初始未装，模块包 587MB，
  国内 CDN 直链见 `modules.json` 的 `webgl` 条目）。
- 静默安装：`UnitySetup-WebGL-Support-for-Editor-2022.3.57f1c2.exe /S /D=D:\Unity\2022.3.57f1c2`
  （NSIS 风格，`/D` 与路径之间**等号相连不加引号**），安装到 `Editor\Data\PlaybackEngines\WebGLSupport`。

### 17.2 打包裁剪规避（本次为出包做的关键改造）
原 `Shader.Find("Sprites/Default")`（高亮线框）与 `Shader.Find("Unlit/Texture")`（生物/角色材质）
在无引用打包时会被裁剪 → **新增自包含着色器**并改为 `Resources.Load`：
- `Resources/Shaders/LineShader.shader`（Voxel/Line：顶点色无光照，LineRenderer 顶点色直接可用，
  颜色改由 `line.startColor/endColor` 注入）；
- `Resources/Shaders/UnlitTextureShader.shader`（Voxel/UnlitTexture：纹理 + 手动雾，与方块雾一致）。
原则：**运行时用到的着色器一律放 Resources**（见 §7.3）。

### 17.3 构建脚本（`Editor/WebGLBuild.cs`）
- 入口 `VoxelCraft.Editor.WebGLBuild.Build`（batchmode 调用，README 有命令）。
- 版本兼容：压缩/解压回退/内存增长等 PlayerSettings **API 名称随小版本漂移**
  （`webGLCompressionGzip` 等在 2022.3 已不存在）→ 全部走**反射逐属性尝试**，编译期零符号依赖；
  2022.3 实际命中：`PlayerSettings.WebGL.compressionFormat=Gzip`、`.decompressionFallback=True`、`.memoryGrowthMode=Geometric`。
- 判据：日志 `WEBGL BUILD OK` + `WEBGL ARTIFACT` 产物清单；失败抛异常（进程非零退出）。
- **实测**（本机首建）：190 秒 / 0 错误 / 总 9.48MB（`WebGL.wasm.unityweb` 6.55MB、
  `WebGL.data.unityweb` 2.78MB、loader 44KB、index.html 5.4KB）；上传包
  `Builds/voxelcraft-webgl.zip` 可直接投递 itch.io。

### 17.4 运行时行为差异（浏览器特有）
- **指针锁定需用户手势**：WebGL 下启动时的 `Cursor.lockState=Locked` 会被浏览器拒绝 →
  HUD 的"点击继续"面板恰好成为必需的首个手势，流程自洽；
- **Esc** 由浏览器原生解锁（键事件可能不送达 Unity），暂停面板按 `lockState` 状态显示，兼容；
- Chrome 解锁后 ~1s 内拒绝再次锁定（安全策略），属已知体验瑕疵；
- **音频**：.ogg 依赖浏览器 Vorbis 解码，Chrome/Edge/Firefox 正常，**部分 Safari 无声**；
- 单线程 WASM 性能约为原生 1/2~1/3：8ms 预算制天然适配（帧内做不完就顺延下一帧），
  低配建议 `viewRadius=5`；`targetFrameRate` 在 WebGL 由 RAF 驱动，设置无副作用。

---
*文档版本：1.1（新增 §17 WebGL 发布）。代码行数 3502+/24+ 文件；回归 30/30 PASS。*
