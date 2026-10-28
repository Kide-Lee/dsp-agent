# 写操作 · 配方、投料与仓储

改变产线和库存的写操作。配方与投料可撤销一层；`game_take_output` 是**不可撤销**的，执行后撤销栈顶会被它占住。

共 9 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_set_recipe`

**label** 更换配方 · **能力位** `game.build.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把一台生产建筑（制造台/熔炉/化工厂/研究站等）的配方改为指定配方。写操作，风险 2：执行前玩家必须确认。会先按游戏 UI 的“重置配方”把机器内原料/产物退回背包，再切换配方；撤销只恢复配方，不把退回背包的物品塞回机器。recipeId=0 表示清空配方。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家明确要求改某一台建筑的配方时使用；先用 game_get_entity 确认它是生产建筑。
- recipeId 先用 kb_search/kb_get 或 game_get_entity 的 recipeId 确认；不要编造。
- 会先退料；如果玩家不想让物品回背包，先说明。撤销只恢复配方。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 生产建筑 entityId |
| `recipeId` | `integer` | 是 | — | 配方 id；0 = 清空配方 |

#### 返回

只返回 `display`：一行确认摘要

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_set_assembler_batch`

**label** 批量配方 · **能力位** `game.build.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
一次修改多台生产建筑的配方（最多 20 台），只弹一次确认、只压一层撤销。写操作，风险 2：执行前玩家必须确认。每台都会先退回机器内原料/产物再切换；任何一台预检失败则整批不执行。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“这几台/这一排都改成同一个配方”时使用；先用 game_get_entities 拿到 id。
- 一次最多 20 台；超过时分批，不要一次塞太多。
- 返回 changed/skipped/items；如果某台不匹配，整批会失败并说明原因。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `selections` | `object[]` | 是 | minItems=1，maxItems=20 | 最多 20 台；同一 buildingId 不能重复 |

#### 返回

只返回 `display`：一行确认摘要（成功台数 / 跳过台数）

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_storage_set_filter`

**label** 存储过滤 · **能力位** `game.storage.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
设置一台存储建筑（箱子/储液罐/物流站仓储）的物品过滤器；itemId=0 清除过滤器。写操作，风险 2：执行前玩家必须确认。已占用且物品不同的格子会跳过，不会替换物品。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“这个箱子只放某物品/不要放某物品”时使用；先用 game_get_entity 确认 storage 段。
- itemId 用 kb_search/kb_get 或已知物品 id；0 表示清除过滤器。
- 只支持普通箱子/储液罐/物流站仓储；燃料/弹药等专用存储不支持。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 存储建筑 entityId |
| `itemId` | `integer` | 是 | — | 物品 id；0 = 清除过滤器 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `buildingId` | `integer` | 存储建筑 id |
| `itemId` | `integer` | 同参数 `itemId`；0 表示清除过滤器 |
| `itemName` | `string` | 物品名；清除时为「清除」 |
| `changedGrids` | `integer` | 实际改动的格数 |
| `slots` | `integer` | 该存储的总格数 |
| `undoable` | `boolean` | 固定 true |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_storage_set_limit`

**label** 存储上限 · **能力位** `game.storage.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
设置一台存储建筑的容量上限（允许使用的格数）。写操作，风险 2：执行前玩家必须确认。limit 是允许使用的格数（0..总格数），不是禁止格数。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“这个箱子只用前 N 格”时使用；先用 game_get_entity 看 storage.slots。
- limit 是允许格数，范围 0..slots。
- 只支持普通箱子/储液罐/物流站仓储。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 存储建筑 entityId |
| `limit` | `integer` | 是 | — | 允许使用的格数，0..总格数 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `buildingId` | `integer` | 存储建筑 id |
| `limit` | `integer` | 同参数 `limit`（允许使用的格数） |
| `slots` | `integer` | 该存储的总格数 |
| `bans` | `integer` | 被禁用的格数 |
| `undoable` | `boolean` | 固定 true |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_storage_move`

**label** 移库 · **能力位** `game.storage.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把指定物品从一台存储建筑移动到另一台存储建筑。写操作，风险 2：执行前玩家必须确认。先预检目标容量/过滤器；目标放不下时整笔不执行。源不足时移动可用的部分并返回 remaining。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“把这箱铜搬到那箱”时使用；先用 game_get_entity 确认两端 storage 段和距离。
- sourceId/targetId 都是建筑 entityId；itemId 用已知 id；count 是请求数量。
- 目标空间不足会整笔失败；源不足只移动可用部分，remaining 表示没移动的数量。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `sourceId` | `integer` | 是 | — | 源存储建筑 entityId |
| `targetId` | `integer` | 是 | — | 目标存储建筑 entityId |
| `itemId` | `integer` | 是 | — | 物品 id |
| `count` | `integer` | 是 | — | 请求移动数量 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `sourceId` | `integer` | 同参数 `sourceId` |
| `targetId` | `integer` | 同参数 `targetId` |
| `itemId` | `integer` | 同参数 `itemId` |
| `itemName` | `string` | 物品名 |
| `requested` | `integer` | 请求移动的数量 |
| `moved` | `integer` | 实际移动的数量 |
| `remaining` | `integer` | 因空间/库存不足没移动的数量 |
| `undoable` | `boolean` | 固定 true |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_storage_deposit`

**label** 存入存储 · **能力位** `game.storage.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把背包里的物品放进一台存储建筑（箱子/储液罐/物流站仓储）。写操作，风险 2：执行前玩家必须确认。存储满时只放入能放下的部分，剩余会退回背包。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“把这些东西放进箱子”时使用；先用 game_get_entity 确认 storage 段。
- itemId/count 必填；存储满时返回 remaining，剩余仍在背包。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 存储建筑 entityId |
| `itemId` | `integer` | 是 | — | 物品 id |
| `count` | `integer` | 是 | — | 放入数量 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `buildingId` | `integer` | 存储建筑 id |
| `itemId` | `integer` | 同参数 `itemId` |
| `itemName` | `string` | 物品名 |
| `requested` | `integer` | 请求存入的数量 |
| `stored` | `integer` | 实际存入的数量 |
| `remaining` | `integer` | 没存进去、仍留在背包的数量 |
| `undoable` | `boolean` | 固定 true |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_storage_withdraw`

**label** 从存储取出 · **能力位** `game.storage.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把一台存储建筑里的物品取到背包。写操作，风险 2：执行前玩家必须确认。背包满时只取能放下的部分，剩余留在存储里。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“从箱子里拿点东西”时使用；先用 game_get_entity 确认 storage 段。
- itemId/count 必填；背包满时返回 remaining，剩余仍在存储。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 存储建筑 entityId |
| `itemId` | `integer` | 是 | — | 物品 id |
| `count` | `integer` | 是 | — | 取出数量 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `buildingId` | `integer` | 存储建筑 id |
| `itemId` | `integer` | 同参数 `itemId` |
| `itemName` | `string` | 物品名 |
| `requested` | `integer` | 请求取出的数量 |
| `taken` | `integer` | 实际取出的数量 |
| `remaining` | `integer` | 没取出、仍留在存储里的数量 |
| `undoable` | `boolean` | 固定 true |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_feed_input`

**label** 投料 · **能力位** `game.build.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
从背包把配方原料直接放进一台生产建筑的输入缓冲。写操作，风险 2：执行前玩家必须确认。这条路径不经过分拣器/传送带；只接受当前配方需要的物品，最多 500 个。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“把背包里的原料塞进这台机器”时使用；先用 game_get_entity 确认 assembler.recipeId/inputs。
- itemId 必须是当前配方 Items 里的输入；不要投成品或配方不需要的物品。
- 它会直接写 served/incServed，绕过分拣器/传送带；确认卡会说明。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 生产建筑 entityId |
| `itemId` | `integer` | 是 | — | 配方输入物品 id |
| `count` | `integer` | 是 | — | 投入数量，1..500 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `buildingId` | `integer` | 生产建筑 id |
| `itemId` | `integer` | 同参数 `itemId` |
| `itemName` | `string` | 物品名 |
| `fed` | `integer` | 实际放进输入缓冲的数量 |
| `served` | `integer` | 该输入槽当前已满足的需求量 |
| `undoable` | `boolean` | 固定 true：投料可撤销 |
| `display` | `string` | 一行确认摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_take_output`

**label** 取成品 · **能力位** `game.build.v1` · **风险** 2（改存档写） · **超时** 8000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把一台生产建筑当前已产出的成品取到背包，保留配方。写操作，风险 2：执行前玩家必须确认。背包满时只取能放下的部分，并返回剩余数量。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要求“把这几台机器的成品收了”时使用；先用 game_get_entity 确认 assembler 段。
- 只取 produced 成品，不会退原料；配方保留。
- 背包满时会返回 remaining，不要把 remaining 当成已取走。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `buildingId` | `integer` | 是 | — | 生产建筑 entityId |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `buildingId` | `integer` | 生产建筑 id |
| `taken` | `integer` | 本次取出的成品总数 |
| `remaining` | `integer` | 取出后仍留在建筑里的数量 |
| `items` | `array` | 取出的物品明细：`id` / `name` / `count` |
| `undoable` | `boolean` | 固定 false：取成品不可撤销，且会占用撤销栈顶 |
| `display` | `string` | 一行确认摘要 |

嵌套对象内部字段（未逐个展开）：`itemId`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
