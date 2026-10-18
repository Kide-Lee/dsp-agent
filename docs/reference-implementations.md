# 可参考的 pi 插件与游戏 Mod

> 调研日期：2026-10-18。GitHub 星数只是成熟度信号，不代表代码质量；引用前请先读源码与测试。
> 本文件只记录"值得看什么、借什么、不要照搬什么"，不改变 `design.md` 的既定契约。

---

## 1. pi 官方示例（版本匹配 0.82.1，优先读）

都在本地依赖里，路径以 `sidecar/node_modules/@earendil-works/pi-coding-agent/` 为根：

| 示例 | 位置 | 可借鉴到 |
| --- | --- | --- |
| `permission-gate.ts` | `examples/extensions/` | `pi.on("tool_call")` 阻塞 + `ctx.ui.confirm`；无 UI 时默认拒绝。M4 写工具门禁的官方范式 |
| `dynamic-tools.ts` | `examples/extensions/` | `session_start` 时注册、运行时追加；`registeredToolNames` 幂等；`promptSnippet` / `promptGuidelines`。M2 按能力注册工具 |
| `truncated-tool.ts` | `examples/extensions/` | `truncateHead/Tail`、`DEFAULT_MAX_BYTES/LINES`、把截断上限写进工具描述。M2 结果裁剪 |
| `timed-confirm.ts` | `examples/extensions/` | `ctx.ui.confirm(..., { timeout })` 与 `AbortSignal`。M4 确认超时（对应 60s 令牌） |
| `confirm-destructive.ts` | `examples/extensions/` | `session_before_*` 返回 `{ cancel: true }`。换档/清空会话的保护 |
| `session-runtime.ts` | `examples/sdk/13-session-runtime.ts` | `runtime.session` 替换后重新绑定订阅/扩展。M2 换档重建会话 |
| `06-extensions.ts` | `examples/sdk/` | `pi.registerTool()` 的最简写法；TypeBox schema |
| `05-tools.ts` | `examples/sdk/` | `createAgentSession({ tools })` 的工具选择语义 |
| `git-checkpoint.ts` / `protected-paths.ts` | `examples/extensions/` | 检查点/回滚、路径允许列表。M4 撤销与写工具边界 |
| `question.ts` / `questionnaire.ts` | `examples/extensions/` | `ctx.ui.select/input` 的结构化提问。`ask_user` 工具 |
| `kimi-deferred-tools.ts` | `examples/extensions/` | 延迟加载工具定义。工具多起来后的 token 控制 |

上游仓库：<https://github.com/earendil-works/pi>（`packages/coding-agent/examples/`）。

**结论**：M2 只需要读 `dynamic-tools.ts`、`truncated-tool.ts`、`session-runtime.ts`、`06-extensions.ts` 四个；M4 再读 `permission-gate.ts`、`timed-confirm.ts`、`git-checkpoint.ts`。

---

## 2. 社区 pi 包（按与本项目相关度）

| 包 | 信号 | 可借鉴 | 注意 |
| --- | --- | --- | --- |
| [`@gotgenes/pi-permission-system`](https://github.com/gotgenes/pi-packages) | 261★，MIT，持续更新 | **在 agent 启动前隐藏被禁工具**；allow / ask / deny；通配符规则；MCP/skill 也纳入同一策略 | 是 `MasuRii/pi-permission-system` 的 fork，配置格式已分叉；面向较新 pi |
| [`pi-mcp-adapter`](https://github.com/nicobailon/pi-mcp-adapter) | 1595★，MIT | 用**一个 ~200 token 的代理工具**按需发现 MCP 服务；服务懒启动、空闲 10 分钟停止 | 其 README 指出 pi 0.99 起已内置 MCP；我们仍钉 0.82.1，不要直接装 |
| [`pi-mcp-extension`](https://github.com/irahardianto/pi-mcp-extension) | 10★，MIT，CI 齐全 | 多传输、分页 `tools/list`、`tools/list_changed` 热刷新、取消传播、重连退避、稳定工具名、子进程 PID 回收 | 星数少，但当"桥接生命周期"参考足够；不必引入 MCP 本身 |
| [`@juicesharp/rpiv-ask-user-question`](https://github.com/juicesharp/rpiv-mono) | 900★（monorepo），MIT | 最多 4 个问题、带选项的结构化提问，答案以结构化数据回填 | 终端 UI，我们的 `ask_user` 要映射到游戏窗口 |

**版本提醒**：社区包普遍面向 pi 0.99+；本项目固定 `0.82.1`（`design.md` §7.2）。把它们当**源码参考**，不要直接 `pi install` 进 sidecar。

---

## 3. 游戏 Mod / 桥接架构

### 3.1 DSP 专用（架构验证，不是成熟依赖）

| 项目 | 信号 | 做法 | 借什么 / 不借什么 |
| --- | --- | --- | --- |
| [`m2ai-mcp-servers/mcp-dsp-game`](https://github.com/m2ai-mcp-servers/mcp-dsp-game) | 4★，MIT，2026-04 | BepInEx 插件（**Harmony IL patch** + 实时指标 + WebSocket）→ Python FastMCP → Claude；工具：产线瓶颈、电网、物流饱和、工厂快照、存档解析、蓝图生成 | 借：领域拆分（production/power/logistics 各一个聚合工具）、存档解析兜底。不借：Harmony patch（我们的设计刻意不用） |
| [`jsmenzies/dyson-sphere-mcp`](https://github.com/jsmenzies/dyson-sphere-mcp) | 1★，无 LICENSE 文件，2026-08 | BepInEx 插件内嵌 WebSocket + JSON-RPC `:18181` → Python MCP 12 工具；研究/行星/物流；`DSP_USE_MOCK` 假数据；可选 REST | 借：研究按星球拆分、ILS 明细、**mock 模式**（我们 M2 的假 mod 测试同思路）。不借：MCP 作为主链路、无许可证代码 |
| [DSP Planner Export](https://thunderstore.io/c/dyson-sphere-program/p/Suite/DSP_Planner_Export/) | Thunderstore | 导出工厂数据给规划器 | 借：物品/建筑/配方的字段口径，和 `game.get_production` 的序列化形状 |
| [CopyPlanetFactory](https://thunderstore.io/c/dyson-sphere-program/p/crecheng/CopyPlanetFactory/) | Thunderstore | 复制行星工厂状态 | 借：遍历 `entityPool` / `veinPool` 的写法与边界处理 |
| [`Selsion/DSPMods`](https://github.com/Selsion/DSPMods) | 多个 DSP 优化/迁移 mod | BepInEx 工程结构、存档迁移 | 借：项目结构与存档兼容处理 |

DSP 这两个 MCP 项目更像"架构可行性验证"，代码成熟度不足以直接依赖；但它们的**工具清单**很适合对照我们 M2 的粒度。

### 3.2 其他游戏（架构质量更高，重点读）

| 项目 | 信号 | 做法 | 借什么 |
| --- | --- | --- | --- |
| [`pardeike/RimBridgeServer`](https://github.com/pardeike/RimBridgeServer) | 39★，MIT，pardeike（Harmony 作者） | RimWorld mod 内置 bridge，向外部 agent 暴露语义状态、UI 操作、截图；GABS 做进程管理 | **M2 的结果契约**：约 30K 字符分页、`page.nextOffset`、每次回复带 `operation`（Id/Status/Success/DurationMs/Warnings）、未知参数以 warning 报告、attention 策略 |
| [`zorrobyte/rimagent`](https://github.com/zorrobyte/rimagent) | 66★，MIT | RimBridge（HTTP `127.0.0.1:8765`）+ Python agent；感知快照含趋势/diff；30 次工具调用上限；watcher 无 LLM；steward 每 tick 自动化；失败流驱动自修复 | **M2 的"缺什么"感知**：趋势 + 世界 diff + 结构化摘要；**有界工具循环**；确定性自动化与 LLM 分离。**不要**借 `engine.get/set/call`（任意对象访问） |
| [`thedemon117/ai-player-v3`](https://github.com/thedemon117/ai-player-v3) | 9★，Factorio 2.0 | mod 写感知快照到 `script-output` → Python bridge → LLM 选 skill → RCON 执行；skill 层负责"做什么"，mod 负责"怎么做"；primitive 兜底；可选 MCP；`set_autonomy(false)` 移交控制 | **工具分层**：高层 skill（少量、参数化）优先，primitive 只做兜底；感知快照；自治权仲裁。**不要**借 `run_lua` / 文件轮询 |
| [`bits-orio/ai-agent-bridge`](https://github.com/bits-orio/ai-agent-bridge) | 2★，MIT，设计文档很完整 | Factorio companion mod 只实现协议，不绑定 agent；`/ask`、会话、mod 发布 lookup；按玩家/服务器/天限制成本；假游戏测试套件 | **协议优先**、agent 可替换；成本护栏；用假游戏做端到端测试。RCON 本身不适用于我们 |
| [`Suixinlei/factorio-ai-coworker`](https://github.com/Suixinlei/factorio-ai-coworker) | 1★，MIT | 有界 allow-list 的 batch/atomic 动作、结构化 query、显式 `agent_id`、skills 文档 | M4 写工具的 allow-list、批量动作语义、操作归属 |
| [`art-from-the-machine/Mantella`](https://github.com/art-from-the-machine/Mantella) | 419★，AGPL | Skyrim/FO4 NPC 对话：mod + Python 服务 + STT/LLM/TTS；成熟的安装/配置/发布 | 进程拆分、首次配置 UX、打包与文档；它不是工具调用架构的参考 |

---

## 4. 直接映射到我们的 M2

| M2 工作 | 首选参考 |
| --- | --- |
| `ToolHost` 绑定/路由/重连 | `pi-mcp-extension` 的生命周期 + `session-runtime.ts` |
| 能力过滤、启动前隐藏工具 | `@gotgenes/pi-permission-system` + `dynamic-tools.ts` |
| `tool_result` 形状、分页、operation 元数据 | `RimBridgeServer` |
| 结果裁剪与"告诉模型还有多少" | `truncated-tool.ts` + RimBridgeServer 的 30K 预算 |
| "我现在缺什么"的感知摘要 | `rimagent` 的趋势/diff + `ai-player-v3` 感知快照 |
| 假游戏测试 | `ai-agent-bridge` 的 fake-game 套件 + `dyson-sphere-mcp` 的 mock 模式 |
| 技能 vs 原语（M4） | `ai-player-v3` 的 skill/primitive 分层 + `rimagent` 的 player-parity verbs |

---

## 5. 明确不要照搬

- **Harmony IL patch 取状态**（mcp-dsp-game）：游戏更新即碎；我们坚持公开 API。
- **`engine.get/set/call` 任意对象访问**（rimagent）：等于把反射当工具，无法做参数与影响面校验。
- **`run_lua` / 任意 RCON 命令**（ai-player-v3）：等价于 shell。
- **MCP 作为主链路**：单客户端场景没有收益；但注意 pi 0.99+ 已内置 MCP，未来若要多客户端，可通过 adapter 接入，不必换 dsh。
- **几十个细碎工具**：RimBridgeServer/rimagent 的工具面远大于我们；M2 保持 6 个只读工具。
