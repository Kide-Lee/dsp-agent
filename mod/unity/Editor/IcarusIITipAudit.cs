using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>诊断：列出 ChatWindow/AgentButton 里所有 UIButton 的 tips 文案。</summary>
    public static class IcarusIITipAudit
    {
        [MenuItem("Icarus II/Audit Button Tips")]
        public static void DumpFromMenu()
        {
            var path = Dump();
            EditorUtility.DisplayDialog("Icarus II", "已写入 " + path, "好");
        }

        public static void DumpBatch()
        {
            Dump();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static string Dump()
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            bool first = true;
            AppendPrefab(sb, "Assets/IcarusII/ChatWindow.prefab", ref first);
            AppendPrefab(sb, "Assets/IcarusII/ChatWindowOld.prefab", ref first);
            AppendPrefab(sb, "Assets/IcarusII/button-agent.prefab", ref first);
            sb.Append("\n]\n");
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.deps/ui-button-tips.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[IcarusII] UIButton tips: " + path);
            return path;
        }

        private static void AppendPrefab(StringBuilder sb, string prefabPath, ref bool first)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null) return;
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var buttons = root.GetComponentsInChildren<UIButton>(true);
                for (int i = 0; i < buttons.Length; i++)
                {
                    var b = buttons[i];
                    if (b == null) continue;
                    if (!first) sb.Append(",\n");
                    first = false;
                    var tr = b.transform;
                    var label = tr.GetComponentInChildren<Text>(true);
                    sb.Append("{\"prefab\":\"").Append(Escape(prefabPath)).Append("\"")
                      .Append(",\"path\":\"").Append(Escape(NodePath(tr))).Append("\"")
                      .Append(",\"label\":\"").Append(Escape(label != null ? label.text : "")).Append("\"")
                      .Append(",\"tipTitle\":\"").Append(Escape(b.tips.tipTitle)).Append("\"")
                      .Append(",\"tipText\":\"").Append(Escape(b.tips.tipText)).Append("\"")
                      .Append(",\"titleFormat\":\"").Append(Escape(b.tipTitleFormatString)).Append("\"")
                      .Append(",\"textFormat\":\"").Append(Escape(b.tipTextFormatString)).Append("\"")
                      .Append(",\"type\":").Append((int)b.tips.type)
                      .Append(",\"itemId\":").Append(b.tips.itemId)
                      .Append(",\"corner\":").Append(b.tips.corner)
                      .Append(",\"level\":").Append(b.tips.level)
                      .Append(",\"topLevel\":").Append(b.tips.topLevel ? "true" : "false")
                      .Append(",\"delay\":").Append(b.tips.delay)
                      .Append(",\"width\":").Append(b.tips.width)
                      .Append("}");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string NodePath(Transform t)
        {
            if (t == null) return "";
            var s = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                s = t.name + "/" + s;
            }
            return s;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }
    }
}
