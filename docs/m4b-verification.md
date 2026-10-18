# M4b / M4c 验收：量化计算与区域产线

> M4b = `kb_plan`（机器/电力/传送带），M4c = `game_analyze_area`（范围聚合）。
> 离线部分：`sidecar/test/kb-plan.mjs`（38 项）覆盖公式与降级；本文只列**必须在游戏里对**的项目。

---

## 1. `kb_plan`（M4b）

前置：知识库必须是**运行时导出**（`ldb-v1-r2-*`）。离线 KB 没有 `prefabDesc`，会退化成 1x。

- [ ] 问「建一条 60/min 的磁线圈产线要多少机器、多少电」，模型调用 `kb_plan`。
- [ ] 抽查 1–2 个配方：
      `buildingCount = ceil(每分钟制作次数 × 配方秒数 ÷ 60 ÷ 速度倍率)`，
      与游戏里实际摆出来的机器数一致。
- [ ] `buildingTier=mk2/mk3` 时机器数按速度倍率下降，且选中的机器原型正确。
- [ ] `powerKw` 与游戏电力面板的「需求」量级一致（误差 < 5%；不含待机与电网损耗）。
- [ ] `raw` 与 `kb_recipe_tree` 对同一 `rate` 的结果**完全一致**（两个工具不能互相矛盾）。
- [ ] 副产物仍按同名原料抵扣；剩余副产物出现在 `byproducts`。
- [ ] 老缓存（`ldb-v1-*` 无 r2）不会被复用：升级后首次进存档会重新导出。
- [ ] 回答里转述了 `assumptions`（未建模增产剂/矿脉/堆叠）。

## 2. `game_analyze_area`（M4c）

- [ ] 站到一条产线中间问「这片是干什么的、多大、耗多少电」。
- [ ] `totals.buildings` 与目测数量同量级；`byRecipe` 的配方与机器数对得上。
- [ ] `installedCraftsPerMin` ≈ 机器数 × 60/配方秒数 × 速度倍率
      （注意：这是**装机**产能，不是实际产出）。
- [ ] `powerNetworks` 的 `requiredKw/servedKw` 与电网面板一致（同一电网 id 可对照）。
- [ ] `entityId` 指定中心时，范围以那栋建筑为圆心。
- [ ] `all=true` 时覆盖整颗星球，且耗时仍 < 1s（万级实体）。
- [ ] 远程行星（未加载工厂）返回 `available=false`，不是异常。
- [ ] 回答里区分「装机」与「实际产出」，并提示实际产出看 `game_get_production`。

## 3. `run_script`（M4d）

- [ ] 问「把这颗星球所有配方的机器数按数量排序」，模型调用 `run_script`。
- [ ] 脚本里 `dsp.call` 能拿到与直接调用相同的结果（同 tool_result 形状）。
- [ ] `console.log` 的输出出现在工具结果里。
- [ ] 脚本只读：尝试 `dsp.call('game_set_recipe', ...)` 被拒绝并报「不在脚本白名单」。
- [ ] `while(true){}` 会被超时中断，游戏不卡死（中断在主线程外，WASM 执行很快）。
- [ ] 脚本里 `typeof process/require/fetch` 都是 `undefined`。
- [ ] 连续跑 5 次 `run_script`，sidecar 内存不持续增长（WASM 模块复用、context 释放）。
- [ ] **打包后**（`scripts/build-sidecar.sh`）脚本仍可用：单文件 SEA 里 WASM 是内联的。

## 4. 回归

- [ ] `game_get_entities` / `game_get_entity` 仍正常（同一份实体池）。
- [ ] 截图与图片卡片仍正常（M4a 已验收的部分不回归）。
- [ ] `hello.capabilities` 现在包含 5 组：read / entity / progress / view / analyze。

---

## 已知限制

| 项 | 说明 |
| --- | --- |
| 范围形状 | 行星局部 XZ 平面上的球形切分，靠近极点会与游戏网格有偏差 |
| 拓扑 | 只做计数与聚合，不识别「一条产线」的连通分量（下一步） |
| 增产剂 | `kb_plan` 不建模 |
| 矿脉 | 只算「到厂需求」，不算矿机数量与矿脉产率 |
| 拓扑 | `game_analyze_area` 无连线，不识别连通分量 |
