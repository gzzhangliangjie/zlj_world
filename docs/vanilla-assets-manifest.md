# Vanilla 资产路径清单（bedrock-samples v1.21.80.3）

> 本清单来自历史目录列举，已核验存在。**以后要官方资源先查这里，不再调 API 列目录。**
> raw 下载前缀: `https://raw.githubusercontent.com/Mojang/bedrock-samples/v1.21.80.3/`
> 列目录 API（仅在确实缺路径时用）: `https://api.github.com/repos/Mojang/bedrock-samples/contents/<dir>?ref=v1.21.80.3`

## 方块贴图 resource_pack/textures/blocks/
- 床: bed_feet_end / bed_feet_side / bed_feet_top / bed_head_end / bed_head_side / bed_head_top .png ✅已用
- 熔炉: furnace_front_off / furnace_front_on / furnace_side / furnace_top .png ✅已用
- 高炉: blast_furnace_front_off / blast_furnace_front_on / blast_furnace_side / blast_furnace_top .png
- 门(每材质 lower/upper 一对): door_wood / door_spruce / door_birch / door_jungle / door_acacia / door_dark_oak / door_iron .png ✅door_wood 本轮使用
- bedrock.png
- （其余 300+ 常规方块贴图按名字直接拼路径即可，如 stone.png、cobblestone.png、planks_oak.png——不用再列目录）

## 实体贴图 resource_pack/textures/entity/
- 箱子: chest/normal.png（64×64 图集，含 latch 可定位正面）、chest/double_normal.png、chest/ender.png、chest/trapped.png ✅normal 本轮使用
- 生物: chicken.png / cow.png / fox.png / goat.png / mooshroom.png / pig.png / sheep.png / sheep_fur.png / wolf.png（已缓存在 _refs/vanilla/textures/）

## 实体几何 resource_pack/models/entity/
- 190 个 mob geo.json（_refs/vanilla/geo/ 已有缓存）；箱子/门无独立 geo（门=方块贴图，箱子=chest entity 64×64 图集自定位）

## 本地缓存
- D:/zlj_world/_refs/vanilla/{geo,textures,anims}/ — 生物管线缓存
- scratch/vanilla_blocks/ — 床+熔炉 10 张已嵌入 PNG
