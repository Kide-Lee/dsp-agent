# 只读 · 建筑、星球与拓扑

从“这片区域有什么”到“这条线断在哪”。实体 id 是这一组工具的共同货币：先用列表类工具拿 id，再用详情类工具下钻。

共 6 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_get_entity`

**label** 建筑详情 · **能力位** `game.entity.v1` · **风险** 0（只读） · **超时** 6000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
按 entityId 读取单个建筑（或用 entityIds 批量读取）的实际情况，尽量对齐游戏面板：原型、配方、输入/输出槽（数量/需求/增产）、功率与供电满足率、工作状态（缺料/没电/运行/待机）、分拣器目标、传送带线路容量/节点/货物数与区间、仓储容量、物流塔槽位（当前/在途/上限/供需模式）、无人机/运输船/翘曲器与充电状态。fields 可裁剪返回段（assembler/miner/inserter/belt/storage/station/components；默认全量）；批量 entityIds 默认只返回 core，最多 10 个。id 来自 space_region op=list 或截图标注；id 会被回收，返回里的 stale=true 表示该 id 已被别的建筑占用。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问某一栋建筑为什么不动、配方是什么、插了什么、有没有电时使用。
- 先只取需要的段：例如只关心缺料时传 fields=["assembler"]；要逐字段调试才传 components。
- 检查多栋建筑时用 entityIds（最多 10 个）并配合 fields，避免一次返回完整详情。
- 如果返回 stale=true，不要使用这条数据：id 已被回收，重新用 space_region op=list 定位。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityId` | `integer` | 否 | — | 单个建筑 id（PlanetFactory.entityPool 的下标） |
| `entityIds` | `integer[]` | 否 | — | 批量建筑 id，最多 10 个；与 entityId 二选一 |
| `expectProtoId` | `integer` | 否 | — | 期望的原型 id；不匹配时返回 stale=true |
| `fields` | `string \| string[]` | 否 | — | 要返回的段：core/assembler/miner/inserter/belt/storage/station/components；也接受逗号分隔字符串 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `planetId` | `integer` | 同参数 `planetId` |
| `entityId` | `integer` | 同参数 `entityId` |
| `available` | `boolean` | 工厂是否已加载；false 时看 `reason` |
| `reason` | `string` | 不可用原因：`factory_not_loaded` / `not_found` / `empty_slot` |
| `protoId` | `integer` | 建筑原型 id |
| `protoName` | `string` | 建筑原型名 |
| `iconTag` | `string` | 图标短码（可直接写进 `<icon tag>`） |
| `category` | `string` | 建筑类别：assembler / miner / belt / station … |
| `yaw` | `number` | 朝向角（度） |
| `power` | `object` | 该建筑的功率信息（需求/供给/满足率） |
| `stale` | `boolean` | id 已被回收或与 expectProtoId 不匹配；true 时整条数据不可用 |
| `expectProtoId` | `integer` | 回显参数 `expectProtoId`，用于核对 |
| `count` | `integer` | 批量读取（entityIds）时的条数 |
| `entities` | `array` | 批量读取结果；默认只含 core 字段 |
| `assembler` | `object` | 装配/制造段：`recipeId` / `recipeName` / `recipeSeconds`（配方与单次耗时）、`speedMultiplier`（速度倍率）、`craftsPerMin`（每分钟次数）、`working`（是否在运行）、`replicating` / `extraSpeed`（增产与加成）、`productPerMin`（每分钟产量）、`forceAccMode`（强制加速）、`inputs` / `outputs`（槽位数组，元素含 `itemId` / `name` / `iconTag` / `count`，输入还有 `needed` / `incServed`）、`powerSatisfactionPct`（供电满足率）、`status`（缺料/没电/运行/待机） |
| `miner` | `object` | 采矿段：`productId` / `productName`（产出物品）、`productCount`（单次产出）、`period`（周期）、`orePerMin`（每分钟产量）、`veinCount`（覆盖矿脉数）、`totalVeinAmount`（剩余总储量）、`workstate`（工作状态） |
| `inserter` | `object` | 分拣器段：`grade`（等级）、`bidirectional`（是否双向）、`stage`、`filterItemId` / `filterItemName`（过滤物品）、`pickTarget` / `insertTarget`（取/放目标 id）与 `pickTargetName` / `insertTargetName`、`handItemId` / `handItemName` / `handItemCount` / `handItemInc`（手上物品）、`stackCount` / `canStack` / `stackInput` / `stackOutput`（堆叠）、`delay`（延迟 tick） |
| `belt` | `object` | 传送带段：`itemsPerSec`（实际吞吐/秒）、`segLength`（段长）、`rangeFrom` / `rangeTo`（起止节点）、`nodeIndex`、`pathId`（所属线路）、`pathCapacity`（线路容量）、`pathNodeCount` / `pathCargoCount`（线路上节点/货物数） |
| `storage` | `object` | 仓储段：`slots`（格数）、`items`（物品数组，元素含 `itemId` / `name` / `iconTag` / `count`）、`totalCount`（总物品数） |
| `station` | `object` | 物流塔段：`isCollector`（轨道采集器）、`isStellar`（星际塔）、`warperCount` / `warperMaxCount`（翘曲器）、`workDroneCount` / `idleDroneCount`（无人机）、`workShipCount` / `idleShipCount`（运输船）、`deliveryDrones` / `deliveryShips`（配送中）、`warpEnableDistAu`（启用翘曲距离，AU）、`energy` / `energyMax` / `chargeKw` / `charging`（储能与充电）、`slots`（槽位数组，元素含 `itemId` / `name` / `inTransit` / `max` / `localOrder` / `remoteOrder` / `localLogic` / `remoteLogic`） |
| `components` | `object` | 逐字段调试段（仅 fields 含 components，返回量大） |
| `location` | `object` | 建筑坐标 `location`（经纬度），由 GameToolFormat.SetLocation 追加 |
| `display` | `string` | 一行摘要 |

嵌套对象内部字段（未逐个展开）：`cycleCount`、`extraCycleCount`、`cargoItems`、`outputId`、`backInputId`、`leftInputId`、`rightInputId`、`kind`、`poolId`、`fields`

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
- 字符只表达类别与占位；朝向、配方、输入输出要再用 space_region op=list / op=production。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityId` | `integer` | 否 | — | 以该建筑为中心 |
| `radius` | `number` | 否 | — | 半径（米），默认 40，范围 4–400 |
| `cellSize` | `number` | 否 | — | 每格米数，默认按半径自动（约 2–8m） |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `planetId` | `integer` | 同参数 `planetId` |
| `available` | `boolean` | 工厂是否已加载 |
| `grid` | `array` | 字符网格行；每个字符代表一个 cellSize 米方格 |
| `width` | `integer` | 网格宽度（格） |
| `height` | `integer` | 网格高度（格） |
| `cellSize` | `number` | 每格米数 |
| `origin` | `object` | 网格原点坐标 `location`（经纬度） |
| `legend` | `array` | 字符图例：字符 → 建筑类别 |
| `orientation` | `string` | 方向约定：上=+Z，左=-X |
| `display` | `string` | 一行摘要 |

嵌套对象内部字段（未逐个展开）：`char`、`kind`、`count`、`centerSource`

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
- hasFactory=true 的行星可继续用 space_region（op=list / op=power / op=production）分析。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `includeVeins` | `boolean` | 否 | — | 是否返回已观察星球的矿脉，默认 true |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `available` | `boolean` | 银河数据是否已加载 |
| `observeLevel` | `integer` | 宇宙探索等级；决定未观察星球是否披露矿脉 |
| `planetCount` | `integer` | 行星总数 |
| `observedCount` | `integer` | 已观察行星数 |
| `factoryCount` | `integer` | 已建工厂的行星数 |
| `planets` | `array` | 行星列表：`id` / `name` / 类型 / 轨道 / `factoryLoaded` / `hasFactory` / `observed` / `veinsHidden`（未观察时不给矿脉） |
| `note` | `string` | 给模型的说明：未观察星球只给基础轨道信息 |
| `display` | `string` | 一行摘要：行星数 · 已观察 · 有工厂 |

嵌套对象内部字段（未逐个展开）：`starId`、`starName`、`type`、`orbitRadiusAu`、`orbitIndex`、`orbitalPeriod`、`rotationPeriod`、`radius`、`theme`、`scanned`、`requiredObserveLevel`、`buildingCount` 等 21 个

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
| `entityId` | `integer` | 是 | — | 起点建筑 id（来自 space_region op=list / 截图） |
| `depth` | `integer` | 否 | minimum=1，maximum=60 | 最大跳数，默认 20 |
| `maxNodes` | `integer` | 否 | minimum=2，maximum=300 | 最大节点数，默认 200 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `available` | `boolean` | 工厂是否已加载，或 seed entityId 是否有效 |
| `planetId` | `integer` | 同参数 `planetId` |
| `seed` | `integer` | 起点建筑 id（回显） |
| `depthLimit` | `integer` | 实际使用的最大跳数 |
| `nodeCount` | `integer` | 节点数 |
| `edgeCount` | `integer` | 边数 |
| `truncated` | `boolean` | 达到深度/节点上限；调大 depth / maxNodes 继续 |
| `nodes` | `array` | 节点：`id` / 名称 / 类别 / 星系 / 行星 / 经纬度 / 深度 |
| `edges` | `array` | 有向边：`from` → `to`（传送带/分拣器连接） |
| `display` | `string` | 一行摘要：节点数 · 连接数 |

嵌套对象内部字段（未逐个展开）：`protoId`、`protoName`、`category`、`depth`、`kind`

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
- 先在这里定位物品与星球，再用 space_region / game_get_planet 下钻。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `itemFilter` | `string` | 否 | — | 物品名或 id，例如“钛块” |
| `planetId` | `integer` | 否 | — | 只看某颗行星 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | minimum=1，maximum=40 | 站点条数，默认 20 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `available` | `boolean` | 银河数据是否已加载 |
| `itemFilter` | `string` | 回显参数 `itemFilter`；未传时为 null |
| `totalStations` | `integer` | 已加载工厂里的物流塔总数 |
| `matchedStations` | `integer` | 命中过滤的塔数 |
| `offset` | `integer` | 同参数 `offset` |
| `limit` | `integer` | 同参数 `limit` |
| `items` | `array` | 按物品聚合的供给/需求：`itemId` / `name` / 供给行星 / 需求行星 |
| `stations` | `array` | 站点明细：星球 / 塔类型 / 槽位方向 / 运输船 / 无人机 / 翘曲器 |
| `display` | `string` | 一行摘要：匹配塔数 · 物品种数 |

嵌套对象内部字段（未逐个展开）：`planetId`、`planetName`、`stationId`、`isStellar`、`isCollector`、`warperCount`、`idleShipCount`、`workShipCount`、`idleDroneCount`、`workDroneCount`、`iconTag`、`supply` 等 18 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_copy_blueprint`

**label** 存为蓝图 · **能力位** `game.entity.v1` · **风险** 1（低风险写，可逆） · **超时** 20000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
把指定的一批建筑（entityIds）存成游戏蓝图文件，复用游戏自己的复制逻辑，产物与游戏内复制同源。保存到游戏蓝图目录（默认 IcarusII/，可传 dir），打开蓝图界面即可看到；只写蓝图文件，不改游戏状态。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家说“把这片产线存成蓝图”时使用；id 来自 space_region op=list。
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

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `saved` | `boolean` | 是否成功写出蓝图文件 |
| `error` | `string` | 失败原因（例如 `factory_not_loaded`） |
| `source` | `string` | 蓝图来源：`copied`（游戏复制逻辑） |
| `path` | `string` | 蓝图文件的完整路径 |
| `relPath` | `string` | 相对蓝图根的路径（默认 `IcarusII/文件名`） |
| `dir` | `string` | 传入的目录参数（回显） |
| `dirRel` | `string` | 相对蓝图根的实际目录；根目录时为空字符串 |
| `fileName` | `string` | 文件名（含 `.txt`） |
| `buildings` | `integer` | 蓝图包含的建筑数 |
| `areas` | `integer` | 蓝图包含的区域数 |
| `name` | `string` | 蓝图标题（来自游戏蓝图元数据） |
| `author` | `string` | 蓝图作者 |
| `version` | `string` | 蓝图自定义版本 |
| `attributes` | `string` | 蓝图外部字段（游戏原始字符串） |
| `description` | `string` | 蓝图说明 |
| `iconItemId` | `integer` | 蓝图图标使用的物品 id |
| `width` | `integer` | 蓝图拖拽框宽（格） |
| `height` | `integer` | 蓝图拖拽框高（格） |
| `display` | `string` | 一行摘要 |

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
