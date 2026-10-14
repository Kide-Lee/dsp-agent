using System;
using UnityEngine;

namespace DSPAgent.Game
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

        /// <summary>游戏是否处于可交互的运行状态。</summary>
        public static bool IsRunning
        {
            get
            {
                return GameMain.notNull && GameMain.isRunning;
            }
        }

        public static bool IsLoading
        {
            get
            {
                return GameMain.notNull && GameMain.isLoading;
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
            var player = GameMain.mainPlayer;
            if (player == null) return false;

            var controller = player.controller;
            if (controller == null) return false;

            var actionBuild = controller.actionBuild;
            if (actionBuild == null || !actionBuild.active) return false;

            var tool = actionBuild.activeTool;
            if (tool == null) return false;

            return !(tool is BuildTool_None);
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
