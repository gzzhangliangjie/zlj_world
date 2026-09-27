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

## 四、红线(违反=返工)
- 语义歧义禁止手推 → 翻本地库源码(eyelib/blockbench/geckolib/SimpleBedrockModel)
- 禁止新 species 特判代码;新配置字段必须先证明"通用机制不可行"
- 禁止调检查器容差凑 PASS
- 用户亲眼所见 > 一切自动检查;vision 误报以 PIL/引擎探针仲裁
- 数值不猜,等 AnimVerify 报告

## 五、已删的特殊字段(机制化的胜利,勿再发明)
- ~~absoluteExtraClips~~ → M3 relative_to 自解析(2026-09-28,av57 全绿删)

## 六、开放项
- animation controllers 状态机未解析(当前用 walk 常驻+extras+behaviours 近似);B 方案完整解 = controllers+molang 条件求值
- ocelot/sit 两域 open;zombie 耳朵细节
- Molang 引擎 query 覆盖率低(target_y_rotation 等=0)
