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
    public sealed class ChatIndexRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, IPointerMoveHandler
    {
        public GameObject DeleteButton;
        public RectTransform DeleteArea;
        public Graphic DeleteIcon;
        public Graphic HoverBackground;
        public Color HoverColor = new Color(0.35f, 0.75f, 1f, 0.10f);
        public Color SelectedColor = new Color(0.35f, 0.75f, 1f, 0.16f);
        public Color WarningColor = new Color(1f, 0.30f, 0.25f, 1f);

        public event Action Clicked;
        public event Action DeleteClicked;
        /// <summary>(是否在删除区域上, 鼠标屏幕坐标)。用于设置红色和显示提示。</summary>
        public event Action<bool, Vector2> DeleteHoverChanged;

        private bool _hovered;
        private bool _selected;
        private bool _deleteHovered;
        private bool _deleteIconColorCaptured;
        private Color _deleteIconNormalColor = Color.white;

        public void SetDeleteIcon(Graphic icon)
        {
            DeleteIcon = icon;
            if (icon == null) return;
            _deleteIconNormalColor = icon.color;
            _deleteIconColorCaptured = true;
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            Apply();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            Apply();
            UpdateDeleteHover(eventData.position, eventData.pressEventCamera, eventData.enterEventCamera);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            SetDeleteHover(false, eventData.position);
            Apply();
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            UpdateDeleteHover(eventData.position, eventData.pressEventCamera, eventData.enterEventCamera);
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
            SetDeleteHover(false, Vector2.zero);
            Apply();
        }

        private void UpdateDeleteHover(Vector2 screenPosition, Camera pressCamera, Camera enterCamera)
        {
            var cam = pressCamera != null ? pressCamera : enterCamera;
            var over = DeleteButton != null && DeleteButton.activeInHierarchy &&
                       DeleteArea != null &&
                       RectTransformUtility.RectangleContainsScreenPoint(DeleteArea, screenPosition, cam);
            SetDeleteHover(over, screenPosition);
        }

        private void SetDeleteHover(bool over, Vector2 screenPosition)
        {
            if (DeleteIcon != null)
            {
                if (over)
                {
                    if (!_deleteIconColorCaptured)
                    {
                        _deleteIconNormalColor = DeleteIcon.color;
                        _deleteIconColorCaptured = true;
                    }
                    DeleteIcon.color = WarningColor;
                }
                else if (_deleteIconColorCaptured)
                {
                    DeleteIcon.color = _deleteIconNormalColor;
                }
            }

            if (over)
            {
                DeleteHoverChanged?.Invoke(true, screenPosition);
            }
            else if (_deleteHovered)
            {
                DeleteHoverChanged?.Invoke(false, screenPosition);
            }
            _deleteHovered = over;
        }

        private void Apply()
        {
            if (DeleteButton != null)
            {
                var show = _hovered;
                DeleteButton.SetActive(show);
                if (!show) SetDeleteHover(false, Vector2.zero);
            }
            if (HoverBackground != null)
                HoverBackground.color = _selected ? SelectedColor : (_hovered ? HoverColor : Color.clear);
        }
    }
}
