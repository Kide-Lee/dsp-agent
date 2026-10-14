using System;
using System.IO;
using DSPAgent.Common.Json;

namespace DSPAgent.Core
{
    /// <summary>
    /// sidecar 启动后写出的 runtime.json（见 docs/protocol-v1.md §1 与 design.md §7.1.4）。
    /// mod 通过它拿到真实端口与握手 token —— token 只存在本机文件里，
    /// 不经过游戏配置，避免被误传出去。
    /// </summary>
    internal sealed class RuntimeInfo
    {
        public string Host = "127.0.0.1";
        public int Port = 8765;
        public string Token = string.Empty;
        public int Pid;
        public string Version = string.Empty;

        public const string FileName = "runtime.json";

        public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

        public static bool TryLoad(string dataDir, out RuntimeInfo info, out string error)
        {
            info = null;
            error = null;

            var path = PathFor(dataDir);
            try
            {
                if (!File.Exists(path))
                {
                    error = $"未找到 {path}";
                    return false;
                }

                var text = File.ReadAllText(path);
                if (!JsonValue.TryParse(text, out var json) || !json.IsObject)
                {
                    error = $"{path} 不是合法 JSON 对象";
                    return false;
                }

                var loaded = new RuntimeInfo
                {
                    Host = json["host"].AsString("127.0.0.1"),
                    Port = json["port"].AsInt(0),
                    Token = json["token"].AsString(""),
                    Pid = json["pid"].AsInt(0),
                    Version = json["version"].AsString(""),
                };

                if (loaded.Port <= 0 || loaded.Port > 65535)
                {
                    error = $"{path} 里的 port 非法：{loaded.Port}";
                    return false;
                }
                if (string.IsNullOrEmpty(loaded.Token))
                {
                    error = $"{path} 里缺少 token";
                    return false;
                }

                info = loaded;
                return true;
            }
            catch (Exception ex)
            {
                error = $"读取 {path} 失败：{ex.Message}";
                return false;
            }
        }
    }
}
