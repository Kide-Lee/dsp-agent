using IcarusII.Game;

namespace IcarusII.Tests
{
    /// <summary>
    /// 目标面板等级覆盖的状态机回归：嵌套只记深度，最外层退出必须还原成进入前的值。
    /// 这个值会写回 gameDesc.goalLevel 并随存档保存，恢复错了会改掉玩家的“目标建议”。
    /// </summary>
    internal static class GoalPanelSwapTests
    {
        public static void Run()
        {
            var swap = new GoalLevelSwap();

            Program.Check("首次进入返回临时等级", swap.Begin(1, 2) == 2);
            Program.Check("首次进入深度为 1", swap.Depth == 1);
            Program.Check("退出返回原等级", swap.End() == 1);
            Program.Check("退出后深度归零", swap.Depth == 0);

            Program.Check("嵌套进入写入临时等级", swap.Begin(1, 2) == 2);
            Program.Check("嵌套层不再写等级", swap.Begin(2, 2) is null);
            Program.Check("嵌套层退出不还原", swap.End() is null);
            Program.Check("最外层退出返回原等级", swap.End() == 1);

            Program.Check("未进入时退出返回 null", swap.End() is null);
            Program.Check("未进入时深度为零", swap.Depth == 0);

            swap.Begin(1, 2);
            Program.Check("多出来的退出调用返回 null", swap.End() == 1 && swap.End() is null);

            var again = new GoalLevelSwap();
            Program.Check("再次进入保存新的原值", again.Begin(3, 2) == 2);
            Program.Check("再次退出恢复新的原值", again.End() == 3);
        }
    }
}
