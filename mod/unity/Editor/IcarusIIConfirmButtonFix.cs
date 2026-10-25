using UnityEditor;
using UnityEngine;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 一次性修复：91f5ea5 重建 prefab 时把 choose 的独立 reject-button 丢了。
    /// 从 b91de6c 版本的旧 ChatWindow.prefab 里只复制这一个按钮子树到当前 prefab；
    /// 不重建窗口，不覆盖用户已调好的其它节点。
    /// </summary>
    public static class IcarusIIConfirmButtonFix
    {
        public const string OldPrefabPath = "Assets/IcarusII/ChatWindowOld.prefab";

        [MenuItem("Icarus II/Fix Confirm Reject Button")]
        public static void FixFromMenu()
        {
            var ok = Ensure("Assets/IcarusII/ChatWindow.prefab", OldPrefabPath);
            EditorUtility.DisplayDialog("Icarus II", ok ? "已补回 reject-button" : "修复失败，见 Console", "好");
        }

        public static void FixBatch()
        {
            var ok = Ensure("Assets/IcarusII/ChatWindow.prefab", OldPrefabPath);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool Ensure(string prefabPath, string templatePath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null ||
                AssetDatabase.LoadAssetAtPath<GameObject>(templatePath) == null)
            {
                Debug.LogWarning("[IcarusII] 缺少当前或旧 ChatWindow.prefab，无法补 reject-button");
                return false;
            }

            GameObject current = null;
            GameObject templateRoot = null;
            try
            {
                current = PrefabUtility.LoadPrefabContents(prefabPath);
                var group = FindRecursive(current.transform, "btn-grop");
                if (group == null)
                {
                    Debug.LogWarning("[IcarusII] 当前 prefab 找不到 btn-grop");
                    return false;
                }
                if (FindRecursive(group, "reject-button") != null)
                {
                    Debug.Log("[IcarusII] reject-button 已存在，跳过");
                    return true;
                }

                templateRoot = PrefabUtility.LoadPrefabContents(templatePath);
                var oldGroup = FindRecursive(templateRoot.transform, "btn-grop");
                var oldReject = oldGroup != null ? FindRecursive(oldGroup, "reject-button") : null;
                if (oldReject == null)
                {
                    Debug.LogWarning("[IcarusII] 旧 prefab 找不到 reject-button");
                    return false;
                }

                var clone = Object.Instantiate(oldReject.gameObject, group, false);
                clone.name = "reject-button";
                clone.transform.SetAsLastSibling();
                clone.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(current, prefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[IcarusII] 已从旧 prefab 补回 reject-button");
                return true;
            }
            finally
            {
                if (templateRoot != null) PrefabUtility.UnloadPrefabContents(templateRoot);
                if (current != null) PrefabUtility.UnloadPrefabContents(current);
            }
        }

        private static Transform FindRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
