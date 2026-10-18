using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 把聊天窗里原先由运行期补丁完成的布局固化到 prefab：
    ///  - output 底边贴 input 顶边（2px）；
    ///  - 正文滚动条向内收 8px；
    ///  - input 的 viewport/content 改成拉伸，避免每帧校正高度；
    ///  - delete 下补一个透明背景块，供 hover 染红；
    ///  - chat-index-tooltip 补进 prefab，运行期只负责显示/移动。
    ///
    /// 只改这些节点，不重建用户排好的其它 UI；幂等，可重复执行。
    /// </summary>
    public static class IcarusIIPrefabLayoutFix
    {
        private const string TooltipName = "chat-index-tooltip";

        public static bool Ensure(string prefabPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过布局固化：" + prefabPath);
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return false;

            try
            {
                FixOutputAndInput(root.transform);
                FixChatIndex(root.transform);
                EnsureTooltip(root.transform);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[IcarusII] 聊天窗布局已固化到 prefab：" + prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void FixOutputAndInput(Transform root)
        {
            var input = root.Find("input") as RectTransform;
            var output = root.Find("output") as RectTransform;
            if (input == null || output == null) return;

            var inputHeight = input.rect.height > 1f ? input.rect.height : input.sizeDelta.y;
            var inputTop = input.anchoredPosition.y + Mathf.Abs(inputHeight);
            var min = output.offsetMin;
            min.y = inputTop + 2f;
            output.offsetMin = min;

            var outputScroll = output.Find("scroll-view") as RectTransform;
            var outputViewport = outputScroll != null ? outputScroll.Find("viewport") as RectTransform : null;
            if (outputViewport != null)
            {
                // 正文 viewport 也必须是正常拉伸矩形。
                // 滚动条宽 6px、右边缘内收 8px；viewport 再留 4px 间隙，避免文字压在滚动条下。
                outputViewport.anchorMin = Vector2.zero;
                outputViewport.anchorMax = Vector2.one;
                outputViewport.pivot = new Vector2(0.5f, 0.5f);
                outputViewport.offsetMin = Vector2.zero;
                outputViewport.offsetMax = new Vector2(-18f, 0f);
            }

            var scrollbar = output.Find("scroll-view/Scrollbar Vertical") as RectTransform;
            if (scrollbar != null)
            {
                var pos = scrollbar.anchoredPosition;
                pos.x = -8f;
                scrollbar.anchoredPosition = pos;
            }

            var inputScroll = input.Find("editor/scroll-view") as RectTransform;
            var viewport = inputScroll != null ? inputScroll.Find("viewport") as RectTransform : null;
            if (viewport != null)
            {
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.pivot = new Vector2(0.5f, 0.5f);
                viewport.offsetMin = Vector2.zero;
                viewport.offsetMax = Vector2.zero;
            }

            var content = viewport != null ? viewport.Find("content") as RectTransform : null;
            if (content != null)
            {
                content.anchorMin = Vector2.zero;
                content.anchorMax = Vector2.one;
                content.pivot = new Vector2(0.5f, 0.5f);
                content.offsetMin = Vector2.zero;
                content.offsetMax = Vector2.zero;
            }
        }

        private static void FixChatIndex(Transform root)
        {
            var index = root.Find("chat-index");
            var scroll = index != null ? index.Find("chat-index-scroll") : null;
            var viewport = scroll != null ? scroll.Find("viewport") : null;
            var content = viewport != null ? viewport.Find("content") : null;
            var row = content != null ? content.Find("turn-row-template") as RectTransform : null;
            if (row == null) return;

            // 行必须横向铺满 content；否则无滚动条时 hover 底色右边会缺一截。
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.anchoredPosition = new Vector2(0f, -2f);
            row.sizeDelta = new Vector2(0f, 24f);

            // 链接右边界给删除区留 34px：24px 删除区 + 10px 间隙。
            var link = row.Find("link") as RectTransform;
            if (link != null)
            {
                link.anchorMin = Vector2.zero;
                link.anchorMax = Vector2.one;
                link.offsetMin = new Vector2(8f, 0f);
                link.offsetMax = new Vector2(-34f, 0f);
            }

            var delete = row.Find("delete") as RectTransform;
            if (delete != null)
            {
                delete.anchorMin = new Vector2(1f, 0f);
                delete.anchorMax = new Vector2(1f, 1f);
                delete.pivot = new Vector2(1f, 0.5f);
                delete.sizeDelta = new Vector2(24f, 0f);
                delete.anchoredPosition = new Vector2(-2f, 0f);
                EnsureDeleteBackground(delete);
            }
        }

        private static void EnsureDeleteBackground(RectTransform delete)
        {
            if (delete.GetComponent<Image>() != null) return;
            var existing = delete.Find("bg") as RectTransform;
            if (existing != null)
            {
                if (existing.GetComponent<Image>() == null)
                    existing.gameObject.AddComponent<Image>();
                return;
            }

            var bg = new GameObject("bg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = (RectTransform)bg.transform;
            rt.SetParent(delete, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetAsFirstSibling();
            rt.gameObject.layer = delete.gameObject.layer;
            var image = bg.GetComponent<Image>();
            image.color = Color.clear;
            image.raycastTarget = false;
        }

        private static void EnsureTooltip(Transform root)
        {
            var existing = root.Find(TooltipName) as RectTransform;
            if (existing != null) return;

            var titleText = root.Find("panel-bg/title-text") as RectTransform;
            var title = titleText != null ? titleText.GetComponent<Text>() : null;
            var font = title != null ? title.font : null;
            var material = title != null ? title.material : null;

            var rt = new GameObject(TooltipName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rt.transform.SetParent(root, false);
            rt.layer = 5;
            var rect = (RectTransform)rt.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0f);
            rect.sizeDelta = new Vector2(86f, 24f);
            rect.anchoredPosition = Vector2.zero;

            var bg = rt.GetComponent<Image>();
            bg.color = new Color(0.02f, 0.03f, 0.04f, 0.96f);
            bg.raycastTarget = false;
            var outline = rt.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 0.30f, 0.25f, 0.45f);
            outline.effectDistance = new Vector2(1f, -1f);

            var label = new GameObject("label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            label.transform.SetParent(rt.transform, false);
            label.layer = 5;
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(8f, 0f);
            lrt.offsetMax = new Vector2(-8f, 0f);
            var text = label.GetComponent<Text>();
            text.font = font;
            text.material = material;
            text.text = "删除";
            text.fontSize = 12;
            text.color = new Color(1f, 0.48f, 0.40f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;

            rt.gameObject.SetActive(false);
        }
    }
}
