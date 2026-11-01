# Node 26 升级验证清单

## 背景

sidecar 通过 Node SEA 把 Node 运行时内嵌进 `icarus-ii-sidecar.exe`，
`scripts/build-sidecar.sh` 的 `NODE_VERSION` 决定玩家实际跑的是哪个 Node。
2026-11-01 从 `24.18.0` 升到 `26.13.0`（当前 LTS），`@types/node` 同步升到 `^26.6.6`。

升级过程中查出并修掉的两个构建脚本缺陷，与 Node 版本无关但只有换版本才会暴露：

1. **SEA blob 必须由同版本 Node 生成**。blob 头部带格式版本号，注入到另一个大版本的运行时上会在启动时断言失败
   （`SeaDeserializer::Read` → `Assertion failed: (format_value) <= kModule`）。
   现在跨平台构建（Linux 上打 Windows）用同版本的宿主平台运行时生成 blob，不依赖构建机装的是哪个 Node。
2. **运行时缓存没有按版本失效**。旧逻辑只要 `node-linux-x64` 存在就复用，
   改了 `NODE_VERSION` 仍会悄悄拿旧运行时打包。现在用 `<runtime>.version` 戳文件判定。

## 本机已验证（Linux x64，2026-11-01）

| 项 | 方式 | 结果 |
| --- | --- | --- |
| 依赖安装 / typecheck | `npm run typecheck`（@types/node 26.6.6 + TS 7.0.2） | 通过 |
| sidecar 全量离线测试 | `npm test`，Node 26.13.0 | 1402 项通过，exit 0 |
| Linux SEA 产物 | `build-sidecar.sh linux-x64` → `--selfcheck` | 通过（153 MB） |
| Windows SEA 产物 | `build-sidecar.sh windows-x64`，blob 由同版本运行时生成 | 已产出（105 MB） |
| Windows blob 有效性 | 把同一份 blob 注入 Node 26 运行时跑 `--selfcheck` | 通过 |
| 打包链路 | `package-release.sh --update-deps` | 通过，`dist/pkg/IcarusII` 114 MB |

**没有验证到的**：Windows 真机/Proton 上的实际运行。本机 wine 起不来
（沙箱里 `/run/user/1000` 只读），exe 只验证了 blob 与运行时匹配、PE 注入成功。

## 发布前必须在实机做的验证

按顺序做，任何一步失败就回滚（见文末）。

1. **加载**：Windows 真机 + Proton 各启动一次，`BepInEx/LogOutput.log` 里没有 sidecar 崩溃、
   没有 `ERR_*`，插件初始化正常。
2. **窗口与协议**：按 R 打开聊天窗，状态从「未连接」变「已连接」，握手字段完整；
   断线重连一次仍正常。
3. **LLM 调用**：用真实 provider 发一条消息，流式输出正常、可中断。
   禁止用 mock 代替这一步。
4. **工具执行**：只读工具（`game_*`、`wiki_*`、`kb_*`）返回正常；
   写工具走确认钩子；`blueprint_save` 能落盘。
5. **`run_script`**：QuickJS-WASM 在 Node 26 上的行为最容易变——跑一个读写脚本，
   确认沙箱里 `typeof process/require/fetch === 'undefined'`、租约与预算生效。
6. **蓝图**：`blueprint_generate` + `game_copy_blueprint` 走通，生成的 `.txt` 能被游戏导入。
7. **持久化**：会话历史、设定页、工具开关重启后仍在。
8. **资源**：长会话（30 分钟以上）观察内存曲线与 CPU；对比 Node 24 版本，
   确认没有因 undici/HTTP 变化导致的泄漏或卡顿。
9. **退出**：退出游戏后 sidecar 进程随之退出，没有孤儿进程。

Steam Deck 走第 1–6 步即可，重点是 Proton 下的启动与 WebSocket 连接。

## 回滚

```bash
sed -i 's/NODE_VERSION:-26.13.0/NODE_VERSION:-24.22.2/' scripts/build-sidecar.sh
cd sidecar && npm install --save-dev @types/node@^24.19.2
rm -rf dist/.build/runtimes
```

然后重跑 `npm test` 与 `package-release.sh`。24.22.2 是同一时间的 24 线最新版，
回滚不会退回 24.18.0。
