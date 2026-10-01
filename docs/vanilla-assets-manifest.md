# Vanilla 资产路径清单（bedrock-samples v1.21.80.3）

> 本清单来自历史目录列举，已核验存在。**以后要官方资源先查这里，不再调 API 列目录。**
> raw 下载前缀: `https://raw.githubusercontent.com/Mojang/bedrock-samples/v1.21.80.3/`
> 列目录 API（仅在确实缺路径时用）: `https://api.github.com/repos/Mojang/bedrock-samples/contents/<dir>?ref=v1.21.80.3`

## 方块贴图 resource_pack/textures/blocks/
- 床: bed_feet_end / bed_feet_side / bed_feet_top / bed_head_end / bed_head_side / bed_head_top .png ✅已用
- 熔炉: furnace_front_off / furnace_front_on / furnace_side / furnace_top .png ✅已用
- 门: door_wood_lower / door_wood_upper .png ✅已用（其余材质 spruce/birch/jungle/acacia/dark_oak/iron 同命名式）
- 高炉: blast_furnace_front_off / blast_furnace_front_on / blast_furnace_side / blast_furnace_top .png
- 门(每材质 lower/upper 一对): door_wood / door_spruce / door_birch / door_jungle / door_acacia / door_dark_oak / door_iron .png ✅door_wood 本轮使用
- bedrock.png
- （其余 300+ 常规方块贴图按名字直接拼路径即可，如 stone.png、cobblestone.png、planks_oak.png——不用再列目录）

## 实体贴图 resource_pack/textures/entity/
- 箱子: chest/normal.png ✅已用（64×64 图集；**已验证 UV 布局**：盖沿 lid@texOffs(0,0) 14×5、箱体 base@texOffs(0,19) 14×10；正面=盖(14,14)+体(14,33)竖拼中央锁扣、侧面=(0,14)+(0,33)、顶=(14,0) 14×14。成品 16×16 tile 缓存在 scratch/vanilla_blocks/chest_{front,side,top}_v.png）、chest/double_normal.png、chest/ender.png、chest/trapped.png
- 生物: chicken.png / cow.png / fox.png / goat.png / mooshroom.png / pig.png / sheep.png / sheep_fur.png / wolf.png（已缓存在 _refs/vanilla/textures/）

## 实体几何 resource_pack/models/entity/
- 190 个 mob geo.json（_refs/vanilla/geo/ 已有缓存）；箱子/门无独立 geo（门=方块贴图，箱子=chest entity 64×64 图集自定位）

## 本地缓存
- D:/zlj_world/_refs/vanilla/{geo,textures,anims}/ — 生物管线缓存
- scratch/vanilla_blocks/ — 床+熔炉 10 张已嵌入 PNG


## 方块贴图（bedrock-samples v1.21.80.3，注意：png 大多 404，实际是 .tga）

| 资源 | 本地缓存 | 用途 | 备注 |
|---|---|---|---|
| leaves_oak（原木树叶） | _refs/vanilla/blocks/leaves_oak.tga | VoxelCraft leaves.png.bytes | **tga 是灰度图**，引擎无 tint，需 PIL 按 plains 绿 (119,171,47) 乘灰上色；leaves_oak_opaque.tga 404 不存在；carried 变体也是灰度 |
| terrain_texture.json | _refs/vanilla/blocks/atlas.json | 查方块贴图路径的权威索引 | 命名规律：leaves_oak / leaves_oak_carried（不是 oak_leaves！）|

## 2026-10-01 扩充:动物移植候选(geo+tex 已缓存至 _refs/vanilla/)
- camel / frog(temperate) / turtle(sea_turtle.png) / sniffer / strider / tadpole / allay / glow_squid(.tga!) — geo 均已下至 _refs/vanilla/geo/,tex 在 _refs/vanilla/textures/
- 其余 89 个未移植 geo 见 api 列表(多为 hostile/variant:blaze/ghast/warden/ender_dragon 等)

## 变体贴图(M36 动物变种,bedrock-samples v1.21.80.3)

下载缓存在 `_refs/vanilla/textures/variants/`(43 张 + manifest.json 记录 species/variant->官方路径)。
落地 `VoxelCraft/Assets/Resources/Textures/{species}_{variant}_skin.png.bytes`(默认变体=拷贝现有 {species}_skin)。

| 物种 | 变体 |
|---|---|
| fox | arctic |
| frog | temperate/cold/warm |
| rabbit | brown/white/black/white_splotched/gold/salt/toast |
| panda | default/lazy/worried/playful/brown/weak/aggressive |
| axolotl | lucy/wild/gold/cyan/blue |
| parrot | red_blue/blue/green/yellow_blue/grey |
| wolf | default/ashen/black/chestnut/rusty/snowy/spotted/striped/woods |
| mooshroom | default/brown |
| chicken / cow / pig | default/warm/cold |
| ocelot | wild/black/red/siamese |

horse/llama 变体是 markings/decor 分层贴图(双文件合成),未纳入本轮。

## 狗/驯服项圈机制(M36)

官方 v1.21.80.3 wolf 的 tame 贴图(wolf_tame.png 等 10 张)**在仓库里不存在**(entity def 引用但文件缺失,404 实锤)——官方驯服外观就是 base 贴图 + 项圈几何件显隐。项目实现:`BlockyAnimal.SetTamed(bool)` 建 Collar 小方块(纯色红材质,不碰贴图)挂 head 骨下,SetActive 切换;预览工具 "tamed (collar)" 开关。wolf variants 回归 9 个生态型(移除误加的 *_tame)。
自建狗资产 dog_brown/dog_urban(用户 Blockbench MCP 作品)已注册为独立物种(geo+skin 128x128,walkClip=quadruped.walk)。

## mmmm VOX 狗的骨骼补全(M36)

dog_brown/dog_urban 源自 mmmm 包 `_refs/mmmm/vox/mob_dog1.vox`/`mob_dog2.vox`(212 体素,vox_to_creature.py 贪婪盒转换)。vox 无骨骼,原始转换只有 body 单骨 → quadruped.walk 找不到 leg0..3,腿冻结。
**补全**(2026-10-01):按方块几何重分组为 body/head/tail/leg0..3 七骨(腿=细柱+脚片、头=大块+耳柱、尾=末端小块),pig 规范 leg0=-x前/leg1=+x前/leg2=-x后/leg3=+x后(对角步态相位 leg0/leg3 同相)。walkClip 无需改(quadruped.walk 本就驱动 leg0..3)。dog_urban 的头柱(z1..3 细高柱)曾误入 body,已归 head。
验证:两狗 walk 40 帧全连通+39/39 帧间运动。
