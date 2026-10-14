# 第三方组件

本项目分发时包含以下第三方组件。各组件版权归其各自所有者，遵循其原始许可证。

| 组件 | 用途 | 许可证 | 分发方式 |
| --- | --- | --- | --- |
| [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.5 | mod 加载器（doorstop 代理 + Harmony） | LGPL-2.1 | **不随包分发**，由玩家自行安装；`scripts/fetch-deps.sh` 仅用于开发期拉取参考程序集 |
| [pi](https://github.com/earendil-works/pi-mono) (`@earendil-works/pi-coding-agent`) | Agent 运行时（Provider 抽象、工具调用循环、会话管理） | MIT | 经 esbuild 打包进 sidecar 可执行文件（M1 起） |
| [Node.js](https://nodejs.org/) | sidecar 运行时 | MIT | 以 SEA（Single Executable Application）形式**内嵌**进 `icarus-ii-sidecar.exe` |
| [ws](https://github.com/websockets/ws) | sidecar 的 WebSocket 服务端 | MIT | 打包进 sidecar |

## 关于 BepInEx

BepInEx 采用 **LGPL-2.1**。本项目**不再分发** BepInEx 本体：
发布包只包含 `icarus-ii` 自己的程序集与 sidecar 可执行文件，
玩家需自行从其官方发布页获取 BepInEx。

仓库内的 `scripts/fetch-deps.sh` 会把 BepInEx 下载到 `.deps/`（已被 `.gitignore` 忽略），
**该目录仅用于编译期引用程序集，不进入任何发布产物**。

## 关于内嵌的 Node.js

`icarus-ii-sidecar.exe` 是通过 Node 官方的 SEA 机制，把 sidecar 代码注入官方 Node 二进制得到的。
因此该文件内含 Node.js 运行时，遵循 Node.js 的 MIT 许可证。

## 游戏本体

本项目不包含《戴森球计划》的任何游戏资源或程序集。
构建时引用的 `Assembly-CSharp.dll`、`UnityEngine.*.dll` 均通过 `<Private>false</Private>`
标记为**不复制到输出目录**，仅用于编译期类型解析。
