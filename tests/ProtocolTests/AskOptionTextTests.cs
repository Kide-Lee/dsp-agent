using IcarusII.UI;

namespace IcarusII.Tests
{
    /// <summary>
    /// 作答面板选项文案的离线回归：`标题\t<color=#555>说明</color>`。
    /// prefab 里的示例文案和 sidecar 提示词都按这个格式写，格式漂了游戏里就会露出裸标签或丢说明。
    /// </summary>
    internal static class AskOptionTextTests
    {
        public static void Run()
        {
            Eq("无说明只显示标题", "火锅",
                AskOptionText.Compose(new AskOption { Label = "火锅" }));
            Eq("空说明不留下制表符", "火锅",
                AskOptionText.Compose(new AskOption { Label = "火锅", Description = "   " }));
            Eq("有说明拼灰色富文本", "火锅\t<color=#555>正好叫些朋友一起过来。</color>",
                AskOptionText.Compose(new AskOption { Label = "火锅", Description = "正好叫些朋友一起过来。" }));
            Eq("说明两端空白被去掉", "烧烤\t<color=#555>油烟大。</color>",
                AskOptionText.Compose(new AskOption { Label = "烧烤", Description = " 油烟大。 " }));
            Eq("空选项返回空串", string.Empty, AskOptionText.Compose(null));
            Eq("颜色值与 prefab 一致", "#555", AskOptionText.DescriptionColor);
        }

        private static void Eq(string name, string expected, string actual)
        {
            Program.Eq(name, expected, actual);
        }
    }
}
