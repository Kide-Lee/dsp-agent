# dsp-agent 通信协议 v1

> mod（游戏进程内，C#）与 sidecar（本地 Node 进程）之间的契约。
> 本文件是**唯一事实来源**：两侧实现都必须以此为准，任何变更先改本文件再改代码。

## 1. 传输层

| 项 | 约定 |
| --- | --- |
| 传输 | WebSocket（RFC 6455），文本帧 |
| 地址 | `ws://127.0.0.1:<port>`，默认 `port = 8765` |
| 帧内容 | 单个 UTF-8 JSON 对象（`JsonValue` 层面的 object） |
| 方向 | sidecar 是**服务端**，mod 是**客户端**（见 design.md §3.3） |
| 心跳 | 传输层 WebSocket ping/pong（mod 侧 `KeepAliveInterval = 20s`）；另有应用层 `ping`/`pong` 用于链路自检 |
| 鉴权 | 首帧 `hello` 必须携带 `token`（sidecar 启动时生成、写入 `runtime.json`，仅本机可读）；不匹配则 `error` + 关闭 |
| 最大帧 | 8 MiB（超出则 `error{code:"frame_too_large"}` 并关闭） |

## 2. 信封

所有应用层消息使用统一信封：

```jsonc
{
  "v": 1,                    // 协议版本（int）
  "id": "m-17",              // 发送方生成的唯一消息 ID（string）
  "type": "prompt",          // 消息类型（string）
  "replyTo": "c-3",          // 可选：应答的目标消息 id
  "payload": { }             // 类型相关的对象；无负载时为 {}
}
```

- `id` 只需在**单个连接生命周期内**唯一。约定前缀：mod 用 `m-<n>`，sidecar 用 `s-<n>`。
- 未知 `type` 必须被**忽略**（前向兼容），不得断开连接。
- 未知 `payload` 字段必须被忽略。

## 3. 版本协商

1. mod 连接后立刻发送 `hello`。
2. sidecar 校验 `v`、`token` 后回复 `ready`；不通过则回复 `error` 并关闭连接（close code `1008`）。
3. 版本不匹配时：sidecar 在 `error.payload.code = "protocol_mismatch"`，并附带 `supported: [1]`。

## 4. 消息类型

### 4.1 `hello` — mod → sidecar

```jsonc
{
  "v": 1, "id": "m-1", "type": "hello",
  "payload": {
    "token": "…",                 // string，必填
    "modVersion": "0.1.0",        // string
    "gameVersion": "0.10.30.3100",// string
    "gameLanguage": "zh-CN",      // string，来自 Localization
    "capabilities": ["ui.chat"],  // string[]，mod 侧已实现的工具域
    "saveKey": "a1b2c3…"          // string|null，当前存档指纹；未进入存档时为 null
  }
}
```

### 4.2 `ready` — sidecar → mod

```jsonc
{
  "v": 1, "id": "s-1", "type": "ready",
  "payload": {
    "serverVersion": "0.1.0",
    "protocol": 1,
    "sessionId": "sess_…",
    "agent": { "provider": "deepseek", "model": "deepseek-chat", "available": true },
    "tools": ["game.get_overview", "…"]   // 本次连接注册的工具名
  }
}
```

### 4.3 `status` — sidecar → mod

驱动聊天窗口的状态指示。

```jsonc
{ "v":1, "id":"s-2", "type":"status",
  "payload": { "state": "idle", "detail": "…" } }
```

`state` 取值：`"idle"` | `"thinking"` | `"tools"` | `"retrying"` | `"compacting"` | `"aborting"`

### 4.4 `prompt` — mod → sidecar

```jsonc
{ "v":1, "id":"m-2", "type":"prompt",
  "payload": { "text": "怎么提高产量？", "conversationId": "sess_…" } }
```

`conversationId` 可选；缺省表示使用连接当前会话。

### 4.5 `stream` — sidecar → mod

增量文本，用于逐字显示。

```jsonc
{ "v":1, "id":"s-3", "type":"stream", "replyTo":"m-2",
  "payload": { "channel": "text", "delta": "可以" } }
```

`channel`：`"text"` | `"reasoning"`

### 4.6 `message` — sidecar → mod

一条完整消息（回合结束或工具卡片）。M0 只用到 `role:"assistant"` 的纯文本。

```jsonc
{ "v":1, "id":"s-4", "type":"message", "replyTo":"m-2",
  "payload": {
    "role": "assistant",
    "final": true,
    "content": [ { "type": "text", "text": "…" } ]
  } }
```

`content` 元素类型：M0 仅 `{"type":"text","text":…}`；M1+ 增加
`{"type":"tool","callId":…,"name":…,"state":"running|ok|error","summary":…}`。

### 4.7 `ping` / `pong` — 双向

```jsonc
{ "v":1, "id":"m-9", "type":"ping", "payload": { "t": 1733234567890 } }
{ "v":1, "id":"s-9", "type":"pong", "replyTo":"m-9", "payload": { "t": 1733234567890 } }
```

收到 `ping` 必须回 `pong`，并原样回传 `t`。

### 4.8 `error` — 双向

```jsonc
{ "v":1, "id":"s-5", "type":"error", "replyTo":"m-2",
  "payload": { "code": "agent_unavailable", "message": "…", "retryable": true } }
```

| code | 含义 |
| --- | --- |
| `protocol_mismatch` | 协议版本不受支持 |
| `unauthorized` | `token` 不匹配 |
| `frame_too_large` | 帧超过上限 |
| `bad_request` | 信封或 payload 结构非法 |
| `agent_unavailable` | LLM/模型不可用（M1+） |
| `tool_failed` | 工具执行失败（M1+） |
| `internal` | 未分类内部错误 |

### 4.9 预留（M0 不实现，仅占位以免 v1 被破坏）

`abort`（mod → sidecar）、`tool_call` / `tool_result`（双向）、`confirm_request` / `confirm_result`（双向）、`game_event`（mod → sidecar）、`ui_event`（mod → sidecar）、`session`（sidecar → mod）。
字段定义见 design.md §5.3；实现出现在 M1/M4。

## 5. M0 的最小闭环

```
mod                                   sidecar
 │  ── hello ──────────────────────►  │  校验 token / v
 │  ◄──────────────────── ready ────  │
 │  ── ping{t} ────────────────────►  │
 │  ◄─────────────── pong{t} ───────  │
 │  ── prompt{"hello"} ────────────►  │  M0 为 echo agent
 │  ◄────────────── status{thinking}  │
 │  ◄──────── stream{text,"echo: "}   │
 │  ◄──────── stream{text,"hello"}    │
 │  ◄────────── message{final:true}   │
 │  ◄──────────────── status{idle}    │
```

## 6. 关闭语义

| 场景 | 行为 |
| --- | --- |
| mod 主动退出 | 发 `close`（WebSocket close，code `1000`） |
| sidecar 退出 | 关闭所有连接（code `1001`） |
| `token` 不匹配 | `error{unauthorized}` + close `1008` |
| 协议不匹配 | `error{protocol_mismatch}` + close `1008` |
| 心跳超时 | 由 WebSocket 传输层判定；mod 侧进入重连退避 |

## 7. 兼容性规则

1. **加字段**：在同版本内允许（接收方必须忽略未知字段）。
2. **加消息类型**：同版本内允许（接收方必须忽略未知 `type`）。
3. **改字段语义 / 删字段**：必须提升 `v`。
4. sidecar 与 mod 的版本可以独立演进，但都必须声明并校验 `v`。
