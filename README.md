# icarus-ii

[《戴森球计划》(Dyson Sphere Program)](https://store.steampowered.com/app/1366540/) 的游戏内 AI 助手 mod。

首次使用：打开窗口后按提示粘贴 DeepSeek API Key（输入不回显，也不会进聊天记录）。

游戏里按 **`R`** 打开聊天窗，可以问机制、要攻略、让 AI 读取你当前的存档状态给建议，
也可以在**你确认之后**让 AI 执行游戏内操作。

> 状态：**M3（游戏本体知识库）**。
> - M0 已完成并实机验证：插件加载 → 窗口开关（`R`）→ 与 sidecar 握手 → 消息回显。
> - M1 已完成：sidecar 接入 [pi](https://github.com/earendil-works/pi) + DeepSeek，
>   mod 侧实现首次密钥引导与配置上报。
> - M2 已实现：`tool_call`/`tool_result` 协议、按连接路由的 ToolHost、六个只读工具
>   （overview / player / planet / power / research / production）、分帧采集与快照缓存、
>   聊天窗工具卡片、按存档隔离的对话 session（再次进入存档自动回放最近对话）、工具结果图标短码。
> - M2.5 已实现：**思考档位**（输入栏右侧下拉框：关闭 / 高 / 最高）+ 输出区的
>   **可折叠思考块 / 工具块**（收起时显示摘要，点 header 展开）。
> - M2.6 已实现：**多供应商 / 多模型**设置页（供应商列表 + 密钥状态 + API 地址 +
>   模型增删），聊天窗「模型」下拉框即时切换；支持自定义 OpenAI 兼容端点。
> - M2.7 已实现：左侧 **chat-index** 提问索引——每条用户提问一个可点击链接，点击定位到对应消息；
>   每行带删除按钮：同时删掉聊天窗显示、会话文件条目与模型上下文（`history.delete`）。
> - M4a–M4d 已实现（离线验证；实机待验收）：建筑详情/建筑列表/进度阶段/截图（含聊天窗缩略图）、
>   `kb_plan` 量化计算、`game_analyze_area` 区域产线、`run_script` 只读脚本沙箱。
> - M3 已实现（离线验证；实机待验收）：**游戏本体知识库**——运行时从 LDB / Locale / Wiki 导出物品、配方、科技、
>   建筑、提示、教程、里程碑、指引与术语；`kb_search` / `kb_get` / `kb_recipe_tree` / `kb_plan`
>   由 sidecar 本地查询，配方原料树与机器数/电力不再靠模型心算。schema 与清洗规则见
>   [docs/kb-v1.md](docs/kb-v1.md)。
> - ⚠️ **真实 DeepSeek 端点与游戏内数值尚未实测**（开发环境没有 API Key；本机未启动游戏）。
>   协议、工具链路、知识库导出/索引/配方树、超时/断线/非法结果已用离线假游戏验证；
>   实机验收清单见 [docs/m2-verification.md](docs/m2-verification.md) 与
>   [docs/m3-verification.md](docs/m3-verification.md)。
>
> 设计与路线图见 [design.md](design.md)，待定想法见 [docs/ideas.md](docs/ideas.md)。

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
| `game_get_overview` | 存档、游戏时间、所在星球/星系、玩家坐标 |
| `game_get_player` | 机甲能量/护盾、背包物品 |
| `game_get_planet` | 行星矿脉汇总、建筑计数 |
| `game_get_power` | 电网需求/供给/容量/储能/满足率（kW、kWh） |
| `game_get_research` | 当前研究、进度、队列、已解锁数量 |
| `game_get_production` | 按物品聚合的产量/消耗/库存（/min），缺料排序 |

## AI 能看什么（M4a）

| 工具 | 内容 |
| --- | --- |
| `game_get_entity` | 单个建筑：原型、配方、功率、分拣器/传送带/仓储/物流塔、坐标；`stale` 标记 id 已被回收 |
| `game_get_entities` | 建筑分页列表，可按原型 / 名称 / 配方过滤 |
| `game_get_progress` | 派生阶段（前期/行星内物流/星际物流/戴森球）+ 判定信号、里程碑、教程、研究队列 |
| `game_capture_view` | 截取当前画面（不含 UI，JPEG），并附可见建筑的屏幕坐标 |
| `game_analyze_area` | 按范围聚合产线：机器/配方计数、装机产能、耗电、物流设施、涉及电网 |

截图会作为图片进入模型上下文，并显示在聊天窗的工具卡里（自动展开、等比缩略图）。
`ModelEntry.input` 已覆盖 pi 目录里"只收文本"的旧标注；老配置会读时兜底，不会静默丢图。
图片不落盘、不混进 JSON 数据，只在调用 `game_capture_view` 时产生。

工具只读，写操作（建造/拆除/改配方）留到 M4，并且必须经过确认。

## AI 能查什么（M3）

| 工具 | 内容 |
| --- | --- |
| `kb_search` | 在物品/配方/科技/建筑/矿脉/提示/教程/里程碑/指引/术语中搜索（默认过滤剧情剧透） |
| `kb_get` | 按类别 + id/名称/SID 读取完整条目 |
| `kb_recipe_tree` | 递归展开主配方，算直接输入、原料总量、副产物（支持 `amount` / `rate` / `depth`） |
| `kb_plan` | 按目标产量计算机器数、总功率、传送带，并给出原料与副产物（`rate` 必填） |

## AI 能写什么（M4d）

| 工具 | 内容 |
| --- | --- |
| `wiki_search` / `wiki_page` | 搜索并读取中/英文 DSP Wiki，回答带来源链接 |
| `blueprint_search` / `blueprint_info` | 在线检索社区蓝图仓库（CC BY-NC-SA 4.0，带署名），并做格式体检 |
| `run_script` | 在 QuickJS-WASM 沙箱里跑只读脚本：`dsp.call` 同步调用 game_* 工具，可循环/筛选/求和；无 fs/net/process，调用与时长都有上限 |

本地工具（`kb_*` / `run_script`）也会在聊天窗里显示工具卡（“量化计算”“知识库检索”“脚本”），
数据来自 sidecar 的 `tool_activity` 事件，不经过游戏侧执行。

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
| mod 加载器 | **BepInEx 5.4.17+（win_x64）**，需自行安装；5.4.23.5 亦验证通过 |

> 版本说明：Thunderstore 社区包是 **5.4.17**，本项目也按 5.4.17 编译（避免 BepInEx 打印
> "plugin targets a wrong version"）。5.4.23.5 同样实测可用；它 changelog 里那条
> `Fix for unity 2022.3.62f3` 只影响取不到 `Application.isBatchMode` 的构建，与本游戏无关。

### 步骤

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
| 输入栏右侧「思考」下拉框 | 关闭 / 高 / 最高。开启后模型会先输出推理，以可折叠的「思考」块显示（收起时只留摘要） |
| 输入栏右侧「模型」下拉框 | 在已配置的多个供应商 / 模型之间切换；下一轮对话生效 |

> 输出区里的 **思考块** 与 **工具块** 都是 dsh 式的折叠条目：收起时显示一行摘要，
> 点击标题行展开详情。思考块在回答开始时会自动收起；工具块失败时自动展开。

### 模型页（设置 → 模型）

模型页参考 dsh Web UI 的组织方式：左栏是供应商列表（带密钥状态点），右栏是当前
供应商的编辑卡片——API 密钥（只写不读）、API 地址、模型列表（可增删），点模型行
把它设为当前模型；修改点右下角原生的「应用设置」提交。模型输入框旁的
**「获取可用模型」** 会从 pi 内置目录或供应商的 OpenAI 兼容 `/models` 端点拉取列表，
在弹层里点「+ 添加」即可加入当前供应商（未知供应商需要先填 API 地址）。
新增供应商时可用**预设下拉框**选择 DeepSeek / OpenAI / Anthropic / Google Gemini /
OpenRouter，也可以选「自定义」输入 ID；API 密钥旁的**「检测」**按钮会做一次
`GET /models` 轻量探测，直接显示 Key 是否可用和延迟。密钥只保存在 sidecar 的
`config.json`（0600），不会回传到游戏 UI。

### 关于 R 键冲突

`R` 在 DSP 里是**建造模式下旋转建筑**的键。本 mod 会自动避让：

| 情境 | `R` 的行为 |
| --- | --- |
| 建造 / 拆除 / 升级 / 改造 / 蓝图模式 | 交给游戏 → 旋转建筑 |
| 漫游、飞行、航行、战斗、挖矿 | 打开聊天窗口 |
| 全屏界面（星图、戴森球编辑器） | 不响应 |
| 窗口已打开 | 不响应（只有 `Esc` 能关） |

如果你仍觉得别扭，可以在 `BepInEx/config/icarus-ii.cfg` 里改成别的键
（例如 `F8`，该键在 DSP 中未被占用）。

---

## 配置

`BepInEx/config/icarus-ii.cfg`

```ini
[General]
Hotkey = R
HotkeyRequireNoBuildTool = true   ; 建造模式下把 R 让给游戏
Language = auto
Streaming = true

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

---

## 排障

| 现象 | 处理 |
| --- | --- |
| 按 `R` 没反应 | 看 `BepInEx/LogOutput.log` 里有没有 `[热键]`/`聊天窗口已创建` |
| 窗口显示"未连接" | 日志里搜 `[sidecar]`；确认 `icarus-ii-sidecar.exe` 在 `BepInEx/plugins/IcarusII/sidecar/` |
| 中文显示成方块 | 日志里看字体来源；`borrowed-from-game-ui` 正常，`builtin-arial(no-cjk)` 说明没借到游戏字体 |
| BepInEx 完全没加载 | Proton 下多半是 `winhttp` 覆盖没生效，见上文 |
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
