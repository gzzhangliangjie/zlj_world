# Blockbench (bbmodel) → VoxelCraft 导入规范

> 适用:用户自建物种(如 goose)。**分流判定先走总入口 Docs/creature-intake-spec.md**:bedrock-samples v1.21.80.3 已有的物种一律走官方移植(SPEC.md §三),不经本规范。
> 依据:goose 全链路踩坑实录(M28b,2026-09-30)。每条规则背后都是一次实际返工。

## 0. 总览

```
bbmodel ──转换脚本──> geo.json + animation.json ──部署──> Assets/Resources/ ──接线──> 门禁五连
```

五类数据五套坑,缺一不可查:**几何(rest rotation)、动画(X 轴符号/loop 字符串)、控制器(变量喂值)、贴图(y 坐标系)、注册(groundOffset/faceOverrides)**。

## 1. 转换(bbmodel → bedrock json)

用 croc_build.py 同款转换脚本,在 bedrock 语义上必须做以下修正——**逐条硬规则**:

### R1 动画 rotation X 必须取负(最高优先级)
bbmodel 动画关键帧的 X 轴与 bedrock 格式**反号**:
- Blockbench 界面 X+ = 仰头;bedrock 动画 X+ = 低头(铁证:sheep grazing X=+180 低头到地)
- Blockbench 官方导出器会自动取负;直读 bbmodel 内部值则必须手动取负,**所有骨、所有 clip、pre/post 关键帧、Y/Z 不动只翻 X**
- 漏翻的检出特征:非对称单方向动作(如伸颈、低头)方向反转;**对称摆动(走路腿 ±16°)翻号后视觉无差,不会报警**——因此不能靠 walk 动画验收
- 位置(position)通道不翻;只有 rotation 的 X 分量取负

### R2 组 rest rotation 原样写入 geo,不烘进 cube
- bbmodel outliner 组的 rotation = bedrock bone "rotation",绕组 origin(pivot),子几何不烘焙,运行时携带(M24 结论,eyelib/SimpleBedrockModel 双源码铁证)
- **绝不加 Inverse(parent rest) 补偿**
- 子骨 localPos = 原始 pivot 差;cube local = centre − pivot

### R3 loop 字符串兼容
bbmodel 导出 `"loop": "loop"`(字符串)。Unity 侧 BedrockAnimationPlayer 已修:含 "loop"=循环、"once"=单次、hold 兼容。转换侧保持字符串原样即可,不要改成 bool。

### R4 顶层结构兼容
bbmodel 转出的 geo 顶层键可能是 `minecraft:geometry`(数组)或裸 `geometry.xxx`——解析脚本两种都要兼容。

### R5 动画 animator 键是 uuid
bbmodel animations 里 animator 以骨骼 uuid 为键,转换时需经 outliner 建 uuid→bone name 映射。

## 2. 部署清单(Assets/Resources/)

| 文件 | 位置 | 备注 |
|---|---|---|
| 模型 | Geo/<sp>.geo.json | R2 规则 |
| 动画 | Anims/<sp>.animation.json | R1/R3 规则 |
| 贴图 | textures/… 128×128 png | 供引用 |
| 实体 | EntityDefs/<sp>.entity.json | 动画映射,勿抄 croc 专属 bite/crawl/look_at_target |
| 控制器 | AnimControllers/<sp>.animation_controllers.json | 见 R6 |
| 注册 | Registry/creatures.json | 见 R7 |

## 2bis. entity.json / animation_controllers.json

### R6 控制器变量必须有 pre_animation 喂值
- AC 状态机上挂 `variable.chasing` 之类的条件前,entity.json 的 scripts.pre_animation 必须喂值,否则状态机卡死
- 简化优先:状态能少则少(walking/swimming 两态),攻击动画走 behaviours extraClips 不经 AC
- AC 动画条目挂 `query.modified_move_speed` 权重可实现停止时淡出(否则 idle 测量超标)
- 无 scripts.animate 时全部控制器自动挂根(legacy);缺 initial_state 回退第一个状态

## 3. creatures.json 注册(R7)

- `locomotion`、`walkClip` 按物种
- `groundOffset`:**先用 DIAG 或探针实测脚底悬空量再填**,不要照抄近似物种(spider 0.31 错了半个月的教训)
- `faceOverrides`:眼睛区域;**y 是 Unity 自底向上坐标**,png 像素 y 需 texH−y−h 转换
- AC 走 controllers:true 时 gait:"var:tcos" 字段无效,纯关键帧物种不必填

## 4. Unity 侧接线

- SelfTest.cs / AnimVerify.cs / GoldenPose.cs / AllFrontSheet.cs / BehaviourGifFrames.cs 各加 species case
- BlockyAnimal.cs: GetDims + headBox 加 case
- BehaviourGifFrames: 每个行为动画各一个 job(walk 之外 flap/attack/swim 都要,用户要求"四个动作")

## 5. 门禁五连(全绿才算完)

1. **AnimVerify**(VoxelCraft.Editor.AnimVerify.Run):SUMMARY pass/fail,含 idle_stability(idle 窗口已改 TickControllers(moving:false),与运行时语义一致)
2. **SelfTest**(VoxelCraft.Editor.SelfTest.RunAll):~47 项
3. **GoldenPose capture**(VoxelCraft.Editor.GoldenPose.Run):首次/动画改动后重捕
4. **GoldenPose check**(-goldenCheck):回归比对,0 diff
5. **BehaviourGifFrames**(GIF_SPECIES=<sp>):每个行为动画渲染 GIF

验收底线(用户明示):
- **GIF 必须是能播的动图**,不是静态拼图
- **每个行为动画都要有 GIF**,不只 walk
- **主体验完整**:画面里有主体、无空镜头、颈/翅/腿无畸形
- **非对称动作逐帧核方向**(喙朝向用橙色像素 y 轨迹量化),视觉复核侧视/正视都要
- 修复动画后**必须重捕 golden 基线**,否则 check 全 FAIL

## 6. Unity 批处理铁律(详见 memory)

- 跑前:powershell Stop-Process Unity + 删 Temp/UnityLockfile
- 跑后常不退(假 exit 127/21):**判定以 logFile 的 RESULT 行为准**;结果到手就杀进程,否则下个批处理撞"Multiple Unity instances"崩溃
- SelfTest 方法名是 RunAll;GoldenPose check 参数是 -goldenCheck

## 7. 已知非问题(不要误判为 bug)

- 部分骨骼原作者没打 keyframe(如 goose beak)→ 该骨静止是原始数据如此
- swim 与 walk 幅度接近是原始动画数据小(±12°),非接入错误
- 侧后视角会把前伸的颈投影成"喙立头顶",判定方向必须正侧视
