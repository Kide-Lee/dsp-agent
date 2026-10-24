# AGENTS.md

本文件面向在本仓库工作的编码 agent。开始改代码前，先读：

1. [README.md](README.md) —— 玩家/开发向的安装、使用和构建入口。
2. [design.md](design.md) —— 已确认的架构与游戏 API 证据。
3. [docs/protocol-v1.md](docs/protocol-v1.md) —— mod ↔ sidecar 的唯一通信契约。
4. [docs/m2-verification.md](docs/m2-verification.md)、[docs/m3-verification.md](docs/m3-verification.md) —— 实机验收项。

---

## 1. 项目一句话

《戴森球计划》的 BepInEx 5 游戏内 AI 助手：

- **mod（C# / Unity）**：窗口、热键、游戏状态采集、工具执行。
- **sidecar（Node / TypeScript）**：内嵌 pi Agent SDK，负责 LLM 调用、工具编排、知识库。
- 两者通过 `127.0.0.1` 的 WebSocket 通信，协议版本为 **v1**。

目标运行环境是 **Linux + Proton**，同时产出 Windows 可执行 sidecar；游戏版本参考 `0.10.30.3100` / Unity `2022.3.62f3c1`。

---

## 2. 硬规则

### 2.1 协议以文档为准

- 任何协议变更，先改 [docs/protocol-v1.md](docs/protocol-v1.md)，再同时改：
  - `mod/src/Shared/Protocol/Protocol.cs`
  - `sidecar/src/protocol.ts`
  - 相关测试。
- 不破坏 v1 的既有字段语义。新增能力优先用可选字段/新消息，保持旧端可忽略。
- `mod/src/Shared` 必须保持 **Unity-free**；不要在 Shared 里引用 `UnityEngine`。

### 2.2 UI 必须是 prefab-first

**静态 UI 和静态布局只允许放在 prefab 里，运行期代码只做查找、绑定和瞬态状态。**

禁止在运行期“永久性调整 UI”来打补丁，包括但不限于：

- 为修正布局而设置 `RectTransform.sizeDelta` / `anchoredPosition` / `offsetMin` / `offsetMax` / anchors / pivot；
- 每帧或每次打开窗口修正 input/content/scrollbar/viewport 尺寸；
- 在 `Ensure*` 里补建静态节点（scroll/viewport/content/template/tooltip/背景块等）；
- 运行期给已有静态节点添加 Image/Text/Outline/LayoutGroup 来改变最终布局；
- 用“先运行，再在代码里挪一下”的方式掩盖 prefab 尺寸错误。

正确做法：

1. 在 `dsp-game/Assets/IcarusII/ChatWindow.prefab` 里改；
2. 如果是构建器负责的固定节点，改 `mod/unity/Editor/` 下对应的幂等工具；
3. 重打 UI AssetBundle；
4. 把 prefab 快照回存到 `mod/unity/Prefab/`。

允许的运行期 UI 逻辑：

- 数据驱动的消息/工具卡/思考块创建；
- 文本、颜色、图标 tag、hover 状态、tooltip 的显示/隐藏与跟随；
- 宿主 Canvas 的分辨率缩放/渲染层适配；
- 用户配置的窗口位置/尺寸。

布局以 prefab 为唯一来源：构建器只允许做缺失节点的**加法**，不得覆盖用户已经调好的 RectTransform、尺寸或样式；运行期只查找/绑定节点。

对应工具：

- `IcarusIIChatIndexBuilder`：只在缺结构时补齐；已有节点不重排、不覆盖；
- `IcarusIIPrefabLayoutFix`：只补 delete bg，不改已有布局；
- `IcarusIIWindowPrefabBuilder.BuildForced()`：**会整窗重建**，只有用户明确要求或 prefab 丢失时才用；
- `scripts/build-ui-bundle.sh --add-parts`：可加部件，默认只打包。

### 2.3 不要引入 Harmony 补丁

当前插件刻意不注册任何 Harmony 补丁，全部走游戏公开类型/成员。新功能优先使用公开 API；确实需要 patch 时，先说明理由和风险，不要静默加入。

### 2.4 工具安全边界

- 不向模型暴露 shell、文件系统、任意 Unity API。
  仅以下例外，且都只操作**游戏蓝图目录**：
  - `blueprint_save`：只写**仓库拉取到**的蓝图；可指定蓝图根内的子目录，默认 `IcarusII/`；
  - `game_copy_blueprint`：只把**当前存档里已有建筑**交给游戏自己的复制逻辑生成蓝图；
  - `blueprint_fs`：蓝图目录的受限虚拟文件系统；`write` 接受模型提供的**合法蓝图文本**，
    其余路径/删除规则见下方专节。
  三者都清洗路径、限制在蓝图根内，默认不覆盖同名。
- sidecar 只注册项目自定义工具；pi 内置的 `bash` / `read` / `write` / `edit` 必须保持关闭。
- 写操作必须经过 sidecar 确认钩子 + mod 侧一次性令牌，不得绕过。
  蓝图文件写入/删除不改变游戏状态，走 `blueprint_fs` 自己的 overwrite/confirm 规则，
  不需要 mod 令牌；`rm`/`rmdir` 仍必须先向玩家确认。
- 工具调用按连接路由；不要把工具请求发到别的会话/存档。
- API Key 只存在 sidecar 配置中；不要写进日志、聊天记录、测试输出或提交。

#### 脚本沙箱（M4d，`run_script`）

- 脚本只在 **QuickJS-WASM** 里执行；不要改成 `node:vm`、`new Function`、子进程或任何能拿回宿主
  全局对象的方式——那些都不是安全边界。
- 脚本的能力面**只能**来自注入的 `dsp.call`：它走同一个 `GameToolHost`，
  白名单来自 `host.tools()`，因此天然继承 risk / 路由 / 超时。
- 脚本 v1 **只读**：不得在脚本层暴露写工具、文件、网络或任意 Unity API。
- 必须保留三重预算：调用次数（≤50）、CPU 时间（≤10s，中断处理器）、返回文本（≤6K 字符）。
- 沙箱不得注入 `process` / `require` / `fetch` / `WebSocket` / 宿主函数以外的对象；
  新增任何注入都要在测试里断言 `typeof ... === 'undefined'`。
- `dsp.call` 的异步执行靠"挂起 → 宿主执行 → 重放"，因此脚本必须可重放：
  不要用 `Date.now` / `Math.random`，也不要在重放间依赖外部可变状态。

#### 蓝图目录虚拟文件系统（`blueprint_fs`）

- 只允许操作游戏蓝图根目录内的路径；虚拟路径以 `/` 为根，默认保存到 `/IcarusII`。
- 拒绝 `..`、盘符、绝对系统路径、NUL 和符号链接；真实路径必须落在蓝图根内。
- `write` 只接受能通过 `inspectBlueprintText` 的 `.txt` 蓝图；不能借它写任意文件。
- `rm`/`rmdir` 是**永久删除**，必须 `confirm=true`；提示词要求先向玩家确认。
- 不新增 shell、`process`、任意 `fs` 工具；蓝图文件内容按不可信外部数据处理。

### 2.5 不提交的内容

`.gitignore` 已排除：

- `dsp-game/`（游戏工程，本地只读参考）；
- `dist/`、`.deps/`、`.probe/`、`.scratch/`；
- `sidecar/node_modules/`、`sidecar/dist/`；
- `.dev-data/`、`runtime.json`；
- `bin/`、`obj/`。

可以提交的是 `mod/unity/Prefab/ChatWindow.prefab` 快照及其 `.meta`；`dsp-game/` 里的实际 prefab 不入库。

---

## 3. 仓库地图

```text
mod/
  src/Shared/                 协议、JSON、WebSocket 传输（Unity-free）
  src/IcarusII/               Unity 侧插件
    UI/                       聊天窗、索引、工具卡、样式、工厂
    Game/                     游戏状态/工具执行/主线程调度
    Transport/                sidecar 客户端与重连
    Config/                   BepInEx 配置
    I18n/                     界面字符串
  unity/Editor/               prefab 构建/修复/打包工具
  unity/Prefab/               聊天窗 prefab 快照
sidecar/
  src/                        Node/TS：server、agent、session、tools、kb
  test/                       离线回归
tests/ProtocolTests/          C# 协议/端到端测试
tools/                        KB 导出、MetaDump 等开发辅助
docs/                         协议、KB、验收、发布文档
scripts/                      构建/安装/打包脚本
```

---

## 4. 常用命令

### 4.1 拉依赖并编译 mod

```bash
scripts/fetch-deps.sh
scripts/build-mod.sh
```

产物：`mod/src/IcarusII/bin/Release/IcarusII.dll`。

游戏目录/BepInEx 路径可用 `-p:DspGameDir=...`、`-p:BepInExDir=...` 或环境变量覆盖。

### 4.2 sidecar

```bash
cd sidecar
npm install        # 首次
npm test           # typecheck + build + 全部离线回归
```

单独构建自包含发布版：

```bash
scripts/build-sidecar.sh windows-x64
```

### 4.3 协议测试

```bash
dotnet run --project tests/ProtocolTests
```

端到端（需要先启动 sidecar）：

```bash
node sidecar/dist/index.js serve --port 8799 --token testtoken --agent echo &
dotnet run --project tests/ProtocolTests -- e2e ws://127.0.0.1:8799 testtoken
```

### 4.4 重打 UI bundle

```bash
scripts/build-ui-bundle.sh
```

要求：

- Unity 工程已关闭（脚本会拒绝 `Temp/UnityLockfile` 存在的情况）；
- 安装了 Unity `2022.3.62f3` 的 Windows Build Support；
- 在受限 sandbox 里需要 GPU / full access，否则 Unity 可能报 `Failed to initialize unity graphics`；
- 构建后脚本会把 `ChatWindow.prefab` 与 `.meta` 回存到 `mod/unity/Prefab/`。

> prefab 有改动但只更新 DLL，游戏里不会看到新 UI。必须重打 bundle。

### 4.5 打包与 Steam 更新

```bash
# 只改 mod 代码、sidecar 二进制未变：省时间
SKIP_SIDECAR=1 scripts/package.sh

# sidecar 有改动或要完整发布包
scripts/build-sidecar.sh windows-x64
scripts/package.sh

# 更新 Steam/Proton 安装，先 dry-run
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh --dry-run
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh
```

更新前退出游戏。Steam 脚本只改四个文件，保留 `config.json`、`runtime.json`、`agent/`、`sessions/`；不要手工删除这些运行期数据。

### 4.6 Thunderstore

见 [docs/publishing.md](docs/publishing.md)。打包用脚本，不要手工改 `dist/`。

---

## 5. 编码约定

### 5.1 C# / mod

- `netstandard2.1`，C# 9；不要启用 nullable/implicit usings。
- 不把 Unity 的 DLL 复制到输出目录（csproj 已设 `Private=false`）。
- Unity API 只能主线程调用；游戏状态采集/执行走现有主线程调度。
- 热路径避免每帧分配和 LINQ；UI 文本更新要节流。
- 新界面文本优先走 `I18n/Strings.cs`，不要散落硬编码中文。
- 注释解释“为什么”，尤其是游戏 API 的坑；不要写无信息注释。
- 修改 Shared 协议后，必须同时跑 ProtocolTests。

### 5.2 Node / sidecar

- Node ≥22.19（pi 1.4 要求），ESM/TypeScript；pi SDK 版本固定，不要随意升级。
- 协议类型、工具 schema、配置迁移必须有离线测试。
- sidecar 只监听 `127.0.0.1`；握手 token 从运行期文件读取，不写日志。
- 会话/历史按存档隔离；不要跨存档复用上下文。
- KB 缓存按“游戏版本 + 语言”失效，不凭模型记忆补数值。

### 5.3 Git

- 提交信息用 `feat:` / `fix:` / `refactor:` / `style:` / `docs:` / `release:` 前缀，主题可中文。
- 一个提交只做一类事；代码、prefab 快照、构建器改动放同一逻辑提交。
- 提交前跑对应测试；UI 改动附上是否重打了 bundle。
- 不提交生成物、密钥、游戏资产。

---

## 6. UI / prefab 工作流

### 6.1 改静态 UI

1. 关闭 Unity（或确认 `Temp/UnityLockfile` 不存在）。
2. 在 Unity 打开 `dsp-game/Assets/IcarusII/ChatWindow.prefab`，或恢复 `mod/unity/Prefab/` 快照后编辑。
3. 优先只改目标节点；不要把整窗重新生成。
4. 需要固定结构时，改 `mod/unity/Editor/IcarusIIPrefabLayoutFix.cs` / `IcarusIIChatIndexBuilder.cs`，保持幂等。
5. 运行 `scripts/build-ui-bundle.sh`。
6. 确认 `mod/unity/Prefab/ChatWindow.prefab` 已更新。
7. 打包并更新 Steam，进游戏验收。

### 6.2 近期踩过的坑

- **PrefabStage.AutoSave 与批处理同时写 prefab** 会让 AssetDatabase 丢 import，出现 `Prefab not found`。不要在 Unity 打开 prefab 时跑批处理；不要手动删正在运行编辑器的 `UnityLockfile`。
- Unity batch 在 sandbox 下没有 `/dev/dri` 会 graphics 初始化失败，需要 GPU/full access。
- 只改 DLL 不会更新 prefab；只重打 bundle 不会更新 DLL。按改动类型分别更新。
- 图标/文字材质是游戏定制资源；需要柔化时优先复用原生 `widget-text-default-icon-set` / `GameTextStyle`，不要随意改全局材质。
- `RectMask2D` + 省略号处理文本溢出，避免压到按钮/滚动条。

### 6.3 UI 验收

- 1080p 和至少一个非 1080p 分辨率下窗口不越界、不重叠。
- 滚动条不与正文重叠，宽度与其它滚动区一致。
- 索引行 hover/删除/提示交互正常。
- 长文本 ellipsis + 硬裁剪，不遮按钮/滚动条。
- 思考块/工具块折叠、流式追加、历史回放正常。

---

## 7. 安全与隐私

- 不向模型暴露 shell/文件系统；不要新增“方便调试”的任意执行工具。
  `blueprint_fs` 是蓝图目录的受限例外，不是通用文件系统。
- API Key、token、玩家存档内容只留在本地运行期目录；日志中不得打印 key。
- 写操作必须确认；未确认不得执行建造/拆除/改配方等。
- sidecar 仅监听 loopback；不要改成 `0.0.0.0`。
- Steam/Proton 的 `winhttp` 覆盖按现有安装脚本处理；不要在游戏/Wine 运行时改注册表。

---

## 8. 完成定义（DoD）

提交前至少确认：

- [ ] `scripts/build-mod.sh` 0 error。
- [ ] `cd sidecar && npm test` 通过（改了 sidecar/协议/KB 时必跑）。
- [ ] `dotnet run --project tests/ProtocolTests` 通过（改了协议/传输时必跑）。
- [ ] 改了 prefab/UI：`scripts/build-ui-bundle.sh` 成功，快照已更新。
- [ ] 改了 UI 静态布局：没有新增运行期永久布局补丁；值在 prefab/构建器里。
- [ ] 改了发布内容：`scripts/package.sh` 成功，Steam 更新脚本通过 SHA-256 校验。
- [ ] 没有提交 `dsp-game/`、`dist/`、`.deps/`、密钥或运行期数据。
- [ ] 协议/验收文档已同步（涉及协议或玩家可见行为时）。
