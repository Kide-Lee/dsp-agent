using System;
using IcarusII.Game;
using IcarusII.I18n;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// Game Menu（右下角环形菜单）里的聊天入口按钮。
    ///
    /// 为什么运行期注入：Game Menu 和它的截图键都直接写在 `DSPGame.unity` 里，
    /// 不是 Resources 下的 prefab；mod 无法在安装时改游戏场景。所以这里只做
    /// “找锚点 → 实例化 → 绑定点击”，按钮本身的静态结构/尺寸/样式全部来自
    /// `button-agent.prefab`（随 UI bundle 分发）。
    ///
    /// 位置以游戏自己的截图键为唯一依据：同一 x，抬到它正上方。
    /// 这样游戏调整菜单布局时不需要改这里的魔法坐标。
    /// </summary>
    internal static class AgentMenuButton
    {
        private const string InstanceName = "button-agent";

        /// <summary>
        /// 两个按钮**根节点**之间的垂直间距（Game Menu 本地坐标）。
        ///
        /// 根节点是 46×46，但真正可见的底图/圆环只有 30/32px（子节点内缩 8px）。
        /// 大圆按钮的根节点中心距 64.7、根尺寸 70 ⇒ 根间距约 -5.3；它们可见圆环
        /// 的间距约 8.7px。这里用 -5：本按钮与截图键的可见圆环间距约 9px、
        /// 可见底图间距约 11px，正好和大圆按钮一致。
        /// </summary>
        private const float Gap = -5f;

        private static GameObject _instance;
        private static Button _button;
        private static bool _warnedMissingPrefab;

        /// <summary>
        /// 确保入口按钮存在并可用。每帧调用；已存在时只做两次引用比较。
        /// </summary>
        public static bool Ensure(KeyCode hotkey, Action onOpen)
        {
            var host = GameRefs.GameMenuRoot;
            var anchor = GameRefs.GameMenuScreenshotButton;
            if (host == null || anchor == null) return false;

            if (_instance != null && _instance.transform.parent == host)
                return true;

            // 场景/UIRoot 重建后旧实例会变成 Unity 的“假 null”，这里统一清掉。
            DestroyInstance();

            var prefab = WindowPrefab.LoadAgentButton();
            if (prefab == null)
            {
                if (!_warnedMissingPrefab)
                {
                    _warnedMissingPrefab = true;
                    Plugin.Log.LogWarning(
                        "[UI] bundle 里没有 IcarusIIAgentButton：Game Menu 入口按钮不会出现。" +
                        "请重打 UI bundle（scripts/build-ui-bundle.sh）。");
                }
                return false;
            }

            var go = UnityEngine.Object.Instantiate(prefab, host, false);
            go.name = InstanceName;

            PlaceAbove(go.transform as RectTransform, anchor.transform as RectTransform);

            // 紧跟截图键之后：同一父节点下，绘制顺序也不会压到别的按钮。
            go.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex() + 1);

            _instance = go;
            _button = go.GetComponent<Button>();
            if (_button != null && onOpen != null)
                _button.onClick.AddListener(() => onOpen());

            BindTip(go, hotkey);

            // 一次性的位置日志：以后调间距/排查“按钮在哪”时不用再猜。
            var selfRt = go.transform as RectTransform;
            var anchorRt = anchor.transform as RectTransform;
            if (selfRt != null && anchorRt != null)
            {
                Plugin.Log.LogInfo(
                    $"[UI] Game Menu 入口位置：截图键 {anchorRt.sizeDelta} @ {anchorRt.anchoredPosition}，" +
                    $"入口 {selfRt.sizeDelta} @ {selfRt.anchoredPosition}。");
            }

            Plugin.Log.LogInfo("[UI] 已在 Game Menu 截图键上方添加聊天入口按钮。");
            return true;
        }

        /// <summary>插件卸载/窗口重建时清掉入口按钮（场景销毁时对象会自己没）。</summary>
        public static void Reset()
        {
            DestroyInstance();
            _warnedMissingPrefab = false;
        }

        private static void PlaceAbove(RectTransform rt, RectTransform anchor)
        {
            if (rt == null || anchor == null) return;

            // 锚点/枢轴跟随宿主按钮，保证“正上方”在任何分辨率下都成立；
            // 尺寸与样式仍以 prefab 为准，这里不改 sizeDelta。
            rt.anchorMin = anchor.anchorMin;
            rt.anchorMax = anchor.anchorMax;
            rt.pivot = anchor.pivot;
            rt.anchoredPosition = new Vector2(
                anchor.anchoredPosition.x,
                anchor.anchoredPosition.y + (anchor.rect.height + rt.rect.height) * 0.5f + Gap);
        }

        private static void BindTip(GameObject go, KeyCode hotkey)
        {
            try
            {
                var uiButton = go.GetComponent<UIButton>();
                if (uiButton == null) return;

                // 标题里的快捷键跟着配置走，不要留 prefab 里写死的 (R)：
                // General.Hotkey 允许玩家改成别的键。
                uiButton.tips.tipTitle = Strings.Get("menu.tip.title", hotkey.ToString());
                if (string.IsNullOrEmpty(uiButton.tips.tipText))
                    uiButton.tips.tipText = Strings.Get("menu.tip.text");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[UI] 入口按钮提示绑定失败：{ex.Message}");
            }
        }

        private static void DestroyInstance()
        {
            if (_button != null) _button.onClick.RemoveAllListeners();
            if (_instance != null) UnityEngine.Object.Destroy(_instance);
            _instance = null;
            _button = null;
        }
    }
}
