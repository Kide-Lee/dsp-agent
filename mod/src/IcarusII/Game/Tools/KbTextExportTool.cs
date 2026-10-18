using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using IcarusII.Common.Json;
using IcarusII.Game;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// M3b：把游戏自带的提示/教程/里程碑/指引/成就/术语表导出给 sidecar。
    ///
    /// 文本来源有三处：
    ///  - LDB 原型（已经过 Translate/Preload，拿的是当前语言）；
    ///  - Wiki/Tutorial/tutorial-*-<lang>.txt（教程正文，不在 Locale 里）；
    ///  - Locale/&lt;lcid&gt;/dictionary.txt（术语表）。
    ///
    /// 只读；与 game_export_prototypes 一样支持 offset/limit，按主线程预算分帧。
    /// </summary>
    internal sealed class ExportTextTool : IGameToolHandler
    {
        public string Name => "game_export_text";
        public int TimeoutMs => 15000;

        private const int MaxLimit = 300;

        private sealed class KindRange
        {
            public string Kind;
            public string Source;
            public int Count;
        }

        public IGameToolJob Start(JsonValue args)
        {
            args = args != null && args.IsObject ? args : JsonValue.NewObject();
            var ranges = Ranges();
            var total = 0;
            foreach (var range in ranges) total += range.Count;

            var offset = Math.Max(0, args["offset"].AsInt(0));
            if (offset > total) offset = total;
            var limit = args["limit"].AsInt(200);
            if (limit <= 0) limit = 200;
            if (limit > MaxLimit) limit = MaxLimit;

            return new TextJob(ranges, offset, limit, total);
        }

        private static List<KindRange> Ranges()
        {
            return new List<KindRange>
            {
                new KindRange { Kind = "tip", Source = "base.txt", Count = Count(LDB.advisorTips) },
                new KindRange { Kind = "tutorial", Source = "Wiki/Tutorial", Count = Count(LDB.tutorial) },
                new KindRange { Kind = "milestone", Source = "base.txt", Count = Count(LDB.milestones) },
                new KindRange { Kind = "goal", Source = "prototype.txt", Count = Count(LDB.goals) },
                new KindRange { Kind = "prompt", Source = "base.txt", Count = Count(LDB.prompts) },
                new KindRange { Kind = "achievement", Source = "base.txt", Count = Count(LDB.achievements) },
                new KindRange { Kind = "journal", Source = "base.txt", Count = Count(LDB.journalPatterns) },
                new KindRange { Kind = "glossary", Source = "dictionary.txt", Count = Glossary().Count },
            };
        }

        private static int Count<T>(ProtoSet<T> set) where T : Proto
        {
            try { return set != null && set.dataArray != null ? set.dataArray.Length : 0; }
            catch { return 0; }
        }

        private sealed class TextJob : IGameToolJob
        {
            private readonly List<KindRange> _ranges;
            private readonly int _offset;
            private readonly int _limit;
            private readonly int _total;
            private readonly int _end;
            private readonly List<JsonValue> _rows = new List<JsonValue>(200);
            private int _cursor;

            public TextJob(List<KindRange> ranges, int offset, int limit, int total)
            {
                _ranges = ranges;
                _offset = offset;
                _limit = limit;
                _total = total;
                _end = Math.Min(total, offset + limit);
                _cursor = offset;
            }

            public bool Step(int budgetMs)
            {
                var watch = Stopwatch.StartNew();
                while (_cursor < _end)
                {
                    _rows.Add(BuildTextRow(_ranges, _cursor));
                    _cursor++;
                    if (watch.ElapsedMilliseconds >= budgetMs) break;
                }
                return _cursor >= _end || _rows.Count >= _limit;
            }

            public GameToolJobResult Finish()
            {
                var data = JsonValue.NewObject();
                data.Set("schemaVersion", 1);
                data.Set("gameVersion", GameRefs.GameVersion);
                data.Set("locale", GameRefs.LanguageCode);
                data.Set("offset", _offset);
                data.Set("limit", _limit);
                data.Set("total", _total);
                var done = _cursor >= _total;
                data.Set("done", done);
                var rows = JsonValue.NewArray();
                foreach (var row in _rows) rows.Add(row);
                data.Set("rows", rows);
                return new GameToolJobResult
                {
                    Data = data,
                    Truncated = !done,
                    NextOffset = done ? -1 : _cursor,
                };
            }
        }

        // ------------------------------------------------------------ 行序列化

        private static JsonValue BuildTextRow(List<KindRange> ranges, int index)
        {
            var offset = 0;
            foreach (var range in ranges)
            {
                if (index < offset + range.Count)
                {
                    var local = index - offset;
                    switch (range.Kind)
                    {
                        case "tip": return BuildTip(local);
                        case "tutorial": return BuildTutorial(local);
                        case "milestone": return BuildMilestone(local);
                        case "goal": return BuildGoal(local);
                        case "prompt": return BuildPrompt(local);
                        case "achievement": return BuildAchievement(local);
                        case "journal": return BuildJournal(local);
                        case "glossary": return BuildGlossary(local);
                    }
                }
                offset += range.Count;
            }
            return Minimal("text", index);
        }

        private static JsonValue BaseRow(string kind, int id, string title, string rawBody,
            string source, string spoiler, int phase, JsonValue extra)
        {
            var row = JsonValue.NewObject();
            row.Set("kind", kind);
            row.Set("id", id);
            row.Set("title", ExportPrototypesTool.Clean(title));
            row.Set("body", ExportPrototypesTool.Clean(rawBody));
            row.Set("phase", phase);
            row.Set("spoiler", spoiler);
            row.Set("source", source);
            row.Set("templated", Regex.IsMatch(rawBody ?? string.Empty, @"\{\d+"));
            row.Set("unresolvedKeys", Regex.IsMatch(rawBody ?? string.Empty, @"\$LKEY_\d+;"));
            if (extra != null) row.Set("extra", extra);
            return row;
        }

        private static JsonValue BuildTip(int index)
        {
            var p = LDB.advisorTips.dataArray[index];
            if (p == null) return Minimal("tip", index);
            var extra = JsonValue.NewObject();
            extra.Set("priority", p.Priority);
            extra.SetIfNotNull("voice", p.Voice);
            return BaseRow("tip", p.ID, "顾问提示 " + p.ID.ToString(CultureInfo.InvariantCulture),
                p.script ?? SafeTranslate(p.Script), "base.txt", "low", 0, extra);
        }

        private static JsonValue BuildTutorial(int index)
        {
            var p = LDB.tutorial.dataArray[index];
            if (p == null) return Minimal("tutorial", index);
            var title = SafeTranslate(p.Name);
            var layout = p.LayoutFileName ?? string.Empty;
            var body = ReadTutorial(layout, title);
            var extra = JsonValue.NewObject();
            extra.Set("layoutFile", layout);
            return BaseRow("tutorial", p.ID, title, body,
                "Wiki/Tutorial/" + layout + "-" + TutorialSuffix() + ".txt",
                p.ID <= 1 ? "story" : "low", 0, extra);
        }

        private static JsonValue BuildMilestone(int index)
        {
            var p = LDB.milestones.dataArray[index];
            if (p == null) return Minimal("milestone", index);
            var body = Join(p.defaultDesc ?? SafeTranslate(p.DefaultDesc), p.conditionString ?? SafeTranslate(p.ConditionString));
            var extra = JsonValue.NewObject();
            extra.Set("positionX", p.Position.x);
            extra.Set("positionY", p.Position.y);
            extra.Set("journalPatternId", p.JournalPatternId);
            return BaseRow("milestone", p.ID, p.name, body, "base.txt", "low", 0, extra);
        }

        private static JsonValue BuildGoal(int index)
        {
            var p = LDB.goals.dataArray[index];
            if (p == null) return Minimal("goal", index);
            var name = SafeTranslate(p.Name);
            var extra = JsonValue.NewObject();
            extra.Set("level", (int)p.Level);
            extra.Set("parentId", p.ParentId);
            var children = JsonValue.NewArray();
            if (p.Childs != null) foreach (var child in p.Childs) children.Add(child);
            extra.Set("children", children);
            return BaseRow("goal", p.ID, name, name, "prototype.txt", "low", (int)p.Level, extra);
        }

        private static JsonValue BuildPrompt(int index)
        {
            var p = LDB.prompts.dataArray[index];
            if (p == null) return Minimal("prompt", index);
            var body = p.text ?? SafeTranslate(p.Text);
            var title = body.Length > 24 ? body.Substring(0, 24) : body;
            return BaseRow("prompt", p.ID, title, body, "base.txt", "none", 0, null);
        }

        private static JsonValue BuildAchievement(int index)
        {
            var p = LDB.achievements.dataArray[index];
            if (p == null) return Minimal("achievement", index);
            var extra = JsonValue.NewObject();
            extra.Set("targetValue", p.TargetValue);
            return BaseRow("achievement", p.ID, p.name ?? SafeTranslate(p.Name), p.description ?? SafeTranslate(p.Description), "base.txt", "low", 0, extra);
        }

        private static JsonValue BuildJournal(int index)
        {
            var p = LDB.journalPatterns.dataArray[index];
            if (p == null) return Minimal("journal", index);
            var extra = JsonValue.NewObject();
            var types = JsonValue.NewArray();
            if (p.DataTypes != null) foreach (var type in p.DataTypes) types.Add(type);
            extra.Set("dataTypes", types);
            return BaseRow("journal", p.ID, "里程碑日志 " + p.ID.ToString(CultureInfo.InvariantCulture),
                p.journalDesc ?? SafeTranslate(p.JournalDesc), "base.txt", "low", 0, extra);
        }

        private static JsonValue BuildGlossary(int index)
        {
            var list = Glossary();
            if (index < 0 || index >= list.Count) return Minimal("glossary", index);
            var pair = list[index];
            return BaseRow("glossary", 0, pair.Key, pair.Value, "dictionary.txt", "none", 0, null)
                .Set("id", pair.Key);
        }

        private static JsonValue Minimal(string kind, int index)
        {
            var row = JsonValue.NewObject();
            row.Set("kind", kind);
            row.Set("id", index);
            row.Set("title", "（导出失败）");
            row.Set("body", string.Empty);
            return row;
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? string.Empty;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "\n" + b;
        }

        private static string SafeTranslate(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try { return key.Translate(); } catch { return key; }
        }

        // ------------------------------------------------------------ 文件

        private static string GameRoot()
        {
            try
            {
                var dataPath = Application.dataPath;
                var parent = Directory.GetParent(dataPath);
                return parent != null ? parent.FullName : dataPath;
            }
            catch { return string.Empty; }
        }

        private static string TutorialSuffix()
        {
            switch (GameRefs.LanguageCode)
            {
                case "zh-CN": return "zh";
                case "en-US": return "en";
                case "fr-FR": return "fr";
                case "de-DE": return "de";
                case "es-ES": return "es";
                case "ja-JP": return "ja";
                case "ko-KR": return "ko";
                case "ru-RU": return "ru";
                default: return "en";
            }
        }

        private static string ReadTutorial(string layout, string title)
        {
            if (string.IsNullOrEmpty(layout)) return string.Empty;
            try
            {
                var path = Path.Combine(GameRoot(), "Wiki", "Tutorial", layout + "-" + TutorialSuffix() + ".txt");
                if (!File.Exists(path)) return string.Empty;
                var raw = File.ReadAllText(path, Encoding.UTF8);
                var sb = new StringBuilder();
                foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.TrimStart().StartsWith("{$", StringComparison.Ordinal)) continue;
                    sb.AppendLine(line);
                }
                var body = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(title) && body.StartsWith(title, StringComparison.Ordinal))
                    body = body.Substring(title.Length).TrimStart();
                body = Regex.Replace(body, @"\n{3,}", "\n\n");
                return body;
            }
            catch { return string.Empty; }
        }

        private static List<KeyValuePair<string, string>> _glossary;
        private static string _glossaryPath;

        private static List<KeyValuePair<string, string>> Glossary()
        {
            var path = string.Empty;
            try
            {
                var lcid = Localization.CurrentLanguageLCID;
                path = Path.Combine(GameRoot(), "Locale", lcid.ToString(CultureInfo.InvariantCulture), "dictionary.txt");
            }
            catch { }

            if (_glossary != null && _glossaryPath == path) return _glossary;
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var lines = File.ReadAllText(path, Encoding.Unicode).Replace("\r\n", "\n").Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Length == 0) continue;
                        var parts = line.Split('\t');
                        if (parts.Length < 2) continue;
                        var key = parts[0].TrimStart('\ufeff').Trim();
                        if (!key.EndsWith("(字典)", StringComparison.Ordinal)) continue;
                        var value = parts[parts.Length - 1].Trim();
                        if (value.Length > 0) list.Add(new KeyValuePair<string, string>(key, value));
                    }
                }
            }
            catch { }
            _glossary = list;
            _glossaryPath = path;
            return list;
        }
    }
}
