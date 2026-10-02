# M40 道具展示(M40PropsSnapshot)踩坑总结

> 2026-10-02,批 3 的 29 件街道具截图交付过程中暴露的全部问题。
> 姊妹文档: `vox-pipeline.md`(建筑管线规则)。本文件只记道具展示台的教训。

## 一、展示台 rig 本身的 bug(4 个,全部数值实锤)

### 1. C# 字符串转义污染 → 编译错误
用 fuzzy patch 写入带引号的 `$"..."` 插值字符串时,工具落盘成了
`string.Join(\\\",\\\", ...)`(字面反斜杠),Unity 报 CS 编译错。
**修法**: 含引号的日志行改用普通字符串拼接
(`Debug.Log("[M40P] census " + name + ...)`),彻底避开转义;
`Object.DestroyImmediate` 同时报 CS0104(与 `object` 二义性)→ 写全 `UnityEngine.Object`。

### 2. Shader 名字写错 → Material 为 null
`Shader.Find("VoxelCraft/Blocks")` 返回 null,`new Material(null)` 直接
ArgumentNullException。正确写法(对照 M40Snapshot.cs L38):
```csharp
var solidMat = new Material(Shader.Find("Voxel/Blocks")) { mainTexture = atlas.atlas };
```
**教训: 新 rig 一律先抄同类 rig 的初始化行,不要凭记忆写 API 名。**

### 3. 平台垫在"地形最高点"→ 高件顶部被 ChunkHeight 静默截断
原实现 `platBaseY = max(HeightAt(平台区))` ≈ 66;dogstand 换全分辨率后 H=20,
伞层 y80+ 超过 `ChunkHeight=80` 被**无提示丢弃**(unity 端 census 287 vs 文件 346,
差的 59 体素正好=源 z13/14/15 伞层)。
**修法**: 平台固定低高度 `platBaseY = 34`,上方清空 40 层。
**教训: 任何 stamp/census 差值都要对账到具体层,ChunkHeight 截断是静默的。**

### 4. 世界生成结构污染展示台
`AnchorsNear` 以 30%/cell 概率在世界各处 stamp 建筑,平台区也在其中:
dogstand census 一度=642(文件才 346),多出的 296 里有 257 个 Brick 是隔壁楼盘。
**修法**: rig 启动时把 `TerrainGenerator.StructureSet`(readonly 数组,元素可变)
全部替换成不存在的名字,`Load()` 返回 null → 跳过。跑完不恢复(批处理进程即退出)。
**教训: 展示台必须关掉随机世界结构,否则 census 永远对不上账。**

## 二、道具数据侧的 bug(3 个)

### 5. 稀疏线框件禁用 /2 降采样
dogstand 源 40×40×20 但只有 **346 个体素**(伞是单层薄壳+杆),`sub=2` 投票后
只剩 90 体素,伞直接消失。**规则: 先看 `n / (X*Y*Z)` 填充率,稀疏件(<10%)
必须 `sub=1` 保真**。dogstand 改 sub=1 后 346 体素全数入镜。

### 6. fence1 轴向误判: 跨度最小的轴不一定是高度
源 31×2×18: y 跨度 2 只是栅栏"厚度",真正的高度在 z(每层 11 体素、连续 18 层)。
`swap=False` 让它整片平铺在地上。改 `swap=True` 立起来(31x18x2 竖栅栏)。
**教训: swap 判定要看逐层体素数剖面(哪根轴层数连续、每层数量稳定),
不能只看跨度极值。**(与 vox-pipeline.md"前后轴向三定律"同源,补充: 薄件看剖面。)

### 7. ITEM_ROLE 漏色 → 落到荒谬最近邻
- dogstand 伞布 (184,184,184) 未映射 → WoolWhite 是补上了,但 (204,152,48)
  木色落到 **Glowstone**(夜光块)发光;需补全角色映射。
- chair1 整椅 WoolBlack(38 体素占 32)——黑椅子在场景里就是"黑洞"。
  改 Plank+Log 木椅。**规则: 小件主色禁纯黑,黑色只做点缀色。**

### 非 bug(数值排除,勿再查)
- trashcan2 顶上"悬浮方块": 源 z=7/9/11 就是孤立单点体素(与下层重叠=0),
  原作者设计如此,保留。
- 拼图第 30 格空白: 29 件填 5×6,排版余位,正常。

## 三、验收流程教训

1. **census 必须与文件体素数精确相等**(346==346, 240==240),不等=有截断/污染,
   先查 ChunkHeight 和 StructureSet,再怀疑转换器。
2. **vision 会超时**: 大拼图(>130KB)连超 3 次;降到 ~110KB / 240p 格成功。
   兜底: 数值自检(如顶部 1/4 非天空像素占比 94% 证明伞在画面里)。
3. **vision 的"浮空/断裂"要回源验证**: trashcan2 顶块回源查连接性(重叠=0)
   才定性为设计;fence1"平铺"回源查剖面才定性为 bug。不能只信画面。
4. lint EXPECT 同步: 尺寸白名单(HEIGHT_WHITELIST)加 dogstand:20、fence1:18;
   chair1/dogstand 角色集同步更新。76/76 PASS 才重渲。

## 四、当前交付状态

- 29 件道具截图 `_shots/m40p_*.jpg`,census 全数对账,无 ZERO 格。
- 提交: 8ff21ac(rig 修复+黑软化)→ 85b4bbd(污染/截断/fence1)。
- 展示台: `VoxelCraft/Assets/Editor/M40PropsSnapshot.Run`,单件入镜,
  平台 y=34,禁世界结构,雾/亮度全局量先于渲染设置(M31 规则)。
