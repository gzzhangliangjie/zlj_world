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
