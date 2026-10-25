using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 在 ChatWindow.prefab 的输入框上方补一个**工具列表**（M4l）。
    ///
    /// 静态结构/样式全部写进 prefab；运行期 ChatWindow 只做：
    ///   * 查找 input/tool-menu 及其子节点；
    ///   * 按 ready.payload.toolCatalog 克隆 tool-row-template、填文字；
    ///   * 显隐 / 高亮 / 滚动。
    ///
    /// 幂等：tool-menu 已存在时直接跳过，绝不覆盖用户在 Unity 里调过的布局。
    /// 需要重做时用菜单 Icarus II/Rebuild Tool Menu（force）。
    ///
    ///   input
    ///   └── tool-menu                 440×360，锚在输入框左上角
    ///       ├── bg / border
    ///       ├── header/{title,hint}
    ///       ├── scroll-view
    ///       │   ├── viewport/content
    ///       │   │   └── tool-row-template   （运行期克隆源，默认 inactive）
    ///       │   └── Scrollbar Vertical
    ///       ├── scroll-view/empty           （无匹配时显示）
    ///       └── footer
    /// </summary>
    public static class IcarusIIToolMenuBuilder
    {
        private const string PrefabPath = "Assets/IcarusII/ChatWindow.prefab";
        private const string MenuName = "tool-menu";
        private const string RowName = "tool-row-template";

        private static readonly Color Accent = new Color(0.353f, 0.749f, 1f, 1f);
        private static readonly Color TextMain = new Color(1f, 1f, 1f, 0.86f);
        private static readonly Color TextDim = new Color(0.561f, 0.639f, 0.722f, 1f);
        private static readonly Color PanelBg = new Color(0.035f, 0.078f, 0.106f, 0.97f);
        private static readonly Color Border = new Color(0.655f, 0.914f, 1f, 0.25f);
        private static readonly Color RowBg = Color.white;

        [MenuItem("Icarus II/Ensure Tool Menu")]
        public static void EnsureFromMenu()
        {
            Ensure(PrefabPath, false);
        }

        [MenuItem("Icarus II/Rebuild Tool Menu")]
        public static void RebuildFromMenu()
        {
            Ensure(PrefabPath, true);
        }

        /// <summary>确保 prefab 里有工具列表。返回 false 表示 prefab 缺失/保存失败。</summary>
        public static bool Ensure(string prefabPath, bool force = false)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过工具列表：" + prefabPath);
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return false;

            try
            {
                var input = root.transform.Find("content-column/input") as RectTransform;
                if (input == null) input = root.transform.Find("input") as RectTransform;
                if (input == null)
                {
                    Debug.LogWarning("[IcarusII] prefab 里找不到 input，跳过工具列表");
                    return false;
                }

                var old = input.Find(MenuName);
                if (old != null && !force)
                {
                    Debug.Log("[IcarusII] 工具列表已存在，跳过：" + prefabPath);
                    return true;
                }
                if (old != null) Object.DestroyImmediate(old.gameObject);

                var title = root.transform.Find("panel-bg/title-text") as RectTransform;
                var titleText = title != null ? title.GetComponent<Text>() : null;
                var inputLabel = root.transform.Find(
                    "content-column/input/editor/scroll-view/viewport/content/InputField/input/input-text") as RectTransform;
                if (inputLabel == null)
                    inputLabel = root.transform.Find(
                        "input/editor/scroll-view/viewport/content/InputField/input/input-text") as RectTransform;
                var inputText = inputLabel != null ? inputLabel.GetComponent<Text>() : null;

                var fontBold = titleText != null ? titleText.font : null;
                var fontBody = inputText != null ? inputText.font : null;
                if (fontBody == null) fontBody = fontBold;
                if (fontBold == null) fontBold = fontBody;
                if (fontBold == null) fontBold = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (fontBody == null) fontBody = fontBold;

                var textMaterial = titleText != null ? titleText.material : null;
                if (textMaterial == null && inputText != null) textMaterial = inputText.material;

                var menu = Build(input, fontBold, fontBody, textMaterial);
                menu.SetAsLastSibling();

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[IcarusII] 工具列表已写入 prefab：" + prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static RectTransform Build(RectTransform input, Font fontBold, Font fontBody, Material textMaterial)
        {
            var menu = NewRect(MenuName, input);
            menu.anchorMin = new Vector2(0f, 0f);
            menu.anchorMax = new Vector2(0f, 0f);
            menu.pivot = new Vector2(0f, 0f);
            menu.anchoredPosition = new Vector2(0f, 100f);
            menu.sizeDelta = new Vector2(440f, 360f);

            var bg = NewRect("bg", menu);
            Stretch(bg);
            var bgImage = bg.gameObject.AddComponent<Image>();
            bgImage.sprite = LoadSprite("Assets/Third Party/UIComponents/Resources/Textures/GUIPanel.png");
            bgImage.type = Image.Type.Sliced;
            bgImage.color = PanelBg;
            bgImage.raycastTarget = true;

            var border = NewRect("border", menu);
            Stretch(border);
            var borderImage = border.gameObject.AddComponent<Image>();
            borderImage.sprite = LoadSprite("Assets/Third Party/UIComponents/Resources/Textures/GUIFrame_thin.png");
            borderImage.type = Image.Type.Sliced;
            borderImage.color = Border;
            borderImage.raycastTarget = false;

            // header：标题 + 一行操作提示（运行期会重新写文案，字号/颜色保持 prefab 样式）
            var header = NewRect("header", menu);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.anchoredPosition = Vector2.zero;
            header.sizeDelta = new Vector2(0f, 34f);

            var title = NewRect("title", header);
            title.anchorMin = new Vector2(0f, 0f);
            title.anchorMax = new Vector2(0f, 1f);
            title.pivot = new Vector2(0f, 0.5f);
            title.anchoredPosition = new Vector2(13f, 0f);
            title.sizeDelta = new Vector2(90f, 0f);
            var titleLabel = AddLabel(title, fontBold, 13, Accent, TextAnchor.MiddleLeft, textMaterial);
            titleLabel.text = "工具";

            var hint = NewRect("hint", header);
            hint.anchorMin = new Vector2(0f, 0f);
            hint.anchorMax = new Vector2(1f, 1f);
            hint.pivot = new Vector2(0.5f, 0.5f);
            hint.offsetMin = new Vector2(100f, 0f);
            hint.offsetMax = new Vector2(-13f, 0f);
            var hintLabel = AddLabel(hint, fontBody, 10, TextDim, TextAnchor.MiddleRight, textMaterial);
            hintLabel.text = "输入 / 筛选 · 选择后本轮强制调用";

            var scroll = NewRect("scroll-view", menu);
            scroll.anchorMin = Vector2.zero;
            scroll.anchorMax = Vector2.one;
            scroll.pivot = new Vector2(0.5f, 0.5f);
            scroll.offsetMin = new Vector2(8f, 24f);
            scroll.offsetMax = new Vector2(-8f, -34f);

            var scrollImage = scroll.gameObject.AddComponent<Image>();
            scrollImage.color = new Color(0f, 0f, 0f, 0f);
            scrollImage.raycastTarget = true;

            var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;
            scrollRect.inertia = false;

            var viewport = NewRect("viewport", scroll);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0f);
            viewportImage.raycastTarget = false;

            var content = NewRect("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(2, 2, 2, 2);
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;

            BuildRowTemplate(content, fontBold, fontBody, textMaterial);

            var empty = NewRect("empty", scroll);
            Stretch(empty, 12f, 8f, 12f, 8f);
            var emptyLabel = AddLabel(empty, fontBody, 12, TextDim, TextAnchor.MiddleCenter, textMaterial);
            emptyLabel.text = "没有匹配的工具";
            empty.gameObject.SetActive(false);

            var scrollbar = BuildScrollbar(scroll);
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scrollRect.verticalScrollbarSpacing = 0f;

            var footer = NewRect("footer", menu);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.anchoredPosition = new Vector2(0f, 3f);
            footer.sizeDelta = new Vector2(0f, 18f);
            var footerLabel = AddLabel(footer, fontBody, 10, TextDim, TextAnchor.MiddleCenter, textMaterial);
            footerLabel.text = "↑↓ 选择 · Enter 插入 · Esc 关闭";

            // 默认隐藏：开窗/点击按钮/输入 / 时才显示。
            menu.gameObject.SetActive(false);
            return menu;
        }

        private static void BuildRowTemplate(RectTransform content, Font fontBold, Font fontBody, Material textMaterial)
        {
            var row = NewRect(RowName, content);
            row.sizeDelta = new Vector2(0f, 54f);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 54f;
            le.preferredHeight = 54f;
            le.flexibleWidth = 1f;

            var bg = NewRect("bg", row);
            Stretch(bg);
            var bgImage = bg.gameObject.AddComponent<Image>();
            bgImage.sprite = LoadSprite("Assets/Third Party/UIComponents/Resources/Textures/rect_p1.png");
            bgImage.type = Image.Type.Sliced;
            bgImage.color = RowBg;
            bgImage.raycastTarget = true;

            var button = row.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.ColorTint;
            button.targetGraphic = bgImage;
            button.colors = RowColors();

            var slash = NewRect("slash", row);
            slash.anchorMin = new Vector2(0f, 1f);
            slash.anchorMax = new Vector2(0f, 1f);
            slash.pivot = new Vector2(0f, 1f);
            slash.anchoredPosition = new Vector2(12f, -7f);
            slash.sizeDelta = new Vector2(14f, 20f);
            var slashLabel = AddLabel(slash, fontBold, 14, Accent, TextAnchor.MiddleLeft, textMaterial);
            slashLabel.text = "/";
            slashLabel.horizontalOverflow = HorizontalWrapMode.Overflow;

            var label = NewRect("label", row);
            label.anchorMin = new Vector2(0f, 1f);
            label.anchorMax = new Vector2(0f, 1f);
            label.pivot = new Vector2(0f, 1f);
            label.anchoredPosition = new Vector2(30f, -6f);
            label.sizeDelta = new Vector2(190f, 21f);
            var labelText = AddLabel(label, fontBody, 13, TextMain, TextAnchor.MiddleLeft, textMaterial);
            labelText.text = "工具名";
            labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
            labelText.verticalOverflow = VerticalWrapMode.Truncate;

            var command = NewRect("command", row);
            command.anchorMin = new Vector2(1f, 1f);
            command.anchorMax = new Vector2(1f, 1f);
            command.pivot = new Vector2(1f, 1f);
            command.anchoredPosition = new Vector2(-12f, -7f);
            command.sizeDelta = new Vector2(210f, 19f);
            var commandLabel = AddLabel(command, fontBody, 10, TextDim, TextAnchor.MiddleRight, textMaterial);
            commandLabel.text = "/tool_name";
            commandLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            commandLabel.verticalOverflow = VerticalWrapMode.Truncate;

            var desc = NewRect("description", row);
            desc.anchorMin = new Vector2(0f, 0f);
            desc.anchorMax = new Vector2(1f, 0f);
            desc.pivot = new Vector2(0.5f, 0f);
            desc.offsetMin = new Vector2(30f, 5f);
            desc.offsetMax = new Vector2(-12f, 27f);
            var descLabel = AddLabel(desc, fontBody, 11, TextDim, TextAnchor.UpperLeft, textMaterial);
            descLabel.text = "工具简介";
            descLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            descLabel.verticalOverflow = VerticalWrapMode.Truncate;

            row.gameObject.SetActive(false);
        }

        private static Scrollbar BuildScrollbar(RectTransform scroll)
        {
            var bar = NewRect("Scrollbar Vertical", scroll);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 1f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(6f, 0f);
            var barImage = bar.gameObject.AddComponent<Image>();
            barImage.color = new Color(0f, 0f, 0f, 0.7f);
            barImage.raycastTarget = true;

            var sliding = NewRect("Sliding Area", bar);
            Stretch(sliding);

            var handle = NewRect("Handle", sliding);
            Stretch(handle);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = new Color(1f, 1f, 1f, 0.55f);
            handleImage.raycastTarget = true;

            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.transition = Selectable.Transition.ColorTint;
            return scrollbar;
        }

        private static ColorBlock RowColors()
        {
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = new Color(1f, 1f, 1f, 1f);
            colors.highlightedColor = new Color(0.353f, 0.749f, 1f, 0.28f);
            colors.pressedColor = new Color(0.353f, 0.749f, 1f, 0.38f);
            colors.selectedColor = new Color(0.353f, 0.749f, 1f, 0.22f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.4f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.06f;
            return colors;
        }

        private static Sprite LoadSprite(string path)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) Debug.LogWarning("[IcarusII] 工具列表贴图缺失：" + path);
            return sprite;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            go.layer = parent != null ? parent.gameObject.layer : 5;
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
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            if (material != null) text.material = material;
            return text;
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
    }
}
