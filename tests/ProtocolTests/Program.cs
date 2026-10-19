using System;
using System.Collections.Generic;
using System.Text;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

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

            var withReply = Protocol.Envelope(Protocol.Pong, Protocol.BuildPing(123), "s-1", "m-9");
            Eq("replyTo 存在", "m-9", withReply["replyTo"].AsString());
            Eq("ping t", 123L, withReply["payload"]["t"].AsLong());

            var hello = Protocol.BuildHello("tok", "0.1.0", "0.10.30.3100", "zh-CN",
                new[] { "ui.chat" }, null);
            Eq("hello token", "tok", hello["token"].AsString());
            Eq("hello modVersion", "0.1.0", hello["modVersion"].AsString());
            Eq("hello capabilities[0]", "ui.chat", hello["capabilities"][0].AsString());
            Check("saveKey 为 null 时不写入", !hello.Has("saveKey"));

            var withSave = Protocol.BuildHello("t", "1", "2", "zh", null, "abc123");
            Eq("saveKey 写入", "abc123", withSave["saveKey"].AsString());
            Eq("capabilities 为 null 时是空数组", 0, withSave["capabilities"].Count);

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

            var keyOnly = Protocol.BuildConfigSet(null, null, "sk-x", null);
            Eq("只传 key 时字段数", 1, keyOnly.Count);
            Eq("只传 key 时 key 存在", "sk-x", keyOnly["apiKey"].AsString());

            var cfgThink = Protocol.BuildConfigSet(null, null, null, null, "high");
            Eq("config.set thinkingLevel", "high", cfgThink["thinkingLevel"].AsString());
            var cfgNoThink = Protocol.BuildConfigSet(null, null, null, null, null);
            Check("config.set 未给出 thinkingLevel 不写入", !cfgNoThink.Has("thinkingLevel"));

            // ready.payload.config（附录 A.2）
            var ready = JsonValue.Parse(
                "{\"protocol\":1,\"sessionId\":\"sess_1\",\"agent\":\"pi/deepseek-v4-flash\"," +
                "\"config\":{\"state\":\"missing_key\",\"provider\":\"deepseek\",\"model\":\"deepseek-v4-flash\"}}");
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

            var csThink = ConfigState.Parse(JsonValue.Parse(
                "{\"state\":\"ready\",\"model\":\"deepseek-v4-pro\",\"thinkingLevel\":\"max\"}"));
            Eq("config.state.thinkingLevel", "max", csThink.ThinkingLevel);

            var bad = ConfigState.Parse(JsonValue.Parse("{\"state\":\"invalid_key\"}"));
            Check("invalid_key 需要重新录入", bad.NeedsKey);

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

            // 解绑：字段必须**存在且为 null**，sidecar 靠这个区分"解绑"与"没提"
            var unbind = Protocol.BuildSaveBind(null, null);
            Check("解绑时 saveKey 字段存在", unbind.Has("saveKey"));
            Check("解绑时 saveKey 为 null", unbind["saveKey"].IsNull);
            Check("解绑时不写 saveName", !unbind.Has("saveName"));
            Eq("解绑载荷字段数", 1, unbind.Count);

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
                "{\"role\":\"assistant\",\"text\":\"上次的回答\"}]}"));
            Eq("save.bound history 条数", 2, withHistory.History.Count);
            Eq("history 第一条 role", "user", withHistory.History[0].Role);
            Eq("history 第一条 text", "上次的问题", withHistory.History[0].Text);
            Eq("history 第一条 timestamp", 123L, withHistory.History[0].Timestamp);
            Eq("history 第二条 role", "assistant", withHistory.History[1].Role);

            var noBound = SaveBoundInfo.Parse(null);
            Eq("null 载荷的安全默认", 0, noBound.MessageCount);
            Check("null 载荷的 SaveKey 为空", string.IsNullOrEmpty(noBound.SaveKey));

            // ---- 附录 A.7：删除对话回合 ----
            Eq("history.delete 类型名", "history.delete", Protocol.HistoryDelete);
            Eq("history.deleted 类型名", "history.deleted", Protocol.HistoryDeleted);

            var del = Protocol.BuildHistoryDelete("3f2a", "上次的问题", 1760000000000L);
            Eq("history.delete saveKey", "3f2a", del["saveKey"].AsString());
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
                "{\"path\":\"C:\\\\bp\\\\电磁涡轮 20.txt\",\"fileName\":\"电磁涡轮 20.txt\"," +
                "\"name\":\"电磁涡轮 20\",\"item\":\"电磁涡轮\",\"itemId\":1206,\"rate\":20," +
                "\"buildings\":24,\"width\":12,\"height\":5,\"author\":\"自动生成\"," +
                "\"version\":\"1.2.3\",\"attributes\":\"\",\"description\":\"测试\"," +
                "\"source\":\"generated\",\"license\":\"CC BY-NC-SA 4.0\"," +
                "\"repository\":\"https://github.com/DSPBluePrints/FactoryBluePrints\"}"));

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

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine($"── {name} ──");
        }

        private static void Check(string name, bool ok)
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

        private static void Eq<T>(string name, T expected, T actual)
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
