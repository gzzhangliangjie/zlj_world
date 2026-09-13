# Unity-CLI：VoxelCraft 命令行工作流（Tools/unity-cli.ps1）

> 一条命令封装本项目的全部 Unity 批处理工作流。判定结果一律**解析 Unity 自己写的日志**，
> 绝不只看退出码；所有日志统一落在工作区根目录 `_logs\`（带时间戳，不再散落在项目根）。

## 快速开始

```powershell
# 交互式（cmd / Win+R）
Tools\vx.cmd status

# 或任意 PowerShell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\unity-cli.ps1 status
```

推荐把 `Tools` 加入 PATH，之后 `vx` 即可用。

## 命令参考

| 命令 | 作用 | 启动 Unity? | 耗时 |
|---|---|---|---|
| `status` | 环境/项目状态快照（Unity 进程、项目锁、许可证、csproj 漂移、最近构建与日志） | 否 | <1s |
| `compile-fast` | MSBuild 直接编译 Unity 生成的 csproj（游戏+编辑器两个程序集） | 否 | **~20s** |
| `compile` | Unity batchmode 全量编译检查（权威判定） | 是 | 1~3min |
| `test` | Unity batchmode 跑 `SelfTest.RunAll` 全量回归 | 是 | 1~3min（热 Library 实测 ~10s） |
| `build webgl` / `build win` | 出 WebGL / Windows 包（复用 `WebGLBuild.Build` / `WindowsBuild.Build`，并校验产物文件存在） | 是 | 数分钟 |
| `publish [msg]` | 构建+提交+推送（委托既有 `Tools\publish-webgl.ps1`，逻辑不重复） | 是 | 数分钟 |
| `snapshot` | batchmode 渲染动物/玩家模型快照 PNG 到 `_logs`（`ModelSnapshot.Run`） | 是 | 1~2min |
| `log [file]` | 解析任意 Unity 日志：错误行 / SELFTEST / BUILD 标记 / 实例冲突，给结论 | 否 | <1s |
| `clean [-Yes]` | 删除 `Library/Temp/obj/Logs`（锁被占用时拒绝执行） | 否 | 秒级 |
| `ci [-WithWebGL]` | **发布闸门**：compile-fast（漂移时自动升级全量 compile）→ test | 两段 | ~10s~4min（随 Library 冷热） |

全局参数：`-UnityExe <路径>`（默认 `D:\Unity\2022.3.57f1c2`）、`-TimeoutSec <秒>`（默认 1200）、`-Yes`（跳过确认）。
退出码：`0` 成功、`1` 失败、`2` 用法错误——可直接接 CI 或 agent 闸门。

## 相比手敲 batchmode 的流程优化

1. **秒级编译反馈**：`compile-fast` 用 VS2022 MSBuild 编译 Unity 生成的 csproj（约 20s，含两个
   程序集），语法错误立刻带 文件(行,列) 报出。不必每次迭代都等 Unity 1~3 分钟。
   *权威编译仍以 `compile`/`test`（真 Unity）为准*——fast 只做"快速失败"。
2. **csproj 漂移检测**：新增 .cs 文件而没开过 Unity 时，csproj 里没有它，fast 编译会**静默漏检**。
   CLI 对比磁盘文件数与 `<Compile Include>` 数，发现漂移即警告；`ci` 会自动升级为全量 compile。
3. **日志判定，退出码兜底**：`error CS####`、`Shader error`、`SELFTEST PASS/FAIL`、
   `WEBGL/WINDOWS BUILD OK/FAIL`、`Aborting batchmode`、实例冲突文案全部自动识别，
   末尾给 PASS/FAIL 结论；`build` 还会校验产物文件真的存在。
4. **并发安全（多会话/多 agent 友好）**：CLI 只在"无 Unity 进程 + 无 `Temp\UnityLockfile`"的
   真空闲瞬间出手；启动后若"无日志即死"（退出码 **140063** = Unity 在日志初始化前夭折）则
   一律重试（最多 8 次、15~25s 抖动退避），不再误报成"测试失败"。**实测两种成因**：
   DSH 沙箱策略过紧（workspace-write 下 8/8 秒退；danger-full-access 下同一命令 9 秒跑完全量
   SelfTest）与实例竞争（另一会话 ~70s/轮连跑时撞锁）。
5. **启动器先行退出坑自动处理**（AGENTS.md 2026-09-13 记录的坑）：bootstrap 秒退但真子编辑器
   还在跑时，CLI 会等子进程结束、日志静止 30s 后再读日志判定。前置条件是我们的日志文件
   确实已建立（否则走 140063 冲突分支，避免把别人的 Unity 当成自己的子进程）。
6. **`Start-Process` 句柄坑**：启动后立即 `$null = $p.Handle`，否则 `.ExitCode` 可能读到空值
   （Windows PowerShell 已知行为）——CLI 已内置。
7. **日志归位**：所有产物日志进 `_logs\`（时间戳命名），项目根目录不再堆 `*.log`
   （历史遗留的 `Vselftest_fix*.log` 已归档入 `_logs`）。
8. **一键闸门**：`ci` = 快速编译 + 全量 SelfTest，是"里程碑完成前必须全绿"的标准动作。

## 典型工作流

```powershell
# 日常改代码迭代（最快回路）
vx compile-fast              # 20s 语法检查

# 改完一批，交付前闸门
vx ci                        # fast 编译 + Unity 全量 SelfTest

# 加了新 .cs 文件后（csproj 未再生）
vx ci                        # 自动检测漂移并升级为 Unity 全量编译，无需特殊操作

# 发布网页版
vx ci && powershell -File Tools\publish-webgl.ps1 -Message "note"

# 上次批处理到底失败了什么？
vx log                       # 不带参数 = 解析 _logs 最新一份日志
```

## 已知边界

- `compile-fast` 只编译 csproj 里已登记的文件，**不能发现"新增文件"的语法错误**（漂移警告会提示）；
  资源导入、Shader 真实编译、运行时行为仍必须走 Unity（`compile` / `test` / `build`）。
- `clean` 前请确认没有别的会话正在用这个项目（CLI 会查锁，但跨会话收尾仍有竞态窗口）。
- Unity batchmode 与图形相关验证**不要加 `-nographics`**（技能 `unity-batchmode` 的既有规则）。

## 后续可探索（roadmap）

- git `pre-push` 钩子自动跑 `vx compile-fast`，把低级错误挡在推送前；
- `watch` 子命令：文件变化自动 compile-fast（配合 `Get-Item .\Assets -Recurse` 轮询即可）；
- 构建产物体积报告（对比 `Builds/WebGL/Build/*.unityweb` 尺寸趋势，防体积回退）；
- 把 `MobReplicator`（皮肤拟合实验）封装成非交互子命令，纳入 `_logs` 日志规范；
- 自托管 runner 上跑完整 `vx ci -WithWebGL`，让 GitHub Actions 只做部署；
- 平台构建矩阵：`build win` / `build webgl` 并行（两台机器或接受排队）。
