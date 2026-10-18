using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// chat-index 单行行为：hover 显示删除、行背景高亮、点击定位。
    /// 删除按钮本身的颜色/提示交给 prefab 上的原生 Button + UIButton 处理；
    /// 本组件只负责“行 hover 才显示删除”和行点击。
    /// </summary>
    public sealed class ChatIndexRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public GameObject DeleteButton;
        public Graphic HoverBackground;
        public Color HoverColor = new Color(0.35f, 0.75f, 1f, 0.10f);
        public Color SelectedColor = new Color(0.35f, 0.75f, 1f, 0.16f);

        public event Action Clicked;

        private bool _hovered;
        private bool _selected;

        public void SetSelected(bool selected)
        {
            _selected = selected;
            Apply();
        }

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
            if (DeleteButton != null) DeleteButton.SetActive(_hovered);
            if (HoverBackground != null)
                HoverBackground.color = _selected ? SelectedColor : (_hovered ? HoverColor : Color.clear);
        }
    }
}
