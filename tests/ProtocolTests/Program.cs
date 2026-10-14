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

            var bad = ConfigState.Parse(JsonValue.Parse("{\"state\":\"invalid_key\"}"));
            Check("invalid_key 需要重新录入", bad.NeedsKey);

            // 缺字段 / null 都必须有安全默认值，不能抛
            var dflt = ConfigState.Parse(null);
            Eq("null 载荷的默认 state", "missing_key", dflt.State);
            var empty = ConfigState.Parse(JsonValue.NewObject());
            Eq("空对象的默认 state", "missing_key", empty.State);
            Check("ConfigState.ToString 可读", dflt.ToString().Contains("missing_key"));

            // 前向兼容：config.state 未来新增字段不应影响解析
            var futureCfg = ConfigState.Parse(JsonValue.Parse("{\"state\":\"ready\",\"futureField\":42}"));
            Check("config.state 未知字段被容忍", futureCfg.IsReady);
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
