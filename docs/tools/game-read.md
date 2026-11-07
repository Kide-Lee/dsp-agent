# 只读 · 存档与产线概览

最常用的入口：先看清玩家在哪、有什么、缺什么，再决定要不要下钻。六个工具都只读存档快照，不改变游戏状态。

共 3 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_get_overview`

**label** 存档概览 · **能力位** `game.read.v1` · **风险** 0（只读） · **超时** 5000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取当前存档概览：存档名、游戏时刻、当前行星/星系、玩家经纬度、是否暂停。玩家问“我在哪”“现在什么情况”时先调用它；不要凭印象回答。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问当前存档、所在星球、游戏时间或“现在什么情况”时使用。

#### 参数

无参数，调用时传 `{}`。

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `saveName` | `string` | 存档名；空字符串表示尚未进入存档 |
| `saveKey` | `string` | 当前存档的稳定标识；换档后变化，可用于判断上下文是否失效 |
| `gameVersion` | `string` | 游戏版本号 |
| `gameTick` | `integer` | 游戏 tick（存档逻辑时间） |
| `gameTimeSec` | `number` | 游戏时间，单位秒（兼容旧字段） |
| `gameTimeDisplay` | `string` | 游戏时间的可读文本（时分秒） |
| `paused` | `boolean` | 游戏是否暂停 |
| `planet` | `object` | 当前行星：`id` / `name` / `type` / `factoryLoaded` |
| `star` | `object` | 当前星系：`id` / `name` / `type` |
| `player` | `object` | 玩家位置 `location`（经纬度）；机甲状态用 game_get_player |
| `mode` | `object` | 存档模式：`peace` / `sandbox` / `combat` / `goalLevel` |
| `display` | `string` | 一行摘要：`行星名 · 游戏时间` |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_planet`

**label** 行星状态 · **能力位** `game.read.v1` · **风险** 0（只读） · **超时** 12000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取指定行星（省略则为当前行星）的类型、半径、矿脉按资源汇总、建筑按原型计数。veins=groups 返回按位置聚合的矿脉群（已扫描的远程行星也可）；veins=nodes 分页返回逐矿脉节点（仅工厂已加载的行星）。远程行星未加载工厂时明确标注 factoryLoaded=false。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问某个星球有什么矿、矿在哪、建了多少建筑时使用。
- 需要矿脉位置时用 veins="groups"；需要单个矿脉的精确经纬度时用 veins="nodes" 并配合 veinOffset/veinLimit。
- 矿脉群/节点可能很多，不要一次拉全量。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `veins` | `string` | 否 | — | summary（默认，按资源汇总）/ groups（矿脉群+位置）/ nodes（逐矿脉+位置，需工厂已加载）/ all |
| `veinOffset` | `integer` | 否 | — | 逐矿脉分页起点，默认 0 |
| `veinLimit` | `integer` | 否 | — | 逐矿脉每页数量，默认 50，最大 200 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `planetId` | `integer` | 同参数 `planetId`；省略时是当前行星 id |
| `name` | `string` | 行星显示名 |
| `type` | `string` | 行星类型（枚举名） |
| `radius` | `number` | 行星半径（米） |
| `factoryLoaded` | `boolean` | 该行星的工厂是否已加载；false 时矿脉/建筑统计不完整 |
| `veins` | `object` | 按资源汇总的矿脉：资源名 → 数量/储量 |
| `buildings` | `object` | 按原型计数的建筑：原型名 → 数量 |
| `veinGroupsAvailable` | `boolean` | `veins=groups` 时是否返回了矿脉群 |
| `veinGroupsReason` | `string` | 不可用原因：`vein_groups_not_ready`（尚未扫描）/ `planet_not_scanned`（未观察） |
| `veinGroupCount` | `integer` | 矿脉群数量（仅 veins=groups） |
| `veinGroups` | `array` | 矿脉群：`groupIndex` / 资源 / 位置 / 节点数（仅 veins=groups） |
| `veinNodesAvailable` | `boolean` | `veins=nodes` 时是否返回了逐矿脉节点 |
| `veinNodesReason` | `string` | 不可用原因：`factory_not_loaded` / `vein_pool_empty` |
| `veinNodes` | `array` | 逐矿脉节点：`id` / 资源 / 位置（仅 veins=nodes；分页用 veinOffset/veinLimit） |
| `display` | `string` | 一行摘要 |

嵌套对象内部字段（未逐个展开）：`productId`、`iconTag`、`amount`、`count`、`items`、`total`、`offset`、`limit`、`truncated`、`nextOffset`、`minerCount`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_research`

**label** 研究状态 · **能力位** `game.read.v1` · **风险** 0（只读） · **超时** 5000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取当前研究科技、进度、队列、研究速度与已解锁科技数量。玩家问“现在研究什么”“还差多少”时调用。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问研究进度、科技队列或已解锁科技时使用。

#### 参数

无参数，调用时传 `{}`。

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `currentTechId` | `integer` | 当前研究科技 id；0 表示没有在研 |
| `current` | `object` | 当前研究详情：名称 / 进度 / 剩余时间 |
| `queue` | `array` | 研究队列（科技 id / 名称 / 顺序） |
| `techSpeed` | `number` | 研究速度倍率 |
| `unlockedCount` | `integer` | 已解锁科技数 |
| `totalTechs` | `integer` | 科技总数 |
| `display` | `string` | 一行摘要：在研科技与进度 |

嵌套对象内部字段（未逐个展开）：`id`、`name`、`iconTag`、`level`、`hashUploaded`、`hashNeeded`、`progressPct`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
