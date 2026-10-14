using System;
using IcarusII.Common.Json;

namespace IcarusII.Common.Protocol
{
    /// <summary>
    /// 协议 v1 的常量与信封辅助。
    /// 契约来源：docs/protocol-v1.md —— 改这里之前先改那份文档。
    /// </summary>
    public static class Protocol
    {
        public const int Version = 1;

        // 消息类型（mod → sidecar）
        public const string Hello = "hello";
        public const string Prompt = "prompt";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Abort = "abort";

        // 消息类型（sidecar → mod）
        public const string Ready = "ready";
        public const string Status = "status";
        public const string Stream = "stream";
        public const string Message = "message";
        public const string Error = "error";

        // status.state 取值
        public const string StateIdle = "idle";
        public const string StateThinking = "thinking";
        public const string StateTools = "tools";
        public const string StateRetrying = "retrying";
        public const string StateCompacting = "compacting";
        public const string StateAborting = "aborting";

        /// <summary>服务端 sidecar 的默认端口。</summary>
        public const int DefaultPort = 8765;

        public static JsonValue Envelope(string type, JsonValue payload, string id, string replyTo)
        {
            var env = JsonValue.NewObject();
            env.Set("v", Version);
            env.Set("id", id);
            env.Set("type", type);
            if (!string.IsNullOrEmpty(replyTo)) env.Set("replyTo", replyTo);
            env.Set("payload", payload ?? JsonValue.NewObject());
            return env;
        }

        public static JsonValue BuildHello(string token, string modVersion, string gameVersion,
            string gameLanguage, string[] capabilities, string saveKey)
        {
            var p = JsonValue.NewObject();
            p.Set("token", token ?? string.Empty);
            p.Set("modVersion", modVersion ?? "0.0.0");
            p.Set("gameVersion", gameVersion ?? "unknown");
            p.Set("gameLanguage", gameLanguage ?? "unknown");

            var caps = JsonValue.NewArray();
            if (capabilities != null) foreach (var c in capabilities) caps.Add(c);
            p.Set("capabilities", caps);

            // saveKey 允许为 null（尚未进入存档）
            if (saveKey != null) p.Set("saveKey", saveKey);

            return p;
        }

        public static JsonValue BuildPrompt(string text, string conversationId)
        {
            var p = JsonValue.NewObject();
            p.Set("text", text ?? string.Empty);
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            return p;
        }

        public static JsonValue BuildPing(long timestampMs)
        {
            var p = JsonValue.NewObject();
            p.Set("t", timestampMs);
            return p;
        }

        public static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
