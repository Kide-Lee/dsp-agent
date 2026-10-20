# 蓝图生成（M4j）

> 早期版本曾计划在 mod 侧用 C# 重新实现 dsp-calc 的规划器；该实现已删除。
> 现在只有一条链路：**KB 产线规划 → sidecar 的 dsp-calc 布局 → 蓝图文件**。

## 1. 链路

```
blueprint_generate (sidecar 本地工具)
  → KnowledgeBase.plan(item, {rate, buildingTier, depth})
      按游戏 tick + 机器速度算每个阶段的机器数（与 kb_plan 同一口径）
  → planner.mjs planBlueprint()
      把每个阶段映射成 dsp-calc 的 ProduceUnit；原矿只来自 plan.raw
  → vendor/dsp-calc 的 MixedConveyerBeltBlueprint
      生成机器/传送带/分拣器/槽位/连接
  → 写 Documents/Dyson Sphere Program/Blueprint/IcarusII/*.txt（可用 dir 指定子目录）
```

mod 只负责打开蓝图浏览器/粘贴，**不再生成蓝图**。

参考实现（木兰宽松许可证 v2，仅算法与数据，不含 UI）：

| 模块 | 用途 |
| --- | --- |
| `sidecar/vendor/dsp-calc/src/global_blueprint.js` | 混带总线、建筑单元、传送带、分拣器 |
| `sidecar/vendor/dsp-calc/src/blueprint/parser.js` | 蓝图二进制编解码 |
| `sidecar/vendor/dsp-calc/data/Buildings.mjs` | 建筑槽位/尺寸模板 |
| `sidecar/vendor/dsp-calc/data/Vanilla.mjs` | 物品/配方基础数据（版本可能与游戏有偏差，机器数不以此为准） |

## 2. 正确性口径

- **机器数只信 `kb_plan`**：`recipes[].buildingCount` 已按
  `craftsPerMin × timeSeconds ÷ 60 ÷ 机器速度` 计算并向上取整。
  `planner.mjs` 不再自己算产量。
- **全链路**：`plan.recipes` 里的每个中间阶段都会生成机器；
  只有 `plan.raw` 里的原矿/流体标记为 `isMineralized`，作为蓝图的输入。
- **原矿不是采矿机**：默认从“原矿投入”开始，矿石需要玩家自己送进物流站；
  蓝图不包含矿机/矿脉。
- **`from` 起点**：`ore`（默认）/ `smelted`（从铁块、磁铁、铜块等冶炼产物开始）/
  物品名或 id（从任意中间产物开始）。被停止的物品计入 `raw`，不再生成上游机器。
- **rows**：每个阶段如果机器数大于行数，会拆成多份相同的建筑单元，
  由 dsp-calc 分行；总机器数不变，宽度近似按 `1/rows` 下降。
- **单张总线容量**：vendor 的混带总线最多 3 条蓝带。
  超过时自动按 2..8 张均分重试（例如 300/min 电磁涡轮 → 3×100/min，
  600/min → 5×120/min），每张独立落盘；全部铺下才达到目标产量。
- **生成后自检**：回读蓝图，按 building id 校验实际放置数与规划数；
  不一致时写入 `warnings`，不静默宣称成功。
- **档位**：mod 在 `save.bind.blueprintTiers` 上报已解锁的机器/带子/分拣器/集装机；
  `blueprint_generate` 默认使用它，显式 `buildingTier`/`beltType`/`insertType` 覆盖。
  未上报时退回 base / 蓝带 / 极速（与旧行为一致）。
- **物品别名**：先查本地 curated 表（蓝马达/绿马达/白糖/各色糖），
  再按需查中文 Wiki 的 `insource:"别名"` + 物品页 `|别称=`；
  命中写 `<dataDir>/wiki-aliases.json`（30 天），多候选返回 `candidates` 让玩家确认。

## 3. 当前限制

- 多产物/多副产物配方仍受 dsp-calc 布局器限制（例如多条氢来源、精炼副产）；
  工具会返回明确错误或 `warnings`。
- 别名只覆盖中文社区叫法；英文别名、玩家自造缩写仍可能miss。
- 所有布局结果仍需**实机粘贴验证**；离线只校验机器数、阶段与尺寸。

## 4. 回归

```bash
cd sidecar
npm run build
node test/blueprint-plan.mjs
```

覆盖：60/min 电磁涡轮 29 台机器 / 7 个阶段、14 台熔炉 + 15 台制造台、
rows 宽度下降且机器数不变、300/min→3 张、600/min→5 张、`from=smelted` 15 台机器、
`from=电动机` 9 台机器、被动档位（mk3/低档/显式覆盖）、知识库缺失报错；
别名另见 `test/wiki-alias.mjs`（curated / Wiki `|别称=` / 缓存 / 多候选）。

实机检查项见 [m4j-verification.md](m4j-verification.md)。
