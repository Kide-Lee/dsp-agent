using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using IcarusII.Common.Json;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 给 AI 起草适配器时补充语义线索。
    ///
    /// 第三方 mod 的公开方法名通常不足以说明“这个功能是干什么的”，
    /// 真正的说明往往在它自己的窗口文字或配置项描述里。这里只读收集这两类文本：
    /// - 归属该插件程序集的 Canvas 下的 Text；
    /// - 配置项的 Description。
    /// 不做任何调用，也不改 UI。
    /// </summary>
    internal static class ModInsight
    {
        internal const int MaxTexts = 60;
        internal const int MaxHints = 40;
        private const int MaxChildScan = 64;
        private const int MaxTextLength = 120;

        internal static JsonValue UiTexts(PluginInfo info, int limit = MaxTexts)
        {
            var result = JsonValue.NewArray();
            var assembly = info?.Instance?.GetType().Assembly;
            if (assembly is null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<int>();

            // 1) 优先从该插件自己的 MonoBehaviour 附近取文字：
            //    窗口根/子节点上通常挂着作者的脚本，向上两级就能覆盖整个窗口。
            foreach (var behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (behaviour is null || behaviour.GetType().Assembly != assembly) continue;
                var node = behaviour.transform;
                for (var level = 0; level < 3 && node is not null; level++)
                {
                    if (visited.Add(node.GetInstanceID())) Collect(node, seen, result, limit);
                    if (result.Count >= limit) return result;
                    node = node.parent;
                }
            }

            // 2) 再按 Canvas 名字/归属兜底（有些 mod 的 UI 没有挂自己的脚本）。
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (canvas is null || !Belongs(canvas, info, assembly)) continue;
                if (visited.Add(canvas.transform.GetInstanceID()))
                    Collect(canvas.transform, seen, result, limit);
                if (result.Count >= limit) return result;
            }
            return result;
        }

        private static void Collect(Transform root, HashSet<string> seen, JsonValue result, int limit)
        {
            foreach (var text in root.GetComponentsInChildren<Text>(true))
            {
                if (text is null) continue;
                var value = (text.text ?? string.Empty).Trim();
                if (value.Length < 2 || value.Length > MaxTextLength) continue;
                if (!seen.Add(value)) continue;
                result.Add(value);
                if (result.Count >= limit) return;
            }
        }

        internal static JsonValue ConfigHints(PluginInfo info, int limit = MaxHints)
        {
            var result = JsonValue.NewArray();
            if (info?.Instance?.Config is null) return result;
            foreach (var entry in ModBridge.Entries(info))
            {
                var description = entry.Description?.Description;
                if (string.IsNullOrWhiteSpace(description)) continue;
                var item = JsonValue.NewObject();
                item.Set("section", entry.Definition.Section ?? string.Empty);
                item.Set("key", entry.Definition.Key ?? string.Empty);
                item.Set("description", description.Trim());
                result.Add(item);
                if (result.Count >= limit) break;
            }
            return result;
        }

        private static bool Belongs(Canvas canvas, PluginInfo info, Assembly assembly)
        {
            var hints = Hints(info, assembly);
            if (Matches(canvas.name, hints)) return true;
            var root = canvas.transform;
            var children = Math.Min(root.childCount, MaxChildScan);
            for (var i = 0; i < children; i++)
                if (Matches(root.GetChild(i).name, hints)) return true;

            if (HasAssembly(canvas.gameObject, assembly)) return true;
            for (var i = 0; i < children; i++)
                if (HasAssembly(root.GetChild(i).gameObject, assembly)) return true;
            return false;
        }

        private static List<string> Hints(PluginInfo info, Assembly assembly)
        {
            var hints = new List<string>();
            AddHint(hints, info?.Metadata?.Name);
            AddHint(hints, info?.Metadata?.GUID);
            AddHint(hints, assembly.GetName().Name);
            AddHint(hints, info?.Location is null ? null : System.IO.Path.GetFileNameWithoutExtension(info.Location));
            return hints;
        }

        private static void AddHint(List<string> hints, string value)
        {
            var normalized = Normalize(value);
            if (normalized.Length >= 3 && !hints.Contains(normalized)) hints.Add(normalized);
        }

        private static bool Matches(string name, List<string> hints)
        {
            var normalized = Normalize(name);
            if (normalized.Length < 3) return false;
            foreach (var hint in hints)
                if (normalized.Contains(hint) || hint.Contains(normalized)) return true;
            return false;
        }

        private static bool HasAssembly(GameObject go, Assembly assembly)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component is null) continue;
                if (component.GetType().Assembly == assembly) return true;
            }
            return false;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value.ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            return sb.ToString();
        }
    }
}
