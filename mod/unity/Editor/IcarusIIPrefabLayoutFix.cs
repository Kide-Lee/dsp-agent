using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 只做 prefab 的**加法**：缺 delete 背景块就补一块，供原生 UIButton 使用。
    /// 不覆盖用户已经在 prefab 里排好的窗口、正文、滚动条、索引行布局。
    /// 幂等，可重复执行。
    /// </summary>
    public static class IcarusIIPrefabLayoutFix
    {
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
                EnsureContentColumn(root.transform);
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

        /// <summary>
        /// 把 output/input/choose 放进一列，由 VerticalLayoutGroup 按当前 active 的
        /// 底部面板 preferredHeight 自动分配高度。运行期只切 active，不写 RectTransform。
        /// 已有 content-column 时只校正布局，不重建子节点。
        /// </summary>
        private static void EnsureContentColumn(Transform root)
        {
            var column = root.Find("content-column") as RectTransform;
            if (column == null)
            {
                var go = new GameObject("content-column", typeof(RectTransform), typeof(VerticalLayoutGroup));
                column = (RectTransform)go.transform;
                column.SetParent(root, false);
                column.SetAsLastSibling();
                column.anchorMin = Vector2.zero;
                column.anchorMax = Vector2.one;
                column.pivot = new Vector2(0.5f, 0.5f);
                column.offsetMin = new Vector2(300f, 40f);
                column.offsetMax = new Vector2(-40f, -64f);
            }

            var layout = column.GetComponent<VerticalLayoutGroup>();
            if (layout == null) layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            MoveInto(column, root, "output", 0, flexibleHeight: true, preferredHeight: 0f);
            MoveInto(column, root, "input", 1, flexibleHeight: false, preferredHeight: 100f);
            MoveInto(column, root, "choose", 2, flexibleHeight: false, preferredHeight: 200f);
        }

        private static void MoveInto(RectTransform column, Transform root, string name,
            int siblingIndex, bool flexibleHeight, float preferredHeight)
        {
            var child = root.Find(name) as RectTransform;
            if (child == null) child = column.Find(name) as RectTransform;
            if (child == null) return;
            if (child.parent != column) child.SetParent(column, false);
            child.SetSiblingIndex(siblingIndex);
            child.anchorMin = Vector2.zero;
            child.anchorMax = Vector2.one;
            child.pivot = new Vector2(0.5f, 0.5f);
            child.anchoredPosition = Vector2.zero;
            child.sizeDelta = Vector2.zero;

            var element = child.GetComponent<LayoutElement>();
            if (element == null) element = child.gameObject.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.flexibleHeight = flexibleHeight ? 1f : 0f;
            element.preferredHeight = preferredHeight;
            element.minHeight = flexibleHeight ? 0f : preferredHeight;
            element.flexibleWidth = 1f;
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
            var direct = delete.GetComponent<Image>();
            if (direct != null)
            {
                direct.raycastTarget = true;
                return;
            }
            var existing = delete.Find("bg") as RectTransform;
            if (existing != null)
            {
                var existingImage = existing.GetComponent<Image>();
                if (existingImage == null) existingImage = existing.gameObject.AddComponent<Image>();
                existingImage.raycastTarget = true;
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
            // 原生 Button 需要 Graphic 作为 raycast target。
            image.raycastTarget = true;
        }

    }
}
