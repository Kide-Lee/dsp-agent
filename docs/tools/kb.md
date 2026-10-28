# 知识库（sidecar 本地）

离线数据，不依赖 mod 连接。数值与文本以游戏版本 + 语言为缓存键，不会让模型凭记忆补数值。

共 4 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `kb_search`

**label** 知识库搜索 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
在游戏本体知识库里搜索物品、配方、科技、建筑、矿脉，以及提示/教程/里程碑/指引/术语。玩家问“X 是什么”“哪个科技解锁 X”“有没有相关提示”时先用它定位；再按 id 用 kb_get 或 kb_recipe_tree。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家提到具体物品、配方、科技、建筑或游戏机制名称，而你需要确认官方数据时使用。
- 先用 kb_search 拿到 kind + id，再用 kb_get 或 kb_recipe_tree；不要凭记忆编造配方/数值。
- 默认 spoiler=low 不返回剧情教程；只有玩家明确问剧情/教程时才传 spoiler="story"，不要主动剧透。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `query` | `string` | 是 | — | 名称、ID 或 SID，例如“量子芯片”“1201”“coil” |
| `kinds` | `string[]` | 否 | — | 可选类别过滤：item/recipe/tech/model/vein/signal/tip/tutorial/milestone/goal/prompt/glossary/achievement/journal |
| `limit` | `integer` | 否 | minimum=1，maximum=30 | 返回条数，默认 8 |
| `spoiler` | `string` | 否 | — | 剧透上限：none（只要无剧透）/low（默认）/story（含剧情/教程）。默认 low |

#### 返回

返回给模型的是一段**文本**（`content` 块），不是 `data` JSON 信封：条目已由 sidecar 渲染成列表或表格。

---

### `kb_get`

**label** 知识库条目 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
按类别 + id/名称/SID 精确读取一条知识库条目（物品、配方、科技、建筑、矿脉、提示、教程、里程碑、指引、术语等）。通常先用 kb_search 拿到 id，再用它读完整字段。
```

#### 模型可见 · 使用规则（system prompt）

- kb_search 已经给出 kind + id，或玩家明确给出条目 id 时使用。
- kb_get 返回的是游戏本体数据；不要用它之外的信息补全配方/科技数值。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `kind` | `string` | 是 | — | item/recipe/tech/model/vein/signal/tip/tutorial/... |
| `idOrName` | `string` | 是 | — | 数字 id、名称或 SID |

#### 返回

返回给模型的是一段**文本**（`content` 块），不是 `data` JSON 信封：条目已由 sidecar 渲染成列表或表格。

---

### `kb_recipe_tree`

**label** 配方树 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
按游戏本体的主配方递归展开一个物品，计算直接输入、原料总量与副产物。玩家问“造 N 个 X 需要多少原料”“每分钟 N 个 X 怎么配”时用它，不要自己心算。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问某个物品需要哪些原料、需要多少、每分钟产量怎么换算时使用。
- rate 表示目标物品每分钟产量；不传 rate 时 amount 表示一次目标产量（默认 1）。
- 结果里的 raw 是原料汇总，byproducts 是副产物；副产物未从原料中抵扣，回答时要说明。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `item` | `string` | 是 | — | 物品名称、id 或 SID |
| `amount` | `number` | 否 | — | 目标数量，默认 1 |
| `rate` | `number` | 否 | — | 目标每分钟产量；给出时忽略 amount |
| `depth` | `integer` | 否 | minimum=1，maximum=16 | 递归深度，默认 8 |
| `recipeId` | `integer` | 否 | — | 可选：指定根物品的配方 id（替代配方） |
| `from` | `string` | 否 | — | 起点：ore（默认，从原矿）/ smelted（从冶炼产物）/ 物品名或 id |

#### 返回

返回给模型的是一段**文本**（`content` 块），不是 `data` JSON 信封：条目已由 sidecar 渲染成列表或表格。

---

### `kb_plan`

**label** 产线规划 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
给定目标物品的每分钟产量，算出要建多少台什么机器、总功率、每条输入要几条传送带，并复用 kb_recipe_tree 给出原料与副产物。玩家问“我要建多少台机器”“这条线要多少电”“需要几条带”时用它。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问某条产线要多少机器、多少电、几条传送带时使用。
- 玩家问“怎么把 X 做到 N/min / 怎么规划 / 要准备什么”时，第一步就调 kb_plan，不要先查现状再心算。
- rate 是目标物品的每分钟产量，必填；机器数已向上取整（exactCount 是未取整值）。
- 结果里的 assumptions 必须转述给玩家：未建模矿脉产率、建筑堆叠与喷涂消耗。
- 玩家提到增产剂/喷涂时传 proliferator=mk1/mk2/mk3；默认 extra（额外产出），要加速则 proliferatorMode=speed。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `item` | `string` | 是 | — | 目标物品名称、id 或 SID |
| `rate` | `number` | 是 | — | 目标每分钟产量 |
| `depth` | `integer` | 否 | minimum=1，maximum=16 | 递归深度，默认 8 |
| `recipeId` | `integer` | 否 | — | 可选：指定根配方 id |
| `buildingTier` | `string` | 否 | — | 机器档位：base（默认，最低速）/ mk2 / mk3 |
| `buildingId` | `integer` | 否 | — | 可选：直接指定机器原型 id |
| `proliferator` | `string \| string \| string \| string` | 否 | — | 增产剂档位，默认 none |
| `proliferatorMode` | `string \| string` | 否 | — | extra=额外产出（默认），speed=加速生产 |
| `beltSpeed` | `number` | 否 | — | 单条带吞吐（个/秒），默认 6（Mk.I） |
| `from` | `string` | 否 | — | 起点：ore（默认，从原矿）/ smelted（从冶炼产物）/ 物品名或 id |

#### 返回

返回给模型的是一段**文本**（`content` 块），不是 `data` JSON 信封：条目已由 sidecar 渲染成列表或表格。

---
