using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 只做 prefab 的**加法**：缺 delete 背景块就补一块，缺 tooltip 就补一块。
    /// 不覆盖用户已经在 prefab 里排好的窗口、正文、滚动条、索引行布局。
    /// 幂等，可重复执行。
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
                EnsureChatIndexAdditions(root.transform);
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

        private static void EnsureChatIndexAdditions(Transform root)
        {
            var index = root.Find("chat-index");
            var scroll = index != null ? index.Find("chat-index-scroll") : null;
            var viewport = scroll != null ? scroll.Find("viewport") : null;
            var content = viewport != null ? viewport.Find("content") : null;
            var delete = content != null ? content.Find("turn-row-template/delete") as RectTransform : null;
            if (delete == null) return;
            EnsureDeleteBackground(delete);
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
