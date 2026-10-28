# DSPGame 场景 UI 清单（实测）

测量对象：`dsp-game/Assets/Scenes/DSPGame.unity`（Unity `2022.3.62f3c1`，游戏 `0.10.30.3100`）。
测量方式：离线解析二进制场景，见文末「复现」。

## 规模

| 项 | 值 |
| --- | --- |
| 场景 GameObject | 14536 |
| UI Root 子树 | 14455 |
| In Game 子树 | 10564 |
| In Game 下挂 `Image` | 5844 |
| In Game 下挂 `Text` | 2901 |
| In Game 下挂 `UIButton` | 1451 |
| In Game 下挂 `Localizer` | 1269 |

## UI Root 一级结构

```
UI Root
├── Combat Cutscene
├── Mecha Editor Scene
├── Milky Way Scene
├── Always on Top
├── World Canvas        Canvas
├── Dyson Map / Splitter Shot / Radar Map / Minimap / Illustration Renderer
├── Game Starmap / Galaxy Select Starmap
├── Auxes
├── Event System
├── Overlay Canvas      Canvas(ScreenSpaceCamera, planeDistance=20, UI Camera)
├── Overlay Canvas - Top
└── UI Camera           Camera + 演出用子场景（Scene_*）
```

`In Game` 是 `Overlay Canvas` 下的 HUD 根，自带 `CanvasGroup`。

## In Game 一级分组

`Common Tools`、`Windows`、`Fullscreen UIs`、`Z Screen`、`Function Panel`、`Game Menu`、`Starmap UIs`、`Globemap`、`UPS Tool`、`Planet & Star Details`、`Planet Shield Details`、`Research Queue`、`Quest Group`、`Space Guide`、`Tutorial Group`、`Top Tips`、`Esc Menu`、`FPS Stats`、`Version Text`、`Sail Panel`、`Green Prebuild Warning`、`Prologue Subtitle`、`Scene UIs`、`High Group`、`Low Group`。

### Windows 下的现成窗口（节选）

`Todo Window`、`Tutorial Window`、`Advisor Tip`、`Itemup Group`、`Slot Picker`、`Mini Lab Panel`、`Goal Panel`、`Window Template`、`Player Inventory`、`Mecha Window`、`Replicator Window`、`Statistics Window`、`Control Panel Window`、`Miner Window`、`Storage Window`、`Tank Window`、`Assembler Window`、`Fractionator Window`、`Lab Window`、`Inserter Window`、`Splitter Window`、`Ejector Window`、`Turret Window`、`Silo Window`、`Accumulator Window`、`Field Generator Window`、`Power Exchanger Window`、`Power Node Window`、`Power Generator Window`、`Belt Window`、`Station Window`、`Marker Window`、`Dispenser Window`、`Monitor Window`、`Spraycoater Window`、`Research Result Window`、`Blueprint Browser`、`Dyson Sphere Editor`、`Death Panel`。

这些是场景里的节点，不是运行时实例化的 prefab；窗口显示与否由 `SetActive` 控制。

## 典型片段

### Function Panel（HUD 底部工具条）

```
Function Panel                       [RectTransform, CanvasRenderer]
├── menu-tweener
├── bg-trans
│   ├── sandbox-btn                  [RectTransform, CanvasRenderer, Image, Button, UIButton]
│   ├── bg / collider / collider-l / collider-r   [Image]
│   ├── mode-text                    [Text]
│   ├── prompt-text                  [Text, Localizer]
│   └── extract-button               [RectTransform, CanvasRenderer, Button, UIButton]
├── Build Menu                       [CanvasGroup]
│   └── general-group / main-group / child-group / reform-group / ux-group
├── Battery Bar                      [Image, UIButton, Button]
│   ├── battery-amount / battery-icon / shield-amount / hp-amount   [Image]
│   ├── details                      [UIButton]
│   └── infinite-energy-btn          [Image, Button, UIButton]
└── Sandbox Menu                     [CanvasGroup]
```

### 输入框（mod prefab 实例）

```
content-column/title/name-input   [Image, InputField, UIButton, SelectableExternalTarget, UIInputField]
└── sel-icon-btn                  [Image, UIButton]
```

## 仍未命名的脚本（字段签名 → 数量）

`ScrollRect`、`UIComboBox`、`UIInputField` 已经进入脚本的映射表；剩下这些只做到"最佳判断"，改动前先看反编译源码：

| 数量 | 字段特征 | 判断 |
| --- | --- | --- |
| 83 | `isWindow` | `UIBlockZone` |
| 71 | `triggers, hoveringLinkBounds, hoveringIconBounds, …` | 文本链接/hover 交互 |
| 56 | `_interpolator, _startDelay, _duration, …` | `Tweener` |
| 45 / 30 | `_tweener, _from, _to, toggleR/G/B/A`、`toggleX/Y/Z` | 颜色/位移补间 |
| 36 | `dragTrigger, dragTrans, refTrans, screenRect, …` | `UIWindowDrag` |

## 复现

```bash
UV_CACHE_DIR="$PWD/.deps/uv-cache" uv venv .deps/unitypy --python 3.12
UV_CACHE_DIR="$PWD/.deps/uv-cache" uv pip install --python .deps/unitypy/bin/python UnityPy

.deps/unitypy/bin/python .dsh/skills/dsp-native-ui/references/scene-ui-inventory.py --stats
.deps/unitypy/bin/python .dsh/skills/dsp-native-ui/references/scene-ui-inventory.py --root "Function Panel" --depth 2
```

脚本只读场景，不启动 Unity；类名靠「字段签名 → 组件」映射（映射表在脚本顶部）。
要拿到权威类名，可改用 Unity 批处理加载场景后 dump 组件（需要关闭编辑器）。
