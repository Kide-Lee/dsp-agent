using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 第三方 mod 桥（P0）：只读 BepInEx 的插件元数据与配置项，并允许写入配置。
    ///
    /// 刻意不做反射调用：第三方方法的语义没有承诺，参数也没有白名单；
    /// 需要调用时走后续的 manifest/适配器。
    /// </summary>
    internal static class ModBridge
    {
        /// <summary>本插件 GUID；桥永远不把自己暴露给模型。</summary>
        internal const string SelfGuid = "icarus-ii";

        internal static bool Enabled => Plugin.BridgeEnabled;

        internal static void RequireEnabled()
        {
            if (!Enabled)
                throw new InvalidOperationException(
                    "第三方 mod 桥未启用（BepInEx/config/icarus-ii.cfg 的 [Bridge] AllowThirdPartyTools=false）");
        }

        /// <summary>当前已加载的第三方插件，按 GUID 排序。</summary>
        internal static List<PluginInfo> Plugins()
        {
            var list = new List<PluginInfo>();
            foreach (var pair in Chainloader.PluginInfos)
            {
                var info = pair.Value;
                if (info?.Metadata is null) continue;
                if (string.Equals(info.Metadata.GUID, SelfGuid, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(info);
            }
            list.Sort((a, b) => string.Compare(
                a.Metadata.GUID ?? string.Empty,
                b.Metadata.GUID ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
            return list;
        }

        internal static PluginInfo Find(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            var wanted = guid.Trim();
            foreach (var info in Plugins())
            {
                if (string.Equals(info.Metadata.GUID, wanted, StringComparison.OrdinalIgnoreCase))
                    return info;
            }
            return null;
        }

        /// <summary>某个插件声明的全部配置项，按 section/key 排序；未加载成功时返回空。</summary>
        internal static List<ConfigEntryBase> Entries(PluginInfo info)
        {
            var list = new List<ConfigEntryBase>();
            var config = info?.Instance?.Config;
            if (config is null) return list;
            foreach (var entry in config.GetConfigEntries())
            {
                if (entry?.Definition is null) continue;
                list.Add(entry);
            }
            list.Sort((a, b) =>
            {
                var section = string.Compare(
                    a.Definition.Section ?? string.Empty,
                    b.Definition.Section ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                return section != 0
                    ? section
                    : string.Compare(a.Definition.Key ?? string.Empty, b.Definition.Key ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        internal static ConfigEntryBase FindEntry(PluginInfo info, string section, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            foreach (var entry in Entries(info))
            {
                if (!string.Equals(entry.Definition.Key, key.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (section is not null &&
                    !string.Equals(entry.Definition.Section, section.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                return entry;
            }
            return null;
        }

        /// <summary>把配置值转成协议 JSON：基本类型保持原样，枚举转名字，其余转字符串。</summary>
        internal static JsonValue ValueJson(object value)
        {
            switch (value)
            {
                case null: return JsonValue.Null;
                case bool b: return JsonValue.Of(b);
                case int i: return JsonValue.Of(i);
                case long l: return JsonValue.Of(l);
                case float f: return JsonValue.Of((double)f);
                case double d: return JsonValue.Of(d);
                case string s: return JsonValue.Of(s);
                case Enum e: return JsonValue.Of(e.ToString());
                default: return JsonValue.Of(Text(value));
            }
        }

        /// <summary>把任意配置值转成 SetSerializedValue 能接受的 invariant 文本。</summary>
        internal static string Text(object value)
        {
            if (value is null) return string.Empty;
            if (value is bool b) return b ? "true" : "false";
            if (value is Enum e) return e.ToString();
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>模型传来的 JSON 值转文本；对象/数组不接受。</summary>
        internal static string InputText(JsonValue node)
        {
            if (node is null || node.IsNull) return string.Empty;
            switch (node.Kind)
            {
                case JsonKind.Bool: return node.AsBool() ? "true" : "false";
                case JsonKind.String: return node.AsString(string.Empty);
                case JsonKind.Number:
                    var number = node.AsDouble();
                    var integral = Math.Abs(number % 1) < double.Epsilon && Math.Abs(number) < 9e15;
                    return integral
                        ? ((long)number).ToString(CultureInfo.InvariantCulture)
                        : number.ToString("R", CultureInfo.InvariantCulture);
                default:
                    throw new InvalidOperationException("value 必须是字符串、数字或布尔值");
            }
        }

        internal static string TypeName(Type type) => type?.Name ?? "Unknown";

        /// <summary>hello.extensions：当前已加载的第三方插件摘要（按 GUID 排序，id 唯一）。</summary>
        internal static ExtensionSummary[] Extensions()
        {
            var list = new List<ExtensionSummary>();
            foreach (var info in Plugins())
            {
                var guid = info.Metadata.GUID ?? string.Empty;
                var manifestTools = ModManifest.ToolsFor(guid);
                var tools = new List<ExtensionToolSummary>();
                foreach (var tool in manifestTools) tools.Add(tool.ToSummary());
                list.Add(new ExtensionSummary
                {
                    Id = IdFor(info),
                    Guid = guid,
                    Name = info.Metadata.Name ?? guid,
                    Version = info.Metadata.Version?.ToString() ?? string.Empty,
                    ConfigCount = info.Instance?.Config is null ? 0 : Entries(info).Count,
                    Tools = tools.Count == 0 ? null : tools.ToArray(),
                });
            }
            return list.ToArray();
        }

        private static Dictionary<string, string> _ids;
        private static int _idsCount = -1;

        /// <summary>插件 GUID → 工具名片段；与 Extensions() 使用同一套规则。</summary>
        internal static string IdFor(PluginInfo info)
        {
            var plugins = Plugins();
            if (_ids is null || _idsCount != plugins.Count)
            {
                _ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _idsCount = plugins.Count;
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var plugin in plugins)
                {
                    var guid = plugin.Metadata.GUID ?? string.Empty;
                    _ids[guid] = UniqueId(Slug(guid), used);
                }
            }
            var wanted = info.Metadata.GUID ?? string.Empty;
            return _ids.TryGetValue(wanted, out var id) ? id : Slug(wanted);
        }

        /// <summary>工具名片段：只留小写字母/数字/下划线，最长 20，避免不同 GUID 撞名。</summary>
        internal static string Slug(string guid)
        {
            var sb = new StringBuilder();
            foreach (var c in (guid ?? string.Empty).ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                if (sb.Length >= 20) break;
            }
            var slug = sb.ToString().Trim('_');
            return slug.Length == 0 ? "mod" : slug;
        }

        private static string UniqueId(string slug, HashSet<string> used)
        {
            var candidate = slug;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                var tail = $"_{suffix++}";
                var head = slug.Length + tail.Length > 24
                    ? slug.Substring(0, 24 - tail.Length)
                    : slug;
                candidate = head + tail;
            }
            return candidate;
        }

        internal static string AssemblyName(PluginInfo info)
        {
            try
            {
                var name = info?.Instance?.GetType().Assembly.GetName().Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch
            {
                // 插件实例可能未初始化；回退到文件位置。
            }
            var location = info?.Location;
            return string.IsNullOrEmpty(location) ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(location);
        }

        internal static JsonValue EntryJson(ConfigEntryBase entry)
        {
            var obj = JsonValue.NewObject();
            obj.Set("section", entry.Definition.Section ?? string.Empty);
            obj.Set("key", entry.Definition.Key ?? string.Empty);
            obj.Set("type", TypeName(entry.SettingType));
            obj.Set("value", ValueJson(entry.BoxedValue));
            obj.Set("default", ValueJson(entry.DefaultValue));
            obj.SetIfNotNull("description", entry.Description.Description);
            return obj;
        }
    }

    internal sealed class ModsListTool : IGameToolHandler
    {
        public string Name => Protocol.ToolModsList;
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new OneShotJob(Build);

        private static GameToolJobResult Build()
        {
            ModBridge.RequireEnabled();
            var mods = JsonValue.NewArray();
            foreach (var info in ModBridge.Plugins())
            {
                var item = JsonValue.NewObject();
                item.Set("guid", info.Metadata.GUID ?? string.Empty);
                item.Set("name", info.Metadata.Name ?? string.Empty);
                item.Set("version", info.Metadata.Version?.ToString() ?? string.Empty);
                item.Set("assembly", ModBridge.AssemblyName(info));
                item.Set("configCount", info.Instance?.Config is null ? 0 : ModBridge.Entries(info).Count);
                item.Set("loadFailed", info.Instance is null);
                mods.Add(item);
            }

            var data = JsonValue.NewObject();
            data.Set("mods", mods);
            data.Set("count", mods.Count);
            data.Set("display", $"已加载 {mods.Count} 个第三方插件");
            return new GameToolJobResult { Data = data };
        }
    }

    /// <summary>
    /// 只读列出插件程序集里可被 manifest 调用的公开静态方法，供 AI 起草适配器。
    /// 只读元数据，不执行任何第三方代码；不写文件。
    /// </summary>
    internal sealed class ModsApiListTool : IGameToolHandler
    {
        private const int MaxMethods = 200;

        public string Name => Protocol.ToolModsApiList;
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new OneShotJob(() => Build(args));

        private static GameToolJobResult Build(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var guid = args["guid"].AsString(null);
            var info = ModBridge.Find(guid)
                ?? throw new InvalidOperationException($"找不到插件：{guid ?? "(空)"}");
            var assembly = info.Instance?.GetType().Assembly
                ?? throw new InvalidOperationException($"插件未成功加载：{info.Metadata.GUID}");

            var methods = JsonValue.NewArray();
            var count = 0;
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsPublic && !type.IsNestedPublic) continue;
                foreach (var method in type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (method.IsSpecialName || method.IsGenericMethodDefinition) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length > 6) continue;
                    var simple = true;
                    foreach (var parameter in parameters)
                        if (!IsSimple(parameter.ParameterType)) { simple = false; break; }
                    if (!simple) continue;

                    var item = JsonValue.NewObject();
                    item.Set("type", type.FullName ?? type.Name);
                    item.Set("method", method.Name);
                    var list = JsonValue.NewArray();
                    foreach (var parameter in parameters)
                        list.Add($"{parameter.ParameterType.Name} {parameter.Name}");
                    item.Set("parameters", list);
                    item.Set("returns", method.ReturnType.Name);
                    methods.Add(item);
                    if (++count >= MaxMethods) break;
                }
                if (count >= MaxMethods) break;
            }

            var data = JsonValue.NewObject();
            data.Set("guid", info.Metadata.GUID ?? string.Empty);
            data.Set("methods", methods);
            data.Set("count", count);
            data.Set("truncated", count >= MaxMethods);
            data.Set("display", $"列出 {count} 个可调用方法" + (count >= MaxMethods ? "（已截断）" : string.Empty));
            return new GameToolJobResult { Data = data, Truncated = count >= MaxMethods };
        }

        private static bool IsSimple(Type type)
        {
            return type == typeof(string) || type == typeof(bool) ||
                   type == typeof(int) || type == typeof(long) ||
                   type == typeof(float) || type == typeof(double) || type.IsEnum;
        }
    }

    internal sealed class ModsConfigListTool : IGameToolHandler
    {
        private const int DefaultLimit = 50;
        private const int MaxLimit = 200;

        public string Name => Protocol.ToolModsConfigList;
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new OneShotJob(() => Build(args));

        private static GameToolJobResult Build(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var guid = args["guid"].AsString(null);
            var info = ModBridge.Find(guid)
                ?? throw new InvalidOperationException($"找不到插件：{guid ?? "(空)"}");
            if (info.Instance is null)
                throw new InvalidOperationException($"插件未成功加载：{info.Metadata.GUID}");

            var section = args["section"].AsString(null);
            var offset = Math.Max(0, args["offset"].AsInt(0));
            var limit = Math.Clamp(args["limit"].AsInt(DefaultLimit), 1, MaxLimit);

            var all = ModBridge.Entries(info);
            var entries = JsonValue.NewArray();
            var matched = 0;
            var shown = 0;
            foreach (var entry in all)
            {
                if (section is not null &&
                    !string.Equals(entry.Definition.Section, section.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (matched++ < offset) continue;
                if (shown >= limit) continue;
                entries.Add(ModBridge.EntryJson(entry));
                shown++;
            }

            var data = JsonValue.NewObject();
            data.Set("guid", info.Metadata.GUID ?? string.Empty);
            data.Set("name", info.Metadata.Name ?? string.Empty);
            data.Set("entries", entries);
            data.Set("total", matched);
            data.Set("offset", offset);
            data.Set("limit", limit);
            data.Set("display", $"{info.Metadata.Name}：{matched} 个配置项（显示 {offset + 1}–{offset + shown}）");
            if (matched > offset + shown) data.Set("truncated", true);
            return new GameToolJobResult { Data = data, Truncated = matched > offset + shown };
        }
    }

    internal sealed class ModsConfigGetTool : IGameToolHandler
    {
        public string Name => Protocol.ToolModsConfigGet;
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new OneShotJob(() => Build(args));

        private static GameToolJobResult Build(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var guid = args["guid"].AsString(null);
            var section = args["section"].AsString(null);
            var key = args["key"].AsString(null);
            var info = ModBridge.Find(guid)
                ?? throw new InvalidOperationException($"找不到插件：{guid ?? "(空)"}");
            var entry = ModBridge.FindEntry(info, section, key)
                ?? throw new InvalidOperationException($"找不到配置项：{section ?? "*"}/{key ?? "(空)"}");

            var data = ModBridge.EntryJson(entry);
            data.Set("guid", info.Metadata.GUID ?? string.Empty);
            data.Set("display", $"{info.Metadata.Name}：{entry.Definition.Section}/{entry.Definition.Key} = {ModBridge.Text(entry.BoxedValue)}");
            return new GameToolJobResult { Data = data };
        }
    }

    internal sealed class ModsConfigSetTool : WriteToolBase
    {
        public override string Name => Protocol.ToolModsConfigSet;
        public override int TimeoutMs => 8000;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var guid = args["guid"].AsString(null);
            var section = args["section"].AsString(null);
            var key = args["key"].AsString(null);
            var info = ModBridge.Find(guid)
                ?? throw Fail($"找不到插件：{guid ?? "(空)"}");
            if (info.Instance is null)
                throw Fail($"插件未成功加载：{info.Metadata.GUID}");
            var entry = ModBridge.FindEntry(info, section, key)
                ?? throw Fail($"找不到配置项：{section ?? "*"}/{key ?? "(空)"}");

            var oldValue = entry.BoxedValue;
            var oldText = ModBridge.Text(oldValue);
            if (!args.Has("value") || args["value"].IsNull)
                throw Fail("value 不能为空");
            var serialized = ModBridge.InputText(args["value"]);
            if (string.Equals(serialized, oldText, StringComparison.Ordinal))
            {
                var unchangedText = $"{info.Metadata.Name}：{entry.Definition.Key} 已经是 {oldText}，未写入";
                var same = JsonValue.NewObject();
                same.Set("guid", info.Metadata.GUID ?? string.Empty);
                same.Set("section", entry.Definition.Section ?? string.Empty);
                same.Set("key", entry.Definition.Key ?? string.Empty);
                same.Set("old", ModBridge.ValueJson(oldValue));
                same.Set("value", ModBridge.ValueJson(entry.BoxedValue));
                same.Set("changed", false);
                same.Set("display", unchangedText);
                return Ok(unchangedText, same);
            }

            try
            {
                entry.SetSerializedValue(serialized);
            }
            catch (Exception ex)
            {
                throw Fail($"值 {serialized} 无法按 {ModBridge.TypeName(entry.SettingType)} 解析：{ex.Message}");
            }

            try
            {
                info.Instance.Config?.Save();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[mods] 保存 {info.Metadata.GUID} 配置失败：{ex.Message}");
            }

            var displayName = info.Metadata.Name ?? info.Metadata.GUID ?? "mod";
            var label = $"{displayName}：{entry.Definition.Section}/{entry.Definition.Key}";
            WriteActionStack.Push($"恢复 {label}", () =>
            {
                entry.SetSerializedValue(oldText);
                info.Instance?.Config?.Save();
            });

            var data = JsonValue.NewObject();
            data.Set("guid", info.Metadata.GUID ?? string.Empty);
            data.Set("section", entry.Definition.Section ?? string.Empty);
            data.Set("key", entry.Definition.Key ?? string.Empty);
            data.Set("old", ModBridge.ValueJson(oldValue));
            data.Set("value", ModBridge.ValueJson(entry.BoxedValue));
            data.Set("changed", true);
            var displayText = $"{label}：{oldText} → {ModBridge.Text(entry.BoxedValue)}（可能需要重启生效）";
            data.Set("display", displayText);
            return Ok(displayText, data);
        }
    }
}
