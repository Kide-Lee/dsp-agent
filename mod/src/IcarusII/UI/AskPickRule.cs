namespace IcarusII.UI
{
    /// <summary>
    /// 单选选项「点一下就进下一步」的判定。
    ///
    /// 只在「多题 + 单选 + 有汇总确认页」时成立：多题的汇总页可以按「返回」改答案，
    /// 单题的汇总页没有「返回」（只有一个提交按钮），误点就没法改回来，所以仍要手动点「下一个」。
    /// 多选要连续勾几个，自定义输入要打字，都不自动跳。
    /// </summary>
    internal static class AskPickRule
    {
        public static bool AdvancesOnPick(int questionCount, bool multiSelect, bool hasReviewPage)
        {
            if (multiSelect) return false;
            if (!hasReviewPage) return false;
            return questionCount > 1;
        }
    }
}
