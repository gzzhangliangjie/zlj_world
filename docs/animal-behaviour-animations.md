# VoxelCraft 动物动画行为体系(M36,2026-10-01)

> 专项文档:动物"动作行为"如何接线、如何触发、如何验证。配套文件清单见
> docs/unity-verification-loop.md §七;GIF 合成规范见同文件 §6.1。

## 一、三层动画架构

```
官方动画文件层         运行时层                    引擎事件层(M36 新增)
─────────────         ────────                    ──────────────────
Anims/*.animation.json → BedrockAnimationPlayer     BlockyAnimal.TickBehaviourEvents
  clip 定义(骨×通道)     Play/Stop/Sample           空闲时随机/强制触发官方
AnimControllers/*.json → BedrockControllerRuntime    引擎事件,设 EntityState
  状态机(states×条件)     每 tick 求值 Molang 条件    标志+变量,持续期后清零
EntityDefs/*.entity.json → scripts.animate 权重表
  动画映射+pre_animation
```

关键原则:**动画调度全部走官方数据**(控制器状态机 + scripts.animate 权重表达式),
引擎层只负责"事件信号"(什么时候低头吃草、什么时候甩水),不碰任何骨骼。

## 二、clip 调度的三条路径

1. **控制器状态机**(controllers:true 物种):AC 文件 states×transitions,每 tick
   `Molang.Eval(条件)` 求值,第一个为真的转移生效;state 的 animations 列表带权重
   表达式(如 camel moving 态 `{"moving": "math.min(1.0, math.lerp(0.4,1.25,mms))"}`)。
2. **scripts.animate 根驱动**(direct-drive 物种:cow/chicken/horse/villager…):
   entity.json 的 animate 数组就是常驻播放列表,条目可带权重表达式
   (horse `{"rear": "variable.stand_anim > 0.0"}`)。
3. **引擎直发**(harness/测试):`player.Play(clip)` 直接播,GIF 展示 job 用。

## 三、引擎事件层(M36 核心,BlockyAnimal)

### 3.1 事件定义表(Events() 按物种)

| 物种 | 事件(名/时长/权重) | 触发的官方动画 |
|---|---|---|
| horse/donkey/mule | rear 2.6s / graze 4s×3 / tail_shake 1.6s×2 | v3.rear(扬前蹄) / v3.eat(低头吃) / v3.tail(甩尾) |
| cow/sheep/pig/mooshroom/llama/chicken | graze 4s×3 / baby 3s | setup 族低头吃草 / baby_transform(幼年) |
| goat | ram_attack 1.4s / attack 0.8s / graze 3.5s×2 | goat.ram_attack(冲撞低头) / goat.attack(顶) |
| wolf | shake 1.6s×2 / interested 2.5s / sit 4s | wolf.shaking(甩水) / head_rot_z / sitting |
| fox | pounce 0.9s / crouch 2.5s / wiggle 1.2s / sit / sleep | fox.pounce(扑击)/crouch(潜行)/wiggle(扭动) 等 |
| croc | bite 0.7s / swim 4s×2 | croc.bite(咬) / crawl(水中爬) |
| goose | swim 4s×2 / attack 0.8s | goose.swim / goose.attack |
| bee | nectar 4s / sting 1.2s | drip(采蜜滴) / no_stinger(蜇后) |
| skeleton/zombie | attack 0.5s | skeleton.attack(挥弓臂) |
| villager | raise_arms 2.2s / sleep 4s | villager.raise_arms(举手) / get_in_bed |
| frog | jump_goal 0.6s / croak 1.8s×2 / eat_mob 0.5s | frog.jump / croak(鸣囊) / tongue(吃) |
| camel | sit 5s / dash 1.5s | camel.sit_down→sit / camel.dash |

权重=相对触发频率;持续期按官方体感定。**事件之间互斥**(activeEvent 单值)。

### 3.2 事件→信号映射(ApplyEventVars)

事件不改骨骼,只设 `EntityState`(控制器 ctx)与 `player.variables`
(pre_animation ctx)两层信号:

- **query 标志**:`is_grazing/is_standing/is_sitting/is_sleeping/is_baby/is_shaking_
  wetness/is_interested/is_stalking/is_on_ground/is_in_water/mark_variant/
  has_target/facing_target_to_range_attack/is_jump_goal_jumping/is_eating_mob/
  is_croaking/has_dash_cooldown`(M36 全部已实现,含 property 表 has_nectar)
- **变量**:`stand_anim/shake_tail/attack_time/should_bow_head/raise_arms`
  (attack_time 随事件推进 0→1,喂给 goat 的 `sin(attack_time*180)*-37.3` 头部顶击)
- **attack_time 推进**:AdvanceEventVars 每帧 `1-clamp01(evLeft/0.8)`

### 3.3 信号到达两条 ctx 的通路(易踩坑)

- 控制器条件读 `BedrockControllerRuntime.BuildCtx()`(直接抄 EntityState)
- entity pre_animation 读 `BedrockAnimationPlayer.BuildCtx()`——**必须经 ev*
  镜像字段**(evIsGrazing 等 19 个),TickControllers 每 tick push。
  漏镜像=变量永远 0,官方 clip 的 `variable.stand_anim>0` 门全关(M36 修)。
- TickControllers 里非游泳物种每 tick 强制 `isOnGround=1/isInWater=0`,
  会**覆盖事件标志**——swim/pounce/dash 三个事件有守卫跳过(M36 修)。

### 3.4 触发 API

- 游戏内:`Update()` 每 tick `TickBehaviourEvents(dt)`(空闲节奏,8~15s 一选)
- 强制:`ForceBehaviourEvent(name, duration)`(GIF harness/测试),返回
  false=物种无此事件。查询当前事件:`ActiveBehaviourEvent`

## 四、审计基线(38 物种 154 clip,M36 前)

- 可达 104;未接 50 中:~20 个 baby_transform/setup 变体(不需要)、25 个引擎
  事件门控(M36 已接)、3 个 query 缺失(is_jump_goal_jumping 等,M36 已补)
- 特例:chicken/cow/pig/horse/donkey 官方无单独 AC(direct-drive);
  salmon/pufferfish/dolphin 共用 fish.animation_controllers.json

## 五、验证(每个新事件必须过全链)

1. `unity-cli.ps1 compile` + `test`(SelfTest 58 项)
2. `GIF_SPECIES=<sp>` 渲染事件 job(frame 20 强制触发,前后对照在同 GIF)
3. 数值确认事件生效:剪影 top-y/像素数在 f20 前后显著变化
   (马 graze:头顶 y71→84、剪影 26k→23k px)
4. 连通域门全帧 1 域;GIF 合成走全局调色板+解码回读(§6.1)
5. 全部过了才 commit

## 六、历史坑(发生过、已修,勿重蹈)

| 坑 | 现象 | 修法 |
|---|---|---|
| AC 文件漏拷 | controllers 物种全静止 40 帧全同 | 五类文件清单对账(docs §七) |
| sit 任务没停 player.moving | walk 叠加坐姿=腿蠕动 | pose 任务停 locomotion clock |
| 每帧独立量化调色板 | GIF 花屏(青紫噪点) | 全局调色板+解码回读门 |
| pre_animation 读引擎 query 恒 0 | 事件动画永远不触发 | ev* 镜像 push 双 ctx |
| TickControllers 覆盖事件标志 | swim/pounce 进不去状态 | 事件期守卫跳过强推 |
| Molang Ctx 是 struct(C#9) | 字段初始化器编译错 | 默认值放构造点 |

## 七、动作预览工具(M36,AnimPreviewWindow)

`Window > VoxelCraft > Anim Preview`(Assets/Editor/AnimPreviewWindow.cs):

- **物种下拉**(读 Registry/creatures.json 全量)+ clip 过滤框
- **左栏**:该物种全部 clip 列表,play/stop 直发 player.Play();stop all
- **右栏**:22 个引擎事件按钮(ForceBehaviourEvent 4s)——官方控制器/
  pre_animation 收到信号后自己调度动画,和游戏内同一条链
- **顶部开关**:walking(locomotion 时钟)/ auto events(空闲事件节奏)/
  Reset model(换物种后重建)
- **状态栏**:当前播放列表(DebugPlaying)+ 当前活跃事件
- 编辑器非播放态实时驱动(TickBehaviourEvents→TickControllers→player.Tick)

排障:窗口里没动物=先点 Reset model;事件按下没反应=该物种事件表无此
事件(ForceBehaviourEvent 返回 false 会显示在状态栏)。

## 九、鹅追人(领地攻击 AI,自建物种,M36)

> 鹅是**用户自建物种**(非官方 Bedrock),没有官方 AC 状态机可走——追击逻辑直接写在 AI 层(`BlockyAnimal.TickGooseChase`),不经过控制器。

**行为链**(全部数值验证,M36GooseChaseProbe):
1. 玩家进入 **6m** → 进入追击:`walking=true`、朝向玩家、`flap_chase` 叠加层常驻(翅膀展开+快节奏步频),速度 ×1.8(walkSpeed 1.4→2.52)
2. 距离 **<1.6m** → 啄击:面向玩家播一次性 `animation.goose.attack`(带 1.2s 冷却),flap_chase 暂停让位
3. 玩家逃出 6m → 追击解除,flap_chase 停,恢复闲逛动画
4. 追击期间 `TickBehaviourEvents` 被抑制(防止官方 swim 事件抢动画)——"领地攻击优先于环境行为"

**坑**:
- `GetClipWeight` 对未播放 clip 返回 1f → `weight<0.5` 守卫恒假,flap_chase 永不启动;改用 `IsPlaying`(本次新增,搜 playing 列表)
- 探针驱动:Update 不在 batchmode 跑,逻辑抽成 `TickGooseChase(dt)` 公共方法,Update/探针共用
- 回读门禁对齐:Pillow 去重静止帧(n_frames<源帧数),需按 duration 展开后逐帧比对(diff=0.0000)
