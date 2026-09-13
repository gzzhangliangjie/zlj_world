# Agent 工作笔记

给 AI agent 看的快速参考。遇到相关问题时先查这里,避免重复踩坑。

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
| sheep | `(2,2,6,6,6)` | `(28,8,8,16,6)` | `(0,16,4,12,4)` | 0.25 | 0.5×0.375×1.0 |
| chicken | `(0,0,4,6,3)`+喙 `(14,0,4,3,1)` | `(0,8,6,8,6)` | `(26,0,3,5,3)` | 0.1875 | 0.375×0.375×0.5 |

### 踩坑备忘(全部真实踩过)

- 猪头是 8×8×**8**,别照抄牛的 8×8×6(眼睛错位 2px);
- 羊头在 `(2,2)`(画师两侧各涂了 2px 边框填充);
- 猪腿只有 **6** 像素高,写 12 会把右+前两个腿面内容竖向串进同一条腿;
- mobs_mc 鸡腿区域几乎全透明 → 已用喙橙 (223,139,77) 填充 6 个腿矩形(改编已记 NOTICE.md);透明像素在 Unlit 无透明 Shader 下渲染为**黑色**;
- MC 常把 -Y 底面留空(鸡头底全透明)→ 自测 `NetHasPixels` 对底面(索引 3)豁免像素检查,仍查越界。

### 验证闸门

- 改任何网格后必须跑 `SelfTest.RunAll`(`m9.skin_uv_nets` 逐矩形越界+像素校验),40 用例全绿才算完成;流程见技能 `unity-batchmode`。
- 改网格前可离线肉眼预览:用 System.Drawing 按上表把六面从皮肤裁剪→按 rot 旋转→拼 T 型展开图查看(2026-09-13 验证时用过,临时脚本已删,照此思路重写即可)。
