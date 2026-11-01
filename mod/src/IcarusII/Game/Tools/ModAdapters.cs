using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// AI 生成适配器：把模型给出的结构化绑定校验后写成
    /// `BepInEx/config/icarus-ii/bridge/&lt;guid&gt;.json`，并触发热载。
    ///
    /// 只接受结构化字段；不接受模型写自由文本文件。
    /// AI 生成的能力一律至少 risk 1，risk 2 必须声明 undo。
    /// </summary>
    internal static class ModAdapters
    {
        internal const int MaxToolsPerAdapter = 12;
        internal const int MinAiRisk = 1;
        private const int MinTimeoutMs = 500;
        private const int MaxTimeoutMs = 30000;

        internal static string KindName(AdapterKind kind)
        {
            switch (kind)
            {
                case AdapterKind.Builtin: return "builtin";
                case AdapterKind.Bundled: return "bundled";
                case AdapterKind.External: return "external";
                case AdapterKind.Ai: return "ai";
                case AdapterKind.VersionMismatch: return "mismatch";
                case AdapterKind.Invalid: return "invalid";
                case AdapterKind.Unavailable: return "unavailable";
                default: return "none";
            }
        }

        /// <summary>创建适配器；返回创建出的完整工具名。</summary>
        internal static List<string> Create(PluginInfo info, JsonValue tools, bool replace)
        {
            var guid = info.Metadata.GUID ?? string.Empty;
            if (tools is null || !tools.IsArray || tools.Count == 0)
                throw new InvalidOperationException("tools 必须是非空数组");
            if (tools.Count > MaxToolsPerAdapter)
                throw new InvalidOperationException($"一次最多创建 {MaxToolsPerAdapter} 个工具");

            var id = ModBridge.IdFor(info);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var existing in ModManifest.ToolsFor(guid))
                used.Add(existing.Name);

            var normalized = JsonValue.NewArray();
            var names = new List<string>();
            foreach (var item in tools.Items)
            {
                var tool = NormalizeTool(info, item, id, used);
                normalized.Add(tool);
                names.Add(tool["name"].AsString(string.Empty));
            }

            var path = ModManifest.ExternalPathFor(guid);
            if (path is null) throw new InvalidOperationException("无法确定适配器路径");
            var exists = File.Exists(path);
            if (exists && !replace)
                throw new InvalidOperationException("该 mod 已有手写适配器；要覆盖请传 replace=true");

            string previous = null;
            if (exists)
            {
                try { previous = File.ReadAllText(path); }
                catch (Exception ex) { throw new InvalidOperationException($"读取旧适配器失败：{ex.Message}"); }
            }

            var root = JsonValue.NewObject();
            root.Set("schema", 1);
            root.Set("pluginGuid", guid);
            var version = info.Metadata.Version;
            if (version is not null) root.Set("pluginVersion", "==" + version);
            root.Set("source", "ai");
            root.Set("tools", normalized);

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToJson());
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"写入适配器失败：{ex.Message}");
            }

            WriteActionStack.Push($"删除 AI 生成的适配器（{guid}）", () =>
            {
                if (previous is null)
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
                else
                {
                    try { File.WriteAllText(path, previous); } catch { /* ignore */ }
                }
                ModManifest.Reset();
                Plugin.Instance?.NotifyExtensionsChanged();
            });

            ModManifest.Reset();
            Plugin.Instance?.NotifyExtensionsChanged();
            return names;
        }

        /// <summary>删除 config 目录下的适配器；随包/内置适配器不受影响。</summary>
        internal static void Delete(PluginInfo info)
        {
            var guid = info.Metadata.GUID ?? string.Empty;
            var path = ModManifest.ExternalPathFor(guid);
            if (path is null || !File.Exists(path))
                throw new InvalidOperationException("这个 mod 没有可删除的适配器文件");

            string previous;
            try { previous = File.ReadAllText(path); }
            catch (Exception ex) { throw new InvalidOperationException($"读取适配器失败：{ex.Message}"); }

            try { File.Delete(path); }
            catch (Exception ex) { throw new InvalidOperationException($"删除适配器失败：{ex.Message}"); }

            WriteActionStack.Push($"恢复适配器（{guid}）", () =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, previous);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[mods] 恢复适配器失败：{ex.Message}");
                }
                ModManifest.Reset();
                Plugin.Instance?.NotifyExtensionsChanged();
            });

            ModManifest.Reset();
            Plugin.Instance?.NotifyExtensionsChanged();
        }

        private static JsonValue NormalizeTool(PluginInfo info, JsonValue item, string id, HashSet<string> used)
        {
            if (item is null || !item.IsObject)
                throw new InvalidOperationException("tools[] 的每一项必须是对象");

            var rawName = item["name"].AsString(null);
            var slug = ModBridge.Slug(rawName);
            if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(slug))
                throw new InvalidOperationException("工具缺少 name");
            var full = $"mod_{id}_{slug}";
            if (!used.Add(full))
                throw new InvalidOperationException($"工具名重复：{full}");

            var risk = item["risk"].AsInt(0);
            risk = Math.Max(MinAiRisk, Math.Min(2, risk));
            var timeout = item["timeoutMs"].AsInt(8000);
            timeout = Math.Max(MinTimeoutMs, Math.Min(MaxTimeoutMs, timeout));

            var tool = JsonValue.NewObject();
            tool.Set("name", slug);
            tool.Set("title", item["title"].AsString(slug));
            tool.Set("description", item["description"].AsString(string.Empty));
            tool.Set("risk", risk);
            tool.Set("timeoutMs", timeout);
            tool.Set("parameters", NormalizeSchema(item["parameters"]));

            var invoke = NormalizeCall(info, item["invoke"], "invoke");
            tool.Set("invoke", invoke);

            var undoNode = item["undo"];
            if (undoNode is not null && undoNode.IsObject)
                tool.Set("undo", NormalizeCall(info, undoNode, "undo"));
            else if (risk >= 2)
                throw new InvalidOperationException($"risk 2 的工具必须声明 undo：{slug}");
            return tool;
        }

        private static JsonValue NormalizeSchema(JsonValue node)
        {
            var schema = ParameterSchema.Normalize(node, out var error);
            if (schema is null)
                throw new InvalidOperationException($"parameters schema 无效：{error}");
            return schema;
        }

        private static JsonValue NormalizeCall(PluginInfo info, JsonValue node, string label)
        {
            if (node is null || !node.IsObject)
                throw new InvalidOperationException($"{label} 必须是对象");
            var typeName = node["type"].AsString(null);
            var methodName = node["method"].AsString(null);
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                throw new InvalidOperationException($"{label} 缺少 type/method");

            var assembly = info.Instance?.GetType().Assembly;
            if (assembly is null) throw new InvalidOperationException("插件未成功加载");
            var type = assembly.GetType(typeName, false);
            if (type is null)
                throw new InvalidOperationException($"{label} 的类型不在该插件程序集里：{typeName}");

            var args = node["args"];
            var argCount = args is not null && args.IsArray ? args.Count : 0;
            var method = FindMethod(type, methodName, argCount);
            if (method is null)
                throw new InvalidOperationException($"{label} 找不到公开静态方法：{typeName}.{methodName}({argCount} 参数)");

            for (var i = 0; i < argCount; i++)
                RequireSimpleType(method.GetParameters()[i].ParameterType, $"{label} 参数 #{i + 1}");

            var mapping = JsonValue.NewArray();
            if (args is not null && args.IsArray)
            {
                foreach (var arg in args.Items)
                {
                    mapping.Add(NormalizeArg(arg, label));
                }
            }

            var call = JsonValue.NewObject();
            call.Set("type", typeName);
            call.Set("method", methodName);
            call.Set("args", mapping);
            return call;
        }

        private static MethodInfo FindMethod(Type type, string name, int argCount)
        {
            foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (candidate.IsSpecialName || candidate.IsGenericMethodDefinition) continue;
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
                if (candidate.GetParameters().Length != argCount) continue;
                return candidate;
            }
            return null;
        }

        private static void RequireSimpleType(Type type, string label)
        {
            if (type == typeof(string) || type == typeof(bool) ||
                type == typeof(int) || type == typeof(long) ||
                type == typeof(float) || type == typeof(double) || type.IsEnum) return;
            throw new InvalidOperationException($"{label} 的类型不受支持：{type.Name}");
        }

        private static JsonValue NormalizeArg(JsonValue arg, string label)
        {
            if (arg is null) throw new InvalidOperationException($"{label} 的参数映射不能为空");
            if (arg.IsString)
            {
                var name = arg.AsString(string.Empty);
                if (name.Length == 0) throw new InvalidOperationException($"{label} 的参数映射不能是空字符串");
                return JsonValue.Of(name);
            }
            if (!arg.IsObject) throw new InvalidOperationException($"{label} 的参数映射必须是字符串或对象");

            var result = JsonValue.NewObject();
            if (arg.Has("const")) { result.Set("const", arg["const"]); return result; }
            if (arg.Has("default")) { result.Set("default", arg["default"]); return result; }
            var refName = arg["arg"].AsString(null) ?? arg["name"].AsString(null);
            if (!string.IsNullOrEmpty(refName)) { result.Set("arg", refName); return result; }
            throw new InvalidOperationException($"{label} 的参数映射只支持 const/default/arg");
        }
    }

    /// <summary>AI 提议创建一个适配器；写入前玩家必须确认。</summary>
    internal sealed class ModsAdapterCreateTool : WriteToolBase
    {
        public override string Name => Protocol.ToolModsAdapterCreate;
        public override int TimeoutMs => 8000;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var guid = args["guid"].AsString(null);
            var info = ModBridge.Find(guid)
                ?? throw Fail($"找不到已启用的插件：{guid ?? "(空)"}");
            var replace = args["replace"].AsBool(false);
            var names = ModAdapters.Create(info, args["tools"], replace);

            var data = JsonValue.NewObject();
            data.Set("guid", info.Metadata.GUID ?? string.Empty);
            var list = JsonValue.NewArray();
            foreach (var name in names) list.Add(name);
            data.Set("tools", list);
            data.Set("count", names.Count);
            var display = $"已为 {info.Metadata.Name} 创建 {names.Count} 个工具；下一轮生效";
            data.Set("display", display);
            return Ok(display, data);
        }
    }

    /// <summary>删除 config 目录下的适配器文件；撤销会恢复原文件。</summary>
    internal sealed class ModsAdapterDeleteTool : WriteToolBase
    {
        public override string Name => Protocol.ToolModsAdapterDelete;
        public override int TimeoutMs => 5000;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            ModBridge.RequireEnabled();
            var guid = args["guid"].AsString(null);
            var info = ModBridge.FindAny(guid)
                ?? throw Fail($"找不到插件：{guid ?? "(空)"}");
            ModAdapters.Delete(info);
            var display = $"已删除 {info.Metadata.Name} 的适配器；撤销可恢复";
            var data = JsonValue.NewObject();
            data.Set("guid", info.Metadata.GUID ?? string.Empty);
            data.Set("display", display);
            return Ok(display, data);
        }
    }
}
