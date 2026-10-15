using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using IcarusII.Config;
using IcarusII.I18n;

namespace IcarusII.UI
{
    /// <summary>
    /// 聊天窗口。
    ///
    /// 设计取舍：
    ///  - 界面在首次进入游戏时**一次性构建**，之后只用 SetActive 开关，
    ///    不销毁重建（避免 GC 抖动与重建闪烁）。
    ///  - 整段聊天记录用**一个 Text** 承载（内部用 committed + streaming 两段拼接），
    ///    而不是每条消息一个 GameObject：M0 阶段更简单，且 UGUI 的
    ///    ContentSizeFitter 能自动撑高。消息卡片化留到 M1 的工具卡片需求出现时再做。
    ///  - 渲染节流到每帧最多一次：流式回复可能一帧收到多个 delta。
    /// </summary>
    /// <summary>点击窗口任意位置即聚焦输入框。</summary>
    internal sealed class ClickToFocus : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public ChatWindow Target;

        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData)
        {
            Target?.FocusInput();
        }
    }

    internal sealed class ChatWindow
    {
        private const int MaxTranscriptChars = 120000;

        // 兜底配色（捕获不到游戏样式时使用）
        private static readonly Color DefaultPanelBg = new Color(0.055f, 0.082f, 0.125f, 0.97f);
        private static readonly Color DefaultHeaderBg = new Color(0.098f, 0.157f, 0.235f, 1f);
        private static readonly Color DefaultInputBg = new Color(0.098f, 0.129f, 0.180f, 1f);
        private static readonly Color DefaultTextMain = new Color(0.878f, 0.918f, 0.961f, 1f);
        private static readonly Color DefaultTextDim = new Color(0.596f, 0.678f, 0.757f, 1f);
        private static readonly Color DefaultAccent = new Color(0.353f, 0.749f, 1f, 1f);

        // 运行时实际使用的文本颜色（ApplyStyle 时按游戏窗口调整）
        private Color _textMain = DefaultTextMain;
        private Color _textDim = DefaultTextDim;
        private Color _accent = DefaultAccent;

        private GameObject _root;
        private RectTransform _panel;
        private RectTransform _content;
        private Image _panelImage;
        private Image _frameImage;
        private Image _glassImage;
        private Image _headerImage;
        private Image _inputImage;
        private Text _statusText;
        private Text _transcript;
        private ScrollRect _scroll;
        private InputField _input;

        private readonly List<string> _lines = new List<string>();
        private readonly StringBuilder _committed = new StringBuilder();
        private readonly StringBuilder _current = new StringBuilder();
        private readonly StringBuilder _renderScratch = new StringBuilder();

        private bool _dirty;
        private int _fontSize;
        private int _maxLines;

        /// <summary>打开窗口前 VFInput.inputing 的原值，关闭时还原。</summary>
        private bool _savedInputing;
        private bool _autoFocusInput;
        private bool _wantInputFocus;
        private bool _keyEntryMode;

        /// <summary>窗口打开期间需要持续压制游戏输入（见 Plugin.LateUpdate）。</summary>
        public bool WantsInputSuppressed => IsOpen;

        /// <summary>聊天输入框是否获得焦点。R 开关与 Esc 的行为都依赖它。</summary>
        public bool InputFocused => _input != null && _input.isFocused;

        /// <summary>
        /// 聚焦输入框（按 Enter 或点击窗口时调用）。
        ///
        /// 记录"期望聚焦"而不是只调一次 API：游戏本身的 UI 逻辑可能会
        /// 清空 EventSystem 的选中对象，导致焦点被抢走。Tick 会据此重申。
        /// </summary>
        public void FocusInput()
        {
            if (_input == null) return;
            _wantInputFocus = true;
            ApplyFocus();
        }

        /// <summary>取消输入框焦点，把键盘交还窗口（之后 R 即可关窗）。</summary>
        public void BlurInput()
        {
            if (_input == null) return;
            _wantInputFocus = false;
            _input.DeactivateInputField();
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.currentSelectedGameObject == _input.gameObject)
                es.SetSelectedGameObject(null);
        }

        private void ApplyFocus()
        {
            if (_input == null) return;
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null && es.currentSelectedGameObject != _input.gameObject)
                    es.SetSelectedGameObject(_input.gameObject);
                _input.ActivateInputField();
                _input.Select();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"聚焦输入框失败：{ex.Message}");
            }
        }

        /// <summary>诊断用：EventSystem 与选中对象状态。</summary>
        public string DescribeFocus()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var selected = es != null && es.currentSelectedGameObject != null
                ? es.currentSelectedGameObject.name
                : "null";
            return $"want={_wantInputFocus} isFocused={(_input != null && _input.isFocused)} " +
                   $"interactable={(_input != null && _input.interactable)} " +
                   $"eventSystem={(es != null ? es.name : "NULL")} selected={selected}";
        }

        /// <summary>玩家提交了一条消息。参数为去除首尾空白后的文本。</summary>
        public event Action<string> Submitted;

        /// <summary>
        /// 处于"录入 API Key"状态时，用户回车提交的是密钥（而不是聊天消息）。
        /// 密钥**不会**进入聊天记录，也**不会**被写进日志。
        /// </summary>
        public event Action<string> KeySubmitted;

        /// <summary>是否正在等待用户录入 API Key。</summary>
        public bool KeyEntryMode => _keyEntryMode;

        public bool IsOpen { get; private set; }

        /// <summary>GameObject 仍然存活（Unity 的 == null 对已销毁对象返回 true）。</summary>
        public bool Alive => _root != null;

        public bool Created => _root != null;

        /// <summary>
        /// 场景切换后 UIRoot 会重建，挂在它下面的窗口会一起被销毁。
        /// 调用方检测到 !Alive 时调用本方法清掉失效引用，下次 Create 会重新搭建。
        /// </summary>
        public void Reset()
        {
            _root = null;
            _panel = null;
            _content = null;
            _statusText = null;
            _transcript = null;
            _scroll = null;
            _input = null;
            _lines.Clear();
            _committed.Length = 0;
            _current.Length = 0;
            _dirty = false;
            IsOpen = false;
            _wantInputFocus = false;
        }

        // ---------------------------------------------------------------- 构建

        public void Create(UIRoot uiRoot, Font font, PluginConfig config)
        {
            if (_root != null) return;

            // 字号优先取游戏原生面板的字号（创建前已捕获），拿不到再用兜底 14
            var baseSize = UiStyle.Current.HasText ? UiStyle.Current.BaseFontSize : 14;
            _fontSize = Mathf.RoundToInt(baseSize * Mathf.Max(0.5f, config.FontScale));
            _maxLines = Mathf.Max(50, config.MaxTranscriptLines);
            _autoFocusInput = config.AutoFocusInput;

            UiFactory.EnsureEventSystem();

            // 自建 Canvas，而不是挂到游戏的 overlayCanvasTop 下。
            //
            // 原因（实机日志证实）：Overlay Canvas - Top 与它的父级 Always on Top
            // 在游戏中都处于 **inactive** 状态（activeInHierarchy=False），
            // 挂进去的窗口即使 SetActive(true) 也不会被渲染。
            // 自己建一个根 Canvas 可以完全不受游戏画布开关状态的影响。
            // **必须是顶层对象（无父级）**。
            // 把根 Canvas 挂到 UI Root 下面看着无害，但它的 RectTransform 会被
            // 父级变换影响，结果面板的锚点解算到错误的位置（实测表现：
            // panelRect/anchoredPosition 都对，却渲染在屏幕左上角）。
            // 顶层 + DontDestroyOnLoad 才是标准做法。
            var rootRt = UiFactory.Node("IcarusIICanvas", null);
            UnityEngine.Object.DontDestroyOnLoad(rootRt.gameObject);

            var canvas = rootRt.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;      // 盖在游戏所有 UI 之上
            var scaler = rootRt.gameObject.AddComponent<CanvasScaler>();
            CopyScalerSettings(scaler, uiRoot);
            // 必须的：没有 GraphicRaycaster，画布内的 UI 收不到任何指针事件，
            // 输入框点不进去、聊天记录也无法拖动滚动。
            rootRt.gameObject.AddComponent<GraphicRaycaster>();

            _root = rootRt.gameObject;

            // 优先用随 mod 分发的 prefab（可在 Unity 里可视化编辑，见 mod/unity）。
            // 拿不到、或结构不完整时退回代码构建——"少一个资源文件"不该让窗口整个消失。
            var prefabPanel = WindowPrefab.Instantiate(rootRt);
            if (prefabPanel != null && TryWireFromPrefab(prefabPanel, config, font))
            {
                Plugin.Log.LogInfo($"[UI] 窗口来自 prefab（{WindowPrefab.Status}）");
            }
            else
            {
                if (prefabPanel != null)
                {
                    // 结构不完整：销毁半成品，避免留下一个残缺窗口
                    UnityEngine.Object.Destroy(prefabPanel);
                    Plugin.Log.LogWarning("[UI] prefab 结构不完整，回退到代码构建");
                }
                BuildUiInCode(rootRt, config, font);
            }

            SetStatus(Strings.Get("status.disconnected"), _textDim);
            _root.SetActive(false);
        }

        /// <summary>代码构建整棵面板子树（原路径，也是 prefab 缺失时的兜底）。</summary>
        private void BuildUiInCode(RectTransform rootRt, PluginConfig config, Font font)
        {
            // 面板：默认屏幕正中
            _panel = UiFactory.Node("Panel", rootRt);
            ApplyPanelPlacement(_panel, config);
            _panelImage = UiFactory.AddPanel(_panel, DefaultPanelBg);

            // 装饰边框与玻璃高光：必须紧跟在底衬之后创建，
            // 这样它们是靠前的同级子物体（先绘制），不会盖住正文与输入框。
            // 机体信息窗就是「底衬 + border + glass」三层叠加出来的观感。
            var frameRt = UiFactory.Node("Frame", _panel);
            UiFactory.Stretch(frameRt);
            _frameImage = frameRt.gameObject.AddComponent<Image>();
            _frameImage.raycastTarget = false;   // 不抢点击

            var glassRt = UiFactory.Node("Glass", _panel);
            UiFactory.Stretch(glassRt);
            _glassImage = glassRt.gameObject.AddComponent<Image>();
            _glassImage.raycastTarget = false;
            // 点击窗口任意处即可开始输入（比"必须先点输入框"更顺手）
            _panel.gameObject.AddComponent<ClickToFocus>().Target = this;

            BuildHeader(font);
            BuildTranscript(font);
            BuildInputBar(font);
        }

        /// <summary>
        /// 把 prefab 实例接到本类的字段上。
        ///
        /// 契约：prefab 只负责**结构与贴图**，行为（事件、本地化文案、颜色、字体）
        /// 全部由运行期接管——因为 prefab 无法引用本 mod 的类型，
        /// 而颜色与字体需要从游戏原生窗口捕获。
        ///
        /// 返回 false 表示结构不符合预期，调用方会销毁它并回退到代码构建。
        /// </summary>
        private bool TryWireFromPrefab(GameObject panelGo, PluginConfig config, Font font)
        {
            var panel = panelGo.GetComponent<RectTransform>();
            if (panel == null) return false;

            var panelImage = panelGo.GetComponent<Image>();
            var frame = FindImage(panel, "Frame");
            var glass = FindImage(panel, "Glass");
            var header = FindImage(panel, "Header");
            var status = FindText(panel, "Header/Status");
            var scroll = FindComponent<ScrollRect>(panel, "Scroll");
            var content = panel.Find("Scroll/Viewport/Content") as RectTransform;
            var transcript = content != null ? content.GetComponent<Text>() : null;
            var input = FindComponent<InputField>(panel, "InputBar/Input");
            var inputImage = FindImage(panel, "InputBar/Input");

            // 前四个是必需的；缺任意一个都说明 prefab 与代码对不上
            if (panelImage == null || frame == null || glass == null || transcript == null || input == null)
            {
                Plugin.Log.LogWarning(
                    $"[UI] prefab 缺少必需节点：panel={panelImage != null} frame={frame != null} " +
                    $"glass={glass != null} transcript={transcript != null} input={input != null}");
                return false;
            }

            _panel = panel;
            _panelImage = panelImage;
            _frameImage = frame;
            _glassImage = glass;
            _headerImage = header;
            _statusText = status;
            _scroll = scroll;
            _content = content;
            _transcript = transcript;
            _input = input;
            _inputImage = inputImage;

            ApplyPanelPlacement(_panel, config);

            // 行为必须运行期接管：prefab 引用不了本 mod 的类型
            _input.onEndEdit.AddListener(OnInputEndEdit);
            _panel.gameObject.AddComponent<ClickToFocus>().Target = this;

            // 文案走本地化（prefab 里只是占位）
            var title = FindText(panel, "Header/Title");
            if (title != null) title.text = Strings.Get("title");
            var placeholder = FindText(panel, "InputBar/Input/Placeholder");
            if (placeholder != null) placeholder.text = Strings.Get("input.placeholder");

            // 字体与字号：prefab 有意留空字体，这里套用游戏真实字体；
            // 字号按 prefab 里烘焙的值整体缩放，尊重你在 Unity 里的排版。
            var scale = Mathf.Max(0.5f, config.FontScale);
            foreach (var t in panelGo.GetComponentsInChildren<Text>(true))
            {
                if (t.font == null && font != null) t.font = font;
                if (!Mathf.Approximately(scale, 1f))
                {
                    t.fontSize = Mathf.Max(8, Mathf.RoundToInt(t.fontSize * scale));
                }
            }

            return true;
        }

        private static Image FindImage(RectTransform root, string path)
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Image>() : null;
        }

        private static Text FindText(RectTransform root, string path)
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Text>() : null;
        }

        private static T FindComponent<T>(RectTransform root, string path) where T : Component
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<T>() : null;
        }


        /// <summary>
        /// 面板摆放。居中用 (0.5,0.5) 锚点：它只依赖 Canvas 矩形的**中心**，
        /// 对矩形尺寸/分辨率变化最不敏感。
        /// </summary>
        private static void ApplyPanelPlacement(RectTransform panel, PluginConfig config)
        {
            var size = new Vector2(config.PanelWidth, config.PanelHeight);
            var mode = (config.PositionMode ?? "center").Trim();

            if (string.Equals(mode, "bottomRight", StringComparison.OrdinalIgnoreCase))
            {
                UiFactory.AnchoredTo(panel,
                    anchor: new Vector2(1f, 0f),
                    pivot: new Vector2(1f, 0f),
                    offset: new Vector2(-24f, 24f),
                    size: size);
            }
            else
            {
                UiFactory.AnchoredTo(panel,
                    anchor: new Vector2(0.5f, 0.5f),
                    pivot: new Vector2(0.5f, 0.5f),
                    offset: Vector2.zero,
                    size: size);
            }
        }

        private void BuildHeader(Font font)
        {
            var header = UiFactory.Node("Header", _panel);
            UiFactory.TopBar(header, 34f);
            _headerImage = UiFactory.AddPanel(header, DefaultHeaderBg);

            var titleRt = UiFactory.Node("Title", header);
            UiFactory.Stretch(titleRt, left: 12f, top: 0f, right: 260f, bottom: 0f);
            var title = UiFactory.AddLabel(titleRt, font, _fontSize + 2, _accent, TextAnchor.MiddleLeft);
            title.text = Strings.Get("title");

            var statusRt = UiFactory.Node("Status", header);
            UiFactory.Stretch(statusRt, left: 200f, top: 0f, right: 12f, bottom: 0f);
            _statusText = UiFactory.AddLabel(statusRt, font, _fontSize - 1, _textDim, TextAnchor.MiddleRight);
            _statusText.text = string.Empty;
        }

        private void BuildTranscript(Font font)
        {
            var scrollRt = UiFactory.Node("Scroll", _panel);
            UiFactory.Stretch(scrollRt, left: 8f, top: 34f + 6f, right: 8f, bottom: 38f + 6f);

            _scroll = scrollRt.gameObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;

            var viewport = UiFactory.Node("Viewport", scrollRt);
            UiFactory.Stretch(viewport);
            // RectMask2D 比 Mask 轻，且不需要额外材质
            viewport.gameObject.AddComponent<RectMask2D>();

            _content = UiFactory.Node("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = new Vector2(0f, -4f);
            _content.sizeDelta = new Vector2(-20f, 100f);

            _transcript = UiFactory.AddLabel(_content, font, _fontSize, _textMain, TextAnchor.UpperLeft);
            _transcript.horizontalOverflow = HorizontalWrapMode.Wrap;
            _transcript.verticalOverflow = VerticalWrapMode.Overflow;

            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewport;
            _scroll.content = _content;
        }

        private void BuildInputBar(Font font)
        {
            var bar = UiFactory.Node("InputBar", _panel);
            UiFactory.BottomBar(bar, 38f);

            var inputRt = UiFactory.Node("Input", bar);
            UiFactory.Stretch(inputRt, left: 8f, top: 3f, right: 8f, bottom: 5f);
            _inputImage = UiFactory.AddPanel(inputRt, DefaultInputBg);
            var bg = _inputImage;

            var textRt = UiFactory.Node("Text", inputRt);
            UiFactory.Stretch(textRt, left: 8f, top: 0f, right: 8f, bottom: 0f);
            var text = UiFactory.AddLabel(textRt, font, _fontSize, _textMain, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;

            var placeholderRt = UiFactory.Node("Placeholder", inputRt);
            UiFactory.Stretch(placeholderRt, left: 8f, top: 0f, right: 8f, bottom: 0f);
            var placeholder = UiFactory.AddLabel(placeholderRt, font, _fontSize, _textDim, TextAnchor.MiddleLeft);
            placeholder.supportRichText = false;
            placeholder.horizontalOverflow = HorizontalWrapMode.Overflow;
            placeholder.text = Strings.Get("input.placeholder");

            _input = inputRt.gameObject.AddComponent<InputField>();
            _input.textComponent = text;
            _input.placeholder = placeholder;
            _input.targetGraphic = bg;
            _input.lineType = InputField.LineType.SingleLine;
            _input.characterLimit = 2000;
            _input.onEndEdit.AddListener(OnInputEndEdit);
        }

        /// <summary>
        /// 套用从游戏窗口捕获到的样式（贴图/颜色/字号）。
        /// 可在捕获成功后随时调用（游戏窗口在主菜单可能还没实例化）。
        /// </summary>
        public void ApplyStyle(UiStyle style)
        {
            if (style == null || _root == null) return;

            if (style.HasText)
            {
                _textMain = style.TextMain;
                _textDim = style.TextDim;
                _accent = style.Accent;
            }

            if (_panelImage != null) style.ApplyPanel(_panelImage);
            if (_frameImage != null) style.ApplyFrame(_frameImage);
            if (_glassImage != null) style.ApplyGlass(_glassImage);
            if (_headerImage != null) UiStyle.ApplyHeader(_headerImage);
            if (_inputImage != null) style.ApplyInput(_inputImage);

            // 文本控件也要跟着换颜色（它们是创建时就定色的）
            if (_transcript != null) _transcript.color = _textMain;
            if (_statusText != null) _statusText.color = _textDim;
            foreach (var t in _root.GetComponentsInChildren<Text>(true))
            {
                if (t == null || t == _transcript || t == _statusText) continue;
                // 输入框的占位文字用暗色，其余用主色
                t.color = t.gameObject.name == "Placeholder" ? _textDim : _textMain;
            }

            if (style.Font != null)
            {
                foreach (var t in _root.GetComponentsInChildren<Text>(true))
                    if (t != null) t.font = style.Font;
            }
        }

        // ---------------------------------------------------------------- 开关

        public void SetOpen(bool open)
        {
            if (_root == null) return;

            IsOpen = open;
            _root.SetActive(open);
            if (!open) _wantInputFocus = false;

            if (open)
            {
                // 让游戏停止响应移动/建造输入。记录原值以便还原。
                _savedInputing = Game.GameRefs.SetTextInputActive(true);
                UiFactory.EnsureEventSystem();
                // 默认不自动聚焦：这样打开后 R 仍可立即关窗。
                // 需要"开窗即打字"的玩家可在配置里打开 AutoFocusInput。
                // 密钥录入态总是自动聚焦：此时玩家唯一要做的事就是填 Key
                if (_autoFocusInput || _keyEntryMode) FocusInput();
                ScrollToBottom();
            }
            else
            {
                if (_input != null)
                {
                    _input.DeactivateInputField();
                    _input.text = string.Empty;
                }
                // 还原游戏的输入标记
                Game.GameRefs.SetTextInputActive(_savedInputing);
            }
        }

        /// <summary>
        /// 复制游戏主画布的缩放设置，保证我们的界面尺寸与游戏 UI 一致
        /// （不同分辨率/UI 缩放下不会出现过大或过小的面板）。
        /// </summary>
        private static void CopyScalerSettings(CanvasScaler target, UIRoot uiRoot)
        {
            try
            {
                CanvasScaler source = null;
                if (uiRoot.overlayCanvas != null) source = uiRoot.overlayCanvas.GetComponent<CanvasScaler>();
                if (source == null && uiRoot.overlayCanvasTop != null) source = uiRoot.overlayCanvasTop.GetComponent<CanvasScaler>();
                if (source == null)
                {
                    Plugin.Log.LogWarning("未找到游戏的 CanvasScaler，界面缩放将使用 Unity 默认值。");
                    return;
                }

                target.uiScaleMode = source.uiScaleMode;
                target.referenceResolution = source.referenceResolution;
                target.screenMatchMode = source.screenMatchMode;
                target.matchWidthOrHeight = source.matchWidthOrHeight;
                target.referencePixelsPerUnit = source.referencePixelsPerUnit;
                _scalerSource = source.gameObject.name;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"复制 CanvasScaler 失败：{ex.Message}");
            }
        }

        private static string _scalerSource = "default";

        /// <summary>缩放设置来源，供日志确认。</summary>
        public static string ScalerSource => _scalerSource;

        /// <summary>
        /// 把窗口的可见性相关状态拼成一行：任何一个祖先 inactive、
        /// 画布未启用、或面板尺寸为 0，都会导致"创建了但看不见"。
        /// </summary>
        public string DescribeVisibility()
        {
            if (_root == null) return "root=null";

            var sb = new StringBuilder(200);
            sb.Append("activeSelf=").Append(_root.activeSelf);
            sb.Append(" activeInHierarchy=").Append(_root.activeInHierarchy);

            var t = _root.transform.parent;
            int depth = 0;
            while (t != null && depth < 6)
            {
                sb.Append(" < ").Append(t.name).Append("[active=").Append(t.gameObject.activeInHierarchy);
                var canvas = t.GetComponent<Canvas>();
                if (canvas != null)
                {
                    sb.Append(" canvas.enabled=").Append(canvas.enabled)
                      .Append(" order=").Append(canvas.sortingOrder)
                      .Append(" scale=").Append(canvas.scaleFactor.ToString("0.##"));
                }
                var group = t.GetComponent<CanvasGroup>();
                if (group != null)
                    sb.Append(" group.alpha=").Append(group.alpha.ToString("0.##"))
                      .Append(" interactable=").Append(group.interactable);
                sb.Append(']');
                t = t.parent;
                depth++;
            }

            var rootCanvas = _root.GetComponent<Canvas>();
            if (rootCanvas != null)
            {
                sb.Append(" canvasPixelRect=").Append(rootCanvas.pixelRect.width.ToString("0")).Append('x').Append(rootCanvas.pixelRect.height.ToString("0"));
                sb.Append(" canvasScaleFactor=").Append(rootCanvas.scaleFactor.ToString("0.##"));
                sb.Append(" canvasSorting=").Append(rootCanvas.sortingOrder);
            }
            var canvasRt = _root.GetComponent<RectTransform>();
            if (canvasRt != null)
            {
                sb.Append(" canvasRect=").Append(canvasRt.rect.width.ToString("0")).Append('x').Append(canvasRt.rect.height.ToString("0"));
                sb.Append(" canvasPivot=").Append(canvasRt.pivot);
            }
            sb.Append(" screen=").Append(Screen.width).Append('x').Append(Screen.height);

            if (_panel != null)
            {
                sb.Append(" panelRect=").Append(_panel.rect.width.ToString("0")).Append('x').Append(_panel.rect.height.ToString("0"));
                sb.Append(" panelAnchored=").Append(_panel.anchoredPosition);
                // 世界坐标四角：直接反映"到底画在哪里"，是判断位置对错的最终依据
                var corners = new Vector3[4];
                _panel.GetWorldCorners(corners);
                sb.Append(" panelWorldBL=").Append(corners[0].x.ToString("0")).Append(',').Append(corners[0].y.ToString("0"));
                sb.Append(" TR=").Append(corners[2].x.ToString("0")).Append(',').Append(corners[2].y.ToString("0"));
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 内容

        public void SetStatus(string text, Color? color = null)
        {
            if (_statusText == null) return;
            _statusText.text = text ?? string.Empty;
            _statusText.color = color ?? _textDim;
        }

        /// <summary>追加一条完整消息行。</summary>
        public void AppendLine(string speaker, string text)
        {
            if (text == null) return;

            var prefix = string.IsNullOrEmpty(speaker)
                ? string.Empty
                : $"<color=#{ColorUtility.ToHtmlStringRGB(_accent)}>{Escape(speaker)}</color>  ";

            PushLine(prefix + Escape(text));
        }

        /// <summary>追加一行系统提示（灰色）。</summary>
        public void AppendSystem(string text)
        {
            if (text == null) return;
            PushLine($"<color=#{ColorUtility.ToHtmlStringRGB(_textDim)}>{Escape(text)}</color>");
        }

        /// <summary>开始一段流式回复；后续 delta 会拼到同一行。</summary>
        public void BeginStream(string speaker)
        {
            var prefix = string.IsNullOrEmpty(speaker)
                ? string.Empty
                : $"<color=#{ColorUtility.ToHtmlStringRGB(_accent)}>{Escape(speaker)}</color>  ";
            _current.Length = 0;
            _current.Append(prefix);
            _dirty = true;
        }

        public void AppendStreamDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _current.Append(Escape(delta));
            _dirty = true;
        }

        public void EndStream()
        {
            // 注意：这里**不能**走 PushLine(_current.ToString())。
            // PushLine 的第一件事是"把正在流式的行落盘"，也就是把 _current
            // 再落一次，然后才落参数——而参数本身就是 _current 的内容，
            // 于是同一行进 _lines 两遍，窗口里就把一段回答画成两段。
            // 这个写法从 M0 起就在，是"回答重复显示"的真正原因。
            if (_current.Length > 0)
            {
                _lines.Add(_current.ToString());
                _current.Length = 0;
                RebuildCommitted();
            }
            _dirty = true;
        }

        public void ClearTranscript()
        {
            _lines.Clear();
            _committed.Length = 0;
            _current.Length = 0;
            _dirty = true;
        }

        private void PushLine(string line)
        {
            // 先把正在流式的行落盘，保证顺序正确
            if (_current.Length > 0)
            {
                _lines.Add(_current.ToString());
                _current.Length = 0;
            }
            _lines.Add(line);
            RebuildCommitted();
            _dirty = true;
        }

        private void RebuildCommitted()
        {
            // 超出行数上限时丢弃最早的行
            while (_lines.Count > _maxLines) _lines.RemoveAt(0);

            _committed.Length = 0;
            foreach (var line in _lines)
            {
                _committed.Append(line).Append('\n');
            }

            if (_committed.Length > MaxTranscriptChars)
            {
                // 极端情况：整体裁剪，保留尾部
                var overflow = _committed.Length - MaxTranscriptChars;
                _committed.Remove(0, overflow);
            }
        }

        /// <summary>插件卸载时清理（Canvas 是顶层对象，不会被场景切换自动带走）。</summary>
        public void Destroy()
        {
            if (_root != null)
            {
                try { UnityEngine.Object.Destroy(_root); } catch { /* ignore */ }
            }
            Reset();
        }

        // ---------------------------------------------------------------- 每帧

        public void Tick()
        {
            if (_root == null || !_root.activeSelf) return;

            // VFInput 的状态每帧由游戏输入系统重算，单次赋值可能被覆盖，
            // 因此窗口打开期间每帧重申一次；关闭时由 SetOpen 还原原值。
            if (IsOpen) Game.GameRefs.SetTextInputActive(true);

            // Enter 进入输入状态（此时 R 才让位给打字）
            if (IsOpen && !InputFocused && UnityEngine.Input.GetKeyDown(KeyCode.Return))
                FocusInput();

            // 期望聚焦但实际没聚焦 → 重申（游戏可能清空了 EventSystem 的选中对象）
            if (IsOpen && _wantInputFocus && _input != null && !_input.isFocused)
                ApplyFocus();

            if (_dirty)
            {
                _dirty = false;
                Render();
            }
        }

        private void Render()
        {
            if (_transcript == null) return;

            _renderScratch.Length = 0;
            _renderScratch.Append(_committed);
            if (_current.Length > 0) _renderScratch.Append(_current);
            _transcript.text = _renderScratch.ToString();

            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            if (_scroll == null) return;
            // 0 = 底部。即使本帧布局还没刷新，下一帧再设一次即可收敛。
            _scroll.verticalNormalizedPosition = 0f;
        }

        // ---------------------------------------------------------------- 首次配置

        /// <summary>
        /// 进入密钥录入态：输入框切为密码模式（不回显明文），并自动聚焦
        /// ——这是唯一"开窗即聚焦"的场景，因为此时玩家唯一能做的事就是填 Key。
        /// </summary>
        public void EnterKeyEntry(string reason)
        {
            if (_input == null) return;

            var first = !_keyEntryMode;
            _keyEntryMode = true;

            _input.contentType = InputField.ContentType.Password;
            _input.characterLimit = 0;
            _input.text = string.Empty;
            if (_input.placeholder is Text ph) ph.text = Strings.Get("key.placeholder");

            if (first)
            {
                var message = string.IsNullOrEmpty(reason)
                    ? Strings.Get("key.prompt")
                    : reason + "\n" + Strings.Get("key.prompt");
                AppendSystem(message);
            }

            SetStatus(Strings.Get("status.needkey"), _textDim);
            FocusInput();
        }

        /// <summary>退出密钥录入态，恢复正常聊天输入。</summary>
        public void ExitKeyEntry()
        {
            if (_input == null) return;

            var wasEntry = _keyEntryMode;
            _keyEntryMode = false;

            _input.contentType = InputField.ContentType.Standard;
            _input.text = string.Empty;
            if (_input.placeholder is Text ph) ph.text = Strings.Get("input.placeholder");

            if (wasEntry) AppendSystem(Strings.Get("key.saved"));
        }

        // ---------------------------------------------------------------- 输入

        private void OnInputEndEdit(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            // onEndEdit 在失焦时也会触发，只有回车才算提交
            var enter = UnityEngine.Input.GetKeyDown(KeyCode.Return)
                     || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter);
            if (!enter) return;

            var text = value.Trim();
            if (_input != null)
            {
                _input.text = string.Empty;
                // 发送后主动失焦：玩家读完回复可直接按 R 关窗，
                // 想继续追问再按 Enter 即可。
                _input.DeactivateInputField();
            }

            try
            {
                if (_keyEntryMode)
                {
                    // 密钥走独立通道：绝不进聊天记录，绝不写日志
                    KeySubmitted?.Invoke(text);
                }
                else
                {
                    Submitted?.Invoke(text);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"提交失败：{ex}");
            }
        }

        /// <summary>把用户输入里可能破坏富文本的字符转义掉。</summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // 只处理会破坏 <color>/<b> 标签的尖括号；中括号等无需处理。
            return s.IndexOf('<') < 0 ? s : s.Replace("<", "<\u200B");
        }
    }
}
