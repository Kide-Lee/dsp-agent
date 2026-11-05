using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 给 `content-column/choose/voting-device` 补一块只读说明区 `explain`：
    /// 写操作确认页在这里显示 `confirm_request.body`（AI 发起的请求详情），
    /// 作答总结页在这里显示全部问答。
    ///
    ///   explain                    铺满标题下、按钮上的区域
    ///   └── scroll-view            ScrollRect + 全透明命中层
    ///       ├── viewport           Mask(Show Mask Graphic=false) + 内置 UIMask
    ///       │   └── content        Text + ContentSizeFitter(纵向 PreferredSize)
    ///       └── Scrollbar Vertical 右侧 6 宽
    ///
    /// 结构与取值抄自 `output/scroll-view`（见 IcarusIIOutputArea），
    /// 只是把 VerticalLayoutGroup 换成单个 Text：说明文字是一整段，不需要逐条排版。
    ///
    /// 旧结构 `voting-device/scroll-view` 里塞的是早期草稿的 InputField（"用 Ctrl + Enter 换行"），
    /// 自定义答案早已改到 `answer-grop/answer-input`，这块没有任何调用方；本工具一并删掉。
    /// mod 侧只按名字找 `voting-device/explain/scroll-view/viewport/content`（见 AskPanel.Bind）。
    ///
    /// 菜单：`Icarus II/Add Ask Explain Area (在 choose 里加说明区)`
    /// 批处理：`-executeMethod IcarusII.EditorTools.IcarusIIExplainArea.Install`
    /// </summary>
    public static class IcarusIIExplainArea
    {
        public const string DefaultPrefabPath = "Assets/IcarusII/ChatWindow.prefab";

        private const string VotingDevicePath = "content-column/choose/voting-device";
        private const string ExplainNodeName = "explain";
        private const string ScrollNodeName = "scroll-view";
        private const string ViewportNodeName = "viewport";
        private const string ContentNodeName = "content";
        private const string ScrollbarNodeName = "Scrollbar Vertical";

        /// <summary>面板顶部 36 是标题，底部 36 被同意/拒绝按钮占着。</summary>
        private const float TitleHeight = 36f;
        private const float ButtonBarHeight = 36f;

        private const float ScrollBarWidth = 6f;
        /// <summary>与 `question/text` 的左边缘对齐（标题左缩进 20）。</summary>
        private const float LeftInset = 20f;
        private const float RightInset = 8f;
        private const int TextFontSize = 14;
        private const string SampleText = "AI 发起的请求详情显示在这里。";

        private static readonly Color ScrollbarBgColor = Rgba(0x00, 0x00, 0x00, 0xDE);
        private static readonly Color ScrollbarHandleColor = Rgba(0xFF, 0xFF, 0xFF, 0x8F);
        private static readonly Color ExplainTextColor = Rgba(0xFF, 0xFF, 0xFF, 0xB3);

        [MenuItem("Icarus II/Add Ask Explain Area (在 choose 里加说明区)")]
        public static void InstallFromMenu()
        {
            string message;
            var ok = InstallAt(DefaultPrefabPath, out message);
            EditorUtility.DisplayDialog("Icarus II", message, "好");
            if (!ok) Debug.LogError("[IcarusII] " + message);
        }

        /// <summary>批处理入口。</summary>
        public static void Install()
        {
            string message;
            var ok = InstallAt(DefaultPrefabPath, out message);
            if (ok) Debug.Log("[IcarusII] " + message);
            else Debug.LogError("[IcarusII] " + message);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>在 <paramref name="path"/> 的 `voting-device` 下重建说明区。失败时不写文件。</summary>
        public static bool InstallAt(string path, out string message)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                message = "读不到 prefab：" + path;
                return false;
            }
            if (prefab.transform.Find(VotingDevicePath) == null)
            {
                message = "prefab 里没有 `" + VotingDevicePath + "`，未做任何修改：" + path;
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var device = root.transform.Find(VotingDevicePath) as RectTransform;
                if (device == null)
                {
                    message = "`" + VotingDevicePath + "` 不是 UI 节点，未做任何修改：" + path;
                    return false;
                }

                Remove(device, ExplainNodeName);
                Remove(device, ScrollNodeName);
                BuildExplain(device);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                message = string.Format(
                    "已在 {0} 的 `{1}` 下重建说明区：explain → scroll-view → viewport → content(Text)"
                    + " ＋ Scrollbar Vertical；旧草稿 scroll-view(InputField) 已删除。"
                    + "取值与 `output/scroll-view` 一致，voting-device 的其他子节点未改动。",
                    path, VotingDevicePath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void BuildExplain(RectTransform device)
        {
            var explain = Node(ExplainNodeName, device);
            explain.anchorMin = Vector2.zero;
            explain.anchorMax = Vector2.one;
            explain.pivot = new Vector2(0.5f, 1f);
            explain.offsetMin = new Vector2(0f, ButtonBarHeight);
            explain.offsetMax = new Vector2(0f, -TitleHeight);

            // 与 input/editor、output 同一套命名：说明区本身只是个容器，
            // 滚动结构放在 scroll-view 里；mod 按 `explain/scroll-view/viewport/content` 取文本。
            var scrollView = Node(ScrollNodeName, explain);
            Stretch(scrollView);

            // 不给背景色块：直接透出面板底。但必须保留一个全透明且收射线的 Image，
            // ScrollRect 需要一块 Graphic 才能收到滚轮/拖拽事件。
            var bg = scrollView.gameObject.AddComponent<Image>();
            bg.sprite = null;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, 0f, 0f, 0f);
            bg.raycastTarget = true;

            var scrollRect = scrollView.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = false;
            scrollRect.decelerationRate = 0.135f;
            scrollRect.elasticity = 0.1f;
            scrollRect.scrollSensitivity = 20f;

            var viewport = Node(ViewportNodeName, scrollView);
            // 先定 pivot 再写 offset：pivot 在 offset 之后改会把已经算好的 rect 整体挪走。
            Place(viewport, LeftInset, 0f, ScrollBarWidth + RightInset, 0f, new Vector2(0f, 1f));
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.sprite = LoadBuiltinMaskSprite();
            vpImg.type = Image.Type.Sliced;
            vpImg.color = Color.white;
            vpImg.raycastTarget = true;
            var mask = viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            // content 直接就是 Text：说明文字整段换行，高度由 CSF 按文本撑开。
            var content = Node(ContentNodeName, viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var label = content.gameObject.AddComponent<Text>();
            // 字体必须给：留空时 UGUI 算 preferredHeight 会 NRE，而这里挂着 ContentSizeFitter。
            // 运行期 ChatWindow 会把所有 Text 换成游戏当前语言的字体。
            label.font = BuiltinFont;
            label.fontSize = TextFontSize;
            label.color = ExplainTextColor;
            label.text = SampleText;
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.alignment = TextAnchor.UpperLeft;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.verticalScrollbar = BuildScrollbar(scrollView);
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scrollRect.verticalScrollbarSpacing = 0f;

            explain.gameObject.SetActive(false);
        }

        /// <summary>右侧细滚动条（与输入区/正文区同一套取值）。</summary>
        private static Scrollbar BuildScrollbar(RectTransform parent)
        {
            var bar = Node(ScrollbarNodeName, parent);
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

            // 放在 Scrollbar 之后：Scrollbar 会用自身的 size/value 改写 Handle 的锚点，
            // 先摆会被它覆盖成 0 尺寸。
            Stretch(handle, left: 2f, top: 3f, right: 2f, bottom: 3f);
            return scrollbar;
        }

        private static void Remove(RectTransform parent, string childName)
        {
            var child = parent.Find(childName);
            if (child != null) Object.DestroyImmediate(child.gameObject);
        }

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
            Place(rt, left, top, right, bottom, new Vector2(0.5f, 0.5f));
        }

        /// <summary>铺满父节点并留四边偏移。pivot 必须和 offset 在同一处写，顺序反了 rect 会移位。</summary>
        private static void Place(RectTransform rt, float left, float top, float right, float bottom, Vector2 pivot)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = pivot;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>UGUI 内置的遮罩图（原生 viewport 用的就是它）。</summary>
        private static Sprite LoadBuiltinMaskSprite()
        {
            return AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd");
        }

        /// <summary>占位字体：内置 Arial（不含中文，运行期必被 mod 换掉）。</summary>
        private static Font BuiltinFont
        {
            get { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        }

        private static Color Rgba(int r, int g, int b, int a)
        {
            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }
    }
}
