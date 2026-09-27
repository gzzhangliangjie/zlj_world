# Bedrock 生物移植通用封装规范 (SPEC.md)

> 目标:新物种接入 = 纯资产 + 纯数据接线,零引擎改动、零新配置字段。
> 每条规则都带证据链(源码库 / 事故案例),禁止"看起来对"的手推。

## 证据源(本地克隆,~/AppData/Local/hermes/cache/scratch/)
- **eyelib** `animation/bedrock/BrClipExecutor.java`:Bedrock 运行时是**逐通道累加语义**
  (每帧每 Entry 先 reset 零,每 clip 采样后 `renderInfoEntry.rotation.add/position.add`;
  常量 0 也是写入;`this` = bind + 已累计)
- **blockbench** `js/animations/keyframe.js:121`:`'this': last_value`,`-this` = 归零回 bind
- **geckolib** `loading/definition/animation/ActorBoneAnimation.java`:`relative_to` 解析为字符串标志
- **SimpleBedrockModel** `common/model/BedrockModel.java:225`:骨级 rest rotation 初始化(左手系→右手系符号)
- **bedrock.dev 官方 schema**:`relative_to:{rotation:"entity"}` = 旋转相对实体而非父骨
- **bedrock-samples v1.21.80.3**:全部原版实体资产(唯一真值)

## 一、机制层(引擎必须原生支持的语义 —— 已实现)

| # | 语义 | 实现 | 证据/事故 |
|---|------|------|-----------|
| M1 | **累加 vs 覆盖**:Bedrock clip 值是"加在 bind 上";我们是覆盖式,经 rest*Euler(delta) 换算 | `Sample()` 相对分支 | eyelib 源码;polarbear.move 覆盖语义冻腿事故 |
| M2 | **absolute/final 语义**:`x - this` = 最终角度 | `Sample(absolute=true)` 的 bindBedrock 换算 | eyelib;wolf setup `45-this` 坐姿 |
| M3 | **relative_to entity**:**自文件解析**,不靠 registry 字段 | Track.entitySpace → absApply | bedrock.dev schema;hoglin 头 50° bind 事故 |
| M3b| entitySpace 数值等价:absolute 换算 = 实体空间最终角(父链无旋转时严格相等;body 无旋转,恒成立) | 同上 | 数学论证 + av56/57 双跑全绿 |
| M4 | **gait 权重只作用于 locomotion clip**(识别方式=vanilla 同款 `anim_time_update: modified_distance_moved`) | `Sample()` w 门 | armadillo 姿势被 gait 权重洗白事故 |
| M5 | **bind_pose 烘焙默认 rotation-only**;position 是引擎补偿键(parrot -6px) | `BakeDefaultLegPose` | parrot 腿悬空事故 |
| M5b| **bakedSetupPos 白名单**:真原版姿势数据的 position 才烘(polarbear body -9px 常量) | registry opt-in | polarbear.move 分析(ss≡0) |
| M6 | **molang 查询缺失返回 0**(且必须显式记录哪些 query 缺失,缺=0 在无目标/静止时通常恰好是 vanilla 值) | `Molang` switch | target_y_rotation 无目标=0 |
| M7 | 贴图 alpha=3 texel 混合消失 → 固体模型 alpha 提 255 | importer | 贴图事故 |
| M8 | faceOverrides 坐标 = Unity 底部原点 v-flip | registry | goat [36,2,21,6] ↔ png y56..62 验证 |
| M9 | 原版控制器状态机:registry `controllers:true` 后,clip 排播全由 AnimControllers/<sp>.json + EntityDefs/<sp>.entity.json 数据驱动(eyelib 语义:transitions 首个为真切换、动画条目 blend-weight molang、嵌套控制器、无 scripts.animate 实体全控制器驱动) | registry | hoglin 试点:av62 300/300 + goldenCheck 0 diff + GPU GIF 40f comps=1(8d47092) |
| M10 | 控制器 blend 权重逐帧落地:runtime Tick 重求值权重并 SetClipWeight,player 把 ctlWeight 连乘进 gait/pose 混合(delta 缩放,非 lerp-to-rest) | eyelib BrClipExecutor.java:21,46(multiplier *= blendWeight; rotation.mul(w)/pos.mul(w)) | spider walk 权重回归 golden 0 diff(8baf908) |
| M11 | 通用 anim_time_update:任意 molang 表达式驱动 clip 时钟(query.anim_time/delta_time 入 Ctx);ram_attack 式门控倒放(-4x)不再是 realtime 正放 | goat ram_attack "Math.max(anim_time + (gate?dt:-dt*4),0)";Ident 大小写归一(Math.==math.) | goat head 漂移 +0.5°/帧→0(8baf908) |
| M12 | rest 烘焙 clip 跳过规则(纯数据推导):entity map .setup 尾(1.8 bind 换算,importer 已应用)+ .default_leg_pose(Bind 烘 REST)+ registry bakedSetupClips(polarbear.move 族,数据层声明哪些烘 REST) | pig/llama/mooshroom setup=["-this"];wolf.setup 位置族;polarbear.move "-9-2*ss-this" | polar_bear body pos delta 0.313m→0(8baf908) |

## 二、数据层(物种接线 = 只填这些字段)

| 字段 | 作用 | 例 |
|------|------|----|
| walkClip | locomotion 主 clip | `"animation.hoglin.walk"` |
| extraClips | 常驻 clip(含 look_at_target 类;entitySpace 自动识别) | hoglin, chicken.general |
| bakedSetupClips(+Pos) | 烘 rest 的 setup(罕见,常量姿势) | polarbear.move |
| behaviours | 行为状态(sit/shake/attack,带 absolute/minT/maxT) | wolf |
| archetype/locomotion/gait | quadruped/biped/arachnid/hopper + dist-cos/var:tcos/... | |
| walkSpeed/gaitWeight/entityScale/groundOffset | 数值(必须字符串) | |
| faceOverrides | 眼睛在侧面等特殊 UV | donkey |

## 三、接入流程(26 物种验证过)
1. trees API 索引 → entity.json(geometry/textures 字段=唯一真值;多版本选最大 v)
2. geo/animation/贴图三资产入 Resources
3. creatures.json 条目 + speciesList 接线(AnimVerify 1 处 + SelfTest 4 处)
4. GetSpeciesNets case + GifFrames job + FrontSheet
5. AnimVerify 全绿 → 双视角 GIF(GIF_YAW=144/-36)→ PIL 逐帧 → 交付

## 三·五、自动化回归门禁(GoldenPose,提交 b612d05/8b35333)
- **基线**:`_logs/golden/<species>.txt`——26 物种×150 帧×全骨 localPos+localRot,AnimVerify 同款确定性 harness(walking/moving/前进爬行/Tick(1/60)、hopper wing_flap 注入对齐)
- **门禁**:`Unity -executeMethod VoxelCraft.Editor.GoldenPose.Run -goldenCheck` 逐帧逐骨 diff,容差 rot 0.5°/pos 5mm,欧拉回绕感知;**任何机制改动(controllers/molang/累加)必须过此门**
- 工作流:改机制 → goldenCheck 全绿 = 与"已正确的 26 只动物"行为等价(这正是它们作为框架测试套件的价值);有 diff = 逐条排查(要么机制 bug,要么旧特判本来就错——都要证据)
- 已验证:自检 26/26 零 diff;负测试(gaitWeight 1.0→0.35)精确咬出 spider 1200 diff;实战首秀即抓住操作失误(gaitWeight 误写 0.3,真值 1.0,walkSpeed 才是 0.3)
- 注意:registry 数值改动会立即改变行为(这是设计),基线重置=重新跑 capture 模式并记录原因

## 四、红线(违反=返工)
- 语义歧义禁止手推 → 翻本地库源码(eyelib/blockbench/geckolib/SimpleBedrockModel)
- 禁止新 species 特判代码;新配置字段必须先证明"通用机制不可行"
- 禁止调检查器容差凑 PASS
- 用户亲眼所见 > 一切自动检查;vision 误报以 PIL/引擎探针仲裁
- 数值不猜,等 AnimVerify 报告

## 五、已删的特殊字段(机制化的胜利,勿再发明)
- ~~absoluteExtraClips~~ → M3 relative_to 自解析(2026-09-28,av57 全绿删)

## 六、特殊配置的 vanilla 出处(它们本是框架测试用例)

每个"特殊字段"都是对 vanilla 控制器状态机某条数据的手工翻译。出处(资产+源码双证):

| 我们的配置 | vanilla 机制(资产证据) | 引擎参考实现(源码证据) |
|---|---|---|
| `extraClips:["standing"]` (parrot) | `controller.animation.parrot.move` standing 态播 `moving+standing` | eyelib `BrControllerExecutor.java:161` `currState.animations().forEach(...)` 逐动画 blendValue |
| `bakedSetupClips:["base"]` (parrot) | `controller.animation.parrot.setup` default 态**永久播** `look_at_target+base` | 同上;`-this` 归零语义=blockbench keyframe.js:121 |
| `bakedSetupClips/Pos:["move"]` (polar_bear) | `controller.animation.polarbear.move` default 态同帧播 `walk+move+look_at_target`(累加) | eyelib `BrClipExecutor.java` rotation.add 逐通道累加 |
| `gaitWeight:"0.3"` (spider) | 控制器动画条目的 **blend value**:`{'walk':'query.modified_move_speed'}`(polar_bear 同款) | `BrControllerExecutor.java:161` `blendValue.eval(scope)` molang 求值 |
| `behaviours`(sit/shake/attack) | 控制器 states + molang transitions(如 hoglin attack 态 `variable.has_target && variable.attack_time>=0`) | `BrControllerExecutor.java:49-57` transitions evalAsBool→switchState |

**结论**:全部收敛到同一个未实现机制 = **控制器状态机**(states×animations×molang 条件×blend)。实现它以后,上表字段全部可删,物种接线回到 `walkClip+controllers` 两个文件级引用。

### B 方案参考实现要点(eyelib BrControllerExecutor.java,已读)
- tick:先按 initial/current state → 逐 transition `evalAsBool` 切态 → blend
- blend:`blendProgress = clamp(stateTimeSec/blendTransition)`;每动画权重=`blendProgress × blendValue.eval(scope)`(状态切换期新旧态动画**交叉淡化**同播)
- 状态切换时:两态共有的动画**不重启**(line 89-90 `if (currState.animations().containsKey(animName)) continue`);旧态独有动画 onFinish
- 我们已有的绝对/累加/`-this` 采样语义(M1-M3)是它的下层,可直接复用

## 七、开放项
- animation controllers 状态机未解析(当前用 walk 常驻+extras+behaviours 近似);B 方案完整解 = controllers+molang 条件求值(参考实现要点见上节)
- ocelot/sit 两域 open;zombie 耳朵细节
- Molang 引擎 query 覆盖率低(target_y_rotation 等=0)
