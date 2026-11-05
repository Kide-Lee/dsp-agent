using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 给 ChatWindow.prefab 的思考块补一层正文裁切框。
    ///
    /// 结构（幂等：已经有 body-clip 就只检查遮罩类型）：
    ///
    ///   thinking-block
    ///   ├── header
    ///   └── body-clip          Mask + LayoutElement（运行期按输出区 2/3 限高）
    ///       └── body           Text，按底边对齐：新行从下往上顶，旧内容从框顶被裁掉
    ///
    /// 裁切必须用 Mask（模板缓冲），不能用 RectMask2D：游戏文本着色器
    /// `Unlit Text Alpha (YCGen)` 只有 _Stencil 系列属性，没有 _ClipRect，
    /// RectMask2D 对它是空操作（实测溢出的思考正文直接画到上面的块上）。
    ///
    /// 只加这一层，不动 header/accent/字体/颜色；已在 Unity 里调好的其它节点保持不变。
    /// 工具块（tool-template）不需要裁切，这里只认 thinking-block。
    /// </summary>
    public static class IcarusIIThinkingClipper
    {
        public const string PrefabPath = "Assets/IcarusII/ChatWindow.prefab";

        /// <summary>裁切框还没被运行期限过时的占位高度（3~4 行正文）。</summary>
        private const float DefaultClipHeight = 120f;

        private const string ClipName = "body-clip";

        [MenuItem("Icarus II/Ensure Thinking Body Clip")]
        public static void EnsureFromMenu()
        {
            Ensure(PrefabPath);
        }

        /// <summary>确保 prefab 里所有 thinking-block 都有正文裁切框。返回 false 表示 prefab 打不开。</summary>
        public static bool Ensure(string prefabPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogWarning("[IcarusII] 找不到 prefab，跳过思考块裁切框：" + prefabPath);
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return false;

            try
            {
                var patched = 0;
                foreach (var block in root.GetComponentsInChildren<RectTransform>(true))
                {
                    if (block.name == "thinking-block" && Patch(block)) patched++;
                }

                if (patched == 0)
                {
                    Debug.Log("[IcarusII] 思考块正文裁切框已是最新结构，跳过：" + prefabPath);
                    return true;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log($"[IcarusII] 已更新 {patched} 个思考块的正文裁切框：{prefabPath}");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// 把 thinking-block 的 body 收进 body-clip。返回 true 表示改了 prefab。
        /// 已有裁切框时只升级遮罩（旧版本用的是对文本无效的 RectMask2D）。
        /// </summary>
        public static bool Patch(RectTransform block)
        {
            if (block == null) return false;

            var existing = block.Find(ClipName) as RectTransform;
            if (existing != null) return EnsureMask(existing);

            var body = block.Find("body") as RectTransform;
            if (body == null)
            {
                Debug.LogWarning("[IcarusII] thinking-block 里找不到 body，跳过裁切框");
                return false;
            }

            var clip = new GameObject(ClipName, typeof(RectTransform), typeof(LayoutElement));
            var clipRect = (RectTransform)clip.transform;
            clipRect.SetParent(block, false);
            clipRect.anchorMin = new Vector2(0f, 1f);
            clipRect.anchorMax = new Vector2(1f, 1f);
            clipRect.pivot = new Vector2(0.5f, 1f);
            clipRect.anchoredPosition = Vector2.zero;
            clipRect.sizeDelta = new Vector2(0f, DefaultClipHeight);
            clip.layer = block.gameObject.layer;

            var layout = clip.GetComponent<LayoutElement>();
            layout.minHeight = 0f;
            layout.preferredHeight = DefaultClipHeight;
            layout.flexibleHeight = 0f;

            body.SetParent(clipRect, false);
            body.anchorMin = new Vector2(0f, 0f);
            body.anchorMax = new Vector2(1f, 0f);
            body.pivot = new Vector2(0.5f, 0f);
            body.anchoredPosition = Vector2.zero;
            body.sizeDelta = Vector2.zero;

            var fitter = body.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = body.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            EnsureMask(clipRect);
            return true;
        }

        /// <summary>
        /// 保证裁切框用 Mask（模板缓冲）而不是 RectMask2D。返回 true 表示做了改动。
        /// Mask 要求同一节点上有 Graphic：用一张无贴图的 Image 当遮罩模板，图形本身不显示。
        /// </summary>
        private static bool EnsureMask(RectTransform clip)
        {
            var changed = false;

            var rectMask = clip.GetComponent<RectMask2D>();
            if (rectMask != null)
            {
                Object.DestroyImmediate(rectMask, true);
                changed = true;
            }

            var image = clip.GetComponent<Image>();
            if (image == null)
            {
                image = clip.gameObject.AddComponent<Image>();
                changed = true;
            }
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.color = Color.white;
            image.raycastTarget = false;

            var mask = clip.GetComponent<Mask>();
            if (mask == null)
            {
                // Image 必须先存在，Mask.OnEnable 要靠它当遮罩模板。
                mask = clip.gameObject.AddComponent<Mask>();
                changed = true;
            }
            mask.showMaskGraphic = false;

            return changed;
        }
    }
}
