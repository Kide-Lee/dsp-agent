using System;
using System.Text;
using IcarusII.Common.Text;

namespace IcarusII.UI
{
    /// <summary>
    /// 顾问提示文案：把后台事件压成一段能塞进顾问字幕面板的文本。
    ///
    /// 面板宽度按 <c>UIAdvisorTip.PlayAdvisorTip</c> 里的 preferredHeight 分段增长，
    /// 原文照搬会把面板顶出屏幕，所以这里统一做「去 Markdown → 折成一行 → 按字数截断」。
    /// 刻意不引用 UnityEngine，离线测试可以直接编译本文件。
    /// </summary>
    internal static class AdvisorText
    {
        /// <summary>与游戏原生顾问字幕一致：base.txt 里 $LKEY 外面那层 color。</summary>
        public const string HotkeyColor = "#61D8FFc0";

        /// <summary>
        /// 各段的字数上限。游戏自带的最长顾问字幕是 164 字（顾问字幕55），
        /// 三段拼起来控制在它附近，面板宽度才不会跳过 840 那一档。
        /// </summary>
        public const int QuestionMaxChars = 24;

        public const int BodyMaxChars = 88;

        public const int TitleMaxChars = 24;

        public const int AskMaxChars = 88;

        private const string Ellipsis = "…";

        /// <summary>把热键渲染成「按[Ctrl+R]键」里那一段；空绑定用 ? 兜底。</summary>
        public static string HotkeyMarkup(string chord)
        {
            var text = string.IsNullOrEmpty(chord) ? "?" : chord;
            return $"<color=\"{HotkeyColor}\">[{text}]</color>";
        }

        public static string ComposeReply(string template, string question, string body, string hotkeyMarkup)
        {
            return string.Format(
                template,
                Quote(question, QuestionMaxChars),
                Flatten(body, BodyMaxChars),
                hotkeyMarkup);
        }

        public static string ComposeConfirm(string template, string title, string body, string hotkeyMarkup)
        {
            return string.Format(
                template,
                TrimTitle(Flatten(title, TitleMaxChars)),
                Flatten(body, BodyMaxChars),
                hotkeyMarkup);
        }

        public static string ComposeAsk(string template, string questions, string hotkeyMarkup)
        {
            return string.Format(template, Flatten(questions, AskMaxChars), hotkeyMarkup);
        }

        /// <summary>
        /// 折成一行：Markdown 还原成纯文本，逐行用「；」连起来再按字数截断。
        ///
        /// 逐行处理而不是整段交给 GameRichText：中文软换行会被合并成「行星：1坐标：2」
        /// 这种读不出边界的句子，而顾问提示只有一行，行间必须留出分隔。
        /// 结果里的 '&lt;' 会插入零宽空格，防止模型输出被 Text 当成富文本标签。
        /// </summary>
        public static string Flatten(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var builder = new StringBuilder(text.Length);
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = StripBullet(GameRichText.Format(rawLine, RichTextProfile.Body).Plain);
                if (line.Length == 0) continue;
                if (builder.Length > 0 && !EndsWithPause(builder)) builder.Append('；');
                builder.Append(line);
            }

            var collapsed = CollapseWhitespace(builder.ToString());
            if (maxChars > 0 && collapsed.Length > maxChars)
                collapsed = collapsed.Substring(0, SafeCut(collapsed, maxChars)) + Ellipsis;
            return EscapeRich(collapsed);
        }

        /// <summary>上一行已经以标点结尾时不再补「；」，否则会得到「重点：；产物」。</summary>
        private static bool EndsWithPause(StringBuilder builder)
        {
            return builder.Length > 0 && "：:，,；;、。！!？?".IndexOf(builder[builder.Length - 1]) >= 0;
        }

        private static string StripBullet(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("• ", StringComparison.Ordinal)) return trimmed.Substring(2).Trim();
            if (trimmed == "•") return string.Empty;
            return trimmed;
        }

        private static string Quote(string text, int maxChars)
        {
            var body = Flatten(text, maxChars);
            return body.Length == 0 ? string.Empty : $"「{body}」";
        }

        /// <summary>
        /// 授权标题在 sidecar 里是问句（「新增标记？」），直接拼进「我请求…：」会变成「…？：」，
        /// 所以去掉句尾的问号/冒号再拼。
        /// </summary>
        private static string TrimTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            var end = title.Length;
            while (end > 0)
            {
                var ch = title[end - 1];
                if (ch != '？' && ch != '?' && ch != '：' && ch != ':' && ch != '。' && ch != '.' && ch != ' ')
                    break;
                end--;
            }
            return end == 0 ? string.Empty : title.Substring(0, end);
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new StringBuilder(text.Length);
            var pendingSpace = false;
            foreach (var ch in text)
            {
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\u3000')
                {
                    if (builder.Length > 0) pendingSpace = true;
                    continue;
                }
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(ch);
            }
            return builder.ToString();
        }

        private static int SafeCut(string text, int maxChars)
        {
            if (maxChars <= 0 || maxChars >= text.Length) return text.Length;
            // 不要在代理对中间切，否则截断处会出现半个字符。
            return char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        }

        private static string EscapeRich(string text)
        {
            return text.IndexOf('<') < 0 ? text : text.Replace("<", "<\u200B");
        }
    }
}
