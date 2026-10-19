using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using IcarusII.Common.Protocol;
using IcarusII.Config;
using IcarusII.Game;
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

    /// <summary>蓝图卡片上的两个动作（协议附录 A.9）。</summary>
    internal enum BlueprintCardAction
    {
        Inspect,
        Paste,
    }

    internal sealed class ChatWindow
    {
        private const int MaxTranscriptChars = 120000;

        /// <summary>气泡最大宽度；超出就封顶换行（与 prefab 里 example-user-1 一致）。</summary>
        private const float MaxBubbleWidth = 260f;

        /// <summary>气泡左右内边距（与 prefab 里 bubble 的 VerticalLayoutGroup 一致），估宽用。</summary>
        private const float BubblePaddingX = 12f;

        // 与 PluginConfig 里 Bind 的默认值保持一致：prefab 路径下如果玩家**没改过**尺寸，
        // 就尊重 prefab 里排好的尺寸（你在 Unity 里调好的版面不该被默认值盖掉）。
        private const float DefaultPanelWidth = 780f;
        private const float DefaultPanelHeight = 520f;

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

        /// <summary>我们自己的画布（正常情况下是游戏 `Overlay Canvas` 下的嵌套 Canvas）。</summary>
        private Canvas _canvas;

        /// <summary>宿主画布（游戏的 `Overlay Canvas`）。为空表示退回顶层 Overlay 兜底。</summary>
        private Canvas _hostCanvas;

        /// <summary>
        /// 宿主画布 1 个单位等于多少像素（= `_hostCanvas.scaleFactor`）。
        /// 我们的面板按"1 单位 = 1 像素"排版，所以面板缩放取它的倒数；
        /// 换算游戏画布里的尺寸（选择器宽度）时也用它。
        /// </summary>
        private float _canvasScale = 1f;

        /// <summary>
        /// 界面来自 prefab（而不是代码构建）。
        ///
        /// prefab 已经把机甲信息窗的外框与信标窗的输入框**烘焙**进去了，
        /// 所以这条路径下 `ApplyStyle` 不再重绘外观，只补字体与正文配色。
        /// </summary>
        private bool _fromPrefab;

        /// <summary>窗口当前是否可见。</summary>
        private bool ViewVisible => _root != null && _root.activeSelf;
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
        private ScrollRect _inputScrollRect;
        private RectTransform _inputViewport;
        private RectTransform _inputContent;
        private bool _caretPrimed;
        private int _iconMapTick;
        private bool _iconMapReady;

        // 游戏版 InputField 的 Caret 是运行时懒创建的；这里缓存反射入口，
        // 在聚焦后主动把 Caret 的材质/可见性补起来（否则有焦点但看不到光标）。
        private static readonly FieldInfo InputCaretRendererField =
            typeof(InputField).GetField("m_CachedInputRenderer",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InputCaretVisibleField =
            typeof(InputField).GetField("m_CaretVisible",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InputAllowInputField =
            typeof(InputField).GetField("m_AllowInput",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InputSetCaretVisibleMethod =
            typeof(InputField).GetMethod("SetCaretVisible",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InputUpdateCaretMaterialMethod =
            typeof(InputField).GetMethod("UpdateCaretMaterial",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InputMarkGeometryDirtyMethod =
            typeof(InputField).GetMethod("MarkGeometryAsDirty",
                BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>消息类型：决定渲染成气泡（用户）、裸文本（助手/系统）还是蓝图卡片。</summary>
        private enum LineKind { Assistant, User, System, Blueprint }

        private struct Line
        {
            public LineKind Kind;
            public string Text;
            /// <summary>所属的一问一答（0 = 系统/欢迎语，不进索引）。</summary>
            public int TurnId;
            /// <summary>这条消息对应的可见对象（普通消息、工具/思考块或蓝图卡片）。</summary>
            public GameObject Go;
            /// <summary>工具/思考块（删除整轮时一起移除）；普通消息为 null。</summary>
            public Block Block;
            /// <summary>蓝图卡片数据（只有 <see cref="LineKind.Blueprint"/> 会填）。</summary>
            public BlueprintCardInfo Card;
        }

        private struct Item
        {
            public GameObject Go;
            public Text Label;
            public LineKind Kind;
            /// <summary>所属的一问一答（0 = 系统/欢迎语）。</summary>
            public int TurnId;
            /// <summary>来自 prefab 示例模板（保留它烘焙的字体/颜色），而不是代码兜底构建。</summary>
            public bool FromPrefab;
        }

        /// <summary>chat-index 里的一条：一个用户提问 + 它的回答。</summary>
        private sealed class TurnEntry
        {
            public int Id;
            public string Text;
            /// <summary>历史回放带来的用户消息时间戳（毫秒）；当场提问为 0。</summary>
            public long Timestamp;
            public GameObject UserGo;
            public GameObject Row;
            public Text Link;
            public ChatIndexRowHover Hover;
        }

        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Item> _items = new List<Item>();

        /// <summary>
        /// 工具块：callId → 输出区里那个可折叠块。工具完成后**原地**更新标题/摘要/正文，
        /// 不会出现“读取中”和“完成”两块。
        /// </summary>
        private readonly Dictionary<string, Block> _toolBlocks =
            new Dictionary<string, Block>(StringComparer.Ordinal);

        /// <summary>当前正在流式输出的思考块（null = 没在思考）。</summary>
        private Block _thinking;
        private readonly StringBuilder _thinkingText = new StringBuilder();

        /// <summary>
        /// 右下角「思考」下拉框（prefab 里挂在 thinking-group/thinking-level；
        /// 旧版直接挂在 tool-bar 下，查找路径两者都兼容）。
        /// </summary>
        private UIComboBox _thinkingCombo;
        private string _thinkingLevel = Protocol.ThinkingHigh;
        private bool _suppressThinkingLevelEvent;

        /// <summary>
        /// 输入栏里的「模型」下拉框（prefab 里挂在 model-group/model-level）。
        /// 选项是 sidecar 的 providers × models 扁平化结果。
        /// </summary>
        private UIComboBox _modelCombo;
        private readonly List<ModelChoice> _modelChoices = new List<ModelChoice>();
        private string _modelProvider = string.Empty;
        private string _modelId = string.Empty;
        private string _modelSignature = string.Empty;
        private bool _suppressModelEvent;
        private ConfigState _pendingModelState;

        private sealed class ModelChoice
        {
            public string Provider;
            public string Model;
            public string Label;
        }

        /// <summary>玩家在聊天窗里切换了模型。参数是 (provider, model)。</summary>
        public event Action<string, string> ModelSelectionChanged;

        /// <summary>当前选中的 provider（空 = 尚未配置）。</summary>
        public string SelectedProvider => _modelProvider;

        /// <summary>当前选中的 model id（空 = 尚未配置）。</summary>
        public string SelectedModel => _modelId;

        /// <summary>工具失败时的标题色。</summary>
        private static readonly Color WarnColor = new Color(1f, 0.47f, 0.32f, 1f);

        /// <summary>
        /// 输出区里的一个可折叠块（思考 / 工具）。
        ///
        /// 结构（运行期创建，样式与 prefab 里的气泡保持同一套配色）：
        ///
        ///   block                VerticalLayoutGroup + 淡底色 + 左侧 2px 强调条
        ///   ├── header           Button（整行可点）+ HorizontalLayoutGroup
        ///   │   ├── chevron      ▾ / ▸
        ///   │   ├── title        “思考” / “工具 · 存档概览”
        ///   │   └── summary      收起时的摘要
        ///   └── body             Text（展开时才激活）
        /// </summary>
        private sealed class Block
        {
            public GameObject Go;
            public Text Chevron;
            public Text Title;
            public LayoutElement TitleLayout;
            public Text Summary;
            public Text Body;
            public bool Expanded;

            /// <summary>工具块用：调用参数摘要（展开时显示）。</summary>
            public string Args;
            public string ToolName;

            /// <summary>展开后显示的原始正文（未做富文本消毒，切换折叠时复用）。</summary>
            public string RawBody;

            /// <summary>工具块可选的截图节点（只有 game_capture_view 会填）。</summary>
            public RectTransform ImageRt;
            public ToolImageSprite ImageOwner;
        }

        /// <summary>正在流式输出的那条消息（下标指向 _items）；-1 表示没有。</summary>
        private readonly StringBuilder _stream = new StringBuilder();
        private int _streamItem = -1;
        private int _itemSeq;

        // prefab 里的 example-* 示例就是消息模板：样式只在 prefab 定义一次，运行期克隆。
        private RectTransform _userTemplate;
        private RectTransform _assistantTemplate;

        // ---- chat-index：左侧提问索引 ----
        private RectTransform _chatIndex;
        private RectTransform _chatIndexContent;
        private RectTransform _chatIndexTemplate;
        private ScrollRect _chatIndexScroll;
        private Text _chatIndexTitle;
        private Text _chatIndexEmpty;
        private readonly List<TurnEntry> _turns = new List<TurnEntry>();
        private int _nextTurnId;
        private int _currentTurnId;
        private int _focusedTurnId;
        private bool _awaitingResponse;

        /// <summary>正文字体：输入框与聊天正文都用它。</summary>
        private Font _bodyFont;

        /// <summary>prefab 里写的输入框提示文案；运行期**不覆盖**它，key 录入结束后还原用。</summary>
        private string _authoredPlaceholder;

        private bool _dirty;
        private bool _suppressAutoScrollOnce;
        private int _fontSize;
        private int _maxLines;

        /// <summary>原生提示层低频自检的帧计数（见 Tick）。</summary>
        private int _tipLayerTick;

        /// <summary>打开窗口前 VFInput.inputing 的原值，关闭时还原。</summary>
        private bool _savedInputing;
        private bool _autoFocusInput;
        private bool _wantInputFocus;
        private bool _keyEntryMode;

        /// <summary>多行输入框按回车后待提交（见 `OnInputNewLine`）。</summary>
        private bool _submitPending;

        /// <summary>本次开窗是否已经补过一次材质重绑（见 RebindInputTextMaterial）。</summary>
        private bool _textMaterialRebound;

        /// <summary>上次看到的输入框内容（用于"第一段文字出现"时补一次材质重绑）。</summary>
        private string _lastInputText;

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
            _caretPrimed = false;
            ApplyFocus();
        }

        /// <summary>取消输入框焦点，把键盘交还窗口（之后 R 即可关窗）。</summary>
        public void BlurInput()
        {
            if (_input == null) return;
            _wantInputFocus = false;
            _caretPrimed = false;
            _input.DeactivateInputField();
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.currentSelectedGameObject == _input.gameObject)
                es.SetSelectedGameObject(null);
        }

        /// <summary>
        /// 游戏 InputField 的 Caret 是懒创建的：首次聚焦时可能只创建了空
        /// CanvasRenderer（材质 null、m_CaretVisible=false），结果输入框有焦点
        /// 但看不见光标。这里补一次材质/可见性，并把光标加粗到 2px。
        /// </summary>
        private void PrimeInputCaret()
        {
            if (_input == null || !_input.isFocused) return;
            try
            {
                if (InputAllowInputField != null
                    && InputAllowInputField.GetValue(_input) is bool allow && !allow)
                    return; // 等 ActivateInputFieldInternal 走完，下一帧再试

                if (_input.caretWidth < 2) _input.caretWidth = 2;
                // 游戏版 InputField 的闪烁协程在聊天窗这条路径上不稳定：
                // 直接关闪烁，保证聚焦期间光标常亮。
                _input.caretBlinkRate = 0f;

                var renderer = InputCaretRendererField?.GetValue(_input) as CanvasRenderer;
                if (renderer != null && renderer.GetMaterial() == null)
                    InputUpdateCaretMaterialMethod?.Invoke(_input, null);

                var visible = InputCaretVisibleField != null
                              && InputCaretVisibleField.GetValue(_input) is bool v && v;
                if (!visible)
                {
                    InputSetCaretVisibleMethod?.Invoke(_input, null);
                    InputMarkGeometryDirtyMethod?.Invoke(_input, null);
                    _input.ForceLabelUpdate();
                }

                var nowVisible = InputCaretVisibleField != null
                                 && InputCaretVisibleField.GetValue(_input) is bool nv && nv;
                if (nowVisible) _caretPrimed = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"补输入光标失败：{ex.Message}");
            }
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
        /// 玩家点了蓝图卡片上的动作。参数是卡片数据 + 动作类型；
        /// 真正的游戏 API 调用由 <see cref="IcarusII.Game.BlueprintActions"/> 完成。
        /// </summary>
        public event Action<BlueprintCardInfo, BlueprintCardAction> BlueprintCardActionRequested;

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
            _canvas = null;
            _hostCanvas = null;
            _canvasScale = 1f;
            _panel = null;
            _content = null;
            _statusText = null;
            _transcript = null;
            _scroll = null;
            _input = null;
            _fromPrefab = false;
            _lines.Clear();
            _items.Clear();
            _toolBlocks.Clear();
            _thinking = null;
            _thinkingText.Length = 0;
            _thinkingCombo = null;
            _suppressThinkingLevelEvent = false;
            _stream.Length = 0;
            _streamItem = -1;
            _userTemplate = null;
            _assistantTemplate = null;
            _chatIndex = null;
            _chatIndexContent = null;
            _chatIndexTemplate = null;
            _chatIndexScroll = null;
            _chatIndexTitle = null;
            _chatIndexEmpty = null;
            _turns.Clear();
            _nextTurnId = 0;
            _currentTurnId = 0;
            _focusedTurnId = 0;
            _bodyFont = null;
            _authoredPlaceholder = null;
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
            _thinkingLevel = PluginConfig.IsValidThinkingLevel(config.ThinkingLevel)
                ? config.ThinkingLevel.ToLowerInvariant()
                : Protocol.ThinkingHigh;
            // 正文字体：两条构建路径（prefab / 代码兜底）都要有，Text 留空会 NRE
            _bodyFont = font;

            UiFactory.EnsureEventSystem();

            // 画布：嵌进游戏的 Overlay Canvas（原因见 CreateCanvas 的注释）。
            // 旧版自建顶层 Overlay 画布，和游戏的 ScreenSpaceCamera 坐标系对不上，
            // 原生图标/取色器会被丢到屏幕角落、并被窗口盖住。
            var rootRt = CreateCanvas(uiRoot);
            _root = rootRt.gameObject;

            // 原生提示（按钮/物品/弹出菜单）默认和游戏 HUD 同一层，会被本窗口（30000）切掉。
            // 这里把提示容器抬到窗口之上，提示才不会出现"从中间被切断"的观感。
            NativeTipLayer.Ensure(uiRoot);

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

            ApplyHostScaleAndLayer();
            EnsureChatIndex();
            ApplySoftTextMaterials();

            // 窗口可能是在收到 save.bound/欢迎语之后才第一次打开的：
            // 那时消息只进了 _lines，还没有可用的 _content。这里统一补建可见项。
            RebuildTranscript();

            SetStatus(Strings.Get("status.disconnected"), _textDim);
            _root.SetActive(false);
        }

        /// <summary>
        /// 建聊天窗的画布。
        ///
        /// **必须嵌进游戏的 `Overlay Canvas`**，不能像旧版那样自建顶层 Overlay 画布。
        ///
        /// 实机证据：游戏 UI 是 `ScreenSpaceCamera`（UI Camera + UI Root 缩放 0.01），
        /// 顶层 Overlay 画布的世界坐标却直接就是像素。两套坐标混用时
        /// `TransformPoint/InverseTransformPoint` 会被放大约 100 倍——图标选择器
        /// 被丢到屏幕右上角（日志 `pos=(792,442)`，正是夹取上界），
        /// 而且顶层画布永远盖在原生弹窗（图标/取色器）之上，看起来就是"点了没反应"。
        ///
        /// 嵌进同一棵画布树后：坐标系一致（选择器定位、点击判定都对），
        /// 我们 30000 与选择器的 30001 是同一根 Canvas 里的嵌套 Canvas，排序也正确。
        ///
        /// 代价：游戏画布 1 单位 = `scaleFactor` 像素（1080p 是 1，900p 是 0.833），
        /// 而 prefab 是按像素排版的，所以面板缩放取 `1/scaleFactor`，
        /// 屏幕上的尺寸/字号与改造前完全一致。
        /// </summary>
        private RectTransform CreateCanvas(UIRoot uiRoot)
        {
            Canvas host = null;
            try { if (uiRoot != null) host = uiRoot.overlayCanvas; } catch { /* 反射受限时忽略 */ }

            var rootRt = UiFactory.Node("IcarusIICanvas", host != null ? host.transform : null);
            var canvas = rootRt.gameObject.AddComponent<Canvas>();
            _canvas = canvas;

            if (host != null)
            {
                // 嵌套画布：拉伸铺满宿主，靠 overrideSorting 排到游戏 UI 之上
                rootRt.anchorMin = Vector2.zero;
                rootRt.anchorMax = Vector2.one;
                rootRt.pivot = new Vector2(0.5f, 0.5f);
                rootRt.anchoredPosition = Vector2.zero;
                rootRt.sizeDelta = Vector2.zero;
                rootRt.localScale = Vector3.one;

                canvas.overrideSorting = true;
                canvas.sortingOrder = 30000;      // 盖在游戏 UI 之上
                // 文字/图标的 UV1 等额外通道必须跟宿主一致，否则游戏字体材质会花
                canvas.additionalShaderChannels = host.additionalShaderChannels;

                _hostCanvas = host;
                _canvasScale = Mathf.Max(0.01f, host.scaleFactor);
                // ScreenSpaceCamera 画布由 UI Camera 渲染，图形必须落在它的 culling mask 里
                rootRt.gameObject.layer = host.gameObject.layer;
            }
            else
            {
                // 宿主不可用：退回顶层 Overlay 画布（世界坐标=像素，scaleFactor=1）
                Plugin.Log.LogWarning("[UI] 拿不到游戏的 Overlay Canvas，退回顶层 Overlay 画布。");
                UnityEngine.Object.DontDestroyOnLoad(rootRt.gameObject);
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;
                var scaler = rootRt.gameObject.AddComponent<CanvasScaler>();
                CopyScalerSettings(scaler, uiRoot);
                _canvasScale = 1f;
            }

            // 必须的：没有 GraphicRaycaster，画布内的 UI 收不到任何指针事件，
            // 输入框点不进去、聊天记录也无法拖动滚动。
            rootRt.gameObject.AddComponent<GraphicRaycaster>();

            return rootRt;
        }

        /// <summary>
        /// 把面板排成"1 单位 = 1 像素"，并把整棵窗口的图层对齐到宿主画布。
        ///
        /// 尺寸缩放是分辨率的函数：游戏改了 UI 缩放/分辨率后 `scaleFactor` 会变，
        /// 所以要能重复调用（Tick 里检测到变化会再调一次）。
        /// </summary>
        private void ApplyHostScaleAndLayer()
        {
            if (_root == null || _panel == null) return;

            if (_hostCanvas != null)
            {
                _canvasScale = Mathf.Max(0.01f, _hostCanvas.scaleFactor);
                _panel.localScale = new Vector3(1f / _canvasScale, 1f / _canvasScale, 1f);
                SetLayerRecursively(_root.transform, _hostCanvas.gameObject.layer);
            }
            else
            {
                _canvasScale = 1f;
                _panel.localScale = Vector3.one;
            }
        }

        private static void SetLayerRecursively(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursively(t.GetChild(i), layer);
        }

        /// <summary>代码构建整棵面板子树（原路径，也是 prefab 缺失时的兜底）。</summary>
        private void BuildUiInCode(RectTransform rootRt, PluginConfig config, Font font)
        {
            _fromPrefab = false;

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
        /// prefab 结构（见 `mod/unity/Editor/IcarusIIWindowPrefabBuilder.cs`，
        /// 外观全部**复制自游戏原生窗口**，不是自己设计的）：
        ///
        ///   IcarusIIWindow                根（没有 Image）
        ///   ├── shadow                    机甲信息窗的投影
        ///   ├── panel-bg                  机甲信息窗的底衬
        ///   │   ├── border / glass / deco 机甲信息窗的外框装饰（glass、deco 原生就是关的）
        ///   │   └── drag-trigger          透明收射线的点击区
        ///   ├── Header/{Title,Status}
        ///   ├── Scroll/Viewport/Content
        ///   └── InputBar/Input            信标窗的输入框
        ///
        /// 契约：prefab 只负责**结构与贴图**，行为（事件、本地化文案）与**字体**
        /// 仍由运行期接管——prefab 无法引用本 mod 的类型，而字体必须是
        /// 游戏当前语言那个（prefab 里的字体只是占位，见生成脚本注释）。
        ///
        /// 每项都给"新结构 → 旧结构"两条路径，兼容手工改过的 prefab。
        /// 返回 false 表示结构不符合预期，调用方会销毁它并回退到代码构建。
        /// </summary>
        private bool TryWireFromPrefab(GameObject panelGo, PluginConfig config, Font font)
        {
            var panel = panelGo.GetComponent<RectTransform>();
            if (panel == null) return false;

            var panelImage = FindImage(panel, "panel-bg");
            if (panelImage == null) panelImage = panelGo.GetComponent<Image>();

            var frame = FindImage(panel, "panel-bg/border", "border", "Frame");
            var glass = FindImage(panel, "panel-bg/glass", "glass", "Glass");
            var header = FindImage(panel, "Header");
            var status = FindText(panel, "Header/Status",
                "Header/title-status", "panel-bg/status-text", "status-text", "Status");

            // 正文区：优先新结构 output/scroll-view/{viewport/content}，
            // 再兼容旧结构（单块 Scroll/Viewport/Content 文本）。
            var scroll = FindComponent<ScrollRect>(panel,
                "output/scroll-view", "Scroll", "scroll-view", "transcript");
            var content = FindNode(panel,
                "output/scroll-view/viewport/content",
                "Scroll/Viewport/Content", "Viewport/Content", "Content",
                "transcript/Viewport/Content", "transcript/content", "content") as RectTransform;
            // content 现在是 VerticalLayoutGroup 容器（一条消息一个子节点），不再是单个 Text。
            var transcript = content != null ? content.GetComponent<Text>() : null;

            // 输入框可能被放在 editor 的 scroll-view 里（新 prefab 结构）：
            //   input/editor/scroll-view/viewport/content/InputField/input
            // 不能只找 "input"——那是整条输入栏，自身没有 InputField 组件；
            // 否则会误抓到 toolbar 里下拉框内部的 Main InputField（打字只进一个字符、
            // 焦点每帧被抢回下拉框，就是这条路径造成的）。
            var inputTf = FindNode(panel,
                "input/editor/scroll-view/viewport/content/InputField/input",
                "editor/scroll-view/viewport/content/InputField/input",
                "InputBar/Input", "input", "InputBar", "input-bar", "Input");
            var input = inputTf != null ? inputTf.GetComponent<InputField>() : null;
            if (input == null)
            {
                // 只接受**当前激活**的输入框；组合框内部的 Main InputField 是隐藏的，
                // 不能拿它当聊天输入框。
                foreach (var candidate in panel.GetComponentsInChildren<InputField>(true))
                {
                    if (!candidate.gameObject.activeInHierarchy) continue;
                    if (IsInsideToolbar(candidate.transform, panel)) continue;
                    input = candidate;
                    break;
                }
            }
            var inputImage = input != null ? input.GetComponent<Image>() : null;

            // scroll-view 的位置/大小完全由 prefab 决定（底边距 37、工具栏 36，
            // 正好 1px）。这里只记录 viewport/content，供文字被 ScrollRect 贴底时
            // 做内容高度校正；不再改 scroll-view 的 anchors/offset。
            var inputScroll = FindNode(panel, "input/editor/scroll-view", "editor/scroll-view") as RectTransform;
            if (inputScroll != null)
            {
                // 输入区 content 的高度原本只有 40（小于 viewport），ScrollRect 会把它
                // 贴到 viewport 底部，文字就跑到 toolbar 那一行。撑到 viewport 高度并
                // 归位到顶部，文字才落在输入区里。
                var inputViewport = inputScroll.Find("viewport") as RectTransform;
                var inputContent = inputViewport != null
                    ? inputViewport.Find("content") as RectTransform
                    : null;
                if (inputViewport != null && inputContent != null)
                {
                    _inputScrollRect = inputScroll.GetComponent<ScrollRect>();
                    _inputViewport = inputViewport;
                    _inputContent = inputContent;
                }
            }

            // 前四个必需；对不上就退回代码构建的界面
            if (panelImage == null || frame == null || glass == null || input == null)
            {
                Plugin.Log.LogWarning(
                    $"[UI] prefab 缺少必需节点：panel={panelImage != null} frame={frame != null} " +
                    $"glass={glass != null} input={input != null}");
                return false;
            }

            if (content == null)
            {
                Plugin.Log.LogWarning("[UI] prefab 缺少正文容器 output/scroll-view/viewport/content");
                return false;
            }
            if (scroll == null)
            {
                scroll = content.GetComponentInParent<ScrollRect>();
            }

            _fromPrefab = true;
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

            // 消息模板：content 里的 example-* 就是样式样板，运行期克隆它们。
            CaptureTemplates(content);

            // prefab 路径：配置没自定义尺寸时用 prefab 里排好的尺寸
            ApplyPanelPlacement(_panel, config, keepAuthoredSize: true);

            // 右上角关闭按钮：点击 = 关窗（与按 R 等价）。
            // 按钮本体是从游戏的"单按钮窗口"（通讯器/复制器…）抄来的，
            // 点击行为在 prefab 里接不了（引用不了本 mod 的类型），所以运行期补上。
            var closeButton = FindComponent<Button>(panel,
                "panel-bg/btn-box/close-wnd-btn", "btn-box/close-wnd-btn", "close-wnd-btn");
            if (closeButton != null)
            {
                closeButton.onClick.AddListener(OnCloseButtonClicked);
                if (closeButton.GetComponent("UIButton") == null)
                    Plugin.Log.LogWarning("关闭按钮缺少原生 UIButton，悬停/音效不可用");
            }
            else
            {
                Plugin.Log.LogWarning("prefab 里没有关闭按钮（panel-bg/btn-box/close-wnd-btn），只能按 R 关窗。");
            }

            // 右上角设置按钮（关闭按钮左边）：打开游戏原生设置窗口，并切到"模型"页签
            WireSettingsButton(panel);

            // 发送按钮（tool-bar 右侧那个"发送"）：点击 = 提交当前输入
            var sendButton = FindComponent<Button>(panel,
                "input/editor/tool-bar/send-button", "tool-bar/send-button", "send-button", "SendButton");
            if (sendButton != null)
            {
                sendButton.onClick.AddListener(SubmitPendingInput);
            }
            else
            {
                Plugin.Log.LogInfo("prefab 里没有发送按钮，改用 Enter 发送（Ctrl+Enter 换行）。");
            }

            // 工具条：插入图标 / 复选框 / 文字颜色 —— 全部调**游戏自己的** API
            // （照原生 UISmartInputField 的做法），所以弹窗、图标表、调色板都和原生一致。
            WireInputToolbar(panel);

            // 右下角「思考等级」下拉框：用游戏自己的 UIComboBox（prefab 里已放好节点）。
            WireThinkingLevelCombo(panel);
            WireModelCombo(panel);

            // ── 从原生窗口抄来时缺的两处运行期初始化（不改你的 prefab）──

            // 1) UIWindowDrag.screenRect 在 prefab 里是空的 → OnPointerDown/Update 里
            //    screenRect.rect 会 NRE（点一下窗口就弹"游戏运行错误"红条，拖拽也失效）。
            var windowDrag = panelGo.GetComponent<UIWindowDrag>();
            if (windowDrag != null && windowDrag.screenRect == null)
            {
                var parentCanvas = panelGo.GetComponentInParent<Canvas>();
                windowDrag.screenRect = parentCanvas != null ? parentCanvas.transform as RectTransform : panel;
                Plugin.Log.LogInfo("[UI] 已补上 UIWindowDrag.screenRect（窗口可拖拽，且不再抛 NRE）。");
            }

            // 2) 输入框的"彩色文本"调色板：原生窗口脚本会在运行时给它赋值
            //    （UIMarkerDesc 里那句 m_CharColorsMapping = Configs.builtin.colorPaletteText），
            //    我们这份没人赋 → 一打字 CalculateColorText 就越界（IndexOutOfRangeException）。
            //
            //    m_CharColors 是**逐字符**的调色板下标，负数 = 跟随 Text 自身颜色
            //    （CalculateColorText 里 `m_CharColors[i] < 0 -> Color.clear -> 不写 <color> 标签`）。
            //    必须预填 -1：0 是调色板 0 号色（alpha≈0.157），会被包成
            //    `<color=#FFFFFF28>…`，再乘上 Text.color 的 0.322 → 约 5% alpha，
            //    配上 widget-text-alpha-5x-thick（_Sharp=0.5）就是"字确实塞进去了但什么都看不见"。
            //
            //    ⚠️ 只在这里种一次是不够的：`InputField.OnDisable` 里有
            //    `Array.Clear(m_CharColors, 0, len)`，而聊天窗是 SetActive 开关的，
            //    所以**每次关窗都会把整张表清成 0**。因此开窗时（ApplyOpenState）要重种一次。
            SeedInputCharColors(input, "建窗");
            RebindInputTextMaterial(input.textComponent, "建窗");

            // 正文 Text 也要能画游戏图标标签（\xxx;）：借用输入框那份 iconMapping。
            // 输入框是从原生窗口抄的，iconMapping 已经是游戏自己的表。
            if (_transcript != null && _input.textComponent != null)
            {
                _transcript.iconMapping = _input.textComponent.iconMapping;
                _transcript.supportRichText = true;
            }

            // 行为必须运行期接管：prefab 引用不了本 mod 的类型
            _input.onEndEdit.AddListener(OnInputEndEdit);
            // 抄自信标窗待办框的输入框是**多行**的：回车不会走 onEndEdit，
            // 而是走游戏改写版 InputField 的 OnNewLine，所以这里单独接一发。
            _input.OnNewLine += OnInputNewLine;
            _panel.gameObject.AddComponent<ClickToFocus>().Target = this;

            // 文案走本地化。标题：prefab 里已经写了字的就**尊重它**（例如手工外框里写了
            // 「伊卡洛斯终端」），只有空标题才填本地化的默认标题。
            var title = FindText(panel, "Header/Title", "Header/title-text", "panel-bg/title-text", "title-text");
            if (title != null && string.IsNullOrEmpty(title.text)) title.text = Strings.Get("title");
            // 输入框提示**尊重 prefab 里写的文案**，运行期不改它。
            // （key 录入态会临时换成 key.placeholder，退出时还原成这里记下的原文。）
            var placeholder = input.transform.Find("Placeholder");
            if (placeholder != null)
            {
                var ph = placeholder.GetComponent<Text>();
                if (ph != null) _authoredPlaceholder = ph.text;
            }

            // 字体：prefab 里填的是内置 Arial 占位（原生字体 SAIRAB/SAIRASB 不含中文），
            // 这里**无条件**换成游戏当前语言的字体——中文能不能显示全靠这一步。
            // 字号按 prefab 里烘焙的值整体缩放，尊重你在 Unity 里的排版。
            var scale = Mathf.Max(0.5f, config.FontScale);
            _bodyFont = font;
            foreach (var t in panelGo.GetComponentsInChildren<Text>(true))
            {
                if (font != null) t.font = font;
                if (!Mathf.Approximately(scale, 1f))
                {
                    t.fontSize = Mathf.Max(8, Mathf.RoundToInt(t.fontSize * scale));
                }
            }
            // 输入框与正文统一用正文字体（prefab 里烘焙的是 SAIRAR/SAIRASB 与内置 Arial 占位）
            var inputLabel = _input != null ? _input.textComponent : null;
            if (inputLabel != null) ApplyBodyFont(inputLabel);
            if (_input != null && _input.placeholder is Text inputPh) ApplyBodyFont(inputPh);


            Plugin.Log.LogInfo($"[UI] 正文字体 = {(font != null ? font.name : "null")}"
                               + $"（来源 {FontProvider.Source}{(string.IsNullOrEmpty(FontProvider.Detail) ? "" : "；" + FontProvider.Detail)}）"
                               + "，输入框与正文共用。");

            return true;
        }

        /// <summary>候选输入框是否位于工具栏（下拉框内部）。</summary>
        private static bool IsInsideToolbar(Transform node, RectTransform panel)
        {
            for (var t = node; t != null && t != panel; t = t.parent)
                if (t.name == "tool-bar") return true;
            return false;
        }

        /// <summary>按给定路径依次查找（取第一个命中的）。</summary>
        private static Transform FindNode(RectTransform root, params string[] paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var t = root.Find(path);
                if (t != null) return t;
            }
            return null;
        }

        private static Image FindImage(RectTransform root, params string[] paths)
        {
            var t = FindNode(root, paths);
            return t != null ? t.GetComponent<Image>() : null;
        }

        private static Text FindText(RectTransform root, params string[] paths)
        {
            var t = FindNode(root, paths);
            return t != null ? t.GetComponent<Text>() : null;
        }

        /// <summary>按路径找组件；路径都没命中时退到"整棵子树里唯一一个"。</summary>
        private static T FindComponent<T>(RectTransform root, params string[] paths) where T : Component
        {
            var t = FindNode(root, paths);
            if (t != null) return t.GetComponent<T>();
            return root.GetComponentInChildren<T>(true);
        }


        /// <summary>
        /// 面板摆放。居中用 (0.5,0.5) 锚点：它只依赖 Canvas 矩形的**中心**，
        /// 对矩形尺寸/分辨率变化最不敏感。
        /// </summary>
        private static void ApplyPanelPlacement(RectTransform panel, PluginConfig config, bool keepAuthoredSize = false)
        {
            var size = new Vector2(config.PanelWidth, config.PanelHeight);
            var mode = (config.PositionMode ?? "center").Trim();

            // prefab 路径：玩家没动过配置（还是默认值）时，保留 prefab 里排好的尺寸。
            if (keepAuthoredSize
                && Mathf.Approximately(config.PanelWidth, DefaultPanelWidth)
                && Mathf.Approximately(config.PanelHeight, DefaultPanelHeight))
            {
                var authored = panel.sizeDelta;
                if (authored.x > 1f && authored.y > 1f)
                {
                    size = authored;
                    Plugin.Log.LogInfo($"[UI] 使用 prefab 里的窗口尺寸 {authored.x:0}x{authored.y:0}（配置未自定义）。");
                }
            }

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
            CreateTranscript(_panel, font, autoFit: false);
        }

        /// <summary>
        /// 建正文区：`Scroll` → `Viewport`(RectMask2D) → `Content`(Text + ContentSizeFitter)。
        /// 代码构建路径与"prefab 没给正文节点"的运行期补齐都走这里。
        ///
        /// <paramref name="autoFit"/>=true 时按窗口里**已有的**标题与输入框实际位置把正文夹在中间，
        /// 这样你在 Unity 里排好的版面会被尊重，而不是套一组写死的边距。
        /// </summary>
        private void CreateTranscript(RectTransform parent, Font font, bool autoFit)
        {
            var scrollRt = UiFactory.Node("Scroll", parent);
            if (autoFit)
            {
                UiFactory.Stretch(scrollRt, left: 20f, top: 64f, right: 20f, bottom: 110f);
                AutoFitTranscript(scrollRt, parent);
            }
            else
            {
                UiFactory.Stretch(scrollRt, left: 8f, top: 34f + 6f, right: 8f, bottom: 38f + 6f);
            }

            _scroll = scrollRt.gameObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;

            var viewport = UiFactory.Node("Viewport", scrollRt);
            UiFactory.Stretch(viewport);
            // RectMask2D 比 Mask 轻，且不需要额外材质
            viewport.gameObject.AddComponent<RectMask2D>();
            // 滚轮/拖拽需要一块 raycast 目标：正文 Text 是 raycastTarget=false，
            // 视口又只有 RectMask2D（不是图形）→ ScrollRect 收不到事件，正文就"滚不动"。
            // 补一块全透明 Image 当命中区（原生待办框的 viewport 也是这个套路）。
            var viewportHit = viewport.gameObject.AddComponent<Image>();
            viewportHit.color = new Color(0f, 0f, 0f, 0f);
            viewportHit.raycastTarget = true;

            _content = UiFactory.Node("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = new Vector2(0f, -4f);
            _content.sizeDelta = new Vector2(-20f, 100f);

            // 代码兜底路径也用"消息条目"模型：容器挂 VLG + CSF，消息由 CreateItem 逐个创建
            var vlg = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 8f;
            vlg.padding = new RectOffset(4, 4, 4, 4);

            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewport;
            _scroll.content = _content;
        }

        /// <summary>
        /// 用现有节点实测位置收紧正文区：上边贴标题下沿、下边贴输入框上沿。
        /// 量不到就保留调用方给的兜底边距（所以不会把版面弄崩）。
        /// </summary>
        private static void AutoFitTranscript(RectTransform scrollRt, RectTransform panel)
        {
            try
            {
                Canvas.ForceUpdateCanvases();

                var panelTop = panel.rect.yMax;
                var panelBottom = panel.rect.yMin;

                var top = panelTop - 64f;          // 兜底：让开标题
                var bottom = panelBottom + 110f;   // 兜底：让开输入区

                foreach (var t in panel.GetComponentsInChildren<Text>(true))
                {
                    if (t.gameObject.name.ToLowerInvariant().IndexOf("title") < 0) continue;
                    var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(panel, t.rectTransform);
                    top = bounds.min.y - 8f;
                    break;
                }

                var field = panel.GetComponentInChildren<InputField>(true);
                if (field != null)
                {
                    var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(panel, field.transform as RectTransform);
                    bottom = bounds.max.y + 8f;
                }

                if (top - bottom < 40f)   // 量出来太扁（比如还没布局）→ 回兜底
                {
                    top = panelTop - 64f;
                    bottom = panelBottom + 110f;
                }

                // offsetMax.y 取负表示"距父级上沿的距离"，offsetMin.y 表示"距父级下沿的距离"
                scrollRt.offsetMax = new Vector2(scrollRt.offsetMax.x, -(panelTop - top));
                scrollRt.offsetMin = new Vector2(scrollRt.offsetMin.x, bottom - panelBottom);

                Plugin.Log.LogInfo($"[UI] 正文区自动贴合：高 {scrollRt.rect.height:0}px。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"正文区自动贴合失败，使用兜底边距：{ex.Message}");
            }
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
            _authoredPlaceholder = placeholder.text;

            _input = inputRt.gameObject.AddComponent<InputField>();
            _input.textComponent = text;
            _input.placeholder = placeholder;
            _input.targetGraphic = bg;
            _input.lineType = InputField.LineType.SingleLine;
            _input.characterLimit = 2000;
            _input.onEndEdit.AddListener(OnInputEndEdit);
        }

        /// <summary>
        /// 套用从游戏窗口捕获到的样式。
        /// 可在捕获成功后随时调用（游戏窗口在主菜单可能还没实例化）。
        ///
        /// **窗口外观（贴图/颜色/材质）只在代码构建的路径上套用**：
        /// prefab 已经把机甲信息窗的外框与信标窗的输入框烘焙进去了，
        /// 运行期不再重画一遍——那套"按捕获/启发式重绘窗口"的做法已弃用。
        /// 两条路径都需要的只有**字体**（prefab 里只是占位）与
        /// **正文/状态栏的文字配色**（属于聊天内容，不属于窗口外观）。
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

            // 字体：只替换占位字体（Arial）；prefab 里手工指定的游戏字体保留。
            if (style.Font != null) _bodyFont = style.Font;

            // 所有文字统一换成柔化材质（不改游戏共享材质）：prefab 烘焙的 Text 也要换，
            // 否则只有运行期新建的正文变清晰，旧气泡/标题仍然一圈大光晕。
            ApplySoftTextMaterials();
            if (style.Font != null)
            {
                foreach (var t in _root.GetComponentsInChildren<Text>(true))
                    ApplyBodyFont(t);
            }

            // 正文与状态栏的文字颜色（聊天内容配色，不是窗口外观）。
            // 用户气泡与 prefab 助手正文保留 prefab 颜色；只给系统行和代码兜底项上色。
            var generalIcons = TryGetPreferredIconMapping();
            foreach (var item in _items)
            {
                if (item.Label == null) continue;
                if (item.Kind == LineKind.System) item.Label.color = _textDim;
                else if (item.Kind == LineKind.Assistant && !item.FromPrefab)
                    item.Label.color = _textMain;
                // 早期创建的消息可能还没等到 IconSet 建表，或被塞了输入框那份
                // 不完整的表，这里每次样式刷新都用全局表覆盖一遍。
                AssignIconMapping(item.Label, generalIcons);
            }
            if (_statusText != null) _statusText.color = _textDim;
            if (_chatIndexTitle != null) _chatIndexTitle.color = _accent;
            if (_chatIndexEmpty != null) _chatIndexEmpty.color = _textDim;
            RefreshIndexSelection();

            if (_fromPrefab)
            {
                return;
            }

            // ---- 以下只服务代码构建的兜底界面 ----
            if (_panelImage != null) style.ApplyPanel(_panelImage);
            if (_frameImage != null) style.ApplyFrame(_frameImage);
            if (_glassImage != null) style.ApplyGlass(_glassImage);
            if (_headerImage != null) UiStyle.ApplyHeader(_headerImage);
            if (_inputImage != null) style.ApplyInput(_inputImage);

            // 文本控件也要跟着换颜色（它们是创建时就定色的）
            foreach (var t in _root.GetComponentsInChildren<Text>(true))
            {
                if (t == null || t == _transcript || t == _statusText) continue;
                // 输入框的占位文字用暗色，其余用主色
                t.color = t.gameObject.name == "Placeholder" ? _textDim : _textMain;
            }
        }

        // ---------------------------------------------------------------- 开关

        public void SetOpen(bool open)
        {
            if (_root == null) return;
            if (open && !GameRefs.IsInGame)
            {
                Plugin.Log.LogInfo("[UI] 不在游戏内，拒绝打开聊天窗");
                return;
            }

            IsOpen = open;
            _root.SetActive(open);
            if (!open) { _wantInputFocus = false; _textMaterialRebound = false; }

            ApplyOpenState(open);
            if (open) EnsureIconMappings();
        }

        /// <summary>
        /// 把输入框的逐字符颜色表整表重置为 -1（跟随 Text 自身颜色）。
        ///
        /// 为什么要反复调：`InputField.OnDisable` 里有 `Array.Clear(m_CharColors)`，
        /// 而聊天窗是 SetActive 开关的 → 每次关窗后整张表都是 0；
        /// 0 指向调色板 0 号色（alpha≈0.157），CalculateColorText 会给整段文字包上
        /// `&lt;color=#FFFFFF28&gt;` 标签，再乘 Text.color 就几乎全透明——
        /// 表现就是"输入框里打字，text 变了、光标在，但一个字形都看不见"。
        /// </summary>
        private static void SeedInputCharColors(InputField f, string why)
        {
            if (f == null) return;
            try
            {
                // 调色板必须**无条件**赋值：prefab 里这个字段是"非 null 的空数组"，
                // 只判断 null 会漏掉——空数组会让 CalculateColorText 静默跳过所有 <color> 标签
                // （取色器选了色也不生效），而且真的带色时会越界。
                var palette = Configs.builtin.colorPaletteText;
                if (palette != null && palette.Length > 0)
                    f.m_CharColorsMapping = palette;

                int need = Mathf.Max(256, (f.text != null ? f.text.Length : 0) + 128);
                var colors = f.m_CharColors;
                if (colors == null || colors.Length < need) colors = new short[need];
                for (int i = 0; i < colors.Length; i++) colors[i] = -1;
                f.m_CharColors = colors;

                Plugin.Log.LogInfo($"[UI] 输入框逐字符颜色已重置为默认色（{colors.Length} 槽，{why}）。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"输入框颜色表重置失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 强制输入框的 Text 重新绑一次材质（连同字体图集纹理）。
        ///
        /// 这是"输入框里有字、光标在，但一个字形都画不出来"的**直接原因**：
        /// prefab 路径上 `input-text` 的 Graphic 材质脏标记从没被处理过，
        /// CanvasRenderer 里 materialCount=1 但材质是 **null** →
        /// `UI Ex/Text Alpha` 采样不到 `_MainTex`（字体图集），
        /// `col.a *= 0` → `clip(col.a - 0.001)` 把每个像素都丢掉，屏幕上自然什么都没有。
        /// 症状极具迷惑性：text 正常、位置正常、生成器也产出了顶点，就是全被 clip 掉。
        ///
        /// 处理顺序见方法体三步：置脏 → 直接按 UpdateMaterial 的做法绑定 → 强制一次
        /// canvas 更新。三步缺一不可（前两步单独做都不会生效，实测）。
        /// </summary>
        private static void RebindInputTextMaterial(Text t, string why)
        {
            if (t == null) return;
            try
            {
                // ① 让 uGUI 自己排一次重建（正常路径）。
                t.SetMaterialDirty();

                // ② 但实测这个窗口里 ① 不会落地：CanvasRenderer 的材质一直是 null，
                //    而这正是"字形全被 clip 掉"的原因。所以直接把 UpdateMaterial()
                //    该做的三件事做掉（与 Graphic.UpdateMaterial 完全一致）：
                //      materialCount=1 → SetMaterial(materialForRendering) → SetTexture(mainTexture)
                //    绑好后渲染器拿得到字体图集，`UI Ex/Text Alpha` 才有 alpha 可乘。
                var cr = t.canvasRenderer;
                if (cr != null)
                {
                    var mat = t.materialForRendering;
                    if (mat != null)
                    {
                        cr.materialCount = 1;
                        cr.SetMaterial(mat, 0);
                        cr.SetTexture(t.mainTexture);
                    }
                }

                // ③ 光设上去还不够：本窗口里 uGUI 的自动重建不会把这个 renderer 的
                //    材质/纹理落到批处理上，必须强制一次 canvas 更新才生效。
                Canvas.ForceUpdateCanvases();

                Plugin.Log.LogInfo($"[UI] 输入框 Text 材质已重新绑定（{why}；mat={(t.materialForRendering != null ? t.materialForRendering.name : "null")}）");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"输入框 Text 材质重绑失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 文本变长后，CalculateColorText 会 Array.Resize 颜色表，新槽是 0（= 调色板 0 号色）。
        /// 提前把容量撑够，保证新字符仍是默认色。每帧一次整数比较，开销可忽略。
        /// </summary>
        private void EnsureInputCharColorHeadroom()
        {
            var f = _input;
            if (f == null) return;
            int need = (f.text != null ? f.text.Length : 0) + 128;
            var colors = f.m_CharColors;
            if (colors != null && colors.Length >= need) return;

            try
            {
                int size = Mathf.Max(256, need);
                var grown = new short[size];
                for (int i = 0; i < size; i++) grown[i] = -1;
                if (colors != null) Array.Copy(colors, grown, Mathf.Min(colors.Length, size));
                f.m_CharColors = grown;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"输入框颜色表扩容失败：{ex.Message}");
            }
        }

        /// <summary>开关时与游戏输入状态的交接（两条路径共用）。</summary>
        private void ApplyOpenState(bool open)
        {
            IsOpen = open;
            if (!open) _wantInputFocus = false;

            if (open)
            {
                // 让游戏停止响应移动/建造输入。记录原值以便还原。
                _savedInputing = Game.GameRefs.SetTextInputActive(true);
                UiFactory.EnsureEventSystem();
                // 关窗时 InputField.OnDisable 会把逐字符颜色清成 0（= 调色板 0 号色，alpha≈0.157），
                // 那样整段文字会被包成 `<color=#FFFFFF28>` → 乘上 Text.color 后基本透明。
                // 开窗必须重种回"跟随默认色"。
                SeedInputCharColors(_input, "开窗");
                // CanvasRenderer 的材质绑定会在关窗/重建后丢失，开窗时重申一次。
                if (_input != null) RebindInputTextMaterial(_input.textComponent, "开窗");
                // 默认不自动聚焦：这样打开后按键仍可立即关窗。
                // 需要"开窗即打字"的玩家可在配置里打开 AutoFocusInput。
                // 密钥录入态总是自动聚焦：此时玩家唯一要做的事就是填 Key
                if (_autoFocusInput || _keyEntryMode) FocusInput();
                EnsureThinkingComboText();
                EnsureModelComboText();
                ScrollToBottom();
            }
            else
            {
                // 选择器是游戏的对象，不会随聊天窗隐藏；而且我们把它抬到了
                // 聊天窗之上，不主动关会留在屏幕上。
                ClosePickers();

                if (_input != null)
                {
                    _input.DeactivateInputField();
                    _input.text = string.Empty;
                }
                // 还原游戏的输入标记
                Game.GameRefs.SetTextInputActive(_savedInputing);
            }
        }

        /// <summary>关掉可能还开着的原生选择器（图标 / 取色）。</summary>
        private static void ClosePickers()
        {
            try
            {
                if (UISignalTagPicker.isOpened) UISignalTagPicker.Close();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"关闭图标选择器失败：{ex.Message}");
            }
            try
            {
                if (UISimpleColorPicker.isOpened) UISimpleColorPicker.Close();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"关闭取色器失败：{ex.Message}");
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

        /// <summary>追加一条带说话人前缀的消息（助手 / 工具输出）。</summary>
        public void AppendLine(string speaker, string text)
        {
            if (text == null) return;

            var prefix = string.IsNullOrEmpty(speaker)
                ? string.Empty
                : $"<color=#{ColorUtility.ToHtmlStringRGB(_accent)}>{Escape(speaker)}</color>  ";

            AddMessage(LineKind.Assistant, prefix + text);
            _awaitingResponse = false;
        }

        /// <summary>
        /// 玩家删除了一个问答回合。参数是用户消息原文与历史时间戳（0 = 本回合新提问）。
        /// 窗口只负责 UI；真正删除会话条目由 Plugin 转给 sidecar（协议 A.7）。
        /// </summary>
        public event Action<string, long> TurnDeleted;

        /// <summary>追加一条用户消息（渲染成气泡，样式来自 prefab 的 example-user-*）。</summary>
        /// <param name="timestampMs">历史回放时带上的时间戳；当场提问传 0。</param>
        public void AppendUser(string text, long timestampMs = 0)
        {
            if (text == null) return;

            _currentTurnId = ++_nextTurnId;
            _awaitingResponse = true;
            AddMessage(LineKind.User, text);
            AddTurn(_currentTurnId, text, timestampMs);
        }

        /// <summary>追加一行系统提示（灰色）。</summary>
        public void AppendSystem(string text)
        {
            if (text == null) return;
            AddMessage(LineKind.System, text);
            // 错误/提示类系统消息意味着本轮已经收尾；允许索引删除已完成的回合。
            _awaitingResponse = false;
        }

        /// <summary>
        /// 在对话末尾追加一张蓝图卡片（协议附录 A.9）。
        ///
        /// 这是**数据驱动的消息卡片**，与工具块/思考块同类：节点在运行期按数据创建，
        /// 不新增 prefab 静态结构。sidecar 在本轮 final 消息之后才发，
        /// 所以卡片天然排在整轮回答的末尾。
        /// </summary>
        public void AppendBlueprintCard(BlueprintCardInfo card)
        {
            if (card == null) return;

            var line = new Line
            {
                Kind = LineKind.Blueprint,
                Text = card.DisplayTitle,
                TurnId = _currentTurnId,
                Card = card,
            };

            // 窗口还没 Create 时只记逻辑行；Create 末尾由 RebuildTranscript 补建。
            if (_content == null)
            {
                _lines.Add(line);
                TrimHistory();
                _dirty = true;
                return;
            }

            var go = CreateBlueprintCard(card);
            if (go == null) return;
            line.Go = go;
            _lines.Add(line);
            TrimHistory();
            _dirty = true;
            ScrollToBottom();
        }

        /// <summary>
        /// 建一张蓝图卡片：
        ///
        ///   blueprint-card      VerticalLayoutGroup + 淡底色 + 左侧强调条
        ///   ├── header          图标 + 标题/文件名 + 来源角标
        ///   ├── meta            产量 / 建筑数 / 占地 / 作者 / 版本
        ///   ├── description     蓝图介绍（最多两行）
        ///   └── actions         查看详情 / 粘贴铺设
        /// </summary>
        private GameObject CreateBlueprintCard(BlueprintCardInfo card)
        {
            if (_content == null || card == null) return null;

            var root = UiFactory.Node("blueprint-card-" + (++_itemSeq), _content);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = Vector2.zero;

            var bg = root.gameObject.AddComponent<Image>();
            bg.color = new Color(0.38f, 0.85f, 1f, 0.07f);
            bg.raycastTarget = false;

            var vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 5f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            // 左侧强调条：ignoreLayout，不参与纵向堆叠。
            var accentRt = UiFactory.Node("accent", root);
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(2f, 0f);
            accentRt.anchoredPosition = Vector2.zero;
            var accent = accentRt.gameObject.AddComponent<Image>();
            accent.color = new Color(_accent.r, _accent.g, _accent.b, 0.55f);
            accent.raycastTarget = false;
            accentRt.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            var headerRt = UiFactory.Node("header", root);
            var headerLe = headerRt.gameObject.AddComponent<LayoutElement>();
            headerLe.minHeight = 40f;
            headerLe.preferredHeight = 40f;
            var headerLayout = headerRt.gameObject.AddComponent<HorizontalLayoutGroup>();
            headerLayout.padding = new RectOffset(0, 0, 0, 0);
            headerLayout.spacing = 8f;
            headerLayout.childControlWidth = true;
            headerLayout.childControlHeight = true;
            headerLayout.childForceExpandWidth = false;
            headerLayout.childForceExpandHeight = false;
            headerLayout.childAlignment = TextAnchor.MiddleLeft;

            var iconRt = UiFactory.Node("icon", headerRt);
            var iconLe = iconRt.gameObject.AddComponent<LayoutElement>();
            iconLe.minWidth = 40f;
            iconLe.preferredWidth = 40f;
            iconLe.minHeight = 40f;
            iconLe.preferredHeight = 40f;
            var iconImage = iconRt.gameObject.AddComponent<Image>();
            iconImage.raycastTarget = false;
            iconImage.preserveAspect = true;
            var icon = ResolveBlueprintIcon(card);
            if (icon != null) iconImage.sprite = icon;
            else iconRt.gameObject.SetActive(false);   // 没有图标时不要留 40px 空白

            var textsRt = UiFactory.Node("texts", headerRt);
            var textsLe = textsRt.gameObject.AddComponent<LayoutElement>();
            textsLe.flexibleWidth = 1f;
            var textsLayout = textsRt.gameObject.AddComponent<VerticalLayoutGroup>();
            textsLayout.padding = new RectOffset(0, 0, 0, 0);
            textsLayout.spacing = 1f;
            textsLayout.childControlWidth = true;
            textsLayout.childControlHeight = true;
            textsLayout.childForceExpandWidth = true;
            textsLayout.childForceExpandHeight = false;
            textsLayout.childAlignment = TextAnchor.MiddleLeft;

            var titleRt = UiFactory.Node("title", textsRt);
            var titleLe = titleRt.gameObject.AddComponent<LayoutElement>();
            titleLe.minHeight = Mathf.Max(16f, _fontSize + 3f);
            titleLe.preferredHeight = Mathf.Max(16f, _fontSize + 3f);
            var title = UiFactory.AddLabel(titleRt, _bodyFont, Mathf.Max(11, _fontSize),
                _textMain, TextAnchor.MiddleLeft);
            title.text = SanitizeRichText(card.DisplayTitle);
            // 标题/文件名只占一行：Wrap + Truncate 不会像 Overflow 那样盖到来源角标上。
            title.horizontalOverflow = HorizontalWrapMode.Wrap;
            title.verticalOverflow = VerticalWrapMode.Truncate;
            title.wideCharSpacing = 1f;

            var fileRt = UiFactory.Node("file", textsRt);
            var fileLe = fileRt.gameObject.AddComponent<LayoutElement>();
            fileLe.minHeight = Mathf.Max(14f, _fontSize);
            fileLe.preferredHeight = Mathf.Max(14f, _fontSize);
            var file = UiFactory.AddLabel(fileRt, _bodyFont, Mathf.Max(9, _fontSize - 2),
                _textDim, TextAnchor.MiddleLeft);
            file.text = SanitizeRichText(string.IsNullOrEmpty(card.FileName) ? card.DisplayTitle : card.FileName);
            file.horizontalOverflow = HorizontalWrapMode.Wrap;
            file.verticalOverflow = VerticalWrapMode.Truncate;

            var source = SourceLabel(card.Source);
            if (!string.IsNullOrEmpty(source))
            {
                var badgeRt = UiFactory.Node("source", headerRt);
                var badgeLe = badgeRt.gameObject.AddComponent<LayoutElement>();
                badgeLe.minWidth = 42f;
                badgeLe.preferredWidth = 44f;
                badgeLe.minHeight = 20f;
                badgeLe.preferredHeight = 20f;
                var badgeImage = badgeRt.gameObject.AddComponent<Image>();
                badgeImage.color = new Color(_accent.r, _accent.g, _accent.b, 0.16f);
                badgeImage.raycastTarget = false;
                var badge = UiFactory.AddLabel(badgeRt, _bodyFont, Mathf.Max(9, _fontSize - 3),
                    _accent, TextAnchor.MiddleCenter);
                badge.text = source;
                badge.horizontalOverflow = HorizontalWrapMode.Overflow;
                badge.verticalOverflow = VerticalWrapMode.Truncate;
            }

            var metaText = BuildBlueprintMeta(card);
            if (!string.IsNullOrEmpty(metaText))
            {
                var metaRt = UiFactory.Node("meta", root);
                var metaLe = metaRt.gameObject.AddComponent<LayoutElement>();
                metaLe.minHeight = Mathf.Max(28f, _fontSize * 2f);
                metaLe.preferredHeight = Mathf.Max(28f, _fontSize * 2f);
                var meta = UiFactory.AddLabel(metaRt, _bodyFont, Mathf.Max(9, _fontSize - 2),
                    _textDim, TextAnchor.UpperLeft);
                meta.text = metaText;
                meta.horizontalOverflow = HorizontalWrapMode.Wrap;
                meta.verticalOverflow = VerticalWrapMode.Truncate;
                meta.wideCharSpacing = 1f;
            }

            if (!string.IsNullOrEmpty(card.Description))
            {
                var descRt = UiFactory.Node("description", root);
                var descLe = descRt.gameObject.AddComponent<LayoutElement>();
                descLe.minHeight = 16f;
                descLe.preferredHeight = Mathf.Max(30f, _fontSize * 2f + 2f);
                var desc = UiFactory.AddLabel(descRt, _bodyFont, Mathf.Max(9, _fontSize - 2),
                    new Color(_textDim.r, _textDim.g, _textDim.b, 0.95f), TextAnchor.UpperLeft);
                desc.text = SanitizeRichText(card.Description);
                desc.horizontalOverflow = HorizontalWrapMode.Wrap;
                desc.verticalOverflow = VerticalWrapMode.Truncate;
                desc.wideCharSpacing = 1f;
            }

            var actionsRt = UiFactory.Node("actions", root);
            var actionsLe = actionsRt.gameObject.AddComponent<LayoutElement>();
            actionsLe.minHeight = 26f;
            actionsLe.preferredHeight = 26f;
            var actionsLayout = actionsRt.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionsLayout.padding = new RectOffset(0, 0, 0, 0);
            actionsLayout.spacing = 6f;
            actionsLayout.childControlWidth = true;
            actionsLayout.childControlHeight = true;
            actionsLayout.childForceExpandWidth = false;
            actionsLayout.childForceExpandHeight = false;
            actionsLayout.childAlignment = TextAnchor.MiddleRight;

            CreateCardButton(actionsRt, Strings.Get("blueprint.card.view"),
                new Color(0.38f, 0.85f, 1f, 0.16f),
                () => RequestCardAction(card, BlueprintCardAction.Inspect));
            CreateCardButton(actionsRt, Strings.Get("blueprint.card.paste"),
                new Color(0.38f, 0.85f, 1f, 0.30f),
                () => RequestCardAction(card, BlueprintCardAction.Paste));

            _items.Add(new Item
            {
                Go = root.gameObject,
                Label = null,
                Kind = LineKind.Blueprint,
                TurnId = _currentTurnId,
                FromPrefab = false,
            });
            return root.gameObject;
        }

        private void CreateCardButton(RectTransform parent, string labelText, Color background, Action onClick)
        {
            var rt = UiFactory.Node("button", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 84f;
            le.preferredWidth = 92f;
            le.preferredHeight = 26f;

            var image = rt.gameObject.AddComponent<Image>();
            image.color = background;
            image.raycastTarget = true;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.onClick.AddListener(() =>
            {
                try { onClick?.Invoke(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[UI] 蓝图卡片动作失败：{ex.Message}"); }
            });

            var labelRt = UiFactory.Node("label", rt);
            UiFactory.Stretch(labelRt);
            var label = UiFactory.AddLabel(labelRt, _bodyFont, Mathf.Max(10, _fontSize - 1),
                _textMain, TextAnchor.MiddleCenter);
            label.text = labelText;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Truncate;
        }

        private void RequestCardAction(BlueprintCardInfo card, BlueprintCardAction action)
        {
            var handler = BlueprintCardActionRequested;
            if (handler == null) return;
            handler(card, action);
        }

        private static string SourceLabel(string source)
        {
            if (source == "generated") return Strings.Get("blueprint.card.source.generated");
            if (source == "downloaded") return Strings.Get("blueprint.card.source.downloaded");
            if (source == "copied") return Strings.Get("blueprint.card.source.copied");
            return null;
        }

        private static string BuildBlueprintMeta(BlueprintCardInfo card)
        {
            var parts = new List<string>(6);
            if (!string.IsNullOrEmpty(card.Item)) parts.Add(card.Item);
            if (card.Rate > 0)
                parts.Add(Strings.Get("blueprint.card.meta.rate",
                    card.Rate.ToString("0.##", CultureInfo.InvariantCulture)));
            if (card.Buildings > 0)
                parts.Add(Strings.Get("blueprint.card.meta.buildings", card.Buildings));
            if (card.Width > 0 && card.Height > 0)
                parts.Add(Strings.Get("blueprint.card.meta.size", card.Width, card.Height));
            if (!string.IsNullOrEmpty(card.Author))
                parts.Add(Strings.Get("blueprint.card.meta.author", card.Author));
            if (!string.IsNullOrEmpty(card.Version))
                parts.Add(Strings.Get("blueprint.card.meta.version", card.Version));
            if (!string.IsNullOrEmpty(card.License))
                parts.Add(Strings.Get("blueprint.card.meta.license", card.License));
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>蓝图 header 的 icon0 → 游戏物品图标；取不到就不显示图标。</summary>
        private static Sprite ResolveBlueprintIcon(BlueprintCardInfo card)
        {
            if (card == null || card.ItemId <= 0) return null;
            try
            {
                if (LDB.items == null) return null;
                var item = LDB.items.Select(card.ItemId);
                return item != null ? item.iconSprite : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 开始一个“工具块”（折叠态，标题显示读取中）。同一个 callId 后续用
        /// <see cref="FinishToolCard"/> 原地改成结果，不会出现两块。
        /// </summary>
        /// <param name="argsText">工具参数的简短文本（展开后显示，可为 null）。</param>
        public void BeginToolCard(string callId, string name, string argsText = null)
        {
            if (string.IsNullOrEmpty(callId) || _content == null) return;

            // 模型发起工具调用 = 这一段思考结束了：先收起思考块，工具块排在它后面。
            if (_thinking != null) EndThinking();

            Block existing;
            if (_toolBlocks.TryGetValue(callId, out existing) && existing.Go != null)
            {
                // 重复的 tool_call（例如上游重试）：更新参数即可，别叠一块。
                existing.Args = argsText;
                SetBlock(existing, existing.Title.text, Strings.Get("tool.running"),
                    BuildToolBody(existing.Args, null), expanded: false, titleColor: _textDim);
                return;
            }

            var block = CreateBlock("tool-block",
                title: Strings.Get("tool.title", name ?? Strings.Get("tool.name")),
                summary: Strings.Get("tool.running"),
                body: BuildToolBody(argsText, null),
                expanded: false,
                // 与「思考」块同色：工具调用不该比思考更亮（5x 文字材质下白色会像发光）。
                titleColor: _textDim);
            if (block == null) return;

            block.Args = argsText;
            block.ToolName = name;
            _toolBlocks[callId] = block;
            _lines.Add(new Line
            {
                Kind = LineKind.System,
                Text = block.Title.text + " " + block.Summary.text,
                TurnId = _currentTurnId,
                Go = block.Go,
                Block = block,
            });
            TrimHistory();
            ScrollToBottom();
        }

        /// <summary>
        /// 把工具块改成成功/失败结果（原地更新）。
        /// <paramref name="detail"/> 为展开后的完整结果，缺省时退回 <paramref name="summary"/>。
        /// </summary>
        public void FinishToolCard(string callId, string name, bool ok, string summary, string detail = null)
        {
            if (string.IsNullOrEmpty(callId)) return;

            Block block;
            if (!_toolBlocks.TryGetValue(callId, out block) || block.Go == null)
            {
                // 已被 TrimHistory 清掉（或第一次就失败）：补一块，别让结果凭空消失。
                BeginToolCard(callId, name, null);
                if (!_toolBlocks.TryGetValue(callId, out block) || block.Go == null) return;
            }

            var result = string.IsNullOrEmpty(summary)
                ? (ok ? Strings.Get("tool.ok") : Strings.Get("tool.failed"))
                : summary;
            SetBlock(block, block.Title.text, Summarize(result, 64),
                BuildToolBody(block.Args, detail ?? summary),
                expanded: !ok,   // 失败时自动展开，让玩家直接看到原因
                titleColor: ok ? _textDim : WarnColor);   // 成功与「思考」块同色；失败才用警示色
            _dirty = true;
        }

        /// <summary>
        /// 给已存在的工具块挂一张截图（M4a 的 `game_capture_view`）。
        ///
        /// 这是**数据驱动内容**（和消息、工具块同类），不是静态布局：节点在这里创建，
        /// 尺寸由 LayoutElement 决定，prefab 不需要新增结构。
        /// 图片是原生纹理，所有权交给 <see cref="ToolImageSprite"/>，随节点销毁释放。
        /// </summary>
        public void AttachToolImage(string callId, byte[] imageBytes)
        {
            if (string.IsNullOrEmpty(callId) || imageBytes == null || imageBytes.Length == 0) return;
            if (_content == null) return;

            Block block;
            if (!_toolBlocks.TryGetValue(callId, out block) || block.Go == null) return;

            Texture2D texture = null;
            Sprite sprite = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(imageBytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    return;
                }

                sprite = Sprite.Create(texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f);

                if (block.ImageRt == null)
                {
                    var rt = UiFactory.Node("tool-image", block.Go.transform);
                    var layout = rt.gameObject.AddComponent<LayoutElement>();
                    layout.minHeight = 120f;
                    layout.preferredHeight = 220f;
                    layout.flexibleHeight = 0f;

                    var graphic = rt.gameObject.AddComponent<Image>();
                    graphic.preserveAspect = true;
                    graphic.raycastTarget = false;

                    block.ImageRt = rt;
                    block.ImageOwner = rt.gameObject.AddComponent<ToolImageSprite>();
                }

                // 先让 owner 接管（它会释放上一张），再挂到 Image 上。
                var owner = block.ImageOwner;
                if (owner == null)
                {
                    owner = block.ImageRt.gameObject.AddComponent<ToolImageSprite>();
                    block.ImageOwner = owner;
                }
                owner.Adopt(texture, sprite);

                var target = block.ImageRt.GetComponent<Image>();
                if (target != null) target.sprite = sprite;

                texture = null;
                sprite = null;

                // 截图是玩家主动要看的：直接展开，避免"结果被折叠起来看不见"。
                SetBlock(block, block.Title.text, block.Summary.text, block.RawBody,
                    expanded: true, titleColor: block.Title.color);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                if (sprite != null) UnityEngine.Object.Destroy(sprite);
                if (texture != null) UnityEngine.Object.Destroy(texture);
                UnityEngine.Debug.LogWarning("[chat] 截图显示失败：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 思考块

        /// <summary>当前思考档位（off / high / max…）。</summary>
        public string ThinkingLevel => _thinkingLevel;

        /// <summary>玩家在聊天窗下拉框里切换了思考档位。参数是协议里的档位名。</summary>
        public event Action<string> ThinkingLevelChanged;

        /// <summary>
        /// 用 sidecar 的 config.state 同步下拉框（不触发 <see cref="ThinkingLevelChanged"/>，
        /// 避免“服务端回显 → 客户端再发一次”的回声）。
        /// </summary>
        public void ApplyThinkingLevel(string level)
        {
            if (!PluginConfig.IsValidThinkingLevel(level)) return;
            SetThinkingLevel(level, notify: false);
        }

        /// <summary>开始一个思考块（第一条 reasoning delta 到达时调用）。</summary>
        public void BeginThinking()
        {
            if (_content == null) return;
            if (_thinking != null && _thinking.Go != null) return;

            var block = CreateBlock("thinking-block",
                title: Strings.Get("thinking.title.streaming"),
                summary: string.Empty,
                body: string.Empty,
                expanded: true,
                titleColor: _accent);
            if (block == null) return;

            _thinking = block;
            _thinkingText.Length = 0;
        }

        /// <summary>追加一段思考增量（只更新块正文，不进聊天正文）。</summary>
        public void AppendThinkingDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;

            if (_thinking == null || _thinking.Go == null) BeginThinking();
            if (_thinking == null) return;

            _thinkingText.Append(delta);
            _thinking.Body.text = SanitizeRichText(_thinkingText.ToString());
            if (!_thinking.Body.gameObject.activeSelf) _thinking.Body.gameObject.SetActive(true);
            _dirty = true;
        }

        /// <summary>
        /// 结束思考块：收起并显示摘要。正文开始出现（或本轮结束/出错）时调用。
        /// 思考内容为空时把块整块移除，不留一个空壳。
        /// </summary>
        public void EndThinking()
        {
            var block = _thinking;
            _thinking = null;
            if (block == null) return;

            var text = _thinkingText.ToString().Trim();
            _thinkingText.Length = 0;
            if (block.Go == null) return;

            if (string.IsNullOrEmpty(text))
            {
                RemoveBlock(block);
                return;
            }

            SetBlock(block, Strings.Get("thinking.title"), Summarize(text, 48), text,
                expanded: false, titleColor: _textDim);
            _lines.Add(new Line
            {
                Kind = LineKind.System,
                Text = Strings.Get("thinking.title") + " " + block.Summary.text,
                TurnId = _currentTurnId,
                Go = block.Go,
                Block = block,
            });
            TrimHistory();
            _dirty = true;
        }

        // ---------------------------------------------------------------- 折叠块实现

        /// <summary>
        /// 在输出区创建一个折叠块。样式与 prefab 里的气泡同一个色系：
        /// 极淡的青色底 + 左侧 2px 强调条 + 一行 header + 展开后的正文。
        /// </summary>
        private Block CreateBlock(string name, string title, string summary, string body,
            bool expanded, Color titleColor)
        {
            if (_content == null) return null;

            var rootRt = UiFactory.Node(name, _content);
            rootRt.anchorMin = new Vector2(0f, 1f);
            rootRt.anchorMax = new Vector2(1f, 1f);
            rootRt.pivot = new Vector2(0.5f, 1f);
            rootRt.anchoredPosition = Vector2.zero;
            rootRt.sizeDelta = new Vector2(0f, 24f);

            var bg = rootRt.gameObject.AddComponent<Image>();
            bg.color = new Color(0.38f, 0.85f, 1f, 0.045f);
            bg.raycastTarget = false;

            var vlg = rootRt.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 6, 6);
            vlg.spacing = 2f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            // 左侧强调条：ignoreLayout，不参与 VLG 的纵向堆叠。
            var barRt = UiFactory.Node("accent", rootRt);
            barRt.anchorMin = new Vector2(0f, 0f);
            barRt.anchorMax = new Vector2(0f, 1f);
            barRt.pivot = new Vector2(0f, 0.5f);
            barRt.sizeDelta = new Vector2(2f, 0f);
            barRt.anchoredPosition = Vector2.zero;
            var bar = barRt.gameObject.AddComponent<Image>();
            bar.color = new Color(titleColor.r, titleColor.g, titleColor.b, 0.5f);
            bar.raycastTarget = false;
            barRt.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            // header：整行可点（折叠/展开）。
            var headerRt = UiFactory.Node("header", rootRt);
            var headerLe = headerRt.gameObject.AddComponent<LayoutElement>();
            headerLe.minHeight = 20f;
            headerLe.preferredHeight = 20f;
            var headerImg = headerRt.gameObject.AddComponent<Image>();
            headerImg.color = new Color(0f, 0f, 0f, 0f);
            headerImg.raycastTarget = true;
            var hlg = headerRt.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.spacing = 6f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var chevRt = UiFactory.Node("chevron", headerRt);
            var chevron = UiFactory.AddLabel(chevRt, _bodyFont, 11, titleColor, TextAnchor.MiddleCenter);
            chevron.horizontalOverflow = HorizontalWrapMode.Overflow;
            var chevLe = chevRt.gameObject.AddComponent<LayoutElement>();
            chevLe.minWidth = 12f;
            chevLe.preferredWidth = 12f;

            var titleRt = UiFactory.Node("title", headerRt);
            var titleText = UiFactory.AddLabel(titleRt, _bodyFont, Mathf.Max(10, _fontSize - 1),
                titleColor, TextAnchor.MiddleLeft);
            titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            titleText.verticalOverflow = VerticalWrapMode.Truncate;
            // 游戏字体的汉字 advance 比字形窄（13 vs 15），密集汉字会叠在一起；
            // 加 2px 字距让"存档概览"这类标题可读。
            titleText.wideCharSpacing = 2f;
            var titleLe = titleRt.gameObject.AddComponent<LayoutElement>();

            var sumRt = UiFactory.Node("summary", headerRt);
            var summaryText = UiFactory.AddLabel(sumRt, _bodyFont, Mathf.Max(10, _fontSize - 2),
                _textDim, TextAnchor.MiddleLeft);
            summaryText.verticalOverflow = VerticalWrapMode.Truncate;
            summaryText.wideCharSpacing = 1f;
            var sumLe = sumRt.gameObject.AddComponent<LayoutElement>();
            sumLe.flexibleWidth = 1f;

            // body：展开后的详情。
            var bodyRt = UiFactory.Node("body", rootRt);
            var bodyText = UiFactory.AddLabel(bodyRt, _bodyFont, Mathf.Max(10, _fontSize - 1),
                new Color(_textDim.r, _textDim.g, _textDim.b, 0.92f), TextAnchor.UpperLeft);
            bodyText.supportRichText = true;
            bodyText.lineSpacing = 1f;
            bodyText.wideCharSpacing = 1f;
            AssignIconMapping(bodyText, TryGetPreferredIconMapping());

            var headerButton = headerRt.gameObject.AddComponent<Button>();
            headerButton.transition = Selectable.Transition.None;
            headerButton.targetGraphic = headerImg;

            // 折叠 header 的动态效果和点击都走游戏原生 UIButton。
            var nativeHeader = headerRt.gameObject.AddComponent<UIButton>();
            nativeHeader.button = headerButton;
            var headerNormal = headerImg.color;
            var headerHover = new Color(
                Mathf.Min(1f, headerNormal.r * 1.25f + 0.04f),
                Mathf.Min(1f, headerNormal.g * 1.25f + 0.04f),
                Mathf.Min(1f, headerNormal.b * 1.25f + 0.04f),
                headerNormal.a);
            nativeHeader.transitions = new[]
            {
                new UIButton.Transition
                {
                    target = headerImg, damp = 0.12f,
                    normalColor = headerNormal,
                    mouseoverColor = headerHover,
                    pressedColor = new Color(headerHover.r * 0.9f, headerHover.g * 0.9f, headerHover.b * 0.9f, headerHover.a),
                    disabledColor = headerNormal,
                    mouseoverSize = 1f, pressedSize = 1f,
                    highlightSizeMultiplier = 1f, highlightColorMultiplier = 1f, highlightAlphaMultiplier = 1f
                }
            };

            var block = new Block
            {
                Go = rootRt.gameObject,
                Chevron = chevron,
                Title = titleText,
                TitleLayout = titleLe,
                Summary = summaryText,
                Body = bodyText,
                Expanded = expanded,
            };
            nativeHeader.onClick += _ => ToggleBlock(block);

            // 先入 _items（TrimHistory 才能按条数裁），再套内容。
            AddBlockItem(block);
            SetBlock(block, title, summary, body, expanded, titleColor, rebuild: false);
            return block;
        }

        private void SetBlock(Block block, string title, string summary, string body,
            bool expanded, Color titleColor, bool rebuild = true)
        {
            if (block == null || block.Go == null) return;

            block.Title.text = title ?? string.Empty;
            block.Title.color = titleColor;

            // 游戏改版 Text 的 preferredWidth 不可靠（中文标题会被算短），
            // 而 Text 在 rect 比实际排版窄时会压缩字形。这里按字符估宽：
            // 汉字按 fontSize，再补上 wideCharSpacing 的 2px/字，留 10px 余量。
            if (block.TitleLayout != null)
                block.TitleLayout.preferredWidth =
                    Mathf.Ceil(EstimateTextWidth(block.Title.text, block.Title.fontSize)
                               + CountWideChars(block.Title.text) * 2f) + 10f;

            block.Summary.text = summary ?? string.Empty;
            block.Summary.gameObject.SetActive(!expanded && !string.IsNullOrEmpty(block.Summary.text));

            block.RawBody = body ?? string.Empty;
            block.Body.text = SanitizeRichText(block.RawBody);
            block.Body.gameObject.SetActive(expanded && !string.IsNullOrEmpty(block.RawBody));

            // 截图只在展开时显示：收起态是一行摘要，塞一张 220px 的图会把聊天串撑乱。
            if (block.ImageRt != null)
                block.ImageRt.gameObject.SetActive(expanded);

            block.Expanded = expanded;
            block.Chevron.text = ChevronFor(expanded);
            block.Chevron.color = titleColor;

            _dirty = true;
            if (rebuild) RebuildLayout();
        }

        private void ToggleBlock(Block block)
        {
            if (block == null || block.Go == null) return;
            // 展开/收起时保持当前滚动位置，让块从标题下方向下长；
            // 不能在这里 ScrollToBottom，否则最后一块会被“钉住底部”，
            // 视觉上变成向上展开。
            _suppressAutoScrollOnce = true;
            SetBlock(block, block.Title.text, block.Summary.text, block.RawBody,
                expanded: !block.Expanded, titleColor: block.Title.color);
        }

        /// <summary>chevron 优先用几何字符；字体没有时退回 ASCII（避免豆腐块）。</summary>
        private string ChevronFor(bool expanded)
        {
            if (_bodyFont != null && _bodyFont.HasCharacter('▾') && _bodyFont.HasCharacter('▸'))
                return expanded ? "▾" : "▸";
            return expanded ? "v" : ">";
        }

        private void RebuildLayout()
        {
            if (_content == null) return;
            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"折叠块布局刷新失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 把块挂进 _items。模型可能先流式输出一段“我先看看…”，再发起工具调用：
        /// 这时块要插在当前流式消息**之前**，保证顺序是 思考 → 工具 → 回答。
        /// </summary>
        private void AddBlockItem(Block block)
        {
            if (block == null || block.Go == null) return;

            var item = new Item
            {
                Go = block.Go,
                Label = null,
                Kind = LineKind.System,
                FromPrefab = false,
            };

            if (_streamItem >= 0 && _streamItem < _items.Count)
            {
                var streamGo = _items[_streamItem].Go;
                _items.Insert(_streamItem, item);
                _streamItem++;
                if (streamGo != null)
                    block.Go.transform.SetSiblingIndex(streamGo.transform.GetSiblingIndex());
            }
            else
            {
                _items.Add(item);
            }
        }

        private void RemoveBlock(Block block)
        {
            if (block == null) return;

            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_items[i].Go, block.Go)) continue;
                _items.RemoveAt(i);
                if (i < _streamItem) _streamItem--;
                break;
            }

            var deadKeys = new List<string>();
            foreach (var kv in _toolBlocks)
                if (ReferenceEquals(kv.Value, block)) deadKeys.Add(kv.Key);
            foreach (var key in deadKeys) _toolBlocks.Remove(key);

            if (block.Go != null) UnityEngine.Object.Destroy(block.Go);
            block.Go = null;
        }

        /// <summary>
        /// 生成收起态的摘要：折叠空白、截断到 maxChars。
        /// （思考正文常是多段，摘要只取开头，后面用省略号表示还有内容。）
        /// </summary>
        private static string Summarize(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(Mathf.Min(text.Length, maxChars + 8));
            var lastWasSpace = false;
            foreach (var c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                sb.Append(c);
                lastWasSpace = false;
                if (sb.Length > maxChars) break;
            }

            var summary = sb.ToString().Trim();
            if (summary.Length > maxChars)
                summary = summary.Substring(0, maxChars).TrimEnd() + "…";
            return summary;
        }

        private static string BuildToolBody(string args, string result)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(args))
            {
                sb.Append(Strings.Get("tool.args")).Append('\n').Append(args.Trim());
            }
            if (!string.IsNullOrEmpty(result))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Strings.Get("tool.result")).Append('\n').Append(result.Trim());
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 思考等级下拉框

        /// <summary>
        /// 接上 prefab 右下角的「思考等级」下拉框（游戏自己的 UIComboBox）。
        /// 只暴露模型真正支持的档位：DeepSeek 的 thinkingLevelMap 只映射 high/max。
        /// </summary>
        private void WireThinkingLevelCombo(RectTransform panel)
        {
            var comboNode = FindNode(panel,
                "input/editor/tool-bar/thinking-group/thinking-level",
                "tool-bar/thinking-group/thinking-level",
                "thinking-group/thinking-level",
                "input/editor/tool-bar/thinking-level",
                "tool-bar/thinking-level", "thinking-level");
            var combo = comboNode != null ? comboNode.GetComponent<UIComboBox>() : null;
            if (combo == null)
            {
                Plugin.Log.LogInfo("[UI] prefab 里没有「思考等级」下拉框，思考档位只能用配置文件切换。");
                return;
            }

            _thinkingCombo = combo;
            combo.translated = true;   // 文案由 Strings 决定，别让游戏再翻译一遍
            combo.Items.Clear();
            combo.ItemsData.Clear();
            combo.Items.Add(Strings.Get("thinking.level.off"));
            combo.ItemsData.Add(0);
            combo.Items.Add(Strings.Get("thinking.level.high"));
            combo.ItemsData.Add(1);
            combo.Items.Add(Strings.Get("thinking.level.max"));
            combo.ItemsData.Add(2);
            combo.UpdateItems();

            SetThinkingLevel(_thinkingLevel, notify: false);
            combo.onItemIndexChange.AddListener(OnThinkingLevelComboChanged);

            // 下拉列表方向：prefab 里默认朝下（会伸出窗口底部）。运行期统一改成朝上，
            // 位置/尺寸仍由 UIComboBox.SetState 负责。
            var comboRt = combo.transform as RectTransform;
            var dropDown = combo.transform.Find("Dropdown List ScrollBox") as RectTransform;
            if (dropDown != null)
            {
                dropDown.anchorMin = new Vector2(0f, 0f);
                dropDown.anchorMax = new Vector2(0f, 0f);
                dropDown.pivot = new Vector2(0f, 0f);
                dropDown.anchoredPosition = new Vector2(0f, comboRt != null ? comboRt.sizeDelta.y : 24f);
            }

            Plugin.Log.LogInfo($"[UI] 已接上「思考等级」下拉框（当前 {_thinkingLevel}）。");
        }

        private void OnThinkingLevelComboChanged()
        {
            if (_suppressThinkingLevelEvent || _thinkingCombo == null) return;

            var level = IndexToThinkingLevel(_thinkingCombo.itemIndex);
            if (string.IsNullOrEmpty(level) || level == _thinkingLevel) return;
            SetThinkingLevel(level, notify: true);
        }

        private void SetThinkingLevel(string level, bool notify)
        {
            if (!PluginConfig.IsValidThinkingLevel(level)) return;

            level = level.ToLowerInvariant();
            var changed = !string.Equals(level, _thinkingLevel, StringComparison.Ordinal);
            _thinkingLevel = level;

            if (_thinkingCombo != null)
            {
                _suppressThinkingLevelEvent = true;
                try { _thinkingCombo.itemIndex = ThinkingLevelToIndex(level); }
                finally { _suppressThinkingLevelEvent = false; }
            }

            if (changed && notify) ThinkingLevelChanged?.Invoke(level);
        }

        private static int ThinkingLevelToIndex(string level)
        {
            switch ((level ?? string.Empty).ToLowerInvariant())
            {
                case Protocol.ThinkingOff: return 0;
                case Protocol.ThinkingMax: return 2;
                // minimal/low/medium/xhigh 在 DeepSeek 上都会被 clamp 到 high。
                default: return 1;
            }
        }

        private static string IndexToThinkingLevel(int index)
        {
            switch (index)
            {
                case 0: return Protocol.ThinkingOff;
                case 1: return Protocol.ThinkingHigh;
                case 2: return Protocol.ThinkingMax;
                default: return null;
            }
        }

        /// <summary>
        /// UIComboBox 的 `m_Input.text` 在某些时机（首次激活、OnDisable 派生类初始化）
        /// 会被清空，而 `SetState` 只会把空值抄到显示 Text 上——表现就是下拉框"一片空白"。
        /// 这里按当前档位重申一次 itemIndex，把 label 补回来；同值重申不会触发 change 事件。
        /// </summary>
        private void EnsureThinkingComboText()
        {
            if (_thinkingCombo == null) return;

            var index = ThinkingLevelToIndex(_thinkingLevel);
            if (_thinkingCombo.itemIndex == index
                && !string.IsNullOrEmpty(_thinkingCombo.m_Input != null ? _thinkingCombo.m_Input.text : null))
                return;

            _suppressThinkingLevelEvent = true;
            try { _thinkingCombo.itemIndex = index; }
            finally { _suppressThinkingLevelEvent = false; }
        }

        // ---------------------------------------------------------------- 模型选择

        /// <summary>
        /// 把 sidecar 的 providers × models 铺进「模型」下拉框，并同步当前选中项。
        /// 设置页保存后、config.state 回显时都会调用。
        /// </summary>
        public void ApplyModels(ConfigState state)
        {
            if (state == null) return;
            if (_modelCombo == null)
            {
                _pendingModelState = state;
                return;
            }

            var signature = BuildModelSignature(state);
            if (signature != _modelSignature)
            {
                _modelSignature = signature;
                _modelChoices.Clear();

                foreach (var provider in state.Providers)
                {
                    if (provider == null || string.IsNullOrEmpty(provider.Id)) continue;
                    foreach (var model in provider.Models)
                    {
                        if (model == null || string.IsNullOrEmpty(model.Id)) continue;
                        _modelChoices.Add(new ModelChoice
                        {
                            Provider = provider.Id,
                            Model = model.Id,
                            Label = string.IsNullOrEmpty(model.Name) ? model.Id : model.Name,
                        });
                    }
                }

                _suppressModelEvent = true;
                try
                {
                    _modelCombo.Items.Clear();
                    _modelCombo.ItemsData.Clear();
                    if (_modelChoices.Count == 0)
                    {
                        _modelCombo.Items.Add(Strings.Get("model.none"));
                        _modelCombo.ItemsData.Add(0);
                    }
                    else
                    {
                        for (var i = 0; i < _modelChoices.Count; i++)
                        {
                            _modelCombo.Items.Add(_modelChoices[i].Label);
                            _modelCombo.ItemsData.Add(i);
                        }
                    }
                    _modelCombo.UpdateItems();
                }
                finally
                {
                    _suppressModelEvent = false;
                }
            }

            var active = IndexOfModel(state.Provider, state.Model);
            if (active < 0) active = 0;
            SelectModelIndex(active, notify: false);
        }

        /// <summary>当前选中的模型下标（找不到时返回 -1）。</summary>
        private int IndexOfModel(string provider, string model)
        {
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return -1;
            for (var i = 0; i < _modelChoices.Count; i++)
            {
                if (string.Equals(_modelChoices[i].Provider, provider, StringComparison.Ordinal)
                    && string.Equals(_modelChoices[i].Model, model, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private void SelectModelIndex(int index, bool notify)
        {
            if (_modelCombo == null) return;
            if (_modelChoices.Count == 0)
            {
                _modelProvider = string.Empty;
                _modelId = string.Empty;
                return;
            }
            if (index < 0 || index >= _modelChoices.Count) index = 0;

            var choice = _modelChoices[index];
            var changed = !string.Equals(choice.Provider, _modelProvider, StringComparison.Ordinal)
                          || !string.Equals(choice.Model, _modelId, StringComparison.Ordinal);
            _modelProvider = choice.Provider;
            _modelId = choice.Model;

            _suppressModelEvent = true;
            try { _modelCombo.itemIndex = index; }
            finally { _suppressModelEvent = false; }

            if (changed && notify) ModelSelectionChanged?.Invoke(choice.Provider, choice.Model);
        }

        private void OnModelComboChanged()
        {
            if (_suppressModelEvent || _modelCombo == null) return;
            SelectModelIndex(_modelCombo.itemIndex, notify: true);
        }

        private static string BuildModelSignature(ConfigState state)
        {
            var sb = new StringBuilder();
            foreach (var provider in state.Providers)
            {
                if (provider == null) continue;
                sb.Append(provider.Id).Append(':');
                foreach (var model in provider.Models)
                {
                    if (model == null) continue;
                    sb.Append(model.Id).Append('=').Append(model.Name).Append(';');
                }
                sb.Append('|');
            }
            return sb.ToString();
        }

        /// <summary>与思考档位相同的兜底：UIComboBox 首次激活时 label 可能被清空。</summary>
        private void EnsureModelComboText()
        {
            if (_modelCombo == null) return;

            var index = IndexOfModel(_modelProvider, _modelId);
            if (index < 0) index = 0;
            if (_modelCombo.itemIndex == index
                && !string.IsNullOrEmpty(_modelCombo.m_Input != null ? _modelCombo.m_Input.text : null))
                return;

            _suppressModelEvent = true;
            try { _modelCombo.itemIndex = index; }
            finally { _suppressModelEvent = false; }
        }

        /// <summary>把 prefab 里的「模型」下拉框接上；选项内容由 ApplyModels 填。</summary>
        private void WireModelCombo(RectTransform panel)
        {
            var comboNode = FindNode(panel,
                "input/editor/tool-bar/model-group/model-level",
                "tool-bar/model-group/model-level",
                "model-group/model-level",
                "input/editor/tool-bar/model-level",
                "tool-bar/model-level", "model-level");
            var combo = comboNode != null ? comboNode.GetComponent<UIComboBox>() : null;
            if (combo == null) combo = CloneModelCombo(panel);
            if (combo == null)
            {
                Plugin.Log.LogInfo("[UI] prefab 里没有「模型」下拉框，模型只能在设置页切换。");
                return;
            }

            ConfigureModelCombo(panel, combo);
        }

        /// <summary>
        /// 兜底：旧 AssetBundle 里没有 model-group（用户只改了 prefab、还没重打包）时，
        /// 克隆同一工具条上的 thinking-group，改名成模型下拉框。位置放在「思考」左边 6px。
        /// </summary>
        private UIComboBox CloneModelCombo(RectTransform panel)
        {
            GameObject clone = null;
            try
            {
                var bar = panel != null ? panel.Find("input/editor/tool-bar") as RectTransform : null;
                if (bar == null || bar.Find("model-group") != null) return null;

                var thinking = bar.Find("thinking-group") as RectTransform;
                if (thinking == null)
                    return BuildRuntimeModelGroup(bar, bar.Find("thinking-level") as RectTransform);

                clone = UnityEngine.Object.Instantiate(thinking.gameObject, bar, false);
                clone.name = "model-group";
                var rt = clone.transform as RectTransform;
                if (rt != null)
                {
                    rt.anchoredPosition = new Vector2(
                        thinking.anchoredPosition.x - rt.sizeDelta.x - 6f,
                        thinking.anchoredPosition.y);
                    rt.SetSiblingIndex(thinking.GetSiblingIndex());
                }

                var label = clone.transform.Find("thinking-label");
                if (label != null)
                {
                    label.name = "model-label";
                    var text = label.GetComponent<Text>();
                    if (text != null) text.text = Strings.Get("model.label");
                }

                var comboGo = clone.transform.Find("thinking-level");
                if (comboGo == null) return null;
                comboGo.name = "model-level";
                var combo = comboGo.GetComponent<UIComboBox>();
                if (combo == null) return null;

                // 在激活前先清掉从「思考」复制来的 3 个档位，避免 Awake 按错误数量建条目。
                combo.Items.Clear();
                combo.ItemsData.Clear();
                combo.Items.Add(Strings.Get("model.none"));
                combo.ItemsData.Add(0);

                clone.SetActive(true);
                clone = null;   // 交给场景，别在 finally 里销毁
                Plugin.Log.LogInfo("[UI] prefab 没有 model-group，已从 thinking-group 克隆「模型」下拉框。");
                return combo;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[UI] 克隆「模型」下拉框失败：{ex.Message}");
                return null;
            }
            finally
            {
                if (clone != null) UnityEngine.Object.Destroy(clone);
            }
        }

        /// <summary>
        /// 更老的 bundle：thinking-level 直接挂在 tool-bar 下（没有 thinking-group）。
        /// 这时手工拼一个 model-group：右侧锚点、40px 标签 + 100px 下拉框。
        /// </summary>
        private UIComboBox BuildRuntimeModelGroup(RectTransform bar, RectTransform thinkingCombo)
        {
            if (bar == null || thinkingCombo == null) return null;

            var groupGo = new GameObject("model-group", typeof(RectTransform));
            var group = (RectTransform)groupGo.transform;
            group.SetParent(bar, false);
            group.anchorMin = new Vector2(1f, 0.5f);
            group.anchorMax = new Vector2(1f, 0.5f);
            group.pivot = new Vector2(0f, 0.5f);
            group.sizeDelta = new Vector2(146f, 24f);
            group.anchoredPosition = new Vector2(
                thinkingCombo.anchoredPosition.x - group.sizeDelta.x - 6f,
                thinkingCombo.anchoredPosition.y);
            group.SetSiblingIndex(thinkingCombo.GetSiblingIndex());

            var labelRt = UiFactory.Node("model-label", group);
            labelRt.anchorMin = new Vector2(0f, 0.5f);
            labelRt.anchorMax = new Vector2(0f, 0.5f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.anchoredPosition = Vector2.zero;
            labelRt.sizeDelta = new Vector2(40f, 24f);
            var label = UiFactory.AddLabel(labelRt, _bodyFont, Mathf.Max(10, _fontSize - 1),
                _textDim, TextAnchor.MiddleLeft);
            label.text = Strings.Get("model.label");
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.raycastTarget = false;

            var clone = UnityEngine.Object.Instantiate(thinkingCombo.gameObject, group, false);
            clone.name = "model-level";
            var comboRt = (RectTransform)clone.transform;
            comboRt.anchorMin = new Vector2(0f, 0.5f);
            comboRt.anchorMax = new Vector2(0f, 0.5f);
            comboRt.pivot = new Vector2(0f, 0.5f);
            comboRt.anchoredPosition = new Vector2(46f, 0f);
            comboRt.sizeDelta = new Vector2(100f, 24f);

            var combo = clone.GetComponent<UIComboBox>();
            if (combo == null)
            {
                UnityEngine.Object.Destroy(clone);
                UnityEngine.Object.Destroy(groupGo);
                return null;
            }

            combo.Items.Clear();
            combo.ItemsData.Clear();
            combo.Items.Add(Strings.Get("model.none"));
            combo.ItemsData.Add(0);
            clone.SetActive(true);
            Plugin.Log.LogInfo("[UI] 老 prefab/bundle 没有 model-group，已在运行期拼出「模型」下拉框。");
            return combo;
        }

        private void ConfigureModelCombo(RectTransform panel, UIComboBox combo)
        {
            _modelCombo = combo;
            combo.translated = true;   // 文案由我们填，别让游戏再翻译
            combo.Items.Clear();
            combo.ItemsData.Clear();
            combo.Items.Add(Strings.Get("model.none"));
            combo.ItemsData.Add(0);
            combo.UpdateItems();
            _suppressModelEvent = true;
            try { combo.itemIndex = 0; }
            finally { _suppressModelEvent = false; }
            combo.onItemIndexChange.AddListener(OnModelComboChanged);

            // 和思考下拉框一样朝上展开（工具条在窗口底部）。
            var comboRt = combo.transform as RectTransform;
            var dropDown = combo.transform.Find("Dropdown List ScrollBox") as RectTransform;
            if (dropDown != null)
            {
                dropDown.anchorMin = new Vector2(0f, 0f);
                dropDown.anchorMax = new Vector2(0f, 0f);
                dropDown.pivot = new Vector2(0f, 0f);
                dropDown.anchoredPosition = new Vector2(0f, comboRt != null ? comboRt.sizeDelta.y : 24f);
            }

            AdjustModelGroupSpacing(panel, combo);
            Plugin.Log.LogInfo("[UI] 已接上「模型」下拉框。");
            if (_pendingModelState != null)
            {
                var pending = _pendingModelState;
                _pendingModelState = null;
                ApplyModels(pending);
            }
        }

        /// <summary>
        /// prefab 里 model-group 和 thinking-group 都靠右摆；玩家改版时两者可能贴太近
        /// （模型框右缘压到「思考」标签）。这里按实际 rect 留 6px 间距，运行时兜底。
        /// </summary>
        private static void AdjustModelGroupSpacing(RectTransform panel, UIComboBox combo)
        {
            var group = combo != null ? combo.transform.parent as RectTransform : null;
            var thinking = panel != null
                ? panel.Find("input/editor/tool-bar/thinking-group") as RectTransform
                : null;
            if (group == null || thinking == null || group.name != "model-group") return;

            var width = group.rect.width > 1f ? group.rect.width : group.sizeDelta.x;
            var groupRight = group.anchoredPosition.x + width;
            var thinkingLeft = thinking.anchoredPosition.x;
            var gap = thinkingLeft - groupRight;
            if (gap >= 6f) return;

            group.anchoredPosition = new Vector2(
                group.anchoredPosition.x - (6f - gap), group.anchoredPosition.y);
            Plugin.Log.LogInfo($"[UI] 模型框与思考框间距过小（{gap:0.#}px），已左移 {6f - gap:0.#}px。");
        }

        /// <summary>开始一段流式回复；后续 delta 会填进同一条消息。</summary>
        public void BeginStream(string speaker)
        {
            // 助手消息不显示说话人名字：prefab 里的样式就是裸文本（见 example-assistant-*）
            _stream.Length = 0;
            _streamItem = CreateItem(LineKind.Assistant);
            if (_streamItem >= 0 && _streamItem < _items.Count && _items[_streamItem].Label != null)
                _items[_streamItem].Label.text = string.Empty;
            _dirty = true;
        }

        public void AppendStreamDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _stream.Append(delta);
            _dirty = true;
        }

        public void EndStream()
        {
            if (_streamItem >= 0 && _streamItem < _items.Count)
            {
                var text = _stream.ToString();
                var item = _items[_streamItem];
                if (item.Label != null) item.Label.text = SanitizeRichText(text);
                AttachReplyFooter(_streamItem, text);
                _lines.Add(new Line
                {
                    Kind = LineKind.Assistant,
                    Text = text,
                    TurnId = _currentTurnId,
                    Go = item.Go,
                });
            }
            _streamItem = -1;
            _stream.Length = 0;
            _awaitingResponse = false;
            TrimHistory();
            _dirty = true;
        }

        /// <summary>聊天记录是否为空（用于决定要不要显示欢迎语）。</summary>
        public bool TranscriptEmpty => _lines.Count == 0 && _streamItem < 0;

        public void ClearTranscript()
        {
            var destroyed = new HashSet<GameObject>();
            foreach (var line in _lines)
            {
                if (line.Go != null && destroyed.Add(line.Go)) UnityEngine.Object.Destroy(line.Go);
            }
            foreach (var item in _items)
            {
                if (item.Go != null && destroyed.Add(item.Go)) UnityEngine.Object.Destroy(item.Go);
            }
            _items.Clear();
            _toolBlocks.Clear();
            _thinking = null;
            _thinkingText.Length = 0;
            _lines.Clear();
            _stream.Length = 0;
            _streamItem = -1;
            _turns.Clear();
            _currentTurnId = 0;
            _nextTurnId = 0;
            _focusedTurnId = 0;
            _awaitingResponse = false;
            ClearIndexRows();
            RefreshIndexEmpty();
            _dirty = true;
        }

        private static string GameClock()
        {
            if (GameMain.data == null) return "--:--";
            long sec = GameMain.gameTick / 60L;
            int hour = (int)((sec / 3600L) % 24L);
            int min = (int)((sec / 60L) % 60L);
            return hour.ToString("00") + ":" + min.ToString("00");
        }

        /// <summary>
        /// 给助手回复挂上“游戏时间 + 点击复制”。按钮是这条消息自带的交互，
        /// 不属于窗口静态布局，因此不需要改 prefab。
        /// </summary>
        private void AttachReplyFooter(int index, string rawText)
        {
            if (index < 0 || index >= _items.Count) return;
            var item = _items[index];
            if (item.Go == null || item.Label == null || string.IsNullOrEmpty(rawText)) return;

            var footer = "\n<size=16><color=#8FA3B8>游戏时间 " + GameClock()
                + " · 点击复制</color></size>";
            item.Label.text = SanitizeRichText(rawText) + footer;

            var button = item.Go.GetComponent<Button>();
            if (button == null) button = item.Go.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            var copy = rawText;
            button.onClick.AddListener(() => GUIUtility.systemCopyBuffer = copy);
        }

        private void AddMessage(LineKind kind, string text)
        {
            var line = new Line { Kind = kind, Text = text, TurnId = _currentTurnId };

            // 窗口还没 Create 时只记逻辑行；Create 末尾用 RebuildTranscript 补建。
            // 否则会在 Canvas 之外建出一堆孤儿 Text，打开窗口后历史反而看不见。
            if (_content == null)
            {
                _lines.Add(line);
                TrimHistory();
                _dirty = true;
                return;
            }

            var index = CreateItem(kind);
            if (index >= 0 && index < _items.Count)
            {
                line.Go = _items[index].Go;
                if (_items[index].Label != null) _items[index].Label.text = SanitizeRichText(text);
                FitBubbleWidth(_items[index].Go, text);
                if (kind == LineKind.Assistant) AttachReplyFooter(index, text);
            }

            _lines.Add(line);
            TrimHistory();
            _dirty = true;
        }

        /// <summary>窗口首次 Create 时，把此前已进入 _lines 的消息补建成可见项。</summary>
        private void RebuildTranscript()
        {
            if (_content == null || _lines.Count == 0) return;

            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                _currentTurnId = line.TurnId;

                // 蓝图卡片不是文本项：按数据重新建一张。
                if (line.Kind == LineKind.Blueprint)
                {
                    line.Go = CreateBlueprintCard(line.Card);
                    if (line.Go != null) _lines[i] = line;
                    continue;
                }

                var index = CreateItem(line.Kind);
                if (index < 0 || index >= _items.Count) continue;
                var item = _items[index];
                line.Go = item.Go;
                _lines[i] = line;
                if (item.Label != null) item.Label.text = SanitizeRichText(line.Text);
                FitBubbleWidth(item.Go, line.Text);
                if (line.Kind == LineKind.Assistant) AttachReplyFooter(index, line.Text);
                if (line.Kind == LineKind.User && line.TurnId > 0)
                    UpdateTurnUserGo(line.TurnId, line.Go);
            }

            if (_lines.Count > 0) _currentTurnId = _lines[_lines.Count - 1].TurnId;
            TrimHistory();
            _dirty = true;
            ScrollToBottom();
        }

        // ---------------------------------------------------------------- 提问索引（chat-index）

        /// <summary>
        /// 找到/补建左侧 chat-index，并在里面建一个可滚动的提问列表。
        /// 用户只在 prefab 里摆了容器；列表内容全部由运行期按聊天记录生成。
        /// </summary>
        private void EnsureChatIndex()
        {
            if (_panel == null) return;

            _chatIndex = _panel.Find("chat-index") as RectTransform;
            if (_chatIndex == null)
            {
                Plugin.Log.LogWarning("[UI] prefab 缺少 chat-index，索引不可用");
                return;
            }

            var scrollRt = _chatIndex.Find("chat-index-scroll") as RectTransform;
            var viewport = scrollRt != null ? scrollRt.Find("viewport") as RectTransform : null;
            _chatIndexScroll = scrollRt != null ? scrollRt.GetComponent<ScrollRect>() : null;
            _chatIndexContent = viewport != null ? viewport.Find("content") as RectTransform : null;
            _chatIndexTemplate = _chatIndexContent != null
                ? _chatIndexContent.Find("turn-row-template") as RectTransform
                : null;
            var emptyRt = _chatIndex.Find("chat-index-empty") as RectTransform;
            _chatIndexEmpty = emptyRt != null ? emptyRt.GetComponent<Text>() : null;
            _chatIndexTitle = null;

            if (_chatIndexScroll == null || _chatIndexContent == null || _chatIndexTemplate == null)
            {
                Plugin.Log.LogWarning(
                    "[UI] prefab 的 chat-index 结构不完整：需要 scroll/viewport/content/turn-row-template");
                return;
            }

            _chatIndexTemplate.gameObject.SetActive(false);
            if (_hostCanvas != null)
                SetLayerRecursively(_chatIndex, _hostCanvas.gameObject.layer);

            RebuildIndexRows();
        }

        private void AddTurn(int turnId, string text, long timestampMs = 0)
        {
            var userGo = _lines.Count > 0 ? _lines[_lines.Count - 1].Go : null;
            var entry = new TurnEntry
            {
                Id = turnId,
                Text = text ?? string.Empty,
                Timestamp = timestampMs,
                UserGo = userGo,
            };
            _turns.Add(entry);
            if (_chatIndexContent != null) CreateIndexRow(entry);
            RefreshIndexEmpty();
        }

        private void UpdateTurnUserGo(int turnId, GameObject go)
        {
            for (int i = 0; i < _turns.Count; i++)
            {
                if (_turns[i].Id != turnId) continue;
                _turns[i].UserGo = go;
                return;
            }
        }

        private void RebuildIndexRows()
        {
            if (_chatIndexContent == null) return;
            ClearIndexRows();
            foreach (var entry in _turns) CreateIndexRow(entry);
            RefreshIndexSelection();
            RefreshIndexEmpty();
        }

        private void CreateIndexRow(TurnEntry entry)
        {
            if (entry == null || _chatIndexContent == null || entry.Row != null) return;
            if (_chatIndexTemplate == null)
            {
                Plugin.Log.LogWarning("[UI] prefab 缺少 turn-row-template，索引行无法创建");
                return;
            }
            CreateIndexRowFromTemplate(entry);
        }

        /// <summary>克隆 prefab 里的 turn-row-template；链接/删除按钮都在 prefab 里定义。</summary>
        private void CreateIndexRowFromTemplate(TurnEntry entry)
        {
            var go = UnityEngine.Object.Instantiate(_chatIndexTemplate.gameObject, _chatIndexContent, false);
            go.name = "turn-" + entry.Id;
            go.SetActive(true);

            var linkTf = go.transform.Find("link");
            var label = linkTf != null ? linkTf.Find("label")?.GetComponent<Text>() : null;
            if (label == null && linkTf != null) label = linkTf.GetComponent<Text>();
            var delTf = go.transform.Find("delete");

            if (label == null || delTf == null || linkTf == null)
            {
                Plugin.Log.LogWarning("[UI] prefab 的 turn-row-template 结构不完整");
                UnityEngine.Object.Destroy(go);
                return;
            }

            // 链接/删除都不用子级 Button：任何子级 Selectable 都会吃掉 pointerEnter/Exit，
            // 导致行级 hover 卡住。点击统一交给行上的 ChatIndexRowHover，按点击位置区分。
            var linkButton = linkTf.GetComponent<Button>();
            if (linkButton != null)
            {
                linkButton.enabled = false;
                UnityEngine.Object.Destroy(linkButton);
            }

            var linkWidth = linkTf is RectTransform linkRect && linkRect.rect.width > 20f
                ? linkRect.rect.width - 12f
                : 176f;
            label.text = IndexLabel(entry.Text, linkWidth);
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.wideCharSpacing = 1f;
            label.raycastTarget = false;

            var hover = go.GetComponent<ChatIndexRowHover>();
            if (hover == null) hover = go.AddComponent<ChatIndexRowHover>();
            hover.DeleteButton = delTf.gameObject;
            hover.HoverBackground = go.GetComponent<Graphic>();
            int linkId = entry.Id;
            int delId = entry.Id;
            hover.Clicked += () => FocusTurn(linkId);

            // 删除区域的 hover 颜色、提示、点击都由 prefab 上的原生 Button + UIButton 处理。
            var nativeDelete = delTf.GetComponent<UIButton>();
            if (nativeDelete != null)
            {
                nativeDelete.onClick += _ => DeleteTurn(delId);
                var tips = nativeDelete.tips;
                tips.tipTitle = Strings.Get("chat.index.delete.tip");
                // 提示 prefab 来自游戏运行时配置；不要把外部 prefab 引用序列化进 bundle，
                // 否则 bundle 载入时可能变成 null。
                tips.simpleGeneralTipPrefab = Configs.builtin != null
                    ? Configs.builtin.uiDashboardSimpleTipPrefab
                    : null;
                nativeDelete.tips = tips;
            }
            else
            {
                Plugin.Log.LogWarning("[UI] prefab 的 delete 缺少原生 UIButton，删除按钮不可用");
            }
            entry.Row = go;
            entry.Link = label;
            entry.Hover = hover;

            // 只有鼠标进入该行时才显示删除按钮。
            delTf.gameObject.SetActive(false);
            hover.SetSelected(entry.Id == _focusedTurnId);

            if (_hostCanvas != null)
                SetLayerRecursively(go.transform, _hostCanvas.gameObject.layer);
            RefreshIndexEmpty();
        }

        private static ColorBlock IndexButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.75f, 0.92f, 1f, 1f);
            colors.pressedColor = new Color(0.6f, 0.8f, 0.9f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            return colors;
        }

        private string IndexLabel(string text, float maxWidth)
        {
            var s = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > 80) s = s.Substring(0, 80);

            var fontSize = Mathf.Max(10, _fontSize - 2);
            maxWidth = Mathf.Max(60f, maxWidth);
            if (EstimateTextWidth(s, fontSize) <= maxWidth) return s;

            while (s.Length > 0 && EstimateTextWidth(s + "…", fontSize) > maxWidth)
                s = s.Substring(0, s.Length - 1);
            return s + "…";
        }

        private void FocusTurn(int turnId)
        {
            TurnEntry entry = null;
            foreach (var candidate in _turns)
            {
                if (candidate.Id == turnId) { entry = candidate; break; }
            }
            if (entry == null) return;

            _focusedTurnId = turnId;
            RefreshIndexSelection();

            var go = entry.UserGo;
            if (go == null)
            {
                for (int i = 0; i < _lines.Count; i++)
                {
                    if (_lines[i].Kind == LineKind.User && _lines[i].TurnId == turnId)
                    {
                        go = _lines[i].Go;
                        entry.UserGo = go;
                        break;
                    }
                }
            }
            if (go == null || _scroll == null || _content == null) return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            var itemRt = go.GetComponent<RectTransform>();
            var viewportRt = _scroll.viewport;
            if (itemRt == null || viewportRt == null) return;

            var viewportHeight = viewportRt.rect.height;
            var contentHeight = _content.rect.height;
            var max = Mathf.Max(1f, contentHeight - viewportHeight);
            var itemTop = -itemRt.anchoredPosition.y;
            _scroll.verticalNormalizedPosition = Mathf.Clamp01(1f - (itemTop - viewportHeight * 0.5f) / max);
        }

        private void DeleteTurn(int turnId)
        {
            // 模型还在回答/思考时不要删，避免流式消息落到已删除的回合。
            if (_streamItem >= 0 || _awaitingResponse || _thinking != null) return;

            TurnEntry entry = null;
            for (int i = 0; i < _turns.Count; i++)
            {
                if (_turns[i].Id == turnId) { entry = _turns[i]; break; }
            }
            if (entry == null) return;

            var destroyed = new HashSet<GameObject>();
            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var line = _lines[i];
                if (line.TurnId != turnId) continue;
                if (line.Block != null) ForgetBlock(line.Block);
                if (line.Go != null && destroyed.Add(line.Go))
                    UnityEngine.Object.Destroy(line.Go);
                _lines.RemoveAt(i);
            }

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].TurnId != turnId) continue;
                var go = _items[i].Go;
                if (go != null && destroyed.Add(go)) UnityEngine.Object.Destroy(go);
                _items.RemoveAt(i);
                if (_streamItem > i) _streamItem--;
            }

            if (entry.Row != null) UnityEngine.Object.Destroy(entry.Row);
            _turns.Remove(entry);
            if (_focusedTurnId == turnId) _focusedTurnId = 0;
            if (_currentTurnId == turnId)
                _currentTurnId = _lines.Count > 0 ? _lines[_lines.Count - 1].TurnId : 0;

            RefreshIndexSelection();
            RefreshIndexEmpty();
            _dirty = true;
            Plugin.Log.LogInfo($"[索引] 已从窗口删除回合 {turnId}");

            // UI 删掉还不够：save.bound 会从会话文件重放，必须让 sidecar 也删。
            try
            {
                TurnDeleted?.Invoke(entry.Text, entry.Timestamp);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[索引] 通知 sidecar 删除回合失败：{ex.Message}");
            }
        }

        private void ForgetBlock(Block block)
        {
            if (block == null) return;
            if (_thinking == block)
            {
                _thinking = null;
                _thinkingText.Length = 0;
            }
            string key = null;
            foreach (var pair in _toolBlocks)
            {
                if (pair.Value != block) continue;
                key = pair.Key;
                break;
            }
            if (key != null) _toolBlocks.Remove(key);
        }

        private void ClearIndexRows()
        {
            if (_chatIndexContent != null)
            {
                for (int i = _chatIndexContent.childCount - 1; i >= 0; i--)
                {
                    var child = _chatIndexContent.GetChild(i);
                    if (_chatIndexTemplate != null && child == _chatIndexTemplate) continue;
                    if (child.name == "turn-row-template") continue;
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
            foreach (var entry in _turns)
            {
                entry.Row = null;
                entry.Link = null;
            }
        }

        private void RefreshIndexEmpty()
        {
            if (_chatIndexEmpty == null) return;
            _chatIndexEmpty.gameObject.SetActive(_turns.Count == 0);
        }

        private void RefreshIndexSelection()
        {
            foreach (var entry in _turns)
            {
                if (entry.Link != null)
                    entry.Link.color = entry.Id == _focusedTurnId ? _textMain : _accent;
                entry.Hover?.SetSelected(entry.Id == _focusedTurnId);
            }
        }

        /// <summary>
        /// 生成一条消息的界面：**克隆 prefab 里的示例节点**
        /// （`example-user-*` → 气泡，`example-assistant-*` → 裸文本）。
        ///
        /// 这样样式只在 prefab 里定义一次：在 Unity 里改气泡，运行期自动跟着变，
        /// 不用在 C# 里再抄一份颜色/贴图。prefab 没带模板时退化成裸文本。
        /// </summary>
        private int CreateItem(LineKind kind)
        {
            var template = kind == LineKind.User
                ? _userTemplate
                : kind == LineKind.Assistant ? _assistantTemplate : null;

            GameObject go;
            if (template != null && _content != null)
            {
                go = UnityEngine.Object.Instantiate(template.gameObject, _content, false);
                go.name = "msg-" + (++_itemSeq);
                go.SetActive(true);
            }
            else
            {
                go = BuildPlainItem(kind);
            }

            var fromPrefab = template != null && _content != null;
            var label = go.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                ApplyBodyFont(label);
                // prefab 里的示例节点自带字体与字色（助手正文是 #FFFFFFB3）。
                // 只有代码兜底构建的项才用运行期配色；用户气泡与 prefab 助手都保留原样。
                if (kind == LineKind.System) label.color = _textDim;
                else if (kind == LineKind.Assistant && !fromPrefab) label.color = _textMain;

                // 富文本必须无条件开启：<i>/<color>/<size> 是模型输出格式的一部分，
                // 不能依赖“输入框是否存在”这个无关条件。
                label.supportRichText = true;

                // 优先沿用输入框/原生 Text 的图标表；不完整时退回游戏的全局表。
                AssignIconMapping(label, TryGetPreferredIconMapping());
            }

            _items.Add(new Item
            {
                Go = go,
                Label = label,
                Kind = kind,
                TurnId = _currentTurnId,
                FromPrefab = fromPrefab,
            });
            return _items.Count - 1;
        }

        private GameObject BuildPlainItem(LineKind kind)
        {
            var rt = UiFactory.Node("msg-" + (++_itemSeq), _content);
            var label = rt.gameObject.AddComponent<Text>();
            label.font = _bodyFont;
            label.fontSize = _fontSize;
            label.color = kind == LineKind.System ? _textDim : _textMain;
            label.alignment = TextAnchor.UpperLeft;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.supportRichText = true;
            label.raycastTarget = false;
            AssignIconMapping(label, TryGetPreferredIconMapping());
            GameTextStyle.Apply(label);
            return rt.gameObject;
        }

        /// <summary>
        /// 正文默认用游戏的**正文字体**（prefab 里 Arial 只是占位）。
        /// 但如果 prefab 被手工换成了游戏字体（例如 SAIRASB），必须尊重它——
        /// 否则运行期会把你在 Unity 里调好的字重/字形整个覆盖掉。
        /// </summary>
        /// <summary>把窗口内所有 Text 换成柔化材质（输入框保留稍厚版本）。</summary>
        private void ApplySoftTextMaterials()
        {
            if (_root == null) return;
            foreach (var t in _root.GetComponentsInChildren<Text>(true))
            {
                if (t == null) continue;
                var thick = _input != null && t.transform.IsChildOf(_input.transform);
                GameTextStyle.Apply(t, thick);
            }
        }

        private void ApplyBodyFont(Text label)
        {
            if (label == null || _bodyFont == null) return;
            if (IsPlaceholderFont(label.font)) label.font = _bodyFont;
        }

        private static bool IsPlaceholderFont(Font font)
        {
            if (font == null) return true;
            var name = font.name ?? string.Empty;
            return name.StartsWith("Arial", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("Liberation Sans", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>游戏全局图标映射；主菜单/加载早期可能尚未构建，失败时返回 null。</summary>
        private static UnityEngine.UI.Youthcat.TextIconMapping TryGetGeneralIconMapping()
        {
            try
            {
                return Configs.builtin != null ? Configs.builtin.generalIconMapping : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 原生 Text 自己的 iconMapping 优先；输入框那份通常就是游戏 TextShortcut 用的表。
        /// 只有它缺失或条目明显少于全局表时，才退回 global（避免丢物品/建筑 tag）。
        /// </summary>
        private UnityEngine.UI.Youthcat.TextIconMapping TryGetPreferredIconMapping()
        {
            var general = TryGetGeneralIconMapping();
            try
            {
                var inputMap = _input != null && _input.textComponent != null
                    ? _input.textComponent.iconMapping
                    : null;
                if (inputMap != null && inputMap.countInUse > 0 &&
                    (general == null || inputMap.countInUse >= general.countInUse))
                    return inputMap;
            }
            catch { /* fall through */ }
            return general;
        }

        private static UnityEngine.UI.Youthcat.TextIconMapping _iconTagSource;
        private static HashSet<string> _iconTags;

        /// <summary>当前全局图标表里能真正渲染的 tag 集合；表没就绪时返回 null。</summary>
        private static HashSet<string> GetValidIconTags()
        {
            var map = TryGetGeneralIconMapping();
            if (map == null || map.iconMappings == null || map.iconMappings.Length == 0)
                return null;
            if (ReferenceEquals(map, _iconTagSource) && _iconTags != null)
                return _iconTags;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in map.iconMappings)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.tag))
                    set.Add(entry.tag);
            }
            _iconTagSource = map;
            _iconTags = set;
            return set;
        }

        /// <summary>
        /// 给 Text 挂上图标表。不能只在创建时挂：主菜单早期表还没建好，
        /// 而且输入框那份表不一定包含全部物品 tag。
        /// </summary>
        private static void AssignIconMapping(Text label, UnityEngine.UI.Youthcat.TextIconMapping mapping)
        {
            if (label == null || mapping == null) return;
            label.supportRichText = true;
            // 图标不是普通字符：`UI Ex/Text Alpha` 用 UV1>=2 去全局图标图集采样。
            // prefab 里 assistant 正文可能只挂了默认 UI 材质，图标会变成白块。
            GameTextStyle.Apply(label);
            if (ReferenceEquals(label.iconMapping, mapping)) return;
            label.iconMapping = mapping;
            label.SetVerticesDirty();
            label.SetLayoutDirty();
        }

        /// <summary>把已经创建的消息/折叠块都补一遍图标表。</summary>
        private void EnsureIconMappings()
        {
            var general = TryGetPreferredIconMapping();
            if (general == null)
            {
                _iconMapReady = false;
                return;
            }
            var icons = general;

            var valid = GetValidIconTags();
            if (valid == null || valid.Count == 0)
            {
                // 表对象有了但条目还没填充：先挂上，等条目就绪后再清理文本。
                AssignIconMapping(_transcript, icons);
                foreach (var item in _items) AssignIconMapping(item.Label, icons);
                if (_thinking != null) AssignIconMapping(_thinking.Body, icons);
                foreach (var block in _toolBlocks.Values) AssignIconMapping(block.Body, icons);
                return;
            }

            var firstReady = !_iconMapReady;
            _iconMapReady = true;

            AssignIconMapping(_transcript, icons);
            foreach (var item in _items)
            {
                if (firstReady) ResanitizeLabel(item.Label);
                AssignIconMapping(item.Label, icons);
            }
            if (_thinking != null)
            {
                if (firstReady) ResanitizeLabel(_thinking.Body);
                AssignIconMapping(_thinking.Body, icons);
            }
            foreach (var block in _toolBlocks.Values)
            {
                if (firstReady) ResanitizeLabel(block.Body);
                AssignIconMapping(block.Body, icons);
            }
        }

        private static void ResanitizeLabel(Text label)
        {
            if (label == null || string.IsNullOrEmpty(label.text)) return;
            var cleaned = SanitizeRichText(label.text);
            if (string.Equals(cleaned, label.text, StringComparison.Ordinal)) return;
            label.text = cleaned;
            label.SetVerticesDirty();
            label.SetLayoutDirty();
        }

        /// <summary>
        /// 规范化图标 tag：
        ///   * `\\tag;` 且 tag 在当前图标表里 → 保留；
        ///   * `\\tag;` 但 tag 不存在 → 整段删掉（否则 Text 会画出 `tag;` 乱码）；
        ///   * **TAB + 可选空格 + `tag;`**：模型经常把 `\\t` 写成 JSON 转义，
        ///     解析后变成真正的 TAB；补回 `\\tag;` 让它渲染图标；
        ///   * **空白后的裸 `tag;`**（例如“： lvt; 引力矩阵”）也补反斜杠。
        /// 图标表没就绪时先把 TAB 短码提升成反斜杠形式，等表好了再清一次。
        /// </summary>
        private static string SanitizeIconTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\\') < 0 && s.IndexOf('\t') < 0) return s;

            var valid = GetValidIconTags();
            if (valid == null || valid.Count == 0)
                return PromoteTabIconTags(s);

            StringBuilder sb = null;
            var last = 0;
            for (var i = 0; i < s.Length;)
            {
                var marker = s[i] == '\\' || s[i] == '\t';
                var tagStart = i;
                if (marker)
                {
                    tagStart = i + 1;
                    if (s[i] == '\t')
                    {
                        while (tagStart < s.Length && (s[tagStart] == ' ' || s[tagStart] == '\t'))
                            tagStart++;
                    }
                }
                else if (!IsIconTagChar(s[i]) || (i > 0 && !char.IsWhiteSpace(s[i - 1])))
                {
                    i++;
                    continue;
                }

                var j = tagStart;
                while (j < s.Length && j - tagStart < 6 && IsIconTagChar(s[j])) j++;
                if (j == tagStart) { i++; continue; }

                var nameForm = j < s.Length && s[j] == '-';
                var tagEnd = j;
                if (nameForm) j++;
                if (j >= s.Length || s[j] != ';') { i++; continue; }

                var tag = s.Substring(tagStart, tagEnd - tagStart);
                var isValid = valid.Contains(tag);

                if (marker && s[i] == '\\' && isValid)
                {
                    i = j + 1;
                    continue;
                }

                if (!isValid)
                {
                    if (!marker)
                    {
                        i = j + 1;
                        continue;
                    }
                    // 未知的反斜杠/TAB 短码：整段删掉，保留后面的名字。
                    if (sb == null) sb = new StringBuilder(s.Length);
                    sb.Append(s, last, i - last);
                    last = j + 1;
                    i = j + 1;
                    continue;
                }

                // 有效但形式不对：TAB+空格 或裸短码 → 补成 \tag;。
                if (sb == null) sb = new StringBuilder(s.Length + 8);
                sb.Append(s, last, i - last);
                if (i > 0 && !char.IsWhiteSpace(s[i - 1])) sb.Append(' ');
                sb.Append('\\');
                sb.Append(s, tagStart, tagEnd - tagStart);
                if (nameForm) sb.Append('-');
                sb.Append(';');
                last = j + 1;
                i = j + 1;
            }

            if (sb == null) return s;
            sb.Append(s, last, s.Length - last);
            return sb.ToString();
        }

        /// <summary>表未就绪时：把 TAB + 可选空格 + 短码 + ';' 提升成反斜杠形式。</summary>
        private static string PromoteTabIconTags(string s)
        {
            if (s.IndexOf('\t') < 0) return s;

            StringBuilder sb = null;
            var last = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\t') continue;

                var j = i + 1;
                while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
                var tagStart = j;
                while (j < s.Length && j - tagStart < 6 && IsIconTagChar(s[j])) j++;
                if (j == tagStart) continue;
                var nameForm = j < s.Length && s[j] == '-';
                var tagEnd = j;
                if (nameForm) j++;
                if (j >= s.Length || s[j] != ';') continue;

                if (sb == null) sb = new StringBuilder(s.Length + 8);
                sb.Append(s, last, i - last);
                if (i > 0 && !char.IsWhiteSpace(s[i - 1])) sb.Append(' ');
                sb.Append('\\');
                sb.Append(s, tagStart, tagEnd - tagStart);
                if (nameForm) sb.Append('-');
                sb.Append(';');
                last = j + 1;
                i = j;
            }

            if (sb == null) return s;
            sb.Append(s, last, s.Length - last);
            return sb.ToString();
        }

        private static bool IsIconTagChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        }

        /// <summary>
        /// 气泡宽度：文本短就让它收缩到刚好包住文字，只有超过上限才封顶换行。
        /// 宽度值写在 `bubble` 的 LayoutElement 上（prefab 里就是这么做的）。
        /// </summary>
        private void FitBubbleWidth(GameObject go, string text)
        {
            if (go == null) return;
            var bubble = FindNamed(go.transform, "bubble");
            if (bubble == null) return;
            var le = bubble.GetComponent<LayoutElement>();
            if (le == null) return;

            var estimate = EstimateTextWidth(text, _fontSize);
            le.preferredWidth = estimate + 2f * BubblePaddingX > MaxBubbleWidth
                ? MaxBubbleWidth
                : -1f;            // -1 = 不设宽，由 VerticalLayoutGroup 收缩包裹
        }

        private static float EstimateTextWidth(string text, int fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            var widest = 0f;
            var line = 0f;
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    if (line > widest) widest = line;
                    line = 0f;
                    continue;
                }
                line += c > 0x2E80 ? fontSize : fontSize * 0.55f;   // 汉字按全角估
            }
            return line > widest ? line : widest;
        }

        /// <summary>统计全角（含汉字）字符数；block 标题的字距补偿按它算。</summary>
        private static int CountWideChars(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var count = 0;
            foreach (var c in text)
                if (c > 0x2E80) count++;
            return count;
        }

        private static Transform FindNamed(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == name) return t;
            }
            return null;
        }

        private void TrimHistory()
        {
            // 流式输出期间 _items 比 _lines 多一条，先不裁，避免下标错位。
            while (_streamItem < 0 && _lines.Count > _maxLines)
            {
                RemoveOldestLine();
            }

            var total = 0;
            foreach (var l in _lines) total += l.Text != null ? l.Text.Length : 0;
            while (_streamItem < 0 && total > MaxTranscriptChars && _lines.Count > 0)
            {
                total -= _lines[0].Text != null ? _lines[0].Text.Length : 0;
                RemoveOldestLine();
            }

            PruneIndexTurns();
        }

        /// <summary>聊天记录裁剪后，把整轮都已消失的索引项一起移除。</summary>
        private void PruneIndexTurns()
        {
            if (_turns.Count == 0) return;
            var changed = false;
            for (int i = _turns.Count - 1; i >= 0; i--)
            {
                var entry = _turns[i];
                var found = false;
                foreach (var line in _lines)
                {
                    if (line.TurnId != entry.Id) continue;
                    found = true;
                    break;
                }
                if (found) continue;

                if (entry.Row != null) UnityEngine.Object.Destroy(entry.Row);
                _turns.RemoveAt(i);
                changed = true;
            }
            if (!changed) return;
            RefreshIndexSelection();
            RefreshIndexEmpty();
        }

        private void RemoveOldestLine()
        {
            if (_lines.Count == 0) return;
            var line = _lines[0];
            if (line.Block != null) ForgetBlock(line.Block);
            if (line.Go != null) UnityEngine.Object.Destroy(line.Go);
            _lines.RemoveAt(0);
            RemoveItemByGo(line.Go);
        }

        private void RemoveItemByGo(GameObject go)
        {
            if (go == null) return;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Go != go) continue;
                _items.RemoveAt(i);
                if (_streamItem > i) _streamItem--;
                return;
            }
        }

        /// <summary>
        /// 从 content 里取 `example-*` 示例节点当消息模板，其余示例删除，模板自身置为不激活。
        ///
        /// 用户气泡优先挑**没设 preferredWidth** 的那个（能自然包裹文本），
        /// 这样短消息的小气泡不会被撑成固定宽度；宽度上限在运行期按文本估宽再决定。
        /// </summary>
        private void CaptureTemplates(RectTransform content)
        {
            if (content == null) return;

            var seen = new List<GameObject>();
            foreach (Transform child in content)
            {
                if (child.name == null || !child.name.StartsWith("example-")) continue;
                seen.Add(child.gameObject);

                if (_userTemplate == null && child.name.StartsWith("example-user"))
                {
                    var bubble = FindNamed(child, "bubble");
                    var le = bubble != null ? bubble.GetComponent<LayoutElement>() : null;
                    if (le == null || le.preferredWidth <= 0f) _userTemplate = child as RectTransform;
                }
                else if (_assistantTemplate == null && child.name.StartsWith("example-assistant"))
                {
                    _assistantTemplate = child as RectTransform;
                }
            }

            // 两个用户示例都设了宽度时，退回第一个
            if (_userTemplate == null)
            {
                foreach (var go in seen)
                {
                    if (go.name.StartsWith("example-user"))
                    {
                        _userTemplate = go.transform as RectTransform;
                        break;
                    }
                }
            }

            foreach (var go in seen)
            {
                if (_userTemplate != null && go.transform == _userTemplate) continue;
                if (_assistantTemplate != null && go.transform == _assistantTemplate) continue;
                UnityEngine.Object.Destroy(go);
            }

            if (_userTemplate != null) _userTemplate.gameObject.SetActive(false);
            if (_assistantTemplate != null) _assistantTemplate.gameObject.SetActive(false);

            Plugin.Log.LogInfo($"[UI] 消息模板：用户={( _userTemplate != null ? _userTemplate.name : "无（退化为裸文本）")}，"
                               + $"助手={(_assistantTemplate != null ? _assistantTemplate.name : "无（退化为裸文本）")}");
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
            if (_root == null) return;

            // 分辨率 / 游戏 UI 缩放变了 → 重新对一次面板缩放（1 单位 = 1 像素）
            if (_hostCanvas != null)
            {
                float sf = Mathf.Max(0.01f, _hostCanvas.scaleFactor);
                if (!Mathf.Approximately(sf, _canvasScale)) ApplyHostScaleAndLayer();
            }

            // 原生提示层偶尔会被游戏重建（场景切换 / 提示系统初始化晚于本窗口），
            // 低频补一次，保证提示始终在窗口之上（Ensure 幂等，已就绪时几乎零开销）。
            if (++_tipLayerTick % 60 == 0) NativeTipLayer.Ensure(Game.GameRefs.UiRoot);

            if (!ViewVisible) return;

            // VFInput 的状态每帧由游戏输入系统重算，单次赋值可能被覆盖，
            // 因此窗口打开期间每帧重申一次；关闭时由 SetOpen 还原原值。
            // ESC：聊天窗开着时由我们消费掉，否则同一次按键会同时关窗并打开游戏的暂停菜单。
            // 游戏自己的窗口也是这个套路：先 UseEscape() 再处理。
            if (IsOpen && !_keyEntryMode && VFInput.escape)
            {
                VFInput.UseEscape();
                SetOpen(false);
                return;
            }

            ModelSettingsTab.Watch();   // 设置窗口打开时自动补「模型」页签

            if (IsOpen) Game.GameRefs.SetTextInputActive(true);
            if (IsOpen) EnsureThinkingComboText();
            if (IsOpen) EnsureModelComboText();

            // Enter 进入输入状态（此时 R 才让位给打字）
            if (IsOpen && !InputFocused && UnityEngine.Input.GetKeyDown(KeyCode.Return))
                FocusInput();

            // 期望聚焦但实际没聚焦 → 重申（游戏可能清空了 EventSystem 的选中对象）
            if (IsOpen && _wantInputFocus && _input != null && !_input.isFocused)
                ApplyFocus();

            // 焦点有了但游戏 InputField 的 Caret 可能还没创建/绑定材质。
            if (IsOpen && _input != null && _input.isFocused)
            {
                if (!_caretPrimed) PrimeInputCaret();
            }
            else
            {
                _caretPrimed = false;
            }

            // 图标表可能比窗口/消息晚就绪（主菜单 → 存档加载）；低频补齐即可。
            if (IsOpen && ++_iconMapTick % 30 == 0) EnsureIconMappings();

            // 多行输入框里按了回车：在 Tick 里落地提交（见 OnInputNewLine）
            if (_submitPending)
            {
                _submitPending = false;
                SubmitPendingInput();
            }

            if (_dirty)
            {
                _dirty = false;
                Render();
            }

            if (IsOpen) EnsureInputCharColorHeadroom();

            // 开窗时那次重绑发生在"输入框还是空的"时刻；CanvasRenderer 没有网格时
            // 绑定不一定站得住，所以第一段文字出现后再绑一次（每次开窗只做一次）。
            if (IsOpen && _input != null && _input.text != _lastInputText)
            {
                _lastInputText = _input.text;
                // 文本变化后 InputField 可能重建 Caret 网格，下一帧重新补一次。
                _caretPrimed = false;
                if (!string.IsNullOrEmpty(_input.text) && !_textMaterialRebound)
                {
                    _textMaterialRebound = true;
                    RebindInputTextMaterial(_input.textComponent, "首次输入");
                }
            }
        }


        private void Render()
        {
            // 只有流式输出需要按帧刷新：直接改那一条消息的 Text。
            // 富文本消毒放在**渲染时**（而不是逐段 append 时）：
            // 流式回复的标签可能被拆到两个 delta 里，逐段处理会把标签打断。
            if (_streamItem >= 0 && _streamItem < _items.Count)
            {
                var item = _items[_streamItem];
                if (item.Label != null) item.Label.text = SanitizeRichText(_stream.ToString());
            }

            if (_suppressAutoScrollOnce) _suppressAutoScrollOnce = false;
            else ScrollToBottom();
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

            // 关键：config.state 每次变化都会调用 ExitKeyEntry()。之前这里无条件
            // `_input.text = ""`，导致切换模型/思考档位收到 config.state 时把玩家
            // 正在输入的内容清空。只有确实处于密钥录入态才需要清理和恢复。
            if (!_keyEntryMode) return;

            _keyEntryMode = false;
            _input.contentType = InputField.ContentType.Standard;
            _input.text = string.Empty;
            if (_input.placeholder is Text ph)
                ph.text = _authoredPlaceholder ?? Strings.Get("input.placeholder");

            AppendSystem(Strings.Get("key.saved"));
        }

        // ---------------------------------------------------------------- 输入

        /// <summary>
        /// 多行输入框里按下回车。
        ///
        /// 多行模式下 UGUI 不会走 onEndEdit，而是插一个换行、再抛 `OnNewLine`（游戏改写版的行为）。
        /// 现在的约定是：
        ///
        ///   * **单独回车 = 发送**：把刚插进去的换行 trim 掉，清空并失焦
        ///   * Ctrl（macOS 上还有 Cmd）+ 回车 = 换行（保留换行，走下面的复选框续写逻辑）
        ///
        /// 事件是在按键处理中间同步抛出来的，此刻直接改 text 会重入 InputField，
        /// 所以只置标志，真正提交放到本帧的 `Tick()` 里做。
        /// </summary>
        private void OnInputNewLine(string text, int newlineIndex)
        {
            if (!IsNewlineModifierHeld())
            {
                _submitPending = true;
                return;
            }

            // 原生 UISmartInputField 的行为：上一行以 [-] / [+] 开头时，新行自动补一个复选框，
            // 这样连续记待办不用每行都点一次按钮。
            try
            {
                if (_input == null || newlineIndex <= 0) return;
                var all = _input.text;
                if (string.IsNullOrEmpty(all)) return;

                const string box = "[-]";
                int lineStart = all.LastIndexOf('\n', newlineIndex - 1) + 1;
                if (lineStart + box.Length > newlineIndex) return;
                if (all[lineStart] != '[' || all[lineStart + 2] != ']') return;
                if (all[lineStart + 1] != '-' && all[lineStart + 1] != '+') return;

                _input.selectionFocusPosition = _input.selectionAnchorPosition = newlineIndex + 1;
                _input.Append(box);
                _input.selectionFocusPosition = _input.selectionAnchorPosition = newlineIndex + 1 + box.Length;
                _input.CalculateColorText(true);
                _input.ForceLabelUpdate();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"自动续写复选框失败：{ex.Message}");
            }
        }

        /// <summary>Ctrl（macOS 上还认 Cmd）是否按着——按着 = 换行，不按 = 发送。</summary>
        private static bool IsNewlineModifierHeld()
        {
            // 注意：在本命名空间里 `Input` 会被解析成 IcarusII.Input 命名空间，必须限定 UnityEngine。
            return UnityEngine.Input.GetKey(KeyCode.LeftControl)
                || UnityEngine.Input.GetKey(KeyCode.RightControl)
                || UnityEngine.Input.GetKey(KeyCode.LeftCommand)
                || UnityEngine.Input.GetKey(KeyCode.RightCommand);
        }

        private void SubmitPendingInput()
        {
            if (_input == null) return;

            var raw = _input.text;
            var value = raw != null ? raw.Trim() : string.Empty;

            _input.text = string.Empty;
            if (value.Length == 0) return;

            // 与单行路径一致：发送后主动失焦，玩家可直接按 R 关窗
            _input.DeactivateInputField();

            try
            {
                if (_keyEntryMode)
                {
                    KeySubmitted?.Invoke(value);
                }
                else
                {
                    Submitted?.Invoke(value);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"提交失败：{ex}");
            }
        }

        /// <summary>
        /// 接上窗口右上角的设置按钮（关闭按钮左边那一枚）。
        ///
        /// 这枚按钮是从游戏原生窗口（机甲信息窗）抄来的「功能菜单」：
        ///
        ///   setting-wnd-btn      ← 只有 UIGenericMenuButton（没有 Graphic、没有 Button）
        ///     menubtn            ← 真正的 UIButton（悬停动效 + 点击）
        ///       col              ← 透明命中层（raycastTarget = true）
        ///       mbicon           ← 齿轮图标
        ///
        /// 所以**不能**只在父节点上加一个 Unity `Button`：指针事件由 EventSystem 交给
        /// 命中的第一个 handler，`col` 会冒泡到 `menubtn` 的 `UIButton`，我们自己加的
        /// Button 永远收不到点击——实机表现就是"点⚙没反应"（日志里也只有
        /// "设置按钮缺 Button，已在运行期补上透明命中层"，之后没有任何 [设置] 记录）。
        ///
        /// 同时 `UIGenericMenuButton.Update` 每帧把悬停提示写回
        /// 「功能菜单 / 点击打开功能菜单。」——那是仪表盘"添加到仪表盘"菜单的提示，
        /// 和本窗口无关（截图里被窗口切掉一半的就是它）。
        ///
        /// 做法（全部在运行期，不改 prefab）：
        ///   1. 摘掉 `UIGenericMenuButton`；
        ///   2. 把点击接到它真正订阅的那个 `UIButton.onClick` 上；
        ///   3. 把悬停提示换成本窗口自己的文案。
        /// </summary>
        private void WireSettingsButton(RectTransform panel)
        {
            var node = FindNode(panel,
                "panel-bg/btn-box/setting-wnd-btn", "btn-box/setting-wnd-btn", "setting-wnd-btn",
                "panel-bg/btn-box/menu-wnd-btn", "btn-box/menu-wnd-btn", "menu-wnd-btn");

            if (node == null)
            {
                Plugin.Log.LogWarning("prefab 里没有设置按钮（setting-wnd-btn / menu-wnd-btn）。");
                return;
            }

            // 1) 摘掉原生「功能菜单」脚本（按名字取，避免依赖游戏类型名）。
            var genericMenu = node.GetComponent("UIGenericMenuButton");
            if (genericMenu != null)
            {
                UnityEngine.Object.Destroy(genericMenu);
                Plugin.Log.LogInfo("[设置] 已摘掉设置按钮上遗留的 UIGenericMenuButton（空菜单 + 假提示）。");
            }

            // 2) 点击：优先接原生 UIButton——它才是真正的命中目标。
            var nativeButton = node.GetComponentInChildren<UIButton>(true);
            if (nativeButton != null)
            {
                nativeButton.onClick += _ => OnSettingsButtonClicked();
                SetSettingsTip(nativeButton);
                Plugin.Log.LogInfo($"[设置] 设置按钮已接到 UIButton（{nativeButton.name}），点击 → 设置 · 模型页。");
                return;
            }

            // 3) 兜底：节点带 Unity Button 就直接用；什么都没有就补一层透明命中层。
            var button = node.GetComponent<Button>();
            if (button == null)
            {
                var hit = node.gameObject.GetComponent<Image>();
                if (hit == null) hit = node.gameObject.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                hit.raycastTarget = true;

                button = node.gameObject.AddComponent<Button>();
                button.targetGraphic = hit;
                Plugin.Log.LogInfo("[设置] 设置按钮没有 Button/UIButton，已在运行期补上透明命中层。");
            }

            button.onClick.AddListener(OnSettingsButtonClicked);
            Plugin.Log.LogInfo("[设置] 设置按钮已接到 Unity Button，点击 → 设置 · 模型页。");
        }

        /// <summary>
        /// 设置按钮的悬停提示，走游戏原生提示（样式和其它窗口一致）。
        /// 必须在摘掉 `UIGenericMenuButton` 之后写，否则会被它每帧覆盖回"功能菜单"。
        /// </summary>
        private static void SetSettingsTip(UIButton button)
        {
            try
            {
                var tips = button.tips;
                tips.tipTitle = Strings.Get("settings.tip.title");
                tips.tipText = Strings.Get("settings.tip.text");
                // prefab 里没配方向（corner=0 表示"永不显示提示"）时补一个右侧展开，
                // 和原生窗口"关闭按钮右边的提示"同一套观感。
                if (tips.corner == 0) tips.corner = 9;
                button.tips = tips;
                button.UpdateTip();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[设置] 设置按钮提示文案写入失败：" + ex.Message);
            }
        }

        /// <summary>右上角设置按钮：打开游戏原生设置窗口，并切到注入的"模型"页签。</summary>
        private void OnSettingsButtonClicked()
        {
            try
            {
                var uiRoot = Game.GameRefs.UiRoot;
                var window = uiRoot != null ? uiRoot.optionWindow : null;
                if (window == null)
                {
                    Plugin.Log.LogWarning("[设置] 拿不到游戏设置窗口 UIOptionWindow。");
                    return;
                }

                // 聊天窗的 Canvas sortingOrder 很高（30000），会盖住设置窗口，
                // 所以进设置前先把自己的窗口收起来。
                if (IsOpen) SetOpen(false);

                uiRoot.OpenOptionWindow();
                ModelSettingsTab.Open(window);
                Plugin.Log.LogInfo("[设置] 已打开游戏设置窗口并切到「模型」页签。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[设置] 打开设置窗口失败：" + ex.Message);
            }
        }

        private void OnCloseButtonClicked()
        {
            SetOpen(false);
        }

        // ------------------------------------------------ 输入工具条（图标 / 复选框 / 颜色）

        private Button _iconToolButton;
        private Button _colorToolButton;

        /// <summary>
        /// 给工具条三个按钮接上游戏自己的实现（对照 `UISmartInputField`）：
        ///   * 复选框 → `InputField.InsertCheckBox()`
        ///   * 图标   → `UISignalTagPicker.Popup(...)`，选中后插入 `LDB.signals.IconTag(id)`
        ///   * 颜色   → `UISimpleColorPicker.Popup(...)`，选中后 `InputField.OnColorChange(...)`
        /// 弹出的选择器因此和游戏里其它输入框完全一致（图标表、调色板、带名字的图标等都是原生的）。
        /// </summary>
        private void WireInputToolbar(RectTransform panel)
        {
            var iconBtn = FindComponent<Button>(panel,
                "input/editor/tool-bar/icons/icon-select-btn", "tool-bar/icons/icon-select-btn", "icon-select-btn");
            var checkBtn = FindComponent<Button>(panel,
                "input/editor/tool-bar/icons/checkbox-btn", "tool-bar/icons/checkbox-btn", "checkbox-btn");
            var colorBtn = FindComponent<Button>(panel,
                "input/editor/tool-bar/icons/text-color-btn", "tool-bar/icons/text-color-btn", "text-color-btn");

            _iconToolButton = iconBtn;
            _colorToolButton = colorBtn;

            if (iconBtn != null) iconBtn.onClick.AddListener(OnInsertIconClicked);
            if (checkBtn != null) checkBtn.onClick.AddListener(OnInsertCheckBoxClicked);
            if (colorBtn != null) colorBtn.onClick.AddListener(OnPickTextColorClicked);

            if (iconBtn == null || checkBtn == null || colorBtn == null)
            {
                Plugin.Log.LogWarning(
                    $"输入工具条不完整（图标={iconBtn != null} 复选框={checkBtn != null} 颜色={colorBtn != null}），只接上存在的按钮。");
            }
            else
            {
                Plugin.Log.LogInfo("[UI] 已接上输入工具条：插入图标 / 复选框 / 文字颜色。");
            }
        }

        /// <summary>
        /// 选择器弹窗关掉后把键盘焦点还给输入框。
        ///
        /// ⚠️ 不要用 FocusInput()/ActivateInputField()：那会重排光标，
        /// 而游戏的 InputField 在**任何光标移动**里都会把 m_BrushColor 重置成 -2
        /// （语义是"复制前一个字符的颜色"）——刚在取色器里选的画笔色就白选了。
        /// 直接把 EventSystem 的选中对象指回输入框即可（isFocused 就是看这个），
        /// 光标不动，画笔色保留。
        /// </summary>
        private void RestoreInputFocus()
        {
            if (_input == null) return;
            _wantInputFocus = true;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.currentSelectedGameObject != _input.gameObject)
                es.SetSelectedGameObject(_input.gameObject);
        }

        private void OnInsertCheckBoxClicked()
        {
            if (_input == null) return;
            try
            {
                // 原生就是这一句：插入 `[-]`（Text 会画成复选框）
                var before = _input.text ?? string.Empty;
                _input.InsertCheckBox();
                _input.ForceLabelUpdate();
                RestoreInputFocus();
                Plugin.Log.LogInfo($"[工具条] 复选框：'{before}' → '{_input.text}' caret={_input.caretPosition} 焦点={_input.isFocused} 显示文本='{(_input.textComponent != null ? _input.textComponent.text : "null")}'");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"插入复选框失败：{ex.Message}");
            }
        }

        private void OnInsertIconClicked()
        {
            if (_input == null) return;
            try
            {
                if (UISignalTagPicker.isOpened)
                {
                    UISignalTagPicker.Close();
                    return;
                }

                var pickerTrans = UISignalTagPicker._PickerTrans;
                // 先把自己画布抬到聊天窗之上，否则选择器会整块藏在窗口后面
                BoostPickerAboveWindow(pickerTrans, "图标");
                var pos = PickerPosition(_iconToolButton, pickerTrans);

                UISignalTagPicker.Popup(pos, signalId =>
                {
                    if (signalId == 0 || _input == null) return;
                    var tag = LDB.signals.IconTag(signalId);
                    if (string.IsNullOrEmpty(tag)) return;
                    // isIconTag=true：插入 `\图标标签;`，Text 靠 iconMapping 把它画成图标
                    var before = _input.text ?? string.Empty;
                    _input.InsertGeneralString(tag, true);
                    _input.ForceLabelUpdate();
                    RestoreInputFocus();
                    Plugin.Log.LogInfo($"[工具条] 图标：tag='{tag}' '{before}' → '{_input.text}'");
                });

                LogPickerState("图标", pickerTrans, pos);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"打开图标选择器失败：{ex.Message}");
            }
        }

        private void OnPickTextColorClicked()
        {
            if (_input == null) return;
            try
            {
                if (UISimpleColorPicker.isOpened)
                {
                    UISimpleColorPicker.Close();
                    return;
                }

                var picker = UIRoot.instance != null && UIRoot.instance.uiGame != null
                    ? UIRoot.instance.uiGame.simpleColorPicker
                    : null;
                var pickerTrans = picker != null ? picker.pickerTrans : null;
                BoostPickerAboveWindow(pickerTrans, "颜色");
                var pos = PickerPosition(_colorToolButton, pickerTrans);

                UISimpleColorPicker.Popup(pos, GetBrushColor(),
                    idx => { if (_input != null) { _input.OnColorChange(idx); RestoreInputFocus(); } },
                    alpha => { if (_input != null) { _input.OnColorChange(alpha, true); RestoreInputFocus(); } });

                LogPickerState("颜色", pickerTrans, pos);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"打开取色器失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 把游戏选择器所在的节点抬到聊天窗画布（30000）之上。
        /// 选择器根节点平时是 inactive 的，这里只加组件；等 `_Open()` 激活时生效。
        /// </summary>
        private static void BoostPickerAboveWindow(RectTransform pickerTrans, string label)
        {
            if (pickerTrans == null)
            {
                Plugin.Log.LogWarning($"[{label}] 拿不到选择器 RectTransform，无法提升画布层级。");
                return;
            }

            try
            {
                var boost = pickerTrans.GetComponent<PickerCanvasBoost>();
                if (boost == null) boost = pickerTrans.gameObject.AddComponent<PickerCanvasBoost>();
                boost.Apply(30001);

                var parentCanvas = pickerTrans.parent != null ? pickerTrans.parent.GetComponentInParent<Canvas>() : null;
                Plugin.Log.LogInfo(
                    $"[{label}] 选择器画布 → 30001（聊天窗 30000；原画布 " +
                    $"{(parentCanvas != null ? parentCanvas.sortingOrder.ToString() : "?")}）");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[{label}] 提升选择器画布失败：{ex.Message}");
            }
        }

        /// <summary>打开后把选择器的真实状态写进日志（排查"点了没反应"用）。</summary>
        private void LogPickerState(string label, RectTransform pickerTrans, Vector2 pos)
        {
            try
            {
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                ManualBehaviour picker = null;
                if (uiGame != null)
                {
                    picker = label == "图标" ? (ManualBehaviour)uiGame.signalTagPicker : uiGame.simpleColorPicker;
                }
                var canvas = pickerTrans != null ? pickerTrans.GetComponent<Canvas>() : null;
                var corners = new Vector3[4];
                if (pickerTrans != null) pickerTrans.GetWorldCorners(corners);
                var cam = _hostCanvas != null ? _hostCanvas.worldCamera : null;
                var screen = cam != null
                    ? (Vector2)RectTransformUtility.WorldToScreenPoint(cam, corners[0])
                    : (Vector2)corners[0];
                Plugin.Log.LogInfo(
                    $"[{label}] 选择器状态：picker={(picker != null)} " +
                    $"inited={(picker != null && picker.inited)} active={(picker != null && picker.active)} " +
                    $"pos=({pos.x:0}, {pos.y:0}) 画布单位/像素={_canvasScale:0.###} " +
                    $"屏幕左上=({screen.x:0}, {screen.y:0}) " +
                    $"canvas={(canvas != null ? canvas.sortingOrder.ToString() : "null")} " +
                    $"override={(canvas != null && canvas.overrideSorting)} " +
                    $"宿主={( _hostCanvas != null ? "嵌套 " + _hostCanvas.sortingOrder : "兜底 Overlay")}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[{label}] 读取选择器状态失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 选择器弹窗位置。游戏是用 `pickerTrans.anchoredPosition = pos` 直接放置的，
        /// 所以要把按钮位置换算到 **picker 父节点**（`Common Tools`）的局部坐标
        /// （照 `UIMarkerDesc.CalcSignalIconPickerPos` 的做法）。
        ///
        /// **单位**：按钮在面板里（1 单位 = 1 像素），`Common Tools` 在游戏画布坐标系里
        /// （1 单位 = `scaleFactor` 像素），所以偏移量要先把选择器宽度从画布单位
        /// 换算成像素（`* _canvasScale`），再交给 `TransformPoint`。
        /// 换算完 `InverseTransformPoint` 回来，结果就是画布单位的锚点坐标。
        ///
        /// picker 的 pivot 是左上角，所以 y 是它的**上边缘**：先尝试放在按钮上方
        /// （聊天输入栏在窗口底部，往下放会直接出屏），放不下再放下面，最后做边界夹取。
        /// 算不出来就退回原生兜底位置。
        /// </summary>
        private Vector2 PickerPosition(Button button, RectTransform pickerTrans)
        {
            var fallback = new Vector2(-206f, 194f);
            if (button == null || pickerTrans == null) return fallback;
            try
            {
                var parent = pickerTrans.parent as RectTransform;
                var rt = button.transform as RectTransform;
                if (parent == null || rt == null) return fallback;

                var pickerW = pickerTrans.rect.width;
                var pickerH = pickerTrans.rect.height;
                var halfW = parent.rect.width * 0.5f;
                var halfH = parent.rect.height * 0.5f;

                // `InverseTransformPoint` 得到的是**以父级 pivot 为原点**的局部坐标，
                // 而 `anchoredPosition` 是相对**锚点**（picker 的 anchor 是 0.5,0.5）的。
                // 两者差一个"pivot → 父级中心"的偏移；`Common Tools` 的 pivot 是 (0,0)，
                // 不减去这个偏移，选择器会被丢到屏幕左下角（原生 CalcSignalIconPickerPos
                // 里的 `-= rect.size * 0.5f` 就是在做这件事）。
                var centerOffset = new Vector2(
                    (0.5f - parent.pivot.x) * parent.rect.width,
                    (0.5f - parent.pivot.y) * parent.rect.height);

                // 选择器宽度是"游戏画布单位"，按钮的本地单位是"像素"
                var pxPerUnit = Mathf.Max(0.01f, _canvasScale);
                var pickerWpx = pickerW * pxPerUnit;

                // 默认放在按钮左侧；左边放不下就换到右侧（同原生 UIMarkerDesc）
                var world = rt.TransformPoint(new Vector3(-pickerWpx - 2f, 0f, 0f));
                var local = (Vector2)parent.InverseTransformPoint(world) - centerOffset;
                if (local.x < -halfW)
                {
                    world = rt.TransformPoint(new Vector3(rt.rect.width + 2f, 0f, 0f));
                    local = (Vector2)parent.InverseTransformPoint(world) - centerOffset;
                }

                // 先尝试按钮上方，再退回下方
                var y = local.y + pickerH;
                var yMax = halfH - 8f;
                if (y > yMax) y = local.y;

                var yMin = -halfH + pickerH;
                if (y < yMin) y = yMin;
                if (y > yMax) y = yMax;

                var x = Mathf.Clamp(local.x, -halfW + 8f, halfW - 8f);
                return new Vector2(Mathf.Round(x), Mathf.Round(y));
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>当前"画笔"颜色（照抄原生 UISmartInputField.GetBrushColor 的语义）。</summary>
        private short GetBrushColor()
        {
            var f = _input;
            if (f == null) return -1;
            if (f.selectionAnchorPosition != f.selectionFocusPosition) return -2;   // 有选区 → 按选区上色
            if (f.m_BrushColor >= -1) return f.m_BrushColor;
            if (f.m_CharColors != null && f.caretPosition > 0 && f.caretPosition <= f.m_CharColors.Length)
            {
                return (short)Mathf.Max(f.m_CharColors[f.caretPosition - 1], -1);
            }
            return -1;
        }

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
        /// <summary>
        /// 富文本消毒：**只放行游戏 Text 自己支持的标签**，其余 `<` 一律换成
        /// "看起来一样但不会被解析"的形式（`&lt;` + 零宽空格）。
        ///
        /// 放行清单（大小写不敏感）：
        ///   &lt;color=#RRGGBB&gt; / &lt;color=#RRGGBBAA&gt; / &lt;/color&gt;
        ///   &lt;i&gt; &lt;/i&gt; &lt;size=NN&gt; &lt;/size&gt;
        /// 游戏改版 UGUI 不支持 &lt;b&gt;，这里把 &lt;b&gt;/&lt;/b&gt; 转成亮白色 &lt;color&gt;/&lt;/color&gt;。
        ///
        /// 游戏自定义的图标标签写作 `\xxx;`（反斜杠 + 短码 + 分号），不含 `<`，原样通过。
        /// 这样一来 AI 只能用游戏支持的格式，玩家消息里的裸尖括号也不会打乱标签配对。
        /// </summary>
        private static string SanitizeRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = SanitizeIconTags(s);
            if (s.IndexOf('<') < 0) return s;

            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '<')
                {
                    sb.Append(s[i]);
                    continue;
                }

                int end = s.IndexOf('>', i + 1);
                if (end > i && IsAllowedTag(s, i, end))
                {
                    var tagLength = end - i + 1;
                    // 游戏自带的 UGUI 改版**没有实现 <b>**（RichTextType 里根本没有 Bold），
                    // 直接放行只会显示成字面量。这里统一转成受支持的亮白色强调，
                    // 保证 ``<b>加粗</b>`` 至少是“被强调”的，而不是把标签画出来。
                    if (tagLength == 3 && (s[i + 1] == 'b' || s[i + 1] == 'B'))
                    {
                        sb.Append("<color=#FFFFFF>");
                    }
                    else if (tagLength == 4 && s[i + 1] == '/' &&
                             (s[i + 2] == 'b' || s[i + 2] == 'B'))
                    {
                        sb.Append("</color>");
                    }
                    else
                    {
                        sb.Append(s, i, end - i + 1);
                    }
                    i = end;
                }
                else
                {
                    sb.Append("<\u200B");
                }
            }
            return sb.ToString();
        }

        /// <summary>`s[start] == '<'`、`s[end] == '>'`，判断这个标签是否在放行清单里。</summary>
        private static bool IsAllowedTag(string s, int start, int end)
        {
            int i = start + 1;
            bool closing = i < end && s[i] == '/';
            if (closing) i++;

            int nameStart = i;
            while (i < end && char.IsLetter(s[i])) i++;
            if (i == nameStart) return false;

            var name = s.Substring(nameStart, i - nameStart).ToLowerInvariant();

            if (closing)
            {
                return i == end && (name == "color" || name == "b" || name == "i" || name == "size");
            }

            switch (name)
            {
                case "b":
                case "i":
                    return i == end;
                case "size":
                    return IsTagValue(s, i, end, withHash: false, minDigits: 1, maxDigits: 3);
                case "color":
                    return IsTagValue(s, i, end, withHash: true, minDigits: 6, maxDigits: 8);
                default:
                    return false;
            }
        }

        /// <summary>校验 `=<可选#><十六进制数字>` 这一段。</summary>
        private static bool IsTagValue(string s, int i, int end, bool withHash, int minDigits, int maxDigits)
        {
            if (i >= end || s[i] != '=') return false;
            i++;
            if (withHash)
            {
                if (i >= end || s[i] != '#') return false;
                i++;
            }

            int digits = 0;
            while (i < end && IsHexDigit(s[i]))
            {
                i++;
                digits++;
            }
            return i == end && digits >= minDigits && digits <= maxDigits;
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // 只处理会破坏 <color>/<b> 标签的尖括号；中括号等无需处理。
            return s.IndexOf('<') < 0 ? s : s.Replace("<", "<\u200B");
        }
    }
}
