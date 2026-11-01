# pi 与 DSH 的 todolist 实现调研

日期：2026-11-01
范围：本机安装的 `@earendil-works/pi-coding-agent` 1.4.0（sidecar 依赖）与 `@deepseek-ai/dsh` 0.2.1-rc.5（当前 harness）。

## 结论摘要

- **pi 没有内置 todolist。** 官方只在 `examples/extensions/todo.ts` 给了一个示例扩展，另有一个 `examples/extensions/plan-mode/` 演示“计划模式 + 步骤勾选”。
- **DSH 有内置 todolist**：`@deepseek-ai/dsh-tool-todo` 提供 `todo_write` 工具，整表替换、三态状态、状态写在会话日志事件 `todo/write` 上，由 `todos` session projection 回放，客户端 `TodoDock` 渲染在输入框上方。
- **DSH 的 todo 是“本轮计划”而非长期任务表**：投影在 `turn/start` 清空、`turn/end` 保留；跨轮次的持久目标由另一套 goal 子系统（`create_goal` / `get_goal` / `update_goal` + `/goal`）承担。
- 我们现有的 `game_todo_*` 语义上更接近 DSH 的 **goal**（跨会话持久、有游戏内进度判定），而不是 DSH 的 todo。

## pi：只有示例，没有内置

### todo.ts 示例

文件：`sidecar/node_modules/@earendil-works/pi-coding-agent/examples/extensions/todo.ts`

- 用 `pi.registerTool({ name: "todo", ... })` 注册工具，参数 `{ action: list|add|toggle|clear, text?, id? }`。
- 状态是扩展进程内的 `todos` 数组；**持久化靠工具结果的 `details`**（`{ action, todos, nextId }`）。
- `session_start` / `session_tree` 时用 `ctx.sessionManager.getBranch()` 扫历史里的 `toolResult` 重建状态，因此换分支后状态与分支一致。
- 另注册 `/todos` 命令，用 `pi-tui` 自绘列表（交互模式专用）。
- 这是“示例”，不是 npm 包；要装只能把文件放进扩展目录或用 `extensionFactories` 内联。

### plan-mode 示例

文件：`examples/extensions/plan-mode/index.ts`、`utils.ts`

- 从助手消息里的 `Plan:` 段落按编号抽步骤（`extractTodoItems`），用 `[DONE:n]` 标记完成（`markCompletedSteps`）。
- 状态用 `pi.appendEntry("plan-mode", { enabled, todos, executing })` 写进会话日志（不进模型上下文）。
- footer 状态 + `ctx.ui.setWidget("plan-todos", ...)` 渲染步骤；计划模式还会 `pi.setActiveTools()` 禁掉写工具。

### pi 的状态承载选项（官方文档 `docs/extensions.md`）

| 用途 | 机制 |
| --- | --- |
| 跟随分支的工具状态 | 工具结果的 `details` |
| 不进模型上下文的持久数据 | `pi.appendEntry()` |
| 自定义内容并送进模型上下文 | `pi.sendMessage()` |
| 跨会话数据 | 外部存储 |

其它相关能力：`exposure`（direct/model-only/codemode/deferred/hidden）、`namespace`、`annotations`、`registerToolRenderer`。

### 对 IcarusII 的影响

sidecar 当前是 `noExtensions: true` + 单个内联 `extensionFactories`（persona 提示词），见 `sidecar/src/agent.ts`。要用 pi 的 todo，只能把等价逻辑写成内联 `ExtensionFactory`（或打开扩展目录），不能直接“安装”那个示例。

## DSH：内置 `todo_write`

### 包与注册

- 包：`@deepseek-ai/dsh-tool-todo`（插件导出 `name/inject/apply`，`inject = ["tools", "sessionProjections"]`）。
- 默认组合挂在 `@deepseek-ai/dsh-base` 的 `cordis.patch.yml`：`id: tool-todo`，配置 `allowParallelInProgress: true`。
- `allowParallelInProgress` 是**必填**配置，同时改写工具描述里“允许几个任务同时 in_progress”的条款。

### 契约

- 工具名 `todo_write`；每次提交**完整列表**，替换旧列表；没有逐项编辑、没有回读工具。
- 条目 `{ content: string, status: "pending" | "in_progress" | "completed" }`，`additionalProperties: false`。
- 校验：`content` 去空格后非空且不重复；禁用并行时最多一个 `in_progress`；非 agent 会话调用被拒绝（`todo_write requires an owning agent session`）。
- 返回：`todos` + `counts`（pending/inProgress/completed），渲染成一行 `Updated todo list: …`。

### 存储与回放

- `execute` 里 `exec.agent.session.append("todo/write", { todos })`，状态只存在会话日志，回放时后写覆盖先写。
- 注册 `todos` session projection：
  - `init: () => null`；
  - `todo/write` → 用最新列表；
  - `turn/start` → 清空为 `null`；
  - `turn/end` 不改，保留刚完成的清单；
  - `wire.view` 把当前列表推给 UI。
- 代码与 README 的表述有出入：README 概述说“跨轮次持续”，实现是每次 `turn/start` 清空——以代码为准，实际是“当前轮次的计划”。

### UI

- `dsh-client-ui-conversation` 的 `TodoDock`：挂在 `conversation.input.dock`，把投影渲染成输入框上方的任务列表，空列表不渲染。
- `dsh-client-ui-tool` 的 `tool-todo-write` / `tool-todo-call`：把每次 `todo/write` 和它的“前一份列表”做成节点，工具卡因此能显示这次改了什么。

### 与 goal 的区别

- DSH 另有 goal 子系统：`create_goal` / `get_goal` / `update_goal`（带 revision、pause/resume/complete/blocked、自动续轮）+ `/goal` 命令，走的是 `goal` 投影。
- todo = 轮内计划；goal = 跨轮次、可暂停/续跑的持久目标。

## 四方对比

| 维度 | pi todo 示例 | pi plan-mode | DSH todo | DSH goal | IcarusII `game_todo_*` |
| --- | --- | --- | --- | --- | --- |
| 形态 | 列表（增删改） | 从计划文本抽步骤 | 整表列表，三态 | 单一目标 + 轮次 | 原生目标分组/子目标 |
| 状态存放 | 工具结果 `details` | `appendEntry` | 会话事件 `todo/write` + 投影 | 会话事件 + 投影 | 存档内的 Global TodoModule + GoalSystem |
| 跨轮次 | 是（分支内） | 是（分支内） | 否（`turn/start` 清空） | 是 | 是（随存档） |
| 进度判定 | 模型自己 toggle | `[DONE:n]` 标记 | 模型自己改 status | 模型/轮次驱动 | 游戏原生 `GoalDeterminator` |
| 玩家可见 | TUI 组件 | footer + widget | 输入框上方 dock | 输入框 dock | 游戏左侧原生目标面板 |
| 是否有回读工具 | `list` action | 无 | 无 | `get_goal` | `game_todo_list` |

## 对 IcarusII 的几种用法

1. **只借 DSH 的契约，不借存储**：把 `game_todo_*` 的模型接口改成整表替换（例如 `game_todo_set`），三态由游戏进度推导，减少“增量增删”带来的同步竞态。
2. **加一层轮内 todo**：sidecar 内联一个 `todo_write`（整表、三态、无回读），状态放 pi 会话（`appendEntry` 或临时字段），聊天窗渲染“本轮计划”。与游戏目标解耦，不受沙盒/自由探索门禁影响；缺点是玩家看不到左侧进度。
3. **完全照 DSH todo**：状态进 sidecar 会话，UI 在聊天窗；游戏目标只保留给“玩家要追踪进度”的场景。会和现有原生面板形成两套列表。
4. **照 DSH goal**：如果我们要的是“跨轮次目标”，DSH goal 的 `create/update` + revision 语义比 todo 更贴近 `game_todo_add`，但它是单目标而不是列表。

建议：先明确要解决的是“模型缺计划感”还是“玩家要在游戏里看到进度”。前者用方案 2，后者继续走原生目标；两者都要时，用同一组 id 把轮内 todo 与原生目标关联，而不是做两套独立列表。

## 参考文件

- pi：`sidecar/node_modules/@earendil-works/pi-coding-agent/examples/extensions/todo.ts`、`examples/extensions/plan-mode/{index,utils}.ts`、`docs/extensions.md`、`docs/session-format.md`
- DSH：`node_modules/@deepseek-ai/dsh-tool-todo/lib/index.js`、`README.md`、`README.zh.md`、`node_modules/@deepseek-ai/dsh-base/cordis.patch.yml`、`node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/types/client/skeleton/TodoPanel.d.ts`、`node_modules/@deepseek-ai/dsh-client-ui-tool/lib/types/client/tool/models/todo-history.d.ts`、`node_modules/@deepseek-ai/dsh-tool-goal/lib/index.js`
- 本仓库：`sidecar/src/agent.ts`（`noExtensions` + 内联 extension factory）、`mod/src/IcarusII/Game/Tools/GameGoals.cs`
