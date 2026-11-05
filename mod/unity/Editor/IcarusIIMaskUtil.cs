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
        /// 节点上已有 Image 的，遮罩图形照常绘制（输入框底色不能丢）；没有的补一张无贴图 Image 并隐藏。
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
            var imageCreated = false;
            if (image == null)
            {
                image = node.gameObject.AddComponent<Image>();
                image.sprite = null;
                image.type = Image.Type.Simple;
                image.color = Color.white;
                image.raycastTarget = false;
                imageCreated = true;
                changed = true;
            }
            // 这张图原本就是画面的一部分吗（输入框底色）？透明占位图不算。
            var hadVisibleGraphic = image.color.a > 0.01f;

            var mask = node.GetComponent<Mask>();
            var maskCreated = mask == null;
            if (maskCreated)
            {
                // Image 必须先存在：Mask.OnEnable 要靠它当遮罩模板。
                mask = node.gameObject.AddComponent<Mask>();
                changed = true;
            }
            if (maskCreated || rectMask != null)
            {
                // 可见底图继续画；透明占位图/临时补的图关掉绘制，否则会盖一层白底。
                mask.showMaskGraphic = !imageCreated && hadVisibleGraphic;
            }

            // 不管是这次设的还是之前设的：隐藏绘制的遮罩图形都不能被剔除。
            // CanvasRenderer.cullTransparentMesh 会把全透明图形整个剔掉，模板不写，
            // 子节点全部通不过 _Stencil 测试 —— 设置页整页空白、左栏记录消失都是这个。
            if (!mask.showMaskGraphic)
            {
                if (image.color.a < 0.01f)
                {
                    image.color = new Color(image.color.r, image.color.g, image.color.b, 1f);
                    changed = true;
                }
                var canvasRenderer = node.GetComponent<CanvasRenderer>();
                if (canvasRenderer != null && canvasRenderer.cullTransparentMesh)
                {
                    canvasRenderer.cullTransparentMesh = false;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>把整份 prefab 里所有现有 Mask 的遮罩图形修一遍（治光秃秃的存量 prefab）。返回改动数量。</summary>
        public static int RepairAll(string prefabPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过遮罩修复：" + prefabPath);
                return 0;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return 0;

            try
            {
                var fixedCount = 0;
                foreach (var mask in root.GetComponentsInChildren<Mask>(true))
                {
                    if (EnsureStencilMask(mask.transform as RectTransform)) fixedCount++;
                }

                if (fixedCount == 0)
                {
                    Debug.Log("[IcarusII] 遮罩图形都不需要修：" + prefabPath);
                    return 0;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log($"[IcarusII] 已修 {fixedCount} 个遮罩图形：{prefabPath}");
                return fixedCount;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>列出会被剔除（模板不写）的遮罩，供构建后自检。返回空表示没有问题。</summary>
        public static string FindBrokenMasks(GameObject root)
        {
            if (root == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (var mask in root.GetComponentsInChildren<Mask>(true))
            {
                var image = mask.GetComponent<Image>();
                var canvasRenderer = mask.GetComponent<CanvasRenderer>();
                if (image == null) { sb.Append(mask.name + "(无 Image) "); continue; }
                if (!mask.showMaskGraphic && image.color.a < 0.01f && canvasRenderer != null && canvasRenderer.cullTransparentMesh)
                    sb.Append(mask.name + "(透明且会被剔除) ");
            }
            return sb.ToString();
        }
    }
}
