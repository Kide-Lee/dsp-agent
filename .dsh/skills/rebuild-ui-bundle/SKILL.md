---
name: rebuild-ui-bundle
description: 从 prefab 重打聊天窗 UI AssetBundle，刷新 prefab 快照，并对比构建前后的 tool-bar 锚点以捕捉布局漂移。改过 prefab/UI 布局后使用，或游戏内聊天窗看起来不对时使用。
---

# 重打 IcarusII 的 UI 资源包

```bash
.dsh/skills/rebuild-ui-bundle/scripts/rebuild-ui-bundle.sh
.dsh/skills/rebuild-ui-bundle/scripts/rebuild-ui-bundle.sh --add-parts   # 先跑"只做加法"的移植工具
```

Unity 占用工程时脚本拒绝执行；它在构建前后各 dump 一次 prefab，四个工具栏锚点一旦漂移就失败。

## 前置条件

- Unity `2022.3.62f3`，装好 Windows Build Support，路径 `$HOME/Unity/Hub/Editor/<version>/Editor/Unity`（可用 `UNITY=/path/to/Unity` 覆盖）。
- Unity 编辑器**必须关闭**：批处理模式会独占锁。存在 `Temp/UnityLockfile` → 脚本直接停。
- 受限沙箱可能需要 GPU/完整权限，否则 Unity 会报 `Failed to initialize unity graphics`。
- prefab 修改在前，走 MCP for Unity。本技能只负责打包：

  1. 通过 MCP for Unity 修改 `dsp-game/Assets/IcarusII/ChatWindow.prefab`；
  2. 截图看窗口效果是否满足预期；
  3. 保存，然后关闭 Unity；
  4. 跑本脚本。

## 为什么要对比 dump

prefab 是二进制 —— `git diff` 只显示 `Bin`，工具栏悄悄移位看起来只是一次无害的字节变化。脚本在 dump 里抓四个右锚点分组，对比 `pos`/`sd`：

| 节点 | 右锚点 |
| --- | --- |
| `send-button` | `-20` |
| `permission-group` | `-100` |
| `thinking-group` | `-220` |
| `model-group` | `-340` |

锚点漂移说明有东西（编辑器自动保存、跑了构建器、打开过 prefab）改写了 `anchoredPosition`。**不要**在运行期补偿：恢复 `mod/unity/Prefab/ChatWindow.prefab` 再重打。

`--add-parts` 是有意改结构，所以这时锚点对比只报告、不判失败。

## 产物与检查项

- `dsp-game/Build/IcarusIIBundle/icarusii-ui` —— StandaloneWindows64 bundle，由 `package.sh` / `update-steam-mod` 打进包。
- `mod/unity/Prefab/` 快照刷新（由 `scripts/build-ui-bundle.sh` 完成）；随后脚本用 `cmp` 比对工作副本与快照。
- `.scratch/ui-dump/{before,after}.txt` 与 `{before,after}.log` —— 报告漂移时留着它们。

## 备注

- `build-mod.sh` 只重打 DLL；只改 prefab 而不重打这个 bundle，游戏里看不到变化。
- `scripts/build-ui-bundle.sh --add-parts` 只补缺失部件；默认路径从不重生成整个窗口。
- 构建成功后：`package-release`，再 `update-steam-mod`，然后用 1080p 和一个非 1080p 分辨率进游戏验收。
