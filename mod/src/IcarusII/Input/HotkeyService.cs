using System;
using UnityEngine;
using IcarusII.Config;
using IcarusII.Game;

namespace IcarusII.Input
{
    /// <summary>
    /// 热键判定。
    ///
    /// 这里最重要的不是"能不能检测到按键"，而是**什么时候不能响应**：
    /// R 在 DSP 中是建造模式下旋转待放置建筑的键（已核实 BuiltinKey 里存在
    /// 「顺时针旋转建筑」「逆时针旋转建筑」动作），所以必须主动避让，
    /// 否则玩家每次转建筑都会弹出聊天窗。判定顺序见 ShouldToggle。
    /// </summary>
    internal sealed class HotkeyService
    {
        private readonly PluginConfig _config;
        private bool _conflictLogged;

        public HotkeyService(PluginConfig config)
        {
            _config = config;
            Key = config.HotkeyChord;
        }

        public KeyChord Key { get; private set; }

        /// <summary>A.14.7：设置页改键后立即生效；冲突自检可以重新跑。</summary>
        public void SetKey(KeyChord key)
        {
            Key = key;
            _conflictLogged = false;
        }

        /// <summary>本帧热键是否被按下（无论最终是否放行）。</summary>
        public bool KeyPressedThisFrame { get; private set; }

        /// <summary>本帧是否按下了 Esc。</summary>
        public bool EscapePressedThisFrame => UnityEngine.Input.GetKeyDown(KeyCode.Escape);

        /// <summary>返回 true 表示本帧应当切换窗口开关状态。</summary>
        public bool ShouldToggle(bool windowOpen, bool inputFocused) => ShouldToggle(windowOpen, inputFocused, out _);

        /// <summary>
        /// 带原因的版本。reason 只在**键确实按下、但被拒绝**时才有值。
        ///
        /// 语义：R 是**开关**键（开也用它，关也用它）。
        ///
        /// 与打字冲突的处理：当任一输入框（聊天框、标题框）获得焦点时，R 必须当作字母「r」输入，
        /// 否则中文拼音里 人/日/让/热 这类以 r 开头的词根本打不出来
        /// （按 R 关窗 → 再打开 → 再按又关，死循环）。
        /// 因此：**输入框聚焦时不响应开关**；此时用 Esc 关闭。
        /// </summary>
        public bool ShouldToggle(bool windowOpen, bool inputFocused, out string reason)
        {
            reason = null;
            KeyPressedThisFrame = false;

            // 1) 必须真的有按键（用 Unity 的，带窗口焦点语义；
            //    不用 WinInput.GetAsyncKeyState —— 那在游戏失焦时也会返回 true）
            if (!KeyChordInput.IsPressedThisFrame(Key)) return false;
            KeyPressedThisFrame = true;

            // 2) 设置页正在捕获快捷键：让出按键，否则按 R 会开/关聊天窗。
            if (UI.ModelSettingsTab.IsCapturingKey) { reason = "设置页正在捕获快捷键"; return false; }

            // 3) 必须真的进入存档：开始菜单、载入中都不要开窗
            if (!GameRefs.IsInGame) { reason = "不在游戏内（开始菜单/载入中）"; return false; }

            // 4) 焦点在输入框（聊天框/标题框）上 → 让位给文字输入
            if (inputFocused) { reason = "输入框已聚焦（R 作为文字输入）"; return false; }

            // 5) 窗口已打开 → 关闭。
            //    必须在"输入/全屏/建造"这些门控之前返回：窗口开着时
            //    VFInput.inputing 正是我们自己置为 true 的，否则永远关不掉。
            if (windowOpen) return true;

            // 以下只作用于"打开"路径
            if (GameRefs.TextInputActive) { reason = "游戏文本输入中"; return false; }
            if (GameRefs.FullscreenGui) { reason = "处于全屏界面"; return false; }

            // 建造类工具激活时把键让给游戏 —— R 的真身是"旋转建筑"
            if (_config.HotkeyRequireNoBuildTool && GameRefs.BuildToolActive())
            {
                reason = "建造工具激活（让位给旋转建筑）";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 启动时做一次冲突自检，只记日志（不阻断）。
        /// 已知冲突「旋转建筑」由 ShouldToggle 第 5 步消解；
        /// 这里负责发现**其它**占用同一个键的动作，让玩家知道可能需要改键。
        /// </summary>
        public string RunConflictScan()
        {
            if (_conflictLogged) return null;
            _conflictLogged = true;

            if (!KeyChordInput.TryResolve(Key, out var keyCode))
                return $"热键 {Key.Format()} 无法解析成 KeyCode，请检查配置。";

            var boundByGame = GameRefs.IsKeyBoundByGame(keyCode);
            if (!boundByGame)
                return $"热键 {Key.Format()} 未在游戏按键表中出现，无冲突。";

            var note = _config.HotkeyRequireNoBuildTool
                ? "建造模式下自动让位（HotkeyRequireNoBuildTool=true）。"
                : "[警告] 已关闭建造模式让位，可能与「旋转建筑」冲突。";

            return $"热键 {Key.Format()} 已被游戏绑定。{note} 如遇冲突可在配置中改键（可用 F8 等）。";
        }
    }
}
