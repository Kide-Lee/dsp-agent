using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using IcarusII.Common.Protocol;
using IcarusII.Common.Text;
using IcarusII.Config;
using IcarusII.Game;
using IcarusII.Game.Tools;
using IcarusII.I18n;
using IcarusII.Input;

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

        /// <summary>盖在游戏 UI 之上的 Canvas 排序值。</summary>
        private const int CanvasSortingOrder = 30000;

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

        /// <summary>GameMain.gameTick 是 60 tick/秒，游戏时间按它换算。</summary>
        private const long GameTickPerSecond = 60L;
        /// <summary>删除按钮悬停时的红色（与标题/时间标记的常态色区分开）。</summary>
        private static readonly Color ConversationDeleteHoverColor = new Color(1f, 0.36f, 0.36f, 1f);
        /// <summary>置顶按钮悬停时的亮青蓝：比标题色和主题蓝都更亮，一眼能看出被点亮。</summary>
        private static readonly Color ConversationPinHoverColor = new Color(0.45f, 0.87f, 1f, 1f);
        /// <summary>左栏记录字体跟随右栏滚动位置时的扫描间隔（帧）。</summary>
        private const int IndexTurnScanIntervalFrames = 5;
        /// <summary>左栏展开/收起时的高度动画时长（秒）。</summary>
        private const float IndexExpandDuration = 0.16f;
        /// <summary>左栏会话换位（置顶、新回复）时的滑动时长（秒）。</summary>
        private const float IndexReorderDuration = 0.2f;
        /// <summary>标题还没排版完成时用的兜底宽度；等于 prefab 里 title-mask 的实际宽度。</summary>
        private const float ConversationTitleFallbackWidth = 150f;

        /// <summary>UIButton.onClick 是 event，外部只能 +=/-=；这里用它清理自己挂的订阅。</summary>
        private static readonly System.Reflection.FieldInfo UiButtonOnClickField =
            typeof(UIButton).GetField("onClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

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

        /// <summary>窗口当前是否可见。</summary>
        private bool ViewVisible => _root != null && _root.activeSelf;
        private RectTransform _panel;
        private RectTransform _content;
        private Text _statusText;
        private Text _titleText;
        /// <summary>标题文字来自 prefab（设计稿）：字体/字号/材质/颜色都不许运行期改。</summary>
        private bool _titleFromPrefab;
        private Text _transcript;
        private ScrollRect _scroll;
        private InputField _input;
        /// <summary>
        /// 关窗时暂存的未发送草稿；null = 窗口没关过或草稿已被发送。
        /// 游戏的 UIInputField 在 OnDisable 里会清掉文本，光靠"关窗不清空"留不住。
        /// </summary>
        private string _draftInput;
        /// <summary>输入栏根节点；ask 期间被 choose 替换（只切 active，不改尺寸）。</summary>
        private RectTransform _inputRoot;
        /// <summary>输出区根节点；尺寸/底边由 prefab 固定，运行期只做滚动。</summary>
        private RectTransform _outputRoot;
        private RectTransform _bottomColumn;
        /// <summary>当前替换输入栏的面板（目前是 choose；将来可扩展其它面板）。</summary>
        private RectTransform _bottomReplacement;
        private RectTransform _chooseRoot;
        private AskPanel _askPanel;
        private bool _caretPrimed;
        /// <summary>上一帧输入框是否有焦点；用于捕捉"刚聚焦"这一刻。</summary>
        private bool _lastInputFocused;
        /// <summary>开窗恢复草稿后armed：聚焦若把文本清掉，允许补回一次，直到玩家开始编辑。</summary>
        private bool _draftRestoreArmed;
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
        // 游戏版 InputField 的可见文本是 m_ColorText（带颜色标签），聚焦/失焦走不同分支。
        private static readonly FieldInfo InputColorTextField =
            typeof(InputField).GetField("m_ColorText",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
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
            /// <summary>工具/思考块（撤回整轮时一起移除）；普通消息为 null。</summary>
            public Block Block;
            /// <summary>这条回复里的蓝图卡片（可多张，一行 2~3 张）。</summary>
            public List<BlueprintCardInfo> Cards;
            /// <summary>这条回复里的链接卡片（建筑/星球）；按钮状态跟随 GameTether。</summary>
            public List<GameTether.Target> LinkTargets;

            /// <summary>
            /// 助手回复里的思考块，按出现顺序一段一个。一次提问里模型可能
            /// 思考 → 工具 → 再思考 → …，每段都是独立的折叠块；
            /// 历史回放时按这个列表逐块重建。
            /// </summary>
            public List<ThinkingSegment> Thinking;
            /// <summary>
            /// 历史回放用：这一轮助手回答产生过的思考/工具步骤（协议 A.6 的 steps）。
            /// 窗口没建好时先记在这里，Create 后由 RebuildTranscript 逐块补建。
            /// </summary>
            public List<HistoryStepInfo> Steps;
            /// <summary>回复底部的游戏时间（创建时记录，历史回放不再取当前时间）。</summary>
            public string Time;
        }

        /// <summary>一段思考正文 + 收起时的一行摘要。</summary>
        private struct ThinkingSegment
        {
            public string Text;
            public string Summary;
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
            /// <summary>助手回复：prefab 回复模板的绑定结果；旧 prefab/裸文本为 null。</summary>
            public ReplyView Reply;
            /// <summary>用户消息：气泡下面的 footer（只有时间 + 复制）。</summary>
            public GameObject Footer;
            public Text FooterTime;
            public Button FooterCopy;
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
            /// <summary>压缩记录行（“自动压缩上下文” + 摘要）；撤回它 = 撤销压缩。</summary>
            public bool IsCompaction;
            /// <summary>对应的 pi compaction entry id；为空时撤销最近一次。</summary>
            public string CompactionId;
            public int TokensBefore;
            public int EstimatedTokensAfter;
        }

        /// <summary>会话列表图标；Normal 显示展开/收起 chevron。</summary>
        private enum ConversationStatus
        {
            Normal,
            Running,
            Attention,
            Completed,
        }

        /// <summary>A.13：左侧索引里的一个会话分组。</summary>
        private sealed class ConversationView
        {
            public string Id;
            public ConversationInfo Info;
            public GameObject Go;
            public RectTransform Header;
            public RectTransform Records;
            public RectTransform TitleMask;
            public Text Title;
            public Color TitleColor;
            public Image StatusChevron;
            public Image StatusWarning;
            public Image StatusLoading;
            public Image StatusNew;
            public ChevronSpin StatusChevronSpin;
            public LoadingSpin StatusLoadingSpin;
            public ConversationIconHover DeleteHover;
            public ConversationIconHover PinHover;
            public ConversationHeaderHover HeaderHover;
            public Text TimeText;
            public GameObject ExpandMore;
            public Text ExpandMoreText;
            public bool Expanded;
            public int Visible;
            public readonly List<GameObject> Rows = new List<GameObject>();
            /// <summary>与 Rows 一一对应的记录；顺序是从新到旧。</summary>
            public readonly List<ConversationRecordInfo> RowRecords = new List<ConversationRecordInfo>();
        }

        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Item> _items = new List<Item>();

        /// <summary>
        /// 工具块：callId → 输出区里那个可折叠块。工具完成后**原地**更新标题/摘要/正文，
        /// 不会出现“读取中”和“完成”两块。
        /// </summary>
        private readonly Dictionary<string, Block> _toolBlocks =
            new Dictionary<string, Block>(StringComparer.Ordinal);

        /// <summary>每个 Text 自己的 linkId → URL 映射；链接点击只在该 Text 内查找。</summary>
        private readonly Dictionary<Text, Dictionary<int, string>> _linkTargets =
            new Dictionary<Text, Dictionary<int, string>>();
        /// <summary>已订阅的点击回调，避免同一个 Text 重复加 handler。</summary>
        private readonly Dictionary<Text, Action<UnityEngine.EventSystems.PointerEventData,
            UnityEngine.UI.Youthcat.TextTriggerBounds>> _linkHandlers =
            new Dictionary<Text, Action<UnityEngine.EventSystems.PointerEventData,
                UnityEngine.UI.Youthcat.TextTriggerBounds>>();

        private static TechProtoSet _techTagSource;
        private static Dictionary<string, int> _techIdsByTag;

        /// <summary>科技 IconTag（如 dcx）→ 科技 ID；表单器通过它把 dsp://tech/dcx 变成 link。</summary>
        private static int ResolveTechTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            var techs = LDB.techs;
            if (techs == null) return 0;

            if (!ReferenceEquals(techs, _techTagSource) || _techIdsByTag == null)
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var array = techs.dataArray;
                if (array != null)
                {
                    for (var i = 0; i < array.Length; i++)
                    {
                        var proto = array[i];
                        if (proto == null || string.IsNullOrEmpty(proto.IconTag)) continue;
                        if (!map.ContainsKey(proto.IconTag)) map[proto.IconTag] = proto.ID;
                    }
                }
                _techTagSource = techs;
                _techIdsByTag = map;
            }

            return _techIdsByTag.TryGetValue(tag.Trim(), out var id) ? id : 0;
        }

        private static ItemProtoSet _itemTagSource;
        private static Dictionary<string, int> _itemIdsByTag;
        private static RecipeProtoSet _recipeTagSource;
        private static Dictionary<string, int> _recipeResultByTag;
        private static AchievementProtoSet _achievementSidSource;
        private static Dictionary<string, int> _achievementIdsBySid;

        /// <summary>物品/配方 IconTag → 物品 ID；配方链接最终打开合成器里的这个物品。</summary>
        private static int ResolveRecipeTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            var key = tag.Trim();

            var items = LDB.items;
            if (items != null &&
                (!ReferenceEquals(items, _itemTagSource) || _itemIdsByTag == null))
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var array = items.dataArray;
                if (array != null)
                {
                    for (var i = 0; i < array.Length; i++)
                    {
                        var proto = array[i];
                        if (proto == null || string.IsNullOrEmpty(proto.IconTag)) continue;
                        if (!map.ContainsKey(proto.IconTag)) map[proto.IconTag] = proto.ID;
                    }
                }
                _itemTagSource = items;
                _itemIdsByTag = map;
            }
            if (_itemIdsByTag != null && _itemIdsByTag.TryGetValue(key, out var itemId))
                return itemId;

            // 也接受配方自己的 IconTag：取第一个产物物品作为合成器目标。
            var recipes = LDB.recipes;
            if (recipes != null &&
                (!ReferenceEquals(recipes, _recipeTagSource) || _recipeResultByTag == null))
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var array = recipes.dataArray;
                if (array != null)
                {
                    for (var i = 0; i < array.Length; i++)
                    {
                        var proto = array[i];
                        if (proto == null || string.IsNullOrEmpty(proto.IconTag)) continue;
                        if (proto.Results == null || proto.Results.Length == 0) continue;
                        if (!map.ContainsKey(proto.IconTag)) map[proto.IconTag] = proto.Results[0];
                    }
                }
                _recipeTagSource = recipes;
                _recipeResultByTag = map;
            }
            return _recipeResultByTag != null && _recipeResultByTag.TryGetValue(key, out var result)
                ? result
                : 0;
        }

        /// <summary>成就 ID 或 SID（ACHIEVEMENT_1）→ 成就 ID。</summary>
        private static int ResolveAchievementKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            var value = key.Trim();

            if (int.TryParse(value, out var numeric))
            {
                var achievements = LDB.achievements;
                if (achievements != null && achievements.Select(numeric) != null) return numeric;
                return 0;
            }

            var set = LDB.achievements;
            if (set == null) return 0;
            if (!ReferenceEquals(set, _achievementSidSource) || _achievementIdsBySid == null)
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var array = set.dataArray;
                if (array != null)
                {
                    for (var i = 0; i < array.Length; i++)
                    {
                        var proto = array[i];
                        if (proto == null || string.IsNullOrEmpty(proto.SID)) continue;
                        if (!map.ContainsKey(proto.SID)) map[proto.SID] = proto.ID;
                    }
                }
                _achievementSidSource = set;
                _achievementIdsBySid = map;
            }
            return _achievementIdsBySid.TryGetValue(value, out var id) ? id : 0;
        }

        private static readonly RichTextLinkResolvers LinkResolvers = new RichTextLinkResolvers
        {
            TechTag = ResolveTechTag,
            RecipeTag = ResolveRecipeTag,
            AchievementKey = ResolveAchievementKey,
        };

        /// <summary>当前正在流式输出的思考块（null = 没在思考）。</summary>
        private Block _thinking;
        private readonly StringBuilder _thinkingText = new StringBuilder();
        /// <summary>上一次真正写进 Text 的思考长度，配合逐帧渲染做节流。</summary>
        private int _thinkingRenderKey = -1;
        /// <summary>思考已经超出渲染上限，尾部会附一句省略提示。</summary>
        private bool _thinkingTruncated;

        /// <summary>
        /// 右下角「思考」下拉框（prefab 里挂在 thinking-group/thinking-level；
        /// 旧版直接挂在 tool-bar 下，查找路径两者都兼容）。
        /// </summary>
        private UIComboBox _thinkingCombo;
        private string _thinkingLevel = Protocol.ThinkingHigh;
        private bool _suppressThinkingLevelEvent;

        /// <summary>
        /// 输入栏右下角的「权限」下拉框（prefab 里挂在 permission-group/permission-level）。
        /// 只读 / 受限 / 接管：决定 AI 调写工具时是否需要玩家确认。
        /// </summary>
        private UIComboBox _permissionCombo;
        private string _permissionLevel = Protocol.DefaultPermission;
        private bool _suppressPermissionEvent;

        /// <summary>工具失败时的标题色。</summary>
        private static readonly Color WarnColor = new Color(1f, 0.47f, 0.32f, 1f);

        /// <summary>
        /// 单个思考块最多渲染的字符数。
        /// 游戏 Text 每个字符 4 个顶点，而 uGUI 一个网格的上限是 65535 顶点
        /// （约 1.6 万字符）；超过之后 Unity 会丢掉超出的三角形，后半段直接不显示。
        /// 12000 留出富文本标签、换行与勾选框的余量。
        /// </summary>
        private const int ThinkingRenderMaxChars = 12000;

        /// <summary>
        /// 流式思考时只渲染尾部这么多字符：每帧重排整段长文本会拖慢主线程，
        /// 而玩家在流式期间真正要看的就是最新几行。收尾时再由 EndThinking 渲染全文。
        /// </summary>
        private const int ThinkingStreamWindow = 2000;

        /// <summary>
        /// 流式思考块最多占输出区可视高度的比例。超过之后正文不再撑高整个对话，
        /// 旧内容从裁切框顶部被裁掉，只留最新几行（见 <see cref="ThinkingBodyClip"/>）。
        /// </summary>
        private const float ThinkingMaxOutputRatio = 2f / 3f;

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
            public Image Accent;
            public Text Chevron;
            public Text Title;
            public LayoutElement TitleLayout;
            public Text Summary;
            public Text Body;
            /// <summary>思考块正文的裁切框（prefab 里是 body-clip；老 prefab 为 null）。</summary>
            public ThinkingBodyClip BodyClip;
            /// <summary>思考块的标题行：算正文可用高度时要扣掉它和块内边距。</summary>
            public RectTransform Header;
            public bool Expanded;

            /// <summary>工具块用：调用参数摘要（展开时显示）。</summary>
            public string Args;
            public string ToolName;

            /// <summary>展开后显示的原始正文（未做富文本消毒，切换折叠时复用）。</summary>
            public string RawBody;

            /// <summary>工具块可选的截图节点（只有 game_capture_view 会填）。</summary>
            public RectTransform ImageRt;
            public ToolImageSprite ImageOwner;

            /// <summary>这个块属于哪条回复；思考/工具块都挂在回复模板里，旧 prefab 为 null。</summary>
            public ReplyView Reply;
        }

        /// <summary>
        /// prefab 里 <c>example-reply</c> 的绑定结果。
        ///
        /// 节点结构（静态布局全部在 ChatWindow.prefab 里）：
        ///   example-reply
        ///   ├── thinking-block     第一个折叠思考块（同时是后续思考块的克隆模板）
        ///   ├── tools              折叠块容器（tool-template 是克隆源；后续思考块也按顺序挂这里）
        ///   ├── body               助手正文
        ///   ├── blueprint-cards    固定卡片网格（blueprint-card-template 是克隆源）
        ///   └── footer             time + copy
        ///
        /// 运行期只改文字、显隐、图标和点击回调，不再补建/挪动静态节点。
        /// </summary>
        private sealed class ReplyView
        {
            public GameObject Go;
            public Text Body;
            /// <summary>正文里真表格/后续文本段动态创建的 Text 节点；重建正文时清掉。</summary>
            public readonly List<GameObject> BodyExtras = new List<GameObject>();
            public GameObject Footer;
            public Text Time;
            /// <summary>「已停止」标记：本轮被玩家中断时显示（A.15）。</summary>
            public Text Stopped;
            public Button Copy;
            public Text CopyLabel;
            public RectTransform ThinkingRoot;
            /// <summary>ThinkingRoot 的绑定结果（第一次思考时直接用它）。</summary>
            public Block Thinking;
            /// <summary>本回复已经用过的思考块（按出现顺序）。</summary>
            public readonly List<Block> ThinkingBlocks = new List<Block>();
            /// <summary>
            /// 本回复创建的全部折叠块（思考 + 工具 + 历史回放的步骤），按创建顺序。
            /// 正文开始时统一收起，因此不能只按“当前思考块/工具字典”去找。
            /// </summary>
            public readonly List<Block> Blocks = new List<Block>();
            /// <summary>
            /// prefab 里名为 loading 的运行中区块（新结构：header + content，
            /// content 里才是 thinking-block / tools）。老 prefab 没有这一层时为 null。
            /// </summary>
            public RectTransform Loading;
            /// <summary>loading 的标题行（点击展开/收起整个分组）。</summary>
            public GameObject LoadingHeader;
            /// <summary>loading 里承载思考/工具块的内容容器。</summary>
            public RectTransform LoadingContent;
            /// <summary>运行中的转圈图标（游戏自带的 loading 图）。</summary>
            public Image LoadingSpinner;
            /// <summary>运行期挂到转圈图标上的旋转组件。</summary>
            public LoadingSpinner LoadingSpin;
            /// <summary>完成后的展开/收起图标（▸ / ▾）。</summary>
            public Text LoadingChevron;
            public Text LoadingTitle;
            /// <summary>loading 分组当前是否展开（内容可见）。</summary>
            public bool LoadingExpanded = true;
            /// <summary>思考/工具这一轮是否已结束（显示“已完成”）。</summary>
            public bool LoadingDone;
            /// <summary>
            /// prefab 里名为 tools 的折叠块容器。工具块和“第二段及以后”的思考块都按创建顺序
            /// 追加到这里，视觉顺序才是 思考 → 工具 → 思考 → 工具 → 正文。
            /// </summary>
            public RectTransform Tools;
            public RectTransform ToolTemplate;
            public RectTransform BlueprintCards;
            public RectTransform CardTemplate;
            public RectTransform BuildingCardTemplate;
            public RectTransform StarCardTemplate;
            public readonly List<CardView> CardViews = new List<CardView>();
            public readonly List<CardView> LinkCards = new List<CardView>();
            public string CopyText = string.Empty;
            /// <summary>这段回复属于哪一轮（footer 的重新生成/撤回按钮用它找回回合）。</summary>
            public int TurnId;
            /// <summary>footer 的重新生成按钮（只有最后一条回答可见）。</summary>
            public Button Return;
            /// <summary>footer 的撤回按钮（只有最后一条回答可见，压缩记录上显示为撤销压缩）。</summary>
            public Button Withdraw;
        }

        /// <summary>一张固定尺寸蓝图卡片的绑定结果。</summary>
        private sealed class CardView
        {
            public GameObject Go;
            public BlueprintCardInfo Card;
            public GameObject ThumbIconRoot;
            public Image ThumbIcon;
            public GameObject ThumbFallback;
            public GameObject ThumbRecipeRoot;
            public Image ThumbRecipe;
            public Text ThumbLabel;
            public Text Name;
            public Text Description;
            public Text Meta;
            public Button CardButton;
            public Button ViewButton;
            public Button UseButton;
            public Text ViewLabel;
            public Text UseLabel;
            /// <summary>链接卡片对应的 tether 目标；蓝图卡片为 null。</summary>
            public GameTether.Target LinkTarget;
        }

        /// <summary>正在流式输出的那条消息（下标指向 _items）；-1 表示没有。</summary>
        private readonly StringBuilder _stream = new StringBuilder();
        private int _streamItem = -1;
        private int _itemSeq;
        private int _lastStreamRenderLength = -1;

        private static readonly RichTextOptions RichOptions14 =
            new RichTextOptions { BodyFontSize = 14 };
        private static readonly RichTextOptions RichOptions13 =
            new RichTextOptions { BodyFontSize = 13 };

        // prefab 里的 example-* 示例就是消息模板：样式只在 prefab 定义一次，运行期克隆。
        private RectTransform _userTemplate;
        private RectTransform _assistantTemplate;
        /// <summary>完整回复模板（思考块 + 工具容器 + 正文 + 蓝图卡片 + 时间/复制）。</summary>
        private RectTransform _replyTemplate;
        private bool _tetherHooked;

        // ---- chat-index：左侧会话分组索引（A.13） ----
        private RectTransform _chatIndex;
        private RectTransform _chatIndexContent;
        private RectTransform _conversationTemplate;
        private RectTransform _recordTemplate;
        private RectTransform _expandMoreTemplate;
        private ScrollRect _chatIndexScroll;
        private Text _chatIndexEmpty;
        private Button _newConversationButton;
        private int _titleIconCaret;
        private int _titleIconAnchor;

        private InputField _conversationTitleInput;
        private Text _conversationTitleOver;
        private ConversationTitleDisplay _conversationTitleDisplay;

        /// <summary>标题编辑结束的帧号：同一帧不要再把焦点抢回聊天输入框（见 Tick）。</summary>
        private int _titleEditEndFrame = -1;
        private readonly List<ConversationInfo> _conversations = new List<ConversationInfo>();
        private readonly List<ConversationView> _conversationViews = new List<ConversationView>();
        private readonly Dictionary<string, bool> _conversationExpanded =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _conversationVisible =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConversationStatus> _conversationStatus =
            new Dictionary<string, ConversationStatus>(StringComparer.Ordinal);
        private string _activeConversationId;
        private int _suppressHeaderClickFrame = -1;
        private string _pendingFocusConversationId;
        private string _pendingFocusText;
        private long _pendingFocusTimestamp;
        private string _pendingScrollBottomConversationId;
        private readonly List<TurnEntry> _turns = new List<TurnEntry>();
        private int _nextTurnId;
        private int _currentTurnId;
        private int _focusedTurnId;
        /// <summary>上一次扫描时的滚动位置与帧计数：滚动没变就不重复遍历消息。</summary>
        private float _indexTurnScanScroll = float.NaN;
        private int _indexTurnScanFrame;
        /// <summary>「时间标记都没文本」的排查日志只打一次，避免每次开窗都刷屏。</summary>
        private bool _timeMarkerDiagnosticLogged;

        /// <summary>
        /// 左栏过渡动画：由 Tick 驱动，不用协程（窗口不是 MonoBehaviour）。
        /// 同一个 key 的动画互相替换，中途切换会先落到终态，避免留下半开半合的列表。
        /// </summary>
        private sealed class IndexTween
        {
            public string Key;
            public float Duration;
            public float Elapsed;
            public Action<float> Step;
            public Action Done;
        }

        private readonly List<IndexTween> _indexTweens = new List<IndexTween>();
        private bool _awaitingResponse;
        /// <summary>AI 正在回答：为 true 时输入框为空就显示「中断」，有内容则显示「发送」（A.15）。</summary>
        private bool _busy;
        private Button _sendButton;
        private Button _breakButton;

        // ---- 输入区高度（内容驱动，上限后交给 prefab 里的滚动条） ----
        private const float InputAreaMinHeight = 100f;
        private const float InputAreaMaxHeight = 240f;
        /// <summary>输入区上下留白：文本高度之外的那一圈。</summary>
        private const float InputAreaPadding = 10f;
        private LayoutElement _inputAreaLayout;
        private ScrollRect _inputScroll;
        private RectTransform _inputScrollContent;
        private RectTransform _inputTextRect;
        private int _lastInputLength = -1;
        private float _lastInputWidth = -1f;
        /// <summary>文本刚变过：接下来几帧再量一次，等 InputField 把文本排版更新完。</summary>
        private int _inputAreaRefreshFrames;

        // ---- 工具列表（输入框上方的 popup；静态结构在 prefab，运行期只绑数据） ----
        private readonly List<ToolMenuItem> _toolCatalog = new List<ToolMenuItem>();
        private RectTransform _toolMenu;
        private ScrollRect _toolScroll;
        private GameObject _toolScrollbar;
        private RectTransform _toolContent;
        private RectTransform _toolRowTemplate;
        private Text _toolMenuEmpty;
        private Text _toolMenuFooter;
        private Text _toolMenuHint;
        private Button _toolButton;
        private readonly List<ToolRowView> _toolRows = new List<ToolRowView>();
        private readonly List<ToolMenuItem> _toolVisible = new List<ToolMenuItem>();
        private int _toolSelected;
        private string _toolFilter = string.Empty;
        private bool _toolMenuOpen;
        private bool _toolCatalogLoaded;
        /// <summary>程序化改输入框时屏蔽 onValueChanged，避免把菜单重新打开。</summary>
        private bool _suppressToolMenuEvents;

        /// <summary>prefab 里 tool-row-template 的绑定结果；行本身只在运行期克隆。</summary>
        private sealed class ToolRowView
        {
            public GameObject Go;
            public Button Button;
            public Image Bg;
            public Text Label;
            public Text Command;
            public Text Description;
        }

        /// <summary>正文字体：输入框与聊天正文都用它。</summary>
        private Font _bodyFont;

        private bool _dirty;
        private bool _missingReplyActionsLogged;
        private bool _suppressAutoScrollOnce;
        /// <summary>
        /// 内容变化后还需要继续贴底几帧。
        ///
        /// 一次性的内容变化（蓝图卡片、系统提示、非流式终稿）只在那一帧置一次
        /// <see cref="_dirty"/>。内容高度要等这一帧的 Canvas 布局重建后才更新，
        /// 当帧设 verticalNormalizedPosition 只能按旧高度算，滚动条会停在旧底部，
        /// 之后又没有 dirty 帧来纠正。所以卡片插入/开窗时先强制重建一次布局，
        /// 并在每次内容渲染后排两帧延迟贴底。
        /// </summary>
        private int _scrollBottomFrames;
        private int _fontSize;
        private int _maxLines;

        /// <summary>原生提示层低频自检的帧计数（见 Tick）。</summary>
        private int _tipLayerTick;

        /// <summary>打开窗口前 VFInput.inputing 的原值，关闭时还原。</summary>
        private bool _savedInputing;
        private bool _autoFocusInput;
        private bool _wantInputFocus;

        /// <summary>
        /// 输入框最后一次处于聚焦态时的选区。
        ///
        /// 点击工具条按钮会让 InputField 失焦，而游戏的 `DeactivateInputField()`
        /// 会把内部 caret 清成 0。若不记住点击前的选区，`InsertGeneralString()` /
        /// `InsertCheckBox()` / `OnColorChange()` 都会作用到文本开头。
        /// </summary>
        private int _lastSelectionAnchor;
        private int _lastSelectionFocus;
        private bool _hasSelectionSnapshot;

        /// <summary>多行输入框按回车后待提交（见 `OnInputNewLine`）。</summary>
        private bool _submitPending;
        /// <summary>A.14.7：发送/换行的当前绑定；设置页改键后由 ApplyKeyBindings 更新。</summary>
        private KeyChord _sendChord;
        private KeyChord _newlineChord;
        /// <summary>装 onValidateInput 之前游戏/输入框已有的校验器，必须先链它。</summary>
        private InputField.OnValidateInput _previousInputValidator;

        /// <summary>本次开窗是否已经补过一次材质重绑（见 RebindInputTextMaterial）。</summary>
        private bool _textMaterialRebound;

        /// <summary>上次看到的输入框内容（用于"第一段文字出现"时补一次材质重绑）。</summary>
        private string _lastInputText;

        /// <summary>窗口打开期间需要持续压制游戏输入（见 Plugin.LateUpdate）。</summary>
        public bool WantsInputSuppressed => IsOpen;

        /// <summary>聊天输入框是否获得焦点。</summary>
        public bool InputFocused => _input != null && _input.isFocused;

        /// <summary>右栏会话标题输入框是否正在编辑。</summary>
        /// <remarks>
        /// 除了 isFocused，还要看 EventSystem 的选中对象。
        /// 点下去到真正激活之间有一帧 m_AllowInput 仍是 false，只认 isFocused 会被 Tick 的焦点重申抢回去。
        /// </remarks>
        public bool TitleEditing
        {
            get
            {
                if (_conversationTitleInput == null) return false;
                if (_conversationTitleInput.isFocused) return true;
                var es = UnityEngine.EventSystems.EventSystem.current;
                return es != null && es.currentSelectedGameObject == _conversationTitleInput.gameObject;
            }
        }

        /// <summary>任一输入框（聊天框或标题框）正在编辑。</summary>
        /// <remarks>热键让位与 Esc 都看这个，否则在标题框里打字时按 R 会直接关窗。</remarks>
        public bool AnyInputFocused => InputFocused || TitleEditing;

        /// <summary>本帧标题编辑是否刚刚结束。</summary>
        /// <remarks>游戏 InputField 自己消费 Esc 时不会留下聚焦状态，Plugin 靠这个避免顺手把窗口关掉。</remarks>
        public bool TitleEditEndedThisFrame => Time.frameCount == _titleEditEndFrame;

        /// <summary>
        /// 聚焦输入框（按 Enter 或点击窗口时调用）。
        ///
        /// 记录"期望聚焦"而不是只调一次 API：游戏本身的 UI 逻辑可能会
        /// 清空 EventSystem 的选中对象，导致焦点被抢走。Tick 会据此重申。
        /// </summary>
        public void FocusInput()
        {
            if (_input == null) return;
            if (AskShowing) return;
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

        /// <summary>取消当前正在编辑的那个输入框（标题优先），热键与 Esc 让位时用。</summary>
        public void BlurActiveInput()
        {
            if (TitleEditing)
            {
                CancelTitleEditing();
                return;
            }
            BlurInput();
        }

        /// <summary>
        /// 退出标题编辑并还原成已保存的标题。
        /// 先还原再失焦：InputField 失焦会抛出 onEndEdit，文本与当前标题相同才不会当成改名提交。
        /// </summary>
        public void CancelTitleEditing()
        {
            if (_conversationTitleInput == null) return;
            _wantInputFocus = false;
            RefreshConversationTitle();
            _conversationTitleInput.DeactivateInputField();
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.currentSelectedGameObject == _conversationTitleInput.gameObject)
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
                   $"title={TitleEditing} " +
                   $"interactable={(_input != null && _input.interactable)} " +
                   $"eventSystem={(es != null ? es.name : "NULL")} selected={selected}";
        }

        /// <summary>玩家提交了一条消息。参数为去除首尾空白后的文本。</summary>
        public event Action<string> Submitted;

        /// <summary>玩家完成一次 ask（多题逐题作答后一次性回传）。</summary>
        public event Action<List<AskAnswer>> AskCompleted;

        /// <summary>ask 被取消（关窗/连接断开/主动取消）。</summary>
        public event Action AskCancelled;

        /// <summary>写操作确认结果：true=同意，false=拒绝/关窗。</summary>
        public event Action<bool> ConfirmationChosen;

        /// <summary>当前是否正在显示 ask 面板。</summary>
        public bool AskShowing => _askPanel != null && _askPanel.IsShowing;

        /// <summary>
        /// 玩家点了蓝图卡片上的动作。参数是卡片数据 + 动作类型；
        /// 真正的游戏 API 调用由 <see cref="IcarusII.Game.BlueprintActions"/> 完成。
        /// </summary>
        public event Action<BlueprintCardInfo, BlueprintCardAction> BlueprintCardActionRequested;

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
            if (_tetherHooked)
            {
                GameTether.Added -= OnTetherAdded;
                GameTether.Changed -= OnTetherChanged;
                _tetherHooked = false;
            }
            _root = null;
            _canvas = null;
            _hostCanvas = null;
            _canvasScale = 1f;
            _panel = null;
            _content = null;
            _statusText = null;
            _titleText = null;
            _titleFromPrefab = false;
            _transcript = null;
            _scroll = null;
            _input = null;
            _draftInput = null;
            _draftRestoreArmed = false;
            _lines.Clear();
            _items.Clear();
            _linkTargets.Clear();
            _linkHandlers.Clear();
            _toolBlocks.Clear();
            _thinking = null;
            _thinkingText.Length = 0;
            _thinkingCombo = null;
            _suppressThinkingLevelEvent = false;
            _permissionCombo = null;
            _suppressPermissionEvent = false;
            _stream.Length = 0;
            _streamItem = -1;
            _lastStreamRenderLength = -1;
            _scrollBottomFrames = 0;
            _userTemplate = null;
            _assistantTemplate = null;
            _replyTemplate = null;
            _chatIndex = null;
            _chatIndexContent = null;
            _conversationTemplate = null;
            _recordTemplate = null;
            _expandMoreTemplate = null;
            _chatIndexScroll = null;
            _chatIndexEmpty = null;
            _newConversationButton = null;
            _conversationTitleInput = null;
            _conversationTitleOver = null;
            _conversationTitleDisplay = null;
            _titleEditEndFrame = -1;
            _activeConversationId = null;
            _pendingFocusConversationId = null;
            _pendingFocusText = null;
            _pendingFocusTimestamp = 0;
            _pendingScrollBottomConversationId = null;
            _conversations.Clear();
            _conversationViews.Clear();
            _conversationExpanded.Clear();
            _conversationVisible.Clear();
            _turns.Clear();
            _nextTurnId = 0;
            _currentTurnId = 0;
            _focusedTurnId = 0;
            _bodyFont = null;
            _dirty = false;
            IsOpen = false;
            _wantInputFocus = false;
            _hasSelectionSnapshot = false;

            // 工具列表的静态节点随窗口一起销毁；目录数据保留，重建窗口后直接复用。
            _toolMenu = null;
            _toolScroll = null;
            _toolScrollbar = null;
            _toolContent = null;
            _toolRowTemplate = null;
            _toolMenuEmpty = null;
            _toolMenuFooter = null;
            _toolMenuHint = null;
            _toolButton = null;
            _toolRows.Clear();
            _toolVisible.Clear();
            _toolMenuOpen = false;
            _toolFilter = string.Empty;
            _toolSelected = 0;
            _suppressToolMenuEvents = false;
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
            _sendChord = config.SendChord;
            _newlineChord = config.NewlineChord;
            _thinkingLevel = PluginConfig.IsValidThinkingLevel(config.ThinkingLevel)
                ? config.ThinkingLevel.ToLowerInvariant()
                : Protocol.ThinkingHigh;
            _permissionLevel = PluginConfig.IsValidPermissionLevel(config.PermissionLevel)
                ? config.PermissionLevel.ToLowerInvariant()
                : Protocol.DefaultPermission;
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

            var prefabPanel = WindowPrefab.Instantiate(rootRt);
            if (prefabPanel == null || !TryWireFromPrefab(prefabPanel, config, font))
            {
                if (prefabPanel != null) UnityEngine.Object.Destroy(prefabPanel);
                Plugin.Log.LogError("[UI] 无法加载 ChatWindow prefab；请确认 ui/icarusii-ui 已随插件安装。");
                UnityEngine.Object.Destroy(rootRt.gameObject);
                _root = null;
                return;
            }
            Plugin.Log.LogInfo($"[UI] 窗口来自 prefab（{WindowPrefab.Status}）");

            ApplyHostScaleAndLayer();
            EnsureChatIndex();
            ApplySoftTextMaterials();

            // 窗口可能是在收到 save.bound/欢迎语之后才第一次打开的：
            // 那时消息只进了 _lines，还没有可用的 _content。这里统一补建可见项。
            RebuildTranscript();

            if (!_tetherHooked)
            {
                GameTether.Added += OnTetherAdded;
                GameTether.Changed += OnTetherChanged;
                _tetherHooked = true;
                }

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
                canvas.sortingOrder = CanvasSortingOrder;
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
                canvas.sortingOrder = CanvasSortingOrder;
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
            var status = FindText(panel, "Header/Status",
                "Header/title-status", "panel-bg/status-text", "status-text", "Status");

            // 正文区：优先新结构 output/scroll-view/{viewport/content}，
            // 再兼容旧结构（单块 Scroll/Viewport/Content 文本）。
            var scroll = FindComponent<ScrollRect>(panel,
                "content-column/output/scroll-view",
                "output/scroll-view", "Scroll", "scroll-view", "transcript");
            var content = FindNode(panel,
                "content-column/output/scroll-view/viewport/content",
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
                "content-column/input/editor/scroll-view/viewport/content/InputField/input",
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

            // 输入区的位置/大小完全由 prefab 决定；运行期只查 input 节点，不碰 scroll-view。

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

            _panel = panel;
            _statusText = status;
            _scroll = scroll;
            _content = content;
            _transcript = transcript;
            _input = input;
            // 聊天窗的 ESC 只是让位/关窗，不是"取消编辑"。
            // 游戏版 InputField 默认把 ESC 当取消：失焦时把文本回滚到聚焦前的内容，
            // 刚输入的一段会整段消失。关掉它，草稿才能留下来。
            if (_input != null) _input.escapeCancel = false;
            BindInputArea(input);

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
            _sendButton = FindComponent<Button>(panel,
                "input/editor/tool-bar/send-button", "tool-bar/send-button", "send-button", "SendButton");
            if (_sendButton != null)
            {
                _sendButton.onClick.AddListener(SubmitPendingInput);
            }
            else
            {
                Plugin.Log.LogInfo("prefab 里没有发送按钮，改用 Enter 发送（Ctrl+Enter 换行）。");
            }

            // 中断按钮（A.15）：和发送按钮同位，AI 回答中且输入框为空时替身显示。
            _breakButton = FindComponent<Button>(panel,
                "input/editor/tool-bar/break-button", "tool-bar/break-button", "break-button", "BreakButton");
            if (_breakButton != null)
            {
                _breakButton.onClick.AddListener(OnBreakClicked);
                _breakButton.gameObject.SetActive(false);
            }
            else
            {
                Plugin.Log.LogInfo("prefab 里没有中断按钮，AI 回答期间无法一键停止。");
            }
            if (input != null) input.onValueChanged.AddListener(_ => RefreshSendButtons());
            RefreshSendButtons();

            // 工具条：插入图标 / 复选框 / 文字颜色 —— 全部调**游戏自己的** API
            // （照原生 UISmartInputField 的做法），所以弹窗、图标表、调色板都和原生一致。
            WireInputToolbar(panel);

            // 输入框上方的工具列表：静态样式来自 prefab，运行期只绑定/筛选/克隆行。
            WireToolMenu(panel);
            WireBottomLayout(panel);
            WireAskPanel(panel);

            // 右下角「思考等级」下拉框：用游戏自己的 UIComboBox（prefab 里已放好节点）。
            WireThinkingLevelCombo(panel);
            // 「权限」下拉框：只读 / 受限 / 接管，决定写操作是否弹确认。
            WirePermissionLevelCombo(panel);

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
            //    注意：只在这里种一次是不够的：`InputField.OnDisable` 里有
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
            // 输入 "/" 或继续输入过滤词时，实时更新工具列表。
            _input.onValueChanged.AddListener(OnInputValueChanged);
            // 抄自信标窗待办框的输入框是**多行**的：回车不会走 onEndEdit，
            // 而是走游戏改写版 InputField 的 OnNewLine，所以这里单独接一发。
            _input.OnNewLine += OnInputNewLine;
            InstallInputKeyBindings();
            _panel.gameObject.AddComponent<ClickToFocus>().Target = this;

            // 文案走本地化。标题：prefab 里已经写了字的就**尊重它**（例如手工外框里写了
            // 「工业机甲终端」），只有空标题才填本地化的默认标题。
            var title = FindText(panel, "Header/Title", "Header/title-text", "panel-bg/title-text", "title-text");
            var authoredTitle = title != null && !string.IsNullOrEmpty(title.text);
            if (title != null && !authoredTitle) title.text = Strings.Get("title");
            _titleText = title;
            _titleFromPrefab = authoredTitle;
            // 字体：prefab 里填的是内置 Arial 占位（原生字体 SAIRAB/SAIRASB 不含中文），
            // 这里**无条件**换成游戏当前语言的字体——中文能不能显示全靠这一步。
            // 字号按 prefab 里烘焙的值整体缩放，尊重你在 Unity 里的排版。
            //
            // 例外：prefab 里已经写了字的标题。标题的字体、字号、颜色完全按 prefab 来
            // （用户就是照 prefab 的设计稿调的），不参与正文字体替换和 FontScale。
            var scale = Mathf.Max(0.5f, config.FontScale);
            _bodyFont = font;
            foreach (var t in panelGo.GetComponentsInChildren<Text>(true))
            {
                if (_titleFromPrefab && ReferenceEquals(t, _titleText)) continue;
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
                if (t == null && (path.StartsWith("input/", StringComparison.Ordinal) ||
                                  path.StartsWith("output/", StringComparison.Ordinal)))
                    t = root.Find("content-column/" + path);
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
                {
                    if (_titleFromPrefab && ReferenceEquals(t, _titleText)) continue;
                    ApplyBodyFont(t);
                }
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
            if (_chatIndexEmpty != null) _chatIndexEmpty.color = _textDim;
            RefreshIndexSelection();

        }

        // ---------------------------------------------------------------- 开关

        public void SetOpen(bool open)
        {
            if (_root == null) return;
            if (!open) FinishIndexTweens();
            if (open && !GameRefs.IsInGame)
            {
                Plugin.Log.LogInfo("[UI] 不在游戏内，拒绝打开聊天窗");
                return;
            }

            if (!open && _askPanel != null && _askPanel.IsShowing) _askPanel.Cancel();

            // 关窗会 SetActive(false)，输入框的 OnDisable 会清掉文本；
            // 必须在禁用之前把草稿抓到字段里，开窗时再塞回去。
            if (!open && _input != null)
            {
                _draftInput = _input.text;
                _draftRestoreArmed = false;
            }

            IsOpen = open;
            _root.SetActive(open);
            if (!open) { _wantInputFocus = false; _textMaterialRebound = false; HideToolMenu(); }

            ApplyOpenState(open);
            if (open)
            {
                EnsureIconMappings();
                RefreshLinkCardInfos();
                BindTitleIconButton();
                // 打开窗口就刷一次所有会话的时间标记：“刚刚 / x分钟”只在打开那一刻是对的。
                RefreshConversationTimes();
            }
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
        internal static void SeedInputCharColors(InputField f, string why, int minChars = 0)
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

                int textChars = f.text != null ? f.text.Length : 0;
                int need = Mathf.Max(256, Mathf.Max(textChars, minChars) + 128);
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
        internal static void RebindInputTextMaterial(Text t, string why)
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
                // 先把颜色表按草稿长度种回"跟随默认色"，再写入文本；顺序反了的话，
                // 写入瞬间就会按 0 号色生成标签，之后改表也不会重算。
                SeedInputCharColors(_input, "开窗", _draftInput != null ? _draftInput.Length : 0);
                // 把关窗时暂存的草稿放回去，并强制刷新一次标签。
                if (_input != null && _draftInput != null)
                {
                    _input.text = _draftInput;
                    _input.caretPosition = _input.text.Length;
                    _input.ForceLabelUpdate();
                    // 聚焦路径可能再次清掉文本；在玩家真正开始编辑之前允许补一次。
                    _draftRestoreArmed = !string.IsNullOrEmpty(_draftInput);
                }
                // CanvasRenderer 的材质绑定会在关窗/重建后丢失，开窗时重申一次。
                if (_input != null) RebindInputTextMaterial(_input.textComponent, "开窗");
                // 默认不自动聚焦：这样打开后按键仍可立即关窗。
                // 需要"开窗即打字"的玩家可在配置里打开 AutoFocusInput。
                if (_autoFocusInput && !AskShowing) FocusInput();
                EnsureThinkingComboText();
                EnsurePermissionComboText();
                RebuildBottomLayout();
                // 刚 SetActive(true)，布局还没重建：直接贴底只能按旧高度算，
                // 打开窗口时可能停在历史中间而不是最新一条（蓝图卡片尤其明显）。
                ScrollToBottomAfterContentChange();
            }
            else
            {
                // 选择器是游戏的对象，不会随聊天窗隐藏；而且我们把它抬到了
                // 聊天窗之上，不主动关会留在屏幕上。
                ClosePickers();

                if (_input != null)
                {
                    _input.DeactivateInputField();
                    // 未发送的草稿保留在输入框里：关窗只是 SetActive(false)，
                    // 下次开窗 SeedInputCharColors 会重新种颜色表，文字照常可见。
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
        /// <param name="gameTime">历史回放时的原始游戏时间；当场回答传 null。</param>
        public void AppendLine(string speaker, string text, string gameTime = null)
        {
            if (text == null) return;

            var prefix = string.IsNullOrEmpty(speaker)
                ? string.Empty
                : $"<color=#{ColorUtility.ToHtmlStringRGB(_accent)}>{Escape(speaker)}</color>  ";

            AddMessage(LineKind.Assistant, prefix + text, gameTime);
            _awaitingResponse = false;
        }

        /// <summary>
        /// 玩家撤回了最后一轮问答。参数是用户消息原文与历史时间戳（0 = 本回合新提问）。
        /// 窗口只负责 UI；真正删除会话条目由 Plugin 转给 sidecar（协议 A.7）。
        /// </summary>
        public event Action<string, long> WithdrawRequested;

        /// <summary>玩家点了左侧“+ 新会话”。</summary>
        public event Action ConversationCreateRequested;

        /// <summary>玩家点了「中断」：请求 sidecar 停下当前回合（A.15）。</summary>
        public event Action BreakRequested;

        /// <summary>玩家点了另一个会话里的记录：请求切换会话（A.13）。</summary>
        public event Action<string> ConversationSwitchRequested;

        /// <summary>玩家点了会话标题右侧的删除按钮（A.13）。</summary>
        public event Action<string> ConversationDeleteRequested;
        public event Action<string, string> ConversationRenameRequested;
        /// <summary>玩家点了会话标题右侧的置顶按钮；参数是点完之后的目标状态。</summary>
        public event Action<string, bool> ConversationPinRequested;

        /// <summary>
        /// 玩家撤回了最后一条压缩记录：请求 sidecar 撤销最近一次压缩（协议 A.11）。
        /// 参数是 compaction entry id；空字符串表示撤销最近一次。
        /// UI 不先删记录，等 sidecar 成功后再移除（失败要保留记录）。
        /// </summary>
        public event Action<string> CompactionUndoRequested;

        /// <summary>
        /// 玩家点了回答 footer 的重新生成：窗口已经把这一轮的助手侧清空，
        /// Plugin 先把旧回合从会话里删掉（A.7），再用同一句提问重新发 prompt。
        /// </summary>
        public event Action<string, long> RegenerateRequested;

        /// <summary>追加一条用户消息（渲染成气泡，样式来自 prefab 的 example-user-*）。</summary>
        /// <param name="timestampMs">历史回放时带上的时间戳；当场提问传 0。</param>
        /// <param name="gameTime">历史回放的游戏时间（HH:mm:ss）；空则用当前游戏时间。</param>
        public void AppendUser(string text, long timestampMs = 0, string gameTime = null)
        {
            AppendUserInternal(text, timestampMs, gameTime, addIndexRecord: true);
        }

        /// <summary>历史回放：记录行已经由 sidecar 的 conversations 列表提供，不再重复追加。</summary>
        public void AppendHistoryUser(string text, long timestampMs = 0, string gameTime = null)
        {
            AppendUserInternal(text, timestampMs, gameTime, addIndexRecord: false);
        }

        private void AppendUserInternal(string text, long timestampMs, string gameTime, bool addIndexRecord)
        {
            if (text == null) return;

            // 新提问属于新回合：上一轮若还在流式思考，先把它收尾，
            // 否则新回合的推理会续写进上一个思考块。
            if (_thinking != null) EndThinking();

            _currentTurnId = ++_nextTurnId;
            _awaitingResponse = true;
            AddMessage(LineKind.User, text, gameTime);
            AddTurn(_currentTurnId, text, timestampMs, addIndexRecord: addIndexRecord);
        }

        /// <summary>追加一行系统提示（灰色）。</summary>
        public void AppendSystem(string text)
        {
            if (text == null) return;
            AddMessage(LineKind.System, text);
            // 错误/提示类系统消息意味着本轮已经收尾。
            _awaitingResponse = false;
        }

        /// <summary>
        /// 追加一条上下文压缩记录（协议 A.11）：一条“自动压缩上下文”用户消息 +
        /// 一条摘要回答。撤回这条记录 = 撤销压缩。
        ///
        /// 它不能改变 `_currentTurnId` 的归属：压缩可能发生在一轮回答开始前或中途，
        /// 之后模型的正文仍应落回**玩家提问的那一轮**，而不是压缩记录。
        /// </summary>
        public void AppendCompaction(HistoryMessageInfo message)
        {
            if (message == null) return;
            AppendCompaction(
                message.Text,
                message.GameTime,
                message.Timestamp,
                message.CompactionId,
                message.TokensBefore,
                message.EstimatedTokensAfter);
        }

        public void AppendCompaction(
            string summary,
            string gameTime,
            long timestampMs,
            string compactionId,
            int tokensBefore = 0,
            int estimatedTokensAfter = 0)
        {
            if (string.IsNullOrEmpty(summary)) summary = Strings.Get("chat.compaction.empty");

            // 压缩发生在流式回答中途时，先把已收到的半截正文收尾；重试会产生新的回答。
            if (_streamItem >= 0) EndStream();

            var answerTurnId = _currentTurnId;
            var wasAwaiting = _awaitingResponse;
            var insertBefore = wasAwaiting && answerTurnId != 0 && !HasAssistantContent(answerTurnId);
            // 时间用游戏时钟：实时压缩由 Plugin 传当前时钟并回存给回放；
            // 老记录没有回存过时间，就不写时间（拿“现在”的时钟冒充是错的）。
            var name = Strings.Get("chat.compaction.label");
            var label = string.IsNullOrEmpty(gameTime) ? name : $"[{gameTime}] {name}";

            _currentTurnId = ++_nextTurnId;
            _awaitingResponse = false;
            AddMessage(LineKind.User, label, null);
            AddTurn(
                _currentTurnId,
                label,
                timestampMs,
                isCompaction: true,
                compactionId: compactionId,
                tokensBefore: tokensBefore,
                estimatedTokensAfter: estimatedTokensAfter,
                addIndexRecord: false);
            AddMessage(LineKind.Assistant, summary, null);

            // 压缩发生在“新问题刚发出、回答还没开始”时：记录应排在问题之前，
            // 这样输出区顺序才和会话文件里“压缩 → 提问 → 回答”一致。
            if (insertBefore)
            {
                MoveCompactionBeforeTurn(_currentTurnId, answerTurnId);
            }

            // 后续正文回到玩家提问的那一轮；没有提问时保持压缩记录为当前轮。
            _currentTurnId = answerTurnId != 0 ? answerTurnId : _currentTurnId;
            _awaitingResponse = wasAwaiting;
            UpdateReplyActionAvailability();
            _dirty = true;
            ScrollToBottom();
        }

        private bool HasAssistantContent(int turnId)
        {
            foreach (var line in _lines)
            {
                if (line.TurnId == turnId && line.Kind == LineKind.Assistant &&
                    !string.IsNullOrEmpty(line.Text))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 把刚追加在末尾的压缩记录整块移到 `targetTurnId` 之前（索引、输出区、_turns 同步）。
        /// 只在“问题已发出但还没有任何回答”时调用，因此目标轮还没有 assistant 项。
        /// </summary>
        private void MoveCompactionBeforeTurn(int compactionTurnId, int targetTurnId)
        {
            int cardTurn = _turns.FindIndex(t => t.Id == compactionTurnId);
            int targetTurn = _turns.FindIndex(t => t.Id == targetTurnId);
            if (cardTurn >= 0 && targetTurn >= 0 && cardTurn > targetTurn)
            {
                var entry = _turns[cardTurn];
                _turns.RemoveAt(cardTurn);
                targetTurn = _turns.FindIndex(t => t.Id == targetTurnId);
                _turns.Insert(targetTurn, entry);
            }

            int cardLine = _lines.FindIndex(l => l.TurnId == compactionTurnId);
            int targetLine = _lines.FindIndex(l => l.TurnId == targetTurnId);
            if (cardLine >= 0 && targetLine >= 0 && cardLine > targetLine)
            {
                var block = _lines.GetRange(cardLine, _lines.Count - cardLine);
                _lines.RemoveRange(cardLine, block.Count);
                targetLine = _lines.FindIndex(l => l.TurnId == targetTurnId);
                _lines.InsertRange(targetLine, block);
            }

            if (_content == null) return;
            int cardItem = _items.FindIndex(i => i.TurnId == compactionTurnId);
            int targetItem = _items.FindIndex(i => i.TurnId == targetTurnId);
            if (cardItem >= 0 && targetItem >= 0 && cardItem > targetItem)
            {
                var block = _items.GetRange(cardItem, _items.Count - cardItem);
                _items.RemoveRange(cardItem, block.Count);
                targetItem = _items.FindIndex(i => i.TurnId == targetTurnId);
                _items.InsertRange(targetItem, block);
                for (int i = 0; i < block.Count; i++)
                {
                    var go = block[i].Go;
                    if (go != null) go.transform.SetSiblingIndex(targetItem + i);
                }
            }
        }

        /// <summary>
        /// 追加调试直调结果（`!tool`）。
        /// 走普通助手正文样式（和正常回答一致），而不是灰色系统行；同时对内容做
        /// 尖括号转义，避免工具返回值里的 `<...>` 被 UGUI 当成富文本标签。
        /// </summary>
        public void AppendDebugResult(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            AddMessage(LineKind.Assistant, Escape(text));
            _awaitingResponse = false;
        }

        /// <summary>
        /// 在对话末尾追加一张蓝图卡片（协议附录 A.9）。
        ///
        /// 新 prefab 里卡片被克隆到回复的 fixed-size 网格中，一行最多 3 张；
        /// 旧 prefab 没有回复模板时仍走代码构建的兜底路径。
        /// </summary>
        public void AppendBlueprintCard(BlueprintCardInfo card)
        {
            if (card == null) return;
            if (string.IsNullOrEmpty(card.CreatedAt))
                card.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd");

            // 窗口还没 Create 时只记逻辑行；Create 末尾由 RebuildTranscript 补建。
            if (_content == null)
            {
                _lines.Add(new Line
                {
                    Kind = LineKind.Blueprint,
                    Text = card.DisplayTitle,
                    TurnId = _currentTurnId,
                    Cards = new List<BlueprintCardInfo> { card },
                });
                TrimHistory();
                _dirty = true;
                return;
            }

            if (_replyTemplate != null)
            {
                var index = FindReplyItemForCard();
                if (index >= 0 && index < _items.Count && _items[index].Reply != null)
                {
                    var item = _items[index];
                    var cardView = AddCard(item.Reply, card);
                    if (cardView != null)
                    {
                        if (_streamItem == index && _stream.Length == 0 && !_awaitingResponse && _thinking == null) _streamItem = -1;
                        var lineIndex = FindLineIndex(item.Go);
                        if (lineIndex >= 0)
                        {
                            var line = _lines[lineIndex];
                            if (line.Cards == null) line.Cards = new List<BlueprintCardInfo>();
                            line.Cards.Add(card);
                            _lines[lineIndex] = line;
                        }
                        else
                        {
                            _lines.Add(new Line
                            {
                                Kind = LineKind.Assistant,
                                Text = string.Empty,
                                TurnId = _currentTurnId,
                                Go = item.Go,
                                Cards = new List<BlueprintCardInfo> { card },
                                Time = GameClock(),
                            });
                        }
                        TrimHistory();
                        _dirty = true;
                        ScrollToBottomAfterContentChange();
                        return;
                    }
                }
            }

            var go = CreateBlueprintCard(card);
            if (go == null) return;
            _lines.Add(new Line
            {
                Kind = LineKind.Blueprint,
                Text = card.DisplayTitle,
                TurnId = _currentTurnId,
                Go = go,
                Cards = new List<BlueprintCardInfo> { card },
            });
            TrimHistory();
            _dirty = true;
            ScrollToBottomAfterContentChange();
        }

        /// <summary>克隆 prefab 的固定卡片模板并绑定一张卡片。</summary>
        private CardView AddCard(ReplyView reply, BlueprintCardInfo card)
        {
            if (reply == null || reply.CardTemplate == null || reply.BlueprintCards == null || card == null)
                return null;

            var go = UnityEngine.Object.Instantiate(reply.CardTemplate.gameObject, reply.BlueprintCards, false);
            go.name = "blueprint-card-" + (++_itemSeq);
            go.SetActive(true);
            var view = BindCardView(go);
            if (view == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            reply.BlueprintCards.gameObject.SetActive(true);
            view.Card = card;
            ApplyCard(view, card);
            reply.CardViews.Add(view);
            return view;
        }

        private void AddCards(ReplyView reply, List<BlueprintCardInfo> cards)
        {
            if (reply == null || cards == null) return;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] != null) AddCard(reply, cards[i]);
            }
        }

        private CardView AddLinkCard(ReplyView reply, GameTether.Target target)
        {
            if (reply == null || reply.BlueprintCards == null || target == null) return null;
            var template = target.Kind == "planet" ? reply.StarCardTemplate : reply.BuildingCardTemplate;
            if (template == null)
            {
                // prefab 被整窗重建时，link 模板可能缺失；退回蓝图卡模板，
                // 至少保留名称/描述/聚焦，不要静默丢功能。
                template = reply.CardTemplate;
                Plugin.Log.LogWarning("[link] 缺少建筑/星球卡模板，回退蓝图卡模板");
                if (template == null) return null;
            }
            if (HasLinkCard(reply, target)) return null;

            var go = UnityEngine.Object.Instantiate(template.gameObject, reply.BlueprintCards, false);
            go.name = "link-card-" + (++_itemSeq);
            go.SetActive(true);
            var view = BindCardView(go);
            if (view == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            view.LinkTarget = target;
            reply.BlueprintCards.gameObject.SetActive(true);
            ApplyLinkCard(view);
            reply.CardViews.Add(view);
            reply.LinkCards.Add(view);
            return view;
        }

        private void AddLinkCards(ReplyView reply, List<GameTether.Target> targets)
        {
            if (reply == null || targets == null) return;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i] != null) AddLinkCard(reply, targets[i]);
        }

        /// <summary>只在本条回答内去重：同一实体在不同回合被再次引用时，应各有自己的卡片。</summary>
        private static bool HasLinkCard(ReplyView reply, GameTether.Target target)
        {
            if (reply == null || target == null) return false;
            for (int c = 0; c < reply.LinkCards.Count; c++)
            {
                var link = reply.LinkCards[c];
                if (link != null && link.LinkTarget != null && link.LinkTarget.Key == target.Key) return true;
            }
            return false;
        }

        private static bool LineHasLinkTarget(Line line, GameTether.Target target)
        {
            if (line.LinkTargets == null || target == null) return false;
            for (int i = 0; i < line.LinkTargets.Count; i++)
            {
                var existing = line.LinkTargets[i];
                if (existing != null && existing.Key == target.Key) return true;
            }
            return false;
        }

        private void ApplyLinkCard(CardView view)
        {
            if (view == null || view.LinkTarget == null) return;
            var target = view.LinkTarget;
            if (view.Name != null) view.Name.text = target.Name;
            if (view.Description != null) view.Description.text = target.Description;
            if (view.Meta != null) view.Meta.text = target.Subtitle;
            if (view.ThumbLabel != null) view.ThumbLabel.text = target.IconLabel;
            if (view.ThumbIconRoot != null)
            {
                view.ThumbIconRoot.SetActive(target.MainIcon != null);
                if (view.ThumbIcon != null) view.ThumbIcon.sprite = target.MainIcon;
            }
            if (view.ThumbFallback != null) view.ThumbFallback.SetActive(target.MainIcon == null);
            if (view.ThumbRecipeRoot != null)
            {
                view.ThumbRecipeRoot.SetActive(target.RecipeIcon != null);
                if (view.ThumbRecipe != null) view.ThumbRecipe.sprite = target.RecipeIcon;
            }
            if (view.UseLabel != null)
                view.UseLabel.text = GameTether.Contains(target)
                    ? Strings.Get("link.card.located")
                    : Strings.Get("link.card.locate");

            if (view.CardButton != null)
            {
                view.CardButton.onClick.RemoveAllListeners();
                view.CardButton.onClick.AddListener(() => FocusLinkCard(view));
            }
            if (view.UseButton != null)
            {
                view.UseButton.onClick.RemoveAllListeners();
                view.UseButton.onClick.AddListener(() => ToggleLinkCard(view));
            }
        }

        private void RefreshLinkCards()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var reply = _items[i].Reply;
                if (reply == null) continue;
                for (int c = 0; c < reply.LinkCards.Count; c++)
                {
                    var view = reply.LinkCards[c];
                    if (view == null || view.LinkTarget == null || view.UseLabel == null) continue;
                    view.UseLabel.text = GameTether.Contains(view.LinkTarget)
                        ? Strings.Get("link.card.located")
                        : Strings.Get("link.card.locate");
                }
            }
        }

        /// <summary>sidecar 回放/重放 link 卡：只带实体 ID 和文案，图标按当前存档资源补。</summary>
        public void AppendLinkCard(LinkCardInfo info)
        {
            if (info == null) return;
            var target = new GameTether.Target
            {
                Kind = info.Kind,
                TargetId = info.Kind == "building" ? info.BuildingId : info.Kind == "vein" ? info.VeinId : info.PlanetId,
                PlanetId = info.PlanetId,
                Name = info.Name,
                Subtitle = info.Subtitle,
                Description = info.Description,
                IconLabel = info.IconLabel,
            };
            GameTether.ResolveIcons(target);
            AppendLinkTarget(target);
        }

        private void OnTetherAdded(GameTether.Target target)
        {
            if (target == null) return;
            AppendLinkTarget(target);
        }

        private void AppendLinkTarget(GameTether.Target target)
        {
            if (target == null) return;
            if (_content == null || _replyTemplate == null)
            {
                // 窗口还没创建（历史回放）：先记在最后一条助手行上，Create 后补建。
                for (int i = _lines.Count - 1; i >= 0; i--)
                {
                    var line = _lines[i];
                    if (line.Kind != LineKind.Assistant || line.TurnId != _currentTurnId) continue;
                    if (LineHasLinkTarget(line, target)) return;
                    if (line.LinkTargets == null) line.LinkTargets = new List<GameTether.Target>();
                    line.LinkTargets.Add(target);
                    _lines[i] = line;
                    _dirty = true;
                    return;
                }
                _lines.Add(new Line
                {
                    Kind = LineKind.Assistant,
                    Text = string.Empty,
                    TurnId = _currentTurnId,
                    LinkTargets = new List<GameTether.Target> { target },
                    Time = GameClock(),
                });
                TrimHistory();
                _dirty = true;
                return;
            }
            int index = FindReplyItemForCard();
            if (index < 0 || index >= _items.Count || _items[index].Reply == null) return;
            var item = _items[index];
            if (HasLinkCard(item.Reply, target)) { RefreshLinkCards(); return; }
            var view = AddLinkCard(item.Reply, target);
            if (view == null) return;
            var lineIndex = FindLineIndex(item.Go);
            if (lineIndex >= 0)
            {
                var line = _lines[lineIndex];
                if (LineHasLinkTarget(line, target)) return;
                if (line.LinkTargets == null) line.LinkTargets = new List<GameTether.Target>();
                line.LinkTargets.Add(target);
                _lines[lineIndex] = line;
            }
            else
            {
                _lines.Add(new Line
                {
                    Kind = LineKind.Assistant,
                    Text = string.Empty,
                    TurnId = _currentTurnId,
                    Go = item.Go,
                    LinkTargets = new List<GameTether.Target> { target },
                    Time = GameClock(),
                });
            }
            TrimHistory();
            _dirty = true;
            ScrollToBottomAfterContentChange();
        }

        /// <summary>窗口每次打开时刷新一次建筑/星球卡片快照；运行中不逐帧采样。</summary>
        private void RefreshLinkCardInfos()
        {
            bool changed = false;
            for (int i = 0; i < _items.Count; i++)
            {
                var reply = _items[i].Reply;
                if (reply == null || reply.LinkCards == null) continue;
                for (int j = 0; j < reply.LinkCards.Count; j++)
                {
                    var view = reply.LinkCards[j];
                    if (view == null || view.LinkTarget == null) continue;
                    GameTether.RefreshInfo(view.LinkTarget);
                    ApplyLinkCard(view);
                    changed = true;
                }
            }
            if (changed) _dirty = true;
        }

        private void OnTetherChanged()
        {
            RefreshLinkCards();
        }

        private void FocusLinkCard(CardView view)
        {
            if (view == null || view.LinkTarget == null) return;
            if (!GameLinkFocus.Focus(view.LinkTarget))
                SetStatus(Strings.Get("link.focus.failed"), WarnColor);
        }

        private void ToggleLinkCard(CardView view)
        {
            if (view == null || view.LinkTarget == null) return;
            if (GameTether.Contains(view.LinkTarget))
            {
                GameTether.Remove(view.LinkTarget);
            }
            else
            {
                GameTether.ReAdd(view.LinkTarget);
            }
        }

        /// <summary>绑定一张固定卡片：只查节点、接回调，不改固定布局。</summary>
        private CardView BindCardView(GameObject go)
        {
            if (go == null) return null;
            var t = go.transform;
            var view = new CardView { Go = go };

            var thumb = t.Find("thumb");
            if (thumb != null)
            {
                var icon = thumb.Find("icon");
                view.ThumbIconRoot = icon != null ? icon.gameObject : null;
                view.ThumbIcon = icon != null ? icon.GetComponent<Image>() : null;
                var fallback = thumb.Find("fallback");
                view.ThumbFallback = fallback != null ? fallback.gameObject : null;
                var recipe = thumb.Find("recipe");
                view.ThumbRecipeRoot = recipe != null ? recipe.gameObject : null;
                view.ThumbRecipe = recipe != null ? recipe.GetComponent<Image>() : null;
                view.ThumbLabel = FindChildText(thumb, "label");
            }

            view.Name = FindChildText(t, "content/name");
            view.Description = FindChildText(t, "content/desc-mask/description");
            view.Meta = FindChildText(t, "content/meta");

            // 新 prefab 取消了“查看”按钮，整张卡片就是查看入口。
            view.CardButton = go.GetComponent<Button>();
            if (view.CardButton != null)
            {
                view.CardButton.onClick.RemoveAllListeners();
                view.CardButton.onClick.AddListener(() => RequestCardAction(view.Card, BlueprintCardAction.Inspect));
            }

            // 最新 prefab 把“使用”直接挂在 content 右下角；旧 prefab 在 actions 容器里。
            var use = t.Find("content/use");
            if (use == null) use = t.Find("content/actions/use");
            if (use != null)
            {
                view.UseButton = use.GetComponent<Button>();
                view.UseLabel = FindChildText(use, "label");
            }

            // 最新 prefab 已取消独立“查看”按钮，这里仅为旧 prefab 保留兼容。
            var viewButton = t.Find("content/view");
            if (viewButton == null) viewButton = t.Find("content/actions/view");
            if (viewButton != null)
            {
                view.ViewButton = viewButton.GetComponent<Button>();
                view.ViewLabel = FindChildText(viewButton, "label");
            }
            if (view.ViewLabel != null) view.ViewLabel.text = Strings.Get("blueprint.card.view");

            // 旧 prefab 仍然有独立“查看”按钮时保持兼容。
            if (view.ViewButton != null)
            {
                view.ViewButton.onClick.RemoveAllListeners();
                view.ViewButton.onClick.AddListener(() => RequestCardAction(view.Card, BlueprintCardAction.Inspect));
            }
            if (view.UseButton != null)
            {
                view.UseButton.onClick.RemoveAllListeners();
                view.UseButton.onClick.AddListener(() => RequestCardAction(view.Card, BlueprintCardAction.Paste));
            }
            return view;
        }

        /// <summary>建一张蓝图卡片：只克隆 prefab 里的 blueprint-card 模板。</summary>
        private GameObject CreateBlueprintCard(BlueprintCardInfo card)
        {
            if (_content == null || card == null) return null;
            var template = _replyTemplate != null
                ? _replyTemplate.Find("blueprint-cards/blueprint-card") as RectTransform
                : null;
            if (template == null && _replyTemplate != null)
                template = _replyTemplate.Find("blueprint-card") as RectTransform;
            if (template == null)
            {
                Plugin.Log.LogError("[UI] prefab 缺少 blueprint-card 模板，无法创建蓝图卡片");
                return null;
            }
            var go = UnityEngine.Object.Instantiate(template.gameObject, _content, false);
            go.name = "blueprint-card-" + (++_itemSeq);
            go.SetActive(true);
            var view = BindCardView(go);
            if (view == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            view.Card = card;
            ApplyCard(view, card);
            return go;
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
            if (source == "written") return Strings.Get("blueprint.card.source.written");
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
            // 工具调用之前的正文是过程正文：按时刻落到块序列里，别并进最终正文。
            FreezeStreamSegment(CurrentStreamReply());

            Block existing;
            if (_toolBlocks.TryGetValue(callId, out existing) && existing.Go != null)
            {
                // 重复的 tool_call（例如上游重试）：更新参数即可，别叠一块。
                existing.Args = argsText;
                SetBlock(existing, existing.Title.text, Strings.Get("tool.running"),
                    BuildToolBody(existing.Args, null), expanded: false, titleColor: _textDim);
                return;
            }

            ReplyView reply = null;
            RectTransform toolsParent = null;
            GameObject toolTemplate = null;
            if (_replyTemplate != null)
            {
                reply = EnsureReplyForTurn();
                if (reply != null && reply.Tools != null)
                {
                    toolsParent = reply.Tools;
                    toolsParent.gameObject.SetActive(true);
                    toolTemplate = reply.ToolTemplate != null ? reply.ToolTemplate.gameObject : null;
                }
                else
                {
                    reply = null;
                }
            }

            var block = CreateBlock("tool-block",
                title: Strings.Get("tool.title", name ?? Strings.Get("tool.name")),
                summary: Strings.Get("tool.running"),
                body: BuildToolBody(argsText, null),
                expanded: false,
                // 与「思考」块同色：工具调用不该比思考更亮（5x 文字材质下白色会像发光）。
                titleColor: _textDim,
                parent: toolsParent,
                template: toolTemplate,
                reply: reply);
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

        // ---------------------------------------------------------------- 权限

        /// <summary>当前权限档位（readonly / limited / takeover）。</summary>
        public string PermissionLevel => _permissionLevel;

        /// <summary>玩家在聊天窗下拉框里切换了权限档位。参数是协议里的档位名。</summary>
        public event Action<string> PermissionLevelChanged;

        /// <summary>用 sidecar 的 config.state 同步下拉框（不触发事件，避免回声）。</summary>
        public void ApplyPermissionLevel(string level)
        {
            if (!PluginConfig.IsValidPermissionLevel(level)) return;
            SetPermissionLevel(level, notify: false);
        }

        // ---------------------------------------------------------------- 思考块

        /// <summary>当前思考档位（off / low / high / max）。</summary>
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

        /// <summary>
        /// 开始一段思考块（每条 reasoning 流的第一条 delta 到达时调用）。
        ///
        /// 一次提问里模型可能「思考 → 工具 → 再思考 → …」。每段思考必须是**独立**的折叠块，
        /// 并且按出现顺序排在工具块之间；否则所有推理都会挤进第一个块里
        /// （实测：第二段推理会把第一段覆盖掉，而且位置永远停在整个回复的最上方）。
        /// </summary>
        public void BeginThinking()
        {
            if (_content == null) return;
            if (_thinking != null && _thinking.Go != null) return;

            // 模型在一段正文之后又发起思考：先把那段正文按时刻收进块序列，
            // 否则它会被并进最终正文，位置跑到所有工具块之后。
            FreezeStreamSegment(CurrentStreamReply());

            Block block = null;
            if (_replyTemplate != null)
            {
                var reply = EnsureReplyForTurn();
                block = EnsureThinkingBlock(reply);
            }

            if (block == null)
            {
                block = CreateBlock("thinking-block",
                    title: Strings.Get("thinking.title.streaming"),
                    summary: string.Empty,
                    body: string.Empty,
                    expanded: true,
                    titleColor: _accent);
            }
            if (block == null) return;

            SetBlock(block, Strings.Get("thinking.title.streaming"), string.Empty, string.Empty,
                expanded: true, titleColor: _accent, rebuild: false);
            _thinking = block;
            _thinkingText.Length = 0;
            _thinkingRenderKey = -1;
            _thinkingTruncated = false;
            RestartThinkingBodyClip(block);
            _dirty = true;
        }

        /// <summary>
        /// 取一个“当前段”的思考块：
        ///   1. 本回复还没用过思考块 → 直接用 prefab 里现成的 thinking-block；
        ///   2. 已经有过思考块 → 克隆 prefab 的 thinking-block，追加到按顺序排列的折叠块容器，
        ///      这样它落在上一段工具块之后，顺序是 思考 → 工具 → 思考 → …
        ///
        /// 只克隆 prefab 节点、只追加内容，不修改任何静态布局。
        /// </summary>
        private Block EnsureThinkingBlock(ReplyView reply)
        {
            if (reply == null || reply.ThinkingRoot == null) return null;

            Block block;
            if (reply.ThinkingBlocks.Count == 0 && !HasVisibleChild(reply.Tools))
            {
                block = reply.Thinking;
                if (block == null || block.Go == null)
                {
                    block = BindReplyBlock(reply.ThinkingRoot.gameObject);
                    reply.Thinking = block;
                }
                reply.ThinkingRoot.gameObject.SetActive(true);
            }
            else
            {
                // 顺序：追加到折叠块容器的最后（工具块也追加在这里）。
                var parent = reply.Tools != null
                    ? (Transform)reply.Tools
                    : reply.ThinkingRoot.parent;
                var clone = UnityEngine.Object.Instantiate(reply.ThinkingRoot.gameObject, parent, false);
                clone.name = "thinking-block-" + (++_itemSeq);
                clone.SetActive(true);
                if (reply.Tools != null)
                {
                    reply.Tools.gameObject.SetActive(true);
                    // Instantiate 已经放在末尾；保持顺序显式化，避免以后改动破坏它。
                    clone.transform.SetAsLastSibling();
                }
                else if (reply.ThinkingBlocks.Count > 0)
                {
                    // 旧 prefab 没有容器：插在上一块后面，别跑到正文/脚注下面。
                    var last = reply.ThinkingBlocks[reply.ThinkingBlocks.Count - 1];
                    if (last != null && last.Go != null)
                    {
                        int anchor = last.Go.transform.GetSiblingIndex();
                        clone.transform.SetSiblingIndex(anchor + 1);
                    }
                }
                block = BindReplyBlock(clone);
            }

            if (block == null) return null;
            block.Reply = reply;
            reply.ThinkingBlocks.Add(block);
            if (!reply.Blocks.Contains(block)) reply.Blocks.Add(block);
            ShowReplyLoading(reply);
            return block;
        }

        // ---------------------------------------------------------------- loading 分组头

        /// <summary>
        /// 有新的思考/工具内容：显示 loading，并把分组头切到“终端运行中…” + 转圈。
        /// 从“已完成”重新开始时自动展开内容；运行中新建块不会改变玩家手动收起的状态。
        /// </summary>
        private void ShowReplyLoading(ReplyView reply)
        {
            if (reply == null || reply.Loading == null) return;

            if (!reply.Loading.gameObject.activeSelf) reply.Loading.gameObject.SetActive(true);
            if (reply.LoadingHeader != null && !reply.LoadingHeader.activeSelf)
                reply.LoadingHeader.SetActive(true);

            bool wasDone = reply.LoadingDone;
            reply.LoadingDone = false;

            if (reply.LoadingTitle != null)
            {
                reply.LoadingTitle.text = Strings.Get("loading.running");
                reply.LoadingTitle.color = _accent;
            }
            if (reply.LoadingSpinner != null && !reply.LoadingSpinner.gameObject.activeSelf)
                reply.LoadingSpinner.gameObject.SetActive(true);
            if (reply.LoadingSpin != null) reply.LoadingSpin.enabled = true;
            if (reply.LoadingChevron != null && reply.LoadingChevron.gameObject.activeSelf)
                reply.LoadingChevron.gameObject.SetActive(false);

            if (wasDone) ApplyLoadingExpanded(reply, true);
        }

        /// <summary>
        /// 思考/工具这一轮结束：标题改成“已完成”，关掉转圈，显示展开图标并收起内容。
        /// 没有产生过任何块时什么都不做（loading 保持隐藏）。
        /// </summary>
        private void MarkLoadingDone(ReplyView reply)
        {
            if (reply == null || reply.Loading == null) return;
            if (!HasLoadingBlocks(reply))
            {
                HideReplyLoadingIfEmpty(reply);
                return;
            }

            if (!reply.Loading.gameObject.activeSelf) reply.Loading.gameObject.SetActive(true);
            if (reply.LoadingHeader != null && !reply.LoadingHeader.activeSelf)
                reply.LoadingHeader.SetActive(true);

            reply.LoadingDone = true;

            if (reply.LoadingTitle != null)
            {
                reply.LoadingTitle.text = Strings.Get("loading.done");
                reply.LoadingTitle.color = _textDim;
            }
            if (reply.LoadingSpin != null) reply.LoadingSpin.enabled = false;
            if (reply.LoadingSpinner != null && reply.LoadingSpinner.gameObject.activeSelf)
                reply.LoadingSpinner.gameObject.SetActive(false);
            if (reply.LoadingChevron != null && !reply.LoadingChevron.gameObject.activeSelf)
                reply.LoadingChevron.gameObject.SetActive(true);

            ApplyLoadingExpanded(reply, false);
            _dirty = true;
        }

        /// <summary>点 loading 标题行：展开/收起它下面的思考/工具块（运行中/已完成都可以点）。</summary>
        private void ToggleLoadingGroup(ReplyView reply)
        {
            if (reply == null || reply.Loading == null) return;
            if (reply.LoadingContent == null || ReferenceEquals(reply.LoadingContent, reply.Loading))
                return;   // 老 prefab 没有独立的内容容器，没有可收起的范围

            _suppressAutoScrollOnce = true;   // 与折叠块一致：收放时别把视图拽到底
            _scrollBottomFrames = 0;
            ApplyLoadingExpanded(reply, !reply.LoadingExpanded);
            _dirty = true;
        }

        /// <summary>真正切换 content 的显隐，并同步展开/收起图标。</summary>
        private void ApplyLoadingExpanded(ReplyView reply, bool expanded)
        {
            if (reply == null) return;
            reply.LoadingExpanded = expanded;
            if (reply.LoadingContent != null && !ReferenceEquals(reply.LoadingContent, reply.Loading)
                && reply.LoadingContent.gameObject.activeSelf != expanded)
            {
                reply.LoadingContent.gameObject.SetActive(expanded);
            }
            if (reply.LoadingChevron != null) reply.LoadingChevron.text = ChevronFor(expanded);
        }

        /// <summary>这一轮是否产生过思考/工具块（块被 loading 收起时 activeSelf 仍为 true）。</summary>
        private static bool HasLoadingBlocks(ReplyView reply)
        {
            if (reply == null) return false;
            for (int i = 0; i < reply.ThinkingBlocks.Count; i++)
            {
                var block = reply.ThinkingBlocks[i];
                if (block != null && block.Go != null && block.Go.activeSelf) return true;
            }
            if (reply.Tools != null)
            {
                for (int i = 0; i < reply.Tools.childCount; i++)
                {
                    var child = reply.Tools.GetChild(i);
                    if (child == null || child == reply.ToolTemplate) continue;
                    if (child.gameObject.activeSelf) return true;
                }
            }
            return false;
        }

        /// <summary>思考/工具都没了：把运行中区块整块隐藏，避免留下空白间距。</summary>
        private static void HideReplyLoadingIfEmpty(ReplyView reply)
        {
            if (reply == null || reply.Loading == null) return;
            if (HasLoadingBlocks(reply)) return;
            if (reply.Loading.gameObject.activeSelf) reply.Loading.gameObject.SetActive(false);
        }

        /// <summary>容器里是否有可见子节点（模板节点是 inactive，不算）。</summary>
        private static bool HasVisibleChild(RectTransform parent)
        {
            if (parent == null) return false;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child != null && child.gameObject.activeSelf) return true;
            }
            return false;
        }

        /// <summary>追加一段思考增量（只更新块正文，不进聊天正文）。</summary>
        public void AppendThinkingDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;

            if (_thinking == null || _thinking.Go == null) BeginThinking();
            if (_thinking == null) return;

            // 只累积增量，渲染交给逐帧的 RenderThinking：
            // 每个 delta 都重排整段思考是 O(n²)，长思考会把主线程拖死。
            if (_thinkingText.Length < ThinkingRenderMaxChars)
            {
                var room = ThinkingRenderMaxChars - _thinkingText.Length;
                _thinkingText.Append(delta.Length <= room ? delta : delta.Substring(0, room));
                if (_thinkingText.Length >= ThinkingRenderMaxChars) _thinkingTruncated = true;
            }
            else
            {
                // 超限之后仍然继续收到 delta：只标记一次，内容不再增长。
                _thinkingTruncated = true;
            }
            if (!_thinking.Body.gameObject.activeSelf) SetBlockBodyVisible(_thinking, true);
            _dirty = true;
        }

        /// <summary>
        /// 把当前思考增量刷到 Text。只在 Render 里调用，且长度没变化时直接返回。
        /// 游戏 Text 每个字符 4 个顶点，超过 65535 顶点（约 1.6 万字符）会渲染失败，
        /// 所以显示也有上限；超出的部分只留一句省略提示。
        /// </summary>
        private void RenderThinking()
        {
            if (_thinking == null || _thinking.Body == null) return;
            var key = _thinkingText.Length * 2 + (_thinkingTruncated ? 1 : 0);
            if (key == _thinkingRenderKey) return;
            _thinkingRenderKey = key;

            var full = _thinkingText.ToString();
            var text = full.Length > ThinkingStreamWindow
                ? "…" + full.Substring(full.Length - ThinkingStreamWindow)
                : full;
            if (_thinkingTruncated) text += Strings.Get("thinking.truncated");
            ApplyRichText(_thinking.Body, text, RichOptions13, RichTextProfile.Body);
            ApplyThinkingBodyClip(_thinking);
        }

        /// <summary>
        /// 流式思考块正文可用的最大高度：输出区可视高度的 2/3，再扣掉标题行和块内边距。
        /// 拿不到输出区高度（刚开窗、布局还没就绪）时返回 0，表示这一帧不限制。
        /// </summary>
        private float ThinkingBodyCap(Block block)
        {
            var viewport = _scroll != null ? _scroll.viewport : null;
            if (viewport == null || block == null || block.Go == null) return 0f;

            var output = viewport.rect.height;
            if (output <= 1f) return 0f;

            var chrome = block.Header != null ? block.Header.rect.height : 0f;
            var layout = block.Go.GetComponent<VerticalLayoutGroup>();
            if (layout != null) chrome += layout.padding.vertical + layout.spacing;
            return Mathf.Max(0f, output * ThinkingMaxOutputRatio - chrome);
        }

        /// <summary>
        /// 把思考块的正文裁切框同步到当前状态：流式中按输出区限高、只增不减；
        /// 收尾后（或历史块）放开限制，展开时按全文重新撑高。
        /// </summary>
        private void ApplyThinkingBodyClip(Block block)
        {
            var clip = block != null ? block.BodyClip : null;
            if (clip == null) return;
            var streaming = ReferenceEquals(block, _thinking);
            clip.GrowOnly = streaming;
            clip.MaxHeight = streaming ? ThinkingBodyCap(block) : 0f;
        }

        /// <summary>开始新一段流式思考：清掉上一段钉住的高度，让裁切框重新按当前文字量高。</summary>
        private void RestartThinkingBodyClip(Block block)
        {
            var clip = block != null ? block.BodyClip : null;
            if (clip == null) return;
            clip.Restart();
            ApplyThinkingBodyClip(block);
        }

        /// <summary>显隐折叠块正文。正文在裁切框里，两者必须一起开关，否则收起后会留下空白。</summary>
        private static void SetBlockBodyVisible(Block block, bool visible)
        {
            if (block == null) return;
            if (block.Body != null) block.Body.gameObject.SetActive(visible);
            if (block.BodyClip != null) block.BodyClip.gameObject.SetActive(visible);
        }

        /// <summary>
        /// 结束当前思考块：收起并显示摘要。正文开始出现、发起工具调用，或本轮结束/出错时调用。
        /// 思考内容为空时把块整块移除，不留一个空壳。
        /// </summary>
        /// <param name="finalize">
        /// true = 本轮已经结束（idle/final/error）：没有流式正文时把回复收尾，
        /// 下一条消息不会复用同一块。正文即将开始时保持 false，让 BeginStream 复用。
        /// </param>
        public void EndThinking(bool finalize = false)
        {
            var block = _thinking;
            _thinking = null;
            if (block == null)
            {
                if (finalize && _stream.Length == 0) FinalizeIdleReply();
                return;
            }

            var text = _thinkingText.ToString().Trim();
            if (_thinkingTruncated) text += Strings.Get("thinking.truncated");
            _thinkingText.Length = 0;
            _thinkingRenderKey = -1;
            _thinkingTruncated = false;
            if (block.Go == null)
            {
                if (finalize && _stream.Length == 0) FinalizeIdleReply();
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                var reply = block.Reply;
                if (reply != null && reply.ThinkingRoot != null
                    && ReferenceEquals(block.Go, reply.ThinkingRoot.gameObject))
                {
                    // prefab 节点保留：同一回合后续若又出 reasoning，直接复用这块，
                    // 避免销毁后 ThinkingRoot 变成伪 null 只能退回代码补建。
                    if (block.Body != null) block.Body.text = string.Empty;
                    block.RawBody = string.Empty;
                    if (block.Go != null) block.Go.SetActive(false);
                    HideReplyLoadingIfEmpty(reply);
                }
                else
                {
                    RemoveBlock(block);
                }
                if (finalize && _stream.Length == 0) FinalizeIdleReply();
                return;
            }

            var summary = Summarize(text, 48);
            SetBlock(block, Strings.Get("thinking.title"), summary, text,
                expanded: false, titleColor: _textDim);
            if (block.Reply != null)
            {
                var reply = block.Reply;
                var lineIndex = FindLineIndex(reply.Go);
                if (lineIndex >= 0)
                {
                    var line = _lines[lineIndex];
                    if (line.Thinking == null) line.Thinking = new List<ThinkingSegment>();
                    line.Thinking.Add(new ThinkingSegment { Text = text, Summary = summary });
                    _lines[lineIndex] = line;
                }
            }
            else
            {
                _lines.Add(new Line
                {
                    Kind = LineKind.System,
                    Text = Strings.Get("thinking.title") + " " + summary,
                    TurnId = _currentTurnId,
                    Go = block.Go,
                    Block = block,
                });
            }

            if (finalize && _stream.Length == 0) FinalizeIdleReply();
            TrimHistory();
            _dirty = true;
        }

        /// <summary>
        /// 本轮彻底结束（idle / 错误）：把当前回复的 loading 切成“已完成”并收起整组。
        /// 不能在每条 assistant 消息的 final 就切——工具可能还在跑，后面还会有正文。
        /// </summary>
        public void FinishTurn()
        {
            MarkCurrentReplyLoadingDone();
        }

        /// <summary>本轮已经没有待完成的思考/工具：把当前回复的 loading 切成“已完成”。</summary>
        private void MarkCurrentReplyLoadingDone()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.Kind != LineKind.Assistant || item.Reply == null) continue;
                if (item.TurnId != _currentTurnId) continue;
                MarkLoadingDone(item.Reply);
                return;
            }
        }

        /// <summary>
        /// 正文开始（或历史回答落地）时，把这一轮此前产生的思考块、工具块全部收起：
        /// 只留 header 的一行标题 + 摘要，玩家想细看可以自己点开。
        ///
        /// 不能只收当前思考块：工具块可能因为失败/截图被自动展开，
        /// 玩家自己点开的块也要在正文出现后回到收起态。
        /// </summary>
        private void CollapseReplyBlocks(ReplyView reply)
        {
            if (reply == null) return;
            for (int i = 0; i < reply.Blocks.Count; i++)
            {
                var block = reply.Blocks[i];
                if (block == null || block.Go == null) continue;
                if (ReferenceEquals(block, _thinking)) continue;   // 正在流式输出的块由 EndThinking 收尾
                if (!block.Go.activeSelf || !block.Expanded) continue;
                SetBlock(block, block.Title.text, block.Summary.text, block.RawBody,
                    expanded: false, titleColor: block.Title.color, rebuild: false);
            }
            // 正文都开始了 ⇒ 这一轮思考/工具已经结束：分组头切成“已完成”并收起。
            MarkLoadingDone(reply);
            _dirty = true;
        }

        // ---------------------------------------------------------------- 折叠块实现

        /// <summary>
        /// 在输出区创建一个折叠块，必须传入 prefab 里的 tool-template/thinking-block。
        /// 样式由模板决定，不再在代码里拼一套。
        /// </summary>
        private Block CreateBlock(string name, string title, string summary, string body,
            bool expanded, Color titleColor, RectTransform parent = null, GameObject template = null,
            ReplyView reply = null)
        {
            if (_content == null) return null;

            // 新 prefab：工具块直接克隆回复模板里的 tool-template，静态样式全部来自 prefab。
            if (template != null && parent != null)
            {
                var clone = UnityEngine.Object.Instantiate(template, parent, false);
                clone.name = name + "-" + (++_itemSeq);
                clone.SetActive(true);
                var prefabBlock = BindReplyBlock(clone);
                prefabBlock.Reply = reply;
                if (reply != null)
                {
                    reply.Blocks.Add(prefabBlock);
                    ShowReplyLoading(reply);
                }
                AddBlockItem(prefabBlock, insideReply: true);
                SetBlock(prefabBlock, title, summary, body, expanded, titleColor, rebuild: false);
                return prefabBlock;
            }

            var rootRt = UiFactory.Node(name, parent != null ? parent : _content);
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
                Accent = bar,
                Chevron = chevron,
                Title = titleText,
                TitleLayout = titleLe,
                Summary = summaryText,
                Body = bodyText,
                Expanded = expanded,
                Reply = reply,
            };
            nativeHeader.onClick += _ => ToggleBlock(block);

            // 先入 _items（TrimHistory 才能按条数裁），再套内容。
            if (reply != null)
            {
                reply.Blocks.Add(block);
                ShowReplyLoading(reply);
            }
            AddBlockItem(block, insideReply: reply != null);
            SetBlock(block, title, summary, body, expanded, titleColor, rebuild: false);
            return block;
        }

        private void SetBlock(Block block, string title, string summary, string body,
            bool expanded, Color titleColor, bool rebuild = true)
        {
            if (block == null || block.Go == null) return;
            if (block.Title == null || block.Summary == null || block.Body == null || block.Chevron == null)
                return;

            block.Title.text = title ?? string.Empty;
            block.Title.color = titleColor;
            if (block.Accent != null)
                block.Accent.color = new Color(titleColor.r, titleColor.g, titleColor.b, 0.5f);

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
            block.Body.text = FormatRichText(block.RawBody, RichOptions13, RichTextProfile.Plain).Rich;
            SetBlockBodyVisible(block, expanded && !string.IsNullOrEmpty(block.RawBody));
            ApplyThinkingBodyClip(block);

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
            _scrollBottomFrames = 0;   // 也别让上一帧的延迟贴底把位置拽回去
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
        private void AddBlockItem(Block block, bool insideReply = false)
        {
            if (block == null || block.Go == null) return;

            var item = new Item
            {
                Go = block.Go,
                Label = null,
                Kind = LineKind.System,
                FromPrefab = false,
                Reply = block.Reply,
            };

            // 回复模板里的工具块由层级顺序决定位置，不再插到 _streamItem 前面；
            // _streamItem 始终指向 reply 本身。
            if (insideReply)
            {
                _items.Add(item);
                return;
            }

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

            if (block.Reply != null) RemoveBlockReference(block);

            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_items[i].Go, block.Go)) continue;
                _items.RemoveAt(i);
                if (i == _streamItem) _streamItem = -1;
                else if (i < _streamItem) _streamItem--;
                break;
            }

            var deadKeys = new List<string>();
            foreach (var kv in _toolBlocks)
                if (ReferenceEquals(kv.Value, block)) deadKeys.Add(kv.Key);
            foreach (var key in deadKeys) _toolBlocks.Remove(key);

            var reply = block.Reply;
            if (block.Go != null)
            {
                block.Go.SetActive(false);
                UnityEngine.Object.Destroy(block.Go);
            }
            block.Go = null;
            block.Reply = null;
            HideToolsIfEmpty(reply);
            HideReplyLoadingIfEmpty(reply);
        }

        private void HideToolsIfEmpty(ReplyView reply)
        {
            if (reply == null || reply.Tools == null) return;
            for (int i = 0; i < reply.Tools.childCount; i++)
            {
                var child = reply.Tools.GetChild(i);
                if (child == null || child == reply.ToolTemplate) continue;
                if (child.gameObject.activeSelf) return;
            }
            reply.Tools.gameObject.SetActive(false);
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

        // ---------------------------------------------------------------- 思考等级 / 权限下拉框

        /// <summary>
        /// 接上 prefab 右下角的「思考等级」下拉框（游戏自己的 UIComboBox）。
        /// 玩家可见四档：关闭 / 低 / 高 / 极限。DeepSeek Flash 映射 low/high/max，
        /// Pro 只映射 high/max（low 会被 pi clamp）。
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
            combo.Items.Add(Strings.Get("thinking.level.low"));
            combo.ItemsData.Add(1);
            combo.Items.Add(Strings.Get("thinking.level.high"));
            combo.ItemsData.Add(2);
            combo.Items.Add(Strings.Get("thinking.level.max"));
            combo.ItemsData.Add(3);
            combo.UpdateItems();

            SetThinkingLevel(_thinkingLevel, notify: false);
            combo.onItemIndexChange.AddListener(OnThinkingLevelComboChanged);

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
                case Protocol.ThinkingLow: return 1;
                case Protocol.ThinkingMax: return 3;
                // minimal/medium/xhigh 只可能来自老配置文件：按 high 显示。
                default: return 2;
            }
        }

        private static string IndexToThinkingLevel(int index)
        {
            switch (index)
            {
                case 0: return Protocol.ThinkingOff;
                case 1: return Protocol.ThinkingLow;
                case 2: return Protocol.ThinkingHigh;
                case 3: return Protocol.ThinkingMax;
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

        /// <summary>
        /// 接上 prefab 输入栏里的「权限」下拉框：只读 / 受限 / 接管。
        /// 只改 Items，不动 prefab 的 RectTransform；老 bundle 没有该节点时只记日志。
        /// </summary>
        private void WirePermissionLevelCombo(RectTransform panel)
        {
            var comboNode = FindNode(panel,
                "input/editor/tool-bar/permission-group/permission-level",
                "tool-bar/permission-group/permission-level",
                "permission-group/permission-level",
                "input/editor/tool-bar/permission-level",
                "tool-bar/permission-level", "permission-level");
            var combo = comboNode != null ? comboNode.GetComponent<UIComboBox>() : null;
            if (combo == null)
            {
                Plugin.Log.LogInfo("[UI] prefab 里没有「权限」下拉框，权限只能用配置文件切换。");
                return;
            }

            _permissionCombo = combo;
            combo.translated = true;
            combo.Items.Clear();
            combo.ItemsData.Clear();
            combo.Items.Add(Strings.Get("permission.level.readonly"));
            combo.ItemsData.Add(0);
            combo.Items.Add(Strings.Get("permission.level.limited"));
            combo.ItemsData.Add(1);
            combo.Items.Add(Strings.Get("permission.level.takeover"));
            combo.ItemsData.Add(2);
            combo.UpdateItems();

            SetPermissionLevel(_permissionLevel, notify: false);
            combo.onItemIndexChange.AddListener(OnPermissionLevelComboChanged);

            Plugin.Log.LogInfo($"[UI] 已接上「权限」下拉框（当前 {_permissionLevel}）。");
        }

        private void OnPermissionLevelComboChanged()
        {
            if (_suppressPermissionEvent || _permissionCombo == null) return;

            var level = IndexToPermissionLevel(_permissionCombo.itemIndex);
            if (string.IsNullOrEmpty(level) || level == _permissionLevel) return;
            SetPermissionLevel(level, notify: true);
        }

        /// <summary>设置页改权限：复用下拉框那条路径，保证两处显示一致。</summary>
        internal void SetPermissionLevelFromSettings(string level) => SetPermissionLevel(level, notify: true);

        private void SetPermissionLevel(string level, bool notify)
        {
            if (!PluginConfig.IsValidPermissionLevel(level)) return;

            level = level.ToLowerInvariant();
            var changed = !string.Equals(level, _permissionLevel, StringComparison.Ordinal);
            _permissionLevel = level;

            if (_permissionCombo != null)
            {
                _suppressPermissionEvent = true;
                try { _permissionCombo.itemIndex = PermissionLevelToIndex(level); }
                finally { _suppressPermissionEvent = false; }
            }

            if (changed && notify) PermissionLevelChanged?.Invoke(level);
        }

        private static int PermissionLevelToIndex(string level)
        {
            switch ((level ?? string.Empty).ToLowerInvariant())
            {
                case Protocol.PermissionReadonly: return 0;
                case Protocol.PermissionTakeover: return 2;
                default: return 1;
            }
        }

        private static string IndexToPermissionLevel(int index)
        {
            switch (index)
            {
                case 0: return Protocol.PermissionReadonly;
                case 1: return Protocol.PermissionLimited;
                case 2: return Protocol.PermissionTakeover;
                default: return null;
            }
        }

        private void EnsurePermissionComboText()
        {
            if (_permissionCombo == null) return;

            var index = PermissionLevelToIndex(_permissionLevel);
            if (_permissionCombo.itemIndex == index
                && !string.IsNullOrEmpty(_permissionCombo.m_Input != null ? _permissionCombo.m_Input.text : null))
                return;

            _suppressPermissionEvent = true;
            try { _permissionCombo.itemIndex = index; }
            finally { _suppressPermissionEvent = false; }
        }


        /// <summary>当前正在流式输出正文的那条回复（没有时返回 null）。</summary>
        private ReplyView CurrentStreamReply()
        {
            if (_streamItem < 0 || _streamItem >= _items.Count) return null;
            return _items[_streamItem].Reply;
        }

        /// <summary>
        /// 正文被思考或工具调用打断时，把这一段正文按发生时刻收进折叠块容器，
        /// 然后清空流缓冲。不这么做的话，中间正文会和最终正文拼成一段，
        /// 位置也永远停在所有工具块之后。
        /// </summary>
        private void FreezeStreamSegment(ReplyView reply)
        {
            if (reply == null || _stream.Length == 0) return;

            var text = _stream.ToString().Trim();
            _stream.Length = 0;
            _lastStreamRenderLength = -1;
            if (reply.Body != null)
            {
                ReleaseLinkTargets(reply.Body);
                reply.Body.text = string.Empty;
            }
            if (string.IsNullOrEmpty(text)) return;

            AppendBodySegment(reply, text);
            _dirty = true;
        }

        /// <summary>
        /// 往折叠块容器末尾追加一段过程正文（直接显示，不折叠）。
        /// 节点来自 prefab 的 tools/text-segment；老 prefab 没有模板时克隆正文样式兜底，
        /// 仍然插进容器，保证它排在正确的时刻位置。
        /// </summary>
        private void AppendBodySegment(ReplyView reply, string rawText, bool trackLine = true)
        {
            if (reply == null || string.IsNullOrEmpty(rawText)) return;
            if (reply.Tools == null) return;   // 没有顺序容器就没法按时刻排

            var label = CreateBodySegmentLabel(reply);
            if (label == null) return;

            reply.Tools.gameObject.SetActive(true);
            label.transform.SetAsLastSibling();
            ApplyRichText(label, rawText, RichOptions14, RichTextProfile.Body);

            // 历史回放正在遍历 _lines，补建节点时不能再往里插行（会重复补建同一轮）。
            if (!trackLine) return;
            _lines.Add(new Line
            {
                Kind = LineKind.Assistant,
                Text = rawText,
                TurnId = _currentTurnId,
                Go = label.gameObject,
            });
            TrimHistory();
        }

        private Text CreateBodySegmentLabel(ReplyView reply)
        {
            if (reply == null || reply.Tools == null) return null;

            var template = reply.Tools.Find("text-segment");
            if (template != null)
            {
                var go = UnityEngine.Object.Instantiate(template.gameObject, reply.Tools);
                go.name = "text-segment-" + (++_itemSeq);
                go.SetActive(true);
                var text = go.GetComponent<Text>();
                if (text == null) { UnityEngine.Object.Destroy(go); return null; }
                text.supportRichText = true;
                text.useCheckBox = false;
                text.raycastTarget = false;
                return text;
            }

            if (reply.Body == null) return null;
            var clone = UnityEngine.Object.Instantiate(reply.Body.gameObject, reply.Tools);
            clone.name = "text-segment-" + (++_itemSeq);
            clone.SetActive(true);
            var label = clone.GetComponent<Text>();
            if (label == null) { UnityEngine.Object.Destroy(clone); return null; }
            label.supportRichText = true;
            label.useCheckBox = false;
            label.raycastTarget = false;
            label.tableColWidths = new float[0];
            label.tableRowHeights = new float[0];
            return label;
        }

        /// <summary>开始一段流式回复；后续 delta 会填进同一条消息。</summary>
        public void BeginStream(string speaker)
        {
            // 助手消息不显示说话人名字：prefab 里的样式就是裸文本（见 example-reply/body）。
            _stream.Length = 0;
            _lastStreamRenderLength = -1;
            if (_replyTemplate != null)
            {
                var reply = EnsureReplyForTurn();
                if (reply != null)
                {
                    // 正文开始：把这一轮之前的思考块、工具块全部收起。
                    CollapseReplyBlocks(reply);
                    ClearBodyExtras(reply);
                    if (reply.Body != null) reply.Body.text = string.Empty;
                    if (reply.Footer != null) reply.Footer.SetActive(false);
                    reply.CopyText = string.Empty;
                    _dirty = true;
                    return;
                }
            }

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
                if (item.Reply != null)
                {
                    if (item.Reply.Body != null)
                        RenderReplyBody(item.Reply, text);
                    var lineIndex = FindLineIndex(item.Go);
                    var time = GameClock();
                    if (lineIndex >= 0)
                    {
                        var line = _lines[lineIndex];
                        line.Text = text;
                        line.Time = time;
                        _lines[lineIndex] = line;
                    }
                    else
                    {
                        _lines.Add(new Line
                        {
                            Kind = LineKind.Assistant,
                            Text = text,
                            TurnId = _currentTurnId,
                            Go = item.Go,
                            Time = time,
                        });
                    }
                    ApplyReplyFooter(item.Reply, text, time);
                }
                else
                {
                    if (item.Label != null)
                        ApplyRichText(item.Label, text, RichOptions14, RichTextProfile.Body);
                    AttachReplyFooter(_streamItem, text);
                    _lines.Add(new Line
                    {
                        Kind = LineKind.Assistant,
                        Text = text,
                        TurnId = _currentTurnId,
                        Go = item.Go,
                        Time = GameClock(),
                    });
                }
            }
            _streamItem = -1;
            _lastStreamRenderLength = -1;
            _stream.Length = 0;
            _awaitingResponse = false;
            TrimHistory();
            _dirty = true;
        }

        /// <summary>
        /// A.15：本轮被玩家中断。把已经流出的部分回答收尾并标「已停止」，
        /// 不写成错误提示——这是玩家自己的操作，不是失败。
        /// </summary>
        public void EndInterrupted()
        {
            EndThinking(finalize: true);
            if (_streamItem >= 0 && _streamItem < _items.Count)
            {
                var reply = _items[_streamItem].Reply;
                EndStream();
                MarkStopped(reply);
                return;
            }
            // 还没流出正文（例如模型还在思考就被停下）：没有回答条目可挂，用一行提示收尾。
            AppendSystem(Strings.Get("reply.stopped"));
        }

        private void MarkStopped(ReplyView reply)
        {
            if (reply == null) return;
            if (reply.Footer != null) reply.Footer.SetActive(true);
            if (reply.Stopped != null)
            {
                reply.Stopped.text = Strings.Get("reply.stopped");
                reply.Stopped.gameObject.SetActive(true);
            }
            else
            {
                // 旧 UI bundle 的 footer 里还没有 stopped 节点：退化成一行提示，标记不能丢。
                AppendSystem(Strings.Get("reply.stopped"));
            }
            _dirty = true;
        }

        // ---------------------------------------------------------------- 回复模板绑定

        /// <summary>
        /// 创建（或复用）当前回合的回复条目。思考/工具/正文都落在同一个 reply 里，
        /// 保证视觉顺序是 思考 → 工具 → 正文 → 蓝图卡片 → 时间/复制。
        /// </summary>
        private ReplyView EnsureReplyForTurn()
        {
            if (_content == null) return null;

            if (_streamItem >= 0 && _streamItem < _items.Count &&
                _items[_streamItem].Kind == LineKind.Assistant &&
                _items[_streamItem].Reply != null &&
                _items[_streamItem].TurnId == _currentTurnId)
            {
                return _items[_streamItem].Reply;
            }

            var index = CreateItem(LineKind.Assistant);
            if (index < 0 || index >= _items.Count) return null;
            _streamItem = index;
            var item = _items[index];
            if (FindLineIndex(item.Go) < 0)
            {
                _lines.Add(new Line
                {
                    Kind = LineKind.Assistant,
                    Text = string.Empty,
                    TurnId = item.TurnId,
                    Go = item.Go,
                    Time = GameClock(),
                });
            }
            _dirty = true;
            return item.Reply;
        }

        private int FindLineIndex(GameObject go)
        {
            if (go == null) return -1;
            for (int i = _lines.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_lines[i].Go, go)) return i;
            return -1;
        }

        private int FindReplyItemForCard()
        {
            if (_streamItem >= 0 && _streamItem < _items.Count &&
                _items[_streamItem].Kind == LineKind.Assistant &&
                _items[_streamItem].Reply != null &&
                _items[_streamItem].TurnId == _currentTurnId)
                return _streamItem;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Kind == LineKind.Assistant && _items[i].Reply != null &&
                    _items[i].TurnId == _currentTurnId)
                    return i;
            }
            EnsureReplyForTurn();
            return _streamItem;
        }

        /// <summary>把时间/复制按钮的数据写进 prefab 的 footer（只改文字与显隐）。</summary>
        private void ApplyReplyFooter(ReplyView view, string rawText, string time)
        {
            if (view == null) return;
            view.CopyText = ToPlainText(rawText);
            var hasText = !string.IsNullOrEmpty(rawText);
            if (view.Footer != null) view.Footer.SetActive(hasText);
            if (view.Time != null)
                view.Time.text = FormatGameTime(time);
            if (view.CopyLabel != null) view.CopyLabel.text = Strings.Get("reply.copy");
            UpdateReplyActionAvailability();
        }

        /// <summary>
        /// 重新生成/撤回只给**最后一条回答**：会话是线性的，重开中间一轮会把新回答追加到末尾、
        /// 还会改变后续上下文，语义不对。
        /// 最后一条是压缩记录时只显示撤回（= 撤销压缩）。
        /// </summary>
        private void UpdateReplyActionAvailability()
        {
            // 以 `_turns` 的最后一轮为准，和 RequestRegenerate / RequestWithdraw 的守卫一致。
            // 压缩发生在回答中途时，最后一条回答仍属于提问那一轮，但最后一条回合是压缩记录；
            // 这时只把撤回给压缩记录，避免出现“按钮看得见、点了没反应”。
            var lastTurnId = _turns.Count > 0 ? _turns[_turns.Count - 1].Id : 0;
            var last = -1;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Kind == LineKind.Assistant && _items[i].Reply != null &&
                    _items[i].TurnId == lastTurnId)
                {
                    last = i;
                    break;
                }
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Kind != LineKind.Assistant || item.Reply == null) continue;
                var isLast = i == last;
                var compaction = IsCompactionTurn(item.TurnId);
                SetActive(item.Reply.Return, isLast && !compaction);
                SetActive(item.Reply.Withdraw, isLast);
            }
        }

        private static void SetActive(Button button, bool active)
        {
            if (button != null && button.gameObject.activeSelf != active)
                button.gameObject.SetActive(active);
        }

        private bool IsCompactionTurn(int turnId)
        {
            for (int i = 0; i < _turns.Count; i++)
            {
                if (_turns[i].Id == turnId) return _turns[i].IsCompaction;
            }
            return false;
        }

        /// <summary>把协议里的蓝图卡片数据填进一张固定卡片。</summary>
        private void ApplyCard(CardView view, BlueprintCardInfo card)
        {
            if (view == null || card == null) return;

            view.Card = card;
            view.LinkTarget = null;
            if (view.UseLabel != null) view.UseLabel.text = Strings.Get("blueprint.card.paste");
            if (view.Name != null)
                view.Name.text = FormatInline(CardTitle(card));
            if (view.Description != null)
                view.Description.text = string.IsNullOrEmpty(card.Description)
                    ? Strings.Get("blueprint.card.no_description")
                    : FormatInline(card.Description);
            if (view.Meta != null)
                view.Meta.text = BuildCardMeta(card);
            if (view.ThumbLabel != null)
            {
                // 缩略图文字是蓝图数据里的 shortDesc（DisplayTitle），不是物品名；
                // 与游戏 short-text 一样按纯文本显示，不过富文本。
                var thumbText = card.DisplayTitle;
                view.ThumbLabel.text = string.IsNullOrEmpty(thumbText)
                    ? Strings.Get("blueprint.card.thumb")
                    : thumbText;
            }

            var icon = ResolveBlueprintIcon(card);
            if (view.ThumbIconRoot != null) view.ThumbIconRoot.SetActive(icon != null);
            if (view.ThumbFallback != null) view.ThumbFallback.SetActive(icon == null);
            if (view.ThumbIcon != null) view.ThumbIcon.sprite = icon;

            var iconMapping = TryGetPreferredIconMapping();
            AssignIconMapping(view.Name, iconMapping);
            AssignIconMapping(view.Description, iconMapping);
            // 缩略图文字与游戏 short-text 一样保持纯文本（rich=false），不挂图标表。
        }

        /// <summary>卡片标题显示蓝图存放路径（相对蓝图根），而不是缩略图文字。</summary>
        private static string CardTitle(BlueprintCardInfo card)
        {
            var path = !string.IsNullOrEmpty(card.RelPath)
                ? card.RelPath
                : !string.IsNullOrEmpty(card.FileName)
                    ? card.FileName
                    : card.Path;
            if (string.IsNullOrEmpty(path)) return card.DisplayTitle;
            path = path.Replace('\\', '/').TrimStart('/');
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);
            return path;
        }

        /// <summary>卡片底部两行：作者/创建日期 + 版本号。</summary>
        private static string BuildCardMeta(BlueprintCardInfo card)
        {
            var author = string.IsNullOrEmpty(card.Author)
                ? Strings.Get("blueprint.card.unknown_author")
                : card.Author;
            var created = string.IsNullOrEmpty(card.CreatedAt)
                ? Strings.Get("blueprint.card.unknown_date")
                : card.CreatedAt;
            var version = string.IsNullOrEmpty(card.Version)
                ? Strings.Get("blueprint.card.unknown_version")
                : card.Version;
            // 脚注直接显示信息，不再带“作者/创建/版本”这些备注词。
            return author + " · " + created + " · " + version;
        }

        private bool HasReplyContent(ReplyView view)
        {
            if (view == null) return false;
            if (view.Body != null && !string.IsNullOrEmpty(view.Body.text)) return true;
            for (int i = 0; i < view.BodyExtras.Count; i++)
            {
                var go = view.BodyExtras[i];
                if (go != null && go.activeSelf) return true;
            }
            if (view.CardViews.Count > 0) return true;
            for (int i = 0; i < view.ThinkingBlocks.Count; i++)
            {
                var block = view.ThinkingBlocks[i];
                if (block != null && block.Go != null && block.Go.activeSelf) return true;
            }
            if (view.Tools != null)
            {
                for (int i = 0; i < view.Tools.childCount; i++)
                {
                    var child = view.Tools.GetChild(i);
                    if (child == null || child == view.ToolTemplate) continue;
                    if (child.gameObject.activeSelf) return true;
                }
            }
            return false;
        }

        /// <summary>思考/工具结束后没有正文时，收尾并避免下一条消息复用同一个空回复。</summary>
        private void FinalizeIdleReply()
        {
            if (_streamItem < 0 || _streamItem >= _items.Count) return;
            var item = _items[_streamItem];
            if (item.Reply == null)
            {
                _streamItem = -1;
                _lastStreamRenderLength = -1;
                _stream.Length = 0;
                return;
            }

            var text = _stream.ToString();
            if (!string.IsNullOrEmpty(text) && item.Reply.Body != null)
                RenderReplyBody(item.Reply, text);

            var lineIndex = FindLineIndex(item.Go);
            var time = GameClock();
            if (lineIndex >= 0)
            {
                var line = _lines[lineIndex];
                line.Text = text;
                line.Time = string.IsNullOrEmpty(line.Time) ? time : line.Time;
                _lines[lineIndex] = line;
            }
            ApplyReplyFooter(item.Reply, text, lineIndex >= 0 ? _lines[lineIndex].Time : time);

            _streamItem = -1;
            _lastStreamRenderLength = -1;
            _stream.Length = 0;

            if (!HasReplyContent(item.Reply)) RemoveReply(item.Reply);
        }

        private void RemoveReply(ReplyView view)
        {
            if (view == null) return;
            ReleaseLinkTargets(view.Body);
            for (var i = 0; i < view.BodyExtras.Count; i++)
            {
                var go = view.BodyExtras[i];
                if (go != null) ReleaseLinkTargets(go.GetComponent<Text>());
            }
            RemoveItemsUnder(view.Go);
            var lineIndex = FindLineIndex(view.Go);
            if (lineIndex >= 0) _lines.RemoveAt(lineIndex);
            if (view.Go != null) UnityEngine.Object.Destroy(view.Go);
        }

        private void RemoveBlockReference(Block block)
        {
            if (block == null) return;
            if (ReferenceEquals(_thinking, block))
            {
                _thinking = null;
                _thinkingText.Length = 0;
            }
            var reply = block.Reply;
            if (reply != null)
            {
                reply.ThinkingBlocks.Remove(block);
                reply.Blocks.Remove(block);
            }
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
            _linkTargets.Clear();
            _linkHandlers.Clear();
            _stream.Length = 0;
            _streamItem = -1;
            _lastStreamRenderLength = -1;
            _turns.Clear();
            _currentTurnId = 0;
            _nextTurnId = 0;
            _focusedTurnId = 0;
            _indexTurnScanScroll = float.NaN;
            _awaitingResponse = false;
            ClearConversationGroups();
            RefreshIndexEmpty();
            _dirty = true;
        }

        /// <summary>
        /// 游戏时钟：`H:mm:ss`，小时 000–999，满 1000 小时归零。
        /// 不按 24 小时归零——那会让存档里的时间读数隔天就对不上。
        /// </summary>
        public static string CurrentGameClock() => GameClock();

        /// <summary>
        /// 给 Plugin 回存 reply.time 用：绝对游戏时刻。
        /// 时钟读数会归零，算不出间隔，时间标记必须靠这个值。
        /// 没有游戏数据时返回 0。
        /// </summary>
        public static long CurrentGameTick() => GameMain.data != null ? GameMain.gameTick : 0L;

        /// <summary>旧历史只存 H:mm；显示时补成 H:mm:ss，保证所有脚注都含秒。</summary>
        private static string FormatGameTime(string value)
        {
            if (string.IsNullOrEmpty(value)) return GameClock();
            if (value == "--:--") return value;
            var parts = value.Split(':');
            if (parts.Length == 2 && parts[1].Length == 2
                && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
                return value + ":00";
            return value;
        }

        private static string GameClock()
        {
            if (GameMain.data == null) return "--:--";
            long sec = GameMain.gameTick / 60L;
            int hour = (int)((sec / 3600L) % 1000L);
            int min = (int)((sec / 60L) % 60L);
            int second = (int)(sec % 60L);
            return hour.ToString("000") + ":" + min.ToString("00") + ":" + second.ToString("00");
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

            var footer = "\n<size=16><color=#8FA3B8>" + Strings.Get("reply.time", GameClock())
                + " · " + Strings.Get("reply.footer.copy") + "</color></size>";
            ApplyRichText(item.Label, rawText, RichOptions14, RichTextProfile.Body);
            item.Label.text = item.Label.text + footer;

            var button = item.Go.GetComponent<Button>();
            if (button == null) button = item.Go.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            var copy = ToPlainText(rawText);
            button.onClick.AddListener(() => GUIUtility.systemCopyBuffer = copy);
        }

        private void AddMessage(LineKind kind, string text, string gameTime = null)
        {
            var line = new Line
            {
                Kind = kind,
                Text = text,
                TurnId = _currentTurnId,
                // 用户消息也要显示时间（footer）：当场提问记当前游戏时间，
                // 历史回放用 sidecar 带回来的 gameTime。
                Time = kind == LineKind.System
                    ? null
                    : (string.IsNullOrEmpty(gameTime) ? GameClock() : gameTime),
            };

            // 窗口还没 Create 时只记逻辑行；Create 末尾用 RebuildTranscript 补建。
            // 否则会在 Canvas 之外建出一堆孤儿 Text，打开窗口后历史反而看不见。
            if (_content == null)
            {
                _lines.Add(line);
                TrimHistory();
                _dirty = true;
                return;
            }

            // 非流式 final：如果本轮已经有一个只有思考块的回复，正文直接落进它，
            // 不要另起一条消息，否则“思考 → 正文”会被拆成两段。
            if (kind == LineKind.Assistant && _replyTemplate != null)
            {
                var existing = FindReplyItemForText();
                if (existing >= 0 && existing < _items.Count && _items[existing].Reply != null)
                {
                    var replyItem = _items[existing];
                    // 正文落地：收起这一轮的思考块/工具块（与流式正文同一规则）。
                    CollapseReplyBlocks(replyItem.Reply);
                    if (replyItem.Reply.Body != null)
                        RenderReplyBody(replyItem.Reply, text);
                    line.Go = replyItem.Go;
                    line.Time = string.IsNullOrEmpty(gameTime) ? GameClock() : gameTime;
                    ApplyReplyFooter(replyItem.Reply, text, line.Time);
                    var lineIndex = FindLineIndex(replyItem.Go);
                    if (lineIndex >= 0)
                    {
                        var oldLine = _lines[lineIndex];
                        line.Thinking = oldLine.Thinking;
                        line.Cards = oldLine.Cards;
                        line.LinkTargets = oldLine.LinkTargets;
                        _lines[lineIndex] = line;
                    }
                    else
                    {
                        _lines.Add(line);
                    }
                    if (_streamItem == existing) _streamItem = -1;
                    TrimHistory();
                    _dirty = true;
                    return;
                }
            }

            var index = CreateItem(kind);
            if (index >= 0 && index < _items.Count)
            {
                var item = _items[index];
                line.Go = item.Go;
                if (item.Reply != null)
                {
                    if (item.Reply.Body != null)
                        RenderReplyBody(item.Reply, text);
                    ApplyReplyFooter(item.Reply, text, line.Time);
                }
                else
                {
                    if (item.Label != null)
                    {
                        if (kind == LineKind.User) ApplyPlainText(item.Label, text);
                        else ApplyRichText(item.Label, text, RichOptions14, RichTextProfile.Body);
                    }
                    FitBubbleWidth(item.Go, text);
                    if (kind == LineKind.Assistant) AttachReplyFooter(index, text);
                    else if (kind == LineKind.User) ApplyUserFooter(index, text, line.Time);
                }
            }

            _lines.Add(line);
            TrimHistory();
            _dirty = true;
        }

        private int FindReplyItemForText()
        {
            if (_streamItem >= 0 && _streamItem < _items.Count &&
                _items[_streamItem].Kind == LineKind.Assistant &&
                _items[_streamItem].Reply != null &&
                _items[_streamItem].TurnId == _currentTurnId)
                return _streamItem;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.Kind != LineKind.Assistant || item.Reply == null ||
                    item.TurnId != _currentTurnId) continue;
                if (item.Reply.Body == null || string.IsNullOrEmpty(item.Reply.Body.text))
                    return i;
            }
            return -1;
        }

        /// <summary>窗口首次 Create 时，把此前已进入 _lines 的消息补建成可见项。</summary>
        private void RebuildTranscript()
        {
            if (_content == null || _lines.Count == 0) return;

            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                _currentTurnId = line.TurnId;

                // 单条消息失败只丢掉这一条：建窗中途抛异常会让整个窗口打不开。
                try
                {
                    // 蓝图卡片不是文本项：新 prefab 把它挂进本轮回复的固定卡片网格；
                    // 旧 prefab 没有回复模板时仍按数据建一张独立卡片。
                    if (line.Kind == LineKind.Blueprint)
                    {
                        var cards = line.Cards;
                        if (_replyTemplate != null && cards != null && cards.Count > 0)
                        {
                            var replyIndex = FindRebuiltReplyItem(line.TurnId);
                            if (replyIndex >= 0)
                            {
                                var replyItem = _items[replyIndex];
                                AddCards(replyItem.Reply, cards);
                                var replyLineIndex = FindLineIndex(replyItem.Go);
                                if (replyLineIndex >= 0)
                                {
                                    var replyLine = _lines[replyLineIndex];
                                    if (replyLine.Cards == null) replyLine.Cards = new List<BlueprintCardInfo>();
                                    for (int c = 0; c < cards.Count; c++)
                                        if (cards[c] != null) replyLine.Cards.Add(cards[c]);
                                    _lines[replyLineIndex] = replyLine;
                                }
                                _lines.RemoveAt(i);
                                i--;
                                continue;
                            }

                            var newIndex = CreateItem(LineKind.Assistant);
                            if (newIndex >= 0 && newIndex < _items.Count)
                            {
                                var newItem = _items[newIndex];
                                line.Kind = LineKind.Assistant;
                                line.Text = string.Empty;
                                line.Go = newItem.Go;
                                line.Time = string.IsNullOrEmpty(line.Time) ? GameClock() : line.Time;
                                _lines[i] = line;
                                AddCards(newItem.Reply, cards);
                                if (newItem.Reply != null && newItem.Reply.Body != null)
                                    newItem.Reply.Body.text = string.Empty;
                                ApplyReplyFooter(newItem.Reply, string.Empty, line.Time);
                                continue;
                            }
                        }

                        var fallbackCard = cards != null && cards.Count > 0 ? cards[0] : null;
                        line.Go = CreateBlueprintCard(fallbackCard);
                        if (line.Go != null) _lines[i] = line;
                        continue;
                    }

                    var index = CreateItem(line.Kind);
                    if (index < 0 || index >= _items.Count) continue;
                    var item = _items[index];
                    line.Go = item.Go;
                    _lines[i] = line;

                    if (item.Reply != null)
                    {
                        // 历史回放：steps 里有思考+工具的完整顺序；实时回合只有 Thinking。
                        if (line.Steps != null && line.Steps.Count > 0)
                            AppendHistorySteps(item.Reply, line.Steps);
                        else if (line.Thinking != null && line.Thinking.Count > 0)
                            RebuildThinking(item.Reply, line);
                        // 补建的都是已经结束的内容：loading 显示“已完成”并收起。
                        if (item.Reply.Loading != null) MarkLoadingDone(item.Reply);
                        AddCards(item.Reply, line.Cards);
                        AddLinkCards(item.Reply, line.LinkTargets);
                        if (item.Reply.Body != null)
                            RenderReplyBody(item.Reply, line.Text);
                        ApplyReplyFooter(item.Reply, line.Text, line.Time);
                    }
                    else
                    {
                        if (item.Label != null)
                        {
                            if (line.Kind == LineKind.User) ApplyPlainText(item.Label, line.Text);
                            else ApplyRichText(item.Label, line.Text, RichOptions14, RichTextProfile.Body);
                        }
                        FitBubbleWidth(item.Go, line.Text);
                        if (line.Kind == LineKind.Assistant) AttachReplyFooter(index, line.Text);
                        else if (line.Kind == LineKind.User) ApplyUserFooter(index, line.Text, line.Time);
                    }

                    if (line.Kind == LineKind.User && line.TurnId > 0)
                        UpdateTurnUserGo(line.TurnId, line.Go);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[UI] 补建聊天记录第 {i} 条失败（{line.Kind}）：{ex.Message}");
                }
            }

            if (_lines.Count > 0) _currentTurnId = _lines[_lines.Count - 1].TurnId;
            TrimHistory();
            _dirty = true;
            ScrollToBottom();
        }

        private int FindRebuiltReplyItem(int turnId)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Kind == LineKind.Assistant && _items[i].Reply != null &&
                    _items[i].TurnId == turnId)
                    return i;
            }
            return -1;
        }

        /// <summary>按记录顺序把一次回复里的多段思考逐块重建（每段一个折叠块）。</summary>
        private void RebuildThinking(ReplyView reply, Line line)
        {
            if (reply == null || reply.ThinkingRoot == null) return;
            if (line.Thinking == null) return;

            for (int i = 0; i < line.Thinking.Count; i++)
            {
                var segment = line.Thinking[i];
                if (string.IsNullOrEmpty(segment.Text)) continue;

                var block = EnsureThinkingBlock(reply);
                if (block == null) return;
                var summary = string.IsNullOrEmpty(segment.Summary)
                    ? Summarize(segment.Text, 48)
                    : segment.Summary;
                SetBlock(block, Strings.Get("thinking.title"), summary, segment.Text,
                    expanded: false, titleColor: _textDim);
            }
        }

        // ---------------------------------------------------------------- 历史回放

        /// <summary>
        /// 回放一条历史助手回答（`save.bound.history`，协议 A.6）。
        ///
        /// 顺序与实时一致：先按 <c>steps</c> 逐条重建思考块/工具块（全部收起），
        /// 再落正文与脚注；蓝图卡片由调用方在这之后追加。
        /// 窗口还没建好时只记逻辑行，Create 后由 <see cref="RebuildTranscript"/> 补建。
        /// </summary>
        public void AppendHistoryReply(HistoryMessageInfo message)
        {
            if (message == null) return;
            var text = message.Text ?? string.Empty;
            var time = string.IsNullOrEmpty(message.GameTime) ? "--:--" : message.GameTime;

            if (_content == null)
            {
                _lines.Add(new Line
                {
                    Kind = LineKind.Assistant,
                    Text = text,
                    TurnId = _currentTurnId,
                    Time = time,
                    Steps = message.Steps,
                });
                _awaitingResponse = false;
                TrimHistory();
                _dirty = true;
                return;
            }

            // 老 prefab 没有回复模板：退回裸文本，与实时路径一致。
            var reply = _replyTemplate != null ? EnsureReplyForTurn() : null;
            if (reply == null)
            {
                AppendLine(Strings.Get("agent"), text, time);
                return;
            }

            AppendHistorySteps(reply, message.Steps);
            // 回放的是已经完成的回答：loading 直接显示“已完成”，内容收起。
            if (reply.Loading != null) MarkLoadingDone(reply);
            if (reply.Body != null)
                RenderReplyBody(reply, text);
            ApplyReplyFooter(reply, text, time);

            var lineIndex = FindLineIndex(reply.Go);
            if (lineIndex >= 0)
            {
                var line = _lines[lineIndex];
                line.Text = text;
                line.Time = time;
                line.Steps = message.Steps;
                _lines[lineIndex] = line;
            }

            _streamItem = -1;
            _lastStreamRenderLength = -1;
            _stream.Length = 0;
            _awaitingResponse = false;
            TrimHistory();
            _dirty = true;
        }

        /// <summary>按顺序重建历史回答里的思考块/工具块（都保持收起态）。</summary>
        private void AppendHistorySteps(ReplyView reply, List<HistoryStepInfo> steps)
        {
            if (reply == null || steps == null || steps.Count == 0) return;

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step == null) continue;

                if (step.Kind == "thinking")
                {
                    if (string.IsNullOrEmpty(step.Text)) continue;
                    var block = EnsureThinkingBlock(reply);
                    if (block == null) continue;
                    SetBlock(block, Strings.Get("thinking.title"), Summarize(step.Text, 48), step.Text,
                        expanded: false, titleColor: _textDim, rebuild: false);
                }
                else if (step.Kind == "text")
                {
                    if (string.IsNullOrEmpty(step.Text)) continue;
                    AppendBodySegment(reply, step.Text, trackLine: false);
                }
                else if (step.Kind == "tool")
                {
                    AppendHistoryToolBlock(reply, step);
                }
            }
        }

        /// <summary>历史里的一个工具块：复用实时工具块的外观，但不再进 _toolBlocks。</summary>
        private void AppendHistoryToolBlock(ReplyView reply, HistoryStepInfo step)
        {
            if (reply == null || step == null || string.IsNullOrEmpty(step.Name)) return;

            var label = ToolNames.Label(step.Name);
            var title = Strings.Get("tool.title", label);
            var ok = step.Ok;
            var result = string.IsNullOrEmpty(step.Summary)
                ? (ok == true ? Strings.Get("tool.ok")
                    : ok == false ? Strings.Get("tool.failed") : Strings.Get("tool.running"))
                : step.Summary;
            var summary = Summarize(result, 64);
            var body = BuildToolBody(step.Args, step.Detail ?? step.Summary);

            if (reply.Tools != null) reply.Tools.gameObject.SetActive(true);
            var block = CreateBlock("tool-block", title, summary, body,
                expanded: false,
                // 与实时一致：失败用警示色，其余用暗色。
                titleColor: ok == false ? WarnColor : _textDim,
                parent: reply.Tools,
                template: reply.ToolTemplate != null ? reply.ToolTemplate.gameObject : null,
                reply: reply);
            if (block == null) return;

            block.Args = step.Args;
            block.ToolName = step.Name;
            // 不单独记 Line：工具块是回复的子节点，随回复一起裁剪/删除；
            // 而且 RebuildTranscript 正在遍历 _lines，这里再插入会让同一轮被重复补建。
            // 思考/工具正文已经算在回复行的 Steps 里（LineCharCount）。
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
            if (_chatIndexContent != null)
            {
                _conversationTemplate = _chatIndexContent.Find("conversation-group-template") as RectTransform;
                _recordTemplate = _conversationTemplate != null
                    ? _conversationTemplate.Find("records/record-row-template") as RectTransform
                    : null;
                _expandMoreTemplate = _conversationTemplate != null
                    ? _conversationTemplate.Find("records/expand-more-template") as RectTransform
                    : null;
            }
            var emptyRt = _chatIndex.Find("chat-index-empty") as RectTransform;
            _chatIndexEmpty = emptyRt != null ? emptyRt.GetComponent<Text>() : null;
            _newConversationButton = _chatIndex
                .Find("chat-index-topbar/new-conversation")?.GetComponent<Button>();
            EnsureConversationTitle();

            if (_chatIndexScroll == null || _chatIndexContent == null ||
                _conversationTemplate == null || _recordTemplate == null || _expandMoreTemplate == null)
            {
                Plugin.Log.LogWarning(
                    "[UI] prefab 的 chat-index 结构不完整：需要 conversation-group-template/record-row-template/expand-more-template");
                return;
            }

            _conversationTemplate.gameObject.SetActive(false);
            _recordTemplate.gameObject.SetActive(false);
            _expandMoreTemplate.gameObject.SetActive(false);

            if (_newConversationButton != null)
            {
                _newConversationButton.onClick.RemoveAllListeners();
                _newConversationButton.onClick.AddListener(OnNewConversationClicked);
                try
                {
                    var tipButton = _newConversationButton.GetComponent<UIButton>();
                    if (tipButton != null)
                    {
                        var tips = tipButton.tips;
                        tips.tipTitle = Strings.Get("chat.index.new.tip");
                        tips.tipText = string.Empty;
                        if (tips.corner == 0) tips.corner = 9;
                        tipButton.tips = tips;
                        tipButton.UpdateTip();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("新会话按钮提示失败：" + ex.Message);
                }
            }
            else
            {
                Plugin.Log.LogWarning("[UI] prefab 缺少 chat-index-topbar/new-conversation，新建会话不可用");
            }

            if (_hostCanvas != null)
                SetLayerRecursively(_chatIndex, _hostCanvas.gameObject.layer);

            RebuildConversationIndex();
        }

        private void AddTurn(
            int turnId,
            string text,
            long timestampMs = 0,
            bool isCompaction = false,
            string compactionId = null,
            int tokensBefore = 0,
            int estimatedTokensAfter = 0,
            bool addIndexRecord = true)
        {
            var userGo = _lines.Count > 0 ? _lines[_lines.Count - 1].Go : null;
            var entry = new TurnEntry
            {
                Id = turnId,
                Text = text ?? string.Empty,
                Timestamp = timestampMs,
                UserGo = userGo,
                IsCompaction = isCompaction,
                CompactionId = compactionId,
                TokensBefore = tokensBefore,
                EstimatedTokensAfter = estimatedTokensAfter,
            };
            _turns.Add(entry);
            if (addIndexRecord && !isCompaction) AddActiveConversationRecord(entry.Text, timestampMs);
            if (!isCompaction) RefreshIndexEmpty();
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

        /// <summary>实时提问：把用户消息追加到活动会话的索引，并在第一条消息时更新标题。</summary>
        private void AddActiveConversationRecord(string text, long timestampMs)
        {
            if (string.IsNullOrEmpty(text)) return;
            var conversation = FindConversation(_activeConversationId);
            if (conversation == null)
            {
                // 老 sidecar 没有 conversations：用当前对话构造一个兼容分组。
                conversation = ConversationInfo.Create("legacy", Strings.Get("chat.conversation.current"));
                _conversations.Add(conversation);
                _activeConversationId = conversation.Id;
            }
            var record = ConversationRecordInfo.Create(
                text,
                timestampMs > 0 ? timestampMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            conversation.AddRecord(record);
            if (conversation.RecordCount == 1)
                conversation.SetTitle(ConversationTitle(text));
            conversation.MarkUpdated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // 正在聊的会话保持展开，并移到列表最前（与 sidecar 的 updatedAt 排序一致）。
            _conversationExpanded[conversation.Id] = true;
            if (!_conversationVisible.TryGetValue(conversation.Id, out var visible) || visible < 5)
                _conversationVisible[conversation.Id] = 5;
            _conversations.Remove(conversation);
            _conversations.Insert(0, conversation);
            SortConversations();
            RebuildConversationIndex();
        }

        private static string ConversationTitle(string text)
        {
            var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length == 0) return Strings.Get("chat.conversation.new");
            return value.Length <= 18 ? value : value.Substring(0, 18) + "…";
        }

        private ConversationInfo FindConversation(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var conversation in _conversations)
                if (conversation != null && conversation.Id == id) return conversation;
            return null;
        }

        /// <summary>
        /// 左侧列表顺序只由两件事决定：是否置顶、最后一条回答的游戏时间。
        /// 切换会话不写入任何参与排序的字段，所以点一下不会让会话跳到最前面。
        /// 同分（比如都还没有回答过）保持列表原有顺序——稳定排序，见 OrderConversations。
        /// 返回顺序是否真的变了——变了才值得重建索引。
        /// </summary>
        private bool SortConversations()
        {
            var before = _conversations.ConvertAll(conversation => conversation?.Id);
            var ordered = OrderConversations(_conversations);
            _conversations.Clear();
            _conversations.AddRange(ordered);
            for (int i = 0; i < _conversations.Count; i++)
            {
                if (!string.Equals(before[i], _conversations[i]?.Id, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 稳定排序：LINQ 的 OrderBy 对相等的项保留原顺序，List.Sort 不保证。
        /// 排序键只有「是否置顶」和「最后回复时间」（会话文件最后写入时间，即最后一条回答）；
        /// 时间相同再按创建时间。切换会话不改这两个字段，所以点一下不会让列表重排。
        /// </summary>
        private static List<ConversationInfo> OrderConversations(List<ConversationInfo> source)
        {
            return source
                .OrderBy(conversation => conversation?.Pinned == true ? 0 : 1)
                .ThenByDescending(conversation => conversation?.UpdatedAt ?? 0L)
                .ThenByDescending(conversation => conversation?.CreatedAt ?? 0L)
                .ToList();
        }

        /// <summary>
        /// 对齐某个会话的置顶状态并重排列表。
        /// 玩家点击时先乐观置顶，sidecar 回执（或发送失败回滚）再走这里。
        /// </summary>
        public void SetConversationPinned(string conversationId, bool pinned)
        {
            var conversation = FindConversation(conversationId);
            if (conversation == null || conversation.Pinned == pinned) return;
            conversation.SetPinned(pinned);
            SortConversations();
            RebuildConversationIndex();
        }

        public string ActiveConversationId => _activeConversationId;

        /// <summary>用 sidecar 返回的会话列表整体替换左侧索引（save.bind / 删除会话后）。</summary>
        public void ApplyConversations(List<ConversationInfo> conversations, string activeId)
        {
            _conversations.Clear();
            if (conversations != null)
                foreach (var conversation in conversations)
                    if (conversation != null) _conversations.Add(conversation);
            _activeConversationId = activeId;
            SortConversations();
            if (string.IsNullOrEmpty(_activeConversationId) && _conversations.Count > 0)
                _activeConversationId = _conversations[0].Id;
            MarkConversationSeen(_activeConversationId);
            RebuildConversationIndex();
        }

        /// <summary>新建或更新一个会话分组（conversation.created / switched）。</summary>
        public void UpsertConversation(ConversationInfo conversation, bool makeActive)
        {
            if (conversation == null || string.IsNullOrEmpty(conversation.Id)) return;
            var index = -1;
            for (int i = 0; i < _conversations.Count; i++)
            {
                if (_conversations[i] != null && _conversations[i].Id == conversation.Id)
                {
                    index = i;
                    break;
                }
            }
            if (index >= 0) _conversations[index] = conversation;
            else _conversations.Insert(0, conversation);
            SortConversations();
            if (makeActive)
            {
                _activeConversationId = conversation.Id;
                MarkConversationSeen(_activeConversationId);
            }
            RebuildConversationIndex();
        }

        /// <summary>
        /// 会话被 sidecar 丢弃后从左侧索引移除（新建但没发过消息的空会话，切走就没意义了）。
        /// 只接受非活动会话，避免把当前正文的归属删掉。
        /// </summary>
        public void RemoveConversation(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            if (conversationId == _activeConversationId) return;
            int removed = _conversations.RemoveAll(c => c != null && c.Id == conversationId);
            if (removed == 0) return;
            _conversationExpanded.Remove(conversationId);
            _conversationVisible.Remove(conversationId);
            _conversationStatus.Remove(conversationId);
            RebuildConversationIndex();
        }

        /// <summary>模型总结标题到达：只改左侧该会话的标题，不重放正文、不重建索引。</summary>
        public void UpdateConversationTitle(string conversationId, string title)
        {
            if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(title)) return;
            var view = FindConversationView(conversationId);
            if (view != null)
            {
                if (view.Info.Title == title) return;
                view.Info.SetTitle(title);
                UpdateConversationHeader(view);
            }
            else
            {
                var conversation = FindConversation(conversationId);
                if (conversation == null || conversation.Title == title) return;
                conversation.SetTitle(title);
            }
            if (conversationId == _activeConversationId &&
                (_conversationTitleInput == null || !_conversationTitleInput.isFocused))
                RefreshConversationTitle();
        }

        public void SetActiveConversation(string conversationId)
        {
            _activeConversationId = conversationId;
            MarkConversationSeen(conversationId);
            RebuildConversationIndex();
        }

        /// <summary>切到该会话即视为已读：清掉 completed/new，warning 保留到操作解决。</summary>
        private void MarkConversationSeen(string conversationId)
        {
            if (!string.IsNullOrEmpty(conversationId) &&
                _conversationStatus.TryGetValue(conversationId, out var status) &&
                status == ConversationStatus.Completed)
            {
                _conversationStatus.Remove(conversationId);
            }
        }

        /// <summary>回放结束后把 TurnEntry 和左侧记录行重新挂上，并处理待定位的记录。</summary>
        public void FinishConversationReplay()
        {
            LinkTurnsToConversationRows();
            RefreshIndexSelection();
            if (!string.IsNullOrEmpty(_pendingScrollBottomConversationId) &&
                _pendingScrollBottomConversationId == _activeConversationId)
            {
                _pendingScrollBottomConversationId = null;
                _pendingFocusConversationId = null;
                _pendingFocusText = null;
                _pendingFocusTimestamp = 0;
                ScrollToBottom();
                return;
            }
            TryFocusPendingConversationRecord();
        }

        /// <summary>切换被 busy/断线拒绝时清掉待打开状态，避免下次误滚动。</summary>
        public void CancelPendingConversationOpen()
        {
            _pendingScrollBottomConversationId = null;
            _pendingFocusConversationId = null;
            _pendingFocusText = null;
            _pendingFocusTimestamp = 0;
        }

        /// <summary>重命名失败/取消时把输入框恢复成当前会话标题。</summary>
        public void RevertConversationTitle()
        {
            RefreshConversationTitle();
        }

        public void ClearConversations()
        {
            FinishIndexTweens();
            _conversations.Clear();
            _activeConversationId = null;
            _pendingScrollBottomConversationId = null;
            _conversationViews.Clear();
            _conversationStatus.Clear();
            ClearConversationGroups();
            RefreshIndexEmpty();
            RefreshConversationTitle();
            _timeMarkerDiagnosticLogged = false;
        }

        private void RebuildConversationIndex()
        {
            if (_chatIndexContent == null || _conversationTemplate == null) return;
            FinishIndexTweens();
            var previousY = new Dictionary<string, float>();
            foreach (var existing in _conversationViews)
            {
                if (existing?.Go == null || string.IsNullOrEmpty(existing.Id)) continue;
                previousY[existing.Id] = ((RectTransform)existing.Go.transform).anchoredPosition.y;
            }
            CaptureConversationState();
            ClearConversationGroups();
            if (_conversations.Count == 0)
            {
                RefreshIndexEmpty();
                return;
            }
            foreach (var conversation in _conversations)
            {
                if (conversation != null) CreateConversationGroup(conversation);
            }
            RefreshIndexEmpty();
            LinkTurnsToConversationRows();
            RefreshIndexSelection();
            RefreshConversationTitle();
            AnimateIndexReorder(previousY);
        }

        private void CaptureConversationState()
        {
            foreach (var view in _conversationViews)
            {
                if (view == null || string.IsNullOrEmpty(view.Id)) continue;
                _conversationExpanded[view.Id] = view.Expanded;
                _conversationVisible[view.Id] = view.Visible;
            }
        }

        private void ClearConversationGroups()
        {
            foreach (var view in _conversationViews)
            {
                if (view == null || view.Go == null) continue;
                view.Go.SetActive(false);
                UnityEngine.Object.Destroy(view.Go);
            }
            _conversationViews.Clear();
            if (_chatIndexContent == null) return;
            for (int i = _chatIndexContent.childCount - 1; i >= 0; i--)
            {
                var child = _chatIndexContent.GetChild(i);
                if (child == _conversationTemplate || child.name == "turn-row-template") continue;
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            foreach (var entry in _turns)
            {
                entry.Row = null;
                entry.Link = null;
            }
        }

        private void CreateConversationGroup(ConversationInfo info)
        {
            var go = UnityEngine.Object.Instantiate(_conversationTemplate.gameObject, _chatIndexContent, false);
            go.name = "conversation-" + SafeNodeName(info.Id);
            go.SetActive(true);

            var header = go.transform.Find("header") as RectTransform;
            var records = go.transform.Find("records") as RectTransform;
            if (header == null || records == null)
            {
                Plugin.Log.LogWarning("[UI] conversation-group-template 缺少 header/records");
                UnityEngine.Object.Destroy(go);
                return;
            }

            // 新 prefab 是 header/status/{chevron,warning,loading,new}；
            // 旧 prefab 只有 header/chevron/icon，保留兼容到 UI bundle 重打完成。
            var status = header.Find("status") as RectTransform;
            if (status == null) status = header.Find("chevron") as RectTransform;
            var view = new ConversationView
            {
                Id = info.Id,
                Info = info,
                Go = go,
                Header = header,
                Records = records,
                TitleMask = header.Find("title-mask") as RectTransform,
                Title = header.Find("title-mask/title")?.GetComponent<Text>(),
                StatusChevron = status?.Find("chevron")?.GetComponent<Image>()
                    ?? status?.Find("icon")?.GetComponent<Image>(),
                StatusWarning = status?.Find("warning")?.GetComponent<Image>(),
                StatusLoading = status?.Find("loading")?.GetComponent<Image>(),
                StatusNew = status?.Find("new")?.GetComponent<Image>(),
            };
            // 展开状态只由标题左侧的状态图标改变；首次出现时只有当前会话默认展开，
            // 并把这次默认写进状态，之后切走再切回不会被重新展开。
            if (!_conversationExpanded.TryGetValue(info.Id, out var expanded))
            {
                expanded = info.Id == _activeConversationId;
                _conversationExpanded[info.Id] = expanded;
            }
            view.Expanded = expanded;
            view.Visible = _conversationVisible.TryGetValue(info.Id, out var visible)
                ? visible
                : 5;
            view.TitleColor = view.Title != null ? view.Title.color : Color.white;
            AssignIconMapping(view.Title, TryGetPreferredIconMapping(), restyle: false);
            _conversationViews.Add(view);

            var headerHover = header.gameObject.GetComponent<ConversationHeaderHover>();
            if (headerHover == null) headerHover = header.gameObject.AddComponent<ConversationHeaderHover>();
            headerHover.Background = header.GetComponent<Image>();
            headerHover.HoverColor = new Color(_accent.r, _accent.g, _accent.b, 0.10f);
            view.HeaderHover = headerHover;

            var headerButton = header.GetComponent<Button>();
            if (headerButton != null)
            {
                headerButton.onClick.RemoveAllListeners();
                headerButton.onClick.AddListener(() => OnConversationHeaderClicked(info.Id));
            }
            // 状态图标无论显示 loading/warning/new/chevron，点击都只展开/收起记录；
            // 切换会话仍然点标题。新 prefab 的 status 自带 Button；旧 prefab 的 chevron 也有。
            var statusButton = status?.GetComponent<Button>();
            if (statusButton == null && status != null)
            {
                var hit = status.GetComponent<Image>();
                if (hit == null)
                {
                    hit = status.gameObject.AddComponent<Image>();
                    hit.color = Color.clear;
                }
                hit.raycastTarget = true;
                statusButton = status.gameObject.AddComponent<Button>();
                statusButton.transition = Selectable.Transition.None;
                statusButton.targetGraphic = hit;
            }
            if (statusButton != null)
            {
                statusButton.onClick.RemoveAllListeners();
                statusButton.onClick.AddListener(() =>
                {
                    // 状态按钮嵌在可切换会话的 header 里；记录本帧，防止事件冒泡把“展开”变成“切换”。
                    _suppressHeaderClickFrame = Time.frameCount;
                    ToggleConversation(info.Id);
                });
            }

            var delete = header.Find("delete");
            var deleteButton = delete != null ? delete.GetComponent<Button>() : null;
            if (deleteButton != null)
            {
                deleteButton.onClick.RemoveAllListeners();
                deleteButton.onClick.AddListener(() => OnDeleteConversationClicked(info.Id));
            }
            if (delete != null)
            {
                var deleteHover = delete.GetComponent<ConversationIconHover>();
                if (deleteHover == null) deleteHover = delete.gameObject.AddComponent<ConversationIconHover>();
                deleteHover.Icon = delete.Find("x")?.GetComponent<Image>();
                deleteHover.HoverColor = ConversationDeleteHoverColor;
                view.DeleteHover = deleteHover;
            }

            var pin = header.Find("pin");
            var pinButton = pin != null ? pin.GetComponent<Button>() : null;
            if (pinButton != null)
            {
                pinButton.onClick.RemoveAllListeners();
                pinButton.onClick.AddListener(() =>
                {
                    // 置顶按钮嵌在可切换会话的 header 里；记录本帧，防止冒泡把“置顶”变成“切换”。
                    _suppressHeaderClickFrame = Time.frameCount;
                    OnConversationPinClicked(info.Id);
                });
            }
            if (pin != null)
            {
                var pinHover = pin.GetComponent<ConversationIconHover>();
                if (pinHover == null) pinHover = pin.gameObject.AddComponent<ConversationIconHover>();
                pinHover.Icon = pin.Find("pin")?.GetComponent<Image>();
                pinHover.HoverColor = ConversationPinHoverColor;
                view.PinHover = pinHover;
            }

            var time = header.Find("time");
            view.TimeText = time != null ? time.Find("num")?.GetComponent<Text>() : null;
            if (view.TimeText != null)
            {
                // 和其它运行期文本一样走游戏文字材质：prefab 里若引用内置默认材质，
                // 进 bundle 后那份引用会丢，字会暗到看不见（历史坑）。
                GameTextStyle.Apply(view.TimeText);
                ApplyBodyFont(view.TimeText);
            }
            // prefab 的 time 自带一个空 Button；它挡在标题行前面，不接线的话点这块会没反应。
            var timeButton = time != null ? time.GetComponent<Button>() : null;
            if (timeButton != null)
            {
                timeButton.onClick.RemoveAllListeners();
                timeButton.onClick.AddListener(() => OnConversationHeaderClicked(info.Id));
            }

            headerHover.DeleteButton = delete != null ? delete.gameObject : null;
            headerHover.PinButton = pin != null ? pin.gameObject : null;
            headerHover.TimeLabel = time != null ? time.gameObject : null;

            BuildConversationRecords(view);
            UpdateConversationHeader(view);
            if (_hostCanvas != null) SetLayerRecursively(go.transform, _hostCanvas.gameObject.layer);
        }

        private void UpdateConversationHeader(ConversationView view)
        {
            if (view == null) return;
            var titleColor = view.Id == _activeConversationId ? _accent : view.TitleColor;
            if (view.Title != null)
            {
                view.Title.text = HeaderTitleLabel(view,
                    string.IsNullOrEmpty(view.Info.Title) ? Strings.Get("chat.conversation.new") : view.Info.Title);
                view.Title.color = titleColor;
            }
            ApplyConversationStatus(view);
            if (view.DeleteHover != null) view.DeleteHover.SetNormal(titleColor);
            // 时间标记和置顶按钮都与标题同色，只有悬停置顶按钮时变成亮蓝色。
            if (view.TimeText != null) view.TimeText.color = titleColor;
            if (view.PinHover != null) view.PinHover.SetNormal(titleColor);
            RefreshConversationTime(view);
            if (view.Records != null) view.Records.gameObject.SetActive(view.Expanded);
        }

        /// <summary>刷新一个会话右侧的时间标记，并让标题行按“置顶 / 有无时间”决定显隐。</summary>
        private void RefreshConversationTime(ConversationView view)
        {
            if (view == null) return;
            var label = ConversationTimeLabel(view.Info);
            if (view.TimeText != null) view.TimeText.text = label;
            if (view.HeaderHover != null)
            {
                view.HeaderHover.HasTime = !string.IsNullOrEmpty(label);
                view.HeaderHover.Pinned = view.Info != null && view.Info.Pinned;
                view.HeaderHover.Refresh();
            }
        }

        /// <summary>
        /// 每次打开聊天窗、每轮回答完成时刷新所有会话的时间标记。
        /// 只改文本和显隐，不重建索引，避免打断悬停和滚动位置。
        /// </summary>
        public void RefreshConversationTimes()
        {
            var hasText = false;
            foreach (var view in _conversationViews)
            {
                RefreshConversationTime(view);
                if (view?.TimeText != null && !string.IsNullOrEmpty(view.TimeText.text)) hasText = true;
            }
            LogTimeMarkerSample(hasText);
        }

        /// <summary>
        /// 每个存档绑定后的第一次刷新打一行时间标记实况：
        /// 有没有文本、节点在不在、用的什么字体/材质、显不显示。
        /// 只打一次，排查“游戏里看不到时间”时不用再猜 prefab 到底生效没有。
        /// </summary>
        private void LogTimeMarkerSample(bool hasText)
        {
            if (_timeMarkerDiagnosticLogged || _conversationViews.Count == 0) return;
            _timeMarkerDiagnosticLogged = true;
            var view = _conversationViews[0];
            var info = view?.Info;
            var text = view?.TimeText;
            var node = text != null ? text.transform.parent.gameObject : null;
            Plugin.Log.LogInfo(
                $"[索引] 时间标记：rows={_conversationViews.Count} hasText={hasText} " +
                $"records={(info != null ? info.Records.Count : -1)} " +
                $"updatedAt={(info != null ? info.UpdatedAt : -1)} " +
                $"lastReplyTick={(info != null ? info.LastReplyTick : -1)} " +
                $"text={((text != null && text.text != null) ? text.text : "<null>")} " +
                $"node={(node != null && node.activeSelf)} " +
                $"size={((text != null) ? text.rectTransform.rect.size.ToString() : "-")} " +
                $"font={(text != null && text.font != null ? text.font.name : "<null>")} " +
                $"mat={(text != null && text.material != null ? text.material.name : "<null>")} " +
                $"color={(text != null ? text.color.ToString() : "-")}");
        }

        /// <summary>
        /// 某一轮回答完成：记下当时的 gameTick（时间标记用）与当前时间戳（排序用），
        /// 刷新所有时间标记，并按「置顶 → 最后回复」重排。
        /// </summary>
        public void MarkConversationReplied(string conversationId, long gameTick)
        {
            var conversation = FindConversation(conversationId);
            if (conversation != null)
            {
                conversation.MarkReplied(gameTick);
                conversation.MarkUpdated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (SortConversations())
                {
                    RebuildConversationIndex();
                    return;
                }
            }
            RefreshConversationTimes();
        }

        /// <summary>
        /// 左侧时间标记：按游戏时间显示距最后一条回答多久（刚刚 / x分钟 / x.x小时 / xx小时）。
        /// 用绝对 gameTick 而不是游戏时钟——时钟也会在 1000 小时处归零；
        /// 也没有用墙上时间，暂停游戏时游戏时间应当冻结。
        /// 老会话没有 gameTick 时退回会话时间戳（真实时间），差一点精度也不留空白。
        /// </summary>
        private static string ConversationTimeLabel(ConversationInfo info)
        {
            if (info == null) return string.Empty;
            long elapsedSeconds;
            if (info.LastReplyTick > 0 && GameMain.data != null)
            {
                elapsedSeconds = (GameMain.gameTick - info.LastReplyTick) / GameTickPerSecond;
            }
            else
            {
                if (info.UpdatedAt <= 0) return string.Empty;
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                elapsedSeconds = (nowMs - info.UpdatedAt) / 1000L;
            }
            if (elapsedSeconds < 0) elapsedSeconds = 0;
            if (elapsedSeconds < 60L) return Strings.Get("chat.conversation.time.just_now");
            if (elapsedSeconds < 3600L)
                return Strings.Get("chat.conversation.time.minutes", elapsedSeconds / 60L);
            // 保留一位小数并向下取整，避免 9.96 小时被格式化成“10.0小时”，与“10 小时以上用整数”打架。
            long tenths = elapsedSeconds * 10L / 3600L;
            if (tenths < 100L)
                return Strings.GetInvariant("chat.conversation.time.hours_tenths", tenths / 10L, tenths % 10L);
            return Strings.Get("chat.conversation.time.hours", elapsedSeconds / 3600L);
        }

        /// <summary>外部状态帧：running=loading，attention=warning，completed=new，idle=chevron。</summary>
        public void SetConversationActivity(string conversationId, string state, string reason = null)
        {
            if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(state)) return;
            switch (state)
            {
                case "idle":
                    _conversationStatus.Remove(conversationId);
                    break;
                case "running":
                    _conversationStatus[conversationId] = ConversationStatus.Running;
                    break;
                case "attention":
                    _conversationStatus[conversationId] = ConversationStatus.Attention;
                    break;
                case "completed":
                    // 焦点会话的完成由 sidecar 发 idle；这里再兜一层，避免当前会话被标成“未读”。
                    if (conversationId == _activeConversationId)
                        _conversationStatus.Remove(conversationId);
                    else
                        _conversationStatus[conversationId] = ConversationStatus.Completed;
                    break;
                default:
                    // 未知状态留空，方便以后增加图标时旧 mod 不误显示。
                    return;
            }
            var view = FindConversationView(conversationId);
            if (view != null) UpdateConversationHeader(view);
        }

        private ConversationStatus StatusOf(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId))
                return ConversationStatus.Normal;
            if (conversationId == _activeConversationId &&
                _conversationStatus.TryGetValue(conversationId, out var activeStatus) &&
                activeStatus == ConversationStatus.Completed)
            {
                _conversationStatus.Remove(conversationId);
                return ConversationStatus.Normal;
            }
            return _conversationStatus.TryGetValue(conversationId, out var status)
                ? status
                : ConversationStatus.Normal;
        }

        private void ApplyConversationStatus(ConversationView view)
        {
            if (view == null) return;
            var status = StatusOf(view.Id);
            SetStatusIconActive(view.StatusChevron, status == ConversationStatus.Normal);
            SetStatusIconActive(view.StatusWarning, status == ConversationStatus.Attention);
            SetStatusIconActive(view.StatusLoading, status == ConversationStatus.Running);
            SetStatusIconActive(view.StatusNew, status == ConversationStatus.Completed);

            if (view.StatusChevron != null) view.StatusChevron.color = status == ConversationStatus.Normal &&
                view.Id == _activeConversationId ? _accent : Color.white;

            if (status == ConversationStatus.Normal && view.StatusChevron != null)
            {
                view.StatusChevronSpin ??= view.StatusChevron.gameObject.AddComponent<ChevronSpin>();
                view.StatusChevronSpin.SetAngle(view.Expanded ? -90f : 0f, true);
            }
            if (view.StatusLoading != null)
            {
                view.StatusLoadingSpin ??= view.StatusLoading.gameObject.AddComponent<LoadingSpin>();
                view.StatusLoadingSpin.SetSpinning(status == ConversationStatus.Running);
            }
        }

        private static void SetStatusIconActive(Image icon, bool active)
        {
            if (icon != null && icon.gameObject.activeSelf != active)
                icon.gameObject.SetActive(active);
        }

        private void BuildConversationRecords(ConversationView view)
        {
            if (view == null || view.Records == null) return;
            for (int i = view.Records.childCount - 1; i >= 0; i--)
            {
                var child = view.Records.GetChild(i);
                if (child == _recordTemplate || child == _expandMoreTemplate) continue;
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            view.Rows.Clear();
            view.RowRecords.Clear();
            view.ExpandMore = null;
            view.ExpandMoreText = null;

            int count = view.Info != null ? view.Info.Records.Count : 0;
            int visible = Mathf.Clamp(view.Visible <= 0 ? 5 : view.Visible, 5, Mathf.Max(5, count));
            if (count < 5) visible = count;
            view.Visible = visible;

            // 记录在 Info.Records 里按时间正序；列表展示倒序，最新的提问排第一。
            for (int i = 0; i < visible; i++)
            {
                var record = view.Info.Records[count - 1 - i];
                var row = UnityEngine.Object.Instantiate(_recordTemplate.gameObject, view.Records, false);
                row.name = "record-" + (i + 1);
                row.SetActive(true);
                var label = row.transform.Find("link/label")?.GetComponent<Text>();
                if (label != null)
                {
                    // 按标签自己的字号和实际宽度截断：拿 _fontSize 推算会比 prefab 里的
                    // 真实字号小，正文标签又是 Overflow，结果记录会画到左栏外面去。
                    float maxWidth = label.rectTransform.rect.width;
                    if (maxWidth <= 20f) maxWidth = 210f;
                    label.text = FitLabel(record.Text, label.fontSize, maxWidth - 4f);
                    // 常态是白色；右栏正对着的那条由 RefreshIndexSelection 单独染蓝。
                    label.color = Color.white;
                }
                var hover = row.GetComponent<ChatIndexRowHover>();
                if (hover == null) hover = row.AddComponent<ChatIndexRowHover>();
                hover.HoverBackground = row.GetComponent<Graphic>();
                var captured = record;
                hover.Clicked += () => OnConversationRecordClicked(view.Id, captured);
                view.Rows.Add(row);
                view.RowRecords.Add(record);
            }

            if (visible < count)
            {
                var more = UnityEngine.Object.Instantiate(_expandMoreTemplate.gameObject, view.Records, false);
                more.name = "expand-more";
                more.SetActive(true);
                var moreText = more.transform.Find("label")?.GetComponent<Text>();
                if (moreText != null)
                    moreText.text = Strings.Get("chat.index.expand_more", count - visible);
                var moreButton = more.GetComponent<Button>();
                if (moreButton != null)
                {
                    moreButton.onClick.RemoveAllListeners();
                    moreButton.onClick.AddListener(() => ExpandConversation(view.Id));
                }
                view.ExpandMore = more;
                view.ExpandMoreText = moreText;
            }

            view.Records.gameObject.SetActive(view.Expanded);
        }

        /// <summary>点会话标题：只切换/滚到该会话，不改变展开状态；展开/收起由标题左侧的状态图标负责。</summary>
        private void OnConversationHeaderClicked(string conversationId)
        {
            if (_suppressHeaderClickFrame == Time.frameCount) return;
            var view = FindConversationView(conversationId);
            if (view == null) return;

            if (conversationId != _activeConversationId)
            {
                _pendingScrollBottomConversationId = conversationId;
                _pendingFocusConversationId = null;
                _pendingFocusText = null;
                _pendingFocusTimestamp = 0;
                try
                {
                    ConversationSwitchRequested?.Invoke(conversationId);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[索引] 请求打开会话失败：{ex.Message}");
                }
                return;
            }

            // 已经是当前会话：和左侧状态图标一样展开/收起。
            ToggleConversation(conversationId);
        }

        // ---------------------------------------------------------------- 左栏过渡动画

        private void StartIndexTween(string key, float duration, Action<float> step, Action done = null)
        {
            FinishIndexTween(key);
            _indexTweens.Add(new IndexTween
            {
                Key = key,
                Duration = Mathf.Max(0.01f, duration),
                Step = step,
                Done = done,
            });
        }

        /// <summary>把某个 key 的动画直接推到终态（含回调），保证布局/受控属性已经复位。</summary>
        private void FinishIndexTween(string key)
        {
            for (int i = _indexTweens.Count - 1; i >= 0; i--)
            {
                if (_indexTweens[i].Key != key) continue;
                var tween = _indexTweens[i];
                _indexTweens.RemoveAt(i);
                tween.Step?.Invoke(1f);
                tween.Done?.Invoke();
            }
        }

        /// <summary>关窗、清空列表、重建索引前调用：所有动画落到终态，避免残影和失控的布局开关。</summary>
        private void FinishIndexTweens()
        {
            if (_indexTweens.Count == 0) return;
            var pending = _indexTweens.ToArray();
            _indexTweens.Clear();
            foreach (var tween in pending)
            {
                tween.Step?.Invoke(1f);
                tween.Done?.Invoke();
            }
        }

        private void UpdateIndexTweens(float deltaTime)
        {
            for (int i = _indexTweens.Count - 1; i >= 0; i--)
            {
                if (i >= _indexTweens.Count) continue;
                var tween = _indexTweens[i];
                tween.Elapsed += deltaTime;
                var progress = Mathf.Clamp01(tween.Elapsed / tween.Duration);
                // ease-out cubic：起步快、收尾稳，和游戏自己的面板动画观感一致。
                tween.Step?.Invoke(1f - Mathf.Pow(1f - progress, 3f));
                if (progress < 1f) continue;
                _indexTweens.RemoveAt(i);
                tween.Done?.Invoke();
            }
        }

        /// <summary>高度在动画中由 LayoutElement 接管，重建这一条列表让下面的会话跟着移动。</summary>
        private void MarkIndexLayoutDirty()
        {
            if (_chatIndexContent != null) LayoutRebuilder.MarkLayoutForRebuild(_chatIndexContent);
        }

        /// <summary>
        /// 展开：records 的高度从 fromHeight 长到内容高度，超出当前高度的部分由 RectMask2D 挡住。
        /// 动画期间要同时关掉 records 自己的 VerticalLayoutGroup——它也算 ILayoutElement，
        /// 会把内容高度报给父级，压过 LayoutElement 设的值。
        /// </summary>
        private void AnimateRecordsReveal(ConversationView view, float fromHeight = 0f)
        {
            if (view?.Records == null) return;
            var records = view.Records;
            if (!records.gameObject.activeInHierarchy) return;
            var layout = records.GetComponent<VerticalLayoutGroup>();
            var fitter = records.GetComponent<ContentSizeFitter>();
            var element = records.GetComponent<LayoutElement>();
            if (layout == null || fitter == null || element == null) return;

            LayoutRebuilder.ForceRebuildLayoutImmediate(records);
            var target = records.rect.height;
            if (target <= 0.5f) return;

            var start = Mathf.Clamp(fromHeight, 0f, target);
            FinishIndexTween(RecordsTweenKey(view.Id));
            layout.enabled = false;
            fitter.enabled = false;
            element.preferredHeight = start;
            MarkIndexLayoutDirty();
            StartIndexTween(RecordsTweenKey(view.Id), IndexExpandDuration,
                progress =>
                {
                    if (element == null) return;
                    element.preferredHeight = Mathf.LerpUnclamped(start, target, progress);
                    MarkIndexLayoutDirty();
                },
                () =>
                {
                    if (element == null || fitter == null || layout == null) return;
                    element.preferredHeight = -1f;
                    layout.enabled = true;
                    fitter.enabled = true;
                    MarkIndexLayoutDirty();
                });
        }

        /// <summary>收起：高度收到 0 之后再真正销毁/隐藏记录行（after 里重建）。</summary>
        private void AnimateRecordsHide(ConversationView view, Action after)
        {
            if (view?.Records == null)
            {
                after?.Invoke();
                return;
            }
            var records = view.Records;
            var fitter = records.GetComponent<ContentSizeFitter>();
            var element = records.GetComponent<LayoutElement>();
            if (fitter == null || element == null || !records.gameObject.activeInHierarchy)
            {
                after?.Invoke();
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(records);
            var start = records.rect.height;
            var layout = records.GetComponent<VerticalLayoutGroup>();
            FinishIndexTween(RecordsTweenKey(view.Id));
            if (layout != null) layout.enabled = false;
            fitter.enabled = false;
            StartIndexTween(RecordsTweenKey(view.Id), IndexExpandDuration,
                progress =>
                {
                    if (element == null) return;
                    element.preferredHeight = Mathf.LerpUnclamped(start, 0f, progress);
                    MarkIndexLayoutDirty();
                },
                () =>
                {
                    if (element == null || fitter == null) return;
                    element.preferredHeight = -1f;
                    fitter.enabled = true;
                    if (layout != null) layout.enabled = true;
                    after?.Invoke();
                    MarkIndexLayoutDirty();
                });
        }

        private static string RecordsTweenKey(string conversationId) => "records:" + conversationId;

        /// <summary>
        /// 会话换位（置顶/取消置顶/新回复后重排）时让标题滑动到新位置。
        /// 位置本来是 VerticalLayoutGroup 管着的：动画期间先关掉它手动驱动，结束后再交还。
        /// </summary>
        private void AnimateIndexReorder(Dictionary<string, float> previousY)
        {
            if (!IsOpen || _chatIndexContent == null || previousY == null || previousY.Count == 0) return;
            if (_conversationViews.Count == 0) return;
            FinishIndexTweens();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_chatIndexContent);

            var rows = new List<(RectTransform Rect, float From, float To)>();
            foreach (var view in _conversationViews)
            {
                if (view?.Go == null || string.IsNullOrEmpty(view.Id)) continue;
                if (!previousY.TryGetValue(view.Id, out var from)) continue;
                var rect = (RectTransform)view.Go.transform;
                var to = rect.anchoredPosition.y;
                if (Mathf.Abs(from - to) < 0.5f) continue;
                rows.Add((rect, from, to));
            }
            if (rows.Count == 0) return;

            // 先冻结滚动内容的当前高度：关掉 layout 后 ContentSizeFitter 会把高度算成 0，
            // 滚动区会瞬间塌陷。高度冻结、位置由这里手动驱动，结束后再交还。
            var layout = _chatIndexContent.GetComponent<VerticalLayoutGroup>();
            var fitter = _chatIndexContent.GetComponent<ContentSizeFitter>();
            var frozenHeight = _chatIndexContent.rect.height;
            var frozenWidth = _chatIndexContent.sizeDelta.x;
            if (fitter != null) fitter.enabled = false;
            if (layout != null) layout.enabled = false;
            _chatIndexContent.sizeDelta = new Vector2(frozenWidth, frozenHeight);
            foreach (var row in rows)
                row.Rect.anchoredPosition = new Vector2(row.Rect.anchoredPosition.x, row.From);

            StartIndexTween("reorder", IndexReorderDuration,
                progress =>
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        if (row.Rect == null) continue;
                        row.Rect.anchoredPosition = new Vector2(
                            row.Rect.anchoredPosition.x,
                            Mathf.LerpUnclamped(row.From, row.To, progress));
                    }
                },
                () =>
                {
                    if (layout != null) layout.enabled = true;
                    if (fitter != null) fitter.enabled = true;
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_chatIndexContent);
                });
        }

        private void ToggleConversation(string conversationId)
        {
            var view = FindConversationView(conversationId);
            if (view == null) return;
            view.Expanded = !view.Expanded;
            _conversationExpanded[view.Id] = view.Expanded;
            if (view.Expanded && view.Visible <= 0) view.Visible = 5;
            UpdateConversationHeader(view);
            LinkTurnsToConversationRows();
            RefreshIndexSelection();
            if (view.Expanded)
            {
                BuildConversationRecords(view);
                AnimateRecordsReveal(view);
                return;
            }
            // 收起先播高度动画，结束后再重建（重建会把记录行销毁并隐藏 records）。
            AnimateRecordsHide(view, () => BuildConversationRecords(view));
        }

        private void ExpandConversation(string conversationId)
        {
            var view = FindConversationView(conversationId);
            if (view == null) return;
            var before = view.Records as RectTransform;
            float beforeHeight = before != null && before.gameObject.activeSelf ? before.rect.height : 0f;
            int count = view.Info != null ? view.Info.Records.Count : 0;
            view.Visible = Mathf.Min(view.Visible + 5, count);
            _conversationVisible[view.Id] = view.Visible;
            BuildConversationRecords(view);
            UpdateConversationHeader(view);
            LinkTurnsToConversationRows();
            RefreshIndexSelection();
            AnimateRecordsReveal(view, beforeHeight);
        }

        private ConversationView FindConversationView(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var view in _conversationViews)
                if (view != null && view.Id == id) return view;
            return null;
        }

        private void OnNewConversationClicked()
        {
            try
            {
                ConversationCreateRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[索引] 请求新建会话失败：{ex.Message}");
            }
        }

        private void OnDeleteConversationClicked(string conversationId)
        {
            try
            {
                ConversationDeleteRequested?.Invoke(conversationId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[索引] 请求删除会话失败：{ex.Message}");
            }
        }

        /// <summary>玩家点置顶按钮：先本地重排让列表立刻响应，再发请求；发送失败由 Plugin 回滚。</summary>
        private void OnConversationPinClicked(string conversationId)
        {
            var conversation = FindConversation(conversationId);
            if (conversation == null) return;
            var pinned = !conversation.Pinned;
            SetConversationPinned(conversationId, pinned);
            try
            {
                ConversationPinRequested?.Invoke(conversationId, pinned);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[索引] 请求置顶会话失败：{ex.Message}");
            }
        }

        private void OnConversationRecordClicked(string conversationId, ConversationRecordInfo record)
        {
            if (record == null) return;
            if (conversationId != _activeConversationId)
            {
                _pendingScrollBottomConversationId = null;
                _pendingFocusConversationId = conversationId;
                _pendingFocusText = record.Text;
                _pendingFocusTimestamp = record.Timestamp;
                try
                {
                    ConversationSwitchRequested?.Invoke(conversationId);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[索引] 请求切换会话失败：{ex.Message}");
                }
                return;
            }
            FocusConversationRecord(record);
        }

        private void FocusConversationRecord(ConversationRecordInfo record)
        {
            if (record == null || string.IsNullOrEmpty(record.Text)) return;
            for (int i = _turns.Count - 1; i >= 0; i--)
            {
                var turn = _turns[i];
                if (turn.IsCompaction || turn.Text != record.Text) continue;
                if (record.Timestamp > 0 && turn.Timestamp > 0 && turn.Timestamp != record.Timestamp) continue;
                FocusTurn(turn.Id);
                return;
            }
        }

        private void TryFocusPendingConversationRecord()
        {
            if (string.IsNullOrEmpty(_pendingFocusConversationId) ||
                _pendingFocusConversationId != _activeConversationId)
                return;
            var record = ConversationRecordInfo.Create(_pendingFocusText, _pendingFocusTimestamp);
            _pendingFocusConversationId = null;
            _pendingFocusText = null;
            _pendingFocusTimestamp = 0;
            FocusConversationRecord(record);
        }

        private void LinkTurnsToConversationRows()
        {
            foreach (var entry in _turns)
            {
                entry.Row = null;
                entry.Link = null;
            }
            var view = FindConversationView(_activeConversationId);
            if (view == null || view.Info == null) return;

            var used = new bool[view.Rows.Count];
            foreach (var entry in _turns)
            {
                if (entry.IsCompaction) continue;
                for (int i = 0; i < view.Rows.Count; i++)
                {
                    if (used[i]) continue;
                    if (i >= view.RowRecords.Count) break;
                    var record = view.RowRecords[i];
                    if (record == null || record.Text != entry.Text) continue;
                    if (entry.Timestamp > 0 && record.Timestamp > 0 && entry.Timestamp != record.Timestamp)
                        continue;
                    used[i] = true;
                    var row = view.Rows[i];
                    entry.Row = row;
                    entry.Link = row.transform.Find("link/label")?.GetComponent<Text>();
                    break;
                }
            }
        }

        private static string SafeNodeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "default";
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') chars[i] = '_';
            }
            return new string(chars);
        }

        /// <summary>
        /// 标题截断：图标标签 `\tag;` 作为整体参与宽度估算和截断。
        /// 把标签切成半截时 Text 会把残余字符当普通文字画出来。
        /// </summary>
        internal static string FitLabel(string text, int fontSize, float maxWidth)
        {
            var s = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) return string.Empty;
            maxWidth = Mathf.Max(20f, maxWidth);
            if (EstimateTextWidth(s, fontSize) <= maxWidth) return s;

            var ellipsis = fontSize * 0.55f;
            var width = 0f;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; )
            {
                var end = i + 1;
                var tokenWidth = s[i] > 0x2E80 ? fontSize : fontSize * 0.55f;
                if (TryReadIconTag(s, i, out var tagEnd))
                {
                    end = tagEnd;
                    tokenWidth = fontSize;
                }
                if (width + tokenWidth + ellipsis > maxWidth) break;
                sb.Append(s, i, end - i);
                width += tokenWidth;
                i = end;
            }
            return sb.ToString().TrimEnd() + "…";
        }

        /// <summary>读一个图标标签 `\tag;`（tag 只含字母/数字/-/_）；不是标签时返回 false。</summary>
        private static bool TryReadIconTag(string text, int start, out int end)
        {
            end = start;
            if (text == null || start < 0 || start >= text.Length || text[start] != '\\') return false;
            var limit = Mathf.Min(text.Length, start + 24);
            for (int i = start + 1; i < limit; i++)
            {
                var c = text[i];
                if (c == ';')
                {
                    end = i + 1;
                    return i > start + 1;
                }
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                         || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return false;
        }

        /// <summary>
        /// 左侧会话标题：按 title-mask 的实际宽度截断，避免压到右侧的时间标记/置顶/删除按钮。
        /// 首次创建时布局还没跑，rect 宽度不可信，退回 prefab 的宽度。
        /// </summary>
        private static string HeaderTitleLabel(ConversationView view, string text)
        {
            var width = view?.TitleMask != null ? view.TitleMask.rect.width : 0f;
            if (width <= 20f) width = ConversationTitleFallbackWidth;
            return FitLabel(text, 13, width - 4f);
        }

        private void EnsureConversationTitle()
        {
            var input = _panel != null
                ? _panel.Find("content-column/title/name-input") as RectTransform
                : null;
            if (input == null) return;
            _conversationTitleInput = input.GetComponent<InputField>();
            _conversationTitleOver = input.Find("name-over")?.GetComponent<Text>();
            if (_conversationTitleInput != null)
            {
                // 玩家按 Enter 或点走时提交；先移除旧监听，避免 Ensure 多次挂重复回调。
                _conversationTitleInput.onEndEdit.RemoveListener(OnConversationTitleEndEdit);
                _conversationTitleInput.onEndEdit.AddListener(OnConversationTitleEndEdit);
            }
            if (_conversationTitleInput != null && _conversationTitleOver != null)
            {
                var display = input.GetComponent<ConversationTitleDisplay>();
                if (display == null) display = input.gameObject.AddComponent<ConversationTitleDisplay>();
                display.Input = _conversationTitleInput;
                display.Over = _conversationTitleOver;
                _conversationTitleDisplay = display;
            }

            // 标题框旁边的图标按钮复用游戏自己的 UIInputField：它点开 UISignalTagPicker，
            // 返回时把 `\tag;` 插进输入框。位置/层级要我们自己给，否则弹窗在窗口后面。
            var titleUi = input.GetComponent<UIInputField>();
            if (titleUi != null)
                titleUi.calcIconPickerPopupPos = PickerPositionBelowTitleInput;
            AssignTitleIconMapping(TryGetPreferredIconMapping());
            BindTitleIconButton();
        }

        /// <summary>
        /// 接管标题框的图标按钮。游戏的 UIInputField 在返回值时会检查输入框是否仍聚焦，
        /// 而选择器一被点击焦点就丢了，于是什么都不插；这里自己弹选择器并恢复光标插入。
        /// </summary>
        private void BindTitleIconButton()
        {
            var ui = _conversationTitleInput != null ? _conversationTitleInput.GetComponent<UIInputField>() : null;
            if (ui == null || ui.iconSelectBtn == null || UiButtonOnClickField == null) return;
            try
            {
                UiButtonOnClickField.SetValue(ui.iconSelectBtn, null);
                ui.iconSelectBtn.onClick += OnTitleIconClicked;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("接管标题图标按钮失败：" + ex.Message);
            }
        }

        private void OnTitleIconClicked(int data)
        {
            if (_conversationTitleInput == null) return;
            try
            {
                if (UISignalTagPicker.isOpened)
                {
                    UISignalTagPicker.Close();
                    return;
                }

                _titleIconCaret = _conversationTitleInput.caretPosition;
                _titleIconAnchor = _conversationTitleInput.selectionAnchorPosition;

                var pickerTrans = UISignalTagPicker._PickerTrans;
                BoostPickerAboveWindow(pickerTrans, "标题图标");
                UISignalTagPicker.Popup(PickerPositionBelowTitleInput(), OnTitleIconPicked);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("打开标题图标选择器失败：" + ex.Message);
            }
        }

        private void OnTitleIconPicked(int signalId)
        {
            if (signalId == 0 || _conversationTitleInput == null) return;
            try
            {
                var tag = LDB.signals.IconTag(signalId);
                if (string.IsNullOrEmpty(tag)) return;

                _conversationTitleInput.caretPosition = _titleIconCaret;
                _conversationTitleInput.selectionAnchorPosition = _titleIconAnchor;
                _conversationTitleInput.InsertGeneralString(tag, true);
                _conversationTitleInput.ForceLabelUpdate();
                _conversationTitleInput.ActivateInputField();
                Plugin.Log.LogInfo($"[标题] 插入图标 tag='{tag}' → '{_conversationTitleInput.text}'");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("插入标题图标失败：" + ex.Message);
            }
        }

        /// <summary>标题相关的 Text（输入框、overText、左栏每一行）共用游戏图标表。</summary>
        /// <summary>
        /// 聊天输入框也要挂全局图标表：prefab 里那份图标表是空的（见 TryGetPreferredIconMapping），
        /// 换上全局表之后 `\tag;` 才会被 Text 解析成图标，否则只是原样字符。
        /// </summary>
        private void AssignInputIconMapping(UnityEngine.UI.Youthcat.TextIconMapping mapping)
        {
            if (mapping == null || _input == null) return;
            var field = _input.textComponent;
            if (field == null || ReferenceEquals(field.iconMapping, mapping)) return;
            field.iconMapping = mapping;
            field.SetVerticesDirty();
        }

        private void AssignTitleIconMapping(UnityEngine.UI.Youthcat.TextIconMapping mapping)
        {
            if (mapping == null) return;
            if (_conversationTitleInput != null)
                AssignIconMapping(_conversationTitleInput.textComponent, mapping, restyle: false);
            AssignIconMapping(_conversationTitleOver, mapping, restyle: false);
            foreach (var view in _conversationViews)
            {
                if (view != null) AssignIconMapping(view.Title, mapping, restyle: false);
            }
        }

        /// <summary>
        /// 右栏标题：输入框里始终是完整标题；未编辑时由 UIInputField 负责把 overText
        /// 显示出来，ConversationTitleDisplay 再按宽度补省略号。不要手工隐藏 overText。
        /// </summary>
        private void RefreshConversationTitle()
        {
            var conversation = FindConversation(_activeConversationId);
            var title = conversation != null ? conversation.Title : string.Empty;
            if (_conversationTitleInput != null && _conversationTitleInput.text != title)
                _conversationTitleInput.SetTextWithoutNotify(title ?? string.Empty);
            _conversationTitleDisplay?.RefreshNow();
        }

        private void OnConversationTitleEndEdit(string value)
        {
            _titleEditEndFrame = Time.frameCount;
            var conversationId = _activeConversationId;
            var conversation = FindConversation(conversationId);
            if (conversation == null)
            {
                RefreshConversationTitle();
                return;
            }
            var title = NormalizeConversationTitleInput(value);
            if (title.Length == 0 || title == conversation.Title)
            {
                RefreshConversationTitle();
                return;
            }
            try
            {
                ConversationRenameRequested?.Invoke(conversationId, title);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[标题] 请求重命名失败：{ex.Message}");
                RefreshConversationTitle();
            }
        }

        private static string NormalizeConversationTitleInput(string value)
        {
            var raw = value ?? string.Empty;
            var builder = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                var ch = raw[i];
                builder.Append(char.IsControl(ch) ? ' ' : ch);
            }
            var text = System.Text.RegularExpressions.Regex
                .Replace(builder.ToString(), @"\s+", " ")
                .Trim();
            var chars = text.ToCharArray();
            return chars.Length <= 40 ? text : new string(chars, 0, 40).TrimEnd();
        }

        private void RemoveActiveConversationRecord(string text, long timestampMs)
        {
            var conversation = FindConversation(_activeConversationId);
            if (conversation == null) return;
            if (!conversation.RemoveRecord(text, timestampMs)) return;
            if (conversation.RecordCount == 0) conversation.SetTitle(Strings.Get("chat.conversation.new"));
            conversation.MarkUpdated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
            // 玩家明确要定位到某个回合：别让延迟贴底把位置拽回底部。
            _scrollBottomFrames = 0;
            _scroll.verticalNormalizedPosition = Mathf.Clamp01(1f - (itemTop - viewportHeight * 0.5f) / max);
        }

        /// <summary>
        /// 撤回最后一轮问答：把提问气泡、回答、索引行一起移除，再让 sidecar 删会话条目。
        /// 只接受 `_turns` 里的最后一轮（`RequestWithdraw` 已按可见性兜底）。
        /// </summary>
        private void WithdrawTurn(TurnEntry entry)
        {
            if (entry == null || entry.IsCompaction) return;

            RemoveTurnVisuals(entry.Id);
            _turns.Remove(entry);
            RemoveActiveConversationRecord(entry.Text, entry.Timestamp);
            if (_focusedTurnId == entry.Id) _focusedTurnId = 0;
            if (_currentTurnId == entry.Id)
                _currentTurnId = _lines.Count > 0 ? _lines[_lines.Count - 1].TurnId : 0;

            RebuildConversationIndex();
            // 撤回的是最后一轮，按钮要回到新的最后一条回答上。
            UpdateReplyActionAvailability();
            _dirty = true;
            Plugin.Log.LogInfo($"[撤回] 已从窗口撤回回合 {entry.Id}");

            // UI 删掉还不够：save.bound 会从会话文件重放，必须让 sidecar 也删。
            try
            {
                WithdrawRequested?.Invoke(entry.Text, entry.Timestamp);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[撤回] 通知 sidecar 删除回合失败：{ex.Message}");
            }
        }

        /// <summary>销毁某一轮在输出区里的所有行/项（索引行由调用方处理）。</summary>
        private void RemoveTurnVisuals(int turnId)
        {
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
        }

        /// <summary>
        /// sidecar 成功撤销压缩后调用：移除压缩记录。
        /// 撤回顺序是从后往前：压缩记录撤回后，它后面/前面的回答又成为最后一条。
        /// </summary>
        public void RemoveCompaction(string compactionId)
        {
            int index = -1;
            for (int i = 0; i < _turns.Count; i++)
            {
                var candidate = _turns[i];
                if (!candidate.IsCompaction) continue;
                if (string.IsNullOrEmpty(compactionId) || candidate.CompactionId == compactionId)
                    index = i;
            }
            if (index < 0) return;

            var entry = _turns[index];
            RemoveTurnVisuals(entry.Id);
            if (entry.Row != null) UnityEngine.Object.Destroy(entry.Row);
            _turns.RemoveAt(index);
            if (_focusedTurnId == entry.Id) _focusedTurnId = 0;
            if (_currentTurnId == entry.Id)
                _currentTurnId = _lines.Count > 0 ? _lines[_lines.Count - 1].TurnId : 0;

            RefreshIndexSelection();
            RefreshIndexEmpty();
            UpdateReplyActionAvailability();
            _dirty = true;
            Plugin.Log.LogInfo($"[索引] 已撤销压缩记录 {entry.CompactionId ?? "(latest)"}");
        }

        /// <summary>
        /// 点回答 footer 的 return：只允许重新生成**最后一条回答**（会话是线性的）。
        /// 保留用户气泡、索引行和回合号，只清掉这一轮的助手内容；
        /// 真正「删旧回合 + 重发提问」由 Plugin 完成（A.7 + prompt）。
        /// </summary>
        private void RequestRegenerate(ReplyView view)
        {
            if (view == null) return;
            if (_streamItem >= 0 || _awaitingResponse || _thinking != null) return;

            // 只有最后一条回答有按钮（UpdateReplyActionAvailability），这里再兜一次。
            if (_turns.Count == 0 || view.TurnId != _turns[_turns.Count - 1].Id) return;

            TurnEntry entry = null;
            for (int i = 0; i < _turns.Count; i++)
            {
                if (_turns[i].Id == view.TurnId) { entry = _turns[i]; break; }
            }
            if (entry == null || entry.IsCompaction || string.IsNullOrEmpty(entry.Text)) return;

            RemoveAssistantSide(view.TurnId);

            // 用户气泡还在，重新回答前先进入「等待响应」，避免撤回/再次点按钮。
            _awaitingResponse = true;
            _dirty = true;
            Plugin.Log.LogInfo($"[重新生成] 重新推理回合 {view.TurnId}：{ShortText(entry.Text)}");

            try
            {
                RegenerateRequested?.Invoke(entry.Text, entry.Timestamp);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[重新生成] 通知 sidecar 重新推理失败：{ex.Message}");
            }
            finally
            {
                // 会话里这条 user 消息会被重新写成新的时间戳；撤回改走文本回退，
                // 否则下次撤回这一轮会拿旧时间戳去匹配、删不掉。
                entry.Timestamp = 0;
            }
        }

        /// <summary>
        /// 点回答 footer 的 withdraw：只允许撤回**最后一轮**（会话是线性的）。
        /// 普通回合：提问 + 回答整轮从窗口移除，并让 sidecar 删会话条目；
        /// 压缩记录：撤回它 = 撤销压缩，等 sidecar 成功后再移除记录。
        /// </summary>
        private void RequestWithdraw(ReplyView view)
        {
            if (view == null) return;
            if (_streamItem >= 0 || _awaitingResponse || _thinking != null) return;
            if (_turns.Count == 0) return;

            var last = _turns[_turns.Count - 1];
            if (view.TurnId != last.Id) return;

            if (last.IsCompaction)
            {
                try
                {
                    CompactionUndoRequested?.Invoke(last.CompactionId ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[撤回] 通知 sidecar 撤销压缩失败：{ex.Message}");
                }
                return;
            }

            WithdrawTurn(last);
        }

        /// <summary>只清掉某一轮的助手侧（回复、思考/工具块、卡片、脚注），保留用户气泡与索引行。</summary>
        private void RemoveAssistantSide(int turnId)
        {
            _streamItem = -1;
            _thinking = null;
            _thinkingText.Length = 0;
            _stream.Length = 0;
            _toolBlocks.Clear();

            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var line = _lines[i];
                if (line.TurnId != turnId || line.Kind == LineKind.User) continue;
                if (line.Block != null) ForgetBlock(line.Block);
                if (line.Go != null) UnityEngine.Object.Destroy(line.Go);
                _lines.RemoveAt(i);
            }

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.TurnId != turnId || item.Kind == LineKind.User) continue;
                if (item.Go != null) UnityEngine.Object.Destroy(item.Go);
                _items.RemoveAt(i);
            }

            UpdateReplyActionAvailability();
        }

        private static string ShortText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var t = text.Replace('\n', ' ');
            return t.Length <= 24 ? t : t.Substring(0, 24) + "…";
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

        private void RefreshIndexEmpty()
        {
            if (_chatIndexEmpty == null) return;
            _chatIndexEmpty.gameObject.SetActive(_conversations.Count == 0);
        }

        private void RefreshIndexSelection()
        {
            foreach (var entry in _turns)
            {
                if (entry.Link != null)
                    entry.Link.color = entry.Id == _focusedTurnId ? _accent : Color.white;
            }
        }

        /// <summary>
        /// 右栏滚到哪，左栏就染蓝哪一条记录：取视口中线落在的那一行所属的回合。
        /// 滚动位置没变就跳过，指针/输入都不受影响。
        /// </summary>
        private void SyncIndexTurnToScroll()
        {
            if (!IsOpen || _scroll == null || _scroll.viewport == null || _lines.Count == 0) return;
            if (++_indexTurnScanFrame % IndexTurnScanIntervalFrames != 0) return;
            var position = _scroll.verticalNormalizedPosition;
            if (!float.IsNaN(_indexTurnScanScroll) && Mathf.Abs(position - _indexTurnScanScroll) < 0.0005f)
                return;
            _indexTurnScanScroll = position;
            var turnId = TurnIdAtViewportCenter();
            if (turnId <= 0 || turnId == _focusedTurnId) return;
            _focusedTurnId = turnId;
            RefreshIndexSelection();
        }

        /// <summary>视口中线落在哪一行，就返回那一行所属回合；都够不到时退回最近的一条。</summary>
        private int TurnIdAtViewportCenter()
        {
            var viewport = _scroll.viewport;
            var centerWorldY = viewport.TransformPoint(viewport.rect.center).y;
            var above = 0;
            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                if (line.TurnId <= 0 || line.Go == null) continue;
                var rect = line.Go.GetComponent<RectTransform>();
                if (rect == null) continue;
                var top = rect.TransformPoint(new Vector3(0f, rect.rect.yMax, 0f)).y;
                var bottom = rect.TransformPoint(new Vector3(0f, rect.rect.yMin, 0f)).y;
                if (centerWorldY >= top) return above != 0 ? above : line.TurnId;
                if (centerWorldY >= bottom) return line.TurnId;
                above = line.TurnId;
            }
            return above;
        }

        /// <summary>
        /// 生成一条消息的界面：**克隆 prefab 里的示例节点**
        /// （`example-user-*` → 气泡，`example-assistant-*` → 裸文本）。
        ///
        /// 这样样式只在 prefab 里定义一次：在 Unity 里改气泡，运行期自动跟着变，
        /// 不用在 C# 里再抄一份颜色/贴图。
        /// </summary>
        private int CreateItem(LineKind kind)
        {
            var replyTemplate = kind == LineKind.Assistant ? _replyTemplate : null;
            var template = kind == LineKind.User
                ? _userTemplate
                : kind == LineKind.Assistant
                    ? (replyTemplate != null ? replyTemplate : _assistantTemplate)
                    : _assistantTemplate;

            GameObject go;
            ReplyView reply = null;
            if (template != null && _content != null)
            {
                go = UnityEngine.Object.Instantiate(template.gameObject, _content, false);
                go.name = "msg-" + (++_itemSeq);
                go.SetActive(true);
                if (replyTemplate != null && ReferenceEquals(template, replyTemplate))
                    reply = BindReply(go);
            }
            else
            {
                Plugin.Log.LogError("[UI] prefab 缺少消息模板，无法创建消息");
                return -1;
            }

            var fromPrefab = template != null && _content != null;

            Text label = null;
            if (reply != null)
            {
                label = reply.Body;
            }
            else if (kind == LineKind.User)
            {
                // 用户气泡的正文固定在 bubble/text；不能笼统取第一个 Text——
                // footer 里的时间也是 Text。
                label = FindChildText(go.transform, "bubble/text");
                if (label == null) label = go.GetComponentInChildren<Text>(true);
            }
            else
            {
                label = go.GetComponentInChildren<Text>(true);
            }

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

            var item = new Item
            {
                Go = go,
                Label = label,
                Kind = kind,
                TurnId = _currentTurnId,
                FromPrefab = fromPrefab,
                Reply = reply,
            };
            if (kind == LineKind.User && fromPrefab) item = BindUserFooter(item, go);
            _items.Add(item);
            return _items.Count - 1;
        }

        /// <summary>绑定用户气泡下的 footer：只有时间 + 复制按钮。</summary>
        /// <remarks>
        /// Item 是 struct，按值传进来再改字段只会改到副本，调用方拿到的 Footer 还是 null，
        /// 于是 footer 一直停在下面的 SetActive(false) 上（用户气泡看不到时间/复制）。
        /// 所以这里必须把 Item 返回给调用方。
        /// </remarks>
        private Item BindUserFooter(Item item, GameObject go)
        {
            if (go == null) return item;
            var footer = go.transform.Find("footer");
            if (footer == null) return item;

            item.Footer = footer.gameObject;
            item.FooterTime = FindChildText(footer, "time");
            var copyTr = footer.Find("copy");
            item.FooterCopy = copyTr != null ? copyTr.GetComponent<Button>() : null;
            footer.gameObject.SetActive(false);   // 等正文落地再显示
            return item;
        }

        /// <summary>填用户消息的 footer：时间 + 复制当前气泡文本。</summary>
        private void ApplyUserFooter(int index, string text, string time)
        {
            if (index < 0 || index >= _items.Count) return;
            var item = _items[index];
            if (item.Footer == null) return;

            item.Footer.SetActive(!string.IsNullOrEmpty(text));
            if (item.FooterTime != null)
                item.FooterTime.text = FormatGameTime(time);
            if (item.FooterCopy != null)
            {
                var copy = text ?? string.Empty;
                item.FooterCopy.onClick.RemoveAllListeners();
                item.FooterCopy.onClick.AddListener(() =>
                {
                    if (!string.IsNullOrEmpty(copy)) GUIUtility.systemCopyBuffer = copy;
                });
            }
            _items[index] = item;
        }

        /// <summary>
        /// 绑定 prefab 的 example-reply。只查节点、接回调，不改布局；
        /// 思考/卡片/工具模板先隐藏，等对应数据到达再显示。
        /// </summary>
        private ReplyView BindReply(GameObject go)
        {
            var view = new ReplyView { Go = go };
            var t = go.transform;
            view.Body = FindChildText(t, "body");
            // prefab 里的示例正文只是排版样板；运行期实例必须从空正文开始。
            if (view.Body != null) view.Body.text = string.Empty;

            // 新 prefab：loading header + content（thinking-block / tools 在 content 里）；
            // 旧 prefab：thinking-block / tools 直接挂在回复下。
            view.Loading = t.Find("loading") as RectTransform;
            var loadingContent = view.Loading != null
                ? view.Loading.Find("content") as RectTransform
                : null;
            view.LoadingContent = loadingContent != null ? loadingContent : view.Loading;
            var thinkingRoot = view.LoadingContent != null
                ? view.LoadingContent.Find("thinking-block") as RectTransform
                : null;
            view.ThinkingRoot = thinkingRoot != null ? thinkingRoot : t.Find("thinking-block") as RectTransform;
            if (view.ThinkingRoot != null)
            {
                view.Thinking = BindReplyBlock(view.ThinkingRoot.gameObject);
                view.ThinkingRoot.gameObject.SetActive(false);
            }

            var toolsRoot = view.LoadingContent != null
                ? view.LoadingContent.Find("tools") as RectTransform
                : null;
            view.Tools = toolsRoot != null ? toolsRoot : t.Find("tools") as RectTransform;
            if (view.Tools != null)
            {
                view.ToolTemplate = view.Tools.Find("tool-template") as RectTransform;
                if (view.ToolTemplate != null) view.ToolTemplate.gameObject.SetActive(false);
                view.Tools.gameObject.SetActive(false);
            }

            if (view.Loading != null)
            {
                BindLoadingHeader(view);
                view.LoadingExpanded = true;
                view.LoadingDone = false;
                if (view.LoadingContent != null && !ReferenceEquals(view.LoadingContent, view.Loading))
                    view.LoadingContent.gameObject.SetActive(true);
                view.Loading.gameObject.SetActive(false);
            }

            var cards = t.Find("cards-group") as RectTransform;
            if (cards == null) cards = t.Find("blueprint-cards") as RectTransform;
            if (cards != null)
            {
                view.BlueprintCards = cards;
                view.CardTemplate = cards.Find("blueprint-card") as RectTransform;
                if (view.CardTemplate == null) view.CardTemplate = cards.Find("blueprint-card-template") as RectTransform;
                view.BuildingCardTemplate = cards.Find("building-card") as RectTransform;
                view.StarCardTemplate = cards.Find("star-card") as RectTransform;
                if (view.CardTemplate != null) view.CardTemplate.gameObject.SetActive(false);
                if (view.BuildingCardTemplate != null) view.BuildingCardTemplate.gameObject.SetActive(false);
                if (view.StarCardTemplate != null) view.StarCardTemplate.gameObject.SetActive(false);
                cards.gameObject.SetActive(false);
                Plugin.Log.LogDebug("[link] BindReply cards-group=" + (view.BlueprintCards != null) +
                    " blueprint=" + (view.CardTemplate != null) +
                    " building=" + (view.BuildingCardTemplate != null) +
                    " star=" + (view.StarCardTemplate != null));
            }

            var footer = t.Find("footer");
            if (footer != null)
            {
                view.Footer = footer.gameObject;
                view.Time = FindChildText(footer, "time");
                view.Stopped = FindChildText(footer, "stopped");
                if (view.Stopped != null)
                {
                    view.Stopped.text = Strings.Get("reply.stopped");
                    view.Stopped.gameObject.SetActive(false);
                }
                view.Copy = footer.Find("copy") != null ? footer.Find("copy").GetComponent<Button>() : null;
                view.CopyLabel = FindChildText(footer, "copy/label");
                if (view.CopyLabel != null) view.CopyLabel.text = Strings.Get("reply.copy");
                if (view.Copy != null)
                {
                    view.Copy.onClick.RemoveAllListeners();
                    view.Copy.onClick.AddListener(() =>
                    {
                        if (!string.IsNullOrEmpty(view.CopyText))
                            GUIUtility.systemCopyBuffer = view.CopyText;
                    });
                }

                var ret = footer.Find("return");
                view.Return = ret != null ? ret.GetComponent<Button>() : null;
                if (view.Return != null)
                {
                    view.Return.onClick.RemoveAllListeners();
                    view.Return.onClick.AddListener(() => RequestRegenerate(view));
                    view.Return.gameObject.SetActive(false);   // 由 UpdateReplyActionAvailability 决定
                }

                var withdraw = footer.Find("withdraw");
                view.Withdraw = withdraw != null ? withdraw.GetComponent<Button>() : null;
                if (view.Withdraw != null)
                {
                    view.Withdraw.onClick.RemoveAllListeners();
                    view.Withdraw.onClick.AddListener(() => RequestWithdraw(view));
                    view.Withdraw.gameObject.SetActive(false);   // 由 UpdateReplyActionAvailability 决定
                }
                // 重建 prefab 时丢过按钮（91f5ea5 的 return-button 同理），缺失必须留痕。
                if ((view.Return == null || view.Withdraw == null) && !_missingReplyActionsLogged)
                {
                    _missingReplyActionsLogged = true;
                    Plugin.Log.LogWarning("[UI] 回复 footer 缺少 return/withdraw 节点，重新生成/撤回按钮不可用");
                }
                footer.gameObject.SetActive(false);
            }
            view.TurnId = _currentTurnId;
            return view;
        }

        /// <summary>
        /// 绑定 loading 的标题行：转圈图标、展开图标、标题，以及点击展开/收起。
        /// 节点全部来自 prefab；运行期只挂旋转组件、接点击。
        /// </summary>
        private void BindLoadingHeader(ReplyView view)
        {
            if (view == null || view.Loading == null) return;
            var header = view.Loading.Find("header");
            if (header == null) return;

            view.LoadingHeader = header.gameObject;
            view.LoadingTitle = FindChildText(header, "title");
            view.LoadingChevron = FindChildText(header, "chevron");

            var spinnerTr = header.Find("spinner");
            if (spinnerTr != null)
            {
                view.LoadingSpinner = spinnerTr.GetComponent<Image>();
                var spin = spinnerTr.GetComponent<LoadingSpinner>();
                if (spin == null) spin = spinnerTr.gameObject.AddComponent<LoadingSpinner>();
                view.LoadingSpin = spin;
            }

            // 点击整行展开/收起：优先接游戏原生 UIButton（悬停/音效），否则退回 Unity Button。
            var native = header.GetComponent<UIButton>();
            var button = header.GetComponent<Button>();
            if (native != null) native.onClick += _ => ToggleLoadingGroup(view);
            else if (button != null) button.onClick.AddListener(() => ToggleLoadingGroup(view));

            // 初始按“运行中”摆好；真正显示时 ShowReplyLoading / MarkLoadingDone 会重置。
            if (view.LoadingTitle != null)
            {
                view.LoadingTitle.text = Strings.Get("loading.running");
                view.LoadingTitle.color = _accent;
            }
            if (view.LoadingChevron != null) view.LoadingChevron.gameObject.SetActive(false);
            if (view.LoadingSpinner != null) view.LoadingSpinner.gameObject.SetActive(true);
        }

        private Block BindReplyBlock(GameObject root)
        {
            var block = new Block { Go = root, Expanded = true };
            var header = root.transform.Find("header");
            block.Header = header as RectTransform;
            block.Accent = root.transform.Find("accent") != null
                ? root.transform.Find("accent").GetComponent<Image>()
                : null;
            if (header != null)
            {
                block.Chevron = FindChildText(header, "chevron");
                block.Title = FindChildText(header, "title");
                block.TitleLayout = block.Title != null ? block.Title.GetComponent<LayoutElement>() : null;
                block.Summary = FindChildText(header, "summary");

                var button = header.GetComponent<Button>();
                var native = header.GetComponent<UIButton>();
                if (native != null) native.onClick += _ => ToggleBlock(block);
                else if (button != null) button.onClick.AddListener(() => ToggleBlock(block));
            }
            block.Body = FindChildText(root.transform, "body-clip/body")
                ?? FindChildText(root.transform, "body");
            AttachThinkingBodyClip(block);
            AssignIconMapping(block.Body, TryGetPreferredIconMapping());
            return block;
        }

        /// <summary>
        /// 给 prefab 里的思考块挂上正文裁切框。老 prefab 没有 body-clip，这里为 null，
        /// 运行期就退回“整段展开”的旧行为。
        /// </summary>
        private static void AttachThinkingBodyClip(Block block)
        {
            if (block == null || block.Go == null || block.Body == null) return;
            var clipRt = block.Go.transform.Find("body-clip") as RectTransform;
            if (clipRt == null) return;
            var clip = clipRt.GetComponent<ThinkingBodyClip>();
            if (clip == null) clip = clipRt.gameObject.AddComponent<ThinkingBodyClip>();
            clip.Body = block.Body;
            block.BodyClip = clip;
        }

        private static Text FindChildText(Transform root, string path)
        {
            if (root == null) return null;
            var t = root.Find(path);
            return t != null ? t.GetComponent<Text>() : null;
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
                // 标题是 prefab 里的设计稿：材质/泛光也按 prefab，不套聊天正文的柔化材质。
                if (_titleFromPrefab && ReferenceEquals(t, _titleText)) continue;
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
        private static void AssignIconMapping(
            Text label,
            UnityEngine.UI.Youthcat.TextIconMapping mapping,
            bool restyle = true)
        {
            if (label == null || mapping == null) return;
            label.supportRichText = true;
            // 图标不是普通字符：`UI Ex/Text Alpha` 用 UV1>=2 去全局图标图集采样。
            // prefab 里 assistant 正文可能只挂了默认 UI 材质，图标会变成白块。
            // 标题框/左栏标题沿用 prefab 自己的字体与材质，所以只挂映射、不重套样式。
            if (restyle) GameTextStyle.Apply(label);
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
            AssignTitleIconMapping(icons);
            AssignInputIconMapping(icons);

            var valid = GetValidIconTags();
            if (valid == null || valid.Count == 0)
            {
                // 表对象有了但条目还没填充：先挂上，等条目就绪后再清理文本。
                AssignIconMapping(_transcript, icons);
                foreach (var item in _items)
                {
                    AssignIconMapping(item.Label, icons);
                    EnsureReplyIconMappings(item.Reply, icons, false);
                }
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
                EnsureReplyIconMappings(item.Reply, icons, firstReady);
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

        private void EnsureReplyIconMappings(ReplyView reply,
            UnityEngine.UI.Youthcat.TextIconMapping icons, bool resanitize)
        {
            if (reply == null) return;
            if (resanitize) ResanitizeLabel(reply.Body);
            AssignIconMapping(reply.Body, icons);
            for (int i = 0; i < reply.BodyExtras.Count; i++)
            {
                var go = reply.BodyExtras[i];
                if (go == null) continue;
                var extra = go.GetComponent<Text>();
                if (extra == null) continue;
                if (resanitize) ResanitizeLabel(extra);
                AssignIconMapping(extra, icons);
            }
            for (int i = 0; i < reply.ThinkingBlocks.Count; i++)
            {
                var thinking = reply.ThinkingBlocks[i];
                if (thinking == null) continue;
                AssignIconMapping(thinking.Body, icons);
            }
            for (int i = 0; i < reply.CardViews.Count; i++)
            {
                var card = reply.CardViews[i];
                if (card == null) continue;
                if (resanitize) ResanitizeLabel(card.Name);
                AssignIconMapping(card.Name, icons);
                AssignIconMapping(card.Description, icons);
            }
        }

        private static void ResanitizeLabel(Text label)
        {
            if (label == null || string.IsNullOrEmpty(label.text)) return;
            // 这里只补图标表校验；不要重新跑 Markdown/HTML 格式化——
            // label.text 已经是游戏标签，重跑会把已经展开的标签当普通文本。
            var cleaned = SanitizeIconTags(label.text);
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
            for (int i = 0; i < text.Length; )
            {
                var c = text[i];
                if (c == '\n')
                {
                    if (line > widest) widest = line;
                    line = 0f;
                    i++;
                    continue;
                }
                if (TryReadIconTag(text, i, out var tagEnd))
                {
                    line += fontSize;   // 图标按一个字宽估
                    i = tagEnd;
                    continue;
                }
                line += c > 0x2E80 ? fontSize : fontSize * 0.55f;   // 汉字按全角估
                i++;
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
            foreach (var l in _lines) total += LineCharCount(l);
            while (_streamItem < 0 && total > MaxTranscriptChars && _lines.Count > 0)
            {
                RemoveOldestLine();
                total = 0;
                foreach (var l in _lines) total += LineCharCount(l);
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

        private static int LineCharCount(Line line)
        {
            var count = line.Text != null ? line.Text.Length : 0;
            if (line.Thinking != null)
            {
                for (int i = 0; i < line.Thinking.Count; i++)
                {
                    var text = line.Thinking[i].Text;
                    if (!string.IsNullOrEmpty(text)) count += text.Length;
                }
            }
            if (line.Steps != null)
            {
                for (int i = 0; i < line.Steps.Count; i++)
                {
                    var step = line.Steps[i];
                    if (step == null) continue;
                    if (!string.IsNullOrEmpty(step.Text)) count += step.Text.Length;
                    if (!string.IsNullOrEmpty(step.Detail)) count += step.Detail.Length;
                }
            }
            if (line.Cards != null)
            {
                for (int i = 0; i < line.Cards.Count; i++)
                {
                    var card = line.Cards[i];
                    if (card != null && !string.IsNullOrEmpty(card.Description))
                        count += card.Description.Length;
                }
            }
            return count;
        }

        private void RemoveOldestLine()
        {
            if (_lines.Count == 0) return;
            var line = _lines[0];
            if (line.Block != null) ForgetBlock(line.Block);

            // 回复的子节点（工具块等）可能也有独立的 Line 条目；先按层级一起删掉，
            // 否则根节点销毁后这些行还留在 _lines 里，索引会指向已消失的回合。
            if (line.Kind == LineKind.Assistant && line.Go != null)
            {
                var root = line.Go.transform;
                for (int i = _lines.Count - 1; i >= 1; i--)
                {
                    var other = _lines[i];
                    if (other.Go == null) continue;
                    if (ReferenceEquals(other.Go, line.Go) || other.Go.transform.IsChildOf(root))
                    {
                        if (other.Block != null) ForgetBlock(other.Block);
                        _lines.RemoveAt(i);
                    }
                }
            }

            RemoveItemsUnder(line.Go);
            ReleaseLinksUnder(line.Go);
            if (line.Go != null) UnityEngine.Object.Destroy(line.Go);
            _lines.RemoveAt(0);
        }

        private void RemoveItemsUnder(GameObject go)
        {
            if (go == null) return;
            var root = go.transform;

            var deadKeys = new List<string>();
            foreach (var kv in _toolBlocks)
            {
                var blockGo = kv.Value != null ? kv.Value.Go : null;
                if (blockGo == null || ReferenceEquals(blockGo, go) || blockGo.transform.IsChildOf(root))
                    deadKeys.Add(kv.Key);
            }
            foreach (var key in deadKeys) _toolBlocks.Remove(key);

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var itemGo = _items[i].Go;
                if (itemGo == null)
                {
                    _items.RemoveAt(i);
                    if (_streamItem > i) _streamItem--;
                    continue;
                }
                if (!ReferenceEquals(itemGo, go) && !itemGo.transform.IsChildOf(root)) continue;
                var reply = _items[i].Reply;
                if (reply != null)
                {
                    for (int b = reply.ThinkingBlocks.Count - 1; b >= 0; b--)
                        RemoveBlockReference(reply.ThinkingBlocks[b]);
                }
                _items.RemoveAt(i);
                if (_streamItem == i) _streamItem = -1;
                else if (_streamItem > i) _streamItem--;
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

                if (child.name == "example-reply")
                {
                    _replyTemplate = child as RectTransform;
                }
                else if (_userTemplate == null && child.name.StartsWith("example-user"))
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
                if (_replyTemplate != null && go.transform == _replyTemplate) continue;
                UnityEngine.Object.Destroy(go);
            }

            if (_userTemplate != null) _userTemplate.gameObject.SetActive(false);
            if (_assistantTemplate != null) _assistantTemplate.gameObject.SetActive(false);
            if (_replyTemplate != null) _replyTemplate.gameObject.SetActive(false);

            Plugin.Log.LogInfo($"[UI] 消息模板：用户={(_userTemplate != null ? _userTemplate.name : "无")}，"
                               + $"助手={(_replyTemplate != null ? _replyTemplate.name : _assistantTemplate != null ? _assistantTemplate.name : "无")}");
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
            // ESC：ask 面板开着时先跳过当前题，不关窗。
            if (IsOpen && AskShowing)
            {
                if (VFInput.escape)
                {
                    VFInput.UseEscape();
                    _askPanel.SkipCurrent();
                    return;
                }
                _wantInputFocus = false;
            }
            // ESC：工具列表开着时先关列表，不关窗；否则聊天窗开着时由我们消费掉，
            // 避免同一次按键同时关窗并打开游戏的暂停菜单。
            // 游戏自己的窗口也是这个套路：先 UseEscape() 再处理。
            if (IsOpen && _toolMenuOpen && VFInput.escape)
            {
                VFInput.UseEscape();
                HideToolMenu();
                if (_input != null && _input.text == "/") SetInputText(string.Empty, 0);
                return;
            }
            if (IsOpen && _toolMenuOpen)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) MoveToolSelection(1);
                if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) MoveToolSelection(-1);
            }
            if (IsOpen && VFInput.escape)
            {
                VFInput.UseEscape();
                SetOpen(false);
                return;
            }

            ModelSettingsTab.Watch();   // 设置窗口打开时自动补「模型」页签

            if (IsOpen) Game.GameRefs.SetTextInputActive(true);
            if (IsOpen) EnsureThinkingComboText();

            // 标题框正在编辑时键盘归它：清掉"期望聚焦"，避免下一行把焦点抢回聊天框。
            // 点过窗口任意位置后 _wantInputFocus 就是 true，不清理的话点标题会立刻被抢回去。
            if (TitleEditing)
            {
                _wantInputFocus = false;
                // 标题刚被选中时聊天框可能还挂着一次待激活，放着不管下一帧会把焦点抢回来。
                if (_input != null) _input.m_ShouldActivateNextUpdate = false;
            }

            // Enter 进入输入状态（此时 R 才让位给打字）；ask 时回车由 AskPanel 处理。
            // 标题框里按 Enter 是在提交改名，不能再顺手把焦点切到聊天框；
            // 若本帧标题编辑刚好结束（事件顺序不定），用帧号兜住。
            if (IsOpen && !AskShowing && !InputFocused && !TitleEditing
                && Time.frameCount != _titleEditEndFrame
                && UnityEngine.Input.GetKeyDown(KeyCode.Return))
                FocusInput();

            // 期望聚焦但实际没聚焦 → 重申（游戏可能清空了 EventSystem 的选中对象）
            if (IsOpen && !AskShowing && _wantInputFocus && !TitleEditing
                && _input != null && !_input.isFocused)
                ApplyFocus();

            // 焦点有了但游戏 InputField 的 Caret 可能还没创建/绑定材质。
            if (IsOpen && _input != null && _input.isFocused)
            {
                SnapshotInputSelection();
                if (!_caretPrimed) PrimeInputCaret();
            }
            else
            {
                _caretPrimed = false;
            }

            // 游戏版 InputField 聚焦时改看 m_ColorText（带颜色标签的渲染串）。
            // 若它没跟着草稿更新，未聚焦能看到、一点击就空白；这里在刚聚焦时补一次并留日志。
            var inputFocusedNow = _input != null && _input.isFocused;
            if (inputFocusedNow != _lastInputFocused)
            {
                _lastInputFocused = inputFocusedNow;

                // 聚焦路径若把草稿清掉，在玩家开始编辑前补回一次。
                if (inputFocusedNow && _input != null && _draftRestoreArmed
                    && _draftInput != null && string.IsNullOrEmpty(_input.text))
                {
                    _input.text = _draftInput;
                    _input.caretPosition = _input.text.Length;
                    _input.CalculateColorText();
                    _input.ForceLabelUpdate();
                }

                var colorText = InputColorTextField != null
                    ? InputColorTextField.GetValue(_input) as string
                    : null;
                Plugin.Log.LogInfo(
                    $"[输入] focused={inputFocusedNow} text={(_input != null ? _input.text.Length : -1)} " +
                    $"colorText={(colorText != null ? colorText.Length : -1)} " +
                    $"comp={(_input != null && _input.textComponent != null ? _input.textComponent.text.Length : -1)} " +
                    $"draft={(_draftInput != null ? _draftInput.Length : -1)}");
                if (inputFocusedNow && _input != null
                    && !string.IsNullOrEmpty(_input.text))
                {
                    // 聚焦后显示层从静态 Text 切到 InputField 自己的绘制路径，
                    // 材质绑定可能还是空的（和开窗时同一个问题），补绑一次。
                    RebindInputTextMaterial(_input.textComponent, "聚焦");
                    if (string.IsNullOrEmpty(colorText))
                    {
                        _input.CalculateColorText();
                        _input.ForceLabelUpdate();
                    }
                }
            }

            // 图标表可能比窗口/消息晚就绪（主菜单 → 存档加载）；低频补齐即可。
            if (IsOpen && ++_iconMapTick % 30 == 0) EnsureIconMappings();

            // 输入区随内容长高：粘贴长文本要撑开，超过上限交给滚动条。
            if (IsOpen) UpdateInputAreaIfNeeded();

            // 左栏展开/收起与换位动画（窗口关着时不推进，直接由 FinishIndexTweens 收拾）。
            if (IsOpen) UpdateIndexTweens(Time.unscaledDeltaTime);

            // 左栏“当前看到的那一条”跟着右栏滚动位置走。
            if (IsOpen) SyncIndexTurnToScroll();

            // A.14.7：非回车键的发送/换行绑定在这里轮询；回车仍走 OnInputNewLine。
            if (IsOpen) PollKeyBindings();

            // 多行输入框里按了回车：在 Tick 里落地提交（见 OnInputNewLine）
            if (_submitPending)
            {
                _submitPending = false;
                SubmitPendingInput();
            }

            if (_dirty)
            {
                _dirty = false;
                // 折叠块的展开/收起要保住当前滚动位置（见 ToggleBlock），
                // 这里先把“本帧不自动贴底”记下来，别把延迟贴底也排进去。
                bool keepScroll = _suppressAutoScrollOnce;
                Render();

                // 内容高度要等这一帧的 Canvas 布局重建后才会更新，
                // 本帧设的 verticalNormalizedPosition 只能按旧高度算。
                // 再贴两帧（下一帧布局已就绪），覆盖“只改一次内容”的场景：
                // 蓝图卡片、系统提示、非流式终稿——它们之后不会再有 dirty 帧。
                if (!keepScroll)
                    _scrollBottomFrames = Mathf.Max(_scrollBottomFrames, 2);
            }

            // 一次性内容插入后继续贴底两帧（见 ScrollToBottomAfterContentChange）。
            // 独立于 _dirty：卡片插入后不会再有 dirty 帧。
            if (_scrollBottomFrames > 0)
            {
                _scrollBottomFrames--;
                ScrollToBottom();
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
                if (_stream.Length != _lastStreamRenderLength)
                {
                    _lastStreamRenderLength = _stream.Length;
                    var label = item.Reply != null ? item.Reply.Body : item.Label;
                    if (label != null)
                        ApplyRichText(label, _stream.ToString(), RichOptions14, RichTextProfile.Body);
                }
            }

            RenderThinking();

            if (_suppressAutoScrollOnce) _suppressAutoScrollOnce = false;
            else ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            if (_scroll == null) return;
            // 0 = 底部。即使本帧布局还没刷新，下一帧再设一次即可收敛。
            _scroll.verticalNormalizedPosition = 0f;
        }

        /// <summary>
        /// 一次性内容插入后的“立刻贴底”。
        ///
        /// 蓝图卡片是插进回复里的网格的：节点先建好，内容高度要等 Canvas 布局重建后
        /// 才变大。此时直接设 verticalNormalizedPosition，ScrollRect 只能按旧高度算，
        /// 滚动条停在旧底部，新卡片就压在视口下面（流式文本每帧都会重贴，所以没这问题；
        /// 卡片只贴一次，之后没有 dirty 帧来纠正）。
        ///
        /// 处理：先强制重建布局再贴底；之后两帧继续贴底，兜住网格/文字晚一帧才定高的
        /// 情况（ContentSizeFitter 的二次收敛）。
        /// </summary>
        private void ScrollToBottomAfterContentChange()
        {
            if (_scroll == null) return;
            RebuildLayout();
            ScrollToBottom();
            _scrollBottomFrames = 2;
        }

        // ---------------------------------------------------------------- 输入

        /// <summary>
        /// 多行输入框里按下回车。
        ///
        /// 多行模式下 UGUI 不会走 onEndEdit，而是插一个换行、再抛 `OnNewLine`（游戏改写版的行为）。
        /// 现在按配置判定（协议附录 A.14.7）：
        ///
        ///   * 组合等于「发送」→ 置待提交标志，由本帧 Tick 清空并失焦
        ///   * 组合等于「换行」→ 保留换行，并复用原生待办框的复选框续写
        ///   * 其它组合 → 不补复选框，按输入框原生行为换行
        ///
        /// 工具菜单开着时回车始终是「选中当前项」，不参与发送/换行。
        /// 事件是在按键处理中间同步抛出来的，此刻直接改 text 会重入 InputField，
        /// 所以发送只置标志，真正提交放到本帧的 `Tick()` 里做。
        /// </summary>
        private void OnInputNewLine(string text, int newlineIndex)
        {
            if (_toolMenuOpen)
            {
                var all = _input != null ? (_input.text ?? text ?? string.Empty) : (text ?? string.Empty);
                if (newlineIndex >= 0 && newlineIndex < all.Length)
                {
                    all = all.Remove(newlineIndex, 1);
                    SetInputText(all, Mathf.Min(newlineIndex, all.Length));
                }
                // 没有匹配项（例如手输了不存在的 /foo）就按普通消息发送，不要把玩家卡住。
                if (_toolVisible.Count == 0)
                {
                    HideToolMenu();
                    _submitPending = true;
                    return;
                }
                SelectHighlightedTool();
                return;
            }

            // 组字过程中按到绑定键不能把半成品发出去，也不该把组合键当换行。
            if (_input != null && !string.IsNullOrEmpty(_input.compositionString)) return;

            var chord = KeyChordInput.CurrentChord(KeyCode.Return);
            if (chord.Equals(_newlineChord))
            {
                ApplyCheckboxContinuation(newlineIndex);
                return;
            }
            if (chord.Equals(_sendChord))
            {
                _submitPending = true;
            }
        }

        /// <summary>
        /// 原生 UISmartInputField 的行为：上一行以 [-] / [+] 开头时，新行自动补一个复选框，
        /// 这样连续记待办不用每行都点一次按钮。
        /// </summary>
        private void ApplyCheckboxContinuation(int newlineIndex)
        {
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

        /// <summary>
        /// A.14.7：把发送/换行绑定到非回车键时，在 Tick 里轮询。
        /// 回车仍走 OnInputNewLine；这里跳过回车，避免同一帧双触发。
        /// </summary>
        private void PollKeyBindings()
        {
            if (_input == null || !_input.isFocused || AskShowing) return;
            if (TitleEditing || _toolMenuOpen) return;
            // 组字中不判定：拼音打到一半按到绑定键会把半成品发出去。
            if (!string.IsNullOrEmpty(_input.compositionString)) return;

            if (TryPollChord(_sendChord, KeyCode.Return))
            {
                _submitPending = true;
                return;
            }
            if (TryPollChord(_newlineChord, KeyCode.Return))
            {
                InsertNewlineAtCaret();
            }
        }

        private static bool TryPollChord(KeyChord chord, KeyCode excludedKey)
        {
            if (chord.IsNone) return false;
            if (!KeyChordInput.TryResolve(chord, out var key)) return false;
            if (key == excludedKey) return false;
            return KeyChordInput.IsPressedThisFrame(chord);
        }

        /// <summary>换行绑定到非回车键时，按光标位置插入换行，并复用复选框续写。</summary>
        private void InsertNewlineAtCaret()
        {
            try
            {
                RestoreInputSelection();
                _input.InsertGeneralString("\n");
                var newlineIndex = Mathf.Clamp(_input.caretPosition - 1, 0, Mathf.Max(0, (_input.text?.Length ?? 1) - 1));
                ApplyCheckboxContinuation(newlineIndex);
                _input.ForceLabelUpdate();
                RestoreInputFocus();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"按绑定键换行失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 可打印字符被绑成发送/换行时，必须把它从输入里拦掉：
        /// 否则「按 S 发送」会先把 s 打进输入框再发送。
        ///
        /// 判定依据是本帧的 GetKeyDown 状态（与事件先后无关）；粘贴/IME 提交
        /// 不会经过该键的 KeyDown，因此不会被误删。
        /// </summary>
        private char ValidateInputChar(string current, int charIndex, char addedChar)
        {
            var previous = _previousInputValidator;
            if (previous is not null)
            {
                var chained = previous(current, charIndex, addedChar);
                if (chained == '\0' || chained != addedChar) return chained;
            }

            if (_input is not null && !string.IsNullOrEmpty(_input.compositionString)) return addedChar;
            if (_sendChord.TryGetTypedChar(out var sendChar) && sendChar == addedChar
                && KeyChordInput.IsPressedThisFrame(_sendChord))
                return '\0';
            if (_newlineChord.TryGetTypedChar(out var newlineChar) && newlineChar == addedChar
                && KeyChordInput.IsPressedThisFrame(_newlineChord))
                return '\0';
            return addedChar;
        }

        private void InstallInputKeyBindings()
        {
            if (_input == null) return;
            _previousInputValidator = _input.onValidateInput;
            _input.onValidateInput = ValidateInputChar;
        }

        /// <summary>A.14.7：设置页改键后同步到已创建的窗口。</summary>
        public void ApplyKeyBindings(KeyChord send, KeyChord newline)
        {
            _sendChord = send;
            _newlineChord = newline;
        }

        private void SubmitPendingInput()
        {
            if (_input == null) return;

            var raw = _input.text;
            var value = raw != null ? raw.Trim() : string.Empty;

            HideToolMenu();
            _input.text = string.Empty;
            _draftInput = null;
            _draftRestoreArmed = false;
            if (value.Length == 0) return;

            // 与单行路径一致：发送后主动失焦，玩家可直接按 R 关窗
            _input.DeactivateInputField();

            try
            {
                Submitted?.Invoke(value);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"提交失败：{ex}");
            }
        }

        /// <summary>
        /// A.15：AI 回答中，输入框为空 → 显示「中断」，有内容 → 换回「发送」。
        /// 两个按钮在 prefab 里同位，所以只切 active。
        /// </summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
            RefreshSendButtons();
        }

        /// <summary>
        /// 输入区高度要跟着内容走，所以先记住 prefab 里那几个节点：
        /// `content-column/input`（高度由 LayoutElement 决定）、输入框自己的 ScrollRect 与 content。
        /// </summary>
        private void BindInputArea(InputField input)
        {
            if (input == null) return;
            var inputRt = input.transform as RectTransform;
            var fieldRt = inputRt != null ? inputRt.parent as RectTransform : null;   // InputField
            _inputScrollContent = fieldRt != null ? fieldRt.parent as RectTransform : null;
            var viewport = _inputScrollContent != null ? _inputScrollContent.parent as RectTransform : null;
            _inputScroll = viewport != null ? viewport.parent != null
                ? viewport.parent.GetComponent<ScrollRect>() : null : null;
            var editor = _inputScroll != null ? _inputScroll.transform.parent as RectTransform : null;
            var area = editor != null ? editor.parent as RectTransform : null;
            _inputAreaLayout = area != null ? area.GetComponent<LayoutElement>() : null;
            _inputTextRect = input.textComponent != null ? input.textComponent.rectTransform : null;
            if (_inputAreaLayout == null || _inputScrollContent == null || _inputTextRect == null)
            {
                Plugin.Log.LogInfo("[UI] 输入区节点没找齐，长文本不会自动撑高。");
                return;
            }
            _inputAreaLayout.minHeight = InputAreaMinHeight;
            UpdateInputAreaHeight(false);
        }

        private void UpdateInputAreaIfNeeded()
        {
            if (_input == null || _inputAreaLayout == null) return;
            var text = _input.text;
            int length = text != null ? text.Length : 0;
            float width = _inputTextRect != null ? _inputTextRect.rect.width : 0f;
            bool changed = length != _lastInputLength || !Mathf.Approximately(width, _lastInputWidth);
            if (!changed && _inputAreaRefreshFrames <= 0) return;
            _lastInputLength = length;
            _lastInputWidth = width;
            if (changed) _inputAreaRefreshFrames = 2;
            else _inputAreaRefreshFrames--;
            UpdateInputAreaHeight(changed);
        }

        /// <summary>
        /// 按文本实际高度撑开输入区；到上限后不再长高，改由 prefab 里那条竖向滚动条滚。
        /// </summary>
        private void UpdateInputAreaHeight(bool scrollToCaret)
        {
            if (_input == null || _inputAreaLayout == null || _inputTextRect == null) return;
            var field = _input.textComponent;
            if (field == null) return;

            float textHeight = field.preferredHeight;
            if (textHeight <= 0f) return;
            float areaHeight = Mathf.Clamp(textHeight + InputAreaPadding, InputAreaMinHeight, InputAreaMaxHeight);
            if (!Mathf.Approximately(_inputAreaLayout.preferredHeight, areaHeight))
                _inputAreaLayout.preferredHeight = areaHeight;

            // ScrollRect 的 content 只有比 viewport 高才滚得动。
            var viewport = _inputScrollContent.parent as RectTransform;
            if (viewport == null) return;
            float viewportHeight = viewport.rect.height;
            float contentHeight = Mathf.Max(textHeight + InputAreaPadding, viewportHeight);
            float delta = contentHeight - viewportHeight;
            if (!Mathf.Approximately(_inputScrollContent.sizeDelta.y, delta))
                _inputScrollContent.sizeDelta = new Vector2(_inputScrollContent.sizeDelta.x, delta);

            // 光标停在末尾（粘贴/续写）时跟到底部，否则新内容会落在视口外面。
            if (scrollToCaret && _inputScroll != null && _input.caretPosition >= (_input.text != null ? _input.text.Length : 0))
                _inputScroll.verticalNormalizedPosition = 0f;
        }

        private bool HasPendingInput()
        {
            if (_input == null) return false;
            var text = _input.text;
            return text != null && text.Trim().Length > 0;
        }

        private void RefreshSendButtons()
        {
            bool typing = HasPendingInput();
            if (_sendButton != null)
            {
                bool showSend = !_busy || typing;
                if (_sendButton.gameObject.activeSelf != showSend)
                    _sendButton.gameObject.SetActive(showSend);
            }
            if (_breakButton != null)
            {
                bool showBreak = _busy && !typing;
                if (_breakButton.gameObject.activeSelf != showBreak)
                    _breakButton.gameObject.SetActive(showBreak);
            }
        }

        private void OnBreakClicked()
        {
            try
            {
                BreakRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[UI] 中断请求失败：{ex.Message}");
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

        /// <summary>
        /// 给工具条三个按钮接上游戏自己的实现（对照 `UISmartInputField`）：
        ///   * 复选框 → `InputField.InsertCheckBox()`
        ///   * 图标   → `UISignalTagPicker.Popup(...)`，选中后插入 `LDB.signals.IconTag(id)`
        ///   * 颜色   → `UISimpleColorPicker.Popup(...)`，选中后 `InputField.OnColorChange(...)`
        /// 弹出的选择器因此和游戏里其它输入框完全一致（图标表、调色板、带名字的图标等都是原生的）。
        ///
        /// 注意：这三个 prefab 按钮带 Unity `Button`（Selectable），点击会把焦点从
        /// InputField 抢走，`DeactivateInputField()` 随后把内部 caret 清 0。所以三个
        /// 插入动作都必须先 `RestoreInputSelection()`（见 Tick 里的快照）。
        /// </summary>
        private void WireInputToolbar(RectTransform panel)
        {
            var iconBtn = FindComponent<Button>(panel,
                "input/editor/tool-bar/icons/icon-select-btn", "tool-bar/icons/icon-select-btn", "icon-select-btn");
            var checkBtn = FindComponent<Button>(panel,
                "input/editor/tool-bar/icons/checkbox-btn", "tool-bar/icons/checkbox-btn", "checkbox-btn");
            var colorBtn = FindComponent<Button>(panel,
                "input/editor/tool-bar/icons/text-color-btn", "tool-bar/icons/text-color-btn", "text-color-btn");

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

        // ---------------------------------------------------------------- 工具列表（M4l）

        /// <summary>
        /// 绑定 prefab 里的工具列表（静态结构/样式全部在 prefab，运行期只找节点）。
        ///
        ///   input/tool-menu
        ///   ├── bg / border
        ///   ├── header/{title,hint}
        ///   ├── scroll-view/viewport/content/tool-row-template
        ///   ├── scroll-view/empty
        ///   └── footer
        /// </summary>
        private void WireToolMenu(RectTransform panel)
        {
            var toolBtnNode = FindNode(panel,
                "input/editor/tool-bar/icons/tool-btn", "tool-bar/icons/tool-btn", "tool-btn");
            _toolButton = toolBtnNode != null ? toolBtnNode.GetComponent<Button>() : null;
            if (_toolButton != null)
            {
                _toolButton.onClick.AddListener(ToggleToolMenu);
            }
            else
            {
                Plugin.Log.LogWarning("[工具] prefab 里没有 tool-btn，无法打开工具列表。");
            }

            _toolMenu = FindNode(panel, "input/tool-menu", "tool-menu") as RectTransform;
            if (_toolMenu == null)
            {
                Plugin.Log.LogWarning("[工具] prefab 里没有 input/tool-menu；点击 / 不会出现工具列表。");
                return;
            }

            var scroll = _toolMenu.Find("scroll-view") as RectTransform;
            _toolScroll = scroll != null ? scroll.GetComponent<ScrollRect>() : null;
            _toolScrollbar = scroll != null && scroll.Find("Scrollbar Vertical") != null
                ? scroll.Find("Scrollbar Vertical").gameObject
                : null;
            _toolContent = scroll != null ? scroll.Find("viewport/content") as RectTransform : null;
            _toolRowTemplate = _toolContent != null
                ? _toolContent.Find("tool-row-template") as RectTransform
                : null;
            _toolMenuEmpty = FindText(_toolMenu, "scroll-view/empty", "empty");
            _toolMenuHint = FindText(_toolMenu, "header/hint");
            _toolMenuFooter = FindText(_toolMenu, "footer");

            if (_toolMenuHint != null) _toolMenuHint.text = Strings.Get("tool.menu.hint");
            if (_toolMenuFooter != null) _toolMenuFooter.text = Strings.Get("tool.menu.footer");
            if (_toolRowTemplate != null) _toolRowTemplate.gameObject.SetActive(false);

            _toolMenu.gameObject.SetActive(false);
            _toolMenuOpen = false;

            if (_toolRowTemplate == null || _toolContent == null || _toolScroll == null)
            {
                Plugin.Log.LogWarning(
                    $"[工具] 工具列表结构不完整（row={_toolRowTemplate != null} content={_toolContent != null} scroll={_toolScroll != null}）。");
            }
            else
            {
                Plugin.Log.LogInfo("[UI] 已接上工具列表（tool-btn / 输入 /）。");
            }
        }

        // ---------------------------------------------------------------- 底部面板（input / ask）

        /// <summary>
        /// 记录输出区、输入栏和 choose。静态尺寸仍在 prefab；这里只缓存引用。
        /// </summary>
        private void WireBottomLayout(RectTransform panel)
        {
            _outputRoot = FindNode(panel, "content-column/output", "output") as RectTransform;
            _inputRoot = FindNode(panel, "content-column/input", "input") as RectTransform;
            _chooseRoot = FindNode(panel, "content-column/choose", "choose") as RectTransform;
            _bottomColumn = _outputRoot != null ? _outputRoot.parent as RectTransform : null;

            // prefab 里 choose 是设计态可见的；运行时默认收起，输入栏默认展开。
            if (_inputRoot != null) _inputRoot.gameObject.SetActive(true);
            if (_chooseRoot != null) _chooseRoot.gameObject.SetActive(false);
            RebuildBottomLayout();
        }

        private void WireAskPanel(RectTransform panel)
        {
            if (_chooseRoot == null)
            {
                Plugin.Log.LogInfo("[ask] prefab 没有 choose，作答面板不可用。");
                return;
            }

            var ask = _chooseRoot.GetComponent<AskPanel>();
            if (ask == null) ask = _chooseRoot.gameObject.AddComponent<AskPanel>();
            if (!ask.Bind(_chooseRoot))
            {
                Plugin.Log.LogWarning("[ask] choose/answer-grop 结构不完整，作答面板不可用。");
                _chooseRoot.gameObject.SetActive(false);
                return;
            }

            ask.Completed += OnAskCompleted;
            ask.Cancelled += OnAskCancelled;
            ask.ConfirmationChosen += OnConfirmationChosen;
            _askPanel = ask;
            _chooseRoot.gameObject.SetActive(false);
        }

        /// <summary>显示一组问题。choose 会替换 input，输出区按 choose 的高度收缩。</summary>
        public bool ShowAsk(List<AskQuestion> questions)
        {
            if (_askPanel == null || questions == null || questions.Count == 0) return false;
            if (_askPanel.IsShowing) return false;
            if (!IsOpen) SetOpen(true);
            if (!IsOpen) return false;

            HideToolMenu();
            _wantInputFocus = false;
            BlurInput();
            _askPanel.Show(questions);
            BeginBottomReplacement(_chooseRoot);
            return true;
        }

        /// <summary>收起 ask（不触发 Answered/Cancelled）。</summary>
        public void HideAsk()
        {
            if (_askPanel != null) _askPanel.Hide();
            EndBottomReplacement();
        }

        /// <summary>取消 ask；会触发 AskCancelled，让 sidecar 拿到 cancelled 结果。</summary>
        public void CancelAsk()
        {
            if (_askPanel != null && _askPanel.IsShowing) _askPanel.Cancel();
        }

        /// <summary>显示写操作确认页（复用 choose 的 question/explain/按钮）。</summary>
        public bool ShowConfirmation(string title, string details)
        {
            if (_askPanel == null || _askPanel.IsShowing) return false;
            if (!IsOpen) SetOpen(true);
            if (!IsOpen) return false;
            HideToolMenu();
            _wantInputFocus = false;
            BlurInput();
            _askPanel.ShowConfirmation(title, details);
            BeginBottomReplacement(_chooseRoot);
            return true;
        }

        public bool ConfirmationShowing => _askPanel != null && _askPanel.IsConfirming;

        /// <summary>
        /// 用另一个底部面板替换 input（ask 用 choose）。只切 active，
        /// 不改任何静态 RectTransform 尺寸；将来别的面板也可以复用。
        /// </summary>
        private void BeginBottomReplacement(RectTransform panel)
        {
            if (panel == null) return;
            if (_inputRoot != null) _inputRoot.gameObject.SetActive(false);
            _bottomReplacement = panel;
            panel.gameObject.SetActive(true);
            RebuildBottomLayout();
        }

        private void EndBottomReplacement()
        {
            if (_bottomReplacement != null) _bottomReplacement.gameObject.SetActive(false);
            _bottomReplacement = null;
            if (_inputRoot != null) _inputRoot.gameObject.SetActive(true);
            RebuildBottomLayout();
        }

        /// <summary>
        /// 高度由 prefab 的 content-column/VerticalLayoutGroup 计算；这里只通知布局组
        /// 在 input/choose 切换后立即重算，不写任何 RectTransform 尺寸。
        /// </summary>
        private void RebuildBottomLayout()
        {
            if (_bottomColumn == null || !_bottomColumn.gameObject.activeInHierarchy) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_bottomColumn);
            Canvas.ForceUpdateCanvases();
        }

        private void OnAskCompleted(List<AskAnswer> answers)
        {
            EndBottomReplacement();
            AskCompleted?.Invoke(answers);
        }

        private void OnAskCancelled()
        {
            EndBottomReplacement();
            AskCancelled?.Invoke();
        }

        private void OnConfirmationChosen(bool accepted)
        {
            EndBottomReplacement();
            ConfirmationChosen?.Invoke(accepted);
        }

        /*
         * 底部区域的 RectTransform 完全由 prefab 决定：
         *   - input 固定 100 高，内部 scroll-view 自己滚动；
         *   - output 的底边在 prefab 里已经是 142（40 + 100 + 2），与 input 顶边对齐；
         *   - choose 是 200 高的模态面板，显示时覆盖聊天区底部，不再运行期改 output 尺寸。
         * 运行期只切换 input/choose 的 active，不写 sizeDelta/offsetMin。
         */

        /// <summary>连接断开时清空目录，避免还显示一份已经不可用的工具。</summary>
        public void ClearToolCatalog()
        {
            _toolCatalog.Clear();
            _toolCatalogLoaded = false;
            if (_toolMenuOpen) RefreshToolMenu();
        }

        /// <summary>ready.payload.toolCatalog 到达后更新列表数据；菜单开着就立即刷新。</summary>
        public void SetToolCatalog(IEnumerable<ToolMenuItem> items)
        {
            _toolCatalog.Clear();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                    if (string.IsNullOrEmpty(item.Label)) item.Label = ToolNames.Label(item.Name);
                    _toolCatalog.Add(item);
                }
            }
            _toolCatalogLoaded = true;
            if (_toolMenuOpen) RefreshToolMenu();
            else if (_toolMenuEmpty != null && _toolCatalog.Count == 0)
                _toolMenuEmpty.text = Strings.Get("tool.menu.empty");
        }

        /// <summary>调试直调用：当前工具目录的副本（不暴露内部列表）。</summary>
        internal List<ToolMenuItem> GetToolCatalog()
        {
            return new List<ToolMenuItem>(_toolCatalog);
        }

        /// <summary>
        /// 解析输入开头的 `/<工具名>`，返回模型可见工具名（也接受显示名）。
        /// 未知名字返回 null，按普通文本发送。
        /// </summary>
        public string GetRequiredTool(string text)
        {
            if (!TryLeadingSlashToken(text, out var token, out var end)) return null;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) return null;
            if (string.IsNullOrEmpty(token)) return null;

            for (int i = 0; i < _toolCatalog.Count; i++)
            {
                var item = _toolCatalog[i];
                if (string.Equals(item.Name, token, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Label, token, StringComparison.OrdinalIgnoreCase))
                    return item.Name;
            }
            return null;
        }

        private void ToggleToolMenu()
        {
            if (_toolMenu == null) return;
            if (_toolMenuOpen)
            {
                HideToolMenu();
                // 只是点按钮打开、还没输入过滤词时，关掉菜单顺手把占位的 "/" 清掉。
                if (_input != null && _input.text == "/") SetInputText(string.Empty, 0);
                return;
            }

            // 空输入时点击按钮等价于输入一个 "/"，之后可以直接打字过滤。
            var text = _input != null ? (_input.text ?? string.Empty) : string.Empty;
            if (text.Length == 0)
            {
                SetInputText("/", 1);
                _toolFilter = string.Empty;
            }
            else if (TryLeadingSlashToken(text, out var token, out var end) && end == text.Length)
            {
                _toolFilter = token;
            }
            else
            {
                _toolFilter = string.Empty;
            }

            _toolSelected = 0;
            ShowToolMenu();
            FocusInput();
        }

        private void OnInputValueChanged(string value)
        {
            // 玩家一旦改过文本（值和暂存草稿不同），就不再补回旧草稿。
            if (_draftRestoreArmed && value != _draftInput) _draftRestoreArmed = false;

            if (_suppressToolMenuEvents || !IsOpen) return;

            var text = value ?? string.Empty;
            if (TryLeadingSlashToken(text, out var token, out var end) && end == text.Length)
            {
                _toolFilter = token;
                _toolSelected = 0;
                ShowToolMenu();
            }
            else if (_toolMenuOpen)
            {
                HideToolMenu();
            }
        }

        private static bool TryLeadingSlashToken(string text, out string token, out int tokenEnd)
        {
            token = null;
            tokenEnd = -1;
            if (string.IsNullOrEmpty(text)) return false;

            int start = 0;
            while (start < text.Length && (text[start] == ' ' || text[start] == '\t')) start++;
            if (start >= text.Length || text[start] != '/') return false;

            int end = start + 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            token = text.Substring(start + 1, end - start - 1);
            tokenEnd = end;
            return true;
        }

        private void ShowToolMenu()
        {
            if (_toolMenu == null || !IsOpen) return;
            _toolMenuOpen = true;
            _toolMenu.gameObject.SetActive(true);
            _toolMenu.SetAsLastSibling();
            // 先激活再排版：LayoutRebuilder 对 inactive 父节点不保证算出 content 高度。
            RefreshToolMenu();
        }

        private void HideToolMenu()
        {
            _toolMenuOpen = false;
            if (_toolMenu != null) _toolMenu.gameObject.SetActive(false);
        }

        private void RefreshToolMenu()
        {
            if (_toolContent == null || _toolRowTemplate == null) return;

            _toolVisible.Clear();
            for (int i = 0; i < _toolCatalog.Count; i++)
            {
                var item = _toolCatalog[i];
                if (item.Matches(_toolFilter)) _toolVisible.Add(item);
            }

            int count = _toolVisible.Count;
            EnsureToolRows(count);
            for (int i = 0; i < _toolRows.Count; i++)
            {
                var row = _toolRows[i];
                bool active = i < count;
                row.Go.SetActive(active);
                if (!active) continue;

                var item = _toolVisible[i];
                if (row.Label != null) row.Label.text = item.Label ?? ToolNames.Label(item.Name);
                if (row.Command != null) row.Command.text = "/" + item.Name;
                if (row.Description != null)
                {
                    row.Description.text = string.IsNullOrEmpty(item.Description)
                        ? item.Name
                        : item.Description;
                }

                var name = item.Name;
                row.Button.onClick.RemoveAllListeners();
                row.Button.onClick.AddListener(() => SelectTool(name));
            }

            if (_toolSelected >= count) _toolSelected = Mathf.Max(0, count - 1);
            if (count == 0) _toolSelected = -1;

            if (_toolMenuEmpty != null)
            {
                _toolMenuEmpty.text = !_toolCatalogLoaded
                    ? Strings.Get("tool.menu.loading")
                    : Strings.Get("tool.menu.empty");
                _toolMenuEmpty.gameObject.SetActive(count == 0);
            }
            if (_toolScrollbar != null) _toolScrollbar.SetActive(count > 1);

            // 行是运行期克隆的；先强制排一次版，接着算滚动位置才有正确的 content 高度。
            if (_toolContent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_toolContent);
            UpdateToolSelection(scrollToSelected: true);
        }

        private void EnsureToolRows(int count)
        {
            while (_toolRows.Count < count)
            {
                var go = UnityEngine.Object.Instantiate(_toolRowTemplate.gameObject, _toolContent);
                go.name = "tool-row";
                go.SetActive(true);
                var rt = go.transform as RectTransform;
                if (rt != null) rt.localScale = Vector3.one;

                var view = new ToolRowView
                {
                    Go = go,
                    Button = go.GetComponent<Button>(),
                    Bg = go.transform.Find("bg")?.GetComponent<Image>(),
                    Label = go.transform.Find("label")?.GetComponent<Text>(),
                    Command = go.transform.Find("command")?.GetComponent<Text>(),
                    Description = go.transform.Find("description")?.GetComponent<Text>(),
                };
                if (view.Button == null)
                {
                    view.Button = go.AddComponent<Button>();
                    view.Button.transition = Selectable.Transition.ColorTint;
                    if (view.Bg != null) view.Button.targetGraphic = view.Bg;
                }
                _toolRows.Add(view);
            }
        }

        private void UpdateToolSelection(bool scrollToSelected)
        {
            for (int i = 0; i < _toolRows.Count; i++)
            {
                var row = _toolRows[i];
                if (row.Button == null) continue;
                var colors = row.Button.colors;
                colors.fadeDuration = 0.06f;
                colors.highlightedColor = new Color(0.353f, 0.749f, 1f, 0.28f);
                colors.pressedColor = new Color(0.353f, 0.749f, 1f, 0.38f);
                colors.selectedColor = new Color(0.353f, 0.749f, 1f, 0.22f);
                colors.normalColor = i == _toolSelected
                    ? new Color(0.353f, 0.749f, 1f, 0.22f)
                    : new Color(1f, 1f, 1f, 0.015f);
                row.Button.colors = colors;
            }

            if (scrollToSelected && _toolScroll != null && _toolVisible.Count > 0 && _toolSelected >= 0)
            {
                float t = _toolVisible.Count <= 1
                    ? 1f
                    : 1f - (float)_toolSelected / (_toolVisible.Count - 1);
                _toolScroll.verticalNormalizedPosition = Mathf.Clamp01(t);
            }
        }

        private void MoveToolSelection(int delta)
        {
            if (!_toolMenuOpen || _toolVisible.Count == 0) return;
            _toolSelected = (_toolSelected + delta + _toolVisible.Count) % _toolVisible.Count;
            UpdateToolSelection(scrollToSelected: true);
        }

        private void SelectHighlightedTool()
        {
            if (_toolSelected >= 0 && _toolSelected < _toolVisible.Count)
                SelectTool(_toolVisible[_toolSelected].Name);
        }

        private void SelectTool(string name)
        {
            if (_input == null || string.IsNullOrEmpty(name)) return;

            var command = "/" + name + " ";
            var text = _input.text ?? string.Empty;
            string next;
            if (TryLeadingSlashToken(text, out _, out var end))
            {
                var rest = end < text.Length ? text.Substring(end).TrimStart() : string.Empty;
                next = command + rest;
            }
            else
            {
                next = command + text.TrimStart();
            }

            SetInputText(next, next.Length);
            HideToolMenu();
            FocusInput();
            Plugin.Log.LogInfo($"[工具] 已插入命令 {command.TrimEnd()}（{Strings.Get("tool.menu.required", ToolNames.Label(name))}）");
        }

        private void SetInputText(string value, int caret)
        {
            if (_input == null) return;
            _suppressToolMenuEvents = true;
            try
            {
                _input.text = value ?? string.Empty;
                int pos = Mathf.Clamp(caret, 0, _input.text != null ? _input.text.Length : 0);
                _input.caretPosition = pos;
                _input.selectionAnchorPosition = pos;
                _input.selectionFocusPosition = pos;
                try { _input.CalculateColorText(true); } catch { /* 旧版输入框没有此方法 */ }
                _input.ForceLabelUpdate();
            }
            finally
            {
                _suppressToolMenuEvents = false;
            }
        }

        /// <summary>选择器弹窗关掉后把键盘焦点还给输入框。
        ///
        /// 注意：不要用 FocusInput()/ActivateInputField()：那会重排光标，
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

        /// <summary>记录输入框仍聚焦时的选区（见 _lastSelectionAnchor 的注释）。</summary>
        private void SnapshotInputSelection()
        {
            if (_input == null || !_input.isFocused) return;
            _lastSelectionAnchor = _input.selectionAnchorPosition;
            _lastSelectionFocus = _input.selectionFocusPosition;
            _hasSelectionSnapshot = true;
        }

        /// <summary>
        /// 把点击工具条前的选区写回 InputField。
        ///
        /// 必须在 `InsertGeneralString` / `InsertCheckBox` / `OnColorChange` 之前调用：
        /// 工具条按钮带 Unity `Button`（Selectable），点击会把输入框顶掉，
        /// `DeactivateInputField()` 随即把内部 caret 清成 0。
        /// </summary>
        private void RestoreInputSelection()
        {
            if (_input == null || !_hasSelectionSnapshot) return;
            _input.selectionAnchorPosition = _lastSelectionAnchor;
            _input.selectionFocusPosition = _lastSelectionFocus;
        }

        private void OnInsertCheckBoxClicked()
        {
            if (_input == null) return;
            try
            {
                // 原生就是这一句：插入 `[-]`（Text 会画成复选框）
                var before = _input.text ?? string.Empty;
                RestoreInputSelection();
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
                HideToolMenu();
                // 先把自己画布抬到聊天窗之上，否则选择器会整块藏在窗口后面
                BoostPickerAboveWindow(pickerTrans, "图标");
                var pos = PickerPositionAboveInput(pickerTrans);

                UISignalTagPicker.Popup(pos, signalId =>
                {
                    if (signalId == 0 || _input == null) return;
                    var tag = LDB.signals.IconTag(signalId);
                    if (string.IsNullOrEmpty(tag)) return;
                    // isIconTag=true：插入 `\图标标签;`，Text 靠 iconMapping 把它画成图标
                    var before = _input.text ?? string.Empty;
                    RestoreInputSelection();
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
                HideToolMenu();
                BoostPickerAboveWindow(pickerTrans, "颜色");
                var pos = PickerPositionAboveInput(pickerTrans);

                UISimpleColorPicker.Popup(pos, GetBrushColor(),
                    idx => { if (_input != null) { RestoreInputSelection(); _input.OnColorChange(idx); RestoreInputFocus(); } },
                    alpha => { if (_input != null) { RestoreInputSelection(); _input.OnColorChange(alpha, true); RestoreInputFocus(); } });

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
        /// 图标/取色弹窗的位置：和工具列表**同一个锚点**——输入框上沿、左对齐。
        ///
        /// 原生 `UISignalTagPicker` / `UISimpleColorPicker` 的 `Popup(pos)` 是把
        /// `pickerTrans.anchoredPosition` 直接设成 `pos`；它们的 pivot 是左上角，
        /// 父节点是游戏的 `Common Tools`（全屏、pivot(0,0)）。
        /// 这里把「工具列表左下角」的世界坐标换算到 `Common Tools` 的局部坐标，
        /// 再按 picker 自己的 pivot 把左下角贴到该锚点（等价于弹窗从输入框上沿向上展开）。
        ///
        /// 不再按按钮位置“左/上”摆放：图标按钮在输入栏左下角，按原生算法弹窗会跑到
        /// 屏幕左侧、和工具列表不在一个位置。
        /// </summary>
        private Vector2 PickerPositionAboveInput(RectTransform pickerTrans)
        {
            var fallback = new Vector2(-206f, 194f);
            if (pickerTrans == null) return fallback;

            try
            {
                var parent = pickerTrans.parent as RectTransform;
                if (parent == null || parent.rect.width <= 0f || parent.rect.height <= 0f)
                    return fallback;
                if (!TryGetToolMenuAnchorWorld(out var anchorWorld)) return fallback;

                // `InverseTransformPoint` 得到的是以父级 pivot 为原点的局部坐标，
                // 而 `anchoredPosition` 相对父级中心；补上这段偏移。
                var centerOffset = new Vector2(
                    (0.5f - parent.pivot.x) * parent.rect.width,
                    (0.5f - parent.pivot.y) * parent.rect.height);
                var local = (Vector2)parent.InverseTransformPoint(anchorWorld) - centerOffset;

                var pickerScale = pickerTrans.localScale;
                var pickerW = pickerTrans.rect.width * Mathf.Abs(pickerScale.x);
                var pickerH = pickerTrans.rect.height * Mathf.Abs(pickerScale.y);
                if (pickerW <= 0f || pickerH <= 0f) return fallback;
                var pivot = pickerTrans.pivot;

                // 把 picker 的左下角贴到锚点；pivot 通常是 (0,1)。
                var pos = new Vector2(
                    local.x + pickerW * pivot.x,
                    local.y + pickerH * pivot.y);
                return ClampPickerToParent(pickerTrans, parent, pos);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// 标题输入框的图标弹窗位置：贴住标题输入框左下角、向下展开。
        /// 标题在窗口顶部，按工具条那样向上弹会出屏。
        /// </summary>
        private Vector2 PickerPositionBelowTitleInput()
        {
            var fallback = new Vector2(-206f, 194f);
            var pickerTrans = UISignalTagPicker._PickerTrans;
            BoostPickerAboveWindow(pickerTrans, "标题图标");
            var anchorRt = _conversationTitleInput != null
                ? _conversationTitleInput.transform as RectTransform
                : null;
            if (pickerTrans == null || anchorRt == null) return fallback;

            try
            {
                var parent = pickerTrans.parent as RectTransform;
                if (parent == null || parent.rect.width <= 0f || parent.rect.height <= 0f)
                    return fallback;

                var corners = new Vector3[4];
                anchorRt.GetWorldCorners(corners); // 0=左下 1=左上 2=右上 3=右下
                var centerOffset = new Vector2(
                    (0.5f - parent.pivot.x) * parent.rect.width,
                    (0.5f - parent.pivot.y) * parent.rect.height);
                var local = (Vector2)parent.InverseTransformPoint(corners[0]) - centerOffset;

                var pickerW = pickerTrans.rect.width * Mathf.Abs(pickerTrans.localScale.x);
                var pickerH = pickerTrans.rect.height * Mathf.Abs(pickerTrans.localScale.y);
                if (pickerW <= 0f || pickerH <= 0f) return fallback;
                var pivot = pickerTrans.pivot;

                // pivot 通常是 (0,1)：让弹窗左上角贴在标题输入框左下角。
                var pos = new Vector2(
                    local.x + pickerW * pivot.x,
                    local.y - pickerH * (1f - pivot.y));
                return ClampPickerToParent(pickerTrans, parent, pos);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>把 picker 的 anchoredPosition 夹在父级可见区域内，避免出屏。</summary>
        private static Vector2 ClampPickerToParent(
            RectTransform pickerTrans,
            RectTransform parent,
            Vector2 pos)
        {
            var pickerW = pickerTrans.rect.width * Mathf.Abs(pickerTrans.localScale.x);
            var pickerH = pickerTrans.rect.height * Mathf.Abs(pickerTrans.localScale.y);
            if (pickerW <= 0f || pickerH <= 0f) return pos;

            var pivot = pickerTrans.pivot;
            var halfW = parent.rect.width * 0.5f;
            var halfH = parent.rect.height * 0.5f;
            var xMin = -halfW + 8f + pickerW * pivot.x;
            var xMax = halfW - 8f - pickerW * (1f - pivot.x);
            var yMin = -halfH + 8f + pickerH * pivot.y;
            var yMax = halfH - 8f - pickerH * (1f - pivot.y);
            if (xMin > xMax) xMin = xMax;
            if (yMin > yMax) yMin = yMax;

            pos.x = Mathf.Clamp(pos.x, xMin, xMax);
            pos.y = Mathf.Clamp(pos.y, yMin, yMax);
            return new Vector2(Mathf.Round(pos.x), Mathf.Round(pos.y));
        }

        /// <summary>
        /// 工具列表的锚点（世界坐标）：`tool-menu` 的左下角。
        /// 没有工具列表时退回输入框左上角（两者在 prefab 里本就对齐）。
        /// </summary>
        private bool TryGetToolMenuAnchorWorld(out Vector3 world)
        {
            world = Vector3.zero;

            if (_toolMenu != null && _toolMenu.rect.width > 0f && _toolMenu.rect.height > 0f)
            {
                // pivot(0,0) 的 RectTransform：局部 (0,0) 就是左下角。
                world = _toolMenu.TransformPoint(new Vector3(
                    -_toolMenu.pivot.x * _toolMenu.rect.width,
                    -_toolMenu.pivot.y * _toolMenu.rect.height,
                    0f));
                return true;
            }

            var inputRt = _input != null ? _input.transform as RectTransform : null;
            if (inputRt == null) return false;

            // 输入框 pivot 是 (0.5,0)：局部左上角 = (-w/2, +h)。
            world = inputRt.TransformPoint(new Vector3(
                -inputRt.pivot.x * inputRt.rect.width,
                (1f - inputRt.pivot.y) * inputRt.rect.height,
                0f));
            return true;
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

            if (_toolMenuOpen)
            {
                if (_toolVisible.Count > 0)
                {
                    SelectHighlightedTool();
                    return;
                }
                HideToolMenu();
            }

            var text = value.Trim();
            HideToolMenu();
            if (_input != null)
            {
                _input.text = string.Empty;
                _draftInput = null;
                _draftRestoreArmed = false;
                // 发送后主动失焦：玩家读完回复可直接按 R 关窗，
                // 想继续追问再按 Enter 即可。
                _input.DeactivateInputField();
            }

            try
            {
                Submitted?.Invoke(text);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"提交失败：{ex}");
            }
        }

        /// <summary>
        /// 把模型输出编译成游戏 Text 的标记。
        ///
        /// 具体语法见 docs/rich-text-output-plan.md：
        ///   - Markdown 作为外层结构；
        ///   - 游戏样式用内联 HTML 子集（color/size/tint/box/sqrt/sup/sub/i/a/icon）；
        ///   - 裸游戏标签作为兼容输入；
        ///   - 未知 HTML/尖括号一律转义。
        ///
        /// 图标短码 `\xxx;` 仍交给 <see cref="SanitizeIconTags"/> 按当前图标表校验，
        /// 因为 formatter 是 Unity-free 的，不知道这一局加载了哪张 iconMapping。
        /// </summary>
        private static RichTextResult FormatRichText(
            string s,
            RichTextOptions options,
            RichTextProfile profile,
            RichTextLinkAllocator linkAllocator = null,
            RichTextLinkResolvers linkResolvers = null)
        {
            var result = GameRichText.Format(s, profile, options, linkAllocator, linkResolvers);
            result.Rich = SanitizeIconTags(result.Rich);
            return result;
        }

        private static string FormatInline(
            string s,
            RichTextLinkAllocator linkAllocator = null,
            List<RichTextLink> links = null)
        {
            return FormatInline(s, linkAllocator, links, null).Rich;
        }

        private static (string Rich, bool HasGameLinks) FormatInline(
            string s,
            RichTextLinkAllocator linkAllocator,
            List<RichTextLink> links,
            RichTextLinkResolvers linkResolvers)
        {
            var result = FormatRichText(
                s, RichOptions14, RichTextProfile.Inline, linkAllocator, linkResolvers);
            if (links != null) links.AddRange(result.Links);
            return (result.Rich, result.HasGameLinks);
        }

        /// <summary>复制按钮用的纯文本：去掉 Markdown/HTML 标记，保留正文与代码。</summary>
        private static string ToPlainText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return GameRichText.Format(raw, RichTextProfile.Body, RichOptions14).Plain;
        }

        /// <summary>
        /// 格式化并直接写进 Text。复选框只在真正包含任务列表时开启，
        /// 避免普通文本里的 `[-]` 被游戏画成勾选框。
        /// </summary>
        private RichTextResult ApplyRichText(
            Text label,
            string s,
            RichTextOptions options,
            RichTextProfile profile,
            RichTextLinkAllocator linkAllocator = null)
        {
            var allocator = linkAllocator ?? new RichTextLinkAllocator();
            var result = FormatRichText(s, options, profile, allocator, LinkResolvers);
            if (label != null)
            {
                if (!label.supportRichText) label.supportRichText = true;
                label.text = result.Rich;
                if (label.useCheckBox != result.HasCheckBoxes)
                    label.useCheckBox = result.HasCheckBoxes;
                RegisterLinkTargets(label, result.Links, result.HasGameLinks);
            }
            return result;
        }

        /// <summary>
        /// 玩家消息不解析 Markdown/HTML：只转义尖括号，保留 `*`、`#`、`-` 等原字符。
        /// </summary>
        private void ApplyPlainText(Text label, string s)
        {
            if (label == null) return;
            ReleaseLinkTargets(label);
            var text = GameRichText.Format(s, RichTextProfile.Plain, RichOptions14).Rich;
            label.text = SanitizeIconTags(text);
            label.raycastTarget = false;
            if (label.useCheckBox) label.useCheckBox = false;
        }

        /// <summary>把一个 Text 的网页/科技/配方/成就链接映射挂到它的 onTriggerClick 上。</summary>
        private void RegisterLinkTargets(Text label, List<RichTextLink> links, bool hasGameLinks)
        {
            if (label == null) return;

            var map = new Dictionary<int, string>();
            if (links != null)
            {
                for (var i = 0; i < links.Count; i++)
                {
                    var link = links[i];
                    if (link == null ||
                        (!RichTextLinkAllocator.IsWebId(link.Id) && !RichTextLinkAllocator.IsEntityLinkId(link.Id))) continue;
                    if (string.IsNullOrEmpty(link.Url)) continue;
                    map[link.Id] = link.Url;
                }
            }

            if (_linkHandlers.TryGetValue(label, out var oldHandler))
            {
                label.onTriggerClick -= oldHandler;
                _linkHandlers.Remove(label);
            }

            if (map.Count == 0 && !hasGameLinks)
            {
                _linkTargets.Remove(label);
                label.raycastTarget = false;
                return;
            }

            _linkTargets[label] = map;
            label.raycastTarget = true;

            Action<UnityEngine.EventSystems.PointerEventData,
                UnityEngine.UI.Youthcat.TextTriggerBounds> handler =
                (data, bounds) => OnRichLinkClicked(label, data, bounds);
            label.onTriggerClick += handler;
            _linkHandlers[label] = handler;
        }

        private void ReleaseLinkTargets(Text label)
        {
            if (label == null) return;
            if (_linkHandlers.TryGetValue(label, out var handler))
            {
                label.onTriggerClick -= handler;
                _linkHandlers.Remove(label);
            }
            _linkTargets.Remove(label);
        }

        private void ReleaseLinksUnder(GameObject root)
        {
            if (root == null) return;
            var rootTransform = root.transform;
            var dead = new HashSet<Text>();
            foreach (var pair in _linkTargets)
                if (pair.Key != null && pair.Key.transform.IsChildOf(rootTransform)) dead.Add(pair.Key);
            foreach (var pair in _linkHandlers)
                if (pair.Key != null && pair.Key.transform.IsChildOf(rootTransform)) dead.Add(pair.Key);
            foreach (var text in dead) ReleaseLinkTargets(text);
        }

        private void OnRichLinkClicked(
            Text label,
            UnityEngine.EventSystems.PointerEventData data,
            UnityEngine.UI.Youthcat.TextTriggerBounds bounds)
        {
            if (data.button != UnityEngine.EventSystems.PointerEventData.InputButton.Left) return;
            if (bounds.type != UnityEngine.UI.Youthcat.TextTriggerBounds.LINK_TYPE) return;

            if (RichTextLinkAllocator.IsTechLinkId(bounds.linkId))
            {
                OpenTechLink(bounds.linkId);
                return;
            }

            if (RichTextLinkAllocator.IsRecipeLinkId(bounds.linkId))
            {
                OpenRecipeLink(bounds.linkId);
                return;
            }

            if (RichTextLinkAllocator.IsAchievementLinkId(bounds.linkId))
            {
                OpenAchievementLink(bounds.linkId);
                return;
            }

            if (RichTextLinkAllocator.IsEntityLinkId(bounds.linkId))
            {
                if (!_linkTargets.TryGetValue(label, out var entityMap)) return;
                if (!entityMap.TryGetValue(bounds.linkId, out var entityUrl) || string.IsNullOrEmpty(entityUrl)) return;
                OpenEntityLink(entityUrl);
                return;
            }

            if (!RichTextLinkAllocator.IsWebId(bounds.linkId)) return;
            if (!_linkTargets.TryGetValue(label, out var map)) return;
            if (!map.TryGetValue(bounds.linkId, out var url) || string.IsNullOrEmpty(url)) return;

            // formatter 只放行 http/https，这里再校验一次；绝不把 file:/javascript: 交给系统。
            if (!GameRichText.IsHttpUrl(url)) return;

            try
            {
                Application.OpenURL(url);
                SetStatus(Strings.Get("reply.link.opening"), _accent);
            }
            catch
            {
                GUIUtility.systemCopyBuffer = url;
                SetStatus(Strings.Get("reply.link.open_failed"), _accent);
            }
        }

        /// <summary>聊天里的实体链接：建筑/星球；复用卡片整卡聚焦的同一套游戏相机。</summary>
        private void OpenEntityLink(string url)
        {
            if (!GameRichText.TryParseEntityUrl(url, out var canonical)) return;
            var parts = canonical.Substring("dsp://entity/".Length).Split('/');
            if (parts.Length == 2 && parts[0] == "planet" &&
                int.TryParse(parts[1], out var planetId))
            {
                GameLinkFocus.Focus(new GameTether.Target { Kind = "planet", PlanetId = planetId, TargetId = planetId });
                return;
            }
            if (parts.Length == 3 && (parts[0] == "building" || parts[0] == "vein") &&
                int.TryParse(parts[1], out var pId) && int.TryParse(parts[2], out var targetId))
            {
                GameLinkFocus.Focus(new GameTether.Target
                {
                    Kind = parts[0],
                    PlanetId = pId,
                    TargetId = targetId,
                });
            }
        }

        /// <summary>
        /// 科技链接：1000–9999 直接作为科技 ID；使用游戏自己的
        /// OpenTechTree + SelectTech，后者会选中、居中并展开节点。
        /// </summary>
        private void OpenTechLink(int linkId)
        {
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame == null || uiGame.techTree == null || !uiGame.techTree.inited) return;

            var techId = linkId;

            if (techId > 0 && LDB.techs != null)
            {
                var proto = LDB.techs.Select(techId);
                if (proto == null)
                {
                    techId = 0;
                }
                else if (proto.IsHiddenTech && GameMain.history != null &&
                         !GameMain.history.TechUnlocked(proto.ID))
                {
                    return;
                }
            }

            SetOpen(false);
            if (!uiGame.techTree.active) uiGame.OpenTechTree();
            if (techId > 0) uiGame.techTree.SelectTech(techId);
        }

        /// <summary>配方链接：10000 + 物品 ID；打开合成器并选中该物品的主配方。</summary>
        private void OpenRecipeLink(int linkId)
        {
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame == null || LDB.items == null) return;

            var itemId = linkId - 10000;
            var item = LDB.items.Select(itemId);
            if (item == null || item.maincraft == null) return;

            SetOpen(false);
            uiGame.FocusOnReplicate(itemId);
        }

        /// <summary>成就链接：20000 + 成就 ID；打开成就页并滚动/高亮对应条目。</summary>
        private void OpenAchievementLink(int linkId)
        {
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame == null) return;

            var statWindow = uiGame.statWindow;
            if (statWindow == null || !statWindow.inited) return;
            var panel = statWindow.achievementPanelUI;
            if (panel == null) return;

            var achId = linkId - 20000;
            if (panel.uiEntries == null || !panel.uiEntries.ContainsKey(achId)) return;

            SetOpen(false);
            panel.HighlightAchievement(achId);
        }

        /// <summary>
        /// 渲染助手正文。没有表格时走单 Text；有表格时把正文切成
        /// “文本段 → 表格 Text → 文本段 …”，表格用游戏 Text 自带的
        /// tableColWidths/tableRowHeights 排版，而不是拿空格画表。
        /// </summary>
        private void RenderReplyBody(ReplyView reply, string raw)
        {
            if (reply == null || reply.Body == null) return;

            ClearBodyExtras(reply);
            var segments = GameRichText.SplitTables(raw ?? string.Empty);
            var hasTable = false;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].IsTable) { hasTable = true; break; }
            }

            if (!hasTable)
            {
                reply.Body.gameObject.SetActive(true);
                ApplyRichText(reply.Body, raw, RichOptions14, RichTextProfile.Body);
                return;
            }

            var insert = reply.Body.transform.GetSiblingIndex() + 1;
            var icons = TryGetPreferredIconMapping();
            var firstText = true;
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.IsTable)
                {
                    if (firstText)
                    {
                        reply.Body.text = string.Empty;
                        ReleaseLinkTargets(reply.Body);
                        reply.Body.gameObject.SetActive(false);
                        firstText = false;
                    }

                    var table = CreateTableText(reply, segment.Table);
                    if (table == null) continue;
                    AssignIconMapping(table, icons);
                    table.transform.SetSiblingIndex(insert++);
                    reply.BodyExtras.Add(table.gameObject);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(segment.Text)) continue;

                Text label;
                if (firstText)
                {
                    label = reply.Body;
                    reply.Body.gameObject.SetActive(true);
                    firstText = false;
                }
                else
                {
                    label = CreateBodyTextClone(reply);
                    if (label == null) continue;
                    AssignIconMapping(label, icons);
                    label.transform.SetSiblingIndex(insert++);
                    reply.BodyExtras.Add(label.gameObject);
                }

                ApplyRichText(label, segment.Text, RichOptions14, RichTextProfile.Body);
            }

            if (firstText)
            {
                reply.Body.text = string.Empty;
                ReleaseLinkTargets(reply.Body);
                reply.Body.gameObject.SetActive(false);
            }
        }

        private void ClearBodyExtras(ReplyView reply)
        {
            if (reply == null) return;
            for (var i = 0; i < reply.BodyExtras.Count; i++)
            {
                var go = reply.BodyExtras[i];
                if (go == null) continue;
                var text = go.GetComponent<Text>();
                if (text != null) ReleaseLinkTargets(text);
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
            }
            reply.BodyExtras.Clear();
            if (reply.Body != null)
            {
                ReleaseLinkTargets(reply.Body);
                reply.Body.raycastTarget = false;
                reply.Body.gameObject.SetActive(true);
            }
        }

        private static Text CreateBodyTextClone(ReplyView reply)
        {
            if (reply == null || reply.Body == null) return null;

            var go = UnityEngine.Object.Instantiate(reply.Body.gameObject, reply.Body.transform.parent);
            go.name = "body-text";
            go.SetActive(true);
            var text = go.GetComponent<Text>();
            if (text == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            text.supportRichText = true;
            text.useCheckBox = false;
            text.raycastTarget = false;
            text.tableColWidths = new float[0];
            text.tableRowHeights = new float[0];
            return text;
        }

        private Text CreateTableText(ReplyView reply, RichTextTable table)
        {
            if (reply == null || reply.Body == null || table == null) return null;

            var columns = Math.Min(4, table.Headers.Count);
            if (columns <= 0) return null;

            var go = UnityEngine.Object.Instantiate(reply.Body.gameObject, reply.Body.transform.parent);
            go.name = "body-table";
            go.SetActive(true);
            var text = go.GetComponent<Text>();
            if (text == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }

            text.supportRichText = true;
            text.useCheckBox = false;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var width = reply.Body.rectTransform.rect.width;
            if (width < 200f && _content != null && _content.rect.width > 200f)
                width = _content.rect.width - 24f;
            if (width < 200f) width = 700f;

            var padding = new Vector4(6f, 6f, 3f, 3f);
            var usable = Math.Max(240f, width - 12f);
            var colWidth = Math.Max(48f, (usable - (padding.x + padding.z) * columns) / columns);

            text.tableColWidths = new float[columns];
            for (var i = 0; i < columns; i++) text.tableColWidths[i] = colWidth;

            var rowCount = Math.Min(40, table.Rows.Count);
            var truncatedRows = table.Rows.Count > rowCount ? 1 : 0;
            text.tableRowHeights = new float[rowCount + 1 + truncatedRows];
            for (var i = 0; i < text.tableRowHeights.Length; i++) text.tableRowHeights[i] = -1f;

            text.tableColAlignments = new Text.HorzAlignment[columns];
            for (var i = 0; i < columns; i++)
            {
                var align = i < table.Alignments.Count ? table.Alignments[i] : RichTextTableAlign.Left;
                text.tableColAlignments[i] = align == RichTextTableAlign.Center
                    ? Text.HorzAlignment.Center
                    : align == RichTextTableAlign.Right
                        ? Text.HorzAlignment.Right
                        : Text.HorzAlignment.Left;
            }

            text.tableRowAlignments = new Text.VertAlignment[rowCount + 1 + truncatedRows];
            for (var i = 0; i < text.tableRowAlignments.Length; i++)
                text.tableRowAlignments[i] = Text.VertAlignment.Middle;

            text.tableCellPadding = padding;
            text.tableLineColor = new Color(1f, 1f, 1f, 0.28f);
            text.tableLineTint = true;

            var allocator = new RichTextLinkAllocator();
            var links = new List<RichTextLink>();
            var hasGameLinks = false;
            var sb = new StringBuilder();
            hasGameLinks |= AppendTableRow(sb, table.Headers, columns, header: true, allocator, links);
            for (var i = 0; i < rowCount; i++)
                hasGameLinks |= AppendTableRow(sb, table.Rows[i], columns, header: false, allocator, links);
            if (table.Rows.Count > rowCount)
            {
                var truncated = new List<string>(columns);
                for (var i = 0; i < columns; i++) truncated.Add(i == 0 ? "…" : string.Empty);
                hasGameLinks |= AppendTableRow(sb, truncated, columns, header: false, allocator, links);
            }
            text.text = sb.ToString().TrimEnd('\n');
            RegisterLinkTargets(text, links, hasGameLinks);
            return text;
        }

        private static bool AppendTableRow(
            StringBuilder sb,
            List<string> cells,
            int columns,
            bool header,
            RichTextLinkAllocator allocator,
            List<RichTextLink> links)
        {
            var hasGameLinks = false;
            for (var i = 0; i < columns; i++)
            {
                if (i > 0) sb.Append('\t');
                var cell = cells != null && i < cells.Count ? cells[i] : string.Empty;
                var (formatted, cellHasGameLinks) = FormatTableCell(cell, header, allocator, links);
                sb.Append(formatted);
                hasGameLinks |= cellHasGameLinks;
            }
            sb.Append('\n');
            return hasGameLinks;
        }

        private static (string Text, bool HasGameLinks) FormatTableCell(
            string cell,
            bool header,
            RichTextLinkAllocator allocator,
            List<RichTextLink> links)
        {
            var (formatted, hasGameLinks) = FormatInline(
                cell ?? string.Empty, allocator, links, LinkResolvers);
            if (header && formatted.Length > 0)
                formatted = "<color=#FFFFFF>" + formatted + "</color>";
            return (formatted
                .Replace("\r", string.Empty)
                .Replace("\n", "↲")
                .Replace("\t", "↹"), hasGameLinks);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // 只处理会破坏 <color>/<b> 标签的尖括号；中括号等无需处理。
            return s.IndexOf('<') < 0 ? s : s.Replace("<", "<\u200B");
        }
    }

    /// <summary>
    /// 思考块正文的裁切框（prefab 里的 `thinking-block/body-clip`）。
    ///
    /// 正文按底边对齐：内容超过上限时新行从下往上顶，旧内容从框顶被裁掉，
    /// 视觉上只剩最新几行。不加滚动条，也不能滚。
    ///
    /// 只在文字长度、框宽或高度上限变化时重新测量：长文本的 preferredHeight
    /// 每次都要重排一遍，不能每帧算。
    /// </summary>
    internal sealed class ThinkingBodyClip : MonoBehaviour
    {
        /// <summary>裁切框里的正文。它在 prefab 里按底边对齐，高度由 ContentSizeFitter 撑开。</summary>
        public Text Body;

        /// <summary>高度上限；&lt;= 0 表示不限制（收尾后的块、历史块）。</summary>
        public float MaxHeight;

        /// <summary>
        /// 流式期间高度只增不减：渲染的只是尾部窗口，窗口每滑动一次换行数就可能变，
        /// 不钉住时块高会一帧一变地抖。
        /// </summary>
        public bool GrowOnly;

        private LayoutElement _layout;
        private RectTransform _rect;
        private int _textLength = -1;
        private float _width = -1f;
        private float _maxHeight = -1f;
        private float _height;
        private bool _wasActive;

        private void Awake()
        {
            _layout = GetComponent<LayoutElement>();
            _rect = transform as RectTransform;
        }

        /// <summary>开始新一段流式思考：清掉上一段钉住的高度，逼下一帧重新测量。</summary>
        public void Restart()
        {
            _height = 0f;
            _textLength = -1;
            _wasActive = false;
        }

        private void LateUpdate()
        {
            if (Body == null || _rect == null || _layout == null) return;
            if (!isActiveAndEnabled || !Body.gameObject.activeInHierarchy)
            {
                _wasActive = false;
                return;
            }

            var width = _rect.rect.width;
            var becameActive = !_wasActive;
            if (!becameActive
                && Body.text.Length == _textLength
                && Mathf.Approximately(width, _width)
                && Mathf.Approximately(MaxHeight, _maxHeight))
                return;

            _wasActive = true;
            // 刚显示的块要等这一帧排版才有真实宽度，宽度没稳定前不量高。
            if (width <= 1f) return;

            var height = Body.preferredHeight;
            if (GrowOnly && height < _height) height = _height;
            if (MaxHeight > 0f && height > MaxHeight) height = MaxHeight;

            _textLength = Body.text.Length;
            _width = width;
            _maxHeight = MaxHeight;
            if (Mathf.Approximately(_height, height)) return;
            _height = height;
            _layout.preferredHeight = height;
        }
    }

    /// <summary>
    /// 右栏会话标题的显示层。输入框里保留完整标题；未编辑时 UIInputField 会把
    /// 文本同步到 overText，这里在 LateUpdate 再按宽度补省略号，避免长标题溢出。
    /// 编辑时跳过，交给 InputField 自己显示完整文本和光标。
    /// </summary>
    internal sealed class ConversationTitleDisplay : MonoBehaviour
    {
        public InputField Input;
        public Text Over;

        private void LateUpdate()
        {
            RefreshNow();
        }

        public void RefreshNow()
        {
            if (Input == null || Over == null || Input.isFocused) return;
            var title = Input.text ?? string.Empty;
            var display = title.Length == 0 ? Strings.Get("chat.index.title_placeholder") : ChatWindow.FitLabel(title, 16, 210f);
            if (Over.text != display) Over.text = display;
        }
    }

}
