using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 会话标题行：鼠标悬停时给标题加一层淡背景，并把右侧的时间标记换成置顶/删除按钮。
    ///
    /// 右侧那块位置是共享的：常态显示“最后回复时间”，悬停显示置顶 + 删除；
    /// 置顶之后常驻显示置顶按钮（时间不再出现），悬停时再补上删除。
    /// 背景色由 Mod 按当前主题设置。
    /// </summary>
    public sealed class ConversationHeaderHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Graphic Background;
        public Color HoverColor = new Color(0.35f, 0.75f, 1f, 0.10f);
        public GameObject DeleteButton;
        public GameObject PinButton;
        public GameObject TimeLabel;

        /// <summary>该会话是否有可显示的时间文本；没有时常态也不露出那块空位。</summary>
        public bool HasTime = true;
        public bool Pinned;

        private bool _hovered;

        public void SetPinned(bool pinned)
        {
            Pinned = pinned;
            Apply();
        }

        public void SetHasTime(bool hasTime)
        {
            HasTime = hasTime;
            Apply();
        }

        /// <summary>重建索引后第一次显示时用：按当前状态摆一遍右侧图标，不依赖 prefab 的默认显隐。</summary>
        public void Refresh()
        {
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

        private void OnDisable()
        {
            _hovered = false;
            Apply();
        }

        private void Apply()
        {
            if (Background != null) Background.color = _hovered ? HoverColor : Color.clear;
            SetActive(DeleteButton, _hovered);
            SetActive(PinButton, Pinned || _hovered);
            SetActive(TimeLabel, !Pinned && !_hovered && HasTime);
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }
    }
}
