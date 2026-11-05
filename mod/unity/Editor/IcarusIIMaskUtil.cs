using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 聊天的滚动/裁切容器统一用 **Mask（模板缓冲）**，不用 RectMask2D。
    ///
    /// 游戏文本着色器 `Unlit Text Alpha (YCGen)` 只有 `_Stencil` 系列属性、没有
    /// `_ClipRect`，RectMask2D 对文字是空操作：滚动列表里的消息、卡片描述一旦超长
    /// 会直接画到容器外面（实测思考正文溢到上面的工具块上）。
    ///
    /// Mask 需要同节点上有 Graphic 当遮罩模板，所以这里顺带保证一张（不显示的）Image。
    /// 输出区 viewport 用的是同一套做法。
    /// </summary>
    public static class IcarusIIMaskUtil
    {
        [MenuItem("Icarus II/Convert RectMask2D To Mask")]
        public static void ConvertMenu()
        {
            ConvertAll(IcarusIIThinkingClipper.PrefabPath);
        }

        /// <summary>把 prefab 里所有 RectMask2D 换成 Mask。返回处理的节点数。</summary>
        public static int ConvertAll(string prefabPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过遮罩转换：" + prefabPath);
                return 0;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return 0;

            try
            {
                var masks = root.GetComponentsInChildren<RectMask2D>(true);
                foreach (var mask in masks)
                    EnsureStencilMask(mask.transform as RectTransform);

                if (masks.Length == 0)
                {
                    Debug.Log("[IcarusII] prefab 里没有 RectMask2D，跳过：" + prefabPath);
                    return 0;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log($"[IcarusII] 已把 {masks.Length} 个 RectMask2D 换成 Mask：{prefabPath}");
                return masks.Length;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// 确保节点用 Mask 裁切。返回 true 表示改了东西。
        /// 已有 Image 的节点沿用那张图（只关掉遮罩图形的绘制），没有就补一张。
        /// </summary>
        public static bool EnsureStencilMask(RectTransform node)
        {
            if (node == null) return false;
            var changed = false;

            var rectMask = node.GetComponent<RectMask2D>();
            if (rectMask != null)
            {
                Object.DestroyImmediate(rectMask, true);
                changed = true;
            }

            var image = node.GetComponent<Image>();
            if (image == null)
            {
                image = node.gameObject.AddComponent<Image>();
                image.sprite = null;
                image.type = Image.Type.Simple;
                image.color = Color.white;
                image.raycastTarget = false;
                changed = true;
            }

            var mask = node.GetComponent<Mask>();
            if (mask == null)
            {
                // Image 必须先存在：Mask.OnEnable 要靠它当遮罩模板。
                mask = node.gameObject.AddComponent<Mask>();
                changed = true;
            }
            mask.showMaskGraphic = false;

            return changed;
        }
    }
}
