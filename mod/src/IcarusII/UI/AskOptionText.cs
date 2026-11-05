namespace IcarusII.UI
{
    /// <summary>
    /// 作答面板选项行的显示文案。
    ///
    /// 格式固定为 `标题` + 制表符 + 灰色的说明：`火锅\t&lt;color=#555&gt;正好叫些朋友一起过来。&lt;/color&gt;`。
    /// prefab 里 `answer-option-1..4` 的示例文案、以及 sidecar 提示词里对 description 的要求都按这个格式写，
    /// 所以单独放在这里，由离线测试盯住，避免改一处忘一处。
    /// </summary>
    internal static class AskOptionText
    {
        /// <summary>说明的富文本颜色；prefab 示例文案用同一个值。</summary>
        public const string DescriptionColor = "#555";

        /// <summary>说明为空时只返回标题，不留下孤零零的制表符。</summary>
        public static string Compose(AskOption option)
        {
            if (option is null) return string.Empty;
            var label = option.Label ?? string.Empty;
            var description = option.Description?.Trim();
            if (string.IsNullOrEmpty(description)) return label;
            return $"{label}\t<color={DescriptionColor}>{description}</color>";
        }
    }
}
