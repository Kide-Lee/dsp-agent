# AGENTS 合规整改结果

> 对应审查：[AGENTS合规审查报告.md](AGENTS合规审查报告.md)
> 验证时间：2026-10-29

## 已修复

### 运行期配置

- 移除 `ICARUS_BLUEPRINT_DIR`、`ICARUS_WIKI_ZH_API`、`ICARUS_WIKI_EN_API`、`ICARUS_II_LOG_LEVEL`。
- `sidecar/config.json` 新增 `blueprintDir`、`wiki.zhApi`、`wiki.enApi`；`ConfigStore` 提供 `blueprintRoot()` 与 `wikiEndpoint()`。
- `blueprint/workspace.ts`、`blueprint/tools.ts` 不再各自解析环境变量。
- 新增 [docs/config.md](../config.md)，`README.md`、`design.md` 改为指向该文档。

### 回环与 sidecar 启动

- `isLoopbackHost` 统一允许 `127.0.0.1` / `127.*` / `::1` / `localhost`。
- `server.ts` 对非回环地址直接抛错；CLI `--host` 在启动前拒绝并退出 2。
- mod 侧 `SidecarLauncher.TryPrepare` 先校验 `SidecarHost`，`SidecarOptions.Uri` 支持 IPv6 方括号。
- `Sidecar Host` 配置说明改为“仅允许回环”。

### 协议

- `docs/protocol-v1.md` 补充 `config_required`、`tool_result.error.retryable?`、B.1 本地工具范围。
- `hello.saveKey` 现在显式写 `null`，不再用“缺字段”表示解绑。
- `config.set` 支持显式清除 `baseUrl`（C# `clearBaseUrl`）。
- `ConfigState` 解析 `active.provider` / `active.model`，并作为 `provider`/`model` 的回退。
- `ConfigState` 的 fallback `hasKey` 改为 `!NeedsKey`，不再用 `IsReady`。
- `tool_debug` 的 64 KiB 限制按 UTF-8 字节计算，C# 与 sidecar 一致。
- `ToolErrorPayload.retryable` 改为可选，缺省 false。
- 增加 `BuildConfigSet` 清除 `baseUrl`、`ConfigState.active`、`hasKey`、`saveKey:null`、64 KiB 的协议测试。

### UI

- 聊天窗删除整窗代码构建兜底、消息/折叠块/蓝图卡/模型下拉组的运行期兜底。
- 缺少 Prefab 时明确报错，不再生成第二套界面。
- 删除不可达的 `AutoFitTranscript` 和 `_fromPrefab` 死字段。
- 删除运行期 `FlipComboDropdownUp`；当前 prefab 的三个下拉框锚点已经是“向上展开”。
- 用 MCP 重新打开 prefab stage 核对：`content-column/input` 与 `output` 由 `content-column` 的布局组驱动，运行期 rect 分别为 `860x100`、`860x506`，工具栏四个右锚点未漂移。
- 重新保存 prefab、刷新 `mod/unity/Prefab/ChatWindow.prefab` 快照并重打 `icarusii-ui`（7965 KB）。
- `package.sh` 在缺少 `icarusii-ui` 时拒绝打包。
- `UiFactory` / `WindowPrefab` 注释改为“聊天窗必须走 Prefab”。

### UI 文本

- `Strings.cs` 新增 237 个 key（共 353 个，无重复）。
- `ModelSettingsTab`、`ToolNames`、`ChatWindow`、`AskPanel`、`Plugin`、`GameLinkCardInfo` 的玩家可见文本迁入 `Strings.Get`。
- `GameLinkCardInfo` 的固定数字格式改走 `Strings.GetInvariant`，不再在调用点用 `string.Format`。

### 边界与安全

- `confirmLocalWrite` 无 transport 时 fail closed。
- 写工具不再因 `internal=true` 跳过确认。
- `run_script` 在沙箱里固定 `Date.now()=0`、`Math.random()=0.5`，并增加断言。
- `blueprint_fs` 拒绝任何 `..`，不只是越出根目录的 `..`。
- `BlueprintActions.ResolvePath` 只接受蓝图根内的 `card.Path`。
- `game_copy_blueprint` 同名后缀超过 999 时返回错误，不再覆盖 `(49).txt`。
- KB 搜索结果不再对外暴露 `score`，排序键改为内部 `rank`。
- `⚠` 文本前缀改为 `[警告]`。

### 测试与文档

- sidecar 测试临时目录统一到 `sidecar/.test-tmp/`（已加入 `.gitignore`）。
- `pi-tools` 断言 pi 内置 `bash`/`read`/`write`/`edit` 未注册。
- `blueprint-plan` 显式传 `BlueprintWorkspace`，不再依赖已删除的环境变量。
- 新增 `docs/development.md`；README 移除开发进度、构建/测试说明与仓库结构。

## 验证

- `scripts/build-mod.sh`：0 warning / 0 error。
- `dotnet run --project tests/ProtocolTests`：598 项通过。
- `sidecar` `npm test`：全部通过。
- `verify.sh mod` / `verify.sh protocol` / `verify.sh sidecar` / `verify.sh e2e`：通过。
- `scripts/build-ui-bundle.sh`：成功，bundle 7965 KB，运行时 prefab 快照已刷新。
- `scripts/package.sh`：成功，`dist/pkg/IcarusII` 106 MB。
- `update-steam-mod.sh`：已部署到 Steam/Proton；4 个文件 SHA-256 与包内一致，旧版本备份在 `.backup-20261029-104700`，`config.json` 迁移自检 `ready` 且 key 保留。

## 未完成 / 软项

### C# 风格仍有软项

- Unity 对象的 `== null` 检查仍保留（`UnityEngine.Object` 重载了 `==`，不能机械改成 `is null`；已把 `Shared` 与不依赖 Unity 的文件里的 103 处改成 `is null`）。
- 仍有超过 20 条语句的方法（`Plugin.HandleMessage`、`GameReadTools.Build`、`ModelSettingsTab.Install` 等）。
- 部分注释仍有多行一句话。

这些没有在缺少 UI 验证的情况下大改，避免引入行为回归。

## 需要用户操作

1. 运行 `scripts/update-steam-mod.sh` 部署到 Steam/Proton，或把 `dist/pkg/IcarusII` 复制到测试环境。
2. 进游戏打开聊天窗，检查 1080p 与一个非 1080p 分辨率下的输入区/正文区/工具栏位置。
3. 如需提交，确认 `mod/unity/Prefab/ChatWindow.prefab` 的二进制快照变化可以接受（只涉及被布局组驱动的两个 `AnchorMax` 序列化值）。
