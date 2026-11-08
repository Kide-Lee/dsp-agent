# 只读 · 空间变量（区域 / 定点 / 动点）

三种主语各一个工具，`op` 与 `docs/dsp-agent.xlsx` 的方法行同名：区域问“这片范围里有什么、产出多少”，定点问“这栋建筑/这个矿脉是谁的、在哪”，动点问“那架无人机现在在哪、在干什么”。不支持的类型与方法组合会直接报错并列出可用方法，不会返回空结果。

共 3 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `space_region`

**label** 区域查询 · **能力位** `space.read.v1` · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
按区域聚合：成员列表、存量、输入/输出/生产/消耗速度、电力、上级、位置、静态信息、备忘录。区域类型：恒星系 / 恒星 / 行星 / 电网 / 工厂 / 太空黑雾巢穴 / 地面黑雾基地 / 戴森球 / 世界区域 / 行星区域 / 蓝图。恒星系包含恒星本体、行星、太空黑雾巢穴与戴森球；恒星只列自己的戴森云与戴森壳。op 与 dsp-agent 表的方法行同名，省略 target 时按玩家所在位置解析。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“某个区域里有什么/产出多少/耗多少电/在哪”时使用；不要自己拿经纬度去拼半径。
- op=storage 是存量，op=input/output 是物流进出，op=production/consumption 是生产消耗，两者不可相加。
- stat.windowNative=false 表示该对象没有分档统计，window 不生效，不要拿它当十分钟均值。
- op=navigation 不接受：导航是写操作，请改用 game_tether_add。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `op` | `string \| string \| string \| string \| string \| string \| string \| string \| string \| string \| string` | 是 | — | list=成员 storage=存量 input/output=物流进出 production/consumption=生产消耗 power=电力 parent=上级 location=位置 information=静态信息 note=备忘录 |
| `type` | `string \| string \| string \| string \| string \| string \| string \| string \| string \| string \| string \| string` | 否 | — | 区域类型；auto 时按玩家所在位置推断（在行星上=planet，否则=star_system） |
| `target` | `string \| object` | 否 | — | 对象实例：字符串给 id（planetId / starId / networkId:seedEntityId / 蓝图路径）；world_box 与 planet_patch 可传对象带 min/max 或 latMin/latMax/lonStart/lonSpan |
| `window` | `string \| string \| string \| string \| string \| string` | 否 | — | 统计时间窗，默认 10m |
| `unit` | `string \| string \| string` | 否 | — | 输出单位，默认 perMin |
| `basis` | `string \| string` | 否 | — | actual=实际统计（默认），installed=按配方与建筑数算的理论值 |
| `itemFilter` | `string` | 否 | — | 统计时按物品名或 id 过滤 |
| `kind` | `string` | 否 | — | list 时按成员类别过滤：building / vein / vegetation / planet / star / df_space_unit 等 |
| `filter` | `string` | 否 | — | list 时按成员名字或 id 过滤 |
| `depth` | `integer` | 否 | minimum=1，maximum=4 | parent 的层数，默认 1 |
| `radius` | `number` | 否 | — | 几何区域半径（米），默认 200 |
| `frame` | `string \| string \| string` | 否 | — | location 的坐标系，默认 auto |
| `precision` | `integer` | 否 | minimum=0，maximum=6 | 经纬度小数位，默认 3 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

未从实现提取到顶层字段；以实际返回为准。

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `space_point`

**label** 定点查询 · **能力位** `space.read.v1` · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
按定点聚合：下辖动点、存量、输入/输出/生产/消耗、电力、归属、行星坐标、备忘录。定点类型：建筑 / 矿脉 / 植被 / 太空黑雾建筑 / 地面黑雾建筑 / 世界坐标 / 行星坐标。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家指着某栋建筑/某个矿脉问“它属于谁/在哪/多少电/进出多少”时使用。
- 省略 target 时先看玩家瞄准的对象，再退到最近的同类；找不到会明确报错，不会静默换目标。
- 矿脉的 output 是按开采机数量算出的理论值（basis=installed），矿脉自身没有实际统计。
- op=navigation 不接受：导航是写操作，请改用 game_tether_add。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `op` | `string \| string \| string \| string \| string \| string \| string \| string \| string \| string` | 是 | — | list=下辖动点 storage=存量 input/output=物流进出 production/consumption=生产消耗 power=电力 parent=归属 location=坐标 note=全息信标备忘录 |
| `type` | `string \| string \| string \| string \| string \| string \| string \| string` | 否 | — | 定点类型；auto 时默认 building |
| `target` | `string` | 否 | — | id：entityId / veinId / vegeId / enemyId；省略时按玩家瞄准或最近解析 |
| `window` | `string \| string \| string \| string \| string \| string` | 否 | — | 统计时间窗，默认 10m |
| `unit` | `string \| string \| string` | 否 | — | 输出单位，默认 perMin |
| `basis` | `string \| string` | 否 | — | actual=实际统计（默认），installed=按配方算的理论值 |
| `itemFilter` | `string` | 否 | — | 统计时按物品名或 id 过滤 |
| `kind` | `string` | 否 | — | list 时按成员类别过滤 |
| `filter` | `string` | 否 | — | list 时按成员名字或 id 过滤 |
| `frame` | `string \| string \| string` | 否 | — | location 的坐标系，默认 auto |
| `precision` | `integer` | 否 | minimum=0，maximum=6 | 经纬度小数位，默认 3 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

未从实现提取到顶层字段；以实际返回为准。

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---

### `space_motion`

**label** 动点查询 · **能力位** `space.read.v1` · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
按动点读位置与状态：玩家 / 建设无人机 / 配送运输机 / 物流运输机 / 星际物流运输船 / 小型运载火箭 / 战斗无人机 / 战舰 / 太空黑雾单位 / 地面黑雾单位 / 垃圾块。位置带 gameTick，跨回合比较前要确认是同一 tick 的快照。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家问“那架无人机/那艘船在哪、在干什么、还剩多少货”时使用。
- 动点会移动：本工具只给当前快照，没有追踪；需要新位置就再调一次，不要拿旧值推算。
- op=storage 只对玩家与运输类动点有效；op=parent 对垃圾块返回无归属。
- op=navigation 不接受：动点无法用 tether 追踪。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `op` | `string \| string \| string \| string \| string` | 是 | — | list=玩家下辖动点 storage=携带物品 parent=归属 location=位置 information=状态 |
| `type` | `string \| string \| string \| string \| string \| string \| string \| string \| string \| string \| string \| string` | 否 | — | 动点类型；auto 时默认 player |
| `target` | `string` | 否 | — | id：craftId / enemyId / trashId / rocketId / stationGid:shipIndex；省略时取最近的一个 |
| `precision` | `integer` | 否 | minimum=0，maximum=6 | 经纬度小数位，默认 3 |
| `offset` | `integer` | 否 | — | 分页偏移，默认 0 |
| `limit` | `integer` | 否 | — | 返回条数上限，默认 25 |

#### 返回

未从实现提取到顶层字段；以实际返回为准。

> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 [index.md](index.md#通用返回信封)。

---
