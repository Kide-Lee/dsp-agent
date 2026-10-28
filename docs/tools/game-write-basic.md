# 写操作 · 标记、面板、研究与丢弃

低风险的界面/标记类写操作，以及两个会把物品或研究状态写进存档的工具。确认与撤销语义见 [index.md](index.md#写操作的确认与撤销)。

共 5 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_place_marker`

**label** 新建标记 · **能力位** `game.write.v1` · **风险** 1（低风险写，可逆） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
在当前行星或指定行星上新建一个地图标记。写操作：执行前玩家必须确认。用 latitude/longitude 指定位置（可从 game_get_entity 的 location 读取），text 是标记文字。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家明确要求“在这里做个标记/标个点”时使用。
- 坐标必须来自游戏数据或玩家给出；不要凭截图猜坐标。优先使用 game_get_entity 返回的 location.latitude/longitude。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略为当前行星 |
| `latitude` | `number` | 否 | — | 纬度（-90..90） |
| `longitude` | `number` | 否 | — | 经度（-180..180） |
| `altitude` | `number` | 否 | — | 海拔（米），默认 0 |
| `position` | `object` | 否 | — | 旧版行星局部坐标，仅兼容用 |
| `text` | `string` | 是 | — | 标记完整文字；地图简述只显示前 2 个字，详情保存完整名称 |
| `icon` | `integer` | 否 | — | 标记图标 id，可选 |

#### 返回

只返回 `display`：一行确认摘要

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_open_panel`

**label** 打开面板 · **能力位** `game.write.v1` · **风险** 1（低风险写，可逆） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
打开游戏内面板：inventory（背包）、tech（科技树）、starmap（星图）、marker（标记）、mecha（机甲）、replicator（复制器）、statistics（统计）、blueprint（蓝图）。写操作：执行前玩家必须确认。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家明确要求打开某个游戏面板时使用。
- 只支持工具描述里列出的面板名；不要编造其他面板。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `panel` | `string` | 是 | — | inventory / tech / starmap / marker / mecha / replicator / statistics / blueprint |

#### 返回

只返回 `display`：一行确认摘要

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_set_research`

**label** 切换研究 · **能力位** `game.write.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把当前研究目标切换为指定科技。写操作，风险 2：可能浪费当前科技已投入的研究进度，执行前玩家必须确认。已完成的科技会被拒绝；科技必须未完成且前置已满足。techId 可以是科技 id 或名称。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家明确要求切换研究目标时使用。
- 调用前先用 game_get_research 或 kb_get 确认 techId；不确定就先问玩家。
- 如果玩家拒绝确认，不要再次调用。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `techId` | `integer \| string` | 是 | — | 科技 id 或名称 |

#### 返回

只返回 `display`：一行确认摘要

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_drop_item`

**label** 丢弃物品 · **能力位** `game.drop.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把背包里的物品丢到当前行星地面。写操作，风险 2：执行前玩家必须确认。丢出的物品会变成可拾取的垃圾，需玩家手动拾回；不进入撤销栈。itemId 可以是物品 id 或名称；count 默认 1，一次最多 500。
```

#### 模型可见 · 使用规则（system prompt）

- 只在玩家明确要求“丢掉/扔了”某个物品时使用。
- 调用前先用 game_get_player 确认背包里有该物品和数量；不要丢掉关键材料。
- 丢弃后不进入撤销栈，必须明确告诉玩家“需要手动拾回”。
- 如果玩家拒绝确认，不要再次调用。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `itemId` | `integer \| string` | 是 | — | 物品 id 或名称 |
| `count` | `integer` | 否 | minimum=1，maximum=500 | 丢弃数量，默认 1，最大 500 |

#### 返回

只返回 `display`：一行确认摘要

嵌套对象内部字段（未逐个展开）：`itemId`、`itemName`、`count`、`requested`、`undoable`、`undoNote`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_undo_last_write`

**label** 撤销上一步 · **能力位** `game.write.v1` · **风险** 1（低风险写，可逆） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
撤销最近一次由本 mod 执行的写操作（视角、标记、面板、研究、配方、存储、投料等）。只保留一层撤销栈；最近一次写操作不可撤销（如丢物品/取成品/方位指示）时返回明确错误；写操作，执行前玩家必须确认。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家说“撤销/恢复刚才那一步”时使用。
- 只撤销最近一次，不要连续撤销多次；不可撤销的操作会返回错误。

#### 参数

无参数，调用时传 `{}`。

#### 返回

只返回 `display`：撤销结果；最近一步不可撤销时会明确说明

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
