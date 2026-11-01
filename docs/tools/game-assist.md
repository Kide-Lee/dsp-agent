# 辅助 · 方位指示、目标与待办

tether 在屏幕上给玩家指方向；todo 写的是游戏原生“目标”面板，不是 mod 自己的清单；game_todo_write 把轮内待办整表写入原生面板的第一组。

共 8 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_todo_templates`

**label** 目标模板 · **能力位** `game.todo.v1` · **风险** 0（只读） · **超时** 5000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
列出游戏原生 GoalProto 目标模板：id、名称、判断器、等级、父分组和默认参数。生成游戏目标前先调用它，选择真实存在的判断器/模板。
```

#### 模型可见 · 使用规则（system prompt）

- 创建游戏目标前先查；不要凭记忆编造 GoalProto 模板 id。
- params 是原生 DeterminatorParams 的默认值；game_todo_add 可覆盖它们。

#### 参数

无参数，调用时传 `{}`。

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `templates` | `array` | 目标模板：`id` / `name` / `determinator` / `level` / `parentId` / `params` |
| `count` | `integer` | 模板数量 |
| `display` | `string` | 一行摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_todo_list`

**label** 当前游戏目标 · **能力位** `game.todo.v1` · **风险** 0（只读） · **超时** 5000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取 AI 创建的原生游戏目标（含轮内待办）及当前值/目标值/阶段，以及目标面板当前是否真的没有显示。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问当前目标进度或准备删除目标时使用。
- 目标在“自由探索”下也会显示（只列 AI 目标）；如果结果里 goalPanelOff=true，说明目标面板确实被关闭了，回答进度时提醒玩家打开目标面板设置改成“关键节点”或“事无巨细”。
- todo=true 的行是轮内待办，用 game_todo_write 更新；不要用 game_todo_remove 删它们。

#### 参数

无参数，调用时传 `{}`。

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `goals` | `array` | AI 创建的目标：`groupId` / `goalId` / `group` / `title` / `templateId` / `current` / `target` / `stage` |
| `count` | `integer` | 目标数量 |
| `goalPanelOff` | `boolean` | 目标面板确实没有显示；true 时提醒玩家打开 |
| `todo` | `boolean` | 该行是轮内待办（group 固定为第一组） |
| `display` | `string` | 一行摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_tether_add`

**label** 方位指示 · **能力位** `game.tether.v1` · **风险** 1（低风险写，可逆） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
建立机甲到行星/建筑/矿脉的方位指示，可同时建立多个（最多 20 个）。本星球的每个建筑/矿脉各画一条 LineGizmo；跨行星时用原生 navigation.indicatorAstroId 指向其中一个目标行星（游戏一次只支持一个导航朝向），落到该行星后，该行星上的所有建筑/矿脉都切换为自绘线。写操作，风险 1：执行前玩家必须确认。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家说“给这些建筑/星球/矿脉都做个方位指示”时使用；先用 game_get_entities/game_get_planet 确认目标。
- kind=planet 时用 planetId（缺省当前星球）；kind=building 时用 buildingId，可选 planetId；kind=vein 时用 veinId，可选 planetId。
- 本星球建筑/矿脉会同时显示多条线；跨行星原生导航一次只指一个目标行星，不要声称所有跨行星线都同时可见。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `kind` | `string` | 是 | enum: planet / building / vein | planet / building / vein |
| `planetId` | `integer` | 否 | — | 行星 id，缺省当前星球 |
| `buildingId` | `integer` | 否 | — | kind=building 时必填 |
| `veinId` | `integer` | 否 | — | kind=vein 时必填 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `kind` | `string` | 指示类型：`planet` / `vein` / `building` |
| `planetId` | `integer` | 目标所在行星 id |
| `veinId` | `integer` | 矿脉 id（仅 kind=vein） |
| `buildingId` | `integer` | 建筑 id（仅 kind=building） |
| `name` | `string` | 目标名称（显示用） |
| `subtitle` | `string` | 副标题 |
| `description` | `string` | 描述文本 |
| `iconLabel` | `string` | 图标短码或 `#id` |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_tether_clear`

**label** 清除方位 · **能力位** `game.tether.v1` · **风险** 1（低风险写，可逆） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
清除本 mod 建立的所有方位指示（导航朝向和建筑追踪线）。写操作，风险 1：执行前玩家必须确认。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家说“取消方位/清除连线”时使用；只清本 mod 建立的目标。

#### 参数

无参数，调用时传 `{}`。

#### 返回

写工具的 `data` 只有 `display`（一句话确认，`WriteToolBase.Ok`）；失败时 `ok=false` 并带 `error.code`。撤销语义见 [index.md](index.md#写操作的确认与撤销)。

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_tether_list`

**label** 方位列表 · **能力位** `game.tether.v1` · **风险** 0（只读） · **超时** 5000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
列出当前方位指示（行星/建筑/矿脉及名称）。只读，不弹确认。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“现在连了哪些目标”时使用。

#### 参数

无参数，调用时传 `{}`。

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `items` | `array` | 当前方位指示：类型 / 行星 / 建筑或矿脉 id / 名称 |
| `display` | `string` | 一行摘要：当前有几个指示 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_todo_add`

**label** 新增游戏目标 · **能力位** `game.todo.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
在原生左侧“游戏目标”面板新增目标。写操作：执行前玩家必须确认。先用 game_todo_templates 查原生 GoalProto 模板；templateId 必填，title 是显示文案，params 可覆盖原生 DeterminatorParams。目标完成判定完全复用游戏原生 GoalDeterminator。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家明确要求“给我一个目标/待办/采集 N 个/解锁科技”等目标时使用。
- 必须先调用 game_todo_templates 选择真实存在的模板；不要编造 templateId 或判断器名。
- title 要写清楚目标；itemId/count 只用于模板参数确实是 item/count 的常见情况，其他模板用 params 按模板原参数顺序覆盖。
- 目标会出现在游戏左侧原生目标面板（玩家选“自由探索”时也显示，只列 AI 目标）；不要重复添加同一个目标。
- 如果结果里 goalPanelOff=true，说明目标面板确实没有显示（玩家选了“自由探索”且模组未接管显示）；目标已经创建但玩家看不到，必须明确提醒玩家打开目标面板设置，把“目标建议”改为“关键节点”或“事无巨细”，不要声称目标已经显示。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `group` | `string` | 否 | — | 分组标题，默认“游戏目标” |
| `goals` | `object[]` | 是 | — | 本轮要新增的目标列表 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `count` | `integer` | 新增的目标数 |
| `goalPanelOff` | `boolean` | 目标面板确实没有显示（自由探索且模组未接管），需要提醒玩家 |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_todo_remove`

**label** 删除游戏目标 · **能力位** `game.todo.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
删除 AI 创建的原生游戏目标；groupId 删除整组，goalId 删除单个子目标。写操作：执行前玩家必须确认。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家明确要求删除目标时使用。
- 先用 game_todo_list 找到 groupId/goalId；不要删除不是 AI 创建的原生目标。
- 轮内待办（列表里 todo=true 的行）不用这个工具删除：改用 game_todo_write 提交去掉它的新列表。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `groupId` | `integer` | 否 | — | 删除整组目标 |
| `goalId` | `integer` | 否 | — | 删除单个目标 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `removed` | `integer` | 删除的目标数 |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_todo_write`

**label** 更新待办 · **能力位** `game.todo.write.v1` · **风险** 0（只读） · **超时** 5000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把当前任务清单整份写入游戏左侧目标面板的第一组“AI 待办”。每次调用替换上一次的列表（空数组清空）；状态 pending / in_progress / completed，完成项在面板里打勾并划线，玩家也可以随时跳过某一条。本轮结束时 sidecar 会自动把清单从面板撤掉，下一轮要显示就重新写。按只读权限登记：不改变游戏状态，也不会请求玩家确认。
```

#### 模型可见 · 使用规则（system prompt）

- 开始一项需要多步完成的工作前先写待办；一步就能做完的琐事不要用。
- 每次更新都提交完整列表，不要只发变化项；做完一步就把它改成 completed。
- 条目是一句可执行的短任务；同一列表里不要重复，最多 20 条。
- 待办显示在游戏左侧目标面板，玩家跳过某条后不要反复重加，除非玩家要求。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `todos` | `object[]` | 是 | — | 完整任务列表，替换上一次；空数组清空 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `group` | `string` | 待办所在的原生目标分组名（固定为第一组） |
| `todos` | `array` | 完整列表：`id` / `content` / `status`（pending / in_progress / completed；玩家跳过的是 skipped） |
| `count` | `integer` | 条目数 |
| `counts` | `object` | `pending` / `inProgress` / `completed` / `skipped` |
| `goalPanelOff` | `boolean` | 目标面板确实没有显示；true 时提醒玩家打开 |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
