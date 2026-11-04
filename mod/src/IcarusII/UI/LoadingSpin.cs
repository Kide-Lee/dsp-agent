using UnityEngine;

namespace IcarusII.UI
{
    /// <summary>
    /// 会话列表里的 loading 图标：只在状态为“回答中”时持续旋转。
    /// 停止时回到 0°，避免下次显示时从任意角度开始。
    /// </summary>
    public sealed class LoadingSpin : MonoBehaviour
    {
        private const float DegreesPerSecond = 220f;

        private bool _spinning;

        public void SetSpinning(bool spinning)
        {
            if (_spinning == spinning) return;
            _spinning = spinning;
            if (!spinning)
                transform.localRotation = Quaternion.identity;
        }

        private void Update()
        {
            if (!_spinning) return;
            transform.Rotate(0f, 0f, -DegreesPerSecond * Time.unscaledDeltaTime);
        }
    }
}
