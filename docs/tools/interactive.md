# 交互与脚本

`ask_user` 停下来问玩家；`run_script` 在 QuickJS-WASM 沙箱里批量调用上面的工具。

共 2 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `ask_user`

**label** 向玩家提问 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
向玩家展示一个或多个带选项的问题，等待玩家在游戏窗口里选择或输入答案。当需要玩家明确偏好、取舍或确认，而现有信息不足以判断时使用。一次最多 8 题，每题最多 4 个选项；选项尽量具体，玩家也可以自定义输入。需要多个答案时把 allowMultiple 设为 true。
```

#### 模型可见 · 使用规则（system prompt）

- 需要玩家明确选择/偏好/确认，且无法从游戏状态或知识库推断时使用。
- 不要问能自己查到或验证的事实；能给出 2–4 个具体选项时优先给选项。
- 一次可以问多个相关问题，UI 会逐题展示并允许玩家返回上一题；提交按钮在非最后一题显示“下一个”。
- 玩家可能跳过问题；收到 skipped=true 时不要重复追问同一题。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `question` | `string` | 否 | — | 单个问题；与 questions 二选一 |
| `questions` | `object[]` | 否 | minItems=1，maxItems=8 | 1–8 个问题；UI 逐题展示 |
| `context` | `string` | 否 | — | 可选上下文 |
| `options` | `object[]` | 否 | maxItems=4 | 单题选项，0–4 个 |
| `allowMultiple` | `boolean` | 否 | — | 是否多选，默认 false |
| `allowFreeform` | `boolean` | 否 | — | 是否允许自定义输入，默认 true |

#### 返回

返回玩家选择的答案；玩家跳过时带 `skipped=true`（不要重复追问同一题）。

---

### `run_script`

**label** 脚本 · **能力位** 无（sidecar 本地工具） · **风险** 1（低风险写，可逆） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
在沙箱里执行一段 JavaScript，用来批量取数、循环、条件判断、聚合，或在确认后批量执行写操作。可用 `dsp.call(工具名, 参数)`（同步）调用当前已注册的 game_* 工具，返回 JSON 字符串；`console.log` 会被收集；脚本要 `return` 一个可 JSON 序列化的结果。脚本包含写工具时，执行前会向玩家弹一次脚本级确认，确认后最多执行 10000 次写调用；没有 fs/net/process/require。
```

#### 模型可见 · 使用规则（system prompt）

- 需要对多个建筑/行星做批量查询、筛选、求和或条件判断时使用；单次查询仍直接用对应工具。
- dsp.call 是同步的，返回 JSON 字符串，需要 JSON.parse；脚本必须 return 一个可序列化的结果。
- 脚本里要写写操作时，必须使用字面量 dsp.call("写工具名", …)，执行前会弹一次脚本级确认；动态拼工具名的写调用会被拒绝。
- 写脚本一次最多 10000 次写调用；仍受 dsp.call 次数（默认 100，最大 10000）与运行时间（默认 30 秒，最大 120 秒）预算约束。
- 不要用 try/catch 包住 dsp.call：它的挂起靠异常实现，被吞掉会导致结果错误；也不要用 Date.now/Math.random（重放会不一致）。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `code` | `string` | 是 | — | 脚本正文。示例：const r = JSON.parse(dsp.call("game_get_power", {})); return {kw: r.data.demandKw}; |
| `timeoutMs` | `integer` | 否 | minimum=100，maximum=120000 | 运行上限，默认 30000ms，最大 120000ms |
| `maxCalls` | `integer` | 否 | minimum=1，maximum=10000 | 工具调用次数上限，默认 100，最大 10000 |

#### 返回

返回脚本最后 `return` 的文本（不超过 6000 字符），不走 `data` 信封。

---
