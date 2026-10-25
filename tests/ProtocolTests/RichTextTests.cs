using IcarusII.Common.Protocol;
using IcarusII.Common.Text;

namespace IcarusII.Tests
{
    /// <summary>
    /// 富文本格式化器的离线回归。重点是“Markdown 外层 + HTML 子集”的展开结果，
    /// 以及不支持/错配的标签不会把后面的文本吞掉。
    /// </summary>
    internal static class RichTextTests
    {
        public static void Run()
        {
            Eq("富文本能力名", "ui.richtext.v2", Protocol.CapabilityRichTextV2);

            // ---- HTML 子集 ----
            Eq("color value", "<color=#FFD966>重点</color>", Body("<color value=\"#FFD966\">重点</color>"));
            Eq("color 命名色", "<color=red>重点</color>", Body("<color value=\"red\">重点</color>"));
            Eq("size value", "<size=20>大字</size>", Body("<size value=\"20\">大字</size>"));
            Eq("box", "<box>关键值</box>", Body("<box>关键值</box>"));
            Eq("sqrt", "<sqrt=√>x+1</sqrt>", Body("<sqrt value=\"√\">x+1</sqrt>"));
            Eq("sup", "x<sup>2</sup>", Body("x<sup>2</sup>"));
            Eq("sub", "H<sub>2</sub>O", Body("H<sub>2</sub>O"));
            Eq("em", "<i>术语</i>", Body("<em>术语</em>"));
            Eq("b -> 亮白", "<color=#FFFFFF>重点</color>", Body("<b>重点</b>"));
            Eq("嵌套 color+size", "<color=#FFD966><size=20>重点</size></color>",
                Body("<color value=\"#FFD966\"><size value=\"20\">重点</size></color>"));

            // ---- Markdown ----
            Eq("粗体", "<color=#FFFFFF>重点</color>", Body("**重点**"));
            Eq("斜体", "<i>术语</i>", Body("*术语*"));
            Eq("下划线斜体", "<i>术语</i>", Body("_术语_"));
            Eq("下划线粗体", "<color=#FFFFFF>重点</color>", Body("__重点__"));
            Eq("粗斜体", "<color=#FFFFFF><i>重点</i></color>", Body("***重点***"));
            Eq("单词内下划线不斜体", "snake_case", Body("snake_case"));
            Eq("标题", "<color=#FFFFFF><size=20>标题</size></color>", Body("# 标题"));
            Eq("二级标题字号", "<color=#FFFFFF><size=18>标题</size></color>", Body("## 标题"));
            Eq("三级标题字号", "<color=#FFFFFF><size=16>标题</size></color>", Body("### 标题"));
            Eq("四级标题回到正文大小", "<color=#FFFFFF><size=14>标题</size></color>", Body("#### 标题"));
            Eq("标题与正文之间空行",
                "<color=#FFFFFF><size=20>标题</size></color>\n\n正文",
                Body("# 标题\n正文"));
            Eq("段落之间空行", "第一段\n\n第二段", Body("第一段\n\n第二段"));
            Eq("英文软换行合并", "hello world", Body("hello\nworld"));
            Eq("中文软换行不加空格", "这是一段", Body("这是\n一段"));
            Eq("中文软换行保留 Plain", "这是一段", GameRichText.Format("这是\n一段", RichTextProfile.Body).Plain);
            Eq("行尾双空格是硬换行", "hello\nworld", Body("hello  \nworld"));
            Eq("行尾反斜杠是硬换行", "hello\nworld", Body("hello\\\nworld"));
            Eq("连续空行折叠", "第一段\n\n第二段", Body("第一段\n\n第二段"));
            Eq("列表与后文之间空行", "• A\n• B\n\n正文", Body("- A\n- B\n正文"));
            Eq("列表前与段落之间空行", "说明\n\n• A", Body("说明\n- A"));
            Eq("代码块与正文之间空行", "<color=#9CDCFE><size=13><box>\u00A0\n  x  \n\u00A0</box></size></color>\n\n正文", Body("```\nx\n```\n正文"));
            Eq("行内代码", "<color=#9CDCFE>x<\u200By</color>", Body("`x<y`"));
            Eq("围栏代码", "<color=#9CDCFE><size=13><box>\u00A0\n  int a = 1;  \n\u00A0</box></size></color>",
                Body("```\nint a = 1;\n```"));
            Eq("无序列表", "• 条目", Body("- 条目"));
            Eq("有序列表", "1. 条目", Body("1. 条目"));
            Eq("任务列表未勾选", "[/] 待办", Body("- [ ] 待办"));
            Eq("任务列表已勾选", "[*] 完成", Body("- [x] 完成"));
            Eq("引用", "<color=#8FA3B8>▏ </color><color=#8FA3B8>引用</color>", Body("> 引用"));
            Eq("引用空行也保留装饰线",
                "<color=#8FA3B8>▏ </color><color=#8FA3B8>第一行</color>\n" +
                "<color=#8FA3B8>▏ </color>\n" +
                "<color=#8FA3B8>▏ </color><color=#8FA3B8>第二行</color>",
                Body("> 第一行\n\n> 第二行"));
            Eq("分隔线", "<color=#8FA3B8>────────</color>", Body("---"));
            Eq("表格降级", "A：1 · B：2", Body("| A | B |\n|---|---|\n| 1 | 2 |"));

            // ---- 样式优先级：显式颜色不能被粗体的白色盖掉 ----
            Eq("MD 粗体包 HTML 颜色", "<color=#FFD966>重点</color>",
                Body("**<color value=\"#FFD966\">重点</color>**"));
            Eq("HTML 颜色包 MD 粗体", "<color=#FFD966>重点</color>",
                Body("<color value=\"#FFD966\">**重点**</color>"));
            Eq("颜色 + 斜体", "<color=red><i>术语</i></color>",
                Body("*<color value=\"red\">术语</color>*"));
            Eq("斜体 + 颜色", "<color=red><i>术语</i></color>",
                Body("<color value=\"red\">*术语*</color>"));
            Eq("删除线降级", "<color=#8FA3B8>删除</color>", Body("~~删除~~"));

            // ---- 链接 ----
            var htmlLink = GameRichText.Format(
                "<a href=\"https://example.com\">文档</a>", RichTextProfile.Body);
            Eq("HTML 链接", "<link=900000>文档</link>", htmlLink.Rich);
            Check("HTML 链接有动作", htmlLink.HasLinks);
            Eq("HTML 链接 URL", "https://example.com", htmlLink.Links.Count == 1 ? htmlLink.Links[0].Url : "");
            Eq("HTML 链接 ID", 900000, htmlLink.Links.Count == 1 ? htmlLink.Links[0].Id : 0);

            var mdLink = GameRichText.Format("[文档](https://example.com)", RichTextProfile.Body);
            Eq("Markdown 链接", "<link=900000>文档</link>", mdLink.Rich);
            Check("Markdown 链接有动作", mdLink.HasLinks);

            // 链接里的粗体不再补白色，避免盖掉链接色。
            Eq("链接内粗体不补白", "<link=900000>文档</link>",
                Body("[**文档**](https://example.com)"));

            var twoLinks = GameRichText.Format(
                "[A](https://a.example) [B](https://b.example)", RichTextProfile.Body);
            Check("两个链接顺序分配",
                twoLinks.Rich.Contains("<link=900000>A</link>") &&
                twoLinks.Rich.Contains("<link=900001>B</link>"));
            var reused = GameRichText.Format(
                "[A](https://a.example) [A2](https://a.example)", RichTextProfile.Body);
            Check("同 URL 复用 ID",
                reused.Rich.Contains("<link=900000>A</link>") &&
                reused.Rich.Contains("<link=900000>A2</link>"));

            var reserved = GameRichText.Format(
                "<link=950000>原生</link> [网页](https://web.example)", RichTextProfile.Body);
            Check("原生 ID 被跳过",
                reserved.Rich.Contains("<link=900000>网页</link>"));

            var techLink = GameRichText.Format("[电磁学](dsp://tech/1001)", RichTextProfile.Body);
            Eq("科技链接数字兼容", "<link=1001>电磁学</link>", techLink.Rich);
            Check("科技链接有科技动作", techLink.HasTechLinks);

            var linkResolvers = new RichTextLinkResolvers
            {
                TechTag = tag => tag == "dcx" ? 1001 : 0,
                RecipeTag = tag => tag == "tk" ? 1101 : 0,
                AchievementKey = key => key == "1" || key == "ACHIEVEMENT_1" ? 1 : 0,
            };

            var tagTechLink = GameRichText.Format(
                "[电磁学](dsp://tech/dcx)", RichTextProfile.Body, null, null, linkResolvers);
            Eq("科技链接 IconTag", "<link=1001>电磁学</link>", tagTechLink.Rich);
            Check("IconTag 科技链接有动作", tagTechLink.HasTechLinks);
            var htmlTechLink = GameRichText.Format(
                "<a href=\"dsp://tech/dcx\">电磁学</a>", RichTextProfile.Body, null, null,
                linkResolvers);
            Eq("HTML 科技链接 IconTag", "<link=1001>电磁学</link>", htmlTechLink.Rich);

            var unknownTech = GameRichText.Format(
                "[未知](dsp://tech/nope)", RichTextProfile.Body, null, null,
                new RichTextLinkResolvers());
            Eq("未知 IconTag 不生成链接", "未知", unknownTech.Rich);
            Check("未知 IconTag 无科技动作", !unknownTech.HasTechLinks);

            var recipeLink = GameRichText.Format(
                "[铁块](dsp://recipe/tk)", RichTextProfile.Body, null, null, linkResolvers);
            Eq("配方链接 IconTag", "<link=11101>铁块</link>", recipeLink.Rich);
            Check("配方链接有动作", recipeLink.HasRecipeLinks && recipeLink.HasGameLinks);

            var achievementLink = GameRichText.Format(
                "[成就](dsp://achievement/1)", RichTextProfile.Body, null, null, linkResolvers);
            Eq("成就链接 ID", "<link=20001>成就</link>", achievementLink.Rich);
            Check("成就链接有动作", achievementLink.HasAchievementLinks && achievementLink.HasGameLinks);

            var achievementSid = GameRichText.Format(
                "[成就](dsp://achievement/ACHIEVEMENT_1)", RichTextProfile.Body, null, null,
                linkResolvers);
            Eq("成就链接 SID", "<link=20001>成就</link>", achievementSid.Rich);

            var entityBuilding = GameRichText.Format(
                "[建筑](dsp://entity/building/103/6956)", RichTextProfile.Body);
            Eq("实体建筑链接 ID", "<link=800000>建筑</link>", entityBuilding.Rich);
            Check("实体建筑链接有动作", entityBuilding.HasEntityLinks && entityBuilding.HasGameLinks);
            var entityPlanet = GameRichText.Format(
                "[星球](dsp://entity/planet/103)", RichTextProfile.Body);
            Eq("实体星球链接 ID（每个 Text 独立分配）", "<link=800000>星球</link>", entityPlanet.Rich);
            Check("实体星球链接有动作", entityPlanet.HasEntityLinks);
            Check("实体 URL 建筑解析",
                GameRichText.TryParseEntityUrl("dsp://entity/building/103/6956", out var eb) &&
                eb == "dsp://entity/building/103/6956");
            Check("实体 URL 星球解析",
                GameRichText.TryParseEntityUrl("dsp://entity/planet/103", out var ep) &&
                ep == "dsp://entity/planet/103");
            Check("实体 URL 拒绝非法段", !GameRichText.TryParseEntityUrl("dsp://entity/building/0/1", out _));
            var entityVein = GameRichText.Format(
                "[矿脉](dsp://entity/vein/103/415)", RichTextProfile.Body);
            Eq("实体矿脉链接 ID", "<link=800000>矿脉</link>", entityVein.Rich);
            Check("实体矿脉链接有动作", entityVein.HasEntityLinks && entityVein.HasGameLinks);
            Check("实体 URL 矿脉解析",
                GameRichText.TryParseEntityUrl("dsp://entity/vein/103/415", out var ev) &&
                ev == "dsp://entity/vein/103/415");
            Check("实体 URL 矿脉拒绝零 id", !GameRichText.TryParseEntityUrl("dsp://entity/vein/103/0", out _));
            var entityAllocator = new RichTextLinkAllocator();
            Eq("实体 ID 从 800000 开始", 800000, entityAllocator.AllocateEntity("dsp://entity/planet/1"));
            Eq("实体 ID 顺序递增", 800001, entityAllocator.AllocateEntity("dsp://entity/planet/2"));
            Check("实体 ID 边界", RichTextLinkAllocator.IsEntityLinkId(800000) &&
                RichTextLinkAllocator.IsEntityLinkId(899999) &&
                !RichTextLinkAllocator.IsEntityLinkId(799999) &&
                !RichTextLinkAllocator.IsEntityLinkId(900000));

            var unknownRecipe = GameRichText.Format(
                "[未知](dsp://recipe/nope)", RichTextProfile.Body, null, null,
                new RichTextLinkResolvers());
            Eq("未知配方 key 不生成链接", "未知", unknownRecipe.Rich);
            Check("未知配方无动作", !unknownRecipe.HasGameLinks);

            var oneLink = GameRichText.Format("[科技树](dsp://tech/1)", RichTextProfile.Body);
            Eq("1 不再作为科技链接", "科技树", oneLink.Rich);
            Check("1 不再有科技动作", !oneLink.HasLinks && !oneLink.HasTechLinks);

            var badTech = GameRichText.Format("[无效](dsp://tech/2)", RichTextProfile.Body);
            Eq("非科技 ID 不生成链接", "无效", badTech.Rich);
            Check("非科技 ID 无链接动作", !badTech.HasLinks && !badTech.HasTechLinks);

            Check("科技 URL 解析 dsp://",
                GameRichText.TryParseTechUrl("dsp://tech/dcx", out var techKey1) && techKey1 == "dcx");
            Check("科技 URL 解析 tech:",
                GameRichText.TryParseTechUrl("tech:1001", out var techKey2) && techKey2 == "1001");
            Check("科技 URL 拒绝空 key", !GameRichText.TryParseTechUrl("dsp://tech/", out _));
            Check("科技 URL 拒绝非法字符", !GameRichText.TryParseTechUrl("dsp://tech/dcx!", out _));
            Check("配方 URL 解析",
                GameRichText.TryParseRecipeUrl("dsp://recipe/tk", out var recipeKey) && recipeKey == "tk");
            Check("成就 URL 解析",
                GameRichText.TryParseAchievementUrl("dsp://achievement/1", out var achKey) && achKey == "1");
            Check("配方/成就 ID 边界",
                RichTextLinkAllocator.IsRecipeLinkId(10000) &&
                RichTextLinkAllocator.IsRecipeLinkId(19999) &&
                !RichTextLinkAllocator.IsRecipeLinkId(9999) &&
                !RichTextLinkAllocator.IsRecipeLinkId(20000) &&
                RichTextLinkAllocator.IsAchievementLinkId(20000) &&
                RichTextLinkAllocator.IsAchievementLinkId(21000) &&
                !RichTextLinkAllocator.IsAchievementLinkId(21001));
            Check("科技 ID 边界",
                !RichTextLinkAllocator.IsTechLinkId(1) &&
                RichTextLinkAllocator.IsTechLinkId(1000) &&
                RichTextLinkAllocator.IsTechLinkId(1001) &&
                RichTextLinkAllocator.IsTechLinkId(9999) &&
                !RichTextLinkAllocator.IsTechLinkId(0) &&
                !RichTextLinkAllocator.IsTechLinkId(2) &&
                !RichTextLinkAllocator.IsTechLinkId(10000));

            var rawTech = GameRichText.Format("<link=1001>电磁学</link>", RichTextProfile.Body);
            Check("游戏原生 link 标记科技动作", rawTech.HasTechLinks);
            var rawRecipe = GameRichText.Format("<link=11101>铁块</link>", RichTextProfile.Body);
            Check("游戏原生 link 标记配方动作", rawRecipe.HasRecipeLinks);
            var rawAchievement = GameRichText.Format("<link=20001>成就</link>", RichTextProfile.Body);
            Check("游戏原生 link 标记成就动作", rawAchievement.HasAchievementLinks);
            var bareLink = GameRichText.Format("<link>裸链接</link>", RichTextProfile.Body);
            Eq("裸 link 避开科技/配方/成就/网页段", "<link=21001>裸链接</link>", bareLink.Rich);

            var bareUrl = GameRichText.Format("来源：https://example.com/a.txt。", RichTextProfile.Body);
            Eq("裸 URL 自动链接", "来源：<link=900000>https://example.com/a.txt</link>。", bareUrl.Rich);
            Check("裸 URL 注册点击映射", bareUrl.Links.Count == 1 && bareUrl.Links[0].Url == "https://example.com/a.txt");
            var bareParen = GameRichText.Format("（https://example.com）", RichTextProfile.Body);
            Eq("裸 URL 去掉中文括号", "（<link=900000>https://example.com</link>）", bareParen.Rich);
            var codeUrl = GameRichText.Format("`https://example.com`", RichTextProfile.Body);
            Check("行内代码不自动链接", !codeUrl.HasLinks && codeUrl.Rich.Contains("https://example.com"));

            Check("打开前允许 https", GameRichText.IsHttpUrl("https://example.com"));
            Check("打开前允许 http", GameRichText.IsHttpUrl("http://example.com"));
            Check("打开前拒绝 javascript", !GameRichText.IsHttpUrl("javascript:alert(1)"));
            Check("打开前拒绝 file", !GameRichText.IsHttpUrl("file:///etc/passwd"));

            var allocator = new RichTextLinkAllocator();
            Eq("网页 ID 从 900000 开始", 900000, allocator.Allocate("https://a.example"));
            Eq("网页 ID 顺序递增", 900001, allocator.Allocate("https://b.example"));
            Eq("同 URL 复用", 900000, allocator.Allocate("https://a.example"));
            allocator.Reserve(900002);
            Eq("跳过已占用 ID", 900003, allocator.Allocate("https://c.example"));
            Check("网页 ID 下界", !RichTextLinkAllocator.IsWebId(899999) &&
                                 RichTextLinkAllocator.IsWebId(900000));
            Check("网页 ID 上界", RichTextLinkAllocator.IsWebId(999999) &&
                                 !RichTextLinkAllocator.IsWebId(1000000));

            // ---- 图标 ----
            Eq("图标", "\\tk;", Body("<icon tag=\"tk\" />"));
            Eq("图标 + 名称", "\\tk-;", Body("<icon tag=\"tk\" name />"));
            Eq("图标 + 着色", "<tint=#FFD966>\\tk;</tint>",
                Body("<tint value=\"#FFD966\"><icon tag=\"tk\" /></tint>"));

            // ---- 游戏原生语法：精确兼容，不放宽到错误变体 ----
            Eq("游戏原生 color", "<color=#FFD966>重点</color>", Body("<color=#FFD966>重点</color>"));
            Eq("游戏原生 size", "<size=20>大字</size>", Body("<size=20>大字</size>"));
            Eq("游戏原生 sqrt", "<sqrt=√>x</sqrt>", Body("<sqrt=√>x</sqrt>"));
            Eq("游戏原生 tint", "<tint=#FFD966>重点</tint>", Body("<tint=#FFD966>重点</tint>"));
            Eq("漏等号的颜色仍按字面量转义",
                "<\u200Bcolor#FFD966>重点<\u200B/color>",
                Body("<color#FFD966>重点</color>"));
            Eq("非游戏标签带等号仍按字面量转义",
                "<\u200Bdiv=foo>x<\u200B/div>",
                Body("<div=foo>x</div>"));

            // ---- 未知标签必须转义，不能吞内容 ----
            Eq("未知 HTML 转义", "<\u200Bdiv>x<\u200B/div>", Body("<div>x</div>"));
            Eq("裸尖括号", "a<\u200B b", Body("a< b"));
            Eq("非法颜色转义", "<\u200Bcolor value=\"nope\">x<\u200B/color>",
                Body("<color value=\"nope\">x</color>"));

            // ---- 纯文本 / 行内 profile ----
            Eq("Plain 转义", "**x** <\u200By", GameRichText.Format("**x** <y", RichTextProfile.Plain).Rich);
            Eq("Inline 不解析标题", "# x", GameRichText.Format("# x", RichTextProfile.Inline).Rich);
            Eq("复制用纯文本", "重点 文字",
                GameRichText.Format("**重点** <color value=\"#FFD966\">文字</color>", RichTextProfile.Body).Plain);

            // ---- 真表格切分 ----
            var split = GameRichText.SplitTables("前\n| A | B |\n|---|---|\n| 1 | 2 |\n后");
            Check("表格切三段", split.Count == 3);
            Eq("表格前文本", "前", split.Count == 3 ? split[0].Text : "");
            Check("中间是表格", split.Count == 3 && split[1].IsTable);
            Eq("表头数", 2, split.Count == 3 ? split[1].Table.Headers.Count : 0);
            Eq("表头 A", "A", split.Count == 3 ? split[1].Table.Headers[0] : "");
            Eq("表格行数", 1, split.Count == 3 ? split[1].Table.Rows.Count : 0);
            Eq("表格后文本", "后", split.Count == 3 ? split[2].Text : "");

            var fenceSplit = GameRichText.SplitTables("```\n| A | B |\n|---|---|\n| 1 | 2 |\n```");
            Check("代码围栏里的表格不切分", fenceSplit.Count == 1 && !fenceSplit[0].IsTable);

            var onlyTable = GameRichText.SplitTables("| A | B |\n|---|---|\n| 1 | 2 |");
            Check("只有表格时仍返回表格段", onlyTable.Count == 1 && onlyTable[0].IsTable);

            var aligned = GameRichText.SplitTables("| A | B | C |\n|:---|:---:|---:|\n| 1 | 2 | 3 |");
            Check("对齐切分", aligned.Count == 1 && aligned[0].IsTable);
            Eq("左对齐", RichTextTableAlign.Left,
                aligned.Count == 1 ? aligned[0].Table.Alignments[0] : RichTextTableAlign.Left);
            Eq("居中", RichTextTableAlign.Center,
                aligned.Count == 1 ? aligned[0].Table.Alignments[1] : RichTextTableAlign.Left);
            Eq("右对齐", RichTextTableAlign.Right,
                aligned.Count == 1 ? aligned[0].Table.Alignments[2] : RichTextTableAlign.Left);

            // ---- 复选框标记 ----
            var checkbox = GameRichText.Format("- [x] 完成", RichTextProfile.Body);
            Check("HasCheckBoxes", checkbox.HasCheckBoxes);

            // ---- 长度上限 ----
            var longText = new string('a', 13000);
            var truncated = GameRichText.Format(longText, RichTextProfile.Body);
            Check("长文本会截断", truncated.Truncated);
            Check("长文本有省略号", truncated.Rich.EndsWith("…", System.StringComparison.Ordinal));
            Check("长文本不超上限太多", truncated.Rich.Length <= 12000 + 256);
        }

        private static string Body(string raw)
        {
            return GameRichText.Format(raw, RichTextProfile.Body).Rich;
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
