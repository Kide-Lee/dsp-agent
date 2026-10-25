using System;
using System.Globalization;
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
        /// <summary>
        /// 游戏版本，格式为 `0.10.35.29151`（第四段是构建号，可能是五位数）。
        ///
        /// `GameConfig.gameVersion` 只带前三段，第四段在 `GameConfig.build`；直接硬编码
        /// 会随游戏更新脱节，也会让生成蓝图 header 的版本号少一位。
        /// </summary>
        private const string FallbackGameVersion = "0.10.35.29151";

        private static string _gameVersion;

        public static string GameVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(_gameVersion)) return _gameVersion;

                try
                {
                    var version = GameConfig.gameVersion;
                    if (version != null)
                    {
                        // 游戏的 Version.ToString() 只有前三段；Build 取实例字段或
                        // GameConfig.build（游戏启动时从 Updates/Versions.txt 读出）。
                        var build = version.Build > 0 ? version.Build : GameConfig.build;
                        if (build > 0)
                        {
                            _gameVersion = version.Major.ToString(CultureInfo.InvariantCulture) + "."
                                + version.Minor.ToString(CultureInfo.InvariantCulture) + "."
                                + version.Release.ToString(CultureInfo.InvariantCulture) + "."
                                + build.ToString(CultureInfo.InvariantCulture);
                            return _gameVersion;
                        }
                    }
                }
                catch
                {
                    // 游戏初始化早期 GameConfig 可能还没就绪；下面用 fallback。
                }

                _gameVersion = FallbackGameVersion;
                return _gameVersion;
            }
        }

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

        /// <summary>
        /// 是否真的在游戏内（已进入存档、玩家对象存在）。
        /// 主菜单 isRunning=false、载入中 mainPlayer=null，因此这个组合
        /// 既挡住开始菜单，也挡住载入过程中的误开窗。
        /// </summary>
        /// <summary>
        /// 主菜单的“演示场景”也会让 isRunning=true、mainPlayer 非空。
        /// 必须额外看 DSPGame.IsMenuDemo / GameMain.instance.isMenuDemo，
        /// 否则主菜单按 R 仍然能开窗。
        /// </summary>
        public static bool IsMenuDemo
        {
            get
            {
                try
                {
                    if (DSPGame.IsMenuDemo) return true;
                    if (GameMain.instance != null && GameMain.instance.isMenuDemo) return true;
                    // 双重保险：主菜单 UI 本身就是 active。
                    return UIRoot.instance != null
                        && UIRoot.instance.uiMainMenu != null
                        && UIRoot.instance.uiMainMenu.active;
                }
                catch { return true; }
            }
        }

        public static bool IsInGame
        {
            get
            {
                try
                {
                    return GameMain.isRunning
                        && !GameMain.isLoading
                        && GameMain.mainPlayer != null
                        && !IsMenuDemo;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 当前**载入会话**是否启用了游戏沙盒工具（`GameMain.sandboxToolsEnabled`）。
        ///
        /// 不要读 `GameDesc.isSandboxMode`：那是"曾经用过沙盒工具"的粘性标记，
        /// 而且 `GameHistoryData.AfterTick` 会把它置 true（存档永久失去成就）。
        /// 本属性只读，任何情况下都不写这个开关。
        /// </summary>
        public static bool SandboxToolsEnabled
        {
            get
            {
                try { return GameMain.isRunning && GameMain.sandboxToolsEnabled; }
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

        /// <summary>
        /// 当前存档的**稳定标识**；没有加载存档（主菜单）时返回 null。
        ///
        /// 由 `GameDesc.creationTime` + `GameDesc.seedKey64` 经 FNV-1a 64 派生。
        /// **刻意不包含 `GameData.gameName`**——那是玩家可改的名字，
        /// 一旦改名就会把已有对话孤立成一个新线程。
        /// 用 FNV 而非 SHA 是为了不依赖加密库（在 Proton/Mono 下更稳）。
        /// </summary>
        public static string CurrentSaveKey()
        {
            try
            {
                if (!GameMain.isRunning) return null;

                var data = GameMain.data;
                if (data == null) return null;

                var desc = data.gameDesc;
                if (desc == null) return null;

                var raw = desc.creationTime.Ticks.ToString("x", CultureInfo.InvariantCulture)
                        + ":" + desc.seedKey64.ToString("x", CultureInfo.InvariantCulture);
                return Fnv1a64Hex(raw);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>存档名，仅用于显示；没有存档时为 null。</summary>
        public static string CurrentSaveName()
        {
            try
            {
                if (!GameMain.isRunning) return null;
                return GameMain.data != null ? GameMain.data.gameName : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Fnv1a64Hex(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var ch in text)
                {
                    hash ^= (byte)(ch & 0xFF);
                    hash *= 1099511628211UL;
                    hash ^= (byte)((ch >> 8) & 0xFF);
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>UI 根节点；未进入游戏时为 null。</summary>
        public static UIRoot UiRoot => UIRoot.instance;

        /// <summary>
        /// 游戏右下角 HUD 的环形菜单（Game Menu）根节点。
        ///
        /// 它和截图键都直接写在 `DSPGame.unity` 里（不是 Resources 下的 prefab），
        /// mod 无法在安装期改场景，所以聊天入口按钮只能在运行期挂到它下面。
        /// 未进入游戏/界面尚未创建时返回 null。
        /// </summary>
        public static Transform GameMenuRoot
        {
            get
            {
                try
                {
                    var root = UIRoot.instance;
                    var game = root != null ? root.uiGame : null;
                    var menu = game != null ? game.gameMenu : null;
                    return menu != null ? menu.transform : null;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Game Menu 里的截图按钮。聊天入口按钮以它为锚点，摆在它正上方。
        /// 拿不到就返回 null（调用方下一帧重试）。
        /// </summary>
        public static UnityEngine.UI.Button GameMenuScreenshotButton
        {
            get
            {
                try
                {
                    var root = UIRoot.instance;
                    var game = root != null ? root.uiGame : null;
                    var menu = game != null ? game.gameMenu : null;
                    return menu != null ? menu.buttonS : null;
                }
                catch { return null; }
            }
        }

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

        /// <summary>玩家已解锁的蓝图档位（0 起）。Available=false 表示存档未加载。</summary>
        public struct BlueprintTierInfo
        {
            public bool Available;
            public int Assembler;
            public int Belt;
            public int Inserter;
            public bool Piler;
            public bool Stacking;
        }

        private static readonly int[] BlueprintBelts = { 2001, 2002, 2003 };
        private static readonly int[] BlueprintInserters = { 2011, 2012, 2013, 2014 };
        private static readonly int[] BlueprintAssemblers = { 2303, 2304, 2305 };

        /// <summary>
        /// 读取已解锁的最高蓝图档位。只读，不碰存档；主线程调用。
        /// 由 save.bind 被动上报给 sidecar，模型不需要再调用查询工具。
        /// </summary>
        public static BlueprintTierInfo CurrentBlueprintTiers()
        {
            var result = new BlueprintTierInfo();
            var history = GameMain.history;
            if (history == null) return result;

            result.Available = true;
            result.Assembler = Math.Max(0, HighestUnlocked(history, BlueprintAssemblers));
            result.Belt = Math.Max(0, HighestUnlocked(history, BlueprintBelts));
            result.Inserter = Math.Max(0, HighestUnlocked(history, BlueprintInserters));
            // 2040 = 自动集装机；集装堆叠与它同一解锁条件。
            result.Piler = history.ItemUnlocked(2040);
            result.Stacking = result.Piler;
            return result;
        }

        private static int HighestUnlocked(GameHistoryData history, int[] ids)
        {
            for (int i = ids.Length - 1; i >= 0; i--)
                if (history.ItemUnlocked(ids[i])) return i;
            return -1;
        }
    }
}
