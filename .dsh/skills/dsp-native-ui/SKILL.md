---
name: dsp-native-ui
description: Use Dyson Sphere Program's own UI building blocks (UIButton, UIComboBox, Localizer, UIToggle/UISwitch, UIInputField) the way the game does — component sets, event wiring, localization and the pitfalls measured from DSPGame.unity. Use when adding or restyling IcarusII UI, picking a native widget, or when a native control looks or behaves wrong.
---

# 用游戏原生 UI 组件

数据来源：`dsp-game/Assets/Scenes/DSPGame.unity` 的 `UI Root/Overlay Canvas/In Game` 子树（10564 个节点），组件名由序列化字段签名还原（场景里的 MonoBehaviour 类名被剥离）。
量化结果与复现脚本见 [references/scene-inventory.md](references/scene-inventory.md)。
逐组件字段/API 见 [references/native-components.md](references/native-components.md)。

## 三条前提

1. 布局只在 prefab 里改（AGENTS.md：prefab-first），运行期只做查找、绑定和数据驱动的增删。
2. 抄场景里已有的节点，不要凭空拼组件：In Game 子树里每个控件都是调好的样板。
3. 组件是"套装"：可点按钮 = `Image` + `Button` + `UIButton`；少一个就会掉 hover 音效、缩放或 tooltip。

## 场景结构（实测）

```
UI Root
├── Overlay Canvas        Canvas(ScreenSpaceCamera, planeDistance=20, UI Camera)
│   └── In Game           HUD 根，10564 节点，自带 CanvasGroup
│       ├── Windows                 49 个现成窗口节点（Storage Window、Mecha Window…）
│       ├── Fullscreen UIs / Z Screen
│       ├── Function Panel          sandbox-btn / Build Menu / Battery Bar …
│       ├── Game Menu / Starmap UIs / Globemap / UPS Tool
│       ├── Planet & Star Details、Quest Group、FPS Stats …
└── Overlay Canvas - Top  顶层提示用，同样 ScreenSpaceCamera
```

- 窗口不是运行时 new 出来的：`Windows` 下面就是场景里的节点，按需 `SetActive`。
- `UIRoot.instance.overlayCanvas` / `overlayCanvasTop` 是宿主画布；画布是 **ScreenSpaceCamera**，所以取屏幕坐标必须走 `UIRoot.ScreenPointIntoRect` / `UIRoot.WorldToScreenPoint`，不要用 `Camera.main`。
- 往游戏 UI 上挂自己的窗口时照 `ChatWindow.CreateCanvas` 做：父子画布 + `overrideSorting` + `sortingOrder=30000` + 复制宿主的 `additionalShaderChannels` + 跟宿主同 layer（UI Camera 的 culling mask）。

## 组件频次（In Game 子树）

| 组件 | 数量 | 用途 |
| --- | --- | --- |
| `RectTransform` | 10564 | 全部节点 |
| `CanvasRenderer` | 9068 | 有图形才会挂 |
| `Image` | 5844 | 底图/图标/命中区 |
| `Text`（DSP 定制） | 2901 | 文本，支持 `wideCharSpacing` 等 |
| `UIButton` | 1451 | hover/按下音效、缩放、tooltip、`onClick` |
| `Localizer` | 1269 | `stringKey` 翻译到 Text/Image/RawImage/AudioSource |
| `Button` | 839 | uGUI 点击回调 |
| `EventTrigger` | 170 | 拖拽/长按等 |
| `Slider` | 166 | 数值条 |
| `Outline` | 150 | 描边 |
| `InputField` | 128 | 输入框 |
| `Mask` | 127 | 裁切 |
| `CanvasGroup` | 101 | 整体透明度/交互开关 |
| `ScrollRect` | 82 | 滚动区 |
| `RawImage` | 73 | 贴图/视频面 |
| `UIComboBox` | 39 | 下拉框 |
| `UIInputField` | 31 | 输入框包装（图标选择/提示） |

## 常见组合（照抄这些）

| 组合 | 数量 | 场景 |
| --- | --- | --- |
| `Image` | 4091 | 纯底图/图标 |
| `Text` | 1789 | 纯文字 |
| `Localizer + Text` | 1071 | 游戏内可翻译文案 |
| `Button + Image + UIButton` | 612 | 标准可点按钮 |
| `Image + UIButton` | 488 | 只要 hover/音效、不要 uGUI 回调 |
| `Image + Slider` | 124 | 滑块 |
| `Image + Mask` | 113 | 裁剪容器 |
| `Image + InputField + UIButton` | 80 | 输入框 |
| `Button + UIButton` | 74 | 无底图按钮 |

## 各组件的用法

### UIButton（最常用，1451 个）

字段：`button`（可选 uGUI Button）、`input`、`eventReceiver`、`data`（int 负载）、`audios`（enter/down/up 音效名）、`tips`（TipSettings）、`transitions[]`、`tipTitleFormatString` / `tipTextFormatString`。

- 事件：`onClick`（`Action<int>`，收到的是 `data`）、`onClickEnable`、`BindOnClickSafe(Action<int>)`。
- `transitions[]` 决定观感：`target` 是 Graphic，配 `normalColor/mouseoverColor/pressedColor/disabledColor`、`damp`、`mouseoverSize/pressedSize`。
- `button` 为空也能工作（自己实现 Pointer 接口）；一旦挂 Button，它会把 `enabled/interactable` 当作禁用条件。
- 命中靠 Graphic 的 `raycastTarget`：按钮底图必须 true，装饰性子节点设 false。

### UIComboBox（39 个）

字段：`m_Input`、`m_Text`、`m_DropDownList`、`m_DropDownContent`、`m_EmptyItemRes`、`m_ListItemRes`、`m_SelectionBG`、`m_Scrollbar`、`ItemHeight`、`DropDownCount`、`InitItemIndexSelf`、`StartItemIndex`、`translated`。

- 数据：`Items`（string 列表）、`ItemsData`（int 列表）、`UpdateItems()`、`itemIndex`、`text`、`isDroppedDown`。
- 事件：`onItemIndexChange`（UnityEvent，无参数，读 `itemIndex` 自己映射）。
- `Awake` 会调 `Translate()`：`translated=false` 时把所有 `Items` 过一遍游戏翻译并置 true。自己提供文案就先 `translated = true`，否则会被翻掉。
- 下拉不会自动向上翻；放在屏幕下半区时要自己挪 `m_DropDownList`（参考 `ChatWindow.FlipComboDropdownUp`）。

### Text（DSP 定制的 UnityEngine.UI.Text）

比原版多 `wideCharSpacing`、`latinCharSpacing`、`minimalMeshGenerateMode`；中文标签常用 `wideCharSpacing = 1~2`。
纯装饰文字 `raycastTarget = false`，否则挡住下面的按钮。

### Localizer（1269 个）

- `stringKey` 经游戏翻译表得到 `translation`，`OnEnable` 与 `Update` 时刷新。
- 同节点上的 `Text` / `Image` / `RawImage` / `AudioSource` 都会被它改写（文本、sprite、clip）。
- 可自动撑宽高：`posApplyTrans` / `sizeApplyTrans` + `wAutoFit` / `hAutoFit`（带 padding）。
- **mod 自己的文案不要挂 Localizer**：它会在下一次 Refresh 覆盖你写的 `Text.text`；这类节点要删掉 Localizer 或用 `Strings.Get` 直接赋值。

### 其余

- `UIToggle`：`isOn`、`toggle`、`image`、`onSprite/offSprite`、`onObjects/offObjects`，勾选动画自带。
- `UISwitch`：`isOn`、`uiButton`、`thumbRailRectTrans`、`thumbRectTrans`、`onBarColor/offBarColor`、`lerpSpeed`。
- `UIInputField`：包一层 `InputField`，带 `overText`、`iconSelectBtn`（图标选择）。
- `Slider` / `ScrollRect` / `Mask` / `CanvasGroup` / `Outline` / `EventTrigger`：标准 uGUI，别自己重写。

## 现成 prefab 与原生对话框

- `Resources.Load<T>("UI/Prefabs/<name>")` 是游戏的取预制体方式（例：`Resources.Load<UISectorView>("UI/Prefabs/Sector View")`），`dsp-game/Assets/Resources/UI/Prefabs` 下有 122 个。
- 原生对话框：`UIMessageBox.Show(title, message, btn1, icon)` 等重载，`UIMessageBox` 继承 `UIDialog`，自带按钮栈。
- 按钮 tooltip 走 `UIButton.tips` + `UISimpleGeneralTip`；不要自己写弹层。

## 踩坑清单

1. 只挂 `Button`：能点，但没有 hover 音效/缩放/tooltip → 观感立刻不像游戏 UI。
2. 按钮底图 `raycastTarget=false`：点不到。
3. 给自绘文案挂了 `Localizer`：文本被翻译表覆盖。
4. `UIComboBox.translated=false` 又自己塞中文：`Awake` 会尝试翻译这些字符串。
5. 下拉框贴屏幕底部：列表会跑到屏幕外，自己翻转。
6. 用 `Camera.main` 算 UI 坐标：overlay canvas 由 UI Camera 渲染，坐标会错。
7. 场景切换后 `UIRoot` 重建：挂在它下面的窗口会被销毁，需要重新创建/绑定。
8. 运行期改 `RectTransform` 尺寸来"修布局"：违反 prefab-first，改 prefab。
9. 图标用 emoji：禁止，复用游戏 sprite 或找同风格美术。

## 验证

- 层级/交互：用 `ui-ugui` skill 检查 prefab；改完按 `rebuild-ui-bundle` 出包。
- 进游戏验收：1080p + 一个非 1080p 分辨率，看 hover、tooltip、下拉方向、长文案省略。
