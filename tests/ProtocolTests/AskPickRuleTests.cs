using IcarusII.UI;

namespace IcarusII.Tests
{
    /// <summary>
    /// 单选「点一下就进下一步」的判定：只在多题且存在汇总确认页时成立。
    /// 单题没有「返回」，误点改不回来，所以必须保持手动点「下一个」。
    /// </summary>
    internal static class AskPickRuleTests
    {
        public static void Run()
        {
            Eq("两题单选可点击即跳", true, AskPickRule.AdvancesOnPick(2, false, true));
            Eq("多题单选可点击即跳", true, AskPickRule.AdvancesOnPick(4, false, true));
            Eq("单题不自动跳", false, AskPickRule.AdvancesOnPick(1, false, true));
            Eq("多选不自动跳", false, AskPickRule.AdvancesOnPick(3, true, true));
            Eq("没有汇总确认页不自动跳", false, AskPickRule.AdvancesOnPick(3, false, false));
        }

        private static void Eq(string name, bool expected, bool actual)
        {
            Program.Eq(name, expected, actual);
        }
    }
}
