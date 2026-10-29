# AGENTS.md 合规审查报告

> 整改结果与剩余阻塞见 [AGENTS合规整改结果.md](AGENTS合规整改结果.md)。本文保留审查时的证据与行号。

- 审查日期：2026-10-29
- 审查基线：`master` / `5e78201`；审查开始时 `git status --short` 为空。
- 审查范围：`mod/src`、`sidecar/src`、`sidecar/test`、`tests`、`docs`、`scripts`、`mod/unity`、`README.md`、`AGENTS.md`。
- 审查方式：静态只读。三名只读子代理并行审查 sidecar 安全、UI/Prefab、协议/测试；关键结论由我复核。未运行 Unity/MCP、未构建、未部署、未修改源码。
- 说明：本报告只新增在 `docs/plans/`，没有改动任何业务代码或 Prefab。

## 0. 结论摘要

工程整体架构与安全主链路基本成立：`mod/src/Shared` 保持 Unity-free；sidecar 只注册自定义工具、关闭 pi 内置工具；`run_script` 使用 QuickJS-WASM；写操作有确认钩子 + mod 一次性令牌；脚本有三重预算与租约；API Key 只落 sidecar 配置并脱敏；未注册 Harmony 补丁；Unity API 统一回主线程。

但与 AGENTS.md 的硬约束仍有明显偏离，主要集中在五处：

1. **UI 仍存在大量运行期代码构建与运行期布局调整**，与「UI 和布局必须定义在 Prefab 中」直接冲突；其中设置页整页、聊天窗整窗兜底、思考/工具块兜底、普通消息行兜底、蓝图卡片兜底最明确。
2. **大量界面文本没有走 `I18n/Strings.cs`**，尤其 `ModelSettingsTab`、`ToolNames`、`Plugin`、`GameLinkCardInfo`。
3. **运行期配置仍由环境变量承载**，至少蓝图根目录与 Wiki 端点是模型可见配置。
4. **sidecar 的「只监听 127.0.0.1」没有强制**，`--host` 可被改成非回环地址，代码只告警。
5. **协议文档与实现存在多处不一致**，且测试把临时文件写到项目目录之外。

此外，当前工作区有两个 UI 流程阻塞项：

- `dsp-game/Temp/UnityLockfile` 存在，Unity 正在运行，`scripts/build-ui-bundle.sh` 会拒绝执行。
- `dsp-game/Assets/IcarusII/ChatWindow.prefab` 与 `mod/unity/Prefab/ChatWindow.prefab` 字节不一致（24 字节差异）；`dsp-game/Build/IcarusIIBundle/icarusii-ui` 的构建时间早于 Prefab 最后修改时间。按 AGENTS UI 工作流第 5-7 步，尚未闭环。

## 1. UI：运行期代码构建与调整（阻塞）

AGENTS.md 第 21-23 条要求 UI 和布局必须定义在 Prefab 中，不得通过代码在运行期构建和调整；动态内容例外也必须在 Prefab 中存在模板。当前实现把「Prefer Prefab」做成了主路径，但保留了一整套代码构建兜底，且设置页整页是代码构建。

### 1.1 设置页整页由代码构建

- `mod/src/IcarusII/UI/ModelSettingsTab.cs:491` `BuildPage`、`:551` `BuildProviderPane`、`:718` `BuildEditorPane`、`:786` `BuildCatalogPopup`、`:1261` `AddScrollList`、`:1324` `AddInput`、`:1380` `AddButton`。
- 证据：`:507` `AddComponent<VerticalLayoutGroup>()`；`:518` `AddText(head, "供应商与模型", ...)`；`:1367` `AddComponent<InputField>()`；`:1392` `AddComponent<Button>()`；`:1499` `new GameObject("revert-slot-hidden", ...)`。
- 该页的布局、尺寸、控件全部在 C# 中；Prefab 中没有对应页面，也没有供应商/模型/目录列表的行模板。
- 结论：确认违反 AGENTS「UI」第 1 条，且不属于动态列表项例外。

### 1.2 聊天窗整窗代码兜底

- `mod/src/IcarusII/UI/ChatWindow.cs:956-972`：Prefab 加载失败或结构不完整时调用 `BuildUiInCode`。
- `ChatWindow.cs:1091-1117`：`BuildUiInCode` 创建 Panel、Header、Transcript、InputBar；`:1446` `BuildHeader`、`:1463` `BuildTranscript`、`:1578` `BuildInputBar`。
- `mod/src/IcarusII/UI/WindowPrefab.cs:21-22` 明确写明“拿不到 bundle 时返回 null，调用方回退到代码构建的界面”。
- `scripts/package.sh:32-33` 明确把 UI bundle 当可选件：“拿不到时 mod 会回退到代码构建的界面”。
- 结论：确认违反 AGENTS「UI」第 1 条。即使正常安装走 Prefab，这个兜底路径仍会在 bundle 缺失时生成一套与设计稿不同的窗口，且让打包流程可以接受缺 UI bundle。

### 1.3 动态项/折叠块/卡片代码兜底

- 思考块/工具块：`ChatWindow.cs:3168-3176` 在 `block == null` 时调用 `CreateBlock`；`:3522` `CreateBlock` 内 `AddComponent<VerticalLayoutGroup>()`、`UiFactory.Node("accent", ...)`、`AddComponent<Image>()`。
- 普通消息行：`ChatWindow.cs:6570-6585` `BuildPlainItem` 直接 `UiFactory.Node("msg-...", ...)` + `AddComponent<Text>()`。
- 蓝图卡片：`ChatWindow.cs:2677-2855` `BuildBlueprintCard` 直接 `UiFactory.Node("blueprint-card-...")`、`AddComponent<Image>()`、`AddComponent<VerticalLayoutGroup>()`、`AddComponent<Button>()`。
- 模型下拉组：`ChatWindow.cs:4282-4397`，没有可克隆的 thinking-group 时 `BuildRuntimeModelGroup` 用 `new GameObject("model-group", ...)` 拼出整个组。
- 结论：确认违反 AGENTS「UI」第 1 条中的“列表项也必须在 Prefab 中存在模板”。Prefab 里虽然有 `example-user`/`example-assistant`/`blueprint-card`/`tool-template` 等模板，但这些兜底路径绕过了模板。

### 1.4 正常 Prefab 路径上仍在运行期改布局

- `ChatWindow.cs:4100-4112` `FlipComboDropdownUp`：即使 Prefab 已存在，仍把思考/权限/模型下拉框的 `anchorMin`、`anchorMax`、`pivot`、`anchoredPosition` 改成朝上展开。
- `ChatWindow.cs:4413-4422`：模型下拉框重复同样四行。
- `ChatWindow.cs:1232` `ApplyPanelPlacement(..., keepAuthoredSize: true)`、`:1410-1443`：仍按配置计算面板锚点/位置/尺寸；`keepAuthoredSize` 只保住尺寸。
- `ChatWindow.cs:1066-1080` `ApplyHostScaleAndLayer`：每个分辨率变化周期改 `_panel.localScale`。
- 结论：`FlipComboDropdownUp` 与 `BuildRuntimeModelGroup` 属于明确的运行期布局调整，确认违反第 1 条。面板尺寸/缩放属于玩家配置与分辨率适配，是否算“调整 UI”需人工定性；按条款字面也不合规。

### 1.5 原生组件复用不足

- `ModelSettingsTab.cs:1324-1377` 自建 `InputField`；`:1380-1414` 自建 `Button` + `ColorTint`；`:1310-1321` 用 8x8 `Image` 画状态点；`:817`/`:1726` 用文本 `×` 当关闭/删除图标；`:585` 用 `+` 当添加入口。
- 结论：设置页没有复用原生 `UIButton`/`UIInputField` 的悬停、音效、tooltip 与输入行为；「一般 UI 动效必须复用游戏原生组件」的要求未满足。`ChatWindow` 主路径大量使用原生 `UIComboBox`/`UIButton`/`UIWindowDrag`/图标选择器/取色器，这部分是合规的，问题集中在设置页。

### 1.6 `UiFactory` 的设计前提已与 AGENTS 冲突

- `mod/src/IcarusII/UI/UiFactory.cs:6-8` 注释：“游戏界面是用代码+预制体搭的，我们只能用代码搭”。
- 该工具被 `ModelSettingsTab` 与聊天窗兜底路径大量调用。
- 结论：这是持续产生“代码构建 UI”的源头，注释本身也需要随规则重写。

## 2. UI 文本未走 I18n（高）

AGENTS.md 第 43 条要求新界面文本走 `I18n/Strings.cs`。`Strings.cs` 已有 116 个键，`ChatWindow` 有 74 处 `Strings.Get`，但以下位置绕过：

- `mod/src/IcarusII/UI/ModelSettingsTab.cs`：`Strings.Get` 调用数为 0；`TabLabel = "模型"`（`:26`）、`"自定义…"`（`:38`）、`"供应商与模型"`（`:518`）、`"供应商"`（`:571`）、`"预设供应商"`（`:576`）、`"供应商 ID"`（`:583`）、`"API 密钥"`（`:742`）、`"获取可用模型"`（`:772`）、`"还没有供应商"`（`:1633`）、`"当前"`（`:1722`）、`" 个"`（`:1730`）等约 95 处非日志中文字面量。
- `mod/src/IcarusII/UI/ToolNames.cs:38-90`：工具显示名全部硬编码中文，`Strings.Get` 调用数为 0。
- `mod/src/IcarusII/UI/ChatWindow.cs`：`"游戏时间 " + GameClock() + " · 点击复制"`（`:4865-4866`）、`ConversationInfo.Create("legacy", "当前会话")`（`:5358`）、`"新会话"`（`:5381`）、`RecordCount + " 条"`（`:5632`）、`"展开其余 ... 条记录"`（`:5698`）、`"会话标题"`（`:9024`）。
- `mod/src/IcarusII/UI/AskPanel.cs:405-406`：`"请确认你的回答"` / `"确认"`。
- `mod/src/IcarusII/Plugin.cs`：`:399`、`:430`、`:509`、`:520`、`:533`、`:686`、`:743`、`:1006`、`:1017`、`:1187`、`:1201`、`:1208`、`:1224`、`:1232`、`:1298`、`:1306` 等直接进入状态栏/系统消息。
- `mod/src/IcarusII/Game/Tools/GameLinkCardInfo.cs`：卡片描述大量中文，例 `:212` `"未供电"`、`:265-267` `"生产速度: ..."`、`:321-323` `"输入端失效"`、`:345-346` `"输入优先"`、`:443-447`、`:482`、`:515`、`:527`、`:536`、`:573`。这些经 `TetherTools.cs:203/288` 写入 `Target.Description`，再由 `ChatWindow.cs:2400` 显示。
- 结论：确认违反第 43 条。`ChatWindow`/`AskPanel` 主流程已本地化，说明基础设施可用，问题是新增/旧有文本没有迁移。

## 3. README 仍是开发手册并写有开发进度（高）

AGENTS.md 第 16 条要求 README 是用户说明，不是开发者手册，不准写开发进度。

- `README.md:11-43`：`状态：M4 已完成并通过实机验收`、M0/M1/M2/M2.5/M2.6/M2.7/M4/M3 逐项完成情况。
- `README.md:60/71/92/101/112`：`AI 能读什么（M2）`、`AI 能看什么（M4a）`、`AI 能查什么（M3）`、`AI 能查/生成什么（M4d–M4j）`、`AI 能做什么写操作（M4/M5）`。
- `README.md:392-419`：`从源码构建`、`运行测试`、`dotnet run --project tests/ProtocolTests`、`node sidecar/dist/index.js serve ...`。
- `README.md:423` 起：`仓库结构` 开发者说明。
- `docs/current-status.md:1-3` 明确写着“本页是当前唯一的状态/计划入口”，与 README 的重复状态冲突。
- `scripts/package.sh:47-50` 注释也承认“仓库根 README 是开发向的，不适合直接塞给玩家”，说明问题已被感知但 README 未改。
- 结论：确认违反第 16 条。建议把状态/M 里程碑/构建测试/仓库结构移到 `docs/current-status.md` 或独立开发文档，README 只保留安装、使用、配置、排障。

## 4. 运行期配置由环境变量承载（高）

AGENTS.md 通用工作方式第 12 条要求玩家/模型可见的运行期配置放 sidecar 的 `config.json`，不得用环境变量承载（构建脚本的路径/版本/跳过开关除外）。

确认违规：

- `sidecar/src/blueprint/workspace.ts:74` `process.env['ICARUS_BLUEPRINT_DIR']`；`sidecar/src/blueprint/tools.ts:238` 同一逻辑第二份拷贝；`blueprint/tools.ts:410` 把 `ws.root` 返回给模型；`README.md:372-374` 指导玩家用该环境变量指向 Proton prefix。
- `sidecar/src/wiki/client.ts:99-100` `ICARUS_WIKI_ZH_API` / `ICARUS_WIKI_EN_API`；该 base 直接决定 `wiki_search`/`wiki_page` 看到的链接与内容（`wiki/client.ts:181/220/261`）。

需判断：

- `sidecar/src/log.ts:28` `ICARUS_II_LOG_LEVEL`：运行期配置，但不是玩家/模型可见；仍建议并入 `config.json` 或只保留 CLI。
- `USERPROFILE`/`homedir` 是 OS 环境，不属于本条款。
- 测试脚本里的环境变量是测试输入，不属于运行期配置。

结论：蓝图根目录与 Wiki 端点确认违规；日志级别建议一并收口。

## 5. sidecar 未强制只监听 127.0.0.1（高）

AGENTS.md 第 54 条要求 sidecar 只监听 `127.0.0.1`，不要改成 `0.0.0.0`。

- `sidecar/src/index.ts:41` 默认 `127.0.0.1`；`:440` `const host = values['host'] ?? DEFAULT_HOST;` 接受任意 `--host`。
- `sidecar/src/server.ts:89-93` 对非回环地址只 `logger.warn`，不拒绝；`:183` `httpServer.listen(options.port, options.host)` 是唯一监听点。
- `mod/src/IcarusII/Config/PluginConfig.cs:152` 暴露 `Sidecar Host`，默认 127.0.0.1；`mod/src/IcarusII/Core/SidecarLauncher.cs:121/135` 把该值原样传给 `--host`。
- `/health` 在 `server.ts:99-113` 不需要 token；一旦监听 0.0.0.0，会额外暴露健康检查。
- 结论：默认合规，但能力上允许改到非回环，且只告警不拒绝。按“只监听 127.0.0.1”的字面要求，确认不合规。应在 sidecar 启动时拒绝非回环 host，或移除 `--host`/mod 的 Host 配置。

## 6. 协议文档与实现不一致（高）

AGENTS.md 第 12-18 条要求协议变更先改 `docs/protocol-v1.md`，再同步 `Protocol.cs`、`protocol.ts` 与测试；不得改变 v1 既有字段语义；新增能力优先用可选字段。

### 6.1 `permissionLevel` 语义在 v1 内被反转

- 旧文档（`de5d628^`）`docs/protocol-v1.md` 约 320-327 行：`readonly` = sidecar 不注册写工具，写调用返回 `permission_denied`。
- 旧代码 `de5d628^:mod/src/Shared/Protocol/Protocol.cs` 245-255 行：`IsReadonlyBlockedTool`。
- 新文档 `docs/protocol-v1.md:325-337`：`permissionLevel` “只决定写操作是否需要玩家确认，不改变工具是否注册”。
- 新代码：`Protocol.cs:310-316` 改为 `AutoApprovesWrite`；`sidecar/src/protocol.ts:104-140` 已无 `permission_denied`；`docs/protocol-v1.md:236-252` 错误码表已删除它；`sidecar/test/tools.mjs:180-192` 还断言只读仍注册写工具。
- 发布面核验：`b856a60`（引入）与 `de5d628`（反转）都不是 `v0.3.0` 的祖先；仓库只有 `v0.3.0` 一个 tag。因此旧语义没有随发布版外泄，当前不构成对已发布客户端的破坏。
- 结论：按 AGENTS「不得改变 v1 既有字段语义」的字面，仍确认违规；但兼容性影响有限。建议在 `docs/current-status.md`/CHANGELOG 明确记录这次“未发布语义修正”，并决定是否保留 `permission_denied` 作为兼容错误码。

### 6.2 `config_required` 未写入协议文档

- 代码/测试：`Protocol.cs:122`、`sidecar/src/protocol.ts:116`、`sidecar/src/agent.ts:2316-2318`、`tests/ProtocolTests/Program.cs:301`、`sidecar/test/config-protocol.mjs:67-71`。
- 文档：`docs/protocol-v1.md:236-252` 错误码表没有；`:540-546` A.3 也没有；全文无 `config_required`。
- 结论：确认违反“先文档再实现”。`agent.ts` 的注释还引用了不存在的 A.3 条目。

### 6.3 `config.set` 的 `baseUrl: null` 与 C# 构造器不一致

- 文档 `docs/protocol-v1.md:306-318` 示例明确 `baseUrl: null`；`sidecar/src/protocol.ts:1238-1242` 把 null 解释为“清除”。
- `Protocol.cs:463-473` 只在非空时写 `baseUrl`；`SidecarClient.cs:162-180` 的参数是 `string baseUrl`，无法区分 null 与“未给出”。
- 当前设置页走 `providerConfig` 规避，但协议构造器本身不能表达文档语义。
- 结论：确认文档/实现不一致。

### 6.4 `tool_result.error.retryable` 可选性冲突

- 文档 `docs/protocol-v1.md:1547`：`error` 为 `{code, message}`。
- C# `Protocol.cs:591-596` 只写 code/message。
- TS `sidecar/src/protocol.ts:227-232` 把 `retryable` 定义为必填；解析时 `:1528-1539` 缺省填 false。
- 结论：确认三方类型不一致；按 TS 类型写代码会产出文档外字段。

### 6.5 其它疑似协议/文档问题

- `ConfigState` 未解析文档里的 `active`：文档 `:398-399/415-416`，sidecar `protocol.ts:1124/1477/1494`，C# `Protocol.cs:1422-1449`。当前 sidecar 同时发 `provider/model`，暂不显现；只发 `active` 时会显示不出模型。
- `hello.saveKey` 文档写 `string|null`（`:49-56`），C# 在 null 时省略（`Protocol.cs:443-445`），测试还断言省略（`Program.cs:280`）；语义等价但可选性表述不一致。
- 旧服务端 fallback 把 `hasKey` 赋成 `IsReady`（`Protocol.cs:1500-1505`，尤其 1502），`invalid_key`/`unreachable` 时可能误报“未配置”。
- `tool_debug.args` 文档写 ≤64 KiB（`:173`），sidecar 用 `JSON.stringify(...).length` 计 UTF-16 字符（`protocol.ts:267/275-279`），C# 不限制（`Protocol.cs:524-531`）；多字节中文可能超字节上限。
- `docs/protocol-v1.md:1262-1263` 仍称无 game 能力时只含 `kb_*`，实际 `registry.ts:579-587` 还返回 wiki/blueprint/run_script；`tools.mjs:177` 断言 12 个本地工具。
- `design.md:1281-1295` 的 `config.json` 字段仍是 host/port/dataDir/modelsPath/authPath/provider/defaultModel/sessionsDir/kb/wiki；实际落盘只有 providers/active/thinkingLevel/permissionLevel（`config.ts:680-685`）。
- 结论：这些是文档陈旧或兼容分支问题，建议按协议文档逐项校正。

## 7. 测试与验证（高）

### 7.1 测试把临时文件写到项目外（确认违规）

AGENTS.md 第 8 条：“编写测试代码或者任何可运行的文件时，绝对禁止将文件输出到项目文件夹以外的路径。”

- `sidecar/test/helpers.mjs:48` 默认 `mkdtempSync(join(tmpdir(), 'icarus-ii-test-'))`。
- 同模式：`config.mjs:27`、`blueprint-fs.mjs:23`、`blueprint-plan.mjs:28`、`conversations.mjs:202`、`negative.mjs:34`、`pi-loopback.mjs:336`、`pi-tools.mjs:207-208`。
- 即使 `finally` 清理，运行期仍向 `/tmp` 写入配置、会话、蓝图等数据；所有用 `startSidecar` 的测试都会继承。
- 结论：确认违反第 8 条。

### 7.2 覆盖缺口（需人工判断）

- 工具 schema 只抽查部分：`sidecar/test/tools.mjs:127-178` 查若干工具存在性/能力位/个别字段；`tool-docs.mjs:23-34` 只校验生成文档是否最新，不校验 required/type/边界；mod 侧 `GameToolDispatcher` 参数校验没有离线测试。
- 配置迁移只覆盖基础旧格式：`config.mjs:162-170`；缺新 `providers` + 旧 `provider/model` 的 active 覆盖分支（`config.ts:377-384`）、迁移后写回格式、非法 providers/active 回退、旧配置缺 `input` 的兜底链路。
- 传输层边界：`negative.mjs:112-202` 覆盖 token/version/未知 type/非 JSON，但没有超大帧（8 MiB）与 `HELLO_TIMEOUT_MS`。
- 脚本沙箱测试断言了 `process`/`require`/`fetch`/`globalThis.process` 为 undefined（`script.mjs:196-208`），但未断言 `WebSocket`；也未断言 `noTools:'builtin'` 生效与三重预算默认/上限。
- 会话测试的 fake agent 复制了真实 PiAgent 的 trim/`invalid_title`/选择下一个 active 规则（`conversations.mjs:64-107`），存在“测试通过但真实规则变化”的风险；Part 2 也测真实 PiAgent，故列为疑似。
- `tests/ProtocolTests/Program.cs:319-321` 的 ready 夹具把 `agent` 写成字符串，而文档 `:536-538` 明确这是曾导致模型名显示不出的错误形态；该测试只解析 `config`，没有断言 `agent` 必须是对象。
- 结论：实现大体合规，但第 18 条“工具 schema、配置迁移必须有离线测试”只部分满足。

## 8. C# 编码约定（中）

### 8.1 方法过长（确认偏离）

AGENTS.md 第 24 条建议一个方法不超过 20 条语句。按分号粗略统计，`mod/src` 中约 166 个方法超过 20 条语句，46 个超过 40 条。代表：

- `GameEntityTools.cs:333` `AppendCategoryDetail`（约 145 条）
- `ChatWindow.cs:2676` `BuildBlueprintCard`（约 130 条）
- `Plugin.cs:389` `HandleMessage`（约 119 条）
- `GameEntityTools.cs:1700` `Build`（约 101 条）
- `GameReadTools.cs:1055` `Build`（约 96 条）
- `ChatWindow.cs:3522` `CreateBlock`（约 92 条）
- `ModelSettingsTab.cs:208` `Install`（约 91 条）

第 24 条是“最好”而非绝对，但这些方法已经明显影响可读性与修改安全性。

### 8.2 嵌套 try/catch（确认违规）

AGENTS.md 第 32 条要求避免嵌套 try/catch。

- `GameEntityTools.cs:1157` 外层 try，`:1164` 内层 try。
- `TetherTools.cs:129` 外层 try，`:134` 内层 try。

### 8.3 自定义 ref 参数（确认违规）

AGENTS.md 第 33 条禁止 ref/out，例外只有依赖库/游戏 API 与 Try* 模式。

- `GameTextStyle.cs:149` `Soften(Material source, ref Material cache, ...)`。
- `ChatWindow.cs:6364` `BindUserFooter(ref Item item, ...)`。
- `ChatWindow.cs:8510` `FormatInline(..., ref bool hasGameLinks, ...)`；`:8969`/`:8985` `AppendTableRow(..., ref bool hasGameLinks)`。
- `JsonValue.cs:346` 起解析器大量 `ParseValue(string s, ref int i, int depth)`、`ParseObject(... ref int i ...)` 等。
- `TakeItemFromPlayer(ref ...)` 是游戏 API，属允许例外；`TryGetValue`/`TryParse`/`Array.Resize` 也属允许例外。
- 结论：自定义 ref 参数确认违反第 33 条。

### 8.4 `string.Format` 代替插值（确认违规）

AGENTS.md 第 25 条要求用插值字符串代替字符串拼接，并把 `string.Format` 列为反例。

- `GameLinkCardInfo.cs:447`、`:515`、`:527`、`:536`、`:563`、`:573` 等大量 `string.Format(CultureInfo.InvariantCulture, "功率: {0}...", ...)`；这些同时是硬编码中文。
- `UiStyle.cs:566` 也有 `string.Format`。
- `Strings.cs:160` 是运行时格式模板（`Get(key, args)`），属于合理用法，不建议改。
- 结论：卡片文本处确认违反第 25 条。

### 8.5 旧语法（部分确认）

- `.HasValue`：`GameRichText.cs:419`、`:1642`、`Protocol.cs:495`；按第 36 条应改为 `is not null` 或模式匹配。
- `== null`：全仓约 1960 处。需要区分：Unity 的 `UnityEngine.Object` 重载了 `==`，用 `== null` 判断“已销毁对象”是必要语义，不能一律改 `is null`；非 Unity 类型（如 `ConfigState`、请求对象、字符串）才适合改。建议按类型分批处理，不要把 Unity 对象误改。
- 未发现 `Nullable<>`、`new ValueTuple`、`#region`、`System.Linq`、DI 容器；这部分合规。

### 8.6 魔数（确认偏离）

AGENTS.md 第 31 条要求字面量用常量封装（日志/追踪除外）。

- `ChatWindow.cs:1032/1047` `canvas.sortingOrder = 30000;` 直接写字面量。
- `ModelSettingsTab.cs:517` `preferredHeight = 32f`、`:538` `24f`、`:562` `new RectOffset(8, 8, 8, 8)`、`:794` 起 `new Vector2(640f, 480f)` 等。
- `ChatWindow`/`ModelSettingsTab` 的颜色多数已提成 `static readonly Color`，这部分合规；内联尺寸/偏移/排序值仍需收口。

### 8.7 `Task.Run` 用于 I/O（疑似）

AGENTS.md 第 34 条要求 async/await 用于 I/O，`Task.Run` 用于 CPU。

- `Plugin.cs:186` `Task.Run(PrepareSidecar)`：`PrepareSidecar` 里是进程启动、runtime.json 读取等 I/O。
- `SidecarClient.cs:117/463-465` 用 `Task.Run` 跑 WebSocket 收发/ping 循环。
- 这些是长驻阻塞循环，改成全异步成本较高；列为疑似，至少应在注释里说明为何不用 async。

### 8.8 注释规范（确认偏离）

AGENTS.md 第 18 条要求一句话一行，禁止把一句话强行拆行；第 19 条禁止在注释里写被否定的决策（除非对应代码真实出现过 bug，并写明现象与解决方式）。

- 按启发式统计，`mod/src` 中约 747 行注释处于“上一行未以句末标点结束、下一行继续同一句”的状态；例如 `ChatWindow.cs:19-21` 的类注释把“界面在首次进入游戏时一次性构建，之后只用 SetActive 开关，不销毁重建”拆成多行。
- 约 106 条注释包含“刻意不/不提供/绝不/禁止/不要/为什么不用”等否定决策。非 bug 背书的代表：
  - `Plugin.cs:26` “刻意不注册任何 Harmony 补丁”。
  - `SidecarClient.cs:56` “本类刻意不依赖 UnityEngine”。
  - `ModelSettingsTab.cs` 关于“模型页不提供恢复本页设置”的说明。
  - `sidecar/src/tools/script.ts:14` “为什么不用 node:vm”。
  - `scripts/build-ui-bundle.sh` “默认只打包，绝不重建 prefab”。
  - `ChatWindow.cs:1613` “那套按捕获/启发式重绘窗口的做法已弃用”。
- 其中不少注释是“实测踩过”的 bug 记录（如 `ChatWindow.cs:1302`、`:8140`），这类是允许的；问题在于无法区分“经验约束”与“被否定的方案”。建议保留有现象/解决方式的，其余删除或改成正面描述。

### 8.9 注释与代码不一致（确认偏离）

- `sidecar/src/tools/script.ts:19` 注释写“调用次数上限 50”，实际 `:40-41` 是默认 100/最大 10000。
- `sidecar/src/config.ts:137` 注释称 `thinkingLevel` 默认 `off`，实际 `protocol.ts:1055` 与文档 `docs/protocol-v1.md:323` 都是 `high`；`config.mjs:89-92` 注释也写“默认 off”却断言 high。
- 结论：按第 20 条应以代码为准，但这些注释会误导后续维护，建议修正。

## 9. 死代码、兜底与边界行为（中）

### 9.1 `AutoFitTranscript` 是不可达死路径（确认）

- `ChatWindow.cs:1465` `CreateTranscript(_panel, font, autoFit: false);`
- `:1475` `CreateTranscript(..., bool autoFit)`；`:1481` 只有 `if (autoFit)` 才调用 `AutoFitTranscript`；`:1533` 是定义。
- 全文件没有 `autoFit: true` 的调用。按第 22 条，确认废弃代码应删除。

### 9.2 兜底路径与「兜底」注释（确认）

- `BuildUiInCode`、`WindowPrefab` 回退、`package.sh` 的“缺 bundle 只警告”、思考/工具块/普通消息/蓝图卡片的代码兜底，都是“保证少一个资源文件也能用”的兜底。
- AGENTS 第 22 条明确不要“保留工作”或“兜底”，确认废弃代码应删除。这里需要先决定：是把 Prefab/bundle 变成硬依赖，还是保留兜底但把它移出生产路径并加显式测试；不能继续让两条 UI 实现并行维护。

### 9.3 蓝图路径与覆盖边界（需人工判断）

- `BlueprintActions.ResolvePath`（`mod/src/IcarusII/Game/BlueprintActions.cs:29-52`）：优先用 `relPath` 拼到 `GameConfig.blueprintFolder`；但 `fileName` 不在游戏目录时，`:44` 会直接返回 `card.Path`（来自 sidecar），`:52` 也直接返回 `card.Path`；`TryLoad`（`:156`）随后 `BlueprintData.CreateFromFile(path)` 读取。
- 这意味着 `card.Path` 没有被校验必须位于蓝图根内；虽然卡片来自本地 sidecar，但按“不向模型暴露任意文件系统”的边界，建议 fail closed，只接受根内路径。
- `game_copy_blueprint` 默认不覆盖：`GameEntityTools.cs:2228-2233` 用 `for (i=2; i<50 && exists; i++)` 找 `(2)..(49)`；如果 49 个同名文件都存在，循环结束后会覆盖 `(49).txt`。这是边界情况，建议改成失败或继续递增。
- `host.ts:117-121` `confirmLocalWrite` 在没有 transport 能力时 `return true`；当前 `Session` 已实现（`:1781-1791`），但方向与“不得绕过”相反，建议 fail closed。
- `session.ts:1429` `if (!token && risk > 0 && options.internal !== true)`：`internal` 调用对 risk>0 无条件跳过确认；当前 internal 调用方是只读/调试白名单，但这是结构性绕过点。
- `session.ts:1499-1505` 脚本级确认卡只列写工具名与次数，不列参数；玩家看不到会改哪台机器。机制上不违反“弹一次确认”，但粒度偏粗。
- `session.ts:1529-1536` 断线/非 ready 时 `releaseScript` 提前 return，租约靠 mod 5 分钟 TTL 过期；正常路径 `script.ts:457-459` 会释放。与“脚本结束立即释放”有边界偏差。
- `script.ts:398` 只用提示词要求不要用 `Date.now`/`Math.random`；沙箱只注入 `dsp`/`console`，没有冻结 `Date`/`Math.random`，测试也没有断言。若要求硬性可重放，应在 QuickJS 里替换/冻结并加测试。
- `blueprint/workspace.ts:144-150` 允许根内 `..`（越出根才拒绝），而 `blueprint/tools.ts:368` 的提示词写“路径不能含 ..”；不构成逃逸，但与条款字面不一致。

### 9.4 打分机制（需人工判断）

AGENTS 第 21 条禁止打分/置信度机制（除非用户要求）。`sidecar/src/kb/knowledge.ts:341-367` 用 `score` 做搜索排序，`KbSearchHit` 也把 `score` 返回（`kb/types.ts:190`）。这是确定性的词法相关度排序，不是 LLM 置信度，但与条款字面冲突。建议明确：如果保留，应在文档里说明它只用于排序、不进入模型上下文；否则改为不暴露 score 的稳定排序。

## 10. 当前工作区阻塞项（阻塞）

### 10.1 Unity 正在运行

- `dsp-game/Temp/UnityLockfile` 存在（mtime 2026-10-29 00:31）。
- `scripts/build-ui-bundle.sh`、`.dsh/skills/rebuild-ui-bundle/scripts/rebuild-ui-bundle.sh` 都会因该文件拒绝执行。
- 影响：本轮无法重打 UI bundle，也无法做构建前后 dump 对比；按 AGENTS UI 工作流第 4 步，应先关闭 Unity。

### 10.2 Prefab 与快照字节不一致

- `dsp-game/Assets/IcarusII/ChatWindow.prefab`：372320 字节，SHA-256 `f5bfc9fd...`，mtime 2026-10-29 01:42。
- `mod/unity/Prefab/ChatWindow.prefab`：372320 字节，SHA-256 `83e1711b...`，mtime 2026-10-28 21:50。
- `cmp -l` 显示 24 个字节、12 处不同；`button-agent.prefab` 两侧一致。
- 当前 `.scratch/chatwindow-dump.txt`（2026-10-28 21:36）显示 `send-button=-20`、`permission-group=-100`、`thinking-group=-220`、`model-group=-340`，与 AGENTS 期望一致；因此“布局是否漂移”暂时没有证据，但快照/工作副本已不是同一份字节。
- 结论：按 AGENTS UI 工作流第 7 步，必须用 `IcarusIIDumpPrefab.DumpChatWindow` 对当前工作副本再做一次 dump 并对比；在 Unity 未关闭前无法完成。

### 10.3 UI bundle 早于 Prefab 修改

- `dsp-game/Build/IcarusIIBundle/icarusii-ui`：2026-10-28 21:36，8156934 字节，MD5 `d6de36d3ab739acc80ffce82efc516de`。
- `dist/pkg/IcarusII/BepInEx/plugins/IcarusII/ui/icarusii-ui`：2026-10-29 01:19，字节/MD5 与上者相同。
- Prefab 最后修改：2026-10-29 01:42，晚于 bundle 构建。
- 结论：至少从时间上看，dist 里的 bundle 不包含最新 Prefab 修改；按 AGENTS UI 工作流第 6-7 步，应重打 bundle、重新打包并部署。仅凭时间不能证明内容一定变了，但当前状态不能视为已闭环。

## 11. 确认合规

以下项目经核对未发现违反 AGENTS.md，列出以说明审查覆盖：

1. **Shared Unity-free**：`mod/src/Shared` 全目录无 `UnityEngine`/`UnityEditor` 引用；`tests/ProtocolTests/ProtocolTests.csproj:20-24` 把 Shared 源码编进无 Unity 引用的测试工程，构成编译门禁。
2. **未注册 Harmony 补丁**：`Plugin.cs:26` 只是注释；源码无 `Harmony.Patch`/`HarmonyPatch` 注册。csproj 引用了 `0Harmony.dll` 但未使用，可作为依赖清理项，不是违规。
3. **sidecar 只注册自定义工具**：`agent.ts:3283-3286` `noTools:'builtin'` + `customTools`；工具全集仅 `host.tools()` + `ask_user` + `kb_*` + `wiki_*` + `blueprint_*` + `run_script`。`test/script.mjs:196-208` 断言 `process`/`require`/`fetch`/`globalThis.process` 不存在。
4. **run_script 仅 QuickJS-WASM**：`script.ts:30-32/62-67`；注入面只有 `dsp`/`console`；全 `src` 无 `child_process`/`node:vm`/`new Function`；唯一 `evalCode` 在 QuickJS 内。
5. **脚本默认只读 + 受控写链路**：无租约时写调用被拒（`script.ts:270-278`）；字面量扫描 + 动态拼名拒绝（`:104-159`/`:279-285`）；脚本哈希、租约 `maxWrites=10000`、TTL 300s、写工具名白名单、`finally` 释放；mod `ConfirmationService.cs:154-160` 校验。
6. **三重预算**：`script.ts:37-43` 8000/30000/120000/100/10000/6000/40；schema 与 clamp 对应；`shouldInterruptAfterDeadline` 中断 CPU；`clip` 截断返回。
7. **写操作确认 + 一次性令牌**：sidecar `session.ts:1429-1434/1470-1476/1553-1557/1582`；mod `ConfirmationService.cs:76-190` 校验 token/callId/tool/argsHash/TTL，一次性删除；脚本租约额外绑脚本哈希与写工具名。
8. **蓝图路径安全**：`workspace.ts:134-165` 拒 NUL/盘符/绝对路径/越界，`assertNoSymlink` 逐段禁符号链接；`write` 必须通过 `inspectBlueprintText` 且强制 `.txt`；`rm`/`rmdir` 必须 `confirm=true`；默认不覆盖。
9. **API Key 安全**：只落 `config.json`（0600/目录 0700）；`ConfigView`/日志/错误帧不含明文；日志只打 `maskSecret` 前缀；上游请求用 header；测试用假 key；未发现提交的密钥。
10. **主线程约束**：`Plugin.OnClientMessage`/`OnClientStateChanged` 用 `Post` 回主线程；`MainThreadDispatcher` 用 `ConcurrentQueue`；`ModelSettingsTab.Bind` 只写静态字段与脏标记，`Watch` 在主线程刷新。
11. **无 LINQ/DI/#region**：`mod/src` 无 `using System.Linq`，无 DI 容器，无 `#region`；UI 目录未发现 `.Where/.Select/.Any` 等 LINQ。
12. **Unity DLL 不复制**：`IcarusII.csproj` 所有 Unity/BepInEx/Assembly-CSharp 引用均 `Private=false`；没有独立 Shared.csproj，但测试工程已能阻止 Unity 依赖进入 Shared。
13. **UI Prefab 主路径与动态模板**：正常安装时 `WindowPrefab.Instantiate` + `TryWireFromPrefab`；`example-user/example-assistant/example-reply/tool-template/blueprint-card/conversation-group-template/record-row-template/tool-row-template` 等模板来自 Prefab；`ChatWindow` 74 处 `Strings.Get`，主流程基本本地化。
14. **原生组件/图标/材质复用**：`ChatWindow` 使用原生 `UIComboBox`、`UIButton`、`UIWindowDrag`、`UISignalTagPicker`、`UISimpleColorPicker`；`AskPanel` 使用原生 `Toggle`/`UIToggle`；蓝图图标来自 `LDB`，外框/输入框贴图与文字材质来自游戏资源。
15. **GUI 测试未使用 mock LLM**：仓库没有 GUI/浏览器自动化测试；`pi-loopback.mjs`/`pi-tools.mjs` 是无 GUI 的 headless 假上游；真实 LLM 用例在 `live.mjs` 且默认跳过。未触发第 47 条禁止项。
16. **构建脚本 GUI 安全**：`build-ui-bundle.sh` 与 `rebuild-ui-bundle.sh` 用 `-batchmode`（后者 `-nographics`），不抢前台焦点、不注入键鼠。

## 12. 建议处理顺序

1. **先处理阻塞项**：关闭 Unity；用 `IcarusIIDumpPrefab.DumpChatWindow` 对比当前 Prefab 与快照锚点；重打 UI bundle；确认 `mod/unity/Prefab/` 与工作副本一致；重新打包并部署。
2. **UI 规则收口**：把 `ModelSettingsTab` 页面迁到 Prefab（含供应商/模型/目录行模板）；删除 `BuildUiInCode`、`BuildPlainItem`、`BuildBlueprintCard`、`CreateBlock` 等代码兜底；把 `FlipComboDropdownUp`/`BuildRuntimeModelGroup` 的布局写回 Prefab；再决定 `ApplyPanelPlacement`/`ApplyHostScaleAndLayer`/`ApplyStyle` 是否属于允许的适配例外。
3. **配置与安全**：把 `ICARUS_BLUEPRINT_DIR`、`ICARUS_WIKI_*_API` 收进 `config.json`；sidecar 启动时拒绝非回环 `--host`（或删除该开关）；`confirmLocalWrite` 无 transport 时 fail closed。
4. **协议文档**：补 `config_required`；修正 `baseUrl:null` 与 `retryable`；决定 `permissionLevel` 旧语义/`permission_denied` 的兼容策略并记录；校正 `ConfigState.active`、`tool_debug` 单位、B.1 工具列表、`design.md` 的 config.json 字段。
5. **测试**：把临时目录改到项目内（如 `.scratch/test/` 或测试专用子目录）并清理；补工具 schema required/type/边界、配置迁移分支、`frame_too_large`/hello 超时、`noTools:'builtin'`、预算默认/上限、`WebSocket`/`Date`/`Math.random` 断言。
6. **文本与 C# 风格**：把 `ModelSettingsTab`/`ToolNames`/`Plugin`/`GameLinkCardInfo`/`AskPanel` 的界面文本迁到 `Strings`；拆分超长方法；消除嵌套 try/catch 与自定义 ref；`string.Format` 改插值；魔数提常量；修正注释的拆行与否定决策；删除 `AutoFitTranscript`。
7. **边界加固**：`BlueprintActions.ResolvePath` 只接受蓝图根内路径；`game_copy_blueprint` 在 49 个同名文件后不要覆盖；脚本确认卡补充参数摘要；决定脚本 `Date.now`/`Math.random` 是否要沙箱层禁止；明确 KB `score` 是否属于第 21 条禁止的打分机制。

## 13. 证据说明与限制

- 本轮是静态只读审查，未运行 Unity/MCP、未构建、未部署；因此 Prefab 布局漂移、bundle 是否包含最新 Prefab 只能给出字节/时间证据，最终需按 AGENTS UI 工作流第 5 步补 dump。
- `.scratch/chatwindow-dump.txt` 的锚点与 AGENTS 期望一致（`-20/-100/-220/-340`），但它是 2026-10-28 21:36 的旧 dump，不能替代对当前 01:42 Prefab 的复核。
- 三名子代理分别审查了 sidecar 安全、UI/Prefab、协议/测试；关键结论已由我抽查源码与 `git show` 复核。`permissionLevel` 的发布面结论基于 `git merge-base --is-ancestor b856a60 v0.3.0` 与 `de5d628 v0.3.0` 均为 NO，仓库只有 `v0.3.0` 一个 tag。
- 本报告只新增 `docs/plans/AGENTS合规审查报告.md`；没有修改源码、Prefab、构建产物或运行期数据。
