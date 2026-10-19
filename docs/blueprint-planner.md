# 蓝图自动布线规划器（移植方案）

目标：把 `antian369/dsp-calc` 的混带蓝图规划器按模块移植进本仓库，
让 `game_generate_blueprint` 能生成“机器 + 传送带 + 分拣器 + 连接槽位”的完整蓝图。

参考实现（木兰宽松许可证 v2，仅移植算法，不复制其 UI）：

| 参考模块 | 行数 | 职责 |
| --- | --- | --- |
| `src/blueprint/parser.js` | ~360 | 蓝图二进制编解码（我们已在 C# 侧实现） |
| `src/global_blueprint.jsx` `BeltUnit` | ~310 | 传送带路径、转弯、多层、优先级 |
| `src/global_blueprint.jsx` `BuildingUnit` | ~700 | 单个生产单元的机器/上下游带/爪子 |
| `src/global_blueprint.jsx` `StationUnit` | ~500 | 物流塔进出料 |
| `src/global_blueprint.jsx` `generate()` | ~120 | 网格占位、行拼接、面积计算 |
| `data/Buildings.json` | 28 KB | 各类建筑的槽位/参数模板 |
| `data/Vanilla.json` | 124 KB | 物品/配方基础数据 |

## 移植顺序

1. **编码器（已完成）**：`BlueprintGenerateTool` 写出标准 blueprint，签名用游戏 `MD5F`。
2. **网格与建筑工厂**：`LayoutGrid`（`matrix[y][x]` 占位）+ `CreateBuilding`（从 `LDB` 取
   `modelIndex`、槽位、参数；不再依赖外部模板 JSON）。
3. **分拣器**：`getInserterScheme(产量, 级别)` + `CreateInserter(begin, end)`：
   - 端点坐标差决定 `yaw`（上→下 180、下→上 0、右→左 270、左→右 90）；
   - `parameters.length` 写入插入距离；
   - `inputFromSlot` / `outputToSlot` 由端点相邻建筑的槽位决定；
   - `inputObjIndex` / `outputObjIndex` 指向本次生成的建筑下标。
4. **传送带**：先支持直线与 90° 转弯，再支持多层/优先级；每段写 `modelIndex`、`yaw`、
   `tilt` 与连接。
5. **生产单元**：先做 `generateDefault`（装配/冶炼/化工/研究通用），再做对撞机/精炼厂。
6. **整图**：上游带 → 生产单元 → 下游带 → 副产/回收，最后算面积与 `dragBoxSize`。

## 槽位解析

参考实现用固定反查表（如 `ASSEMBLER_SOLT_REVERSE`）。我们优先用游戏数据：

- `PrefabDesc.slotPoses` 给出建筑局部槽位；
- 把槽位从“局部米”换算到蓝图网格（`PlanetGrid` 的经纬网格），取与爪子端点最近的槽；
- 找不到时回退到参考实现的固定表。

这一步必须用 Unity 里的 `BlueprintData.LoadBlueprintData` + `BlueprintBuilding` 回读校验，
再进游戏粘贴验证连接。

## v1 验收

- [ ] 单配方、单行：机器 + 上游带 + 下游带 + 爪子，粘贴后能自动进料/出料。
- [ ] 槽位正确：装配机 2 输入 1 输出时，两个输入分别进不同槽。
- [ ] 多行拼接：行间不重叠，面积覆盖全部建筑。
- [ ] 爪子 `parameters.length` 与两端坐标一致。
- [ ] 生成后 `LoadBlueprintData == OK`，且 `buildings` 数量/坐标回读一致。

## v1 不做

- 混带（多种物品共用一条带）；
- 集装分拣器堆叠、四向分流器回收；
- 物流塔自动进出料；
- 喷涂机自动布置。
