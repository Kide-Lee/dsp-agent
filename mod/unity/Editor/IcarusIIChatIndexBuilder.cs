using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 在手工维护的 ChatWindow.prefab 里补齐 chat-index 的可见结构。
    ///
    /// 运行期 ChatWindow 只负责**引用**这些节点（找不到才回退到代码补建），
    /// 所以列表行、按钮、滚动区都能在 Unity 里直接调样式。
    ///
    /// 幂等：已有 chat-index-scroll + turn-row-template 时直接跳过。
    /// </summary>
    public static class IcarusIIChatIndexBuilder
    {
        private const string IndexName = "chat-index";
        private const string HeaderName = "chat-index-header";
        private const string ScrollName = "chat-index-scroll";
        private const string ViewportName = "viewport";
        private const string ContentName = "content";
        private const string TemplateName = "turn-row-template";
        private const string EmptyName = "chat-index-empty";

        private static readonly Color Accent = new Color(0.353f, 0.749f, 1f, 1f);
        private static readonly Color Dim = new Color(0.596f, 0.678f, 0.757f, 1f);
        private static readonly Color PanelBg = new Color(0f, 0f, 0f, 0.56f);

        /// <summary>确保 prefab 里有完整 chat-index。返回 false 表示 prefab 缺失/保存失败。</summary>
        public static bool Ensure(string prefabPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过 chat-index：" + prefabPath);
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return false;

            try
            {
                var index = root.transform.Find(IndexName) as RectTransform;
                if (index == null)
                {
                    index = NewRect(IndexName, root.transform);
                    index.anchorMin = new Vector2(0f, 0f);
                    index.anchorMax = new Vector2(0f, 1f);
                    index.pivot = new Vector2(0f, 0.5f);
                    index.anchoredPosition = new Vector2(40f, -4f);
                    index.sizeDelta = new Vector2(250f, -88f);
                    Debug.Log("[IcarusII] 已创建 chat-index 容器");
                }

                var title = root.transform.Find("panel-bg/title-text") as RectTransform;
                var titleText = title != null ? title.GetComponent<Text>() : null;
                var font = titleText != null ? titleText.font : null;
                var material = titleText != null ? titleText.material : null;

                var bg = index.GetComponent<Image>();
                if (bg == null) bg = index.gameObject.AddComponent<Image>();
                bg.color = PanelBg;
                bg.raycastTarget = false;

                var oldHeader = index.Find(HeaderName);
                if (oldHeader != null) UnityEngine.Object.DestroyImmediate(oldHeader.gameObject);

                var scroll = index.Find(ScrollName) as RectTransform;
                var content = scroll != null
                    ? scroll.Find(ViewportName + "/" + ContentName) as RectTransform
                    : null;
                var template = content != null ? content.Find(TemplateName) as RectTransform : null;
                if (template != null &&
                    (template.Find("link/label") == null ||
                     template.GetComponent<Image>() == null ||
                     template.Find("link").GetComponent<Button>() != null ||
                     template.Find("delete").GetComponent<Button>() != null))
                {
                    // 旧版模板：重建为带行底色/遮罩/删除 hover 的新结构。
                    UnityEngine.Object.DestroyImmediate(template.gameObject);
                    template = null;
                }

                if (scroll == null || content == null || template == null)
                {
                    if (scroll == null)
                    {
                        scroll = NewRect(ScrollName, index);
                        scroll.anchorMin = Vector2.zero;
                        scroll.anchorMax = Vector2.one;
                        scroll.pivot = new Vector2(0.5f, 0.5f);
                        scroll.offsetMin = new Vector2(4f, 4f);
                        scroll.offsetMax = new Vector2(-4f, -4f);
                    }

                    var hit = scroll.GetComponent<Image>();
                    if (hit == null)
                    {
                        hit = scroll.gameObject.AddComponent<Image>();
                        hit.color = new Color(0f, 0f, 0f, 0f);
                        hit.raycastTarget = true;
                    }

                    var viewport = scroll.Find(ViewportName) as RectTransform;
                    if (viewport == null)
                    {
                        viewport = NewRect(ViewportName, scroll);
                        Stretch(viewport);
                    }
                    if (viewport.GetComponent<RectMask2D>() == null)
                        viewport.gameObject.AddComponent<RectMask2D>();

                    if (content == null)
                    {
                        content = NewRect(ContentName, viewport);
                        content.anchorMin = new Vector2(0f, 1f);
                        content.anchorMax = new Vector2(1f, 1f);
                        content.pivot = new Vector2(0.5f, 1f);
                        content.anchoredPosition = Vector2.zero;
                        content.sizeDelta = Vector2.zero;
                    }

                    var vlg = content.GetComponent<VerticalLayoutGroup>();
                    if (vlg == null) vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
                    vlg.padding = new RectOffset(2, 2, 2, 2);
                    vlg.spacing = 2f;
                    vlg.childControlWidth = true;
                    vlg.childControlHeight = true;
                    vlg.childForceExpandWidth = true;
                    vlg.childForceExpandHeight = false;
                    vlg.childAlignment = TextAnchor.UpperLeft;

                    var fitter = content.GetComponent<ContentSizeFitter>();
                    if (fitter == null) fitter = content.gameObject.AddComponent<ContentSizeFitter>();
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                    var scrollRect = scroll.GetComponent<ScrollRect>();
                    if (scrollRect == null) scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
                    scrollRect.viewport = viewport;
                    scrollRect.content = content;
                    scrollRect.horizontal = false;
                    scrollRect.vertical = true;
                    scrollRect.movementType = ScrollRect.MovementType.Clamped;
                    scrollRect.elasticity = 0.08f;
                    scrollRect.inertia = true;
                    scrollRect.decelerationRate = 0.135f;
                    scrollRect.scrollSensitivity = 24f;
                    viewport.offsetMin = new Vector2(0f, 0f);
                    viewport.offsetMax = new Vector2(-10f, 0f);
                    EnsureScrollbar(scroll, scrollRect);

                    if (template == null)
                        template = BuildRowTemplate(content, font, material);
                }

                var empty = index.Find(EmptyName) as RectTransform;
                if (empty == null) empty = NewRect(EmptyName, index);
                empty.anchorMin = Vector2.zero;
                empty.anchorMax = Vector2.one;
                empty.offsetMin = new Vector2(4f, 4f);
                empty.offsetMax = new Vector2(-4f, -4f);
                var emptyText = empty.GetComponent<Text>();
                if (emptyText == null) emptyText = empty.gameObject.AddComponent<Text>();
                Style(emptyText, font, material, 12, Dim, TextAnchor.MiddleCenter);
                emptyText.text = "暂无提问";
                emptyText.raycastTarget = false;

                var finalScroll = index.Find(ScrollName) as RectTransform;
                if (finalScroll != null)
                {
                    finalScroll.offsetMin = new Vector2(4f, 4f);
                    finalScroll.offsetMax = new Vector2(-4f, -4f);
                    var finalViewport = finalScroll.Find(ViewportName) as RectTransform;
                    if (finalViewport != null) finalViewport.sizeDelta = new Vector2(-9f, 0f);
                    var finalScrollRect = finalScroll.GetComponent<ScrollRect>();
                    if (finalScrollRect != null) EnsureScrollbar(finalScroll, finalScrollRect);
                }
                var finalEmpty = index.Find(EmptyName) as RectTransform;
                if (finalEmpty != null)
                {
                    finalEmpty.offsetMin = new Vector2(4f, 4f);
                    finalEmpty.offsetMax = new Vector2(-4f, -4f);
                }

                var uiLayer = index.gameObject.layer == 0 ? 5 : index.gameObject.layer;
                SetLayer(index, uiLayer);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[IcarusII] chat-index prefab 结构已补齐：" + prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static RectTransform BuildRowTemplate(RectTransform content, Font font, Material material)
        {
            var row = NewRect(TemplateName, content);
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.anchoredPosition = Vector2.zero;
            row.sizeDelta = new Vector2(0f, 24f);
            var rowBg = row.gameObject.AddComponent<Image>();
            rowBg.color = Color.clear;
            rowBg.raycastTarget = true;
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 24f;
            le.preferredHeight = 24f;

            var link = NewRect("link", row);
            link.anchorMin = Vector2.zero;
            link.anchorMax = Vector2.one;
            link.offsetMin = new Vector2(8f, 0f);
            link.offsetMax = new Vector2(-34f, 0f);
            var linkHit = link.gameObject.AddComponent<Image>();
            linkHit.color = Color.clear;
            linkHit.raycastTarget = true;
            link.gameObject.AddComponent<RectMask2D>();

            var label = NewRect("label", link);
            Stretch(label);
            var linkText = label.gameObject.AddComponent<Text>();
            Style(linkText, font, material, Mathf.Max(10, 12), Accent, TextAnchor.MiddleLeft);
            linkText.text = "提问";
            linkText.supportRichText = false;
            linkText.horizontalOverflow = HorizontalWrapMode.Overflow;
            linkText.verticalOverflow = VerticalWrapMode.Truncate;
            linkText.wideCharSpacing = 1f;
            linkText.raycastTarget = false;

            // link 故意不加 Button：Button 会截获 pointerEnter，行级 hover 就收不到。
            // 点击由运行期 ChatIndexRowHover 统一处理；delete 仍使用自己的 Button。

            var del = NewRect("delete", row);
            del.anchorMin = new Vector2(1f, 0.5f);
            del.anchorMax = new Vector2(1f, 0.5f);
            del.pivot = new Vector2(1f, 0.5f);
            del.sizeDelta = new Vector2(24f, 24f);
            del.anchoredPosition = new Vector2(-2f, 0f);
            var delImage = del.gameObject.AddComponent<Image>();
            delImage.color = new Color(1f, 1f, 1f, 0.01f);
            delImage.raycastTarget = true;
            // delete 也不加 Button：点击由运行期 ChatIndexRowHover 按点击位置判断，
            // 这样 pointerEnter/Exit 不会被 Selectable 截断。

            var delText = NewRect("x", del);
            Stretch(delText);
            var delLabel = delText.gameObject.AddComponent<Text>();
            Style(delLabel, font, material, Mathf.Max(12, 14), Dim, TextAnchor.MiddleCenter);
            delLabel.text = "×";
            delLabel.raycastTarget = false;

            row.gameObject.SetActive(false);
            return row;
        }

        private static void EnsureScrollbar(RectTransform scroll, ScrollRect scrollRect)
        {
            var sb = scroll.Find("Scrollbar Vertical") as RectTransform;
            if (sb == null)
                sb = NewRect("Scrollbar Vertical", scroll);
            sb.anchorMin = new Vector2(1f, 0f);
            sb.anchorMax = new Vector2(1f, 1f);
            sb.pivot = new Vector2(1f, 1f);
            sb.sizeDelta = new Vector2(6f, 0f);
            sb.anchoredPosition = Vector2.zero;

            var track = sb.GetComponent<Image>();
            if (track == null) track = sb.gameObject.AddComponent<Image>();
            track.color = new Color(0f, 0f, 0f, 0.70f);
            track.raycastTarget = true;

            var sliding = sb.Find("Sliding Area") as RectTransform;
            if (sliding == null)
            {
                sliding = NewRect("Sliding Area", sb);
                Stretch(sliding);
            }
            var handle = sliding.Find("Handle") as RectTransform;
            if (handle == null)
            {
                handle = NewRect("Handle", sliding);
                Stretch(handle);
                handle.offsetMin = new Vector2(1f, 1f);
                handle.offsetMax = new Vector2(-1f, -1f);
            }
            var handleImage = handle.GetComponent<Image>();
            if (handleImage == null) handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = new Color(1f, 1f, 1f, 0.55f);
            handleImage.raycastTarget = true;

            var scrollbar = sb.GetComponent<Scrollbar>();
            if (scrollbar == null) scrollbar = sb.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = track;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };

            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scrollRect.verticalScrollbarSpacing = 2f;
        }

        private static void Style(Text text, Font font, Material material, int fontSize, Color color, TextAnchor anchor)
        {
            text.font = font;
            text.material = material;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = anchor;
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private static ColorBlock ButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.75f, 0.92f, 1f, 1f);
            colors.pressedColor = new Color(0.6f, 0.8f, 0.9f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            return colors;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            rt.gameObject.layer = parent != null ? parent.gameObject.layer : 5;
            return rt;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i), layer);
        }
    }
}
