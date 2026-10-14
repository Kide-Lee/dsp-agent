# icarus-ii 通信协议 v1

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

---

## 附录 A：v1 增量扩展（M1 — 对话与配置）

以下全部为**增量**变更：新类型对旧客户端不可见（按 §2「未知类型必须忽略」），
`ready` 仅新增可选字段。因此协议版本 `v` **仍为 1**。

### A.1 新增消息类型

#### `config.set`（mod → sidecar）

```json
{
  "v": 1, "id": "m-7", "type": "config.set",
  "payload": {
    "provider": "deepseek",
    "model": "deepseek-v4-flash",
    "apiKey": "sk-...",
    "baseUrl": null
  }
}
```

- 所有字段**均可选**，只更新给出的字段；未给出的保持不变。
- `apiKey` 只在此方向传输一次；sidecar 持久化后**永不回传**。

#### `config.state`（sidecar → mod）

```json
{
  "v": 1, "id": "s-9", "type": "config.state", "replyTo": "m-7",
  "payload": {
    "state": "ready",
    "provider": "deepseek",
    "model": "deepseek-v4-flash",
    "message": null
  }
}
```

`state` 取值：

| 值 | 含义 | mod 应做 |
| --- | --- | --- |
| `ready` | 已配置且可用 | 正常对话 |
| `missing_key` | 尚无凭证 | 显示首次引导，进入密钥录入态 |
| `invalid_key` | 凭证被上游拒绝（401/403） | 提示重新录入 |
| `unreachable` | 无法连通上游 | 提示网络问题，可重试 |

**`config.state` 的任何字段都不得包含 `apiKey`。**

### A.2 `ready` 新增可选字段

```json
{
  "v": 1, "id": "s-1", "type": "ready",
  "payload": {
    "protocol": 1,
    "sessionId": "sess_...",
    "agent": { "provider": "deepseek", "model": "deepseek-v4-flash", "available": true },
    "config": {
      "state": "missing_key",
      "provider": "deepseek",
      "model": "deepseek-v4-flash"
    }
  }
}
```

旧客户端忽略 `config` 字段，行为不变。mod 可据此在**第一次握手**就知道要不要进引导态，
无需额外往返。

> `agent` 是**对象**（与 §4.2 一致），不是字符串。实现时曾把这里写成
> `"agent": "pi/deepseek-v4-flash"`，与 C# 客户端的 `payload["agent"]["model"]` 冲突，
> 会导致 mod 显示不出模型名。以 §4.2 为准。

### A.3 `error` 新增 code

| code | 触发条件 |
| --- | --- |
| `invalid_key` | 上游对凭证返回 401 / 403 |
| `upstream_error` | 其它上游错误；`payload.message` 携带**已脱敏**的可读信息 |
| `rate_limited` | 上游限流 |

### A.4 安全要求（实现必须满足）

1. `apiKey` **只允许**出现在 `config.set` 的入站载荷中。
   任何出站消息（含 `error`、`status`、日志）都不得包含它或它的任何子串。
2. sidecar 写日志时必须脱敏：最多打印前 4 位与总长度，例如 `sk-a…(48)`。
3. 配置落盘权限：Linux 下 `0600`；Windows 下依赖 `%APPDATA%` 自身的 ACL。
4. mod 侧输入密钥时输入框应为**密码态**（不回显明文）。

### A.5 实现注意（M1 实测，踩过的坑）

以下几条都是**读类型定义看不出来、只有实跑才会暴露**的行为，写在这里避免重复踩：

1. **必须显式关掉思考档位。** pi 的 `thinkingLevel` 默认是 `medium`，
   而 DeepSeek 的 `thinkingLevelMap` 把 `minimal`/`low`/`medium` 全设为 `null`，
   clamp 逻辑会**向后找**到 `high` —— 于是"什么都没配"反而变成
   `thinking:{type:"enabled"}` + `reasoning_effort:"high"`，既变慢又变贵。
   M1 必须显式传 `thinkingLevel: "off"`。

2. **凭证注入不能走 `ModelRuntime.create({ apiKey })`** —— 该选项不存在。
   带 `apiKey` 的类型是 `ModelRuntimeAuthOverrides`，只被 `getAuth()` 接受；
   `setRuntimeApiKey()` 存在但内部会触发**联网**刷新模型目录。
   正确做法：自建内存 `CredentialStore` 传给 `create({ credentials })`，
   并显式 `allowModelNetwork: false`。

3. **即使传了 `credentials`，`modelsPath` 仍默认 `~/.pi/agent/models.json`。**
   要真正隔离用户环境，必须**同时**显式传 `authPath` 与 `modelsPath`
   （指向 sidecar 数据目录），否则会读写用户的全局 pi 目录。

4. **`AgentSession.prompt()` 在上游出错时不会 reject**，而是以合成助手消息
   （`stopReason:"error"` + `errorMessage`）经事件送达。
   因此错误分类必须读事件载荷，不能靠 catch。

5. **文本增量不在 `AgentEvent` 上**，而在 `message_update.assistantMessageEvent`
   （pi-ai 的 `AssistantMessageEvent`：`text_delta` / `thinking_delta` / `error` / `done`）。

6. **pi 包是 ESM-only**（exports 只有 `import`），`require()` 会直接
   `ERR_PACKAGE_PATH_NOT_EXPORTED`。sidecar 因此整体转为 ESM
   （`"type":"module"` + `nodenext`）。已实测 esbuild `--format=cjs` 单文件
   打包与 Node SEA 注入不受影响。

### A.6 `save.bind` / `save.bound` — 一个存档绑定一个对话串

**需求**：对话上下文跟随存档。玩家换存档 → 换对话；回到主菜单 → 解绑；
再次进入同一存档 → 恢复该存档此前的上下文。

#### `save.bind`（mod → sidecar）

```json
{
  "v": 1, "id": "m-11", "type": "save.bind",
  "payload": { "saveKey": "3f2a91c4d8e05b17", "saveName": "我的档" }
}
```

- `saveKey`：存档的**稳定标识**——同一个存档每次读取都必须得到同一个值。
  mod 由 `GameDesc.creationTime` 与 `GameDesc.seedKey64` 经 FNV-1a 64 派生为 16 位十六进制串。
  **刻意不包含 `GameData.gameName`**：那是玩家可改的名字，
  一旦改名就会把已有对话孤立成新线程。
- `saveKey: null`：当前没有加载存档（主菜单）→ 解绑，但**不销毁**线程。
- `saveName`：仅用于显示。

#### `save.bound`（sidecar → mod）

```json
{
  "v": 1, "id": "s-12", "type": "save.bound", "replyTo": "m-11",
  "payload": { "saveKey": "3f2a91c4d8e05b17", "saveName": "我的档",
               "existed": true, "messageCount": 12 }
}
```

- `existed`：该存档此前是否已有对话（`false` = 新建）
- `messageCount`：恢复的历史消息条数

#### 语义

1. 尚未绑定就收到 `prompt` → `error{code:"save_required"}`。
2. 换绑**不打断**进行中的回合；新绑定在回合结束后生效（与 A.1 的 `config.set` 同规则）。
3. 线程在 sidecar 重启后仍可恢复——持久化在数据目录内。
4. `hello.payload.saveKey`（§4.1）在连接时若已有存档则填值。
   实际几乎总是 `null`：mod 在插件加载时就连接，那时还在主菜单，
   真正的绑定靠随后的 `save.bind`。
5. 重复绑定同一个 `saveKey` 必须**幂等**（不重建、不丢历史）。
