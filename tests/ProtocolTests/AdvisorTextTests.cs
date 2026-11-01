using IcarusII.UI;

namespace IcarusII.Tests
{
    /// <summary>
    /// 顾问提示文案的离线回归：截断、去 Markdown、富文本转义，以及快捷键着色。
    ///
    /// 面板宽度是按文本高度分段跳的（UIAdvisorTip.PlayAdvisorTip），
    /// 这里压不住字数就会把顾问字幕撑出屏幕。
    /// </summary>
    internal static class AdvisorTextTests
    {
        // 模板不写死换行：顾问字幕面板居中，短行会被看成首行缩进，交给 Text 自动折行。
        private const string ReplyTemplate = "我已回复{0}，请过目：{1}请您按{2}键进入工业机甲终端查看完整内容。";
        private const string ConfirmTemplate = "工程师，我请求{0}：{1}请您按{2}键进入工业机甲终端进行授权。";
        private const string AskTemplate = "我需要您为如下事项做出抉择：{0}请您按{1}键进入工业机甲终端作出批示。";

        public static void Run()
        {
            Eq("快捷键着色", "<color=\"#61D8FFc0\">[R]</color>", AdvisorText.HotkeyMarkup("R"));
            Eq("快捷键组合着色", "<color=\"#61D8FFc0\">[Ctrl+R]</color>", AdvisorText.HotkeyMarkup("Ctrl+R"));
            Eq("空绑定兜底", "<color=\"#61D8FFc0\">[?]</color>", AdvisorText.HotkeyMarkup(null));

            Eq("Markdown 折成纯文本", "重点：产物", AdvisorText.Flatten("**重点**：\n产物", 64));
            Eq("换行用分号连接", "第一行；第二行", AdvisorText.Flatten("第一行\n第二行", 64));
            Eq("列表符号不残留", "苹果；香蕉", AdvisorText.Flatten("- 苹果\n- 香蕉", 64));
            Eq("按字数截断", "一二三…", AdvisorText.Flatten("一二三四五六七八九十", 3));
            Eq("刚好不截断", "一二三", AdvisorText.Flatten("一二三", 3));
            Eq("空文本", string.Empty, AdvisorText.Flatten(null, 8));
            Eq("尖括号转义", "a<\u200Bb", AdvisorText.Flatten("a<b", 64));
            Eq("表格行整行跳过", "结果如下：以上。", AdvisorText.Flatten("结果如下：\n| 行星 | 类型 |\n|---|---|\n| 文昌六-1号星 | 火山 |\n以上。", 64));

            var reply = AdvisorText.ComposeReply(ReplyTemplate, "怎么造戴森球", "先铺太阳帆，再搭框架。", AdvisorText.HotkeyMarkup("R"));
            Check("回复：带问题原文", reply.Contains("「怎么造戴森球」"));
            Check("回复：引号只出现一层", !reply.Contains("「「"));
            Check("回复：带正文", reply.Contains("先铺太阳帆，再搭框架。"));
            Check("回复：带着色热键", reply.Contains("<color=\"#61D8FFc0\">[R]</color>键进入工业机甲终端查看完整内容。"));

            var confirm = AdvisorText.ComposeConfirm(ConfirmTemplate, "新增标记？", "行星：1\n坐标：2 / 3", AdvisorText.HotkeyMarkup("F8"));
            Check("授权：标题去掉问号", confirm.StartsWith("工程师，我请求新增标记："));
            Check("授权：细节折成一行", confirm.Contains("行星：1；坐标：2 / 3"));
            Check("授权：带着色热键", confirm.Contains("<color=\"#61D8FFc0\">[F8]</color>键进入工业机甲终端进行授权。"));

            var ask = AdvisorText.ComposeAsk(AskTemplate, "要建在哪？；用几条线？", AdvisorText.HotkeyMarkup("R"));
            Check("选择：带问题列表", ask.Contains("要建在哪？；用几条线？"));
            Check("选择：带着色热键", ask.Contains("<color=\"#61D8FFc0\">[R]</color>键进入工业机甲终端作出批示。"));

            var longBody = new string('长', 500);
            var trimmed = AdvisorText.ComposeReply(ReplyTemplate, "问题", longBody, AdvisorText.HotkeyMarkup("R"));
            Check("超长正文被截断", trimmed.Contains("长…") && trimmed.Length < 220);
        }

        private static void Eq<T>(string name, T expected, T actual)
        {
            Program.Eq(name, expected, actual);
        }

        private static void Check(string name, bool ok)
        {
            Program.Check(name, ok);
        }
    }
}
