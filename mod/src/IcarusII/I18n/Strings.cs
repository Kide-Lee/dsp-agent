using System.Collections.Generic;
using IcarusII.Game;

namespace IcarusII.I18n
{
    /// <summary>
    /// 界面文案。M0 只有中文，但所有文案都走查表，
    /// 后续加语言只需补一个字典（design.md §11）。
    /// </summary>
    internal static class Strings
    {
        private static readonly Dictionary<string, string> ZhCn = new Dictionary<string, string>
        {
            ["title"] = "伊卡洛斯·贰",
            ["status.connecting"] = "连接中…",
            ["status.handshaking"] = "握手中…",
            ["status.ready"] = "就绪",
            ["status.thinking"] = "思考中…",
            ["status.tools"] = "调用工具…",
            ["status.backoff"] = "重连中…",
            ["status.disconnected"] = "未连接",
            ["input.placeholder"] = "按 Enter 或点击此处开始输入…（Enter 发送 · R 关闭）",
            ["hint.disconnected"] = "未连接到 sidecar。请检查 BepInEx 日志，或改用 LaunchMode=connectOnly 自行启动。",
            ["hint.conflict"] = "提示：{0}",
            ["system"] = "系统",
            ["you"] = "你",
            ["agent"] = "助手",
        };

        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["zh-CN"] = ZhCn,
            };

        public static string Get(string key, params object[] args)
        {
            var table = ResolveTable();
            if (!table.TryGetValue(key, out var value))
            {
                // 回退到中文，再回退到 key 本身（便于暴露缺失项）
                if (!ZhCn.TryGetValue(key, out value)) value = key;
            }
            return args != null && args.Length > 0 ? string.Format(value, args) : value;
        }

        private static Dictionary<string, string> ResolveTable()
        {
            var code = GameRefs.LanguageCode;
            if (!string.IsNullOrEmpty(code) && Tables.TryGetValue(code, out var table))
                return table;
            return ZhCn;
        }
    }
}
