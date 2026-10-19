using System;
using System.Collections.Generic;
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

        /// <summary>mod → sidecar：拉取某供应商的可用模型目录（M3）。</summary>
        public const string ModelCatalog = "model.catalog";

        /// <summary>mod → sidecar：检测某供应商的 Key/端点是否可用（M3）。</summary>
        public const string ProviderTest = "provider.test";

        /// <summary>config.state 的取值。</summary>
        public const string ConfigReady = "ready";
        public const string ConfigMissingKey = "missing_key";
        public const string ConfigInvalidKey = "invalid_key";
        public const string ConfigUnreachable = "unreachable";

        /// <summary>mod → sidecar：把对话串绑定到某个存档（附录 A.6）。</summary>
        public const string SaveBind = "save.bind";

        /// <summary>sidecar → mod：绑定结果。</summary>
        public const string SaveBound = "save.bound";

        // ---- M4e 增量：删除对话回合（协议附录 A.7）----

        /// <summary>mod → sidecar：删除一个问答回合。</summary>
        public const string HistoryDelete = "history.delete";

        /// <summary>sidecar → mod：删除结果。</summary>
        public const string HistoryDeleted = "history.deleted";

        /// <summary>sidecar → mod：本地工具的可视化事件（附录 A.8，仅 UI）。</summary>
        public const string ToolActivity = "tool_activity";

        /// <summary>sidecar → mod：蓝图卡片（附录 A.9，仅 UI）。</summary>
        public const string BlueprintCard = "blueprint_card";

        /// <summary>error.payload.code 的 M1 新增取值。</summary>
        public const string CodeInvalidKey = "invalid_key";
        public const string CodeUpstreamError = "upstream_error";
        public const string CodeRateLimited = "rate_limited";
        public const string CodeConfigRequired = "config_required";
        public const string CodeSaveRequired = "save_required";

        // ---- M2 增量：只读工具（协议附录 B）----

        /// <summary>sidecar → mod：要求执行一个游戏工具。</summary>
        public const string ToolCall = "tool_call";

        /// <summary>mod → sidecar：工具执行结果（replyTo = callId）。</summary>
        public const string ToolResult = "tool_result";

        /// <summary>mod 声明的只读游戏工具能力。</summary>
        public const string CapabilityGameRead = "game.read.v1";

        /// <summary>mod 声明的建筑查询能力（game_get_entity / game_get_entities）。</summary>
        public const string CapabilityGameEntity = "game.entity.v1";

        /// <summary>mod 声明的进度查询能力（game_get_progress）。</summary>
        public const string CapabilityGameProgress = "game.progress.v1";

        /// <summary>mod 声明的截图能力（game_capture_view）。</summary>
        public const string CapabilityGameView = "game.view.v1";

        /// <summary>mod 声明的区域分析能力（game_analyze_area）。</summary>
        public const string CapabilityGameAnalyze = "game.analyze.v1";

        /// <summary>tool_result.error.code 的 M2 取值。</summary>
        public const string CodeToolNotFound = "tool_not_found";
        public const string CodeToolUnavailable = "tool_unavailable";
        public const string CodeNotInGame = "not_in_game";
        public const string CodeInvalidArgs = "invalid_args";
        public const string CodeTimeout = "timeout";
        public const string CodeBusy = "busy";
        public const string CodeCancelled = "cancelled";

        // status.state 取值
        public const string StateIdle = "idle";
        public const string StateThinking = "thinking";
        public const string StateTools = "tools";
        public const string StateRetrying = "retrying";
        public const string StateCompacting = "compacting";
        public const string StateAborting = "aborting";

        // 思考档位（与 pi 的 ThinkingLevel 一致；DeepSeek 只映射 high/max）
        public const string ThinkingOff = "off";
        public const string ThinkingHigh = "high";
        public const string ThinkingMax = "max";

        /// <summary>服务端 sidecar 的默认端口。</summary>
        public const int DefaultPort = 8765;

        /// <summary>构造 model.catalog 请求。apiKey 只用于本次探测，sidecar 不回传。</summary>
        public static JsonValue BuildModelCatalogRequest(string provider, string baseUrl, string apiKey)
        {
            var p = JsonValue.NewObject();
            p.Set("provider", provider ?? string.Empty);
            if (!string.IsNullOrEmpty(baseUrl)) p.Set("baseUrl", baseUrl);
            if (!string.IsNullOrEmpty(apiKey)) p.Set("apiKey", apiKey);
            return p;
        }

        /// <summary>构造 provider.test 请求。apiKey 只用于本次探测，sidecar 不回传。</summary>
        public static JsonValue BuildProviderTestRequest(string provider, string baseUrl, string apiKey)
        {
            var p = JsonValue.NewObject();
            p.Set("provider", provider ?? string.Empty);
            if (!string.IsNullOrEmpty(baseUrl)) p.Set("baseUrl", baseUrl);
            if (!string.IsNullOrEmpty(apiKey)) p.Set("apiKey", apiKey);
            return p;
        }

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
        public static JsonValue BuildConfigSet(string provider, string model, string apiKey,
            string baseUrl, string thinkingLevel = null,
            JsonValue providerConfig = null, JsonValue removeProviders = null,
            string activeProvider = null, string activeModel = null)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(provider)) p.Set("provider", provider);
            if (!string.IsNullOrEmpty(model)) p.Set("model", model);
            if (!string.IsNullOrEmpty(apiKey)) p.Set("apiKey", apiKey);
            if (!string.IsNullOrEmpty(baseUrl)) p.Set("baseUrl", baseUrl);
            if (!string.IsNullOrEmpty(thinkingLevel)) p.Set("thinkingLevel", thinkingLevel);
            if (providerConfig != null && !providerConfig.IsNull) p.Set("providerConfig", providerConfig);
            if (removeProviders != null && !removeProviders.IsNull) p.Set("removeProviders", removeProviders);
            if (!string.IsNullOrEmpty(activeProvider)) p.Set("activeProvider", activeProvider);
            if (!string.IsNullOrEmpty(activeModel)) p.Set("activeModel", activeModel);
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

        /// <summary>
        /// 构造 `history.delete` 载荷（附录 A.7）。`timestampMs` 来自历史回放；
        /// 没有时传 0，sidecar 会退化成"最后一条同文本的用户消息"。
        /// </summary>
        public static JsonValue BuildHistoryDelete(string saveKey, string text, long timestampMs)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(saveKey)) p.Set("saveKey", saveKey);
            p.Set("text", text ?? string.Empty);
            if (timestampMs > 0) p.Set("timestamp", timestampMs);
            return p;
        }

        /// <summary>
        /// 构造 tool_result 载荷（协议附录 B）。`error` 与 `meta` 始终写出，
        /// 便于 sidecar 端做统一解析，避免"字段缺失"与"空值"两种形态。
        /// </summary>
        public static JsonValue BuildToolResult(string callId, bool ok, JsonValue data,
            string errorCode, string errorMessage, JsonValue meta, JsonValue image = null)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(callId)) p.Set("callId", callId);
            p.Set("ok", ok);
            p.Set("data", data ?? JsonValue.Null);

            if (ok)
            {
                p.Set("error", JsonValue.Null);
            }
            else
            {
                var error = JsonValue.NewObject();
                error.Set("code", string.IsNullOrEmpty(errorCode) ? CodeToolUnavailable : errorCode);
                error.Set("message", errorMessage ?? string.Empty);
                p.Set("error", error);
            }

            // 附件只在成功时出现；失败结果带图会让模型对"成功了什么"产生错觉。
            if (ok && image != null && !image.IsNull) p.Set("image", image);

            p.Set("meta", meta ?? JsonValue.NewObject());
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

    /// <summary>存档绑定结果里用于回放的一条历史消息（M2.7）。</summary>
    /// <summary>`tool_activity` 的解析结果（附录 A.8）。字段缺失一律容忍。</summary>
    public sealed class ToolActivityInfo
    {
        public string CallId { get; private set; }
        public string Phase { get; private set; }
        public string Name { get; private set; }
        public string Args { get; private set; }
        public bool Ok { get; private set; }
        public string Summary { get; private set; }
        public string Detail { get; private set; }

        public static ToolActivityInfo Parse(JsonValue payload)
        {
            var r = new ToolActivityInfo();
            if (payload == null || payload.IsNull) return r;
            r.CallId = payload["callId"].AsString(null);
            r.Phase = payload["phase"].AsString("end");
            r.Name = payload["name"].AsString(string.Empty);
            r.Args = payload["args"].AsString(null);
            r.Ok = payload["ok"].AsBool(false);
            r.Summary = payload["summary"].AsString(null);
            r.Detail = payload["detail"].AsString(null);
            return r;
        }
    }

    /// <summary>
    /// `blueprint_card` 的解析结果（附录 A.9）。
    ///
    /// 所有字段都是可选的（`fileName` 除外）；字段缺失/类型不对一律当没有，
    /// 由 UI 决定显示哪几项。**不在 Shared 里碰 Unity**。
    /// </summary>
    public sealed class BlueprintCardInfo
    {
        public string Path { get; private set; }
        public string FileName { get; private set; }
        public string Name { get; private set; }
        public string Item { get; private set; }
        public int ItemId { get; private set; }
        public double Rate { get; private set; }
        public int Buildings { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Author { get; private set; }
        public string Version { get; private set; }
        public string Attributes { get; private set; }
        public string Description { get; private set; }
        /// <summary>`generated` | `downloaded` | `copied`；未知/缺失为 null。</summary>
        public string Source { get; private set; }
        public string License { get; private set; }
        public string Repository { get; private set; }

        /// <summary>卡片标题：蓝图名 → 文件名（去 .txt）→ 兜底。</summary>
        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                var file = FileName;
                if (string.IsNullOrEmpty(file)) return "蓝图";
                return file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(0, file.Length - 4)
                    : file;
            }
        }

        public static BlueprintCardInfo Parse(JsonValue payload)
        {
            var r = new BlueprintCardInfo();
            if (payload == null || payload.IsNull) return r;
            r.Path = payload["path"].AsString(null);
            r.FileName = payload["fileName"].AsString(null);
            r.Name = payload["name"].AsString(null);
            r.Item = payload["item"].AsString(null);
            r.ItemId = payload["itemId"].AsInt(0);
            r.Rate = payload["rate"].AsDouble(0);
            r.Buildings = payload["buildings"].AsInt(0);
            r.Width = payload["width"].AsInt(0);
            r.Height = payload["height"].AsInt(0);
            r.Author = payload["author"].AsString(null);
            r.Version = payload["version"].AsString(null);
            r.Attributes = payload["attributes"].AsString(null);
            r.Description = payload["description"].AsString(null);
            r.Source = payload["source"].AsString(null);
            r.License = payload["license"].AsString(null);
            r.Repository = payload["repository"].AsString(null);
            return r;
        }
    }

    /// <summary>`history.deleted` 的解析结果（附录 A.7）。</summary>
    public sealed class HistoryDeletedInfo
    {
        public string SaveKey { get; private set; }
        public string Text { get; private set; }
        public bool Ok { get; private set; }
        public int Removed { get; private set; }
        public string Error { get; private set; }

        public static HistoryDeletedInfo Parse(JsonValue payload)
        {
            var r = new HistoryDeletedInfo();
            if (payload == null || payload.IsNull) return r;
            r.SaveKey = payload["saveKey"].AsString(null);
            r.Text = payload["text"].AsString(string.Empty);
            r.Ok = payload["ok"].AsBool(false);
            r.Removed = payload["removed"].AsInt(0);
            r.Error = payload["error"].AsString(null);
            return r;
        }
    }

    public sealed class HistoryMessageInfo
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public long Timestamp { get; set; }
        /// <summary>该回答产出的蓝图卡片（附录 A.6/A.9，可选）。</summary>
        public List<BlueprintCardInfo> Cards { get; set; } = new List<BlueprintCardInfo>();
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
        /// <summary>最近若干条可回放的 user/assistant 文本；只用于 UI，不进入模型上下文。</summary>
        public List<HistoryMessageInfo> History { get; private set; } = new List<HistoryMessageInfo>();

        public static SaveBoundInfo Parse(JsonValue payload)
        {
            var r = new SaveBoundInfo();
            if (payload == null || payload.IsNull) return r;
            r.SaveKey = payload["saveKey"].AsString(null);
            r.SaveName = payload["saveName"].AsString(null);
            r.Existed = payload["existed"].AsBool(false);
            r.MessageCount = payload["messageCount"].AsInt(0);

            var history = payload["history"];
            if (history != null && history.Count > 0)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    var item = history[i];
                    var role = item["role"].AsString(null);
                    if (role != "user" && role != "assistant") continue;
                    var text = item["text"].AsString(null);
                    if (string.IsNullOrEmpty(text)) continue;
                    var info = new HistoryMessageInfo
                    {
                        Role = role,
                        Text = text,
                        Timestamp = item["timestamp"].AsLong(0),
                    };
                    var cards = item["cards"];
                    if (cards != null && cards.Count > 0)
                    {
                        for (int c = 0; c < cards.Count; c++)
                        {
                            var card = BlueprintCardInfo.Parse(cards[c]);
                            if (card != null && (!string.IsNullOrEmpty(card.FileName) || !string.IsNullOrEmpty(card.Path)))
                                info.Cards.Add(card);
                        }
                    }
                    r.History.Add(info);
                }
            }
            return r;
        }

        public override string ToString()
        {
            return $"{SaveName ?? SaveKey ?? "(无存档)"} existed={Existed} messages={MessageCount} history={History.Count}";
        }
    }

    /// <summary>
    /// sidecar 上报的配置状态。同时用于 ready.payload.config（附录 A.2）
    /// 与 config.state（附录 A.1）——两者载荷结构一致。
    /// </summary>
    public sealed class ModelInfo
    {
        public string Id { get; private set; }
        public string Name { get; private set; }

        public static ModelInfo Parse(JsonValue value)
        {
            if (value == null || !value.IsObject) return null;
            var id = value["id"].AsString(null);
            if (string.IsNullOrEmpty(id)) return null;
            return new ModelInfo
            {
                Id = id,
                Name = value["name"].AsString(id),
            };
        }

        public static ModelInfo Create(string id, string name = null)
        {
            return new ModelInfo { Id = id, Name = string.IsNullOrEmpty(name) ? id : name };
        }

        public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    /// <summary>model.catalog 的响应（模型目录，绝不含 apiKey）。</summary>
    public sealed class ModelCatalogResult
    {
        public string Provider { get; private set; }
        public List<ModelInfo> Models { get; private set; }
        public string Source { get; private set; }
        public string Error { get; private set; }

        public ModelCatalogResult()
        {
            Models = new List<ModelInfo>();
        }

        public static ModelCatalogResult Parse(JsonValue value)
        {
            var result = new ModelCatalogResult();
            if (value == null || !value.IsObject) return result;
            result.Provider = value["provider"].AsString(null);
            result.Source = value["source"].AsString(null);
            result.Error = value["error"].AsString(null);

            var models = value["models"];
            if (models != null && models.IsArray)
            {
                foreach (var item in models.Items)
                {
                    var model = ModelInfo.Parse(item);
                    if (model != null) result.Models.Add(model);
                }
            }
            return result;
        }
    }

    /// <summary>provider.test 的响应（Key/连通性检测，绝不含 apiKey）。</summary>
    public sealed class ProviderTestResult
    {
        public string Provider { get; private set; }
        public bool Ok { get; private set; }
        public int Status { get; private set; }
        public int LatencyMs { get; private set; }
        public string Message { get; private set; }
        public string Source { get; private set; }

        public static ProviderTestResult Parse(JsonValue value)
        {
            var result = new ProviderTestResult();
            if (value == null || !value.IsObject) return result;
            result.Provider = value["provider"].AsString(null);
            result.Ok = value["ok"].AsBool(false);
            result.Status = value["status"].AsInt(0);
            result.LatencyMs = value["latencyMs"].AsInt(0);
            result.Message = value["message"].AsString(null);
            result.Source = value["source"].AsString(null);
            return result;
        }
    }

    public sealed class ProviderInfo
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string BaseUrl { get; private set; }
        public bool HasKey { get; private set; }
        public bool IsActive { get; private set; }
        public List<ModelInfo> Models { get; private set; }

        public static ProviderInfo Parse(JsonValue value)
        {
            if (value == null || !value.IsObject) return null;
            var id = value["id"].AsString(null);
            if (string.IsNullOrEmpty(id)) return null;

            var provider = new ProviderInfo
            {
                Id = id,
                Name = value["name"].AsString(id),
                BaseUrl = value["baseUrl"].AsString(null),
                HasKey = value["hasKey"].AsBool(false),
                IsActive = value["isActive"].AsBool(false),
                Models = new List<ModelInfo>(),
            };
            var models = value["models"];
            if (models != null && models.IsArray)
            {
                foreach (var item in models.Items)
                {
                    var model = ModelInfo.Parse(item);
                    if (model != null) provider.Models.Add(model);
                }
            }
            return provider;
        }

        public static ProviderInfo Create(string id, string name, bool hasKey, bool isActive)
        {
            return new ProviderInfo
            {
                Id = id,
                Name = string.IsNullOrEmpty(name) ? id : name,
                HasKey = hasKey,
                IsActive = isActive,
                Models = new List<ModelInfo>(),
            };
        }

        public override string ToString() => $"{Name ?? Id} ({Models?.Count ?? 0} models)";
    }

    public sealed class ConfigState
    {
        public string State { get; private set; }
        public string Provider { get; private set; }
        public string Model { get; private set; }

        /// <summary>
        /// 多供应商列表（M3）。老服务端不返回时为空，调用方回退到
        /// <see cref="Provider"/> / <see cref="Model"/> 单条。
        /// </summary>
        public List<ProviderInfo> Providers { get; private set; }

        /// <summary>
        /// 思考档位（协议附录 A.1 新增，可选）。老服务端不返回时保持 null，
        /// 调用方应回退到本地配置/`off`。
        /// </summary>
        public string ThinkingLevel { get; private set; }

        public string Message { get; private set; }

        public ConfigState()
        {
            State = Protocol.ConfigMissingKey;
            Providers = new List<ProviderInfo>();
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
            result.ThinkingLevel = payload["thinkingLevel"].AsString(null);
            result.Message = payload["message"].AsString(null);

            var providers = payload["providers"];
            if (providers != null && providers.IsArray)
            {
                foreach (var item in providers.Items)
                {
                    var provider = ProviderInfo.Parse(item);
                    if (provider != null) result.Providers.Add(provider);
                }
            }

            // 老服务端只返回 provider/model：合成为一条，UI 仍能显示"模型"下拉框。
            if (result.Providers.Count == 0 && !string.IsNullOrEmpty(result.Provider))
            {
                var fallback = ProviderInfo.Create(result.Provider, result.Provider, result.IsReady, true);
                if (!string.IsNullOrEmpty(result.Model))
                    fallback.Models.Add(ModelInfo.Create(result.Model));
                result.Providers.Add(fallback);
            }

            return result;
        }

        public override string ToString()
        {
            return $"{State} (provider={Provider ?? "-"}, model={Model ?? "-"})";
        }
    }

    /// <summary>
    /// 解析后的 tool_call（协议附录 B）。
    /// `callId` 允许只出现在信封 replyTo 里；两者都有时以 payload 为准。
    /// </summary>
    public sealed class ToolCallInfo
    {
        public string CallId { get; private set; }
        public string Name { get; private set; }
        public JsonValue Args { get; private set; }
        public int Risk { get; private set; }
        public int TimeoutMs { get; private set; }
        /// <summary>sidecar 内部调用（如知识库导出），UI 不显示工具卡。</summary>
        public bool Internal { get; private set; }

        public static ToolCallInfo Parse(JsonValue payload, string replyTo)
        {
            if (payload == null || payload.IsNull || !payload.IsObject) return null;

            var name = payload["name"].AsString(null);
            if (string.IsNullOrEmpty(name)) return null;

            var callId = payload["callId"].AsString(null);
            if (string.IsNullOrEmpty(callId)) callId = replyTo;
            if (string.IsNullOrEmpty(callId)) return null;

            var args = payload["args"];
            if (!args.IsObject) args = JsonValue.NewObject();

            var timeout = payload["timeoutMs"].AsInt(8000);
            if (timeout < 500) timeout = 500;
            if (timeout > 30000) timeout = 30000;

            return new ToolCallInfo
            {
                CallId = callId,
                Name = name,
                Args = args,
                Risk = payload["risk"].AsInt(0),
                TimeoutMs = timeout,
                Internal = payload["internal"].AsBool(false),
            };
        }
    }
}
