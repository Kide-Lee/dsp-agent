using System;
using System.Collections.Generic;
using IcarusII.Common.Json;

namespace IcarusII.Common.Protocol
{
    /// <summary>
    /// 工具参数 JSON Schema 的规范化与校验。
    ///
    /// 上游模型服务要求标准 JSON Schema；mod 作者/AI 常写成 `"type": "int"` 这类别名，
    /// 一旦进入工具表会让整次请求 400（`Invalid schema for function ...`）。
    /// 这里在加载与创建两处统一规范化，别名转标准类型，非法结构直接拒绝。
    /// </summary>
    internal static class ParameterSchema
    {
        private const int MaxDepth = 8;
        private const int MaxNodes = 400;

        private static readonly HashSet<string> Types = new HashSet<string>(StringComparer.Ordinal)
        {
            "object", "array", "string", "number", "integer", "boolean", "null",
        };

        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["int"] = "integer",
                ["int32"] = "integer",
                ["int64"] = "integer",
                ["uint"] = "integer",
                ["uint32"] = "integer",
                ["uint64"] = "integer",
                ["long"] = "integer",
                ["short"] = "integer",
                ["byte"] = "integer",
                ["float"] = "number",
                ["single"] = "number",
                ["double"] = "number",
                ["decimal"] = "number",
                ["num"] = "number",
                ["bool"] = "boolean",
                ["str"] = "string",
                ["text"] = "string",
                ["list"] = "array",
                ["dict"] = "object",
                ["map"] = "object",
            };

        internal static JsonValue Empty()
        {
            var schema = JsonValue.NewObject();
            schema.Set("type", "object");
            schema.Set("properties", JsonValue.NewObject());
            return schema;
        }

        /// <summary>规范化一个工具参数 schema；失败时返回 null 并给出原因。</summary>
        internal static JsonValue Normalize(JsonValue schema, out string error)
        {
            error = null;
            if (schema is null || schema.IsNull) return Empty();
            if (!schema.IsObject)
            {
                error = "parameters 必须是对象";
                return null;
            }

            var budget = new int[1];
            var result = NormalizeNode(schema, 0, budget, out error);
            if (result is null) return null;

            var type = result["type"];
            if (!type.IsNull)
            {
                if (type.IsString)
                {
                    if (!string.Equals(type.AsString(), "object", StringComparison.Ordinal))
                    {
                        error = "根参数必须声明 type=object";
                        return null;
                    }
                }
                else if (type.IsArray && !ContainsObject(type))
                {
                    error = "根参数必须允许 object";
                    return null;
                }
            }
            else
            {
                result.Set("type", "object");
            }
            if (!result.Has("properties")) result.Set("properties", JsonValue.NewObject());
            return result;
        }

        private static bool ContainsObject(JsonValue types)
        {
            foreach (var item in types.Items)
                if (item.IsString && string.Equals(item.AsString(), "object", StringComparison.Ordinal)) return true;
            return false;
        }

        private static JsonValue NormalizeNode(JsonValue node, int depth, int[] budget, out string error)
        {
            error = null;
            if (node is null || !node.IsObject)
            {
                error = "schema 节点必须是对象";
                return null;
            }
            if (depth > MaxDepth)
            {
                error = "schema 嵌套过深";
                return null;
            }
            if (++budget[0] > MaxNodes)
            {
                error = "schema 节点过多";
                return null;
            }

            var result = JsonValue.NewObject();
            CopyScalar(node, result, "description");
            CopyScalar(node, result, "title");
            CopyScalar(node, result, "format");
            CopyScalar(node, result, "pattern");
            CopyScalar(node, result, "default");
            CopyScalar(node, result, "const");
            CopyNumber(node, result, "minimum");
            CopyNumber(node, result, "maximum");
            CopyNumber(node, result, "exclusiveMinimum");
            CopyNumber(node, result, "exclusiveMaximum");
            CopyNumber(node, result, "minLength");
            CopyNumber(node, result, "maxLength");
            CopyNumber(node, result, "minItems");
            CopyNumber(node, result, "maxItems");
            CopyBool(node, result, "uniqueItems");

            if (node.Has("type"))
            {
                var type = NormalizeType(node["type"], out var typeError);
                if (type is null)
                {
                    error = typeError;
                    return null;
                }
                result.Set("type", type);
            }

            if (node.Has("required"))
            {
                var required = JsonValue.NewArray();
                foreach (var item in node["required"].Items)
                {
                    if (!item.IsString) continue;
                    var name = item.AsString(string.Empty);
                    if (name.Length > 0) required.Add(name);
                }
                result.Set("required", required);
            }

            if (node.Has("enum"))
            {
                var values = JsonValue.NewArray();
                foreach (var item in node["enum"].Items)
                {
                    if (item.IsObject || item.IsArray) continue;
                    values.Add(item);
                }
                if (values.Count > 0) result.Set("enum", values);
            }

            if (node.Has("properties"))
            {
                var properties = node["properties"];
                if (!properties.IsObject)
                {
                    error = "properties 必须是对象";
                    return null;
                }
                var normalized = JsonValue.NewObject();
                foreach (var pair in properties.Members)
                {
                    var child = NormalizeNode(pair.Value, depth + 1, budget, out var childError);
                    if (child is null)
                    {
                        error = $"properties.{pair.Key}：{childError}";
                        return null;
                    }
                    normalized.Set(pair.Key, child);
                }
                result.Set("properties", normalized);
            }

            if (node.Has("items"))
            {
                var items = node["items"];
                if (items.IsArray)
                {
                    var list = JsonValue.NewArray();
                    foreach (var item in items.Items)
                    {
                        var child = NormalizeNode(item, depth + 1, budget, out var childError);
                        if (child is null)
                        {
                            error = $"items：{childError}";
                            return null;
                        }
                        list.Add(child);
                    }
                    result.Set("items", list);
                }
                else
                {
                    var child = NormalizeNode(items, depth + 1, budget, out var childError);
                    if (child is null)
                    {
                        error = $"items：{childError}";
                        return null;
                    }
                    result.Set("items", child);
                }
            }

            if (node.Has("additionalProperties"))
            {
                var extra = node["additionalProperties"];
                if (extra.Kind == JsonKind.Bool)
                {
                    result.Set("additionalProperties", extra);
                }
                else
                {
                    var child = NormalizeNode(extra, depth + 1, budget, out var childError);
                    if (child is null)
                    {
                        error = $"additionalProperties：{childError}";
                        return null;
                    }
                    result.Set("additionalProperties", child);
                }
            }

            foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (!node.Has(key)) continue;
                var list = JsonValue.NewArray();
                foreach (var item in node[key].Items)
                {
                    var child = NormalizeNode(item, depth + 1, budget, out var childError);
                    if (child is null)
                    {
                        error = $"{key}：{childError}";
                        return null;
                    }
                    list.Add(child);
                }
                if (list.Count == 0)
                {
                    error = $"{key} 不能为空";
                    return null;
                }
                result.Set(key, list);
            }

            if (result.Has("required") && result.Has("properties"))
            {
                var filtered = JsonValue.NewArray();
                foreach (var item in result["required"].Items)
                {
                    var name = item.AsString(string.Empty);
                    if (name.Length > 0 && result["properties"].Has(name)) filtered.Add(name);
                }
                result.Set("required", filtered);
            }

            if (!result.Has("type"))
            {
                if (result.Has("properties")) result.Set("type", "object");
                else if (result.Has("items")) result.Set("type", "array");
            }
            return result;
        }

        private static JsonValue NormalizeType(JsonValue type, out string error)
        {
            error = null;
            if (type.IsString)
            {
                var name = Canonical(type.AsString(null));
                if (name is null)
                {
                    error = $"不支持的 type：{type.AsString()}";
                    return null;
                }
                return JsonValue.Of(name);
            }
            if (type.IsArray)
            {
                var list = JsonValue.NewArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in type.Items)
                {
                    if (!item.IsString)
                    {
                        error = "type 数组只能包含字符串";
                        return null;
                    }
                    var name = Canonical(item.AsString(null));
                    if (name is null)
                    {
                        error = $"不支持的 type：{item.AsString()}";
                        return null;
                    }
                    if (seen.Add(name)) list.Add(name);
                }
                if (list.Count == 0)
                {
                    error = "type 数组不能为空";
                    return null;
                }
                return list;
            }
            error = "type 必须是字符串或字符串数组";
            return null;
        }

        private static string Canonical(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim();
            if (Types.Contains(value)) return value;
            if (Aliases.TryGetValue(value, out var alias)) return alias;
            // 大小写不敏感地再试一次标准类型
            foreach (var known in Types)
                if (string.Equals(known, value, StringComparison.OrdinalIgnoreCase)) return known;
            return null;
        }

        private static void CopyScalar(JsonValue source, JsonValue target, string key)
        {
            var value = source[key];
            if (value.IsNull) return;
            if (value.IsString || value.Kind == JsonKind.Bool || value.Kind == JsonKind.Number)
                target.Set(key, value);
        }

        private static void CopyNumber(JsonValue source, JsonValue target, string key)
        {
            var value = source[key];
            if (value.Kind == JsonKind.Number) target.Set(key, value);
        }

        private static void CopyBool(JsonValue source, JsonValue target, string key)
        {
            var value = source[key];
            if (value.Kind == JsonKind.Bool) target.Set(key, value);
        }
    }
}
