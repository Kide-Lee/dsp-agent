# icarus-ii

[《戴森球计划》(Dyson Sphere Program)](https://store.steampowered.com/app/1366540/) 的游戏内 AI 助手 mod。

首次使用：按 `R` 打开聊天窗后，点击窗口右上角的**设置按钮**，在「模型」页填写
DeepSeek API Key（密钥只写不读，只保存在本机 sidecar 配置里）。

游戏里按 **`R`** 打开聊天窗，可以问机制、要攻略、让 AI 读取你当前的存档状态给建议，
也可以在**你确认之后**让 AI 执行游戏内操作。

> 状态：**M4 已完成并通过实机验收**（2026-10-24）。
> - M0 已完成并实机验证：插件加载 → 窗口开关（`R`）→ 与 sidecar 握手 → 消息回显。
> - M1 已完成：sidecar 接入 [pi](https://github.com/earendil-works/pi) + DeepSeek，
>   mod 侧实现首次配置引导（指向右上角设置按钮）与配置上报。
> - M2 已实现：`tool_call`/`tool_result` 协议、按连接路由的 ToolHost、六个只读工具
>   （overview / player / planet / power / research / production）、分帧采集与快照缓存、
>   聊天窗工具卡片、按存档隔离的对话 session（再次进入存档自动回放最近对话）、工具结果图标短码。
> - M2.5 已实现：**思考档位**（输入栏右侧下拉框：关闭 / 低 / 高 / 极限）+ 输出区的
>   **可折叠思考块 / 工具块**（收起时显示摘要，点 header 展开）。
> - 权限档位（输入栏右侧「权限」下拉框：只读 / 受限 / 接管）决定 AI 调写工具时
>   是否弹确认；只读全部要确认，受限只放行 risk 1，接管全部自动同意但保留一次性令牌校验。
> - M2.6 已实现：**多供应商 / 多模型**设置页（供应商列表 + 密钥状态 + API 地址 +
>   模型增删），聊天窗「模型」下拉框即时切换；支持自定义 OpenAI 兼容端点。
> - M2.7 已实现：左侧 **chat-index** 提问索引——每条用户提问一个可点击链接，点击定位到对应消息；
>   每行带删除按钮：同时删掉聊天窗显示、会话文件条目与模型上下文（`history.delete`）。
> - M4a–M4j 已实现并通过实机验收：建筑详情/建筑列表/进度阶段/截图（含聊天窗缩略图）、
>   `kb_plan` 量化计算、`game_analyze_area` 区域产线、`run_script` 脚本沙箱（默认只读，写脚本需一次确认）、
>   Wiki/蓝图仓库、蓝图卡片，以及按知识库真实配方生成的**完整产线蓝图**。
> - M4 写操作已实现并通过实机验收：复用 `choose` 的确认页（`question/explain` + 红拒绝/蓝同意）、
>   sidecar 确认钩子 + mod 一次性令牌（绑定 callId/tool/argsHash，60s 单次），
>   以及 `game_open_panel` / `game_set_research`
>   + `game_undo_last_write`。确认页崩溃、原生按钮动效、`explain` 垂直居中、建筑经纬度输出、
>   卡片点击聚焦和研究切换判定已修复。
>   当前状态与后续计划见 [docs/current-status.md](docs/current-status.md)。
> - M3 已实现并随 M4 实机验收：**游戏本体知识库**——运行时从 LDB / Locale / Wiki 导出物品、配方、科技、
>   建筑、提示、教程、里程碑、指引与术语；`kb_search` / `kb_get` / `kb_recipe_tree` / `kb_plan`
>   由 sidecar 本地查询，配方原料树与机器数/电力不再靠模型心算。schema 与清洗规则见
>   [docs/kb-v1.md](docs/kb-v1.md)。
> - 真实 DeepSeek 端点、KB 导出、工具调用、蓝图落盘与卡片已在实机跑通；
>   M4 验收已全部通过，验收用的临时清单已从仓库移除，结论保留在
>   [docs/current-status.md](docs/current-status.md)。
>
> 设计与路线图见 [design.md](design.md)，当前状态与下一步见 [docs/current-status.md](docs/current-status.md)，待定想法见 [docs/ideas.md](docs/ideas.md)。

---

## 它长什么样

```
┌─ Icarus II ──────────────────────── 就绪 ─┐
│ 系统  已连接 sidecar（会话 3f9a…，模型 deepseek-v4-flash）│
│ 你    你好                                │
│ 助手  你好！有什么可以帮你的？              │
│                                           │
├───────────────────────────────────────────┤
│ 输入问题…（Enter 发送，Esc 关闭）          │
└───────────────────────────────────────────┘
```

## AI 能读什么（M2）

| 工具 | 内容 |
| --- | --- |
| `game_get_overview` | 存档、游戏时间、所在星球/星系、玩家经纬度 |
| `game_get_player` | 机甲能量/护盾、背包物品 |
| `game_get_planet` | 行星矿脉汇总、建筑计数 |
| `game_get_power` | 电网需求/供给/容量/储能/满足率（kW、kWh） |
| `game_get_research` | 当前研究、进度、队列、已解锁数量 |
| `game_get_production` | 按物品聚合的产量/消耗/库存（/min），缺料排序 |

## AI 能看什么（M4a）

| 工具 | 内容 |
| --- | --- |
| `game_get_entity` | 单个建筑：原型、配方、功率、分拣器/传送带/仓储/物流塔、星系/行星/经纬度；`stale` 标记 id 已被回收 |
| `game_get_entities` | 建筑分页列表，可按原型 / 名称 / 配方过滤 |
| `game_get_progress` | 派生阶段（前期/行星内物流/星际物流/戴森球）+ 判定信号、里程碑、教程、研究队列 |
| `game_capture_view` | 截取当前画面（不含 UI，JPEG），并附可见建筑屏幕坐标与经纬度；`view=topdown` 可临时切俯视，`view=orbit` 可绕目标点换角度（一次一个机位，换角度就多次调用）；默认画编号框并返回 legend |
| `game_render_layout` | 把区域建筑排布画成字符网格，判断布局/走线 |
| `game_copy_blueprint` | 把选中的一批建筑存成游戏蓝图文件（复用游戏复制逻辑） |
| `game_get_planets` | 跨行星总览：轨道、工厂、电力、按探索等级披露的矿脉 |
| `game_get_logistics` | 全银河物流塔的供给/需求与站点，定位跨星供应链 |
| `game_trace_line` | 沿传送带/分拣器追踪上下游拓扑 |
| `game_analyze_area` | 按范围聚合产线：机器/配方计数、装机产能、耗电、物流设施、涉及电网 |

截图会作为图片进入模型上下文，并显示在聊天窗的工具卡里（自动展开、等比缩略图）。
`ModelEntry.input` 已覆盖 pi 目录里"只收文本"的旧标注；老配置会读时兜底，不会静默丢图。
图片不落盘、不混进 JSON 数据，只在调用 `game_capture_view` 时产生。

上面的读工具只读；写工具（镜头/标记/面板/研究/撤销）在 M4 提供，执行前必须经过玩家确认。

## AI 能查什么（M3）

| 工具 | 内容 |
| --- | --- |
| `kb_search` | 在物品/配方/科技/建筑/矿脉/提示/教程/里程碑/指引/术语中搜索（默认过滤剧情剧透） |
| `kb_get` | 按类别 + id/名称/SID 读取完整条目 |
| `kb_recipe_tree` | 递归展开主配方，算直接输入、原料总量、副产物（支持 `amount` / `rate` / `depth`） |
| `kb_plan` | 按目标产量计算机器数、总功率、传送带，并给出原料与副产物（`rate` 必填） |

## AI 能查/生成什么（M4d–M4j）

| 工具 | 内容 |
| --- | --- |
| `wiki_search` / `wiki_page` | 搜索并读取中/英文 DSP Wiki，回答带来源链接 |
| `blueprint_search` / `blueprint_info` | 在线检索 DSPBluePrints 仓库与 Dyson Sphere Blueprints 网站（带来源署名），并做格式体检 |
| `blueprint_save` | 直接把蓝图保存到游戏蓝图目录，打开蓝图界面即可用，无需退出游戏下载 |
| `blueprint_generate` | 按知识库真实配方生成完整产线（各阶段机器 + 带 + 爪）；单张总线容量不足时自动拆成多张 |
| `blueprint_fs` | 在蓝图目录里 `ls/cd/pwd/cat/mkdir/write/rm/rmdir`；默认保存到 `IcarusII/`，不能越出蓝图根 |
| `run_script` | 在 QuickJS-WASM 沙箱里跑脚本：`dsp.call` 同步调用 game_* 工具，可循环/筛选/求和；默认只读，含写工具时先弹一次脚本级确认，执行期凭租约最多写 10000 次；无 fs/net/process |

## AI 能做什么写操作（M4/M5）

| 工具 | 说明 |
| --- | --- |
| `game_place_marker` | 在行星上新建标记；可撤销 |
| `game_open_panel` | 打开背包/科技树/星图/标记/机甲/复制器/统计/蓝图面板 |
| `game_set_research` | 切换当前研究目标；只允许前置科技已满足的科技 |
| `game_drop_item` | 把背包物品丢到当前行星地面；需手动拾回，不进入撤销栈 |
| `game_set_recipe` | 更换一台生产建筑的配方；先把机器内原料/产物退回背包 |
| `game_set_assembler_batch` | 批量更换生产建筑配方（≤20 台）；一次确认、一层撤销 |
| `game_storage_set_filter` | 设置/清除存储建筑过滤器；可撤销 |
| `game_storage_set_limit` | 设置存储建筑可用格数上限；可撤销 |
| `game_storage_move` | 在两台存储建筑之间移动物品；预检目标容量 |
| `game_storage_deposit` | 把背包物品存入存储；满时退剩余 |
| `game_storage_withdraw` | 从存储取出物品到背包；背包满时留剩余 |
| `game_feed_input` | 把背包原料直接投进生产建筑（绕过分拣器/传送带） |
| `game_tether_add` | 建立机甲到行星/建筑/矿脉的方位指示 |
| `game_tether_clear` | 清除本 mod 建立的方位指示 |
| `game_tether_list` | 列出当前方位指示 |
| `game_take_output` | 取出一台生产建筑的成品，保留配方（不进入撤销栈） |
| `game_undo_last_write` | 撤销最近一次可撤销写操作（单层栈；也需确认） |

写操作采用 sidecar 确认 + mod 一次性令牌双门禁；拒绝、超时、换档、断线都会取消。

本地工具（`kb_*` / `run_script`）也会在聊天窗里显示工具卡（“量化计算”“知识库检索”“脚本”），
数据来自 sidecar 的 `tool_activity` 事件，不经过游戏侧执行。

`blueprint_generate` 默认**从原矿投入开始**（不含采矿机/矿脉，需要自己把矿石送进蓝图里的物流站），
会展开知识库里的全部中间阶段；单张混带总线最多承载 3 条蓝带，超过时自动拆成多张，
每张独立可粘贴。物品别名（蓝马达/绿马达/白糖等）先查内置表，再按需查 Wiki 的物品
`|别称=` 并缓存 30 天；对应多个物品时会先问玩家。想让蓝图从铁块/钛块等中间产物开始，
传 `from=smelted` 或 `from=铁块`。下载/生成/游戏复制默认都保存到 `IcarusII/`；
AI 也可以直接用 `blueprint_fs` 查看、建子目录、写任意合法蓝图文本，或永久删除（会先确认）。
机器/带子/分拣器档位由 mod 在 `save.bind` 里被动上报，
不需要模型额外查询；显式参数可以覆盖。多产物/多副产物配方目前仍有布局器限制，工具会返回 `warnings`。

**蓝图卡片**：生成 / 下载 / 复制蓝图成功后，sidecar 会在本轮回答末尾追加一张卡片
（协议附录 A.9），显示名称、产量、建筑数、占地、作者/版本与来源；
点「查看」打开游戏蓝图浏览器并定位到该文件，点「使用」直接进入粘贴预览。
卡片是运行期按数据创建的，不需要改 prefab；sidecar 会持久化卡片记录，
重进存档时随 `save.bound.history` 一起回放。

脚本与模型走**同一个工具注册表**：白名单来自当前连接已注册的工具，因此权限/路由/超时不会被绕过。

知识库在**进入存档后**由 mod 分帧导出到 sidecar，缓存在
`%APPDATA%/icarus-ii/kb/ldb-v1-r2-<游戏版本>-<语言>.json`。
游戏版本或语言变化会自动重建；导出失败时 `kb_*` 返回 `kb_unavailable`，
助手会说明“知识库还在建立”，而不是凭记忆编数值。

配方树会把副产物按同名原料抵扣；剩余副产物单列。例：
「造 10 个量子芯片」→ 直接输入、原料总量、剩余副产物一次给出。

工具结果里的 `iconTag` 是游戏图标短码，AI 可以在物品/建筑/科技名前写成 `\tag;`；
游戏自带的 UGUI 不支持 `<b>` 加粗，mod 会把残留的 `<b>` 转成亮白强调。

## 架构一览

```
游戏进程（Windows / Proton）
├── BepInEx 5
│   └── IcarusII.dll          UI、热键、游戏状态、操作执行
└── sidecar: icarus-ii-sidecar.exe
      内嵌 pi 的 Agent SDK     LLM 调用、工具编排、知识库
```

两者通过 `127.0.0.1` 上的 WebSocket 通信，协议见 [docs/protocol-v1.md](docs/protocol-v1.md)。

**sidecar 是自包含单文件可执行程序**：内嵌 Node 运行时与全部依赖，
所以你**不需要安装 Node.js**，也不需要 `npm install`。

---

## 安装

### 前提

| 项 | 说明 |
| --- | --- |
| 游戏 | Dyson Sphere Program（`0.10.30.3100` 验证通过） |
| mod 加载器 | **BepInEx 5.4.17+（win_x64）**；用 r2modman 会自动安装，手动安装需自备；5.4.23.5 亦验证通过 |

> 版本说明：Thunderstore 社区包是 **5.4.17**，本项目也按 5.4.17 编译（避免 BepInEx 打印
> "plugin targets a wrong version"）。5.4.23.5 同样实测可用；它 changelog 里那条
> `Fix for unity 2022.3.62f3` 只影响取不到 `Application.isBatchMode` 的构建，与本游戏无关。

### 玩家安装（推荐：r2modman）

1. 安装 [r2modman](https://thunderstore.io/c/dyson-sphere-program/p/ebkr/r2modman/)，在游戏列表里选
   **Dyson Sphere Program**，使用默认配置档即可。
2. 进入 **Online**，搜索 `IcarusII`（作者 `from_dsp_bwiki`），安装；BepInEx 5.4.17
   会作为依赖自动装好。
3. 安装完成后点 **Start modded** 启动游戏。**不要从 Steam 直接启动**——r2modman 的配置档
   不会生效，表现就是「装了没效果」。
4. 进游戏按 `R`，窗口出现即安装成功。

> r2modman 把 mod 和 BepInEx 放在自己的配置档目录，不会写进游戏根目录。
> 更新也在 r2modman 里完成；不要手动往配置档里覆盖 DLL。

### 手动安装（开发 / 无管理器）

> 只有明确不用 r2modman，或需要自己管理游戏目录时才走这条路。手动安装和 r2modman
> 配置档是两套目录，**不要混着装**。

```bash
# 1) 安装 BepInEx 5.4.17（或 5.4.23.5）到游戏目录（解压即用）
#    下载：https://github.com/BepInEx/BepInEx/releases/tag/v5.4.17
#    选 BepInEx_x64_5.4.17.0.zip，解压到 Dyson Sphere Program/ 下

# 2) 安装本 mod
./install.sh
#   或： ./install.sh --game-dir "/path/to/Dyson Sphere Program"

# 3) 启动游戏，按 R
```

### Linux / Proton（Steam Deck 同样适用）

用 r2modman 时，**Start modded** 会处理 Proton 环境，不需要手动改 Steam 启动项；
下面只针对手动安装 / 自行管理 BepInEx 的情况。

Wine 默认会优先加载内置的 `winhttp.dll`，盖掉 BepInEx 的代理 DLL。
`install.sh` 会自动向前缀注册表写入**按可执行文件的覆盖**，因此**不需要**改 Steam 启动项：

```ini
[Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
"winhttp"="native,builtin"
```

如果你更习惯传统做法，也可以用 Steam 启动选项替代：

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

> 写注册表前请**完全退出 Steam**——Wine 退出时会把内存里的注册表写回，
> 覆盖运行期间的改动。脚本会检测并提醒。

---

## 使用

| 操作 | 说明 |
| --- | --- |
| `R` | 打开 / 关闭聊天窗口 |
| `Esc` | 关闭窗口 |
| `Enter` | 发送 |
| `Shift+Enter` | 换行 |
| 输入栏右侧「思考」下拉框 | 关闭 / 低 / 高 / 极限。开启后模型会先输出推理，以可折叠的「思考」块显示（收起时只留摘要） |
| 输入栏右侧「权限」下拉框 | 只读 / 受限 / 接管。只读 = 所有写操作都先弹确认；受限 = 镜头/标记/面板/方位/撤销等低风险写操作直接放行，改存档的操作仍需确认；接管 = 所有写操作直接执行、不再询问 |
| 输入栏右侧「模型」下拉框 | 在已配置的多个供应商 / 模型之间切换；下一轮对话生效 |
| 输入栏左侧「工具」按钮 / 输入 `/` | 在输入框上方打开工具列表；输入 `/` 后继续打字可筛选 |
| 选择工具 / 输入 `/<工具名>` | 本轮强制模型至少调用该工具；`<工具名>` 可用列表里的英文名或中文显示名 |

> 工具列表只展示 **sidecar 当前连接实际注册**的工具（来自 `ready.payload.toolCatalog`），
> 所以换存档、断线或能力变化后列表会跟着变。选中一项会把 `/工具名` 插到输入框开头，
> 后续文字照常输入；发送时 mod 会把它解析成 `prompt.requiredTool`。
> 如果模型本轮没有调用指定工具，sidecar 会先纠正一次，仍未调用则报
> `tool_required`，不会静默给出普通回答。

> 输出区里的 **思考块** 与 **工具块** 都收在一个 `loading` 分组里：模型思考/调用工具时，
> 分组头显示「终端运行中…」和游戏自带的转圈图；这一轮结束后变成「已完成」并收起整组，
> 只留一行标题和展开图标，点标题行随时展开/收起（运行中也可以点）。
> 展开后每个 **思考块** / **工具块** 仍是独立的折叠条目：收起时显示一行摘要，点击看详情；
> 工具块失败时会自动展开。一旦**正文开始**，这一轮此前的思考块和工具块会全部收起。
> 一次提问里模型可能「思考 → 工具 → 再思考」，每段思考都是独立一块，
> 按发生顺序排在工具块之间。重进存档时这些思考/工具记录会随会话一起还原（保持收起）。
>
> 每条消息下面都有**脚注**：回答的脚注是「游戏时间 + 复制 + 刷新」，提问气泡下面是
> 「游戏时间 + 复制」。点刷新会清掉这一轮的回答、保留提问，再让模型重新推理一遍
> （只对最后一轮显示刷新——会话是线性的，重开中间一轮会打乱上下文）。

### 模型页（设置 → 模型）

模型页参考 dsh Web UI 的组织方式：左栏是供应商列表（带密钥状态点），右栏是当前
供应商的编辑卡片——API 密钥（只写不读）、API 地址、模型列表（可增删），点模型行
把它设为当前模型；修改点右下角原生的「应用设置」提交。模型输入框旁的
**「获取可用模型」** 会从 pi 内置目录或供应商的 `/models` 端点拉取列表，
在弹层里点「+ 添加」即可加入当前供应商（未知供应商需要先填 API 地址）。
新增供应商时可用**预设下拉框**——第一项固定是**「自定义…」**（选中后清空 ID 输入并聚焦，
手填供应商 ID 和 API 地址），其余项由 `provider.catalog` 从 pi 内置目录自动拉取、顺序与
目录一致；目录没拉到也能在 ID 输入框直接填写。API 密钥旁的**「检测」**
按钮会做一次 `GET /models` 轻量探测，直接显示 Key 是否可用和延迟。

「检测」和「获取可用模型」共用同一套端点规则：按供应商使用各自的鉴权头
（OpenAI 兼容用 `Authorization: Bearer`，Anthropic 用 `x-api-key`，Gemini 用
`x-goog-api-key`）；粘贴成 `.../v1/chat/completions` 的完整端点会自动削成 base URL，
漏写 `/v1` 时自动补一次。两者不会再出现「检测通过但拿不到模型」；检测成功后
如果聊天需要的地址与输入框不同（例如缺少 `/v1`），输入框会被自动补全。
密钥只保存在 sidecar 的 `config.json`（0600），不会回传到游戏 UI。

> 已知限制：sidecar 默认不读取系统代理设置。需要代理才能访问上游时，请给游戏进程
> 设置 `HTTPS_PROXY` 与 `NODE_OPTIONS=--use-env-proxy`（Node 24 才支持该参数），
> 或改用可直连的中转地址。

### 关于 R 键冲突

`R` 在 DSP 里是**建造模式下旋转建筑**的键。本 mod 会自动避让：

| 情境 | `R` 的行为 |
| --- | --- |
| 建造 / 拆除 / 升级 / 改造 / 蓝图模式 | 交给游戏 → 旋转建筑 |
| 漫游、飞行、航行、战斗、挖矿 | 打开聊天窗口 |
| 全屏界面（星图、戴森球编辑器） | 不响应 |
| 窗口已打开 | 不响应（只有 `Esc` 能关） |

如果你仍觉得别扭，可以在 r2modman 配置档里的 `BepInEx/config/icarus-ii.cfg`
（手动安装则是游戏目录下的同名文件）里改成别的键（例如 `F8`，该键在 DSP 中未被占用）。

---

## 配置

r2modman 配置档里的 `BepInEx/config/icarus-ii.cfg`；手动安装则是
`<游戏目录>/BepInEx/config/icarus-ii.cfg`。

```ini
[General]
Hotkey = R
HotkeyRequireNoBuildTool = true   ; 建造模式下把 R 让给游戏
Language = auto
Streaming = true
DebugToolConsole = false   ; 输入框 !tools / !tool 调试直调（需 sidecar --debug-tools）

[Sidecar]
LaunchMode = spawnExe    ; spawnExe | connectOnly | spawnNode
Host = 127.0.0.1
Port = 8765
ConnectTimeoutMs = 3000
StartupTimeoutMs = 15000

[UI]
FontScale = 1.0
Width = 780
Height = 520
MaxTranscriptLines = 500
```

| 配置项 | 说明 |
| --- | --- |
| `LaunchMode=spawnExe` | 由插件自动拉起随包的 `icarus-ii-sidecar.exe`（默认，推荐） |
| `LaunchMode=connectOnly` | 插件只连接；你自己先启动 sidecar（便于调试 / 多存档共用） |
| `Hotkey` | 任意 Unity `KeyCode` 名 |
| `DebugToolConsole=true` | 开启输入框 `!tools` / `!tool` 调试直调；只读白名单，不经过 AI。`spawnExe` 会自动给 sidecar 加 `--debug-tools`，`connectOnly` 需手动加 |

### 调试直调 `!tool`

开发/排障用：不调用 AI，直接执行一个只读工具并显示原始返回值。

```text
!tools
!tool game_get_power {}
!tool game_get_entities {"limit": 5}
!tool kb_search {"query": "量子芯片"}
```

- 需要 `DebugToolConsole=true`，且 sidecar 以 `--debug-tools` 启动。
- 结果作为系统调试块显示，不写入对话历史、不消耗 token。
- 只允许只读工具；`game_copy_blueprint`、`blueprint_save`、`blueprint_generate`、
  `blueprint_fs`、`run_script` 不在白名单内。

原生 Linux 跑 `connectOnly` 时，sidecar 与游戏不在同一 Wine 环境，需用
`ICARUS_BLUEPRINT_DIR` 指向 Proton prefix 里的 `Documents/Dyson Sphere Program/Blueprint`
（否则 `blueprint_fs`/`blueprint_save` 会写到宿主 home 下）。

---

## 排障

| 现象 | 处理 |
| --- | --- |
| 从 Steam 启动没有 mod | 用 r2modman 的 **Start modded** 启动；r2modman 的配置档不会注入 Steam 直接启动 |
| 按 `R` 没反应 | 看 r2modman 配置档（手动安装为游戏目录）里的 `BepInEx/LogOutput.log` 有没有 `[热键]`/`聊天窗口已创建` |
| 窗口显示"未连接" | 日志里搜 `[sidecar]`；确认 `icarus-ii-sidecar.exe` 在配置档的 `BepInEx/plugins/IcarusII/sidecar/` |
| 设置窗口里没有「模型」页 | mod 没加载；查日志里有没有 `Icarus II 0.4.0 已加载` |
| 中文显示成方块 | 日志里看字体来源；`borrowed-from-game-ui` 正常，`builtin-arial(no-cjk)` 说明没借到游戏字体 |
| BepInEx 完全没加载 | 用 r2modman 时先确认是 `Start modded`；手动安装 + Proton 下多半是 `winhttp` 覆盖没生效 |
| 建造时按 R 弹窗 | 检查 `HotkeyRequireNoBuildTool` 是否为 `true` |

---

## 从源码构建

```bash
scripts/fetch-deps.sh          # 拉取 BepInEx 参考程序集到 .deps/
scripts/build-mod.sh           # 编译 IcarusII.dll

# 可选：从本地游戏资源离线生成知识库（开发/离线测试用；运行时会自动导出）
PYTHONPATH=.scratch/pylib python3 tools/build-kb.py \
  --prototypes dsp-game/Assets/Resources/Prototypes \
  --locale-dir dsp-game/Locale/2052 --tutorial-dir dsp-game/Wiki/Tutorial \
  --locale zh-CN --game-version 0.10.30.3100 --out .scratch/kb/kb-zh.json

cd sidecar && npm install      # 首次
scripts/build-sidecar.sh       # 生成自包含的 icarus-ii-sidecar.exe

scripts/install.sh --with-bepinex
```

### 运行测试

```bash
# JSON / 协议单测（无需游戏、无需 sidecar）
dotnet run --project tests/ProtocolTests

# 端到端：用与 mod 相同的协议实现去连真实 sidecar
node sidecar/dist/index.js serve --port 8799 --token testtoken --agent echo &
dotnet run --project tests/ProtocolTests -- e2e ws://127.0.0.1:8799 testtoken
```

---

## 仓库结构

```
design.md              设计文档（含已核实的游戏 API 证据）
docs/protocol-v1.md    mod ↔ sidecar 通信协议（唯一契约）
mod/                   C# BepInEx 插件
  src/Shared/          协议/JSON/传输层（刻意 Unity-free，可独立测试）
  src/IcarusII/        Unity 侧：UI、热键、游戏访问
sidecar/               Node/TypeScript 本地服务
  src/kb/              M3 知识库：导出编排 / 索引 / kb_* 工具
  test/                离线回归（含 kb / kb-tools / kb-session）
tests/ProtocolTests/   本机可跑的验证程序
tools/MetaDump/        开发辅助：转储游戏程序集元数据
tools/build-kb.py      开发辅助：从本地游戏资源离线生成知识库
docs/kb-v1.md          知识库 schema、文本清洗与缓存
scripts/               构建 / 安装
```

## 安全

- **不向模型暴露 shell 与文件系统**：pi 的内置工具（`bash`/`read`/`write`/`edit`）全部关闭。
- **写操作双重门禁**：sidecar 侧确认钩子 + mod 侧一次性令牌（M4 实现）。
- **凭证**：API Key 只存在 sidecar 自己的数据目录，插件不接触。
- sidecar 只监听 `127.0.0.1`，握手带 token。

## 许可

见 `LICENSE`。第三方组件许可见 `THIRD-PARTY.md`（BepInEx 为 LGPL-2.1，pi 为 MIT，Node.js 为 MIT）。
