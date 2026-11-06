using System;
using System.Collections.Generic;
using System.Text;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;
using IcarusII.Core;

namespace IcarusII.Tests
{
    /// <summary>
    /// M0 的本机验证程序（零测试框架依赖，便于在任何环境跑）。
    ///
    ///   dotnet run --project tests/ProtocolTests                 # JSON / 协议单测
    ///   dotnet run --project tests/ProtocolTests -- e2e <url> <token>   # 端到端（需 sidecar 在跑）
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length > 0 && args[0] == "e2e")
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("用法: ProtocolTests e2e <ws-url> <token>");
                    return 2;
                }
                return EndToEndTests.Run(args[1], args[2]);
            }

            Section("JSON 解析");
            JsonParseTests();

            Section("JSON 序列化");
            JsonWriteTests();

            Section("JSON 边界与错误");
            JsonEdgeTests();

            Section("协议信封");
            ProtocolTests();

            Section("蓝图卡片");
            BlueprintCardTests();

            Section("富文本输出");
            RichTextTests.Run();

            Section("作答选项文案");
            AskOptionTextTests.Run();

            Section("作答选项点击即跳规则");
            AskPickRuleTests.Run();

            Section("设定页（A.14）");
            SettingsProtocolTests.Run();

            Section("目标面板等级覆盖");
            GoalPanelSwapTests.Run();

            Section("AI 目标状态持久化");
            GoalStoreTests.Run();

            Section("顾问提示文案");
            AdvisorTextTests.Run();

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine($"✅ 全部通过（{_checks} 项断言）");
                return 0;
            }
            Console.WriteLine($"❌ {_failures} / {_checks} 项断言失败");
            return 1;
        }

        // ---------------------------------------------------------------- JSON 解析

        private static void JsonParseTests()
        {
            Eq("字符串", "hello", JsonValue.Parse("\"hello\"").AsString());
            Eq("整数", 42L, JsonValue.Parse("42").AsLong());
            Eq("负数", -17L, JsonValue.Parse("-17").AsLong());
            Eq("浮点", 3.5, JsonValue.Parse("3.5").AsDouble());
            Eq("指数", 1500.0, JsonValue.Parse("1.5e3").AsDouble());
            Eq("true", true, JsonValue.Parse("true").AsBool());
            Eq("false", false, JsonValue.Parse("false").AsBool());
            Check("null", JsonValue.Parse("null").IsNull);
            Check("null 的 Kind", JsonValue.Parse("null").Kind == JsonKind.Null);

            var obj = JsonValue.Parse("{\"a\":1,\"b\":\"x\",\"c\":[1,2,3],\"d\":{\"e\":true}}");
            Check("对象 Kind", obj.IsObject);
            Eq("对象成员数", 4, obj.Count);
            Eq("嵌套取整数", 1L, obj["a"].AsLong());
            Eq("嵌套取字符串", "x", obj["b"].AsString());
            Eq("数组长度", 3, obj["c"].Count);
            Eq("数组元素", 2L, obj["c"][1].AsLong());
            Eq("深两层", true, obj["d"]["e"].AsBool());

            // 缺失键必须返回 Null 而不是抛异常
            Check("缺失键返回 Null", obj["nope"].IsNull);
            Check("越界索引返回 Null", obj["c"][99].IsNull);
            Check("对非对象取键不抛异常", JsonValue.Parse("[1]")["x"].IsNull);

            // 空白容忍
            Eq("带缩进的 JSON", 1L, JsonValue.Parse("  {\n  \"a\" : 1\n }  ")["a"].AsLong());

            // 转义
            Eq("转义引号", "a\"b", JsonValue.Parse("\"a\\\"b\"").AsString());
            Eq("转义反斜杠", "a\\b", JsonValue.Parse("\"a\\\\b\"").AsString());
            Eq("转义换行", "a\nb", JsonValue.Parse("\"a\\nb\"").AsString());
            Eq("转义 tab", "a\tb", JsonValue.Parse("\"a\\tb\"").AsString());
            Eq("转义斜杠", "a/b", JsonValue.Parse("\"a\\/b\"").AsString());
            Eq("\\u ASCII", "A", JsonValue.Parse("\"\\u0041\"").AsString());

            // 中文（协议里必须原样可读）
            Eq("中文字面量", "铁块", JsonValue.Parse("\"铁块\"").AsString());
            Eq("中文 \\u 转义", "铁块", JsonValue.Parse("\"\\u94c1\\u5757\"").AsString());

            // 代理对（emoji）：两个 \u 转义应拼成一个字符
            var emoji = JsonValue.Parse("\"\\ud83d\\ude00\"").AsString();
            Eq("代理对长度", 2, emoji.Length);          // UTF-16 下是代理对
            Eq("代理对内容", "😀", emoji);

            // 重复键：后者胜出，且不产生重复项
            var dup = JsonValue.Parse("{\"a\":1,\"a\":2}");
            Eq("重复键后者胜出", 2L, dup["a"].AsLong());
            Eq("重复键不重复计数", 1, dup.Count);

            // 空容器
            Eq("空对象", 0, JsonValue.Parse("{}").Count);
            Eq("空数组", 0, JsonValue.Parse("[]").Count);
            Eq("空字符串", "", JsonValue.Parse("\"\"").AsString());
        }

        // ---------------------------------------------------------------- 序列化

        private static void JsonWriteTests()
        {
            var o = JsonValue.NewObject();
            o.Set("s", "hi");
            o.Set("n", 42);
            o.Set("f", 1.5);
            o.Set("b", true);
            o.Set("z", JsonValue.Null);
            Eq("对象序列化", "{\"s\":\"hi\",\"n\":42,\"f\":1.5,\"b\":true,\"z\":null}", o.ToJson());

            var arr = JsonValue.NewArray();
            arr.Add(1).Add("两").Add(false);
            Eq("数组序列化", "[1,\"两\",false]", arr.ToJson());

            // 中文不转义（协议可读性 + 省带宽）
            Eq("中文不转义", "\"铁块\"", JsonValue.Of("铁块").ToJson());

            // 控制字符必须转义，否则产出非法 JSON
            Eq("控制字符转义", "\"a\\nb\"", JsonValue.Of("a\nb").ToJson());
            Eq("引号转义", "\"a\\\"b\"", JsonValue.Of("a\"b").ToJson());
            Eq("反斜杠转义", "\"a\\\\b\"", JsonValue.Of("a\\b").ToJson());
            Eq("U+0001 转义", "\"\\u0001\"", JsonValue.Of("\u0001").ToJson());

            // 整数值不应写成 42.0
            Eq("整数不带小数点", "1733234567890", JsonValue.Of(1733234567890L).ToJson());

            // NaN/Infinity 必须退化为 null 而不是产出非法 JSON
            Eq("NaN 退化为 null", "null", JsonValue.Of(double.NaN).ToJson());
            Eq("Infinity 退化为 null", "null", JsonValue.Of(double.PositiveInfinity).ToJson());

            // 插入顺序稳定
            var ordered = JsonValue.NewObject();
            ordered.Set("z", 1);
            ordered.Set("a", 2);
            ordered.Set("m", 3);
            Eq("保持插入顺序", "{\"z\":1,\"a\":2,\"m\":3}", ordered.ToJson());

            // 重复 Set 覆盖但保位
            ordered.Set("a", 9);
            Eq("覆盖不改位置", "{\"z\":1,\"a\":9,\"m\":3}", ordered.ToJson());

            // 往返一致
            var roundTrip = JsonValue.Parse(o.ToJson());
            Eq("往返：对象", o.ToJson(), roundTrip.ToJson());

            var tricky = JsonValue.NewObject();
            tricky.Set("cn", "戴森球计划 \n\t\"引号\" \\ 反斜杠 😀");
            Eq("往返：复杂字符串", tricky.ToJson(), JsonValue.Parse(tricky.ToJson()).ToJson());
        }

        // ---------------------------------------------------------------- 边界与错误

        private static void JsonEdgeTests()
        {
            ExpectFail("空输入", "");
            ExpectFail("只有空白", "   ");
            ExpectFail("未闭合对象", "{\"a\":1");
            ExpectFail("未闭合数组", "[1,2");
            ExpectFail("未闭合字符串", "\"abc");
            ExpectFail("单引号", "'abc'");
            ExpectFail("尾逗号", "[1,2,]");
            ExpectFail("裸字面量", "undefined");
            ExpectFail("缺冒号", "{\"a\" 1}");
            ExpectFail("多余内容", "{} {}");
            ExpectFail("非法转义", "\"a\\qb\"");
            ExpectFail("\\u 位数不足", "\"\\u12\"");
            ExpectFail("非法数字", "1.2.3");
            ExpectFail("无引号键", "{a:1}");

            // 深度限制
            var deep = new StringBuilder();
            for (int i = 0; i < 200; i++) deep.Append('[');
            for (int i = 0; i < 200; i++) deep.Append(']');
            ExpectFail("嵌套过深被拒绝", deep.ToString());

            // TryParse 不应抛异常
            Check("TryParse 失败返回 false", !JsonValue.TryParse("{oops", out _));
            var parseOk = JsonValue.TryParse("{oops", out var bad);
            Check("TryParse 失败时输出 Null", !parseOk && bad != null && bad.IsNull);

            // 类型不匹配时的兜底
            var v = JsonValue.Parse("{\"s\":\"x\"}");
            Eq("AsLong 对字符串兜底", 7L, v["s"].AsLong(7));
            Eq("AsString 对缺失键兜底", "d", v["missing"].AsString("d"));
            Eq("AsBool 对缺失兜底", true, v["missing"].AsBool(true));
            Eq("AsDouble 从数字字符串解析", 1.5, JsonValue.Parse("{\"n\":\"1.5\"}")["n"].AsDouble());
            Eq("AsDouble 对非法字符串兜底", -1.0, JsonValue.Parse("{\"n\":\"xx\"}")["n"].AsDouble(-1.0));
        }

        // ---------------------------------------------------------------- 协议

        private static void ProtocolTests()
        {
            Eq("协议版本", 1, Protocol.Version);

            var env = Protocol.Envelope(Protocol.Prompt, Protocol.BuildPrompt("你好", null), "m-1", null);
            Eq("信封 v", 1L, env["v"].AsLong());
            Eq("信封 type", "prompt", env["type"].AsString());
            Eq("信封 id", "m-1", env["id"].AsString());
            Eq("信封 payload.text", "你好", env["payload"]["text"].AsString());
            Check("无 replyTo 时不写该字段", !env.Has("replyTo"));
            Check("无 requiredTool 时不写该字段", !env["payload"].Has("requiredTool"));

            var forced = Protocol.BuildPrompt("/game_get_power 电够吗", null, "game_get_power");
            Eq("requiredTool 写入", "game_get_power", forced["requiredTool"].AsString());
            Eq("requiredTool 不改变原文", "/game_get_power 电够吗", forced["text"].AsString());

            // ---- M4m：调试直调 ----
            Eq("tool_debug 类型名", "tool_debug", Protocol.ToolDebug);
            Eq("tool_debug_result 类型名", "tool_debug_result", Protocol.ToolDebugResult);
            var debugPayload = Protocol.BuildToolDebug(
                "game_get_power", JsonValue.Parse(@"{""planetId"": 1}"));
            Eq("tool_debug name", "game_get_power", debugPayload["name"].AsString());
            Eq("tool_debug args.planetId", 1L, debugPayload["args"]["planetId"].AsLong());
            var huge = JsonValue.NewObject();
            huge.Set("blob", new string('x', Protocol.ToolDebugArgsMaxBytes + 1));
            var rejected = false;
            try { Protocol.BuildToolDebug("game_get_power", huge); }
            catch (ArgumentException) { rejected = true; }
            Check("tool_debug 超过 64KiB 被拒绝", rejected);

            var debugInfo = ToolDebugResultInfo.Parse(JsonValue.Parse(
                @"{
                    ""ok"": true,
                    ""tool"": ""game_get_power"",
                    ""source"": ""game"",
                    ""durationMs"": 42,
                    ""content"": ""model text"",
                    ""details"": { ""ok"": true, ""data"": { ""demandKw"": 12.5 } },
                    ""error"": null,
                    ""truncated"": false,
                    ""image"": { ""mimeType"": ""image/jpeg"", ""width"": 1280, ""height"": 720, ""bytes"": 3456 }
                }"));
            Check("tool_debug_result ok", debugInfo.Ok);
            Eq("tool_debug_result tool", "game_get_power", debugInfo.Tool);
            Eq("tool_debug_result source", "game", debugInfo.Source);
            Eq("tool_debug_result duration", 42L, debugInfo.DurationMs);
            Eq("tool_debug_result content", "model text", debugInfo.Content);
            Check("tool_debug_result details 可读", debugInfo.DetailsText().Contains("demandKw"));
            Eq("tool_debug_result 图片宽", 1280, debugInfo.ImageWidth);
            Eq("tool_debug_result 图片字节", 3456L, debugInfo.ImageBytes);
            Check("tool_debug_result 未截断", !debugInfo.Truncated);

            var debugFail = ToolDebugResultInfo.Parse(JsonValue.Parse(
                @"{
                    ""ok"": false,
                    ""tool"": ""run_script"",
                    ""error"": { ""code"": ""tool_failed"", ""message"": ""not allowed"" }
                }"));
            Check("tool_debug_result 失败", !debugFail.Ok);
            Eq("tool_debug_result 错误码", "tool_failed", debugFail.ErrorCode);
            Eq("tool_debug_result 缺字段安全默认", "null", ToolDebugResultInfo.Parse(null).DetailsText());

            var withReply = Protocol.Envelope(Protocol.Pong, Protocol.BuildPing(123), "s-1", "m-9");
            Eq("replyTo 存在", "m-9", withReply["replyTo"].AsString());
            Eq("ping t", 123L, withReply["payload"]["t"].AsLong());

            var hello = Protocol.BuildHello("tok", "0.1.0", "0.10.30.3100", "zh-CN",
                new[] { "ui.chat" }, null);
            Eq("hello token", "tok", hello["token"].AsString());
            Eq("hello modVersion", "0.1.0", hello["modVersion"].AsString());
            Eq("hello capabilities[0]", "ui.chat", hello["capabilities"][0].AsString());
            Check("saveKey 为 null 时显式写 null", hello["saveKey"].IsNull);

            var withSave = Protocol.BuildHello("t", "1", "2", "zh", null, "abc123");
            Eq("saveKey 写入", "abc123", withSave["saveKey"].AsString());
            Eq("capabilities 为 null 时是空数组", 0, withSave["capabilities"].Count);
            Check("没有扩展时不写 hello.extensions", !withSave.Has("extensions"));

            var withExtensions = Protocol.BuildHello("t", "1", "2", "zh",
                new[] { Protocol.CapabilityModBridge }, "abc123",
                new[]
                {
                    new ExtensionSummary
                    {
                        Id = "auxilaryfunction",
                        Guid = "Auxilaryfunction",
                        Name = "Auxilaryfunction",
                        Version = "3.1.1",
                        ConfigCount = 72,
                        Tools = new[]
                        {
                            new ExtensionToolSummary
                            {
                                Name = "mod_auxilaryfunction_open",
                                Label = "打开",
                                Description = "测试工具",
                                Risk = 1,
                                TimeoutMs = 5000,
                                Parameters = JsonValue.Parse(@"{""type"":""object"",""properties"":{}}"),
                            },
                        },
                    },
                });
            Eq("extensions 数量", 1, withExtensions["extensions"].Count);
            Eq("extensions[0].id", "auxilaryfunction", withExtensions["extensions"][0]["id"].AsString());
            Eq("extensions[0].guid", "Auxilaryfunction", withExtensions["extensions"][0]["guid"].AsString());
            Eq("extensions[0].configCount", 72L, withExtensions["extensions"][0]["configCount"].AsLong());
            Eq("extensions[0].tools 数量", 1, withExtensions["extensions"][0]["tools"].Count);
            Eq("extensions[0].tools[0].name", "mod_auxilaryfunction_open",
                withExtensions["extensions"][0]["tools"][0]["name"].AsString());
            Eq("extensions[0].tools[0].risk", 1L, withExtensions["extensions"][0]["tools"][0]["risk"].AsLong());
            Eq("extensions[0].tools[0].parameters.type", "object",
                withExtensions["extensions"][0]["tools"][0]["parameters"]["type"].AsString());
            Eq("mod.bridge.v1 能力串", "mod.bridge.v1", Protocol.CapabilityModBridge);
            Eq("mods_list 工具名", "mods_list", Protocol.ToolModsList);
            Eq("mods_config_set 工具名", "mods_config_set", Protocol.ToolModsConfigSet);
            Check("mods_config_set 记为写工具", Protocol.IsWriteTool(Protocol.ToolModsConfigSet));
            Check("mods_adapter_create 记为写工具", Protocol.IsWriteTool(Protocol.ToolModsAdapterCreate));
            Check("mods_adapter_delete 记为写工具", Protocol.IsWriteTool(Protocol.ToolModsAdapterDelete));

            var clientState = Protocol.BuildClientState(
                new[] { Protocol.CapabilityModBridge },
                new[]
                {
                    new ExtensionSummary { Id = "aux", Guid = "Auxilaryfunction", ConfigCount = 3 },
                });
            Eq("extensions 推送 capabilities[0]", Protocol.CapabilityModBridge,
                clientState["capabilities"][0].AsString());
            Eq("extensions 推送 extensions 数量", 1, clientState["extensions"].Count);
            Eq("extensions 推送工具名", "extensions", Protocol.Extensions);

            var intSchema = ParameterSchema.Normalize(JsonValue.Parse(
                "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"int\"}}}"), out var intError);
            Check("schema 把 int 规范成 integer",
                intSchema is not null && intSchema["properties"]["count"]["type"].AsString() == "integer");
            Check("schema 规范化不报错", intError is null);

            var anySchema = ParameterSchema.Normalize(JsonValue.Parse(
                "{\"type\":\"object\",\"properties\":{\"a\":{\"anyOf\":[{\"type\":\"bool\"}]}}}"), out _);
            Check("schema 递归规范化 anyOf",
                anySchema is not null && anySchema["properties"]["a"]["anyOf"][0]["type"].AsString() == "boolean");

            var badRoot = ParameterSchema.Normalize(JsonValue.Parse("{\"type\":\"int\"}"), out var badRootError);
            Check("schema 拒绝非 object 根", badRoot is null && !string.IsNullOrEmpty(badRootError));

            var nullSchema = ParameterSchema.Normalize(null, out _);
            Check("schema 缺省为空对象", nullSchema is not null && nullSchema["type"].AsString() == "object");

            var dropped = ParameterSchema.Normalize(JsonValue.Parse("{\"type\":\"object\",\"unknownKeyword\":1}"), out _);
            Check("schema 丢弃未知关键字", dropped is not null && !dropped.Has("unknownKeyword"));

            var clientStateEmpty = Protocol.BuildClientState(new string[0], null);
            Check("空推送也显式写 extensions 数组", clientStateEmpty["extensions"].IsArray);
            Eq("空推送 extensions 长度为 0", 0, clientStateEmpty["extensions"].Count);

            var confirmWithMod = ConfirmRequestInfo.Parse(JsonValue.Parse(
                "{\"requestId\":\"r\",\"callId\":\"c\",\"tool\":\"mod_aux_open\",\"token\":\"t\"," +
                "\"modGuid\":\"Auxilaryfunction\"}"));
            Check("confirm 解析 modGuid", confirmWithMod is not null && confirmWithMod.ModGuid == "Auxilaryfunction");

            // 顺序敏感：parse 后按 type 分派是客户端主路径
            var wire = env.ToJson();
            var parsed = JsonValue.Parse(wire);
            Eq("线格式往返 type", "prompt", parsed["type"].AsString());
            Eq("线格式往返 text", "你好", parsed["payload"]["text"].AsString());

            // 未知字段必须被容忍（前向兼容）
            var future = JsonValue.Parse("{\"v\":1,\"id\":\"x\",\"type\":\"new_thing\",\"payload\":{\"k\":1},\"extra\":true}");
            Eq("未知 type 可读取", "new_thing", future["type"].AsString());
            Check("未知顶层字段不报错", future["extra"].AsBool());

            // ---- M1 增量：配置协议（docs/protocol-v1.md 附录 A）----
            Eq("config.set 类型名", "config.set", Protocol.ConfigSet);
            Eq("config.state 类型名", "config.state", Protocol.ConfigState);
            Eq("error code invalid_key", "invalid_key", Protocol.CodeInvalidKey);
            Eq("error code config_required", "config_required", Protocol.CodeConfigRequired);

            var cfgSet = Protocol.BuildConfigSet("deepseek", "deepseek-v4-flash", "sk-secret", null);
            Eq("config.set provider", "deepseek", cfgSet["provider"].AsString());
            Eq("config.set model", "deepseek-v4-flash", cfgSet["model"].AsString());
            Eq("config.set apiKey", "sk-secret", cfgSet["apiKey"].AsString());
            Check("config.set 未给出的 baseUrl 不写入", !cfgSet.Has("baseUrl"));
            var cfgClearBase = Protocol.BuildConfigSet(null, null, null, null,
                permissionLevel: null, clearBaseUrl: true);
            Check("config.set clearBaseUrl 写 null", cfgClearBase["baseUrl"].IsNull);

            var keyOnly = Protocol.BuildConfigSet(null, null, "sk-x", null);
            Eq("只传 key 时字段数", 1, keyOnly.Count);
            Eq("只传 key 时 key 存在", "sk-x", keyOnly["apiKey"].AsString());

            var cfgThink = Protocol.BuildConfigSet(null, null, null, null, "high");
            Eq("config.set thinkingLevel", "high", cfgThink["thinkingLevel"].AsString());
            var cfgNoThink = Protocol.BuildConfigSet(null, null, null, null, null);
            Check("config.set 未给出 thinkingLevel 不写入", !cfgNoThink.Has("thinkingLevel"));

            // ready.payload.config（附录 A.2）
            var ready = JsonValue.Parse(
                "{\"protocol\":1,\"sessionId\":\"sess_1\"," +
                "\"agent\":{\"provider\":\"deepseek\",\"model\":\"deepseek-v4-flash\",\"available\":true}," +
                "\"config\":{\"state\":\"missing_key\",\"provider\":\"deepseek\",\"model\":\"deepseek-v4-flash\"}}");
            Check("ready.agent 必须是对象", ready["agent"].IsObject);
            Eq("ready.agent.model", "deepseek-v4-flash", ready["agent"]["model"].AsString());
            var rc = ConfigState.Parse(ready["config"]);
            Eq("ready.config.state", "missing_key", rc.State);
            Eq("ready.config.provider", "deepseek", rc.Provider);
            Eq("ready.config.model", "deepseek-v4-flash", rc.Model);
            Check("missing_key 需要录入", rc.NeedsKey);
            Check("missing_key 不是就绪", !rc.IsReady);

            // config.state（附录 A.1）
            var cs = ConfigState.Parse(JsonValue.Parse("{\"state\":\"ready\",\"model\":\"deepseek-v4-pro\"}"));
            Check("ready 视为就绪", cs.IsReady);
            Check("ready 不需要录入", !cs.NeedsKey);
            Eq("config.state.model", "deepseek-v4-pro", cs.Model);
            Check("缺 thinkingLevel 时为 null", cs.ThinkingLevel == null);

            var csActive = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"active\":{\"provider\":\"openai\",\"model\":\"gpt-4o\"}}"));
            Eq("active 回填 provider", "openai", csActive.Provider);
            Eq("active 回填 model", "gpt-4o", csActive.Model);
            Eq("active.provider 保留", "openai", csActive.ActiveProvider);
            Eq("active.model 保留", "gpt-4o", csActive.ActiveModel);

            var csThink = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"model\":\"deepseek-v4-pro\",\"thinkingLevel\":\"max\"}"));
            Eq("config.state.thinkingLevel", "max", csThink.ThinkingLevel);

            // 权限档位（readonly / limited / takeover）
            Check("思考低档存在", Protocol.ThinkingLow == "low");
            Check("只读权限合法", Protocol.IsValidPermissionLevel(Protocol.PermissionReadonly));
            Check("受限权限合法", Protocol.IsValidPermissionLevel(Protocol.PermissionLimited));
            Check("接管权限合法", Protocol.IsValidPermissionLevel(Protocol.PermissionTakeover));
            Check("未知权限非法", !Protocol.IsValidPermissionLevel("allow_all"));
            Check("只读：risk1 也要确认", !Protocol.AutoApprovesWrite(Protocol.PermissionReadonly, 1));
            Check("只读：risk2 要确认", !Protocol.AutoApprovesWrite(Protocol.PermissionReadonly, 2));
            Check("受限：risk1 直接放行", Protocol.AutoApprovesWrite(Protocol.PermissionLimited, 1));
            Check("受限：risk2 仍要确认", !Protocol.AutoApprovesWrite(Protocol.PermissionLimited, 2));
            Check("接管：risk1 直接放行", Protocol.AutoApprovesWrite(Protocol.PermissionTakeover, 1));
            Check("接管：risk2 直接放行", Protocol.AutoApprovesWrite(Protocol.PermissionTakeover, 2));
            Check("未知权限：保守地要求确认", !Protocol.AutoApprovesWrite("allow_all", 1));
            Check("空权限：保守地要求确认", !Protocol.AutoApprovesWrite(null, 2));

            var cfgPerm = Protocol.BuildConfigSet(null, null, null, null, null, null, null, null, null,
                Protocol.PermissionTakeover);
            Eq("config.set permissionLevel", "takeover", cfgPerm["permissionLevel"].AsString());
            var cfgNoPerm = Protocol.BuildConfigSet(null, null, null, null);
            Check("config.set 未给出 permissionLevel 不写入", !cfgNoPerm.Has("permissionLevel"));

            Check("缺 permissionLevel 时为 null", cs.PermissionLevel == null);
            var csPerm = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"model\":\"deepseek-v4-pro\",\"permissionLevel\":\"readonly\"}"));
            Eq("config.state.permissionLevel", "readonly", csPerm.PermissionLevel);

            var bad = ConfigState.Parse(JsonValue.Parse("{\"state\":\"invalid_key\"}"));
            Check("invalid_key 需要重新录入", bad.NeedsKey);

            var unreachable = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"unreachable\",\"provider\":\"deepseek\"}"));
            Check("unreachable 的 fallback hasKey=true", unreachable.Providers[0].HasKey);
            var missing = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"missing_key\",\"provider\":\"deepseek\"}"));
            Check("missing_key 的 fallback hasKey=false", !missing.Providers[0].HasKey);

            // 缺字段 / null 都必须有安全默认值，不能抛
            var dflt = ConfigState.Parse(null);
            Eq("null 载荷的默认 state", "missing_key", dflt.State);
            var empty = ConfigState.Parse(JsonValue.NewObject());
            Eq("空对象的默认 state", "missing_key", empty.State);
            Check("ConfigState.ToString 可读", dflt.ToString().Contains("missing_key"));

            // 向后兼容：老服务端的 ready 不带 config 字段时不能误判为"待配置"
            var legacy = ConfigState.AssumeReady();
            Check("缺 config 字段时假定为就绪", legacy.IsReady);
            Check("缺 config 字段时不要求录入", !legacy.NeedsKey);

            // ---- M3：多供应商 / 多模型 ----
            var providerPatch = JsonValue.NewObject();
            var openai = JsonValue.NewObject();
            openai.Set("apiKey", "sk-openai");
            openai.Set("baseUrl", "https://api.openai.com/v1");
            var openaiModels = JsonValue.NewArray();
            var gpt = JsonValue.NewObject();
            gpt.Set("id", "gpt-4o");
            gpt.Set("name", "GPT-4o");
            openaiModels.Add(gpt);
            openai.Set("models", openaiModels);
            providerPatch.Set("openai", openai);

            var multiSet = Protocol.BuildConfigSet(null, null, null, null, null,
                providerPatch, null, "openai", "gpt-4o");
            Eq("config.set providerConfig.apiKey",
                "sk-openai", multiSet["providerConfig"]["openai"]["apiKey"].AsString());
            Eq("config.set activeProvider", "openai", multiSet["activeProvider"].AsString());
            Eq("config.set activeModel", "gpt-4o", multiSet["activeModel"].AsString());
            Eq("config.set models[0].name",
                "GPT-4o", multiSet["providerConfig"]["openai"]["models"][0]["name"].AsString());

            var stateMulti = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"provider\":\"openai\",\"model\":\"gpt-4o\"," +
                "\"providers\":[" +
                "{\"id\":\"deepseek\",\"name\":\"DeepSeek\",\"hasKey\":true,\"models\":" +
                "[{\"id\":\"deepseek-v4-flash\",\"name\":\"DeepSeek V4 Flash\"}]}," +
                "{\"id\":\"openai\",\"name\":\"OpenAI\",\"hasKey\":true,\"baseUrl\":\"https://api.openai.com/v1\"," +
                "\"models\":[{\"id\":\"gpt-4o\",\"name\":\"GPT-4o\"}],\"isActive\":true}" +
                "]}"));
            Eq("config.state providers 数量", 2, stateMulti.Providers.Count);
            Eq("config.state providers[0].name", "DeepSeek", stateMulti.Providers[0].Name);
            Check("config.state providers[0].hasKey", stateMulti.Providers[0].HasKey);
            Eq("config.state providers[1].models[0].id", "gpt-4o", stateMulti.Providers[1].Models[0].Id);
            Eq("config.state providers[1].baseUrl",
                "https://api.openai.com/v1", stateMulti.Providers[1].BaseUrl);

            var legacyState = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"provider\":\"deepseek\",\"model\":\"deepseek-v4-flash\"}"));
            Eq("老 config.state 合成一条 provider", 1, legacyState.Providers.Count);
            Eq("老 config.state 合成一个 model", 1, legacyState.Providers[0].Models.Count);
            Check("老 config.state provider 标记 active", legacyState.Providers[0].IsActive);

            // ---- M3：模型目录 ----
            var catalogReq = Protocol.BuildModelCatalogRequest(
                "openai", "https://api.openai.com/v1", "sk-catalog");
            Eq("model.catalog provider", "openai", catalogReq["provider"].AsString());
            Eq("model.catalog baseUrl", "https://api.openai.com/v1", catalogReq["baseUrl"].AsString());
            Eq("model.catalog apiKey", "sk-catalog", catalogReq["apiKey"].AsString());

            var catalog = ModelCatalogResult.Parse(JsonValue.Parse(
                "{\"provider\":\"openai\",\"source\":\"catalog\",\"models\":[" +
                "{\"id\":\"gpt-4o\",\"name\":\"GPT-4o\"}," +
                "{\"id\":\"gpt-4o-mini\"}],\"error\":null}"));
            Eq("model.catalog provider 回显", "openai", catalog.Provider);
            Eq("model.catalog 模型数", 2, catalog.Models.Count);
            Eq("model.catalog models[0].name", "GPT-4o", catalog.Models[0].Name);
            Eq("model.catalog models[1].name 缺省用 id", "gpt-4o-mini", catalog.Models[1].Name);
            Check("model.catalog error 为空", string.IsNullOrEmpty(catalog.Error));

            var catalogEmpty = ModelCatalogResult.Parse(JsonValue.Parse(
                "{\"provider\":\"my-proxy\",\"models\":[],\"error\":\"端点没有返回模型列表\"}"));
            Eq("model.catalog 空列表", 0, catalogEmpty.Models.Count);
            Eq("model.catalog 错误信息", "端点没有返回模型列表", catalogEmpty.Error);

            // ---- 预设供应商目录 ----
            var providerCatalogReq = Protocol.BuildProviderCatalogRequest();
            Check("provider.catalog 请求是空对象",
                providerCatalogReq.IsObject && providerCatalogReq.Count == 0);

            var providerCatalog = ProviderCatalogResult.Parse(JsonValue.Parse(
                "{\"source\":\"catalog\",\"providers\":[" +
                "{\"id\":\"amazon-bedrock\",\"name\":\"Amazon Bedrock\"}," +
                "{\"id\":\"deepseek\"}]}"));
            Eq("provider.catalog 供应商数", 2, providerCatalog.Providers.Count);
            Eq("provider.catalog 保持目录顺序", "amazon-bedrock", providerCatalog.Providers[0].Id);
            Eq("provider.catalog providers[0].name", "Amazon Bedrock", providerCatalog.Providers[0].Name);
            Eq("provider.catalog providers[1].name 缺省用 id", "deepseek", providerCatalog.Providers[1].Name);
            Check("provider.catalog error 为空", string.IsNullOrEmpty(providerCatalog.Error));

            var providerCatalogEmpty = ProviderCatalogResult.Parse(JsonValue.Parse(
                "{\"providers\":[],\"source\":\"none\",\"error\":\"当前 agent 不支持供应商目录\"}"));
            Eq("provider.catalog 空列表", 0, providerCatalogEmpty.Providers.Count);
            Eq("provider.catalog 错误信息", "当前 agent 不支持供应商目录", providerCatalogEmpty.Error);

            var testReq = Protocol.BuildProviderTestRequest(
                "openai", "https://api.openai.com/v1", "sk-test");
            Eq("provider.test provider", "openai", testReq["provider"].AsString());
            Eq("provider.test baseUrl", "https://api.openai.com/v1", testReq["baseUrl"].AsString());
            Eq("provider.test apiKey", "sk-test", testReq["apiKey"].AsString());

            var testOk = ProviderTestResult.Parse(JsonValue.Parse(
                "{\"provider\":\"openai\",\"ok\":true,\"status\":200," +
                "\"latencyMs\":123,\"message\":\"Key 可用\",\"source\":\"http\"}"));
            Check("provider.test ok", testOk.Ok);
            Eq("provider.test status", 200, testOk.Status);
            Eq("provider.test latency", 123, testOk.LatencyMs);
            Eq("provider.test message", "Key 可用", testOk.Message);
            Check("provider.test 未返回 resolvedBaseUrl 时为 null", testOk.ResolvedBaseUrl == null);

            var testResolved = ProviderTestResult.Parse(JsonValue.Parse(
                "{\"provider\":\"openai\",\"ok\":true,\"status\":200,\"latencyMs\":10," +
                "\"message\":\"Key 可用\",\"source\":\"http\"," +
                "\"resolvedBaseUrl\":\"https://api.example.com/v1\"}"));
            Eq("provider.test resolvedBaseUrl", "https://api.example.com/v1", testResolved.ResolvedBaseUrl);

            var testBad = ProviderTestResult.Parse(JsonValue.Parse(
                "{\"provider\":\"openai\",\"ok\":false,\"status\":401," +
                "\"latencyMs\":80,\"message\":\"API Key 被拒绝（HTTP 401）\",\"source\":\"http\"}"));
            Check("provider.test 失败", !testBad.Ok);
            Eq("provider.test 失败状态", 401, testBad.Status);
            Eq("provider.test 失败信息", "API Key 被拒绝（HTTP 401）", testBad.Message);

            // ---- 附录 A.6：存档 ↔ 对话串绑定 ----
            Eq("save.bind 类型名", "save.bind", Protocol.SaveBind);
            Eq("save.bound 类型名", "save.bound", Protocol.SaveBound);
            Eq("error code save_required", "save_required", Protocol.CodeSaveRequired);

            var bind = Protocol.BuildSaveBind("3f2a91c4d8e05b17", "我的档");
            Eq("save.bind saveKey", "3f2a91c4d8e05b17", bind["saveKey"].AsString());
            Eq("save.bind saveName", "我的档", bind["saveName"].AsString());
            Check("save.bind 默认不带 blueprintTiers", !bind.Has("blueprintTiers"));

            var tiers = JsonValue.NewObject();
            tiers.Set("assembler", 2);
            tiers.Set("belt", 1);
            tiers.Set("inserter", 3);
            tiers.Set("piler", true);
            var bindTiers = Protocol.BuildSaveBind("save-a", "档 A", tiers);
            Eq("save.bind tiers assembler", 2L, bindTiers["blueprintTiers"]["assembler"].AsLong());
            Eq("save.bind tiers belt", 1L, bindTiers["blueprintTiers"]["belt"].AsLong());
            Check("save.bind tiers piler", bindTiers["blueprintTiers"]["piler"].AsBool());
            Check("save.bind tiers 是可选字段", !Protocol.BuildSaveBind("save-a", "档 A").Has("blueprintTiers"));

            // 解绑：字段必须**存在且为 null**，sidecar 靠这个区分"解绑"与"没提"
            var unbind = Protocol.BuildSaveBind(null, null);
            Check("解绑时 saveKey 字段存在", unbind.Has("saveKey"));
            Check("解绑时 saveKey 为 null", unbind["saveKey"].IsNull);
            Check("解绑时不写 saveName", !unbind.Has("saveName"));
            Eq("解绑载荷字段数", 1, unbind.Count);

            // ---- 沙盒门禁（M5+ 基础设施）----
            Eq("error code sandbox_required", "sandbox_required", Protocol.CodeSandboxRequired);
            var bindSandbox = Protocol.BuildSaveBind("save-s", "沙盒档", null, true);
            Check("save.bind 写 sandbox=true", bindSandbox.Has("sandbox") && bindSandbox["sandbox"].AsBool());
            var bindNoSandbox = Protocol.BuildSaveBind("save-s", "普通档", null, false);
            Check("save.bind 写 sandbox=false", bindNoSandbox.Has("sandbox") && !bindNoSandbox["sandbox"].AsBool());
            Check("save.bind 未给 sandbox 时省略", !bind.Has("sandbox"));
            Check("game_spawn_item 是沙盒专属", Protocol.IsSandboxOnlyTool("game_spawn_item"));
            Check("game_teleport 是沙盒专属", Protocol.IsSandboxOnlyTool("game_teleport"));
            Check("game_get_overview 不是沙盒专属", !Protocol.IsSandboxOnlyTool("game_get_overview"));
            Check("沙盒关闭拒绝 spawn", !Protocol.SandboxAllows("game_spawn_item", false));
            Check("沙盒开启允许 spawn", Protocol.SandboxAllows("game_spawn_item", true));
            Check("沙盒关闭仍允许普通读工具", Protocol.SandboxAllows("game_get_overview", false));

            // ---- D1：丢弃工具（独立能力串）----
            Eq("game.drop.v1 能力串", "game.drop.v1", Protocol.CapabilityGameDrop);
            Eq("game_drop_item 工具名", "game_drop_item", Protocol.ToolDropItem);
            Check("game_drop_item 不是沙盒专属", !Protocol.IsSandboxOnlyTool(Protocol.ToolDropItem));
            Check("非沙盒仍允许 game_drop_item", Protocol.SandboxAllows(Protocol.ToolDropItem, false));

            // ---- 生产建筑写工具：独立 game.build.v1 ----
            Eq("game.build.v1 能力串", "game.build.v1", Protocol.CapabilityGameBuild);
            Eq("game_set_recipe 工具名", "game_set_recipe", Protocol.ToolSetRecipe);
            Eq("game_set_assembler_batch 工具名", "game_set_assembler_batch", Protocol.ToolSetAssemblerBatch);
            Eq("script.write.v1 能力串", "script.write.v1", Protocol.CapabilityScriptWrite);
            Eq("game.storage.v1 能力串", "game.storage.v1", Protocol.CapabilityGameStorage);
            Eq("game_storage_set_filter 工具名", "game_storage_set_filter", Protocol.ToolStorageSetFilter);
            Eq("game_storage_set_limit 工具名", "game_storage_set_limit", Protocol.ToolStorageSetLimit);
            Eq("game_storage_move 工具名", "game_storage_move", Protocol.ToolStorageMove);
            Eq("game_storage_deposit 工具名", "game_storage_deposit", Protocol.ToolStorageDeposit);
            Eq("game_storage_withdraw 工具名", "game_storage_withdraw", Protocol.ToolStorageWithdraw);
            Eq("game_feed_input 工具名", "game_feed_input", Protocol.ToolFeedInput);
            Eq("game.tether.v1 能力串", "game.tether.v1", Protocol.CapabilityGameTether);
            Eq("game_tether_add 工具名", "game_tether_add", Protocol.ToolTetherAdd);
            Eq("game_tether_clear 工具名", "game_tether_clear", Protocol.ToolTetherClear);
            Eq("game_tether_list 工具名", "game_tether_list", Protocol.ToolTetherList);
            Eq("game_take_output 工具名", "game_take_output", Protocol.ToolTakeOutput);
            Eq("game_place_marker 工具名", "game_place_marker", Protocol.ToolPlaceMarker);
            Check("写工具表含撤销", Protocol.IsWriteTool(Protocol.ToolUndoLastWrite));
            Check("写工具表含标记", Protocol.IsWriteTool(Protocol.ToolPlaceMarker));
            Check("写工具表不含只读工具", !Protocol.IsWriteTool("game_get_entity"));

            // ---- 受控写脚本：一次确认的脚本租约 ----
            Eq("confirm_release 类型名", "confirm_release", Protocol.ConfirmRelease);
            var scriptConfirm = ConfirmRequestInfo.Parse(JsonValue.Parse(
                @"{
                    ""requestId"": ""r1"", ""callId"": ""script"", ""tool"": ""run_script"",
                    ""argsHash"": ""abc"", ""token"": ""tok"", ""title"": ""脚本写操作"",
                    ""body"": ""最多 3 次"", ""risk"": 2, ""timeoutMs"": 60000, ""maxWrites"": 3,
                    ""writeNames"": [""game_set_recipe"", ""game_set_assembler_batch""]
                }"));
            Check("脚本确认可解析", scriptConfirm != null);
            Eq("脚本确认 maxWrites", 3, scriptConfirm.MaxWrites);
            Eq("脚本确认 tool", "run_script", scriptConfirm.Tool);
            Check("脚本确认写工具名", scriptConfirm.WriteNames.Contains("game_set_recipe"));
            Check("脚本确认写工具数", scriptConfirm.WriteNames.Count == 2);

            var leases = new ConfirmationService();
            Check("脚本确认可注册", leases.TryRegister(scriptConfirm, out _));
            Check("脚本确认可同意", leases.TryResolve("r1", true, out _));
            var scriptWriteCall = ToolCallInfo.Parse(JsonValue.Parse(
                @"{
                    ""callId"": ""call_s1"", ""name"": ""game_set_recipe"",
                    ""args"": { ""buildingId"": 1, ""recipeId"": 2 },
                    ""confirmToken"": ""tok"", ""confirmArgsHash"": ""abc""
                }"), "call_s1");
            Check("脚本租约允许第一次写", leases.TryConsume(scriptWriteCall, out _));
            var otherCall = ToolCallInfo.Parse(JsonValue.Parse(
                @"{
                    ""callId"": ""call_s2"", ""name"": ""game_set_research"",
                    ""args"": { ""techId"": 1 },
                    ""confirmToken"": ""tok"", ""confirmArgsHash"": ""abc""
                }"), "call_s2");
            Check("脚本租约拒绝未列出的写工具",
                !leases.TryConsume(otherCall, out var leaseErr4) &&
                leaseErr4 == Protocol.CodeConfirmationInvalid);
            Check("脚本租约允许第二次写", leases.TryConsume(scriptWriteCall, out _));
            Check("脚本租约允许第三次写", leases.TryConsume(scriptWriteCall, out _));
            Check("脚本租约次数用尽",
                !leases.TryConsume(scriptWriteCall, out var leaseErr3) &&
                leaseErr3 == Protocol.CodeConfirmationExpired);

            var badNames = ConfirmRequestInfo.Parse(JsonValue.Parse(
                @"{
                    ""requestId"": ""r2"", ""callId"": ""script"", ""tool"": ""run_script"",
                    ""argsHash"": ""abc"", ""token"": ""tok2"", ""title"": ""x"",
                    ""body"": ""x"", ""risk"": 2, ""timeoutMs"": 60000, ""maxWrites"": 3,
                    ""writeNames"": []
                }"));
            var badNamesSvc = new ConfirmationService();
            Check("空 writeNames 可注册", badNamesSvc.TryRegister(badNames, out _));
            Check("空 writeNames 租约被拒绝", !badNamesSvc.TryResolve("r2", true, out _));

            var wrongTool = ConfirmRequestInfo.Parse(JsonValue.Parse(
                @"{
                    ""requestId"": ""r3"", ""callId"": ""call"", ""tool"": ""game_open_panel"",
                    ""argsHash"": ""abc"", ""token"": ""tok3"", ""title"": ""x"",
                    ""body"": ""x"", ""risk"": 2, ""timeoutMs"": 60000, ""maxWrites"": 3,
                    ""writeNames"": [""game_set_recipe""]
                }"));
            var wrongToolSvc = new ConfirmationService();
            Check("非 run_script 可注册", wrongToolSvc.TryRegister(wrongTool, out _));
            Check("非 run_script 租约被拒绝", !wrongToolSvc.TryResolve("r3", true, out _));

            // ---- link_card 载荷 ----
            var linkPlanet = LinkCardInfo.Parse(JsonValue.Parse(
                "{\"kind\":\"planet\",\"planetId\":103,\"name\":\"文昌六\"}"));
            Check("link_card 星球卡", linkPlanet != null && linkPlanet.Kind == "planet");
            var linkBuilding = LinkCardInfo.Parse(JsonValue.Parse(
                "{\"kind\":\"building\",\"planetId\":103,\"buildingId\":6956,\"name\":\"制造台\"}"));
            Check("link_card 建筑卡", linkBuilding != null && linkBuilding.BuildingId == 6956);
            var linkVein = LinkCardInfo.Parse(JsonValue.Parse(
                "{\"kind\":\"vein\",\"planetId\":103,\"veinId\":415,\"name\":\"铁矿脉\"}"));
            Check("link_card 矿脉卡", linkVein != null && linkVein.VeinId == 415);
            Check("link_card 缺 name 拒绝", LinkCardInfo.Parse(JsonValue.Parse(
                "{\"kind\":\"planet\",\"planetId\":103}")) == null);
            Check("link_card 建筑缺 id 拒绝", LinkCardInfo.Parse(JsonValue.Parse(
                "{\"kind\":\"building\",\"planetId\":103,\"name\":\"x\"}")) == null);

            // 名字可改，但键必须稳定：键里不允许出现存档名
            Check("saveKey 不含存档名", !bind["saveKey"].AsString().Contains("我的档"));

            var bound = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"saveName\":\"我的档\",\"existed\":true,\"messageCount\":12}"));
            Eq("save.bound saveKey", "3f2a", bound.SaveKey);
            Eq("save.bound existed", true, bound.Existed);
            Eq("save.bound messageCount", 12, bound.MessageCount);
            Check("save.bound ToString 可读", bound.ToString().Contains("我的档"));

            var fresh = SaveBoundInfo.Parse(JsonValue.Parse("{\"saveKey\":\"aa\",\"existed\":false}"));
            Check("新对话 existed=false", !fresh.Existed);
            Eq("缺 messageCount 时为 0", 0, fresh.MessageCount);
            Eq("缺 history 时为空", 0, fresh.History.Count);

            var withHistory = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"existed\":true,\"messageCount\":2,\"history\":[" +
                "{\"role\":\"user\",\"text\":\"上次的问题\",\"timestamp\":123}," +
                "{\"role\":\"assistant\",\"text\":\"上次的回答\",\"gameTime\":\"08:42\"}]}"));
            Eq("save.bound history 条数", 2, withHistory.History.Count);
            Eq("history 第一条 role", "user", withHistory.History[0].Role);
            Eq("history 第一条 text", "上次的问题", withHistory.History[0].Text);
            Eq("history 第一条 timestamp", 123L, withHistory.History[0].Timestamp);
            Eq("history 第二条 role", "assistant", withHistory.History[1].Role);
            Eq("history 第二条 gameTime", "08:42", withHistory.History[1].GameTime);

            // save.bound.history 的可选 steps（附录 A.6，M4k+）
            var withSteps = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"history\":[" +
                "{\"role\":\"user\",\"text\":\"查产量\"}," +
                "{\"role\":\"assistant\",\"text\":\"30/min\",\"steps\":[" +
                "{\"kind\":\"thinking\",\"text\":\"先看整体\"}," +
                "{\"kind\":\"tool\",\"name\":\"game_get_production\",\"args\":\"{}\",\"ok\":true,\"summary\":\"30/min\",\"detail\":\"细节\"}," +
                "{\"kind\":\"tool\",\"name\":\"run_script\",\"ok\":false}," +
                "{\"kind\":\"tool\",\"name\":\"game_get_entity\"}," +
                "{\"kind\":\"weird\"}]}]}"));
            Eq("回答带 4 个步骤（未知 kind 忽略）", 4, withSteps.History[1].Steps.Count);
            Eq("步骤一 kind", "thinking", withSteps.History[1].Steps[0].Kind);
            Eq("步骤一 text", "先看整体", withSteps.History[1].Steps[0].Text);
            Eq("步骤二工具名", "game_get_production", withSteps.History[1].Steps[1].Name);
            Eq("步骤二 ok", true, withSteps.History[1].Steps[1].Ok);
            Eq("步骤二 summary", "30/min", withSteps.History[1].Steps[1].Summary);
            Eq("步骤三失败", false, withSteps.History[1].Steps[2].Ok);
            Check("步骤四没有结果记录", withSteps.History[1].Steps[3].Ok == null);
            Eq("user 消息没有步骤", 0, withSteps.History[0].Steps.Count);

            // 过程正文（kind:"text"）是排在思考/工具之间的独立步骤，不是回答本身。
            var withTextStep = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2b\",\"history\":[" +
                "{\"role\":\"user\",\"text\":\"看看电网\"}," +
                "{\"role\":\"assistant\",\"text\":\"电网正常。\",\"steps\":[" +
                "{\"kind\":\"thinking\",\"text\":\"先看整体\"}," +
                "{\"kind\":\"text\",\"text\":\"我先看一下电网\"}," +
                "{\"kind\":\"tool\",\"name\":\"game_get_overview\",\"ok\":true}]}]}"));
            Eq("过程正文也能回放", 3, withTextStep.History[1].Steps.Count);
            Eq("过程正文 kind", "text", withTextStep.History[1].Steps[1].Kind);
            Eq("过程正文 text", "我先看一下电网", withTextStep.History[1].Steps[1].Text);
            Eq("过程正文之后的工具仍在", "game_get_overview", withTextStep.History[1].Steps[2].Name);

            var noBound = SaveBoundInfo.Parse(null);
            Eq("null 载荷的安全默认", 0, noBound.MessageCount);
            Check("null 载荷的 SaveKey 为空", string.IsNullOrEmpty(noBound.SaveKey));

            // ---- 附录 A.13：一个存档多个会话 ----
            Eq("conversation.create 类型名", "conversation.create", Protocol.ConversationCreate);
            Eq("conversation.created 类型名", "conversation.created", Protocol.ConversationCreated);
            Eq("conversation.switch 类型名", "conversation.switch", Protocol.ConversationSwitch);
            Eq("conversation.switched 类型名", "conversation.switched", Protocol.ConversationSwitched);
            Eq("conversation.delete 类型名", "conversation.delete", Protocol.ConversationDelete);
            Eq("conversation.deleted 类型名", "conversation.deleted", Protocol.ConversationDeleted);
            Eq("conversation.updated 类型名", "conversation.updated", Protocol.ConversationUpdated);
            Eq("conversation.rename 类型名", "conversation.rename", Protocol.ConversationRename);
            Eq("conversation.renamed 类型名", "conversation.renamed", Protocol.ConversationRenamed);
            Eq("conversation.pin 类型名", "conversation.pin", Protocol.ConversationPin);
            Eq("conversation.pinned 类型名", "conversation.pinned", Protocol.ConversationPinned);
            Eq("conversation.create 空载荷", 0, Protocol.BuildConversationCreate().Count);
            Eq("conversation.switch payload", "conv-a",
                Protocol.BuildConversationTarget("conv-a")["conversationId"].AsString());

            // ---- 附录 A.15：打断与插话 ----
            Eq("abort 类型名", "abort", Protocol.Abort);
            Eq("abort 空载荷", 0, Protocol.BuildAbort().Count);
            var abortTarget = Protocol.BuildAbort("conv-a");
            Eq("abort 目标会话", "conv-a", abortTarget["conversationId"].AsString());
            Eq<string>("prompt.mode 缺省不写", null,
                Protocol.BuildPrompt("你好", "conv-a")["mode"].AsString());
            Eq("prompt.mode=steer", "steer",
                Protocol.BuildPrompt("插一句", "conv-a", null, Protocol.PromptModeSteer)["mode"].AsString());
            Eq("prompt.mode=steer 保留原文", "插一句",
                Protocol.BuildPrompt("插一句", "conv-a", null, Protocol.PromptModeSteer)["text"].AsString());

            var multi = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"activeConversationId\":\"conv-a\"," +
                "\"conversations\":[{" +
                "\"id\":\"conv-a\",\"title\":\"量子芯片\",\"createdAt\":100,\"updatedAt\":200," +
                "\"recordCount\":2,\"records\":[" +
                "{\"text\":\"帮我规划\",\"timestamp\":1},{\"text\":\"再算一遍\",\"timestamp\":2,\"gameTime\":\"08:42\"}]}," +
                "{\"id\":\"conv-b\",\"title\":\"新会话\",\"recordCount\":0,\"records\":[]}]}"));
            Eq("save.bound activeConversationId", "conv-a", multi.ActiveConversationId);
            Eq("save.bound conversations 数", 2, multi.Conversations.Count);
            Eq("conversation title", "量子芯片", multi.Conversations[0].Title);
            Eq("conversation records 数", 2, multi.Conversations[0].Records.Count);
            Eq("conversation record text", "帮我规划", multi.Conversations[0].Records[0].Text);
            Eq("conversation record gameTime", "08:42", multi.Conversations[0].Records[1].GameTime);
            Eq("conversation 未置顶缺省 false", false, multi.Conversations[0].Pinned);

            var pinnedInfo = ConversationInfo.Parse(JsonValue.Parse(
                "{\"id\":\"conv-a\",\"title\":\"量子芯片\",\"recordCount\":1,\"records\":[]," +
                "\"pinned\":true}"));
            Eq("conversation pinned", true, pinnedInfo.Pinned);
            pinnedInfo.SetPinned(false);
            Eq("conversation SetPinned", false, pinnedInfo.Pinned);

            var switched = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"conversationId\":\"conv-a\",\"existed\":true,\"messageCount\":2," +
                "\"conversation\":{\"id\":\"conv-a\",\"title\":\"量子芯片\",\"recordCount\":1,\"records\":[{\"text\":\"帮我规划\",\"timestamp\":1}]}," +
                "\"history\":[{\"role\":\"user\",\"text\":\"帮我规划\",\"timestamp\":1}]}"));
            Eq("conversation.switched id", "conv-a", switched.ConversationId);
            Eq("conversation.switched running 缺省 false", false, switched.Running);
            Eq("conversation.switched state 缺省 idle", "idle", switched.State);
            var switchedRunning = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"conversationId\":\"conv-a\",\"running\":true,\"state\":\"tools\"}"));
            Eq("conversation.switched running", true, switchedRunning.Running);
            Eq("conversation.switched state", "tools", switchedRunning.State);
            Eq("conversation.switched conversation", "量子芯片", switched.Conversation.Title);
            Eq("conversation.switched history", 1, switched.History.Count);
            Check("conversation.switched 无丢弃字段时为空", string.IsNullOrEmpty(switched.DiscardedConversationId));

            // 附录 A.13：切走一个没落盘的空会话，sidecar 回传丢弃的 id。
            var switchedWithDrop = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"conversationId\":\"conv-a\",\"existed\":true,\"messageCount\":1," +
                "\"discardedConversationId\":\"conv-9a\"," +
                "\"conversation\":{\"id\":\"conv-a\",\"title\":\"量子芯片\",\"recordCount\":1,\"records\":[]}," +
                "\"history\":[]}"));
            Eq("conversation.switched discardedConversationId", "conv-9a", switchedWithDrop.DiscardedConversationId);

            Eq("conversation.activity 类型名", "conversation.activity", Protocol.ConversationActivity);
            var activity = ConversationActivityInfo.Parse(JsonValue.Parse(
                "{\"conversationId\":\"conv-a\",\"state\":\"attention\",\"reason\":\"confirm\"}"));
            Eq("conversation.activity id", "conv-a", activity?.ConversationId);
            Eq("conversation.activity state", "attention", activity?.State);
            Eq("conversation.activity reason", "confirm", activity?.Reason);
            Check("conversation.activity 缺 id 拒绝",
                ConversationActivityInfo.Parse(JsonValue.Parse("{\"state\":\"running\"}")) == null);
            Check("conversation.activity 缺 state 拒绝",
                ConversationActivityInfo.Parse(JsonValue.Parse("{\"conversationId\":\"conv-a\"}")) == null);

            var updated = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"conversationId\":\"conv-a\",\"title\":\"量子芯片产线规划\"}"));
            Eq("conversation.updated id", "conv-a", updated.ConversationId);
            Eq("conversation.updated title", "量子芯片产线规划", updated.Title);
            var updatedPayload = Protocol.BuildConversationUpdated("conv-a", "量子芯片产线规划");
            Eq("conversation.updated builder id", "conv-a", updatedPayload["conversationId"].AsString());
            Eq("conversation.updated builder title", "量子芯片产线规划", updatedPayload["title"].AsString());
            var renamePayload = Protocol.BuildConversationRename("conv-a", "玩家标题");
            Eq("conversation.rename builder id", "conv-a", renamePayload["conversationId"].AsString());
            Eq("conversation.rename builder title", "玩家标题", renamePayload["title"].AsString());
            var renamedInfo = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"conversationId\":\"conv-a\",\"title\":\"玩家标题\",\"ok\":true,\"error\":null}"));
            Eq("conversation.renamed ok", true, renamedInfo.Ok);
            Eq("conversation.renamed title", "玩家标题", renamedInfo.Title);
            var failedRename = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"conversationId\":\"conv-a\",\"title\":\"\",\"ok\":false,\"error\":\"invalid_title\"}"));
            Check("conversation.renamed 失败 ok=false", !failedRename.Ok);
            Eq("conversation.renamed 失败错误", "invalid_title", failedRename.Error);

            var deletedInfo = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"conversationId\":\"conv-a\",\"ok\":true,\"error\":null," +
                "\"activeConversationId\":\"conv-b\",\"conversations\":[{\"id\":\"conv-b\",\"title\":\"新会话\",\"recordCount\":0}]," +
                "\"history\":[]}"));
            Eq("conversation.deleted ok", true, deletedInfo.Ok);
            Eq("conversation.deleted active", "conv-b", deletedInfo.ActiveConversationId);
            Eq("conversation.deleted conversations", 1, deletedInfo.Conversations.Count);
            var failedDelete = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"conversationId\":\"conv-a\",\"ok\":false,\"error\":\"busy\"}"));
            Check("conversation.deleted 失败 ok=false", !failedDelete.Ok);
            Eq("conversation.deleted 失败错误", "busy", failedDelete.Error);

            // ---- 附录 A.10：回存回答游戏时间 ----
            Eq("reply.time 类型名", "reply.time", Protocol.ReplyTime);
            var replyTime = Protocol.BuildReplyTime("问题", "回答", "08:42", "conv-a");
            Eq("reply.time prompt", "问题", replyTime["prompt"].AsString());
            Eq("reply.time reply", "回答", replyTime["reply"].AsString());
            Eq("reply.time gameTime", "08:42", replyTime["gameTime"].AsString());
            Eq("reply.time conversationId", "conv-a", replyTime["conversationId"].AsString());

            Eq("conversation.pin builder id", "conv-a",
                Protocol.BuildConversationPin("conv-a", true)["conversationId"].AsString());
            Eq("conversation.pin builder pinned", true,
                Protocol.BuildConversationPin("conv-a", true)["pinned"].AsBool());
            Eq("conversation.pin builder saveKey", "3f2a",
                Protocol.BuildConversationPin("conv-a", true, "3f2a")["saveKey"].AsString());
            var pinnedAck = ConversationEventInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"conversationId\":\"conv-a\",\"pinned\":true,\"ok\":true,\"error\":null}"));
            Eq("conversation.pinned ok", true, pinnedAck.Ok);
            Eq("conversation.pinned pinned", true, pinnedAck.Pinned);
            Eq("conversation.pinned id", "conv-a", pinnedAck.ConversationId);

            // ---- 附录 A.7：删除对话回合 ----
            Eq("history.delete 类型名", "history.delete", Protocol.HistoryDelete);
            Eq("history.deleted 类型名", "history.deleted", Protocol.HistoryDeleted);

            var del = Protocol.BuildHistoryDelete("3f2a", "上次的问题", 1760000000000L, "conv-a");
            Eq("history.delete saveKey", "3f2a", del["saveKey"].AsString());
            Eq("history.delete conversationId", "conv-a", del["conversationId"].AsString());
            Eq("history.delete text", "上次的问题", del["text"].AsString());
            Eq("history.delete timestamp", 1760000000000L, del["timestamp"].AsLong());

            var delNoTs = Protocol.BuildHistoryDelete(null, "x", 0);
            Check("无时间戳时不写 timestamp", !delNoTs.Has("timestamp"));
            Check("无 saveKey 时不写 saveKey", !delNoTs.Has("saveKey"));

            var deletedOk = HistoryDeletedInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"text\":\"上次的问题\",\"ok\":true,\"removed\":2,\"error\":null}"));
            Check("history.deleted ok", deletedOk.Ok);
            Eq("history.deleted removed", 2, deletedOk.Removed);
            Check("history.deleted error 为 null", string.IsNullOrEmpty(deletedOk.Error));

            var deletedFail = HistoryDeletedInfo.Parse(JsonValue.Parse(
                "{\"ok\":false,\"removed\":0,\"error\":\"busy\"}"));
            Check("history.deleted 失败", !deletedFail.Ok);
            Eq("history.deleted 失败原因", "busy", deletedFail.Error);
            Eq("history.deleted 缺字段安全默认", 0, HistoryDeletedInfo.Parse(null).Removed);

            // ---- 附录 A.11：压缩记录与撤销 ----
            Eq("context.compacted 类型名", "context.compacted", Protocol.ContextCompacted);
            Eq("history.undo_compaction 类型名", "history.undo_compaction", Protocol.HistoryUndoCompaction);
            Eq("history.compaction_undone 类型名", "history.compaction_undone", Protocol.HistoryCompactionUndone);

            var undo = Protocol.BuildHistoryUndoCompaction("3f2a", "c1", "conv-a");
            Eq("undo.compaction saveKey", "3f2a", undo["saveKey"].AsString());
            Eq("undo.compaction conversationId", "conv-a", undo["conversationId"].AsString());
            Eq("undo.compaction id", "c1", undo["compactionId"].AsString());
            var undoLatest = Protocol.BuildHistoryUndoCompaction(null, null);
            Check("撤销最近一次不写 compactionId", !undoLatest.Has("compactionId"));
            Check("无 saveKey 时不写 saveKey", !undoLatest.Has("saveKey"));

            var compacted = ContextCompactedInfo.Parse(JsonValue.Parse(
                "{\"summary\":\"摘要\",\"compactionId\":\"c1\",\"timestamp\":1760000000000,"
                + "\"tokensBefore\":96000,\"estimatedTokensAfter\":24000,\"reason\":\"threshold\"}"));
            Eq("context.compacted summary", "摘要", compacted.Summary);
            Eq("context.compacted id", "c1", compacted.CompactionId);
            Eq("context.compacted tokensBefore", 96000, compacted.TokensBefore);
            Eq("context.compacted tokensAfter", 24000, compacted.EstimatedTokensAfter);
            Check("空 summary 解析为 null", ContextCompactedInfo.Parse(JsonValue.Parse("{\"summary\":\"\"}")) == null);

            var undone = HistoryCompactionUndoneInfo.Parse(JsonValue.Parse(
                "{\"compactionId\":\"c1\",\"ok\":true,\"removed\":1,\"mayRecompact\":true}"));
            Check("compaction_undone ok", undone.Ok);
            Eq("compaction_undone id", "c1", undone.CompactionId);
            Eq("compaction_undone removed", 1, undone.Removed);
            Check("compaction_undone mayRecompact", undone.MayRecompact);
            Eq("compaction_undone 缺字段安全默认", 0, HistoryCompactionUndoneInfo.Parse(null).Removed);

            var withCompaction = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"3f2a\",\"history\":[" +
                "{\"role\":\"user\",\"text\":\"旧问题\"}," +
                "{\"role\":\"assistant\",\"kind\":\"compaction\",\"text\":\"旧对话摘要\"," +
                "\"compactionId\":\"c1\",\"tokensBefore\":1000,\"estimatedTokensAfter\":200}]}"));
            Eq("压缩记录条数", 2, withCompaction.History.Count);
            Eq("压缩记录 kind", "compaction", withCompaction.History[1].Kind);
            Eq("压缩记录 id", "c1", withCompaction.History[1].CompactionId);
            Eq("压缩记录 tokensBefore", 1000, withCompaction.History[1].TokensBefore);
            Eq("压缩记录 tokensAfter", 200, withCompaction.History[1].EstimatedTokensAfter);

            // ---- 附录 A.8：本地工具可视化 ----
            Eq("tool_activity 类型名", "tool_activity", Protocol.ToolActivity);
            var act = ToolActivityInfo.Parse(JsonValue.Parse(
                "{\"callId\":\"local:abc\",\"phase\":\"end\",\"name\":\"kb_plan\","
                + "\"ok\":true,\"summary\":\"宇宙矩阵 20/min\",\"detail\":\"…\"}"));
            Eq("tool_activity callId", "local:abc", act.CallId);
            Eq("tool_activity phase", "end", act.Phase);
            Eq("tool_activity name", "kb_plan", act.Name);
            Check("tool_activity ok", act.Ok);
            Eq("tool_activity summary", "宇宙矩阵 20/min", act.Summary);
            var actStart = ToolActivityInfo.Parse(JsonValue.Parse(
                "{\"callId\":\"l1\",\"phase\":\"start\",\"name\":\"kb_search\",\"args\":\"{}\"}"));
            Eq("tool_activity start 无 ok 时为 false", false, actStart.Ok);
            Eq("tool_activity phase 缺省为 end", "end", ToolActivityInfo.Parse(JsonValue.Parse("{}")).Phase);
            var scopedAct = ToolActivityInfo.Parse(JsonValue.Parse(
                "{\"callId\":\"c\",\"conversationId\":\"conv-a\",\"phase\":\"start\"}"));
            Eq("tool_activity conversationId", "conv-a", scopedAct.ConversationId);

            // 前向兼容：config.state 未来新增字段不应影响解析
            var futureCfg = ConfigState.Parse(JsonValue.Parse("{\"state\":\"ready\",\"futureField\":42}"));
            Check("config.state 未知字段被容忍", futureCfg.IsReady);

            // ---- 附录 B：tool_call / tool_result ----
            Eq("tool_call 类型名", "tool_call", Protocol.ToolCall);
            Eq("tool_result 类型名", "tool_result", Protocol.ToolResult);
            Eq("只读能力名", "game.read.v1", Protocol.CapabilityGameRead);

            var callPayload = JsonValue.Parse(
                "{\"callId\":\"call_1\",\"name\":\"game_get_power\"," +
                "\"args\":{\"planetId\":3},\"risk\":0,\"timeoutMs\":8000}");
            var call = ToolCallInfo.Parse(callPayload, null);
            Check("tool_call 可解析", call != null);
            Eq("tool_call callId", "call_1", call.CallId);
            Eq("tool_call name", "game_get_power", call.Name);
            Eq("tool_call args.planetId", 3L, call.Args["planetId"].AsLong());
            Eq("tool_call timeout", 8000, call.TimeoutMs);
            var scopedCall = ToolCallInfo.Parse(JsonValue.Parse(
                "{\"callId\":\"c\",\"name\":\"game_get_power\",\"conversationId\":\"conv-a\"}"), null);
            Eq("tool_call conversationId", "conv-a", scopedCall.ConversationId);

            var fallback = ToolCallInfo.Parse(
                JsonValue.Parse("{\"name\":\"game_get_overview\",\"args\":{}}"), "call_from_replyTo");
            Check("callId 可回退到 replyTo", fallback != null && fallback.CallId == "call_from_replyTo");
            Check("缺 name 时拒绝", ToolCallInfo.Parse(JsonValue.Parse("{\"callId\":\"c\"}"), null) == null);
            Check("args 非对象时退化为空对象",
                ToolCallInfo.Parse(JsonValue.Parse("{\"callId\":\"c\",\"name\":\"x\",\"args\":[]}"), null)
                    .Args.IsObject);

            var clamped = ToolCallInfo.Parse(
                JsonValue.Parse("{\"callId\":\"c\",\"name\":\"x\",\"timeoutMs\":999999}"), null);
            Eq("timeout 上限被钳制", 30000, clamped.TimeoutMs);

            Eq("ask 能力名", "ui.ask.v1", Protocol.CapabilityUiAsk);
            Eq("ask 工具名", "ask_user", Protocol.ToolAskUser);
            Eq("ask 旧名别名", "ui_ask_user", Protocol.ToolUiAskUser);
            Eq("ask 超时常量", 600000, Protocol.AskTimeoutMs);
            Eq("write 能力名", "game.write.v1", Protocol.CapabilityGameWrite);
            Eq("写工具名", "game_set_research", Protocol.ToolSetResearch);
            Eq("确认超时常量", 60000, Protocol.ConfirmTimeoutMs);

            var confirm = ConfirmRequestInfo.Parse(JsonValue.Parse(
                "{\"requestId\":\"confirm_1\",\"callId\":\"call_1\",\"tool\":\"game_open_panel\"," +
                "\"argsHash\":\"h\",\"token\":\"t\",\"title\":\"打开面板？\",\"body\":\"inventory\"," +
                "\"risk\":1,\"timeoutMs\":60000}"));
            Eq("confirm requestId", "confirm_1", confirm?.RequestId);
            Eq("confirm callId", "call_1", confirm?.CallId);
            Eq("confirm tool", "game_open_panel", confirm?.Tool);
            Eq("confirm risk", 1, confirm?.Risk);
            var scopedConfirm = ConfirmRequestInfo.Parse(JsonValue.Parse(
                "{\"requestId\":\"r\",\"callId\":\"c\",\"tool\":\"game_set_recipe\"," +
                "\"token\":\"t\",\"conversationId\":\"conv-a\"}"));
            Eq("confirm conversationId", "conv-a", scopedConfirm.ConversationId);
            var writeCall = ToolCallInfo.Parse(JsonValue.Parse(
                "{\"callId\":\"call_1\",\"name\":\"game_open_panel\",\"risk\":1," +
                "\"confirmToken\":\"t\",\"confirmArgsHash\":\"h\"}"), null);
            Eq("写调用 token", "t", writeCall?.ConfirmToken);
            Eq("写调用 argsHash", "h", writeCall?.ConfirmArgsHash);
            var askClamped = ToolCallInfo.Parse(
                JsonValue.Parse("{\"callId\":\"c\",\"name\":\"ask_user\",\"timeoutMs\":999999}"), null);
            Eq("ask timeout 放宽到 600000", 600000, askClamped.TimeoutMs);
            var askDefault = ToolCallInfo.Parse(
                JsonValue.Parse("{\"callId\":\"c\",\"name\":\"ask_user\"}"), null);
            Eq("ask 缺省 timeout 也用 600000", 600000, askDefault.TimeoutMs);
            var askLegacy = ToolCallInfo.Parse(
                JsonValue.Parse("{\"callId\":\"c\",\"name\":\"ui_ask_user\",\"timeoutMs\":999999}"), null);
            Eq("ask 旧名 timeout 也放宽", 600000, askLegacy.TimeoutMs);

            var meta = JsonValue.NewObject();
            meta.Set("gameTick", 123);
            var okResult = Protocol.BuildToolResult("call_1", true,
                JsonValue.Parse("{\"display\":\"ok\"}"), null, null, meta);
            Check("ok=true 写出", okResult["ok"].AsBool());
            Eq("tool_result data 透传", "ok", okResult["data"]["display"].AsString());
            Check("ok 时 error 为 null", okResult["error"].IsNull);
            Eq("tool_result meta.gameTick", 123L, okResult["meta"]["gameTick"].AsLong());

            var errResult = Protocol.BuildToolResult("call_2", false, JsonValue.Null,
                Protocol.CodeTimeout, "超时", null);
            Check("ok=false 写出", !errResult["ok"].AsBool());
            Eq("error.code", "timeout", errResult["error"]["code"].AsString());
            Eq("error.message", "超时", errResult["error"]["message"].AsString());
            Check("meta 缺省时仍为对象", errResult["meta"].IsObject);

            // ---- 附录 B.3：image 附件（M4a）----
            var image = JsonValue.NewObject();
            image.Set("mimeType", "image/jpeg");
            image.Set("data", "AAAA");
            image.Set("width", 1280);
            image.Set("height", 720);
            var withImage = Protocol.BuildToolResult("call_3", true,
                JsonValue.Parse("{\"display\":\"已截图\"}"), null, null, meta, image);
            Eq("image.mimeType 透传", "image/jpeg", withImage["image"]["mimeType"].AsString());
            Eq("image.width 透传", 1280L, withImage["image"]["width"].AsLong());
            Check("image 不混进 data", !withImage["data"].Has("image"));

            var failedWithImage = Protocol.BuildToolResult("call_4", false, JsonValue.Null,
                Protocol.CodeTimeout, "超时", meta, image);
            Check("ok=false 时不写 image", !failedWithImage.Has("image"));

            var noImage = Protocol.BuildToolResult("call_5", true,
                JsonValue.Parse("{\"display\":\"ok\"}"), null, null, meta);
            Check("没有 image 时不写字段", !noImage.Has("image"));

            // 空载荷/空对象不能抛
            Check("null 载荷不抛", ToolCallInfo.Parse(null, null) == null);
        }

        // ---------------------------------------------------------------- 蓝图卡片（附录 A.9）

        private static void BlueprintCardTests()
        {
            Eq("消息类型常量", "blueprint_card", Protocol.BlueprintCard);

            var card = BlueprintCardInfo.Parse(JsonValue.Parse(
                "{\"path\":\"C:\\\\bp\\\\IcarusII\\\\电磁涡轮 20.txt\",\"relPath\":\"IcarusII/电磁涡轮 20.txt\",\"fileName\":\"电磁涡轮 20.txt\"," +
                "\"name\":\"电磁涡轮 20\",\"item\":\"电磁涡轮\",\"itemId\":1206,\"rate\":20," +
                "\"buildings\":24,\"width\":12,\"height\":5,\"author\":\"自动生成\"," +
                "\"version\":\"1.2.3\",\"attributes\":\"\",\"description\":\"测试\"," +
                "\"source\":\"generated\",\"license\":\"CC BY-NC-SA 4.0\"," +
                "\"repository\":\"https://github.com/DSPBluePrints/FactoryBluePrints\"}"));

            Eq("relPath", "IcarusII/电磁涡轮 20.txt", card.RelPath);
            Eq("fileName", "电磁涡轮 20.txt", card.FileName);
            Eq("name", "电磁涡轮 20", card.Name);
            Eq("item", "电磁涡轮", card.Item);
            Eq("itemId", 1206, card.ItemId);
            Eq("rate", 20.0, card.Rate);
            Eq("buildings", 24, card.Buildings);
            Eq("width", 12, card.Width);
            Eq("height", 5, card.Height);
            Eq("author", "自动生成", card.Author);
            Eq("version", "1.2.3", card.Version);
            Eq("description", "测试", card.Description);
            Eq("source", "generated", card.Source);
            Eq("license", "CC BY-NC-SA 4.0", card.License);
            Eq("标题优先用 name", "电磁涡轮 20", card.DisplayTitle);

            // 缺字段：不能抛，DisplayTitle 退化为去 .txt 的文件名。
            var sparse = BlueprintCardInfo.Parse(JsonValue.Parse("{\"fileName\":\"abc.txt\"}"));
            Eq("缺 name 时标题用文件名", "abc", sparse.DisplayTitle);
            Eq("缺 rate 默认 0", 0.0, sparse.Rate);
            Check("缺 source 为 null", sparse.Source == null);

            var none = BlueprintCardInfo.Parse(null);
            Check("null 载荷不抛", none != null);
            Eq("null 载荷兜底标题", "蓝图", none.DisplayTitle);

            // save.bound.history 的可选 cards（附录 A.6/A.9）
            var bound = SaveBoundInfo.Parse(JsonValue.Parse(
                "{\"saveKey\":\"s1\",\"history\":[" +
                "{\"role\":\"user\",\"text\":\"生成蓝图\"}," +
                "{\"role\":\"assistant\",\"text\":\"已生成\",\"cards\":[" +
                "{\"fileName\":\"a.txt\",\"name\":\"A\",\"rate\":20}]}]}"));
            Eq("历史消息 2 条", 2, bound.History.Count);
            Eq("回答带 1 张卡片", 1, bound.History[1].Cards.Count);
            Eq("历史卡片文件名", "a.txt", bound.History[1].Cards[0].FileName);
            Eq("历史卡片名称", "A", bound.History[1].Cards[0].Name);
            Eq("user 消息没有卡片", 0, bound.History[0].Cards.Count);
        }

        // ---------------------------------------------------------------- 小测试框架

        internal static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine($"── {name} ──");
        }

        internal static void Check(string name, bool ok)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine($"  ok   {name}");
            }
            else
            {
                _failures++;
                Console.WriteLine($"  FAIL {name}");
            }
        }

        internal static void Eq<T>(string name, T expected, T actual)
        {
            _checks++;
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Console.WriteLine($"  ok   {name}");
            }
            else
            {
                _failures++;
                Console.WriteLine($"  FAIL {name}: 期望 <{Fmt(expected)}> 实际 <{Fmt(actual)}>");
            }
        }

        private static string Fmt(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + s.Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
            return v.ToString();
        }

        private static void ExpectFail(string name, string json)
        {
            _checks++;
            try
            {
                var v = JsonValue.Parse(json);
                _failures++;
                Console.WriteLine($"  FAIL {name}: 期望解析失败，却得到 {v.ToJson()}");
            }
            catch (JsonParseException)
            {
                Console.WriteLine($"  ok   {name}");
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"  FAIL {name}: 抛出了非预期异常 {ex.GetType().Name}");
            }
        }
    }
}
