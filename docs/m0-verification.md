# M0 验证清单

> 本文件是 M0 阶段的**交接单**：哪些已经自动验证过、哪些必须由你在游戏里确认。
> 每一项都写了「期望」与「失败时怎么办」。

---

## 1. 已经在本机自动验证的部分

| # | 项目 | 方法 | 结果 |
| --- | --- | --- | --- |
| A1 | 零依赖 JSON 解析/序列化 | `tests/ProtocolTests` 89 项断言 | ✅ 全过 |
| A2 | 协议信封编解码 | 同上 | ✅ 全过 |
| A3 | mod 能对着真实游戏程序集编译 | `dotnet build` 引用游戏 `Assembly-CSharp.dll` | ✅ 0 错误 0 警告 |
| A4 | mod DLL 无多余依赖 | 检查程序集引用表 | ✅ 仅 netstandard / BepInEx / UnityEngine.* / Assembly-CSharp |
| A5 | `ClientWebSocket` 在游戏 Mono 中可用 | 转储游戏 `System.dll` 元数据 | ✅ `ClientWebSocket` / `ClientWebSocketOptions` / `ManagedWebSocket` 均在 |
| A6 | `ClientWebSocket` 成员签名匹配 | 转储方法签名 | ✅ 与实现一致 |
| A7 | mod ↔ sidecar 端到端协议 | C# 测试客户端连真实 sidecar | ✅ 见 §4 |
| A8 | sidecar 自包含单文件可执行 | 空目录运行，无 Node 安装 | ✅ Windows 107MB / Linux 132MB |
| A9 | sidecar 负向/边界用例 | `cd sidecar && npm test` | ✅ 15 项全过 |
| A10 | 部署完整性 | 检查游戏目录与前缀注册表 | ✅ 见 §6 |

### 实现注意（测试中发现的真实问题）

**`StateChanged(Ready)` 早于 `Received("ready")` 触发。** `SidecarClient.HandleFrame`
先执行状态迁移（`SetState`），事件才在函数末尾统一派发。所以：

```csharp
// ✗ 有竞态：主线程可能先于事件处理器读到这个字段
client.Received += (type, payload, _) => { if (type == "ready") _sid = payload["sessionId"]; };
if (await WaitForReady()) Use(_sid);          // _sid 可能还是 null

// ✓ 正确：读客户端自身的属性
if (await WaitForReady()) Use(client.SessionId);
```

这个坑在 M0 端到端测试里真实踩到过（表现为"握手成功但 sessionId 为空"）。

## 2. 必须在游戏内确认的部分

> 这些无法在没有 GUI + Steam 的环境里自动化。日志统一在
> `Dyson Sphere Program/BepInEx/LogOutput.log`。

### V1 插件被加载

**期望**日志出现：

```
Icarus II 0.1.0 已加载
插件目录：.../BepInEx/plugins/IcarusII
热键：R（建造模式让位 = True）
sidecar：spawnExe @ 127.0.0.1:8765
```

**失败**：日志里完全没有 `Icarus II` → BepInEx 没生效。
Proton 下多半是 `winhttp` 覆盖没写上，见 §3。

---

### V2 sidecar 被拉起并连接

**期望**日志出现：

```
正在准备 sidecar…
sidecar 启动模式：spawnExe；数据目录：...
已拉起 sidecar：pid=... 
READY {"port":8765,"token":"..."}
[连接] Ready ...
```

**失败排查**：

| 日志里的线索 | 原因 | 处理 |
| --- | --- | --- |
| `找不到 sidecar 可执行文件` | 没复制 sidecar | 确认 `BepInEx/plugins/IcarusII/sidecar/icarus-ii-sidecar.exe` 存在 |
| `sidecar 进程提前退出` | Wine 下起不来 | 看紧跟的 `[sidecar:err]` 行 |
| `等待 sidecar 启动超时` | 端口被占 / 启动慢 | 看 `%APPDATA%\icarus-ii\runtime.json` 是否生成 |
| 一直没有 `[连接]` | 网络层问题 | 日志搜 `[client]` |

---

### V3 按 R 打开窗口

**操作**：进入存档 → 按 `R`。

**期望**：
- 右下角出现深色面板，标题 `Icarus II`，右上角状态 `就绪`（绿色）
- 输入框获得焦点，可直接打字
- 中文正常显示（不是方块/问号）

**同时检查**：窗口打开时按 `W/A/S/D`，**机甲不应该移动**。

**失败排查**：

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 按 R 无反应 | 见 V4；或字体创建失败 | 日志搜 `聊天窗口已创建` / `找不到可用字体` |
| 中文显示成方块 | 没借到游戏字体 | 日志搜 `字体来源`；若是 `builtin-arial(no-cjk)` 需要报告 |
| 窗口打开但 WASD 还在动 | 输入压制被游戏覆盖 | 报告，需要改用 Harmony patch `VFInput` |
| 窗口位置/大小不合适 | 配置项 | 改 `BepInEx/config/icarus-ii.cfg` 的 `[UI]` |

---

### V4 R 键避让（本项最关键）

`R` 在 DSP 里是**建造模式下旋转建筑**的键。需要确认两条路径互不干扰：

| 操作 | 期望 |
| --- | --- |
| 进入建造模式，选一个建筑，按 `R` | **旋转建筑**，聊天窗口**不弹出** |
| 按 `Esc` 退出建造模式后按 `R` | 聊天窗口弹出 |
| 拆除 / 升级 / 蓝图模式下按 `R` | 由游戏处理，不弹窗 |
| 窗口打开时按 `R` | 只是输入字符 `r`，窗口不关闭 |
| 窗口打开时按 `Esc` | 窗口关闭 |
| 打开星图后按 `R` | 不弹窗 |

**失败**：如果建造时按 `R` 仍然弹窗，说明 `BuildToolActive()` 判定不对
（依赖 `GameMain.mainPlayer.controller.actionBuild.activeTool`）。
请把日志里 `[热键]` 那行贴给我。

---

### V5 消息往返（echo）

**操作**：在窗口里输入 `你好`，回车。

**期望**依次出现：
1. `你  你好`
2. 状态变为 `思考中…`
3. 逐字出现 `助手  echo: 你好`
4. 状态回到 `就绪`

**失败**：看日志有没有 `[client]` 报错；若是 `尚未连接到 sidecar`，
说明连接没建立（回到 V2）。

---

### V6 断线重连

**操作**：窗口打开状态下，用任务管理器/`kill` 结束 `icarus-ii-sidecar.exe`。

**期望**：状态变 `重连中…`（黄色），日志出现退避重试；sidecar 不会自动重启（M0 已知限制）。

---

### V7 场景切换

**操作**：回到主菜单，再进入存档。

**期望**：日志出现 `UI 随场景销毁，将在新场景中重建`，
进入存档后 `聊天窗口已创建` 再次出现，按 `R` 仍能开窗。

---

## 3. Proton 的 DLL 覆盖（本次由安装脚本写入）

已验证据（写入前）：你的 Proton 前缀 `compatdata/1366540/pfx/user.reg` 中
**已有 27 条**由 Steam/Proton 自己写入的 `AppDefaults\<exe>\DllOverrides` 条目，
说明该机制是 Proton 的一等公民行为。本次新增：

```ini
[Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
"winhttp"="native,builtin"
```

**若 BepInEx 根本没加载**，按顺序检查：

1. 前缀里那条记录还在不在（Wine 在退出时可能覆盖）：
   ```bash
   grep -A2 'DSPGAME.exe..DllOverrides' \
     ~/.local/share/Steam/steamapps/compatdata/1366540/pfx/user.reg
   ```
2. 游戏目录里有没有 `winhttp.dll` 与 `doorstop_config.ini`
3. 兜底：改用 Steam 启动项 `WINEDLLOVERRIDES="winhttp=n,b" %command%`

---

## 4. 本机可复现的自动化验证命令

想自己重跑一遍时：

```bash
# 单元测试（JSON + 协议）
dotnet run --project tests/ProtocolTests

# 端到端（先起 sidecar）
# --agent echo：这条用例断言的是 M0 的 echo 帧序与文本不变量。
# M1 起 sidecar 默认 --agent pi，没有 API Key 时会回 config_required，
# 该用例会失败——这是预期行为，不是回归。
node sidecar/dist/index.js serve --port 8799 --token testtoken --agent echo &
dotnet run --project tests/ProtocolTests -- e2e ws://127.0.0.1:8799 testtoken

# 重新构建
scripts/build-mod.sh
```

---

## 5. M0 已知限制（不是 bug）

| 限制 | 说明 |
| --- | --- |
| 没有接入大模型 | M0 只有 echo；M1 接 DeepSeek |
| sidecar 崩溃后不自动重启 | 插件只在启动时拉起一次；M1 补看护逻辑 |
| 消息条目不支持工具卡片 | 整段聊天用一个 Text；M1 随工具调用需求改造 |
| 输入框是标准 UGUI 而非游戏组件 | 中文 IME 需实测；不行则换 `UISmartInputField` |
| 无历史持久化 | 聊天记录只在内存，重启即失 |
| 无快捷键提示 UI | 输入框占位文字里写了 `Esc 关闭` |
