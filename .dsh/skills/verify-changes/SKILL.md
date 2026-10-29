---
name: verify-changes
description: 运行 IcarusII 的验证矩阵 —— mod 构建、sidecar 离线测试、C# 协议测试、可选的协议端到端 —— 按改动范围裁剪。提交前使用，或被要求跑测试 / 确认改动仍可构建时使用。
---

# 验证 IcarusII 改动

一个脚本，按你实际改过的范围跑：

```bash
.dsh/skills/verify-changes/scripts/verify.sh              # mod + sidecar + protocol
.dsh/skills/verify-changes/scripts/verify.sh mod          # 只编译 mod
.dsh/skills/verify-changes/scripts/verify.sh sidecar      # typecheck + build + 离线回归
.dsh/skills/verify-changes/scripts/verify.sh protocol     # C# 协议/端到端离线用例
.dsh/skills/verify-changes/scripts/verify.sh e2e          # 起临时 sidecar 跑真连接
.dsh/skills/verify-changes/scripts/verify.sh all          # mod + sidecar + protocol
```

## 改动对应跑哪个范围

| 改动 | 必跑 |
| --- | --- |
| `mod/src/Shared/**`、协议字段、`sidecar/src/protocol.ts` | `protocol` + `sidecar` + `mod` |
| `mod/src/IcarusII/**`（游戏逻辑、传输、UI 代码） | `mod` |
| `sidecar/src/**`、工具 schema、配置迁移 | `sidecar` |
| `mod/unity/Editor/**`、`mod/unity/Prefab/**`、prefab | `mod`，并用 `rebuild-ui-bundle` skill |
| 只改 `docs/`、`*.md` | 不用跑 |

改了 WebSocket 握手、重连、工具路由时再加 `e2e`。

## 各步骤实际做什么

- **mod** → `scripts/build-mod.sh`（先跑 `fetch-deps.sh`，产物 `mod/src/IcarusII/bin/Release/IcarusII.dll`）。
- **sidecar** → `cd sidecar && npm test`：`typecheck` + `build` + 全部离线回归。
- **protocol** → `dotnet run --project tests/ProtocolTests`：不依赖游戏，直接跑 Shared 源码。
- **e2e** → 在 `127.0.0.1:8799` 起 `sidecar/dist/index.js serve --agent echo`，跑 `ProtocolTests -- e2e`，结束后自动关掉。
  需要先构建 sidecar（`npm test` 会构建）；端口/令牌用 `SIDECAR_E2E_PORT` / `SIDECAR_E2E_TOKEN` 覆盖。

## 环境

- 构建配置用 `CONFIG`（默认 `Release`）。
- `fetch-deps.sh` 需要能访问 NuGet；游戏目录可用 `-p:DspGameDir=...` 或环境变量覆盖（`build-mod.sh` 会透传额外参数）。
- 失败时脚本以非零码退出并保留原始输出，不要 `|| true` 吞掉。

## 之后

- UI/prefab 改动：接着跑 `rebuild-ui-bundle` skill，确认 `tool-bar` 锚点没有漂移。
- 要部署：先 `package-release`，再 `update-steam-mod`。
