using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 给 ChatWindow.prefab 的思考块补一层正文裁切框。
    ///
    /// 结构（幂等：已经有 body-clip 就跳过）：
    ///
    ///   thinking-block
    ///   ├── header
    ///   └── body-clip          RectMask2D + LayoutElement（运行期按输出区 2/3 限高）
    ///       └── body           Text，按底边对齐：新行从下往上顶，旧内容从框顶被裁掉
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
                    Debug.Log("[IcarusII] 思考块已有正文裁切框，跳过：" + prefabPath);
                    return true;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log($"[IcarusII] 已给 {patched} 个思考块补上正文裁切框：{prefabPath}");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>把 thinking-block 的 body 收进 body-clip。返回 false 表示已经是新结构或结构不符。</summary>
        public static bool Patch(RectTransform block)
        {
            if (block == null || block.Find(ClipName) != null) return false;
            var body = block.Find("body") as RectTransform;
            if (body == null)
            {
                Debug.LogWarning("[IcarusII] thinking-block 里找不到 body，跳过裁切框");
                return false;
            }

            var clip = new GameObject(ClipName, typeof(RectTransform), typeof(RectMask2D), typeof(LayoutElement));
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

            return true;
        }
    }
}
