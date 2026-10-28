# 只读 · 进度、截图与区域分析

回答“我现在到哪一步”“画面上是什么”“这片产线多大”。截图会以图片内容块返回，其余是文本。

共 3 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `game_get_progress`

**label** 进度阶段 · **能力位** `game.progress.v1` · **风险** 0（只读） · **超时** 6000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
读取玩家进度：派生阶段（电磁/炼油/钛/星际物流/翘曲/戴森球…）及判定依据、已解锁科技数、里程碑、教程、当前研究与队列。玩家问“我下一步该干什么”“我在什么阶段”时先调用它。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家询问当前进度、下一步该做什么、阶段目标时使用。
- 回答“下一步”时只给当前阶段与下一阶段的必要条件，不要剧透更后面的科技细节。

#### 参数

无参数，调用时传 `{}`。

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `signals` | `object` | 阶段判定信号：是否已解锁关键科技/建筑 |
| `stage` | `string` | 派生阶段（电磁 / 炼油 / 钛 / 星际物流 / 翘曲 / 戴森球…） |
| `stageNote` | `string` | 提示：阶段是派生结果，回答时要说明依据 |
| `unlockedTechs` | `integer` | 已解锁科技数 |
| `tutorials` | `integer` | 已完成教程数 |
| `currentTechId` | `integer` | 当前研究科技 id |
| `currentTechName` | `string` | 当前研究科技名 |
| `techSpeed` | `number` | 研究速度倍率 |
| `queue` | `array` | 研究队列 |
| `milestoneCount` | `integer` | 里程碑达成数 |
| `recentMilestones` | `array` | 最近的里程碑 |
| `display` | `string` | 一行摘要 |

嵌套对象内部字段（未逐个展开）：`logisticsSystem`、`intraplanetaryStation`、`interstellarStation`、`dispenser`、`foundation`、`marker`、`dysonSphereSystem`、`ultraPhoton`、`verticalConstruct`、`combatDrone`、`combatShip`、`id` 等 15 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_capture_view`

**label** 截图 · **能力位** `game.view.v1` · **风险** 0（只读） · **超时** 15000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
截取当前游戏画面（不含 UI），以 JPEG 返回图片本身，并附带可见建筑的屏幕坐标。view=topdown 临时切俯视看平面布局；view=orbit 绕目标点换一个角度；拍完自动还原玩家视角。注意：只能拍到当前行星已渲染的区块，相机不会触发远处地形/建筑加载。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家让你看画面、问布局/摆放/接线是否正确时使用。
- 需要平面布局时用 view=topdown，并用 entityId/center 指定中心、cameraHeight 指定高度（10–150m）。
- 需要换角度时用 view=orbit，配合 entityId/center、distance、pitch、yaw；一次调用只拍一个角度，想看多个方向就分多次调用并改 yaw/pitch/distance。
- 俯视图只覆盖玩家附近已加载的区块；画面缺一块时说明是流送限制，不要当成空地。
- 图上的编号框与 legend 的 index 一一对应；先按编号定位，再结合 game_get_entity / game_get_entities 的数值下结论，不要只凭像素猜配方。
- annotate=false 可关掉标注（例如只想看纯净画面）。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `width` | `integer` | 否 | — | 像素宽，默认 1280，上限 1600 |
| `height` | `integer` | 否 | — | 像素高，默认 720，上限 900 |
| `includeEntities` | `boolean` | 否 | — | 是否附带可见建筑的屏幕坐标，默认 true |
| `annotate` | `boolean` | 否 | — | 是否在图上画编号框并返回 legend，默认 true |
| `view` | `string \| string \| string` | 否 | — | player=当前镜头（默认），topdown=临时俯视，orbit=绕目标点换角度 |
| `entityId` | `integer` | 否 | — | 取景中心：建筑 id（topdown/orbit） |
| `center` | `object` | 否 | — | 取景中心：latitude+longitude；x/y/z 仅兼容旧调用（topdown/orbit） |
| `cameraHeight` | `number` | 否 | — | topdown 高度（米），默认 60，范围 10–150 |
| `distance` | `number` | 否 | — | orbit 环绕半径（米），默认 80，范围 10–500 |
| `pitch` | `number` | 否 | — | orbit 俯角（度），默认 35，范围 5–90；90=正俯视 |
| `yaw` | `number` | 否 | — | topdown/orbit 方位角（度），默认 0 |
| `fov` | `number` | 否 | — | topdown/orbit 视场角，默认 60，范围 20–90 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `width` | `integer` | 图像宽度（像素） |
| `height` | `integer` | 图像高度（像素） |
| `available` | `boolean` | 游戏/相机是否就绪 |
| `reason` | `string` | 不可用原因（例如 `camera_unavailable`） |
| `bytes` | `integer` | JPEG 字节数 |
| `view` | `string` | 实际使用的机位：player / topdown / orbit |
| `camera` | `object` | 机位参数（距离/俯角/方位角/视场角/中心点） |
| `visibleEntities` | `array` | 可见建筑的屏幕坐标：编号 / 名称 / x / y（与图片标注对应） |
| `annotated` | `boolean` | 是否在图上画了编号标注 |
| `legend` | `array` | 标注图例：编号 → 建筑名 |
| `display` | `string` | 一行摘要 |

嵌套对象内部字段（未逐个展开）：`mimeType`、`data`、`center`、`centerSource`、`fov`、`yaw`、`distance`、`pitch`、`location`、`forward`、`id`、`protoId` 等 17 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `game_analyze_area`

**label** 产线分析 · **能力位** `game.analyze.v1` · **风险** 0（只读） · **超时** 15000 ms

#### 模型可见 · 工具描述（tools 数组）

```text
按空间范围聚合产线：各类建筑数量、各配方的机器数与装机产能、耗电、传送带/分拣器/物流塔数量、涉及电网的供需。用于回答“这片区域是什么产线、规模多大、吃多少电”。只给装机口径；实际产出请再用 game_get_production。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“这片产线是干什么的/多大/耗多少电”时使用。
- game_get_production 返回 statsEmpty=true 或 items 为空时，用它看装机产能（可 all=true 扫整颗星球）。
- 结果里 byRecipe 是“机器数 × 配方速度”，不要把它说成实际产出。
- 结果是装机产能，不是实际产出；回答实际产量要结合 game_get_production。
- 范围默认以玩家为中心 60m；可用 entityId 指定中心，或用 all 分析整星球。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `planetId` | `integer` | 否 | — | 行星 id；省略时使用当前所在行星 |
| `entityId` | `integer` | 否 | — | 以该建筑为圆心 |
| `radius` | `number` | 否 | — | 半径（米），默认 60，范围 5–500 |
| `center` | `object` | 否 | — | 中心：latitude+longitude；x/y/z 仅兼容旧调用 |
| `all` | `boolean` | 否 | — | 分析整颗星球，忽略 radius/center |
| `includeEntities` | `boolean` | 否 | — | 是否列出范围内建筑，默认 false |
| `limit` | `integer` | 否 | — | 列出的建筑上限，默认 40，最大 100 |

#### 返回

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `planetId` | `integer` | 同参数 `planetId` |
| `available` | `boolean` | 工厂是否已加载 |
| `area` | `object` | 分析范围：中心 `location` / 半径 / 是否整星 |
| `byRecipe` | `array` | 按配方聚合：配方 id/名 / 机器数 / 装机产能 / 耗电 |
| `categories` | `object` | 按建筑类别计数：类别 → 数量 |
| `totals` | `object` | 汇总：建筑数 / 耗电 / 传送带 / 分拣器 / 物流塔 |
| `powerNetworks` | `array` | 涉及的电网供需 |
| `entities` | `array` | 范围内的建筑 id 列表（可继续用 game_get_entity 下钻） |
| `assumptions` | `array` | 口径假设（装机产能 ≠ 实际产出等） |
| `display` | `string` | 一行摘要 |

嵌套对象内部字段（未逐个展开）：`mode`、`center`、`centerSource`、`radius`、`id`、`protoId`、`protoName`、`category`、`recipeId`、`recipeName`、`buildings`、`installedCraftsPerMin` 等 23 个

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
