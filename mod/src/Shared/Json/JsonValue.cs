using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IcarusII.Common.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// 极小但完整的 JSON 值模型 + 解析器 + 序列化器。
    ///
    /// 为什么不用 Unity 的 JsonUtility / System.Text.Json：
    ///  - JsonUtility 不支持字典、异构数组、顶层数组，而 M1 的工具结果需要这些；
    ///  - System.Text.Json 不在 Unity 的 netstandard2.1 里（已实测编译失败）；
    ///  - 自研可保持本文件 **零 Unity 依赖**，从而能在普通 .NET 进程里跑端到端测试
    ///    （这是 M0 唯一能在没有游戏的情况下验证 C# 侧代码的手段）。
    ///
    /// 有意不支持的 JSON 扩展：注释、尾逗号、NaN/Infinity、超大深度。
    /// </summary>
    public sealed class JsonValue
    {
        private const int MaxDepth = 64;

        private readonly JsonKind _kind;
        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;

        // 对象按插入顺序保存，保证序列化输出稳定（便于测试与 diff）。
        private List<KeyValuePair<string, JsonValue>> _members;
        private Dictionary<string, JsonValue> _memberIndex;

        private List<JsonValue> _items;

        private static readonly JsonValue NullValue = new JsonValue();

        private JsonValue() { _kind = JsonKind.Null; }

        private JsonValue(bool v) { _kind = JsonKind.Bool; _bool = v; }
        private JsonValue(double v) { _kind = JsonKind.Number; _number = v; }
        private JsonValue(string v) { _kind = JsonKind.String; _string = v ?? string.Empty; }

        private JsonValue(JsonKind kind)
        {
            _kind = kind;
            if (kind == JsonKind.Array) _items = new List<JsonValue>();
            if (kind == JsonKind.Object)
            {
                _members = new List<KeyValuePair<string, JsonValue>>();
                _memberIndex = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            }
        }

        public static JsonValue Null => NullValue;
        public static JsonValue Of(bool v) => new JsonValue(v);
        public static JsonValue Of(double v) => new JsonValue(v);
        public static JsonValue Of(int v) => new JsonValue((double)v);
        public static JsonValue Of(long v) => new JsonValue((double)v);
        public static JsonValue Of(string v) => v is null ? NullValue : new JsonValue(v);
        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);
        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);

        public JsonKind Kind => _kind;

        public bool IsNull => _kind == JsonKind.Null;
        public bool IsObject => _kind == JsonKind.Object;
        public bool IsArray => _kind == JsonKind.Array;
        public bool IsString => _kind == JsonKind.String;

        // ---------------------------------------------------------------- 对象 API

        /// <summary>取对象成员；不存在或类型不符时返回 JsonValue.Null（不抛异常）。</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_kind != JsonKind.Object || key is null) return NullValue;
                return _memberIndex.TryGetValue(key, out var v) ? v : NullValue;
            }
        }

        public bool Has(string key)
        {
            return _kind == JsonKind.Object && key is not null && _memberIndex.ContainsKey(key);
        }

        /// <summary>设置对象成员；重复 key 覆盖原值但保留原位置。</summary>
        public JsonValue Set(string key, JsonValue value)
        {
            RequireKind(JsonKind.Object);
            if (key is null) throw new ArgumentNullException(nameof(key));
            value = value ?? NullValue;
            if (_memberIndex.ContainsKey(key))
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    if (string.Equals(_members[i].Key, key, StringComparison.Ordinal))
                    {
                        _members[i] = new KeyValuePair<string, JsonValue>(key, value);
                        break;
                    }
                }
            }
            else
            {
                _members.Add(new KeyValuePair<string, JsonValue>(key, value));
            }
            _memberIndex[key] = value;
            return this;
        }

        public JsonValue Set(string key, string value) => Set(key, Of(value));
        public JsonValue Set(string key, bool value) => Set(key, Of(value));
        public JsonValue Set(string key, int value) => Set(key, Of(value));
        public JsonValue Set(string key, long value) => Set(key, Of(value));
        public JsonValue Set(string key, double value) => Set(key, Of(value));

        /// <summary>仅在值非空时设置（避免写入 null 污染协议）。</summary>
        public JsonValue SetIfNotNull(string key, string value)
        {
            if (value is not null) Set(key, Of(value));
            return this;
        }

        public IEnumerable<KeyValuePair<string, JsonValue>> Members
        {
            get
            {
                if (_kind != JsonKind.Object) yield break;
                foreach (var m in _members) yield return m;
            }
        }

        // ---------------------------------------------------------------- 数组 API

        public JsonValue this[int index]
        {
            get
            {
                if (_kind != JsonKind.Array || index < 0 || index >= _items.Count) return NullValue;
                return _items[index];
            }
        }

        public int Count => _kind == JsonKind.Array ? _items.Count : (_kind == JsonKind.Object ? _members.Count : 0);

        public JsonValue Add(JsonValue value)
        {
            RequireKind(JsonKind.Array);
            _items.Add(value ?? NullValue);
            return this;
        }

        public JsonValue Add(string value) => Add(Of(value));
        public JsonValue Add(double value) => Add(Of(value));
        public JsonValue Add(int value) => Add(Of(value));
        public JsonValue Add(long value) => Add(Of(value));
        public JsonValue Add(bool value) => Add(Of(value));

        public IEnumerable<JsonValue> Items
        {
            get
            {
                if (_kind != JsonKind.Array) yield break;
                foreach (var v in _items) yield return v;
            }
        }

        // ---------------------------------------------------------------- 读值

        public string AsString(string fallback = null)
        {
            if (_kind == JsonKind.String) return _string;
            return fallback;
        }

        public bool AsBool(bool fallback = false)
        {
            if (_kind == JsonKind.Bool) return _bool;
            if (_kind == JsonKind.Number) return _number != 0;
            return fallback;
        }

        public double AsDouble(double fallback = 0)
        {
            if (_kind == JsonKind.Number) return _number;
            if (_kind == JsonKind.String &&
                double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return fallback;
        }

        public long AsLong(long fallback = 0)
        {
            if (_kind != JsonKind.Number) return fallback;
            if (_number > 9.2233720368547758E18 || _number < -9.2233720368547758E18) return fallback;
            return (long)Math.Round(_number, MidpointRounding.AwayFromZero);
        }

        public int AsInt(int fallback = 0)
        {
            var l = AsLong(fallback);
            if (l > int.MaxValue || l < int.MinValue) return fallback;
            return (int)l;
        }

        private void RequireKind(JsonKind expected)
        {
            if (_kind != expected)
                throw new InvalidOperationException($"JsonValue 类型为 {_kind}，但期望 {expected}");
        }

        // ---------------------------------------------------------------- 序列化

        public override string ToString() => ToJson();

        public string ToJson()
        {
            var sb = new StringBuilder(256);
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (_kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    WriteNumber(sb, _number);
                    break;
                case JsonKind.String:
                    WriteString(sb, _string);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _items[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _members.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _members[i].Key);
                        sb.Append(':');
                        _members[i].Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteNumber(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                // 协议层不允许 NaN/Infinity，退化为 null 而不是产出非法 JSON。
                sb.Append("null");
                return;
            }
            // 整数值写成整数形式，避免 "1733234567890.0" 这种噪音。
            if (v == Math.Floor(v) && v >= -9.2233720368547758E18 && v <= 9.2233720368547758E18)
            {
                sb.Append(((long)v).ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // 非 ASCII 原样输出：中文在协议里保持可读，也省带宽。
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- 解析

        private sealed class ParserState
        {
            public int Index;
        }

        public static JsonValue Parse(string json)
        {
            if (json is null) throw new JsonParseException("输入为 null");
            var state = new ParserState();
            var v = ParseValue(json, state, 0);
            SkipWhitespace(json, state);
            if (state.Index != json.Length)
                throw new JsonParseException($"第 {state.Index} 个字符后存在多余内容");
            return v;
        }

        public static bool TryParse(string json, out JsonValue value)
        {
            try
            {
                value = Parse(json);
                return true;
            }
            catch (JsonParseException)
            {
                value = NullValue;
                return false;
            }
        }

        private static JsonValue ParseValue(string s, ParserState state, int depth)
        {
            if (depth > MaxDepth) throw new JsonParseException("嵌套层级过深");
            SkipWhitespace(s, state);
            if (state.Index >= s.Length) throw new JsonParseException("内容意外结束");

            char c = s[state.Index];
            switch (c)
            {
                case '{': return ParseObject(s, state, depth);
                case '[': return ParseArray(s, state, depth);
                case '"': return Of(ParseString(s, state));
                case 't': Expect(s, state, "true"); return Of(true);
                case 'f': Expect(s, state, "false"); return Of(false);
                case 'n': Expect(s, state, "null"); return NullValue;
                default: return Of(ParseNumber(s, state));
            }
        }

        private static JsonValue ParseObject(string s, ParserState state, int depth)
        {
            var obj = NewObject();
            state.Index++; // '{'
            SkipWhitespace(s, state);
            if (state.Index < s.Length && s[state.Index] == '}') { state.Index++; return obj; }

            while (true)
            {
                SkipWhitespace(s, state);
                if (state.Index >= s.Length || s[state.Index] != '"')
                    throw new JsonParseException($"第 {state.Index} 个字符处期望对象键（字符串）");
                string key = ParseString(s, state);

                SkipWhitespace(s, state);
                if (state.Index >= s.Length || s[state.Index] != ':')
                    throw new JsonParseException($"第 {state.Index} 个字符处期望 ':'");
                state.Index++;

                var val = ParseValue(s, state, depth + 1);
                obj.Set(key, val);

                SkipWhitespace(s, state);
                if (state.Index >= s.Length) throw new JsonParseException("对象未闭合");
                if (s[state.Index] == ',') { state.Index++; continue; }
                if (s[state.Index] == '}') { state.Index++; return obj; }
                throw new JsonParseException($"第 {state.Index} 个字符处期望 ',' 或 '}}'");
            }
        }

        private static JsonValue ParseArray(string s, ParserState state, int depth)
        {
            var arr = NewArray();
            state.Index++; // '['
            SkipWhitespace(s, state);
            if (state.Index < s.Length && s[state.Index] == ']') { state.Index++; return arr; }

            while (true)
            {
                arr.Add(ParseValue(s, state, depth + 1));
                SkipWhitespace(s, state);
                if (state.Index >= s.Length) throw new JsonParseException("数组未闭合");
                if (s[state.Index] == ',') { state.Index++; continue; }
                if (s[state.Index] == ']') { state.Index++; return arr; }
                throw new JsonParseException($"第 {state.Index} 个字符处期望 ',' 或 ']'");
            }
        }

        private static string ParseString(string s, ParserState state)
        {
            state.Index++; // 开引号
            var sb = new StringBuilder(32);
            while (true)
            {
                if (state.Index >= s.Length) throw new JsonParseException("字符串未闭合");
                char c = s[state.Index++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (state.Index >= s.Length) throw new JsonParseException("转义序列不完整");
                char e = s[state.Index++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (state.Index + 4 > s.Length) throw new JsonParseException("\\u 转义不完整");
                        int code = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int d = HexDigit(s[state.Index + k]);
                            if (d < 0) throw new JsonParseException($"\\u 转义含非法十六进制字符 '{s[state.Index + k]}'");
                            code = (code << 4) | d;
                        }
                        state.Index += 4;
                        // 直接 Append((char)code)：.NET 字符串是 UTF-16，
                        // 代理对会被两个连续 \u 转义自然拼出，无需特殊处理。
                        sb.Append((char)code);
                        break;
                    default:
                        throw new JsonParseException($"未知转义序列 '\\{e}'");
                }
            }
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static double ParseNumber(string s, ParserState state)
        {
            int start = state.Index;
            if (state.Index < s.Length && (s[state.Index] == '-' || s[state.Index] == '+')) state.Index++;
            while (state.Index < s.Length && (char.IsDigit(s[state.Index]) || s[state.Index] == '.'
                || s[state.Index] == 'e' || s[state.Index] == 'E'
                || ((s[state.Index] == '-' || s[state.Index] == '+')
                    && (s[state.Index - 1] == 'e' || s[state.Index - 1] == 'E'))))
                state.Index++;

            string token = s.Substring(start, state.Index - start);
            if (token.Length == 0 ||
                !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new JsonParseException($"第 {start} 个字符处不是合法数字：'{token}'");
            return value;
        }

        private static void Expect(string s, ParserState state, string literal)
        {
            if (state.Index + literal.Length > s.Length
                || string.CompareOrdinal(s, state.Index, literal, 0, literal.Length) != 0)
                throw new JsonParseException($"第 {state.Index} 个字符处期望 '{literal}'");
            state.Index += literal.Length;
        }

        private static void SkipWhitespace(string s, ParserState state)
        {
            while (state.Index < s.Length)
            {
                char c = s[state.Index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') state.Index++;
                else break;
            }
        }
    }

    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }
}
