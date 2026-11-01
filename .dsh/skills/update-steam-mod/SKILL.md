---
name: update-steam-mod
description: 从 dist/pkg 安全更新 r2modman 配置档（默认）或手动安装的游戏目录里的 IcarusII BepInEx mod，含备份、SHA-256 校验、配置迁移与旧安装清理。被要求更新/安装/部署 mod，或验证打包产物与已安装实例是否一致时使用。
---

# 更新 r2modman 配置档里的 IcarusII

在用户要求**更新 mod**、部署打包好的构建，或核实已安装实例是最新构建时使用。

默认目标是 r2modman 配置档，不是游戏根目录：

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh
```

不确定已安装状态时，一律先跑 `--dry-run`：

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh --dry-run
```

脚本只复制四个文件，先备份旧文件、校验哈希，再确认现有 sidecar `config.json` 仍能带着 key 迁移。它**绝不**写 r2modman 的 `mods.yml`、其他 mod 或 sidecar 数据。

## 目标位置

| 场景 | 插件目录 |
|------|----------|
| r2modman（默认） | `~/.config/r2modmanPlus-local/DysonSphereProgram/profiles/<配置档>/BepInEx/plugins/IcarusII/` |
| 手动安装 / 无管理器 | `<DSP>/BepInEx/plugins/IcarusII/` |

自动检测顺序：

1. `--game-dir` 指定时，按手动安装处理。
2. `--r2modman-dir` / `--profile` 指定时，按 r2modman 处理。
3. 否则在 `r2modmanPlus-local`（`$XDG_CONFIG_HOME`、`~/.config`、r2modman Flatpak）里找 `DysonSphereProgram/profiles`：
   - 只有一个装好 BepInEx 的配置档 → 直接用；
   - 多个 → 报错并要求 `--profile`；
   - 一个都没有 → 退回游戏目录（并提示）。

为什么是配置档：r2modman 启动时把 Doorstop 的目标指到
`profiles/<配置档>/BepInEx/core/BepInEx.Preloader.dll`，BepInEx 的插件根目录随之变成配置档的 `BepInEx/`。游戏根目录里的 `BepInEx/plugins` 在 r2modman 启动下**不会被加载**。

## 更新哪些文件

来源：`dist/pkg/IcarusII/BepInEx/plugins/IcarusII/`

| 文件 | 用途 |
|------|---------|
| `IcarusII.dll` | BepInEx 插件 |
| `IcarusII.pdb` | 堆栈 / 调试 |
| `sidecar/icarus-ii-sidecar.exe` | Windows sidecar（Proton 下运行的就是它） |
| `ui/icarusii-ui` | 聊天窗 AssetBundle |

## 配置与旧安装

- 配置：目标配置档的 `BepInEx/config/icarus-ii.cfg`。目标文件不存在时，脚本会从游戏目录的同名文件复制一次；已存在时**绝不覆盖**。
- 每个配置档有独立配置。切换配置档后热键/权限/模型等设置会回到该档的值。
- 旧安装：`--remove-legacy` 把 `<DSP>/BepInEx/plugins/IcarusII` 移到
  `<repo>/dist/legacy-install/IcarusII-<timestamp>/`，不直接删除。
- 游戏根目录的 `BepInEx/core`、`winhttp.dll` 和 `doorstop_config.ini` 保留：r2modman 的 Doorstop 代理仍在游戏根目录，删除会让直接启动和高版本 BepInEx 场景失效。要清理也只在确认不用手动安装后处理。

## 绝不触碰

- `<配置档>/mods.yml` —— r2modman 的 mod 清单
- `<配置档>/BepInEx/plugins/` 下的其他 mod
- `.../compatdata/1366540/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii/config.json` —— key/供应商
- `.../icarus-ii/runtime.json` —— 握手令牌
- `.../icarus-ii/agent/` 与 `.../sessions/` —— 历史记录

**不要**跑 `install.sh --uninstall`，也不要整体替换插件目录。保留 `.backup-*`。

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
3. 确认配置档里的 `BepInEx/core/BepInEx.dll` 存在；没有就先在 r2modman 里给该配置档装 BepInEx。
4. 不要在同一个配置档里再从 Online 装 Thunderstore 的 IcarusII：r2modman 管的同名 mod 会覆盖手写部署。线上包和本地开发包二选一。

## 手工兜底

脚本用不了时（以配置档 `DSP` 为例）：

```bash
R2="$HOME/.config/r2modmanPlus-local/DysonSphereProgram/profiles/DSP"
PLUGIN="$R2/BepInEx/plugins/IcarusII"
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

手动安装把 `R2=...` 换成 `DSP="$HOME/.local/share/Steam/steamapps/common/Dyson Sphere Program"`，插件目录换成 `$DSP/BepInEx/plugins/IcarusII`。

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

- 从 r2modman 点 **Start modded** 启动（不要从 Steam 直接启动），按 `R`。
  r2modman 模式下日志在配置档的 `BepInEx/LogOutput.log`；手动安装在 `<DSP>/BepInEx/LogOutput.log`。
  日志里应出现 `Loading [Icarus II <版本>]`。

## 回滚

把最新的 `BepInEx/plugins/IcarusII/.backup-<timestamp>/` 里那四个文件拷回原位。
如果用过 `--remove-legacy`，把 `dist/legacy-install/IcarusII-<timestamp>/` 移回
`<DSP>/BepInEx/plugins/IcarusII`。
sidecar/配置数据未被触碰，所以回滚不会丢 key 或历史。

## 常见故障

| 现象 | 原因 / 处理 |
|---------|-------------|
| `DSPGAME 正在运行` | 退出游戏后重跑。 |
| `r2modman 有 N 个可用配置档` | 用 `--profile <名字>` 指定，或删掉不用的配置档。 |
| `配置档没有 BepInEx` | 在 r2modman 里给它装 BepInEx，或换一个配置档。 |
| 哈希不一致 | 复制被中断；重跑；检查磁盘/权限。 |
| 界面还是旧的 | prefab 改完没有重打 `ui/icarusii-ui`；重打 bundle。 |
| 游戏里没变化 | 从 Steam 直接启动了；要从 r2modman 的 **Start modded** 启动。 |
| r2modman 更新后变回线上版 | 同一配置档里装过 Thunderstore 的 IcarusII；卸掉线上包，或改用手写部署的独立配置档。 |
| 旧 key 丢了 | 看迁移输出；新 sidecar 应把 `provider/model/apiKey` 迁移到 `providers.deepseek`。绝不删 `config.json`。 |
| `wineserver` 告警 | 复制文件仍然安全；只有改注册表才需要停 Wine。 |

## 备注

- r2modman 数据根可能是 `~/.config/r2modmanPlus-local` 或 r2modman 的 Flatpak 配置目录；脚本都会找。
- 配置档里手写的 `plugins/IcarusII` 不在 r2modman 的 mod 列表里，配置档导出也不会包含它；更新靠本脚本。
- Steam 库可能在 `~/.local/share/Steam` 之外；脚本会读 `libraryfolders.vdf`。自动定位失败时用 `--game-dir`。
- app id 是 `1366540`；Proton prefix 是 `steamapps/compatdata/1366540/pfx`。
- 游戏在 Windows 上原生运行时，跳过 Proton 注册表检查，改用 `%APPDATA%\icarus-ii`。
