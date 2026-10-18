using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 把游戏原生的**悬浮提示**（按钮提示 / 物品提示 / 弹出菜单）抬到聊天窗画布之上。
    ///
    /// 为什么需要：聊天窗是嵌在 `Overlay Canvas` 里的嵌套 Canvas
    /// （`overrideSorting = 30000`），而游戏的提示挂在
    /// `In Game/Top Tips/Button Tips`（同一个根 Canvas，sortingOrder = 0）。
    /// 于是提示画出来了，却被聊天窗盖住一半——实机表现就是"提示从中间被切断、
    /// 文字缺了前半截"（截图里设置按钮那条提示正是如此）。
    ///
    /// 做法与 `PickerCanvasBoost` 一致：给**提示容器**加一个嵌套 Canvas
    /// （`overrideSorting`）和一个自己的 `GraphicRaycaster`
    /// （嵌套 Canvas 的图形不再被父 Canvas 的 raycaster 拾取，不加会丢点击），
    /// 排序排在窗口之上。
    ///
    /// 只动提示容器，不碰 HUD / 游戏窗口，避免改变其它 UI 的层级与点击行为。
    /// </summary>
    internal static class NativeTipLayer
    {
        /// <summary>聊天窗画布是 30000，提示排在它上面一格。</summary>
        public const int Order = 30001;

        private static bool _logged;

        /// <summary>
        /// 幂等：重复调用只做检查。窗口/场景重建后容器会换新对象，
        /// 这里按"当前对象上是否已经是我们设过的嵌套 Canvas"判断，需要时重新抬一次。
        /// </summary>
        public static void Ensure(UIRoot uiRoot)
        {
            if (uiRoot == null) return;

            Canvas root;
            try { root = uiRoot.overlayCanvas; }
            catch { root = null; }
            if (root == null) return;

            var boosted = 0;
            boosted += Boost(root, SafeTransform(() => uiRoot.buttonTipTransform), "按钮提示", false);
            boosted += Boost(root, SafeTransform(() => uiRoot.itemTipTransform), "物品提示", false);
            boosted += Boost(root, SafeTransform(() => uiRoot.genericMenuTransform), "弹出菜单", true);
            boosted += Boost(root, SafeTransform(() => uiRoot.buttonTopTipTransform), "顶层提示", false);

            if (boosted > 0 && !_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo(
                    $"[UI] 原生提示层已抬到 {Order}（聊天窗 30000），共 {boosted} 个容器；" +
                    "提示不会再被窗口切掉。");
            }
        }

        private static RectTransform SafeTransform(System.Func<RectTransform> getter)
        {
            try { return getter(); }
            catch { return null; }
        }

        /// <summary>返回 1 表示这次真的改了，0 表示已就绪 / 不可用。</summary>
        private static int Boost(Canvas root, RectTransform target, string label, bool needRaycast)
        {
            if (target == null) return 0;

            var canvas = target.GetComponent<Canvas>();
            if (canvas != null && canvas.overrideSorting && canvas.sortingOrder == Order)
            {
                // 已经抬过（也可能宿主 Canvas 的额外顶点通道变了，顺手对齐一次）
                canvas.additionalShaderChannels = root.additionalShaderChannels;
                return 0;
            }

            try
            {
                if (canvas == null) canvas = target.gameObject.AddComponent<Canvas>();

                // 嵌套 Canvas 要继承父 Canvas 的额外顶点通道（游戏用的是 TexCoord1），
                // 否则文字/图标的 UV1 会被裁掉（和 PickerCanvasBoost 同一个坑）。
                canvas.additionalShaderChannels = root.additionalShaderChannels;
                canvas.overrideSorting = true;
                canvas.sortingOrder = Order;

                // 嵌套 Canvas 的图形不会再被父 Canvas 的 raycaster 拾取。
                //   * 纯提示：**故意不加** raycaster —— 提示不能挡住鼠标，
                //     否则"提示出现在光标下 → 按钮丢悬停 → 提示消失"会来回抖。
                //   * 弹出菜单：是按钮列表，少了这一层会"看得见、点不到"。
                if (needRaycast)
                {
                    var raycaster = target.GetComponent<GraphicRaycaster>();
                    if (raycaster == null) raycaster = target.gameObject.AddComponent<GraphicRaycaster>();
                    raycaster.enabled = true;
                }

                Plugin.Log.LogInfo(
                    $"[UI] 提示容器「{label}」→ sortingOrder {Order}" + (needRaycast ? "（带 raycaster）" : ""));
                return 1;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[UI] 提升提示容器「{label}」失败：{ex.Message}");
                return 0;
            }
        }
    }
}
