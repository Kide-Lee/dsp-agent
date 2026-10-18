using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 把游戏原生的图标/取色选择器抬到**聊天窗画布之上**。
    ///
    /// 为什么需要：聊天窗自建 Canvas 的 `sortingOrder = 30000`，而游戏的
    /// `Signal Tag Picker` / `Simple Color Picker` 挂在 `UI Root/Overlay Canvas`
    /// （sortingOrder = 0）。不改排序的话，选择器确实被"打开"了，但整块被
    /// 聊天窗盖住——看起来就像按钮没反应。
    ///
    /// 做法：给选择器根节点加一个**嵌套 Canvas**（`overrideSorting`）和一个
    /// 自己的 `GraphicRaycaster`（嵌套 Canvas 的图形不会再被父 Canvas 的
    /// raycaster 拾取），打开时排在 30001，关闭时自动还原。
    /// </summary>
    internal sealed class PickerCanvasBoost : MonoBehaviour
    {
        private Canvas _canvas;
        private GraphicRaycaster _raycaster;

        public void Apply(int order)
        {
            if (_canvas == null)
            {
                _canvas = gameObject.GetComponent<Canvas>();
                if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
            }

            // 嵌套 Canvas 要继承父 Canvas 的额外顶点通道（游戏用的是 TexCoord1），
            // 否则文字/图标的 UV1 会被裁掉。
            var parentCanvas = transform.parent != null ? transform.parent.GetComponentInParent<Canvas>() : null;
            if (parentCanvas != null)
            {
                _canvas.additionalShaderChannels = parentCanvas.additionalShaderChannels;
            }

            _canvas.overrideSorting = true;
            _canvas.sortingOrder = order;

            if (_raycaster == null)
            {
                _raycaster = gameObject.GetComponent<GraphicRaycaster>();
                if (_raycaster == null) _raycaster = gameObject.AddComponent<GraphicRaycaster>();
            }
            _raycaster.enabled = true;
        }

        private void OnEnable()
        {
            if (_canvas != null) _canvas.overrideSorting = true;
            if (_raycaster != null) _raycaster.enabled = true;
        }

        private void OnDisable()
        {
            // 选择器关闭后回到游戏原本的画布层级
            if (_canvas != null) _canvas.overrideSorting = false;
            if (_raycaster != null) _raycaster.enabled = false;
        }
    }
}
