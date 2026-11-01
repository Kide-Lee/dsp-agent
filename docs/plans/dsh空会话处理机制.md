# DSH 对「空的新会话」的处理机制

- 调研日期：2026-11-01
- 调研对象：本机安装的 `@deepseek-ai/dsh` 0.2.1-rc.5（`~/.nvm/versions/node/v24.18.0/lib/node_modules/@deepseek-ai/dsh/`）。
- 证据基础：npm 包里发布的是编译后的 JS（带原始 JSDoc 注释），不是源码仓库。下文行号都按这份构建产物计算，路径统一相对包内 `node_modules/@deepseek-ai/`。
- 调研方式：只读代码。未启动新会话、未改仓库代码、未构建、未部署。

## 0. 结论

DSH 里没有「空会话」这个独立对象，它的正式名字是 **blank Session**，语义是「还没有任何一次 `turn/start` 的会话」。
这套机制同时解决四个问题：

1. **不浪费**：点开「新会话」时不立刻落盘、不调 LLM、不建标题，只建一个内存里的会话实体。
2. **不堆积**：再次点「新会话」时优先**复用**工作区里那个还没用过的 blank，而不是无限新建。
3. **不闪烁**：客户端在点击的同一帧就把行标记为已使用，Host 侧再按 `turn/start` 事件校正。
4. **失败可退**：第一条消息发送失败时，会话仍然是 blank，还能被下次「新会话」复用。

## 1. blank 的判定标准

Host 侧有两个互相独立的判定点，都以「有没有 `turn/start`」为准：

- **投影**：`turnBoundary` 投影维护 `openTurnStartSeq` 与 `lastTurn`，只有 `turn/start` 会推进 `lastTurn`（`dsh-agent-loop/lib/index.js:1364`）。
- **复用判定**：`isBlank()` 返回 `boundary === undefined || (boundary.openTurnStartSeq === null && boundary.lastTurn === 0)`（`dsh-api-session-controller/lib/index.js:494`）。
- **列表元数据**：`sessionListMetadata` 投影初始 `{ blank: true, lastPromptAt: null }`，`blank` 只会在遇到 `turn/start` 时翻成 `false`；`lastPromptAt` 只认 `source.kind === "user"` 的 `user/message`（同文件 `:1854`）。
- **兜底**：活会话列表摘要用 `blank: metadata?.blank ?? session.seq === 0`，冷会话用 `blank: metadata?.blank ?? false`（同文件 `:1949`、`:1999`）。

所以「空」是**日志性质**，不是「文件大小为 0」或「消息数组为空」：一个只写过 `session/title`、`model/selection` 之类事件的会话仍然是 blank。

## 2. 创建路径

### 2.1 客户端发起

`SessionManager.create()` 调 `session.create`。请求只带 `workspaceId` 或 `cwd`，可选带一个 `sessionId` 表示「我想复用这个 blank」，以及 `agentPreset`（`dsh-api-session-controller/lib/types/client/sessions/manager.js:437`）。

成功后客户端**立刻**把它并进会话列表，并标记 `blank: true`：

```js
{ kind: 'placeholder', summary: { agentAvailable: true, sessionId, updatedAt: Date.now(), running: false, blank: true, ... } }
```

注释写得很直白：「A created session is blank by definition (entity birth precedes the first message)」。

### 2.2 Host 侧决策

`SessionCommandController.create()`（`dsh-api-session-controller/lib/index.js:756`）解析出 cwd 后调 `agents.createSession({ sessionId?, cwd, agentPreset?, reusable })`：

- **没带 sessionId** → `createMinted()`，用 `session-<uuid>` 新建（`:266`、`:508`）。
- **带了 sessionId** → 进入 `claim(sessionId, request)`（`:416`），按顺序判断：
  1. 这个 id 属于子代理 → 抛 `ApiSessionSubagentOwnership`；
  2. 这个 id 有活 Agent → `reuseLive()`，要求 `request.reusable(id)` 且 `isBlank()`，可复用就原地用，否则 `createMinted()`；
  3. 没有活 Agent → `observeSession()` 读日志；找不到（例如只在内存、又没活 Agent）就用这个 id 直接 `createAgent()`；
  4. 找到日志 → `reusable = request.reusable(id) && !events.some(e => e.type === "turn/start")`，可复用就 `agents.resume()`，并在 `reuseSetup()` 里用 writer 再复查一次 blank；
  5. resume 抛 `SessionAlreadyOwnedError` → 有活 Agent 就重试 claim，否则新建；抛 `ApiSessionStartedSinceObserved` → 新建。

`reusable` 谓词由调用方给，`session.create` 传的是「不在归档里，且属于请求的工作区」（`:769`）。同一 id 的并发创建用 `inOrder()` 串行化（`:404`），复用时的 blank 复查在 `reuseSetup()` 里再做一次，拿不到 writer 就抛 `ApiSessionStartedSinceObserved` 回退到新建（`:482`）。

### 2.3 Agent 实体本身

`agents.create` 走 `dsh-agent-loop` 的工厂：`SessionPreparation.create(sessions.prepare(id, { meta }))` → `persistence.create(header)` → `setup()` → `publish("startup")`（`dsh-agent-loop/lib/index.js:1893`）。
`publish` 只做 enter/announce，**不启动 turn**，所以空会话的状态是 idle，不存在「刚建好就先跑一轮」。

新建时会话通常**零事件**：`session/end-seed` 只在有 seed（fork/重放）时追加（`dsh-session/lib/types/index.js:544`）。

## 3. 落盘：懒物化

`dsh-session-persistence-jsonl` 的类注释是权威描述：

> Sessions materialize lazily: a created session is visible to this process immediately, reaches disk on its first append or flush, and never existed if the process crashes before that.

实现上 `create()` 只 `registerCreated()` 并把 handle 标成 `materialized: false`，物理文件在第一次 `append` / `flush` 时才出现（`dsh-session-persistence-jsonl/lib/index.js:2399`、`:2465`）。

推论：

- 点了「新会话」但一句话没说、也没改模型/预设 → 磁盘上没有这个会话的文件；Host 重启后它**彻底消失**。
- 反之，只要有一次 append（`turn/start`、`user/message`、`session/title`、`model/selection`、`agent-preset/selected`）就会落盘；此时如果仍然没有 `turn/start`，文件存在但列表里还是 blank。
- 同进程内列表不会漏掉它：`sessionQuery.listSessions()` 会把 `ctx.sessions.list()` 的活会话并进来（`dsh-session-query/lib/index.js:95`），所以刷新页面还能复用同一个 blank；只有 Host 进程退出才丢。
- 工作区记录里会留下这个 id（`attachSession` 会写 `sessionIds`）。重启后它读不到 header，`candidateMatches` 判 false，被从成员列表里过滤掉并打一条 `workspace ... filtered session ...: session header is missing` 警告（`dsh-workspace/lib/index.js:367`、`:905`）。

## 4. UI 上的表现

### 4.1 会话区：hero 阶段

`conversationPhase()` 把空会话算成 `blank`：

```js
conversation.activeTargets.size > 0 || !session.blank && !session.awaitingFirstTurn || session.running ? "active" : session.promptAttempted ? "engaging" : "blank"
```

（`dsh-client-ui-conversation/lib/client.js:17379`）

于是：

- `phase = "hero"` 时显示欢迎页：标题「探索未至之境 / Into the Unknown」、预览版角标、工作区选择器，输入框居中（同文件 `:17610`、`:16143`）。
- 头部进入 `headerBlank`，去掉底部边框和标题区（`:17965`）。
- `DefaultConversationViews` 在 blank 阶段直接 `return null`，不渲染任何消息视图（`:18031`）。
- 三种阶段之间还有一个 `settling`：blank 会话的 history 正在 `loading` 且列表摘要还不是 blank 时，输入框先隐藏，避免「hero 闪成 active」（`:17609`）。

### 4.2 侧栏：一个「新会话」占位行

- 只有**当前选中的那个 blank** 才可见，其他 blank 一律过滤（`dsh-client-ui-workspace/lib/client.js:961`）；当前选中的如果是普通会话，侧栏里就没有「新会话」行，新会话要按按钮/快捷键现取。当前 blank 会被顶到列表最前（`:951`）。
- 行标题固定显示本地化的「新会话 / New Session」，普通无标题会话显示「未命名 / Untitled」（`:1802`）。
- blank 行禁用：拖拽、双击改名、右侧「⋯」菜单、时间戳、状态点、复制标题（`:2262` 起）。

### 4.3 标题

- blank 期间 `title` 投影是 `null`，列表显示的是「新会话」而不是「未命名」。
- 标题只在第一条真实用户消息之后才生成：`sessionTitle.onUserMessage()` 在 `count === 1` 且尚无标题时排一次 provider 调用，并总是先落一个确定性的兜底标题（`dsh-session-title/lib/index.js:380`）。
- 显式改名会写 `session/title` 并且「钉住」标题，后续不再自动生成；改名要求会话不是 blank。

### 4.4 草稿

空会话的未发送草稿按会话 id 存在浏览器 `localStorage` 里（store 键 `dsh.conversation`，读取处 `dsh-client-ui-conversation/lib/client.js:4461`），所以：

- 「新会话」按钮回到同一个 blank 时，草稿还在；
- 刷新页面后草稿也还在（只要会话 id 还能被恢复）；
- 如果这个 blank 从未落盘而 Host 重启过，id 消失、列表里没有对应行，这条草稿就成了 localStorage 里的孤儿键。

## 5. 第一句话之后发生什么

1. 点击发送时，客户端 `beginSubmission()` 同步把 `promptAttempted = true`（`.../client/sessions/session.js:159`），阶段立刻从 `blank` 变 `engaging`，会话从侧栏「新会话」行变成正常行，避免等 Host 回包才变色。
2. `prompt()` 在第一个 `await` 之前就设 `promptAttempted`；若此时 `blankBit` 为真，再置 `firstPromptPendingTurn = true`。
3. Host 接受后（`session.prompt` RPC），客户端把 `blankBit` 置 false 并回调 `onEngaged`，Manager 据此把列表行从 blank 翻掉（`manager.js:220`、`:791`）。
4. Host 侧真正翻状态靠 `turn/start` 事件：`turnBoundary.lastTurn` 推进，`sessionListMetadata.blank` 变 false。
5. 第一条用户消息触发生成兜底标题 + LLM 标题。

## 6. 失败与边界

- **首条消息失败**：`prompt()` 返回失败时不翻 `blankBit`，注释明确写着「Rejection must leave a first prompt blank and eligible for workspace reuse」。也就是说发失败的新会话仍然是「新会话」，下次点 + 会复用它。
- **首轮还没开始时刷新**：`awaitingFirstTurn` 让阶段停在 `engaging`（输入框已停靠），直到观测到 `turn/start` 或 running 位翻转（`session.js:665`、`:440`）。
- **空输入**：输入框的 `submit()` 在 `draft.trim() === ""` 且没有附件时不进入状态机（`:15202`）；状态机的 `onEnter` 对空草稿返回空效果列表（`:5168`）；发送按钮在 `empty` 时直接不提交（`:19159`）。即使绕过 UI，RPC 也会拒：`prompt content must include non-whitespace text or an attachment` / `gateway/bad-request`（`dsh-api-session-controller/lib/index.js:3220`）。
- **同一个 blank 被并发创建**：`inOrder(sessionId, ...)` 按到达顺序串行，后到者在 writer 竞争或 blank 复查失败时回退新建，不会出现两个 Agent 抢一个 id。
- **预设不匹配**：请求指定了 `agentPreset`，而 blank 记录的预设不同时，优先 `presets.select()` 改预设（写 `agent-preset/selected`）；预设被锁定或不存在则回退新建（`:466`）。
- **冷启动恢复**：`restoreSelection()` 发现上次选的是 blank、且还在同工作区未归档，就走 `restoreBlank()`（按其日志记录的预设恢复）；否则落到最近工作区并 `connectWorkspace()` 重新取/建 blank；一个工作区都没有时才会 `initializeDefault()` 建默认工作区（`dsh-client-ui-workspace/lib/client.js:1606` 起）。

## 7. 一句话流程

```text
点「新会话」
  → 客户端找当前工作区里第一个 blank（未归档、cwd 匹配、预设兼容）
  → session.create(workspaceId, sessionId?)
  → Host claim(): 有 turn/start 就新建，没有就 resume 这个 blank
  → 客户端本地插入 blank:true 占位行（同帧，不等回包）
  → 无事件、不落盘、不调 LLM、标题为空
  → UI 显示 hero（欢迎页 + 居中输入框），侧栏显示「新会话」，无右键菜单/拖拽/改名
首条消息
  → 本地 blank→engaging（立刻），Host 侧 turn/start 落日志后真正变 active
  → 首条 user/message 触发兜底标题 + LLM 标题；首次 append（通常就是这次 turn/start 或 user/message）时才物化文件
发送失败
  → 保持 blank，仍可被下次「新会话」复用
```

## 8. 关键代码位置

| 关注点 | 位置（相对 `node_modules/@deepseek-ai/`） |
| --- | --- |
| `session.create` RPC 与工作区分支 | `dsh-api-session-controller/lib/index.js:756` |
| blank 复用决策 `claim()` | `dsh-api-session-controller/lib/index.js:416` |
| `isBlank()` | `dsh-api-session-controller/lib/index.js:494` |
| 列表 blank 投影与摘要 | `dsh-api-session-controller/lib/index.js:1854`、`:1949` |
| 空内容 RPC 拒绝 | `dsh-api-session-controller/lib/index.js:3220` |
| 客户端 create 占位行 | `dsh-api-session-controller/lib/types/client/sessions/manager.js:437` |
| 客户端 blank 翻转 | `dsh-api-session-controller/lib/types/client/sessions/session.js:176`、`:222`、`:486` |
| 懒物化 | `dsh-session-persistence-jsonl/lib/index.js:2399`、`:2465` |
| `turnBoundary` 投影 | `dsh-agent-loop/lib/index.js:1364` |
| 创建不启动 turn | `dsh-agent-loop/lib/index.js:1786`、`:1893` |
| 标题触发 | `dsh-session-title/lib/index.js:380` |
| hero 阶段判定 | `dsh-client-ui-conversation/lib/client.js:17379`、`:17610` |
| 空输入与发送门槛 | `dsh-client-ui-conversation/lib/client.js:5168`、`:15202`、`:19159` |
| 侧栏 blank 可见性/标题/禁用行操作 | `dsh-client-ui-workspace/lib/client.js:951`、`:961`、`:1802`、`:2262` |
| 冷启动恢复与默认工作区 | `dsh-client-ui-workspace/lib/client.js:1606` |

## 9. 值得借鉴的点

对自研 Agent（例如 IcarusII 的 sidecar）直接可借用的三条：

1. **把「空」定义成事件性质而不是容器性质**：判据是「有没有 turn/start」这一个布尔，比数消息条数稳，且天然覆盖「只改了模型/预设」的会话。
2. **复用优先于新建**：客户端先挑一个可复用的 blank 并把 id 传给 Host，Host 再用日志复核；这样既省实体又不会误复用已开始的会话。
3. **懒物化 + 失败可退**：创建不落盘、首条消息失败不改变 blank，两者一起把「误点新会话」的成本压到接近零。
