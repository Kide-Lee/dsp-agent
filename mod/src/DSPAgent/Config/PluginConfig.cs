using System;
using BepInEx.Configuration;
using UnityEngine;

namespace DSPAgent.Config
{
    /// <summary>
    /// BepInEx 配置（BepInEx/config/dsp-agent.cfg）。
    /// 字段与 design.md §6.7 对应；M0 只实现其中一部分，其余先占位以便配置文件稳定。
    /// </summary>
    internal sealed class PluginConfig
    {
        // [General]
        public KeyCode Hotkey { get; private set; }
        public bool HotkeyRequireNoBuildTool { get; private set; }
        public string Language { get; private set; }
        public bool Streaming { get; private set; }

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

        internal PluginConfig(ConfigFile cfg)
        {
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

            LaunchMode = cfg.Bind("Sidecar", "LaunchMode", "spawnExe",
                "spawnExe = 由本插件拉起随包分发的 dsp-agent-sidecar.exe（默认）\n" +
                "connectOnly = 只连接，不拉起（需自行启动 sidecar）\n" +
                "spawnNode = 开发用，拉起本机 node + dist/sidecar.cjs").Value;

            SidecarHost = cfg.Bind("Sidecar", "Host", "127.0.0.1", "sidecar 监听地址。").Value;
            SidecarPort = cfg.Bind("Sidecar", "Port", 8765, "sidecar 监听端口。").Value;
            SidecarExePath = cfg.Bind("Sidecar", "ExePath", "",
                "留空 = 使用插件目录下的 sidecar/dsp-agent-sidecar.exe。").Value;
            DataDir = cfg.Bind("Sidecar", "DataDir", "",
                "留空 = %APPDATA%\\dsp-agent（Windows 语义，Proton 下同样适用）。").Value;
            ConnectTimeoutMs = cfg.Bind("Sidecar", "ConnectTimeoutMs", 3000, "单次连接超时（毫秒）。").Value;
            SidecarStartupTimeoutMs = cfg.Bind("Sidecar", "StartupTimeoutMs", 15000,
                "spawnExe 模式下等待 runtime.json 出现的超时（毫秒）。").Value;
            AutoReconnect = cfg.Bind("Sidecar", "AutoReconnect", true, "断线后自动重连。").Value;

            FontScale = cfg.Bind("UI", "FontScale", 1.0f, "界面字号缩放。").Value;
            PanelWidth = cfg.Bind("UI", "Width", 780f, "窗口宽度（像素）。").Value;
            PanelHeight = cfg.Bind("UI", "Height", 520f, "窗口高度（像素）。").Value;
            MaxTranscriptLines = cfg.Bind("UI", "MaxTranscriptLines", 500,
                "聊天记录保留的最大行数（超出丢弃最早的行）。").Value;
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
