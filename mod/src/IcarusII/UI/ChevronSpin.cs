using UnityEngine;

namespace IcarusII.UI
{
    /// <summary>
    /// 左栏会话标题的展开/收起三角：在 0° 与目标角度之间平滑旋转。
    /// 只改 RectTransform 的 Z 旋转，不碰其它布局。
    /// </summary>
    public sealed class ChevronSpin : MonoBehaviour
    {
        private const float Duration = 0.14f;
        private const float AngleEpsilon = 0.01f;

        private float _from;
        private float _to;
        private float _progress = 1f;
        private bool _initialized;

        /// <summary>设置目标角度（度）。false 时立即就位，true 时从当前角度转过去。</summary>
        public void SetAngle(float angle, bool animate)
        {
            if (!_initialized || !animate)
            {
                _initialized = true;
                _from = _to = angle;
                _progress = 1f;
                Apply(angle);
                return;
            }
            if (Mathf.Abs(Mathf.DeltaAngle(angle, _to)) < AngleEpsilon) return;

            var current = transform.localEulerAngles.z;
            if (current > 180f) current -= 360f;
            _from = current;
            _to = angle;
            _progress = 0f;
        }

        private void Update()
        {
            if (_progress >= 1f) return;
            _progress = Mathf.Min(1f, _progress + Time.unscaledDeltaTime / Duration);
            var eased = Mathf.SmoothStep(0f, 1f, _progress);
            Apply(Mathf.Lerp(_from, _to, eased));
        }

        private void Apply(float angle)
        {
            transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }
    }
}
