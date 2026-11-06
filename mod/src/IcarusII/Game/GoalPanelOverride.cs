using System;
using HarmonyLib;
using IcarusII.Game.Tools;

namespace IcarusII.Game
{
    /// <summary>
    /// 让“自由探索”和沙盒存档下仍然展开原生目标面板，只展示 AI 创建的目标。
    ///
    /// 原生有两条门：UIGoalPanel 把 goalLevel == Off 当作“关面板”；
    /// 沙盒里 GoalTools.CheckSandbox1KeyUnlockTech() 直接把整个目标系统关掉。
    /// 前者在三个显示方法执行期间把等级临时视为 Key，退出时立即恢复；
    /// 后者只在存在 AI 目标时让沙盒门禁返回 false，再由 GameGoals 补一次 GoalLogic.Init。
    /// DetermineEntry 不参与替换，IsLevelPass 仍按 Off 过滤，原生目标不会跟着回来。
    /// </summary>
    internal static class GoalPanelOverride
    {
        public static bool FreeExploreEnabled { get; private set; }
        public static bool SandboxEnabled { get; private set; }

        private static readonly GoalLevelSwap Swap = new GoalLevelSwap();
        private static GameDesc _desc;
        private static Harmony _harmony;

        public static void Apply(bool freeExplore, bool sandbox)
        {
            FreeExploreEnabled = freeExplore;
            SandboxEnabled = sandbox;
            if ((!freeExplore && !sandbox) || _harmony != null) return;

            try
            {
                _harmony = new Harmony("icarus-ii.goal-panel");
                _harmony.PatchAll(typeof(GoalPanelOverride).Assembly);
                Plugin.Log.LogInfo("目标面板补丁已注册：自由探索/沙盒下仍显示 AI 目标");
            }
            catch (Exception ex)
            {
                // PatchAll 是按类型逐个应用的；中途失败时前面的补丁可能已经生效，
                // 必须撤销，否则 _harmony 被丢掉后再也无法还原。
                try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }
                _harmony = null;
                FreeExploreEnabled = false;
                SandboxEnabled = false;
                Plugin.Log.LogWarning($"目标面板补丁注册失败，自由探索/沙盒下不显示 AI 目标：{ex.Message}");
            }
        }

        /// <summary>插件卸载：撤掉本补丁，避免离开插件后仍改写 goalLevel。</summary>
        public static void Unpatch()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }
            _harmony = null;
            FreeExploreEnabled = false;
            SandboxEnabled = false;
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

        /// <summary>“自由探索”下目标面板是否仍然会显示；供工具返回 goalPanelOff 时判断。</summary>
        public static bool PanelShowsInOffMode()
        {
            if (!GameGoals.HasActiveAiGoals()) return false;
            if (FreeExploreEnabled) return true;
            if (!SandboxEnabled) return false;

            var logic = GameMain.gameScenario != null ? GameMain.gameScenario.goalLogic : null;
            return logic != null && logic.isInit;
        }

        private static bool ShouldOverride(UIGoalPanel panel)
        {
            if (!FreeExploreEnabled || panel == null) return false;

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

    /// <summary>
    /// 沙盒存档里原生会因为“沙盒工具 + 科技全解锁”关闭整个目标系统。
    /// 只有当前存档还有未完成的 AI 目标时才绕过这条门禁；否则保持原生“沙盒无目标”。
    /// </summary>
    [HarmonyPatch(typeof(GoalTools), nameof(GoalTools.CheckSandbox1KeyUnlockTech))]
    internal static class SandboxGoalGatePatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!GoalPanelOverride.SandboxEnabled) return true;
            if (!GameGoals.HasActiveAiGoals()) return true;

            __result = false;
            return false;
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
