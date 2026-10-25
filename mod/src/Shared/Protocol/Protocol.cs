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

        // ---- 上下文压缩（协议附录 A.11）----

        /// <summary>sidecar → mod：一次上下文压缩完成；只给 UI，不进入模型上下文。</summary>
        public const string ContextCompacted = "context.compacted";

        /// <summary>mod → sidecar：撤销最近一次上下文压缩。</summary>
        public const string HistoryUndoCompaction = "history.undo_compaction";

        /// <summary>sidecar → mod：撤销压缩结果。</summary>
        public const string HistoryCompactionUndone = "history.compaction_undone";

        /// <summary>sidecar → mod：本地工具的可视化事件（附录 A.8，仅 UI）。</summary>
        public const string ToolActivity = "tool_activity";

        /// <summary>sidecar → mod：蓝图卡片（附录 A.9，仅 UI）。</summary>
        public const string BlueprintCard = "blueprint_card";

        /// <summary>sidecar → mod：实体链接卡片（建筑/星球；仅 UI）。</summary>
        public const string LinkCard = "link_card";

        /// <summary>mod → sidecar：回存回答完成时的游戏时间（附录 A.10）。</summary>
        public const string ReplyTime = "reply.time";

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

        // ---- M4m 增量：调试直调（不经过 AI）----

        /// <summary>mod → sidecar：输入框 !tool 的调试直调请求。</summary>
        public const string ToolDebug = "tool_debug";

        /// <summary>sidecar → mod：调试直调的原始结果（replyTo = 请求 id）。</summary>
        public const string ToolDebugResult = "tool_debug_result";

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

        /// <summary>mod 支持游戏内结构化提问（ask_user，附录 B.1.4）。</summary>
        public const string CapabilityUiAsk = "ui.ask.v1";

        /// <summary>mod 支持写操作（附录 B.1.5）：确认令牌 + 写工具。</summary>
        public const string CapabilityGameWrite = "game.write.v1";

        /// <summary>mod 支持沙盒门禁：save.bind 上报 sandbox，并拒绝沙盒专属工具。</summary>
        public const string CapabilityGameSandbox = "game.sandbox.v1";

        /// <summary>mod 支持丢弃物品（game_drop_item）。新工具用独立能力串，避免老 mod 被注册。</summary>
        public const string CapabilityGameDrop = "game.drop.v1";

        /// <summary>mod 支持生产建筑写操作（改配方/批量改配方）。</summary>
        public const string CapabilityGameBuild = "game.build.v1";

        /// <summary>mod 支持存储建筑写操作（过滤器/容量上限）。</summary>
        public const string CapabilityGameStorage = "game.storage.v1";

        /// <summary>mod 支持方位指示（tether）。</summary>
        public const string CapabilityGameTether = "game.tether.v1";

        /// <summary>mod 支持受控写脚本（run_script 脚本级确认 + 租约）。</summary>
        public const string CapabilityScriptWrite = "script.write.v1";

        /// <summary>sidecar → mod：请求玩家确认一次写操作。</summary>
        public const string ConfirmRequest = "confirm_request";

        /// <summary>mod → sidecar：玩家确认结果。</summary>
        public const string ConfirmResult = "confirm_result";

        /// <summary>sidecar → mod：脚本执行结束，释放脚本写租约。</summary>
        public const string ConfirmRelease = "confirm_release";

        public const string ToolFocusCamera = "game_focus_camera";
        public const string ToolPlaceMarker = "game_place_marker";
        public const string ToolOpenPanel = "game_open_panel";
        public const string ToolSetResearch = "game_set_research";
        public const string ToolUndoLastWrite = "game_undo_last_write";
        public const string ToolDropItem = "game_drop_item";
        public const string ToolSetRecipe = "game_set_recipe";
        public const string ToolSetAssemblerBatch = "game_set_assembler_batch";
        public const string ToolStorageSetFilter = "game_storage_set_filter";
        public const string ToolStorageSetLimit = "game_storage_set_limit";
        public const string ToolStorageMove = "game_storage_move";
        public const string ToolStorageDeposit = "game_storage_deposit";
        public const string ToolStorageWithdraw = "game_storage_withdraw";
        public const string ToolFeedInput = "game_feed_input";
        public const string ToolTetherAdd = "game_tether_add";
        public const string ToolTetherClear = "game_tether_clear";
        public const string ToolTetherList = "game_tether_list";
        public const string ToolTakeOutput = "game_take_output";

        /// <summary>沙盒专属工具名（尚未实现；先用于门禁/测试）。</summary>
        public const string ToolSpawnItem = "game_spawn_item";
        public const string ToolTeleport = "game_teleport";

        /// <summary>工具是否只能在游戏沙盒工具启用时调用。</summary>
        public static bool IsSandboxOnlyTool(string name)
        {
            return string.Equals(name, ToolSpawnItem, StringComparison.Ordinal)
                || string.Equals(name, ToolTeleport, StringComparison.Ordinal);
        }

        /// <summary>当前沙盒状态是否允许调用该工具（读工具与普通写工具始终允许）。</summary>
        public static bool SandboxAllows(string toolName, bool sandboxEnabled)
        {
            return sandboxEnabled || !IsSandboxOnlyTool(toolName);
        }

        /// <summary>
        /// 需要确认令牌的写工具**唯一来源**。
        /// mod 的确认门禁、脚本租约白名单与分发器缓存都必须引用这张表，
        /// 不要再在 Plugin / Dispatcher / ConfirmationService 各抄一份。
        /// </summary>
        public static readonly string[] WriteToolNames =
        {
            ToolFocusCamera,
            ToolPlaceMarker,
            ToolOpenPanel,
            ToolSetResearch,
            ToolDropItem,
            ToolSetRecipe,
            ToolSetAssemblerBatch,
            ToolStorageSetFilter,
            ToolStorageSetLimit,
            ToolStorageMove,
            ToolStorageDeposit,
            ToolStorageWithdraw,
            ToolFeedInput,
            ToolTetherAdd,
            ToolTetherClear,
            ToolTakeOutput,
            ToolUndoLastWrite,
        };

        public static bool IsWriteTool(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            for (int i = 0; i < WriteToolNames.Length; i++)
                if (string.Equals(WriteToolNames[i], toolName, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>确认请求默认有效期（设计 §9.3）。</summary>
        public const int ConfirmTimeoutMs = 60000;

        /// <summary>脚本写租约有效期；覆盖脚本最大运行时间并留出释放余量。</summary>
        public const int ScriptLeaseMs = 300000;

        /// <summary>
        /// 交互工具名：sidecar 通过 tool_call 要求 mod 弹出选择题。
        /// 与 Pi 社区的 `pi-ask-user` 包保持同名；mod 在进入 GameToolDispatcher 前拦截。
        /// </summary>
        public const string ToolAskUser = "ask_user";

        /// <summary>sidecar 本地脚本工具名；脚本写租约只允许由它创建。</summary>
        public const string ToolRunScript = "run_script";

        /// <summary>旧名兼容：早期实现使用 ui_ask_user，继续接受。</summary>
        public const string ToolUiAskUser = "ui_ask_user";

        /// <summary>ask 工具允许的最长等待（玩家阅读/输入时间）。</summary>
        public const int AskTimeoutMs = 600000;

        /// <summary>mod 支持 Markdown + 小型 HTML 子集的输出富文本（可选）。</summary>
        public const string CapabilityRichTextV2 = "ui.richtext.v2";

        /// <summary>tool_result.error.code 的 M2 取值。</summary>
        public const string CodeToolNotFound = "tool_not_found";
        public const string CodeToolUnavailable = "tool_unavailable";
        public const string CodeNotInGame = "not_in_game";
        public const string CodeInvalidArgs = "invalid_args";
        public const string CodeTimeout = "timeout";
        public const string CodeBusy = "busy";
        public const string CodeCancelled = "cancelled";
        public const string CodeDebugDisabled = "debug_disabled";
        public const string CodeDebugNotAllowed = "debug_not_allowed";

        /// <summary>M4l：prompt.requiredTool 指定的工具本轮没有被调用。</summary>
        public const string CodeToolRequired = "tool_required";
        public const string CodeConfirmationRequired = "confirmation_required";
        public const string CodeSandboxRequired = "sandbox_required";
        public const string CodeConfirmationInvalid = "confirmation_invalid";
        public const string CodeConfirmationExpired = "confirmation_expired";
        public const string CodeConfirmationDenied = "confirmation_denied";

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

        public static JsonValue BuildPrompt(string text, string conversationId, string requiredTool = null)
        {
            var p = JsonValue.NewObject();
            p.Set("text", text ?? string.Empty);
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            // M4l：玩家用 /<工具名> 指定本轮必须调用的工具。空值不写，老端忽略。
            if (!string.IsNullOrEmpty(requiredTool)) p.Set("requiredTool", requiredTool);
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
        public static JsonValue BuildSaveBind(string saveKey, string saveName,
            JsonValue blueprintTiers = null, bool? sandbox = null)
        {
            var p = JsonValue.NewObject();
            p.Set("saveKey", saveKey == null ? JsonValue.Null : JsonValue.Of(saveKey));
            if (!string.IsNullOrEmpty(saveName)) p.Set("saveName", saveName);
            if (blueprintTiers != null && !blueprintTiers.IsNull) p.Set("blueprintTiers", blueprintTiers);
            if (sandbox.HasValue) p.Set("sandbox", sandbox.Value);
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
        /// 构造 `history.undo_compaction` 载荷（附录 A.11）。
        /// `compactionId` 为空表示撤销最近一次压缩。
        /// </summary>
        public static JsonValue BuildHistoryUndoCompaction(string saveKey, string compactionId)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(saveKey)) p.Set("saveKey", saveKey);
            if (!string.IsNullOrEmpty(compactionId)) p.Set("compactionId", compactionId);
            return p;
        }

        /// <summary>构造 tool_debug 载荷（M4m）。参数必须是 JSON 对象。</summary>
        public static JsonValue BuildToolDebug(string name, JsonValue args)
        {
            var p = JsonValue.NewObject();
            p.Set("name", name ?? string.Empty);
            p.Set("args", args ?? JsonValue.NewObject());
            return p;
        }

        /// <summary>构造 reply.time 载荷（协议附录 A.10）。</summary>
        public static JsonValue BuildReplyTime(string prompt, string reply, string gameTime)
        {
            var p = JsonValue.NewObject();
            p.Set("prompt", prompt ?? string.Empty);
            p.Set("reply", reply ?? string.Empty);
            p.Set("gameTime", gameTime ?? string.Empty);
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

    /// <summary>`tool_debug_result` 的解析结果（M4m）。字段缺失一律容忍。</summary>
    public sealed class ToolDebugResultInfo
    {
        public bool Ok { get; private set; }
        public string Tool { get; private set; }
        public string Source { get; private set; }
        public long DurationMs { get; private set; }
        public string Content { get; private set; }
        public JsonValue Details { get; private set; }
        public bool Truncated { get; private set; }
        public string ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; }

        public string ImageMimeType { get; private set; }
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }
        public long ImageBytes { get; private set; }

        public string DetailsText()
        {
            if (Details == null || Details.IsNull) return "null";
            return Details.IsString ? Details.AsString() : Details.ToJson();
        }

        public static ToolDebugResultInfo Parse(JsonValue payload)
        {
            var r = new ToolDebugResultInfo();
            if (payload == null || payload.IsNull) return r;
            r.Ok = payload["ok"].AsBool(false);
            r.Tool = payload["tool"].AsString(string.Empty);
            r.Source = payload["source"].AsString("unknown");
            r.DurationMs = payload["durationMs"].AsLong(0);
            r.Content = payload["content"].AsString(string.Empty);
            r.Details = payload["details"];
            r.Truncated = payload["truncated"].AsBool(false);

            var error = payload["error"];
            if (error != null && error.IsObject)
            {
                r.ErrorCode = error["code"].AsString(null);
                r.ErrorMessage = error["message"].AsString(null);
            }

            var image = payload["image"];
            if (image != null && image.IsObject)
            {
                r.ImageMimeType = image["mimeType"].AsString(null);
                r.ImageWidth = image["width"].AsInt(0);
                r.ImageHeight = image["height"].AsInt(0);
                r.ImageBytes = image["bytes"].AsLong(0);
            }
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
        /// <summary>相对蓝图根的 POSIX 路径，例如 IcarusII/a.txt；老卡片为 null。</summary>
        public string RelPath { get; private set; }
        public string FileName { get; private set; }
        public string Name { get; private set; }
        public string Item { get; private set; }
        public int ItemId { get; private set; }
        public double Rate { get; private set; }
        public int Buildings { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Author { get; private set; }
        /// <summary>卡片本地创建日期（yyyy-MM-dd）；协议不传时由 UI/历史时间补。</summary>
        public string CreatedAt { get; set; }
        public string Version { get; private set; }
        public string Attributes { get; private set; }
        public string Description { get; private set; }
        /// <summary>`generated` | `downloaded` | `copied` | `written`；未知/缺失为 null。</summary>
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
            r.RelPath = payload["relPath"].AsString(null);
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

    /// <summary>`context.compacted` 的解析结果（附录 A.11）。</summary>
    public sealed class ContextCompactedInfo
    {
        public string Summary { get; private set; }
        public string CompactionId { get; private set; }
        public long Timestamp { get; private set; }
        public int TokensBefore { get; private set; }
        public int EstimatedTokensAfter { get; private set; }
        public string Reason { get; private set; }

        public static ContextCompactedInfo Parse(JsonValue payload)
        {
            if (payload == null || payload.IsNull) return null;
            var summary = payload["summary"].AsString(null);
            if (string.IsNullOrEmpty(summary)) return null;
            var r = new ContextCompactedInfo
            {
                Summary = summary,
                CompactionId = payload["compactionId"].AsString(null),
                Timestamp = payload["timestamp"].AsLong(0),
                TokensBefore = payload["tokensBefore"].AsInt(0),
                EstimatedTokensAfter = payload["estimatedTokensAfter"].AsInt(0),
                Reason = payload["reason"].AsString(null),
            };
            return r;
        }
    }

    /// <summary>`history.compaction_undone` 的解析结果（附录 A.11）。</summary>
    public sealed class HistoryCompactionUndoneInfo
    {
        public string SaveKey { get; private set; }
        public string CompactionId { get; private set; }
        public bool Ok { get; private set; }
        public int Removed { get; private set; }
        public string Error { get; private set; }
        public bool MayRecompact { get; private set; }

        public static HistoryCompactionUndoneInfo Parse(JsonValue payload)
        {
            var r = new HistoryCompactionUndoneInfo();
            if (payload == null || payload.IsNull) return r;
            r.SaveKey = payload["saveKey"].AsString(null);
            r.CompactionId = payload["compactionId"].AsString(null);
            r.Ok = payload["ok"].AsBool(false);
            r.Removed = payload["removed"].AsInt(0);
            r.Error = payload["error"].AsString(null);
            r.MayRecompact = payload["mayRecompact"].AsBool(false);
            return r;
        }
    }

    /// <summary>
    /// 历史回答里的一条思考/工具步骤（协议附录 A.6 的 <c>steps</c>）。
    ///
    /// sidecar 从 pi 会话记录里按发生顺序提取：思考块和工具调用会夹在正文之前，
    /// mod 回放时逐条重建成折叠块（默认收起），玩家点开仍能看到原文。
    /// </summary>
    public sealed class HistoryStepInfo
    {
        /// <summary><c>thinking</c> = 思考段；<c>tool</c> = 一次工具调用。</summary>
        public string Kind { get; set; }
        /// <summary>思考正文（<c>thinking</c>）。</summary>
        public string Text { get; set; }
        /// <summary>工具名（<c>tool</c>），由 UI 层映射成中文短名。</summary>
        public string Name { get; set; }
        /// <summary>工具参数（<c>tool</c>，已由 sidecar 截断）。</summary>
        public string Args { get; set; }
        /// <summary>
        /// 工具是否成功（<c>tool</c>）。null = 会话里没有对应结果
        /// （回答被中断 / 存档关闭），回放时显示为“读取中…”。
        /// </summary>
        public bool? Ok { get; set; }
        public string Summary { get; set; }
        public string Detail { get; set; }

        public static HistoryStepInfo Parse(JsonValue value)
        {
            if (value == null || !value.IsObject) return null;
            var kind = value["kind"].AsString(null);
            if (kind == "thinking")
            {
                var text = value["text"].AsString(null);
                if (string.IsNullOrEmpty(text)) return null;
                return new HistoryStepInfo { Kind = kind, Text = text };
            }
            if (kind == "tool")
            {
                var name = value["name"].AsString(null);
                if (string.IsNullOrEmpty(name)) return null;
                var rawOk = value["ok"];
                var ok = rawOk != null && !rawOk.IsNull ? (bool?)rawOk.AsBool(false) : null;
                return new HistoryStepInfo
                {
                    Kind = kind,
                    Name = name,
                    Args = value["args"].AsString(null),
                    Ok = ok,
                    Summary = value["summary"].AsString(null),
                    Detail = value["detail"].AsString(null),
                };
            }
            return null;
        }
    }

    /// <summary>`link_card` 的解析结果：只描述实体，不携带图标资源。</summary>
    public sealed class LinkCardInfo
    {
        public string Kind { get; private set; }
        public int PlanetId { get; private set; }
        public int BuildingId { get; private set; }
        public int VeinId { get; private set; }
        public string Name { get; private set; }
        public string Subtitle { get; private set; }
        public string Description { get; private set; }
        public string IconLabel { get; private set; }

        public static LinkCardInfo Parse(JsonValue payload)
        {
            if (payload == null || payload.IsNull) return null;
            var r = new LinkCardInfo
            {
                Kind = payload["kind"].AsString(null),
                PlanetId = payload["planetId"].AsInt(0),
                BuildingId = payload["buildingId"].AsInt(0),
                VeinId = payload["veinId"].AsInt(0),
                Name = payload["name"].AsString(null),
                Subtitle = payload["subtitle"].AsString(null),
                Description = payload["description"].AsString(null),
                IconLabel = payload["iconLabel"].AsString(null),
            };
            if (r.Kind != "planet" && r.Kind != "building" && r.Kind != "vein") return null;
            if (r.PlanetId <= 0) return null;
            if (string.IsNullOrWhiteSpace(r.Name)) return null;
            if (r.Kind == "building" && r.BuildingId <= 0) return null;
            if (r.Kind == "vein" && r.VeinId <= 0) return null;
            return r;
        }
    }

    public sealed class HistoryMessageInfo
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public long Timestamp { get; set; }
        /// <summary>回答完成时的游戏时间 HH:mm:ss（附录 A.6/A.10，可选）。</summary>
        public string GameTime { get; set; }
        /// <summary>
        /// `compaction` = 上下文压缩记录（附录 A.11）；普通回答为 null。
        /// 老 sidecar 不发送该字段。
        /// </summary>
        public string Kind { get; set; }
        /// <summary>压缩记录对应的 compaction entry id（撤销时用，可选）。</summary>
        public string CompactionId { get; set; }
        /// <summary>压缩前/后的估算 token（只用于展示）。</summary>
        public int TokensBefore { get; set; }
        public int EstimatedTokensAfter { get; set; }
        /// <summary>该回答产出的蓝图卡片（附录 A.6/A.9，可选）。</summary>
        public List<BlueprintCardInfo> Cards { get; set; } = new List<BlueprintCardInfo>();
        /// <summary>该回答产出的实体链接卡片（可选）。</summary>
        public List<LinkCardInfo> LinkCards { get; set; } = new List<LinkCardInfo>();
        /// <summary>
        /// 该回答之前产生的思考/工具步骤，按发生顺序（附录 A.6，可选）。
        /// 老 sidecar 不带这个字段时为空列表。
        /// </summary>
        public List<HistoryStepInfo> Steps { get; set; } = new List<HistoryStepInfo>();
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
                        GameTime = item["gameTime"].AsString(null),
                        Kind = item["kind"].AsString(null),
                        CompactionId = item["compactionId"].AsString(null),
                        TokensBefore = item["tokensBefore"].AsInt(0),
                        EstimatedTokensAfter = item["estimatedTokensAfter"].AsInt(0),
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
                    var linkCards = item["linkCards"];
                    if (linkCards != null && linkCards.Count > 0)
                    {
                        for (int c = 0; c < linkCards.Count; c++)
                        {
                            var card = LinkCardInfo.Parse(linkCards[c]);
                            if (card != null) info.LinkCards.Add(card);
                        }
                    }
                    // 思考/工具步骤：老 sidecar 不发送，空数组即可。
                    var steps = item["steps"];
                    if (steps != null && steps.Count > 0)
                    {
                        for (int s = 0; s < steps.Count; s++)
                        {
                            var step = HistoryStepInfo.Parse(steps[s]);
                            if (step != null) info.Steps.Add(step);
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
        /// <summary>sidecar 探测成功后给出的「聊天可用地址」（可能为空）。</summary>
        public string ResolvedBaseUrl { get; private set; }

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
            result.ResolvedBaseUrl = value["resolvedBaseUrl"].AsString(null);

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
        /// <summary>sidecar 探测成功后给出的「聊天可用地址」（可能为空）。</summary>
        public string ResolvedBaseUrl { get; private set; }

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
            result.ResolvedBaseUrl = value["resolvedBaseUrl"].AsString(null);
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
        /// <summary>写操作的确认令牌；risk &gt; 0 时必需。</summary>
        public string ConfirmToken { get; private set; }
        /// <summary>确认时绑定的参数哈希；与令牌一起校验参数未被替换。</summary>
        public string ConfirmArgsHash { get; private set; }

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

            var isAsk = name == Protocol.ToolAskUser || name == Protocol.ToolUiAskUser;
            var timeout = payload["timeoutMs"].AsInt(isAsk ? Protocol.AskTimeoutMs : 8000);
            if (timeout < 500) timeout = 500;
            var maxTimeout = isAsk ? Protocol.AskTimeoutMs : 30000;
            if (timeout > maxTimeout) timeout = maxTimeout;

            return new ToolCallInfo
            {
                CallId = callId,
                Name = name,
                Args = args,
                Risk = payload["risk"].AsInt(0),
                TimeoutMs = timeout,
                Internal = payload["internal"].AsBool(false),
                ConfirmToken = payload["confirmToken"].AsString(null),
                ConfirmArgsHash = payload["confirmArgsHash"].AsString(null),
            };
        }
    }


    /// <summary>sidecar → mod 的写操作确认请求（协议附录 A.12）。</summary>
    public sealed class ConfirmRequestInfo
    {
        public string RequestId { get; private set; }
        public string CallId { get; private set; }
        public string Tool { get; private set; }
        public string ArgsHash { get; private set; }
        public string Token { get; private set; }
        public string Title { get; private set; }
        public string Body { get; private set; }
        public int Risk { get; private set; }
        public int TimeoutMs { get; private set; }
        /// <summary>脚本写租约允许的写调用次数；0 = 普通单次确认。</summary>
        public int MaxWrites { get; private set; }
        /// <summary>脚本确认时列出的写工具名；租约只允许这些工具。</summary>
        public List<string> WriteNames { get; private set; } = new List<string>();

        public static ConfirmRequestInfo Parse(JsonValue payload)
        {
            if (payload == null || !payload.IsObject) return null;
            var requestId = payload["requestId"].AsString(null);
            var callId = payload["callId"].AsString(null);
            var tool = payload["tool"].AsString(null);
            var token = payload["token"].AsString(null);
            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(callId) ||
                string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(token)) return null;
            var timeout = payload["timeoutMs"].AsInt(Protocol.ConfirmTimeoutMs);
            if (timeout < 500) timeout = 500;
            if (timeout > Protocol.ConfirmTimeoutMs) timeout = Protocol.ConfirmTimeoutMs;
            var request = new ConfirmRequestInfo
            {
                RequestId = requestId,
                CallId = callId,
                Tool = tool,
                ArgsHash = payload["argsHash"].AsString(string.Empty),
                Token = token,
                Title = payload["title"].AsString("确认操作"),
                Body = payload["body"].AsString(string.Empty),
                Risk = payload["risk"].AsInt(1),
                TimeoutMs = timeout,
                MaxWrites = Math.Max(0, Math.Min(10000, payload["maxWrites"].AsInt(0))),
            };
            var writeNames = payload["writeNames"];
            if (writeNames != null && writeNames.IsArray)
            {
                for (int i = 0; i < writeNames.Count; i++)
                {
                    var name = writeNames[i].AsString(null);
                    if (!string.IsNullOrEmpty(name)) request.WriteNames.Add(name);
                }
            }
            return request;
        }
    }

}
