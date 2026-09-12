# VoxelCraft 开发路线图（类 Minecraft 体素沙盒 · Unity）

> 版本：v1.3 ｜ 状态：**待用户最终验收**（v1.3 变更：新增风格 B 贴图套装与风格 A/B 选择；DSH 辅助技能已安装生效）
> 目标目录：`D:\zlj world\VoxelCraft` ｜ 性能取向：用户已确认 **平衡**（视野半径 7 区块 ≈112m + AO 开）

---

## 1. 环境勘察结论（已实测，非推测）

| 项目 | 结果 | 影响 |
|---|---|---|
| Unity 2022.3.57f1c2 LTS | ✅ 已安装于 `D:\Unity\2022.3.57f1c2\Editor\Unity.exe` | **锁定为目标版本**（另装 2023.1.4f1 备用） |
| Unity 许可证 `Unity_lic.ulf` | ✅ 存在（C:\ProgramData\Unity） | 可无人值守批处理编译验证 |
| Unity Hub 3.3.3 / dotnet / git | ✅ 可用 | 无阻碍 |
| GitHub 网络通道 | ✅ **git 通道可达**（用户确认可登录；实测 `git ls-remote`/sparse clone 成功；HTTP 栈被沙箱阻断不影响） | 可拉取开源资源 ✅ |
| 免费资源库接入 | ✅ 已 sparse-clone `minetest/minetest_game`（241 张贴图 + 77 个音效），所需资源已本地暂存 | 贴图+音效问题解决 |
| 本地 Asset Store 缓存（已验货） | ✅ **Animals FREE**：7 种动物 FBX+30 个 .anim+7 控制器；**Battle Wizard**：绑定角色+38 FBX 动作+控制器 | 可选扩展素材，无需下载 |

## 2. 关键决策

### 决策 A：贴图等素材"拿来用"，核心代码自研（v1.1 修订）
用户建议"网上有现成的更好可以拿来改改用"。结论分两层：

1. **素材层——采用现成免费资源 ✅**：贴图取自 **Minetest 官方资源库 `minetest_game`**（GitHub 免费开源，媒体许可 **CC BY-SA 3.0**：署名+相同方式共享即可自由使用、商用）。所需 14 张 16×16 贴图已全部拉取校验完毕（均为 16×16）：草顶/草侧(已离线合成)/泥土/石头/沙子/原木侧/原木顶/树叶/水/木板/圆石/玻璃/雪/砖。合规措施：README 附作者署名（celeron55/Perttu Ahola 及 Minetest Game 贡献者）+ 项目内随附 LICENSE 副本。
2. **代码层——仍自研 ✅**：GitHub 上的开源体素项目多为教学演示或年久失修，许可证不一（GPL 传染风险）、Unity 版本老旧、耦合严重，改造成本高于自研；本项目核心约 **3500 行 C#** 属成熟算法，自研完全可控、零许可证风险。算法层面参考公开经典方案（0fps 网格剔除/AO、Perlin fBm 地形学）。

### 决策 B：零外部 UPM 包依赖（离线约束）
- 不引用任何包（Burst/Jobs/InputSystem 需联网下载）→ `manifest.json` 置空依赖，只用引擎内置模块；
- **Built-in 渲染管线 + 旧版 Input + IMGUI**——离线零依赖，2022.3/2023.1 双版本兼容。

### 决策 C：三层贴图供给链（v1.1 修订）
1. **第一层（默认启用）**：已下载的 minetest_game 真实贴图，M1 时转为 `.png.bytes` 放入 `Assets/Resources/Textures/`，运行时 `LoadImage` 加载并合成 4×4 图集；
2. **第二层（兜底）**：`TextureFactory` 程序化绘制任何缺失槽位（保证项目在任意机器可运行）；
3. **第三层（未来换装）**：任何人把其它 CC0/免费贴图按同名规范丢进该目录即可整体替换（如换 Kenney 包）。

### 决策 D：场景零手工配置
主场景近乎空场景，全部对象由 `RuntimeInitializeOnLoadMethod` 自举创建（世界/玩家/相机/灯光/UI）。打开项目按 Play 即玩，杜绝"忘了拖脚本"类问题。

## 3. 项目范围

### 本期实现（对应用户三点需求）
1. **大世界创建**：区块化地形流式加载（视野半径 7 区块 ≈112m，Inspector 可调），含山地/平原/湖泊海洋/沙滩/沙漠/雪山/树木，同种子可复现；
2. **人员移动**：第一人称，走/跑/跳、飞行模式、水中游泳，出生点安全落地；
3. **方块搭建**：左键破坏、右键放置、9 格热栏（数字键/滚轮切换）、选中方块黑框高亮、放置不会卡进玩家身体。

### 明确不做（后续路线见 §9）
存档系统、洞穴、昼夜光照/阴影、生物 AI、合成系统、联机、移动端。

## 4. 总体架构

```
RuntimeInitializeOnLoadMethod ──> Game (组合根, 挂 VoxelGameRoot)
   ├─ Art:    TextureFactory (三层贴图供给 → 64×64 图集+图标) 
   │          BlocksShader / WaterShader (顶点色×图集 + 手动距离雾 + 玻璃镂空)
   ├─ Gen:    Noise(Perlin2D + fBm + 整数哈希)
   │          TerrainGenerator(高度图/群系/表层规则/水体/确定性树木, 树冠跨区块安全)
   ├─ World:  Chunk(16×16×80 byte[]) ── ChunkMesher(面剔除+顶点AO+面向光烘焙+水网格)
   │          World(区块字典 / 每帧预算式加载卸载 / 邻块就绪门控 / DDA体素射线)
   ├─ Player: PlayerMotor(CharacterController: 走跑跳/飞行/游泳)
   │          MouseLook(指针锁定视角) ── BlockInteraction(DDA拾取/破坏/放置/热栏)
   └─ UI:     Hud(IMGUI: 准星/热栏/帮助/FPS/种子坐标)
```

数据流：噪声 → Chunk 字节体素 →（邻块查询）→ 网格(顶点/UV/颜色/索引) → MeshCollider 供 CharacterController 碰撞；编辑方块 → SetBlock → 同步重建本块及受影响邻块网格。

## 5. 里程碑计划（每个完成后自动进入下一轮继续工作）

| 里程碑 | 内容 | 交付物 | 自测验收标准（我可独立验证） | 预计轮次 |
|---|---|---|---|---|
| **M0 骨架+编译管线** | 目录结构、ProjectSettings(锁定 2022.3.57)、空主场景+BuildSettings、.gitignore、Editor 自测脚手架、贴图入库(.bytes)+许可副本 | 可打开的项目骨架 | Unity batchmode 打开+编译，日志 **0 个 CS 错误**（顺验许可证） | 1 |
| **M1 方块+图集+着色器** | BlockDatabase(13 种方块：名称/三面贴图槽/不透明/碰撞/可放置)、TextureFactory 三层供给、Blocks/Water 着色器 | 贴图与渲染基座 | 批处理编译过 + 自测：14 张外部贴图全部加载成功（缺一走兜底并报告）、图集 64×64、槽位映射正确 | 1~2 |
| **M2 地形生成** | Noise、TerrainGenerator（大陆/山脊/群系 fBm；草/沙/雪/石表层；水体；确定性树木含跨界裁剪） | 世界数据层 | 自测：同 seed 两次生成**逐字节一致**；水域/雪山/沙漠均出现；区块边界树木无缺失 | 1~2 |
| **M3 网格+流式世界** | ChunkMesher（面剔除/UV/角点AO/面向明暗、水面降 0.9、双材质）、World 调度（每帧 ≤8ms 预算、8 邻块门控、远距卸载） | 世界渲染层 | 自测：完全包裹的实心体内面数为 0；AO 四级明暗出现；远距卸载无残留 | 2 |
| **M4 玩家控制** | PlayerMotor / MouseLook / 出生安全落地（同步预热出生区 3×3 区块） | 可移动角色 | 编译过 + 参数审查（走 4.3 / 跑 7 / 跳 ≈1.25 格 / 飞 12 m/s、水中重力衰减）；碰撞由 MeshCollider+CharacterController 保证 | 1 |
| **M5 建造交互** | DDA 体素射线（≤6 格精准）、黑线高亮框、左键连发破坏(0.22s)/右键放置、防卡入玩家、热栏 1-9/滚轮 | 玩法闭环 | 编译过 + 自测：射线命中/放置格用例（斜向/贴脸/边界）全对；边界放置触发邻块重建 | 1 |
| **M6 验证交付** | 全量批处理回归、默认参数调优、README(中文操作/调参/署名/代码导读) | 最终交付 | batchmode 全量编译 0 错误；向用户逐条给出 §6 验收对照表 | 1~2 |
| **M7 可选扩展**（用户勾选后纳入） | A：方块音效（挖掘/放置/脚步，minetest 77 个 .ogg）；B：扩展方块（矿石×6/冰/黑曜石/砂岩/砾石/苔石/丛林木/松木）；C：动物（本地 Animals FREE 包：鸡/鹿/狗/马/猫/企鹅/虎 漫游+动画）；D：第三人称角色（Battle Wizard + 38 个动作，V 键切换视角） | 按选择交付 | A：对应操作触发正确音效；B：热栏/图集扩展且生成器产出新方块；C/D：场景可见动画实体、无报错 | 1~3/项 |

**合计约 8~11 轮**（上限 12）。每轮结束汇报进度；你随时可打开项目试玩、随时叫停或改需求。

### 自动化验证手段（离线无 GUI 的关键）
`Assets/Editor/SelfTest.cs`：静态方法可被 `Unity.exe -batchmode -executeMethod` 调用，依次执行图集校验（外部贴图命中数）→ 地形确定性比对 → 网格剔除率统计 → 射线用例，结果以 `SELFTEST PASS/FAIL` 写入 Editor.log。每个里程碑全量回归，不依赖人眼。

## 6. 总体验收标准（最终交付时逐条对照，你可直接检验）

1. 用 Unity Hub 以 **2022.3.57f1c2** 打开 `D:\zlj world\VoxelCraft`，双击 `Assets/Scenes/Main.unity`，按 Play **无需任何手工配置**即进入游戏；
2. 出生在地表安全位置，鼠标锁定，区块逐步浮现，远处被雾自然遮蔽；
3. **移动**：WASD 行走、Shift 疾跑、空格跳跃（可上 1 格台阶）、F 飞行（空格升/Ctrl 降）、入水可游，不穿墙、不坠出世界；
4. **世界**：可见山地、平原、大面积水面、沙滩、沙漠、雪山和树木；改 `Game` 组件的 Seed 重进 Play 地形随之改变；
5. **搭建**：准星指向方块出现黑色高亮框；按住左键连续破坏；右键放置当前方块；数字键 1-9/滚轮切换热栏（草/土/石/沙/原木/树叶/木板/圆石/玻璃）；区块边缘编辑时相邻区块同步更新；
6. 性能：常规场景编辑器 Play 稳定 60+ FPS，移动/加载无卡顿尖峰（世界生成每帧预算 ≤8ms）；
7. 资源合规：贴图来自 minetest_game（CC BY-SA 3.0），README 含**作者署名与许可链接**、项目内随附 LICENSE 副本；程序化绘制兜底保留；零外部包依赖。

## 7. 风险与备选方案

| 风险 | 概率 | 备选方案 |
|---|---|---|
| 批处理编译遇许可证问题 | 低（lic 已在） | 降级为静态审查 + 首次打开时验收反馈 |
| 2022.3.57 中国版(c2)行为差异 | 极低 | 切 2023.1.4f1 重验（代码双版本兼容） |
| 外部贴图个别缺失/加载失败 | 极低（14/14 已本地校验） | 程序化兜底自动补位并在日志报告 |
| 大规模网格性能不达标 | 低 | 视野降到 5；AO/预算可调；后续可上 Job/Burst（需联网） |
| 水面半透明排序瑕疵 | 低 | 已规避（不写深度+单层水面）；极端视角轻微混合次序问题属可接受项 |

## 8. 交付物清单

```
D:\zlj world\
├─ ROADMAP.md                      ← 本文件
├─ _external\chosen_textures\      ← 已下载贴图暂存（14张 + LICENSE 副本）
└─ VoxelCraft\
   ├─ .gitignore  ├─ README.md     ← 中文：操作/调参/贴图替换/署名/代码导读
   ├─ Packages\manifest.json       ← 空依赖（零外部包）
   ├─ ProjectSettings\             ← ProjectVersion(2022.3.57f1c2)、EditorSettings、BuildSettings 等
   └─ Assets\
      ├─ Scenes\Main.unity         ← 近空场景，运行时自举
      ├─ Editor\SelfTest.cs        ← 离线自动化回归
      ├─ Resources\
      │  ├─ Shaders\BlocksShader.shader / WaterShader.shader
      │  └─ Textures\              ← 14 张 .png.bytes + LICENSE-minetest_game.txt（热替换插槽）
      └─ Scripts\
         ├─ Core\  (VoxelMath, BlockType, BlockDatabase, GameBoot, Game)
         ├─ Gen\   (Noise, TerrainGenerator)
         ├─ World\ (Chunk, ChunkMesher, World)
         ├─ Player\(PlayerMotor, MouseLook, BlockInteraction)
         ├─ UI\    (Hud)
         └─ Art\   (TextureFactory)
```

## 9. 后续扩展路线（本期不做，架构已预留）
存档(JSON 区块增量)→ 洞穴/矿物 → 昼夜与平滑光照 → 简单生物 → 合成/背包 → Job/Burst 多线程网格 → 联机。

## 10. 资源附录（v1.2 全量盘点，全部免费可用）

### A. GitHub·Minetest 官方资源库（已下载到 `_external\`，CC BY-SA 3.0：署名+同享，README 将附署名与 LICENSE 副本）
- **方块贴图**：已锁定 14 张核心贴图（草顶/草侧合成/土/石/沙/原木侧/原木顶/树叶/水/木板/圆石/玻璃/雪/砖，均 16×16）
- **扩展贴图**（M7-B 可启用）：煤矿/铁矿/金矿/钻石矿/铜矿/锡矿、冰、黑曜石、砂岩、砾石、苔石、丛林木全套、松木全套、珊瑚等
- **音效**（M7-A 可启用）：77 个 .ogg——挖掘(土/草/石/木/金属…)、破坏、放置、脚步(草/土/砂/玻璃…)，Unity 原生支持
- 探测结论：Kenney 官方 GitHub 仓库可达但风格不合（城市建造）；tenplus1/mobs_animal 需认证不可达（放弃，本地动物包更优）；glTF 样例库可达但 Unity 导入需联网装包（不适用）

### B. 本地 Asset Store 缓存（已在硬盘，免下载，Asset Store 标准许可：项目内自由使用）
- **Animals FREE**（ithappy）：鸡/鹿/狗/马/猫/企鹅/虎 共 7 个 FBX 模型 + 30 个 .anim 动画剪辑（idle/walk/run…）+ 7 个 AnimatorController + 贴图 —— M7-C 素材
- **Battle Wizard Poly Art**（Dungeon Mason）：绑定人形角色 + 38 个 FBX 动作（攻击×4/战斗跑走×5/防御×3/死亡×2/受击/眩晕/待机×3/跳跃×3/交互…）+ AnimatorController + 贴图 —— M7-D 素材
- 另有官方 Starter Assets ThirdPerson / Tanks 完整项目 / UI Samples / DOTween（本期不采用，已登记备查）

### C. 程序化兜底
- `TextureFactory` 运行时程序化绘制任何缺失贴图槽位（保证任意机器可运行）；缺省时游戏自动降级并写日志

### D. 两套可选贴图风格（v1.3，二选一，启动前定稿）
| | 风格 A：Minetest 经典 | 风格 B：Pixel Perfection MC 高仿（推荐） |
|---|---|---|
| 来源 | minetest_game 官方默认材质 | VoxeLibre（基于 XSSheep 的 Pixel Perfection 资源包，多为原版搬用） |
| 观感 | 柔和清新，自成一体 | **最接近我的世界原版**，粗犷像素感 |
| 已备素材 | 14 张核心贴图 | 24 张：核心 14 + 基岩/4种矿石/砾石/冰/黑曜石/苔石/石砖（M7-B 原生同风格） |
| 许可 | CC BY-SA 3.0（celeron55 等） | CC BY-SA 4.0（XSSheep 等），README 附署名 |
| 本地预览 | `_external\chosen_textures\` | `_external\styleB_voxelibre\`（可用资源管理器肉眼对比） |

### E. DSH 辅助开发插件（v1.3 已安装生效）
调研结论：DSH 为全插件化架构，120+ 官方插件随安装包就位（文件/终端/子代理/工作流/目标/沙箱等），无需也无法额外安装；真正可扩展的机制为 **技能(Skills)/MCP/动态插件** 三种，离线可行的是技能。已交付两个项目专属技能（`.dsh\skills\`，热加载，全项目会话自动可用）：
- `unity-batchmode`：Unity 批处理编译验证流程（命令模板/日志判读/SelfTest 调用/常见坑）
- `voxelcraft-guide`：项目架构地图/硬性规范（零外部包、英文代码中文文档、三层贴图供给）/许可署名要求/方块扩展配方

---
**验收方式**：v1.1 已纳入 GitHub 免费资源方案（14 张贴图就绪）。批准后我立即从 M0 开始自动连续工作并逐里程碑汇报；如仍需调整（键位/方块种类/视野/范围增减），直接提出。
