# `game_get_entity` 建筑字段覆盖（M4i）

原则：**游戏面板上看得到的，工具里至少有一个对应字段**。分两层：

1. **专门字段**：装配机、矿机、分拣器、传送带、仓储、物流塔——字段按面板语义命名。
2. **通用组件快照**：其余建筑按 `EntityData` 的组件 id 找到 pool，反射导出该组件公有字段
   （最多 30 个；数组给出长度，短的基础类型数组展开）。新增建筑类型不用改工具。

## 专门字段

| 建筑 | 字段 |
| --- | --- |
| 装配机 | `recipeId/Name`、`recipeSeconds`、`speedMultiplier`、`craftsPerMin`、`cycleCount`、`extraCycleCount`、`working`、`forceAccMode`、`inputs[]`（count/needed/incServed）、`outputs[]`、`powerSatisfactionPct`、`status`（working/missing_input/no_power/idle）、`replicating`、`extraSpeed`、`productPerMin`（与装配机面板同源） |
| 矿机 | `productId/Name`、`productCount`、`period`、`orePerMin`、`veinCount`、`totalVeinAmount`、`workstate` |
| 分拣器 | `grade`、`bidirectional`、`stage`、`filterItemId/Name`、`pickTarget/Name`、`insertTarget/Name`、`handItemId/Name/Count/Inc`、`stackCount`、`canStack`、`stackInput/Output`、`delay` |
| 传送带 | `itemsPerSec`、`segLength`、`rangeFrom/To`、`nodeIndex`、`pathId`、`pathCapacity`、`pathNodeCount`、`pathCargoCount`、`cargoItems[]`（`itemId/name/iconTag/count`，按堆数聚合）、`outputId/backInputId/leftInputId/rightInputId` |
| 仓储 | `slots`、`items[]`（itemId/name/iconTag/count，最多 12）、`totalCount` |
| 物流塔 | `isCollector`、`isStellar`、`warperCount/Max`、`idle/workDroneCount`、`idle/workShipCount`、`deliveryDrones/Ships`、`warpEnableDistAu`、`energy/Max`、`chargeKw`、`charging`、`storage[]`（count/inTransit/max/localOrder/remoteOrder/localLogic/remoteLogic） |
| 电力（通用） | `power`：consumerId/networkId/requiredKw/workKw/idleKw；genKw/fuelKw/photovoltaic/wind/geothermal；蓄电器与节点给 networkId |

## 通用组件快照（`components`）

| 建筑 | 组件 | 来源 pool |
| --- | --- | --- |
| 分馏塔 | `fractionator` | `factorySystem.fractionatorPool` |
| 弹射器 | `ejector` | `factorySystem.ejectorPool` |
| 发射井 | `silo` | `factorySystem.siloPool` |
| 研究站 | `lab` | `factorySystem.labPool` |
| 分流器 | `splitter` | `cargoTraffic.splitterPool` |
| 打包机 | `piler` | `cargoTraffic.pilerPool` |
| 流量监视器 | `monitor` | `cargoTraffic.monitorPool` |
| 喷涂机 | `spraycoater` | `cargoTraffic.spraycoaterPool` |
| 储液罐 | `tank` | `factoryStorage.tankPool` |
| 配送器 | `dispenser` | `transport.dispenserPool` |
| 电力设备 | `power_consumer/generator/accumulator/exchanger/node` | `powerSystem.*Pool` |
| 扬声器 | `speaker` | `digitalSystem.speakerPool` |

## 限制

- 反射字段是**组件内部命名**，不同建筑的可读性不一致；熟悉的类型走专门字段。
- 炮塔/信标/护盾等 `DefenseSystem` 使用 `DataPool` 而非数组，暂未纳入。
- 装配机 `productPerMin` 按面板公式（速度/增产剂加速/是否运行）计算，不包含电网供电比例。
- 传送带 `cargoItems` 用堆数聚合，和面板“线路货物数”同源；不返回每个货物的位置。
