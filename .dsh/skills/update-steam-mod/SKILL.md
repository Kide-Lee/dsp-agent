---
name: update-steam-mod
description: 从 dist/pkg 安全更新 Steam/Proton 版《戴森球计划》里的 IcarusII BepInEx mod，含备份、SHA-256 校验、配置保留与旧配置迁移检查。被要求更新/安装/部署 mod 到 Steam，或验证打包产物与已安装游戏是否一致时使用。
---

# 更新 Steam/Proton 版 DSP 里的 IcarusII

在用户要求**更新 Steam 上的 mod**、部署打包好的构建，或核实已安装的游戏是最新构建时使用。

一条命令的路径是：

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh
```

不确定已安装状态时，一律先跑 `--dry-run`：

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh --dry-run
```

脚本只复制四个文件，先备份旧文件、校验哈希，再确认现有 sidecar `config.json` 仍能带着 key 迁移。它**绝不**写游戏配置或 sidecar 数据。

## 更新哪些文件

来源：`dist/pkg/IcarusII/BepInEx/plugins/IcarusII/`

| 文件 | 用途 |
|------|---------|
| `IcarusII.dll` | BepInEx 插件 |
| `IcarusII.pdb` | 堆栈 / 调试 |
| `sidecar/icarus-ii-sidecar.exe` | Windows sidecar（Proton 下运行的就是它） |
| `ui/icarusii-ui` | 聊天窗 AssetBundle |

目标位置：`<DSP>/BepInEx/plugins/IcarusII/`。

## 绝不触碰

- `<DSP>/BepInEx/config/icarus-ii.cfg` —— 玩家设置
- `.../compatdata/1366540/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii/config.json` —— key/供应商
- `.../icarus-ii/runtime.json` —— 握手令牌
- `.../icarus-ii/agent/` 与 `.../sessions/` —— 历史记录

**不要**跑 `install.sh --uninstall`，也不要整体替换插件目录。保留 `.bak-*` 和 `.backup-*`。

## 运行前

1. 确认打包产物是最新的：

   ```bash
   ./scripts/build-mod.sh                 # mod 源码改过才需要
   ./scripts/build-sidecar.sh windows-x64 # sidecar 源码改过才需要
   ```

   prefab / UI 改过时，bundle 在**运行中的 Unity 编辑器**里打：
   菜单 `Icarus II / Build UI AssetBundle (Windows)`，或 MCP 调 `IcarusIIBundleBuilder.BuildAll()`。
   不需要关闭编辑器，构建器会自己回存 prefab 快照。

2. 退出 DSP。`DSPGAME.exe` 在跑时脚本会拒绝执行。
3. 确认 `BepInEx/core/BepInEx.dll` 存在。
4. Proton 下 prefix 里应已有 `winhttp` 覆盖；脚本只检查并告警，不改注册表。

## 手工兜底

脚本用不了时：

```bash
DSP="$HOME/.local/share/Steam/steamapps/common/Dyson Sphere Program"
PLUGIN="$DSP/BepInEx/plugins/IcarusII"
SRC="$PWD/dist/pkg/IcarusII/BepInEx/plugins/IcarusII"
TS=$(date +%Y%m%d-%H%M%S)
mkdir -p "$PLUGIN/.backup-$TS/sidecar" "$PLUGIN/.backup-$TS/ui"
cp -a "$PLUGIN/IcarusII.dll" "$PLUGIN/IcarusII.pdb" "$PLUGIN/.backup-$TS/"
cp -a "$PLUGIN/sidecar/icarus-ii-sidecar.exe" "$PLUGIN/.backup-$TS/sidecar/"
cp -a "$PLUGIN/ui/icarusii-ui" "$PLUGIN/.backup-$TS/ui/"
cp -f "$SRC/IcarusII.dll" "$PLUGIN/IcarusII.dll"
cp -f "$SRC/IcarusII.pdb" "$PLUGIN/IcarusII.pdb"
cp -f "$SRC/sidecar/icarus-ii-sidecar.exe" "$PLUGIN/sidecar/icarus-ii-sidecar.exe"
cp -f "$SRC/ui/icarusii-ui" "$PLUGIN/ui/icarusii-ui"
sha256sum "$SRC/IcarusII.dll" "$PLUGIN/IcarusII.dll"
```

## 验证

- 每个被复制文件的 SHA-256 与源包一致。脚本会强制校验。
- 旧版 sidecar 配置仍能加载：跑脚本的迁移检查，或：

  ```bash
  node --input-type=module - <<'NODE'
  import { pathToFileURL } from 'node:url';
  const { ConfigStore } = await import(pathToFileURL('sidecar/dist/config.js').href);
  const s = new ConfigStore({ path: '/tmp/config-copy.json', requireApiKey: true });
  console.log(s.view());
  NODE
  ```

  期望看到 `state: "ready"`、旧的 provider/model 仍为 active、`hasKey: true`。不要打印 key。
- Proton prefix 的 `user.reg` 里有：

  ```
  [Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
  "winhttp"="native,builtin"
  ```

- 启动游戏按 `R`；日志在 `<DSP>/BepInEx/LogOutput.log`。

## 回滚

把最新的 `BepInEx/plugins/IcarusII/.backup-<timestamp>/` 里那四个文件拷回原位。sidecar/配置数据未被触碰，所以回滚不会丢 key 或历史。

## 常见故障

| 现象 | 原因 / 处理 |
|---------|-------------|
| `DSPGAME 正在运行` | 退出游戏后重跑。 |
| 哈希不一致 | 复制被中断；重跑；检查磁盘/权限。 |
| BepInEx 没加载 | 缺 Proton `winhttp` 覆盖；在没有 Wine 进程运行时补上。 |
| 界面还是旧的 | prefab 改完没有重打 `ui/icarusii-ui`；重打 bundle。 |
| 旧 key 丢了 | 看迁移输出；新 sidecar 应把 `provider/model/apiKey` 迁移到 `providers.deepseek`。绝不删 `config.json`。 |
| `wineserver` 告警 | 复制文件仍然安全；只有改注册表才需要停 Wine。 |

## 备注

- Steam 库可能在 `~/.local/share/Steam` 之外；脚本会读 `libraryfolders.vdf`。自动定位失败时用 `--game-dir`。
- app id 是 `1366540`；Proton prefix 是 `steamapps/compatdata/1366540/pfx`。
- 游戏在 Windows 上原生运行时，跳过 Proton 注册表检查，改用 `%APPDATA%\icarus-ii`。
