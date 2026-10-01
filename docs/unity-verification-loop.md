# Unity 验证标准流程（VoxelCraft 调试循环）

> 2026-10-01 建立。M34 载具五轮修 bug 的教训沉淀：验证循环的耗时大头是"多轮串行 + 锁竞争等待",不是单次 Unity 冷启动(实测 15-30s)。外部 unity-cli(官方/社区四家)全部要求 Unity 6 或需手动接线,2022.3 项目不引入——结论:用自家 Tools/unity-cli.ps1。

## 一、一条命令的验证矩阵(实测耗时)

| 命令 | 干什么 | 耗时(实测) |
|---|---|---|
| `powershell -File Tools/unity-cli.ps1 compile` | 全量编译检查(权威) | 17s |
| `powershell -File Tools/unity-cli.ps1 test` | SelfTest.RunAll 58 项回归 | 22s |
| `powershell -File Tools/unity-cli.ps1 m34-ctl` | 载具 18 项物理断言 | 16s |
| `powershell -File Tools/unity-cli.ps1 m34-gif` | 四载具 GIF 帧生成+连通域门 | 29s |
| `powershell -File Tools/unity-cli.ps1 snapshot` | 模型截图入 _logs | ~20s |
| `powershell -File Tools/unity-cli.ps1 build webgl` | WebGL 构建 | 分钟级 |

所有命令自动:排队等锁→启动 Unity batchmode→**按日志标记判读结果**(绝不只看 exit code,Unity batch 的 exit code 不可靠)→输出 PASS/FAIL。日志落在 `_logs/` 带时间戳。

## 二、标准调试循环(改一行代码 → 结论)

```
1. 改代码(Assets/Scripts/... 或 Tools/vox_to_creature.py + 重生成资源)
2. powershell -File Tools/unity-cli.ps1 m34-ctl     ← 行为断言,最快失真信号
3. 失败 → 读 _logs/m34ctl_*.log 里的 [M34C] FAIL 行(带 value/range)
4. 行为过了但要看画面 → m34-gif → 出 GIF 到 _logs/gif/vehicles/
5. 涉及全局回归 → test(58 项)
6. 全绿 → git commit + push
```

**顺序原则:数值断言先行,GIF/视觉只做辅助确认。** 断言能定位到"哪个值超了哪个范围",截图只能告诉你"看起来不对"。两者矛盾时,写一次性探针(见 §四)拿 ground truth,不猜。

## 三、unity-cli.ps1 内置的防坑机制(别绕过它手写命令)

直接裸调 `Unity.exe -batchmode` 会踩的坑,脚本都处理了:
- **锁竞争**:启动前检测 Temp/UnityLockfile + Unity 进程,排队等待;锁死但无进程视为 stale 放行
- **instance race**:Unity 启动即死且无日志(exit 140063)自动重试最多 8 次
- **bootstrap 假退出**:父进程退了但子编辑器还活着,盯日志增长继续等
- **exit code 假报**:全部按日志标记(SELFTEST PASS / M34CONTROL RESULT: PASS / VehGifs] RESULT: PASS ...)判读
- 结果标记数组在脚本头部 `$GoodMarkers/$BadMarkers`,新增验证方法时**同步加标记**,否则命令会报 "no RESULT marker"(M34 第一轮就踩过)

## 四、一次性探针(拿 ground truth 的手段)

断言和截图都说不清时,写个临时 Editor 脚本(Assets/Editor/XxxProbe.cs,`static void Run()`),batchmode 跑完即删:
```bash
powershell -Command "Start-Process -FilePath 'C:/Users/zlj10/Unity/2022.3.57f1/Editor/Unity.exe' -ArgumentList '-batchmode','-quit','-projectPath','D:/zlj_world/VoxelCraft','-executeMethod','VoxelCraft.Editor.XxxProbe.Run','-logFile','D:/zlj_world/_logs/probe.log' -Wait"
```
M34 实战案例:M34LegProbe 直连 player.Tick 证实腿骨旋转恒 0(定位 guard bug);M34WheelProbe 证"陷地"是视觉误判。**batch 探针守卫用 `Application.isPlaying`,不要用 `Time.frameCount==0`**(编辑器启动已加载帧,该守卫永假)。

## 五、资源再生的验证链(vox → 资产 → Unity)

改 `Tools/vox_to_creature.py` 后,必须走完整交付链再验证:
```
python 转换 → Resources/Geo/*.geo.json + Textures/*_skin.png
→ **拷贝 _skin.png → _skin.png.bytes**(Unity 读 .bytes;忘拷 = 交付旧图,M34 贴图"没修好"假象的根因)
→ m34-ctl + m34-gif 验证
```
geo 级数值审计(读 .geo.json 的 cube origin/size)是尺寸/对称性问题的最终权威;渲染像素审计受 mask 碎片+阴影干扰,只做参考。

## 六、GIF 交付规范(用户明确要求)

- **每个载具/生物一个独立 GIF**,不许拼组照
- 300×300、40 帧、<5MB(Feishu 限制),输出在 `_logs/gif/vehicles/`(动物在 `_logs/gif/`)
- 每帧过 8-连通域门(主体完整、非空镜头)才算 PASS
- 回发用户前逐个确认文件存在且大小正常;发图路径铁律见 memory(MEDIA:C:/... 大写盘符正斜杠)

### 6.1 GIF 合成规范(M35 花屏事故,2026-10-01)

合成 GIF **必须全帧共用一个全局调色板**,严禁每帧独立 `convert('P', adaptive)`:

- **事故**:每帧独立量化出各自调色板,Pillow 写盘时只保留第一帧的全局调色板 → 后续帧的索引全部错位映射 → 橙色青蛙解码成青色/紫色噪点(用户:"花花绿绿""贴图全部错了")。游戏内贴图本身是对的——坏的只是合成这一步。
- **正确流程**(2026-10-01 起,模板可直接抄 `_logs` 里的合成脚本):
  1. 全帧像素池采样 → `quantize(colors=255, MEDIANCUT)` 生成**一份**调色板
  2. 每帧 `frame.quantize(palette=pal_p, dither=NONE)` 映射到同一调色板
  3. `save(save_all=True, disposal=2)`
- **解码回读门禁(发图前硬性步骤)**:用 PIL 重新打开刚生成的 GIF,逐帧 `seek(i)` + `convert('RGB')` 与源 PNG 逐像素比对,`diff > 60` 的像素占比必须全帧为 0.0000。不对就重编,不许发。
- **帧间运动检查**:合成前先比对相邻源帧(`diff>10` 占比 >0.1%),39/39 帧间必须有运动——全静止 = 动画没播(见 §7 camel 事故),直接交付静态图会被用户骂。
- 静止帧序列 Pillow 会自动去重成 1 帧——`n_frames==1` 就是动画冻结的信号,不是编码问题。

## 七、动物移植文件清单(M35 camel 漏拷 AC 事故,2026-10-01)

> 事故:首次移植 camel/frog/turtle 只拷了四类文件,漏了 animation_controllers。
> `controllers:true` 的物种**只靠 AC 状态机调度 clip**,没有 AC → `playing=[]` →
> walk/sit 全静止 40 帧一模一样。查了 6 层(Molang 解析器/clip 解析/骨名映射/
> 控制器状态机/时序)才定位到"文件根本不在"。教训:**移植清单先行,拷完对账**。

官方 bedrock-samples 物种移植,**五类文件一个都不能少**(B = raw.githubusercontent
.com/Mojang/bedrock-samples/<ref>,当前 ref **v1.21.80.3**):

| # | 文件 | 源路径(B=resource_pack 侧) | 落地到 VoxelCraft/Assets/Resources/ | 缺了会怎样 |
|---|---|---|---|---|
| 1 | geo 几何 | `models/entity/$s.geo.json` | `Geo/$s.geo.json` | 直接走方块兜底模型 |
| 2 | 动画 clip | `animations/$s.animation.json` | `Anims/$s.animation.json` | 无动作,腿不动 |
| 3 | 实体定义 | `behavior_pack/entities/$s.entity.json` | `EntityDefs/$s.entity.json` | 无 clip 映射/pre_animation |
| 4 | **AC 控制器** | `animation_controllers/$s.animation_controllers.json` | **`AnimControllers/`** | **clip 永不调度,全静止(本次事故)** |
| 5 | 贴图 | `textures/entity/...`(entity.json 的 textures 字段指路,注意变体如 frog 要 temperate_frog.png) | `Textures/$s_skin.png.bytes` | 白模/花屏 |

外加代码/注册侧(非拷贝,但同样必需):
- `Registry/creatures.json` 注册(species dict:walkClip/archetype/locomotion/gait/behaviours/walkSpeed/controllers)
- `BlockyAnimal.GetSpeciesNets` 兜底 net(geo 导入失败时用;UV 坐标**必须抄 geo json 的真实值**,不许目测编)
- `BlockyAnimal` collider 分支 + `BedrockGeoImporter` 腿骨映射(命名腿如 left_front_leg)
- SelfTest/AnimVerify/BehaviourGifFrames 三处 species 列表 + GIF jobs
- 移植后:`unity-cli.ps1 test` → `BehaviourGifFrames.Run`(设 GIF_SPECIES 只跑新物种)→ 帧间运动检查(§6.1)→ 全局调色板合成+解码回读 → commit

**AC 特例(别去下载不存在的文件)**:chicken/cow/pig/horse/donkey 官方**没有**单独 AC——它们的 `scripts.animate` 直接驱动 clip(direct drive);salmon/pufferfish/dolphin 共用 `fish.animation_controllers.json`(内含 controller.animation.fish.general);turtle geo 是老版 1.10 格式(顶层键 `geometry.turtle` 而非 `minecraft:geometry` 数组),importer 已有分支处理。

