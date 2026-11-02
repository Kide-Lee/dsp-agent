using System;
using System.Collections.Generic;
using IcarusII.Common.Json;

namespace IcarusII.Common.Protocol
{
    /// <summary>
    /// hello.extensions 的一条：第三方插件摘要。
    /// `Id` 用于工具名（`mod_&lt;id&gt;_config`），由 mod 生成并保证唯一。
    /// </summary>
    public sealed class ExtensionSummary
    {
        public string Id;
        public string Guid;
        public string Name;
        public string Version;
        public int ConfigCount;
        /// <summary>manifest 声明的工具（P2）；没有 manifest 时为空数组。</summary>
        public ExtensionToolSummary[] Tools;
    }

    /// <summary>hello.extensions[].tools[]：一个由第三方 manifest 声明的工具。</summary>
    public sealed class ExtensionToolSummary
    {
        /// <summary>完整工具名（`mod_&lt;id&gt;_&lt;name&gt;`），sidecar 原样使用。</summary>
        public string Name;
        public string Label;
        public string Description;
        public int Risk;
        public int TimeoutMs;
        /// <summary>JSON Schema 对象。</summary>
        public JsonValue Parameters;
    }

    /// <summary>
    /// 协议 v1 的常量与信封辅助。
    /// 契约来源：docs/protocol-v1.md —— 改这里之前先改那份文档。
    /// </summary>
    public static class Protocol
    {
        public const int Version = 1;

        // 消息类型（mod → sidecar）
        public const string Hello = "hello";
        /// <summary>mod → sidecar：能力/扩展列表变化时补发一次（B.1.9）；旧端按未知类型忽略。</summary>
        public const string Extensions = "extensions";
        public const string Prompt = "prompt";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Abort = "abort";
        /// <summary>A.15：prompt.mode 的插话取值。</summary>
        public const string PromptModeSteer = "steer";

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

        /// <summary>mod → sidecar：列出 pi 内置目录的全部供应商（设置页预设下拉框）。</summary>
        public const string ProviderCatalog = "provider.catalog";

        /// <summary>mod → sidecar：检测某供应商的 Key/端点是否可用（M3）。</summary>
        public const string ProviderTest = "provider.test";

        // ---- 设定页（协议附录 A.14）----

        /// <summary>mod → sidecar：整份替换被关闭的工具名。</summary>
        public const string ToolsSet = "tools.set";

        /// <summary>sidecar → mod：工具开关回执 + 完整工具目录。</summary>
        public const string ToolsState = "tools.state";

        /// <summary>mod → sidecar：跨存档会话索引（只读）。</summary>
        public const string ConversationsIndex = "conversations.index";

        /// <summary>sidecar → mod：按存档分组的会话索引。</summary>
        public const string ConversationsIndexed = "conversations.indexed";

        /// <summary>mod → sidecar：把一个会话导出成 Markdown。</summary>
        public const string ConversationsExport = "conversations.export";

        /// <summary>sidecar → mod：导出结果（路径与字节数）。</summary>
        public const string ConversationsExported = "conversations.exported";

        /// <summary>config.state 的取值。</summary>
        public const string ConfigReady = "ready";
        public const string ConfigMissingKey = "missing_key";
        public const string ConfigInvalidKey = "invalid_key";
        public const string ConfigUnreachable = "unreachable";

        /// <summary>mod → sidecar：把对话串绑定到某个存档（附录 A.6）。</summary>
        public const string SaveBind = "save.bind";

        /// <summary>sidecar → mod：绑定结果。</summary>
        public const string SaveBound = "save.bound";

        // ---- 多会话（协议附录 A.13）----

        /// <summary>mod → sidecar：新建一个空会话并设为活动会话。</summary>
        public const string ConversationCreate = "conversation.create";

        /// <summary>sidecar → mod：新会话已创建。</summary>
        public const string ConversationCreated = "conversation.created";

        /// <summary>mod → sidecar：切换到指定会话。</summary>
        public const string ConversationSwitch = "conversation.switch";

        /// <summary>sidecar → mod：会话已切换，带回该会话历史。</summary>
        public const string ConversationSwitched = "conversation.switched";

        /// <summary>mod → sidecar：删除指定会话。</summary>
        public const string ConversationDelete = "conversation.delete";

        /// <summary>sidecar → mod：会话已删除，带回新的活动会话与列表。</summary>
        public const string ConversationDeleted = "conversation.deleted";

        /// <summary>sidecar → mod：会话标题已由模型总结更新（只更新索引标题）。</summary>
        public const string ConversationUpdated = "conversation.updated";

        /// <summary>mod → sidecar：玩家重命名会话。</summary>
        public const string ConversationRename = "conversation.rename";

        /// <summary>sidecar → mod：重命名结果。</summary>
        public const string ConversationRenamed = "conversation.renamed";

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
        public const string CodeConversationNotFound = "conversation_not_found";
        public const string CodeConversationMismatch = "conversation_mismatch";
        public const string CodeInvalidTitle = "invalid_title";

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

        /// <summary>mod 支持原生 GoalProto 目标注入与读取。</summary>
        public const string CapabilityGameTodo = "game.todo.v1";

        /// <summary>mod 支持轮内待办：整表写入原生目标面板的第一组。</summary>
        public const string CapabilityGameTodoWrite = "game.todo.write.v1";

        /// <summary>mod 支持受控写脚本（run_script 脚本级确认 + 租约）。</summary>
        public const string CapabilityScriptWrite = "script.write.v1";

        /// <summary>mod 支持第三方 mod 桥（读取插件列表与配置，P0）。</summary>
        public const string CapabilityModBridge = "mod.bridge.v1";

        /// <summary>mod 支持 uGUI 控件树快照与合成点击（P3）。</summary>
        public const string CapabilityUiTree = "ui.tree.v1";

        /// <summary>sidecar → mod：请求玩家确认一次写操作。</summary>
        public const string ConfirmRequest = "confirm_request";

        /// <summary>mod → sidecar：玩家确认结果。</summary>
        public const string ConfirmResult = "confirm_result";

        /// <summary>sidecar → mod：脚本执行结束，释放脚本写租约。</summary>
        public const string ConfirmRelease = "confirm_release";

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
        public const string ToolTodoTemplates = "game_todo_templates";
        public const string ToolTodoList = "game_todo_list";
        public const string ToolTodoAdd = "game_todo_add";
        public const string ToolTodoRemove = "game_todo_remove";
        public const string ToolTakeOutput = "game_take_output";
        public const string ToolCopyBlueprint = "game_copy_blueprint";

        /// <summary>第三方 mod 桥（P0）：插件列表与配置读写。</summary>
        public const string ToolModsList = "mods_list";
        public const string ToolModsConfigList = "mods_config_list";
        public const string ToolModsConfigGet = "mods_config_get";
        public const string ToolModsConfigSet = "mods_config_set";
        public const string ToolModsApiList = "mods_api_list";
        public const string ToolModsAdapterCreate = "mods_adapter_create";
        public const string ToolModsAdapterDelete = "mods_adapter_delete";
        public const string ToolUiSnapshot = "ui_snapshot";
        public const string ToolUiClick = "ui_click";

        /// <summary>
        /// sidecar 本地「写文件」工具。它们不改变游戏状态，所以不需要 mod 令牌，
        /// 但权限档位同样要求确认（只读必问、受限/接管对 risk 1 自动放行）。
        /// </summary>
        public const string ToolBlueprintSave = "blueprint_save";
        public const string ToolBlueprintGenerate = "blueprint_generate";
        public const string ToolBlueprintFs = "blueprint_fs";

        public static readonly string[] LocalWriteToolNames =
        {
            ToolBlueprintSave,
            ToolBlueprintGenerate,
            ToolBlueprintFs,
        };

        public static bool IsLocalWriteTool(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            for (int i = 0; i < LocalWriteToolNames.Length; i++)
                if (string.Equals(LocalWriteToolNames[i], toolName, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>写操作（游戏状态或蓝图文件）；确认令牌只用于游戏状态写工具。</summary>
        public static bool IsAnyWriteTool(string toolName)
        {
            return IsWriteTool(toolName) || IsLocalWriteTool(toolName);
        }

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
            ToolTodoAdd,
            ToolTodoRemove,
            ToolTakeOutput,
            ToolUndoLastWrite,
            // 只写蓝图文件，但属于游戏工具；risk 1，走同一确认/令牌链路。
            ToolCopyBlueprint,
            // 第三方 mod 配置写入；可撤销，risk 1。
            ToolModsConfigSet,
            // 第三方 mod 适配器创建/删除：改的是 AI 的能力面，risk 2。
            ToolModsAdapterCreate,
            ToolModsAdapterDelete,
            // 合成点击：语义未知、不可撤销，risk 2。
            ToolUiClick,
        };

        /// <summary>
        /// 第三方 manifest 工具的写判定钩子（P2）。由 mod 侧按当前 manifest 设置；
        /// 为空时只看静态写工具表。sidecar 侧有各自的判定，不共享这个委托。
        /// </summary>
        public static Func<string, bool> ExtraWriteTool;

        public static bool IsWriteTool(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            for (int i = 0; i < WriteToolNames.Length; i++)
                if (string.Equals(WriteToolNames[i], toolName, StringComparison.Ordinal)) return true;
            var extra = ExtraWriteTool;
            return extra is not null && extra(toolName);
        }

        /// <summary>
        /// 当前权限档位是否对某个写操作直接放行、不弹确认。
        ///
        /// - readonly：所有写操作都要问；
        /// - limited：只放行 risk 1（可逆、无存档副作用）的写操作；
        /// - takeover：全部放行。
        ///
        /// 读操作（risk 0）本来就不走确认，返回 true 也不影响。
        /// </summary>
        public static bool AutoApprovesWrite(string permission, int risk)
        {
            if (risk <= 0) return true;
            if (string.Equals(permission, PermissionTakeover, StringComparison.Ordinal)) return true;
            if (string.Equals(permission, PermissionLimited, StringComparison.Ordinal)) return risk <= 1;
            return false;
        }

        /// <summary>sidecar 只允许监听回环地址；主机名/IP 必须落在这个集合内。</summary>
        public static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            var value = host.Trim().ToLowerInvariant();
            return value == "127.0.0.1" || value == "::1" || value == "localhost"
                || value.StartsWith("127.", StringComparison.Ordinal);
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

        // 思考档位（与 pi 的 ThinkingLevel 一致）。聊天窗暴露四档：
        // off / low / high / max；DeepSeek Flash 映射 low/high/max，Pro 映射 high/max。
        public const string ThinkingOff = "off";
        public const string ThinkingLow = "low";
        public const string ThinkingHigh = "high";
        public const string ThinkingMax = "max";

        // 权限档位：AI 调工具时是否需要玩家同意。
        public const string PermissionReadonly = "readonly";
        public const string PermissionLimited = "limited";
        public const string PermissionTakeover = "takeover";
        public const string DefaultPermission = PermissionLimited;

        public static bool IsValidPermissionLevel(string level)
        {
            return string.Equals(level, PermissionReadonly, StringComparison.Ordinal)
                || string.Equals(level, PermissionLimited, StringComparison.Ordinal)
                || string.Equals(level, PermissionTakeover, StringComparison.Ordinal);
        }

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

        /// <summary>构造 provider.catalog 请求：只读 pi 目录，不带任何参数。</summary>
        public static JsonValue BuildProviderCatalogRequest()
        {
            return JsonValue.NewObject();
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

        /// <summary>`hello.extensions` 与 `extensions` 推送共用的序列化。</summary>
        public static JsonValue BuildExtensions(ExtensionSummary[] extensions)
        {
            var list = JsonValue.NewArray();
            if (extensions is null) return list;
            foreach (var extension in extensions)
            {
                if (extension is null) continue;
                var item = JsonValue.NewObject();
                item.Set("id", extension.Id ?? string.Empty);
                item.Set("guid", extension.Guid ?? string.Empty);
                item.Set("name", extension.Name ?? string.Empty);
                item.Set("version", extension.Version ?? string.Empty);
                item.Set("configCount", extension.ConfigCount);
                if (extension.Tools is not null && extension.Tools.Length > 0)
                {
                    var tools = JsonValue.NewArray();
                    foreach (var tool in extension.Tools)
                    {
                        if (tool is null || string.IsNullOrEmpty(tool.Name)) continue;
                        var toolJson = JsonValue.NewObject();
                        toolJson.Set("name", tool.Name);
                        toolJson.Set("label", tool.Label ?? tool.Name);
                        toolJson.Set("description", tool.Description ?? string.Empty);
                        toolJson.Set("risk", tool.Risk);
                        toolJson.Set("timeoutMs", tool.TimeoutMs > 0 ? tool.TimeoutMs : 8000);
                        toolJson.Set("parameters",
                            tool.Parameters is not null ? tool.Parameters : JsonValue.NewObject());
                        tools.Add(toolJson);
                    }
                    if (tools.Count > 0) item.Set("tools", tools);
                }
                list.Add(item);
            }
            return list;
        }

        /// <summary>B.1.9：能力/扩展列表推送的负载（显式写空数组表示清空）。</summary>
        public static JsonValue BuildClientState(string[] capabilities, ExtensionSummary[] extensions)
        {
            var p = JsonValue.NewObject();
            var caps = JsonValue.NewArray();
            if (capabilities is not null) foreach (var c in capabilities) caps.Add(c);
            p.Set("capabilities", caps);
            p.Set("extensions", BuildExtensions(extensions));
            return p;
        }

        public static JsonValue BuildHello(string token, string modVersion, string gameVersion,
            string gameLanguage, string[] capabilities, string saveKey,
            ExtensionSummary[] extensions = null)
        {
            var p = JsonValue.NewObject();
            p.Set("token", token ?? string.Empty);
            p.Set("modVersion", modVersion ?? "0.0.0");
            p.Set("gameVersion", gameVersion ?? "unknown");
            p.Set("gameLanguage", gameLanguage ?? "unknown");

            var caps = JsonValue.NewArray();
            if (capabilities is not null) foreach (var c in capabilities) caps.Add(c);
            p.Set("capabilities", caps);

            // 第三方 mod 桥：工具与插件同生命周期；没装/没启用时不写这个字段。
            if (extensions is not null && extensions.Length > 0)
                p.Set("extensions", BuildExtensions(extensions));

            // saveKey 允许为 null（尚未进入存档）；显式写出 null，避免“缺字段”与“解绑”两种语义。
            p.Set("saveKey", saveKey is null ? JsonValue.Null : JsonValue.Of(saveKey));

            return p;
        }

        /// <param name="mode">A.15：null/空 = 普通提问；"steer" = 插话到当前回合。</param>
        public static JsonValue BuildPrompt(string text, string conversationId, string requiredTool = null,
            string mode = null)
        {
            var p = JsonValue.NewObject();
            p.Set("text", text ?? string.Empty);
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            // M4l：玩家用 /<工具名> 指定本轮必须调用的工具。空值不写，老端忽略。
            if (!string.IsNullOrEmpty(requiredTool)) p.Set("requiredTool", requiredTool);
            // A.15：插话。空值不写，老 sidecar 按普通提问处理。
            if (!string.IsNullOrEmpty(mode)) p.Set("mode", mode);
            return p;
        }

        /// <summary>A.15：中断当前回合（空负载）。</summary>
        public static JsonValue BuildAbort()
        {
            return JsonValue.NewObject();
        }

        /// <summary>
        /// 构造 config.set。只把非空字段写进去（sidecar 按"未给出的保持不变"处理）。
        /// apiKey 是敏感数据：发送后不要留在任何日志或 UI 历史里。
        /// </summary>
        public static JsonValue BuildConfigSet(string provider, string model, string apiKey,
            string baseUrl, string thinkingLevel = null,
            JsonValue providerConfig = null, JsonValue removeProviders = null,
            string activeProvider = null, string activeModel = null,
            string permissionLevel = null, bool clearBaseUrl = false,
            IList<string> disabledTools = null)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(provider)) p.Set("provider", provider);
            if (!string.IsNullOrEmpty(model)) p.Set("model", model);
            if (!string.IsNullOrEmpty(apiKey)) p.Set("apiKey", apiKey);
            if (!string.IsNullOrEmpty(baseUrl)) p.Set("baseUrl", baseUrl);
            else if (clearBaseUrl) p.Set("baseUrl", JsonValue.Null);
            if (!string.IsNullOrEmpty(thinkingLevel)) p.Set("thinkingLevel", thinkingLevel);
            if (providerConfig is not null && !providerConfig.IsNull) p.Set("providerConfig", providerConfig);
            if (removeProviders is not null && !removeProviders.IsNull) p.Set("removeProviders", removeProviders);
            if (!string.IsNullOrEmpty(activeProvider)) p.Set("activeProvider", activeProvider);
            if (!string.IsNullOrEmpty(activeModel)) p.Set("activeModel", activeModel);
            if (!string.IsNullOrEmpty(permissionLevel)) p.Set("permissionLevel", permissionLevel);
            if (disabledTools is not null) p.Set("disabledTools", BuildStringArray(disabledTools));
            return p;
        }

        /// <summary>把字符串列表构造成 JSON 数组；空列表写成 `[]`（= 全部启用）。</summary>
        private static JsonValue BuildStringArray(IList<string> values)
        {
            var array = JsonValue.NewArray();
            if (values is not null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    var value = values[i];
                    if (string.IsNullOrEmpty(value)) continue;
                    array.Add(value);
                }
            }
            return array;
        }

        /// <summary>构造 tools.set（协议附录 A.14.1）：整份替换被关闭的工具名。</summary>
        public static JsonValue BuildToolsSet(IList<string> disabled)
        {
            var p = JsonValue.NewObject();
            p.Set("disabled", BuildStringArray(disabled));
            return p;
        }

        /// <summary>构造 conversations.index 的空载荷（协议附录 A.14.2）。</summary>
        public static JsonValue BuildConversationsIndex()
        {
            return JsonValue.NewObject();
        }

        /// <summary>构造 conversations.export（协议附录 A.14.3）。</summary>
        public static JsonValue BuildConversationsExport(string saveKey, string conversationId,
            string format = "md")
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(saveKey)) p.Set("saveKey", saveKey);
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            p.Set("format", string.IsNullOrEmpty(format) ? "md" : format);
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
            p.Set("saveKey", saveKey is null ? JsonValue.Null : JsonValue.Of(saveKey));
            if (!string.IsNullOrEmpty(saveName)) p.Set("saveName", saveName);
            if (blueprintTiers is not null && !blueprintTiers.IsNull) p.Set("blueprintTiers", blueprintTiers);
            if (sandbox is bool sandboxValue) p.Set("sandbox", sandboxValue);
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
        public const int ToolDebugArgsMaxBytes = 64 * 1024;

        public static JsonValue BuildToolDebug(string name, JsonValue args)
        {
            var payload = args ?? JsonValue.NewObject();
            var bytes = System.Text.Encoding.UTF8.GetByteCount(payload.ToJson());
            if (bytes > ToolDebugArgsMaxBytes)
                throw new ArgumentException($"tool_debug args 超过 {ToolDebugArgsMaxBytes} 字节：{bytes}");
            var p = JsonValue.NewObject();
            p.Set("name", name ?? string.Empty);
            p.Set("args", payload);
            return p;
        }

        /// <summary>构造 conversation.create 的空载荷（附录 A.13）。</summary>
        public static JsonValue BuildConversationCreate()
        {
            return JsonValue.NewObject();
        }

        /// <summary>构造 conversation.switch / conversation.delete 载荷（附录 A.13）。</summary>
        public static JsonValue BuildConversationTarget(string conversationId, string saveKey = null)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            if (!string.IsNullOrEmpty(saveKey)) p.Set("saveKey", saveKey);
            return p;
        }

        /// <summary>构造 conversation.updated 载荷（附录 A.13）：只更新标题。</summary>
        public static JsonValue BuildConversationUpdated(string conversationId, string title)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            if (!string.IsNullOrEmpty(title)) p.Set("title", title);
            return p;
        }

        /// <summary>构造 conversation.rename 载荷（附录 A.13）：玩家改标题。</summary>
        public static JsonValue BuildConversationRename(string conversationId, string title,
            string saveKey = null)
        {
            var p = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(conversationId)) p.Set("conversationId", conversationId);
            p.Set("title", title ?? string.Empty);
            if (!string.IsNullOrEmpty(saveKey)) p.Set("saveKey", saveKey);
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
            if (ok && image is not null && !image.IsNull) p.Set("image", image);

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
            if (payload is null || payload.IsNull) return r;
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
            if (Details is null || Details.IsNull) return "null";
            return Details.IsString ? Details.AsString() : Details.ToJson();
        }

        public static ToolDebugResultInfo Parse(JsonValue payload)
        {
            var r = new ToolDebugResultInfo();
            if (payload is null || payload.IsNull) return r;
            r.Ok = payload["ok"].AsBool(false);
            r.Tool = payload["tool"].AsString(string.Empty);
            r.Source = payload["source"].AsString("unknown");
            r.DurationMs = payload["durationMs"].AsLong(0);
            r.Content = payload["content"].AsString(string.Empty);
            r.Details = payload["details"];
            r.Truncated = payload["truncated"].AsBool(false);

            var error = payload["error"];
            if (error is not null && error.IsObject)
            {
                r.ErrorCode = error["code"].AsString(null);
                r.ErrorMessage = error["message"].AsString(null);
            }

            var image = payload["image"];
            if (image is not null && image.IsObject)
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
            if (payload is null || payload.IsNull) return r;
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
            if (payload is null || payload.IsNull) return r;
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
            if (payload is null || payload.IsNull) return null;
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
            if (payload is null || payload.IsNull) return r;
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
        /// <summary>
        /// <c>thinking</c> = 思考段；<c>text</c> = 一段过程正文；
        /// <c>tool</c> = 一次工具调用。
        /// </summary>
        public string Kind { get; set; }
        /// <summary>思考正文（<c>thinking</c>）或过程正文（<c>text</c>）。</summary>
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
            if (value is null || !value.IsObject) return null;
            var kind = value["kind"].AsString(null);
            if (kind == "thinking" || kind == "text")
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
                var ok = rawOk is not null && !rawOk.IsNull ? (bool?)rawOk.AsBool(false) : null;
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
            if (payload is null || payload.IsNull) return null;
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

        /// <summary>解析 save.bound / conversation.* 共用的 history 数组。</summary>
        public static void ParseList(JsonValue history, List<HistoryMessageInfo> target)
        {
            if (history is null || target is null || history.Count <= 0) return;
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
                if (cards is not null && cards.Count > 0)
                {
                    for (int c = 0; c < cards.Count; c++)
                    {
                        var card = BlueprintCardInfo.Parse(cards[c]);
                        if (card is not null && (!string.IsNullOrEmpty(card.FileName) || !string.IsNullOrEmpty(card.Path)))
                            info.Cards.Add(card);
                    }
                }
                var linkCards = item["linkCards"];
                if (linkCards is not null && linkCards.Count > 0)
                {
                    for (int c = 0; c < linkCards.Count; c++)
                    {
                        var card = LinkCardInfo.Parse(linkCards[c]);
                        if (card is not null) info.LinkCards.Add(card);
                    }
                }
                // 思考/工具步骤：老 sidecar 不发送，空数组即可。
                var steps = item["steps"];
                if (steps is not null && steps.Count > 0)
                {
                    for (int s = 0; s < steps.Count; s++)
                    {
                        var step = HistoryStepInfo.Parse(steps[s]);
                        if (step is not null) info.Steps.Add(step);
                    }
                }
                target.Add(info);
            }
        }
    }

    /// <summary>会话索引里的一条用户提问（附录 A.13）。</summary>
    public sealed class ConversationRecordInfo
    {
        public string Text { get; private set; }
        public long Timestamp { get; private set; }
        public string GameTime { get; private set; }

        public static ConversationRecordInfo Parse(JsonValue value)
        {
            if (value is null || value.IsNull) return null;
            var text = value["text"].AsString(null);
            if (string.IsNullOrEmpty(text)) return null;
            return new ConversationRecordInfo
            {
                Text = text,
                Timestamp = value["timestamp"].AsLong(0),
                GameTime = value["gameTime"].AsString(null),
            };
        }

        public static ConversationRecordInfo Create(string text, long timestamp, string gameTime = null)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return new ConversationRecordInfo
            {
                Text = text,
                Timestamp = timestamp,
                GameTime = gameTime,
            };
        }
    }

    /// <summary>一个会话的元信息（协议附录 A.13）。</summary>
    public sealed class ConversationInfo
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public long CreatedAt { get; private set; }
        public long UpdatedAt { get; private set; }
        public int RecordCount { get; private set; }
        public List<ConversationRecordInfo> Records { get; private set; } = new List<ConversationRecordInfo>();

        public static ConversationInfo Create(string id, string title)
        {
            return new ConversationInfo
            {
                Id = id,
                Title = title ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RecordCount = 0,
            };
        }

        /// <summary>把一条新的用户提问追加到索引（本机实时提问用）。</summary>
        public void AddRecord(ConversationRecordInfo record)
        {
            if (record is null || string.IsNullOrEmpty(record.Text)) return;
            Records.Add(record);
            RecordCount = Records.Count;
        }

        /// <summary>删除一条提问索引；优先按时间戳，否则匹配最后一条同文本。</summary>
        public bool RemoveRecord(string text, long timestamp)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = Records.Count - 1; i >= 0; i--)
            {
                var record = Records[i];
                if (record is null || record.Text != text) continue;
                if (timestamp > 0 && record.Timestamp > 0 && record.Timestamp != timestamp) continue;
                Records.RemoveAt(i);
                RecordCount = Records.Count;
                return true;
            }
            return false;
        }

        public void SetTitle(string title)
        {
            Title = title ?? string.Empty;
        }

        public void MarkUpdated(long timestamp)
        {
            if (timestamp > 0) UpdatedAt = timestamp;
        }

        public static ConversationInfo Parse(JsonValue value)
        {
            if (value is null || value.IsNull) return null;
            var id = value["id"].AsString(null);
            if (string.IsNullOrEmpty(id)) return null;
            var info = new ConversationInfo
            {
                Id = id,
                Title = value["title"].AsString(string.Empty),
                CreatedAt = value["createdAt"].AsLong(0),
                UpdatedAt = value["updatedAt"].AsLong(0),
                RecordCount = value["recordCount"].AsInt(0),
            };
            var records = value["records"];
            if (records is not null && records.Count > 0)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var record = ConversationRecordInfo.Parse(records[i]);
                    if (record is not null) info.Records.Add(record);
                }
            }
            if (info.RecordCount <= 0) info.RecordCount = info.Records.Count;
            return info;
        }
    }

    /// <summary>
    /// conversation.created / switched / deleted / updated 的解析结果（附录 A.13）。
    /// 各回执共用同一个结构；没有的字段保持默认值。
    /// </summary>
    public sealed class ConversationEventInfo
    {
        public string SaveKey { get; private set; }
        public string ConversationId { get; private set; }
        public string ActiveConversationId { get; private set; }
        public string Title { get; private set; }
        public string Error { get; private set; }
        public bool Ok { get; private set; }
        public bool Existed { get; private set; }
        public int MessageCount { get; private set; }
        /// <summary>conversation.switched 里被丢弃的空会话 id（附录 A.13，可选）。</summary>
        public string DiscardedConversationId { get; private set; }
        public ConversationInfo Conversation { get; private set; }
        public List<ConversationInfo> Conversations { get; private set; } = new List<ConversationInfo>();
        public List<HistoryMessageInfo> History { get; private set; } = new List<HistoryMessageInfo>();

        public static ConversationEventInfo Parse(JsonValue payload)
        {
            var r = new ConversationEventInfo();
            if (payload is null || payload.IsNull) return r;
            r.SaveKey = payload["saveKey"].AsString(null);
            r.ConversationId = payload["conversationId"].AsString(null);
            r.ActiveConversationId = payload["activeConversationId"].AsString(null);
            r.Title = payload["title"].AsString(null);
            r.Error = payload["error"].AsString(null);
            r.Ok = payload["ok"].AsBool(true);
            r.Existed = payload["existed"].AsBool(false);
            r.MessageCount = payload["messageCount"].AsInt(0);
            r.DiscardedConversationId = payload["discardedConversationId"].AsString(null);
            r.Conversation = ConversationInfo.Parse(payload["conversation"]);
            if (string.IsNullOrEmpty(r.ConversationId) && r.Conversation is not null)
                r.ConversationId = r.Conversation.Id;

            var conversations = payload["conversations"];
            if (conversations is not null && conversations.Count > 0)
            {
                for (int i = 0; i < conversations.Count; i++)
                {
                    var info = ConversationInfo.Parse(conversations[i]);
                    if (info is not null) r.Conversations.Add(info);
                }
            }
            HistoryMessageInfo.ParseList(payload["history"], r.History);
            return r;
        }
    }

    /// <summary>存档绑定结果（协议附录 A.6 / A.13）。</summary>
    public sealed class SaveBoundInfo
    {
        public string SaveKey { get; private set; }
        public string SaveName { get; private set; }
        /// <summary>该存档此前是否已有对话（false = 新建）。</summary>
        public bool Existed { get; private set; }
        /// <summary>恢复的历史消息条数。</summary>
        public int MessageCount { get; private set; }
        /// <summary>当前活动会话 id（附录 A.13，可选）。</summary>
        public string ActiveConversationId { get; private set; }
        /// <summary>该存档的会话列表（附录 A.13，可选；老 sidecar 为空）。</summary>
        public List<ConversationInfo> Conversations { get; private set; } = new List<ConversationInfo>();
        /// <summary>最近若干条可回放的 user/assistant 文本；只用于 UI，不进入模型上下文。</summary>
        public List<HistoryMessageInfo> History { get; private set; } = new List<HistoryMessageInfo>();

        public static SaveBoundInfo Parse(JsonValue payload)
        {
            var r = new SaveBoundInfo();
            if (payload is null || payload.IsNull) return r;
            r.SaveKey = payload["saveKey"].AsString(null);
            r.SaveName = payload["saveName"].AsString(null);
            r.Existed = payload["existed"].AsBool(false);
            r.MessageCount = payload["messageCount"].AsInt(0);
            r.ActiveConversationId = payload["activeConversationId"].AsString(null);

            var conversations = payload["conversations"];
            if (conversations is not null && conversations.Count > 0)
            {
                for (int i = 0; i < conversations.Count; i++)
                {
                    var info = ConversationInfo.Parse(conversations[i]);
                    if (info is not null) r.Conversations.Add(info);
                }
            }
            HistoryMessageInfo.ParseList(payload["history"], r.History);
            return r;
        }

        public override string ToString()
        {
            return $"{SaveName ?? SaveKey ?? "(无存档)"} existed={Existed} messages={MessageCount} " +
                   $"conversations={Conversations.Count} history={History.Count}";
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
            if (value is null || !value.IsObject) return null;
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
            if (value is null || !value.IsObject) return result;
            result.Provider = value["provider"].AsString(null);
            result.Source = value["source"].AsString(null);
            result.Error = value["error"].AsString(null);
            result.ResolvedBaseUrl = value["resolvedBaseUrl"].AsString(null);

            var models = value["models"];
            if (models is not null && models.IsArray)
            {
                foreach (var item in models.Items)
                {
                    var model = ModelInfo.Parse(item);
                    if (model is not null) result.Models.Add(model);
                }
            }
            return result;
        }
    }

    /// <summary>provider.catalog 里的一条供应商（只含展示所需的 id/name）。</summary>
    public sealed class ProviderCatalogEntry
    {
        public string Id { get; private set; }
        public string Name { get; private set; }

        public static ProviderCatalogEntry Parse(JsonValue value)
        {
            if (value is null || !value.IsObject) return null;
            var id = value["id"].AsString(null);
            if (string.IsNullOrEmpty(id)) return null;
            return new ProviderCatalogEntry
            {
                Id = id,
                Name = value["name"].AsString(id),
            };
        }

        public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    /// <summary>provider.catalog 的响应（pi 内置供应商目录，顺序即目录顺序）。</summary>
    public sealed class ProviderCatalogResult
    {
        public List<ProviderCatalogEntry> Providers { get; private set; }
        public string Source { get; private set; }
        public string Error { get; private set; }

        public ProviderCatalogResult()
        {
            Providers = new List<ProviderCatalogEntry>();
        }

        public static ProviderCatalogResult Parse(JsonValue value)
        {
            var result = new ProviderCatalogResult();
            if (value is null || !value.IsObject) return result;
            result.Source = value["source"].AsString(null);
            result.Error = value["error"].AsString(null);

            var providers = value["providers"];
            if (providers is not null && providers.IsArray)
            {
                foreach (var item in providers.Items)
                {
                    var entry = ProviderCatalogEntry.Parse(item);
                    if (entry is not null) result.Providers.Add(entry);
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
            if (value is null || !value.IsObject) return result;
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
            if (value is null || !value.IsObject) return null;
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
            if (models is not null && models.IsArray)
            {
                foreach (var item in models.Items)
                {
                    var model = ModelInfo.Parse(item);
                    if (model is not null) provider.Models.Add(model);
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

        /// <summary>当前选中的供应商（协议附录 A.1 的 active.provider，可选）。</summary>
        public string ActiveProvider { get; private set; }

        /// <summary>当前选中的模型（协议附录 A.1 的 active.model，可选）。</summary>
        public string ActiveModel { get; private set; }

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

        /// <summary>
        /// 权限档位（readonly / limited / takeover，可选）。老服务端不返回时保持 null，
        /// 调用方回退到本地配置（默认 limited）。
        /// </summary>
        public string PermissionLevel { get; private set; }

        /// <summary>
        /// 被玩家关闭的工具名（协议附录 A.14.5，可选）。老服务端不返回时为空列表
        /// （= 全部启用），调用方应把它当作权威，不要自己维护第二份开关。
        /// </summary>
        public List<string> DisabledTools { get; private set; }

        public string Message { get; private set; }

        public ConfigState()
        {
            State = Protocol.ConfigMissingKey;
            Providers = new List<ProviderInfo>();
            DisabledTools = new List<string>();
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
            if (payload is null || payload.IsNull) return result;

            var state = payload["state"].AsString(null);
            if (!string.IsNullOrEmpty(state)) result.State = state;

            var active = payload["active"];
            if (active is not null && active.IsObject)
            {
                result.ActiveProvider = active["provider"].AsString(null);
                result.ActiveModel = active["model"].AsString(null);
            }
            result.Provider = payload["provider"].AsString(null);
            result.Model = payload["model"].AsString(null);
            if (string.IsNullOrEmpty(result.Provider)) result.Provider = result.ActiveProvider;
            if (string.IsNullOrEmpty(result.Model)) result.Model = result.ActiveModel;
            result.ThinkingLevel = payload["thinkingLevel"].AsString(null);
            result.PermissionLevel = payload["permissionLevel"].AsString(null);
            result.Message = payload["message"].AsString(null);

            var disabledTools = payload["disabledTools"];
            if (disabledTools is not null && disabledTools.IsArray)
            {
                foreach (var item in disabledTools.Items)
                {
                    var name = item.AsString(null);
                    if (!string.IsNullOrEmpty(name)) result.DisabledTools.Add(name);
                }
            }

            var providers = payload["providers"];
            if (providers is not null && providers.IsArray)
            {
                foreach (var item in providers.Items)
                {
                    var provider = ProviderInfo.Parse(item);
                    if (provider is not null) result.Providers.Add(provider);
                }
            }

            // 老服务端只返回 provider/model：合成为一条，UI 仍能显示"模型"下拉框。
            if (result.Providers.Count == 0 && !string.IsNullOrEmpty(result.Provider))
            {
                var fallback = ProviderInfo.Create(result.Provider, result.Provider, !result.NeedsKey, true);
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
            if (payload is null || payload.IsNull || !payload.IsObject) return null;

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
        /// <summary>B.1.6：写操作属于哪个第三方插件；非第三方工具为空。</summary>
        public string ModGuid { get; private set; }
        /// <summary>脚本写租约允许的写调用次数；0 = 普通单次确认。</summary>
        public int MaxWrites { get; private set; }
        /// <summary>脚本确认时列出的写工具名；租约只允许这些工具。</summary>
        public List<string> WriteNames { get; private set; } = new List<string>();

        public static ConfirmRequestInfo Parse(JsonValue payload)
        {
            if (payload is null || !payload.IsObject) return null;
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
                ModGuid = payload["modGuid"].AsString(null),
                MaxWrites = Math.Max(0, Math.Min(10000, payload["maxWrites"].AsInt(0))),
            };
            var writeNames = payload["writeNames"];
            if (writeNames is not null && writeNames.IsArray)
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


    /// <summary>
    /// 工具目录里的一条（协议附录 A.14.1）。老 sidecar 不带 group/risk/source/enabled，
    /// 调用方按名字推断、按「全部启用」显示。
    /// </summary>
    public sealed class ToolCatalogEntryInfo
    {
        public string Name;
        public string Label;
        public string Description;
        /// <summary>A.14.1：game.read | game.write | kb | blueprint | script | ask | thirdparty | other。</summary>
        public string Group;
        /// <summary>A.14.1：0 = 只读，1 = 可逆写，2 = 改变存档。</summary>
        public int Risk;
        /// <summary>A.14.1：builtin 或第三方插件 id。</summary>
        public string Source;
        /// <summary>A.14.1：来源显示名。</summary>
        public string SourceName;
        /// <summary>A.14.1：false = 被玩家关掉。</summary>
        public bool Enabled = true;

        public static ToolCatalogEntryInfo Parse(JsonValue value)
        {
            if (value is null || !value.IsObject) return null;
            var name = value["name"].AsString(null);
            if (string.IsNullOrEmpty(name)) return null;
            return new ToolCatalogEntryInfo
            {
                Name = name,
                Label = value["label"].AsString(name),
                Description = value["description"].AsString(string.Empty),
                Group = value["group"].AsString(null),
                Risk = value["risk"].AsInt(0),
                Source = value["source"].AsString(null),
                SourceName = value["sourceName"].AsString(null),
                Enabled = value["enabled"].AsBool(true),
            };
        }

        public static void ParseList(JsonValue array, List<ToolCatalogEntryInfo> target)
        {
            if (array is null || !array.IsArray || target is null) return;
            foreach (var item in array.Items)
            {
                var entry = Parse(item);
                if (entry is not null) target.Add(entry);
            }
        }
    }

    /// <summary>`tools.state`（协议附录 A.14.1）：开关回执 + 完整工具目录。</summary>
    public sealed class ToolsStateInfo
    {
        public bool Ok { get; private set; }
        public bool Rebuilt { get; private set; }
        public string Error { get; private set; }
        public List<string> Disabled { get; private set; } = new List<string>();
        public List<ToolCatalogEntryInfo> ToolCatalog { get; private set; } =
            new List<ToolCatalogEntryInfo>();

        public static ToolsStateInfo Parse(JsonValue payload)
        {
            var result = new ToolsStateInfo();
            if (payload is null || !payload.IsObject) return result;
            result.Ok = payload["ok"].AsBool(false);
            result.Rebuilt = payload["rebuilt"].AsBool(false);
            result.Error = payload["error"].AsString(null);
            var disabled = payload["disabled"];
            if (disabled is not null && disabled.IsArray)
            {
                foreach (var item in disabled.Items)
                {
                    var name = item.AsString(null);
                    if (!string.IsNullOrEmpty(name)) result.Disabled.Add(name);
                }
            }
            ToolCatalogEntryInfo.ParseList(payload["toolCatalog"], result.ToolCatalog);
            return result;
        }
    }

    /// <summary>`conversations.indexed` 里的一条会话（协议附录 A.14.2）。</summary>
    public sealed class ConversationIndexEntryInfo
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public long UpdatedAt { get; private set; }
        public int MessageCount { get; private set; }
        public bool IsActive { get; private set; }

        public static ConversationIndexEntryInfo Parse(JsonValue value)
        {
            if (value is null || !value.IsObject) return null;
            var id = value["id"].AsString(null);
            if (string.IsNullOrEmpty(id)) return null;
            return new ConversationIndexEntryInfo
            {
                Id = id,
                Title = value["title"].AsString(null),
                UpdatedAt = value["updatedAt"].AsLong(0),
                MessageCount = value["messageCount"].AsInt(0),
                IsActive = value["isActive"].AsBool(false),
            };
        }
    }

    /// <summary>`conversations.indexed` 里的一个存档分组（协议附录 A.14.2）。</summary>
    public sealed class ConversationSaveGroupInfo
    {
        public string SaveKey { get; private set; }
        public string SaveName { get; private set; }
        public long LastSeen { get; private set; }
        public bool Current { get; private set; }
        public List<ConversationIndexEntryInfo> Conversations { get; private set; } =
            new List<ConversationIndexEntryInfo>();

        public static ConversationSaveGroupInfo Parse(JsonValue value)
        {
            if (value is null || !value.IsObject) return null;
            var saveKey = value["saveKey"].AsString(null);
            if (string.IsNullOrEmpty(saveKey)) return null;
            var group = new ConversationSaveGroupInfo
            {
                SaveKey = saveKey,
                SaveName = value["saveName"].AsString(null),
                LastSeen = value["lastSeen"].AsLong(0),
                Current = value["current"].AsBool(false),
            };
            var conversations = value["conversations"];
            if (conversations is not null && conversations.IsArray)
            {
                foreach (var item in conversations.Items)
                {
                    var entry = ConversationIndexEntryInfo.Parse(item);
                    if (entry is not null) group.Conversations.Add(entry);
                }
            }
            return group;
        }
    }

    /// <summary>`conversations.indexed`（协议附录 A.14.2）：只读索引结果。</summary>
    public sealed class ConversationsIndexedInfo
    {
        public string CurrentSaveKey { get; private set; }
        public List<ConversationSaveGroupInfo> Saves { get; private set; } =
            new List<ConversationSaveGroupInfo>();

        public static ConversationsIndexedInfo Parse(JsonValue payload)
        {
            var result = new ConversationsIndexedInfo();
            if (payload is null || !payload.IsObject) return result;
            result.CurrentSaveKey = payload["currentSaveKey"].AsString(null);
            var saves = payload["saves"];
            if (saves is not null && saves.IsArray)
            {
                foreach (var item in saves.Items)
                {
                    var group = ConversationSaveGroupInfo.Parse(item);
                    if (group is not null) result.Saves.Add(group);
                }
            }
            return result;
        }
    }

    /// <summary>`conversations.exported`（协议附录 A.14.3）：导出结果。</summary>
    public sealed class ConversationsExportedInfo
    {
        public bool Ok { get; private set; }
        public string Path { get; private set; }
        public int Bytes { get; private set; }
        public int MessageCount { get; private set; }
        public string Error { get; private set; }

        public static ConversationsExportedInfo Parse(JsonValue payload)
        {
            var result = new ConversationsExportedInfo();
            if (payload is null || !payload.IsObject) return result;
            result.Ok = payload["ok"].AsBool(false);
            result.Path = payload["path"].AsString(null);
            result.Bytes = payload["bytes"].AsInt(0);
            result.MessageCount = payload["messageCount"].AsInt(0);
            result.Error = payload["error"].AsString(null);
            return result;
        }
    }

}
