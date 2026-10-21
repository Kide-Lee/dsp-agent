using UnityEngine;

namespace IcarusII.UI
{
    /// <summary>
    /// 让 loading 图标（游戏自带的 loading 图）转起来。
    ///
    /// 只由 <see cref="ChatWindow"/> 在运行期挂到 prefab 的 spinner 节点上：
    /// 思考/工具运行中启用，结束后禁用并隐藏图标。
    /// </summary>
    internal sealed class LoadingSpinner : MonoBehaviour
    {
        /// <summary>负值 = 顺时针（与游戏 loading 的转向一致）。</summary>
        public float DegreesPerSecond = -180f;

        private RectTransform _rect;

        private void Awake()
        {
            _rect = transform as RectTransform;
        }

        private void OnEnable()
        {
            if (_rect == null) _rect = transform as RectTransform;
            if (_rect != null) _rect.localRotation = Quaternion.identity;
        }

        private void Update()
        {
            if (_rect == null) return;
            _rect.Rotate(0f, 0f, DegreesPerSecond * Time.unscaledDeltaTime, Space.Self);
        }
    }
}
