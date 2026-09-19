# Agent 工作笔记

给 AI agent 看的快速参考。遇到相关问题时先查这里,避免重复踩坑。

---

## 原版真值资源与 MobReplicator 锚定(2026-09-20 固化)

**网络一直可用,别再"两眼一抹黑":官方数据先拉再写码。**

### 原版资源本地库(`D:\zlj world\_refs\vanilla\`)

- **贴图**(`textures\`,InventivetalentDev/minecraft-assets 官方资产镜像,1.20.4):pig/cow/sheep/chicken + creeper/zombie/skeleton/spider/enderman/villager/wolf/slime/iron_golem/mooshroom/panda/bee/ghast/blaze/zombie_villager/phantom/goat/ocelot/squid/bat/fox 等约 20 张。缺失路径变体:witch/blaze(分支或路径不同)。
- **模型几何**(`geo\`,**Mojang 官方** bedrock-samples 仓库,45 个 .geo.json):pig/cow/sheep/chicken/villager/wolf/creeper/zombie/spider/enderman/horse/llama/sniffer/allay/armadillo...。约定:**bedrock −Z=正面,我们 +Z=正面,移植时 z 取反;单位 = 像素/16 米**。
- **官方游戏逻辑源码**(`_vanilla-src\decompiled\`,5048 个 .java,官方命名):管线 = 活动版本清单 piston-meta.mojang.com → 下载 client.jar + client_mappings.txt(ProGuard 格式,**左=命名,右=混淆**)→ NeoForge **AutoRenamingTool**(`--reverse` 关键!否则方向反了等于没映射)→ vineflower 反编译。Vineflower 单体**不会**应用映射;ART 在 maven.neoforged.net,artifact=`net/neoforged/AutoRenamingTool`,要下 **`-all` 胖jar**(裸 jar 无主清单)。PigModel/ChickenModel 等在 `net\minecraft\client\model\`。
- ⚠️ bedrock geo 与 Java 源在个别盒子上差 1-2px(猪鼻 y):**以参考图视觉 + bedrock 为准**(wiki 渲染与 bedrock 一致)。

### MobReplicator 锚定架构(run39 定型,三物种全 PASS)

轮廓 IoU 有平台期(±10° 分数不动),脸却已错位——**必须特征锚定**:

1. **先看参考图再设计锚点**(本轮最大教训:猪参考图面朝左前方、双眼/鼻孔清晰可见,猜了 5 轮全是错的);锚点实测:猪左眼 (20,270) 右眼 (158,339) 鼻孔 (~90,375)。
2. **原版实测锚点表** `VanillaAnchors()`:瞳/鼻孔在脸矩形上的 (a,b) 用 GetPixel 实测(猪眼 a=0.0625/0.9375 b=0.5625;鸡眼 a=0.125/0.875 b=0.75)。
3. **多候选+全局指派**:每锚点取局部极大值前 5 候选,枚举组合(禁共享点+成对间距一致性惩罚)。**独立 argmax 会把双眼塌缩到同一个最强特征上**(猪右眼和鼻孔都抓到左眼)。
4. **头偏转自由度**:wiki 渲染的头相对身体有偏转,单刚体姿态无解(三锚点残差方向互相矛盾就是症状)。`BuildGameModel(species, headYawDeg)` 绕颈枢轴(头背面)转 Head 再采集;对 headYaw ∈ {0,±10,±20,±30} 各建几何各解姿态,取 rms 最小者。猪最优解 headYaw=10°。
5. **姿态扫描+闭式平移**:旋转与平移耦合(逐轴坐标下降会卡死)→ 每个姿态候选下最优 center = 锚点残差均值(闭式),只扫描 yaw/pitch/scale。
6. **验收双门**:锚点 rms < 12px 且 < 0.35 纹素(绝对像素阈值不随尺度缩放,羊 9px 在 49px/纹素下已是亚纹素);**对称闸门** `SheetFaceAsym` < 0.45 否则**不写游戏皮肤**。所有指标都可能骗人,**只有看图仲裁**——数字 PASS 但涂出来是糊的、数字 FAIL 但脸完美,都真实发生过。

### 几何修正(对齐 bedrock 真值)

- 猪头中心 y=0.75m(原公式 1.075m,高 5px,脸全错位的根因);猪鼻**4×3×1** uv(16,16)(原来 8×4×1 错了 2.4 倍间距);鸡头 y=0.75m。
- 快照入口是 `ModelSnapshot.Run` **不是 RunAll**;`-batchmode` 渲染**必须带图形,别加 -nographics**,若忘了 `-quit` Unity 会挂住,CPU 增长+日志出现 MODEL SNAPSHOT OK 后可手动 Stop-Process 收图。

---

## DSH(Harness)模型能力配置

**最后验证:2026-09-13,glm-5.3-flash 读图问题已按此修复并实测通过。**

### 真正生效的配置文件位置

```
DSH_HOME = C:\Users\zlj10\AppData\Roaming\dsh-desktop\dsh-home
生效配置 = C:\Users\zlj10\AppData\Roaming\dsh-desktop\dsh-home\settings.yaml
```

⚠️ **陷阱**:`C:\Users\zlj10\.dsh\settings.yaml` 是旧位置,**不被读取**(改它无效)。
用 `$env:DSH_HOME` 确认真实位置,别只看用户目录下有没有 `.dsh`。

### 模型"支持图像输入"是配置声明出来的

`read_image` 前会检查模型声明的 `inputModalities` 是否含 `image`。
未声明的模型默认按 `["text"]` 处理 → 报错:

```
cannot read "xxx.png" as an image: model "glm-5.3-flash" does not declare image input
```

模型端点本身是否支持视觉与此无关——声明缺失时 harness 在读文件**之前**就拒绝。

### 修复方法

编辑生效配置的 `settings.yaml`,给对应模型条目加 `input`:

```yaml
llm-pi-ai:
  providers:
    glm:
      models:
        - id: glm-5.3-flash
          input:
            - text
            - image        # 加这两行(列表形式)即可
```

- 改完**无需重启**:settings.yaml 有 chokidar watcher 热重载,下一个请求即生效。
- 若想给同 provider 其他模型(如 glm-5.2 / glm-5.3 / glm-5-turbo)开读图,同样加 `input` 声明。
- 过度声明有代价:若端点实际不收图,会在消息已入库后于请求中途失败,会话会反复重试无法成功的请求——声明前先用 `read_image` 实测一张小图确认。

### 排查路径(备查)

- 报错检查代码:`node_modules\@deepseek-ai\dsh-tool-fs\lib\index.js` → `assertImageCapableRoute`
- 能力解析:`@deepseek-ai\dsh-llm-pi-ai\lib\index.js` → `resolveProfiles` / `modelInfo`
- 未声明默认值:`DEFAULT_INPUT = ["text"]`(代码注释:under-claiming 拒图于附着前,over-claiming 卡死会话)
- 热重载机制:`@deepseek-ai\dsh-settings-file`(watch 默认开启)

---

## VoxelCraft 皮肤 UV 网格规则(已固化)

**最后验证:2026-09-13,SelfTest 40/40 绿(含 `m9.skin_uv_nets`)。动皮肤/动物模型前先读这节,别重新踩坑。**

### 通用约定

- 皮肤 64×32,`VoxelCraft/Assets/Resources/Textures/<name>_skin.png.bytes`(TextAsset→LoadImage);动物:pig/cow/sheep/chicken,玩家:player(标准 Steve 排布)。
- 面序固定 `+X, -X, +Y, -Y, +Z, -Z`(`BoxBuilder.SkinnedBox` 六面);矩形 `Vector4(u,v,w,h)`,**v 从贴图顶部数**;动物 +Z 朝头/前;1 世界单位比例 = 皮肤像素/16(`GetDims` 尺寸即像素/16)。
- `SkinnedBox(..., int[] faceRot)` 按面做 90° 采样旋转:`rot1=(b,1-a)`, `rot2=(1-a,1-b)`, `rot3=(1-b,a)`(`RotateUvQuarter`);inset 0.25px 防邻面串色。

### 直立盒子 → `McNet(u,v,W,H,D)`(rot 全 0)

| 面 | 矩形 |
|---|---|
| +X | `(u, v+D, D, H)` |
| -X | `(u+D+W, v+D, D, H)` |
| +Y | `(u+D, v, W, D)` |
| -Y | `(u+D+W, v, W, D)` |
| +Z | `(u+D, v+D, W, H)` |
| -Z | `(u+2D+W, v+D, W, H)` |

适用:玩家头 `(0,0,8,8,8)` / 身 `(16,16,8,12,4)` / 臂 `(40,16,4,12,4)` / 腿 `(0,16,4,12,4)`;动物的头和腿。

### 四足躯干 → `QuadrupedBodyNet(u,v,W,H,D)` + `QuadrupedBodyRots = {1,3,2,0,0,0}`

**躯干在皮肤里绕 X 轴旋转 90° 存储(长轴竖直)**,按直立盒直接展开必然错乱。正确映射:

| 世界面 | 取 MC 矩形 | 矩形 | rot |
|---|---|---|---|
| +X | 东 | `(u, v+D, D, H)` | 1 |
| -X | 西 | `(u+D+W, v+D, D, H)` | 3 |
| +Y(顶) | 北 | `(u+2D+W, v+D, W, H)` | 2 |
| -Y(底) | 南 | `(u+D, v+D, W, H)` | 0 |
| +Z(前) | MC顶 | `(u+D, v, W, D)` | 0 |
| -Z(后) | MC底 | `(u+D+W, v, W, D)` | 0 |

### 物种参数表(`BlockyAnimal.GetSpeciesNets` / `GetDims`,网格参数=像素)

| 种类 | 头 McNet | 躯干 QuadNet | 腿 McNet | 腿厚 | 躯干尺寸(世界) |
|---|---|---|---|---|---|
| pig | `(0,0,8,8,8)` | `(28,8,10,16,8)` | `(0,16,4,6,4)` | 0.25 | 0.625×0.5×1.0 |
| cow | `(0,0,8,8,6)` | `(18,4,12,18,10)` | `(0,16,4,12,4)` | 0.25 | 0.75×0.625×1.125 |
| sheep | `(2,2,6,6,6)` | 羊躯干**不用 QuadNet**:mobs_mc 把"剪毛灰身"画在 x28..47、"有毛白身"画在 **x48..55(bitmap rows16..31)**。我们的羊永远有毛 → 六面全采羊毛矩形 `{(48,16,8,16)×4, 顶(48,16,8,6), 底(48,26,8,6)}`,rot 全 0 | `(0,16,4,12,4)` | 0.25 | 0.5×0.375×1.0 |
| chicken | `(0,0,4,6,3)`+喙 `(14,0,4,3,1)`+翅 `(24,13,1,4,6)` | `(0,8,6,8,6)` | `(26,0,2,5,2)` | 0.125 | 0.375×0.375×0.5 |

⚠️ ASCII 亮度图**数列极易错位**(已数错过两次):判定"某矩形是否透明/是否亮色"必须用 `System.Drawing.GetPixel` 按候选点实测 alpha/luminance,别靠肉眼数列。羊身 x56..63 全透明就是 ASCII 数错列得出的假结论。

鸡翅膀:原版 1×4×6px,挂在躯干侧面 x=±(半宽+0.0325)(留 1mm 缝防共面);翅膀顶面矩形 (25,13,1,6) 在原皮肤中透明,补丁已填羽白 (242,224,208)。鸡**只有 2 条腿**(人形,髋位 x=±0.0625)——`BuildModel` 的 4 髋循环曾让鸡也长 4 条腿, clustered 橙腿看起来就是"粗腿大方块";动画循环对空腿槽需 `continue`。喙区 (14,0)-(25,7) 的透明像素已补喙橙,否则喙盒侧面采到透明→黑色条纹盖在脸上("脸是糊的")。

### 踩坑备忘(全部真实踩过)

- 💣 **挂在其他 SkinnedBox 下的子盒子(喙/猪鼻)必须做父缩放补偿**(2026-09-13e 修复):SkinnedBox 会设 `localScale = size`,子盒继承父(头)缩放后体积被压成薄片(喙缩到 1cm 厚"消失"),且 **localPosition 也会被父缩放缩水**(喙 z 偏移 0.107×0.1875=2cm → 整个埋进头里"看不到")。统一用 `BlockyAnimal.AddSkinnedChild`:localPosition 和 localScale 都除以 `parent.lossyScale`,这样传入的位置/尺寸就是世界米数;
- 💣 **`BoxBuilder.SkinnedBox` 曾漏写 `localScale = size`(2026-09-13d 修复)**:所有 SkinnedBox 部件(动物全身、玩家模型、手持方块)渲染成 **1×1×1 米立方体**——这就是玩家连续报"鸡腿变粗方块/猪牛身体太短/没有翅膀/贴图错乱+闪烁/脸糊/只一只眼"的总根因:①几何比例全毁;②巨盒互相穿插→大面积共面 z-fight;③ UV 拉伸到 1m 脸上自然糊。**给 mesh 顶点做单位立方体+transform.localScale 缩放时,顶点必须居中(`corners - 0.5`)再 scale,否则锚点在角上**。修尺寸类 bug 前先查 `SkinnedBox`/`Box` 是否真的应用了 size;
- **批模式离屏渲染验证流程(强烈推荐,2026-09-13 建立)**:`Assets/Editor/ModelSnapshot.cs` 在 `-batchmode`(带图形,勿加 -nographics)里造出真实动物/玩家模型,手动 `Shader.SetGlobalFloat("_VoxelDayBrightness",1f)`(否则 unlit 皮肤渲染全黑)+`_VoxelFogRange=(100,300)`,`cam.Render()` 到 RenderTexture→ReadPixels→PNG 存 `_logs/`,再用读图工具**亲眼看**。推理十轮不如看图一张——本次"贴图错乱"连续几轮没修好,就是直到出快照才看见真凶;
- 注意:`Renderer.bounds`/`mesh.bounds` 在批模式快照里可能返回无意义值(单位立方体),别拿它当几何证据;模型 invisible 可能是设计行为(玩家 `ThirdPersonRig:108` 第一人称默认 `modelRoot.SetActive(false)`),快照时需手动激活;
- 猪头是 8×8×**8**,别照抄牛的 8×8×6(眼睛错位 2px);
- 羊头在 `(2,2)`(画师两侧各涂了 2px 边框填充);
- 猪腿只有 **6** 像素高,写 12 会把右+前两个腿面内容竖向串进同一条腿;
- mobs_mc 鸡腿区域几乎全透明 → 已用喙橙 (223,139,77) 填充 6 个腿矩形(改编已记 NOTICE.md);透明像素在 Unlit 无透明 Shader 下渲染为**黑色**;
- MC 常把 -Y 底面留空(鸡头底全透明)→ 自测 `NetHasPixels` 对底面(索引 3)豁免像素检查,仍查越界;
- **共面即闪(z-fighting)**:盒子相接处两边面落在同一数学平面(深度完全相等)→ 该区域永久闪烁,看起来像"贴图错乱"。规则:**相接盒子必须重叠沉入对方 ≥10mm,绝不齐平**。已修:动物四腿顶面沉入躯干 12mm、鸡喙背面沉入头部 18mm、玩家手臂内侧面沉入躯干 12mm(肩点 x=0.362)、四肢 Z 宽 24cm(躯干 25cm)、玩家头 X 宽 48cm(躯干 50cm)。摆新盒子时逐对检查 6 个面;
- `Unity.exe &` 调用偶发**启动器先行退出(exit 0)而子编辑器还在跑**:批处理任务"完成"但日志只有 13KB 停在启动期时,先 `Get-Process Unity` 轮询等子进程退出再读日志;
- 💣 **批模式秒退 exit 140063 且完全不写 -logFile(2026-09-14 双重实锤)**:两个成因——①**DSH 文件沙箱策略过紧**:workspace-write 下 Unity 启动期写被拒,8/8 连败;切 danger-full-access 后同一命令 **9 秒**跑完全量 SelfTest(热 Library 时批模式可秒级完成);②实例竞争(另一会话 ~70s/轮连跑 MobReplicator 时撞锁)。规范:140063 无日志**先看当前沙箱/文件策略**,再确认"无 Unity 进程 + 无 `Temp\UnityLockfile`";重试加抖动退避。已封装进 `Tools/unity-cli.ps1`(自动排队+重试+日志判定),优先用它别手敲;
- 💣 **PS `Start-Process -PassThru` 的 ExitCode 可能为空**(2026-09-14 实锤):spawn 后必须立即 `$null = $p.Handle` 拿句柄,否则进程退出后 `.ExitCode` 读回空值,成功会被判成失败;
- **版本号 `Game.BuildId`**(如 2026-09-13d)显示在 HUD 右下角——GitHub Pages 对 index.html 也有 ≤10 分钟缓存,玩家报"改了没生效"先让其 Ctrl+F5 并核对角落 build 号。
- `publish-webgl.ps1` 里**所有 git 命令段都要降 EAP=Continue**(git 的 LF/CRLF warning 走 stderr,PS5.1 在 EAP=Stop 下会把它变成终止错误;调用方若再套 `2>&1` 更必炸)。commit+push 已拆成段内局部降级,新增 git 步骤记得照做。

### 验证闸门

- 改任何网格后必须跑 `SelfTest.RunAll`(`m9.skin_uv_nets` 逐矩形越界+像素校验;`m11.skinned_uv_sampling` 读回真实 mesh.uv 对照 MC 网格+旋转约定)全绿才算完成;流程见技能 `unity-batchmode`。
- 改网格前可离线肉眼预览:用 System.Drawing 按上表把六面从皮肤裁剪→按 rot 旋转→拼 T 型展开图查看(2026-09-13 验证时用过,临时脚本已删,照此思路重写即可)。

### 图像行序/翻转陷阱(多次踩坑总结,2026-09-14 固化)

**翻转类 bug 反复出现,根源是整条管线里同时存在 4 种行序约定,任何一处漏翻就上下颠倒。** 动图像代码前先对照这张表:

| 环节 | 行序约定 |
|---|---|
| `Texture2D.GetPixels/SetPixels` | row 0 = **底部**(GL 风格,自下而上) |
| PNG 文件 / System.Drawing / 读图工具显示 | row 0 = **顶部**(自上而下) |
| `MobReplicator` 屏幕坐标 `Project()` | **y 向下**(顶部为 0),`Ref.pxTop/bgTop` 已转成自上而下 |
| `Camera.Render` → `ReadPixels` | 读回**自下而上**,必须翻成自上而下再和参考图对比/合成 |

排查口诀:**看到" anatomically 不可能"(鸡肉髯长喙上方、猪蹄朝天)先怀疑管线行序,再怀疑素材**;用 `System.Drawing.GetPixel` 实测特征色(肉髯红/蹄深色)的 meanY 位置做客观判定,别靠肉眼读缩略图——本次连续三轮把正常参考图误判为翻转,就是缩略拼图看走眼,像素统计和全分辨率单图才是仲裁。

- `MobReplicator` 已内置**自动定向**:对原图和垂直翻转图各拟合一次投影取 IoU 高者(`FlipVertical` + `RunOne` 双拟合),新参考图翻转也自愈;但注意**正立模型 + pitch 钳制(5..80°)本来无法拟合倒立目标**,翻转分支得分低不完全等于图没翻转,最终仲裁仍靠上面的特征色实测。
- 透明像素在 Unlit 无透明 Shader 下渲染为**黑色**(两次踩到):生成皮肤后强制 `a=1`;参考图 alpha<0.5 的 AA 光晕按背景处理。
- 对比渲染相机必须与软件投影器**严格等价**(ortho:`orthographicSize = h/(2*projScale)`;透视:`fov = 2*atan(h/(2*focal))`,相机距 `projDist`,中心对 `projCenter`),否则复刻面板取景错乱像"相机插进模型里"。`Renderer.bounds` 取景在批模式是垃圾值,禁用。

### MobReplicator v2(截图复刻工具,2026-09-14 重写)

`Assets/Editor/MobReplicator.cs`,批处理入口 `RunExperiment`(三物种)/`RunAngleProbe`(投影诊断)。**架构原则:模型即游戏模型,杜绝任何约定复制。**

- **几何**:`BlockyAnimal.BuildModel()` 造真实游戏模型(临时 GO,重置根旋转后),从 24 顶点 mesh 直接采集每面世界四边形+UV 角点——画到哪个贴图纹素由 mesh UV 双线性插值给出,McNet/QuadNet/rot/行序全部零重复;
- **拟合**:几何固定不动,只拟合投影。粗搜 4 象限 yaw×pitch → 坐标下降 → **宽范围 scale/center IoU 下降**(bbox 对齐只做初值!wiki 裁剪图主体贴边被切,主体 bbox=图像边界,直接用必偏)→ yaw/pitch 交替精修;
- **实测结论**:wiki 渲染 ≈ 正交投影(透视距离 2.5/4/6m 全部更差)、yaw≈133-146、**pitch≈38-43(不是 30)**;参考图是裁剪切边的,fit IoU 天花板 ≈0.87(猪/鸡/羊实测 0.874/0.877/0.874,渲染回读 0.86/0.84/0.71);
- **画肤**:①逐像素深度预通道(所有朝画面 1px 密度光栅化取 min-depth)→ ②每个纹素先遮挡测试(`depthBuf < d - 0.02m` 则跳过)再采样,治好"鸡腿采到脚部黑影变黑腿";③全局 min-depth staging 解决 4 腿+2 脚共用一个 legNet 的争抢;④零样本盒(肉髯)先盒均值回退、再穿透采样兜底;
- **应用**:产出直接写 `Assets/Resources/Textures/<species>_skin.png.bytes`(游戏加载路径,`applyToGame` 开关),牛无参考图保持原样;自测闸门不变(`vx ci`,`m9.skin_uv_nets` 对新皮肤照常校验)。

### 手持道具 3D 模型(m10)

- 工具/食物 = **24×24 图标 alpha 挤出**:front+back 双面 + 透明邻边的侧壁,单 Mesh 单 DrawCall(`ItemModelFactory.BuildFlat`);1 像素=0.02 世界单位,厚 0.04;
- 手持方块 = `BoxBuilder.SkinnedBox` + **图集像素矩形**(BlockDef 的 side/top/bottom tile,96×80 图集坐标 `AtlasPxRect`),不是 16×16 图标——草方块才有绿顶土侧;
- 材质统一 `Shaders/UnlitTextureShader`(Resources.Load,带昼夜变暗/雾);实例按 `tool:N`/`block:N`/`item:x` 缓存复用,Show 时在两个锚点间搬移;
- 锚点:第一人称挂相机 `(0.44,-0.37,0.62)`,第三人称挂 `ThirdPersonRig.hand`(右臂 -0.7 处子节点,随臂摆);挥手由 `BlockInteraction.OnUse` 事件驱动;
- 自测:`m10.item_models`(全部工具/食物/22 种方块模型有网格)+ `m10.held_view`(无引用 Build 不抛)。
