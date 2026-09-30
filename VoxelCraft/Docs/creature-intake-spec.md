# VoxelCraft 新增动物流程规范(双入口)

> 总入口。先分流,再走各自入口;两个入口最终汇合到同一套门禁验收。
> 版本:M29(2026-09-30);依据 33 物种实战(vanilla 32 + 自建 goose)。

## 0. 分流决策树(每个新动物从这里开始)

```
新动物请求
    │
    ├─ Q1. bedrock-samples v1.21.80.3 里有这个物种吗?
    │      (trees API 索引查 entity/<name>.entity.json 是否存在,详见 §1.1)
    │
    ├─[有]──→ 入口 A:官方移植(bedrock-samples,成熟流程,SPEC.md §三)
    │
    └─[没有]─→ Q2. 资产形态是什么?
           ├─ bbmodel 源文件(Blockbench 自建) → 入口 B:blockbench-import-spec.md
           └─ 其他(direct-from-json/第三方/裸 geo) → 先按入口 B 的
              §1 转换核对单逐项过滤,仍不能转的按个例评估,勿直接部署
```

**分流铁律**:
1. 官方已有的物种**必须**走入口 A——不得"顺手"用 Blockbench 重画或第三方模型替代(用户原话:"之前的复刻模型流程已经很成熟了 为什么要另起炉灶")。
2. 自建物种(bbmodel)是入口 B 的唯一适用对象,且须用户明示(如 goose)。
3. 判定依据是 trees API 索引的**存在性**,不是"名字像不像官方"——例如 croc/goose 在 samples 里没有,但 bee/bat 有;变体(如 donkey_chestnut vs donkey)也按存在性判。
4. 两个入口共用:门禁五连(§3)、Unity 批处理铁律(§4)、验收红线(SPEC.md §四)。

---

## 1. 入口 A:官方已有动物(bedrock-samples 移植)

> 详细防坑清单:skills/devops/bedrock-creature-porting/SKILL.md(资产查找纪律/贴图/骨架/验证阶梯)。此处只列主干。

### A1. 拉资产(trees API 索引,禁止猜文件名)
1. `curl gh-proxy.com/https://api.github.com/repos/Mojang/bedrock-samples/git/trees/v1.21.80.3?recursive=1 -o tree.json`(可缓存复用)
2. Python 筛 `<species>` → 拿全目录(entity/geo/动画/贴图/变体文件名)
3. **entity.json 是接线唯一真值**:geometry 字段(用哪个 geo)、textures(贴图目录,如 horse2/)、animations(常驻名单)、scripts.pre_animation(gait 变量族)

### A2. 部署
- geo/animation/贴图三资产入 `Assets/Resources/`(Geo/、Anims/、textures/)
- **tag 固定 v1.21.80.3 或更早**;禁用 main/v1.21.90.3+(Mojang 删 bind_pose_rotation 未烘坐标)
- 同物种多版本(geo v1/v2/v3)选**最大 v**;文件名带 v1.0/v2 的先对比无后缀版本

### A3. 接线
- creatures.json 条目:walkClip/extraClips/bakedSetupClips/behaviours/archetype/locomotion/gait/walkSpeed/gaitWeight/entityScale/groundOffset/faceOverrides(**数值一律字符串**)
- speciesList 接线:AnimVerify 1 处 + SelfTest 4 处 + GoldenPose/AllFrontSheet/BehaviourGifFrames 各 1 处
- BlockyAnimal.GetSpeciesNets case + headBox case;BehaviourGifFrames 每个行为动画一个 job

### A4. 门禁五连(全绿才算完)→ §3

---

## 2. 入口 B:官方没有 → Blockbench bbmodel 自建

> 详细规范:blockbench-import-spec.md(五类数据五套坑+验收底线)。此处只列主干。

### B1. 转换(bbmodel → bedrock json,croc_build.py 同款脚本)
硬规则(漏一条=返工):
- **R1 动画 rotation X 全量取负**(bbmodel X+ =仰头,bedrock X+ =低头;对称摆动翻号不报警,只有非对称动作暴露——不可用 walk 验收)
- R2 组 rest rotation 原样写入 geo,不烘 cube,不加 Inverse 补偿
- R3 loop 字符串("loop"/"once"/hold)保持字符串,播放器已兼容
- R4 geo 顶层键两种(`minecraft:geometry` 数组 / 裸 `geometry.xxx`)都要兼容
- R5 animator 键是 uuid,需 outliner uuid→bone name 映射

### B2. 部署+接线
- 部署清单同 A2(Geo/Anims/贴图/EntityDefs/AnimControllers/Registry 六件套;入口 B 额外要写 entity.json 与 animation_controllers.json)
- **AC 状态机变量必须有 pre_animation 喂值**,否则卡死;状态能少则少,攻击走 behaviours extraClips 不经 AC;动画条目挂 query.modified_move_speed 权重
- creatures.json:groundOffset 先实测悬空量再填;faceOverrides 的 y 是 Unity 自底向上坐标

### B3. 门禁五连 → §3;验收底线见 blockbench-import-spec.md §5

---

## -entry? 明确不做什么
- 不做"输入物种名自动跑完全程"的自动机:物种间差异(变体骨/贴图 alpha/legacy rig)决定必须有人工复核点(trees 索引判读、GIF 视觉验收)。
- 不在引擎里写"来源=official/custom"的物种标记字段:分流只发生在流程文档层,接线后两入口产物完全同构(都是 Resources 六件套+creatures.json 条目),引擎无需感知来源。

---

## 3. 门禁五连(两入口共用,全绿才算完)

| # | 门禁 | 命令(executeMethod) | 判定 |
|---|------|------|------|
| 1 | AnimVerify | `VoxelCraft.Editor.AnimVerify.Run` | SUMMARY pass/fail,含 idle_stability |
| 2 | SelfTest | `VoxelCraft.Editor.SelfTest.RunAll` | ~47 项(注意方法名 RunAll) |
| 3 | GoldenPose capture | `VoxelCraft.Editor.GoldenPose.Run` | 首次/动画改动后重捕 |
| 4 | GoldenPose check | 同上 + `-goldenCheck` 参数 | 回归 0 diff(容差 rot 0.5°/pos 5mm) |
| 5 | BehaviourGifFrames | `GIF_SPECIES=<sp> ...BehaviourGifFrames.Run` | 每个行为动画一个 GIF |

验收红线(SPEC.md §四摘要):
- GIF 必须可播放动图,每个行为动画都要有;主体现内完整无畸形
- **非对称动作逐帧核方向**(数值化,如喙 y 轨迹),不能只看对称摆动
- 动画改动后必须重捕 golden 基线
- 用户亲眼所见 > 一切自动检查

## 4. Unity 批处理铁律(两入口共用)
- 跑前:`powershell Stop-Process -Name Unity -Force` + 删 `Temp/UnityLockfile`
- 跑后常不退(假 exit 127/21):判定以 logFile RESULT 行为准;结果到手即杀进程,否则下轮撞 "Multiple Unity instances" 崩溃
- 渲染前设 shader 全局量+双 Render() 预热;批处理不跑 Update,运行态显式 player.Tick(dt)
