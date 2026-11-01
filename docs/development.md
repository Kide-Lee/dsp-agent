# 开发与构建

本文是开发者手册；玩家安装与使用见 [README.md](../README.md)。

## 构建

```bash
scripts/fetch-deps.sh          # 拉取 BepInEx 参考程序集到 .deps/
scripts/build-mod.sh           # 编译 IcarusII.dll
scripts/build-ui-bundle.sh     # 重打 UI AssetBundle（需先关闭 Unity）

# 可选：从本地游戏资源离线生成知识库（开发/离线测试用；运行时会自动导出）
PYTHONPATH=.scratch/pylib python3 tools/build-kb.py \
  --prototypes dsp-game/Assets/Resources/Prototypes \
  --locale-dir dsp-game/Locale/2052 --tutorial-dir dsp-game/Wiki/Tutorial \
  --locale zh-CN --game-version 0.10.30.3100 --out .scratch/kb/kb-zh.json

cd sidecar && npm install      # 首次
scripts/build-sidecar.sh       # 生成自包含的 icarus-ii-sidecar.exe
```

### 部署到 r2modman

r2modman 从自己的配置档加载 BepInEx，游戏根目录的 `BepInEx/plugins` 在 **Start modded** 下不生效。
打完包后用：

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh --dry-run
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh
```

默认写到 `~/.config/r2modmanPlus-local/DysonSphereProgram/profiles/<配置档>/BepInEx/plugins/IcarusII/`。
只有一个装好 BepInEx 的配置档时自动选；多个用 `--profile`；数据目录不在默认位置用 `--r2modman-dir`。

- 首次部署会把游戏目录的 `BepInEx/config/icarus-ii.cfg` 复制过来，目标已存在时不覆盖。
- `--remove-legacy` 把游戏目录的旧插件移到 `dist/legacy-install/`，不直接删除。
- 手动安装（不用 r2modman）仍可用 `scripts/install.sh --game-dir ...`，但本地开发不再走这条路。

启动游戏用 r2modman 的 **Start modded**；日志在配置档的 `BepInEx/LogOutput.log`。
sidecar 数据仍在 Proton 前缀的 `%APPDATA%\icarus-ii`，换部署位置不影响 key 和历史。

### 测试

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
docs/tools/            工具参考：提示词原文、参数、返回字段（生成物，勿手改）
scripts/               构建 / 安装
```

