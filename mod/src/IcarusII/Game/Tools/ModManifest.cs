using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

namespace IcarusII.Game.Tools
{
    /// <summary>manifest 里的一个调用：目标类型/方法 + 参数映射。</summary>
    internal sealed class ManifestCall
    {
        public string Type;
        public string Method;
        /// <summary>参数映射数组：字符串 = 取同名工具参数；{"const": x} = 字面量；{"default": x} = 缺省值。</summary>
        public JsonValue Args;
    }

    /// <summary>manifest 声明的一个工具（Name 是短名，加载时补上 `mod_&lt;id&gt;_` 前缀）。</summary>
    internal sealed class ManifestTool
    {
        public string Name;
        public string Label;
        public string Description;
        public int Risk;
        public int TimeoutMs = 8000;
        public JsonValue Parameters;
        public ManifestCall Invoke;
        public ManifestCall Undo;

        internal ExtensionToolSummary ToSummary()
        {
            return new ExtensionToolSummary
            {
                Name = Name,
                Label = Label,
                Description = Description,
                Risk = Risk,
                TimeoutMs = TimeoutMs,
                Parameters = Parameters ?? JsonValue.NewObject(),
            };
        }
    }

    internal sealed class Manifest
    {
        public string Guid;
        public string Assembly;
        public string VersionRange;
        public List<ManifestTool> Tools = new List<ManifestTool>();
    }

    /// <summary>
    /// 第三方 mod manifest（P2）：声明「把哪个公开静态方法暴露成工具」。
    ///
    /// 约束：只调用声明插件自己程序集里的公开静态方法；参数类型限基本类型/字符串/枚举；
    /// 版本不匹配整份禁用；risk 2 必须声明 undo，否则不注册。
    /// </summary>
    internal static class ModManifest
    {
        private const int SchemaVersion = 1;

        private static readonly Dictionary<string, Manifest> ByGuid =
            new Dictionary<string, Manifest>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ManifestTool> ByToolName =
            new Dictionary<string, ManifestTool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PluginInfo> ByToolPlugin =
            new Dictionary<string, PluginInfo>(StringComparer.Ordinal);
        private static bool _loaded;

        /// <summary>插件加载/卸载后调用；下次访问时重建。</summary>
        internal static void Reset() => _loaded = false;

        internal static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            ByGuid.Clear();
            ByToolName.Clear();
            ByToolPlugin.Clear();
            if (!ModBridge.Enabled) return;

            foreach (var info in ModBridge.Plugins())
            {
                if (info.Instance is null) continue;
                Manifest manifest;
                try
                {
                    manifest = LoadExternal(info) ?? ReferenceAdapters.For(info);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[mods] manifest 加载失败：{info.Metadata.GUID}：{ex.Message}");
                    continue;
                }
                if (manifest is null) continue;
                if (!VersionMatches(info, manifest.VersionRange))
                {
                    Plugin.Log?.LogWarning(
                        $"[mods] manifest 版本不匹配，禁用：{info.Metadata.GUID} 需要 {manifest.VersionRange}");
                    continue;
                }

                var id = ModBridge.IdFor(info);
                var accepted = new List<ManifestTool>();
                foreach (var tool in manifest.Tools)
                {
                    if (tool is null || string.IsNullOrEmpty(tool.Name)) continue;
                    if (tool.Risk >= 2 && tool.Undo is null)
                    {
                        Plugin.Log?.LogWarning($"[mods] risk 2 工具缺少 undo，跳过：{tool.Name}");
                        continue;
                    }
                    tool.Name = $"mod_{id}_{ModBridge.Slug(tool.Name)}";
                    if (ByToolName.ContainsKey(tool.Name)) continue;
                    accepted.Add(tool);
                }
                if (accepted.Count == 0) continue;
                manifest.Tools = accepted;
                ByGuid[manifest.Guid ?? info.Metadata.GUID] = manifest;
                foreach (var tool in accepted)
                {
                    ByToolName[tool.Name] = tool;
                    ByToolPlugin[tool.Name] = info;
                }
            }
            if (ByToolName.Count > 0)
                Plugin.Log?.LogInfo($"[mods] 已加载 {ByToolName.Count} 个 manifest 工具");
        }

        internal static IReadOnlyList<ManifestTool> ToolsFor(string guid)
        {
            EnsureLoaded();
            return ByGuid.TryGetValue(guid, out var manifest)
                ? (IReadOnlyList<ManifestTool>)manifest.Tools
                : Array.Empty<ManifestTool>();
        }

        internal static bool TryResolve(string toolName, out PluginInfo plugin, out ManifestTool tool)
        {
            plugin = null;
            tool = null;
            if (!ModBridge.Enabled) return false;
            EnsureLoaded();
            if (string.IsNullOrEmpty(toolName)) return false;
            return ByToolName.TryGetValue(toolName, out tool)
                && ByToolPlugin.TryGetValue(toolName, out plugin);
        }

        /// <summary>Protocol.ExtraWriteTool 的钩子：risk &gt; 0 的 manifest 工具需要确认令牌。</summary>
        internal static bool IsWrite(string toolName)
        {
            return TryResolve(toolName, out _, out var tool) && tool.Risk > 0;
        }

        // ---------------------------------------------------------------- 加载

        private static Manifest LoadExternal(PluginInfo info)
        {
            foreach (var path in ExternalPaths(info))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var manifest = Parse(File.ReadAllText(path));
                    if (manifest is null)
                    {
                        Plugin.Log?.LogWarning($"[mods] manifest 解析失败：{path}");
                        continue;
                    }
                    if (!Matches(info, manifest))
                    {
                        Plugin.Log?.LogWarning($"[mods] manifest 与插件不匹配：{path}");
                        continue;
                    }
                    return manifest;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[mods] 读取 manifest 失败：{path}：{ex.Message}");
                }
            }
            return null;
        }

        private static IEnumerable<string> ExternalPaths(PluginInfo info)
        {
            var configRoot = Path.Combine(Paths.ConfigPath ?? ".", "icarus-ii", "bridge");
            var guid = info.Metadata.GUID ?? string.Empty;
            yield return Path.Combine(configRoot, guid + ".json");
            var location = info.Location;
            var dir = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, "IcarusII.tools.json");
        }

        private static bool Matches(PluginInfo info, Manifest manifest)
        {
            if (!string.IsNullOrEmpty(manifest.Guid) &&
                string.Equals(manifest.Guid, info.Metadata.GUID, StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(manifest.Assembly) &&
                   string.Equals(manifest.Assembly, ModBridge.AssemblyName(info), StringComparison.OrdinalIgnoreCase);
        }

        internal static Manifest Parse(string text)
        {
            var root = JsonValue.Parse(text);
            if (root is null || !root.IsObject) return null;
            if (root["schema"].AsInt(0) != SchemaVersion) return null;

            var manifest = new Manifest
            {
                Guid = root["pluginGuid"].AsString(null),
                Assembly = root["pluginAssembly"].AsString(null),
                VersionRange = root["pluginVersion"].AsString(null),
            };
            foreach (var item in root["tools"].Items)
            {
                if (!item.IsObject) continue;
                var tool = new ManifestTool
                {
                    Name = item["name"].AsString(null),
                    Label = item["title"].AsString(item["name"].AsString(null)),
                    Description = item["description"].AsString(string.Empty),
                    Risk = Math.Max(0, Math.Min(2, item["risk"].AsInt(0))),
                    TimeoutMs = item["timeoutMs"].AsInt(8000),
                    Parameters = item["parameters"],
                    Invoke = ParseCall(item["invoke"]),
                    Undo = ParseCall(item["undo"]),
                };
                if (!string.IsNullOrEmpty(tool.Name) && tool.Invoke is not null)
                    manifest.Tools.Add(tool);
            }
            return manifest;
        }

        private static ManifestCall ParseCall(JsonValue node)
        {
            if (node is null || !node.IsObject) return null;
            var call = new ManifestCall
            {
                Type = node["type"].AsString(null),
                Method = node["method"].AsString(null),
                Args = node["args"],
            };
            return string.IsNullOrEmpty(call.Type) || string.IsNullOrEmpty(call.Method) ? null : call;
        }

        // ---------------------------------------------------------------- 版本

        internal static bool VersionMatches(PluginInfo info, string range)
        {
            if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*") return true;
            var current = info.Metadata.Version;
            if (current is null) return false;
            foreach (var clause in range.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Satisfies(current, clause.Trim())) return false;
            }
            return true;
        }

        private static bool Satisfies(System.Version current, string clause)
        {
            var op = ">=";
            foreach (var candidate in new[] { ">=", "<=", "==", ">", "<", "=" })
            {
                if (!clause.StartsWith(candidate, StringComparison.Ordinal)) continue;
                op = candidate;
                clause = clause.Substring(candidate.Length);
                break;
            }
            if (!System.Version.TryParse(clause.Trim(), out var wanted)) return false;
            var cmp = current.CompareTo(wanted);
            return op switch
            {
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                "<" => cmp < 0,
                _ => cmp == 0,
            };
        }

        // ---------------------------------------------------------------- 调用

        /// <summary>同步调用 manifest 目标；只在主线程执行。</summary>
        internal static JsonValue Invoke(PluginInfo info, ManifestCall call, JsonValue toolArgs)
        {
            var assembly = info.Instance?.GetType().Assembly;
            if (assembly is null) throw new InvalidOperationException("插件未成功加载");
            var type = assembly.GetType(call.Type, false);
            if (type is null) throw new InvalidOperationException($"程序集里找不到类型：{call.Type}");

            var args = BuildArguments(toolArgs, call.Args);
            MethodInfo method = null;
            foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(candidate.Name, call.Method, StringComparison.Ordinal)) continue;
                if (candidate.GetParameters().Length != args.Count) continue;
                method = candidate;
                break;
            }
            if (method is null)
                throw new InvalidOperationException($"找不到方法：{call.Type}.{call.Method}({args.Count} 参数)");

            var parameters = method.GetParameters();
            var final = new object[args.Count];
            for (var i = 0; i < args.Count; i++)
                final[i] = Convert(args[i], parameters[i].ParameterType);

            object result;
            try
            {
                result = method.Invoke(null, final);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new InvalidOperationException($"{call.Method} 抛出异常：{ex.InnerException.Message}");
            }
            return ToJson(result);
        }

        private static List<JsonValue> BuildArguments(JsonValue toolArgs, JsonValue mapping)
        {
            var list = new List<JsonValue>();
            if (mapping is null || !mapping.IsArray) return list;
            foreach (var item in mapping.Items)
            {
                if (item.IsString)
                {
                    list.Add(toolArgs[item.AsString(string.Empty)]);
                    continue;
                }
                if (!item.IsObject) { list.Add(JsonValue.Null); continue; }
                if (item.Has("const")) { list.Add(item["const"]); continue; }
                var name = item["arg"].AsString(null) ?? item["name"].AsString(null);
                if (name is not null) { list.Add(toolArgs[name]); continue; }
                if (item.Has("default")) { list.Add(item["default"]); continue; }
                list.Add(JsonValue.Null);
            }
            return list;
        }

        private static object Convert(JsonValue value, Type target)
        {
            if (target == typeof(string)) return value.AsString(string.Empty);
            if (target == typeof(bool)) return value.AsBool(false);
            if (target == typeof(int)) return (int)value.AsDouble(0);
            if (target == typeof(long)) return (long)value.AsDouble(0);
            if (target == typeof(float)) return (float)value.AsDouble(0);
            if (target == typeof(double)) return value.AsDouble(0);
            if (target.IsEnum) return Enum.Parse(target, value.AsString(string.Empty), true);
            throw new InvalidOperationException($"不支持的参数类型：{target.Name}");
        }

        private static JsonValue ToJson(object value)
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
                default: return JsonValue.Of(value.ToString() ?? string.Empty);
            }
        }
    }
}

namespace IcarusII.Game.Tools
{
    /// <summary>把一条 manifest 工具接到 dispatcher；只在桥启用时可用。</summary>
    internal sealed class ManifestToolHandler : IGameToolHandler
    {
        private readonly string _name;
        private readonly BepInEx.PluginInfo _plugin;
        private readonly ManifestTool _tool;

        internal ManifestToolHandler(string name, BepInEx.PluginInfo plugin, ManifestTool tool)
        {
            _name = name;
            _plugin = plugin;
            _tool = tool;
        }

        public string Name => _name;
        public int TimeoutMs => _tool.TimeoutMs > 0 ? _tool.TimeoutMs : 8000;

        public IGameToolJob Start(JsonValue args) => new OneShotJob(() => Run(args));

        private GameToolJobResult Run(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var result = ModManifest.Invoke(_plugin, _tool.Invoke, args);
            var data = JsonValue.NewObject();
            data.Set("display", $"{_tool.Label}：已执行");
            data.Set("result", result);

            // 写操作必须占据撤销栈顶；没有 undo 的写操作标记为不可撤销，
            // 否则 game_undo_last_write 会跳过它去撤销更早的无关操作。
            if (_tool.Risk > 0)
            {
                if (_tool.Undo is null)
                    WriteActionStack.MarkNonUndoable(_tool.Label);
                else
                    WriteActionStack.Push($"撤销 {_tool.Label}",
                        () => ModManifest.Invoke(_plugin, _tool.Undo, args));
            }
            return new GameToolJobResult { Data = data };
        }
    }
}
