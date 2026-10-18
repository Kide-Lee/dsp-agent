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
