using System;
using System.Text;
using UnityEngine;

namespace IcarusII.Game
{
    /// <summary>
    /// 对游戏内部 API 的**唯一**访问点。
    ///
    /// 目的：把"哪些游戏类型/成员被依赖"集中到一处，将来游戏更新导致编译失败时
    /// 只需要改这一个文件（见 design.md §16 R3）。其它文件一律通过这里访问游戏。
    ///
    /// 所有成员都做过空引用防护：这些代码每帧都会被调用，
    /// 而游戏在各场景切换期间会有大量对象为 null。
    /// </summary>
    internal static class GameRefs
    {
        /// <summary>游戏版本。TODO(M1)：改为运行时从游戏读取，避免与游戏更新脱节。</summary>
        public const string GameVersion = "0.10.30.3100";

        /// <summary>
        /// 游戏是否处于可交互的运行状态。
        ///
        /// **不要用 `GameMain.notNull`**：实机日志证明它的语义与直觉相反——
        /// 主菜单为 True、进入游戏后为 False。用它做与运算会把整个 mod 锁死。
        /// 实测（0.10.30.3100）：
        ///   主菜单      notNull=True  isRunning=False player=null
        ///   载入存档中  notNull=False isRunning=False isLoading=True
        ///   游戏中      notNull=False isRunning=True  player=ok
        /// 结论：isRunning / isLoading 才是可靠信号。
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                try { return GameMain.isRunning; }
                catch { return false; }
            }
        }

        public static bool IsLoading
        {
            get
            {
                try { return GameMain.isLoading; }
                catch { return false; }
            }
        }

        /// <summary>玩家当前是否在某个文本输入框中输入（游戏自身的标记）。</summary>
        public static bool TextInputActive => VFInput.inputing;

        /// <summary>是否处于全屏界面（星图 / 戴森球编辑器等）。</summary>
        public static bool FullscreenGui => VFInput.inFullscreenGUI;

        /// <summary>
        /// 建造类工具是否处于激活状态。
        ///
        /// 这是 R 热键避让的核心判定：建造/拆除/升级/改造/蓝图模式下
        /// R 属于游戏（旋转建筑），此时绝不能被我们截获。
        /// 访问链（已对 0.10.30.3100 的 Assembly-CSharp 核实）：
        ///   GameMain.mainPlayer : Player
        ///     → Player.controller : PlayerController
        ///       → PlayerController.actionBuild : PlayerAction_Build
        ///         → active : bool, activeTool : BuildTool
        /// </summary>
        public static bool BuildToolActive()
        {
            try
            {
                var player = GameMain.mainPlayer;
                if (player == null) return false;

                var controller = player.controller;
                if (controller == null) return false;

                var actionBuild = controller.actionBuild;
                if (actionBuild == null) return false;

                // 只用 active。
                //
                // 实机日志（0.10.30.3100）显示 activeTool **恒为 null**，
                // 因此原先 "tool != null && !(tool is BuildTool_None)" 的写法
                // 在建造模式下也会返回 false —— 避让逻辑等于没生效。
                // 而 active 的取值恰好正确：
                //   漫游中   ab.active=False activeTool=null
                //   建造模式 ab.active=True  activeTool=null
                return actionBuild.active;
            }
            catch
            {
                // 异常时保守返回 true：宁可让 R 留给游戏，也不要误吞建造按键
                return true;
            }
        }

        /// <summary>
        /// 把热键判定的**所有输入**拼成一行，用于诊断"按 R 没反应"。
        /// 只有真的打印出来才知道是哪个条件挡住了——这些字段的语义
        /// （尤其 actionBuild.active / activeTool）无法从元数据推断。
        /// </summary>
        public static string DescribeHotkeyGate()
        {
            var sb = new StringBuilder(160);
            sb.Append("notNull=").Append(GameMain.notNull);
            sb.Append(" isRunning=").Append(SafeBool(() => GameMain.isRunning));
            sb.Append(" isLoading=").Append(SafeBool(() => GameMain.isLoading));
            sb.Append(" vf.inputing=").Append(SafeBool(() => VFInput.inputing));
            sb.Append(" vf.fullscreenGUI=").Append(SafeBool(() => VFInput.inFullscreenGUI));
            sb.Append(" vf.onGUI=").Append(SafeBool(() => VFInput.onGUI));

            var player = GameMain.mainPlayer;
            if (player == null) { sb.Append(" player=null"); return sb.ToString(); }
            sb.Append(" player=ok");

            var controller = player.controller;
            if (controller == null) { sb.Append(" controller=null"); return sb.ToString(); }
            sb.Append(" controller=ok");

            var actionBuild = controller.actionBuild;
            if (actionBuild == null) { sb.Append(" actionBuild=null"); return sb.ToString(); }

            sb.Append(" ab.active=").Append(actionBuild.active);
            var tool = actionBuild.activeTool;
            if (tool == null) sb.Append(" activeTool=null");
            else sb.Append(" activeTool=").Append(tool.GetType().Name).Append('"').Append(tool.name ?? "-").Append('"');
            sb.Append(" isNoneTool=").Append(tool is BuildTool_None);
            sb.Append(" => BuildToolActive=").Append(BuildToolActive());
            return sb.ToString();
        }

        private static bool SafeBool(Func<bool> get)
        {
            try { return get(); } catch { return false; }
        }

        /// <summary>UI 根节点；未进入游戏时为 null。</summary>
        public static UIRoot UiRoot => UIRoot.instance;

        /// <summary>当前语言代码（BCP-47 子集）。</summary>
        public static string LanguageCode
        {
            get
            {
                try
                {
                    if (!global::Localization.Loaded) return "unknown";
                    switch (global::Localization.CurrentLanguageLCID)
                    {
                        case 2052: return "zh-CN";
                        case 1033: return "en-US";
                        case 1036: return "fr-FR";
                        case 1031: return "de-DE";
                        case 3082: return "es-ES";
                        case 1041: return "ja-JP";
                        case 1042: return "ko-KR";
                        case 1049: return "ru-RU";
                        default: return "lcid-" + global::Localization.CurrentLanguageLCID;
                    }
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        public static bool IsChinese
        {
            get
            {
                try { return global::Localization.Loaded && global::Localization.isZHCN; }
                catch { return false; }
            }
        }

        /// <summary>
        /// 用游戏自己的译名表翻译；找不到时返回 key 本身（便于暴露缺失项）。
        /// </summary>
        public static string Translate(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                return global::Localization.CanTranslate(key) ? global::Localization.Translate(key) : key;
            }
            catch
            {
                return key;
            }
        }

        /// <summary>
        /// 设置游戏的"正在输入文本"标记。
        /// 窗口打开时置 true，游戏的移动/建造输入会随之停止响应。
        /// 返回设置前的原值，供关闭时还原。
        /// </summary>
        public static bool SetTextInputActive(bool active)
        {
            var previous = VFInput.inputing;
            VFInput.inputing = active;
            return previous;
        }

        /// <summary>
        /// 扫描游戏已解析的按键表，判断某个键是否已被占用。
        /// 这两个数组是 VFInput 在加载 KeyConfig 后填充的实际绑定，
        /// 因此不需要再去拿 KeyConfig 资源。动作名需要 BuiltinKey，M1 再补。
        /// </summary>
        public static bool IsKeyBoundByGame(KeyCode key)
        {
            try
            {
                var simple = VFInput.simple_buttons;
                if (simple != null)
                {
                    foreach (var k in simple)
                        if (k == key) return true;
                }

                var overrides = VFInput.override_keys;
                if (overrides != null)
                {
                    foreach (var ck in overrides)
                        if (ck.keyCode == (int)key && ck.modifier == 0) return true;
                }
            }
            catch
            {
                // 键表尚未加载时静默跳过：冲突检测是辅助信息，不能影响主流程
            }
            return false;
        }
    }
}
