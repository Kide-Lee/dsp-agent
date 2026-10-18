using System;
using BepInEx.Configuration;
using UnityEngine;

namespace IcarusII.Config
{
    /// <summary>
    /// BepInEx 配置（BepInEx/config/icarus-ii.cfg）。
    /// 字段与 design.md §6.7 对应；M0 只实现其中一部分，其余先占位以便配置文件稳定。
    /// </summary>
    internal sealed class PluginConfig
    {
        // [General]
        public KeyCode Hotkey { get; private set; }
        public bool HotkeyRequireNoBuildTool { get; private set; }
        public string Language { get; private set; }
        public bool Streaming { get; private set; }
        public bool DebugLogGate { get; private set; }

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

        private readonly ConfigFile _cfg;
        private readonly ConfigEntry<string> _thinkingLevelEntry;

        /// <summary>pi 的 ThinkingLevel 取值（DeepSeek 只映射 high/max，其余由 pi clamp）。</summary>
        private static readonly string[] ThinkingLevels =
            { "off", "minimal", "low", "medium", "high", "xhigh", "max" };

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
            ThinkingLevel = level;
            try
            {
                if (_thinkingLevelEntry != null) _thinkingLevelEntry.Value = level;
                _cfg?.Save();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"保存思考档位失败：{ex.Message}");
            }
        }

        internal PluginConfig(ConfigFile cfg)
        {
            _cfg = cfg;
            var general = cfg.Bind("General", "Hotkey", "R",
                "打开/关闭聊天窗口的按键（Unity KeyCode 名，如 R / F8）。\n" +
                "注意：建造模式下 R 属于游戏（旋转建筑），本 mod 会自动避让。");
            Hotkey = ParseKey(general.Value, KeyCode.R);

            HotkeyRequireNoBuildTool = cfg.Bind("General", "HotkeyRequireNoBuildTool", true,
                "true = 建造/拆除/升级/蓝图模式下不让出热键（把 R 留给游戏）。").Value;

            Language = cfg.Bind("General", "Language", "auto",
                "auto | zh-CN | en-US。M0 仅 zh-CN。").Value;

            Streaming = cfg.Bind("General", "Streaming", true,
                "是否逐字显示流式回复。").Value;

            DebugLogGate = cfg.Bind("General", "DebugLogGate", false,
                "每秒输出一次热键判定依据与窗口可见性，用于排查「按 R 没反应」。\n" +
                "排查完成后可关闭。").Value;

            LaunchMode = cfg.Bind("Sidecar", "LaunchMode", "spawnExe",
                "spawnExe = 由本插件拉起随包分发的 icarus-ii-sidecar.exe（默认）\n" +
                "connectOnly = 只连接，不拉起（需自行启动 sidecar）\n" +
                "spawnNode = 开发用，拉起本机 node + dist/sidecar.cjs").Value;

            SidecarHost = cfg.Bind("Sidecar", "Host", "127.0.0.1", "sidecar 监听地址。").Value;
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

            _thinkingLevelEntry = cfg.Bind("Agent", "ThinkingLevel", "high",
                "思考档位：off | minimal | low | medium | high | xhigh | max。\n" +
                "DeepSeek 只映射 high / max，其余档位会被 pi clamp；off = 不请求思考；默认 high（开启）。\n" +
                "游戏里也可以用聊天窗右下角的「思考」下拉框切换（会同步到 sidecar）。");
            var rawLevel = _thinkingLevelEntry.Value;
            ThinkingLevel = IsValidThinkingLevel(rawLevel) ? rawLevel.ToLowerInvariant() : "high";
        }

        /// <summary>把配置里的字符串解析成 KeyCode；失败时回退并交由调用方记录。</summary>
        internal static KeyCode ParseKey(string raw, KeyCode fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            var trimmed = raw.Trim();
            if (Enum.TryParse(trimmed, true, out KeyCode parsed) && parsed != KeyCode.None)
                return parsed;

            // 常见别名容忍
            switch (trimmed.ToUpperInvariant())
            {
                case "ESC": return KeyCode.Escape;
                case "ENTER": return KeyCode.Return;
                case "SPACE": return KeyCode.Space;
                case "TAB": return KeyCode.Tab;
            }
            return fallback;
        }
    }
}
