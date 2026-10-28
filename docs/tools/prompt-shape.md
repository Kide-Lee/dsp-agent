# 模型看到的工具提示词：形状、来源与拼接顺序

> 本文回答一个问题：**一轮对话里，模型到底收到了哪些关于工具的文字？**
>
> 这是工具文档的基础层。单工具文档（参数、返回示例、错误码）是对它的补充，
> 不是替代——因为模型真正读到的就是这些文本。
>
> 核对基准：`sidecar/src/prompt/sections.ts`、`sidecar/src/agent.ts`
> （`toolPromptSection` / `currentExtraPrompt`）、`sidecar/src/tools/host.ts`、
> `sidecar/src/tools/registry.ts`。改动这些实现后必须同步本文。

---

## 1. 先纠正一个常见误解

工具的提示词**不是一段**，也不是全部塞在 `ToolDefinition` 里。

pi SDK 的两条路径是分开的：

| 字段 | 去向 | 模型何时看到 |
| --- | --- | --- |
| `description` | function calling 的 **tools 数组**（JSON Schema 同级） | 每次请求 |
| `parameters`（TypeBox） | 同上，转成 JSON Schema | 每次请求 |
| `promptGuidelines` | **system prompt** 的 Guidelines 段 | 每轮 `before_agent_start` |
| 工具名清单 | **system prompt** 的能力段 | 同上 |

SDK 对 `promptGuidelines` 的原话是 "Optional guideline bullets appended to the default
system prompt Guidelines section when this tool is active"——它天生属于 **system prompt**。

而本仓库**整轮替换**了 pi 的默认 coding prompt（为了去掉
`Current working directory: …` 尾巴和 coding agent 身份），默认分支不再执行，
所以 `promptGuidelines` 不会自动出现。`sections.ts` 里那句注释说的就是这个：

> pi 的 DefaultResourceLoader 只会在默认 coding prompt 分支里拼这些规则；
> 我们整轮替换 system prompt，所以必须自己渲染——否则这些规则是死代码。

**结论：** 文档要收录的"暴露给模型的提示词"包含三种形态——tools 数组里的
`description`、system prompt 里的**能力段**、system prompt 里的**规则段**。
只抄 `promptGuidelines` 字段会同时漏掉前两者，且抄到的还不是模型看到的渲染结果。

---

## 2. 一轮请求里模型看到的全文

### 2.1 system prompt

由扩展 `before_agent_start` 整轮替换，拼接方式是：

```text
<persona>

<能力段>

<规则段>

<权限段>

<沙盒段>

<强制工具段>
```

- `persona` = `sidecar/prompts/system.zh.md`（构建期编译成常量，见 §3）
- 后面所有段合成 `extra`，用 `\n\n` 连接
- 权限段三分支必中其一，所以**实际请求里 persona 之后一定跟着 extra**；
  「只有 persona」只存在于代码分支，正常流程到不了
- 段落顺序按「稳定 → 易变」排，对上游 prompt cache 友好

示意（工具数量与顺序随能力位变化，这里用省略号）：

```text
你是《戴森球计划》的游戏内助手，由名为 Icarus II 的 mod 实现。
…（人格正文，见 §3）
- 函数列表里没有的能力不要承诺。

本会话注册的工具（以本轮函数列表为唯一权威）：共 28 个
- 游戏只读（16 个）：game_get_overview、game_get_player、…
- 低风险写（不改存档；受限档直接执行，只读档仍会确认）（4 个）：…
- 改存档写（受限/只读档会先请求确认；接管档直接执行）（8 个）：…

工具使用规则（与工具描述同等效力）：
- game_get_overview：
  - 玩家询问当前存档、所在星球、游戏时间或“现在什么情况”时使用。
- game_get_entity：
  - 玩家问某一栋建筑为什么不动、配方是什么、插了什么、有没有电时使用。
  - 如果返回 stale=true，不要使用这条数据：id 已被回收，重新用 game_get_entities 定位。

当前权限为「受限」：镜头/标记/面板/方位/撤销等低风险写操作直接执行；改变存档状态的写操作（改配方、研究、存储、投料等）仍会请求玩家确认。

当前存档未启用游戏沙盒工具。沙盒专属工具不会出现在工具列表中，不要尝试调用。
```

### 2.2 tools 数组

每个模型可见工具贡献一项：

```json
{
  "name": "game_get_entity",
  "description": "按 entityId 读取单个建筑……",
  "parameters": { "type": "object", "properties": { … } }
}
```

`label`（如「建筑详情」）**不进模型上下文**，只给 mod 的 UI 显示用。

### 2.3 工具结果（tool_result）

模型看到的工具返回值也属于提示词的一部分，格式固定：

```text
<display>

<data 的 JSON>
(gameTick=12345, save=xxx, 结果已截断，可用 offset 继续)
```

- `display`：`data.display` 若是字符串，放在 JSON 前面（人话摘要）
- 失败时只有一行：`[<code>] <message>`
- `meta.gameTick` / `meta.saveKey` / `meta.truncated` 会渲染成尾注
- 文本超过 **9000 字符**截断，追加 `…（结果过长，已截断；请用 offset/limit 缩小范围）`
  （`kb_*` 同样是 9000，后缀不同；`run_script` 是 6000）
- 图片不进文本，单独作为 `image` 内容块（`game_capture_view`）

---

## 3. 人格段（persona）

**唯一手写源**：`sidecar/prompts/system.zh.md`
**编译产物**：`sidecar/src/prompts.generated.ts`（`scripts/gen-prompts.mjs` 在 build/typecheck 时生成）

不要在本文件里复制全文——那会制造第二份真相。这里只记**与工具相关的约束**：

`## 使用工具` 一节是通用规则，与自动生成的能力段/规则段互补：

- 能力边界以本轮函数列表为唯一权威；
- 需要连续调用相同工具时用 `run_script`；
- 函数列表里没有的能力不要承诺；
- 不要编造没看到过的信息，不要假装已完成操作。

还有一条硬约束（写在 `persona.ts` 的改动约束里）：**人格里禁止手写工具清单**。
实机踩过：人格仍称"只能读"，模型因此拒绝调用新加的写工具。

**人格不包含**：具体工具名、权限档位、沙盒状态、当前强制工具——这些全部每轮动态追加。

---

## 4. 能力段（自动生成）

**生成器**：`renderCapabilitySection()` / `groupToolsForPrompt()`（`sidecar/src/prompt/sections.ts`）

### 4.1 模板

```text
本会话注册的工具（以本轮函数列表为唯一权威）：共 <N> 个
- <分组标题>（<n> 个）：<工具名>、<工具名>、…
- …
```

标题固定为 `本会话注册的工具（以本轮函数列表为唯一权威）：`，后面直接接 `共 N 个`。

### 4.2 分组（顺序固定）

| 分组标题（原文，含括号内说明） | 成员判定 |
| --- | --- |
| `游戏只读` | `game_*` 且不在 `WRITE_TOOL_NAMES` |
| `低风险写（不改存档；受限档直接执行，只读档仍会确认）` | 写工具中 `writeToolRisk() === 1` |
| `改存档写（受限/只读档会先请求确认；接管档直接执行）` | 写工具中 `writeToolRisk() === 2` |
| `知识库（只读，sidecar 本地数据）` | `kb_*` |
| `Wiki（只读）` | `wiki_*` |
| `蓝图（读/写本地蓝图文件；写入按权限档位确认）` | `blueprint_*` |
| `脚本（默认只读；脚本内写调用需脚本级确认）` | `run_script` |
| `交互（向玩家提问）` | `ask_user` |
| `其他` | 以上都没覆盖到的（兜底，保证不静默丢工具） |

空分组不输出。**分组标题本身就是提示词**：括号里那半句在向模型解释权限语义，
改标题等于改模型行为，不要当成纯 UI 文案。

### 4.3 为什么必须生成

工具集会随 mod 声明的能力变化。手写清单会立刻过期（见 §3 的实机事故）。
按注册表生成还带来一个副作用：**能力集没变时，这段字节完全稳定**。

---

## 5. 规则段（`promptGuidelines` 的渲染结果）

**生成器**：`renderToolRules()`

### 5.1 模板

```text
工具使用规则（与工具描述同等效力）：
- <工具名>：
  - <guideline 1>
  - <guideline 2>
- <工具名>：
  - …
```

- 一个工具一行标题，guideline 用两个空格缩进 + `- `
- 空 guideline 与空数组会被过滤；没有规则的工具不会出现
- 全部工具都没有 guideline 时，整段不输出
- 工具顺序 = 注册顺序（不是字母序）

### 5.2 为什么是"同等效力"

模型对 system prompt 和 tool description 的信任度不一定相同，写这句是为了明确
`promptGuidelines` 里的约束（例如 `stale=true 不要用这条数据`）不是建议，是规则。

### 5.3 写作要求

`promptGuidelines` 是**给模型的指令**，不是给人看的注释。判断标准：

> 这句话如果删掉，模型是否会做一个错误的决定？

会 → 留；不会（解释实现细节、历史原因）→ 删掉或挪到代码注释。
现有写法可以分成四类，都值得保留：

1. **触发条件**：`玩家询问电力是否足够、为什么停电时使用。`
2. **禁令/边界**：`不要一次拉全量。` / `不要把它说成实际产出。`
3. **结果解读**：`statsEmpty=true 时不要下"没有产线"的结论。`
4. **降级路径**：`结果只含实体连接，不含配方流向与星际物流；跨星用 game_get_logistics。`

---

## 6. 权限段

**生成器**：`currentExtraPrompt()` 内联，按 `permissionLevel` 三选一。

固定文本，逐字如下：

**只读（`readonly`）**

```text
当前权限为「只读」：所有写操作执行前都会请求玩家确认。读工具可以直接用；玩家拒绝时不要重试，改成询问意图。
```

**接管（`takeover`）**

```text
当前权限为「接管」：所有写操作直接执行，不再请求确认。执行前仍要确认目标、范围和参数明确；有歧义时先用 ask_user 问清楚，不要猜。
```

**受限（默认，其余取值）**

```text
当前权限为「受限」：镜头/标记/面板/方位/撤销等低风险写操作直接执行；改变存档状态的写操作（改配方、研究、存储、投料等）仍会请求玩家确认。
```

注意三点：

- 这段**只说档位语义**，不列具体工具名——工具名在能力段里，避免两处漂移；
- `takeover` 仍要求先确认目标明确，不是"无脑执行"；
- 这段与能力段的两个写分组标题**语义重叠但都要有**：分组标题解释"这组工具什么风险"，
  权限段解释"当前档位会怎么处理"。删任一处都会让模型少一个判断依据。

---

## 7. 沙盒段

**触发条件**：mod 在 `hello.capabilities` 里声明了 `game.sandbox.v1`。
没声明时**整段不出现**（老 mod 看不到，也不会被误导）。

**启用**

```text
当前存档启用了游戏沙盒工具。工具列表中若有沙盒专属工具，执行前仍需玩家确认。
```

**未启用**

```text
当前存档未启用游戏沙盒工具。沙盒专属工具不会出现在工具列表中，不要尝试调用。
```

沙盒是**每次载入存档**的游戏运行时状态，不是 sidecar 进程设置。

实现状态（截至 M5 前置）：沙盒专属工具 `game_spawn_item` / `game_teleport`
**尚未在 sidecar 注册**，`sandbox.ts` 只保留了名字列表与门禁。因此当前实际行为是：

- `GameToolHost.tools()` 里的 `isSandboxOnlyTool` 过滤是**为将来实现预留**的，
  现在匹配不到任何已注册工具；
- 若脚本或调试直调凭名字调用这两个工具，`GameToolHost.call()` 会以
  `sandbox_required` 拒绝，不会发往 mod——这是当前唯一生效的一层。

实现沙盒工具时要同步三处：`SANDBOX_ONLY_TOOL_NAMES`、`TOOL_CAPABILITY`、
以及本节（能力段分组里它们会落到「改存档写」还是单独成组需要重新决定）。

注意启用文案用的是「工具列表中**若有**沙盒专属工具」这种条件措辞，不是断言。
正因为工具可能缺席（未实现、或未广告给模型）。改成「工具列表中有 …」会在工具
真的不存在时让模型去找一个查不到的工具。

---

## 8. 强制工具段

**触发条件**：玩家在输入框用 `/<tool>` 显式要求调用某个工具（`prompt.requiredTool`）。
**生命周期**：本轮有效；模型一旦真的调用了该工具，`requiredToolSatisfied` 置真，
后续模型轮次不再追加。

**原文**（`forcedToolInstruction(tool)`）：

```text
【本轮强制工具】玩家在输入里用 /<tool> 明确要求必须调用工具 <tool>。
- 这是硬性要求：本轮至少调用一次 <tool>，不要跳过它直接回答。
- **本轮工具列表是唯一权威**：即使历史对话说过"没有这个工具/做不到"，也以本轮提供的函数为准。
- 不要因为工具名字陌生、或历史回答里否定过它，就拒绝调用；先直接发起调用。
- 如果 <tool> 返回错误，如实说明错误，不要编造结果。
- 调用 <tool> 之后，可以继续调用其他工具或直接回答。
```

它被放在**最后**（离用户消息最近），因为这是本轮最强的指令，位置影响遵从度。

---

## 9. 工具 schema 段

`description` 是模型看到的**唯一**工具说明正文，`parameters` 是 JSON Schema。

### 9.1 description 的写法

现有 description 普遍是「做什么 + 关键字段语义 + 什么时候该用」三段式，例如：

```text
读取指定行星（省略则为当前行星）的电网：需求、已供、容量、储能、满足率与充放电。
所有功率单位为 kW，储能为 kWh；玩家问"电够不够"时调用。
```

要求：

- **单位写在 description 里**，不能只写在 docs（模型不读 docs）；
- 枚举取值、默认值、上限也写在这里（JSON Schema 的 `description` 同样可以写，见下）；
- 不要写"见文档"——模型没有文档。

`parameters` 的每个字段都有自己的 `description`，会和 JSON Schema 一起进模型上下文。
分页上限这类硬约束两处都要写（schema 的 `minimum`/`maximum` + 文字说明）：
数值约束是给 provider 校验/呈现用的，不同 provider 对 schema 的序列化方式不同，
不能假设模型一定会从 `maximum: 200` 自己推出「超过会被夹紧」。

### 9.2 `label` 的边界

`label`（`建筑详情`、`存档概览`）**只有 mod UI 用**，不进模型上下文。
给工具起名要同时考虑两件事：模型看到的是 `name` + `description`，玩家看到的是 `label`。

顺带一提：`toolCatalog()` 会把 `description` 截到 160 字符给聊天窗显示。
这只影响 UI 文案，不影响模型——但改 description 时要意识到前半句会被单独看见，
别把关键信息只放在末尾。

---

## 10. 生成、缓存与生效

### 10.1 什么时候重新生成

| 触发 | 影响 |
| --- | --- |
| 每次 `before_agent_start` | 能力段/规则段按当前注册表重算（有缓存，见下） |
| 能力集变化 | `setCapabilities()` 清空工具缓存，下一轮重建 |
| 沙盒开关变化 | `setSandbox()` 清空工具缓存，下一轮重建 |
| 权限档位变化 | 每轮实时读取，无缓存 |
| `requiredTool` 变化 | 每轮实时读取 |

### 10.2 缓存键（重要）

```ts
toolsSignature(tools) = tools.map(t => `${t.name}:${t.promptGuidelines.length}`).join(',')
```

缓存只按 **工具名 + guideline 条数** 判断。这意味着：

- **只改 guideline 的文字、不改条数 → 缓存命中，同一 sidecar 进程内模型仍看到旧规则。**
- 改 `description` 不影响这个缓存（它不进 system prompt），但见 §10.3。

### 10.3 改完提示词怎么让它生效

工具定义（`description` / `parameters`）在**会话创建时**由
`createAgentSession({ customTools: this.buildToolDefinitions() })` 传入并持有；
pi 只在工具注册表刷新时重建。运行期改代码不会热更新。

**可靠做法：重启 sidecar。** 排查时可看日志：

```text
pi 会话已建立 … systemPromptChars=<n>
```

`systemPromptChars` 变化说明人格/工具段落确实换了。若出现
`pi 没有采用自定义 system prompt` 的 warn，说明人格没生效，此时模型看到的是
pi 默认 coding prompt——所有工具段落都白写了。

---

## 11. 代码位置对照

| 内容 | 位置 |
| --- | --- |
| 人格文本 | `sidecar/prompts/system.zh.md` |
| 人格编译产物 | `sidecar/src/prompts.generated.ts` |
| 拼装顺序 | `agent.ts:createPersonaSystemPromptExtension`、`currentExtraPrompt` |
| 能力段 / 规则段 | `sidecar/src/prompt/sections.ts` |
| 工具 schema（`game_*`） | `sidecar/src/tools/registry.ts`（`gameTool()` + spec） |
| 工具 schema（写工具） | `sidecar/src/tools/write.ts` |
| 工具 schema（本地工具） | `kb/tools.ts`、`wiki/tools.ts`、`blueprint/tools.ts`、`tools/ask.ts`、`tools/script.ts` |
| 工具过滤 / 沙盒门禁 | `tools/host.ts`、`tools/sandbox.ts` |
| 能力位登记 | `TOOL_CAPABILITY`（registry.ts）+ 各模块 `CAPABILITY_*` |
| 结果格式化 / 截断 | `registry.ts:formatToolResult`、`buildToolContent` |
| 强制工具原文 | `agent.ts:forcedToolInstruction` |

---

## 12. 校验与调试

### 12.1 离线断言

- `sidecar/test/prompts.mjs`：人格禁词（不能出现 cwd / shell / 代码库等）
- `sidecar/test/pi-tools.mjs`：`systemText.includes('工具使用规则')`

现有断言偏粗。改提示词流程时应补充：

- 能力段按 `groupToolsForPrompt()` 的分组顺序稳定；
- 每个 tool 的 guideline 都出现在渲染结果里；
- 同一工具集两次渲染字节一致（缓存友好性）。

### 12.2 实机确认

1. 重启 sidecar；
2. 看 `pi 会话已建立` 日志的 `systemPromptChars` 与 `tools` 列表；
3. 问一个依赖该规则的问题（例如"这片区域是不是没有产线"用来验证 `statsEmpty` 规则）；
4. 若模型行为没变，先确认是不是 §10.2 的缓存问题，再看是不是被历史对话带了偏。

### 12.3 调试直调

`!tool <name> <json>`（需开 `--debug-tools`）完全绕开 LLM，只验证工具本身。
它**不能**验证提示词——提示词的问题只能在正常 AI 路径上复现。

---

## 13. 已知坑

1. **`promptGuidelines` 差点是死代码。** 整轮替换 system prompt 后，pi 默认分支不再
   渲染它，现在由 `renderToolRules()` 手动渲染。若将来改成不替换 system prompt、
   或绕过 `toolPromptSection()`，这些规则会**静默消失**——模型不会报错，只是不遵守。
   新增工具后务必确认规则段里出现了它的名字。
2. **人格里手写工具清单会导致行为过期。** 已在 M5 踩过：人格仍称"只能读"，
   模型据此拒绝调用写工具。
3. **改 guideline 文字不重启 sidecar 可能不生效**（§10.2 缓存键只看条数）。
4. **分组标题是提示词。** 给分组改名/改括号说明属于行为改动，不是文案改动。
5. **结果截断后缀是提示词。** 9000 字符截断时追加的"请用 offset/limit 缩小范围"
   在教模型怎么续读；删掉它模型会重复拉全量。
6. **`toolNamesForCapabilities()` 的顺序与 `buildToolDefinitions()` 不同**
   （前者 ask→game→kb→wiki→blueprint→script，后者 game→ask→…）。
   这只影响 `ready.payload.tools` 的展示顺序，不影响模型，但写测试时别假设两者一致。

---

## 14. 相关文档

- [index.md](index.md)：**每个模型可见工具**的提示词原文、参数、返回字段、风险等级
- [protocol-v1.md](../protocol-v1.md) 附录 B：协议字段与能力位协商
- [current-status.md](../current-status.md)：各工具的实现与验收状态

单工具文档由 `sidecar/scripts/gen-tool-docs.mts` 从运行期 `ToolDefinition` 生成，
只引用本文、不重复描述拼接机制。改工具的 `description` / `promptGuidelines` 后
必须重新生成，否则 `npm test` 里的 `test:tool-docs` 会失败。
