# pi 升级评估：0.82.1 → 1.4.0

> 状态：**已执行（2026-10-23）**。依赖、兼容代码、测试、打包自检均已完成；P1 的历史回放改造仍待做。
>
> 结论：**可以升，而且比预期便宜。**
> 代码层面只需要给 `PersonaResourceLoader` 补两个方法、把 `@earendil-works/pi-ai` 变成直接依赖；
> 实测 typecheck 和全量离线测试都通过。
> 真正要小心的是**会话格式新增了 entry 类型**、`compaction` 语义变了，以及升级后不能随便降级。

- 升级前：`@earendil-works/pi-coding-agent@0.82.1`（2026-07-25）
- 当前：`@earendil-works/pi-coding-agent@1.4.0`（2026-10-21）
- 中间跨了 24 个发布（0.83 → 1.4），约 3 个月。

---

## 1. 一句话总结

1.4 的大部分新增是 **CLI/TUI、codemode/MCP、provider 目录**，对 headless sidecar 影响不大；
SDK 的核心入口（`createAgentSession`、`AgentSession`、`SessionManager`、`ModelRuntime`、`ToolDefinition`）保持兼容。
真正需要处理的是：

- `ResourceLoader` 新增两个方法（唯一编译错误）；
- `pi-ai` 从“pi 的传递依赖”变成需要显式声明的依赖；
- 会话文件新增 `usage` / `context_edit` / system message；
- `compaction.reserveTokens` 不再限制摘要长度；
- 我们的 `removeTurnFromEntries()` 需要把 `context_edit` 当成不支持类型；
- 聊天记录回放不能再依赖 `buildContextEntries()`。

---

## 2. 三个月的主题

| 版本 | 主要变化 | 对我们 |
|---|---|---|
| 0.83 | TypeBox 1.3（移除一批废弃 API） | 我们没用被移除的 API，测试通过 |
| 0.84 | TUI fullscreen；`message_update` 只发 delta；ModelRegistry/ModelRuntime 鉴权签名调整 | `message_update` 我们本来就只用 delta，兼容 |
| 0.85 | 外部 entry 恢复内存会话；图片输入限制 | 可选用 |
| 0.86 | **按模型配 compaction 预算**；prompt cache warming；**system prompt/工具变更持久化**；`pi.on()` 返回 unsubscribe | 与上下文压缩方案直接相关 |
| 0.87 | **`ContextEditEntry`**；`SessionManager` 成为模型上下文的唯一权威；`context` 钩子不再看到 system 消息；session replacement 移到 `AgentSessionRuntime` | 影响历史/删除/裁剪 |
| 0.99 | codemode / MCP / 虚拟模型 / classifier；内置扩展 | SDK 默认**不加载**这些内置扩展，安全边界不变 |
| 1.0 | fullscreen 默认；codemode 更省 token；MCP OAuth 加固 | 基本无关 |
| 1.1–1.3 | 工具暴露/加载、性能、GPT-6/Claude Opus 5.5、恢复会话保留工具、切换模型丢弃旧 thinking | 部分可靠性修复对我们有利 |
| 1.4 | **摘要不再受固定输出上限**；models.json 参考；codemode 并发；Rex 终端 | 摘要成本/长度需要注意 |

完整 changelog 见 [pi 的 CHANGELOG](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/CHANGELOG.md)。

---

## 3. 对 Icarus II 的实际影响

### 3.1 编译/运行必须改

**（1）`PersonaResourceLoader` 缺两个方法**

pi 1.4 的 `ResourceLoader` 接口新增：

```ts
getSystemPromptSource(): { path: string } | undefined;
getAppendSystemPromptSources(): Array<{ path: string }>;
```

我们的装饰类在 [`sidecar/src/agent.ts`](sidecar/src/agent.ts) 里实现这个接口，所以会报：

```text
Class 'PersonaResourceLoader' incorrectly implements interface 'ResourceLoader'.
  Type is missing the following properties: getSystemPromptSource, getAppendSystemPromptSources
```

补法（保持“永远用我们的人格、不暴露外部来源”）：

```ts
getSystemPromptSource(): { path: string } | undefined {
  return undefined;
}

getAppendSystemPromptSources(): Array<{ path: string }> {
  return [];
}
```

**（2）`@earendil-works/pi-ai` 要变成直接依赖**

我们直接从 `@earendil-works/pi-ai` 引 `Api, Model`（[agent.ts](sidecar/src/agent.ts)），但 `sidecar/package.json` 只声明了 pi-coding-agent。
0.82 时它恰好被 pnpm 提升到顶层；1.4 里如果顶层还残留旧的 `pi-ai@0.82`，`Model` 类型会和 pi 1.4 内部用的 `pi-ai@1.4` 不相等，报 `compat` 不兼容。

建议在 `sidecar/package.json` 显式加：

```json
"@earendil-works/pi-ai": "1.4.0"
```

并把 pi-coding-agent 也改成**精确版本**，不要 `^1.4.0`（它发版很快，1.x 也可能有 breaking change）：

```json
"@earendil-works/pi-coding-agent": "1.4.0"
```

**（3）Node 版本**

0.82.1 和 1.4.0 的 `engines` 都是 `>=22.19.0`。升级前我们的 `sidecar/package.json` 写的是 `>=20`，构建脚本也用 `--target=node20`——这是**既有偏差**，不是 1.4 新增的。打包用的 SEA 运行时是 Node 24，所以发布物没问题；现已统一为 Node ≥22.19 / `node22`。

**（4）TypeBox**

pi 1.4 用 `typebox@1.3.27`；我们声明 `^1.1.38`，干净安装会解析到 1.3.x。实测代码/测试通过，但建议把 `typebox` 也 pin 到 `1.3.27`，避免下次安装漂移。

### 3.2 会话格式：三种新 entry，历史/删除要适配

`CURRENT_SESSION_VERSION` 仍然是 3，但 `SessionEntry` 联合类型新增了：

| 新 entry | 作用 | 对我们的影响 |
|---|---|---|
| `usage` | 记录不进入模型上下文的用量（如 cache warm） | 历史回放忽略即可；删除回合时保留/重连都安全 |
| `context_edit` | append-only 地隐藏/替换某条旧消息的模型上下文 | `targetId` 可能指向被删回合；`removeTurnFromEntries()` 必须拒绝或正确处理 |
| `message(role: "system")` | 持久化 system prompt / 工具声明的变更 | `extractHistoryMessages()` 已按 role 过滤，不会显示；但删除重连时要保留 |

另外 `CompactionEntry` 现在有 `systemMessage` 检查点，`firstKeptEntryId` 可以是 `null`（retain-none compaction）。

**升级前的两个冲突**（和上下文压缩文档里说的一样）：

1. `getHistory()` 用 `buildContextEntries()`：压缩后旧消息不会回到 UI。**已在本次升级中改为 `getBranch()` 全量读取**，再过滤 `type === 'message'` 且 `role` 为 user/assistant。
2. `removeTurnFromEntries()` 原先只拒绝 `compaction` / `branch_summary`；**已把 1.4 的 `context_edit` 也加入拒绝列表**，避免删除后留下悬空 edit。压缩前回合仍不能删，这是产品限制，不是升级回归。

**降级风险**：1.4 会在会话里写 system message / `usage` / `context_edit`；再用 0.82 打开这些文件没有保证。升级前备份 `sessions/`，升级后不要指望能无缝回退。

### 3.3 行为语义变化

| 变化 | 说明 | 我们是否受影响 |
|---|---|---|
| `message_update` 只发 delta（0.84） | 不再带累计 `message` / `partial` | 否：我们只用 `assistantMessageEvent.delta` |
| `SessionManager` 是模型上下文唯一权威（0.87） | 改 `session.agent.state.messages` 不再影响后续请求 | 否：我们不这么干 |
| `AgentSession` 移除 `newSession/switchSession/fork`（0.87） | 换会话改用 `AgentSessionRuntime` | 否：我们按存档新建 session |
| `context` 钩子不再看到 system 消息（0.87） | 钩子后 Pi 会恢复 prompt / 工具声明 | **是**：以后做 L1 裁剪不能依赖 system 消息 |
| system prompt / 工具变更持久化（0.86） | 会写 system message，resume 后可重放 | **是**：rich-text 切换会累积 system message；UI 过滤即可 |
| `compaction.reserveTokens` 只控制触发（1.4） | 摘要改用模型输出上限，不再被 reserve 截断 | **是**：自定义摘要要自己限长/限成本 |
| 恢复会话保留 active tools（1.3） | 工具选择会随会话恢复 | **是**：强制工具回合如果异常退出，限制可能被持久化；`finally` 已恢复，但要有启动兜底 |
| 切换模型丢弃旧 thinking（1.3） | 避免把上一模型的 reasoning 当正文 | 对我们有利 |
| `agent_settled`（0.86+） | 表示不会再自动继续 | 可选：比 `prompt()` 返回更适合做最终态 |
| `getContextUsage()` 可能为 null | 压缩后到下一次响应前 token 未知 | 如果以后显示 token 占用要处理 |

### 3.4 可以直接用的新能力

- **`ContextEditEntry`（0.87+）**：`sessionManager.appendContextEdit(entryId, null)` 把旧消息从模型上下文隐藏，但不改写历史。它比 `context` 钩子更适合做「旧工具结果/截图只对模型隐藏」的持久裁剪；UI 仍能回放原文。映射可用 `buildSessionProjection().entries[].sourceEntry.id`。
- **按模型 compaction 预算（0.86+）**：`compaction.modelOverrides` 可以按 provider/model 配 `reserveTokens` / `keepRecentTokens`，比把 `model.contextWindow` 夹小更干净。
- **`SessionManager.inMemory(cwd, options, entries)`（0.85+）**：从外部 entry 恢复会话；以后如果做「导入聊天记录」会用到。
- **`AgentSessionRuntime`（0.87+）**：如果以后想在同一进程里切换存档/会话，用它；现在不需要。
- **SDK 不自动加载 codemode/MCP**（1.4 文档明确）：我们继续保持 `noExtensions: true`，安全边界不变。

---

## 4. 兼容性实测

在 `.scratch/sidecar14` 里做了一次干净安装（不影响仓库）：

```text
pi-coding-agent 1.4.0
pi-ai 1.4.0
Node 24.18.0
```

步骤与结果：

| 检查 | 结果 |
|---|---|
| `tsc --noEmit`（补两个方法前） | 2 个错误，都是 `PersonaResourceLoader` 缺方法 |
| 补 `getSystemPromptSource` / `getAppendSystemPromptSources` | typecheck 通过 |
| `npm test`（全量离线，26 个套件） | **EXIT 0** |
| esbuild 单文件 bundle | **8.5 MB**（升级前 0.82.1 的 bundle 是 8.6 MB） |
| 空目录 `--selfcheck` | 通过 |

结论：**没有隐藏的运行时大坑**；至少 loopback agent、工具、历史、蓝图、KB、脚本沙箱这些离线路径都过了。

没覆盖到的：

- 真实 provider（OpenAI/Anthropic/DeepSeek）的流式/工具/缓存行为；
- 1.4 新写的 system message / `context_edit` / `usage` 的实际回放；
- 真实游戏进程里的工具调用和 UI 端到端；
- 打包后的 SEA 可执行文件（只测了 CJS bundle 的 selfcheck）。

---

## 5. 建议的升级步骤

### P0：编译与安全（0.5–1 天）

1. 备份 `sessions/`（至少复制一份；升级后不保证能降级）。
2. `sidecar/package.json`：
   - `@earendil-works/pi-coding-agent`: `1.4.0`（精确）
   - `@earendil-works/pi-ai`: `1.4.0`
   - `typebox`: `1.3.27`（可选但建议）
3. 补 `PersonaResourceLoader` 两个方法。
4. `removeTurnFromEntries()` 把 `context_edit` 加入不支持类型；补测试夹具。
5. 文档/构建：Node ≥22.19；`scripts/build-sidecar.sh` 的 `--target` 已从 `node20` 改为 `node22`。
6. 跑：`npm test`、`scripts/build-sidecar.sh`（至少本平台）、空目录 selfcheck。

### P1：历史与删除语义（1–2 天）

- `getHistory()` **已改为** `getBranch()` 全量消息，过滤 system；还缺「压缩后重进存档历史仍在」的真实会话测试。
- 删除逻辑已覆盖：compaction / branch_summary / context_edit / usage 四种 entry（离线测试）。
- 待办：用 1.4 生成一个真实会话（含 system message），验证回放/删除/换存档。

### P2：可选能力（按需）

- 用 `compaction.modelOverrides` 配上下文预算（配合 [context-compression.md](context-compression.md)）。
- 用 `ContextEditEntry` 做旧观测的持久隐藏。
- 用 `agent_settled` 替代部分 `prompt()` 后的收尾判断。

---

## 6. 风险

- **发版节奏快**：1.x 仍可能带 breaking change。精确 pin，升级走同一套回归。
- **不能随便降级**：1.4 写的新 entry 旧版本不保证能读。
- **真实 provider 未测**：离线测试不能覆盖流式解析、工具调用、缓存计费。
- **摘要成本**：1.4 的摘要不再受 `reserveTokens` 限制，长 reasoning 模型可能生成更长的摘要；自定义摘要要限 `maxTokens`。
- **强制工具状态**：工具选择会持久化；异常中断时要有启动兜底，避免只剩一个工具。
- **`pi-ai` 传递依赖**：不显式声明的话，干净安装/不同包管理器可能解析到不同实例。

---

## 7. 执行记录（2026-10-23）

已落地：

- `sidecar/package.json`：`pi-coding-agent@1.4.0`、`pi-ai@1.4.0`、`typebox@1.3.27`（均精确版本），`engines.node` 改为 `>=22.19.0`；`package-lock.json` 已更新。
- `sidecar/src/agent.ts`：`PersonaResourceLoader` 补 `getSystemPromptSource()` / `getAppendSystemPromptSources()`；`getHistory()` 改用 `getBranch()`，压缩后 UI 历史不再丢。
- `sidecar/src/history.ts`：`context_edit` 加入 `unsupported_session`；新增 system message / usage / context_edit 三种 entry 的删除测试。
- `scripts/build-sidecar.sh`：`--target=node22`，Node 门槛与 bundle 大小注释同步。
- `design.md`、`docs/reference-implementations.md`、`docs/context-compression.md`：版本与 Node 门槛同步。

验证：

| 检查 | 结果 |
|---|---|
| `npm run typecheck` | 通过 |
| `npm test` | **EXIT 0** |
| `scripts/build-sidecar.sh linux-x64` | **EXIT 0**，bundle **8.5 MB**，SEA **127 MB** |
| 空目录 `--selfcheck` | 通过 |

仍未做：

- 真实 provider / 真实游戏端到端冒烟；
- 用 1.4 生成含 system message 的真实会话做回放/删除验证；
- 压缩前回合的删除策略（目前明确拒绝）。

注意：本机全局 `pi` CLI 仍是 0.82.1；升级的是 sidecar 的本地依赖，不影响全局命令。

---

## 8. 参考

- [pi-coding-agent 1.4.0（npm）](https://www.npmjs.com/package/@earendil-works/pi-coding-agent/v/1.4.0)
- [pi CHANGELOG](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/CHANGELOG.md)
- [pi SDK 文档](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/sdk.md)
- [Session File Format](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/session-format.md)
- 本地已安装版本：`sidecar/node_modules/@earendil-works/pi-coding-agent/package.json`
- 上下文压缩方案：[docs/context-compression.md](context-compression.md)
