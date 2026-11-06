using System;
using System.Collections.Generic;
using IcarusII.Common.Json;

namespace IcarusII.Game
{
    /// <summary>
    /// AI 目标在存档里的固定字段：前缀、目标状态取值、轮内待办 id 段与三态。
    ///
    /// 目标状态必须自己存一份：原生 GoalSystem 虽然会导出 GoalData，
    /// 但读档时 Import 会丢掉“LDB.goals 里还没有对应 Proto”的条目（见 GoalSystem.Import），
    /// 而 AI 目标的 Proto 是本模组读档后才注入的，所以原生那份完成/跳过状态每次重启都会消失。
    /// </summary>
    internal static class GoalStore
    {
        public const string Prefix = "icarus2-goals:";

        /// <summary>与原生 EGoalStage 的取值一致；这里只存整数，避免持久化层引用游戏枚举。</summary>
        public const int StageDisabled = 0;
        public const int StageEnabled = 1;
        public const int StageIgnored = 2;
        public const int StageCompleted = 3;

        /// <summary>待办固定占用目标 id 900 起的独立段；id 小于原生目标（1000+），面板排序时排在第一。</summary>
        public const int TodoGroupId = 900;
        public const int TodoItemIdStart = 901;
        public const int TodoItemLimit = 20;
        public const string TodoStatusPending = "pending";
        public const string TodoStatusInProgress = "in_progress";
        public const string TodoStatusCompleted = "completed";
        public const string TodoStatusSkipped = "skipped";

        public static string NormalizeTodoStatus(string status)
        {
            if (string.Equals(status, TodoStatusCompleted, StringComparison.Ordinal)) return TodoStatusCompleted;
            if (string.Equals(status, TodoStatusInProgress, StringComparison.Ordinal)) return TodoStatusInProgress;
            return TodoStatusPending;
        }

        /// <summary>只有完成和跳过会挡住注入；Disabled 及其它未知值都按启用处理，避免旧存档的目标被吞掉。</summary>
        public static int NormalizeStage(int stage)
        {
            return stage == StageCompleted || stage == StageIgnored ? stage : StageEnabled;
        }
    }

    /// <summary>一条 AI 目标定义；stage/manualIgnore 记录玩家在原生面板里完成或跳过的结果。</summary>
    [Serializable]
    internal sealed class StoredGoal
    {
        public int groupId;
        public int goalId;
        public string group;
        public string title;
        public int templateId;
        public long[] parameters;
        public int stage = GoalStore.StageEnabled;
        public bool manualIgnore;

        public bool IsDismissed => stage == GoalStore.StageCompleted || stage == GoalStore.StageIgnored;
    }

    /// <summary>轮内待办的一项；id 固定落在 901+，text 是显示文案，status 是三态。</summary>
    [Serializable]
    internal sealed class StoredTodo
    {
        public int id;
        public string text;
        public string status;
    }

    /// <summary>
    /// 目标的持久化集合：定义过滤、状态写回、JSON 编解码。
    /// 纯数据与状态机，不依赖 UnityEngine，便于离线回归。
    /// </summary>
    internal sealed class GoalStoreState
    {
        public List<StoredGoal> goals = new List<StoredGoal>();
        public List<StoredTodo> todos = new List<StoredTodo>();

        public bool HasActiveGoals
        {
            get
            {
                for (int i = 0; i < goals.Count; i++)
                    if (goals[i] != null && !goals[i].IsDismissed) return true;
                return false;
            }
        }

        /// <summary>还需要注入到原生目标系统的目标；已完成/已跳过的目标留在存档里但不再注入。</summary>
        public List<StoredGoal> ActiveGoals()
        {
            var list = new List<StoredGoal>(goals.Count);
            for (int i = 0; i < goals.Count; i++)
                if (goals[i] != null && !goals[i].IsDismissed) list.Add(goals[i]);
            return list;
        }

        /// <summary>把单个目标的原生状态写回；只有完成、或玩家手动跳过才算收起。返回是否有变化。</summary>
        public bool SyncGoal(int goalId, int stage, bool manualIgnore)
        {
            if (stage != GoalStore.StageCompleted &&
                !(stage == GoalStore.StageIgnored && manualIgnore)) return false;
            for (int i = 0; i < goals.Count; i++)
            {
                var g = goals[i];
                if (g == null || g.goalId != goalId) continue;
                return SetStage(g, stage, stage == GoalStore.StageIgnored);
            }
            return false;
        }

        /// <summary>
        /// 分组收起时写回整组：玩家跳过分组只会把子目标置成 Ignored（isManualIgnore 留在分组上），
        /// 分组完成则子目标同样应视为完成。返回被改动的目标数。
        /// </summary>
        public int SyncGroup(int groupId, int stage, bool manualIgnore)
        {
            int target;
            if (stage == GoalStore.StageCompleted) target = GoalStore.StageCompleted;
            else if (stage == GoalStore.StageIgnored && manualIgnore) target = GoalStore.StageIgnored;
            else return 0;

            int changed = 0;
            for (int i = 0; i < goals.Count; i++)
            {
                var g = goals[i];
                if (g == null || g.groupId != groupId) continue;
                if (SetStage(g, target, target == GoalStore.StageIgnored)) changed++;
            }
            return changed;
        }

        public static GoalStoreState Parse(string content)
        {
            var set = new GoalStoreState();
            if (string.IsNullOrEmpty(content) ||
                !content.StartsWith(GoalStore.Prefix, StringComparison.Ordinal)) return set;
            try
            {
                var root = JsonValue.Parse(content.Substring(GoalStore.Prefix.Length));
                var arr = root["goals"];
                for (int i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    var g = new StoredGoal
                    {
                        groupId = item["groupId"].AsInt(0),
                        goalId = item["goalId"].AsInt(0),
                        group = item["group"].AsString("游戏目标"),
                        title = item["title"].AsString("游戏目标"),
                        templateId = item["templateId"].AsInt(0),
                        parameters = ParseParams(item["params"]),
                        // 旧存档没有 stage/manualIgnore；缺省即玩家没有完成或跳过过。
                        stage = GoalStore.NormalizeStage(item["stage"].AsInt(GoalStore.StageEnabled)),
                        manualIgnore = item["manualIgnore"].AsBool(false),
                    };
                    if (g.goalId > 0 && g.templateId > 0) set.goals.Add(g);
                }

                // 轮内待办只活在当前回合/当前进程：旧版本写进存档的 todos 在这里忽略，
                // 下一次 ToContent 也不会再写回去。这样 game_todo_write 不修改存档。
                return set;
            }
            catch { return new GoalStoreState(); }
        }

        public string ToContent()
        {
            var root = JsonValue.NewObject();
            var arr = JsonValue.NewArray();
            for (int i = 0; i < goals.Count; i++)
            {
                var g = goals[i];
                if (g == null) continue;
                var row = JsonValue.NewObject();
                row.Set("groupId", g.groupId);
                row.Set("goalId", g.goalId);
                row.Set("group", g.group ?? string.Empty);
                row.Set("title", g.title ?? string.Empty);
                row.Set("templateId", g.templateId);
                row.Set("params", LongArray(g.parameters));
                row.Set("stage", GoalStore.NormalizeStage(g.stage));
                row.Set("manualIgnore", g.manualIgnore);
                arr.Add(row);
            }
            root.Set("goals", arr);

            // 不序列化轮内待办：它是一轮内的临时 UI 状态，不属于存档。
            root.Set("todos", JsonValue.NewArray());

            return GoalStore.Prefix + root.ToJson();
        }

        private static bool SetStage(StoredGoal g, int stage, bool manualIgnore)
        {
            if (g.IsDismissed) return false;
            if (g.stage == stage && g.manualIgnore == manualIgnore) return false;
            g.stage = stage;
            g.manualIgnore = manualIgnore;
            return true;
        }

        internal static long[] ParseParams(JsonValue arr)
        {
            var list = new long[arr.Count];
            for (int i = 0; i < arr.Count; i++) list[i] = arr[i].AsLong(0);
            return list;
        }

        internal static JsonValue LongArray(long[] values)
        {
            var arr = JsonValue.NewArray();
            if (values != null) for (int i = 0; i < values.Length; i++) arr.Add(JsonValue.Of(values[i]));
            return arr;
        }
    }
}
