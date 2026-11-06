# 第三方组件

本项目分发时包含以下第三方组件。各组件版权归其各自所有者，遵循其原始许可证。

| 组件 | 用途 | 许可证 | 分发方式 |
| --- | --- | --- | --- |
| [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.17（构建期参考程序集） | mod 加载器（doorstop 代理 + Harmony） | LGPL-2.1 | **不随包分发**，由玩家自行安装；`scripts/fetch-deps.sh` 仅用于开发期拉取参考程序集 |
| [pi](https://github.com/earendil-works/pi) (`@earendil-works/pi-coding-agent` 1.4.0) | Agent 运行时（Provider 抽象、工具调用循环、会话管理） | MIT | 经 esbuild 打包进 sidecar 可执行文件（M1 起） |
| [Node.js](https://nodejs.org/) | sidecar 运行时 | MIT | 以 SEA（Single Executable Application）形式**内嵌**进 `icarus-ii-sidecar.exe` |
| [ws](https://github.com/websockets/ws) | sidecar 的 WebSocket 服务端 | MIT | 打包进 sidecar |
| [typebox](https://github.com/sinclairzx81/typebox) | 工具参数 JSON Schema（M2 起） | MIT | 打包进 sidecar |
| [QuickJS](https://github.com/quickjs-ng/quickjs) / [quickjs-emscripten](https://github.com/justjake/quickjs-emscripten) | `run_script` 的脚本沙箱（M4d） | MIT | 打包进 sidecar；WASM 以 base64 内联（`@jitl/quickjs-singlefile-cjs-release-sync`），不额外落文件 |
| [dsp-calc](https://github.com/antian369/dsp-calc) | 蓝图布局算法与建筑/配方数据（`blueprint_generate`） | 木兰宽松许可证，第 2 版（Mulan PSL v2） | 仅算法与数据 vendored 到 `sidecar/vendor/dsp-calc/`，打包进 sidecar；不包含其 UI |
| [dsp-handcraft](https://github.com/zoujack018/dsp-handcraft) | 蓝图生成器的参考实现（排线 / 分馏阵列 / 集装机等做法） | GPL-3.0 | **不随包分发、不复制代码**；开发期克隆到 `dsp-handcraft/`（已被 `.gitignore` 忽略），仅作只读参考 |
| [Unity Skills](https://github.com/Unity-Technologies/skills) — `ui-ugui` | Agent Skill：Unity uGUI / Canvas / RectTransform / Layout Group 开发规范 | Unity Companion License | 内容收录于 `.dsh/skills/ui-ugui/`，仅用于开发期辅助，不进入发布产物 |
| [Lucide](https://lucide.dev/) — `message-circle-plus`、`refresh-cw`、`undo-2` | 左栏“新会话”、回复 footer“重新生成 / 撤回”按钮图标 | ISC | 转成白色 PNG 后打包进 UI AssetBundle（`new-conversation.png`、`regenerate.png`、`withdraw.png`） |

## 关于 BepInEx

BepInEx 采用 **LGPL-2.1**。本项目**不再分发** BepInEx 本体：
发布包只包含 `icarus-ii` 自己的程序集与 sidecar 可执行文件，
玩家需自行从其官方发布页获取 BepInEx。

仓库内的 `scripts/fetch-deps.sh` 会把 BepInEx 下载到 `.deps/`（已被 `.gitignore` 忽略），
**该目录仅用于编译期引用程序集，不进入任何发布产物**。

## 关于 dsp-calc

`sidecar/vendor/dsp-calc/` 收录上游的蓝图布局算法与数据（`src/`、`data/`），
遵循 **木兰宽松许可证，第 2 版**（许可证原文见 `sidecar/vendor/dsp-calc/LICENSE`）。
本项目只移植/调用算法，不复制其 UI；生成的蓝图版权归玩家自己的存档内容。

## 关于 dsp-handcraft

`dsp-handcraft` 是上游独立的蓝图生成器，本项目只把它当作**只读参考**：
开发期克隆到 `dsp-handcraft/`（已被 `.gitignore` 忽略），用来对照排线、分馏阵列、自动集装机等做法。

- 许可证为 **GPL-3.0**，与本项目（MIT）不兼容：**不得把它的代码复制进本仓库或发布产物**，
  否则整个项目会被 GPL-3.0 传染。
- 它不参与构建，也不进入 `dist/pkg`；`scripts/check-deps.mjs` 只比对上游 `master` 并提示有新提交，
  不阻塞打包，也不自动拉取。
- 其物品数据与图标另有来源，见上游 `data/THIRD_PARTY.md`。

## 关于内嵌的 Node.js

`icarus-ii-sidecar.exe` 是通过 Node 官方的 SEA 机制，把 sidecar 代码注入官方 Node 二进制得到的。
因此该文件内含 Node.js 运行时，遵循 Node.js 的 MIT 许可证。

## 关于 `ui-ugui` Agent Skill

`.dsh/skills/ui-ugui/` 收录的是 Unity Technologies 官方发布的
[skills 仓库](https://github.com/Unity-Technologies/skills) 中的 `ui-ugui` skill，
遵循 **Unity Companion License**（<https://unity3d.com/legal/licenses/unity_companion_license>）。
许可证原文见 `.dsh/skills/ui-ugui/LICENSE.md`。

- 上游路径：`skills/ui-ugui/`（版本快照：`main` 分支，2026-10 拉取）
- 用途：让 agent 在处理 UGUI（Canvas / RectTransform / Layout Group / prefab UI）时
  遵循官方规范；它是**开发期提示词资产**，不会编译进游戏，也不进入发布产物
- 更新方式：

  ```bash
  base=https://cdn.jsdelivr.net/gh/Unity-Technologies/skills@main/skills/ui-ugui
  curl -sSL -o .dsh/skills/ui-ugui/SKILL.md "$base/SKILL.md"
  curl -sSL -o .dsh/skills/ui-ugui/references/scrollview-setup.md "$base/references/scrollview-setup.md"
  ```

> 注：GitHub 原始域名在部分网络环境下不可达，故使用 jsDelivr 镜像。

## 游戏本体

本项目不包含《戴森球计划》的任何游戏资源或程序集。
构建时引用的 `Assembly-CSharp.dll`、`UnityEngine.*.dll` 均通过 `<Private>false</Private>`
标记为**不复制到输出目录**，仅用于编译期类型解析。

## 开发期脚本依赖

`tools/build-kb.py`（M3 离线知识库生成器）使用 [UnityPy](https://github.com/K0lb3/UnityPy)
读取本地 Unity 资源；它只在开发/离线测试时运行，**不会进入发布产物**，
也不会把游戏文本/程序集打包进 mod。
