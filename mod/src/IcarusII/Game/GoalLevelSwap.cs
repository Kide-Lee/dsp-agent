using System;

namespace IcarusII.Game
{
    /// <summary>
    /// 显示方法期间的等级临时覆盖：嵌套调用只记深度，最外层退出时给出要恢复的值。
    /// 纯状态机，不依赖 UnityEngine，便于离线回归。
    /// </summary>
    internal sealed class GoalLevelSwap
    {
        private int _depth;
        private int _saved;

        public int Depth => _depth;

        /// <summary>进入一层；返回要写入的临时值，嵌套层返回 null 表示不需要再写。</summary>
        public int? Begin(int current, int temporary)
        {
            _depth++;
            if (_depth > 1) return null;
            _saved = current;
            return temporary;
        }

        /// <summary>退出一层；最外层退出时返回要恢复的等级，其余层返回 null。</summary>
        public int? End()
        {
            if (_depth <= 0) return null;
            _depth--;
            return _depth == 0 ? _saved : (int?)null;
        }
    }
}
