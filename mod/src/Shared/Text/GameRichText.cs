using System;
using System.Collections.Generic;
using System.Text;

namespace IcarusII.Common.Text
{
    /// <summary>输出文本的用途。Body 走完整 Markdown + HTML；Inline 只做行内；Plain 全部转义。</summary>
    public enum RichTextProfile
    {
        Body,
        Inline,
        Plain,
    }

    public sealed class RichTextOptions
    {
        public int BodyFontSize = 14;
        public int MinSize = 10;
        public int MaxSize = 36;
        public int MaxLength = 12000;
        public bool AllowMarkdown = true;

        public string HeadingColor = "#FFFFFF";
        public string CodeColor = "#9CDCFE";
        public string QuoteColor = "#8FA3B8";
    }

    public sealed class RichTextLink
    {
        public int Id;
        public string Url;
        public string Text;
    }

    /// <summary>
    /// 网页链接的顺序 ID 分配器。900000–999999 专供网页；1000–9999 科技、
    /// 10000–19999 配方、20000–21000 成就、21001–899999 其它用途。
    /// 按 Text 标签复用一个实例，保证同一个 Text 内的 ID 不重复；同一个 URL 复用同一个 ID。
    /// </summary>
    public sealed class RichTextLinkAllocator
    {
        public const int WebMin = 900000;
        public const int WebMax = 999999;
        /// <summary>实体链接段：800000–899999（与设计 §20.2 一致）。</summary>
        public const int EntityMin = 800000;
        public const int EntityMax = 899999;

        private readonly Dictionary<string, int> _urlToId =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<int> _used = new HashSet<int>();
        private int _next = WebMin;
        private int _nextEntity = EntityMin;

        public int Allocate(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            var key = url.Trim();
            if (key.Length == 0) return 0;
            if (_urlToId.TryGetValue(key, out var existing)) return existing;

            while (_next <= WebMax && _used.Contains(_next)) _next++;
            if (_next > WebMax) return 0;

            var id = _next++;
            _urlToId[key] = id;
            _used.Add(id);
            return id;
        }

        /// <summary>实体链接单独分配，避免占用网页段；同一个 URL 复用。</summary>
        public int AllocateEntity(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            var key = url.Trim();
            if (key.Length == 0) return 0;
            if (_urlToId.TryGetValue(key, out var existing)) return existing;
            while (_nextEntity <= EntityMax && _used.Contains(_nextEntity)) _nextEntity++;
            if (_nextEntity > EntityMax) return 0;
            var id = _nextEntity++;
            _urlToId[key] = id;
            _used.Add(id);
            return id;
        }

        /// <summary>把游戏原生 &lt;link=N&gt; 已占用的 ID 登记进来，网页分配时跳过。</summary>
        public void Reserve(int id)
        {
            if (id >= 1 && id <= WebMax) _used.Add(id);
        }

        public static bool IsWebId(int id)
        {
            return id >= WebMin && id <= WebMax;
        }

        public static bool IsEntityLinkId(int id)
        {
            return id >= EntityMin && id <= EntityMax;
        }

        /// <summary>1000–9999 是科技链接；1 不再分配给科技。</summary>
        public static bool IsTechLinkId(int id)
        {
            return id >= 1000 && id <= 9999;
        }

        public static bool IsRecipeLinkId(int id)
        {
            return id >= 10000 && id <= 19999;
        }

        public static bool IsAchievementLinkId(int id)
        {
            return id >= 20000 && id <= 21000;
        }
    }

    /// <summary>
    /// 游戏内容链接的 key → 游戏 ID 解析器。Shared 层不认识 LDB，
    /// 由 UI 层提供；返回 0 表示解析失败，链接会降级为普通文本。
    /// </summary>
    public sealed class RichTextLinkResolvers
    {
        public Func<string, int> TechTag;
        public Func<string, int> RecipeTag;
        public Func<string, int> AchievementKey;
    }

    public sealed class RichTextResult
    {
        public string Rich = string.Empty;
        public string Plain = string.Empty;
        public bool HasLinks;
        public bool HasTechLinks;
        public bool HasRecipeLinks;
        public bool HasAchievementLinks;
        public bool HasEntityLinks;
        public bool HasGameLinks => HasTechLinks || HasRecipeLinks || HasAchievementLinks || HasEntityLinks;
        public bool HasCheckBoxes;
        public bool Truncated;
        public readonly List<RichTextLink> Links = new List<RichTextLink>();
    }

    public enum RichTextTableAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>Markdown 表格的原始结构；UI 层据此设置 Text 的表格数组。</summary>
    public sealed class RichTextTable
    {
        public readonly List<string> Headers = new List<string>();
        public readonly List<List<string>> Rows = new List<List<string>>();
        public readonly List<RichTextTableAlign> Alignments = new List<RichTextTableAlign>();
    }

    /// <summary>正文切分结果：普通文本段，或一个表格段。</summary>
    public sealed class RichTextSegment
    {
        public bool IsTable;
        public string Text;
        public RichTextTable Table;
    }

    /// <summary>
    /// 把“Markdown + 一小撮内联 HTML + 兼容裸游戏标签”编译成游戏 Text 能吃的标记。
    ///
    /// 为什么放在 Shared：这是纯字符串处理，不能依赖 UnityEngine；放在这里可以
    /// 直接进 ProtocolTests，不需要启动 Unity。
    ///
    /// 支持的 HTML 子集（标签名大小写不敏感）：
    ///   color value / size value / tint value / box / sqrt value / sup / sub /
    ///   i / em / b / strong / a href / icon tag [name]
    /// 不在此列的一律按普通文本转义，避免把任意 HTML 带进游戏。
    /// </summary>
    public static class GameRichText
    {
        private static readonly HashSet<string> NamedColors = new HashSet<string>(StringComparer.Ordinal)
        {
            "black", "blue", "brown", "clear", "cyan", "aqua", "darkblue", "fade",
            "fuchsia", "magenta", "gray", "grey", "green", "lightblue", "lime",
            "maroon", "navy", "olive", "orange", "pink", "purple", "red", "silver",
            "skyblue", "teal", "turquoise", "white", "yellow",
        };

        private static readonly HashSet<string> TagNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "color", "size", "tint", "box", "sqrt", "sup", "sub",
            "i", "em", "b", "strong", "a", "icon", "link",
        };

        public static RichTextResult Format(
            string raw,
            RichTextProfile profile = RichTextProfile.Body,
            RichTextOptions options = null,
            RichTextLinkAllocator linkAllocator = null,
            RichTextLinkResolvers gameLinkResolvers = null)
        {
            var opts = options ?? new RichTextOptions();
            var ctx = new Context(opts, linkAllocator ?? new RichTextLinkAllocator(), gameLinkResolvers);
            var text = (raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

            if (profile == RichTextProfile.Plain)
            {
                ctx.Plain.Append(text);
                ctx.Rich.Append(EscapeText(text, code: false));
            }
            else if (profile == RichTextProfile.Inline)
            {
                var style = new Style();
                var pos = 0;
                RenderInline(text, ref pos, text.Length, style, ctx, null);
            }
            else
            {
                RenderBlocks(text, ctx);
            }

            return ctx.Build();
        }

        /// <summary>
        /// 把正文按 Markdown 表格切成“文本段 / 表格段”，供 UI 用独立 Text 渲染真表格。
        /// 代码围栏里的 `|` 不参与切分；没有表格时返回一个普通文本段。
        /// </summary>
        public static List<RichTextSegment> SplitTables(string raw)
        {
            var result = new List<RichTextSegment>();
            var text = (raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');
            var buffer = new StringBuilder();
            var inFence = false;
            var fence = string.Empty;

            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (IsFence(line, out var marker))
                {
                    if (!inFence)
                    {
                        inFence = true;
                        fence = marker;
                    }
                    else if (marker == fence)
                    {
                        inFence = false;
                    }

                    AppendLine(buffer, line);
                    i++;
                    continue;
                }

                if (!inFence && IsTableStart(lines, i))
                {
                    FlushTextSegment(result, buffer);
                    result.Add(new RichTextSegment
                    {
                        IsTable = true,
                        Table = ParseTable(lines, ref i),
                    });
                    continue;
                }

                AppendLine(buffer, line);
                i++;
            }

            FlushTextSegment(result, buffer);
            if (result.Count == 0)
                result.Add(new RichTextSegment { Text = string.Empty });
            return result;
        }

        private static RichTextTable ParseTable(string[] lines, ref int index)
        {
            var table = new RichTextTable();
            var headers = SplitCells(lines[index]);
            var separators = SplitCells(lines[index + 1]);
            for (var i = 0; i < headers.Length; i++)
            {
                table.Headers.Add(headers[i]);
                table.Alignments.Add(ParseAlignment(i < separators.Length ? separators[i] : string.Empty));
            }

            index += 2;
            while (index < lines.Length &&
                   !string.IsNullOrWhiteSpace(lines[index]) &&
                   lines[index].IndexOf('|') >= 0 &&
                   !IsFence(lines[index], out _))
            {
                // 下一段表格的开头（分隔行）说明当前表格结束。
                if (IsTableStart(lines, index)) break;
                table.Rows.Add(new List<string>(SplitCells(lines[index])));
                index++;
            }

            return table;
        }

        private static RichTextTableAlign ParseAlignment(string cell)
        {
            var value = (cell ?? string.Empty).Trim();
            var left = value.StartsWith(":", StringComparison.Ordinal);
            var right = value.EndsWith(":", StringComparison.Ordinal);
            if (left && right) return RichTextTableAlign.Center;
            if (right) return RichTextTableAlign.Right;
            return RichTextTableAlign.Left;
        }

        private static void AppendLine(StringBuilder buffer, string line)
        {
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
        }

        private static void FlushTextSegment(List<RichTextSegment> result, StringBuilder buffer)
        {
            if (buffer.Length == 0) return;
            var text = buffer.ToString();
            buffer.Length = 0;
            if (text.Length == 0) return;
            result.Add(new RichTextSegment { Text = text });
        }

        // ---------------------------------------------------------------- 块级

        private static void RenderBlocks(string text, Context ctx)
        {
            var lines = text.Split('\n');
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];

                if (IsFence(line, out var fence))
                {
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length)
                    {
                        if (IsFenceLine(lines[i], fence)) { i++; break; }
                        if (code.Length > 0) code.Append('\n');
                        code.Append(lines[i]);
                        i++;
                    }

                    var codeText = code.ToString();
                    if (codeText.Length > 0)
                    {
                        ctx.BlankLine();
                        EmitCodeBlock(codeText, ctx);
                        ctx.Newline();
                        ctx.BlankLine();
                    }
                    continue;
                }

                if (TryHeading(line, out var headingLevel, out var headingText))
                {
                    ctx.BlankLine();
                    var style = new Style
                    {
                        Color = ctx.Options.HeadingColor,
                        Size = HeadingSize(headingLevel, ctx.Options),
                        Bold = true,
                    };
                    RenderInline(headingText, 0, headingText.Length, style, ctx, null);
                    ctx.Newline();
                    ctx.BlankLine();
                    i++;
                    continue;
                }

                if (IsBlockquote(line))
                {
                    ctx.BlankLine();
                    var quoteStyle = new Style { Color = ctx.Options.QuoteColor };
                    while (i < lines.Length)
                    {
                        if (IsBlockquote(lines[i]))
                        {
                            var content = StripBlockquote(lines[i]);
                            EmitRun("▏ ", quoteStyle, ctx);
                            RenderInline(content, 0, content.Length, quoteStyle, ctx, null);
                            ctx.Newline();
                            i++;
                            continue;
                        }

                        // 连续引用之间的无引用空行也画一条竖线，避免装饰线中断。
                        if (string.IsNullOrWhiteSpace(lines[i]))
                        {
                            var next = i + 1;
                            while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
                            if (next < lines.Length && IsBlockquote(lines[next]))
                            {
                                EmitRun("▏ ", quoteStyle, ctx);
                                ctx.Newline();
                                i = next;
                                continue;
                            }
                        }
                        break;
                    }
                    ctx.BlankLine();
                    continue;
                }

                if (TryListItem(line, out var item))
                {
                    if (i == 0 || !TryListItem(lines[i - 1], out _)) ctx.BlankLine();
                    var prefix = ListPrefix(item, ctx);
                    EmitRun(prefix, new Style(), ctx);
                    if (item.CheckBox.HasValue) ctx.HasCheckBoxes = true;
                    RenderInline(item.Content, 0, item.Content.Length, new Style(), ctx, null);
                    ctx.Newline();
                    if (i + 1 >= lines.Length || !TryListItem(lines[i + 1], out _)) ctx.BlankLine();
                    i++;
                    continue;
                }

                if (IsHorizontalRule(line))
                {
                    ctx.BlankLine();
                    EmitRun("────────", new Style { Color = ctx.Options.QuoteColor }, ctx);
                    ctx.Newline();
                    ctx.BlankLine();
                    i++;
                    continue;
                }

                if (IsTableStart(lines, i))
                {
                    ctx.BlankLine();
                    i = RenderTableFallback(lines, i, ctx);
                    ctx.BlankLine();
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    // 段落之间使用两行空白，保证聊天窗里也能看出层次。
                    ctx.BlankLine();
                    i++;
                    continue;
                }

                // 普通段落：Markdown 软换行按段落合并；行尾两个空格或反斜杠
                // 才是硬换行。中文之间不额外插空格，英文之间补一个空格。
                var paragraph = new StringBuilder();
                var previousHardBreak = false;
                while (i < lines.Length &&
                       !string.IsNullOrWhiteSpace(lines[i]) &&
                       !IsFence(lines[i], out _) &&
                       !TryHeading(lines[i], out _, out _) &&
                       !IsBlockquote(lines[i]) &&
                       !TryListItem(lines[i], out _) &&
                       !IsHorizontalRule(lines[i]) &&
                       !IsTableStart(lines, i))
                {
                    var sourceLine = lines[i];
                    if (paragraph.Length > 0)
                    {
                        if (previousHardBreak) paragraph.Append('\n');
                        else AppendSoftSpace(paragraph, sourceLine);
                    }

                    previousHardBreak = AppendParagraphLine(paragraph, sourceLine);
                    i++;
                }

                if (paragraph.Length > 0)
                {
                    var textValue = paragraph.ToString();
                    RenderInline(textValue, 0, textValue.Length, new Style(), ctx, null);
                    ctx.Newline();
                }
            }

            ctx.TrimTrailingNewline();
        }

        /// <summary>去掉行尾硬换行标记，返回是否硬换行。</summary>
        private static bool AppendParagraphLine(StringBuilder sb, string line)
        {
            var text = line.TrimEnd();
            var hard = false;
            if (text.EndsWith("\\", StringComparison.Ordinal))
            {
                hard = true;
                text = text.Substring(0, text.Length - 1).TrimEnd();
            }
            else if (line.Length - text.Length >= 2)
            {
                hard = true;
            }
            sb.Append(text);
            return hard;
        }

        /// <summary>软换行：中文/中文标点之间不加空格，英文之间加空格。</summary>
        private static void AppendSoftSpace(StringBuilder sb, string nextLine)
        {
            if (sb.Length == 0) return;
            var left = sb[sb.Length - 1];
            var right = nextLine.Length > 0 ? nextLine[0] : '\0';
            if (left == ' ' || right == ' ' || right == '\0') return;
            if (IsCjk(left) || IsCjk(right)) return;
            sb.Append(' ');
        }

        private static bool IsCjk(char c)
        {
            return (c >= '\u2E80' && c <= '\u9FFF') ||
                   (c >= '\u3000' && c <= '\u303F') ||
                   (c >= '\uFF00' && c <= '\uFFEF');
        }

        private static void EmitCodeBlock(string code, Context ctx)
        {
            if (string.IsNullOrEmpty(code))
            {
                return;
            }

            var style = new Style
            {
                Code = true,
                Color = ctx.Options.CodeColor,
                Size = Math.Max(ctx.Options.MinSize, ctx.Options.BodyFontSize - 1),
                Box = true,
            };

            // <box> 会贴着文字画边框；在盒内加不可见空行和左右空格，
            // 让边框与代码之间留出呼吸空间。
            var padded = "\u00A0\n  " +
                         code.Replace("\n", "  \n  ") +
                         "  \n\u00A0";
            EmitRun(padded, style, ctx);
        }

        private static int HeadingSize(int level, RichTextOptions options)
        {
            var delta = level <= 1 ? 6 : level == 2 ? 4 : level == 3 ? 2 : 0;
            return Clamp(options.BodyFontSize + delta, options.MinSize, options.MaxSize);
        }

        // ---------------------------------------------------------------- 行内

        private static void RenderInline(
            string s,
            ref int pos,
            int end,
            Style style,
            Context ctx,
            string stopTag)
        {
            var text = new StringBuilder();
            while (pos < end)
            {
                if (ctx.Truncated)
                {
                    break;
                }

                var c = s[pos];

                if (c == '\\' && pos + 1 < end && IsEscapable(s[pos + 1]))
                {
                    text.Append(s[pos + 1]);
                    pos += 2;
                    continue;
                }

                // 裸 http(s) URL 也自动变成可点击链接：模型输出蓝图来源时可能
                // 直接贴 URL，而不是 Markdown 链接。
                if (style.LinkId == 0 && (c == 'h' || c == 'H') &&
                    TryReadBareUrl(s, pos, end, out var bareUrl, out var bareEnd))
                {
                    Flush(text, style, ctx);
                    var id = ctx.LinkAllocator.Allocate(bareUrl);
                    if (id > 0)
                    {
                        var linkStyle = style;
                        linkStyle.LinkId = id;
                        ctx.Links.Add(new RichTextLink { Id = id, Url = bareUrl, Text = bareUrl });
                        ctx.HasLinks = true;
                        EmitRun(bareUrl, linkStyle, ctx);
                    }
                    else
                    {
                        text.Append(bareUrl);
                    }
                    pos = bareEnd;
                    continue;
                }

                if (c == '`')
                {
                    var run = 1;
                    while (pos + run < end && s[pos + run] == '`') run++;
                    var close = FindRun(s, pos + run, end, run);
                    if (close >= 0)
                    {
                        Flush(text, style, ctx);
                        var code = s.Substring(pos + run, close - pos - run);
                        EmitRun(code, new Style { Code = true, Color = ctx.Options.CodeColor }, ctx);
                        pos = close + run;
                        continue;
                    }
                    text.Append(c);
                    pos++;
                    continue;
                }

                if (c == '<')
                {
                    if (TryReadTag(s, pos, end, out var tag))
                    {
                        Flush(text, style, ctx);

                        if (tag.Closing)
                        {
                            if (stopTag != null && tag.Name == stopTag)
                            {
                                pos = tag.End;
                                return;
                            }

                            // 不匹配的闭合标签：按普通文本保留，避免吞掉内容。
                            text.Append(s, tag.Start, tag.End - tag.Start);
                            pos = tag.End;
                            continue;
                        }

                        if (tag.Name == "icon")
                        {
                            EmitIcon(tag, style, ctx);
                            pos = tag.End;
                            continue;
                        }

                        var inner = style;
                        if (!ApplyTag(tag, ref inner, ctx))
                        {
                            text.Append(s, tag.Start, tag.End - tag.Start);
                            pos = tag.End;
                            continue;
                        }

                        pos = tag.End;
                        RenderInline(s, ref pos, end, inner, ctx, tag.Name);
                        continue;
                    }

                    text.Append('<');
                    pos++;
                    continue;
                }

                if (c == '!' && pos + 1 < end && s[pos + 1] == '[')
                {
                    if (TryParseLink(s, pos + 1, end, out var image, out var imageEnd))
                    {
                        Flush(text, style, ctx);
                        // 图片不在游戏里加载；保留 alt 文本。
                        RenderInline(image.Text, 0, image.Text.Length, style, ctx, null);
                        pos = imageEnd;
                        continue;
                    }
                }

                if (c == '[')
                {
                    if (TryParseLink(s, pos, end, out var link, out var linkEnd))
                    {
                        Flush(text, style, ctx);
                        var inner = style;
                        if (link.Url != null)
                        {
                            if (!TryApplyGameLink(link.Url, ref inner, ctx))
                            {
                                var id = ctx.LinkAllocator.Allocate(link.Url);
                                if (id > 0)
                                {
                                    inner.LinkId = id;
                                    ctx.Links.Add(new RichTextLink { Id = id, Url = link.Url, Text = link.Text });
                                    ctx.HasLinks = true;
                                }
                            }
                        }
                        RenderInline(link.Text, 0, link.Text.Length, inner, ctx, null);
                        pos = linkEnd;
                        continue;
                    }
                }

                if (c == '*' || c == '_' || c == '~')
                {
                    var marker = DelimiterAt(s, pos, end);
                    if (marker.Length > 0)
                    {
                        var close = FindDelimiter(s, pos + marker.Length, end, marker);
                        if (close >= 0)
                        {
                            Flush(text, style, ctx);
                            var inner = style;
                            if (marker == "~~") inner.Strike = true;
                            else
                            {
                                if (marker.IndexOf('*') >= 0 || marker.IndexOf('_') >= 0)
                                {
                                    if (marker.Length >= 2) inner.Bold = true;
                                    if (marker.Length == 1 || marker.Length == 3) inner.Italic = true;
                                }
                            }
                            var innerText = s.Substring(pos + marker.Length, close - pos - marker.Length);
                            RenderInline(innerText, 0, innerText.Length, inner, ctx, null);
                            pos = close + marker.Length;
                            continue;
                        }
                    }
                }

                if (c == '\n')
                {
                    text.Append('\n');
                    pos++;
                    continue;
                }

                text.Append(c);
                pos++;
            }

            Flush(text, style, ctx);
        }

        private static void RenderInline(
            string s,
            int start,
            int end,
            Style style,
            Context ctx,
            string stopTag)
        {
            var pos = start;
            RenderInline(s, ref pos, end, style, ctx, stopTag);
        }

        private static void Flush(StringBuilder text, Style style, Context ctx)
        {
            if (text.Length == 0) return;
            EmitRun(text.ToString(), style, ctx);
            text.Length = 0;
        }

        // ---------------------------------------------------------------- 标签

        private sealed class Tag
        {
            public int Start;
            public int End;
            public string Name;
            public bool Closing;
            public bool SelfClosing;
            public string GameArg;
            public Dictionary<string, string> Attributes;
        }

        private static bool TryReadTag(string s, int pos, int end, out Tag tag)
        {
            tag = null;
            var gt = FindTagEnd(s, pos, end);
            if (gt < 0) return false;

            var inner = s.Substring(pos + 1, gt - pos - 1).Trim();
            if (inner.Length == 0) return false;

            var closing = inner[0] == '/';
            if (closing) inner = inner.Substring(1).TrimStart();

            var selfClosing = inner.EndsWith("/", StringComparison.Ordinal);
            if (selfClosing) inner = inner.Substring(0, inner.Length - 1).TrimEnd();

            var nameEnd = 0;
            while (nameEnd < inner.Length && IsTagNameChar(inner[nameEnd])) nameEnd++;
            if (nameEnd == 0) return false;

            var name = inner.Substring(0, nameEnd).ToLowerInvariant();
            if (!TagNames.Contains(name)) return false;

            var rest = inner.Substring(nameEnd).TrimStart();
            var tagValue = new Tag
            {
                Start = pos,
                End = gt + 1,
                Name = name,
                Closing = closing,
                SelfClosing = selfClosing,
            };

            if (closing)
            {
                tag = tagValue;
                return true;
            }

            if (rest.StartsWith("=", StringComparison.Ordinal) && IsGameTag(name))
            {
                // 游戏文本自身就是这种写法；模型从游戏文本抄进上下文时
                // 必须能按原样识别，但不能放宽到任意缺属性名的标签。
                tagValue.GameArg = rest.Substring(1).Trim();
            }
            else if (rest.Length > 0)
            {
                if (!TryParseAttributes(rest, out tagValue.Attributes)) return false;
            }

            tag = tagValue;
            return true;
        }

        private static bool IsGameTag(string name)
        {
            return name == "color" || name == "tint" || name == "size" ||
                   name == "sqrt" || name == "link";
        }

        private static bool TryParseAttributes(string text, out Dictionary<string, string> attributes)
        {
            attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;

                var nameStart = i;
                while (i < text.Length && IsAttributeNameChar(text[i])) i++;
                if (i == nameStart) return false;

                var name = text.Substring(nameStart, i - nameStart).ToLowerInvariant();
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

                string value = string.Empty;
                if (i < text.Length && text[i] == '=')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i >= text.Length) return false;

                    if (text[i] == '"' || text[i] == '\'')
                    {
                        var quote = text[i];
                        i++;
                        var valueStart = i;
                        while (i < text.Length && text[i] != quote) i++;
                        if (i >= text.Length) return false;
                        value = text.Substring(valueStart, i - valueStart);
                        i++;
                    }
                    else
                    {
                        var valueStart = i;
                        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                        value = text.Substring(valueStart, i - valueStart);
                    }
                }

                attributes[name] = value;
            }

            return true;
        }

        private static bool ApplyTag(Tag tag, ref Style style, Context ctx)
        {
            switch (tag.Name)
            {
                case "color":
                {
                    if (!NormalizeColor(ValueOf(tag), out var color)) return false;
                    style.Color = color;
                    return true;
                }
                case "tint":
                {
                    if (!NormalizeColor(ValueOf(tag), out var color)) return false;
                    style.Tint = color;
                    return true;
                }
                case "size":
                {
                    if (!TryNormalizeSize(ValueOf(tag), ctx.Options, out var size)) return false;
                    style.Size = size;
                    return true;
                }
                case "box":
                    style.Box = true;
                    return true;
                case "sqrt":
                {
                    var value = ValueOf(tag);
                    if (string.IsNullOrEmpty(value)) value = "√";
                    if (value.Length != 1 || value[0] == '<' || value[0] == '>') return false;
                    style.Sqrt = value;
                    return true;
                }
                case "sup":
                    style.Level = Math.Min(3, style.Level + 1);
                    return true;
                case "sub":
                    style.Level = Math.Max(-3, style.Level - 1);
                    return true;
                case "i":
                case "em":
                    style.Italic = true;
                    return true;
                case "b":
                case "strong":
                    style.Bold = true;
                    return true;
                case "a":
                {
                    var href = Attr(tag, "href");
                    if (!TryApplyGameLink(href, ref style, ctx) && IsHttpUrl(href))
                    {
                        var id = ctx.LinkAllocator.Allocate(href);
                        if (id > 0)
                        {
                            style.LinkId = id;
                            ctx.Links.Add(new RichTextLink { Id = id, Url = href, Text = string.Empty });
                            ctx.HasLinks = true;
                        }
                    }
                    return true;
                }
                case "link":
                {
                    // 真正的 HTML <link> 是 void 元素；带属性但没有游戏语法参数时
                    // 不要把它当游戏链接，避免把后面的正文整段吞成链接。
                    if (tag.Attributes != null && tag.Attributes.Count > 0 &&
                        string.IsNullOrEmpty(tag.GameArg))
                        return false;

                    var arg = ValueOf(tag);
                    if (string.IsNullOrEmpty(arg))
                    {
                        // 裸 <link> 借用 21001–899999，避开科技/配方/成就/网页段。
                        var rawId = ctx.NextRawLinkId++;
                        style.LinkId = rawId >= 21001 && rawId < RichTextLinkAllocator.WebMin ? rawId : 0;
                    }
                    else if (int.TryParse(arg, out var id) && id > 0 && id <= 999999)
                    {
                        // 游戏原生 link 可能落在网页段；先登记，网页分配时跳过。
                        ctx.LinkAllocator.Reserve(id);
                        style.LinkId = id;
                        if (RichTextLinkAllocator.IsTechLinkId(id)) ctx.HasTechLinks = true;
                        if (RichTextLinkAllocator.IsRecipeLinkId(id)) ctx.HasRecipeLinks = true;
                        if (RichTextLinkAllocator.IsAchievementLinkId(id)) ctx.HasAchievementLinks = true;
                        if (RichTextLinkAllocator.IsEntityLinkId(id)) ctx.HasEntityLinks = true;
                    }
                    else
                    {
                        return false;
                    }
                    if (style.LinkId > 0) ctx.HasLinks = true;
                    return true;
                }
                default:
                    return false;
            }
        }

        private static string ValueOf(Tag tag)
        {
            if (!string.IsNullOrEmpty(tag.GameArg)) return Unquote(tag.GameArg);
            return Attr(tag, "value");
        }

        private static string Attr(Tag tag, string name)
        {
            if (tag.Attributes == null) return string.Empty;
            return tag.Attributes.TryGetValue(name, out var value) ? value : string.Empty;
        }

        private static void EmitIcon(Tag tag, Style style, Context ctx)
        {
            var name = Attr(tag, "tag").Trim();
            if (name.Length == 0 || name.Length > 6) return;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                    return;
            }

            var showName = tag.Attributes != null && tag.Attributes.ContainsKey("name");
            var value = "\\" + name + (showName ? "-" : string.Empty) + ";";
            // 图标短码不能被转义，但外层样式（tint/size/color/sub…）要保留。
            EmitStyled(value, style, ctx, string.Empty, escape: false);
        }

        // ---------------------------------------------------------------- 输出

        private struct Style
        {
            public string Color;
            public string Tint;
            public int Size;
            public int Level;
            public bool Bold;
            public bool Italic;
            public bool Box;
            public bool Strike;
            public bool Code;
            public string Sqrt;
            public int LinkId;
        }

        private static void EmitRun(string text, Style style, Context ctx)
        {
            EmitStyled(text, style, ctx, text, escape: true);
        }

        private static void EmitStyled(
            string text,
            Style style,
            Context ctx,
            string plain,
            bool escape)
        {
            if (string.IsNullOrEmpty(text) || ctx.Truncated) return;

            var color = style.Color;
            if (color == null && style.Strike) color = ctx.Options.QuoteColor;
            // 链接色由游戏的 <link> 负责；粗体只在没有显式颜色/链接时补白色。
            if (color == null && style.Bold && style.LinkId == 0) color = "#FFFFFF";

            // 预留标签和省略号的空间。截断只发生在文本上，保证标签仍然闭合。
            var remaining = ctx.Options.MaxLength - ctx.Rich.Length - 64;
            if (remaining <= 0)
            {
                ctx.Truncated = true;
                return;
            }

            if (plain.Length > remaining)
            {
                plain = plain.Substring(0, remaining);
                ctx.Truncated = true;
            }

            var rich = new StringBuilder(plain.Length + 24);

            if (style.LinkId > 0) rich.Append("<link=").Append(style.LinkId).Append('>');
            if (color != null) rich.Append("<color=").Append(color).Append('>');
            if (style.Tint != null) rich.Append("<tint=").Append(style.Tint).Append('>');
            if (style.Size > 0) rich.Append("<size=").Append(style.Size).Append('>');

            var levels = Math.Abs(style.Level);
            for (var i = 0; i < levels; i++)
                rich.Append(style.Level > 0 ? "<sup>" : "<sub>");

            if (style.Box) rich.Append("<box>");
            if (!string.IsNullOrEmpty(style.Sqrt)) rich.Append("<sqrt=").Append(style.Sqrt).Append('>');
            if (style.Italic) rich.Append("<i>");

            rich.Append(escape ? EscapeText(plain, style.Code) : text);

            if (style.Italic) rich.Append("</i>");
            if (!string.IsNullOrEmpty(style.Sqrt)) rich.Append("</sqrt>");
            if (style.Box) rich.Append("</box>");
            for (var i = 0; i < levels; i++)
                rich.Append(style.Level > 0 ? "</sup>" : "</sub>");
            if (style.Size > 0) rich.Append("</size>");
            if (style.Tint != null) rich.Append("</tint>");
            if (color != null) rich.Append("</color>");
            if (style.LinkId > 0) rich.Append("</link>");

            ctx.AppendRich(rich.ToString());
            ctx.AppendPlain(plain);
        }

        private static string EscapeText(string text, bool code)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length + 8);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '<')
                {
                    sb.Append("<\u200B");
                }
                else if (code && c == '\\')
                {
                    sb.Append("\\\u200B");
                }
                else if (code && c == '[')
                {
                    sb.Append("[\u200B");
                }
                else if (code && c == '\t')
                {
                    sb.Append("    ");
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 解析辅助

        private static bool IsEscapable(char c)
        {
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '_':
                case '{':
                case '}':
                case '[':
                case ']':
                case '(':
                case ')':
                case '#':
                case '+':
                case '-':
                case '.':
                case '!':
                case '|':
                case '<':
                case '>':
                case '~':
                    return true;
                default:
                    return false;
            }
        }

        private static int FindRun(string s, int start, int end, int run)
        {
            for (var i = start; i + run <= end; i++)
            {
                var ok = true;
                for (var k = 0; k < run; k++)
                {
                    if (s[i + k] != '`') { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        private static string DelimiterAt(string s, int pos, int end)
        {
            var c = s[pos];
            if (c == '~')
                return pos + 1 < end && s[pos + 1] == '~' ? "~~" : string.Empty;

            if (c != '*' && c != '_') return string.Empty;
            var run = 1;
            while (pos + run < end && s[pos + run] == c && run < 3) run++;

            // '_' 不处理单词内部，避免 snake_case 被当成斜体。
            if (c == '_' && pos > 0 && char.IsLetterOrDigit(s[pos - 1])) return string.Empty;
            if (run == 1) return c.ToString();
            if (run == 2) return new string(c, 2);
            return new string(c, 3);
        }

        private static int FindDelimiter(string s, int start, int end, string marker)
        {
            for (var i = start; i + marker.Length <= end; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                var ok = true;
                for (var k = 0; k < marker.Length; k++)
                {
                    if (s[i + k] != marker[k]) { ok = false; break; }
                }
                if (!ok) continue;

                // '_' 的收尾只要求后面不是字母数字；前面必须是正文，
                // 否则 `_bar_` 的闭合会被误判成单词内部下划线。
                if (marker == "_" &&
                    i + 1 < end && char.IsLetterOrDigit(s[i + 1])) continue;
                return i;
            }
            return -1;
        }

        private static bool TryParseLink(string s, int pos, int end, out LinkInfo link, out int next)
        {
            link = default;
            next = pos;

            var closeBracket = FindBalanced(s, pos + 1, end, '[', ']');
            if (closeBracket < 0 || closeBracket + 1 >= end || s[closeBracket + 1] != '(') return false;

            var closeParen = FindBalanced(s, closeBracket + 2, end, '(', ')');
            if (closeParen < 0) return false;

            var text = s.Substring(pos + 1, closeBracket - pos - 1);
            var url = s.Substring(closeBracket + 2, closeParen - closeBracket - 2).Trim();
            if (url.StartsWith("<", StringComparison.Ordinal) && url.EndsWith(">", StringComparison.Ordinal))
                url = url.Substring(1, url.Length - 2).Trim();

            // 去掉 "url \"title\"" 里的 title。
            var space = url.IndexOf(' ');
            if (space > 0) url = url.Substring(0, space);

            link = new LinkInfo
            {
                Text = text,
                Url = IsHttpUrl(url) || IsGameKeyUrl(url) ? url : null,
            };
            next = closeParen + 1;
            return true;
        }

        private static int FindBalanced(string s, int start, int end, char open, char close)
        {
            var depth = 0;
            for (var i = start; i < end; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == open) depth++;
                else if (s[i] == close)
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        private struct LinkInfo
        {
            public string Text;
            public string Url;
        }

        /// <summary>点击打开前再校验一次协议，避免把 file:/javascript: 交给系统。</summary>
        public static bool IsHttpUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <summary>
        /// 解析科技链接：支持 `dsp://tech/dcx`、`dsp:tech/dcx`、`tech:dcx`。
        /// `dcx` 是科技 IconTag；也兼容旧的数字 ID（如 `dsp://tech/1001`）。
        /// </summary>
        public static bool TryParseTechUrl(string url, out string techKey)
        {
            return TryParseGameKey(url, "tech:", "dsp:tech/", "dsp://tech/", out techKey);
        }

        /// <summary>解析配方链接：key 是物品 IconTag（如 tk）。</summary>
        public static bool TryParseRecipeUrl(string url, out string recipeKey)
        {
            return TryParseGameKey(url, "recipe:", "dsp:recipe/", "dsp://recipe/", out recipeKey);
        }

        /// <summary>解析成就链接：key 是成就 ID 或 SID（如 1 / ACHIEVEMENT_1）。</summary>
        public static bool TryParseAchievementUrl(string url, out string achievementKey)
        {
            return TryParseGameKey(
                url, "achievement:", "dsp:achievement/", "dsp://achievement/", out achievementKey);
        }

        private static bool TryParseGameKey(
            string url,
            string plainPrefix,
            string dspPrefix,
            string dspUrlPrefix,
            out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(url)) return false;

            var value = url.Trim();
            string rest = null;
            if (value.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase))
                rest = value.Substring(plainPrefix.Length);
            else if (value.StartsWith(dspPrefix, StringComparison.OrdinalIgnoreCase))
                rest = value.Substring(dspPrefix.Length);
            else if (value.StartsWith(dspUrlPrefix, StringComparison.OrdinalIgnoreCase))
                rest = value.Substring(dspUrlPrefix.Length);

            if (rest == null) return false;
            rest = rest.Trim().Trim('/');
            if (rest.Length == 0 || rest.Length > 48) return false;
            for (var i = 0; i < rest.Length; i++)
            {
                var c = rest[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    return false;
            }
            key = rest;
            return true;
        }

        private static bool TryReadBareUrl(
            string s,
            int pos,
            int end,
            out string url,
            out int next)
        {
            url = null;
            next = pos;

            var isHttps = pos + 8 <= end &&
                string.Compare(s, pos, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0;
            var isHttp = !isHttps && pos + 7 <= end &&
                string.Compare(s, pos, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0;
            if (!isHttps && !isHttp) return false;

            var i = pos;
            while (i < end && IsBareUrlChar(s[i])) i++;
            var value = s.Substring(pos, i - pos);

            // 句末标点不属于 URL；成对括号保留，未闭合的右括号去掉。
            while (value.Length > 0 && ".,;:!?".IndexOf(value[value.Length - 1]) >= 0)
                value = value.Substring(0, value.Length - 1);

            var open = 0;
            var close = 0;
            for (var k = 0; k < value.Length; k++)
            {
                if (value[k] == '(') open++;
                else if (value[k] == ')') close++;
            }
            while (value.Length > 0 && value[value.Length - 1] == ')' && close > open)
            {
                value = value.Substring(0, value.Length - 1);
                close--;
            }

            if (value.Length <= (isHttps ? 8 : 7)) return false;
            url = value;
            next = pos + value.Length;
            return true;
        }

        private static bool IsBareUrlChar(char c)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return true;
            return "-._~:/?#[]@!$&'()*+,;=%".IndexOf(c) >= 0;
        }

        private static bool IsGameKeyUrl(string url)
        {
            return TryParseTechUrl(url, out _) ||
                   TryParseRecipeUrl(url, out _) ||
                   TryParseAchievementUrl(url, out _) ||
                   TryParseEntityUrl(url, out _);
        }

        private static int ResolveTechId(string techKey, Func<string, int> techTagResolver)
        {
            if (string.IsNullOrEmpty(techKey)) return 0;

            if (techTagResolver != null)
            {
                var resolved = techTagResolver(techKey);
                if (RichTextLinkAllocator.IsTechLinkId(resolved)) return resolved;
            }

            // 兼容旧输出 dsp://tech/1001；新输出优先走 IconTag。
            if (int.TryParse(techKey, out var numeric) &&
                RichTextLinkAllocator.IsTechLinkId(numeric))
                return numeric;
            return 0;
        }

        private static int ResolveRecipeItemId(string recipeKey, Func<string, int> recipeTagResolver)
        {
            if (string.IsNullOrEmpty(recipeKey)) return 0;

            if (recipeTagResolver != null)
            {
                var resolved = recipeTagResolver(recipeKey);
                if (resolved >= 1 && resolved <= 9999) return resolved;
            }

            if (int.TryParse(recipeKey, out var numeric) && numeric >= 1 && numeric <= 9999)
                return numeric;
            return 0;
        }

        private static int ResolveAchievementId(
            string achievementKey,
            Func<string, int> achievementKeyResolver)
        {
            if (string.IsNullOrEmpty(achievementKey)) return 0;

            if (achievementKeyResolver != null)
            {
                var resolved = achievementKeyResolver(achievementKey);
                if (resolved >= 1 && resolved <= 1000) return resolved;
            }

            if (int.TryParse(achievementKey, out var numeric) && numeric >= 1 && numeric <= 1000)
                return numeric;
            return 0;
        }

        /// <summary>把科技/配方/成就 URL 应用到 style；命中特殊 URL 时返回 true。</summary>
        private static bool TryApplyGameLink(string url, ref Style style, Context ctx)
        {
            if (TryParseTechUrl(url, out var techKey))
            {
                var techId = ResolveTechId(techKey, ctx.GameLinkResolvers?.TechTag);
                style.LinkId = techId;
                if (techId > 0)
                {
                    ctx.HasLinks = true;
                    ctx.HasTechLinks = true;
                }
                return true;
            }

            if (TryParseRecipeUrl(url, out var recipeKey))
            {
                var itemId = ResolveRecipeItemId(recipeKey, ctx.GameLinkResolvers?.RecipeTag);
                if (itemId > 0)
                {
                    style.LinkId = 10000 + itemId;
                    ctx.HasLinks = true;
                    ctx.HasRecipeLinks = true;
                }
                else
                {
                    style.LinkId = 0;
                }
                return true;
            }

            if (TryParseAchievementUrl(url, out var achievementKey))
            {
                var achId = ResolveAchievementId(achievementKey, ctx.GameLinkResolvers?.AchievementKey);
                if (achId > 0)
                {
                    style.LinkId = 20000 + achId;
                    ctx.HasLinks = true;
                    ctx.HasAchievementLinks = true;
                }
                else
                {
                    style.LinkId = 0;
                }
                return true;
            }

            if (TryParseEntityUrl(url, out var entityKey))
            {
                var id = ctx.LinkAllocator.AllocateEntity(entityKey);
                if (id > 0)
                {
                    style.LinkId = id;
                    ctx.Links.Add(new RichTextLink { Id = id, Url = entityKey, Text = string.Empty });
                    ctx.HasLinks = true;
                    ctx.HasEntityLinks = true;
                }
                else
                {
                    style.LinkId = 0;
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// dsp://entity/building/{planetId}/{buildingId}
        /// dsp://entity/planet/{planetId}
        /// 只接受正整数，避免把任意字符串带进 UI 分发。
        /// </summary>
        public static bool TryParseEntityUrl(string url, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(url)) return false;
            const string prefix = "dsp://entity/";
            if (!url.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var rest = url.Substring(prefix.Length);
            var parts = rest.Split('/');
            if (parts.Length == 2 && parts[0] == "planet")
            {
                if (!int.TryParse(parts[1], out var planetId) || planetId <= 0) return false;
                canonical = prefix + "planet/" + planetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            if (parts.Length == 3 && (parts[0] == "building" || parts[0] == "vein"))
            {
                if (!int.TryParse(parts[1], out var planetId) || planetId <= 0) return false;
                if (!int.TryParse(parts[2], out var targetId) || targetId <= 0) return false;
                canonical = prefix + parts[0] + "/" + planetId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "/" + targetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        private static int FindTagEnd(string s, int pos, int end)
        {
            char quote = '\0';
            for (var i = pos + 1; i < end; i++)
            {
                var c = s[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '>') return i;
            }
            return -1;
        }

        private static bool IsTagNameChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                   (c >= '0' && c <= '9') || c == '-';
        }

        private static bool IsAttributeNameChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                   (c >= '0' && c <= '9') || c == '-' || c == '_' || c == ':';
        }

        private static bool TryHeading(string line, out int level, out string content)
        {
            level = 0;
            content = string.Empty;
            while (level < line.Length && level < 6 && line[level] == '#') level++;
            if (level == 0 || level >= line.Length || line[level] != ' ') return false;
            content = line.Substring(level + 1).Trim();
            return true;
        }

        private static bool IsBlockquote(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.Length > 0 && trimmed[0] == '>';
        }

        private static string StripBlockquote(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '>') return line;
            trimmed = trimmed.Substring(1);
            return trimmed.StartsWith(" ", StringComparison.Ordinal) ? trimmed.Substring(1) : trimmed;
        }

        private struct ListItem
        {
            public int Indent;
            public bool Ordered;
            public string Number;
            public string Content;
            public bool? CheckBox;
        }

        private static bool TryListItem(string line, out ListItem item)
        {
            item = default;
            var indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent += line[indent] == '\t' ? 4 : 1;
            }
            if (indent >= line.Length) return false;

            var rest = line.Substring(indent);
            var ordered = false;
            var number = string.Empty;

            if (rest.Length >= 2 && (rest[0] == '-' || rest[0] == '*' || rest[0] == '+') && rest[1] == ' ')
            {
                rest = rest.Substring(2);
            }
            else
            {
                var digits = 0;
                while (digits < rest.Length && char.IsDigit(rest[digits]) && digits < 9) digits++;
                if (digits == 0 || digits + 1 >= rest.Length || rest[digits] != '.' || rest[digits + 1] != ' ')
                    return false;
                ordered = true;
                number = rest.Substring(0, digits);
                rest = rest.Substring(digits + 2);
            }

            bool? checkbox = null;
            if (rest.Length >= 3 && rest[0] == '[' && rest[2] == ']')
            {
                var c = char.ToLowerInvariant(rest[1]);
                if (c == ' ' || c == 'x')
                {
                    checkbox = c == 'x';
                    rest = rest.Substring(3);
                    if (rest.StartsWith(" ", StringComparison.Ordinal)) rest = rest.Substring(1);
                }
            }

            item = new ListItem
            {
                Indent = Math.Min(indent, 12),
                Ordered = ordered,
                Number = number,
                Content = rest,
                CheckBox = checkbox,
            };
            return true;
        }

        private static string ListPrefix(ListItem item, Context ctx)
        {
            var sb = new StringBuilder();
            sb.Append(' ', item.Indent);
            if (item.CheckBox.HasValue)
            {
                sb.Append(item.CheckBox.Value ? "[*] " : "[/] ");
            }
            else if (item.Ordered)
            {
                sb.Append(item.Number).Append(". ");
            }
            else
            {
                sb.Append("• ");
            }
            return sb.ToString();
        }

        private static bool IsHorizontalRule(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3) return false;
            var marker = '\0';
            var count = 0;
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == ' ') continue;
                if (c != '-' && c != '*' && c != '_') return false;
                if (marker == '\0') marker = c;
                else if (marker != c) return false;
                count++;
            }
            return count >= 3;
        }

        private static bool IsFence(string line, out string fence)
        {
            fence = null;
            var trimmed = line.TrimStart();
            if (trimmed.Length < 3) return false;
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) { fence = "```"; return true; }
            if (trimmed.StartsWith("~~~", StringComparison.Ordinal)) { fence = "~~~"; return true; }
            return false;
        }

        private static bool IsFenceLine(string line, string fence)
        {
            return line.TrimStart().StartsWith(fence, StringComparison.Ordinal);
        }

        private static bool IsTableStart(string[] lines, int index)
        {
            if (index + 1 >= lines.Length) return false;
            var header = lines[index];
            var sep = lines[index + 1];
            if (header.IndexOf('|') < 0 || sep.IndexOf('|') < 0) return false;

            var cells = SplitCells(sep);
            if (cells.Length == 0) return false;
            foreach (var cell in cells)
            {
                var t = cell.Trim();
                if (t.Length == 0) return false;
                var start = 0;
                var finish = t.Length;
                if (t[0] == ':') start++;
                if (finish > start && t[finish - 1] == ':') finish--;
                if (finish - start < 3) return false;
                for (var i = start; i < finish; i++)
                {
                    if (t[i] != '-') return false;
                }
            }
            return true;
        }

        private static int RenderTableFallback(string[] lines, int index, Context ctx)
        {
            var header = SplitCells(lines[index]);
            var row = index + 2;
            while (row < lines.Length && !string.IsNullOrWhiteSpace(lines[row]) && lines[row].IndexOf('|') >= 0)
            {
                var cells = SplitCells(lines[row]);
                var sb = new StringBuilder();
                var count = Math.Max(header.Length, cells.Length);
                for (var i = 0; i < count; i++)
                {
                    var cell = i < cells.Length ? cells[i].Trim() : string.Empty;
                    if (cell.Length == 0) continue;
                    if (sb.Length > 0) sb.Append(" · ");
                    if (i < header.Length && header[i].Trim().Length > 0)
                    {
                        sb.Append(header[i].Trim()).Append('：');
                    }
                    sb.Append(cell);
                }
                if (sb.Length > 0)
                {
                    RenderInline(sb.ToString(), 0, sb.Length, new Style(), ctx, null);
                    ctx.Newline();
                }
                row++;
            }

            if (row == index + 2)
            {
                // 只有表头/分隔行：仍然给出一行可读文本。
                var text = string.Join(" · ", header);
                if (text.Length > 0)
                {
                    RenderInline(text, 0, text.Length, new Style(), ctx, null);
                    ctx.Newline();
                }
            }
            return row;
        }

        private static string[] SplitCells(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 1);
            var parts = trimmed.Split('|');
            for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        private static bool NormalizeColor(string value, out string normalized)
        {
            normalized = null;
            value = Unquote(value).Trim();
            if (value.Length == 0) return false;

            if (value[0] == '#')
            {
                var digits = value.Length - 1;
                if (digits != 1 && digits != 2 && digits != 3 && digits != 4 && digits != 6 && digits != 8)
                    return false;
                for (var i = 1; i < value.Length; i++)
                {
                    if (!IsHex(value[i])) return false;
                }
                normalized = value;
                return true;
            }

            var name = value.ToLowerInvariant();
            if (!NamedColors.Contains(name)) return false;
            normalized = name;
            return true;
        }

        private static bool TryNormalizeSize(string value, RichTextOptions options, out int size)
        {
            size = 0;
            value = Unquote(value).Trim();
            if (value.Length == 0 || value == "0") return true;
            if (value.Length > 6) return false;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }
            if (!int.TryParse(value, out var parsed)) return false;
            size = Clamp(parsed, options.MinSize, options.MaxSize);
            return true;
        }

        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2) return value ?? string.Empty;
            var first = value[0];
            if ((first == '"' || first == '\'') && value[value.Length - 1] == first)
                return value.Substring(1, value.Length - 2);
            return value;
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ---------------------------------------------------------------- 上下文

        private sealed class Context
        {
            public readonly RichTextOptions Options;
            public readonly StringBuilder Rich = new StringBuilder();
            public readonly StringBuilder Plain = new StringBuilder();
            public readonly List<RichTextLink> Links = new List<RichTextLink>();
            public readonly RichTextLinkAllocator LinkAllocator;
            public readonly RichTextLinkResolvers GameLinkResolvers;
            /// <summary>没有 URL 的裸 &lt;link&gt; 只用游戏保留段，避免占用网页段。</summary>
            public int NextRawLinkId = 21001;
            public bool HasLinks;
            public bool HasTechLinks;
            public bool HasRecipeLinks;
            public bool HasAchievementLinks;
            public bool HasEntityLinks;
            public bool HasCheckBoxes;
            public bool Truncated;

            public Context(
                RichTextOptions options,
                RichTextLinkAllocator linkAllocator,
                RichTextLinkResolvers gameLinkResolvers)
            {
                Options = options;
                LinkAllocator = linkAllocator;
                GameLinkResolvers = gameLinkResolvers;
            }

            public void AppendRich(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                Rich.Append(value);
            }

            public void AppendPlain(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                Plain.Append(value);
            }

            public void Newline()
            {
                if (Truncated) return;
                if (Rich.Length > 0 && Rich[Rich.Length - 1] == '\n') return;
                Rich.Append('\n');
                Plain.Append('\n');
            }

            /// <summary>
            /// 段落间距：确保当前行结束后再空一行，但连续空行只保留一行。
            /// Newline 会折叠连续换行，所以段落之间需要单独的 BlankLine。
            /// </summary>
            public void BlankLine()
            {
                BlankLines(1);
            }

            /// <summary>确保当前行后至少留下 count 个空行；已存在的空行不重复叠加。</summary>
            public void BlankLines(int count)
            {
                if (Truncated || Rich.Length == 0) return;
                if (count < 1) count = 1;
                if (Rich[Rich.Length - 1] != '\n')
                {
                    Rich.Append('\n');
                    Plain.Append('\n');
                }

                var trailing = 0;
                while (trailing < Rich.Length && Rich[Rich.Length - 1 - trailing] == '\n')
                    trailing++;
                while (trailing < count + 1)
                {
                    Rich.Append('\n');
                    Plain.Append('\n');
                    trailing++;
                }
            }

            public void TrimTrailingNewline()
            {
                while (Rich.Length > 0 && Rich[Rich.Length - 1] == '\n') Rich.Length--;
                while (Plain.Length > 0 && Plain[Plain.Length - 1] == '\n') Plain.Length--;
            }

            public RichTextResult Build()
            {
                TrimTrailingNewline();
                if (Truncated)
                {
                    Rich.Append('…');
                    Plain.Append('…');
                }

                var result = new RichTextResult
                {
                    HasLinks = HasLinks,
                    HasTechLinks = HasTechLinks,
                    HasRecipeLinks = HasRecipeLinks,
                    HasAchievementLinks = HasAchievementLinks,
                    HasEntityLinks = HasEntityLinks,
                    HasCheckBoxes = HasCheckBoxes,
                    Truncated = Truncated,
                };
                result.Rich = Rich.ToString();
                result.Plain = Plain.ToString();
                result.Links.AddRange(Links);
                return result;
            }
        }
    }
}
