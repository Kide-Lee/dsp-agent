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

        // ---- M1 增量（契约见 docs/protocol-v1.md 附录 A）----

        /// <summary>mod → sidecar：设置 provider/model/apiKey。</summary>
        public const string ConfigSet = "config.set";

        /// <summary>sidecar → mod：当前配置状态（绝不含 apiKey）。</summary>
        public const string ConfigState = "config.state";

        /// <summary>config.state 的取值。</summary>
        public const string ConfigReady = "ready";
        public const string ConfigMissingKey = "missing_key";
        public const string ConfigInvalidKey = "invalid_key";
        public const string ConfigUnreachable = "unreachable";

        /// <summary>mod → sidecar：把对话串绑定到某个存档（附录 A.6）。</summary>
        public const string SaveBind = "save.bind";

        /// <summary>sidecar → mod：绑定结果。</summary>
        public const string SaveBound = "save.bound";

        /// <summary>error.payload.code 的 M1 新增取值。</summary>
        public const string CodeInvalidKey = "invalid_key";
        public const string CodeUpstreamError = "upstream_error";
        public const string CodeRateLimited = "rate_limited";
        public const string CodeConfigRequired = "config_required";
        public const string CodeSaveRequired = "save_required";

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

        /// <summary>
        /// 构造 config.set。只把非空字段写进去（sidecar 按"未给出的保持不变"处理）。
        /// apiKey 是敏感数据：发送后不要留在任何日志或 UI 历史里。
        /// </summary>
        public static JsonValue BuildConfigSet(string provider, string model, string apiKey, string baseUrl)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(provider)) p.Set("provider", provider);
            if (!string.IsNullOrEmpty(model)) p.Set("model", model);
            if (!string.IsNullOrEmpty(apiKey)) p.Set("apiKey", apiKey);
            if (!string.IsNullOrEmpty(baseUrl)) p.Set("baseUrl", baseUrl);
            return p;
        }

        /// <summary>
        /// 构造 save.bind。saveKey 为 null 表示"当前没有存档"（主菜单）——
        /// 必须**显式写出 null**，因为 sidecar 要靠"字段存在且为 null"
        /// 区分"解绑"与"没提这件事"。
        /// </summary>
        public static JsonValue BuildSaveBind(string saveKey, string saveName)
        {
            var p = JsonValue.NewObject();
            p.Set("saveKey", saveKey == null ? JsonValue.Null : JsonValue.Of(saveKey));
            if (!string.IsNullOrEmpty(saveName)) p.Set("saveName", saveName);
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

    /// <summary>存档绑定结果（协议附录 A.6）。</summary>
    public sealed class SaveBoundInfo
    {
        public string SaveKey { get; private set; }
        public string SaveName { get; private set; }
        /// <summary>该存档此前是否已有对话（false = 新建）。</summary>
        public bool Existed { get; private set; }
        /// <summary>恢复的历史消息条数。</summary>
        public int MessageCount { get; private set; }

        public static SaveBoundInfo Parse(JsonValue payload)
        {
            var r = new SaveBoundInfo();
            if (payload == null || payload.IsNull) return r;
            r.SaveKey = payload["saveKey"].AsString(null);
            r.SaveName = payload["saveName"].AsString(null);
            r.Existed = payload["existed"].AsBool(false);
            r.MessageCount = payload["messageCount"].AsInt(0);
            return r;
        }

        public override string ToString()
        {
            return $"{SaveName ?? SaveKey ?? "(无存档)"} existed={Existed} messages={MessageCount}";
        }
    }

    /// <summary>
    /// sidecar 上报的配置状态。同时用于 ready.payload.config（附录 A.2）
    /// 与 config.state（附录 A.1）——两者载荷结构一致。
    /// </summary>
    public sealed class ConfigState
    {
        public string State { get; private set; }
        public string Provider { get; private set; }
        public string Model { get; private set; }
        public string Message { get; private set; }

        public ConfigState()
        {
            State = Protocol.ConfigMissingKey;
        }

        /// <summary>已配置且可用。</summary>
        public bool IsReady => State == Protocol.ConfigReady;

        /// <summary>需要玩家提供（或重新提供）API Key。</summary>
        public bool NeedsKey =>
            State == Protocol.ConfigMissingKey || State == Protocol.ConfigInvalidKey;

        /// <summary>
        /// 服务端**没有**上报 config 时的假定（例如 M0 老服务端）。
        ///
        /// 附录 A.2 把 ready.payload.config 定义为**可选**字段，
        /// 因此"字段缺失"必须解释为"服务端没意见"，而不是"需要配置"——
        /// 否则新 mod 连老服务端会立刻弹出密钥引导，且永远退不出去。
        /// </summary>
        public static ConfigState AssumeReady()
        {
            var result = new ConfigState();
            result.State = Protocol.ConfigReady;
            return result;
        }

        public static ConfigState Parse(JsonValue payload)
        {
            var result = new ConfigState();
            if (payload == null || payload.IsNull) return result;

            var state = payload["state"].AsString(null);
            if (!string.IsNullOrEmpty(state)) result.State = state;

            result.Provider = payload["provider"].AsString(null);
            result.Model = payload["model"].AsString(null);
            result.Message = payload["message"].AsString(null);
            return result;
        }

        public override string ToString()
        {
            return $"{State} (provider={Provider ?? "-"}, model={Model ?? "-"})";
        }
    }
}
