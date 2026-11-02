# 蓝图生成

> mod 只负责打开蓝图浏览器和粘贴，蓝图由 sidecar 生成。
> 布局只有一条路径：**总线（混带）布局**；放不下就按张数拆分，没有线性链或旧布局器回退。

## 1. 链路

```
blueprint_generate (sidecar 本地工具)
  → KnowledgeBase.plan(item, {rate, buildingTier, depth})
      按游戏 tick + 机器速度算每个阶段的机器数（与 kb_plan 同一口径）
  → planner.mjs planBlueprint()
      busSpecFromPlan() 把阶段排成 DAG，layoutBus() 摆机器、带子、分拣器
  → validateParsed()
      碰撞 / 分拣器长度 / 带子相邻 / 区域尺寸 / 未知 ID，任何一条不过就报错
  → 写 Documents/Dyson Sphere Program/Blueprint/IcarusII/*.txt（可用 dir 指定子目录）
```

数据依赖：

| 模块 | 用途 |
| --- | --- |
| `sidecar/vendor/dsp-calc/src/blueprint/parser.js` | 蓝图二进制编解码（0.10.35 的 content 字段已兼容） |
| `sidecar/vendor/dsp-calc/data/Buildings.mjs` | 建筑模板（模型编号、槽位） |
| `sidecar/vendor/dsp-calc/data/Vanilla.mjs` | 物品/配方基础数据（版本可能滞后，机器数不以此为准） |
| `sidecar/src/blueprint/colliders.mjs`、`slots.mjs`、`ports.mjs` | 自己从游戏反解的碰撞盒、槽位、端口几何 |

## 2. 正确性口径

- **机器数只信 `kb_plan`**：`recipes[].buildingCount` 已按
  `craftsPerMin × timeSeconds ÷ 60 ÷ 机器速度` 计算并向上取整。
- **全链路**：`plan.recipes` 里的每个中间阶段都会生成机器；只有 `plan.raw` 里的原矿/流体是蓝图输入。
- **原矿不是采矿机**：默认从“原矿投入”开始，矿石由玩家送进物流站或接在带子标签上。
- **`from` 起点**：`ore`（默认）/ `smelted`（从铁块、磁铁、铜块等冶炼产物开始）/ 物品名或 id。
- **单阶段配方**：只有一个生产阶段（例如只造铁块、氘核燃料棒）也能出图。
- **布局方式**：`layout: 'bus'`（默认，单条混带；放不下按张数拆）或 `'factory'`
  （每个配方阶段一个模块，模块之间用物流站供货/收货，整条产线出一张图）。
  复杂产品（小火箭、宇宙矩阵）用 `factory`，不会再被拆成多张。
- **蓝图统计**：返回 `report`，含建筑清单、传送带/分拣器数量、每个物流站的仓储物品与端口方向、
  原料总量、面积与空间利用率。
- **rows**：行数是偏好；按它算的宽度超过 240 或高度超过 50 时自动分页/加宽。
- **拆张**：单张总线容量不够时按提示张数拆分（最多 512 张，总预算 8 秒）；
  到上限会明确报错并建议降产量或分线。
- **供料方式**：`supply: 'belt'`（默认，物品标签）或 `'station'`（行首物流站接出料带，最多 3 种原料）。
- **生成后自检**：回读蓝图跑硬校验，并把校验错误作为异常抛出，不静默产出坏图。
- **档位**：mod 在 `save.bind.blueprintTiers` 上报已解锁的机器/带子/分拣器/集装机；
  `blueprint_generate` 默认使用它，显式 `buildingTier`/`beltType`/`insertType` 覆盖。
- **物品别名**：先查本地 curated 表（蓝马达/绿马达/白糖/各色糖），
  再按需查中文 Wiki 的 `insource:"别名"` + 物品页 `|别称=`；
  命中写 `<dataDir>/wiki-aliases.json`（30 天），多候选返回 `candidates` 让玩家确认。

## 3. 当前限制

- 总线布局放不下的建筑（分馏塔 2314 这类带子直连机器、2312 等超大建筑）会直接报错，不再回退。
- 物流站供料的“星际需求”写不进蓝图（游戏 ApplyParameters 不生效），贴完需要玩家手动点；
  `factory` 布局的模块间供货走“本地供给/需求”，同一星球内有效。
- 所有布局仍需实机粘贴验证；离线只做几何与连接校验。

## 4. 回归

```bash
cd sidecar
npm run build
node test/blueprint.mjs
node test/blueprint-plan.mjs
node test/blueprint-emit.mjs
node test/blueprint-validate.mjs
node test/blueprint-tiers.mjs
```

覆盖：电磁涡轮 60/min（29 台/7 阶段）、300/min→2 张、600/min→5 张、1200/min→10 张、
rows 宽度变化、`from=smelted`/`from=电动机`、单阶段配方、档位、站供料、
真实站蓝图夹具（0.10.35 content 字段）、碰撞/分拣器/带子/尺寸/未知 ID。
