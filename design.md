# icarus-ii 设计文档

> Dyson Sphere Program 的 AI 助手 mod：按 `R` 打开聊天窗口，可就游戏内容提问、索取指引，并在玩家确认后让 AI 执行游戏内操作。

| 项 | 值 |
| --- | --- |
| 文档版本 | v1.0（M4 已完成） |
| 目标游戏版本 | DSP `0.10.30.3100`（Unity `2022.3.62f3c1`，Mono） |
| 目标运行环境 | Linux + Proton（已核实 `Proton Experimental` / Wine `11.0-100`）；原生 Windows 为次要目标 |
| 作者 | — |
| 状态 | 已实现并通过 M4 实机验收（2026-10-24） |

---

## 0. 结论摘要

**一句话架构**：BepInEx 插件（C#）负责 UI、游戏状态读取与操作执行；本地 sidecar（Node/TypeScript）内嵌 **pi 的 Agent SDK** 作为 LLM 推理与工具编排引擎；两者通过 `127.0.0.1` 上的 WebSocket 双向通信。

**关键决策**（详见 §3）：

1. **不自研 agent loop**，直接复用 `@earendil-works/pi-coding-agent` 的 SDK（2026-10-23 升级到 v1.4.0）。它一次性提供 Provider 抽象（OpenAI 兼容 + Anthropic Messages）、工具调用循环、流式事件、会话持久化、压缩/重试/中止，以及**可阻塞的工具调用钩子 + 用户确认 UI**——正好覆盖本项目全部需求。
2. **禁用全部内置工具**（`bash`/`read`/`write`/`edit`）。游戏内聊天窗口绝不能获得 shell 与文件系统权限。只注册我们自己的工具。
3. **信息按需获取**：不预先把存档状态塞进上下文；所有游戏数据、LDB 知识、Wiki 都由 AI 主动调用工具拉取。
4. **写操作双重门禁**：sidecar 侧的确认钩子 + mod 侧的一次性确认令牌，缺一不可。
5. **复用游戏自身的 UI 组件**：`UnityEngine.UI` 被 DSP 深度定制（含 `UnityEngine.UI.Youthcat` 富文本/多语言字体栈），直接复用可自动获得中文字体、IME 输入与美术风格一致性。
6. **`R` 热键主动避让建造旋转**：`R` 在游戏中已是"旋转建筑"（已核实，见 §6.2），因此热键以 `PlayerAction_Build.activeTool` 为主门控——建造模式下 `R` 归游戏，其余状态才开窗；配置可切 `Ctrl+R`。
7. **玩家侧零附加依赖**：sidecar 打成自包含单文件可执行（已在本机跑通全链路：pi + 全部依赖 → 7.1MB 单 JS → 107MB Windows exe / 132MB Linux 二进制，零原生模块）。玩家只需装 BepInEx；sidecar 由插件自动拉起（§7.1.1、§2.2）。
8. **Windows 优先**：mod 目标平台为 Windows，主要产物是 `icarus-ii-sidecar.exe`；它同时覆盖原生 Windows 与 Proton（PE 可在 Wine 内直接 `Process.Start`）。全部状态路径按 Windows 语义落在 `%APPDATA%\icarus-ii\`（§2.4）。

**已确认的产品决策**（详见 §19）：默认模型 **DeepSeek**（`deepseek-v4-flash`，M1 实测修正见 §7.2）；凭证完全自持、**不复用**其他工具；热键**只有 `R`**；**支持**截图（M4a，决策已改判，见 §8.1）；**不做**写前自动存档；**接受**发布包体积增大、sidecar 全部内置。

---

## 1. 目标与非目标

### 1.1 目标

| 编号 | 目标 | 验收方式 |
| --- | --- | --- |
| G1 | 游戏内按 `R` 打开/关闭聊天窗口，不打断游戏操作（尤其不与建造旋转冲突） | 手动测试：开关窗口时移动/建造输入正确屏蔽与恢复；建造模式下按 `R` 只旋转、不开窗 |
| G2 | 可就游戏机制、配方、科技、发展路线提问并得到回答 | 手动测试 + 一组固定问答用例 |
| G3 | AI 能读取当前存档的真实状态（资源、产线、电力、科技、行星） | 对比游戏内数值与 AI 复述数值 |
| G4 | AI 能执行游戏内操作；写操作按玩家选择的权限档位决定是否确认 | 只读下每次写都确认；受限下 risk 2 确认、risk 1 直接执行；接管下全部直接执行；拒绝后无副作用 |
| G5 | 回答与指引基于游戏真实数据（LDB）与社区资料，而非模型臆测 | 配方/科技数值与游戏内一致 |
| G6 | 中文界面；代码结构支持后续多语言 | 资源文件与字符串表分离 |
| G7 | 按可发布规范组织（目录、依赖、配置、文档），自用为主 | 能产出可安装的发布包 |
| G8 | **安装体验接近 drop-in**：玩家除装 BepInEx 外无需命令行/配置文件操作 | 干净机器上走通 §2.3.4 的三步流程；Proton 下无需改 Steam 启动项 |

### 1.2 非目标（明确排除）

- **不做**多人/联机支持（DSP 无官方联机）。
- **默认不做**全自动代打：默认「受限」权限只放行低风险写操作，改变存档的操作仍需确认。
  玩家可显式切到「只读」（全部确认）或「接管」（全部放行）；「接管」仍保留一次性令牌，
  且只由玩家主动选择，不是默认行为。
- **不做**存档文件直接读写：所有状态经游戏内存 API 获取，避免存档损坏与版本耦合。
- **不做**语音输入/输出。
- **不做**模型微调或本地模型权重分发。
- **不把** `bash`/文件系统能力暴露给模型。

> §20.1 D2 = **暂不做自动驾驶**（2026-10-24）：本条保持有效；未来重启需先复议本条与 §20.1。

---

## 2. 运行环境（已核实）

| 项 | 实测值 | 来源 |
| --- | --- | --- |
| 游戏版本 | `0.10.30.3100` | 程序集内嵌源文件名 `ProtoSignature_0_10_30_3100.cs`、`ConfigConstant_0_10_30_3100.cs` |
| Unity 版本 | `2022.3.62f3c1` | `DSPGAME_Data/globalgamemanagers` |
| 脚本后端 | **Mono**（非 IL2CPP） | 存在 `DSPGAME_Data/Managed/Assembly-CSharp.dll` 与 `MonoBleedingEdge/`；PE 为 `il-only` |
| 主程序集 MVID | `4bd0041b-46ae-442c-aba2-460c8e3630d8` | `Assembly-CSharp.dll` 元数据 |
| 主程序集 SHA-256 | `b84b60df5a24947a014801f23b9b85d1e77853f42a331918e2dc85b153e50527` | 同上 |
| 运行平台 | Linux + Proton | `steamapps/compatdata/1366540/pfx` 存在；`config_info` = `Proton Experimental` / `11.0-100` |
| Wine 前缀 | `steamapps/compatdata/1366540/pfx` | 上文 |
| Node.js | `v24.18.0`（`/home/kidelee/.local/bin/node`） | `node --version` |
| 已装可复用件 | `pi`（全局 CLI v0.82.1；sidecar 依赖 v1.4.0）、`dsh` v0.2.0-rc.2、`bwiki-mcp` v0.3.0 | 全局 `node_modules` + sidecar 本地依赖 |

### 2.1 由环境推导出的硬约束

1. **Mono 后端** ⇒ 使用 **BepInEx 5.4.x + HarmonyX**（BepInEx 6 主要面向 IL2CPP，本项目用不上）。
2. **Proton 运行** ⇒ BepInEx 的门禁 DLL（`winhttp.dll`）默认会被 Wine 的 builtin 版本抢占，需要显式覆盖。社区做法是 Steam 启动项 `WINEDLLOVERRIDES="winhttp=n,b" %command%`；本设计改用**前缀注册表按 exe 覆盖**以保住 drop-in 体验（§2.3.2，M0 实测确认）。
3. **Wine 与宿主共享网络栈** ⇒ sidecar 监听 `127.0.0.1` 即可被游戏进程访问，无需端口转发。
4. **进程派生跨边界** ⇒ 游戏进程是 Windows PE（Wine 内），无法直接 `Process.Start` 宿主 Linux 的 `node`。sidecar 启动策略需要分环境处理（见 §7.1）。
5. **目标框架** ⇒ mod 编译目标为 `netstandard2.1` / `net472`（Unity 2022 Mono 的 `Assembly-CSharp` 引用 `netstandard 2.1`）。

### 2.2 玩家侧依赖清单（本设计的目标：只有游戏 + BepInEx）

| 依赖 | 是否必需 | 能否消除 | 方案 |
| --- | --- | --- | --- |
| Dyson Sphere Program | ✅ 必需 | — | — |
| BepInEx 5.4.x + HarmonyX | ✅ 必需 | 不可（mod 加载器） | 按常规安装；Proton 下的 DLL 覆盖由安装器自动写入前缀注册表（§2.3.2），无需改 Steam 启动项 |
| ~~Node.js~~ | ❌ 原本需要 | ✅ **可以消除，已验证** | sidecar 打成自包含单文件可执行（§7.1.1） |
| ~~pi 及其 node_modules（170MB）~~ | ❌ 原本需要 | ✅ 已消除 | esbuild bundle 成 7.1MB 单文件；原生依赖为 0 |
| ~~systemd 单元 / 启动脚本~~ | ❌ 原本需要 | ✅ 已消除 | Windows 版自包含 exe 就是 PE，Wine 内可直接 `Process.Start`（§7.1.2） |
| LLM API Key | ✅ 必需 | **不可消除** | 这是 mod 的功能本体；可用本地模型，但那样反而多一个推理运行时依赖 |
| 网络访问 | ✅ 必需（除非纯本地模型） | 不可消除 | 仅访问 `127.0.0.1` + 所配 LLM 端点；Wiki 抓取可关 |

**结论**：除 API Key 与网络外，**可以做到零附加依赖**——玩家装好 BepInEx 后，mod 自带 `icarus-ii-sidecar.exe`，由插件自动拉起、退出时回收。

> **已决定（用户确认）**：接受发布包变大，`sidecar/icarus-ii-sidecar.exe` **直接内置**，不做"首次启动引导下载"。`LaunchMode` 仍保留引导下载的抽象以备将来，但默认不启用。

---

### 2.3 安装形态：能否"丢进游戏目录就能用"？

**结论：可以把玩家侧操作压缩到「跑一次安装脚本 → 游戏内设置页填写 API Key」两步。** 分平台说明如下。

#### 2.3.1 原生 Windows：标准 drop-in ✅

BepInEx 的 doorstop 依赖 **Windows 的 DLL 搜索顺序**——放在应用目录的 `winhttp.dll` 会优先于系统 DLL 被加载。因此标准形态就是纯 drop-in：

```
Dyson Sphere Program/
├── DSPGAME.exe
├── winhttp.dll                    ← BepInEx doorstop 代理
├── doorstop_config.ini
└── BepInEx/
    ├── core/…
    └── plugins/IcarusII/
        ├── IcarusII.dll
        └── sidecar/icarus-ii-sidecar.exe   （自包含，§7.1.1）
```

解压进去、启动游戏、按 `R` 即可。**无需任何额外配置**（除首次填 API Key）。

#### 2.3.2 Linux / Proton：默认不行，但可自动化解决 ⚠️→✅

**问题**：Wine **默认优先加载 builtin 的 `winhttp.dll`**，而非游戏目录里的 BepInEx 版本，导致 doorstop 不生效。社区标准解法是加 Steam 启动项 `WINEDLLOVERRIDES="winhttp=n,b" %command%`——这就破坏了 drop-in。

**已核实的更好方案**：Wine 支持**按可执行文件**的 DLL 覆盖，存放在 Proton 前缀的 `user.reg`：

```ini
[Software\\Wine\\AppDefaults\\<exe名>\\DllOverrides]
"<dll>"="native,builtin"
```

**这不是野路子**：本机 `compatdata/1366540/pfx/user.reg` 中**已有 27 条**此类条目，全部由 Steam/Proton 自己写入（例如 `Avengers.exe` → `"atiadlxx"="builtin"`）。说明这是 Proton 的一等公民机制。

因此安装脚本只需追加一条：

```ini
[Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
"winhttp"="native,builtin"
```

即可**在不改 Steam 启动项的前提下**让 BepInEx 生效。

> **M0 必须实测确认**（两条）：
> 1. Proton 是否尊重 `AppDefaults\<exe>\DllOverrides` 注册表覆盖（本机 27 条同类条目强烈支持，但未能实跑验证——沙箱下 `wineserver` 需要只读的 `/run/user/1000/wine`）。
> 2. BepInEx 5 在 DSP 上使用的代理 DLL 名。BepInEx 5 有 `winhttp` / `version` / `dinput8` 三种变体，机制相同、键名不同；确认后在脚本中固定。
>
> 写入时机：**游戏必须处于关闭状态**——Wine 在退出时会把内存中的注册表写回 `user.reg`，运行中修改会被覆盖。

#### 2.3.3 无法消除的一步：API Key

API Key 是外部服务凭证，mod 无从得知，**这一步不可自动化**。但可以做到**不必手改配置文件**：

- 首次启动时，聊天窗会提示尚未配置 AI。玩家点击窗口右上角的**设置按钮**，在「模型」页选择 Provider → 填写 API Key → 选择模型 → **连通性自检** → 保存即可。
- 凭证写入 sidecar 自己的数据目录 `%APPDATA%\icarus-ii\config.json`（§2.4），**不回写游戏目录**，卸载干净。
- **已决定（用户确认）：不复用 `pi` 或其他工具的既有登录**。sidecar 使用完全独立的凭证与模型配置，避免与玩家其他工具的配置相互干扰。因此设置页是**必经路径**，必须做好（不能依赖"检测到已有登录就跳过"）。

#### 2.3.4 目标安装流程

```
① 关闭游戏
② 运行 install.sh（Windows: install.bat，或直接解压 zip）
   ├─ 从 steamapps/libraryfolders.vdf 定位游戏目录
   ├─ 检测运行形态（原生 Windows / Proton）
   ├─ 复制 BepInEx 5 + BepInEx/plugins/IcarusII/
   └─ Proton 分支：向 pfx/user.reg 追加 AppDefaults\DSPGAME.exe\DllOverrides
③ 启动游戏 → 按 R → 点击右上角设置按钮，在「模型」页填写 API Key
```

之后的使用体验就是"丢进去，能用"。安装脚本同时提供**纯解压版**（不含步骤②的自动注册表写入），供不想跑脚本的玩家手动处理。

---

### 2.4 目标平台约定：Windows 优先

> **用户确认：mod 目标平台是 Windows。**

这条约束影响若干路径与默认值，全文遵循以下约定：

| 项 | 约定 |
| --- | --- |
| 首要构建目标 | **Windows x64**（`icarus-ii-sidecar.exe`），Proton 下同样运行此 Windows 产物 |
| 次要/开发目标 | Linux x64 二进制——**仅用于开发期在本机原生调试 sidecar**，不作为发布形态 |
| sidecar 运行身份 | 一个 **Windows 进程**（原生 Windows 直接运行；Proton 下在 Wine 内运行） |
| 数据目录 | **`%APPDATA%\icarus-ii\`**（Windows 语义）。该代码路径在原生 Windows 与 Wine 下完全一致，无需分支 |
| Proton 下的实际落盘位置 | `compatdata/1366540/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii/` |
| 游戏目录 | 只读+单向写入：安装器写入 `BepInEx/`；**运行期不回写游戏目录**（配置、会话、知识库都在 `%APPDATA%`） |

**因此全文不采用 `~/.config/icarus-ii/`、`~/.pi/agent` 等 Linux 家目录路径。** 附录与配置示例中出现的所有路径均按 Windows 语义。

---

## 3. 关键设计决策

### 3.1 决策一：复用 pi SDK 作为 Agent 运行时（不自研）

用户要求考虑复用现有开源 harness。已在本机评估三个候选：

| 候选 | 形态 | 可嵌入性 | 工具扩展 | Provider | 结论 |
| --- | --- | --- | --- | --- | --- |
| **pi** (`@earendil-works/pi-coding-agent`) | npm 库 + CLI + 子进程 RPC | ★★★ 直接 `import` 同一进程内使用 `createAgentSession()` | `defineTool()` / `pi.registerTool()`，TypeBox schema | `ModelRuntime`，内置 Anthropic / OpenAI / Google，`models.json` 可加任意 OpenAI 兼容端点 | **选它** |
| dsh (`@deepseek-ai/dsh`) | 应用启动器，一切皆 profile | ★★ SDK / ACP 是 profile，需以子进程 + JSON-RPC/ACP 通信 | 通过插件（cordis）注册 | 内置，支持 OpenAI/Anthropic 等 | 备选（见下） |
| reasonix | CLI/TUI 二进制 | ★ 无库接口 | 无 | DeepSeek 系为主 | 不选 |

**选 pi 的理由**（均为本机文档核实，非推测）：

- `createAgentSession()` + `customTools` + `session.subscribe()` 三个 API 即可构成完整闭环。
- 流式事件粒度恰好匹配聊天窗口需求：`message_update`（`text_delta` / `thinking_delta` / `toolcall_*`）、`tool_execution_start|update|end`、`agent_settled`。
- `ModelRuntime` 直接满足"OpenAI 兼容 + Anthropic Messages"双协议要求：Anthropic 原生支持；OpenAI 兼容端点通过 `models.json` 的 `{"api": "openai-completions", "baseUrl": "..."}` 声明（已在 `docs/models.md` 核实，明确列出 Ollama / LM Studio / vLLM）。
- `pi.on("tool_call", …)` **可阻塞**，配合 `ctx.ui.confirm()` 实现"写操作默认必须确认"，这是原生能力而非自研；权限档位在该钩子与 mod 令牌层之上再收窄/放行。
- `noTools: "builtin"` 可在保留自定义工具的同时关闭全部内置工具，正好满足安全要求。
- `SessionManager` 提供 JSONL 会话树（持久化、分支、压缩），聊天历史不必自研。

**dsh 的定位**：dsh 本身是 profile 化的应用启动器（`--profile sdk` / `--profile acp` 均以子进程 + JSON-RPC/ACP 暴露）。它带来 MCP 客户端、子代理、Web UI 等能力，但对"游戏里一个聊天窗"而言过重，且引入 profile/插件体系耦合。因此：
**在 sidecar 内定义一个 `AgentRuntime` 接口，默认实现 `PiRuntime`；把 dsh 的 ACP profile 作为可选的第二个实现**，以便将来需要 MCP（如复用已安装的 `bwiki-mcp`）或多代理时平滑切换。设计不锁死。

### 3.2 决策二：进程职责划分

| 进程 | 语言 | 负责 | 绝不负责 |
| --- | --- | --- | --- |
| **mod**（游戏进程内） | C# / BepInEx | UI 渲染、热键、输入焦点、游戏状态采集、操作执行与确认、撤销 | 不持有 API Key；不直接调用 LLM |
| **sidecar**（宿主进程） | Node / TypeScript | LLM 调用、工具编排、知识库、会话持久化、Prompt 组装 | 不接触游戏文件；不直接改动存档 |

理由：LLM 调用链的依赖（HTTP/2、SSE、TLS、JSON Schema、npm 生态）在 Node 侧远比 Unity Mono 侧舒适；且出错不会拖垮游戏进程。

### 3.3 决策三：sidecar 为服务端，mod 为客户端

`sidecar` 监听 `127.0.0.1:8765`（WebSocket），mod 启动后连接并重连。

- 由 sidecar 承载"工具调用"这一**服务端发起**的请求，是 RPC 的自然方向（服务端 → 客户端调用）。
- sidecar 生命周期可独立于游戏：游戏重启不影响会话，重连后继续。
- 端口冲突、鉴权、多实例等策略集中在 sidecar 一处。

### 3.4 决策四：信息按需获取（lazy retrieval）

严格遵循"AI 需要时才通过工具获取"：

- **不**在每轮对话前把存档状态快照塞进 system prompt。
- System prompt 只包含：角色设定、工具清单与使用时机、输出风格、当前存档的**极小元信息**（游戏名/语言/版本，可由配置关闭）。
- 大对象（产线清单、配方表）一律走工具 + 分页/聚合，避免 token 爆炸。

### 3.5 决策五：安全默认值

| 项 | 默认 |
| --- | --- |
| 内置工具 | 全关（`noTools: "builtin"`） |
| 网络工具 | 仅限 sidecar 侧的白名单 Wiki 抓取；不给模型通用 HTTP 工具 |
| 写操作 | 权限档位决定是否确认（只读=全部确认，受限=risk 2 确认，接管=全部放行）+ 影响面预览 + 可撤销；**不做写前自动存档**（用户确认关闭） |
| API Key | 只存 sidecar 侧（复用 pi 的 `auth.json` 或环境变量）；mod 不接触 |
| 存档数据外发 | 脱敏：默认不上传存档名/账号名（可配置开启） |

---

## 4. 总体架构

### 4.1 组件图

```
┌─ DSPGAME.exe (Wine/Proton 内) ─────────────────┐
│                                                │
│  BepInEx 5 (winhttp doorstop)                  │
│   └─ IcarusII.dll                              │
│       ├─ Plugin.cs            插件入口/Harmony │
│       ├─ ChatWindow           UGUI 聊天窗口    │
│       ├─ HotkeyService        R 键 / 焦点管理  │
│       ├─ GameStateService     只读状态采集     │
│       ├─ ActionService        写操作 + 确认门禁│
│       └─ SidecarClient        WS 客户端/重连   │
│                                                │
└───────────────────┬────────────────────────────┘
                    │  ws://127.0.0.1:8765  (JSON)
                    │  ↑ 流式事件 / 工具调用 / 确认请求
                    │  ↓ 用户输入 / 工具结果 / 确认答复
┌───────────────────┴────────────────────────────┐
│ icarus-ii-sidecar (宿主 Node/TS)               │
│                                                │
│  Fastify + ws  ── 会话与协议层                 │
│      └─ AgentRuntime (接口)                    │
│           └─ PiRuntime  ← 默认实现             │
│                ├─ pi AgentSession              │
│                │    ├─ ModelRuntime → LLM API  │
│                │    └─ customTools             │
│                ├─ GameTools   → 经 WS 回调 mod │
│                ├─ KnowledgeTools → 本地 KB     │
│                └─ WikiTools   → MediaWiki API  │
│           └─ DshAcpRuntime  ← 可选（预留）     │
│                                                │
│  数据：sessions/*.jsonl  kb/ldb.sqlite  config │
└────────────────────────────────────────────────┘
```

### 4.2 一次对话的时序

```
玩家按 R        mod                     sidecar(pi)              LLM API
  │              │                          │                      │
  ├─ 打开窗口 ──►│                          │                      │
  ├─ 输入并回车 ►│                          │                      │
  │              ├─ prompt ────────────────►│                      │
  │              │                          ├─ 组装 system+tools ─►│
  │              │                          │◄──── 流式 text ──────┤
  │◄─ stream ────┤◄──── text_delta ─────────┤                      │
  │  (逐字显示)  │                          │                      │
  │              │                          ├─ tool_call(plan) ───►│
  │              │                          │◄─ tool_call 请求 ────┤
  │              │◄─ tool_call ─────────────┤                      │
  │  (读状态)    ├─ tool_result ───────────►│                      │
  │              │                          ├─ 结果回填 ──────────►│
  │              │                          │◄─ 需要写操作 ────────┤
  │              │                          ├─ tool_call(act) 门禁 │
  │◄─ 确认框 ────┤◄─ confirm_request ───────┤                      │
  │  玩家点"允许"│                          │                      │
  ├─ 确认答复 ──►├─ confirm_result ────────►│                      │
  │              │◄─ tool_call(act) ────────┤                      │
  │  执行操作    ├─ tool_result ───────────►│                      │
  │◄─ 完成提示 ──┤◄─ stream(总结) ──────────┤                      │
```

---

## 5. 通信协议（mod ↔ sidecar）

### 5.1 传输与帧

- **传输**：WebSocket over `127.0.0.1`。
- **帧**：文本帧，每帧一个 JSON 对象。不使用 stdio（mod 无 stdout 可控性保证）。
- **编码**：UTF-8。
- **协议版本**：每次连接首帧交换 `v`，不匹配则 sidecar 拒绝并返回可读错误。

### 5.2 消息信封

```jsonc
{ "v": 1, "id": "c7f1", "type": "tool_result", "replyTo": "call_abc", "payload": { } }
```

| 字段 | 说明 |
| --- | --- |
| `v` | 协议版本 |
| `id` | 发送方生成的消息 ID（用于日志与去重） |
| `type` | 消息类型 |
| `replyTo` | 需要应答时的原消息 ID |
| `payload` | 类型相关负载 |

### 5.3 消息类型表

**mod → sidecar**

| type | payload | 说明 |
| --- | --- | --- |
| `hello` | `{modVersion, gameVersion, locale, saveName?, capabilities[]}` | 连接握手 |
| `prompt` | `{text, conversationId}` | 玩家发送消息 |
| `abort` | `{}` | 中止当前运行 |
| `tool_result` | `{callId, ok, data?, error?}` | 工具执行结果 |
| `tool_progress` | `{callId, partial}` | 长任务进度（可选） |
| `confirm_result` | `{requestId, decision: "allow"|"deny"|"allow_always", token}` | 确认答复（`token` 见 §9.3） |
| `ui_event` | `{event: "window_open"|"window_close"}` | 窗口可见性变化 |
| `game_event` | `{event: "save_loaded"|"planet_changed"|"research_done", data}` | 上下文失效通知 |

**sidecar → mod**

| type | payload | 说明 |
| --- | --- | --- |
| `ready` | `{capabilities[], model, sessionId}` | 握手应答 |
| `stream` | `{conversationId, channel: "text"|"reasoning", delta}` | 增量文本 |
| `message` | `{conversationId, role, content[], final}` | 完整消息（含工具卡片） |
| `status` | `{state: "idle"|"thinking"|"tools"|"aborting", detail?}` | 状态指示 |
| `tool_call` | `{callId, name, args, risk: 0|1|2, timeoutMs}` | 要求 mod 执行游戏工具 |
| `tool_progress` | `{callId, partial}` | 由 pi 的 `onUpdate` 透传 |
| `confirm_request` | `{requestId, title, body, options[], risk, timeoutMs}` | 请求玩家确认 |
| `error` | `{code, message, retryable}` | 错误 |
| `session` | `{sessionId, title}` | 会话元信息 |

### 5.4 连接与重连

- mod 启动即尝试连接；失败按指数退避重试（1s→30s 上限），期间聊天窗口显示"未连接 sidecar"并给出启动指引。
- 重连后 mod 发送 `hello`；sidecar 用 `sessionId` + `get_entries(since)` 恢复上下文，向 mod 重放最近 N 条消息用于渲染。
- 心跳：WebSocket ping/pong，30s 无 pong 判定断开。

---

## 6. mod 端设计（C# / BepInEx）

### 6.1 插件装配与补丁点

```csharp
[BepInPlugin("icarus-ii", "Icarus II", "0.2.0")]
[BepInProcess("DSPGAME.exe")]
public sealed class Plugin : BaseUnityPlugin
```

**注入方式**：不使用 Harmony 去"劫持"游戏逻辑，而是注册自己的 `ManualBehaviour` 到 `UIRoot`，并挂一个 `Update` 驱动。

游戏侧已核实的可用锚点：

| 用途 | 类型 / 成员 | 备注 |
| --- | --- | --- |
| UI 根单例 | `UIRoot._instance` | 含 `overlayCanvas`、`overlayCanvasTop` |
| 挂载容器 | `UIRoot.overlayCanvasTop` | 顶层画布，盖住游戏 HUD |
| 游戏窗口容器 | `UIGame.windowGroup` (`RectTransform`) | 与游戏窗口同层，便于风格一致 |
| UI 生命周期基类 | `ManualBehaviour : MonoBehaviour` | `_Create/_Init/_Open/_Close/_Update/_OnOpen/...` |
| 全局状态 | `GameMain.instance`、`GameMain.data` | — |
| 静态数据 | `LDB.items` / `LDB.recipes` / `LDB.techs` 等 | 只读 |
| 本地化 | `Localization.Translate(key)`、`Localization.isZHCN` | 复用游戏术语译名 |
| 输入门面 | `VFInput.*`（`inputing`、`escape`、`simple_buttons`、`override_keys`） | 焦点判定 |
| 按键配置 | `KeyConfig.builtinKeys : BuiltinKey[]`，`CombineKey.GetKeyDown()` | 冲突检测 |
| 底层按键 | `WinInput.GetKey(VKey.R)`、`VKey` 枚举 | 兜底（注意无焦点也返回） |
| 建造工具状态 | `GameMain.mainPlayer.controller.actionBuild` → `active` / `activeTool : BuildTool` | **R 键避让判定**（见 §6.2） |

> 仅在"必须拦截原逻辑"时才上 Harmony。首批功能不需要 Harmony 补丁——这显著降低版本升级的破坏面。

### 6.2 热键与输入焦点

**需求**：`R` 开关窗口；窗口打开时 WASD/建造/滚轮不得传给游戏；中文输入法可用。

#### 6.2.1 `R` 落在游戏的建造操作区间内

DSP 中 **`R` 是"旋转待放置建筑"**，因此这不是"也许冲突"，而是必须主动避让：

**已核实（元数据证据）**：

- `BuiltinKey` 的动作字段列表中存在 **`顺时针旋转建筑0` / `顺时针旋转建筑1`** 与 **`逆时针旋转建筑`**（相邻还有 `切换模型样式`、`扩大地基光标`、`缩小地基光标`、`切换网格吸附方式` 等建造相关动作）。
- 对应输入面确实存在：`VFInput._rotate : InputValue` 与 `VFInput._counterRotate : InputValue`（`InputValue` 含 `value` / `pressing` / `onDown` / `onUp` ⇒ 支持长按连续旋转）。
- `顺时针旋转建筑0/1` 两个索引说明该动作有不止一个默认绑定。

**待实测（M0）**：`BuiltinKey.key : CombineKey` 里绑定的具体 `keyCode`。默认键位表存放在 `KeyConfig` 资源（非程序集内，无法离线读取），所以"旋转 = `R`"这一步属于**高置信度的领域常识 + 动作存在性已证实**，而非元数据直读结论。M0 用运行时打印 `KeyConfig.builtinKeys` 一次坐实。

**无论如何都要做的结论**：只要"旋转建筑"这个动作位于 `R` 附近，无条件把 `R` 注册为聊天热键就会**在玩家每次旋转建筑时弹窗**。所以避让是硬需求，而不是冲突检测失败后的兜底。

#### 6.2.2 避让设计：以"建造工具是否激活"为主门控

判定路径（**已由实机日志修正**，见附录 A.3）：

```csharp
// Player → PlayerController.actionBuild : PlayerAction_Build → active : bool
var build = GameMain.mainPlayer?.controller?.actionBuild;
bool buildToolActive = build != null && build.active;
```

> ⚠️ **不要用 `activeTool` 判定。** 设计初稿写成
> `activeTool != null && !(activeTool is BuildTool_None)`，
> 但实机日志显示 `activeTool` **在漫游与建造模式下恒为 `null`**，
> 该写法在建造模式中同样返回 false —— 避让逻辑等于完全没生效。
> `active` 才是正确信号（漫游 False / 建造 True）。

`R` **只有在 `buildToolActive == false` 时才响应为聊天热键**。这样：

| 情境 | `R` 的行为 |
| --- | --- |
| 建造模式下（选中任意建造工具/手持建筑预览） | 交给游戏 → 旋转建筑；**不开窗** |
| 拆除 / 升级 / 改造 / 蓝图模式 | 同上（`activeTool` 非 `None`） |
| 漫游、飞行、航行、战斗、挖矿（工具为 `None`） | 打开聊天窗口 |
| 地图/星图/戴森球编辑器等全屏界面 | 由 `VFInput.inFullscreenGUI` 判定，默认**不**开窗 |
| 窗口已打开 | `R` 不再作为开关键（避免误触），仅 `Esc` 关闭 |

**兜底与可配置**：

- `General.Hotkey` 可改成任意 `KeyCode`；`General.HotkeyRequireNoBuildTool = true` 可关闭避让（不建议）。
- 若玩家仍觉别扭，配置里可一键切到 **`Ctrl+R`** —— 带修饰键时永不与游戏裸键冲突（`CombineKey.modifierExact` 语义保证游戏侧不会把 `Ctrl+R` 当成裸 `R`）。
- 首次打开窗口时，若检测到 `R` 与其他非建造动作也冲突（见下），在窗口顶部显示一次性提示。

#### 6.2.3 其余设计要点

1. **检测方式**：用 Unity 的 `Input.GetKeyDown`（有窗口焦点语义），**不用** `WinInput.GetKey`（Win32 `GetAsyncKeyState` 在游戏失焦时仍返回 true，会导致切出去按 R 也触发）。`WinInput` 仅作可选后端保留。
2. **焦点屏蔽**：窗口打开时把 `VFInput.inputing = true`（游戏自身的"正在输入文本"标记），关闭时恢复原值。游戏的移动/建造逻辑据此自然停止响应。
3. **热键消费**：窗口打开时 `Esc` 优先关闭窗口；`R`、`Enter` 交给输入框（`Enter` 发送、`Shift+Enter` 换行）。
4. **完整冲突扫描**（启动时一次，结果记日志 + 首次开窗提示）：
   遍历 `KeyConfig.builtinKeys : BuiltinKey[]`，把每项 `CombineKey.keyCode` / `modifier` 与目标热键比对，产出一张"热键 → 冲突动作名"表。动作名可从 `Locale/<lcid>/keys.txt` 取本地化文本。
   当前已知冲突（旋转建筑）由 §6.2.2 的建造工具门控消解；扫描用于发现**其他**潜在占用（例如 `切换模型样式`、`扩大地基光标` 是否也落在 `R` 上），并在门控未覆盖到的情况下提示玩家改键。
   > **M0 待实测确认**：`顺时针旋转建筑` 与 `逆时针旋转建筑` 的默认键是否正是 `R` / `Shift+R`，以及是否还有第三个动作占用裸 `R`。核实方法：运行时打印 `KeyConfig.builtinKeys`（首次启动即输出该表），或游戏内「设置 → 按键」查看。设计不依赖确切答案——建造工具门控已能保证最常见路径不冲突。
5. **文本输入**：复用游戏的 `UISmartInputField : ManualBehaviour`（游戏中用于待办/物流输入框），它已接通 DSP 的 IME 链路（程序集内含 `rail.RailIMEHelperTextInputCompositionState` 等 Rail IME 类型），中文输入可直接工作。若实测有问题，回退到自定义 `InputField` + 明确的 IME 组合期处理。

### 6.3 聊天窗口 UI

**界面入口**：除热键（默认 `R`）外，右下角 Game Menu 的**截图键正上方**有一个聊天入口
按钮。按钮的静态结构、尺寸、图标与 hover 动画都在 `button-agent.prefab`（随
`icarusii-ui` bundle 分发）；运行期只把实例挂到 `UIGameMenu` 根节点下，以
`UIGameMenu.buttonS` 为锚点（同 x、抬到截图键正上方）并绑定点击打开窗口。
Game Menu 与截图键直接写在 `DSPGame.unity` 里（不是 Resources prefab），mod 无法在
安装期改场景，因此这里只做“挂载 + 锚点适配”，不修改按钮内部布局。

**组件树**（全部 UGUI，挂在 `UIRoot.overlayCanvasTop` 下的自建 `RectTransform`）：

```
AgentRoot (RectTransform, 默认 inactive)
├─ Backdrop            Image (半透明遮罩, 点击=关闭, 可选)
└─ Panel               Image (九宫格, 仿游戏面板)
   ├─ TitleBar
   │   ├─ Title        Text      "工业机甲终端"（字体/颜色/字号 = prefab 设计稿）
   │   ├─ StatusDot    Image     连接/思考状态
   │   └─ CloseButton  UIButton
   ├─ MessageScroll    ScrollRect
   │   └─ Content      VerticalLayoutGroup + ContentSizeFitter
   │       ├─ MessageBubble (Text, 富文本)
   │       ├─ ToolCard      (标题 + 状态 + 可折叠结果)
   │       └─ ThinkingFold  (默认折叠的推理区)
   ├─ ConfirmLayer     (写操作确认卡片：标题 / 说明 / [允许][拒绝][总是允许])
   └─ InputBar
       ├─ Input        UISmartInputField (多行, 中文 IME)
       ├─ SendButton   UIButton
       └─ Hint         Text  "Enter 发送 · Shift+Enter 换行 · Esc 关闭"
```

**渲染策略**

- **窗口标题以 prefab 为准**：`panel-bg/title-text` 里已经写了字的标题是设计稿——
  运行期不改文字，也**不参与**正文字体替换和 `FontScale` 缩放，字体/颜色/字号完全按
  prefab；只有标题为空时才填 `Strings.Get("title")`（「工业机甲终端」）。
- **消息条目的静态结构固定在 prefab**：`ChatWindow.prefab` 的 `example-reply` 里放了
  `loading`（运行中的思考/工具区）→ `thinking-block` / `tools`、正文、蓝图卡片网格和底部
  （游戏时间 + 复制按钮）。运行期只克隆这个模板、绑定文字/图标/显隐和点击回调；
  思考/工具/正文/卡片共享同一条回复容器，不再由 C# 拼静态节点。
- **loading 是分组头**：`loading` 里再分 `header`（转圈图标 + 标题 + 展开图标，整行可点）
  和 `content`（thinking-block / tools）。运行中标题是「终端运行中…」+ 游戏自带的
  `loading-20` 转圈图；思考/工具结束后切成「已完成」并收起 `content`，只留标题行和
  ▸ 展开图标；点标题行随时展开/收起（运行中也一样）。`loading` 与 `content` 都不加左右
  padding，块的左边缘与正文、卡片、脚注对齐；块的 10px padding 只影响块内文字。
  prefab 里保留一份思考块 + 工具块示例（`tool-template` 可见）用于预览，
  运行期 `BindReply` 会把两者隐藏。
- **每段思考各自一块**：一次提问里模型会「思考 → 工具 → 再思考 → …」。第一个思考块用
  prefab 里的 `thinking-block`；之后每段思考都**克隆** `thinking-block` 追加到 `tools`
  折叠块容器末尾（工具块也追加在这里），所以顺序始终是 思考 → 工具 → 思考 → …。
  不能把所有推理都续写进第一块：那样第二段会覆盖第一段，而且位置永远停在整条回复最上方。
- **正文开始即收起**：第一段正文到达（或非流式终稿落地）时，把这条回复里
  **所有**思考块和工具块收起，只留 header 的标题 + 摘要；同时 loading 分组头切成
  「已完成」并收起整组（玩家点 loading 标题行展开整组，再点具体块看细节）。
  失败/截图工具块之前可能被自动展开，也一并收回。`loading` 在没有思考/工具时隐藏，
  不留空白间距。
- **思考/工具步骤随会话还原**：sidecar 从 pi 会话记录里按顺序提取
  `save.bound.history[].steps`（附录 A.6），mod 回放时逐条重建成收起态的折叠块，
  重进存档后看到的就是上次关闭时的样子；蓝图卡片仍走 A.9 的独立记录。
- **蓝图卡片是固定尺寸双列网格**：用户调整后为单张 400×130，实机正文宽度约 864px，
  一行 2 张；窄窗口落 1 张。卡片用 `litter-panel-cc` 圆角面板 + 左侧强调条，左栏照游戏
  蓝图浏览器的文件卡（`blueprint-file-220px` 100×110 底板 + 80px 物品图标 + 压在底板上的
  35px 缩略图文字；缩略图文字用 `Name`/`shortDesc`，卡片标题用 `RelPath` 存放路径），
  介绍超出 70px 固定高度由 `RectMask2D` 裁掉；取消独立“查看”按钮，整张卡片点击查看，
  脚注直接显示 `作者 · 创建日期 · 版本`，只保留直接挂在内容右下角的 48×20“使用”按钮。
  卡片网格左侧无 padding，卡片左边缘与思考块/正文/脚注左对齐；脚注时间只显示 `08:42:00`，
  复制按钮为 16×16 的 `copy` 图标、不再带文字。sidecar 在本轮 `blueprint_fs rm/rmdir`
  删掉已生成蓝图时会撤回待发卡片，避免历史里出现已不存在的文件。
- **工具列表是 prefab 里的 popup**（M4l）：`input/tool-menu` 固定在输入框上方，
  包含背景/边框/header/`ScrollRect` + `viewport/content`/`tool-row-template`/empty/footer。
  运行期只按 `ready.payload.toolCatalog` 克隆行、填 label/命令/简介，并做筛选/高亮/滚动；
  不新增静态节点、不改 RectTransform。点击 `tool-btn` 或输入 `/` 打开；`↑↓` 选择、
  `Enter` 插入 `/工具名 `、`Esc` 关闭。选择后发送 `prompt.requiredTool`，sidecar
  把本轮 active tools 限制为该工具、要求模型至少调用一次；没调用会纠正一次，仍失败则
  回 `error{code:"tool_required"}`，不静默给出普通回答。
  图标选择器和文字颜色选择器也复用 `tool-menu` 的左下角锚点（输入框上沿、左对齐），
  不再按工具条按钮的位置向左/向上摆放。
- **调试直调**（M4m）：输入框 `!tools` / `!tool <工具名> <JSON>`；mod 发 `tool_debug`，
  sidecar 不建 pi 会话、不调 LLM，直接执行只读白名单工具并回 `tool_debug_result`。
  游戏工具内部仍走 `tool_call`，但标 `internal=true`，mod 不画普通工具卡；结果由
  `tool_debug_result` 作为系统调试块显示，不写对话历史。开关为 `DebugToolConsole=true`
  + sidecar `--debug-tools`，写文件/脚本类工具不在白名单。
- **每条消息都有脚注**：助手脚注是「时间 + 复制 + 刷新」；用户气泡下面是「时间 + 复制」
  （右对齐、只有这两项）。复制按钮拷的都是该条消息的原文。
  刷新只出现在**最后一轮**回答上（会话是线性的，重开中间一轮会改变上下文顺序）：
  点击后窗口清掉这一轮的助手侧、保留用户气泡与索引行，Plugin 先发 `history.delete`
  删掉旧回合（A.7），成功后再用同一句提问发 `prompt`；这样模型上下文里不会残留旧答案。
- **历史脚注的游戏时间由 sidecar 持久化**：mod 在每轮 `message{final}` 后发
  `reply.time{prompt, reply, gameTime}`；sidecar 按存档写 `reply-times.jsonl`，后续
  `save.bound.history` 的 assistant 消息带可选 `gameTime`，mod 回放时用它填脚注；
  user 消息也带同一个时间，用来填用户气泡下面的脚注。
- 使用游戏自己的 `UnityEngine.UI.Text`（来自被 DSP 定制的 `UnityEngine.UI.dll`）。该程序集在 `UnityEngine.UI.Youthcat` 命名空间下提供了定制文本栈：
  - `TextLayoutLocale`：按语言提供字体替换对（`LanguageFontPair {oldFont, newFont}`）与字距/缩进参数 ⇒ **中文字体由游戏自动处理**，无需自带字体。
  - `TextIconMapping`：把文本标签映射为内嵌图标（`GetIconInString`）⇒ AI 回复里可直接内联物品/建筑图标。
  - `TextEventTrigger` / `TextTriggerBounds`：文本区间级触发器 ⇒ 支持"点击回复中的物品名 → 打开该物品详情"。
- 输出富文本由 `mod/src/Shared/Text/GameRichText.cs` 编译：外层用标准 Markdown，游戏样式用一小撮内联 HTML（`color/size/tint/box/sqrt/sup/sub/i/a/icon`），游戏原生 `<color=...>`/`<size=...>`/`<link=...>` 语法精确兼容输入（模型可能从游戏文本抄入），错误变体转义；Markdown 表格由 `SplitTables` 切成独立 Text 段，用游戏 `tableColWidths/tableRowHeights` 做真表格；网页链接使用 900000–999999 的顺序 ID，点击打开外部浏览器（失败时复制 URL）；科技链接使用 1000–9999，输出 `dsp://tech/<iconTag>`（如 `dsp://tech/dcx`），点击打开科技树并定位、展开对应科技；配方链接使用 10000–19999，输出 `dsp://recipe/<itemIconTag>`，点击打开合成器并选中对应物品主配方；成就链接使用 20000–21000，输出 `dsp://achievement/<id>`，点击打开成就页并定位条目；裸 `http(s)://...` 也自动转成网页链接（代码除外）；块级排版在标题/段落/列表组/引用/代码块/分隔线之间保留一个空行，连续空行折叠；段落内单换行按 Markdown 软换行合并（中文不加空格、英文补空格），行尾两空格/反斜杠作为硬换行。详细映射见 `mod/src/Shared/Text/GameRichText.cs` 与 §6.5；**不**引入第三方 Markdown 渲染器。
- 流式显示：`stream.delta` 到达即追加到当前气泡；渲染节流到**每帧最多一次**（避免逐字触发重建布局）。
- 历史上限：内存中保留最近 N 条（默认 200），更早的由 sidecar 会话文件承载，可通过"打开完整历史"分页拉取。

**布局与交互细节**

- 默认尺寸约 760×520，位置右下，可拖拽/缩放（存配置）。
- 打开时自动聚焦输入框；关闭时清空焦点并把键盘交还游戏。
- 未连接 sidecar 时：输入框禁用，面板显示引导（启动命令 + 文档链接）。
- 思考中显示 `status` 动效；提供"中止"按钮（发 `abort`）。

### 6.4 游戏状态采集（只读工具的实现侧）

已核实的数据入口（`GameData`）：

| 数据 | 入口 |
| --- | --- |
| 存档概要 | `GameMain.data.gameName` / `gameDesc` / `statistics` / `history` / `preferences` |
| 玩家/机甲 | `GameMain.data.mainPlayer` |
| 当前行星 / 星系 | `GameMain.data.localPlanet` / `localStar` / `galaxy` |
| 全部工厂 | `GameMain.data.factories` (`PlanetFactory[]`) + `factoryCount` |
| 戴森球 | `GameMain.data.dysonSpheres` (`DysonSphere[]`) |
| 星际物流 | `GameMain.data.galacticTransport` |
| 警告 | `GameMain.data.warningSystem` |
| 静态原型 | `LDB.items` / `LDB.recipes` / `LDB.techs` / `LDB.models` / `LDB.signals` / `LDB.milestones` / `LDB.goals` … |

**实现原则**：

1. **一切采集在主线程分帧执行**。禁止在单帧内遍历全部工厂——`PlanetFactory` 数量与实体规模都很大。实现为一个**分帧任务队列**：每帧处理预算（默认 2ms），未完成则返回"进行中"，结果缓存若干秒。
2. **聚合优先、原始其次**。例如"电力"工具返回每行星的发电/耗电/储能汇总，而不是逐个发电机的明细；需要明细时由模型带 `detail` 参数二次请求。
3. **分页与上限**。所有列表类结果带 `{total, offset, limit, truncated}`；单次返回体积上限（默认 24KB 文本），超出则截断并给出继续查询的指引。
4. **ID 与名称双写**。返回 `{id: 2001, name: "铁块", nameEn: "Iron Ingot"}`，便于模型理解与回填。
5. **快照一致性**：一次工具调用内使用同一 `gameTick` 的数据；若采集跨帧，记录并返回 `tickAtStart/tickAtEnd`。

### 6.5 操作执行与确认（写工具的实现侧）

**分级**：

| 级别 | 含义 | 例子 | 默认策略 |
| --- | --- | --- | --- |
| L0 | 纯读 | `game.get_*` | 自动执行 |
| L1 | 可逆、无存档副作用 | 移动视角、打标记、打开某面板 | 自动执行，但在聊天流中记录一条"已执行"卡片 |
| L2 | 改变存档状态 | 设置配方、改物流航线、粘贴蓝图、切换研究 | **必须玩家确认**；必须提供撤销或明确标注不可撤销 |

**门禁实现（双重 + 玩家权限档位）**：

1. sidecar 侧：`pi.on("tool_call")` 钩子对 L2 工具调用 `ctx.ui.confirm()`，玩家拒绝则 `{ block: true }`。
2. mod 侧：`confirm_result` 携带一次性 `token`，`ActionService` 校验 token（未过期、未被使用、匹配 `callId`）后才执行。**即使 sidecar 被替换/被绕过，mod 仍不会执行未经玩家确认的写操作。**
   注意：`argsHash` 由 sidecar 计算并在两条消息里复用，mod 不持有原始 args、无法重算；它只能发现 sidecar 内部不一致，不能阻止被替换的 sidecar 在玩家确认后替换参数。参数完整性依赖确认卡文案 + 玩家判断，这是 v1 的已知边界。
3. **权限档位**（聊天窗「权限」下拉框）只决定确认页是否自动同意，不改变工具注册或 token 校验：
   - `readonly`：所有写操作（risk 1/2）都弹确认。
   - `limited`（默认）：risk 1（镜头/标记/面板/方位/撤销）自动放行，risk 2 弹确认。
   - `takeover`：所有写操作自动放行。
   `run_script` 的脚本级确认使用脚本内写工具的最高 risk，因此纯 risk 1 脚本在受限档位下也可直接执行。
   本地蓝图写工具（`blueprint_save` / `blueprint_generate` / `blueprint_fs` 写操作）与
   `game_copy_blueprint` 也按 risk 1 参与同一审批；本地蓝图工具不消耗 mod 令牌，
   确认只代表玩家同意写文件。
   权限只能由玩家修改，模型无法通过工具提权。

**执行约束**：

- 写操作在主线程执行；执行前检查游戏状态（非暂停、非加载中、`GameMain.isRunning`）。
- 每个 L2 工具必须声明 `undo` 能力；能撤销的提供"撤销上一步"按钮。
- 批量写操作（如粘贴蓝图）需要二次确认，并展示影响面（建筑数量、占用面积）。
- 失败必须回滚已做的部分变更，或明确返回"部分成功 + 已做/未做清单"。

### 6.6 主线程调度与性能

- `SidecarClient` 的接收在后台线程；**所有触及 Unity API 的回调经主线程队列派发**（`ConcurrentQueue<Action>` + `Update` 中排空，单帧上限 4ms）。
- 序列化使用 `System.Text.Json`（`netstandard2.1` 可用）或轻量手写；避免在热路径分配（复用 `StringBuilder`）。
- 网络异常不得抛到游戏主循环；全部捕获并转成聊天窗口内的状态提示。
- GC 预算：聊天窗口与状态采集每帧分配 < 32KB；列表类结果使用对象池。

### 6.7 配置（BepInEx）

配置文件 `BepInEx/config/icarus-ii.cfg`：

```ini
[General]
Hotkey = R
HotkeyRequireNoBuildTool = true   ; R 在建造模式下让位给"旋转建筑"（见 §6.2.2）
Language = auto          ; auto | zh-CN | en-US
Streaming = true
LogLevel = Info
DebugToolConsole = false   ; !tool 调试直调；sidecar 需 --debug-tools

[Sidecar]
LaunchMode = spawnExe    ; spawnExe(推荐) | connectOnly | spawnNode | spawnBinary
Host = 127.0.0.1
Port = 8765
ExePath =                ; 空=使用随包分发的 icarus-ii-sidecar.exe
NodePath =               ; 仅 spawnNode 模式
ConnectTimeoutMs = 3000
AutoReconnect = true

[Agent]
AgentDir =               ; 空=使用 %APPDATA%\icarus-ii（sidecar 自持凭证，不复用其他工具）
Model =                  ; 空=使用 sidecar 默认（deepseek-v4-flash）
SystemPromptFile =       ; 空=使用内置

[Actions]
ConfirmLevel2 = true
BackupBeforeWrite = false  ; 用户确认关闭；开启后写操作前自动存档（额外占用磁盘）
AllowedTools =           ; 空=全部注册工具；可拉黑

[UI]
FontScale = 1.0
Opacity = 0.95
MaxHistory = 200
Width = 760
Height = 520

[Privacy]
SendSaveName = false
AllowWikiFetch = true
```

---

## 7. sidecar 端设计（Node / TypeScript）

### 7.1 进程与启动

**技术栈**：Node 22.19+、TypeScript ESM、`fastify`（HTTP，用于健康检查/配置/未来 Web UI）+ `ws`（WebSocket）、`zod`（协议与工具参数校验）。

#### 7.1.1 零额外依赖分发：自包含单文件可执行（**已验证**）

目标是让玩家**除游戏与 BepInEx 外不需要安装任何东西**。本机已跑通完整链路：

**步骤 1｜打成单 JS 文件（含 pi SDK）**

```bash
esbuild src/index.ts \
  --bundle --platform=node --format=cjs --target=node22 --minify \
  --external:@mariozechner/clipboard \
  --define:import.meta.url=import_meta_url \
  --banner:js="const import_meta_url = require('url').pathToFileURL(__filename).href;" \
  --outfile=dist/sidecar.cjs
```

实测结果与踩坑：

| 观察 | 数据 |
| --- | --- |
| 未压缩 bundle | **13.7 MB**，单文件 |
| `--minify` 后 | **7.1 MB** |
| 外部依赖 | 仅 `@mariozechner/clipboard`（可选，纯 try/catch） |
| 原生 `.node` 依赖 | **0**（见下） |
| `--format=esm` | 可运行，但需 `--banner` 注入 `createRequire`；SEA 不接受 ESM 入口 |
| `--format=cjs` | **必须**加 `--define:import.meta.url=` + `--banner`，否则 `fileURLToPath(undefined)` 崩溃 |
| photon（图像处理）WASM | 已内嵌进 bundle（base64），无外部文件 |

**为什么没有原生依赖**：pi 的原生模块都是**平台守卫 + try/catch，且注释明确写明为打包路径设计**：

- `pi-tui` 的 `win32-console-mode.node` 只在 `process.platform === "win32"` 时通过 `cjsRequire` 动态加载，失败即静默降级（仅影响终端 Shift+Tab 区分）——我们不用 TUI。
- `pi-tui` 的 `darwin-modifiers.node` 只在 darwin 加载。
- `clipboard-native.js` 用 `createRequire` + try/catch 加载 `@mariozechner/clipboard`，且在 Linux 无 `DISPLAY`/`WAYLAND_DISPLAY` 时直接跳过，失败返回 `null`。

**步骤 2｜把 Node 运行时一起打包成单个可执行文件**

用 Node 官方 SEA（Single Executable Application）：

```bash
# 2a. 生成 blob（入口必须是 CJS）
cat > sea-config.json <<'EOF'
{ "main": "dist/sidecar.cjs", "output": "sea-prep.blob",
  "disableExperimentalSEAWarning": true, "useSnapshot": false, "useCodeCache": false }
EOF
node --experimental-sea-config sea-config.json

# 2b. 拷贝对应平台的 node 二进制并注入 blob
cp "$(command -v node)" icarus-ii-sidecar            # Linux x64
# cp node.exe icarus-ii-sidecar.exe                 # Windows x64（可从 nodejs.org 下载后离线注入）
npx postject icarus-ii-sidecar NODE_SEA_BLOB sea-prep.blob \
  --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2
```

**实测结果**：

| 产物 | 体积 | 验证状态 |
| --- | --- | --- |
| Linux x64 `icarus-ii-sidecar` | **132 MB** | ✅ 在**空目录**（无 `node_modules`、无 Node 安装）中运行成功，正确加载 pi SDK |
| Windows x64 `icarus-ii-sidecar.exe` | **107 MB** | ⚠️ 注入成功、PE 有效；运行测试被本机沙箱挡住（见下），M0 复测 |

> **沙箱限制说明**：Windows 产物在 Wine 下的运行验证未能完成——`wineserver` 强制使用 `/run/user/1000/wine`，在当前沙箱下只读。这是**验证环境**的限制，不是方案的限制。M0 需在真实环境跑一次冒烟测试（`wine icarus-ii-sidecar.exe` 应打印 `esbuild-bundle-ok`）。

**体积说明与优化方向**：132MB 中约 125MB 是 Node 运行时本身，JS 只占 7MB。若需缩小，可选方案（按性价比排序）：

1. 用 **Bun `--compile`** 或 **Deno compile** 替代 SEA，预估 55–90MB；代价是需验证 pi 在 Bun/Deno 的 Node 兼容层上的行为（尤其 `undici`/`child_process`）。
2. 保持 SEA 但接受体积（一次性下载，之后无感）。
3. 退化为"引导安装 Node"（见备选模式），体积最小但增加玩家操作。

**许可证**：`@earendil-works/pi-coding-agent` 为 **MIT**，Node.js 亦为 MIT 类许可 ⇒ 可合法随包分发。

#### 7.1.2 启动模式

| 模式 | 适用环境 | 机制 | 玩家依赖 |
| --- | --- | --- | --- |
| **`spawnExe`（默认）** | 原生 Windows **与** Proton | mod `Process.Start` 随包分发的 `icarus-ii-sidecar.exe`（Windows x64） | **无** |
| `connectOnly` | 开发期 / 高级玩家 | 已有 sidecar 常驻，mod 只连接 | 视情况 |
| `spawnNode` | 开发期（宿主循环最快） | mod 启动本机 `node` + `dist/sidecar.cjs` | Node |
| `spawnBinary` | 开发期（Linux 原生调试） | mod 启动随包的 Linux 二进制 | 无 |

> **关键点**：游戏在 Wine 内运行，`Process.Start` 只能启动 PE 文件。而**自包含 Windows exe 恰好就是 PE** ⇒ `spawnExe` 在 Proton 与原生 Windows 上**用同一套逻辑**即可自动拉起 sidecar。这同时解决了"零依赖"与"Proton 下无法自动启动"两个问题——这正是 §16 R2 的解法，也是 §2.4"Windows 优先"的直接收益。

**降级链**（mod 启动时按序尝试）：

```
spawnExe（随包 exe）                        ← 默认，覆盖原生 Windows + Proton
  └─ 失败 → connectOnly（等 3s，看是否已有 sidecar 在监听）
       └─ 失败 → spawnNode / spawnBinary（开发期路径，若配置了路径）
            └─ 失败 → 窗口显示手动启动指引
```

#### 7.1.3 `connectOnly` 的常驻方案（可选，非默认）

适合希望 sidecar 独立于游戏生命周期（多存档共用、便于调试）或需要在本机原生运行 sidecar 的开发场景。

**Windows**：把 `icarus-ii-sidecar.exe` 注册为计划任务/启动项，或直接手动运行。

**Linux（开发期，本机原生运行 sidecar 以便热重载）**：systemd 用户级 socket 激活——

```ini
# ~/.config/systemd/user/icarus-ii-sidecar.socket
[Socket]
ListenStream=127.0.0.1:8765

[Install]
WantedBy=sockets.target
```

```ini
# ~/.config/systemd/user/icarus-ii-sidecar.service
[Service]
ExecStart=%h/.local/bin/icarus-ii-sidecar serve --systemd-socket
Restart=on-failure
```

```bash
systemctl --user enable --now icarus-ii-sidecar.socket
```

效果：mod 首次连接时 systemd 才拉起 sidecar（空闲零开销），游戏退出后 sidecar 自动空闲退出。

#### 7.1.4 端口与单实例

默认 8765；若被占用，sidecar 记录明确错误。写一个 `runtime.json`（含实际端口、pid、启动时间）到约定的数据目录，mod 优先读它，支持非默认端口。`spawnExe` 模式下由 mod 指定端口并等待子进程写出 `runtime.json`。

### 7.2 pi 集成

```ts
import {
  createAgentSession, DefaultResourceLoader, ModelRuntime,
  SessionManager, SettingsManager, defineTool,
} from "@earendil-works/pi-coding-agent";
```

要点（均与 pi 文档核实）：

1. **模型与凭证**：`ModelRuntime.create({ authPath, modelsPath })`，两者都指向 **`%APPDATA%\icarus-ii\`**（§2.4）。**不复用 `pi` 的既有登录**（用户确认）——sidecar 完全自持凭证。
   - **默认 Provider：DeepSeek**（用户确认）。
   - Anthropic Messages：pi 原生支持，作为备选 Provider 保留。
   - 其它 OpenAI 兼容端点（OpenAI / 智谱 / 本地 Ollama / vLLM…）：通过 `models.json` 声明。
   - 内置 `models.json` 默认内容：
     ```jsonc
     { "providers": { "deepseek": {
         "name": "DeepSeek",
         "baseUrl": "https://api.deepseek.com/v1",
         "api": "openai-completions",
         "models": [
           { "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash", "contextWindow": 1000000, "maxTokens": 384000 },
           { "id": "deepseek-v4-pro",   "name": "DeepSeek V4 Pro",   "contextWindow": 1000000, "maxTokens": 384000 }
         ]
     } } }
     ```
     > `contextWindow` / `maxTokens` 为保守占位值。**本机无法访问 `api-docs.deepseek.com`（DNS 被拦）**，未能在设计阶段核实当前实际数值；首次配置自检时应从端点探测或由玩家覆盖，实现时以官方文档为准。
   - **默认模型：`deepseek-v4-flash`**。理由：价格档位最低（$0.14/$0.28 每 M token），
     适合"按 token 扣能量"的设计——单位算力的成本越低，玩家的资源消耗曲线越好调。
   - **模型清单已实测修正**（M1，升级前的 pi `0.82.1`）：内置的 DeepSeek 目录为
     `deepseek-v4-flash` 与 `deepseek-v4-pro`，**不存在** `deepseek-chat` /
     `deepseek-reasoner`。设计初稿依据的是旧版 DeepSeek API 命名，已作废。
     两者均为 `reasoning: true`、上下文 1,000,000、最大输出 384,000。
   - **思考档位**：Flash 的 `thinkingLevelMap` 映射 `low` / `high` / `max`；
     Pro 只映射 `high` / `max`（`minimal`/`low`/`medium` 为 `null`，`low` 会被 clamp）。
     聊天窗暴露 `关闭 / 低 / 高 / 极限` 四档，默认 `高`；思考 token 同样计入能量成本。
   - 首次配置流程（设置页）必须包含一次**最小工具调用自检**（发一个只含 dummy tool 的请求，断言返回结构里出现 tool_call）——这是唯一能可靠判定"该模型+该端点是否真能驱动本项目"的方法。
2. **工具集**：`noTools: "builtin"` + `customTools: [...]`。内置 `bash`/`read`/`write`/`edit`/`grep`/`find`/`ls` **全部不可用**。
3. **系统提示**：`DefaultResourceLoader({ systemPromptOverride })`，内容见 §7.6。
4. **事件桥接**：`session.subscribe(ev => bridge(ev))`，映射规则：
   | pi 事件 | 到 mod 的消息 |
   | --- | --- |
   | `message_update` + `text_delta` | `stream{channel:"text"}` |
   | `message_update` + `thinking_delta` | `stream{channel:"reasoning"}` |
   | `message_update` + `toolcall_end` | `message`（工具卡片占位） |
   | `tool_execution_start/update/end` | `tool_progress` / 卡片状态更新 |
   | `agent_start` / `agent_settled` | `status` |
   | `auto_retry_start/end` | `status`（含重试提示） |
   | `compaction_start/end` | `status`（"整理上下文中"） |
5. **确认门禁**：`pi.on("tool_call", async (e, ctx) => {...})`，对 L2 工具 `await ctx.ui.confirm(...)`；确认请求经桥接转发到 mod 的确认卡片。
6. **中止**：`session.abort()`；对应 mod 的"中止"按钮。
7. **会话**：`SessionManager.create(<dataDir>/sessions)` 持久化；每局存档一个会话（以 `gameName + saveId` 命名），便于"接着上次聊"。
8. **版本锁定**：`package.json` 固定 `"@earendil-works/pi-coding-agent": "1.4.0"`，避免 SDK 漂移导致行为变化（其事件与 API 仍在演进）。

### 7.3 工具注册表

工具在 sidecar 内定义，按实现来源分三类：

| 类别 | 实现位置 | 是否需要 mod 往返 |
| --- | --- | --- |
| `game.*` 读 | mod | 是（WS `tool_call`） |
| `game.*` 写 | mod | 是 + 确认门禁 |
| `kb_*`（及后续 `wiki_*`） | sidecar | 否 |

**通用桥接工具工厂**：

```ts
function gameTool<T>(spec: {
  name: string; description: string; parameters: TSchema;
  risk: 0 | 1 | 2; timeoutMs?: number;
}): ToolDefinition
```

`execute` 内部：分配 `callId` → 校验连接 → 发送 `tool_call` → 等待 `tool_result`（超时/断线返回结构化错误）→ 结果裁剪到 token 预算内 → 返回 `{ content: [{type:"text", text}], details }`。

**结果裁剪**：统一在 sidecar 侧做（mod 只提供上限内数据）。策略：JSON→紧凑文本；表格类按行截断并附"还有 N 条，可用 offset 继续"；超限时优先保留聚合值。

#### 7.3.1 工具路由与单位约定（M2 定案）

- **路由**：sidecar 进程内只有一个 `PiAgent`。工具执行由 `ToolHost` 路由到**发起本轮 prompt 的 mod 连接**（每轮 `bind(session)`），不假设"全局只有一个连接"。断线、换档或能力变化时，所有 pending 调用立即以 `tool_unavailable` / `not_in_game` 失败，并重建 pi 会话。
- **注册**：工具按 `hello.capabilities` 的交集注册（M2 为 `game.read.v1`）；未声明能力不注册，`ready.payload.tools` 报告实际列表。能力变化后必须重建 pi 会话，避免模型看到不存在的工具。
- **串行**：所有 `game.*` 工具 `executionMode: "sequential"`，且在主线程采集；同一连接同一时刻最多一个工具调用。
- **命名**：模型可见名用下划线（`game_get_overview` 等）。OpenAI 兼容端点会把非 `[a-zA-Z0-9_-]` 字符替换成 `_`，用点号会导致模型看到的工具名与回传的 `tool_call.name` 不一致。本文中的 `game.*` 仅指语义分组，协议以附录 B.1 为准。
- **单位**：电力 **kW**（`PowerNetwork` 的每 tick 值 × 60 ÷ 1000）、储能 **kWh**、产线 **/min**、时间 **s**、比例 **%**。换算在 mod 侧完成（只有 mod 知道 tick 语义），sidecar 只负责取整/格式化。每个数值字段带单位后缀，并附 `display` 字符串（如 `"1.23 MW"`），避免模型自行换算。
- **快照**：`tool_result.meta` 必带 `gameTick`、`saveKey`、`tickAtStart/tickAtEnd`；跨帧采集如实标注。

### 7.4 知识层

四类知识源，全部**按需**：

**(a) LDB 导出库（离线、权威）**

- 首次运行（或游戏版本变化时）触发一次导出：mod 侧工具 `game_export_prototypes` 把 `LDB` 的关键原型集序列化为 JSON，经 WS 分块传给 sidecar，写入 `kb/ldb-v1-<gameVersion>-<locale>.json`。
- sidecar 建立**内存索引**（名称/ID/SID/描述子串；数据 <1MB、查询 <20ms）。SQLite FTS5 留作
  wiki/大量文本接入后的备选；M3 不引入 DB 生命周期。
- 覆盖：`items`(ItemProtoSet)、`recipes`、`techs`、`models`(建筑)、`signals`、`veins`、`milestones`、`goals`。
- 导出体积控制：只导出"AI 有用"的字段（id、名称（当前语言）、描述、配方输入输出与耗时、科技前置与消耗、建筑参数），跳过模型/贴图/GUID 等。实测全量 JSON ~750KB。
- 缓存：`%APPDATA%/icarus-ii/kb/ldb-v1-<gameVersion>-<locale>.json`；版本/语言变化才重建。
- **建筑参数（`assemblerSpeed`/`workEnergyPerTick`/`minerPeriod`）在 `ItemProto.prefabDesc` 里，
  不在 LDB 的 asset 中**，只能运行时导出。

**(a2) 游戏文本与指引（M3b）**

- 顾问字幕（`Locale/<lcid>/base.txt` 的 `顾问字幕*`）、里程碑、成就、控制提示；
- 指引条目（`prototype.txt` 的 `指引*`）；
- 教程正文（`Wiki/Tutorial/tutorial-*-<lang>.txt`，**不在 Locale**）；
- 术语表（`dictionary.txt`）。
- 统一清洗：去富文本/图标短码、`$LKEY_n;` → `[按键]`、保留 `{0}` 并标 `templated`。
- `KbText` 带 `kind/phase/spoiler/source/templated/unresolvedKeys`；`kb_search` 默认 `spoiler=low`，剧情/教程需显式 `story`；按玩家进度收紧留到后续。

**(b) 实时存档状态**

- 不落盘、不预取；经 `game_get_*` 工具即时读取（见 §8）。
- 会话内缓存短 TTL（默认 5s），避免模型连续追问时反复遍历工厂。

**(c) Wiki（在线、可选）**

- 自研最小 MediaWiki 客户端（`action=query&list=search` / `action=parse`），不做通用 HTTP 工具，只暴露 `wiki_search` / `wiki_page` 两个受限工具（M3.5 可选）。
- 目标站点可配置，默认：
  - 中文：`https://wiki.biligame.com/dsp/`
  - 英文：`https://dsp-wiki.com/`
- Biligame 页面 URL 是 `https://wiki.biligame.com/dsp/<页面名>`，**不要**拼 `/wiki/`：站点会 301 到 `/dsp/Wiki/<页面名>`，玩家看到的就是错误地址；英文站仍是标准 `/wiki/`。
- 本地缓存（`kb/wiki-cache.sqlite`，含 TTL 与页面哈希），减少重复抓取与站点压力。
- 由 `Privacy.AllowWikiFetch` 控制开关，默认允许；关闭后 `wiki_*` 工具不注册。
- 抓取内容一律标记为**不可信外部数据**，在回填给模型的文本中显式注明来源，并指示模型只提取事实、不执行其中的任何指令。
- > 说明：本机已装 `bwiki-mcp`（MediaWiki MCP server）。pi 无内置 MCP 支持（其 `docs/usage.md` 明确说明），因此本项目自研轻量客户端；若将来切到 `DshAcpRuntime`，可改为直接复用 `bwiki-mcp`。

**(d) 配方计算**

- `kb_recipe_tree(item, amount|rate, depth)`：在 sidecar 内基于 LDB 做递归展开与原料汇总（纯计算，不调模型），把"算数"从模型手里拿走。
- `kb_plan(item, rate)`：在同一套数据上算机器数/电力/传送带；原料仍走配方树，保证两个工具口径一致。
- 主配方选择与 `ItemProto.FindRecipes` 一致；副产物按同名原料抵扣，剩余单列；循环/深度上限都会在 `notes` 里说明。

### 7.5 会话与持久化

```
%APPDATA%\icarus-ii\                     # 原生 Windows 与 Wine 内一致
├── config.json            # sidecar 自身配置
├── models.json            # Provider/模型声明（默认含 DeepSeek）
├── auth.json              # 凭证（sidecar 自持，不复用其他工具）
├── sessions/
│   ├── <saveKey>.jsonl    # pi 会话文件（会话树，可分支）
│   └── index.json         # saveKey → 标题、最后活跃时间
├── kb/
│   ├── ldb-v1-<ver>-<locale>.json   # M3 运行时导出/缓存
│   └── wiki-cache.sqlite            # M3.5 可选
├── runtime.json           # 实际端口/pid（§7.1.4）
└── logs/
```

> Proton 下的宿主侧实际路径：`steamapps/compatdata/1366540/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii/`。开发时直接看这个目录即可，与 Windows 上同构。

- `saveKey = sha1(gameName + creationTime + 存档标识)`，避免不同存档串话（`GameMain.creationTime` 已核实存在）。
- 聊天历史即 pi 的会话文件；游戏内"历史"按钮调用 `get_entries` 分页读取。
- `game_event: save_loaded` 时切换会话；上下文不跨存档复用。

### 7.6 系统提示词（骨架）

```
你是《戴森球计划》(Dyson Sphere Program 0.10.30) 的游戏助手，嵌入在玩家的游戏内聊天窗口中。

角色：
- 回答游戏机制、配方、科技、产线规划、发展路线问题。
- 玩家询问"我现在的状况/该怎么办"时，先调用 game.* 工具读取真实状态，不要凭印象回答。

数据纪律（重要）：
- 任何配方、数值、科技前置关系，必须来自 kb_* 工具（游戏本体数据），不得凭记忆编造。
- 工具未返回的信息，明确说"我没有这项数据"，不要猜测。
- 数值计算必须调用 kb_* 工具，不要自己心算：原料/kb_recipe_tree，机器数/电力/kb_plan。

行动纪律（重要）：
- 写操作是否弹确认由玩家的「权限」档位决定：只读=全部确认；受限=低风险直接执行、改存档确认；接管=全部直接执行。
- 执行写操作前，先用简短语句说明你打算做什么、影响是什么。
- 一次只做一件可解释的事；不要把多个写操作打包成一次"大动作"。
- 玩家拒绝后不要重试同一操作，改为询问意图。

表达：
- 中文回答，简洁，面向正在玩游戏的玩家（他只有半屏注意力）。
- 优先给结论与可执行步骤，其次才是原理。
- 涉及游戏内术语时使用游戏官方译名（可通过 kb_* 查询确认）。
```

`DefaultResourceLoader({ systemPromptOverride })` 注入；玩家可通过 `SystemPromptFile` 覆盖。

---

## 8. 工具清单

> `risk`：0=只读，1=可逆无副作用，2=改变存档状态（强制确认）。
> 所有 `game.*` 工具支持 `offset`/`limit`，返回 `{total, truncated}`。

### 8.1 游戏只读工具（risk 0，由 mod 执行）

| 工具 | 参数 | 返回 | 对应游戏 API |
| --- | --- | --- | --- |
| `game.get_overview` | — | 存档名、游戏时刻、模式、当前行星/星系、玩家位置 | `GameMain.data.gameName` / `gameDesc` / `localPlanet` / `localStar` |
| `game.get_player` | `detail?` | 机甲能量/护盾/核心、背包物品（id+名称+数量） | `GameMain.data.mainPlayer` |
| `game.get_planet` | `planetId?`, `detail?`, `veins?`（summary/groups/nodes）, `veinOffset?`, `veinLimit?` | 行星类型、矿脉汇总/矿脉群/逐矿脉（经纬度，分页）、建筑计数、面积/经纬 | `PlanetData`、`PlanetFactory`、`VeinGroup`/`VeinData` |
| `game.get_production` | `planetId?`, `itemFilter?`, `offset/limit` | 各物品产能/消耗/净额、堵塞建筑 | `PlanetFactory` 生产统计 |
| `game.get_power` | `planetId?` | 发电/耗电/储能、电网余量、告警 | 电力系统 |
| `game.get_logistics` | `planetId?` | 物流塔/站、航线、运输船与翘曲器状态 | `galacticTransport`、`PlanetFactory` |
| `game.get_research` | — | 已解锁科技、当前研究、队列、可用升级 | `GameHistoryData` / 科技系统 |
| `game.get_dyson` | `starId?` | 戴森球壳/框架/太阳帆、发电量 | `GameMain.data.dysonSpheres` |
| `game.get_alerts` | — | 警告系统摘要（断电、缺料、袭击等） | `warningSystem` |
| `game.query_item` | `nameOrId` | 物品原型（多语言名、描述、堆叠、来源） | `LDB.items` |
| `game.query_recipe` | `nameOrId` | 配方（输入/输出/耗时/建筑/解锁科技） | `LDB.recipes` |
| `game.query_tech` | `nameOrId` | 科技（前置、消耗、解锁内容） | `LDB.techs` |
| `game.export_prototypes` | `sets[]`, `chunk` | 分块导出原型数据（供 KB 建库） | `LDB.*` |

**M4a 已实现的三组能力**（协议附录 B.1，按能力单独广告，老 mod 不受影响）：

| 工具 | 能力 | 参数 | 返回 | 对应游戏 API |
| --- | --- | --- | --- | --- |
| `game_get_entity` | `game.entity.v1` | `planetId?`, `entityId?`, `entityIds?`（≤10）, `expectProtoId?`, `fields?` | 单/批量建筑：原型、配方、功率、分拣器/传送带/仓储/物流塔、星系/行星/经纬度、`stale`；`fields` 裁剪段 | `PlanetFactory.entityPool` + `factorySystem` / `cargoTraffic` / `transport` |
| `game_get_entities` | `game.entity.v1` | `planetId?`, `protoId?`, `protoName?`, `recipeId?`, `offset/limit` | 建筑分页列表 | 同上 |
| `game_get_progress` | `game.progress.v1` | — | 派生阶段 + 判定信号、已解锁科技、里程碑、教程、研究队列 | `GameHistoryData`、`GameMain.data.milestoneSystem` |
| `game_capture_view` | `game.view.v1` | `width?`, `height?`, `includeEntities?` | JPEG 图片 + 可见实体屏幕坐标与经纬度 | `GameCamera.CaptureScreenshot` |
| `game_analyze_area` | `game.analyze.v1` | `planetId?`, `entityId?`, `radius?`, `center?`, `all?` | 范围聚合：建筑/配方计数、装机产能、耗电、物流设施、电网 | `PlanetFactory` + `PowerSystem` |

> **截图（M4a 起支持）**：`game_capture_view` 复用 `GameCamera.CaptureScreenshot`——
> 它把 `cullingMask` 设成 `gameLayerMask`，UGUI 不进画面，所以不需要"隐藏界面"开关；
> PNG 字节在 `GameMain.data.screenShot`，mod 侧转 JPEG（q80，长边 ≤1280）后按协议
> `tool_result.image` 回传。图片既不混进 `data`，也不落盘。
>
> 模型侧：`ModelEntry.input` 覆盖 pi 目录的模态（目录仍把 DeepSeek 标成只收文本）。
> 不覆盖时 pi 的 `openai-completions` 适配层会按 `model.input.includes("image")`
> 静默丢弃截图；覆盖后它会补一条带 `image_url` 的 user 消息。
>
> **聊天窗展示**：截图由本机产生，不走协议回传——`Plugin` 直接把 `GameToolOutcome.Image`
> 的字节交给 `ChatWindow.AttachToolImage`，在工具块里挂一个 220px 高的 `Image`
> 并自动展开。纹理/精灵的所有权交给节点上的 `ToolImageSprite`，随节点销毁释放
> （`TrimHistory` / `ClearTranscript` / `RemoveBlock` 三条路径都覆盖）。
> 这是数据驱动内容，不改 prefab 静态布局。
>
> **高频读工具**：`game_get_entity` 是 AI 最常反复调用的详情工具（§20.1 D4）。实现时优先做字段裁剪（`fields`）、按实体/帧缓存、批量 `entityIds` 和统一结果上限，避免每次返回完整详情把上下文和延迟吃掉。

### 8.2 知识工具（risk 0，sidecar 本地）

| 工具 | 参数 | 说明 |
| --- | --- | --- |
| `kb_search` | `query`, `kinds?`, `limit?` | 搜索（物品/配方/科技/建筑/提示/教程/里程碑/指引/术语） |
| `kb_get` | `kind`, `idOrName` | 精确取一条（含 M3b 文本类） |
| `kb_recipe_tree` | `item`, `amount?`, `rate?`, `depth?`, `recipeId?` | 递归配方展开 + 原料汇总（纯计算，副产物抵扣） |
| `kb_plan` | `item`, `rate`, `depth?`, `recipeId?`, `buildingTier?`, `buildingId?`, `beltSpeed?` | 产线规划：机器数/电力/传送带 + 复用配方树的原料（M4b） |
| `run_script` | `code`, `timeoutMs?`, `maxCalls?` | 脚本：QuickJS 沙箱 + `dsp.call` 复用同一工具注册表；默认只读，脚本含字面量写工具时先做一次脚本级确认（M4d/M5） |

> 本地工具（`kb_*` / `run_script`）不经过 `tool_call`，默认在聊天窗里不可见；
> sidecar 通过 `tool_activity`（协议 A.8）补发 start/end，mod 用同一套工具卡渲染。
> 这样玩家能看到“量化计算”“知识库检索”“脚本”确实被执行，而不是只看到游戏工具。
| `kb_compare` | `items[]` | 多物品对比（M3 后置；需要运行时 `building` 参数） |
| `wiki_search` | `query`, `lang?`, `limit?` | MediaWiki 搜索（M3.5 可选；外部不可信数据） |
| `wiki_page` | `title`/`pageId`, `lang?`, `section?` | 拉取页面正文（M3.5 可选） |

### 8.3 游戏写工具（risk 1/2，由 mod 执行）

| 工具 | risk | 参数 | 影响面说明（确认卡中展示） |
| --- | --- | --- | --- |
| `game.focus_camera` | 1 | `target`（行星/星系/建筑 id） | 视角移动 |
| `game.open_panel` | 1 | `panel`（inventory/tech/starmap/…） | 打开游戏面板 |
| `game.set_research` | 2 | `techId` | 切换研究目标（可能浪费已投入） |
| `game.drop_item` | 2 | `itemId`, `count?` | 丢弃背包物品到地面；需手动拾回，不进入撤销栈 |
| `game.set_recipe` | 2 | `buildingId`, `recipeId` | 改变一台建筑的生产配方 |
| `game.set_assembler_batch` | 2 | `selections[]` | 批量设置（二次确认） |
| `game.set_logistics_route` | 2 | `stationId`, `route` | 修改物流航线 |
| `game.paste_blueprint` | 2 | `blueprintId`/`data`, `anchor` | 大规模建造（二次确认 + 占地/建筑数预览） |
| `game.add_to_build_bar` | 2 | `itemId`, `slot` | 修改建造栏 |
| ~~`game.dismantle`~~ | — | — | 按 §9.1「不可撤销的 L2 默认不实现」不提供 |
| `game.take_output` | 2 | `buildingId` | 取出生产建筑成品，保留配方 |
| `game.feed_input` | 2 | `buildingId`, `itemId`, `count` | 背包原料直接进生产建筑输入缓冲 |
| `game.storage_set_filter` | 2 | `buildingId`, `itemId` | 设置/清除存储过滤器 |
| `game.storage_set_limit` | 2 | `buildingId`, `limit` | 设置存储可用格数上限 |
| `game.storage_move` | 2 | `sourceId`, `targetId`, `itemId`, `count` | 存储间移动物品 |
| `game.storage_deposit` | 2 | `buildingId`, `itemId`, `count` | 背包→存储 |
| `game.storage_withdraw` | 2 | `buildingId`, `itemId`, `count` | 存储→背包 |
| `game.tether_add` | 1 | `kind`, `planetId?`, `buildingId?` | 建立机甲到行星/建筑的方位指示 |
| `game.tether_clear` | 1 | 无 | 清除方位指示 |
| `game.tether_list` | 0 | 无 | 列出方位指示 |

> 当前实现 `focus_camera` / `open_panel` / `set_research` / `drop_item` / `set_recipe` / `set_assembler_batch` / 撤销；其余按里程碑推进（§15）。写工具集是风险最高的部分，必须逐个设计撤销语义。
>
> §20 的候选工具尚未实现，**不注册**。新增写工具必须使用新的能力串（如 `game.build.v1` / `game.storage.v1`），不能继续挂在 `game.write.v1` 下，否则老 mod 会被注册它并不存在的工具（§20.5）。

### 8.4 交互工具（risk 0）

| 工具 | 说明 |
| --- | --- |
| `ui.present` | 在聊天窗中渲染结构化卡片（计划清单、对比表、路线图） |
| `ask_user` | 就模糊意图向玩家提问（映射到 pi 的 `ctx.ui.select/input`） |

---

## 9. 安全与隐私

### 9.1 威胁模型

| 威胁 | 后果 | 缓解 |
| --- | --- | --- |
| Prompt Injection（Wiki 页面/物品描述里藏指令） | 模型被诱导执行恶意操作 | 外部数据显式标注为不可信；写操作强制人确认；工具白名单；不提供通用 HTTP/shell |
| 模型误判导致破坏性操作 | 存档损坏、产线被拆 | L2 强制确认 + 影响面预览 + 撤销栈；**无自动存档兜底**（用户关闭），因此"不可撤销的 L2 工具默认不实现"是硬约束 |
| API Key 泄露 | 经济损失 | Key 只在 sidecar；mod 配置不含 Key；支持环境变量注入 |
| 本地端口被其他进程接入 | 伪造工具调用 | 绑定 `127.0.0.1`；连接时握手校验 `instanceToken`（sidecar 启动时生成，写入 `runtime.json`，仅本机可读） |
| 存档信息外泄 | 隐私 | 默认不发送存档名/账号名；**截图会进入模型上下文**（可能含画面里的存档名、布局、聊天），只在玩家显式要求或调用 `game_capture_view` 时产生；不落盘；明确文档说明哪些数据会离开本机 |
| 游戏崩溃 | 玩家损失 | 所有网络/异常路径与游戏主循环隔离；写操作在主线程但做前置状态检查 |

### 9.2 工具权限模型

- 三层：**未注册**（模型看不到）→ **注册但被拒绝**（`tool_call` 钩子 block）→ **允许**。
- `Actions.AllowedTools` 允许玩家进一步收窄（拉黑清单在 sidecar 侧生效，之后才轮到确认流程）。
- 默认**不注册**任何风险等级 > 2 的能力（本项目不存在 L3）。

### 9.3 确认令牌机制

```
sidecar                                          mod
  │ 生成 confirmRequestId + token(随机32B, 单次)
  ├─ confirm_request ───────────────────────────►│
  │                                              │ 玩家点击
  │◄─ confirm_result{decision, token} ───────────┤
  │ 校验 decision，写操作 tool_call 带 token ────►│
  │                                              │ 校验 token 有效且匹配 callId
  │                                              │ 消耗 token → 执行
  │◄─ tool_result ───────────────────────────────┤
```

- token 绑定 `{callId, toolName, argsHash}`，参数变化则失效（防"确认了 A 却执行 B"）。
- token 有效期默认 60s，单次使用。
- v1 **不实现** per-tool `allow_always`。确认是否自动通过由玩家的权限档位统一决定：
  `readonly` 全部要问；`limited` 自动放行 risk 1、risk 2 仍问；`takeover` 全部放行。
  三种档位都保留一次性 token 校验。

---

## 10. 性能预算

| 指标 | 目标 |
| --- | --- |
| 热键响应 | < 1 帧 |
| 窗口开关 | 无卡顿（UI 对象池复用，不销毁重建） |
| 流式渲染 | 每帧最多 1 次布局重建；长回复不掉帧 |
| 单次只读工具采集 | 主线程累计 < 4ms/帧，总时长 < 200ms（大范围查询允许分帧到 1s） |
| sidecar 空闲内存 | < 120MB |
| sidecar 冷启动到可连接 | < 1.5s |
| 断线重连 | 指数退避，最长 30s |

---

## 11. 国际化

- **首期只做 `zh-CN`**，但结构上分离：
  - mod：`Localization/Strings.zh-CN.json` + `Strings.en-US.json`，键名如 `chat.title`、`confirm.allow`；启动时按 `Localization.isZHCN`（已核实）或配置选择，缺失键回退 `zh-CN` 再回退键名。
  - 游戏术语（物品/建筑名）**不**自建词表，直接用 `Localization.Translate` 与 `LDB` 提供的多语言名，避免与游戏译名不一致。
  - sidecar：系统提示词与工具描述模板化为 `prompts/<locale>/system.md`、`tools.<locale>.json`。
- 模型回答语言由 system prompt 控制（首期固定中文）。

---

## 12. 目录结构与构建

```
icarus-ii/
├── design.md                     # 本文档
├── README.md                     # 安装与使用（含 Proton 步骤）
├── docs/
│   ├── install-linux-proton.md
│   ├── install-windows.md
│   ├── configuration.md
│   └── tools-reference.md        # 自动从工具注册表生成
├── mod/                          # C# BepInEx 插件
│   ├── IcarusII.sln
│   ├── Directory.Build.props     # DSPGameDir / BepInExDir 路径变量
│   └── src/IcarusII/
│       ├── IcarusII.csproj       # netstandard2.1
│       ├── Plugin.cs
│       ├── Config/PluginConfig.cs
│       ├── Transport/{SidecarClient,Protocol,ReconnectPolicy}.cs
│       ├── UI/{ChatWindow,MessageList,ToolCard,ConfirmCard,InputBar,UITheme}.cs
│       ├── Input/{HotkeyService,FocusGuard,KeyConflictDetector}.cs
│       ├── Game/{GameStateService,StateTasks,ActionService,ActionGuards,UndoStack,GameRefs}.cs
│       └── Localization/Strings.*.json
├── sidecar/                      # Node/TS
│   ├── package.json              # 固定 pi SDK 版本
│   ├── tsconfig.json
│   └── src/
│       ├── index.ts              # CLI: serve | export | doctor
│       ├── server.ts             # fastify + ws + 握手/鉴权
│       ├── protocol.ts           # zod schema（与 mod 侧一致）
│       ├── runtime/{AgentRuntime.ts,PiRuntime.ts,DshAcpRuntime.ts}
│       ├── tools/{gameTools.ts,actionTools.ts,kbTools.ts,wikiTools.ts,uiTools.ts}
│       ├── kb/{ldbIndex.ts,recipeTree.ts,wikiClient.ts,exportLdb.ts}
│       ├── session/{store.ts,saveKey.ts}
│       └── prompts/{system.zh-CN.md}
├── tools/MetaDump/               # 开发辅助：.NET 元数据转储（用于核对游戏 API）
├── scripts/
│   ├── build-mod.sh
│   ├── build-sidecar.sh          # esbuild bundle + SEA 注入，多平台（已验证，见 §7.1.1）
│   ├── package.sh                # 生成 dist/ 发布包
│   ├── install.sh                # 玩家侧安装器：定位游戏目录 + 复制 + 写 Proton DLL 覆盖（§2.3.4）
│   ├── install.bat               # 同上，Windows
│   └── dev-link.sh               # 开发期符号链接到游戏目录
└── dist/                         # 发布产物（不入库）
```

**构建**

- mod：`dotnet build -c Release`，产物 `IcarusII.dll` → `BepInEx/plugins/IcarusII/`。
- sidecar（四步流水线，`scripts/build-sidecar.sh`）：
  1. `tsc` 类型检查 + `esbuild` 打单文件 `dist/sidecar.cjs`（7.1MB，零原生依赖，参数见 §7.1.1）
  2. `node --experimental-sea-config` 生成 blob
  3. 对每个目标平台注入：Windows x64（`node.exe`）→ `icarus-ii-sidecar.exe`；Linux x64 → `icarus-ii-sidecar`
  4. 校验：在空目录中冒烟运行，断言输出版本号
- 发布包布局（面向后续 Thunderstore 发布）：
  ```
  IcarusII/
  ├── manifest.json
  ├── README.md
  ├── icon.png
  ├── BepInEx/
  │   ├── plugins/IcarusII/
  │   │   ├── IcarusII.dll
  │   │   └── sidecar/icarus-ii-sidecar.exe      # 自包含，约 107MB（Windows）
  │   └── config/icarus-ii.cfg (示例)
  └── CHANGELOG.md
  ```
  > **已决定（用户确认）**：接受发布包变大，sidecar **全部内置**，不做引导下载。
  > 若将来要发布到 Thunderstore（其对单文件体积/可执行文件有实际限制），可选"包内只放引导器 + 从 GitHub Releases 取 exe"作为**发布专用**变体；`LaunchMode` 已为该抽象留位，不影响主设计。

**开发流程**

1. 修改游戏目录 `Managed/` 下 DLL 后，`Directory.Build.props` 指向它编译。
2. `dev-link.sh` 把 DLL 软链到游戏 `BepInEx/plugins/`，重启游戏即生效。
3. sidecar 支持 `--port` + `tsx watch` 热重载；协议变更时先改 `protocol.ts`，mod 侧 `Protocol.cs` 与之对齐（用一份 JSON Schema 双向生成或对照测试）。

---

## 13. 配置项一览

见 §6.7（mod 侧）与下（sidecar 侧，`%APPDATA%\icarus-ii\config.json`）：

```jsonc
{
  "host": "127.0.0.1",
  "port": 8765,
  "dataDir": "%APPDATA%\\icarus-ii",   // 全部状态的根目录（§2.4）
  "modelsPath": null,                   // 默认 <dataDir>/models.json
  "authPath": null,                     // 默认 <dataDir>/auth.json（自持，不复用他处）
  "provider": "deepseek",               // 默认 Provider（用户确认）
  "defaultModel": "deepseek-v4-flash",  // 默认模型（需支持工具调用，见 §7.2）
  "sessionsDir": "<dataDir>/sessions",
  "kb": {
    "sqlitePath": "<dataDir>/kb/ldb.sqlite",
    "autoExportOnVersionChange": true
  },
  "wiki": {
    "enabled": true,
    "sites": {
      "zh": "https://wiki.biligame.com/dsp/",
      "en": "https://dsp-wiki.com/"
    },
    "cacheTtlHours": 168,
    "maxPageBytes": 200000
  },
  "limits": {
    "toolResultMaxChars": 24000,
    "stateCacheTtlMs": 5000,
    "confirmTimeoutMs": 60000
  },
  "logLevel": "info"
}
```

---

## 14. 错误处理与降级

| 场景 | 行为 |
| --- | --- |
| sidecar 未运行 | 窗口显示引导（systemd 命令 / 手动启动命令 / 文档链接），输入禁用，保留历史 |
| 连接中断 | 保留当前回复，状态栏显示重连中；恢复后重放未确认的工具调用结果（幂等） |
| LLM 429/5xx | pi 自动重试；重试中在状态栏显示"重试中 (1/3)" |
| 上下文超限 | pi 自动压缩；状态栏显示"整理上下文中" |
| API Key 缺失/无效 | 明确错误 + 指向配置文档；不静默失败 |
| 工具超时 | 返回结构化超时错误给模型（模型可改小范围重试）；L2 超时不执行 |
| 游戏正在加载/暂停 | `game.*` 工具返回 `ELoadingState`；写操作直接拒绝并说明原因 |
| 游戏版本不匹配 | 启动时比对版本，警告"未针对该版本测试"，并禁用高风险写工具 |
| 协议版本不匹配 | sidecar 拒绝连接并返回可读原因；mod 提示升级 |
| 存档切换 | 自动切换会话；聊天窗插入一条系统分隔线"存档已切换" |

---

## 15. 里程碑

| 里程碑 | 内容 | 完成判据 |
| --- | --- | --- |
| **M0 骨架** | BepInEx 插件加载、`R` 键开关一个空窗口（含建造工具避让）、WS 握手与 echo、`ManualBehaviour` 生命周期接入、**打通 sidecar 打包链路并验证 `spawnExe` 自动拉起（含 Wine 内冒烟测试）**、**验证 Proton `AppDefaults` DLL 覆盖可让 BepInEx 免启动项生效** | 游戏内按键出现窗口；建造模式下按 `R` 只旋转建筑；日志显示 sidecar 已连接且无需预装 Node；确认无需改 Steam 启动项 |
| **M1 能聊** | sidecar + pi 集成、DeepSeek 默认配置、**首次配置流程（设置页，含工具调用自检）**、流式显示、系统提示词、中文字体正确、会话持久化 | 干净环境在设置页填 API Key 后能流式回答；工具调用自检通过；重启游戏后历史还在 |
| **M2 懂现状** | `game_get_overview/player/planet/power/research/production` + 分帧采集框架 + 工具链路（协议、ToolHost、执行器、卡片） | 问"我现在缺什么"能定位到具体物品/电力缺口，数值与游戏面板一致 |
| **M2.6 配置完善** | 多供应商/多模型设置页、模型目录、Key 检测、自定义 OpenAI 兼容端点 | 可在多个供应商/模型间切换；Key 不落聊天记录、不出站 |
| **M3 有知识** | 运行时 LDB 导出 + 本地索引 + `kb_search`/`kb_get`/`kb_recipe_tree` + 提示/教程/里程碑/指引/术语（M3b） | 配方/科技数值与游戏一致；能正确算原料总量（副产物抵扣）；提示/教程可检索 |
| **M4a 能看** | `game.entity.v1` / `game.progress.v1` / `game.view.v1`：建筑详情、建筑列表、进度阶段、截图（JPEG 进模型上下文）；协议 `tool_result.image`；模型 `input` 覆盖 | 截图不含 UI 且模型能描述画面；建筑字段与游戏面板一致；id 回收返回 `stale` |
| **M4b 能算** | `kb_plan`：按目标产量计算机器数/电力/传送带；KB 增加 `building.assemblerRecipeType`，缓存换 `r2` 强制重导 | 机器数与游戏面板一致（base/mk2 档位）；缺建筑参数时降级为 1x 并 warning |
| **M4c 能析** | `game_analyze_area`：范围聚合建筑/配方/装机产能/耗电/物流设施/电网 | 与目测数量一致；装机口径与实际产出分开陈述 |
| **M4d 能编** | `run_script`：QuickJS-WASM 沙箱 + `dsp.call` 复用同一工具注册表；默认只读，含写工具时脚本级确认后凭租约执行；单文件内联 WASM | 脚本可批量取数/受控写入；越权/超时/无宿主全局、写租约和释放都有测试；SEA 打包后仍可运行 |
| **M4g 能查外网** | `wiki_search` / `wiki_page`：中英 DSP Wiki，带来源链接 | 外部内容显式标注不可信；失败返回 `wiki_unavailable` |
| **M4h 能取蓝图** | `blueprint_search` / `blueprint_info` / `blueprint_save`：社区蓝图仓库检索、格式体检、直接保存 | 带作者/仓库/许可署名；路径清洗；同名不覆盖 |
| **M4i 能看卡片** | 协议 A.9 `blueprint_card` + 聊天窗数据驱动卡片 + 历史回放 | 生成/下载/复制后出现卡片；重进存档可回放；删除回合一起消失 |
| **M4j 能出蓝图** | `blueprint_generate`：KB `kb_plan` 的真实机器数 → dsp-calc 布局 → 写入蓝图目录；`from` 起点、rows 拆阶段、超高产量拆多张、save.bind 被动档位 | 60/min 电磁涡轮 29 台机器/7 阶段；300/min→3 张、600/min→5 张 |
| **M4 能动手** | 确认 UI + 令牌门禁 + `focus_camera`/`open_panel`/`set_research` + 撤销 | 拒绝确认时无任何副作用（重点测试） |
| **M5 写工具/实体链接** | 受控写脚本、丢弃/配方/存储/投料/取成品、tether、实体链接卡、沙盒门禁、读侧优化；打包流水线与 i18n 作为发布收尾 | 离线测试通过；实机逐项验收见 §20.3 |
| **M6 扩展**（可选） | 更多 L2 工具（物流/蓝图）、`DshAcpRuntime`、Web 配置页 | — |
| **M7 候选**（D1–D5 已拍板） | §20 剩余的合成器队列/仪表盘等；自动驾驶暂不做 | 见 §20.2、§20.3 |

---

## 16. 风险与待定项

| 编号 | 项 | 影响 | 计划 |
| --- | --- | --- | --- |
| R1 | **`R` 与"旋转建筑"键位重叠** | 中 | **已确认冲突并已设计避让**（§6.2.2：`actionBuild.activeTool` 门控 + 可选 `Ctrl+R`）；M0 实测确认旋转的实际绑键与是否还有第三个动作占用裸 `R` |
| R2 | **Proton 下 sidecar 自动启动** | 中 | **已解决**：随包分发 Windows 自包含 exe（PE），Wine 内 `Process.Start` 直接可拉起（§7.1.2）；`connectOnly` + systemd 作为备选。Windows 产物的 Wine 冒烟测试留 M0 |
| R3 | **游戏更新导致 API 失效** | 高 | 只依赖已核实的公开类型/成员；避免 Harmony 补丁；版本不匹配时降级为只读并提示 |
| R4 | **pi SDK 版本演进** | 中 | 锁定 `1.4.0`；`AgentRuntime` 抽象隔离，必要时可切 dsh ACP |
| R5 | **大存档状态采集体积** | 中 | 强制聚合 + 分页 + 结果上限；sidecar 统一裁剪；FTS/缓存 |
| R6 | **LDB 导出体积与时间** | 低 | **已实现**：只导出必要字段、分帧/分块、按 `gameVersion+locale` 缓存；全量 ~750KB，实测离线生成/索引通过 |
| R7 | **L2 写操作的真实撤销能力** | 高 | **因关闭了写前自动存档，此风险上升**：逐个工具确认撤销语义，无法撤销的工具一律不实现；每个 L2 确认卡必须显式标注"可撤销/不可撤销" |
| R8 | **中文 IME 在 `UISmartInputField` 上的实测表现** | 中 | M1 首要验证项；不行则回退自建 InputField |
| R9 | **发布包体积（~107MB exe）** | 低 | **已决定内置**（用户确认）。仅当未来要上 Thunderstore 时需再看其体积限制；降低体积的备选是 Bun/Deno compile（§7.1.1） |
| R10 | **成本不可控** | 中 | 显示 token/费用统计（pi 已提供 `get_session_stats`）；限制单次上下文；默认模型可选便宜档 |
| R11 | **Wiki 站点协议/反爬变化** | 低 | 限流 + 缓存 + 可关闭；失败不影响离线功能 |
| R12 | **Wine 内 `Process.Start` 的路径与权限** | 中 | 仅 Windows 原生使用 `spawnNode`；Proton 走 `connectOnly` |
| R13 | **Proton 的 `AppDefaults\DSPGAME.exe\DllOverrides` 是否被尊重** | 中 | 决定"能否不改 Steam 启动项就 drop-in"（§2.3.2）。本机前缀已有 27 条 Steam 自写的同类条目，可信度高；M0 实测确认。失败则回退到文档化启动项 |
| R14 | **沙盒门禁与能力协商** | 中 | 已实现 `save_bind.sandbox` + mod/sidecar 双重拒绝 + 回合边界重建；接第一个沙盒工具时补聊天窗工具列表刷新与 `tools_changed`（§20.2 第 8 项） |
| R15 | **长任务（未来）** | 中 | D2=B 暂不做自动驾驶；未来重启需要 start/progress/cancel 与玩家抢占，并先复议 §1.2（§20.1） |
| R16 | **丢弃/拆除的不可逆性** | 中 | D1=B 用丢弃替代摧毁：物品以 trash 落地、可撤销（需手动拾回）但不是一键撤销；拆除按 §9.1 不实现。确认卡要如实标注（§20.1 D1） |

---

## 17. 附录 A：已核实的游戏 API 证据

以下均在 `Assembly-CSharp.dll`（MVID `4bd0041b-46ae-442c-aba2-460c8e3630d8`）或 `UnityEngine.UI.dll` 中直接读取元数据得到，非推测。

**输入**

- `VFInput`（`Assets\Scripts\Controller\VFInput.cs`，静态类）字段包含：`escape`、`onGUI`、**`inputing`**、`inFullscreenGUI`、`simple_buttons : KeyCode[]`、`override_keys : CombineKey[]`，属性含 `escKey`、`uiConfirm`、`_openInventoryPanel`、`_openTechTree`、`_openStarmap`、`_openDysonEditor` 等。
- `WinInput`（`Assets\Scripts\Common\Input\WinInput.cs`）提供 `GetAsyncKeyState(int)` 与 `GetKey(VKey)`；`VKey` 枚举含 `A`…`Z`、`ALPHA0`…`ALPHA9`、`ESCAPE`、`SPACE`、`LEFT` 等（**含 `VKey.R`**）。
- `CombineKey`（struct）字段：`keyCode : int`、`modifier : byte`、`modifierExact`、`action : ECombineKeyAction`、`noneKey`；方法：`GetKey()` / `GetKeyDown()` / `GetKeyUp()` / `get__keyCode : KeyCode`。
- `KeyConfig : ScriptableObject` 字段：`builtinKeys : BuiltinKey[]`；`BuiltinKey` 字段：`name : string`、`id : int`、`key : CombineKey`、`canOverride : bool`、`conflictGroup : int`。
- **`R` 键冲突证据**：`BuiltinKey` 的序列化字段列表中存在动作 **`顺时针旋转建筑0`**、**`顺时针旋转建筑1`**、**`逆时针旋转建筑`**（同列表还含 `切换模型样式`、`扩大地基光标`、`缩小地基光标`、`切换网格吸附方式` 等）；对应 `VFInput` 属性 `_rotate : InputValue` 与 `_counterRotate : InputValue`（`InputValue` 含 `value`/`pressing`/`onDown`/`onUp`）。⇒ 建造模式下 `R` 被游戏占用。
- **避让所依赖的状态链**：`Player.controller : PlayerController` → `PlayerController.actionBuild : PlayerAction_Build` → `PlayerAction_Build.active : bool`、`PlayerAction_Build.activeTool : BuildTool`（`BuildTool` 有 `name`、`active`，子类含 `BuildTool_None` / `BuildTool_Click` / `BuildTool_Path` / `BuildTool_Inserter` / `BuildTool_Reform` / `BuildTool_Dismantle` / `BuildTool_Upgrade` / `BuildTool_BlueprintCopy` / `BuildTool_BlueprintPaste` / `BuildTool_Addon`）。
- `VFInput` 另有 `readyToBuild` / `readyToEnterBlueprintMode` / `readyToPlant`（`bool`）与 `buildTarget`（`Player.buildTarget : Transform`）可作辅助判定。
- `UIInputField : MonoBehaviour`、`UISmartInputField : ManualBehaviour`（复用对象）。
- 程序集含 `rail.RailIMEHelperTextInputCompositionState`、`rail.IRailTextInputHelperImpl` ⇒ 存在 IME 输入链路。

**UI**

- `UIRoot : ManualBehaviour`，字段 `_instance`、`overlayCanvas : Canvas`、`overlayCanvasTop : Canvas`、`uiGame : UIGame`、`uiMainMenu`、`uiMilkyWay`、`uiMechaEditor` 等。
- `UIGame : ManualBehaviour`，字段含 `canvasGroup`、`windowGroup : RectTransform`、`highGroup`、`lowGroup`，以及大量窗口字段：`tutorialWindow : UITutorialWindow`、`advisorTip : UIAdvisorTip`、`goalPanel : UIGoalPanel`、`generalTips : UIGeneralTips`、`keyTips : UIKeyTips`、`inventoryWindow`、`techTree`、`starmap`、`dysonEditor` 等。
- `ManualBehaviour : MonoBehaviour` 生命周期：`_Create/_Destroy/_Init/_Free/_Open/_Close/_Update/_LateUpdate` 与对应 `_On*` 回调、`_OpenArray/_CloseArray/_UpdateArray` 等批量方法，属性 `created/inited/active/destroyed`。
- `UnityEngine.UI.Youthcat` 命名空间（位于 **DSP 定制的 `UnityEngine.UI.dll`**）：
  - `TextLayoutLocale : ScriptableObject` → `languageLayoutSettings : LanguageLayout[]`；`LanguageLayout` 含 `name`、`LCID`、`paragraphIndent`、`generalSpacing`、`compactness`、`extraSpacing`、**`fontPairs : LanguageFontPair[]`**（`{oldFont, newFont}`）。
  - `TextIconMapping : ScriptableObject` → `GetIconInString(string, ref int, ref bool)`、`GetIconByTag(string)`，`IconConfig{name, tag, key, uvMin, uvMax, size, outline}`。
  - `TextEventTrigger : MonoBehaviour`、`TextTriggerBounds`（struct）、`UnityEngine.UI.Youthcat.TextGenerator`（含 `RichTextType` 枚举、`RichTextStyle`）。
- `Configs : MonoBehaviour` 单例，属性 `builtin : BuiltinConfig`；`BuiltinConfig : ScriptableObject` 含 `generalTextLayoutLocale : TextLayoutLocale`、`markerBreifInfoWordFont : Font`、`generalIconMapping : TextIconMapping` 等。

**数据与状态**

- `GameMain : MonoBehaviour`，`instance`、`data : GameData`、`localPlanet`、`localStar`、`galaxy`、`statistics`、`history`、`preferences`、`logic`、`sectorModel`、`creationTime`、`isRunning`、`isPaused`、`gameTick`。
- `GameData` 字段：`gameName`、`gameDesc`、`galaxy : GalaxyData`、`history : GameHistoryData`、`statistics : GameStatData`、`preferences : GamePrefsData`、`factories : PlanetFactory[]`、`factoryCount`、`dysonSpheres : DysonSphere[]`、`galacticTransport`、`warningSystem`、`mainPlayer : Player`、`localPlanet`、`localStar`、`milestoneSystem`、`goalSystem`、`account`。
- `LDB`（静态类）原型集：`items`、`recipes`、`techs`、`models`、`signals`、`veins`、`veges`、`themes`、`players`、`enemies`、`fleets`、`vehicleParts`、`audios`、`MIDIs`、`effectEmitters`、`prompts`、`advisorTips`、`tutorial`、`achievements`、`milestones`、`journalPatterns`、`goals`、`cosmicMessages`、`doodads`、`abnormalities`。
- `Localization`（静态类）：`Translate(string)`、`CanTranslate`、`TranslateParam`、`isZHCN`、`isCJK`、`CurrentLanguageLCID`、`OnLanguageChange`。
- 语言资源：`Locale/<LCID>/{base,keys,prototype,dictionary,parameters,combat,creation}.txt`，UTF-16LE；`Locale/Header.txt` 声明各语言元数据。

**环境**

- `DSPGAME_Data/Managed/` 下存在 `Assembly-CSharp.dll`（8.0MB）、`mscorlib.dll`、`UnityEngine.*.dll`、**`UnityEngine.UI.dll`（被定制）**；`MonoBleedingEdge/` 存在 ⇒ Mono 后端。
- CLI PE 头 `flags = il-only`，`runtime_version = 2.5`，程序集引用 `netstandard 2.1` 与 `UnityEngine.UI`、`InputLegacyModule`、`IMGUIModule`、`UnityWebRequestModule`。
- 游戏目录仅有 Windows 构建（`DSPGAME.exe` + `UnityPlayer.dll`，无 `.so` / `.x86_64`），且 `compatdata/1366540/pfx` 存在 ⇒ **必须经 Proton 运行，无原生 Linux 版**。游戏目录当前**不含**任何 mod loader 痕迹（无 `winhttp.dll` / `doorstop_config.ini` / `BepInEx/`）。
- **Proton DLL 覆盖机制证据**：`compatdata/1366540/pfx/user.reg` 中存在 **27 条** `[Software\\Wine\\AppDefaults\\<exe>\\DllOverrides]` 段，由 Steam/Proton 自己写入（例：`Avengers.exe` → `"atiadlxx"="builtin"`）。**尚无 `DSPGAME.exe` 条目**。⇒ 可按 exe 追加 `"winhttp"="native,builtin"` 来替代 `WINEDLLOVERRIDES` 启动项（§2.3.2）。

**A.3 运行时实测修正（来自真实游戏的 BepInEx 日志）**

以下事实**无法从元数据推断**，是与游戏实机对照后修正的设计假设。全部来自
DSP `0.10.30.3100` + Proton 的 `LogOutput.log`。

| 观测 | 值 | 对设计的影响 |
| --- | --- | --- |
| `GameMain.notNull` | 主菜单 `True` / **游戏中 `False`** | **语义与直觉相反**。初稿写 `notNull && isRunning` 导致游戏中恒为 false，热键被永久拒绝。改用 `GameMain.isRunning` |
| `GameMain.isRunning` | 主菜单 `False` / 载入中 `False` / 游戏中 `True` | 可靠的"是否在游戏中"信号 |
| `GameMain.isLoading` | 载入存档时 `True` | 可靠 |
| `PlayerAction_Build.active` | 漫游 `False` / 建造模式 `True` | **正确的建造模式判定** |
| `PlayerAction_Build.activeTool` | **恒为 `null`** | 不能用它判定；初稿的避让逻辑因此失效 |
| `VFInput.inputing` | 全程 `False`（未开输入框时） | 可安全用于"是否在输入文本"判定 |
| `VFInput.inFullscreenGUI` | 仅全屏界面为 `True` | 可安全用于全屏界面判定 |
| `UIRoot.overlayCanvasTop` 及其父级 `Always on Top` | 游戏中 **`activeInHierarchy = False`** | **不能把窗口挂在这里**——挂进去 SetActive(true) 也不渲染。改为自建根 Canvas（ScreenSpaceOverlay，sortingOrder 30000） |
| `UIRoot.instance` | 主菜单与游戏中均可用 | 可直接作为自建 Canvas 的父级 |
| `UIRoot.overlayCanvas` | 存在 `CanvasScaler` | 复制其缩放设置，保证界面尺寸与游戏一致 |

**harness（本机已装，已读文档核实）**

- `@earendil-works/pi-coding-agent` v1.4.0：`createAgentSession()`、`defineTool()`、`session.subscribe()`、`ModelRuntime`、`SessionManager`、`SettingsManager`、`DefaultResourceLoader`、`runRpcMode`。
- `docs/models.md` 明确支持 `{"api": "openai-completions", "baseUrl": ...}` 接入 Ollama / LM Studio / vLLM 等；`docs/providers.md` 支持 Anthropic 原生与 `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `auth.json`。
- `docs/extensions.md`：`pi.on("tool_call", …)` **可阻塞**，`return { block: true, reason }`；`ctx.ui.confirm/select/input/notify`；`noTools: "builtin"` 保留自定义工具并关闭内置工具。
- `docs/usage.md` 明确：**pi CLI 1.4 内置 MCP**，但 SDK 默认不加载 codemode/MCP/tool_search；本项目不需要，继续保持不加载。子代理、权限弹窗、plan mode 仍需自行扩展或改用 dsh。
- `@deepseek-ai/dsh` v0.2.0-rc.2：`--profile sdk` / `--profile acp` 以子进程 + JSON-RPC/ACP 暴露；有 `dsh-mcp-client`。作为备选运行时。
- 另装 `bwiki-mcp` v0.3.0（MediaWiki MCP server），可在切到 dsh 时直接复用。

**打包可行性（本机实测，非推测）**

- `esbuild --bundle --platform=node --format=cjs --target=node22 --minify` 打包 `@earendil-works/pi-coding-agent` 的 SDK 入口：成功，**8.5MB 单文件**（升级到 1.4.0 后实测；0.82.1 时为 7.1MB）。
- 在**空目录**（无 `node_modules`、无任何外部文件）中运行该 bundle：成功加载 pi SDK（`createAgentSession` / `defineTool` / `ModelRuntime` 均为 function）。
- bundle 内**无原生 `.node` 依赖**：`pi-tui` 的 `win32-console-mode.node` / `darwin-modifiers.node` 均为平台守卫 + `cjsRequire` + `try/catch`（源码注释明确说明"Dynamic require so non-Windows and bundled/browser paths never load the native helper"）；`utils/clipboard-native.js` 用 `createRequire` + `try/catch`，且在 Linux 无 `DISPLAY`/`WAYLAND_DISPLAY` 时直接跳过。唯一外部项 `@mariozechner/clipboard` 为可选（失败返回 `null`）。
- `TextIconMapping` 依赖的 `@silvia-odwyer/photon-node` 是 WASM，已 base64 内嵌进 bundle。
- Node SEA：`node --experimental-sea-config`（入口须 CJS）+ `postject` 注入成功。Linux x64 产物 **132MB**，在空目录运行成功；Windows x64 产物 **107MB**，PE 注入成功（Wine 运行验证被本机沙箱的 `/run/user/1000/wine` 只读限制阻断，留 M0）。
- 踩坑记录：`--format=cjs` 必须配 `--define:import.meta.url=import_meta_url` 与 `--banner:js="const import_meta_url = require('url').pathToFileURL(__filename).href;"`，否则 `fileURLToPath(undefined)` 抛 `ERR_INVALID_ARG_TYPE`。`--format=esm` 可运行但 SEA 入口按 CJS 执行，故不可用于 SEA。
- 许可证：pi 为 **MIT**。

---

## 18. 附录 B：关键代码骨架

**mod 侧：注册 UI 与热键（含 `R` 避让）**

```csharp
/// R 已被"旋转建筑"占用：只有建造工具未激活时才把 R 当聊天热键。
internal static class FocusGuard
{
    public static bool ShouldToggleChat(bool chatOpen)
    {
        if (!GameMain.notNull || !GameMain.instance.isRunning || GameMain.instance.isLoading)
            return false;
        if (VFInput.inputing || VFInput.inFullscreenGUI)      // 正在输入文本 / 全屏界面
            return false;
        if (!UnityEngine.Input.GetKeyDown(HotkeyService.Key)) // 有焦点语义，不用 WinInput
            return false;
        if (chatOpen) return false;                            // 开着时只由 Esc 关闭
        return !BuildToolActive();                             // 关键：建造模式下让位给旋转
    }

    private static bool BuildToolActive()
    {
        var build = GameMain.mainPlayer?.controller?.actionBuild;   // PlayerAction_Build
        return build != null && build.active
            && build.activeTool != null && !(build.activeTool is BuildTool_None);
    }
}
```

**mod 侧：写操作门禁**

```csharp
public async Task<ToolResult> ExecuteAsync(ToolCall call)
{
    if (call.Risk >= 2)
    {
        if (!_tokens.TryConsume(call.CallId, call.Name, call.ArgsHash, out var reason))
            return ToolResult.Denied(reason);      // 无有效确认令牌 → 拒绝
        if (!GameMain.notNull || GameMain.instance.isLoading || !GameMain.instance.isRunning)
            return ToolResult.Error("EGameState", "游戏未在运行状态，已取消操作");
        if (_cfg.BackupBeforeWrite) await GameSave.BackupOnce();   // 默认关闭（用户确认）
    }
    return await _mainThread.InvokeAsync(() => _registry[call.Name](call.Args));
}
```

**sidecar 侧：pi 会话与工具桥接**

```ts
const modelRuntime = await ModelRuntime.create({
  authPath: join(agentDir, "auth.json"),
  modelsPath: join(agentDir, "models.json"),
});

const gameRead = bridgeGameTool({ name: "game.get_power", risk: 0, parameters: PowerArgs });

const { session } = await createAgentSession({
  modelRuntime,
  noTools: "builtin",                 // 关闭 bash/read/write/edit
  customTools: [...gameTools, ...kbTools, ...wikiTools, ...uiTools],
  resourceLoader: loader,             // systemPromptOverride
  sessionManager: SessionManager.create(sessionsDir),
  settingsManager: SettingsManager.inMemory({ compaction: { enabled: true }, retry: { enabled: true } }),
});

// 写操作门禁：先问玩家，再执行
pi.on("tool_call", async (event, ctx) => {
  const risk = registry.riskOf(event.toolName);
  if (risk < 2) return;
  const ok = await ctx.ui.confirm(
    "需要你的许可",
    describeImpact(event.toolName, event.input),
  );
  if (!ok) return { block: true, reason: "玩家拒绝" };
  currentCallTokens.set(event.toolCallId, issueToken(event));
});

session.subscribe(ev => bridge.toMod(ev));   // text_delta / tool_execution_* / agent_settled
```

---

## 19. 已确认的产品决策

以下由用户确认，已落入正文相应章节：

| # | 决策 | 影响位置 |
| --- | --- | --- |
| 1 | **默认模型 = DeepSeek**，默认取 `deepseek-v4-flash`（M1 实测修正：pi 内置目录中不存在 `deepseek-chat`） | §7.2、§13 |
| 2 | **不复用任何既有登录**；sidecar 自持凭证。**mod 目标平台是 Windows** ⇒ 全部路径按 Windows 语义（`%APPDATA%\icarus-ii\`），Windows 产物为首要构建目标 | §2.4、§2.3.3、§7.2、§7.5 |
| 3 | **不需要第二个热键**，只有 `R`（+ `Esc` 关闭） | §6.2、§6.7 |
| 4 | ~~不做截图工具~~ → **M4a 改判：做截图**（`game_capture_view`，给模型看也给玩家看）。依据：pi 的 `openai-completions` 适配层本就把工具结果图片转成 user 图像消息，唯一闸门是 `model.input`；用 `ModelEntry.input` 覆盖即可，不需要 VLM 旁路 | §8.1、§9.1、§15 |
| 5 | **不做写前自动存档** | §3.5、§6.5、§6.7、§9.1、§16 R7 |
| 6 | **接受发布包变大**，`icarus-ii-sidecar.exe` 直接内置 | §2.2、§12、§16 R9 |

> §20 的候选能力不在本表内。D1=B（丢弃替代摧毁）与 D2=B（暂不做自动驾驶）均不修改本表与 §1.2；实现约束见 §20.1。

### 19.1 仍需在实现阶段坐实的技术项（非产品决策）

| 项 | 何时 | 验证方法 |
| --- | --- | --- |
| Proton 是否尊重 `AppDefaults\DSPGAME.exe\DllOverrides` | M0 | 写入后启动游戏，看 BepInEx 是否加载（§2.3.2、§16 R13） |
| Wine 内 `Process.Start` 拉起自包含 exe | M0 | 游戏内观察 sidecar 进程与日志；沙箱外复测 `wine icarus-ii-sidecar.exe` |
| BepInEx 5 在 DSP 上使用哪个代理 DLL 名 | M0 | 查看 BepInEx 发行包内的 `winhttp.dll` / `version.dll` / `dinput8.dll` |
| `R` / `Shift+R` 的确切绑键与是否还有第三个动作占用裸 `R` | M0 | 运行时打印 `KeyConfig.builtinKeys`；游戏内「设置 → 按键」 |
| DeepSeek 当前的 `contextWindow` / `maxTokens` 实际值 | M1 | 设计阶段 `api-docs.deepseek.com` 被 DNS 拦截，未能核实；实现时按官方文档填入 `models.json` 默认值 |
| `deepseek-reasoner` 是否支持 function calling | M1 | 配置面板的最小工具调用自检（§7.2） |

## 20. 候选能力清单（M5+，待实现）

> 本节由 2026-10-24 提出的 13 条需求整理而来。**D1–D5 已于 2026-10-24 拍板**，
> 但除 §20.1 结论外，各工具仍需逐条设计（schema / 确认卡 / 撤销语义 / 能力串 / 验收）后才注册；
> §1.2 非目标、§9.1 硬约束、§16 R7 仍然有效。

### 20.0 需求原文（保留用于对照）

```text
1. AI应该能查询到星球上各矿脉的位置和储量
2. AI应该能编辑`仪表盘`
3. 将链接ID的800000-899999分配给“游戏实体”，包括建筑、星球、黑雾单位等，点击链接可定位那个实体（在机甲和实体之间出现连线），再点击即消失。
4. AI应该可以主动连接某个游戏实体和机甲，但在这么做之后，对话的末尾应当出现这个实体的卡片，以便玩家自行取消连接。
5. AI应当可以在玩家的合成器中增加和撤销任务
6. 对于玩家范围内的一个生产建筑，AI应该可以
   1. 拆除
   2. 改变配方
   3. 取出成品到背包，只要背包有富裕空位
   4. 将背包中的原料塞到产线中
7. 对于玩家范围内的一个存储建筑，AI应该可以
   1. 拆除
   2. 设置各个格子的过滤器
   3. 设置自动化输入限制
   4. 在玩家的背包和建筑之间交流物品
8. 各工具应当区分沙盒模式和非沙盒模式。在非沙盒模式下，AI的工具应当依据游戏性有必要的限制，并隐藏部分工具
9. AI应当有直接摧毁玩家背包中某物品的能力
10. 在沙盒模式下，AI应当有在玩家背包中直接生成某物品的能力
11. AI应当有生成一些**游戏目标**的能力，包括文案和完成此项目标的触发器
12. AI应当有自动驾驶的功能，也就是给定一个目标后，规划自有能量和翘曲器，以各种方式控制机甲抵达目标。移动方式包括
    1.  走过去。需要注意绕开障碍物
    2.  低空飞行。可绕开绝大多数障碍物，但保持飞行时要消耗较多能量
    3.  星际飞行。几乎没有障碍物，匀速直线时不消耗能量，加速、减速、拐弯时要消耗很多能量，需要考虑行星的引力和一定的能量规划，而且在恒星际间“硬飞”极耗时间
    4.  曲速飞行。几乎没有障碍物，非常快，但也会消耗大量能量，需要消耗翘曲器
    5.  网上有现成的自动驾驶mod，可以把他的源码下载过来参照实现
13. 在沙盒模式下，AI应当有控制玩家瞬移的能力
```

### 20.1 已拍板：5 个决策（2026-10-24）

| 编号 | 问题 | 选项 | 结论 |
| --- | --- | --- | --- |
| D1 | 第 9 条"摧毁背包物品"是否实现？ | A 不做；B 改"丢弃到地面"；C 仅沙盒 + 二次确认 + 不可撤销 | **B 丢弃**。实现用 `Player.TakeItemFromPlayer(..., true, ...)` 从背包取出，再 `Player.ThrowTrash(..., life=0)`；物品以可拾回的 trash 落地/漂浮。按 risk 2 实现；确认卡标注"可撤销（需手动拾回）"，不是一键撤销。这不属于 R7 的"无法撤销" |
| D2 | 第 12 条"自动驾驶"与 §1.2"不做全自动代打"冲突 | A 改为"确认后的一次性长任务"；B 保持 §1.2 不做；C 独立扩展模块 | **B 暂不做**。长任务/自动驾驶仍不排期。2026-10-27 新增的「接管」权限只免除**单次写操作确认**，不是长任务自动驾驶：仍由玩家逐轮提问、模型逐步调用工具，且可随时切回只读/受限 |
| D3 | 第 11 条"游戏目标"用原生还是 mod 侧 | A 原生 `TodoModule` + mod 侧有界触发器；B 注入 `GoalProto`；C 只在聊天窗做任务卡 | **B（2026-10-26 改）**。用户要求复用原生左侧 `UIGoalPanel`；注入 `GoalProto`/`GoalData`，触发器复用原生 `GoalDeterminator`；目标定义存 Global TodoModule JSON，读档重注入。**2026-10-27 修正**：AI 目标按 `EGoalLevel.Off` 注入并插到 `goalQueue` 队首（原生面板最多显示 2 个分组），注入后立即补跑 `DetermineOnPatch`/`DetermineOnLoad`；否则 `GD_UnlockTech` 等判断器的 `targetValue` 为 0，面板会把它当“忽略/无进度”而不显示。玩家选“自由探索”时面板仍不显示，`game_todo_add`/`game_todo_list` 返回 `goalLevel`/`goalPanelOff`/`notice`，由模型提醒玩家打开目标设置，不强行改玩家偏好 |
| D4 | 高频 risk 2 的确认方式 | A 批量工具，一次确认、一次撤销；B 会话级租约（需改 §9.3）；C 每次确认 | **A 批量工具**。补充：`game_get_entity` 是高频**读**工具（risk 0），不走确认，但要按读侧优化：字段裁剪、按实体/帧缓存、批量 id、结果上限（2026-10-24 已实现 fields/entityIds，缓存沿用 dispatcher 5s） |
| D5 | 新增工具的能力串粒度 | A 每族一个新能力串 + `save_bind.sandbox`；B hello 里带工具清单；C 继续复用 `game.write.v1`/`game.read.v1` | **A**。新增工具按族拆能力串；不继续复用粗粒度能力串 |

### 20.2 能力与工具（草案）

> 工具名都是**草案**，未注册；risk 是建议值，最终以确认卡设计为准。
> 第 3/8 条不是新工具，而是渲染/门禁能力。

| # | 能力 | 草案工具 | risk | 沙盒门禁 | 撤销语义 | 协议/能力 | 状态 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 矿脉位置/储量 | 扩 `game_get_planet`（`veins` / `veinOffset` / `veinLimit`） | 0 | 任意 | 只读 | 扩 `game.read.v1` 参数（向后兼容） | **已实现（2026-10-24）** |
| 2 | 仪表盘编辑 | `game_dashboard_get` / `game_dashboard_apply`（批量） | 2 | 任意 | 按批次记录 diff，可回滚 | 新 `game.dashboard.v1` | 需先定义语义 |
| 3 | 实体链接 | 无新工具；扩展 `GameRichText` + `ChatWindow` 分发 | 0 | 任意 | 只读 | 扩 `ui.richtext.v2` 的 `dsp://entity/...` | **已实现 planet/building/vein 点击聚焦（2026-10-25）** |
| 4 | 实体连接（tether） | `game_tether_add` / `game_tether_list` / `game_tether_clear` | 1 | 任意 | 瞬态，可清除 | 新 `game.tether.v1` | **已实现多目标（≤20；本星球多建筑多线，跨行星原生导航一次一个，落地后切换，2026-10-24）；link 卡片持久化/统计/备忘录已接** |
| 5 | 合成器队列 | `game_craft_queue_get` / `game_craft_queue_add` / `game_craft_queue_cancel` | 2 | 任意 | 部分可撤销（退料可能失败） | 新 `game.craft.v1` | 可做，需预检 |
| 6 | 生产建筑 | `game_set_recipe` / `game_set_assembler_batch` / `game_take_output` / `game_feed_input` | 2 | 任意 | 改配方先退料；取成品保留配方 | 新 `game.build.v1` | **已实现改配方/取成品/投料（2026-10-25）；拆除按 §9.1 不实现** |
| 7 | 存储建筑 | `game_storage_set_filter` / `game_storage_set_limit` / `game_storage_move` / `game_storage_deposit` / `game_storage_withdraw` | 2 | 任意 | 记录原过滤器/限制，可回滚 | 新 `game.storage.v1` | **已实现 filter/limit/move + 背包存取（2026-10-24）** |
| 8 | 沙盒门禁（D5=A） | 无新工具；`save_bind.sandbox` + 工具集刷新 | — | 全局 | — | `save_bind` 可选字段 + 回合边界重建会话 | **已实现门禁（2026-10-24）**；沙盒工具未实现 |
| 9 | 丢弃物品（D1=B） | `game_drop_item` | 2 | 任意 | 可撤销（需手动拾回） | 新 `game.drop.v1` | **已实现（2026-10-24，待实机）** |
| 10 | 生成物品 | `game_spawn_item` | 2 | 仅沙盒 | 可撤销（移除刚生成） | 新 `game.sandbox.v1` | 待拍板 |
| 11 | 游戏目标/待办（D3=B） | `game_todo_add` / `game_todo_list` / `game_todo_remove` / `game_todo_templates`（触发器复用原生 GoalDeterminator） | 2 | 任意 | 删除只隐藏 AI 目标 | 新 `game.todo.v1`（对用户称"目标"） | **已实现；2026-10-27 修正即时显示（Off 等级 + 队首 + 补跑 OnLoad），待实机** |
| 12 | 自动驾驶（D2=B） | `game_autopilot_start` / `game_autopilot_status` / `game_autopilot_cancel` | 2 + 租约 | 任意 | 可取消；位置不可逆 | 新 `game.task.v1`（长任务） | **暂不做**；重启需复议 |
| 13 | 瞬移 | `game_teleport` | 2 | 仅沙盒 | 不可撤销（可再传送返回） | 新 `game.sandbox.v1` | 待拍板 |

**范围口径**：第 6/7 条的"玩家范围内"统一按 `mecha.buildArea` 定义，并在执行时用实体当前位置重新校验；令牌只绑定参数，不绑定世界状态，确认后玩家走开也必须失败。

**链接段口径**：第 3 条暂用 800000–899999；实现时要同步改 `GameRichText` 的段注释、`Reserve`、`IsEntityLinkId`、`NextRawLinkId` 上限和 `ChatWindow` 分发，并补测试。实体身份不要编码进数字（planetId+entityId 可能超出段宽），用每个 Text 的 URL map；同星球连线用 `LineGizmo` 局部坐标，跨行星不要画伪连线（只做定位/星图指示）。

### 20.3 验收要点

1. 已加载星球：矿脉群聚合与逐矿脉分页一致；未加载/未扫描明确返回 `unavailable`；原油/水返回"流速"而不是"储量"。
2. 加/删图表后仪表盘立即正确；删除共享 stat plan 不留下悬空 chart；面板打开/拖动时无布局错乱；重进存档保持。
3. 点击实体链接：当前星球定位并显示连线；再次点击消失；跨行星不画伪连线；`game_capture_view` 的取景符合预期；ID 段不与网页/科技/配方/成就冲突。
4. `tether_add` 出现卡片且可取消；换档/实体失效自动清理；数量上限生效；历史回放不重复创建。
5. `add` 走 `TryAddTask`；取消只撤 AI 创建的任务；背包满时明确报告退料失败与级联影响。
6. 拆除按 §9.1 不实现；改配方前退料；取成品保留配方；范围外/工厂未加载/实体失效有明确错误码；`feed_input` 走确认 + 租约/可撤销。
7. 过滤器设置读回校验；`bans` 与游戏 UI 一致；箱子/储液罐/物流站分别可用；部分转移返回剩余。
8. 非沙盒下 spawn/teleport 不可见且 mod 拒绝；载入界面开关沙盒后，下一回合的工具列表/提示立即变化；AI 永不写 `sandboxToolsEnabled`；UI 显示当前权限。
9. 丢弃：从背包取出的 item/count/inc 正确；物品以 trash 落地/漂浮、可拾回；背包不足/物品不存在有明确错误码；不复制物品；确认卡标注"可撤销（需手动拾回）"，不是一键撤销。
10. 仅沙盒；校验 itemId/数量/背包空间；刚生成的物品可撤销。
11. （D3=A）Todo 文案正确落盘/可删除；触发器只在有界 DSL 内（物品数/科技/建筑数/电力/时间）；跨存档不串；有 i18n；先验证 `TodoModule` 的重载引用。
12. （D2=B 暂不做）若未来重启：起/停/取消/到站/玩家抢占/死亡/翘曲器不足都正确；不阻塞聊天；时间/能量估算有明确口径。
13. 仅沙盒；走原生快速旅行；目标行星/坐标语义明确；失败有错误码。

> 高频读工具（`game_get_entity`）不属于本表 13 项，但按 D4 要求单独验收：同一实体重复查询命中缓存；支持字段裁剪；支持批量 id；结果不超过统一上限。

### 20.4 已核实的事实（设计依据）

以下均来自本地游戏工程或现有仓库代码，不是推测。游戏路径相对 `dsp-game/Assets/Scripts/`，仓库路径相对仓库根。

| 主题 | 事实 | 依据 |
| --- | --- | --- |
| 沙盒标志 | `GameMain.sandboxToolsEnabled` 是当前载入会话的开关；`GameDesc.isSandboxMode` 是"曾用过沙盒工具"的粘性标记；`GameHistoryData.AfterTick` 只要 `sandboxToolsEnabled` 为真就会把它置 true | `Toplevel/GameMain.cs:52-56,333`；`Data/GameDesc.cs:44-49`；`Data/GameHistoryData.cs:657-659`；`Toplevel/DSPGame.cs:191,209` |
| 载入时开关 | 玩家可在载入界面为任意存档启用沙盒工具；启用后该存档成就失效 | `UI/Window/UILoadGameWindow.cs:213-215,354-356` |
| 矿脉 | `VeinGroup` 有 type/pos/count/amount；`pos` 是归一化方向；逐矿脉位置仅工厂加载时可得；扫描过的远程星球才有群数据 | `Data/VeinData.cs:195-200`；`Data/PlanetData.cs:81,130,756,1685` |
| 原油/水 | 按流速而非储量；`VeinData.oilSpeedMultiplier` | `Data/VeinData.cs:44` |
| 仪表盘 | 数据在 `GameMain.statistics.charts`（存档内）；`DashboardLayout` 最多 10 页；`ChartData`/`StatPlan`；`UIDashboard.RefreshGrid()` | `Toplevel/GameMain.cs:92`；`Data/GameData.cs:238,770`；`Data/Dashboard/DashboardLayout.cs:8`；`Data/Dashboard/ChartData.cs:4-15`；`UI/Dashboard/UIDashboard.cs:217` |
| 合成器 | `mainPlayer.mecha.forge`；`AddTask` 不校验材料，`TryAddTask` 才校验；`CancelTask` 只退 `served`，背包满会失败并级联删除 | `UI/Window/UIReplicatorWindow.cs:282`；`Controller/Player/Mecha/MechaForge.cs:178-273,429,665` |
| 配方 | `AssemblerComponent.SetRecipe` 整体替换 `served/produced`；原生"选配方"路径未先退料 | `Component/Assembler/AssemblerComponent.cs:383`；`UI/Window/UIAssemblerWindow.cs:787` |
| 拆除 | `PlanetFactory.DismantleFinally` 处理退料/成就/场景通知/沙盒即时拆除 | `Data/PlanetFactory.cs:5042,5059` |
| 存储 | `SetFilter` 对已占用且物品不同的格子只告警不生效；`SetBans` 是输入限制；`SetFilter` 不触发 `onStorageChange` | `Component/Storage/StorageComponent.cs:258-267,372-402` |
| 丢弃 | `Player.TakeItemFromPlayer(..., true, ...)` 从背包取；`Player.ThrowTrash(..., life=0)` 生成 trash；trash 落地后可被 `PlayerAction_Pick` 拾回，`life=0` 不过期 | `Controller/Player/Player.cs:297,437`；`Component/Systems/TrashSystem.cs:163`；`Controller/Player/Action/PlayerAction_Pick.cs:41` |
| 导航 | `Player.navigation` 公开；`Navigate/Abort/Pause/Resume` 可用；`GameTick` 被禁用；移动脚本在 `navigating` 时调用 `Determine*`；直线转向、无绕障；翘曲器/能量由 `UseWarper`/`UseWarpEnergy` 处理 | `Controller/Player/PlayerNavigation.cs:273,367,475`；`Controller/Player/Action/PlayerMove_Sail.cs:177,1000`；`Controller/Player/Mecha/Mecha.cs:1352` |
| 目标 | 原生目标由 LDB `GoalProto` 驱动；`DeterminatorName` 反射创建；`DeterminatorParams` 仅 `long[]`；文案在原型。`UIGoalPanel` 每帧按 `goalQueue` 取组，最多 `GoalLogic.kMaxShowEntry=2` 个，并用 `IsLevelPass(proto.Level)` 过滤；`GoalLogic.DetermineGoal` 每 tick 还会按 `CheckLogic`/`target==0` 把不满足的组移出队列。判断器的初始进度只在 `GoalLogic.InitDeterminator` 的 `DetermineOnPatch`/`DetermineOnLoad` 里建立，`OnGoalStageChanged` 自动建判断器时不会补跑 | `Prototypes/Tables/GoalProto.cs:8-16`；`UI/Window/UIGoalPanel.cs:615-670,864-871`；`Scenario/Goal/GoalLogic.cs:310,450-485,520-560`；`Scenario/Goal/Determinator/GD_UnlockTech.cs` |
| Todo | 原生 `TodoModule` 有 title/content/owner（Global/Astro/Entity），随存档保存；`AddTodoModule`/`RemoveTodoModule` 公开 | `Component/Todo Module/TodoModule.cs:13-14,174-179`；`Component/Systems/GalacticDigitalSystem.cs:173,181` |
| Marker/Tether | `MarkerComponent` 可挂 `entityId`、`todo`、name/icon/visibility，上限 256；`SetHeight`/`SetRadius` 公开；`LineGizmo` 在 `GameGizmo.gizmoGroup`，`ClearGizmos` 会统一清空 | `Component/Marker/MarkerComponent.cs:9,32,48,83,94`；`UI/Gizmo/GameGizmo.cs:6-28`；`UI/Gizmo/LineGizmo.cs:8` |
| 能力协商 | 工具按 hello 的 `capabilities` 注册；`game.write.v1` 是粗粒度能力；`save_bind` 只有 key/name/tiers | `Shared/Protocol/Protocol.cs:108-126,277-281`；`sidecar/src/tools/registry.ts:39-60`；`docs/protocol-v1.md:54,888-896` |
| 撤销栈 | 只保留最近一次；换档/断线清空 | `Game/Tools/WriteTools.cs:11-45` |
| 链接段 | 科技 1000–9999、配方 10000–19999、成就 20000–21000、网页 900000–999999；裸 link 从 21001 起；`Reserve` 只登记 ≤ WebMax | `Shared/Text/GameRichText.cs:36-37,80-95,931,1765` |

### 20.5 跨条目设计缺口

- **工具级设计**：每个能力都要有工具名、参数 schema、risk、确认卡文案、撤销语义、失败码、分页/上限、沙盒门禁、能力串、里程碑、验收。
- **能力粒度**：D5=A，新增工具按族拆新能力串，不能继续挂在 `game.write.v1` / `game.read.v1` 下，否则老 mod 会被注册它不存在的工具。
- **高频操作**：取成品/转移物品这类小步写操作若每次确认，体验不可用；D4=A 采用批量工具。另：`game_get_entity` 是高频**读**工具（risk 0），已实现 `fields`/`entityIds`；缓存沿用 dispatcher 5s，接真实高频用法后再评估是否要更细的实体级缓存。
- **撤销**：`WriteActionStack` 只有一层且换档清空；批量改配方/批量转移必须一次 undo 或明确不可撤销。D1=B 的丢弃是"可撤销（需手动拾回）"，不是一键撤销；确认卡不能简化成"一键撤销"。
- **读侧配套**：AI 需要发现"附近实体、当前 tether、当前任务、仪表盘现状"；`game_get_entities` 没有距离过滤，`game_analyze_area` 有（center 默认玩家、`includeEntities`），但 limit 100 且是 XZ 球，建议补 `game_get_nearby`；`game_get_entity` 也归入高频读侧，优化口径见上一条。
- **长任务/并发**：D2=B 暂不做自动驾驶；若未来重启，需要 start/progress/cancel/event，且要能被玩家输入抢占、切档取消，当前没有这套协议。
- **状态可见性**：tether/任务/目标/仪表盘改动都要有 `game_get_*` 或系统提示注入，否则模型无法"取消/避免重复"。
- **第三方代码**：第 12 条提到的现成自动驾驶 mod 不直接复制；本仓库仍只用公开 API、无 Harmony（`AGENTS.md` 2.3），如确需参考源码，先做许可审查并记录在 `THIRD-PARTY.md`。
- **性能/隐私/i18n/测试**：矿脉/范围扫描走分帧预算；连线/标记可能进截图（需确认图层）；目标文案和卡片文案进 i18n；范围外、实体失效、背包满、沙盒关闭、翘曲器不足都要有离线测试。

### 20.6 排序与 spike

1. **§20.2 第 8 项已完成**（`save_bind.sandbox` + mod/sidecar 双重拒绝 + 回合边界重建）；接第一个沙盒工具时再补聊天窗工具列表刷新。
2. **spike（目标）**：验证 `TodoModule` 的增删/落盘/重载；若走 `GoalProto`，另验注入与卸载后残留（不推荐）。
3. **实现顺序**：8（沙盒门禁，已完成）→ 1/2（读侧，已完成）→ 9（丢弃，已完成）→ 6（改配方/取成品/拆除/投料，已完成）→ 7（存储 filter/limit/move + 背包存取，已完成）→ 4（tether：行星导航 + 建筑自绘线，已完成）→ 5（合成器队列）→ 3（富文本实体链接）→ 10/13（沙盒，待拍板）→ 11（目标）。
4. **自动驾驶 D2=B**：不排期、不做 spike；未来重启需先复议 §1.2，并补长任务协议与导航 spike。
5. 所有新工具默认不注册；完成决策 + 验收后再从 §20.2 移入 §8.1/§8.3 并排期。

### 20.7 同步约定

- 本节不修改协议 v1；新增能力串/字段都必须是可选，旧端忽略。
- 实现任一工具前，先按 `AGENTS.md` 2.1 改 `docs/protocol-v1.md`，再同步 `Protocol.cs` / `protocol.ts` / 测试。
- 第 3 条是渲染/交互能力，不新增工具；第 8 条是门禁，不新增工具。
- 本节与 §1.2、§8.3、§15、§16、§19 的指针已加；未实现项保持"不注册"。
