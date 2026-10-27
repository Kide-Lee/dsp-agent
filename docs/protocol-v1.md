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
    "gameVersion": "0.10.35.29151",// string
    "gameLanguage": "zh-CN",      // string，来自 Localization
    "capabilities": ["ui.chat"],  // string[]，mod 侧已实现的工具域/UI 能力
    "saveKey": "a1b2c3…"          // string|null，当前存档指纹；未进入存档时为 null
  }
}
```

`capabilities` 里的未知项必须被忽略。除工具域外，当前还有：

- `ui.richtext.v2`：mod 的输出渲染支持 Markdown + 小型 HTML 子集（`color/size/tint/box/sqrt/sup/sub/i/a/icon`）。sidecar 可据此选用新版格式提示词；没有该能力时继续用旧的“只用游戏标签”提示。
- `ui.ask.v1`：mod 支持 `ask_user` 交互问答（玩家在游戏窗口里选择/输入答案）。sidecar 据此注册问答工具；老 mod 不会看到该工具。
- `game.write.v1`：mod 支持写操作确认与一次性令牌；sidecar 据此注册 `game_focus_camera` / `game_place_marker` / `game_open_panel` / `game_set_research` / `game_undo_last_write`。老 mod 不会看到这些工具。
- `game.sandbox.v1`：mod 支持沙盒门禁：会在 `save.bind.sandbox` 上报本次载入是否启用游戏沙盒工具，
  并在 mod 侧拒绝沙盒专属工具。sidecar 据此在非沙盒时隐藏/拒绝沙盒专属工具；老 mod 不声明时
  一律按“非沙盒”处理。当前预留的沙盒专属名为 `game_spawn_item` / `game_teleport`（尚未实现）。
- `game.drop.v1`：mod 支持 `game_drop_item`（把背包物品丢到地面）。新写工具用独立能力串，
  避免老 mod 被注册它并不存在的工具。
- `game.build.v1`：mod 支持生产建筑写操作：`game_set_recipe`（单台改配方）、
  `game_set_assembler_batch`（最多 20 台，一次确认、一层撤销）、`game_take_output`（取成品）、
  `game_feed_input`（背包原料直接投料）。改配方前会把机器内原料/产物退回背包。
- `script.write.v1`：mod 支持受控写脚本：`run_script` 检测到字面量写工具时，先弹一次脚本级确认，
  mod 发放短期脚本租约（token + 脚本哈希 + 写工具名 + 最多 10000 次），脚本结束立即释放。
- `game.storage.v1`：mod 支持存储建筑写操作：`game_storage_set_filter`、`game_storage_set_limit`、
  `game_storage_move`（存储间转移）、`game_storage_deposit`（背包→存储）、`game_storage_withdraw`（存储→背包）。
- `game.tether.v1`：mod 支持方位指示：`game_tether_add` / `game_tether_list` / `game_tether_clear`。
  可同时建立多个（最多 20 个）：本星球每个建筑一条 `LineGizmo`；跨行星用原生
  `navigation.indicatorAstroId` 指向其中一个目标行星（游戏一次只支持一个朝向），落地后该行星上的建筑切换为自绘线。
- `game.todo.v1`：mod 支持原生 GoalProto 目标注入与读取：`game_todo_templates` /
  `game_todo_list` / `game_todo_add` / `game_todo_remove`。目标直接进入原生
  `LDB.goals` / `GoalSystem` / `UIGoalPanel`；完成判定复用原生 `GoalDeterminator`。
  AI 目标以 `EGoalLevel.Off` 注入并插到目标队列队首（原生面板最多显示 2 个分组），
  注入后立即补跑判断器的 `DetermineOnPatch` / `DetermineOnLoad`，不受原生
  `GoalLogic.CheckLogic` 的显示条件影响。玩家把“目标建议”选成“自由探索”
  (`EGoalLevel.Off`) 时原生面板仍不会展示目标；`game_todo_add` / `game_todo_list`
  会返回 `goalPanelOff=true`，由模型提醒玩家打开目标设置，改为“关键节点”或
  “事无巨细”。

### 4.2 `ready` — sidecar → mod

```jsonc
{
  "v": 1, "id": "s-1", "type": "ready",
  "payload": {
    "serverVersion": "0.1.0",
    "protocol": 1,
    "sessionId": "sess_…",
    "agent": { "provider": "deepseek", "model": "deepseek-chat", "available": true },
    "tools": ["game.get_overview", "…"],  // 本次连接注册的工具名
    // 可选（M4l）：工具目录，供聊天窗的“/ 工具列表”展示。老 mod 忽略。
    "toolCatalog": [
      {
        "name": "game_get_overview",        // 模型可见名（也是 / 命令名）
        "label": "存档概览",                 // 显示名
        "description": "读取当前存档、星球、时间与玩家位置的概览",
        "debuggable": true                  // 可选（M4m）：!tool 调试直调是否允许
      }
    ]
  }
}
```

`toolCatalog` 是 `tools` 的**可读元数据**，不改变 `tools` 的既有语义；
老服务端不返回时，mod 回退到 `tools` + 本地 `ToolNames.Label` 显示。

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
  "payload": {
    "text": "怎么提高产量？",
    "conversationId": "sess_…",
    "requiredTool": "game_get_production"   // 可选（M4l）
  } }
```

`conversationId` 可选；缺省表示使用连接当前会话。

`requiredTool` 可选（M4l）：玩家在输入框里用 `/<工具名>` 指定“本轮必须调用这个工具”。
- 取值是 `ready.payload.tools` 里的模型可见工具名；缺省或 `null` 表示不强制。
- 服务端必须在本轮至少看到一次该工具的调用；没调用时先重试一次，仍失败则回
  `error{code:"tool_required"}`，不得静默返回普通回答。
- `text` **保留**玩家输入的 `/工具名` 前缀（对话历史与 UI 显示一致）；
  服务端只把它当作命令，不要求模型回答斜杠本身。
- 老 mod 不传该字段；服务端可从 `text` 开头的 `/<工具名>` 推断，保持兼容。

### 4.4.1 `tool_debug` / `tool_debug_result` — 调试直调（M4m）

供开发者在输入框里手动执行一个工具，**完全不经过 AI / pi 会话 / prompt 历史**。

```jsonc
// mod → sidecar
{ "v":1, "id":"d-1", "type":"tool_debug",
  "payload": { "name": "game_get_power", "args": { "planetId": 1 } } }

// sidecar → mod
{ "v":1, "id":"s-9", "type":"tool_debug_result", "replyTo":"d-1",
  "payload": {
    "ok": true,
    "tool": "game_get_power",
    "source": "game",                 // game | local | unknown
    "durationMs": 42,
    "content": "……模型可见的格式化文本……",
    "details": { "ok": true, "data": { }, "meta": { } },  // 原始返回
    "error": null,
    "truncated": false,
    "image": null                     // 有截图时只给 mimeType/width/height/bytes
  } }
```

- `args` 必须是 JSON 对象（≤64 KiB）；payload 结构非法返回 `error{code:"bad_request"}`，
  按工具 TypeBox schema 校验失败返回 `tool_debug_result.ok=false` + `error.code="invalid_args"`。
- 游戏工具内部仍走 `tool_call` / `tool_result`，但 `tool_call.payload.internal=true`，
  mod 不显示普通工具卡；结果统一由 `tool_debug_result` 展示。
- 本地工具（`kb_*` / `wiki_*` / `blueprint_search` / `blueprint_info`）在 sidecar 内直接执行，不经过 mod。
- 不创建 pi 会话、不调用 LLM、不需要 API Key、不写入对话历史；与 prompt 共用串行链，
  忙时排队执行。
- `details` 里的图片 `data`（base64）会被剥离，只保留 `image` 元数据；超长结果截断并置
  `truncated=true`。
- 开关双重门禁：mod 配置 `General/DebugToolConsole=true`，sidecar 以 `--debug-tools` 启动；
  未开启时 sidecar 返回 `error{code:"debug_disabled"}`。
- **只读白名单**：`game_get_*` / `game_analyze_area` / `game_capture_view` /
  `game_render_layout` / `game_trace_line` / `game_get_planets` / `game_get_logistics`、
  `kb_*`、`wiki_*`、`blueprint_search`、`blueprint_info`。
  不包含 `game_copy_blueprint`、`blueprint_save`、`blueprint_generate`、`blueprint_fs`、
  `run_script`（写文件/可间接写文件）；调用它们时 `tool_debug_result.ok=false`，
  `error.code="debug_not_allowed"`。
- mod 侧命令：`!tools` 列出工具目录；`!tool <name> <json>` 发起调试直调。
  命令在 `AppendUser` 之前拦截，不进入普通对话气泡或历史。

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
| `tool_required` | `prompt.requiredTool` 指定的工具在本轮没有被调用（M4l） |
| `debug_disabled` | `tool_debug` 未开启（sidecar 未带 `--debug-tools`，M4m） |
| `debug_not_allowed` | 工具不在调试只读白名单（M4m） |
| `confirmation_required` | 写工具缺少一次性确认令牌（M4） |
| `confirmation_invalid` | 令牌无效/已使用/参数不匹配（M4） |
| `confirmation_expired` | 确认令牌已过期（M4） |
| `confirmation_denied` | 玩家拒绝确认，未执行（M4） |
| `sandbox_required` | 当前载入未启用游戏沙盒工具，沙盒专属工具被拒绝（M5+） |
| `internal` | 未分类内部错误 |

### 4.9 预留（M0 不实现，仅占位以免 v1 被破坏）

`abort`（mod → sidecar）、`tool_call` / `tool_result`（双向）、`confirm_request` / `confirm_result`（双向）、`confirm_release`（sidecar → mod，脚本租约释放）、`game_event`（mod → sidecar）、`ui_event`（mod → sidecar）、`session`（sidecar → mod）。
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
5. **新增能力串**：允许；接收方必须忽略不认识的 `capabilities` 项，不得因此报错或关闭连接。

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
    "thinkingLevel": "high",
    "permissionLevel": "limited"
  }
}
```

- 所有字段**均可选**，只更新给出的字段；未给出的保持不变。
- `apiKey` 只在此方向传输一次；sidecar 持久化后**永不回传**。
- `thinkingLevel` 取值 `off | minimal | low | medium | high | xhigh | max`，与 pi 的
  `ThinkingLevel` 一致。聊天窗只暴露 `off | low | high | max` 四档。
  DeepSeek `thinkingLevelMap`：Flash 映射 `low` / `high` / `max`；Pro 只映射
  `high` / `max`（`low` 会被 pi clamp）。`off` 表示**不请求思考**，默认 `high`（开启）。
  非法取值 ⇒ `error{code:"bad_request"}`，不落盘。
- `permissionLevel` 取值 `readonly | limited | takeover`，默认 `limited`。
  它只决定**写操作是否需要玩家确认**，不改变工具是否注册，也不改变一次性 token：
  - `readonly`（只读）：所有写操作（risk 1 与 risk 2）都先走 A.12 的
    `confirm_request`，玩家同意后才执行。
  - `limited`（受限）：risk 1（可逆、无存档副作用：镜头/标记/面板/方位/撤销）
    直接放行；risk 2（改变存档状态）仍走 `confirm_request`。
  - `takeover`（接管）：所有写操作直接放行，不再弹确认页。
  后两种档位下 sidecar 仍会发 `confirm_request` 以取得一次性 token，mod 按档位
  自动回传 `allow`；token 的一次性/绑定校验不放松。`run_script` 的脚本级确认
  使用脚本里写工具的最高 risk，因此纯 risk 1 脚本在「受限」下也可直接执行。
  本地蓝图写工具（`blueprint_save` / `blueprint_generate` / `blueprint_fs` 的
  `mkdir`/`write`/`rm`/`rmdir`）以及 `game_copy_blueprint` 也按 risk 1 参与同一
  审批；其中本地蓝图工具不消耗 mod 令牌，确认只代表玩家同意写文件。
  权限只能由玩家在聊天窗/配置文件里修改，模型无法通过工具提权。
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
    "permissionLevel": "limited",
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
| `missing_key` | 尚无凭证 | 在聊天窗提示玩家点击右上角设置按钮，在「模型」页配置（不再通过聊天输入框采集密钥） |
| `invalid_key` | 凭证被上游拒绝（401/403） | 提示玩家去设置页检查或更换 Key |
| `unreachable` | 无法连通上游 | 提示网络问题；自定义地址可在设置页修改 |

**`config.state` 的任何字段都不得包含 `apiKey`。**
`thinkingLevel` / `permissionLevel` 是新增字段；老服务端不返回时 mod 应保持本地配置值
（权限默认 `limited`）。
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
- `baseUrl` 会先清洗：补 `https://`、去掉尾部 `/chat/completions` 之类的完整端点、去尾斜杠；
  鉴权与解析规则与 `provider.test` 相同（Anthropic `x-api-key`、Gemini `x-goog-api-key`，
  Gemini 的 `models/` 前缀会被去掉）。
- 失败时 `models: []`，`payload.error` 是人类可读的错误（不含 key）。
- 显式给了 `baseUrl` 时只问端点，不会拿官方目录冒充端点模型。
- `resolvedBaseUrl`（可选，向后兼容）：探测成功后返回「聊天适配器期望的 base」。
  地址写错时（OpenAI 兼容缺 `/v1`、Anthropic 多 `/v1`、Gemini 缺版本段）mod 会用它
  补全输入框，避免「检测通过、发消息 404」。

#### `provider.catalog`（mod → sidecar → mod）

设置页的「预设供应商」下拉框使用，列出 pi 内置目录里的全部供应商，**顺序与目录一致**。
请求不带业务字段：

```json
{ "v": 1, "id": "m-15", "type": "provider.catalog", "payload": {} }
```

响应（`replyTo` = 请求 id）：

```json
{ "v": 1, "id": "s-15", "type": "provider.catalog", "replyTo": "m-15",
  "payload": {
    "source": "catalog",
    "providers": [
      { "id": "amazon-bedrock", "name": "Amazon Bedrock" },
      { "id": "anthropic", "name": "Anthropic" },
      { "id": "deepseek", "name": "DeepSeek" }
    ]
  } }
```

- 只读 pi 目录，不联网、不需要 Key、不落盘；`providers` 只含 `id` / `name`。
- 失败时 `providers: []`，`payload.error` 是人类可读的错误；mod 不维护备用名单。

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
- 404 → 提示「端点没有 /models（HTTP 404，可能缺 /v1）」。
- 未知供应商需要先填 `baseUrl`；已知供应商会用 pi 目录里该模型自己的 API 地址。
- 鉴权按供应商选择：Anthropic `x-api-key` + `/v1/models`，Gemini `x-goog-api-key` +
  `/v1beta/models`，Azure `api-key` + `api-version`，其余 `Authorization: Bearer`。
- 与 `model.catalog` 共用同一套清洗与回退：缺 `/v1` 自动补一次；401/403/404 才换写法重试
  （最多 3 次，单次 9s / 回退 5s，总预算 17s）。
- `resolvedBaseUrl`（可选）：同 `model.catalog`，成功时给出聊天可用的 base。

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
4. mod 侧**不再通过聊天输入框采集密钥**；设置页的 API Key 输入框必须为**密码态**
   （不回显明文），提交后清空本地草稿。

### A.5 实现注意（M1 实测，踩过的坑）

以下几条都是**读类型定义看不出来、只有实跑才会暴露**的行为，写在这里避免重复踩：

1. **默认档位必须显式写 `high`。** pi 的 `thinkingLevel` 默认是 `medium`，
   而 DeepSeek Pro 的 `thinkingLevelMap` 把 `minimal`/`low`/`medium` 设为 `null`，
   clamp 逻辑会**向后找**到 `high` —— 于是"什么都没配"反而变成
   `thinking:{type:"enabled"}` + `reasoning_effort:"high"`，既变慢又变贵。
   M1 起 sidecar 默认显式传 `thinkingLevel: "high"`（默认开启思考）；
   玩家可在聊天窗右下角的「思考」下拉框里选 `off` 关闭、`low` 降档或 `max` 提高（见 A.1）。
   Flash 映射 `low` / `high` / `max`；Pro 只有 `high` / `max`，选 `low` 会被 clamp。

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
  "payload": {
    "saveKey": "3f2a91c4d8e05b17",
    "saveName": "我的档",
    "blueprintTiers": {
      "assembler": 2, "belt": 2, "inserter": 3,
      "piler": true, "stacking": true
    },
    "sandbox": false
  }
}
```

- `blueprintTiers`（可选，M4j+）：玩家当前**已解锁**的蓝图档位，供 sidecar 生成蓝图时默认使用，
  不需要模型再调用查询工具。字段都可省略；缺失时 sidecar 退回安全档（base / 黄带 / 普通分拣器）。
  - `assembler`: 0 = Mk.I / 1 = Mk.II / 2 = Mk.III
  - `belt`: 0 = 黄带 / 1 = 绿带 / 2 = 蓝带
  - `inserter`: 0..3（分拣器 / 高速 / 极速 / 集装）
  - `piler` / `stacking`: 是否解锁自动集装机 / 集装堆叠
  老 sidecar 忽略该字段；老 mod 不发，sidecar 用默认档。
- `sandbox`（可选，M5+）：本次载入是否启用游戏沙盒工具（`GameMain.sandboxToolsEnabled`）。
  必须是布尔值；缺失或非法时 sidecar 按 `false` 处理（安全默认），不能因此拒绝 `save.bind`。
  该字段只描述**当前载入会话**，不是存档的永久属性；同一个 `saveKey` 下次以不同开关载入时必须重新上报。
  老 mod 不发；老 sidecar 忽略。mod 侧仍会独立校验沙盒专属工具，不能只依赖本字段。
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
                 { "role": "assistant", "text": "上次的回答",
                   "steps": [
                     { "kind": "thinking", "text": "先看整体产量…" },
                     { "kind": "tool", "name": "game_get_production",
                       "args": "{\"item\":\"iron\"}", "ok": true,
                       "summary": "铁矿 30/min", "detail": "…" }
                   ] }
               ] }
}
```

- `existed`：该存档此前是否已有对话（`false` = 新建）
- `messageCount`：本次带回的历史消息条数
- `history`：最近最多 40 条 `user` / `assistant` 的可见文本（单条最多 4000 字符），
  只用于 mod **回放聊天窗**，不进入模型上下文。压缩记录会作为
  `kind:"compaction"` 的 assistant 消息回放（附录 A.11）；老 mod 会把它当普通回答显示。
  历史读取失败时为空数组，不阻断 `save.bound`。
- `history` 的 `assistant` 消息可带可选的 `kind:"compaction"`（附录 A.11）：
  这是一条上下文压缩记录，`text` 是摘要；同时带 `compactionId` / `tokensBefore` /
  `estimatedTokensAfter`（均可选）。mod 应把它渲染成“自动压缩上下文”对话串，
  并把这条记录之前的回合标灰、禁止单独删除。老 mod 忽略 `kind`，按普通 assistant 显示。
- `history` 的 `assistant` 消息可带可选的 `cards`（附录 A.9）：该回答产出的蓝图卡片，
  形如 `[{fileName, path, name, ...}]`。老客户端忽略该字段；mod 回放时在回答后重建卡片。
- `history` 的 `assistant` 消息可带可选的 `gameTime`（字符串，`HH:mm:ss`）：该回答完成时的
  游戏时间。老 mod 忽略该字段；mod 回放时显示在回答脚注。
- `history` 的 `user` 消息也可带同一个可选的 `gameTime`：用于回放用户气泡下面的脚注
  （只有时间 + 复制）。sidecar 用同一回合记录的时间近似提问时间；没有记录时省略，
  mod 退回到当前游戏时间。
- `history` 的 `assistant` 消息可带可选的 `steps`：**该回答之前**的思考块与工具调用，
  按发生顺序排列（M4k+）。一次提问里模型可能「思考 → 工具 → 再思考 → …」，
  sidecar 从 pi 会话记录里把这些中间步骤收拢到最终回答上。
  - `kind: "thinking"`：`{ "kind": "thinking", "text": "…" }`
  - `kind: "tool"`：`{ "kind": "tool", "name": "…", "args": "…", "ok": true,
    "summary": "…", "detail": "…" }`；`args`/`summary`/`detail` 可省略，
    `ok` 缺省表示会话里没有结果记录（回答被中断）。
  - 上限（防止单帧超过 §1 的 8 MiB）：单条回答最多 60 步；思考 4000 字符、
    工具参数 600、结果摘要 160、结果详情 2000；所有历史消息的 `steps` 合计约 2 MiB。
  - 老 mod 忽略该字段；新 mod 回放时按顺序重建为**收起态**的思考块/工具块，
    玩家点开仍能看到原文。

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
  `unsupported_session`（会话含压缩/分支/上下文中编辑，拒绝改写）、`io_error` 之一。
- 成功后 sidecar 会**丢弃当前内存会话**，下一次 `prompt` 从改写后的文件重建上下文，
  因此被删回合不会继续进入模型上下文。
- 实现是线性会话重写：保留条目按原顺序重新串成 `parentId` 链；
  含 `compaction` / `branch_summary` / `context_edit` 的会话拒绝删除（避免破坏上下文树）。
  已压缩的回合要撤销请走附录 A.11 的 `history.undo_compaction`，不要用 `history.delete`。

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
    "path": "/home/me/Documents/Dyson Sphere Program/Blueprint/IcarusII/电磁涡轮 20.txt",
    "relPath": "IcarusII/电磁涡轮 20.txt",
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
| `relPath` | 否 | 相对蓝图根的 POSIX 路径，例如 `IcarusII/x.txt`；**子目录卡片必须带它** |
| `path` | 否 | sidecar 侧的绝对路径；**不保证** mod 能直接打开（Proton/Wine 路径可能不同） |
| `name` | 否 | 蓝图标题（header 的 `shortDesc`） |
| `item` / `itemId` / `rate` | 否 | 产物与目标产量（`blueprint_generate` 才有） |
| `buildings` / `width` / `height` | 否 | 建筑数与占地（从蓝图二进制读出） |
| `author` / `version` / `attributes` / `description` | 否 | 蓝图 header 元数据 |
| `source` | 否 | `"generated"` \| `"downloaded"` \| `"copied"` \| `"written"`，用于卡片角标 |
| `license` / `repository` | 否 | 下载来源的许可与仓库，卡片可据此署名 |

- 一次工具调用成功 → 一张卡片；一轮里调用多次就发多条，顺序与工具完成顺序一致。
- 工具失败不发卡片。
- 同一轮里如果 `blueprint_fs rm/rmdir` 删掉了已经生成的蓝图，sidecar 会撤回待发卡片，
  不写历史；mod 不应假设收到卡片时文件一定还在。
- mod 必须容忍字段缺失；有 `relPath` 时优先 `GameConfig.blueprintFolder + relPath`
  （并校验仍在根内），否则回退到 `GameConfig.blueprintFolder + fileName`。
- sidecar 会把卡片按「用户提问原文」持久化，并在后续 `save.bound.history` 的
  assistant 消息上以可选 `cards` 字段带回；mod 回放时在回答后重建卡片。
  记录损坏/找不到对应回合时只是不回放，不影响历史文本。

### A.9.1 `link_card` — 实体链接卡片（sidecar → mod）

`game_tether_add` 成功后，sidecar 在本轮收尾时按目标逐条发 `link_card`。mod 用它回放/重建
建筑卡、星球卡或矿脉卡；实时 tether 事件也会先自动建卡，mod 只在同一回合内按目标 key 去重。

```jsonc
{
  "v": 1, "id": "s-81", "type": "link_card",
  "payload": {
    "kind": "building",
    "planetId": 103,
    "buildingId": 6956,
    "name": "制造台Mk.I",
    "subtitle": "南门二A",
    "description": "生产速度: 30.0/min\n电力: 10kW|100%\n增产效果: +0.0% 额外产出\n状态: 待机",
    "iconLabel": "#6956"
  }
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `kind` | 是 | `building`、`planet` 或 `vein` |
| `planetId` | 是 | 目标行星数字 id |
| `buildingId` | 建筑必填 | 建筑实体 id |
| `veinId` | 矿脉必填 | 矿脉节点 id |
| `name` | 是 | 游戏原名的快照，不做显示层改写 |
| `subtitle` | 否 | 星系/行星副标题 |
| `description` | 否 | 建筑统计或星球备忘录快照 |
| `iconLabel` | 否 | 缩略图占位文字 |

- sidecar 按「用户提问原文 + 回答原文」把卡片持久化到存档会话的 `link-cards.jsonl`；
  `save.bound.history` 的 assistant 消息用可选 `linkCards` 字段带回，mod 回放时重建。
  只按提问匹配会在重复提问（「现在什么情况」）时错配，因此回答原文也参与消歧。
- 卡片只描述目标，不传图标；mod 用 `planetId`/`buildingId`/`veinId` 从当前存档资源解析图标。
- 卡片点击是聚焦；按钮是 tether 连线，已连线显示 `已定位`。

### A.9.2 `dsp://entity/...` — 聊天实体链接

富文本可用三种 URL：

- `dsp://entity/planet/<planetId>`
- `dsp://entity/building/<planetId>/<buildingId>`
- `dsp://entity/vein/<planetId>/<veinId>`

它们占用 800000–899999 链接段；mod 点击后复用卡片聚焦逻辑：星球进星图，建筑进当前行星视图；
建筑/矿脉在外星则无效果。实体 ID 不编码进数字，按每个 Text 的 URL 映射分发。

### A.10 `reply.time` — 回存每条回答的游戏时间（mod → sidecar）

mod 收到 `message{final}` 并完成渲染后发送。sidecar 不回复；它按存档持久化一条
「提问原文 → 游戏时间」记录，后续 `save.bound.history` 会把该时间挂到对应 assistant
消息的 `gameTime` 字段上。

```jsonc
{
  "v": 1, "id": "m-61", "type": "reply.time",
  "payload": {
    "prompt": "帮我看看这条日志里的过滤规则是什么？",
    "reply": "日志里是 Filter: legacy……",
    "gameTime": "08:42:00"
  }
}
```

- `prompt`：本轮用户提问原文，用于没有 `reply` 时回退匹配；过长时按 sidecar 存储上限截断。
- `reply`（可选）：本轮 assistant 终稿原文。sidecar 优先按回答原文匹配，能区分
  “继续”这类重复提问；过长时截断。
- `gameTime`：`HH:mm:ss`；没有游戏数据时 mod 可跳过不发。
- sidecar 忽略空值/非法值，不因记录失败影响回答；同一提问/回答以最后一条记录为准。
- 老 sidecar 忽略未知 `type`；老 mod 不发该消息，历史脚注退回显示 `--:--` 或当前时间。

### A.11 `context.compacted` / `history.undo_compaction` — 压缩记录与撤销

pi 的自动压缩完成后，sidecar 把摘要作为一条**只给 UI** 的记录发给 mod，并提供
“撤销最近一次压缩”的请求。压缩记录和撤销都不改变模型可见的普通消息格式；
摘要不会作为 user/assistant 消息写进会话。

#### `context.compacted`（sidecar → mod）

```jsonc
{
  "v": 1, "id": "s-71", "type": "context.compacted",
  "payload": {
    "summary": "## Goal\n…",
    "compactionId": "a1b2c3d4",
    "timestamp": 1760000000000,
    "tokensBefore": 96000,
    "estimatedTokensAfter": 24000,
    "reason": "threshold"
  }
}
```

- `summary`：pi 生成的压缩摘要；mod 作为一条 assistant 记录渲染。
- `compactionId`：pi compaction entry id；事件刚发生时可能缺省，撤销时可省略。
- `timestamp`：压缩完成时间（毫秒）；mod 用本地时间生成 `[HH:mm:ss]`。
- `tokensBefore` / `estimatedTokensAfter`：可选，只用于展示。
- `reason`：`manual` | `threshold` | `overflow`，可选。
- mod 收到后插入一条“`[HH:mm:ss] 自动压缩上下文`”记录，并把它之前的回合标灰、
  禁止单独删除；老 mod 忽略未知 `type`。

压缩期间 sidecar 还会发 `status{state:"compacting"}`；mod 应显示“整理上下文中…”。

#### `history.undo_compaction`（mod → sidecar）

```jsonc
{
  "v": 1, "id": "m-72", "type": "history.undo_compaction",
  "payload": { "saveKey": "3f2a91c4d8e05b17", "compactionId": "a1b2c3d4" }
}
```

- `compactionId` 可省略：省略表示撤销最近一次压缩。
- 只允许撤销**最近一次**压缩（LIFO）；指定更早的压缩返回 `not_latest`。
- 目标不存在返回 `not_found`；正在回答返回 `busy`；会话无法安全改写返回
  `unsupported_session`；写盘失败返回 `io_error`。

#### `history.compaction_undone`（sidecar → mod）

```jsonc
{
  "v": 1, "id": "s-73", "type": "history.compaction_undone",
  "replyTo": "m-72",
  "payload": {
    "saveKey": "3f2a91c4d8e05b17",
    "compactionId": "a1b2c3d4",
    "ok": true,
    "removed": 1,
    "error": null,
    "mayRecompact": true
  }
}
```

- 成功：sidecar 已从会话文件删除 compaction entry，旧消息重新进入模型上下文；
  mod 移除压缩记录、重新计算灰色区间，并提示“如果上下文再次超限，会重新压缩”。
- `mayRecompact`：提示下一次提问可能立刻再次触发压缩。
- 老 sidecar 不认识该请求；老 mod 不发送，压缩记录保持不可撤销。

**注意**：`history.delete` 仍然拒绝含 `compaction` 的会话（避免删除单轮后摘要悬空）；
撤销压缩必须走本节的专用消息。

---

### A.12 `confirm_request` / `confirm_result` — 写操作确认（M4）

写操作默认需要玩家确认；权限档位（见 A.1）决定哪些写操作可以直接放行。
sidecar 生成 32 字节随机 token（16 进制），把
`requestId/callId/tool/argsHash/token/title/body/risk/timeoutMs` 发给 mod；玩家点
**同意** 后 mod 原样回传 token，sidecar 才把 token 放进 `tool_call.confirmToken`
发给 mod 执行。普通 token 60 秒有效、单次使用，并绑定 `callId + tool + argsHash`。
需要自动放行的档位也走同一条消息，只是 mod 不弹页、直接回传 `allow`。

> **v1 边界**：mod 不持有原始 args，无法重算 `argsHash`；该字段只能发现 sidecar
> 两条消息自相矛盾，不能阻止被替换的 sidecar 在玩家确认后替换参数。真正的安全边界是
> “玩家确认 + 一次性 token + 工具名白名单”；确认卡文案必须如实描述影响面。

**权限档位对确认流程的影响**（`permissionLevel`，见 A.1）：

| 档位 | write tool 的 risk | sidecar | mod |
| --- | --- | --- | --- |
| `readonly` | 1 / 2 | 正常发 `confirm_request` | 弹确认页，玩家决定 |
| `limited` | 1 | 正常发 `confirm_request` | **不弹页**，自动 `allow` 并回传 token |
| `limited` | 2 | 正常发 `confirm_request` | 弹确认页，玩家决定 |
| `takeover` | 1 / 2 | 正常发 `confirm_request` | **不弹页**，自动 `allow` 并回传 token |

`limited` 自动放行的判据是 `confirm_request.risk <= 1`。`run_script` 的脚本级
确认把 `risk` 设为脚本里写工具的最高 risk，因此只含低风险写工具的脚本在
`limited` 下也会自动放行；含 risk 2 的脚本仍会弹一次确认。
`blueprint_save` / `blueprint_generate` / `blueprint_fs` 的写操作也复用
`confirm_request`（risk 1），但 mod 签发的 token 不会进入 `tool_call`，只用于记录
玩家同意。
token 仍然由 mod 签发/校验，一次性和绑定关系不放松。权限档位只影响插件运行期，
不会改写会话历史或游戏存档。

`run_script` 的写模式使用**脚本写租约**：`tool` 为 `run_script`，`argsHash` 是脚本
SHA-256，并可带 `maxWrites`（1–10000）与 `writeNames`（脚本里出现的写工具名）。
玩家同意后，mod 在 300 秒内只允许该 token + 同一脚本哈希 + `writeNames` 内的工具，
最多 N 次写 `tool_call`；sidecar 在脚本
结束/失败后发 `confirm_release{token}` 立即释放。脚本里的写工具必须写成字面量
`dsp.call("工具名", …)`，动态拼名的写调用会被 sidecar 拒绝。

#### `confirm_request`（sidecar → mod）

```jsonc
{
  "v": 1, "id": "s-80", "type": "confirm_request",
  "payload": {
    "requestId": "confirm_1",
    "callId": "call_7",
    "tool": "game_set_research",
    "argsHash": "sha256…",
    "token": "64-hex…",
    "title": "切换研究目标？",
    "body": "科技：电磁学\n可能浪费当前研究已投入的进度。",
    "risk": 2,
    "timeoutMs": 60000,
    // 仅脚本租约（tool=run_script）时可选；普通写操作不带这两个字段。
    "maxWrites": 10000,
    "writeNames": ["game_set_recipe", "game_feed_input"]
  }
}
```

- `maxWrites`：可选，1–10000；缺省/0 表示普通单次 token。
  mod 只接受 `tool=run_script` 且 `argsHash` 非空、`writeNames` 非空且全部属于写工具表的租约；
  空 `writeNames` 会被 mod 直接拒绝，不再等于“放行全部写工具”。
- `writeNames`：可选，脚本里出现的写工具名；租约内每次 `tool_call` 都必须命中其一。

mod 用 `choose/question` 显示 `title`，`choose/explain` 显示 `body`，右下角显示
红色「拒绝」和蓝色「同意」；关窗、Esc、超时都按拒绝处理。

#### `confirm_result`（mod → sidecar）

```jsonc
{
  "v": 1, "id": "m-81", "type": "confirm_result",
  "payload": {
    "requestId": "confirm_1",
    "decision": "allow",
    "token": "64-hex…"
  }
}
```

- `decision`：`allow` 或 `deny`；v1 不实现 `allow_always`。
- sidecar 校验 `requestId/token` 后才发写操作 `tool_call`；拒绝返回
  `confirmation_denied`，不产生任何游戏副作用。
- 玩家在 60 秒内不回答：mod 发 `deny`，sidecar 也按超时拒绝。
- 脚本写租约只由带 `maxWrites` 的 `confirm_request` 创建；普通写操作仍是单次 token。
- `confirm_release`（sidecar → mod）：`{ "token": "…" }`，用于脚本结束后立即释放租约。

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
| `game_get_overview` | 存档、时间、当前星球/星系、玩家经纬度 |
| `game_get_player` | 机甲能量/护盾、背包 |
| `game_get_planet` | 行星矿脉汇总、可选矿脉群/逐矿脉节点（分页 + 经纬度）、建筑计数 |
| `game_get_power` | 电网需求/供给/容量/储能 |
| `game_get_research` | 当前研究、进度、队列 |
| `game_get_production` | 按物品聚合的产量/消耗/库存 |

**`game.entity.v1`（M4a）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_get_entity` | 单个/批量建筑详情：原型、配方、产物/消耗、功率、分拣器/传送带/仓储/物流塔、星系/行星/经纬度与朝向；`fields` 裁剪段 |
| `game_render_layout` | 区域建筑字符布局图（上=+Z，左=-X），origin 用经纬度 |
| `game_get_planets` | 银河行星一览；矿脉按宇宙探索等级披露 |
| `game_get_logistics` | 跨行星物流塔供给/需求汇总 |
| `game_trace_line` | 沿传送带/分拣器追踪产线拓扑；节点用星系/行星/经纬度 |
| `game_get_entities` | 行星内建筑分页列表；可按 `protoId` / `protoName` / `recipeId` / 区域过滤；每行带 location |

**`game.analyze.v1`（M4c）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_analyze_area` | 按空间范围聚合产线：建筑/配方计数、装机产能、耗电、物流设施、涉及电网；center 用经纬度 |

**`game.progress.v1`（M4a）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_get_progress` | 玩家进度：派生阶段、已解锁科技、里程碑、教程、研究队列 |

**`game.view.v1`（M4a）**

| 协议/模型名 | 说明 |
| --- | --- |
| `game_capture_view` | 截取当前画面（JPEG，可带可见实体的屏幕坐标与 location），图片走 `tool_result.image` |

> `game_capture_view` 只截**游戏层**：`GameCamera.CaptureScreenshot` 把 `cullingMask` 设为
> `gameLayerMask`，UGUI 不进画面，因此不需要额外的"隐藏界面"开关。

> **坐标约定**：建筑/玩家位置统一输出 `location` 对象：
> `starId` / `starName` / `planetId` / `planetName` / `latitude` / `longitude` / `altitude`，
> 辅助字段 `latitudeText` / `longitudeText`。不再输出会随行星自转/公转变化的 x/y/z。
> 经纬度输入可直接传 `latitude` + `longitude`；`x/y/z` 仅保留兼容旧调用。

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
| `blueprint_search` | 在线搜索 DSPBluePrints/FactoryBluePrints 与 Dyson Sphere Blueprints 的蓝图（不打包内容） |
| `blueprint_info` | 读取蓝图并做格式体检（header/base64+gzip/md5），支持 GitHub path 或网站 URL，带来源与署名 |
| `blueprint_save` | 保存到游戏蓝图目录（默认 `IcarusII/`，可传 `dir`），支持 GitHub path 或网站 URL，同名自动加序号 |
| `blueprint_fs` | 蓝图目录的受限虚拟文件系统：`pwd/cd/ls/cat/mkdir/write/rm/rmdir`；路径以蓝图根为 `/` |
| `blueprint_generate` | 按知识库真实配方生成完整产线蓝图，保存到蓝图目录；单张总线容量不足时自动拆成多张 |

- `blueprint_generate` 是 sidecar 本地工具，依赖已建立的 M3 知识库：
  知识库未建立时返回 `kb_unavailable`；它先调用 `kb_plan` 的同一套配方展开，再交给 dsp-calc 布局。
  返回值除 `fileName`/`path`/`buildings`/`width`/`height` 外，还带 `stages`（每阶段机器与产出）、
  `raw`、`byproducts`、`warnings`、`validation`；产量超过单张混带总线容量时会返回
  `tileCount` / `ratePerTile` / `tiles[]`（每张独立文件）。输入是社区别名时，结果带可选的
  `alias: { input, resolved, source }`（source = curated/wiki/cache）；`from` 为实际使用的起点标签。
- 全部由 sidecar 直接访问外网；失败显式返回 `wiki_unavailable` / `blueprint_unavailable`，
  不允许用模型记忆补内容。
- 蓝图内容许可 **CC BY-NC-SA 4.0**：结果必须带 repository 与许可，回答需署名；
  本仓库不缓存/分发蓝图内容。
- Wiki 内容版权以各站点页面声明为准，回答需附 `url`。

### B.1.2 脚本工具（M4d，sidecar 本地）

| 模型名 | 参数 | 说明 |
| --- | --- | --- |
| `run_script` | `code`, `timeoutMs?`, `maxCalls?` | QuickJS-WASM 脚本；`dsp.call(name, args)` 同步调用已注册工具。默认只读；含字面量写工具时先弹一次脚本级确认，最多 10000 次写调用；`timeoutMs` 默认 30s/最大 120s，`maxCalls` 默认 100/最大 10000 |

- 沙箱里没有 `process` / `require` / `fetch` / 文件系统；能力面只有注入的 `dsp.call`。
- `dsp.call` 走同一个 `GameToolHost`，白名单来自当前连接的 `host.tools()`，
  因此自动继承 risk / 路由 / 超时；写工具必须写成字面量 `dsp.call("工具名", …)` 并由 `script.write.v1` 租约放行。
- 预算：`maxCalls` 默认 100 / 最大 10000、`timeoutMs` 默认 30s / 最大 120s、返回 ≤ 6KB；
  超时由 QuickJS 中断处理器打断。
- **协议层没有新帧**：脚本完全在 sidecar 内执行，`dsp.call` 复用既有的 `tool_call`/`tool_result`，
  因此游戏内仍会出现这些调用的工具卡（`internal=false`）。
- 实现细节：同步变体 + "挂起 → 宿主执行 → 重放"（memo 已完成的调用），
  所以脚本必须可重放，不要用 `Date.now` / `Math.random`。

工具结果中的物品/建筑/科技可带 `iconTag`（游戏图标短码，不含反斜杠与分号）；
模型在正文里写成 `\tag;`。聊天窗必须使用 `Configs.builtin.generalIconMapping` 才能渲染这些短码。

> 游戏自带的 UGUI 改版没有实现 `<b>`；`IcarusII` 在写入 `Text` 前会把 `<b>...</b>` 转成受支持的亮白 `<color=...>...</color>`。

### B.1.4 交互问答 `ask_user`（`ui.ask.v1`）

模型需要玩家明确选择、而现有信息不足以继续时，可调用 `ask_user`。
mod 收到后把输入栏临时替换成 `choose` 面板；玩家在游戏里作答，答案通过同一个
`tool_call` / `tool_result` 通道回填，**不新增协议帧**。

工具名与参数形状对齐 Pi 社区包 [`pi-ask-user`](https://github.com/edlsh/pi-ask-user)，
因此社区 skill/提示词里的 `ask_user` 调用可以直接复用；但本实现不加载 pi 的 TUI 扩展，
UI 由 mod 的 `choose` 面板提供。旧名 `ui_ask_user` 继续接受。

请求（`tool_call.payload.args`）：

```jsonc
{
  "questions": [
    {
      "question": "今晚想吃什么？",
      "context": "玩家在游戏里问晚饭",             // 可选，UI 暂不展示
      "options": [
        { "title": "火锅" },
        { "title": "烧烤", "description": "可选说明" }
      ],
      "allowFreeform": true,                    // 可选，默认 true
      "allowMultiple": false                    // 可选，默认 false；多选时选项前显示复选框
    },
    { "question": "几点吃？", "options": [{ "title": "六点" }, { "title": "七点" }] }
  ]
}
```

- `questions` 1–8 个；每题 `options` 0–4 个，选项可以是字符串或 `{title, description?}`（兼容 `label/text/value/name/option` 别名）。
- 也支持单题写法：顶层 `question` + `options` + `allowMultiple` / `allowFreeform`。
- 每个问题都会在 `choose/question/num` 显示 `当前 / 总数`；`返回` 回到上一题并恢复已填答案。
- `allowFreeform` 为 true（默认）时，最后固定有一个输入项；为 false 时不显示输入项。
- `allowMultiple` 为 true 时，每个选项数字前显示复选框，可同时选多个；自定义输入也算一个答案。
- 多个问题时，非最后一题的提交按钮显示「下一个」，最后一题显示「提交」。
- 玩家点 `跳过`（或按 Esc）跳过当前题；最后一题跳过后返回已答/跳过的结果。
- 玩家关闭聊天窗时 mod 以 `cancelled` 结束；此时 sidecar 不再等待。
- 同一连接同一时刻只允许一个 pending ask；重复调用返回 `busy`。

结果（`tool_result.payload.data`）：

```jsonc
{
  "answers": [
    { "index": 0, "question": "今晚想吃什么？", "answer": "火锅", "optionIndex": 0,
      "answers": ["火锅"], "optionIndexes": [0], "custom": false, "skipped": false },
    { "index": 1, "question": "几点吃？", "answer": null, "optionIndex": -1,
      "answers": [], "optionIndexes": [], "custom": false, "skipped": true }
  ]
}
```

- `answer` 是选项文字或玩家输入的自定义文本（多选时用「、」连接）；`skipped=true` 时 `answer=null`、`optionIndex=-1`。
- `answers` / `optionIndexes` 是完整答案列表；多选时按选项顺序，自定义项下标为 -1。
- `optionIndex >= 0` 表示普通选项；`custom=true` 表示答案里包含自定义输入。
- `timeoutMs` 对 `ask_user` 放宽到 **600000ms**（玩家阅读/输入时间）；其它工具仍然
  最多 30000ms。mod 与 sidecar 都必须按工具名做这个特判。
- 失败码：`invalid_args`、`busy`、`cancelled`、`tool_unavailable`、`not_in_game`。

sidecar 侧把上述结果转成可读文本交给模型，例如：

```
玩家已回答：
1. 今晚想吃什么？ → 火锅
2. 几点吃？ → （跳过）
```

### B.1.5 写工具（M4/M5，`game.write.v1` / `game.drop.v1` / `game.build.v1`）

| 工具 | risk | 参数 | 说明 |
| --- | --- | --- | --- |
| `game_focus_camera` | 1 | `target`（行星/星系/建筑 id） | 移动视角；确认后执行 |
| `game_place_marker` | 1 | `planetId?`, `latitude`, `longitude`, `altitude?`, `text`, `icon?` | 新建标记；确认后执行；可撤销 |
| `game_open_panel` | 1 | `panel`（inventory/tech/starmap/marker/mecha/replicator/statistics/blueprint） | 打开面板；确认后执行 |
| `game_set_research` | 2 | `techId`（id 或名称） | 切换研究目标；确认后执行 |
| `game_drop_item` | 2 | `itemId`（id 或名称）, `count?` | 丢弃背包物品到地面；确认后执行；需手动拾回，不进入撤销栈 |
| `game_set_recipe` | 2 | `buildingId`, `recipeId` | 更换一台生产建筑配方；先退料；撤销只恢复配方 |
| `game_set_assembler_batch` | 2 | `selections[]`（≤20） | 批量更换配方；一次确认、一层撤销；整批预检失败则不执行 |
| `game_storage_set_filter` | 2 | `buildingId`, `itemId` | 设置/清除存储过滤器；已占用异物品格跳过；可撤销 |
| `game_storage_set_limit` | 2 | `buildingId`, `limit` | 设置存储可用格数上限；可撤销 |
| `game_storage_move` | 2 | `sourceId`, `targetId`, `itemId`, `count` | 存储间移动物品；预检目标容量；可撤销 |
| `game_storage_deposit` | 2 | `buildingId`, `itemId`, `count` | 背包→存储；满时退剩余；可撤销 |
| `game_storage_withdraw` | 2 | `buildingId`, `itemId`, `count` | 存储→背包；背包满时留剩余；可撤销 |
| `game_feed_input` | 2 | `buildingId`, `itemId`, `count`（≤500） | 背包原料直接进生产建筑输入缓冲；不经过分拣器/传送带；可撤销 |
| `game_tether_add` | 1 | `kind`（planet/building/vein）, `planetId?`, `buildingId?`, `veinId?` | 建立机甲到行星/建筑/矿脉的方位指示；使用游戏原生动线 |
| `game_tether_clear` | 1 | 无 | 清除本 mod 建立的所有方位指示 |
| `game_tether_list` | 0 | 无 | 列出当前方位指示 |
| `game_todo_templates` | 0 | 无 | 列出原生 GoalProto 模板与判断器 |
| `game_todo_list` | 0 | 无 | 列出 AI 创建的目标、进度与阶段；自由探索时带 `goalPanelOff` |
| `game_todo_add` | 2 | `group?`, `goals[]`（`templateId`, `title?`, `params?`, `itemId?`, `count?`） | 注入原生目标；确认后执行；补跑 GoalDeterminator；自由探索时带 `goalPanelOff` |
| `game_todo_remove` | 2 | `groupId?`, `goalId?` | 隐藏/删除 AI 创建的目标；确认后执行 |
| `game_take_output` | 2 | `buildingId` | 取生产建筑成品；保留配方；背包满返回剩余；不进入撤销栈 |
| `game_undo_last_write` | 1 | 无 | 撤销最近一次可撤销写操作；最近一次不可撤销时返回错误 |

- `game_focus_camera` / `game_place_marker` / `game_open_panel` / `game_set_research` / `game_undo_last_write` 需要 `game.write.v1`；`game_drop_item` 需要 `game.drop.v1`；`game_set_recipe` / `game_set_assembler_batch` / `game_take_output` / `game_feed_input` 需要 `game.build.v1`；存储五件套需要 `game.storage.v1`；方位三件套需要 `game.tether.v1`；目标四件套需要 `game.todo.v1`。
- risk 1/2 的工具都会先走 A.12；拒绝确认时 mod 不执行、无任何副作用。
- 写操作只保留一层撤销；`game_set_assembler_batch` 整批算一层。换档/断线清空撤销栈。
- `game_drop_item`、`game_take_output`、`game_tether_add/clear` 不进入撤销栈：
  它们会把栈顶标记为“不可撤销”，随后 `game_undo_last_write` 返回明确错误，不会去撤销更早的无关操作。
- `game_undo_last_write` 也走 A.12 确认；只撤销 mod 记录的一步。

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
    "internal": false,
    // 写操作（risk>0）才出现：
    "confirmToken": "64-hex…",
    "confirmArgsHash": "sha256…"
  }
}
```

- `callId` 由 sidecar 生成，单连接内唯一；mod 必须在 `tool_result.replyTo` 原样回传。
- `internal` 可选，缺省 `false`。为 `true` 时表示 sidecar 内部调用（M3 知识库导出）：
  mod 应正常执行并回 `tool_result`，但**不要**显示工具卡、不要改状态栏。
- `timeoutMs` 由 sidecar 钳制在 500–30000ms；`ask_user` 例外，上限 600000ms。超时后 sidecar 不再接受该 `callId` 的结果。
- M2 的 `risk` 恒为 0；写工具（M4）才使用 1/2。
- `confirmToken` / `confirmArgsHash` 只有写操作才出现；mod 必须校验 token 未过期、未使用，且 callId/tool/argsHash 与确认时一致。
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
  `timeout`、`busy`、`internal`、`cancelled`、`sandbox_required`。
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
