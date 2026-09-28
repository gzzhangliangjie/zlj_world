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
| M13 | molang 三角函数=度制(eyelib MolangMath.java cos/sin 包 Deg2Rad;parrot dance.x=cos(life_time*57.3*20) 自证)。dist 时钟统一:query.modified_distance_moved / timeFromDistance 一律 米×14.325(=57.3/4),与 rad 时代 0.25×57.3 严格等价 | 14.33 会引入 0.03% 频率漂移(golden15 0.5° 族),必须用 14.325 | 13 物种 0.2Hz 冻结→2.2Hz 恢复(av66→av69) |
| M14 | pre_animation 通用求值器(player.preAnimation 列表,Tick 内逐行 variable.X=expr 入 variables;eyelib EntityRenderOrchestrator.java:259,623 每帧求值)。引擎默认变量播种:gliding_speed_value=0.6/attack_time=-1(TryAdd,可覆盖);scripts.initialize 常量行一次性播种。molang ?? 空合并:仅 variable.x ?? expr 模式(armadillo rolled_up_time),未定义变量取右支 | goat tcos/hoglin/zombie tcos0/wolf shake 族/armadillo state 族全部由 entity.json 数据驱动,species 旗标关闭 | hoglin/goat 腿冻结修复(av67);变量存储单作用域(player.variables ↔ controller State.variables 每 tick 同步) |
| M15 | 引擎角色变量(无 pre_anim 源,行为层/引擎合成):rabbit jump_rotation(旗标合成器保留,controllers 模式不关)/ocelot 族 variable.state(0 sneak/1 sprint/2 sit/3 walk,TickControllers 按 moving 设 3/2)/parrot is_on_ground=1(地面场景)/wing_flap_position 由 airborneWings 时钟合成 | parrot state=1→standing+moving clip;ocelot 卡 sitting→walking | parrot 尾摆 0.2°→77°(av72);ocelot sneak 57.3° 步态(av73) |
| M16 | Molang 逻辑层与三参函数:`&&`/`||`(AndOr 层,非零=真)与单目 `!`(Unary 层)进表达式语法;`math.clamp/lerp` 三参在闭括号**前**取第三参(旧实现在 Eat(')') 之后读第三参会越过表达式末尾→Eval catch→整表达式 0)。armadillo walking= `mms>0.01 && !is_rolled_up` 全依赖此层 | armadillo walk 权重 `min(1.4,lerp(0.2,2.4,mms))` 曾恒 0;walking 曾恒 0 | av80 前 armadillo 腿冻结;修复后 av81 300/300 |
| M17 | 多 clip 同 bone 通道 = **累加(ADDITIVE)**(eyelib BrClipExecutor.java:74-84 `renderInfoEntry.rotation.add(sampled)`):两遍制采样——pass1 按 bone|channel 累积相对 delta+absolute 终值,pass2 flush;单 clip 物种保持逐 Apply 位等价(基线不动)。`-this` 表达式=读**当前累积值**的替换语义(pass1 里用 acc 现值重采样,数学=eyelib live this) | steve bob x=0 曾整组覆盖 move.arms tcos0(arm 静止);wolf angry 尾摆曾被 tail_default 覆盖(基线 y=0 伪影) | av87 300/300+golden28 26/26 |
| M18 | 动画库族加载:BlockyAnimal 从 EntityDefs/<sp>.entity.json 的 animations 映射收集 `animation.<family>.…` 引用,按前缀段 join('_') 探测 Anims/<family>.animation.json(horse_v3 家族不再靠硬编码名单);steve 接 vanilla player.animation_controllers.json(root→third_person 态,vars 缺省 0=第三人称) | horse entity 引用 animation.horse.v3.* 但库只有 animation.horse.*→Play 静默丢弃 | horseprobe4-6:LegFL 40.86° 反相摆动 |
| M19 | BP 实体属性通用链路:registry species.properties(name→当前值,支持 string 或 {default} 对象,镜像 BP description.properties)→CreatureRegistry 解析→stringPropertyEq 查表(硬编码 armadillo_state 已删);player.species 由 BuildModel 赋值 | 曾硬编码 `name=="minecraft:armadillo_state"?"unrolled"` | bedrock-samples v1.21.80.3 armadillo BP:enum5值 default"unrolled" |
| M20 | swing 验收窗按体型收紧:bipedLike(steve/zombie/skeleton/villager/biped)上限 90°(Java ModelBiped 1.4rad=80.2° 基准);四足维持 170° | gsv=0.6 时人形腿 133.7° 从 170° 平窗蒙混(腿飞) | av89 300/300;zombie 窗 [10,90] |
| M21 | ocelot sit 正/侧视 2 连通域=vanilla 原生视觉(坐姿身体后仰45°+前腿垂直撑地,斜视投影腿根与腹部脱开;3D bounds 实际相交);SitProbe 实证 sit 动画数值全对(frontleg 42.15°/backleg -45°/body -45° 每 tick 稳定) | 曾疑 '-this' 双计 | 前爪分离块 64x28px 居中,原版固有 |
| M23 | 骨骼级 rest rotation 映射:X/Z 恒等、仅 Y 取负(Ry(π) 共轭)——与 cube bind_pose_rotation、Apply() 相对分支同一约定;entitySpace(relative_to:entity)通道=bind 之上的**加法增量**(eyelib BrClipExecutor rotation.add),常量 0 分量保留 bind,不再当绝对终值替换;证据=Java HoglinModel.DEFAULT_HEAD_X_ROT=+0.8727rad(低头)↔geo [50,0,0] | 旧 (-x,-y,-z) 把 hoglin 头仰起、马/驴脖子后仰"昂着"、马尾上翘、耳朵内倒;绝对替换把 50° bind 压平成 0 | 探针 REST 310°→50°;golden rebase 5 rotation 物种(armadillo/bee/horse/donkey/hoglin),其余 21 个 0 diff;av91+golden36+SelfTest 全绿;侧视截图 AI 复核 3 项全过 |
| M22 | 通用 part_visibility:Resources/RenderControllers/<species>.render_controllers.json(原版数据文件)→BedrockGeoImporter.ApplyPartVisibility;支持 Bone/Bone*/*Suffix 通配 + 布尔字面量 + molang 布尔表达式;query.*/variable.* 引擎角色默认 false(裸生物无装备) | 驴/马曾把 Saddle/BagL/R/Bridle/Bit/Reins/短Ear 全建模(22骨全家桶),裸驴驮箱+马露出骡耳 | donkey_v3 RC:Bag*=is_chested,Saddle=is_saddled,Ear*=false;horse_v3:Bag*=false,MuleEar*=false;golden rebase horse/donkey(golden35 26/26) |



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

## M24 (2026-09-28) 骨骼 rest rotation 携带子骨骼(马/驴歪头+疣猪耳飞 同根因)
- **语义**(eyelib `ModelPoseTransforms.applyBone`: translate(pivot)→rotate(rest)→translate(-pivot) 递归;SimpleBedrockModel `convertPivot`+`translateAndRotateAndScale`): 骨骼显式 `rotation` 通过场景图携带子骨骼。子骨 localPos = 原始 pivot 差,不反旋转。
- **旧 bug**: importer pass 2c/3 用 `Inverse(parent.localRotation)` 抵消父 rest → 骨自身 cube 随 rest 转、子骨冻结在未旋转 authored 坐标。马 Neck[30,0,0] 转了颈块但 Head/Muzzle/耳留在高位(歪头,look_at_player 的颈 yaw 也传不到头);疣猪 head[50,0,0] 低头但双耳留在未低头位置(耳朵飞)。
- **修复**: 删除两处 Inverse 补偿(BedrockGeoImporter 2c + pass 3 cube local)。数值验证: horse Head cube (0,1.906,0.469)→(0,1.809,0.895)(被颈 30° 携带);马耳 y 2.245>头顶 2.151(飞)→2.059<2.054(贴合);驴 MuleEar 基座嵌入头顶;疣猪耳 z 1.296(脸前悬空)→0.547(头后侧贴附)。BCF 结构正视: Head/Mane/Neck centre=149(正中), 双耳 134/165 精确镜像。
- **门禁**: m23b.lateral_symmetry 改静止骨架语义(walk trot 中途帧对角步态合法不对称);SelfTest PASS;golden rebase(horse/donkey/hoglin 3 物种恰为 diff 集)后 26/26;AnimVerify PASS。
- **教训**: rest rotation 是场景图变换,不是"仅自身 cube"的修饰 — 两个参考实现源码是权威,数值探针(EAP/BCF)比视觉截图快且硬。

## M25 (2026-09-28) 水生三物种(salmon/pufferfish/axolotl):fish archetype + 家族 AC + 引擎态注入
- **资产**:原版 RP 三件套(entity/geo/tex)+ anims + 家族 AC。salmon 1.8 旧格式、
  axolotl 1.21 `minecraft:geometry` 数组、pufferfish large(鼓起态,无 tailfin=官方设计)。
- **fish 家族 AC fallback**:salmon/pufferfish 引用 `controller.animation.fish.general`,
  住在 `fish.animation_controllers.json`(原版一族一 AC 文件,不是一物种一文件)。
  BlockyAnimal:per-species AC 缺失时按 entity animations 表里 `controller.animation.<family>.`
  前缀收养家族文件(通用机制,后续热带鱼/鳕鱼直接复用)。
- **引擎态注入(locomotion=swim)**:TickControllers 里 `isInWater=1, isOnGround=0`
  (axolotl move.v2 靠 `is_in_water && !is_on_ground` 选 swim;fish.general 靠
  is_in_water 停在 swimming 态)。`aquaticDryLand` 公共标志供 AnimVerify 强制 flop。
- **AnimationAmount 相位**:engine 变量,pre_anim `AnimationAmountBlend=lerp(Prev,Amount,
  frame_alpha)`;我们注入 `animationamount = distanceMoved*14.325*4`(与 dist-cos 同源),
  变量名一律小写存储(Molang 大小写不敏感,pre_anim 大写赋值动画小写读)。
- **query 补齐**:is_in_water/is_levitating/is_playing_dead/time_stamp/frame_alpha/
  ground_speed/vertical_speed/body_x_rotation(fish pre_anim + axolotl controller 全覆盖)。
- **AnimVerify 分派**:fish archetype=fin_swing(全后代变换摆幅;无 tailfin 的鼓起河豚
  记 skip);swim 四足(axolotl)swing=窗内中值相对摆幅(腿持有固定泳姿 72.5/80..110/95,
  bind 相对读数恒 ~180);feet_y→swim_y 悬浮带 [-0.35,1.2];head_seam 对 fish 豁免。
- **face**:salmon 眼睛在头两侧面(同 goat 先例),faceOverride rect (22,25,8,4) +
  sideEyes 条带扫 png y4..7。教训:**改 faceOverride 分支后老物种 face 全线 FAIL =
  else-if 链断裂**(ovr 为 null 时才走 HeadFaceRectFromGeo)。
- **门禁**:SelfTest PASS / AnimVerify 324 PASS / golden 29/29(新 3 物种 capture 入基线)。
