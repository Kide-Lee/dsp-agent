using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// chat-index 单行行为：行背景高亮与点击定位。
    ///
    /// 背景只表示“鼠标在这行上”。右栏当前看到的那条记录用字体颜色区分（见 ChatWindow），
    /// 不再用常驻底色——常驻底色会被误认成“被选中”。
    /// </summary>
    public sealed class ChatIndexRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public Graphic HoverBackground;
        public Color HoverColor = new Color(0.35f, 0.75f, 1f, 0.10f);

        public event Action Clicked;

        private bool _hovered;

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            Apply();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            Apply();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
                Clicked?.Invoke();
        }

        private void OnDisable()
        {
            _hovered = false;
            Apply();
        }

        private void Apply()
        {
            if (HoverBackground != null) HoverBackground.color = _hovered ? HoverColor : Color.clear;
        }
    }
}
