# 知识库 schema v1（M3）

> 目标：让 AI 的配方、科技、建筑、提示/教程/里程碑来自**游戏本体**，而不是模型记忆。
> 协议层见 [protocol-v1.md](protocol-v1.md) 附录 B.1.1 / B.5。

---

## 1. 数据流

```
游戏进程（mod）                                     sidecar
─────────────────────────────────────────────────────────────────
LDB.items/recipes/techs/...  ──game_export_prototypes──▶  KbData(items/recipes/...)
Locale/<lcid>/*.txt          ──game_export_text─────────▶  KbData(texts)
Wiki/Tutorial/*-<lang>.txt   ──game_export_text─────────▶  KbData(texts)
                                                          │
                                                          ├─ 内存索引（名称/ID/SID）
                                                          ├─ kb_search / kb_get
                                                          └─ kb_recipe_tree
                                                          │
                                                     %APPDATA%/icarus-ii/kb/
                                                     ldb-v1-r2-<ver>-<locale>.json
```

- 触发时机：`save.bind`（进入存档）后。缓存命中则直接加载；缺失/版本变化才导出。
- 导出是 `tool_call.internal=true`，mod 不显示工具卡。
- 首问如果知识库还在建立，sidecar 最多等 2.5s；仍未就绪时 `kb_*` 返回 `kb_unavailable`。

## 2. schema

顶层：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schemaVersion` | `1` | 当前固定 1 |
| `gameVersion` | string | 例如 `0.10.30.3100`；缓存键的一部分 |
| `locale` | string | BCP-47，例如 `zh-CN` |
| `builtAt` | string? | ISO 时间 |
| `source` | `runtime-export` / `offline-extract` | 数据来源 |
| `items` | `KbItem[]` | 物品/建筑原型 |
| `recipes` | `KbRecipe[]` | 配方 |
| `techs` | `KbTech[]` | 科技 |
| `models` | `KbModel[]`? | 建筑模型（名称/类型） |
| `veins` | `KbVein[]`? | 矿脉 |
| `signals` | `KbSignal[]`? | 图标/信号 |
| `texts` | `KbText[]`? | M3b 提示/教程/里程碑/指引/术语 |

### 2.1 `KbItem`

只保留 AI 有用的字段：

`id`, `sid`, `name`, `description`, `type`, `stackSize`, `isFluid`, `isEntity`,
`canBuild`, `gridIndex`, `iconTag`, `miningFrom`, `produceFrom`,
`mainRecipeId`, `handRecipeId`, `building`。

`building` 来自 `ItemProto.prefabDesc`（运行时才有）：

`assemblerSpeed`, `assemblerRecipeType`, `workEnergyPerTick`, `minerPeriod`, `beltSpeed`,
`storageCol`, `storageRow`, `stationMaxEnergyAcc`, `isCollectStation`。

> `assemblerRecipeType` 是 `PrefabDesc` 的配方类型枚举值，`kb_plan` 靠它把配方映射到机器；
> 缺了它就只能按 1x 估算。

> 注意：`prefabDesc` **不在 LDB 的 asset 里**，离线提取拿不到；需要机器数/建筑能耗时
> 必须用运行时导出。

### 2.2 `KbRecipe`

`id`, `sid`, `name`, `description`, `type`, `handcraft`, `explicit`,
`timeSpend`（tick）, `timeSeconds`（tick ÷ 60）, `inputs[]`, `outputs[]`,
`nonProductive`, `iconTag`。

`inputs`/`outputs` 是 `{ id, count }`。

### 2.3 `KbTech`

`id`, `sid`, `name`, `description`, `conclusion`, `level`, `maxLevel`,
`preTechs[]`, `preTechsImplicit[]`, `items[]`, `itemPoints[]`, `hashNeeded`,
`unlockRecipes[]`, `unlockFunctions[]`, `unlockValues[]`, `published`,
`isHiddenTech`, `isObsolete`, `iconTag`（运行时另有 `unlockText`）。

### 2.4 `KbText`（M3b）

`kind`: `tip | tutorial | milestone | goal | prompt | glossary | achievement | journal`

| 字段 | 说明 |
| --- | --- |
| `id` | 数字 id；术语表用字符串 key |
| `title` | 标题（提示类可能只是「顾问提示 N」） |
| `body` | 清洗后的正文 |
| `phase` | 阶段/层级（Goal 的 Level；其余暂为 0） |
| `spoiler` | `none` / `low` / `story`；用于剧透过滤 |
| `source` | `base.txt` / `prototype.txt` / `Wiki/Tutorial/...` / `dictionary.txt` |
| `templated` | 正文仍含 `{0}` 等模板参数 |
| `unresolvedKeys` | 正文仍含 `$LKEY_n;`（清洗后一般已替换成 `[按键]`，此标记保留原始事实） |
| `extra` | Goal 的 `level/parentId/children`、Milestone 的坐标、Journal 的 `dataTypes` 等 |

## 3. 文本来源（M3b）

| 内容 | 位置 | 备注 |
| --- | --- | --- |
| 顾问字幕 | `Locale/<lcid>/base.txt` 的 `顾问字幕*` | 100 条；`AdvisorTipProto.Script` 是 key |
| 里程碑 | `base.txt` 的 `里程碑*` | 名称/描述/达成条件 |
| 成就 | `base.txt` 的 `成就*` | 名称/描述 |
| 控制提示 | `base.txt` 的 `prompt*` | 含 `$LKEY_n;` 按键占位符 |
| 指引条目 | `Locale/<lcid>/prototype.txt` 的 `指引*` | `GoalProto.Name` 是 key |
| 术语表 | `Locale/<lcid>/dictionary.txt` | key 以 `(字典)` 结尾 |
| 教程正文 | `Wiki/Tutorial/tutorial-*-<lang>.txt` | **不在 Locale**；布局文本 |
| 教程标题 | `base.txt`，`TutorialProto.Name` 是 key | |

语言后缀：`zh/en/fr/de/es/ja/ko/ru`，与 LCID 的映射见 `tools/build-kb.py`。

### 3.1 清洗规则

导出前统一处理（mod 的 `ExportPrototypesTool.Clean` / `tools/build-kb.py` 的 `normalize`）：

1. `\r\n`、`\\r\\n`、`\r` → `\n`；NBSP/thin space/zero-width → 普通空格。
2. 去掉富文本标签 `<color=...>`、`<size=...>` 等。
3. 去掉游戏图标短码 `\\tag;`（`\tag;`）。
4. `$LKEY_n;` → `[按键]`，同时标记 `unresolvedKeys`。
5. 保留 `{0}` / `{1}` 模板参数，并标记 `templated`；不要把它当事实。
6. 合并空白、压缩 3 个以上连续换行。
7. `Goal` 的标题用 `Name.Translate()`；`TooltipText` 只是兜底，不要当本地化 key。

> 运行时 `Localization.Translate()` 只做“key → 文本”，**不解析** `$LKEY` 和 `{0}`；
> 这两步是 UI 层做的，所以知识库必须自己清洗。

## 4. 缓存与版本

- 路径：`%APPDATA%/icarus-ii/kb/ldb-v1-r2-<gameVersion>-<locale>.json`
- `r2` 是缓存修订号：M4b 新增 `building.assemblerRecipeType` 后，老缓存缺字段会让
  `kb_plan` 退化成 1x 估算，因此换文件名强制重导（schema 仍是 v1）。
  （Proton：`steamapps/compatdata/1366540/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii/kb/`）。
- 键：`gameVersion + locale`；游戏更新或切换语言后重新导出。
- 写入：临时文件 + rename；权限 `0600`。
- 文本导出失败：保留结构化知识库（M3a），只记 warn。
- 原型导出失败：整个知识库不可用，`kb_*` 返回 `kb_unavailable`。

## 5. 工具语义

### `kb_search(query, kinds?, limit?, spoiler?)`

- 名称/ID/SID/描述的**子串**匹配；中文按字符子串，英文按词。
- 评分：精确名称 100、前缀 50、包含 30、ID 80、SID 40、描述 10。
- 返回 `kind + id + name + summary + iconTag`；模型再用 `kb_get`/`kb_recipe_tree`。
- `spoiler`：`none` / `low`（默认）/ `story`。默认不返回剧情/教程类 `story` 条目；
  玩家明确问剧情时才传 `story`。`kb_get` 是精确读取，不受此限制。

### `kb_get(kind, idOrName)`

- 数字 → id；否则精确名称或 SID。
- 文本类支持 `kind:id` 或标题精确匹配。
- 不存在返回 `not_found`，不要猜。

### `kb_recipe_tree(item, amount?, rate?, depth?, recipeId?, from?)`

- 选主配方规则与 `ItemProto.FindRecipes` 一致：结果下标最小，其次 recipe id 最小。
- `amount`：目标物品数量（默认 1）；`rate`：目标物品每分钟产量（给出时忽略 amount）。
- `depth`：默认 8，上限 16；到上限的项按原料处理，`depthLimited=true`。
- 循环：路径里出现过的物品按原料处理，`cycles[]` 列出。
- 副产物：与同名原料**抵扣**；仍有剩余才进 `byproducts[]`。
- `from`：`ore`（默认，从原矿）/ `smelted`（从冶炼产物开始）/ 物品名或 id
  （从该中间产物开始）；被停止的物品计入 `raw`，不再展开上游。
- 返回 `tree`（直接输入）、`raw`（原料汇总）、`byproducts`、`cycles`、`notes`。
- 注意：机器数/建筑能耗需要 `building` 字段（运行时导出）；M3 的配方树只算物料，
  M4b 起改用 `kb_plan` 算机器与电力。

### `kb_plan(item, rate, depth?, recipeId?, buildingTier?, buildingId?, beltSpeed?, from?)`

- `rate` **必填**：目标物品每分钟产量。
- 机器数：`craftsPerMin × timeSeconds ÷ 60 ÷ speed`，再**向上取整**；
  `exactCount` 保留未取整值，供回答"余量多少"。
- 机器选择：按 `recipe.type` 匹配 `building.assemblerRecipeType`，按 `assemblerSpeed` 升序
  取档（`base` / `mk2` / `mk3`）；`buildingId` 可直接指定。
- 电力：`buildingCount × workEnergyPerTick × 60 ÷ 1000`（kW），不含待机与电网损耗。
- 传送带：每条输入流按 `beltSpeed`（默认 6 个/秒）折算，取最大路数。
- `from`：与 `kb_recipe_tree` 同口径；蓝图生成默认 `ore`，可用 `smelted` 从铁块/钛块开始。
- 原料/副产物：直接复用 `kb_recipe_tree` 的结果（同一套抵扣规则）。
- **不建模**（写在 `assumptions` 里，必须转述给玩家）：增产剂、矿脉产率、建筑堆叠。
- 离线 KB 没有 `prefabDesc` ⇒ 没有 `building`，机器数按 1x 估算并给出 warning；
  只有运行时导出才能给出正确的机器数与功率。

## 6. 离线生成（开发/测试）

```bash
# 依赖：pip install UnityPy
PYTHONPATH=.scratch/pylib python3 tools/build-kb.py \
  --prototypes dsp-game/Assets/Resources/Prototypes \
  --locale-dir dsp-game/Locale/2052 \
  --tutorial-dir dsp-game/Wiki/Tutorial \
  --locale zh-CN --game-version 0.10.30.3100 \
  --out .scratch/kb/kb-zh.json

# 只导出结构化数据（M3a）
... --no-texts
```

- 全量规模参考：175 物品 / 162 配方 / 315 科技 / 664 模型 / 14 矿脉 / 40 信号 /
  100 顾问提示 / 22 教程 / 42 里程碑 / 148 指引 / 28 控制提示 / 128 成就 /
  42 日志 / 181 术语；实际入库约 690 条文本（完全为空的条目会被过滤），JSON 约 750KB。
- 离线 fixture 只保证**结构与算法**；具体数值以运行时导出为准（游戏版本可能不同）。
- 不要把全量游戏文本随 mod 分发；运行时从玩家自己的安装读取，只缓存在 `%APPDATA%`。

## 7. 测试

| 测试 | 覆盖 |
| --- | --- |
| `sidecar/test/kb.mjs` | schema、搜索、剧透过滤、精确查询、配方树（多级/副产物/循环/深度/rate）、文本类 |
| `sidecar/test/kb-tools.mjs` | kb_* 工具、剧透过滤、未建立降级、分块导出、internal、缓存、失败不崩 |
| `sidecar/test/tools.mjs` | `ready.tools` 含 6 game + 3 kb；无 game 能力时仍含 kb |
| `sidecar/test/pi-tools.mjs` | 真实 pi 会话看到 game + kb 工具并执行 `game_get_overview` |
| `sidecar/test/pi-loopback.mjs` | 纯对话只注册 kb_*，不注册 game_* |

全量：`cd sidecar && npm test`；mod 侧：`scripts/build-mod.sh`。

## 8. 已知限制

- 建筑参数只有运行时才有；离线 KB 的 `building` 为空。
- 文本模板（`{0}`）和按键占位符（`$LKEY`）已清洗/标记，但不能凭空还原运行时数值；
  回答时要说明“这是模板/以游戏内为准”。
- 提示/教程可能包含剧透；M3b 只打了 `spoiler` 标签，工具暂未按玩家进度过滤。
- 目前是内存索引，不是 FTS5；数据量 <1MB，搜索 <20ms 足够。若以后接 wiki/大量文本，
  再评估 `node:sqlite` FTS5（Node 24 可用）。
