using System;
using UnityEngine;
using DSPAgent.Config;
using DSPAgent.Game;

namespace DSPAgent.Input
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
            Key = config.Hotkey;
        }

        public KeyCode Key { get; }

        /// <summary>返回 true 表示本帧应当打开窗口。</summary>
        public bool ShouldToggle(bool windowOpen)
        {
            // 1) 必须在游戏内且未在加载
            if (!GameRefs.IsRunning || GameRefs.IsLoading) return false;

            // 2) 必须真的有按键（用 Unity 的，带窗口焦点语义；
            //    不用 WinInput.GetAsyncKeyState —— 那在游戏失焦时也会返回 true）
            if (!UnityEngine.Input.GetKeyDown(Key)) return false;

            // 3) 窗口已打开时不响应开关键（避免误触；此时只有 Esc 能关）
            if (windowOpen) return false;

            // 4) 玩家正在输入文本 / 处于全屏界面时不抢键
            if (GameRefs.TextInputActive || GameRefs.FullscreenGui) return false;

            // 5) 建造类工具激活时把键让给游戏 —— R 的真身是"旋转建筑"
            if (_config.HotkeyRequireNoBuildTool && GameRefs.BuildToolActive()) return false;

            return true;
        }

        /// <summary>返回 true 表示本帧应当关闭窗口。</summary>
        public bool ShouldClose(bool windowOpen)
        {
            if (!windowOpen) return false;
            return UnityEngine.Input.GetKeyDown(KeyCode.Escape);
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

            var boundByGame = GameRefs.IsKeyBoundByGame(Key);
            if (!boundByGame)
                return $"热键 {Key} 未在游戏按键表中出现，无冲突。";

            var note = _config.HotkeyRequireNoBuildTool
                ? "建造模式下自动让位（HotkeyRequireNoBuildTool=true）。"
                : "⚠ 已关闭建造模式让位，可能与「旋转建筑」冲突。";

            return $"热键 {Key} 已被游戏绑定。{note} 如遇冲突可在配置中改键（可用 F8 等）。";
        }
    }
}
