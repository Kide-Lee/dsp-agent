# 上下文压缩方案研究（Icarus II）

> 结论：**不要把 billion-context 直接接进来。**
> 推荐「pi 压缩兜底 + 确定性上下文裁剪 + 每存档记忆卡」的三层方案。
> 在接任何压缩之前，先修三个会把聊天记录/删除功能弄坏的问题（见 §2）。

---

## 1. 一句话结论

- 我们的上下文增长主要来自 **游戏状态快照、KB/Wiki 摘录、截图**，不是对话本身。
- 这些东西的共同点是：**可重新查询、半衰期短、过期后有害**。所以首选是「确定性丢弃/降级」，而不是「让模型决定压缩哪段」。
- pi 自带的压缩已经能兜底，但当前没有接线，而且会和我们「聊天窗历史回放 / 删除一轮」的语义冲突。
- billion-context 是给**月级 coding agent** 设计的：模型主动 `compress`、分块可搜索、多层摘要。它的强项（精确回忆文件路径/代码/错误串）不是我们的强项；它的成本（额外工具、额外 LLM 调用、4MB 扩展、pi 版本耦合、sidecar 状态）我们都要付。

---

## 2. 现状体检（先修这里，再谈压缩）

### 2.1 pi 的自动压缩其实默认是开的，但没接线

`createAgentSession` 没有传 `settingsManager`，pi 会 `SettingsManager.create(cwd, agentDir)` 读盘；没有配置文件时用默认值：

```ts
// pi 的默认值
{ enabled: true, reserveTokens: 16384, keepRecentTokens: 20000 }
```

也就是说，只要上下文超过 `contextWindow - 16K`，pi 就会自动压缩。但 `AgentSessionEvent` 的 `compaction_start` / `compaction_end` 在 `sidecar/src/agent.ts` 的 `handleEvent` 里没有分支，协议里已有的 `'compacting'` 状态（`sidecar/src/protocol.ts:114`）从未发出。玩家只会看到回答前突然卡住很久。

设计文档其实写了「上下文超限 → pi 自动压缩；状态栏显示整理上下文中」（`design.md:1261`、`design.md:847`），实现漏了。

### 2.2 压缩与「聊天窗历史」「删除一轮」的冲突

两个具体问题：

1. `getHistory()` 曾用 `manager.buildContextEntries()`。这个 API 是**压缩感知**的：一旦发生过 compaction，被摘要掉的旧消息不会出现在返回结果里，聊天窗重进存档后旧对话会凭空消失。**已在 2026-10-23 升级 pi 1.4 时改为 `getBranch()` 修复。**
2. `removeTurnFromEntries()` 拒绝含 `compaction` / `branch_summary` / `context_edit` 的会话（`sidecar/src/history.ts:57`）。也就是说，**第一次压缩之后，玩家仍然不能删除历史回合**，会得到 `unsupported_session`；这是尚未解决的限制。

根因是把「给模型看的上下文」和「给玩家看的聊天记录」当成了同一份数据。必须拆开。

### 2.3 上下文到底装了什么

| 内容 | 典型体量 | 可重建？ | 半衰期 | 建议 |
|---|---|---|---|---|
| 系统提示词 / 人格 | ~3K tokens | 否（固定） | 永久 | 不动；保持前缀稳定 |
| 27 个工具 schema | 约几 K tokens | 否（固定） | 永久 | 不动；按需收窄工具集已有机制 |
| 玩家提问 | 小 | 否 | 永久 | 永远保留 |
| 助手 thinking | 中 | 否 | 短 | 旧轮直接丢 |
| 游戏状态 JSON | 2–10KB/次 | **是** | 分钟级 | 旧结果降级成一行摘要 |
| 截图 | 约 1–3K tokens/张 | **是**（可重截） | 一次性 | 只保留当前轮 |
| KB / Wiki 摘录 | 3–8KB/次 | **是** | 静态（按版本） | 旧结果丢，保留 id/名字 |
| 蓝图卡片 | 小 | 部分 | 长 | 单独持久化（已有） |

一个 20 轮左右、每轮带 2–4 次工具调用的规划对话，很容易到 50–150K tokens。DeepSeek 这类 1M 窗口的模型不会触发压缩，但**每轮都在为越来越长的历史付费**；128K 的自定义端点则会在中途触发压缩。

### 2.4 「1M 窗口」不等于「应该用 1M」

`resolveModel` 给自定义 OpenAI 兼容端点硬编码了 `contextWindow: 128000`（`sidecar/src/agent.ts:1656`），而目录里的 DeepSeek 是 1M。压缩阈值跟着 `model.contextWindow` 走，于是：

- 128K 端点：到 ~112K 才压缩，已经偏晚；
- 1M 模型：到 ~984K 才压缩，单轮成本可能已经涨到不可接受。

应该引入一个**独立于模型窗口的软预算**（例如默认 128K，可配置），压缩/裁剪按软预算触发，而不是等模型真溢出。

---

## 3. 为什么不能照搬 coding agent 的压缩

| | Coding agent | 游戏内助手（我们） |
|---|---|---|
| 长上下文里最值钱的东西 | 文件路径、代码、报错原文、diff | 玩家目标、当前计划、已确认的决策、星球/坐标/蓝图名 |
| 工具结果 | 文件内容，基本不变 | 世界状态快照，分钟级就过期 |
| 重新获取成本 | 读文件（便宜） | 调游戏 API（很便宜，本地） |
| 截图 | 少 | 多，且一次性 |
| 会话寿命 | 项目级，月级 | 单存档，可能月级但单次游玩短 |
| 压缩失败代价 | 重新读文件 | 引用过期数值，给出错误建议 |

所以对我们来说：

- **「保留原文、以后 decompress」价值不大**——旧数值本来就不该再被引用。
- **「模型自己决定压缩哪段」收益不大**——规则是确定的：旧工具结果、旧截图、旧 thinking 就该丢。
- **真正需要语义压缩的是「意图/计划/决策」**，这部分很小，用一个每存档记忆卡就能承载。

---

## 4. 候选方案对比

| 方案 | 做法 | 额外 LLM 调用 | 对聊天记录的影响 | 适配度 |
|---|---|---|---|---|
| pi 内置压缩 | 超阈值后摘要旧消息，保留最近 20K | 每次压缩 1 次 | 会改会话树；当前实现会丢 UI 历史 | 可做兜底，需修 |
| 调参 + 接线 | 软预算 + 事件 + 历史分离 | 同上 | 可做到不影响 | **推荐底座** |
| 确定性裁剪 | `context` 钩子丢旧工具结果/图/thinking | 0 | 不影响（不改会话） | **推荐主力** |
| 每存档记忆卡 | goal/plan/decision 单独存，注入 | 低频 1 次 | 不影响 | **推荐补充** |
| billion-context-pi | 模型调 `compress`，分块/分层/可搜索 | 每次压缩 1 次 + 工具调用 | 宿主 transcript 保留，另有 `.acp.json` | 过度，暂不接 |
| LLMLingua 类 token 压缩 | 压 token 而非消息 | 小模型 | 可读性/数值精度受损 | 不适合 |
| MemGPT / 向量记忆 | 分页记忆 + 检索 | 多次 | 不影响 | 跨会话记忆才考虑 |

### 4.1 为什么不是 LLMLingua

它压的是 token 表面形式，最容易伤到配方名、建筑 id、坐标、数量。游戏助手恰恰依赖这些精确值；而且中文/游戏术语的压缩收益不稳定。不做。

### 4.2 为什么不是 MemGPT

分页记忆 + 函数调用适合「跨会话长期人格」，但会引入额外的检索调用和状态机。我们当前的问题在单会话内，先用更简单的手段。

---

## 5. billion-context 专项评估

### 5.1 它是什么

用户给的是主仓库 [`ranxianglei/billion-context`](https://github.com/ranxianglei/billion-context)，它主要是一个**代理/启动器**，给 Codex、Claude Code、Cursor、OpenCode 等做服务端压缩。我们的宿主是 pi，真正对应的是扩展 [`billion-context-pi`](https://pi.dev/packages/billion-context-pi)（[npm](https://www.npmjs.com/package/billion-context-pi)）。

它的机制（来自其 README）：

- 拦截 pi 的 `context` 事件，给每条消息加不可见的 `<acp>` ref（`m00001`…）；
- 给模型一个 `compress` 工具，由模型指定要压缩的连续区间；
- 压缩成带标签的块，支持多层（T1 → T2 → T3）、`decompress`、`search_context`；
- 保护最近 N 条 / 最近 ~5K tokens、最后一条用户消息、指定工具；
- **取消 pi 内置压缩**，自己成为唯一的上下文管理者；
- 压缩状态存在会话旁的 `<id>.jsonl.acp.json`，原子替换。

它宣称的规模：4.5 个月、17.4 万次模型调用、187.6 亿累计输入 token、20 万窗口零溢出、单会话 8.5K–12K 次调用。这些数字说明它确实为**长寿命 coding session** 打磨过。

### 5.2 为什么不适合我们

1. **领域错配。** 它的价值在「保留文件路径、代码、错误串，之后还能搜出来/decompress」。我们的旧游戏状态不应该被保留，应该被丢弃并重新查询。
2. **模型驱动压缩的可靠性/延迟。** 每次压缩要模型先判断、再发一个 `compress` 工具调用、再等一次摘要 LLM。游戏内是交互场景，玩家在等回答；小模型/本地模型对 `compress` 的参数选择也不稳定。我们的裁剪规则是确定性的，不需要模型参与。
3. **工具面膨胀。** 它最多带 9 个模型可见工具（6 个核心：compress/decompress/search_context/acp_status/acp_cache/acp_rule；3 个 delegate 可关），还有每轮注入的压缩指导。对一个只有 27 个工具的助手来说，这是显著的提示词和选择成本。
4. **版本耦合。** 它是按 pi 0.83.0 开发和测试的；我们已经升级到 `@earendil-works/pi-coding-agent@1.4.0`。peer 写的是 `*`，但 `context` 事件、会话 API、压缩取消仍是强耦合点。
5. **状态复杂度。** 多一个 `.acp.json` 要跟会话文件一起迁移/备份；上游自己也出现过「升级后老会话压缩状态丢失、按 117 万 token 原文重放」的 [issue #3573](https://github.com/ranxianglei/billion-context/issues/3573)。我们还要让聊天记录回放、删除一轮、蓝图卡片挂接都理解这套状态，成本不低。
6. **它解决的 5× 成本问题，我们有更便宜的解法。** 它的对比基准是「上下文膨胀到 1M」；我们只要把软预算压到 128K，并丢掉旧工具结果/截图，就能拿到大部分收益，而且没有额外依赖。

### 5.3 可以借的想法

- 给消息/区间一个稳定 id（便于日志和调试，不一定要暴露给模型）；
- 「保护最近工作集 + 最后一条用户消息」的规则；
- 分层摘要（T1/T2）——如果以后真的需要跨月记忆；
- 压缩状态放在会话旁、原子写——如果以后做记忆卡持久化；
- **聊天记录（transcript）和模型上下文分离**——这是它做对的关键一点。

---

## 6. 推荐架构

```
┌─────────────────────────────────────────────────────────────┐
│ L0 聊天记录（transcript）                                     │
│   pi 会话树全量 message entries；只用于 UI 回放/删除          │
│   永远不因为压缩而丢                                           │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│ L1 模型视图构建（context hook，确定性）                        │
│   system + 记忆卡 + 最近 N 轮原文 + 旧轮降级                   │
│   规则见 §6.2；不写回会话                                      │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│ L2 pi 压缩（兜底，低频）                                       │
│   软预算 85% 才触发；保留最近 8–12K；事件上报状态              │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│ L3 每存档记忆卡（跨天/跨月）                                   │
│   goal / plan / constraints / decisions / entities / pending │
└─────────────────────────────────────────────────────────────┘
```

### 6.1 软预算

```
effectiveBudget = min(model.contextWindow, config.contextBudgetTokens ?? 128_000)
softPruneAt    = 60% × effectiveBudget
hardCompactAt  = 85% × effectiveBudget
reserveTokens  = 8_192
keepRecent     = 12_000 tokens / 最近 2 轮
```

- 对 32K 的本地模型，`effectiveBudget` 可配成 24K；
- 对 1M 的 DeepSeek，默认仍按 128K 管理，避免成本线性上涨；
- 裁剪尽量发生在**轮次边界**，并且按「epoch」触发（到 60% 裁一次，之后稳定到 80% 再裁），减少 prompt cache 失效。

### 6.2 裁剪优先级（从先丢到后丢）

1. **旧截图**：只保留当前轮；旧图替换成 `[画面截图已省略，可重新调用 game_capture_view]`。
2. **旧工具结果**：保留最近 6 条 / 最近 2 轮完整；更早的替换成一行 digest：
   `[第 12 轮 <工具名> 结果已省略；数据可能过期，需要时重新调用]`。
3. **旧 thinking**：整块丢。
4. **旧对话**：只保留 user 原文 + assistant 最终正文，丢掉中间的 tool call/result 链。
5. **仍超限**：从最旧的整轮开始丢；记忆卡和最近一条 compaction summary 永远保留。

硬规则：

- **toolCall 和 toolResult 必须成对**，按 assistant turn 分组，不能只删一半；
- **最后一条 user 消息、当前轮、待确认的写操作调用**永远不裁剪；
- 不要裁剪「正在等待玩家确认」的工具调用参数；
- 被裁剪的工具结果要带「可能过期」提示，避免模型把旧数值当现在。

### 6.3 每存档记忆卡

存储：`<data>/sessions/<save>/memory.json`（和蓝图卡片同级，按存档隔离）。

建议字段：

```json
{
  "updatedAt": "2026-10-23T...",
  "goal": "在本星系建 120/min 蓝糖产线",
  "plan": ["先补电力", "再扩钛化工厂", "最后拉物流塔"],
  "constraints": ["不拆现有主线", "优先本地资源"],
  "decisions": [
    { "what": "蓝糖产线放在极地", "why": "赤道留给科研" }
  ],
  "entities": {
    "planets": ["冰巨星-2"],
    "blueprints": ["IcarusII/polar-blue"],
    "coordinates": ["x=..., y=..."]
  },
  "pending": ["等待确认是否拆除旧冶炼区"],
  "state": {
    "summary": "电力 80MW，蓝糖 45/min",
    "verifiedAt": "2026-10-23T...",
    "stale": true
  }
}
```

注入方式：每次请求前从 `memory.json` 读取，作为一条 custom message 放在 system 之后、最近对话之前（不写回会话树，避免重复堆积），并明确写：

> 以下是本存档的长期记忆，可能已过时；涉及当前数值/位置时，先调用工具核实。

更新策略（三选一，按实现成本）：

- **A. 每 N 轮（推荐 N=10）用便宜模型合并一次**：可靠、额外调用少；
- **B. 模型通过 `memory_update` 工具主动写**：零额外调用，但小模型可能不写/乱写；
- **C. 只在 compaction 时把摘要落成记忆卡**：如果 L1 裁剪做得好，compaction 很少发生，记忆卡会长期不更新，不推荐单独用。

建议先做 A；如果成本敏感，A 的频率可配置。

### 6.4 pi 压缩兜底

- 显式传 `SettingsManager.inMemory({ compaction: { enabled: true, reserveTokens: 8192, keepRecentTokens: 12000 }, retry: { enabled: true } })`，不要依赖磁盘/默认值；
- 把传给 pi 的 `model.contextWindow` 夹到 `effectiveBudget`，让压缩在软预算附近触发，而不是 1M；
- `compaction_start` → `status('compacting')`；`compaction_end` → 回到 `thinking`/`idle`，并记录压缩前后 token；
- 在 `session_before_compact` 里（P4）替换成游戏向摘要指令，不再统计 read/modified files。

### 6.5 聊天记录与删除的修复

- `getHistory()` 已改用 `manager.getBranch()`（全量 entry）并只过滤 `type === 'message'`；压缩 entry 跳过，但不影响旧消息回放（2026-10-23 已落地）；
- `removeTurnFromEntries()` 对 compaction 的处理：
  - 目标回合在最后一次 compaction **之后**：保持现有改写逻辑；
  - 目标回合在 compaction **之前**：返回明确错误 `compacted`，UI 提示「该回合已被压缩，无法单独删除」；
  - 以后可选：用 transcript 重建一个不带该回合的新会话（代价高，先不做）；
- 补 `compaction_start/end` 事件处理；
- 日志增加每轮 `contextTokens` 与预算占比（先只写日志，协议加字段另议）。

---

## 7. 分阶段落地

### P0：修语义（1–2 天，必须先做）

- 历史回放改用全量 branch；
- 删除对 compaction 给出明确错误；
- `compaction_start/end` 接状态；
- 显式 `SettingsManager.inMemory`；
- 加 `contextTokens` 日志。

验收：手动触发一次压缩后，重进存档历史仍在；删除压缩前回合得到可读提示；状态栏出现「整理上下文中」。

### P1：软预算与配置（1 天）

- 新增 `contextBudgetTokens`（默认 128K）、`compaction.reserveTokens`、`keepRecentTokens`、`contextPrune.*`；
- 把 `model.contextWindow` 夹到预算；
- 配置迁移 + 离线测试。

验收：1M 模型在 128K 预算下不会长到 900K；32K 模型能正常压缩。

### P2：确定性裁剪（3–5 天，主力）

- 新增 `sidecar/src/context/`：纯函数 `buildModelContext(messages, policy)`；
- 在 persona 的内联扩展里注册 `pi.on('context', ...)`（或拆成独立 extension 组合）；
- 规则按 §6.2；覆盖 tool 配对、图片、thinking、旧轮降级；
- 单测 fixture：工具调用/结果对、截图、compaction summary、最后一条 user、待确认调用。

验收：100 轮合成会话在 128K 预算下上下文不超过 80%；截图在上下文中不超过 2 张；无 tool 配对错误。

### P3：记忆卡（3–4 天）

- `<save>/memory.json` 读写、按存档隔离；
- 每 10 轮合并一次（可配），或先做手动「记住这个计划」；
- 注入 custom message，带「可能过时」提示；
- 测试：换存档不串记忆；记忆卡损坏时降级为不注入。

验收：跨天重开存档，助手能说出上次的计划和已确认决策；不会把旧电力数值当当前值。

### P4：游戏向摘要（可选，2 天）

- `session_before_compact` + `generateSummaryWithUsage`，自定义指令：
  - 保留目标/计划/决策/命名实体/待确认；
  - 丢弃过期数值与截图描述；
  - 明确写「状态需重新查询」；
- 可选用便宜模型做摘要；
- 失败时回退 pi 默认摘要。

### P5：再评估 billion-context（触发式）

只有满足以下任一条才重新评估：

- 真实日志里 p95 上下文长期 > 400K；
- 产品要求跨存档/跨月记忆；
- pi 升级到 0.83+ 且有隔离 feature flag。

否则不引入。

---

## 8. 指标与验收

先加日志（本地、默认不上报）：

| 指标 | 用途 |
|---|---|
| `contextTokens` p50/p95 / 预算占比 | 判断裁剪是否生效 |
| `compactionCount` / 每次耗时 | 判断兜底是否频繁 |
| `toolResultBytes` / `imageCount` | 找增长来源 |
| 每轮输入/输出 token、缓存命中（provider 返回） | 成本 |
| 首 token 延迟 / 整轮延迟 | 体验 |
| 历史回放条数、删除失败次数 | 回归 |
| 玩家纠正「数值不对」的次数（人工） | 过期状态问题 |

建议的离线实验：

- 用 3 类合成会话回放：① 20 轮规划；② 50 轮带截图；③ 100 轮带 KB 查询；
- 每类在 32K / 128K / 1M 三个预算下跑；
- 断言：上下文曲线在 60% 附近被压平；tool 配对始终合法；UI 历史完整。

---

## 9. 开放问题

1. 「单存档记忆」是产品需求吗？玩家是否希望一个月后助手还记得上次的计划？
2. 默认软预算取多少？128K 是成本/效果的折中，但本地 32K 模型需要单独默认值。
3. 记忆卡更新用便宜模型，还是复用主模型？需要配置项还是自动选？
4. 是否要在 UI 显示 token/上下文占用？这需要协议加可选字段。
5. 删除「压缩前回合」是只报错，还是值得做重建会话？后者成本高，建议先观察。

---

## 10. pi 1.4 的影响（已升级）

sidecar 已从 0.82.1 升级到 1.4.0；pi 1.4.0 对本文方案有几个直接影响：

- **`ContextEditEntry`（0.87+）**：可以 `sessionManager.appendContextEdit(entryId, null)` 把某条旧消息从**模型上下文**里隐藏，而不改写会话历史。它比 `context` 钩子更适合做「旧工具结果/截图只对模型隐藏」的持久裁剪；UI 仍能回放原文。映射消息到 entry 可用 `buildSessionProjection().entries[].sourceEntry.id`。代价是 edit 条目会累积，且是 branch-relative。
- **`compaction.modelOverrides`（0.86+）**：可按 provider/model 配 `reserveTokens` / `keepRecentTokens`，比把 `model.contextWindow` 夹小更干净。
- **1.4 的 `reserveTokens` 只控制触发时机**：摘要请求改用模型输出上限，不再受 `reserveTokens` 限制。自定义摘要时要自己限制摘要长度/成本。
- **`context` 钩子（0.87+）不再看到 system 消息**，Pi 会在钩子后恢复 prompt 与工具声明；L1 裁剪函数不要依赖 system 消息，也不要试图在钩子里改工具集。
- **system prompt / 工具变更会作为 system 消息持久化（0.86+）**：rich-text 切换会在会话里留下 system 消息；UI 回放必须跳过它们（按 `role` 过滤即可）。

升级前（0.82.1）「隐藏旧消息」只能靠 `context` 钩子；升级后优先用 `ContextEditEntry`，并保留 `context` 钩子作为预算兜底。

**已落地的部分（2026-10-23）**：压缩完成时 sidecar 发 `context.compacted`，mod 插入
“[HH:mm:ss] 自动压缩上下文 + 摘要”记录，并把之前回合标灰、禁止单独删除；
`history.undo_compaction` 会真正删掉 compaction entry 并展开旧上下文（LIFO，只允许最近一次），
成功后恢复灰色区间。注意：撤销后如果上下文仍超预算，下一次提问可能立刻再次压缩。
这仍只是“压缩可见/可撤销”，L1 确定性裁剪、软预算和记忆卡尚未实现。

---

## 11. 参考

- pi 压缩文档：`sidecar/node_modules/@earendil-works/pi-coding-agent/docs/compaction.md`（默认 `enabled: true`、`reserveTokens: 16384`、`keepRecentTokens: 20000`；`session_before_compact` 可自定义摘要）。
- pi 源码：<https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/src/core/compaction/compaction.ts>
- billion-context 主仓库：<https://github.com/ranxianglei/billion-context>
- billion-context-pi（Pi 扩展）：<https://pi.dev/packages/billion-context-pi> / <https://www.npmjs.com/package/billion-context-pi>
- 上游状态迁移问题示例：<https://github.com/ranxianglei/billion-context/issues/3573>
- 本项目设计约束：`design.md:21`（复用 pi SDK）、`design.md:23`（信息按需获取）、`design.md:1261`（上下文超限行为）。
