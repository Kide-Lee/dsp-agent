using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 生成聊天窗的 prefab（`Assets/IcarusII/IcarusIIWindow.prefab`）。
    ///
    /// 为什么要有这个脚本而不是手搭：prefab 是**二进制** Unity 资源，
    /// 手搭出来的东西无法 review、也无法复现。用脚本生成可以入库、可重跑、
    /// 且在 prefab 被误改时能重建。生成之后你依然可以在 Unity 里可视化微调，
    /// 微调结果会保存在 prefab 里（脚本只在显式调用时覆盖）。
    ///
    /// 节点结构（与 mod 侧 `ChatWindow` 的字段一一对应，mod 按名字查找）：
    ///
    ///   IcarusIIWindow            ← mod 把它当作 Panel
    ///   ├── Frame                 Image（装饰边框, window-border-1）
    ///   ├── Glass                 Image（玻璃高光, window-glass-1）
    ///   ├── Header                Image
    ///   │   ├── Title             Text
    ///   │   └── Status            Text
    ///   ├── Scroll                ScrollRect
    ///   │   └── Viewport          RectMask2D
    ///   │       └── Content       Text（正文控件就挂在 Content 上）+ ContentSizeFitter
    ///   └── InputBar
    ///       └── Input             Image + InputField
    ///           ├── Text          Text
    ///           └── Placeholder   Text
    ///
    /// **Canvas 不在这里**：它依赖游戏 UI Root 的 CanvasScaler 设置，且必须是
    /// 顶层 DontDestroyOnLoad 对象，所以仍由 mod 在运行时创建，本 prefab 挂进去。
    ///
    /// 字体刻意留空：mod 会在运行期套用从游戏窗口捕获到的真实字体
    /// （见 UiStyle）；若你在 prefab 里手动指定了字体，mod 会尊重它。
    /// 颜色同理，运行期会被游戏原生配色覆盖。
    /// </summary>
    public static class IcarusIIWindowPrefabBuilder
    {
        private const string PrefabDir = "Assets/IcarusII";
        private const string PrefabPath = PrefabDir + "/IcarusIIWindow.prefab";
        private const string SpriteDir = "Assets/Resources/UI/Textures/Sprites/Sci-fi/";

        private const float PanelWidth = 780f;
        private const float PanelHeight = 520f;
        private const float HeaderHeight = 34f;
        private const float InputBarHeight = 38f;

        // 占位配色（运行期会被游戏原生配色覆盖）
        private static readonly Color PanelColor = new Color(0.10f, 0.15f, 0.22f, 1f);
        private static readonly Color FrameColor = new Color(1f, 1f, 1f, 0.29f);
        private static readonly Color GlassColor = new Color(1f, 1f, 1f, 0.15f);
        private static readonly Color TextMain = new Color(0.878f, 0.918f, 0.961f, 1f);
        private static readonly Color TextDim = new Color(0.596f, 0.678f, 0.757f, 1f);
        private static readonly Color Accent = new Color(0.353f, 0.749f, 1f, 1f);

        private const int FontSize = 14;

        [MenuItem("Icarus II/Rebuild Window Prefab")]
        public static void BuildFromMenu()
        {
            Build();
            EditorUtility.DisplayDialog("Icarus II", "已重建 " + PrefabPath, "好");
        }

        /// <summary>批处理入口：`-executeMethod IcarusII.EditorTools.IcarusIIWindowPrefabBuilder.Build`</summary>
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(PrefabDir))
            {
                AssetDatabase.CreateFolder("Assets", "IcarusII");
            }

            var root = BuildHierarchy();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            if (prefab == null)
            {
                Debug.LogError("[IcarusII] prefab 保存失败：" + PrefabPath);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[IcarusII] 已生成 prefab：" + PrefabPath);
        }

        private static GameObject BuildHierarchy()
        {
            var rootRt = Node("IcarusIIWindow", null);
            var root = rootRt.gameObject;
            rootRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            // 底衬（Panel 本体）
            var panel = root.AddComponent<Image>();
            panel.sprite = LoadSprite("window-trsl-1");
            panel.type = Image.Type.Sliced;
            panel.color = PanelColor;

            // 装饰边框 + 玻璃：紧跟在底衬之后，保证它们是靠前的同级子物体
            // （先绘制），不会盖住正文与输入框。
            var frame = Node("Frame", rootRt);
            Stretch(frame);
            var frameImg = frame.gameObject.AddComponent<Image>();
            frameImg.sprite = LoadSprite("window-border-1");
            frameImg.type = Image.Type.Sliced;
            frameImg.color = FrameColor;
            frameImg.raycastTarget = false;

            var glass = Node("Glass", rootRt);
            Stretch(glass);
            var glassImg = glass.gameObject.AddComponent<Image>();
            glassImg.sprite = LoadSprite("window-glass-1");
            glassImg.type = Image.Type.Sliced;
            glassImg.color = GlassColor;
            glassImg.raycastTarget = false;

            BuildHeader(rootRt);
            BuildTranscript(rootRt);
            BuildInputBar(rootRt);

            return root;
        }

        private static void BuildHeader(RectTransform parent)
        {
            var header = Node("Header", parent);
            TopBar(header, HeaderHeight);
            var img = header.gameObject.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);   // 机体信息窗没有独立标题栏
            img.raycastTarget = false;

            var title = Node("Title", header);
            Stretch(title, left: 12f, right: 260f);
            var titleText = AddLabel(title, FontSize + 2, Accent, TextAnchor.MiddleLeft);
            titleText.text = "伊卡洛斯·贰";

            var status = Node("Status", header);
            Stretch(status, left: 200f, right: 12f);
            var statusText = AddLabel(status, FontSize - 1, TextDim, TextAnchor.MiddleRight);
            statusText.text = string.Empty;
        }

        private static void BuildTranscript(RectTransform parent)
        {
            var scroll = Node("Scroll", parent);
            Stretch(scroll, left: 8f, top: HeaderHeight + 6f, right: 8f, bottom: InputBarHeight + 6f);

            var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30f;

            var viewport = Node("Viewport", scroll);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = Node("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = new Vector2(0f, -4f);
            content.sizeDelta = new Vector2(-20f, 100f);

            // 正文控件挂在 Content 自身（mod 的 _transcript 就指向它）
            var transcript = AddLabel(content, FontSize, TextMain, TextAnchor.UpperLeft);
            transcript.horizontalOverflow = HorizontalWrapMode.Wrap;
            transcript.verticalOverflow = VerticalWrapMode.Overflow;
            transcript.supportRichText = true;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
        }

        private static void BuildInputBar(RectTransform parent)
        {
            var bar = Node("InputBar", parent);
            BottomBar(bar, InputBarHeight);

            var input = Node("Input", bar);
            Stretch(input, left: 8f, top: 3f, right: 8f, bottom: 5f);

            var bg = input.gameObject.AddComponent<Image>();
            bg.sprite = LoadSprite("window-trsl-1");
            bg.type = Image.Type.Sliced;
            bg.color = new Color(PanelColor.r * 0.78f, PanelColor.g * 0.78f, PanelColor.b * 0.78f, PanelColor.a);

            var textHolder = Node("Text", input);
            Stretch(textHolder, left: 8f, right: 8f);
            var text = AddLabel(textHolder, FontSize, TextMain, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;

            var placeholderHolder = Node("Placeholder", input);
            Stretch(placeholderHolder, left: 8f, right: 8f);
            var placeholder = AddLabel(placeholderHolder, FontSize, TextDim, TextAnchor.MiddleLeft);
            placeholder.supportRichText = false;
            placeholder.horizontalOverflow = HorizontalWrapMode.Overflow;
            placeholder.text = "输入后回车发送…";

            var field = input.gameObject.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = bg;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 2000;
            field.customCaretColor = true;
            field.caretColor = TextMain;
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

        private static void TopBar(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        private static void BottomBar(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        private static Text AddLabel(RectTransform rt, int size, Color color, TextAnchor anchor)
        {
            var text = rt.gameObject.AddComponent<Text>();
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            // 字体刻意留空：运行期由 mod 套用游戏真实字体（见类注释）
            return text;
        }

        private static Sprite LoadSprite(string fileName)
        {
            var path = SpriteDir + fileName + ".png";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogWarning("[IcarusII] 找不到贴图：" + path);
            }
            return sprite;
        }
    }
}
