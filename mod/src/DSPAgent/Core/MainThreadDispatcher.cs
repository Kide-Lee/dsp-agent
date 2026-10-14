using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DSPAgent.Core
{
    /// <summary>
    /// 把后台线程（WebSocket 收帧、进程回调）的工作搬回 Unity 主线程。
    ///
    /// 规则：**任何触碰 UnityEngine API 的代码都不得在后台线程执行**。
    /// sidecar 客户端的事件全部在后台线程触发，所以统一 Post 到这里，
    /// 再由 Plugin.Update 每帧限量排空（避免长任务卡帧）。
    /// </summary>
    internal sealed class MainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly Stopwatch _watch = new Stopwatch();

        public int Pending => _queue.Count;

        /// <summary>投递一个主线程任务。可从任意线程调用。</summary>
        public void Post(Action action)
        {
            if (action != null) _queue.Enqueue(action);
        }

        /// <summary>
        /// 在给定时间预算内排空队列。返回本次处理的任务数。
        /// 预算用尽时剩下的任务留到下一帧，保证不长时间阻塞渲染。
        /// </summary>
        public int Drain(int budgetMs, Action<Exception> onError = null)
        {
            int processed = 0;
            _watch.Restart();

            while (_watch.ElapsedMilliseconds < budgetMs && _queue.TryDequeue(out var action))
            {
                processed++;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // 单个任务失败绝不能影响游戏主循环
                    onError?.Invoke(ex);
                }
            }

            _watch.Stop();
            return processed;
        }

        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
