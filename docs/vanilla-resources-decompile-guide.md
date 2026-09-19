# 原版资源获取与官方代码反编译指南

> 2026-09-20 固化。全套流程当天实测跑通:45 个官方模型几何、20 张原版贴图、
> 5048 个官方命名的反编译源文件(`_vanilla-src\decompiled\`)。
> 一键脚本:`Tools\Fetch-VanillaResources.ps1`、`Tools\Invoke-VanillaDecompile.ps1`。

---

## 0. 总原则

- **网络一直是通的,别再"两眼一抹黑"。** 官方数据先拉到本地,再动手写代码。
- 数据权威性排序:**官方数据文件(bedrock-samples / 官方映射)> 官方资产镜像 > 反编译源码推读 > 肉眼猜**。
- bedrock geo 与 Java 源码在个别盒子上差 1-2px(如猪鼻 y):**以参考图视觉 + bedrock 为准**。

## 1. 官方资源获取

### 1.1 贴图(64×32 皮肤表)

- 来源:`https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/<版本>/assets/minecraft/textures/entity/<path>.png`
  - 该仓库是 **Mojang 官方资产文件的逐版本 GitHub 镜像**,分支名 = 游戏版本号(如 `1.20.4`)。
  - 路径示例:`entity/pig/pig.png`、`entity/creeper/creeper.png`、`entity/chicken.png`(不同生物层级不一,404 就查仓库目录树)。
- 备选(同为官方):版本清单 → `assetIndex` → 资源对象 CDN `https://resources.download.minecraft.net/<hash前2位>/<hash>`,按官方索引文件取完整资产表(较繁琐,一般不需要)。
- 落盘:`_refs\vanilla\textures\<name>.png`。

### 1.2 模型几何(每个盒子的尺寸/原点/枢轴/UV,数据驱动)

- 来源:**Mojang 官方仓库** `https://github.com/Mojang/bedrock-samples`,目录 `resource_pack/models/entity/<mob>.geo.json`。
- 列目录:`https://api.github.com/repos/Mojang/bedrock-samples/contents/resource_pack/models/entity`(190 个)。
- 约定与移植:
  - bedrock JSON 中 **−Z = 正面**,本项目 +Z = 正面 → **z 全部取反**;
  - 单位 = **像素/16 米**;`origin` 是盒子角点不是中心;带 `"pivot"+"rot"` 的盒子绕枢轴旋转(如四足躯干 rot[90],本项目用 `QuadrupedBodyNet` 对应);
  - bedrock 贴图 UV 与 Java 版基本一致,个别生物不同,以本项目皮肤表的实测矩形为准。
- 落盘:`_refs\vanilla\geo\<name>.geo.json`。

### 1.3 参考渲染图(拿来当复刻参考/锚点来源)

- minecraft.wiki 的生物渲染(裁切图,约正交投影,头常相对身体偏转)。
- 特征锚点位置(瞳/鼻孔/喙/髯)用 `System.Drawing.GetPixel` 在原图上**实测**,别凭印象写。

## 2. 官方代码反编译(游戏逻辑源码)

官方不开源,但**官方渠道能拿齐反编译所需的一切**:官方 jar + 官方混淆映射表(1.14.4 起 Mojang 随版本发布)。Fabric/Forge 生态同款原理。

### 2.1 四件套下载

| 组件 | 来源 | 备注 |
|---|---|---|
| JRE 21 | `https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse` | zip 免安装 |
| 版本清单 | `https://piston-meta.mojang.com/mc/game/version_manifest_v2.json` | → 版本 URL → 版本 JSON |
| client.jar + client_mappings.txt | 版本 JSON 的 `downloads.client` / `downloads.client_mappings` | 官方分发,约 24MB+9MB |
| Vineflower | `https://repo1.maven.org/maven2/org/vineflower/vineflower/1.10.1/vineflower-1.10.1.jar` | 反编译器 |
| AutoRenamingTool | `https://maven.neoforged.net/releases/net/neoforged/AutoRenamingTool/<ver>/AutoRenamingTool-<ver>-all.jar` | 映射反改名;**必须 `-all` 胖 jar**(裸 jar 无主清单);版本看 maven-metadata.xml 的 `<release>` |

### 2.2 关键命令

```powershell
# ① 官方映射反改名(映射方向坑见下)
java -jar art.jar --input client.jar --output client-mapped.jar `
     --map client_mappings.txt --reverse --ann-fix --ids-fix --src-fix --record-fix

# ② 全量反编译(3-5 分钟,内部类合并进外部类文件)
java -jar vineflower.jar --silent client-mapped.jar decompiled\
```

### 2.3 踩过的坑(全部真实踩过)

- 💣 **映射方向**:官方 ProGuard 映射行形如 `net.minecraft.world.entity.animal.Pig -> aua:`——**左=命名,右=混淆**。输入 jar 里是右侧(混淆)名,所以 ART **必须 `--reverse`**,否则等于没映射(症状:产物只有 obfuscate/ 目录、没有 world/entity/animal/Pig.java)。
- 💣 **Vineflower 单体不应用外部映射**,只反编译;改名必须先过 ART(或 Fabric loom 等工具链)。
- 💣 ART 裸 jar 无主清单(`没有主清单属性`),报这个就是下错文件,要 `-all` 分类器。
- ART 的 `--help` 在中文环境下会抛 `MissingResourceException`(joptsimple 资源本地化缺失),无害,选项照用。
- Vineflower `--thread-count` 等 unknown option 会 `warn: missing ... ignored`,无害。
- 验证映射是否成功:看 `decompiled\net\minecraft\world\entity\animal\Pig.java` 是否存在、类声明是否 `Pig extends Animal implements ItemSteerable, Saddleable`。

### 2.4 产物导航(`_vanilla-src\decompiled\`)

| 想看什么 | 路径 |
|---|---|
| 生物属性/行为(AI、繁殖、食物) | `net\minecraft\world\entity\animal\Pig.java` 等 34 个 |
| 模型几何(盒子尺寸/枢轴/UV) | `net\minecraft\client\model\PigModel.java`、`QuadrupedModel.java`、`ChickenModel.java` |
| 渲染层 | `net\minecraft\client\renderer\entity\...` |
| 总量 | 5048 个 .java(22598 个 class,内部类已合并) |

### 2.5 许可边界

- 官方映射文件头写明"按原样提供,仅用于开发目的";反编译产物版权归 Mojang,受 Minecraft EULA 约束。
- **只作本机开发参考,不要再分发**(包括把反编译源码提交进仓库)。`_vanilla-src\` 已在本地,注意别 push。

## 3. 快速上手

```powershell
# 资源(默认集合;加 -More 扩展;geo -More 拉全部 190 个)
powershell -ExecutionPolicy Bypass -File Tools\Fetch-VanillaResources.ps1

# 官方源码反编译(默认 1.20.4;可 -Version 1.21.1)
powershell -ExecutionPolicy Bypass -File Tools\Invoke-VanillaDecompile.ps1
```

两个脚本均可重复执行(已存在的文件跳过;反编译会清空重建 `decompiled\`)。
