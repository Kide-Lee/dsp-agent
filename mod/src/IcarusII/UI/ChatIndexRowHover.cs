using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// chat-index 单行 hover 行为：
    ///  - 普通状态只显示提问链接；
    ///  - 鼠标进入该行才显示右侧删除按钮，并给行背景一点高亮；
    ///  - 选中行保持高亮，但删除按钮仍然只在 hover 时出现。
    /// </summary>
    public sealed class ChatIndexRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public GameObject DeleteButton;
        public RectTransform DeleteArea;
        public Graphic HoverBackground;
        public Color HoverColor = new Color(0.35f, 0.75f, 1f, 0.10f);
        public Color SelectedColor = new Color(0.35f, 0.75f, 1f, 0.16f);

        public event Action Clicked;
        public event Action DeleteClicked;

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
            if (eventData.button != PointerEventData.InputButton.Left) return;

            var cam = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
            if (DeleteArea != null && DeleteArea.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(DeleteArea, eventData.position, cam))
            {
                DeleteClicked?.Invoke();
                return;
            }
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
