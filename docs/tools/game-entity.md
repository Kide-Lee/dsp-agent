# 只读 · 建筑、星球与拓扑

从“这片区域有什么”到“这条线断在哪”。实体 id 是这一组工具的共同货币：先用列表类工具拿 id，再用详情类工具下钻。

共 7 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_get_entity`

**label** 建筑详情 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 6000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
按 entityId 读取单个建筑（或用 entityIds 批量读取）的实际情况，尽量对齐游戏面板：原型、配方、输入/输出槽（数量/需求/增产）、功率与供电满足率、工作状态（缺料/没电/运行/待机）、分拣器目标、传送带线路容量/节点/货物数与区间、仓储容量、物流塔槽位（当前/在途/上限/供需模式）、无人机/运输船/翘曲器与充电状态。fields 可裁剪返回段（assembler/miner/inserter/belt/storage/station/components；默认全量）；批量 entityIds 默认只返回 core，最多 10 个。id 来自 game_get_entities 或截图标注；id 会被回收，返回里的 stale=true 表示该 id 已被别的建筑占用。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问某一栋建筑为什么不动、配方是什么、插了什么、有没有电时使用。
- 先只取需要的段：例如只关心缺料时传 fields=["assembler"]；要逐字段调试才传 components。
- 检查多栋建筑时用 entityIds（最多 10 个）并配合 fields，避免一次返回完整详情。
- 如果返回 stale=true，不要使用这条数据：id 已被回收，重新用 game_get_entities 定位。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityId` | `integer` | 否 | — | 单个建筑 id（PlanetFactory.entityPool 的下标） |
| `entityIds` | `integer[]` | 否 | — | 批量建筑 id，最多 10 个；与 entityId 二选一 |
| `expectProtoId` | `integer` | 否 | — | 期望的原型 id；不匹配时返回 stale=true |
| `fields` | `string \| string[]` | 否 | — | 要返回的段：core/assembler/miner/inserter/belt/storage/station/components；也接受逗号分隔字符串 |

#### 返回

顶层字段（mod 侧实现）：`planetId`、`entityId`、`available`、`reason`、`display`、`protoId`、`protoName`、`iconTag`、`category`、`yaw`、`power`、`stale`、`expectProtoId`、`count`、`entities`、`assembler`、`miner`、`inserter`、`belt`、`storage`、`station`、`components`

其他出现过的字段（多为嵌套）：`recipeId`、`recipeName`、`recipeSeconds`、`speedMultiplier`、`craftsPerMin`、`cycleCount`、`extraCycleCount`、`working`、`replicating`、`extraSpeed`、`productPerMin`、`forceAccMode` 等 87 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_entities`

**label** 建筑列表 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 10000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
分页列出指定行星（省略则为当前行星）的建筑：id、原型、配方、星系/行星/经纬度、朝向。可按原型 id/名称、配方 id 过滤；配合 game_get_entity 查看单体细节。大星球请用 offset/limit 分页，不要一次拉全量。
```

#### 模型可见 · 使用规则（system prompt）

- 需要“这片区域有哪些建筑”“某个配方有多少台机器”时使用。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `protoId` | `integer` | 否 | — | 按建筑原型 id 过滤 |
| `protoName` | `string` | 否 | — | 按建筑原型名称子串过滤，例如“组装机” |
| `recipeId` | `integer` | 否 | — | 按配方 id 过滤 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

顶层字段（mod 侧实现）：`planetId`、`available`、`rows`、`total`、`display`、`offset`、`limit`、`hasMore`

其他出现过的字段（多为嵌套）：`id`、`protoId`、`protoName`、`iconTag`、`category`、`yaw`、`recipeId`、`recipeName`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_render_layout`

**label** 布局图 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 10000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把一片区域的建筑排布画成字符网格（上=+Z，左=-X）：物流站 S、生产设备 M、仓储 B、发电 P、采矿 V、分拣器 >、传送带 =。用来判断“布局对不对/有没有断路/密度如何”，比截图更省 token。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问布局、走线、是否堵死，或截图看不清时使用。
- 字符只表达类别与占位；朝向、配方、输入输出要再用 game_get_entities / game_analyze_area。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityId` | `integer` | 否 | — | 以该建筑为中心 |
| `radius` | `number` | 否 | — | 半径（米），默认 40，范围 4–400 |
| `cellSize` | `number` | 否 | — | 每格米数，默认按半径自动（约 2–8m） |

#### 返回

顶层字段（mod 侧实现）：`planetId`、`available`、`display`、`grid`、`width`、`height`、`cellSize`、`origin`、`legend`、`orientation`

其他出现过的字段（多为嵌套）：`char`、`kind`、`count`、`centerSource`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_planets`

**label** 星球一览 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 20000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
列出银河里所有行星：轨道/类型/半径、是否有工厂与建筑数、电力汇总，以及按宇宙探索等级披露的矿脉。未观察的星球只给基础轨道信息（veinsHidden=true），不泄露矿脉；已建工厂的星球可继续用区域工具分析。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“哪个星球有 X 矿/哪颗星在产什么/银河整体情况”时使用。
- observed=false 或 veinsHidden=true 表示玩家还没探索到，不要编造矿脉；如实说明需要提升宇宙探索等级。
- hasFactory=true 的行星可继续用 game_analyze_area / game_get_entities / game_get_power 分析。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `includeVeins` | `boolean` | 否 | — | 是否返回已观察星球的矿脉，默认 true |

#### 返回

顶层字段（mod 侧实现）：`available`、`display`、`observeLevel`、`planetCount`、`observedCount`、`factoryCount`、`planets`、`note`

其他出现过的字段（多为嵌套）：`id`、`name`、`starId`、`starName`、`type`、`orbitRadiusAu`、`orbitIndex`、`orbitalPeriod`、`rotationPeriod`、`radius`、`theme`、`scanned` 等 27 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_trace_line`

**label** 产线拓扑 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 15000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
从一栋建筑出发，沿传送带与分拣器把相连设备串成图：节点（id/名称/类别/星系/行星/经纬度/深度）与有向边。用于回答“这条线从哪到哪、哪里断了、上下游有哪些机器”。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问某台机器的上下游、整条线是否连通、断在哪时使用。
- 结果只含实体连接，不含配方流向与星际物流；跨星用 game_get_logistics。
- truncated=true 表示达到深度/节点上限，可调 depth / maxNodes 继续。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityId` | `integer` | 是 | — | 起点建筑 id（来自 game_get_entities / 截图） |
| `depth` | `integer` | 否 | minimum=1，maximum=60 | 最大跳数，默认 20 |
| `maxNodes` | `integer` | 否 | minimum=2，maximum=300 | 最大节点数，默认 200 |

#### 返回

顶层字段（mod 侧实现）：`available`、`display`、`planetId`、`seed`、`depthLimit`、`nodeCount`、`edgeCount`、`truncated`、`nodes`、`edges`

其他出现过的字段（多为嵌套）：`id`、`protoId`、`protoName`、`category`、`depth`、`from`、`to`、`kind`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_get_logistics`

**label** 星际物流 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 20000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
汇总所有已加载工厂的物流塔：哪些星球/站点在供给或需求某物品、星际塔与轨道采集器、运输船/无人机与翘曲器数量。这是跨行星产线分析的入口。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“某个原料从哪来/哪颗星在供应/为什么跨星运不过来”时使用。
- supply/demand 来自物流塔槽位的远程供应/需求设置；具体航线与在途量用 game_get_entity 看站点详情。
- 先在这里定位物品与星球，再用 game_analyze_area / game_get_planet 下钻。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `itemFilter` | `string` | 否 | — | 物品名或 id，例如“钛块” |
| `planetId` | `integer` | 否 | — | 只看某颗行星 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | minimum=1，maximum=40 | 站点条数，默认 20 |

#### 返回

顶层字段（mod 侧实现）：`available`、`display`、`itemFilter`、`totalStations`、`matchedStations`、`offset`、`limit`、`items`、`stations`

其他出现过的字段（多为嵌套）：`planetId`、`planetName`、`stationId`、`isStellar`、`isCollector`、`warperCount`、`idleShipCount`、`workShipCount`、`idleDroneCount`、`workDroneCount`、`itemId`、`name` 等 20 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_copy_blueprint`

**label** 存为蓝图 · **能力位** `game.entity.v1` · **风险** 1（低风险写，可逆） · **超时** 20000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把指定的一批建筑（entityIds）存成游戏蓝图文件，复用游戏自己的复制逻辑，产物与游戏内复制同源。保存到游戏蓝图目录（默认 IcarusII/，可传 dir），打开蓝图界面即可看到；只写蓝图文件，不改游戏状态。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家说“把这片产线存成蓝图”时使用；id 来自 game_analyze_area / game_get_entities。
- 一次最多 500 个；工具只保存文件，不建造、不粘贴；权限档位会决定是否弹确认。
- 保存后告诉玩家文件名与位置；重名会自动加 (2)/(3)。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityIds` | `integer[]` | 是 | — | 要包含的建筑 id |
| `name` | `string` | 否 | — | 蓝图文件名（不含 .txt），默认 IcarusII-时间戳 |
| `dir` | `string` | 否 | — | 保存目录（相对蓝图根，默认 IcarusII）；不存在会自动创建 |

#### 返回

顶层字段（mod 侧实现）：`saved`、`error`、`display`、`source`、`path`、`relPath`、`dir`、`dirRel`、`fileName`、`buildings`、`areas`、`name`、`author`、`version`、`attributes`、`description`、`iconItemId`、`width`、`height`

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
