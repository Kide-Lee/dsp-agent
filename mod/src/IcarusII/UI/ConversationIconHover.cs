using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 会话标题行上的小图标按钮：平时跟随标题颜色，鼠标移到按钮上换成强调色。
    /// 删除按钮悬停变红、置顶按钮悬停变白，颜色都由调用方给定。
    /// </summary>
    public sealed class ConversationIconHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Graphic Icon;
        public Color NormalColor = Color.white;
        public Color HoverColor = new Color(1f, 0.36f, 0.36f, 1f);

        private bool _hovered;

        public void SetNormal(Color color)
        {
            NormalColor = color;
            if (Icon != null && !_hovered) Icon.color = color;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            if (Icon != null) Icon.color = HoverColor;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            if (Icon != null) Icon.color = NormalColor;
        }

        private void OnDisable()
        {
            _hovered = false;
            if (Icon != null) Icon.color = NormalColor;
        }
    }
}
