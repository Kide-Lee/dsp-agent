using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 开发用：把一份窗口 prefab 的层级与组件dump成文本，便于**离线核对**
    /// （节点名、锚点、贴图、颜色、材质、组件类型），也方便批处理模式里跑：
    ///
    ///   Unity -batchmode -nographics -quit -projectPath dsp-game \
    ///         -executeMethod IcarusII.EditorTools.IcarusIIDumpPrefab.DumpChatWindow \
    ///         -logFile /tmp/unity-dump.log
    ///
    /// 输出写到 <c>OUT_PATH</c>（默认仓库 .scratch 下），stdout 也打一份。
    /// mod 是按**节点名**找控件的，所以这个 dump 是改 mod 查找路径前的事实依据。
    /// </summary>
    public static class IcarusIIDumpPrefab
    {
        private const string PrefabPath = "Assets/IcarusII/ChatWindow.prefab";
        private const string OutPath = "/home/kidelee/Projects/dsp-agent/.scratch/chatwindow-dump.txt";

        /// <summary>批处理入口（默认 ChatWindow.prefab）。</summary>
        public static void DumpChatWindow()
        {
            var text = Dump(PrefabPath);
            File.WriteAllText(OutPath, text);
            Debug.Log(text);
            Debug.Log("[IcarusII] dump 已写入 " + OutPath);
            EditorApplication.Exit(0);
        }

        public static string Dump(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return "读不到 prefab: " + path;

            var sb = new StringBuilder();
            sb.AppendLine("prefab = " + path + "  (root=" + prefab.name + ", bytes=" +
                          (File.Exists(path) ? new FileInfo(path).Length : -1) + ")");
            sb.AppendLine();

            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                if (t == prefab.transform) continue;

                var relative = t.name;
                var cursor = t.parent;
                while (cursor != null && cursor != prefab.transform)
                {
                    relative = cursor.name + "/" + relative;
                    cursor = cursor.parent;
                }

                var line = new StringBuilder("  " + relative);

                var rt = t as RectTransform;
                if (rt != null)
                {
                    line.Append("  [").Append(rt.rect.width.ToString("0.#")).Append("x").Append(rt.rect.height.ToString("0.#"))
                        .Append(" a=").Append(rt.anchorMin.ToString("0.##")).Append("..").Append(rt.anchorMax.ToString("0.##"))
                        .Append(" p=").Append(rt.pivot.ToString("0.##"))
                        .Append(" pos=").Append(rt.anchoredPosition.ToString("0.#"))
                        .Append(" sd=").Append(rt.sizeDelta.ToString("0.#")).Append("]");
                }

                if (!t.gameObject.activeSelf) line.Append("  INACTIVE");

                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null)
                    {
                        line.Append("  {缺失脚本}");
                        continue;
                    }

                    var typeName = c.GetType().Name;
                    if (typeName == "RectTransform" || typeName == "CanvasRenderer") continue;

                    var text = c as Text;
                    var image = c as Image;
                    if (text != null)
                    {
                        line.Append("  Text('").Append(text.text).Append("' sz=").Append(text.fontSize)
                            .Append(" col=").Append(ColorUtility.ToHtmlStringRGBA(text.color))
                            .Append(" mat=").Append(text.material != null ? text.material.name : "null")
                            .Append(" font=").Append(text.font != null ? text.font.name : "NULL")
                            .Append(" align=").Append(text.alignment)
                            .Append(" rich=").Append(text.supportRichText)
                            .Append(")");
                    }
                    else if (image != null)
                    {
                        line.Append("  Image(").Append(typeName)
                            .Append(" sp=").Append(image.sprite != null ? image.sprite.name : "null")
                            .Append(" col=").Append(ColorUtility.ToHtmlStringRGBA(image.color))
                            .Append(" mat=").Append(image.material != null ? image.material.name : "null")
                            .Append(" type=").Append(image.type)
                            .Append(" ray=").Append(image.raycastTarget)
                            .Append(")");
                    }
                    else
                    {
                        line.Append("  <").Append(typeName);

                        var input = c as InputField;
                        if (input != null)
                        {
                            line.Append(" line=").Append(input.lineType)
                                .Append(" limit=").Append(input.characterLimit)
                                .Append(" text=").Append(input.textComponent != null ? input.textComponent.name : "null")
                                .Append(" ph=").Append(input.placeholder != null ? input.placeholder.name : "null");
                        }

                        var scroll = c as ScrollRect;
                        if (scroll != null)
                        {
                            line.Append(" viewport=").Append(scroll.viewport != null ? scroll.viewport.name : "null")
                                .Append(" content=").Append(scroll.content != null ? scroll.content.name : "null")
                                .Append(" vBar=").Append(scroll.verticalScrollbar != null ? scroll.verticalScrollbar.name : "null");
                        }

                        var button = c as Button;
                        if (button != null)
                        {
                            line.Append(" transition=").Append(button.transition)
                                .Append(" target=").Append(button.targetGraphic != null ? button.targetGraphic.name : "null")
                                .Append(" onClick=").Append(button.onClick.GetPersistentEventCount());
                        }

                        line.Append(">");
                    }
                }

                sb.AppendLine(line.ToString());
            }

            return sb.ToString();
        }
    }
}
