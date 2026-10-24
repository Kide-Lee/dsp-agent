using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

namespace IcarusII.UI
{
    /// <summary>一个可选项（UI 目前只显示 Label，Description 预留给后续 tooltip）。</summary>
    internal sealed class AskOption
    {
        public string Label;
        public string Description;
    }

    /// <summary>ask 工具里的一个问题。</summary>
    internal sealed class AskQuestion
    {
        public string Prompt;
        public string Header;
        public readonly List<AskOption> Options = new List<AskOption>();
        public bool AllowCustom = true;
        /// <summary>true = 多选；选项前的复选框由 UI 显示。</summary>
        public bool MultiSelect;
    }

    /// <summary>玩家对一道题的最终答案；skipped=true 时 Answer 为 null。</summary>
    internal sealed class AskAnswer
    {
        public int Index;
        public string Prompt;
        public string Answer;
        public int OptionIndex = -1;
        public bool Custom;
        public bool Skipped;
        /// <summary>多选时的全部答案；单选时也写一个元素，便于 sidecar 统一处理。</summary>
        public readonly List<string> Answers = new List<string>();
        /// <summary>多选时对应的选项下标；自定义项为 -1。</summary>
        public readonly List<int> OptionIndexes = new List<int>();
    }

    /// <summary>解析 ui_ask_user 的参数。解析失败时给出可直接回给模型的错误文案。</summary>
    internal static class AskArgs
    {
        public const int MaxQuestions = 8;
        public const int MaxOptions = 4;

        public static bool TryParse(JsonValue args, out List<AskQuestion> questions, out string error)
        {
            questions = null;
            error = null;
            if (args == null || args.IsNull || !args.IsObject)
            {
                error = "args 必须是对象";
                return false;
            }

            var list = new List<AskQuestion>();
            var array = args["questions"];
            if (array.IsArray)
            {
                foreach (var item in array.Items)
                {
                    if (!TryParseQuestion(item, list.Count, out var question, out error)) return false;
                    list.Add(question);
                    if (list.Count > MaxQuestions)
                    {
                        error = "一次最多问 " + MaxQuestions + " 个问题";
                        return false;
                    }
                }
            }
            else
            {
                // 兼容单题写法：{ question, options, allowCustom }
                if (!TryParseQuestion(args, 0, out var single, out error)) return false;
                list.Add(single);
            }

            if (list.Count == 0)
            {
                error = "questions 不能为空";
                return false;
            }

            questions = list;
            return true;
        }

        private static bool TryParseQuestion(JsonValue value, int index, out AskQuestion question,
            out string error)
        {
            question = null;
            error = null;
            if (value == null || value.IsNull || !value.IsObject)
            {
                error = "questions[" + index + "] 必须是对象";
                return false;
            }

            var prompt = value["question"].AsString(null);
            if (string.IsNullOrWhiteSpace(prompt)) prompt = value["prompt"].AsString(null);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                error = "questions[" + index + "].question 不能为空";
                return false;
            }

            var q = new AskQuestion
            {
                Prompt = prompt.Trim(),
                Header = value["header"].AsString(null),
                AllowCustom = value["allowCustom"].AsBool(value["allowFreeform"].AsBool(true)),
                MultiSelect = value["multiSelect"].AsBool(value["allowMultiple"].AsBool(false)),
            };

            var options = value["options"];
            if (options != null && options.IsArray)
            {
                if (options.Count > MaxOptions)
                {
                    error = "questions[" + index + "] 最多 " + MaxOptions + " 个选项";
                    return false;
                }
                foreach (var option in options.Items)
                {
                    if (q.Options.Count >= MaxOptions) break;
                    var parsed = ParseOption(option);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Label)) q.Options.Add(parsed);
                }
            }

            if (!q.AllowCustom && q.Options.Count == 0)
            {
                error = "questions[" + index + "] 没有选项且不允许自定义输入";
                return false;
            }

            question = q;
            return true;
        }

        private static AskOption ParseOption(JsonValue value)
        {
            if (value == null || value.IsNull) return null;

            if (!value.IsObject)
            {
                var label = value.AsString(null);
                return string.IsNullOrWhiteSpace(label) ? null : new AskOption { Label = label.Trim() };
            }

            var text = value["label"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) text = value["title"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) text = value["text"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) text = value["value"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) text = value["name"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) text = value["option"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) return null;

            return new AskOption
            {
                Label = text.Trim(),
                Description = value["description"].AsString(null),
            };
        }
    }
}
