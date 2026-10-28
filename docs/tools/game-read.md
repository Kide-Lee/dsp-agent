# 只读 · 存档与产线概览

最常用的入口：先看清玩家在哪、有什么、缺什么，再决定要不要下钻。六个工具都只读存档快照，不改变游戏状态。

共 6 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

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

顶层字段（mod 侧实现）：`saveName`、`saveKey`、`gameVersion`、`gameTick`、`gameTimeSec`、`gameTimeDisplay`、`paused`、`planet`、`star`、`player`、`mode`、`display`

其他出现过的字段（多为嵌套）：`id`、`name`、`type`、`factoryLoaded`、`peace`、`sandbox`、`combat`、`goalLevel`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_player`

**label** 玩家状态 · **能力位** `game.read.v1` · **风险** 0（只读） · **超时** 6000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取伊卡洛斯的能量、护盾、反应堆能量与背包物品（id、名称、数量）。玩家问“我还有多少能量”“背包里有什么”时调用。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问自己的能量、护盾或背包物品时使用。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

顶层字段（mod 侧实现）：`display`、`mecha`、`inventory`

其他出现过的字段（多为嵌套）：`coreEnergy`、`coreEnergyCap`、`coreEnergyPct`、`reactorEnergy`、`shieldEnergy`、`shieldCapacity`、`shieldPct`、`id`、`name`、`iconTag`、`count`、`items` 等 17 个

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

顶层字段（mod 侧实现）：`planetId`、`name`、`type`、`radius`、`factoryLoaded`、`veins`、`buildings`、`display`、`veinGroupsAvailable`、`veinGroupsReason`、`veinGroupCount`、`veinGroups`、`veinNodesAvailable`、`veinNodesReason`、`veinNodes`

其他出现过的字段（多为嵌套）：`productId`、`iconTag`、`amount`、`count`、`id`、`items`、`total`、`offset`、`limit`、`truncated`、`nextOffset`、`groupIndex` 等 13 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_power`

**label** 电力状态 · **能力位** `game.read.v1` · **风险** 0（只读） · **超时** 6000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取指定行星（省略则为当前行星）的电网：需求、已供、容量、储能、满足率与充放电。所有功率单位为 kW，储能为 kWh；玩家问“电够不够”时调用。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问电力是否足够、为什么停电或电网负载时使用。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |

#### 返回

顶层字段（mod 侧实现）：`planetId`、`planetName`、`factoryLoaded`、`demandKw`、`servedKw`、`capacityKw`、`storedKwh`、`accumulatorKw`、`satisfactionPct`、`nets`、`display`、`demandDisplay`、`servedDisplay`、`capacityDisplay`、`generatorCount`、`consumerCount`、`accumulatorCount`

其他出现过的字段（多为嵌套）：`id`

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

顶层字段（mod 侧实现）：`display`、`currentTechId`、`current`、`queue`、`techSpeed`、`unlockedCount`、`totalTechs`

其他出现过的字段（多为嵌套）：`id`、`name`、`iconTag`、`level`、`hashUploaded`、`hashNeeded`、`progressPct`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_production`

**label** 实际产出 · **能力位** `game.read.v1` · **风险** 0（只读） · **超时** 12000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取指定行星（省略则为当前行星）按物品聚合的产量/消耗/库存（每分钟口径），按净产出从低到高排序，并给出库存还能撑几分钟。玩家问“我缺什么”时调用它，再结合 game_get_power 判断瓶颈。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“我缺什么”、某条产线为什么停、库存还能撑多久时使用。
- basis=stats 是实际统计；basis=installed 表示 1 分钟桶为空，items 是按装配机算的装机产能（statsEmpty=true）。
- statsEmpty=true 时不要下“没有产线”的结论；设备在别处或需要空间明细时改调 game_analyze_area。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `itemFilter` | `string` | 否 | — | 按物品名或 id 过滤，例如“铁块”或“1001” |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

顶层字段（mod 侧实现）：`planetId`、`planetName`、`statsPending`、`items`、`shortageCount`、`surplusCount`、`display`、`total`、`offset`、`limit`、`truncated`、`nextOffset`、`powerSatisfactionPct`、`buildingCount`、`statsEmpty`、`basis`、`hint`

其他出现过的字段（多为嵌套）：`id`、`name`、`iconTag`、`producedPerMin`、`consumedPerMin`、`netPerMin`、`stored`、`minutesOfStock`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
