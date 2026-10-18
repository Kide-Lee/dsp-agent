using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 把聊天窗需要的部件**加**到一份已有的窗口 prefab 上，**不动它原有的外框**。
    ///
    /// 为什么需要这个工具：`IcarusIIWindowPrefabBuilder` 是"整棵从代码重建"，
    /// 在手工维护过的 prefab 上会把人的改动整块冲掉（2026-10-16 真出过一次事故）。
    /// 所以约定是：
    ///
    ///   * **外框（shadow / panel-bg / border / glass / deco / 标题）由人维护**，
    ///     用这个工具往上加东西；
    ///   * 生成器只用于"从零造一份完全由代码定义的 prefab"，且默认拒绝覆盖已存在的文件。
    ///
    /// 这个工具**只做三件事**（幂等，可反复跑）：
    ///
    ///   1. 在根下加 `Header`（全透明，只有 `Status`；`Title` 不重复加——
    ///      标题用你外框里已有的那个 Text）；
    ///   2. 加 `Scroll`/`Viewport`/`Content`（聊天正文）；
    ///   3. 加 `InputBar`/`Input`：**整块照抄信标窗的 `to-do-list`**
    ///      （tool-bar、遮罩滚动态、多行 InputField、右侧滚动条，贴图/颜色/材质/锚点全抄）。
    ///
    ///   已存在同名节点会先删掉再建（所以重跑不会堆叠）；除此以外不改任何节点，
    ///   也不动面板材质。
    ///
    ///  另外会**剥掉游戏脚本**（`UIMechaWindow` / `UIWindowDrag` / `Localizer` /
    ///  `EventTrigger` / `UIBlockZone` / `UIButton` / `UIInputField` …）——
    ///  它们引用游戏自己的类型，在 mod 的 prefab 里跑起来只会报错。
    ///
    ///  **但图形组件一律保留**：`TranslucentImage`（面板底衬用的那个）本身是 `Image`
    ///  的子类，它负责把 Main Camera 上 `TranslucentImageSource` 的模糊贴图喂给
    ///  `UI/TranslucentImage` 着色器——按"脚本"删掉它，面板底色会跟着一起消失
    ///  （2026-10-16 踩过这个坑），而且窗口就和游戏其他窗口不一致了。
    ///
    /// 菜单：`Icarus II/Add Chat Parts To Prefab`（先在 Project 里选中 prefab）
    /// 批处理：`-executeMethod IcarusII.EditorTools.IcarusIIWindowTransplant.Transplant`
    /// </summary>
    public static class IcarusIIWindowTransplant
    {
        /// <summary>批处理/无选中时的默认目标。</summary>
        public const string DefaultPrefabPath = "Assets/IcarusII/ChatWindow.prefab";

        /// <summary>
        /// 判断某个组件要不要保留。**按类型（含继承）判断，不按名字**——
        /// 踩过一次坑：`panel-bg` 上的 `TranslucentImage` 本身就是 `Image` 的子类，
        /// 按名字白名单会把它当"脚本"删掉，等于把面板底色一起删了。
        /// </summary>
        private static bool ShouldKeep(Component c)
        {
            // 图形一律**原样保留**，包括非标准的子类：
            // `TranslucentImage`（原生窗口面板用的就是它）本身是 Image 的子类，
            // 而且它负责把 Main Camera 上的 `TranslucentImageSource` 模糊贴图喂给
            // `UI/TranslucentImage` 着色器——删掉它或换成普通 Image，面板就和游戏别的窗口不一致了。
            if (c is Graphic) return true;
            if (c is Selectable) return true;                    // Button/Toggle/Slider/Scrollbar/InputField/Dropdown
            if (c is ScrollRect) return true;
            if (c is Mask || c is RectMask2D) return true;
            if (c is ContentSizeFitter || c is LayoutElement) return true;
            if (c is LayoutGroup || c is AspectRatioFitter) return true;
            if (c is CanvasGroup) return true;
            if (c is CanvasScaler || c is GraphicRaycaster) return true;
            if (c is Shadow) return true;                        // Outline 继承 Shadow
            return false;
        }

        [MenuItem("Icarus II/Add Chat Parts To Prefab (选中 prefab)")]
        public static void TransplantFromMenu()
        {
            var path = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
            {
                EditorUtility.DisplayDialog("Icarus II",
                    "请先在 Project 窗口里选中要加部件的 prefab（例如 Assets/IcarusII/Mecha Window.prefab）。", "好");
                return;
            }

            string message;
            var ok = TransplantAt(path, out message);
            EditorUtility.DisplayDialog("Icarus II", message, "好");
        }

        /// <summary>批处理入口。</summary>
        public static void Transplant()
        {
            string message;
            var ok = TransplantAt(DefaultPrefabPath, out message);
            if (ok) Debug.Log("[IcarusII] " + message);
            else Debug.LogError("[IcarusII] " + message);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// 在 <paramref name="path"/> 上做加法。返回 false 时**没有写任何东西**。
        /// </summary>
        public static bool TransplantAt(string path, out string message)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                message = "读不到 prefab：" + path;
                return false;
            }

            // 先只读地检查一遍，确认这确实是一份原生窗口 prefab，避免乱加东西
            var probePanel = prefab.transform.Find("panel-bg");
            if (probePanel == null)
            {
                message = "这份 prefab 里没有 panel-bg 节点，看起来不是窗口 prefab，未做任何修改：" + path;
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var rootRt = root.GetComponent<RectTransform>();
                if (rootRt == null)
                {
                    message = "prefab 根不是 UI 节点（没有 RectTransform），未做修改：" + path;
                    return false;
                }

                // 幂等：上次加的三块先删掉（只删我们自己加的，名字固定）
                RemoveChild(root.transform, "Header");
                RemoveChild(root.transform, "Scroll");
                RemoveChild(root.transform, "InputBar");

                // 剥游戏脚本（外框节点本身不动，只摘掉 MonoBehaviour）
                var removed = new List<string>();
                var addedCloseButton = false;
                var stripped = StripGameScripts(root, removed);

                // 加法：Header(只有 Status) + 正文 + 信标待办输入框
                IcarusIIWindowPrefabBuilder.BuildHeader(rootRt, false);
                IcarusIIWindowPrefabBuilder.BuildTranscript(rootRt);
                IcarusIIWindowPrefabBuilder.BuildInputBar(rootRt);

                // 关闭按钮：**只在没有的时候加**。外框里本来就带 btn-box 的话（原生窗口就有），
                // 那份是人家手改/原生的，不要碰，运行期接一下点击即可。
                var panelNode = root.transform.Find("panel-bg");
                if (panelNode != null && panelNode.Find("btn-box") == null)
                {
                    IcarusIIWindowPrefabBuilder.BuildCloseButton(panelNode.GetComponent<RectTransform>());
                    addedCloseButton = true;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);

                message = string.Format(
                    "已在 {0} 上加好聊天窗部件：Header(Status) / Scroll(正文) / InputBar(抄自信标窗 to-do-list){3}。\n" +
                    "剥掉游戏脚本 {1} 个{2}。原有外框、标题、材质未改动。",
                    path, stripped,
                    removed.Count > 0 ? "（" + string.Join(", ", removed.ToArray()) + "）" : string.Empty,
                    addedCloseButton ? " / btn-box 关闭按钮" : "");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void RemoveChild(Transform root, string name)
        {
            var t = root.Find(name);
            if (t != null) Object.DestroyImmediate(t.gameObject);
        }

        /// <summary>
        /// 只保留 UGUI 组件，其余 MonoBehaviour（游戏脚本 / 事件桥）全部剥掉；
        /// 非标准图形换成普通 `Image`（保留贴图/类型/颜色/材质/射线），
        /// 避免"把图形当脚本删掉"。被处理的收集到 <paramref name="removed"/> 便于复核。
        /// </summary>
        private static int StripGameScripts(GameObject root, List<string> removed)
        {
            var count = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var behaviours = t.GetComponents<MonoBehaviour>();
                for (int i = 0; i < behaviours.Length; i++)
                {
                    var b = behaviours[i];
                    if (b == null) continue;                       // 缺失脚本引用

                    if (b is Graphic) continue;                    // 图形原样保留（见 ShouldKeep 注释）

                    if (ShouldKeep(b)) continue;

                    removed.Add(t.name + "→" + b.GetType().Name);
                    Object.DestroyImmediate(b);
                    count++;
                }
            }
            return count;
        }
    }
}
