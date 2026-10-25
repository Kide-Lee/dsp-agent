using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 给手工维护的窗口 prefab 里的 `output` 节点补一套**滚动区**（聊天正文 / 对话串），
    /// 并在 content 里放几条**示例消息**，让样式在编辑器里直接看得见。
    ///
    /// 结构对齐输入区 `input/editor/scroll-view`（取值见 `IcarusIIWindowPrefabBuilder.BuildInputScrollView`），
    /// 只是内容区换成"纵向堆叠、随内容长高"的形式：
    ///
    ///   output
    ///   └── scroll-view              ScrollRect + 全透明命中层（无背景色块）
    ///       ├── viewport             Mask(Show Mask Graphic=false) + Image(内置 UIMask)
    ///       │   └── content          VerticalLayoutGroup + ContentSizeFitter(纵向 PreferredSize)
    ///       │       ├── example-assistant-1   裸文本，无边框（助手消息样式）
    ///       │       ├── example-user-1        ┐ 气泡：bg + border 两层 + footer（时间/复制）
    ///       │       ├── example-assistant-2   │ 由 VerticalLayoutGroup 整行右对齐
    ///       │       └── example-user-2        ┘
    ///       └── Scrollbar Vertical   6 宽，Handle 上下各 3 留白
    ///
    /// ## 消息样式
    ///
    /// **助手消息**：直接一个 `Text`，无任何背景/边框——正文就该是"浮"在面板上的。
    ///
    /// **用户消息**：复刻游戏里的圆角提示气泡（对照 `item-tooltip` 的底/边组合）：
    ///   * `bg`      Image `GUIPanel` Sliced `#09141BF7`（圆角底，游戏 tip 统一底色）
    ///   * `border`  Image `GUIFrame_thin` Sliced `#A7E9FF40` + 材质 `widget-alpha-5x`
    ///               （1px 圆角描边；着色器 `UI Ex/Widget Alpha`）
    /// 这些层都挂 `LayoutElement.ignoreLayout = true`，不参与 VerticalLayoutGroup 的排版，
    /// 只按锚点拉伸/定位；否则会被当成"条目"堆进文本前面。
    ///
    /// 气泡宽度由 `bubble` 上的 `LayoutElement.preferredWidth` 封顶，文本在里面换行；
    /// 不设则气泡收缩到刚好包住文本。运行期换文案时按需要给 preferredWidth 即可。
    /// **无尾巴**：只要圆角矩形，不做指向性尖角。
    ///
    /// ## 两个必须注意的点
    ///
    /// 1. **Text 必须给字体**。字体只作占位（运行期由 mod 换成游戏当前语言那个），
    ///    但**不能留空**：UGUI 改写版 TextGenerator 在 `font == null` 时算 `preferredHeight`
    ///    会抛 NullReferenceException，而 content 上挂着 ContentSizeFitter，一进编辑器就刷屏。
    /// 2. **运行期要先清空 content 再填消息**，否则每次打开面板都会重复堆叠
    ///    （示例节点统一用 `example-` 前缀，便于识别与清理）。
    ///
    /// 工具**只做加法**：不动外框、不动 `input`、不动 `output` 的其他子节点；
    /// 重复运行会先删掉旧的 `output/scroll-view` 再重建（幂等）。
    ///
    /// 菜单：`Icarus II/Add Output Scroll Area (在 output 里加滚动区)`
    ///       `Icarus II/Add Chat Message Examples (重建示例消息)`
    /// 批处理：`-executeMethod IcarusII.EditorTools.IcarusIIOutputArea.Install`
    ///         `-executeMethod IcarusII.EditorTools.IcarusIIOutputArea.AddExamples`
    /// </summary>
    public static class IcarusIIOutputArea
    {
        /// <summary>批处理/无选中时的默认目标。</summary>
        public const string DefaultPrefabPath = "Assets/IcarusII/ChatWindow.prefab";

        /// <summary>对话串容器（人手工建的那个节点）。</summary>
        private const string OutputNodeName = "output";

        /// <summary>和输入区同名，方便 mod 按名字找。</summary>
        private const string ScrollNodeName = "scroll-view";

        /// <summary>示例消息的前缀（运行期据此清理）。</summary>
        private const string ExamplePrefix = "example-";

        // ---- 取值逐项抄自输入区（IcarusIIWindowPrefabBuilder.BuildInputScrollView / BuildScrollbar）
        private static readonly Color ScrollbarBgColor = Rgba(0x00, 0x00, 0x00, 0xDE);        // 滚动条底
        private static readonly Color ScrollbarHandleColor = Rgba(0xFF, 0xFF, 0xFF, 0x8F);    // Handle
        private const float ScrollBarWidth = 6f;

        // ---- 消息样式：气泡取值抄自游戏提示框 dashboard-simple-tip / holo-beacon-simple-tip
        private static readonly Color BubbleBgColor = Rgba(0x09, 0x14, 0x1B, 0xF7);           // bg
        private static readonly Color BubbleBorderColor = Rgba(0xA7, 0xE9, 0xFF, 0x40);       // 圆角描边
        private static readonly Color BubbleTextColor = Rgba(0xFF, 0xFF, 0xFF, 0xE6);         // 气泡里的字
        private static readonly Color AssistantTextColor = Rgba(0xFF, 0xFF, 0xFF, 0xB3);      // 助手正文
        private static readonly Color FooterTimeColor = Rgba(0x8F, 0xA3, 0xB8, 0xFF);         // footer 时间
        private const int MessageFontSize = 14;
        private const float BubblePaddingX = 12f;
        private const float BubblePaddingY = 8f;

        private const string PanelSpritePath = "Third Party/UIComponents/Resources/Textures/GUIPanel.png";
        private const string FrameSpritePath = "Third Party/UIComponents/Resources/Textures/GUIFrame_thin.png";
        private const string BorderMaterialPath = "Resources/UI/Materials/widget-alpha-5x.mat";
        private const string CopyIconPath = "Resources/UI/Textures/Sprites/Icons/copy.png";


        [MenuItem("Icarus II/Add Output Scroll Area (在 output 里加滚动区)")]
        public static void InstallFromMenu()
        {
            string message;
            var ok = InstallAt(DefaultPrefabPath, out message);
            EditorUtility.DisplayDialog("Icarus II", message, "好");
            if (!ok) Debug.LogError("[IcarusII] " + message);
        }

        [MenuItem("Icarus II/Add Chat Message Examples (重建示例消息)")]
        public static void AddExamplesFromMenu()
        {
            string message;
            var ok = AddExamplesAt(DefaultPrefabPath, out message);
            EditorUtility.DisplayDialog("Icarus II", message, "好");
            if (!ok) Debug.LogError("[IcarusII] " + message);
        }

        /// <summary>批处理入口：建滚动区（含示例）。</summary>
        public static void Install()
        {
            string message;
            var ok = InstallAt(DefaultPrefabPath, out message);
            if (ok) Debug.Log("[IcarusII] " + message);
            else Debug.LogError("[IcarusII] " + message);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>批处理入口：只重建示例消息。</summary>
        public static void AddExamples()
        {
            string message;
            var ok = AddExamplesAt(DefaultPrefabPath, out message);
            if (ok) Debug.Log("[IcarusII] " + message);
            else Debug.LogError("[IcarusII] " + message);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// 在 <paramref name="path"/> 的 `output` 下建滚动区 + 示例消息。
        /// 返回 false 时**没有写任何东西**。
        /// </summary>
        public static bool InstallAt(string path, out string message)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                message = "读不到 prefab：" + path;
                return false;
            }

            // 先只读地确认目标节点在，避免瞎加
            if (prefab.transform.Find(OutputNodeName) == null &&
                prefab.transform.Find("content-column/" + OutputNodeName) == null)
            {
                message = "prefab 里没有 `" + OutputNodeName + "` 节点，未做任何修改：" + path +
                          "\n（先在 Unity 里手工建好这个容器，再跑一次本工具。）";
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var output = root.transform.Find("content-column/" + OutputNodeName) as RectTransform;
                if (output == null) output = root.transform.Find(OutputNodeName) as RectTransform;
                if (output == null)
                {
                    message = "`" + OutputNodeName + "` 不是 UI 节点（没有 RectTransform），未做任何修改：" + path;
                    return false;
                }

                // 幂等：旧的 scroll-view 先删掉（只删这一个名字，output 的其他子节点原样保留）
                var previous = output.Find(ScrollNodeName);
                if (previous != null) Object.DestroyImmediate(previous.gameObject);

                BuildOutputScrollView(output);

                PrefabUtility.SaveAsPrefabAsset(root, path);

                message = string.Format(
                    "已在 {0} 的 `{1}` 下建好滚动区：{1}/{2}（ScrollRect）→ viewport（Mask）→ content"
                    + "（VerticalLayoutGroup + ContentSizeFitter）＋ Scrollbar Vertical，"
                    + "并在 content 里放了 4 条示例消息（2 条用户气泡 + 2 条助手裸文本）。\n"
                    + "取值与 `input/editor/scroll-view` 一致；output 的其他子节点、外框、输入区均未改动。",
                    path, OutputNodeName, ScrollNodeName);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>只重建 content 下的示例消息，不动滚动区结构。</summary>
        public static bool AddExamplesAt(string path, out string message)
        {
            var contentPath = "content-column/" + OutputNodeName + "/" + ScrollNodeName + "/viewport/content";
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                message = "读不到 prefab：" + path;
                return false;
            }

            try
            {
                var content = root.transform.Find(contentPath) as RectTransform;
                if (content == null)
                {
                    contentPath = OutputNodeName + "/" + ScrollNodeName + "/viewport/content";
                    content = root.transform.Find(contentPath) as RectTransform;
                }
                if (content == null)
                {
                    message = "找不到 " + contentPath + "，先跑一次“Add Output Scroll Area”。未做任何修改。";
                    return false;
                }

                ClearExamples(content);
                BuildExamples(content);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                message = "已重建 " + contentPath + " 下的示例消息（2 条用户气泡 + 2 条助手裸文本）。";
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// 建 `scroll-view` → `viewport` → `content` ＋ `Scrollbar Vertical`，并塞示例消息。
        /// 结构/贴图/颜色/锚点对齐 `BuildInputScrollView`，content 换成 VLG + CSF。
        /// </summary>
        private static RectTransform BuildOutputScrollView(RectTransform output)
        {
            var scrollView = Node(ScrollNodeName, output);
            Stretch(scrollView);

            // 不给背景色块：output 区域直接透出面板底，不再压一层深色。
            // 但**必须保留一个全透明且收射线的 Image**——ScrollRect 需要一块 Graphic
            // 才能收到滚轮/拖拽事件，否则正文"看着有、滚不动"。
            var bg = scrollView.gameObject.AddComponent<Image>();
            bg.sprite = null;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, 0f, 0f, 0f);
            bg.raycastTarget = true;

            var scrollRect = scrollView.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = false;            // 与输入区一致
            scrollRect.decelerationRate = 0.135f;
            scrollRect.elasticity = 0.1f;
            scrollRect.scrollSensitivity = 20f;

            // viewport：Mask + Image（Mask 需要 Image 才能工作，用内置 UIMask 当遮罩模板）
            var viewport = Node("viewport", scrollView);
            Stretch(viewport, right: ScrollBarWidth);
            viewport.pivot = new Vector2(0f, 1f);
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.sprite = LoadBuiltinMaskSprite();
            vpImg.type = Image.Type.Sliced;
            vpImg.color = Color.white;
            vpImg.raycastTarget = true;
            var mask = viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            // content：对话串在这里纵向堆叠，由 CSF 撑高
            var content = Node("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;          // 宽度由父级给，文字才会按视口宽度换行
            vlg.childControlHeight = true;         // 高度取子节点自己的 preferredHeight
            vlg.childForceExpandWidth = true;      // 每条消息占满整行，气泡靠 HLG 自己靠右
            vlg.childForceExpandHeight = false;    // 不拉伸，避免条目被撑高
            vlg.spacing = 8f;
            vlg.padding = new RectOffset(4, 4, 4, 4);

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.verticalScrollbar = BuildScrollbar(scrollView);
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scrollRect.verticalScrollbarSpacing = 0f;

            BuildExamples(content);
            return scrollView;
        }

        // ---------------------------------------------------------------- 示例消息

        private static void ClearExamples(RectTransform content)
        {
            var doomed = new System.Collections.Generic.List<GameObject>();
            foreach (Transform child in content)
            {
                if (child.name.StartsWith(ExamplePrefix)) doomed.Add(child.gameObject);
            }
            foreach (var go in doomed) Object.DestroyImmediate(go);
        }

        private static void BuildExamples(RectTransform content)
        {
            BuildAssistantLine(content, "example-assistant-1", "已连接到终端，等待你的指令。");
            // maxWidth > 0：气泡封顶换行（长消息）；<= 0：不设宽度，气泡自然包裹文本（短消息）
            BuildUserBubble(content, "example-user-1", "帮我看看这条日志里的过滤规则是什么？", 260f);
            BuildAssistantLine(content, "example-assistant-2", "日志里是 Filter: legacy，它会把旧格式的记录整条丢掉，所以看不到错误行。");
            BuildUserBubble(content, "example-user-2", "明白了，谢谢", 0f);
        }

        /// <summary>助手消息：裸文本，无背景、无边框。Text 直接挂在消息节点上，
        /// 这样它自己就是 ILayoutElement，能给 content 的 VLG 提供 preferredHeight。</summary>
        private static void BuildAssistantLine(RectTransform content, string name, string text)
        {
            var holder = Node(name, content);
            var label = AddTextComponent(holder, text, AssistantTextColor);
            label.alignment = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// 用户消息：整行右对齐的 `bubble`，里面 bg + border 两层圆角底 + 文本（无尾巴）。
        /// </summary>
        private static void BuildUserBubble(RectTransform content, string name, string text, float maxWidth)
        {
            // 整行容器：气泡在上、footer 在下，都靠右
            var row = Node(name, content);
            var rowVlg = row.gameObject.AddComponent<VerticalLayoutGroup>();
            rowVlg.childControlWidth = true;
            rowVlg.childControlHeight = true;
            rowVlg.childForceExpandWidth = false;     // 气泡按自身 preferredWidth，不拉满整行
            rowVlg.childForceExpandHeight = false;
            rowVlg.childAlignment = TextAnchor.UpperRight;
            rowVlg.padding = new RectOffset(0, 0, 0, 0);
            rowVlg.spacing = 2f;

            // 气泡本体：宽度由 LayoutElement 封顶，高度由 VLG 按文本算
            var bubble = Node("bubble", row);
            var vlg = bubble.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(
                Mathf.RoundToInt(BubblePaddingX), Mathf.RoundToInt(BubblePaddingX),
                Mathf.RoundToInt(BubblePaddingY), Mathf.RoundToInt(BubblePaddingY));

            var le = bubble.gameObject.AddComponent<LayoutElement>();
            // maxWidth > 0 才封顶。设了 preferredWidth 后气泡就是固定宽度、文本在内部换行；
            // 不设则气泡按自身 VLG 的 preferredWidth 收缩到刚好包住文本（短消息就是这个效果）。
            // 运行期遇到长文本时按需要给 preferredWidth 封顶即可。
            if (maxWidth > 0f) le.preferredWidth = maxWidth;

            // 底 + 边：不参与 VLG 排版，只按锚点铺满气泡
            var bg = Node("bg", bubble);
            Stretch(bg);
            var bgImg = bg.gameObject.AddComponent<Image>();
            bgImg.sprite = LoadPanelSprite();      // GUIPanel：圆角方块
            bgImg.type = Image.Type.Sliced;
            bgImg.color = BubbleBgColor;           // #09141BF7（游戏 tip 统一底色）
            bgImg.raycastTarget = false;
            IgnoreLayout(bg.gameObject);

            var border = Node("border", bubble);
            Stretch(border);
            var borderImg = border.gameObject.AddComponent<Image>();
            borderImg.sprite = LoadFrameSprite();  // GUIFrame_thin：1px 圆角描边
            borderImg.type = Image.Type.Sliced;
            borderImg.color = BubbleBorderColor;   // #A7E9FF1A（青色描边）
            borderImg.material = LoadBorderMaterial();   // UI Ex/Widget Alpha
            borderImg.raycastTarget = false;
            IgnoreLayout(border.gameObject);

            var label = AddMessageLabel(bubble, text, BubbleTextColor);
            label.alignment = TextAnchor.UpperLeft;

            // footer：只有复制 + 时间（顺序与助手回复的 footer 一致）
            var footer = Node("footer", row);
            var footerHlg = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            footerHlg.padding = new RectOffset(0, 0, 0, 0);
            footerHlg.spacing = 8f;
            footerHlg.childControlWidth = true;
            footerHlg.childControlHeight = true;
            footerHlg.childForceExpandWidth = false;
            footerHlg.childForceExpandHeight = false;
            footerHlg.childAlignment = TextAnchor.MiddleRight;

            var copy = Node("copy", footer);
            var copyLe = copy.gameObject.AddComponent<LayoutElement>();
            copyLe.minWidth = 16f;
            copyLe.preferredWidth = 16f;
            copyLe.minHeight = 16f;
            copyLe.preferredHeight = 16f;
            var copyImg = copy.gameObject.AddComponent<Image>();
            copyImg.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(CopyIconPath);
            copyImg.color = new Color(1f, 1f, 1f, 0.70f);
            copyImg.raycastTarget = true;
            var copyButton = copy.gameObject.AddComponent<Button>();
            copyButton.targetGraphic = copyImg;
            copyButton.transition = Selectable.Transition.ColorTint;
            var colors = copyButton.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            copyButton.colors = colors;

            var time = Node("time", footer);
            var timeLe = time.gameObject.AddComponent<LayoutElement>();
            timeLe.preferredWidth = 45f;
            timeLe.minHeight = 18f;
            timeLe.preferredHeight = 18f;
            var timeText = AddMessageLabel(time, "08:42:00", FooterTimeColor);
            timeText.fontSize = 12;
            timeText.alignment = TextAnchor.MiddleLeft;
            timeText.supportRichText = false;
            timeText.horizontalOverflow = HorizontalWrapMode.Overflow;
            timeText.verticalOverflow = VerticalWrapMode.Truncate;
        }

        /// <summary>
        /// 消息正文（作为 <paramref name="parent"/> 的子节点，供 VerticalLayoutGroup 排版）。
        /// **字体必须给**（占位字体即可）：留空时 UGUI 算 preferredHeight 会 NRE，
        /// 而 content 上挂着 ContentSizeFitter，一进编辑器就刷屏。运行期由 mod 换成游戏字体。
        /// </summary>
        private static Text AddMessageLabel(RectTransform parent, string text, Color color)
        {
            var go = new GameObject("text", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return AddTextComponent(rt, text, color);
        }

        /// <summary>把 Text 挂在给定节点上（不新建子节点）。</summary>
        private static Text AddTextComponent(RectTransform target, string text, Color color)
        {
            var label = target.gameObject.AddComponent<Text>();
            label.font = BuiltinFont;
            label.fontSize = MessageFontSize;
            label.color = color;
            label.text = text;
            label.supportRichText = true;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        private static void IgnoreLayout(GameObject go)
        {
            var le = go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
        }

        /// <summary>占位字体：内置 Arial（不含中文，运行期必被 mod 换掉）。</summary>
        private static Font BuiltinFont
        {
            get { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        }

        // ---------------------------------------------------------------- 滚动条

        /// <summary>右侧细滚动条（与输入区同一套取值）。</summary>
        private static Scrollbar BuildScrollbar(RectTransform parent)
        {
            var bar = Node("Scrollbar Vertical", parent);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 1f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(ScrollBarWidth, 0f);

            var barImg = bar.gameObject.AddComponent<Image>();
            barImg.sprite = null;
            barImg.type = Image.Type.Sliced;
            barImg.color = ScrollbarBgColor;
            barImg.raycastTarget = true;

            var area = Node("Sliding Area", bar);
            Stretch(area);

            var handle = Node("Handle", area);
            Stretch(handle, left: 2f, top: 3f, right: 2f, bottom: 3f);
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.sprite = null;
            handleImg.type = Image.Type.Sliced;
            handleImg.color = ScrollbarHandleColor;
            handleImg.raycastTarget = true;

            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImg;
            scrollbar.numberOfSteps = 0;
            scrollbar.size = 1f;
            scrollbar.value = 0f;
            scrollbar.transition = Selectable.Transition.ColorTint;
            return scrollbar;
        }

        // ---------------------------------------------------------------- 工具

        private static RectTransform Node(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        private static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }


        /// <summary>气泡底：圆角方块（32×32，九宫拉伸后四角保持圆）。</summary>
        private static Sprite LoadPanelSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/" + PanelSpritePath);
            if (sprite == null) Debug.LogWarning("[IcarusII] 找不到圆角底贴图：Assets/" + PanelSpritePath);
            return sprite;
        }

        /// <summary>气泡描边：1px 圆角边框（item-tooltip 用的就是它）。</summary>
        private static Sprite LoadFrameSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/" + FrameSpritePath);
            if (sprite == null) Debug.LogWarning("[IcarusII] 找不到描边贴图：Assets/" + FrameSpritePath);
            return sprite;
        }

        /// <summary>描边用的 `UI Ex/Widget Alpha` 材质（游戏 tip 用的就是它）。</summary>
        private static Material LoadBorderMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/" + BorderMaterialPath);
            if (material == null) Debug.LogWarning("[IcarusII] 找不到材质：Assets/" + BorderMaterialPath);
            return material;
        }

        /// <summary>UGUI 内置的遮罩图（原生 viewport 用的就是它）。</summary>
        private static Sprite LoadBuiltinMaskSprite()
        {
            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd");
            if (sprite == null)
            {
                Debug.LogWarning("[IcarusII] 取不到内置 UIMask 贴图");
            }
            return sprite;
        }

        private static Color Rgba(int r, int g, int b, int a)
        {
            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }
    }
}
