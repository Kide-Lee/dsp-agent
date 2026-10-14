using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 运行时构建 UGUI 的小工具集。
    /// 游戏界面是用代码+预制体搭的，我们只能用代码搭，所以把这些样板集中起来。
    /// </summary>
    internal static class UiFactory
    {
        /// <summary>创建带 RectTransform 的节点；parent 为 null 时作为顶层对象（根 Canvas 必须这样建）。</summary>
        public static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            if (parent != null) rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        public static Image AddPanel(RectTransform rt, Color color)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            return img;
        }

        public static Text AddLabel(RectTransform rt, Font font, int fontSize, Color color, TextAnchor anchor)
        {
            var text = rt.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>拉伸填满父级，四边留出内边距。</summary>
        public static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>横向拉伸、固定在父级顶部，指定高度。</summary>
        public static void TopBar(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        /// <summary>横向拉伸、固定在父级底部，指定高度。</summary>
        public static void BottomBar(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        /// <summary>固定在父级某角，指定尺寸与像素偏移。</summary>
        public static void AnchoredTo(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 offset, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
        }

        /// <summary>确保场景里有 EventSystem，否则 UGUI 的输入框无法工作。</summary>
        public static void EnsureEventSystem()
        {
            try
            {
                if (UnityEngine.EventSystems.EventSystem.current != null) return;

                var existing = Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
                if (existing != null) return;

                var go = new GameObject("IcarusIIEventSystem");
                go.AddComponent<UnityEngine.EventSystems.EventSystem>();
                go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                Object.DontDestroyOnLoad(go);
                Plugin.Log.LogInfo("场景中缺少 EventSystem，已创建（聊天输入框需要它）。");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"创建 EventSystem 失败：{ex.Message}");
            }
        }
    }
}
