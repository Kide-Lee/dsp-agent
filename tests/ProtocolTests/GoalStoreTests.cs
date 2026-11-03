using IcarusII.Game;

namespace IcarusII.Tests
{
    /// <summary>
    /// AI 目标状态持久化的回归：原生读档时 GoalSystem.Import 会丢掉“LDB 里还没有 Proto”的 GoalData，
    /// 而 AI 目标的 Proto 是读档后才注入的，所以完成/跳过状态只存在存档的 stage/manualIgnore 里。
    /// 这里验证这份状态的写回、跨重启保留，以及不再注入已收起的目标。
    /// </summary>
    internal static class GoalStoreTests
    {
        private const int GroupId = 910000;
        private const int OtherGroupId = 910100;
        private const int FirstGoalId = 910001;
        private const int SecondGoalId = 910002;
        private const int OtherGoalId = 910101;
        private const int TemplateId = 1001;

        public static void Run()
        {
            // 存档里的整数必须和原生 EGoalStage 对得上，否则读档后会被当成别的阶段。
            Program.Eq("Disabled 与原生一致", 0, GoalStore.StageDisabled);
            Program.Eq("Enabled 与原生一致", 1, GoalStore.StageEnabled);
            Program.Eq("Ignored 与原生一致", 2, GoalStore.StageIgnored);
            Program.Eq("Completed 与原生一致", 3, GoalStore.StageCompleted);

            LegacyStoreDefaultsToEnabled();
            CompletedSurvivesRestart();
            ManualSkipSurvivesRestart();
            NativeIgnoreIsNotPlayerDismiss();
            GroupSkipDismissesWholeGroup();
            GroupCompleteDismissesWholeGroup();
        }

        private static void LegacyStoreDefaultsToEnabled()
        {
            // 加状态字段之前写的存档没有 stage/manualIgnore，不能因为字段缺失就把目标吞掉。
            var legacy = GoalStoreState.Parse(GoalStore.Prefix +
                "{\"goals\":[{\"groupId\":910000,\"goalId\":910001,\"templateId\":1001}],\"todos\":[]}");
            Program.Eq("旧存档目标数", 1, legacy.goals.Count);
            Program.Check("旧存档目标默认启用", !legacy.goals[0].IsDismissed &&
                                                legacy.goals[0].stage == GoalStore.StageEnabled);
            Program.Eq("旧存档目标照常注入", 1, legacy.ActiveGoals().Count);
        }

        private static void CompletedSurvivesRestart()
        {
            var store = NewStore();
            Program.Check("完成目标会写回", store.SyncGoal(FirstGoalId, GoalStore.StageCompleted, false));

            var reloaded = GoalStoreState.Parse(store.ToContent());
            Program.Check("重开后完成状态保留", reloaded.goals[0].IsDismissed &&
                                                reloaded.goals[0].stage == GoalStore.StageCompleted);
            Program.Eq("重开后同组只注入未完成目标", 1, ActiveInGroup(reloaded, GroupId));
            Program.Eq("重开后注入的是剩下那个", SecondGoalId,
                reloaded.ActiveGoals().Find(g => g.groupId == GroupId).goalId);
            Program.Check("重复同步不再写回", !reloaded.SyncGoal(FirstGoalId, GoalStore.StageCompleted, false));
        }

        private static void ManualSkipSurvivesRestart()
        {
            var store = NewStore();
            Program.Check("玩家跳过会写回", store.SyncGoal(FirstGoalId, GoalStore.StageIgnored, true));

            var reloaded = GoalStoreState.Parse(store.ToContent());
            Program.Check("重开后跳过状态保留", reloaded.goals[0].IsDismissed && reloaded.goals[0].manualIgnore);
            Program.Eq("跳过的目标不再注入", 1, ActiveInGroup(reloaded, GroupId));
        }

        private static void NativeIgnoreIsNotPlayerDismiss()
        {
            // 原生会因为判断器条件（如非战斗模式）自己 Ignore，这不是玩家要求收起，不能持久化。
            var store = NewStore();
            Program.Check("原生自动忽略不写回", !store.SyncGoal(FirstGoalId, GoalStore.StageIgnored, false));
            Program.Eq("原生自动忽略后目标仍全部注入", 2, ActiveInGroup(store, GroupId));
        }

        private static void GroupSkipDismissesWholeGroup()
        {
            var store = NewStore();
            Program.Eq("整组跳过的目标数", 2, store.SyncGroup(GroupId, GoalStore.StageIgnored, true));

            var reloaded = GoalStoreState.Parse(store.ToContent());
            Program.Eq("整组跳过后只剩别的组", 1, reloaded.ActiveGoals().Count);
            Program.Eq("整组跳过不影响别的组", OtherGoalId, reloaded.ActiveGoals()[0].goalId);
        }

        private static void GroupCompleteDismissesWholeGroup()
        {
            var store = NewStore();
            Program.Eq("整组完成的目标数", 2, store.SyncGroup(GroupId, GoalStore.StageCompleted, false));
            Program.Eq("整组完成后不再注入", 0, GoalStoreState.Parse(store.ToContent()).ActiveGoals()
                .FindAll(g => g.groupId == GroupId).Count);
        }

        private static int ActiveInGroup(GoalStoreState store, int groupId)
        {
            return store.ActiveGoals().FindAll(g => g.groupId == groupId).Count;
        }

        private static GoalStoreState NewStore()
        {
            var store = new GoalStoreState();
            store.goals.Add(NewGoal(GroupId, FirstGoalId));
            store.goals.Add(NewGoal(GroupId, SecondGoalId));
            store.goals.Add(NewGoal(OtherGroupId, OtherGoalId));
            return store;
        }

        private static StoredGoal NewGoal(int groupId, int goalId)
        {
            return new StoredGoal
            {
                groupId = groupId,
                goalId = goalId,
                group = "游戏目标",
                title = "目标 " + goalId,
                templateId = TemplateId,
                parameters = new long[0],
            };
        }
    }
}
