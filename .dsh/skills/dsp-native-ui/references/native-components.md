# 原生组件字段与 API

从 `.scratch/decomp/*.cs`（Assembly-CSharp，0.10.30.3100 反编译）摘录，只列与"用"相关的成员。

## UIButton

```csharp
public class UIButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
                        IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
{
    public Button button;              // 可选；为空时自己处理禁用态
    public InputField input;           // 可选，回车提交用
    public UIEventReceiver eventReceiver;
    public int data;                   // 点击时回传给 onClick
    public AudioSettings audios;       // enterName / downName / upName
    public TipSettings tips;
    public Transition[] transitions;
    public string tipTitleFormatString;
    public string tipTextFormatString;

    public event Action<int> onClick;         // 参数是 data
    public event Action<int, bool> onClickEnable;
    public void BindOnClickSafe(Action<int> func);   // 幂等，重复绑定不会叠
}
```

`Transition` 字段：`target`(Graphic)、`damp`、`mouseoverSize`、`pressedSize`、`normalColor`、`mouseoverColor`、`pressedColor`、`disabledColor`、`alphaOnly`、`highlight*Multiplier`、`highlightColorOverride`。

`TipSettings` 字段：`topLevel`、`simpleGeneralTipPrefab`、`level`、`delay`、`itemId`、`itemCount`、`itemInc`、`tipSprite`、`tipTitle`、`tipText`、`corner`、`offset`、`width`、`type`。

用法（ChatWindow 折叠 header，运行时补 UIButton）：

```csharp
var headerButton = headerRt.gameObject.AddComponent<Button>();
headerButton.transition = Selectable.Transition.None;
headerButton.targetGraphic = headerImg;

var native = headerRt.gameObject.AddComponent<UIButton>();
native.button = headerButton;
native.transitions = new[]
{
    new UIButton.Transition
    {
        target = headerImg, damp = 0.12f,
        normalColor = headerNormal, mouseoverColor = headerHover, pressedColor = headerPressed,
        disabledColor = headerNormal,
        mouseoverSize = 1f, pressedSize = 1f,
        highlightSizeMultiplier = 1f, highlightColorMultiplier = 1f, highlightAlphaMultiplier = 1f,
    }
};
native.onClick += _ => ToggleBlock(block);
```

## UIComboBox

```csharp
public class UIComboBox : MonoBehaviour
{
    public InputField m_Input;
    public Text m_Text;
    public RectTransform m_DropDownList;   // 弹出列表根
    public RectTransform m_DropDownContent;
    public Text m_EmptyItemRes;            // 空项模板
    public Button m_ListItemRes;           // 选项模板
    public Image m_SelectionBG;
    public Scrollbar m_Scrollbar;
    public bool CanInput = true;
    public bool DoubleClickChange;
    public int ItemHeight = 20;
    public int DropDownCount = 8;
    public bool InitItemIndexSelf = true;
    public int StartItemIndex = -1;
    public bool mouseWheelScroll;
    public bool translated;                // true = 文案已本地化，别再翻

    public ChangeEvent onItemIndexChange;  // UnityEvent，无参
    public List<string> Items;
    public List<int> ItemsData;
    public string text { get; set; }
    public int itemIndex { get; set; }
    public bool isDroppedDown { get; set; }
    public void UpdateItems();
}
```

`Awake()` 里调 `Translate()`：`translated == false` 时把 `Items` 全部 `Translate()` 一次再置 true。自己塞文案就先设 `translated = true`。

用法（ChatWindow 的思考档位）：

```csharp
combo.translated = true;
combo.Items.Clear();
combo.ItemsData.Clear();
combo.Items.Add(Strings.Get("thinking.level.off"));  combo.ItemsData.Add(0);
combo.Items.Add(Strings.Get("thinking.level.low"));  combo.ItemsData.Add(1);
combo.UpdateItems();
combo.onItemIndexChange.AddListener(OnComboChanged);
FlipComboDropdownUp(combo);   // 贴近屏幕底部时把 m_DropDownList 移到上方
```

## Localizer

```csharp
public class Localizer : MonoBehaviour
{
    public string stringKey;
    public string translation;        // 运行时由 stringKey.Translate() 填充
    public RectTransform posApplyTrans;
    public RectTransform sizeApplyTrans;
    public string xParamKey, yParamKey, wParamKey, hParamKey;
    public bool wAutoFit, hAutoFit;
    public float wAutoFitPadding, hAutoFitPadding;
}
```

- `Refresh()` 在 `OnEnable`/`Update` 调用：取同节点的 `Text`、`Image`、`RawImage`、`AudioSource`，分别写入译文、sprite、音频。
- 因此它既做文本本地化，也做图标/音效本地化。
- mod 自绘文本不要保留 Localizer，否则会被下一次 Refresh 覆盖。

## UIToggle / UISwitch / UIInputField

```csharp
public class UIToggle : MonoBehaviour
{
    public RectTransform rectTransform;
    public Toggle toggle;
    public Image image;
    public Sprite onSprite, offSprite;
    public GameObject[] onObjects, offObjects;
    public UIButton.AudioSettings audios;
    public bool isOn { get; set; }     // 内部同步 toggle 与 sprite
}

public class UISwitch : MonoBehaviour
{
    public UIButton uiButton;
    public RectTransform selfRectTrans, thumbRailRectTrans, thumbRectTrans;
    public Image thumbImg, barImg;
    public Color onBarColor, offBarColor;
    public float lerpSpeed = 20f;
    public bool isOn { get; set; }
}

public class UIInputField : MonoBehaviour
{
    public InputField inputField;
    public Text overText;
    public UIButton iconSelectBtn;
    public Func<Vector2> calcIconPickerPopupPos;
}
```

## UIRoot

```csharp
public class UIRoot : ManualBehaviour
{
    public static UIRoot instance { get; }
    public Canvas overlayCanvas;       // 主 UI 画布（ScreenSpaceCamera, planeDistance 20）
    public Canvas overlayCanvasTop;    // 顶层提示
    public UIGame uiGame;
    public UIMainMenu uiMainMenu;
    public UIMilkyWay uiMilkyWay;
    public UIMechaEditor uiMechaEditor;
    public RectTransform buttonTipTransform, buttonTopTipTransform, itemTipTransform;
    public static int tipLevel;

    public static bool ScreenPointIntoRect(Vector3 screenPoint, RectTransform rect, out Vector2 rectPoint);
    public static Vector2 WorldToScreenPoint(Vector3 worldPos);
}
```

## UIMessageBox（原生对话框）

```csharp
public class UIMessageBox : UIDialog
{
    public static UIMessageBox Show(string title, string message, string btn1, int icon);
    public static UIMessageBox Show(string title, string message, string btn1, string btn2, int icon,
                                    Response resp1, Response resp2);
    public static bool CloseTopMessage();
    public static void CloseAllMessage();
    public static int activeCount { get; }
}
```

## 画布嵌套（把 mod 窗口挂到游戏 UI 上）

`ChatWindow.CreateCanvas(UIRoot)` 的要点：

```csharp
Canvas host = uiRoot.overlayCanvas;
var rootRt = UiFactory.Node("IcarusIICanvas", host.transform);
var canvas = rootRt.gameObject.AddComponent<Canvas>();

rootRt.anchorMin = Vector2.zero;
rootRt.anchorMax = Vector2.one;
rootRt.anchoredPosition = Vector2.zero;
rootRt.sizeDelta = Vector2.zero;

canvas.overrideSorting = true;
canvas.sortingOrder = 30000;
canvas.additionalShaderChannels = host.additionalShaderChannels;  // 缺了游戏字体材质会花
rootRt.gameObject.layer = host.gameObject.layer;                  // UI Camera 的 culling mask
```

宿主不可用时退回 `ScreenSpaceOverlay` 顶层画布，并复制宿主的 `CanvasScaler` 设置。
