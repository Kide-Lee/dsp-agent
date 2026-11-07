using System;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 参数/前置条件错误：分发器把它转成 `invalid_args`，而不是 `internal`。
    ///
    /// 分帧任务在 Finish() 阶段才真正执行，异常落在那里；如果一律按 internal 报，
    /// 模型会把「参数写错了」当成「插件出 bug」，从而反复重试同一个调用。
    /// </summary>
    internal sealed class ArgumentErrorException : Exception
    {
        public ArgumentErrorException(string message) : base(message) { }
    }
}
