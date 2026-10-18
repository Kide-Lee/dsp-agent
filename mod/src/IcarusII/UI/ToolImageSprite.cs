using UnityEngine;

namespace IcarusII.UI
{
    /// <summary>
    /// 工具卡截图的生命周期宿主。
    ///
    /// 为什么需要一个组件：聊天记录会被 <c>TrimHistory</c> / <c>ClearTranscript</c> /
    /// <c>RemoveBlock</c> 从不同路径销毁，只有挂在节点上的 <see cref="OnDestroy"/>
    /// 能保证 <see cref="Texture2D"/> 与 <see cref="Sprite"/> 一定被释放——
    /// 截图是原生纹理，漏掉就是几百 KB 的常驻内存。
    /// </summary>
    internal sealed class ToolImageSprite : MonoBehaviour
    {
        private Texture2D _texture;
        private Sprite _sprite;

        /// <summary>接管一对纹理/精灵；旧的会先释放。</summary>
        public void Adopt(Texture2D texture, Sprite sprite)
        {
            Release();
            _texture = texture;
            _sprite = sprite;
        }

        private void OnDestroy()
        {
            Release();
        }

        private void Release()
        {
            // Sprite 先于 Texture 释放；反过来的话 Sprite 会持有已销毁的纹理。
            if (_sprite != null)
            {
                Destroy(_sprite);
                _sprite = null;
            }
            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }
        }
    }
}
