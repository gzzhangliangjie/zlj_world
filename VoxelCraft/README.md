# VoxelCraft —— 类 Minecraft 体素沙盒（Unity 2022.3）

一个完全离线、零外部包依赖的体素沙盒游戏：区块化大世界流式加载、第一人称移动、
方块破坏与建造、真实免费材质与音效、方块化动物与第三人称角色。

> 🎮 **在线试玩（网页版）**：https://gzzhangliangjie.github.io/zlj_world/
> （GitHub Actions 自动部署：main 分支推送构建产物后约 1~2 分钟更新）

## 快速开始

1. 安装 **Unity 2022.3.57f1c2**（含中国版）via Unity Hub；
2. Unity Hub → Open → 选择本目录 `VoxelCraft`（首次打开会导入资产，约 1~2 分钟）；
3. 双击 `Assets/Scenes/Main.unity`，按 **Play** —— 无需任何手工配置，游戏自动自举启动。

> 提示：进入游戏后鼠标自动锁定，按 **Esc** 解锁并显示操作说明面板，点击画面恢复。

## 操作键位

| 按键 | 功能 |
|---|---|
| W A S D | 移动 |
| Shift | 疾跑（7 m/s） |
| Space | 跳跃（约 1.25 格）/ 飞行上升 / 游泳上浮 |
| Ctrl | 飞行下降 |
| F | 切换飞行模式（11 m/s） |
| V | 第一 / 第三人称切换（方块化角色） |
| T / E | 打开 / 关闭**道具栏**（点选或按 1-5 切换工具，右侧显示背包；Esc 也可关闭） |
| 鼠标左键（按住） | 破坏方块（0.22s 连发，音效随材质变化） |
| 鼠标右键 | 放置当前方块（防卡入身体） |
| 1-9 / 滚轮 | 选择热栏槽位 |
| Tab | 热栏翻页（第 2 页：雪/砾石/冰/黑曜石/苔石/石砖/矿石） |
| Q | 在草地/泥土上播种（对准地面） |
| G | 切换创造模式（无限方块） |
| Esc | 暂停面板 / 解锁鼠标 |

## 世界特性

- **无限地形**：视野半径 7 区块（约 112 米）流式加载，每帧生成预算 ≤8ms，远处以雾自然遮蔽；
- **生物群系**：草原 / 山地（y≥46 积雪）/ 沙漠 / 湖泊海洋（海平面 y=30，寒带湖面结冰）/ 沙滩；
- **地下**：煤层（y<52）、铁矿（y<42）、金矿（y<25）、钻石（y<16）、砾石团、深域黑曜石、y=0 基岩（不可破坏）；
- **植被**：确定性橡树（跨区块无缝）、沙漠无树、雪山无树；
- **动物**：猪 / 牛 / 羊 / 鸡 在玩家附近草地游荡（方块化模型 + MC 格式真实皮肤 + 代码动画，鸡有喙）；
- **同种子可复现**：修改 `VoxelGameRoot → Game` 组件的 `Seed` 字段重进 Play 即换世界。

## 参数调节（Inspector 中选中 VoxelGameRoot）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Seed` | 1337 | 世界种子 |
| `View Radius` | 7 | 视野区块半径（5=性能优先 / 9=画质优先；雾距自动跟随） |
| `Sky Color` | 淡蓝 | 天空与雾颜色 |

更多底层常量（跳跃力、游速、树密度、雪线、矿物概率等）见：
`Scripts/Player/PlayerMotor.cs`、`Scripts/Gen/TerrainGenerator.cs` 顶部常量区。

## 素材许可与署名（重要）

本项目使用以下**免费开源素材**，遵循知识共享 署名-相同方式共享 许可：

- **方块贴图**（`Assets/Resources/Textures/*.png.bytes`，24 张 16×16）：
  来自 [VoxeLibre](https://github.com/MineClone2/MineClone2)，其核心材质为
  **Pixel Perfection** 资源包（作者 **XSSheep**，[来源](https://www.planetminecraft.com/texture_pack/131pixel-perfection/)），
  许可 **CC BY-SA 4.0**；其余为 CC BY-SA 3.0。修改说明：grass_side 由 dirt + 草边叠加合成、
  water 取自水面动画首帧，其余未改动。
- **音效**（`Assets/Resources/Sounds/*.ogg`，77 个）：来自
  [minetest_game](https://github.com/minetest/minetest_game)，作者 celeron55 (Perttu Ahola)
  及贡献者，许可 **CC BY-SA 3.0**。
- 完整许可文本见 `Assets/ThirdPartyNotices/`（NOTICE.md + 两份原版许可）。
- 动物/角色皮肤（`*_skin.png.bytes`）：动物皮肤取自 VoxeLibre `mobs_mc`（CC0 / CC BY 3.0 / CC BY-SA 4.0），
  角色皮肤取自 [simple_skins](https://github.com/qwertysmeerkaas/simple_skins)（MIT，作者 TenPlus1）；
  图集兜底贴图为本项目**程序化生成**（无版权限制）。

## 换装指南（贴图三层供给）

1. **默认**：使用上述内置真实贴图；
2. **兜底**：任何缺失槽位自动程序化绘制（日志会提示），保证任意机器可运行；
3. **整体换肤**：把任意 16×16 PNG 改名为 `grass_top.png.bytes`、`stone.png.bytes` 等
   放入 `Assets/Resources/Textures/` 即替换对应方块（同名覆盖；删除文件自动回退）。
   可用槽位名：`grass_top grass_side dirt stone sand log_side log_top leaves water plank
   cobble glass snow brick bedrock coal_ore iron_ore gold_ore diamond_ore gravel ice obsidian
   mossy stone_brick`。

## 工程结构与代码导读

> 📄 **技术实现细节查阅手册**：[`Docs/TECHNICAL.md`](Docs/TECHNICAL.md) —— 算法公式、数据结构、调度策略、踩坑记录、扩展配方全覆盖。

```
Assets/Scripts/
├─ Core/     VoxelMath(坐标/常量)  BlockType/BlockDatabase(方块注册表)
│            GameBoot+Game(自举组合根：贴图/材质/天空/玩家/世界/动物装配)
├─ Gen/      Noise(Perlin fBm+整数哈希)  TerrainGenerator(高度/群系/矿藏/树木)
├─ World/    Chunk(体素存储)  ChunkMesher(面剔除+AO+水面下沉)
│            WorldSim(纯C#流式模拟：预算调度/邻块门控/DDA射线)  WorldRoot(Unity胶水)
├─ Player/   PlayerMotor(CharacterController)  MouseLook
│            BlockInteraction(破坏/放置/热栏)  BlockAudio(挖掘/脚步音效)
│            ThirdPersonRig(方块化角色+V键视角)
├─ Creatures/ BlockyAnimal(盒体动物+游荡AI)  CreatureSpawner(种群管理)
├─ UI/       Hud(准星/热栏/状态/暂停面板)
└─ Art/      TextureFactory(三层图集)  AudioLibrary(音效分组)
             CreatureTextureFactory+BoxBuilder(程序皮肤/盒体建模)
Assets/Editor/SelfTest.cs   离线自动化回归（31 个用例）
```

设计要点：场景零手工配置（运行时自举）；世界模拟与 Unity 对象分离（WorldSim 纯 C# 可离线测试）；
贴图以 `.png.bytes` 走 TextAsset+LoadImage，不依赖导入器设置。

## 自动化自测（无人值守）

```powershell
& "D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -quit `
  -projectPath "<本项目路径>" `
  -executeMethod VoxelCraft.Editor.SelfTest.RunAll `
  -logFile "<日志路径>"
# 日志中检查：SELFTEST RESULT: PASS（当前 31/31 用例）
```

覆盖：图集完整性、地形确定性、面剔除精确计数、AO、水面下沉、编辑联动、
DDA 射线（轴向/斜向/穿水/射程）、音效分组、矿物生成、模型构建等。

## 发布到网页游玩（WebGL）

本项目支持一键导出为**浏览器直接游玩**的 WebGL 版本（Chrome / Edge / Firefox 推荐）。

### 自动发布（推荐：改代码 → 网页自动更新）

```powershell
powershell -File Tools\publish-webgl.ps1        # 一条命令：构建→提交→推送
```

推送后 GitHub Actions（`.github/workflows/publish-webgl.yml`）自动把
`VoxelCraft/Builds/WebGL` 部署到 Pages，**约 1~2 分钟**后 https://gzzhangliangjie.github.io/zlj_world/ 生效。
也可在仓库 **Actions** 页面手动点 Run workflow 重发。

### 手动一键构建（不发布，仅出包）

```powershell
& "D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -quit `
  -projectPath "<本项目路径>" `
  -executeMethod VoxelCraft.Editor.WebGLBuild.Build `
  -logFile "webgl_build.log"
# 日志出现 WEBGL BUILD OK 即成功；产物在 Builds\WebGL\
```

> 前置条件：编辑器需已安装 **WebGL Build Support** 模块
> （Unity Hub → Installs → 2022.3.57f1c2 → Add Module → 勾选 WebGL Build Support，约 560MB）。
> 已启用 **gzip + 解压回退**：产物可扔到**任意静态托管**，无需配置 Content-Encoding 响应头。

### 本地试运行

WebGL 不能用 file:// 直接打开，需要任意静态服务器，例如在 `Builds\WebGL` 目录下：

```powershell
python -m http.server 8080     # 然后浏览器访问 http://localhost:8080
```

### 托管上线（任选）

- **itch.io**（最简单）：New Project → Kind of project = HTML → 上传 `Builds\WebGL` 打成的 zip → 勾选 "This file will be played in the browser" → Save。
- **GitHub Pages / Gitee Pages / 任意虚拟主机**：把 `Builds\WebGL` 目录内全部文件传到站点根目录即可（解压回退已开启，无特殊 MIME 要求）。

### 浏览器端注意事项

- **首次点击**：浏览器要求用户手势才允许指针锁定/音频——进入页面后按提示点击一次即开始；
- **Esc**：浏览器原生解除指针锁定并显示暂停面板；Chrome 解锁后约 1 秒内再次点击可能无效（浏览器安全限制），稍等再点；
- **Safari**：可玩，但 .ogg 音效在部分 Safari 版本无声（其余正常）；
- **性能**：网页端为 WASM 单线程，低配设备建议把 `Game.viewRadius` 调到 5 再构建。

## 常见问题

- **首次打开很慢**：Unity 在构建 Library（1~3 分钟属正常）；
- **鼠标不能动**：已锁定指针，移动鼠标即转视角；Esc 可解锁；
- **出生在水里**：出生算法会搜索附近草地；极端种子下可按 F 飞行离开；
- **性能不足**：把 `View Radius` 降到 5；
- **打不出方块**：基岩（最底层）不可破坏，属预期。

## 后续扩展路线

存档（JSON 区块增量）→ 洞穴 → 昼夜与平滑光照 → 更多生物与AI → 合成/背包 →
Job/Burst 多线程网格（需联网装包）→ 联机。
