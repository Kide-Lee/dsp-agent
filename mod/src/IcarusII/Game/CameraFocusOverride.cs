using UnityEngine;

namespace IcarusII.Game
{
    /// <summary>
    /// 建筑聚焦的临时相机覆盖。
    ///
    /// 游戏每帧会用 Player.cameraTarget 重建相机位姿，直接改 cameraTarget 会
    /// 被 PlayerController 立刻插值回去；这里在 onPreRender 最后一步把镜头
    /// 放到目标建筑上方，直到玩家撤销/换档。只移动相机，不移动玩家。
    /// </summary>
    internal static class CameraFocusOverride
    {
        private static Vector3? _target;
        private static float _distance = 40f;
        private static bool _subscribed;

        public static bool Active => _target.HasValue;

        public static void Focus(Vector3 worldPosition, float distance)
        {
            _target = worldPosition;
            _distance = Mathf.Max(8f, distance);
            if (_subscribed) return;
            Camera.onPreRender += OnPreRender;
            _subscribed = true;
        }

        public static void Clear()
        {
            _target = null;
            if (!_subscribed) return;
            Camera.onPreRender -= OnPreRender;
            _subscribed = false;
        }

        private static void OnPreRender(Camera camera)
        {
            if (!_target.HasValue || camera == null) return;
            var target = _target.Value;
            // 保持当前朝向，只把相机拉到目标上方；roll 不变。
            var forward = camera.transform.forward;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            camera.transform.position = target - forward.normalized * _distance;
            camera.transform.LookAt(target, Vector3.up);
        }
    }
}
