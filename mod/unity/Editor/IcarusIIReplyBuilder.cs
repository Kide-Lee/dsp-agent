using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 在手工维护的 ChatWindow.prefab 里补齐**完整回复模板**：
    ///
    ///   example-reply
    ///   ├── thinking-block   折叠思考块（accent/header/chevron/title/summary/body）
    ///   ├── tools            工具块容器（内含 tool-template）
    ///   ├── body             助手正文
    ///   ├── blueprint-card   蓝图卡片（header/icon/texts/source/meta/desc/actions）
    ///   └── footer           time + copy 按钮
    ///
    /// 运行期 ChatWindow 只克隆这个模板并绑定数据，不再用代码拼静态布局。
    /// 幂等：已存在 example-reply 时直接跳过；要重做请传 force=true。
    /// </summary>
    public static class IcarusIIReplyBuilder
    {
        public const string PrefabPath = "Assets/IcarusII/ChatWindow.prefab";
        private const string ReplyName = "example-reply";

        private static readonly Color Accent = new Color(0.353f, 0.749f, 1f, 1f);
        private static readonly Color TextMain = new Color(1f, 1f, 1f, 0.702f);          // FFFFFFB3
        private static readonly Color TextDim = new Color(0.561f, 0.639f, 0.722f, 1f);   // 8FA3B8
        private static readonly Color BlockBg = new Color(0.38f, 0.85f, 1f, 0.045f);
        private static readonly Color CardPanel = new Color(0.055f, 0.13f, 0.16f, 0.92f);
        private static readonly Color TextMuted = new Color(0.314f, 0.357f, 0.404f, 1f);

        [MenuItem("Icarus II/Ensure Reply Template")]
        public static void EnsureFromMenu()
        {
            Ensure(PrefabPath, false);
        }

        [MenuItem("Icarus II/Rebuild Reply Template")]
        public static void RebuildFromMenu()
        {
            Ensure(PrefabPath, true);
        }

        /// <summary>确保回复模板存在。返回 false 表示 prefab 缺失/保存失败。</summary>
        public static bool Ensure(string prefabPath, bool force = false)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过回复模板：" + prefabPath);
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return false;

            try
            {
                var content = root.transform.Find("output/scroll-view/viewport/content") as RectTransform;
                if (content == null)
                {
                    Debug.LogWarning("[IcarusII] prefab 里找不到 output/scroll-view/viewport/content，跳过回复模板");
                    return false;
                }

                var old = content.Find(ReplyName);
                if (old != null && !force)
                {
                    if (HasAllParts(old))
                    {
                        Debug.Log("[IcarusII] 回复模板已存在，跳过：" + prefabPath);
                        return true;
                    }
                    Debug.LogWarning("[IcarusII] 回复模板结构不完整，重建：" + prefabPath);
                }
                if (old != null) Object.DestroyImmediate(old.gameObject);

                var sample = content.Find("example-assistant") as RectTransform;
                var title = root.transform.Find("panel-bg/title-text") as RectTransform;
                var titleText = title != null ? title.GetComponent<Text>() : null;
                var inputText = root.transform.Find(
                    "input/editor/scroll-view/viewport/content/InputField/input/input-text") as RectTransform;
                var bodyText = inputText != null ? inputText.GetComponent<Text>() : null;

                var fontBold = titleText != null ? titleText.font : null;
                var fontBody = bodyText != null ? bodyText.font : null;
                if (fontBold == null) fontBold = fontBody;
                if (fontBody == null) fontBody = fontBold;
                if (fontBold == null) fontBold = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (fontBody == null) fontBody = fontBold;

                var material = titleText != null ? titleText.material : null;
                if (material == null && sample != null)
                {
                    var sampleText = sample.GetComponent<Text>();
                    material = sampleText != null ? sampleText.material : null;
                }

                var reply = BuildReply(content, fontBold, fontBody, material);
                // 和其它 example-* 一样保持 active，方便在 Unity 里直接看到完整回复；
                // 运行期 CaptureTemplates 会在建窗时把它置为 inactive 并作为克隆源。
                reply.gameObject.SetActive(true);
                SetLayer(reply, content.gameObject.layer == 0 ? 5 : content.gameObject.layer);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[IcarusII] 回复模板已写入 prefab：" + prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool HasAllParts(Transform reply)
        {
            return reply.Find("thinking-block") != null
                && reply.Find("tools/tool-template") != null
                && reply.Find("body") != null
                && reply.Find("blueprint-cards/blueprint-card-template") != null
                && reply.Find("footer/time") != null
                && reply.Find("footer/copy") != null;
        }

        private static RectTransform BuildReply(RectTransform content, Font bold, Font body, Material material)
        {
            var reply = NewRect(ReplyName, content);
            reply.anchorMin = new Vector2(0f, 1f);
            reply.anchorMax = new Vector2(1f, 1f);
            reply.pivot = new Vector2(0.5f, 1f);
            reply.anchoredPosition = Vector2.zero;
            reply.sizeDelta = Vector2.zero;
            var vlg = reply.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            BuildThinking(reply, bold, body, material);
            BuildTools(reply, bold, body, material);
            BuildBody(reply, body, material);
            BuildBlueprintCards(reply, bold, body, material);
            BuildFooter(reply, bold, material);
            return reply;
        }

        // ---------------------------------------------------------------- 思考块

        private static void BuildThinking(RectTransform reply, Font bold, Font body, Material material)
        {
            var root = BuildBlockRoot(reply, "thinking-block", "思考中…");
            var block = BindBlockParts(root, bold, body, material, title: "思考中…", summary: "正在推理…",
                bodyText: "思考内容示例：先确认玩家问的是日志过滤规则，再解释 legacy 过滤器的行为。");
            block.header.gameObject.SetActive(true);
            block.body.gameObject.SetActive(true);
            root.gameObject.SetActive(true);
        }

        private static RectTransform BuildTools(RectTransform reply, Font bold, Font body, Material material)
        {
            var tools = NewRect("tools", reply);
            tools.anchorMin = new Vector2(0f, 1f);
            tools.anchorMax = new Vector2(1f, 1f);
            tools.pivot = new Vector2(0.5f, 1f);
            tools.anchoredPosition = Vector2.zero;
            tools.sizeDelta = Vector2.zero;
            var vlg = tools.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 4f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var template = BuildBlockRoot(tools, "tool-template", "工具 · 示例");
            BindBlockParts(template, bold, body, material, title: "工具 · 示例",
                summary: "读取中…",
                bodyText: "参数\n示例参数\n\n结果\n示例结果");
            template.gameObject.SetActive(false);
            return tools;
        }

        private sealed class BlockParts
        {
            public RectTransform root;
            public RectTransform header;
            public Text chevron;
            public Text title;
            public Text summary;
            public Text body;
            public LayoutElement titleLayout;
        }

        private static RectTransform BuildBlockRoot(RectTransform parent, string name, string title)
        {
            var root = NewRect(name, parent);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(0f, 24f);

            var bg = root.gameObject.AddComponent<Image>();
            bg.color = BlockBg;
            bg.raycastTarget = false;

            var vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 6, 6);
            vlg.spacing = 2f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var bar = NewRect("accent", root);
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(0f, 1f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.sizeDelta = new Vector2(2f, 0f);
            bar.anchoredPosition = Vector2.zero;
            var barImage = bar.gameObject.AddComponent<Image>();
            barImage.color = new Color(Accent.r, Accent.g, Accent.b, 0.5f);
            barImage.raycastTarget = false;
            bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            return root;
        }

        private static BlockParts BindBlockParts(RectTransform root, Font bold, Font body, Material material,
            string title, string summary, string bodyText)
        {
            var parts = new BlockParts { root = root };

            var header = NewRect("header", root);
            parts.header = header;
            var headerLe = header.gameObject.AddComponent<LayoutElement>();
            headerLe.minHeight = 20f;
            headerLe.preferredHeight = 20f;
            var headerImage = header.gameObject.AddComponent<Image>();
            headerImage.color = new Color(0f, 0f, 0f, 0f);
            headerImage.raycastTarget = true;
            var hlg = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.spacing = 6f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var chevron = NewRect("chevron", header);
            parts.chevron = AddLabel(chevron, bold, 11, Accent, TextAnchor.MiddleCenter, material);
            parts.chevron.text = "▾";
            parts.chevron.horizontalOverflow = HorizontalWrapMode.Overflow;
            AddLayout(chevron, minWidth: 12f, preferredWidth: 12f);

            var titleRt = NewRect("title", header);
            parts.title = AddLabel(titleRt, bold, 13, Accent, TextAnchor.MiddleLeft, material);
            parts.title.text = title;
            parts.title.horizontalOverflow = HorizontalWrapMode.Overflow;
            parts.title.verticalOverflow = VerticalWrapMode.Truncate;
            parts.title.wideCharSpacing = 2f;
            parts.titleLayout = titleRt.gameObject.AddComponent<LayoutElement>();

            var sumRt = NewRect("summary", header);
            parts.summary = AddLabel(sumRt, body, 12, TextDim, TextAnchor.MiddleLeft, material);
            parts.summary.text = summary;
            parts.summary.verticalOverflow = VerticalWrapMode.Truncate;
            parts.summary.wideCharSpacing = 1f;
            var sumLe = sumRt.gameObject.AddComponent<LayoutElement>();
            sumLe.flexibleWidth = 1f;

            var bodyRt = NewRect("body", root);
            parts.body = AddLabel(bodyRt, body, 13, new Color(TextDim.r, TextDim.g, TextDim.b, 0.92f),
                TextAnchor.UpperLeft, material);
            parts.body.text = bodyText;
            parts.body.lineSpacing = 1f;
            parts.body.wideCharSpacing = 1f;

            var button = header.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = headerImage;

            var native = header.gameObject.AddComponent<UIButton>();
            native.button = button;
            var normal = headerImage.color;
            var hover = new Color(
                Mathf.Min(1f, normal.r * 1.25f + 0.04f),
                Mathf.Min(1f, normal.g * 1.25f + 0.04f),
                Mathf.Min(1f, normal.b * 1.25f + 0.04f),
                normal.a);
            native.transitions = new[]
            {
                new UIButton.Transition
                {
                    target = headerImage, damp = 0.12f,
                    normalColor = normal,
                    mouseoverColor = hover,
                    pressedColor = new Color(hover.r * 0.9f, hover.g * 0.9f, hover.b * 0.9f, hover.a),
                    disabledColor = normal,
                    mouseoverSize = 1f, pressedSize = 1f,
                    highlightSizeMultiplier = 1f, highlightColorMultiplier = 1f,
                    highlightAlphaMultiplier = 1f
                }
            };
            return parts;
        }

        // ---------------------------------------------------------------- 正文

        private static void BuildBody(RectTransform reply, Font body, Material material)
        {
            var bodyRt = NewRect("body", reply);
            var text = AddLabel(bodyRt, body, 14, TextMain, TextAnchor.UpperLeft, material);
            text.text = "日志里是 Filter: legacy，它会把旧格式的记录整条丢掉，所以看不到错误行。";
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
        }

        // ---------------------------------------------------------------- 蓝图卡片

        private static void BuildBlueprintCards(RectTransform reply, Font bold, Font body, Material material)
        {
            // 用户调整后的卡片：400×130，正文实际宽度约 864px 时一行 2 张。
            var root = NewRect("blueprint-cards", reply);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = Vector2.zero;
            var grid = root.gameObject.AddComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(0, 0, 0, 0);
            grid.cellSize = new Vector2(400f, 130f);
            grid.spacing = new Vector2(8f, 8f);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.Flexible;

            var card = NewRect("blueprint-card-template", root);
            card.anchorMin = new Vector2(0f, 0f);
            card.anchorMax = new Vector2(0f, 0f);
            card.pivot = new Vector2(0f, 1f);
            card.anchoredPosition = Vector2.zero;
            card.sizeDelta = new Vector2(400f, 130f);
            var bg = card.gameObject.AddComponent<Image>();
            bg.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/Resources/UI/Textures/Sprites/Sci-fi/litter-panel-cc.png");
            bg.type = Image.Type.Sliced;
            bg.color = CardPanel;
            bg.raycastTarget = true;
            var cardButton = card.gameObject.AddComponent<Button>();
            cardButton.targetGraphic = bg;
            cardButton.transition = Selectable.Transition.None;

            var accent = NewRect("accent", card);
            accent.anchorMin = new Vector2(0f, 0.5f);
            accent.anchorMax = new Vector2(0f, 0.5f);
            accent.pivot = new Vector2(0f, 0.5f);
            accent.anchoredPosition = Vector2.zero;
            accent.sizeDelta = new Vector2(3f, 130f);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = new Color(Accent.r, Accent.g, Accent.b, 0.55f);
            accentImage.raycastTarget = false;

            // 左栏：游戏蓝图浏览器的 100×110 文件卡。
            var thumb = NewRect("thumb", card);
            thumb.anchorMin = new Vector2(0f, 0.5f);
            thumb.anchorMax = new Vector2(0f, 0.5f);
            thumb.pivot = new Vector2(0f, 0.5f);
            thumb.anchoredPosition = new Vector2(12f, 0f);
            thumb.sizeDelta = new Vector2(100f, 110f);

            var fileBg = NewRect("file-bg", thumb);
            fileBg.anchorMin = new Vector2(0f, 1f);
            fileBg.anchorMax = new Vector2(0f, 1f);
            fileBg.pivot = new Vector2(0f, 1f);
            fileBg.anchoredPosition = Vector2.zero;
            fileBg.sizeDelta = new Vector2(100f, 110f);
            var fileImage = fileBg.gameObject.AddComponent<Image>();
            fileImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/Resources/UI/Textures/Sprites/blueprint-file-220px.png");
            fileImage.type = Image.Type.Sliced;
            fileImage.color = new Color32(161, 163, 166, 255);
            fileImage.preserveAspect = false;
            fileImage.raycastTarget = false;

            var icon = NewRect("icon", thumb);
            icon.anchorMin = new Vector2(0.5f, 0.5f);
            icon.anchorMax = new Vector2(0.5f, 0.5f);
            icon.pivot = new Vector2(0.5f, 0.5f);
            icon.anchoredPosition = new Vector2(0f, 10f);
            icon.sizeDelta = new Vector2(80f, 80f);
            var iconImage = icon.gameObject.AddComponent<Image>();
            iconImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/Resources/Icons/ItemRecipe/belt-1.png");
            iconImage.raycastTarget = false;
            iconImage.preserveAspect = false;
            icon.gameObject.SetActive(true);

            var thumbLabel = NewRect("label", thumb);
            thumbLabel.anchorMin = new Vector2(0f, 0f);
            thumbLabel.anchorMax = new Vector2(1f, 0f);
            thumbLabel.pivot = new Vector2(0.5f, 0f);
            thumbLabel.anchoredPosition = new Vector2(0f, 4f);
            thumbLabel.sizeDelta = new Vector2(-10f, 35f);
            var thumbText = AddLabel(thumbLabel, body, 12, Color.white, TextAnchor.LowerCenter, material);
            thumbText.text = "火箭组装 1-min v2";
            thumbText.horizontalOverflow = HorizontalWrapMode.Wrap;
            thumbText.verticalOverflow = VerticalWrapMode.Truncate;
            thumbText.supportRichText = false;

            // 右栏：存放路径 / 介绍 / 作者·日期·版本 + 使用按钮。
            var content = NewRect("content", card);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 0.5f);
            content.offsetMin = new Vector2(124f, 10f);
            content.offsetMax = new Vector2(-12f, -10f);

            var nameRt = NewRect("name", content);
            nameRt.anchorMin = new Vector2(0f, 1f);
            nameRt.anchorMax = new Vector2(1f, 1f);
            nameRt.pivot = new Vector2(0.5f, 1f);
            nameRt.anchoredPosition = Vector2.zero;
            nameRt.sizeDelta = new Vector2(0f, 20f);
            var nameText = AddLabel(nameRt, bold, 13, new Color(0.878f, 0.918f, 0.961f, 1f),
                TextAnchor.MiddleLeft, material);
            nameText.text = "IcarusII/火箭组装 1-min v2";
            nameText.horizontalOverflow = HorizontalWrapMode.Wrap;
            nameText.verticalOverflow = VerticalWrapMode.Truncate;
            nameText.wideCharSpacing = 1f;

            var descMask = NewRect("desc-mask", content);
            descMask.anchorMin = new Vector2(0f, 1f);
            descMask.anchorMax = new Vector2(1f, 1f);
            descMask.pivot = new Vector2(0.5f, 1f);
            descMask.anchoredPosition = new Vector2(0f, -20f);
            descMask.sizeDelta = new Vector2(0f, 70f);
            descMask.gameObject.AddComponent<RectMask2D>();
            var desc = NewRect("description", descMask);
            Stretch(desc);
            var descText = AddLabel(desc, body, 11, new Color(TextDim.r, TextDim.g, TextDim.b, 0.95f),
                TextAnchor.MiddleLeft, material);
            descText.text = "小型运载火箭组装单元 v2：制造台 Mk.I ×1，满速 10/min，1/min 只需 10% 节拍。进料：氘核燃料棒 4/min、量子芯片 2/min、戴森球组件 2/min。介绍太长时只显示前几行，超出部分会被裁掉。";
            descText.horizontalOverflow = HorizontalWrapMode.Wrap;
            descText.verticalOverflow = VerticalWrapMode.Truncate;
            descText.wideCharSpacing = 1f;

            var metaRt = NewRect("meta", content);
            metaRt.anchorMin = new Vector2(0f, 0f);
            metaRt.anchorMax = new Vector2(1f, 0f);
            metaRt.pivot = new Vector2(0.5f, 0f);
            metaRt.anchoredPosition = new Vector2(-30f, 0f);
            metaRt.sizeDelta = new Vector2(-60f, 20f);
            var metaText = AddLabel(metaRt, body, 11, TextMuted, TextAnchor.LowerLeft, material);
            metaText.text = "作者名 · 2026-10-20 · 版本号";
            metaText.horizontalOverflow = HorizontalWrapMode.Wrap;
            metaText.verticalOverflow = VerticalWrapMode.Truncate;
            metaText.wideCharSpacing = 1f;

            AddCompactCardButton(content, "use", "使用", new Color(Accent.r, Accent.g, Accent.b, 0.30f), body, material);
        }

        private static void AddCompactCardButton(RectTransform parent, string name, string labelText, Color bg,
            Font font, Material material)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(48f, 20f);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = bg;
            image.raycastTarget = true;
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            ConfigureButtonColors(button);

            var labelRt = NewRect("label", rt);
            Stretch(labelRt);
            var label = AddLabel(labelRt, font, 12, new Color(0.878f, 0.918f, 0.961f, 1f),
                TextAnchor.MiddleCenter, material);
            label.text = labelText;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Truncate;
        }

        // ---------------------------------------------------------------- 回复底部

        private static void BuildFooter(RectTransform reply, Font bold, Material material)
        {
            var footer = NewRect("footer", reply);
            AddLayout(footer, minHeight: 18f, preferredHeight: 18f);
            var hlg = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var time = NewRect("time", footer);
            var timeLe = time.gameObject.AddComponent<LayoutElement>();
            timeLe.preferredWidth = 30f;
            timeLe.minHeight = 18f;
            timeLe.preferredHeight = 18f;
            var timeText = AddLabel(time, bold, 12, TextDim, TextAnchor.MiddleLeft, material);
            timeText.text = "08:42";
            timeText.horizontalOverflow = HorizontalWrapMode.Overflow;
            timeText.verticalOverflow = VerticalWrapMode.Truncate;

            var copy = NewRect("copy", footer);
            AddLayout(copy, minWidth: 16f, preferredWidth: 16f, minHeight: 16f, preferredHeight: 16f);
            var copyImage = copy.gameObject.AddComponent<Image>();
            copyImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/Resources/UI/Textures/Sprites/Icons/copy.png");
            copyImage.color = new Color(1f, 1f, 1f, 0.70f);
            copyImage.raycastTarget = true;
            var copyButton = copy.gameObject.AddComponent<Button>();
            copyButton.targetGraphic = copyImage;
            copyButton.transition = Selectable.Transition.ColorTint;
            ConfigureButtonColors(copyButton);
        }

        private static void ConfigureButtonColors(Button button)
        {
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        // ---------------------------------------------------------------- 小工具

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            go.layer = 5;
            return rt;
        }

        private static Text AddLabel(RectTransform rt, Font font, int size, Color color, TextAnchor anchor,
            Material material)
        {
            var text = rt.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            text.raycastTarget = false;
            if (material != null) text.material = material;
            return text;
        }

        private static LayoutElement AddLayout(RectTransform rt, float minWidth = -1f, float preferredWidth = -1f,
            float minHeight = -1f, float preferredHeight = -1f)
        {
            var le = rt.gameObject.AddComponent<LayoutElement>();
            if (minWidth >= 0f) le.minWidth = minWidth;
            if (preferredWidth >= 0f) le.preferredWidth = preferredWidth;
            if (minHeight >= 0f) le.minHeight = minHeight;
            if (preferredHeight >= 0f) le.preferredHeight = preferredHeight;
            return le;
        }

        private static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f,
            float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static void SetLayer(Transform root, int layer)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
        }
    }
}
