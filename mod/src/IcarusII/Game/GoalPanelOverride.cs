using System;
using HarmonyLib;
using IcarusII.Game.Tools;

namespace IcarusII.Game
{
    /// <summary>
    /// 让“自由探索”下仍然展开原生目标面板，只展示 AI 创建的目标。
    ///
    /// 原生 UIGoalPanel 把 goalLevel == Off 当作“关面板”：_OnInit 清空状态，
    /// DetermineVisiable / UpdateTransform / DetermineTransform 三处强制折叠并画“（关闭）”。
    /// 这些判断直接读 gameDesc.goalLevel，且两个状态字段是私有的，外部改不动。
    /// 因此在三个显示方法执行期间把等级临时视为 Key，退出时立即恢复。
    /// DetermineEntry 不参与替换，IsLevelPass 仍按 Off 过滤，原生目标不会跟着回来。
    /// </summary>
    internal static class GoalPanelOverride
    {
        public static bool Enabled { get; private set; }

        private static readonly GoalLevelSwap Swap = new GoalLevelSwap();
        private static GameDesc _desc;
        private static Harmony _harmony;

        public static void Apply(bool enabled)
        {
            Enabled = enabled;
            if (!enabled || _harmony != null) return;

            try
            {
                _harmony = new Harmony("icarus-ii.goal-panel");
                _harmony.PatchAll(typeof(GoalPanelOverride).Assembly);
                Plugin.Log.LogInfo("目标面板补丁已注册：自由探索下仍显示 AI 目标");
            }
            catch (Exception ex)
            {
                _harmony = null;
                Enabled = false;
                Plugin.Log.LogWarning($"目标面板补丁注册失败，自由探索下不显示 AI 目标：{ex.Message}");
            }
        }

        /// <summary>显示方法进入时调用；只有存在 AI 目标才把等级临时提到 Key。</summary>
        public static void Begin(UIGoalPanel panel)
        {
            if (!ShouldOverride(panel)) return;

            var desc = panel.gameData != null ? panel.gameData.gameDesc : null;
            if (desc == null) return;

            var applied = Swap.Begin((int)desc.goalLevel, (int)EGoalLevel.Key);
            if (applied is null) return;

            _desc = desc;
            desc.goalLevel = (EGoalLevel)applied.Value;
        }

        /// <summary>显示方法退出时调用；重复调用安全。</summary>
        public static void End()
        {
            var restore = Swap.End();
            if (restore is null || _desc == null) return;

            _desc.goalLevel = (EGoalLevel)restore.Value;
            _desc = null;
        }

        private static bool ShouldOverride(UIGoalPanel panel)
        {
            if (!Enabled || panel == null) return false;

            var desc = panel.gameData != null ? panel.gameData.gameDesc : null;
            if (desc == null || desc.goalLevel != EGoalLevel.Off) return false;

            // 蓝图模式和护盾详情走自己的分支，交给原生逻辑，避免抢它们的折叠状态。
            try
            {
                if (panel.isInBlueprintMode || panel.isShieldDetail) return false;
            }
            catch (Exception)
            {
                return false;
            }

            return GameGoals.HasActiveAiGoals();
        }
    }

    [HarmonyPatch(typeof(UIGoalPanel), nameof(UIGoalPanel.DetermineVisiable))]
    internal static class GoalPanelVisiablePatch
    {
        private static void Prefix(UIGoalPanel __instance) => GoalPanelOverride.Begin(__instance);

        private static void Postfix() => GoalPanelOverride.End();

        private static Exception Finalizer(Exception __exception)
        {
            GoalPanelOverride.End();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(UIGoalPanel), nameof(UIGoalPanel.DetermineTransform))]
    internal static class GoalPanelTransformPatch
    {
        private static void Prefix(UIGoalPanel __instance) => GoalPanelOverride.Begin(__instance);

        private static void Postfix() => GoalPanelOverride.End();

        private static Exception Finalizer(Exception __exception)
        {
            GoalPanelOverride.End();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(UIGoalPanel), nameof(UIGoalPanel.UpdateTransform))]
    internal static class GoalPanelUpdateTransformPatch
    {
        private static void Prefix(UIGoalPanel __instance) => GoalPanelOverride.Begin(__instance);

        private static void Postfix() => GoalPanelOverride.End();

        private static Exception Finalizer(Exception __exception)
        {
            GoalPanelOverride.End();
            return __exception;
        }
    }
}
