---
name: rebuild-ui-bundle
description: 在运行中的 Unity 编辑器里重打聊天窗 UI AssetBundle，并刷新 prefab 快照、对比 tool-bar 锚点。改过 prefab/UI 后使用，或游戏内聊天窗看起来不对时使用。
---

# 重打 IcarusII 的 UI 资源包

**不关 Unity**，直接在编辑器里构建。

两条等价入口：

- 菜单：`Icarus II / Build UI AssetBundle (Windows)`
- MCP for Unity：调用 `IcarusII.EditorTools.IcarusIIBundleBuilder.BuildAll()`

构建器会在成功后自动把 prefab 与随包贴图回存到 `mod/unity/Prefab/`，所以不需要再跑额外的回存脚本。

## 前置条件

- Unity `2022.3.62f3`，装好 Windows Build Support。
- 编辑器里的 `Assets/IcarusII/Editor/*.cs` 必须是仓库里的最新版本；改过 `mod/unity/Editor/` 就先同步：
  `cp mod/unity/Editor/*.cs dsp-game/Assets/IcarusII/Editor/`
- 关于页的二维码贴图同理：`cp mod/unity/Resources/qq-group.png dsp-game/Assets/IcarusII/Resources/`

## 构建前先 dump 一次

prefab 是二进制，`git diff` 只显示 `Bin`，工具栏悄悄移位看起来只是一次无害的字节变化。构建前后各 dump 一次，比对这四个右锚点：

| 节点 | 右锚点 |
| --- | --- |
| `send-button` | `-20` |
| `permission-group` | `-100` |
| `thinking-group` | `-220` |
| `model-group` | `-340` |

dump 用 MCP for Unity 调 `IcarusII.EditorTools.IcarusIIDumpPrefab.DumpChatWindow()`，产物在 `.scratch/chatwindow-dump.txt`；抓锚点的命令：

```bash
grep -E 'tool-bar/(send-button|permission-group|thinking-group|model-group) +\[' .scratch/chatwindow-dump.txt \
  | sed -E 's#.*tool-bar/([a-z-]+) +\[.*pos=\(([^)]*)\) sd=\(([^)]*)\)\].*#\1 pos=\2 size=\3#'
```

锚点漂移说明有东西（编辑器自动保存、跑了构建器、打开过 prefab）改写了 `anchoredPosition`。**不要**在运行期补偿：恢复 `mod/unity/Prefab/ChatWindow.prefab` 再重打。

## 产物与检查项

- `dsp-game/Build/IcarusIIBundle/icarusii-ui` —— StandaloneWindows64 bundle，由 `package.sh` / `update-steam-mod` 打进包。
- `mod/unity/Prefab/` 快照由构建器回存；构建后用 `cmp` 比对工作副本与快照，应当一致。

## 备注

- `build-mod.sh` 只重打 DLL；只改 prefab 而不重打这个 bundle，游戏里看不到变化。
- 需要有"只做加法"的移植（补聊天窗缺失部件）时，调 `IcarusIIBundleBuilder.AddPartsAndBuild()`。
- 构建成功后：`package-release`，再 `update-steam-mod`，然后用 1080p 和一个非 1080p 分辨率进游戏验收。
