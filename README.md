# dsp-agent

[《戴森球计划》(Dyson Sphere Program)](https://store.steampowered.com/app/1366540/) 的游戏内 AI 助手 mod。

游戏里按 **`R`** 打开聊天窗，可以问机制、要攻略、让 AI 读取你当前的存档状态给建议，
也可以在**你确认之后**让 AI 执行游戏内操作。

> 状态：**M0（骨架）**。目前打通的是"插件加载 → 窗口开关 → 与 sidecar 握手 → 消息回显"这条链路，
> 还没有接入大模型。设计与路线图见 [design.md](design.md)。

---

## 它长什么样

```
┌─ DSP Agent ──────────────────────── 就绪 ─┐
│ 系统  已连接 sidecar（会话 3f9a…，模型 echo）│
│ 你    你好                                │
│ 助手  echo: 你好                          │
│                                           │
├───────────────────────────────────────────┤
│ 输入问题…（Enter 发送，Esc 关闭）          │
└───────────────────────────────────────────┘
```

## 架构一览

```
游戏进程（Windows / Proton）
├── BepInEx 5
│   └── DSPAgent.dll          UI、热键、游戏状态、操作执行
└── sidecar: dsp-agent-sidecar.exe
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
| mod 加载器 | **BepInEx 5.4.23.5（win_x64）**，需自行安装 |

> BepInEx 版本请用 **5.4.23.5**：它的 changelog 明确包含
> `Fix for unity 2022.3.62f3`，而这正是本游戏使用的 Unity 版本。

### 步骤

```bash
# 1) 安装 BepInEx 5.4.23.5 到游戏目录（解压即用）
#    下载：https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5
#    选 BepInEx_win_x64_5.4.23.5.zip，解压到 Dyson Sphere Program/ 下

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

### 关于 R 键冲突

`R` 在 DSP 里是**建造模式下旋转建筑**的键。本 mod 会自动避让：

| 情境 | `R` 的行为 |
| --- | --- |
| 建造 / 拆除 / 升级 / 改造 / 蓝图模式 | 交给游戏 → 旋转建筑 |
| 漫游、飞行、航行、战斗、挖矿 | 打开聊天窗口 |
| 全屏界面（星图、戴森球编辑器） | 不响应 |
| 窗口已打开 | 不响应（只有 `Esc` 能关） |

如果你仍觉得别扭，可以在 `BepInEx/config/dsp-agent.cfg` 里改成别的键
（例如 `F8`，该键在 DSP 中未被占用）。

---

## 配置

`BepInEx/config/dsp-agent.cfg`

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
| `LaunchMode=spawnExe` | 由插件自动拉起随包的 `dsp-agent-sidecar.exe`（默认，推荐） |
| `LaunchMode=connectOnly` | 插件只连接；你自己先启动 sidecar（便于调试 / 多存档共用） |
| `Hotkey` | 任意 Unity `KeyCode` 名 |

---

## 排障

| 现象 | 处理 |
| --- | --- |
| 按 `R` 没反应 | 看 `BepInEx/LogOutput.log` 里有没有 `[热键]`/`聊天窗口已创建` |
| 窗口显示"未连接" | 日志里搜 `[sidecar]`；确认 `dsp-agent-sidecar.exe` 在 `BepInEx/plugins/DSPAgent/sidecar/` |
| 中文显示成方块 | 日志里看字体来源；`borrowed-from-game-ui` 正常，`builtin-arial(no-cjk)` 说明没借到游戏字体 |
| BepInEx 完全没加载 | Proton 下多半是 `winhttp` 覆盖没生效，见上文 |
| 建造时按 R 弹窗 | 检查 `HotkeyRequireNoBuildTool` 是否为 `true` |

---

## 从源码构建

```bash
scripts/fetch-deps.sh          # 拉取 BepInEx 参考程序集到 .deps/
scripts/build-mod.sh           # 编译 DSPAgent.dll

cd sidecar && npm install      # 首次
scripts/build-sidecar.sh       # 生成自包含的 dsp-agent-sidecar.exe

scripts/install.sh --with-bepinex
```

### 运行测试

```bash
# JSON / 协议单测（无需游戏、无需 sidecar）
dotnet run --project tests/ProtocolTests

# 端到端：用与 mod 相同的协议实现去连真实 sidecar
node sidecar/dist/index.js serve --port 8799 --token testtoken &
dotnet run --project tests/ProtocolTests -- e2e ws://127.0.0.1:8799 testtoken
```

---

## 仓库结构

```
design.md              设计文档（含已核实的游戏 API 证据）
docs/protocol-v1.md    mod ↔ sidecar 通信协议（唯一契约）
mod/                   C# BepInEx 插件
  src/Shared/          协议/JSON/传输层（刻意 Unity-free，可独立测试）
  src/DSPAgent/        Unity 侧：UI、热键、游戏访问
sidecar/               Node/TypeScript 本地服务
tests/ProtocolTests/   本机可跑的验证程序
tools/MetaDump/        开发辅助：转储游戏程序集元数据
scripts/               构建 / 安装
```

## 安全

- **不向模型暴露 shell 与文件系统**：pi 的内置工具（`bash`/`read`/`write`/`edit`）全部关闭。
- **写操作双重门禁**：sidecar 侧确认钩子 + mod 侧一次性令牌（M4 实现）。
- **凭证**：API Key 只存在 sidecar 自己的数据目录，插件不接触。
- sidecar 只监听 `127.0.0.1`，握手带 token。

## 许可

见 `LICENSE`。第三方组件许可见 `THIRD-PARTY.md`（BepInEx 为 LGPL-2.1，pi 为 MIT，Node.js 为 MIT）。
