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
    "baseUrl": null,
    "thinkingLevel": "high"
  }
}
```

- 所有字段**均可选**，只更新给出的字段；未给出的保持不变。
- `apiKey` 只在此方向传输一次；sidecar 持久化后**永不回传**。
- `thinkingLevel` 取值 `off | minimal | low | medium | high | xhigh | max`，与 pi 的
  `ThinkingLevel` 一致。DeepSeek 的 `thinkingLevelMap` 只映射 `high` / `max`，
  其余档位由 pi clamp；`off` 表示**不请求思考**，默认 `high`（开启）。
  非法取值 ⇒ `error{code:"bad_request"}`，不落盘。
- 改档位会让 sidecar 丢弃当前 pi session，在**下一次** prompt 生效；
  会话文件仍在，历史不会丢。

**M2.6 多供应商扩展**（与上面的老字段可同时出现）：

```json
{
  "providerConfig": {
    "openai": {
      "apiKey": "sk-...",
      "baseUrl": null,
      "models": [
        { "id": "gpt-4o", "name": "GPT-4o" },
        { "id": "gpt-4o-mini", "name": "GPT-4o mini" }
      ]
    }
  },
  "removeProviders": ["my-old-proxy"],
  "activeProvider": "openai",
  "activeModel": "gpt-4o"
}
```

- `providerConfig` 的键是 provider id；每个字段都可选：
  - `models`：**整份替换**该供应商的模型列表；至少要有 `id`，`name` 缺省用 `id`。
  - `apiKey`：只在此方向出现；`""` 表示清除，省略表示保持不变。
  - `baseUrl`：`null` 清除覆盖，省略保持不变。
- `removeProviders` 删除供应商（同时删除其 key）。
- `activeProvider` / `activeModel` 切换当前模型；未指定模型时用该供应商的第一个。
- 老的 `provider/model/apiKey/baseUrl` 仍然有效，只作用于**当前**供应商。
- **`apiKey` 永远不会出现在 `config.state` / `ready` / 日志里**；界面只能看到
  `hasKey`，编辑时输入新 key 覆盖旧值。

#### `config.state`（sidecar → mod）

```json
{
  "v": 1, "id": "s-9", "type": "config.state", "replyTo": "m-7",
  "payload": {
    "state": "ready",
    "provider": "deepseek",
    "model": "deepseek-v4-flash",
    "thinkingLevel": "high",
    "message": null,
    "providers": [
      {
        "id": "deepseek",
        "name": "DeepSeek",
        "hasKey": true,
        "baseUrl": null,
        "models": [
          { "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash" },
          { "id": "deepseek-v4-pro", "name": "DeepSeek V4 Pro" }
        ],
        "isActive": true
      }
    ],
    "active": { "provider": "deepseek", "model": "deepseek-v4-flash" }
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
`thinkingLevel` 是新增字段；老服务端不返回时 mod 应保持本地配置值。
`providers` / `active` 是 M2.6 新增字段；老服务端不返回时 mod 回退到
`provider` + `model`，并把它合成一条供应商显示。

#### `model.catalog`（mod → sidecar → mod）

设置页的「获取可用模型」按钮使用。请求：

```json
{ "v": 1, "id": "m-12", "type": "model.catalog",
  "payload": { "provider": "deepseek", "baseUrl": null, "apiKey": null } }
```

- `provider` 必填；`baseUrl` / `apiKey` 可选，**草稿值优先**，用于在保存前探测。
- `apiKey` 只用于本次请求，sidecar 不回传、不落盘、不写日志。

响应（`replyTo` = 请求 id）：

```json
{ "v": 1, "id": "s-12", "type": "model.catalog", "replyTo": "m-12",
  "payload": {
    "provider": "deepseek",
    "source": "catalog",
    "models": [
      { "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash" },
      { "id": "deepseek-v4-pro", "name": "DeepSeek V4 Pro" }
    ]
  } }
```

- `source`：`catalog`（pi 内置目录）或 `openai-compatible`（`GET {baseUrl}/models`）。
- 失败时 `models: []`，`payload.error` 是人类可读的错误（不含 key）。
- 显式给了 `baseUrl` 时只问端点，不会拿官方目录冒充端点模型。

#### `provider.test`（mod → sidecar → mod）

设置页的「检测」按钮使用，只做轻量的 `GET /models` 探测，不发聊天请求、不产生 token 费用。

请求：

```json
{ "v": 1, "id": "m-13", "type": "provider.test",
  "payload": { "provider": "deepseek", "baseUrl": null, "apiKey": null } }
```

响应（`replyTo` = 请求 id）：

```json
{ "v": 1, "id": "s-13", "type": "provider.test", "replyTo": "m-13",
  "payload": {
    "provider": "deepseek",
    "ok": true,
    "status": 200,
    "latencyMs": 123,
    "message": "Key 可用",
    "source": "http"
  } }
```

- `apiKey` 只用于本次探测，sidecar 不回传、不落盘、不写日志。
- 401/403 → `ok:false, status:401/403, message:"API Key 被拒绝（HTTP …）"`。
- 未知供应商需要先填 `baseUrl`；已知供应商会用 pi 目录里该模型自己的 API 地址。
- Anthropic 用 `x-api-key` + `/v1/models`；Google 用 `?key=`；其余按 OpenAI 兼容 Bearer。

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

1. **默认档位必须显式写 `high`。** pi 的 `thinkingLevel` 默认是 `medium`，
   而 DeepSeek 的 `thinkingLevelMap` 把 `minimal`/`low`/`medium` 全设为 `null`，
   clamp 逻辑会**向后找**到 `high` —— 于是"什么都没配"反而变成
   `thinking:{type:"enabled"}` + `reasoning_effort:"high"`，既变慢又变贵。
   M1 起 sidecar 默认显式传 `thinkingLevel: "high"`（默认开启思考）；
   玩家可在聊天窗右下角的「思考」下拉框里选 `off` 关闭或 `max` 提高（见 A.1）。

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
               "existed": true, "messageCount": 2,
               "history": [
                 { "role": "user", "text": "上次的问题", "timestamp": 1760000000000 },
                 { "role": "assistant", "text": "上次的回答" }
               ] }
}
```

- `existed`：该存档此前是否已有对话（`false` = 新建）
- `messageCount`：本次带回的历史消息条数
- `history`：最近最多 40 条 `user` / `assistant` 的可见文本（单条最多 4000 字符），
  只用于 mod **回放聊天窗**，不进入模型上下文；工具调用、工具结果、思考块和
  压缩摘要不会出现在这里。历史读取失败时为空数组，不阻断 `save.bound`。
- `history` 的 `assistant` 消息可带可选的 `cards`（附录 A.9）：该回答产出的蓝图卡片，
  形如 `[{fileName, path, name, ...}]`。老客户端忽略该字段；mod 回放时在回答后重建卡片。

#### 语义

1. 尚未绑定就收到 `prompt` → `error{code:"save_required"}`。
2. 换绑**不打断**进行中的回合；新绑定在回合结束后生效（与 A.1 的 `config.set` 同规则）。
3. 线程在 sidecar 重启后仍可恢复——持久化在数据目录内。
4. `hello.payload.saveKey`（§4.1）在连接时若已有存档则填值。
   实际几乎总是 `null`：mod 在插件加载时就连接，那时还在主菜单，
   真正的绑定靠随后的 `save.bind`。
5. 重复绑定同一个 `saveKey` 必须**幂等**（不重建、不丢历史）。

### A.7 `history.delete` / `history.deleted` — 删除一个问答回合（M4e）

聊天窗的「删除」不能只删 UI：`save.bound.history` 是 sidecar 从会话文件重放的，
只删窗口的话，重进存档又会出现。因此删除必须落到 sidecar 的会话文件上。

#### `history.delete`（mod → sidecar）

```jsonc
{
  "v": 1, "id": "m-51", "type": "history.delete",
  "payload": {
    "saveKey": "3f2a91c4d8e05b17",
    "text": "上次的问题",
    "timestamp": 1760000000000
  }
}
```

- `text`：该回合的**用户消息原文**（必填，用于定位）。
- `timestamp`：可选。历史回放里带回来的毫秒时间戳；有它时优先按时间戳匹配，
  没有时才退化为「最后一条同文本的用户消息」。

#### `history.deleted`（sidecar → mod）

```jsonc
{
  "v": 1, "id": "s-52", "type": "history.deleted",
  "replyTo": "m-51",
  "payload": {
    "saveKey": "3f2a91c4d8e05b17",
    "text": "上次的问题",
    "ok": true,
    "removed": 2,
    "error": null
  }
}
```

- 成功时 `removed` 是删掉的会话条目数（通常 user + assistant，可能含工具消息）。
- 失败时 `ok=false`，`error` 为 `busy`（正在回答）、`no_save`、`turn_not_found`、
  `unsupported_session`（会话含压缩/分支，拒绝改写）、`io_error` 之一。
- 成功后 sidecar 会**丢弃当前内存会话**，下一次 `prompt` 从改写后的文件重建上下文，
  因此被删回合不会继续进入模型上下文。
- 实现是线性会话重写：保留条目按原顺序重新串成 `parentId` 链；
  含 `compaction` / `branch_summary` 的会话拒绝删除（避免破坏上下文树）。

### A.8 `tool_activity` — sidecar 本地工具的可视化（M4f）

`kb_*` 与 `run_script` 在 sidecar 内执行，不经过 `tool_call`，因此默认在聊天窗里
**看不到任何卡片**——玩家会误以为模型没有用计算器。这条消息只用于 UI 展示，
不参与工具执行，也不改变模型上下文。

```jsonc
{
  "v": 1, "id": "s-70", "type": "tool_activity",
  "payload": {
    "callId": "kb_plan-3f9a",
    "phase": "start",           // start | end
    "name": "kb_plan",
    "args": "{\"item\":\"宇宙矩阵\",\"rate\":20}",
    "ok": true,                 // 仅 phase=end
    "summary": "宇宙矩阵 20/min 机器：…",
    "detail": "…"               // 可展开正文，服务端截断
  }
}
```

- 同一次工具调用 `callId` 相同；mod 用它在 `start` 建卡、`end` 原地更新。
- mod 必须容忍：只收到 `end`（补建卡）、`name` 未知（显示原名）、字段缺失。
- 只对**模型可见**的本地工具发送；知识库导出等 internal 调用不发。

### A.9 `blueprint_card` — 蓝图卡片（sidecar → mod）

生成 / 下载 / 复制蓝图成功后，sidecar 在**本轮收尾**时（`message{final}` 之后、
`status{idle}` 之前）发一条或多条 `blueprint_card`。mod 把卡片追加到对话末尾，
卡片上提供「查看详情」与「粘贴铺设」两个动作。

```jsonc
{
  "v": 1, "id": "s-80", "type": "blueprint_card",
  "payload": {
    "path": "/home/me/Documents/Dyson Sphere Program/Blueprint/电磁涡轮 20.txt",
    "fileName": "电磁涡轮 20.txt",
    "name": "电磁涡轮 20",
    "item": "电磁涡轮",
    "itemId": 1206,
    "rate": 20,
    "buildings": 24,
    "width": 12,
    "height": 5,
    "author": "自动生成",
    "version": "0.10.35.29151",
    "attributes": "",
    "description": "由 dsp-calc 规划器生成",
    "source": "generated",
    "license": "CC BY-NC-SA 4.0",
    "repository": "https://github.com/DSPBluePrints/FactoryBluePrints"
  }
}
```

字段约定：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `fileName` | 是 | 蓝图文件名（含 `.txt`） |
| `path` | 否 | sidecar 侧的绝对路径；**不保证** mod 能直接打开（Proton/Wine 路径可能不同） |
| `name` | 否 | 蓝图标题（header 的 `shortDesc`） |
| `item` / `itemId` / `rate` | 否 | 产物与目标产量（`blueprint_generate` 才有） |
| `buildings` / `width` / `height` | 否 | 建筑数与占地（从蓝图二进制读出） |
| `author` / `version` / `attributes` / `description` | 否 | 蓝图 header 元数据 |
| `source` | 否 | `"generated"` \| `"downloaded"` \| `"copied"`，用于卡片角标 |
| `license` / `repository` | 否 | 下载来源的许可与仓库，卡片可据此署名 |

- 一次工具调用成功 → 一张卡片；一轮里调用多次就发多条，顺序与工具完成顺序一致。
- 工具失败不发卡片。
- mod 必须容忍字段缺失；`path` 打不开时应回退到
  `GameConfig.blueprintFolder + fileName` 再尝试。
- sidecar 会把卡片按「用户提问原文」持久化，并在后续 `save.bound.history` 的
  assistant 消息上以可选 `cards` 字段带回；mod 回放时在回答后重建卡片。
  记录损坏/找不到对应回合时只是不回放，不影响历史文本。

---

## 附录 B：只读游戏工具（M2）与知识库工具（M3）

M2 新增 `game.read.v1` 能力。mod 在 `hello.capabilities` 中声明该能力后，
sidecar 才会注册并广告 `game_get_*` 工具；未声明时 `ready.payload.tools` **只含 M3 的
`kb_*` 本地工具**（它们不依赖 mod 能力）。

### B.1 工具名

模型可见的工具名使用下划线（OpenAI 兼容端点会把非 `[a-zA-Z0-9_-]` 替换成 `_`）：

工具按**能力**分组：mod 在 `hello.capabilities` 里声明了哪一组，sidecar 才注册并广告哪一组。
老版本 mod 只声明 `game.read.v1` 时，看不到后来新增的工具（避免 `tool_not_found`）。

**`game.read.v1`（M2）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_get_overview` | 存档、时间、当前星球/星系、玩家坐标 |
| `game_get_player` | 机甲能量/护盾、背包 |
| `game_get_planet` | 行星矿脉汇总、建筑计数 |
| `game_get_power` | 电网需求/供给/容量/储能 |
| `game_get_research` | 当前研究、进度、队列 |
| `game_get_production` | 按物品聚合的产量/消耗/库存 |

**`game.entity.v1`（M4a）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_get_entity` | 单个建筑详情：原型、配方、产物/消耗、功率、分拣器/传送带/仓储/物流塔、坐标与朝向 |
| `game_render_layout` | 区域建筑字符布局图（上=+Z，左=-X） |
| `game_get_planets` | 银河行星一览；矿脉按宇宙探索等级披露 |
| `game_get_logistics` | 跨行星物流塔供给/需求汇总 |
| `game_trace_line` | 沿传送带/分拣器追踪产线拓扑 |
| `game_get_entities` | 行星内建筑分页列表；可按 `protoId` / `protoName` / `recipeId` / 区域过滤 |

**`game.analyze.v1`（M4c）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_analyze_area` | 按空间范围聚合产线：建筑/配方计数、装机产能、耗电、物流设施、涉及电网 |

**`game.progress.v1`（M4a）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_get_progress` | 玩家进度：派生阶段、已解锁科技、里程碑、教程、研究队列 |

**`game.view.v1`（M4a）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_capture_view` | 截取当前画面（JPEG，可带可见实体的屏幕坐标），图片走 `tool_result.image` |

> `game_capture_view` 只截**游戏层**：`GameCamera.CaptureScreenshot` 把 `cullingMask` 设为
> `gameLayerMask`，UGUI 不进画面，因此不需要额外的"隐藏界面"开关。

### B.1.1 M3 知识库工具（sidecar 本地）

| 模型名 | 说明 |
| --- | --- |
| `kb_search` | 在物品/配方/科技/建筑/矿脉/提示/教程/里程碑/指引/术语中搜索 |
| `kb_get` | 按 kind + id/名称/SID 精确读取一条 |
| `kb_recipe_tree` | 递归展开主配方，计算直接输入、原料总量、副产物 |
| `kb_plan` | 按目标产量计算机器数、电力、传送带；原料复用配方树（M4b） |

- 这几个工具**始终注册**；知识库尚未建立时执行返回 `kb_unavailable`，
  不会让模型“看不到工具”而凭记忆编造。
- 它们不出现在 `tool_call` 帧里（sidecar 本地执行），因此游戏内不会出现对应的工具卡；
  结果作为 pi 的工具结果回填。
- 知识库的数据来源与 schema 见 [docs/kb-v1.md](kb-v1.md)。

### B.1.3 Wiki 与蓝图（M4g/M4h，sidecar 本地）

| 模型名 | 说明 |
| --- | --- |
| `wiki_search` | 搜索中/英文 DSP Wiki（MediaWiki API），返回标题/摘要/链接 |
| `wiki_page` | 读取 Wiki 页面纯文本（自动重定向，≤8000 字） |
| `blueprint_search` | 在线搜索 DSPBluePrints/FactoryBluePrints 的 `.txt` 蓝图路径（不打包内容） |
| `blueprint_info` | 读取蓝图并做格式体检（header/base64+gzip/md5），带来源与许可 |
| `blueprint_save` | 保存到游戏蓝图目录（Documents/Dyson Sphere Program/Blueprint），同名自动加序号 |

- 全部由 sidecar 直接访问外网；失败显式返回 `wiki_unavailable` / `blueprint_unavailable`，
  不允许用模型记忆补内容。
- 蓝图内容许可 **CC BY-NC-SA 4.0**：结果必须带 repository 与许可，回答需署名；
  本仓库不缓存/分发蓝图内容。
- Wiki 内容版权以各站点页面声明为准，回答需附 `url`。

### B.1.2 脚本工具（M4d，sidecar 本地）

| 模型名 | 参数 | 说明 |
| --- | --- | --- |
| `run_script` | `code`, `timeoutMs?`, `maxCalls?` | 在 QuickJS-WASM 沙箱里执行只读脚本；`dsp.call(name, args)` **同步**调用已注册的 `game_*` 工具 |

- 沙箱里没有 `process` / `require` / `fetch` / 文件系统；能力面只有注入的 `dsp.call`。
- `dsp.call` 走同一个 `GameToolHost`，白名单来自当前连接的 `host.tools()`，
  因此自动继承 risk / 路由 / 超时；v1 只允许只读工具。
- 预算：`maxCalls` ≤ 50、`timeoutMs` ≤ 10s、返回 ≤ 6KB；超时由 QuickJS 中断处理器打断。
- **协议层没有新帧**：脚本完全在 sidecar 内执行，`dsp.call` 复用既有的 `tool_call`/`tool_result`，
  因此游戏内仍会出现这些调用的工具卡（`internal=false`）。
- 实现细节：同步变体 + "挂起 → 宿主执行 → 重放"（memo 已完成的调用），
  所以脚本必须可重放，不要用 `Date.now` / `Math.random`。

工具结果中的物品/建筑/科技可带 `iconTag`（游戏图标短码，不含反斜杠与分号）；
模型在正文里写成 `\tag;`。聊天窗必须使用 `Configs.builtin.generalIconMapping` 才能渲染这些短码。

> 游戏自带的 UGUI 改版没有实现 `<b>`；`IcarusII` 在写入 `Text` 前会把 `<b>...</b>` 转成受支持的亮白 `<color=...>...</color>`。

### B.2 `tool_call`（sidecar → mod）

```jsonc
{
  "v": 1, "id": "s-31", "type": "tool_call",
  "payload": {
    "callId": "call_7",
    "name": "game_get_power",
    "args": { "planetId": 3 },
    "risk": 0,
    "timeoutMs": 8000,
    "internal": false
  }
}
```

- `callId` 由 sidecar 生成，单连接内唯一；mod 必须在 `tool_result.replyTo` 原样回传。
- `internal` 可选，缺省 `false`。为 `true` 时表示 sidecar 内部调用（M3 知识库导出）：
  mod 应正常执行并回 `tool_result`，但**不要**显示工具卡、不要改状态栏。
- `timeoutMs` 由 sidecar 钳制在 500–30000ms；超时后 sidecar 不再接受该 `callId` 的结果。
- M2 的 `risk` 恒为 0；写工具（M4）才使用 1/2。
- 同一连接同一时刻最多一个 `game.*` 调用（sidecar 串行化）。

### B.3 `tool_result`（mod → sidecar）

```jsonc
{
  "v": 1, "id": "m-41", "type": "tool_result",
  "replyTo": "call_7",
  "payload": {
    "callId": "call_7",
    "ok": true,
    "data": { "demandKw": 1234.5, "display": "需求 1.23 MW · 满足 92%" },
    "image": null,
    "error": null,
    "meta": {
      "tool": "game_get_power",
      "gameTick": 123456,
      "saveKey": "3f2a91c4d8e05b17",
      "tickAtStart": 123450,
      "tickAtEnd": 123456,
      "elapsedMs": 18,
      "truncated": false
    }
  }
}
```

- `ok=false` 时 `data` 为 `null`，`error` 为 `{code, message}`。
- `meta` 必带 `gameTick`、`saveKey`、`tickAtStart`、`tickAtEnd`；列表截断时带
  `truncated:true` 与 `nextOffset`。
- 错误码：`tool_not_found`、`tool_unavailable`、`not_in_game`、`invalid_args`、
  `timeout`、`busy`、`internal`、`cancelled`。
- 断线、换档、退游戏时 sidecar 立即以 `tool_unavailable` / `not_in_game` 结束 pending 调用，
  不等待 mod 回复。

#### `image`（可选，M4a）

只由 `game_capture_view` 产生。字段：

```jsonc
"image": {
  "mimeType": "image/jpeg",
  "data": "<base64，不含前缀>",
  "width": 1280,
  "height": 720
}
```

- 只在 `ok=true` 时出现；`data` 解码后 ≤ **2 MiB**，整帧仍受 §1 的 8 MiB 限制。
- **图片不进 `data`**：`data` 保持纯 JSON，sidecar 把 `image` 转成 pi 的 `ImageContent`，
  由 provider 适配层决定怎么发（`openai-completions` 会补一条带 `image_url` 的 `user` 消息）。
- 模型 `input` 不含 `image` 时，sidecar 丢弃图片并在文本里注明「已截图，但当前模型不支持看图」，
  不让结果凭空消失。
- 编码约定：JPEG quality 80、长边 ≤ 1280；`width`/`height` 是实际像素。

### B.4 单位约定（M2 定案）

| 量 | 单位 |
| --- | --- |
| 电力 | `kW`（`PowerNetwork` 每 tick 值 × 60 ÷ 1000） |
| 储能 | `kWh`（J ÷ 3.6e6） |
| 产线 | `/min`（`ProductStat.total[1]`） |
| 时间 | 秒 |
| 比例 | 0–100 |

每个数值字段带单位后缀；关键结果另附 `display` 字符串，供模型直接引用。

### B.5 内部导出工具（M3，模型不可见）

这两个工具只由 sidecar 在 `save.bind` 后调用（`internal: true`），用于建立本地知识库；
不注册给模型。

| 工具 | 参数 | 返回 |
| --- | --- | --- |
| `game_export_prototypes` | `set`（items/recipes/techs/models/veins/signals）、`offset`、`limit`、`locale?` | `{schemaVersion, gameVersion, locale, set, offset, limit, total, done, rows[]}` |
| `game_export_text` | `offset`、`limit` | `{schemaVersion, gameVersion, locale, offset, limit, total, done, rows[]}` |

- 按 `offset/limit` 分块；`done=true` 表示该集合已到末尾。
- mod 侧按主线程预算分帧序列化；sidecar 串行调用，缓存到 `%APPDATA%/icarus-ii/kb/`。
- `game_export_text` 失败时 sidecar 保留结构化知识库（M3a），只记录 warn；
  `game_export_prototypes` 失败则知识库整体不可用，`kb_*` 返回 `kb_unavailable`。
