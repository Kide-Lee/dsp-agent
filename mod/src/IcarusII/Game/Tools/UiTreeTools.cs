using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// uGUI 控件树（P3）：按层级路径列控件、按路径派发一次合成 PointerClick。
    /// 不做像素坐标，也不做 OS 输入；不受分辨率/语言影响。
    /// </summary>
    internal static class UiTree
    {
        private const int MaxNodes = 4000;
        private const int MaxDepth = 14;
        private const int MaxResults = 150;

        internal static List<JsonValue> Snapshot(string filter, bool includeHidden)
        {
            var results = new List<JsonValue>();
            var visited = 0;
            var lower = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim().ToLowerInvariant();
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                if (canvas.renderMode == RenderMode.WorldSpace) continue;
                Walk(canvas.transform, "/" + canvas.name, 0, lower, includeHidden, results, ref visited);
                if (results.Count >= MaxResults || visited >= MaxNodes) break;
            }
            return results;
        }

        private static void Walk(Transform node, string path, int depth, string filter,
            bool includeHidden, List<JsonValue> results, ref int visited)
        {
            if (node == null || depth > MaxDepth || results.Count >= MaxResults || visited >= MaxNodes)
                return;
            visited++;

            var go = node.gameObject;
            var visible = includeHidden || go.activeInHierarchy;
            var selectable = go.GetComponent<Selectable>();
            if (selectable != null && visible)
            {
                var label = Label(go);
                if (filter is null || path.ToLowerInvariant().Contains(filter) ||
                    (label is not null && label.ToLowerInvariant().Contains(filter)))
                {
                    results.Add(Describe(go, selectable, path, label));
                }
            }

            var count = node.childCount;
            for (var i = 0; i < count; i++)
            {
                var child = node.GetChild(i);
                Walk(child, path + "/" + child.name + "#" + i, depth + 1, filter, includeHidden, results, ref visited);
            }
        }

        private static JsonValue Describe(GameObject go, Selectable selectable, string path, string label)
        {
            var item = JsonValue.NewObject();
            item.Set("path", path);
            item.Set("type", selectable.GetType().Name);
            item.Set("label", label ?? go.name);
            item.Set("interactable", selectable.IsInteractable());
            item.Set("active", go.activeInHierarchy);
            var toggle = go.GetComponent<Toggle>();
            if (toggle != null) item.Set("value", toggle.isOn);
            var input = go.GetComponent<InputField>();
            if (input != null) item.Set("text", input.text ?? string.Empty);
            return item;
        }

        /// <summary>控件上显示的文字：优先 uGUI Text，其次 GameObject 名。</summary>
        internal static string Label(GameObject go)
        {
            var text = go.GetComponent<Text>();
            if (text != null && !string.IsNullOrEmpty(text.text)) return text.text;
            var childText = go.GetComponentInChildren<Text>(true);
            if (childText != null && !string.IsNullOrEmpty(childText.text)) return childText.text;
            return go.name;
        }

        /// <summary>按 `Name#index` 路径找控件；只接受画布下的活动节点。</summary>
        internal static GameObject Find(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var parts = path.Trim().Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var canvas = FindCanvas(parts[0]);
            if (canvas == null) return null;

            Transform current = canvas.transform;
            for (var i = 1; i < parts.Length; i++)
            {
                if (!TryParseSegment(parts[i], out var name, out var index)) return null;
                if (index < 0 || index >= current.childCount) return null;
                var child = current.GetChild(index);
                if (child == null || !string.Equals(child.name, name, StringComparison.Ordinal)) return null;
                current = child;
            }
            return current.gameObject;
        }

        private static Canvas FindCanvas(string name)
        {
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas != null && string.Equals(canvas.name, name, StringComparison.Ordinal)) return canvas;
            }
            return null;
        }

        private static bool TryParseSegment(string segment, out string name, out int index)
        {
            name = segment;
            index = -1;
            var hash = segment.LastIndexOf('#');
            if (hash <= 0 || hash == segment.Length - 1) return false;
            name = segment.Substring(0, hash);
            return int.TryParse(segment.Substring(hash + 1), out index);
        }

        internal static void Click(GameObject go)
        {
            var selectable = go.GetComponent<Selectable>();
            if (selectable is null) throw new InvalidOperationException("目标不是可交互控件");
            if (!go.activeInHierarchy) throw new InvalidOperationException("目标不可见");
            if (!selectable.IsInteractable()) throw new InvalidOperationException("目标已被禁用");

            var rect = go.transform as RectTransform;
            var canvas = go.GetComponentInParent<Canvas>();
            if (rect is null || canvas is null) throw new InvalidOperationException("目标缺少 RectTransform/Canvas");

            var screen = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, rect.TransformPoint(rect.rect.center));
            var eventSystem = EventSystem.current;
            if (eventSystem is null) throw new InvalidOperationException("EventSystem 未就绪");
            var data = new PointerEventData(eventSystem)
            {
                position = screen,
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                pointerId = -1,
            };
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerClickHandler);
        }
    }

    internal sealed class UiSnapshotTool : IGameToolHandler
    {
        public string Name => Protocol.ToolUiSnapshot;
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new OneShotJob(() => Build(args));

        private static GameToolJobResult Build(JsonValue args)
        {
            var filter = args["filter"].AsString(null);
            var includeHidden = args["includeHidden"].AsBool(false);
            var items = UiTree.Snapshot(filter, includeHidden);
            var data = JsonValue.NewObject();
            var array = JsonValue.NewArray();
            foreach (var item in items) array.Add(item);
            data.Set("controls", array);
            data.Set("count", items.Count);
            data.Set("display", $"列出 {items.Count} 个可交互控件" +
                (string.IsNullOrEmpty(filter) ? string.Empty : $"（过滤：{filter}）"));
            return new GameToolJobResult { Data = data };
        }
    }

    internal sealed class UiClickTool : WriteToolBase
    {
        public override string Name => Protocol.ToolUiClick;
        public override int TimeoutMs => 5000;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            var path = args["path"].AsString(null);
            if (string.IsNullOrWhiteSpace(path)) throw Fail("path 不能为空");
            var go = UiTree.Find(path);
            if (go is null) throw Fail($"找不到控件：{path}");
            var label = UiTree.Label(go);
            UiTree.Click(go);

            // 语义未知、无法安全撤销：占住撤销栈顶，避免撤销到更早的无关操作。
            WriteActionStack.MarkNonUndoable($"点击 {label}");
            var data = JsonValue.NewObject();
            data.Set("path", path);
            data.Set("label", label);
            var display = $"已点击「{label}」；如需确认结果，再调用 ui_snapshot";
            data.Set("display", display);
            return Ok(display, data);
        }
    }
}
