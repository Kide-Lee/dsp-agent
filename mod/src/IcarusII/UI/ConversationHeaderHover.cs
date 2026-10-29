using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 会话标题行：只有鼠标悬停时给标题加一层淡背景，其它情况保持透明。
    /// 背景色由 Mod 按当前主题设置。
    /// </summary>
    public sealed class ConversationHeaderHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Graphic Background;
        public Color HoverColor = new Color(0.35f, 0.75f, 1f, 0.10f);

        public void OnPointerEnter(PointerEventData eventData)
        {
            Apply(true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Apply(false);
        }

        private void OnDisable()
        {
            Apply(false);
        }

        private void Apply(bool hovered)
        {
            if (Background != null) Background.color = hovered ? HoverColor : Color.clear;
        }
    }
}
