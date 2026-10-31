using System;
using BepInEx.Configuration;
using UnityEngine;
using IcarusII.Input;

namespace IcarusII.Config
{
    /// <summary>
    /// BepInEx 配置（BepInEx/config/icarus-ii.cfg）。
    /// 字段与 design.md §6.7 对应；M0 只实现其中一部分，其余先占位以便配置文件稳定。
    /// </summary>
    internal sealed class PluginConfig
    {
        // [General] / [Keys]
        /// <summary>打开/关闭聊天窗的组合键（A.14.7，默认 R）。</summary>
        public KeyChord HotkeyChord { get; private set; }
        /// <summary>配置里的按键动作标识；设置页与冲突提示共用。</summary>
        public const string KeyActionHotkey = "hotkey";
        public const string KeyActionSend = "send";
        public const string KeyActionNewline = "newline";

        public static readonly KeyChord DefaultHotkey = KeyChord.Of("R");
        public static readonly KeyChord DefaultSend = KeyChord.Of("Return");
        public static readonly KeyChord DefaultNewline = KeyChord.Of("Return", ctrl: true);

        /// <summary>发送消息的组合键（A.14.7，默认 Enter）。</summary>
        public KeyChord SendChord { get; private set; }
        /// <summary>换行的组合键（A.14.7，默认 Ctrl+Enter）。</summary>
        public KeyChord NewlineChord { get; private set; }

        public bool HotkeyRequireNoBuildTool { get; private set; }
        public string Language { get; private set; }
        public bool Streaming { get; private set; }
        /// <summary>M4m：输入框 !tool 调试直调；需要 sidecar 同时用 --debug-tools 启动。</summary>
        public bool DebugToolConsole { get; private set; }

        /// <summary>
        /// 第三方 mod 桥：允许读取其他 BepInEx 插件的列表与配置，并写入配置。
        /// 默认关闭；改完需要重启游戏。
        /// </summary>
        public bool AllowThirdPartyTools { get; private set; }

        // [Sidecar]
        public string LaunchMode { get; private set; }
        public string SidecarHost { get; private set; }
        public int SidecarPort { get; private set; }
        public string SidecarExePath { get; private set; }
        public string DataDir { get; private set; }
        public int ConnectTimeoutMs { get; private set; }
        public int SidecarStartupTimeoutMs { get; private set; }
        public bool AutoReconnect { get; private set; }

        // [UI]
        public float FontScale { get; private set; }
        public float PanelWidth { get; private set; }
        public float PanelHeight { get; private set; }
        public int MaxTranscriptLines { get; private set; }
        public string PositionMode { get; private set; }
        public bool AutoFocusInput { get; private set; }

        // [Agent]
        /// <summary>
        /// 思考档位：off / minimal / low / medium / high / xhigh / max。
        /// sidecar 的 config.json 是权威来源；这里是本地兜底（未连接时也能记住玩家的选择）。
        /// </summary>
        public string ThinkingLevel { get; private set; }

        /// <summary>
        /// 权限档位：
        /// readonly（只读，所有写操作都要确认）/
        /// limited（受限，risk 1 低风险写操作直接放行，risk 2 仍需确认）/
        /// takeover（接管，全部写操作直接放行）。
        /// sidecar 的 config.json 是权威来源；这里是本地兜底，断线时也能记住玩家的选择。
        /// </summary>
        public string PermissionLevel { get; private set; }

        private readonly ConfigFile _cfg;
        private readonly ConfigEntry<string> _thinkingLevelEntry;
        private readonly ConfigEntry<string> _permissionLevelEntry;
        private readonly ConfigEntry<string> _hotkeyEntry;
        private readonly ConfigEntry<string> _sendChordEntry;
        private readonly ConfigEntry<string> _newlineChordEntry;

        /// <summary>pi 的 ThinkingLevel 取值（DeepSeek Flash 映射 low/high/max）。</summary>
        private static readonly string[] ThinkingLevels =
            { "off", "minimal", "low", "medium", "high", "xhigh", "max" };

        private static readonly string[] PermissionLevels =
            { "readonly", "limited", "takeover" };

        public static bool IsValidPermissionLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return false;
            foreach (var candidate in PermissionLevels)
                if (string.Equals(candidate, level, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsValidThinkingLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return false;
            foreach (var candidate in ThinkingLevels)
                if (string.Equals(candidate, level, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>下拉框选择变化时调用：更新内存值并写回 icarus-ii.cfg。</summary>
        public void SetThinkingLevel(string level)
        {
            if (!IsValidThinkingLevel(level)) return;
            level = level.ToLowerInvariant();
            var changed = !string.Equals(level, ThinkingLevel, StringComparison.Ordinal);
            ThinkingLevel = level;
            if (!changed) return;
            try
            {
                if (_thinkingLevelEntry is not null) _thinkingLevelEntry.Value = level;
                _cfg?.Save();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"保存思考档位失败：{ex.Message}");
            }
        }

        /// <summary>更新权限档位并写回 icarus-ii.cfg；同值不重复落盘。</summary>
        public void SetPermissionLevel(string level)
        {
            if (!IsValidPermissionLevel(level)) return;
            level = level.ToLowerInvariant();
            var changed = !string.Equals(level, PermissionLevel, StringComparison.Ordinal);
            PermissionLevel = level;
            if (!changed) return;
            try
            {
                if (_permissionLevelEntry is not null) _permissionLevelEntry.Value = level;
                _cfg?.Save();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"保存权限档位失败：{ex.Message}");
            }
        }

        internal PluginConfig(ConfigFile cfg)
        {
            _cfg = cfg;
            _hotkeyEntry = cfg.Bind("General", "Hotkey", "R",
                "打开/关闭聊天窗口的按键；可带修饰键（Ctrl+Alt+Shift+KeyCode）。\n" +
                "例子：R / F8 / Ctrl+R。注意：建造模式下 R 属于游戏（旋转建筑），本 mod 会自动避让。");
            HotkeyChord = ParseChord(_hotkeyEntry, DefaultHotkey, "General.Hotkey");

            _sendChordEntry = cfg.Bind("Keys", "Send", "Return",
                "发送消息的按键；可带修饰键（Ctrl+Alt+Shift+KeyCode）。\n" +
                "默认 Return = Enter。与换行键、打开键不能重复。");
            SendChord = ParseChord(_sendChordEntry, DefaultSend, "Keys.Send");

            _newlineChordEntry = cfg.Bind("Keys", "Newline", "Control+Return",
                "换行的按键；可带修饰键。默认 Control+Return = Ctrl+Enter。\n" +
                "与发送键、打开键不能重复。");
            NewlineChord = ParseChord(_newlineChordEntry, DefaultNewline, "Keys.Newline");
            EnsureDistinctKeyBindings();

            HotkeyRequireNoBuildTool = cfg.Bind("General", "HotkeyRequireNoBuildTool", true,
                "true = 建造/拆除/升级/蓝图模式下不让出热键（把 R 留给游戏）。").Value;

            Language = cfg.Bind("General", "Language", "auto",
                "auto | zh-CN | en-US。M0 仅 zh-CN。").Value;

            Streaming = cfg.Bind("General", "Streaming", true,
                "是否逐字显示流式回复。").Value;

            DebugToolConsole = cfg.Bind("General", "DebugToolConsole", false,
                "输入框调试直调：!tools 列工具，!tool <工具名> <JSON> 直接执行只读工具。\n" +
                "不经过 AI、不消耗 token；需要 sidecar 以 --debug-tools 启动。\n" +
                "默认关闭，调试完建议关掉。").Value;

            LaunchMode = cfg.Bind("Sidecar", "LaunchMode", "spawnExe",
                "spawnExe = 由本插件拉起随包分发的 icarus-ii-sidecar.exe（默认）\n" +
                "connectOnly = 只连接，不拉起（需自行启动 sidecar）\n" +
                "spawnNode = 开发用，拉起本机 node + dist/sidecar.cjs").Value;

            SidecarHost = cfg.Bind("Sidecar", "Host", "127.0.0.1", "sidecar 监听地址（仅允许回环：127.0.0.1 / ::1 / localhost）。").Value;
            SidecarPort = cfg.Bind("Sidecar", "Port", 8765, "sidecar 监听端口。").Value;
            SidecarExePath = cfg.Bind("Sidecar", "ExePath", "",
                "留空 = 使用插件目录下的 sidecar/icarus-ii-sidecar.exe。").Value;
            DataDir = cfg.Bind("Sidecar", "DataDir", "",
                "留空 = %APPDATA%\\icarus-ii（Windows 语义，Proton 下同样适用）。").Value;
            ConnectTimeoutMs = cfg.Bind("Sidecar", "ConnectTimeoutMs", 3000, "单次连接超时（毫秒）。").Value;
            SidecarStartupTimeoutMs = cfg.Bind("Sidecar", "StartupTimeoutMs", 15000,
                "spawnExe 模式下等待 runtime.json 出现的超时（毫秒）。").Value;
            AutoReconnect = cfg.Bind("Sidecar", "AutoReconnect", true, "断线后自动重连。").Value;

            FontScale = cfg.Bind("UI", "FontScale", 1.0f, "界面字号缩放。").Value;
            PanelWidth = cfg.Bind("UI", "Width", 780f, "窗口宽度（像素）。").Value;
            PanelHeight = cfg.Bind("UI", "Height", 520f, "窗口高度（像素）。").Value;
            MaxTranscriptLines = cfg.Bind("UI", "MaxTranscriptLines", 500,
                "聊天记录保留的最大行数（超出丢弃最早的行）。").Value;
            PositionMode = cfg.Bind("UI", "PositionMode", "center",
                "center = 屏幕正中（默认）；bottomRight = 右下角。").Value;
            AutoFocusInput = cfg.Bind("UI", "AutoFocusInput", false,
                "true = 打开窗口时自动聚焦输入框（可直接打字，但此时 R 是文字输入、不能关窗）。\n" +
                "false = 需按 Enter 或点击窗口后开始输入；R 可随时开关窗口（默认）。").Value;

            AllowThirdPartyTools = cfg.Bind("Bridge", "AllowThirdPartyTools", false,
                "允许 AI 读取其他 BepInEx 插件的配置并写入（第三方 mod 桥）。\n" +
                "默认关闭；修改后需要重启游戏。").Value;

            _thinkingLevelEntry = cfg.Bind("Agent", "ThinkingLevel", "high",
                "思考档位：off | low | high | max（聊天窗只暴露这四档）。\n" +
                "DeepSeek Flash 映射 low / high / max；Pro 只映射 high / max，low 会被 pi clamp；\n" +
                "off = 不请求思考；默认 high（开启）。\n" +
                "游戏里也可以用聊天窗右下角的「思考」下拉框切换（会同步到 sidecar）。");
            var rawLevel = _thinkingLevelEntry.Value;
            ThinkingLevel = IsValidThinkingLevel(rawLevel) ? rawLevel.ToLowerInvariant() : "high";

            _permissionLevelEntry = cfg.Bind("Agent", "PermissionLevel", "limited",
                "权限档位：readonly | limited | takeover。\n" +
                "readonly = 只读，所有写操作都要玩家确认；\n" +
                "limited = 受限，低风险写操作（镜头/标记/面板/方位/撤销，risk 1）直接放行，\n" +
                "          改变存档的写操作（risk 2）仍需确认；\n" +
                "takeover = 接管，所有写操作直接执行、不再询问。\n" +
                "游戏里也可以用聊天窗右下角的「权限」下拉框切换（会同步到 sidecar）。");
            var rawPermission = _permissionLevelEntry.Value;
            PermissionLevel = IsValidPermissionLevel(rawPermission)
                ? rawPermission.ToLowerInvariant()
                : "limited";
        }

        /// <summary>读取一个 chord 配置；无法解析时回落到默认并记一条 warning。</summary>
        private static KeyChord ParseChord(ConfigEntry<string> entry, KeyChord fallback, string name)
        {
            var raw = entry != null ? entry.Value : null;
            if (KeyChord.TryParse(raw, out var chord)) return chord;
            Plugin.Log?.LogWarning($"[配置] {name} 无法解析为按键组合（{raw}），已回落到 {fallback.Format()}");
            return fallback;
        }

        /// <summary>三个绑定两两不同；重复时全部回落默认并记录一次。</summary>
        private void EnsureDistinctKeyBindings()
        {
            if (!HotkeyChord.Equals(SendChord) && !HotkeyChord.Equals(NewlineChord) &&
                !SendChord.Equals(NewlineChord)) return;

            HotkeyChord = DefaultHotkey;
            SendChord = DefaultSend;
            NewlineChord = DefaultNewline;
            Plugin.Log?.LogWarning("[配置] 打开/发送/换行存在重复绑定，三项已回落到默认值");
        }

        /// <summary>某个动作换成新组合会不会与另外两项冲突；返回冲突动作 id，不冲突返回 null。</summary>
        public string KeyConflict(string action, KeyChord chord)
        {
            if (action != KeyActionHotkey && chord.Equals(HotkeyChord)) return KeyActionHotkey;
            if (action != KeyActionSend && chord.Equals(SendChord)) return KeyActionSend;
            if (action != KeyActionNewline && chord.Equals(NewlineChord)) return KeyActionNewline;
            return null;
        }

        /// <summary>恢复三项默认绑定；默认值本身两两不同，不需要逐个校验。</summary>
        public void ResetKeyBindings()
        {
            HotkeyChord = DefaultHotkey;
            SendChord = DefaultSend;
            NewlineChord = DefaultNewline;
            if (_hotkeyEntry is not null) _hotkeyEntry.Value = HotkeyChord.Format();
            if (_sendChordEntry is not null) _sendChordEntry.Value = SendChord.Format();
            if (_newlineChordEntry is not null) _newlineChordEntry.Value = NewlineChord.Format();
            try
            {
                _cfg?.Save();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"保存按键设置失败：{ex.Message}");
            }
        }

        /// <summary>设置页保存一个按键绑定；重复时拒绝，返回 false。</summary>
        public bool SetKeyBinding(string action, KeyChord chord)
        {
            if (chord.IsNone) return false;
            if (KeyConflict(action, chord) is not null) return false;

            var changed = false;
            switch (action)
            {
                case KeyActionHotkey:
                    if (_hotkeyEntry is not null) _hotkeyEntry.Value = chord.Format();
                    if (!chord.Equals(HotkeyChord))
                    {
                        HotkeyChord = chord;
                        changed = true;
                    }
                    break;
                case KeyActionSend:
                    if (_sendChordEntry is not null) _sendChordEntry.Value = chord.Format();
                    if (!chord.Equals(SendChord))
                    {
                        SendChord = chord;
                        changed = true;
                    }
                    break;
                case KeyActionNewline:
                    if (_newlineChordEntry is not null) _newlineChordEntry.Value = chord.Format();
                    if (!chord.Equals(NewlineChord))
                    {
                        NewlineChord = chord;
                        changed = true;
                    }
                    break;
                default:
                    return false;
            }

            if (!changed) return true;
            try
            {
                _cfg?.Save();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"保存按键设置失败：{ex.Message}");
            }
            return true;
        }

    }
}
