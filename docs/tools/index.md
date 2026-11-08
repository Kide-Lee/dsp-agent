# 工具参考

本目录是工具的第二层文档：**提示词原文 + 参数 + 返回字段线索**。模型看到的文本如何拼装，看 [prompt-shape.md](prompt-shape.md)；协议字段与能力位协商，看 [protocol-v1.md](../protocol-v1.md) 附录 B。

> 本页与各分组页由 `sidecar/scripts/gen-tool-docs.mts` 生成，**不要手改**。
> 改工具的 description / promptGuidelines / 参数后运行 `npx tsx sidecar/scripts/gen-tool-docs.mts`。

## 工具清单

共 54 个模型可见工具。

| 工具 | label | 能力位 | 风险 | 超时 | 一句话 |
| --- | --- | --- | --- | --- | --- |
| [`mods_adapter_create`](index.md#mods_adapter_create) | 创建 mod 适配器 | `mod.bridge.v1` | risk 2 | 8000ms | 把某个第三方 mod 的公开静态方法绑定成工具，写入 BepInEx/config/icarus-ii/bridge/<guid>.json 并热载。 |
| [`mods_adapter_delete`](index.md#mods_adapter_delete) | 删除 mod 适配器 | `mod.bridge.v1` | risk 2 | 8000ms | 删除 config/icarus-ii/bridge 下的适配器文件并热载；随包与内置适配器不受影响。 |
| [`mods_api_list`](index.md#mods_api_list) | mod 可调用方法 | `mod.bridge.v1` | 只读 | — | 只读列出某个插件程序集里可被 manifest 调用的公开静态方法：类型、方法、参数、返回值。 |
| [`mods_list`](index.md#mods_list) | 已装 mod 列表 | `mod.bridge.v1` | 只读 | — | 列出除本 mod 以外、当前已启用的 BepInEx 插件：GUID、id、名称、版本、程序集、配置项数量、是否加载失败、适配器状态。 |
| [`ui_click`](index.md#ui_click) | 点击界面控件 | `ui.tree.v1` | risk 2 | 8000ms | 按 ui_snapshot 给出的路径派发一次合成点击（PointerClick）。 |
| [`ui_snapshot`](index.md#ui_snapshot) | 界面控件 | `ui.tree.v1` | 只读 | — | 列出当前可见的 uGUI 可交互控件：层级路径、类型、显示文字、是否可用、Toggle/InputField 的值。 |
| [`game_get_overview`](game-read.md#game_get_overview) | 存档概览 | `game.read.v1` | 只读 | 5000ms | 读取当前存档概览：存档名、游戏时刻、当前行星/星系、玩家经纬度、是否暂停。 |
| [`game_get_research`](game-read.md#game_get_research) | 研究状态 | `game.read.v1` | 只读 | 5000ms | 读取当前研究科技、进度、队列、研究速度与已解锁科技数量。 |
| [`game_copy_blueprint`](game-entity.md#game_copy_blueprint) | 存为蓝图 | `game.entity.v1` | risk 1 | 20000ms | 把指定的一批建筑（entityIds）存成游戏蓝图文件，复用游戏自己的复制逻辑，产物与游戏内复制同源。 |
| [`game_get_entity`](game-entity.md#game_get_entity) | 建筑详情 | `game.entity.v1` | 只读 | 6000ms | 按 entityId 读取单个建筑（或用 entityIds 批量读取）的实际情况，尽量对齐游戏面板：原型、配方、输入/输出槽（数量/需求/增产）、功率与供电满足率、工作状态（… |
| [`game_get_logistics`](game-entity.md#game_get_logistics) | 星际物流 | `game.entity.v1` | 只读 | 20000ms | 汇总所有已加载工厂的物流塔：哪些星球/站点在供给或需求某物品、星际塔与轨道采集器、运输船/无人机与翘曲器数量。 |
| [`game_get_planets`](game-entity.md#game_get_planets) | 星球一览 | `game.entity.v1` | 只读 | 20000ms | 列出银河里所有行星：轨道/类型/半径、是否有工厂与建筑数、电力汇总，以及按宇宙探索等级披露的矿脉。 |
| [`game_render_layout`](game-entity.md#game_render_layout) | 布局图 | `game.entity.v1` | 只读 | 10000ms | 把一片区域的建筑排布画成字符网格（上=+Z，左=-X）：物流站 S、生产设备 M、仓储 B、发电 P、采矿 V、分拣器 >、传送带 =。 |
| [`game_trace_line`](game-entity.md#game_trace_line) | 产线拓扑 | `game.entity.v1` | 只读 | 15000ms | 从一栋建筑出发，沿传送带与分拣器把相连设备串成图：节点（id/名称/类别/星系/行星/经纬度/深度）与有向边。 |
| [`game_capture_view`](game-progress.md#game_capture_view) | 截图 | `game.view.v1` | 只读 | 15000ms | 截取当前游戏画面（不含 UI），以 JPEG 返回图片本身，并附带可见建筑的屏幕坐标。 |
| [`game_get_progress`](game-progress.md#game_get_progress) | 进度阶段 | `game.progress.v1` | 只读 | 6000ms | 读取玩家进度：派生阶段（电磁/炼油/钛/星际物流/翘曲/戴森球…）及判定依据、已解锁科技数、里程碑、教程、当前研究与队列。 |
| [`space_motion`](game-space.md#space_motion) | 动点查询 | `space.read.v1` | 只读 | — | 按动点读位置与状态：玩家 / 建设无人机 / 配送运输机 / 物流运输机 / 星际物流运输船 / 小型运载火箭 / 战斗无人机 / 战舰 / 太空黑雾单位 / 地面黑雾单位 /… |
| [`space_point`](game-space.md#space_point) | 定点查询 | `space.read.v1` | 只读 | — | 按定点聚合：下辖动点、存量、输入/输出/生产/消耗、电力、归属、行星坐标、备忘录。 |
| [`space_region`](game-space.md#space_region) | 区域查询 | `space.read.v1` | 只读 | — | 按区域聚合：成员列表、存量、输入/输出/生产/消耗速度、电力、上级、位置、静态信息、备忘录。 |
| [`game_drop_item`](game-write-basic.md#game_drop_item) | 丢弃物品 | `game.drop.v1` | risk 2 | 8000ms | 把背包里的物品丢到当前行星地面。 |
| [`game_open_panel`](game-write-basic.md#game_open_panel) | 打开面板 | `game.write.v1` | risk 1 | 8000ms | 打开游戏内面板：inventory（背包）、tech（科技树）、starmap（星图）、marker（标记）、mecha（机甲）、replicator（复制器）、statist… |
| [`game_place_marker`](game-write-basic.md#game_place_marker) | 放置标记 | `game.write.v1` | risk 1 | 8000ms | 在玩家当前所在的行星上放置一个定位标记，效果与行星视图右键相同。 |
| [`game_set_research`](game-write-basic.md#game_set_research) | 切换研究 | `game.write.v1` | risk 2 | 8000ms | 把当前研究目标切换为指定科技。 |
| [`game_undo_last_write`](game-write-basic.md#game_undo_last_write) | 撤销上一步 | `game.write.v1` | risk 1 | 8000ms | 撤销最近一次由本 mod 执行的写操作（视角、标记、面板、研究、配方、存储、投料等）。 |
| [`game_feed_input`](game-write-build.md#game_feed_input) | 投料 | `game.build.v1` | risk 2 | 8000ms | 从背包把配方原料直接放进一台生产建筑的输入缓冲。 |
| [`game_set_assembler_batch`](game-write-build.md#game_set_assembler_batch) | 批量配方 | `game.build.v1` | risk 2 | 8000ms | 一次修改多台生产建筑的配方（最多 20 台），只弹一次确认、只压一层撤销。 |
| [`game_set_recipe`](game-write-build.md#game_set_recipe) | 更换配方 | `game.build.v1` | risk 2 | 8000ms | 把一台生产建筑（制造台/熔炉/化工厂/研究站等）的配方改为指定配方。 |
| [`game_storage_deposit`](game-write-build.md#game_storage_deposit) | 存入存储 | `game.storage.v1` | risk 2 | 8000ms | 把背包里的物品放进一台存储建筑（箱子/储液罐/物流站仓储）。 |
| [`game_storage_move`](game-write-build.md#game_storage_move) | 移库 | `game.storage.v1` | risk 2 | 8000ms | 把指定物品从一台存储建筑移动到另一台存储建筑。 |
| [`game_storage_set_filter`](game-write-build.md#game_storage_set_filter) | 存储过滤 | `game.storage.v1` | risk 2 | 8000ms | 设置一台存储建筑（箱子/储液罐/物流站仓储）的物品过滤器；itemId=0 清除过滤器。 |
| [`game_storage_set_limit`](game-write-build.md#game_storage_set_limit) | 存储上限 | `game.storage.v1` | risk 2 | 8000ms | 设置一台存储建筑的容量上限（允许使用的格数）。 |
| [`game_storage_withdraw`](game-write-build.md#game_storage_withdraw) | 从存储取出 | `game.storage.v1` | risk 2 | 8000ms | 把一台存储建筑里的物品取到背包。 |
| [`game_take_output`](game-write-build.md#game_take_output) | 取成品 | `game.build.v1` | risk 2 | 8000ms | 把一台生产建筑当前已产出的成品取到背包，保留配方。 |
| [`game_tether_add`](game-assist.md#game_tether_add) | 方位指示 | `game.tether.v1` | risk 1 | 8000ms | 建立机甲到行星/建筑/矿脉的方位指示，可同时建立多个（最多 20 个）。 |
| [`game_tether_clear`](game-assist.md#game_tether_clear) | 清除方位 | `game.tether.v1` | risk 1 | 8000ms | 清除本 mod 建立的所有方位指示（导航朝向和定位标记）。 |
| [`game_tether_list`](game-assist.md#game_tether_list) | 方位列表 | `game.tether.v1` | 只读 | 5000ms | 列出当前方位指示（行星/建筑/矿脉及名称）。 |
| [`game_todo_add`](game-assist.md#game_todo_add) | 新增游戏目标 | `game.todo.v1` | risk 2 | 8000ms | 在原生左侧“游戏目标”面板新增目标。 |
| [`game_todo_list`](game-assist.md#game_todo_list) | 当前游戏目标 | `game.todo.v1` | 只读 | 5000ms | 读取 AI 创建的原生游戏目标（含轮内待办）及当前值/目标值/阶段，以及目标面板当前是否真的没有显示。 |
| [`game_todo_remove`](game-assist.md#game_todo_remove) | 删除游戏目标 | `game.todo.v1` | risk 2 | 8000ms | 删除 AI 创建的原生游戏目标；groupId 删除整组，goalId 删除单个子目标。 |
| [`game_todo_templates`](game-assist.md#game_todo_templates) | 目标模板 | `game.todo.v1` | 只读 | 5000ms | 列出游戏原生 GoalProto 目标模板：id、名称、判断器、等级、父分组和默认参数。 |
| [`game_todo_write`](game-assist.md#game_todo_write) | 更新待办 | `game.todo.write.v1` | 只读 | 5000ms | 把当前任务清单整份写入游戏左侧目标面板的第一组“AI 待办”。 |
| [`kb_get`](kb.md#kb_get) | 知识库条目 | 本地 | 只读 | — | 按类别 + id/名称/SID 精确读取一条知识库条目（物品、配方、科技、建筑、矿脉、提示、教程、里程碑、指引、术语等）。 |
| [`kb_plan`](kb.md#kb_plan) | 产线规划 | 本地 | 只读 | — | 给定目标物品的每分钟产量，算出要建多少台什么机器、总功率、每条输入要几条传送带，并复用 kb_recipe_tree 给出原料与副产物。 |
| [`kb_recipe_tree`](kb.md#kb_recipe_tree) | 配方树 | 本地 | 只读 | — | 按游戏本体的主配方递归展开一个物品，计算直接输入、原料总量与副产物。 |
| [`kb_search`](kb.md#kb_search) | 知识库搜索 | 本地 | 只读 | — | 在游戏本体知识库里搜索物品、配方、科技、建筑、矿脉，以及提示/教程/里程碑/指引/术语。 |
| [`wiki_page`](wiki.md#wiki_page) | Wiki 页面 | 本地 | 只读 | — | 读取一个中/英文 DSP Wiki 页面的纯文本正文（自动处理重定向，最长 8000 字）。 |
| [`wiki_search`](wiki.md#wiki_search) | Wiki 搜索 | 本地 | 只读 | — | 在中/英文戴森球计划 Wiki 里搜索页面，返回标题、摘要与链接。 |
| [`blueprint_fs`](blueprint.md#blueprint_fs) | 蓝图文件 | 本地 | 只读 | — | 在游戏的蓝图目录里查看和整理文件，类似受限的 ls/cd/pwd/cat/mkdir/rm/rmdir。 |
| [`blueprint_generate`](blueprint.md#blueprint_generate) | 生成产线蓝图 | 本地 | 只读 | — | 按知识库里的真实配方，从原矿开始生成完整产线蓝图（各阶段机器 + 传送带 + 分拣器 + 连接槽位），保存到游戏蓝图目录。 |
| [`blueprint_info`](blueprint.md#blueprint_info) | 蓝图详情 | 本地 | 只读 | — | 读取一个蓝图文件并做格式体检（头部/base64+gzip/md5），返回大小与解码后字节数，不解析建筑。 |
| [`blueprint_save`](blueprint.md#blueprint_save) | 保存蓝图 | 本地 | 只读 | — | 把 blueprint_search 找到的蓝图保存到游戏蓝图目录（Documents/Dyson Sphere Program/Blueprint），支持 GitHub 仓库… |
| [`blueprint_search`](blueprint.md#blueprint_search) | 蓝图搜索 | 本地 | 只读 | — | 在 DSPBluePrints/FactoryBluePrints 仓库和 Dyson Sphere Blueprints 网站里按关键词搜索蓝图（在线按需，不打包内容）。 |
| [`ask_user`](interactive.md#ask_user) | 向玩家提问 | 本地 | 只读 | — | 向玩家展示一个或多个带选项的问题，等待玩家在游戏窗口里选择或输入答案。 |
| [`run_script`](interactive.md#run_script) | 脚本 | 本地 | 只读 | — | 在沙箱里执行一段 JavaScript，用来批量取数、循环、条件判断、聚合，或在确认后批量执行写操作。 |

## 分组

| 文件 | 内容 | 数量 |
| --- | --- | --- |
| [game-read.md](game-read.md) | 只读 · 存档与产线概览 | 2 |
| [game-entity.md](game-entity.md) | 只读 · 建筑、星球与拓扑 | 6 |
| [game-progress.md](game-progress.md) | 只读 · 进度、截图与区域分析 | 2 |
| [game-space.md](game-space.md) | 只读 · 空间变量（区域 / 定点 / 动点） | 3 |
| [game-write-basic.md](game-write-basic.md) | 写操作 · 标记、面板、研究与丢弃 | 5 |
| [game-write-build.md](game-write-build.md) | 写操作 · 配方、投料与仓储 | 9 |
| [game-assist.md](game-assist.md) | 辅助 · 方位指示、目标与待办 | 8 |
| [kb.md](kb.md) | 知识库（sidecar 本地） | 4 |
| [wiki.md](wiki.md) | Wiki（sidecar 本地，联网） | 2 |
| [blueprint.md](blueprint.md) | 蓝图（本地文件 + 社区站点） | 5 |
| [interactive.md](interactive.md) | 交互与脚本 | 2 |

### 参数的“必填”列

文档按 JSON Schema 的 `required` 渲染。当前工具的 TypeBox schema 大多没有设置 `required`，所以会出现整列都是“否”，但工具实现仍要求二选一的情况——例如 `game_get_entity` 的 `entityId` / `entityIds`、`ask_user` 的 `question` / `questions`。这类约束写在工具描述或参数说明里，以那里为准。

## 能力位

mod 在 `hello.capabilities` 里声明了哪一组，sidecar 才注册并广告哪一组。老 mod 看不到新工具，是刻意的（避免 `tool_not_found`）。

| 能力位 | 工具数 | 代表工具 |
| --- | --- | --- |
| `game.read.v1` | 2 | `game_get_overview`、`game_get_research` |
| `game.entity.v1` | 6 | `game_get_entity`、`game_render_layout`、`game_get_planets` |
| `game.progress.v1` | 1 | `game_get_progress` |
| `game.view.v1` | 1 | `game_capture_view` |
| `space.read.v1` | 3 | `space_region`、`space_point`、`space_motion` |
| `game.todo.v1` | 4 | `game_todo_templates`、`game_todo_list`、`game_todo_add` |
| `mod.bridge.v1` | 4 | `mods_api_list`、`mods_adapter_create`、`mods_adapter_delete` |
| `ui.tree.v1` | 2 | `ui_snapshot`、`ui_click` |
| `game.write.v1` | 4 | `game_place_marker`、`game_open_panel`、`game_set_research` |
| `game.drop.v1` | 1 | `game_drop_item` |
| `game.build.v1` | 4 | `game_set_recipe`、`game_set_assembler_batch`、`game_feed_input` |
| `game.storage.v1` | 5 | `game_storage_set_filter`、`game_storage_set_limit`、`game_storage_move` |
| `game.tether.v1` | 3 | `game_tether_add`、`game_tether_clear`、`game_tether_list` |
| `game.todo.write.v1` | 1 | `game_todo_write` |
| 本地（始终注册） | 13 | `ask_user`、`run_script`、`kb_search` |

### 不在模型工具列表里的工具

| 工具 | 状态 | 原因 |
| --- | --- | --- |
| `game_export_prototypes` / `game_export_text` | mod 有实现，不注册 | KB 导出用的内部工具（协议附录 B.5），由 sidecar 导出流程直调，不广告给模型 |
| `game_spawn_item` / `game_teleport` | **尚未实现** | 沙盒专属；`tools/sandbox.ts` 只保留了名字与门禁。见 [prompt-shape.md](prompt-shape.md) 的沙盒段 |
| `mods_config_list` / `mods_config_get` / `mods_config_set` | 内部线级工具 | `mod_<id>_config` 的实现细节，由 sidecar 带固定 `guid` 直调；不广告给模型 |
| `mod_<id>_config` | 动态注册 | 每个已加载的第三方插件一个；插件不在 `hello.extensions` 里时不存在 |
| `mod_<id>_<name>` | 动态注册 | manifest 声明的第三方工具；同样随插件加载/卸载出现和消失 |

所以：mod 侧共 39 个 `Name` 声明，其中 37 个进入模型工具列表，2 个是内部导出工具；第三方 mod 工具按 `hello.extensions` 动态生成，不在固定清单里。

### 返回字段表怎么来的

每个工具「返回」下的字段表：字段名由生成器从 mod 侧 C# 实现里提取，类型与说明来自 `sidecar/scripts/tool-field-notes.mts`（**人工维护**）。C# 里出现、说明表里没有的字段会让 `npm test` 的 `test:tool-docs` 失败，所以不会悄悄漏字段。

嵌套对象只展开一层：对象/数组字段的内部结构写在它自己的说明里（例如 `inputs` 的元素含 `itemId` / `name` / `count`）。更深的结构以 C# 实现为准。

## 通用返回信封

**只有 `game_*` 工具**走 mod 的 `data` 信封（协议附录 B.3）。`kb_*` / `wiki_*` / `blueprint_*` / `ask_user` / `run_script` 是 sidecar 本地工具，返回给模型的是文本内容块，不是 JSON 信封。


```json
{
  "ok": true,
  "data": { "display": "给人看的一句话摘要", "...": "工具自己的字段" },
  "error": null,
  "meta": { "gameTick": 12345, "saveKey": "…", "truncated": false },
  "image": null
}
```

失败时：

```json
{ "ok": false, "data": null, "error": { "code": "not_found", "message": "…", "retryable": false } }
```

模型看到的文本不是原始 JSON 而是渲染结果：`display` → JSON → 尾注。超过 9000 字符截断，追加「请用 offset/limit 缩小范围」。详见 [prompt-shape.md](prompt-shape.md#23-工具结果tool_result)。

### 常见错误码

code 取自 `mod/src/Shared/Protocol/Protocol.cs` 的 `Code*` 常量：

| code | 来源 | 含义 |
| --- | --- | --- |
| `tool_not_found` | sidecar | 调用了未注册的工具 |
| `tool_unavailable` | sidecar | 没有绑定到游戏连接（断线/未进存档） |
| `not_in_game` | mod | 未进入存档，或游戏已退出 |
| `invalid_args` | mod / sidecar | 参数不满足 schema |
| `busy` | mod | 同时执行的工具过多，请串行调用 |
| `timeout` | mod | 超过工具超时或分帧预算 |
| `cancelled` | mod | 调用被取消（断线/换档） |
| `sandbox_required` | sidecar | 沙盒专属工具在未启用沙盒的存档里被调用 |
| `confirmation_required` | mod | 写操作需要确认，但调用方没走确认流程 |
| `confirmation_denied` | mod | 玩家在确认页拒绝 |
| `confirmation_expired` | mod | 确认超时（一次性令牌失效） |
| `confirmation_invalid` | mod | 令牌/参数指纹不匹配 |
| `debug_disabled` | sidecar | 调试直调未开启（sidecar 未带 `--debug-tools`） |
| `tool_required` | sidecar | 强制工具回合没有调用指定工具 |

注意：`stale=true`、`reason="not_found"` 是**返回字段**（`ok` 仍为 true），不是错误码。工具自身的业务失败也可能把 `internal` 作为 code 透出，以 `error.message` 为准。

`confirmation_required` / `confirmation_expired` / `confirmation_invalid` 属于确认流程（`confirm_request` → `confirm_result`）的 code；工具调用被拒时，模型看到的是`confirmation_denied`。

## 写操作的确认与撤销

| 风险 | 权限档位「只读」 | 「受限」（默认） | 「接管」 |
| --- | --- | --- | --- |
| 0 只读 | 直接执行 | 直接执行 | 直接执行 |
| 1 低风险写 | 先确认 | 直接执行 | 直接执行 |
| 2 改存档写 | 先确认 | 先确认 | 直接执行 |

一次性令牌始终校验，权限档位只决定是否弹确认页。「接管」也要求模型先确认目标明确（见 [prompt-shape.md](prompt-shape.md#6-权限段)）。

撤销只保留**一层**，且只对真正压栈的写操作有效：`game_place_marker`、`game_open_panel`、`game_set_research`、`game_set_recipe`、`game_set_assembler_batch`、`game_storage_*`、`game_feed_input`。

以下写操作**不可撤销**，但会占用栈顶（`WriteActionStack.MarkNonUndoable`），让 `game_undo_last_write` 明确告知“最近一步不可撤销”，而不是跳过它去撤销更早的操作：`game_drop_item`、`game_take_output`、`game_tether_add`、`game_tether_clear`、`game_todo_add`、`game_todo_remove`。
