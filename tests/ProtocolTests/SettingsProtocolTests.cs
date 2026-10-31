using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;
using IcarusII.Input;

namespace IcarusII.Tests
{
    /// <summary>
    /// 协议附录 A.14（设定页）与 A.14.7（快捷键）的离线断言。
    /// 只组装输入并断言输出，不重写业务逻辑。
    /// </summary>
    internal static class SettingsProtocolTests
    {
        public static void Run()
        {
            KeyChordTests();
            PayloadTests();
            ParseTests();
        }

        private static void KeyChordTests()
        {
            Check("空串解析失败", !KeyChord.TryParse("", out _));
            Check("只有修饰键解析失败", !KeyChord.TryParse("Ctrl+Shift", out _));
            Check("两个主键解析失败", !KeyChord.TryParse("A+B", out _));

            Check("解析 Ctrl+Enter", KeyChord.TryParse("Ctrl+Enter", out var chord)
                && chord.Format() == "Ctrl+Return");
            Eq("显示 Ctrl + Enter", "Ctrl + Enter", chord.Display());
            Check("解析 Control+Return 别名", KeyChord.TryParse("Control+Return", out var ctrlReturn)
                && ctrlReturn.Equals(chord));

            Check("解析小写", KeyChord.TryParse("ctrl+alt+shift+f8", out var f8)
                && f8.Format() == "Ctrl+Alt+Shift+F8");
            Eq("F8 显示", "Ctrl + Alt + Shift + F8", f8.Display());

            Check("单键 R", KeyChord.TryParse("R", out var r) && r.Format() == "R" && !r.Ctrl);
            Check("字母大小写等价", r.Equals(KeyChord.Of("r")));
            Check("Shift+Enter 与 Enter 不等", !KeyChord.Of("Return").Equals(KeyChord.Of("Return", shift: true)));
            Check("Ctrl+Enter 与 Enter 不等", !KeyChord.Of("Return").Equals(KeyChord.Of("Return", ctrl: true)));

            Check("解析失败回落默认", KeyChord.ParseOrDefault("nonsense+", KeyChord.Of("F8")).Equals(KeyChord.Of("F8")));
            Check("空值回落默认", KeyChord.ParseOrDefault(null, KeyChord.Of("F8")).Equals(KeyChord.Of("F8")));
            Check("None 没有主键", KeyChord.None.IsNone);

            // 可打印字符判定表
            Check("S 输入 s", KeyChord.Of("S").TryGetTypedChar(out var lower) && lower == 's');
            Check("Shift+S 输入 S", KeyChord.Of("S", shift: true).TryGetTypedChar(out var upper) && upper == 'S');
            Check("Ctrl+S 不产生字符", !KeyChord.Of("S", ctrl: true).TryGetTypedChar(out _));
            Check("Alt+S 不产生字符", !KeyChord.Of("S", alt: true).TryGetTypedChar(out _));
            Check("Alpha1 输入 1", KeyChord.Of("Alpha1").TryGetTypedChar(out var one) && one == '1');
            Check("Shift+Alpha1 输入 !", KeyChord.Of("Alpha1", shift: true).TryGetTypedChar(out var bang) && bang == '!');
            Check("Space 输入空格", KeyChord.Of("Space").TryGetTypedChar(out var space) && space == ' ');
            Check("Period 输入 .", KeyChord.Of("Period").TryGetTypedChar(out var dot) && dot == '.');
            Check("Shift+Comma 输入 <", KeyChord.Of("Comma", shift: true).TryGetTypedChar(out var lt) && lt == '<');
            Check("Return 不产生字符", !KeyChord.Of("Return").TryGetTypedChar(out _));
            Check("F8 不产生字符", !KeyChord.Of("F8").TryGetTypedChar(out _));
            Check("方向键不产生字符", !KeyChord.Of("UpArrow").TryGetTypedChar(out _));
        }

        private static void PayloadTests()
        {
            var disabled = new List<string> { "game_take_output", "", "run_script" };
            var toolsSet = Protocol.BuildToolsSet(disabled);
            Eq("tools.set 去空", 2, toolsSet["disabled"].Count);
            Eq("tools.set 第一项", "game_take_output", toolsSet["disabled"][0].AsString());
            Eq("tools.set 第二项", "run_script", toolsSet["disabled"][1].AsString());

            var emptySet = Protocol.BuildToolsSet(new List<string>());
            Eq("tools.set 空列表写成 []", 0, emptySet["disabled"].Count);
            Check("tools.set 空列表是数组", emptySet["disabled"].IsArray);

            var export = Protocol.BuildConversationsExport("saveA", "conv_1");
            Eq("export saveKey", "saveA", export["saveKey"].AsString());
            Eq("export conversationId", "conv_1", export["conversationId"].AsString());
            Eq("export format", "md", export["format"].AsString());

            var target = Protocol.BuildConversationTarget("conv_1", "saveB");
            Eq("delete saveKey", "saveB", target["saveKey"].AsString());
            var rename = Protocol.BuildConversationRename("conv_1", "标题", "saveB");
            Eq("rename saveKey", "saveB", rename["saveKey"].AsString());
            Eq("rename title", "标题", rename["title"].AsString());

            var config = Protocol.BuildConfigSet(null, null, null, null, null, null, null, null, null,
                "limited", false, new List<string> { "run_script" });
            Eq("config.set disabledTools", "run_script", config["disabledTools"][0].AsString());
        }

        private static void ParseTests()
        {
            var catalog = JsonValue.Parse(
                "{\"name\":\"game_get_overview\",\"label\":\"存档概览\",\"description\":\"d\"," +
                "\"group\":\"game.read\",\"risk\":0,\"source\":\"builtin\",\"sourceName\":\"内置\"," +
                "\"enabled\":false}");
            var entry = ToolCatalogEntryInfo.Parse(catalog);
            Check("目录项名称", entry != null && entry.Name == "game_get_overview");
            Eq("目录项 group", "game.read", entry.Group);
            Eq("目录项 risk", 0, entry.Risk);
            Eq("目录项 source", "builtin", entry.Source);
            Eq("目录项 sourceName", "内置", entry.SourceName);
            Check("目录项 enabled=false", !entry.Enabled);

            var list = new List<ToolCatalogEntryInfo>();
            ToolCatalogEntryInfo.ParseList(JsonValue.Parse(
                "[{\"name\":\"a\"},{\"name\":\"b\"},{\"label\":\"缺名字\"}]"), list);
            Eq("目录解析跳过缺 name", 2, list.Count);
            Check("老目录缺省 enabled=true", list[0].Enabled);

            var state = ToolsStateInfo.Parse(JsonValue.Parse(
                "{\"ok\":true,\"rebuilt\":true,\"disabled\":[\"run_script\"]," +
                "\"toolCatalog\":[{\"name\":\"run_script\",\"enabled\":false}]}"));
            Check("tools.state ok", state.Ok && state.Rebuilt);
            Eq("tools.state disabled", "run_script", state.Disabled[0]);
            Eq("tools.state 目录", 1, state.ToolCatalog.Count);

            var configState = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"disabledTools\":[\"run_script\",\"game_take_output\"]}"));
            Eq("config.state disabledTools", 2, configState.DisabledTools.Count);
            Eq("config.state disabledTools[1]", "game_take_output", configState.DisabledTools[1]);

            var index = ConversationsIndexedInfo.Parse(JsonValue.Parse(
                "{\"currentSaveKey\":\"saveA\",\"saves\":[{\"saveKey\":\"saveA\",\"saveName\":\"我的档\"," +
                "\"lastSeen\":1760000000000,\"current\":true,\"conversations\":[" +
                "{\"id\":\"conv_1\",\"title\":\"产线\",\"updatedAt\":1760000000001,\"messageCount\":12,\"isActive\":true}]}]}"));
            Eq("索引当前存档", "saveA", index.CurrentSaveKey);
            Eq("索引分组数", 1, index.Saves.Count);
            Eq("索引存档名", "我的档", index.Saves[0].SaveName);
            Check("索引 current", index.Saves[0].Current);
            Eq("索引会话标题", "产线", index.Saves[0].Conversations[0].Title);
            Eq("索引消息数", 12, index.Saves[0].Conversations[0].MessageCount);
            Check("索引 isActive", index.Saves[0].Conversations[0].IsActive);

            var exported = ConversationsExportedInfo.Parse(JsonValue.Parse(
                "{\"ok\":true,\"path\":\"/tmp/x.md\",\"bytes\":42,\"messageCount\":3}"));
            Check("导出 ok", exported.Ok);
            Eq("导出 path", "/tmp/x.md", exported.Path);
            Eq("导出 bytes", 42, exported.Bytes);
            Eq("导出 messageCount", 3, exported.MessageCount);

            var failed = ConversationsExportedInfo.Parse(JsonValue.Parse(
                "{\"ok\":false,\"error\":\"empty\"}"));
            Check("导出空会话", !failed.Ok && failed.Error == "empty");
        }

        private static void Check(string name, bool condition)
        {
            Program.Check(name, condition);
        }

        private static void Eq(string name, object expected, object actual)
        {
            Program.Eq(name, expected, actual);
        }
    }
}
