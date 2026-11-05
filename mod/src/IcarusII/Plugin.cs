using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;
using P = IcarusII.Common.Protocol.Protocol;
using IcarusII.Common.Transport;
using IcarusII.Config;
using IcarusII.Core;
using IcarusII.Game;
using IcarusII.Game.Tools;
using IcarusII.Input;
using IcarusII.I18n;
using IcarusII.UI;
using UnityEngine;

namespace IcarusII
{
    /// <summary>
    /// 插件入口。
    ///
    /// 接入点默认只用游戏暴露的公开类型/成员
    /// （UIRoot / GameMain / VFInput / PlayerAction_Build），游戏更新时最坏情况是
    /// 编译失败或功能降级（design.md §6.1）。
    /// 唯一的例外是目标面板：原生把 goalLevel == Off 写死在 UIGoalPanel 的私有状态里，
    /// 不改这三处显示方法就无法在“自由探索”下展示 AI 目标。补丁只在存在 AI 目标时
    /// 临时改写等级并在同一帧恢复，失败会自行注销（GoalPanelOverride）。
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    [BepInProcess("DSPGAME.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "icarus-ii";
        public const string PluginName = "Icarus II";
        public const string PluginVersion = "1.1.298";

        /// <summary>每帧用于处理后台线程回调的时间预算（毫秒）。</summary>
        private const int MainThreadBudgetMs = 4;

        private static readonly Color AccentColor = new Color(0.353f, 0.749f, 1f, 1f);
        private static readonly Color OkColor = new Color(0.45f, 0.85f, 0.55f);
        private static readonly Color WarnColor = new Color(0.95f, 0.75f, 0.35f);

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        private PluginConfig _config;
        private MainThreadDispatcher _dispatcher;
        private SidecarLauncher _launcher;
        private SidecarClient _client;
        private HotkeyService _hotkey;
        private ChatWindow _chat;
        private GameToolDispatcher _tools;

        private string _pluginDir;
        private bool _uiInitialized;
        private bool _streaming;
        /// <summary>焦点会话是否正在回答（从提交到 idle）；后台会话的状态不会改它。</summary>
        private bool _agentRunning;
        /// <summary>callId → 发起工具调用的会话；后台会话只执行、不画卡片。</summary>
        private readonly Dictionary<string, string> _toolConversations =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private string _pendingHint;
        private volatile bool _sidecarStartAttempted;
        private bool _uiCreateFailureLogged;
        private bool _styleApplied;
        private bool _initFailed;
        private ConfigState _lastConfig;

        /// <summary>
        /// 上一次真正应用到 UI 的配置状态。
        ///
        /// 用来避免每收到一次 `config.state` 就重复追加同一条引导；
        /// 场景切换后窗口会重建，这里也要清空，让新窗口重新显示一次引导。
        /// </summary>
        private string _lastConfigState;

        /// <summary>思考档位在本地改过、但还没成功发给 sidecar（下次 ready 时补发）。</summary>
        private bool _thinkingLevelDirty;

        /// <summary>权限档位在本地改过、但还没成功发给 sidecar（下次 ready 时补发）。</summary>
        private bool _permissionLevelDirty;

        /// <summary>最近一次推送给 sidecar 的扩展列表指纹。</summary>
        private string _lastModsSnapshot;

        /// <summary>最近一个已渲染终稿的 promptId，用于对重复的 message{final} 去重。</summary>
        private string _lastFinalReplyTo;
        /// <summary>请求 id → 用户提问原文；final 到达时用来回存该回答的游戏时间。</summary>
        private readonly Dictionary<string, string> _promptById = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>history.delete 请求 id → 重新推理的提问原文（删除成功后重发 prompt）。</summary>
        private readonly Dictionary<string, string> _regenerateDeleteId = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>M4m：调试直调请求 id → 请求信息（!tool）。</summary>
        private readonly Dictionary<string, DebugRequest> _debugById = new Dictionary<string, DebugRequest>(StringComparer.Ordinal);

        /// <summary>当前存档标识（null = 主菜单）。</summary>
        private string _saveKey;

        /// <summary>sidecar 内部工具调用（知识库导出）：不显示工具卡，只回结果。</summary>
        private readonly HashSet<string> _internalCalls = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>当前等待玩家作答的 ask_user；同一时刻只允许一个。</summary>
        private sealed class PendingAsk
        {
            public string CallId;
            public float Deadline;
        }
        private PendingAsk _pendingAsk;

        /// <summary>聊天窗关着时收到的 ask_user；开窗后再把作答面板放出来。</summary>
        private List<AskQuestion> _pendingAskQuestions;

        /// <summary>聊天窗关着时收到的写操作确认；开窗后再把确认页放出来。</summary>
        private ConfirmRequestInfo _pendingConfirm;

        /// <summary>后台事件 → 游戏原生顾问字幕（不受「顾问提示」开关影响）。</summary>
        private AdvisorNotifier _advisor;

        /// <summary>写操作确认令牌（sidecar 生成、mod 校验、单次使用）。</summary>
        private readonly ConfirmationService _confirmation = new ConfirmationService();

        /// <summary>当前存档标识是否已经同步给服务端。</summary>
        private bool _saveSynced;

        /// <summary>最近一次上报给 sidecar 的沙盒状态（用于同档内开关变化时重发）。</summary>
        private bool _sandboxValue;

        /// <summary>已经提示过沙盒状态的存档；同一存档不重复提示。</summary>
        private string _sandboxNoticeFor;

        /// <summary>每个存档只扫描一次旧标记，避免每帧遍历 markerPool。</summary>
        private bool _markersSanitized;

        /// <summary>已经回放过历史的存档；同一存档重连时不重复粘贴历史。</summary>
        private string _historyReplayedFor;


        /// <summary>欢迎语只补一次，之后即使清空记录也不再打扰。</summary>
        private bool _welcomeShown;
        private bool _styleCaptured;
        private int _styleWaitFrames;

        /// <summary>等待游戏窗口实例化的最大帧数（约 6 秒 @60fps）。</summary>
        private const int StyleWaitFramesBeforeFallback = 360;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            try
            {
                _config = new PluginConfig(Config);
                // manifest 工具的写判定；桥关闭时 manifest 不会加载，钩子恒为 false。
                IcarusII.Common.Protocol.Protocol.ExtraWriteTool = Game.Tools.ModManifest.IsWrite;
                GoalPanelOverride.Apply(_config.ShowAiGoalsInFreeExplore, _config.ShowAiGoalsInSandbox);
                _dispatcher = new MainThreadDispatcher();
                _tools = new GameToolDispatcher(Log);
                _hotkey = new HotkeyService(_config);
                _advisor = new AdvisorNotifier(_hotkey);
                _chat = new ChatWindow();
                _chat.Submitted += OnUserSubmitted;
                _chat.ThinkingLevelChanged += OnThinkingLevelChanged;
                _chat.PermissionLevelChanged += OnPermissionLevelChanged;
                _chat.TurnDeleted += OnTurnDeleted;
                _chat.CompactionUndoRequested += OnCompactionUndoRequested;
                _chat.RegenerateRequested += OnRegenerateRequested;
                _chat.ConversationCreateRequested += OnConversationCreateRequested;
                _chat.ConversationSwitchRequested += OnConversationSwitchRequested;
                _chat.ConversationDeleteRequested += OnConversationDeleteRequested;
                _chat.ConversationRenameRequested += OnConversationRenameRequested;
                _chat.ConversationPinRequested += OnConversationPinRequested;
                _chat.BreakRequested += OnBreakRequested;
                _chat.BlueprintCardActionRequested += OnBlueprintCardAction;
                _chat.AskCompleted += OnAskCompleted;
                _chat.AskCancelled += OnAskCancelled;
                _chat.ConfirmationChosen += OnConfirmationChosen;
                // 注意：_client 尚未创建（它在后台的 PrepareSidecar 里才 new），
                // 所以它的 ConfigChanged 必须在创建之后再订阅。
                // 这里曾经写成 _client.ConfigChanged += ...，直接 NullReference，
                // 导致 Awake 抛出、整个插件静默失效（日志里连"已加载"都没有）。

                _pluginDir = ResolvePluginDir();
                _launcher = new SidecarLauncher(_config, Log, _pluginDir);
            }
            catch (Exception ex)
            {
                // Awake 抛出会让插件彻底失效。这里兜住并显式记录，
                // 否则异常只出现在 Unity 的 Player.log 里，BepInEx 日志中毫无线索。
                _initFailed = true;
                Logger.LogError($"初始化失败，插件将不可用：{ex}");
                return;
            }

            Log.LogInfo($"{PluginName} {PluginVersion} 已加载");
            Log.LogInfo($"插件目录：{_pluginDir}");
            Log.LogInfo($"热键：{_config.HotkeyChord.Format()}（建造模式让位 = {_config.HotkeyRequireNoBuildTool}）");
            Log.LogInfo($"sidecar：{_config.LaunchMode} @ {_config.SidecarHost}:{_config.SidecarPort}");
            Log.LogInfo($"数据目录：{_launcher.DataDir}");

            // 后台准备好 sidecar，避免首次按 R 时还要等进程启动。
            // 全部失败路径都通过 dispatcher 回到主线程处理。
            Task.Run(PrepareSidecar);
        }

        private void Update()
        {
            // 设置窗口打开时自动补「模型」页签（不依赖从哪个入口进入设置）；
            // Tick 负责每帧的快捷键捕获与异步回执，Watch 只做 30 帧一次的安装检查。
            UI.ModelSettingsTab.Tick();
            UI.ModelSettingsTab.Watch();

            if (_initFailed) return;

            _dispatcher.Drain(MainThreadBudgetMs, ex => Log.LogError($"主线程任务异常：{ex}"));

            // 先同步存档绑定：换档后旧工具调用必须在同帧被取消，不能带着新档的数据返回。
            SyncSaveBinding();

            // 建筑 tether 的自绘线每帧跟随玩家/建筑；跨行星不画伪连线。
            GameTether.Update();

            // 原生 GoalProto 注入/读档后重建；没有自定义目标时只做一次空检查。
            GameGoals.Tick();

            // 工具采集必须在 UI 就绪之前也持续运行：模型可能在窗口关闭时仍在用工具。
            _tools.Tick(2, OnToolDone);

            // UIRoot 会在场景切换时重建；检测到旧对象被销毁就重置，等待重建
            if (_uiInitialized && !_chat.Alive)
            {
                _uiInitialized = false;
                _streaming = false;
                _chat.Reset();
                AgentMenuButton.Reset();
                _lastConfigState = null;
                Log.LogInfo("UI 随场景销毁，将在新场景中重建。");
            }

            EnsureUi();
            if (!_uiInitialized) return;

            // 回到开始菜单/载入中：强制关窗，R 也不应把窗口留在主菜单上。
            if (!GameRefs.IsInGame)
            {
                if (_chat.IsOpen) _chat.SetOpen(false);
                _advisor.DismissAll();
                return;
            }

            _chat.Tick();

            // 右下角 Game Menu（截图键正上方）的聊天入口。
            // 菜单/UIRoot 重建后自动补回；已存在时这里只做两次引用比较。
            IcarusII.Input.KeyChordInput.TryResolve(_hotkey.Key, out var hotkeyCode);
            AgentMenuButton.Ensure(hotkeyCode, OpenChatFromMenu);

            if (_uiInitialized && !_markersSanitized && GameRefs.IsInGame)
            {
                var fixedMarkers = MarkerSanitizer.FixAll(Log);
                if (fixedMarkers >= 0) _markersSanitized = true;
            }

            CheckAskTimeout();
            CheckConfirmTimeout();
            TryApplyGameStyle();

            // Esc：输入框（聊天框或标题框）聚焦时先取消焦点，否则关闭窗口。
            // 标题框可能抢先消费掉 Esc 并失焦，这种情况窗口要保持打开。
            if (_chat.IsOpen && _hotkey.EscapePressedThisFrame)
            {
                if (_chat.AnyInputFocused) _chat.BlurActiveInput();
                else if (!_chat.TitleEditEndedThisFrame) _chat.SetOpen(false);
                return;
            }

            // R：开关窗口（输入框聚焦时让位给文字输入）
            if (_hotkey.ShouldToggle(_chat.IsOpen, _chat.AnyInputFocused, out var blockedBy))
            {
                var opening = !_chat.IsOpen;
                _chat.SetOpen(opening);
                if (opening)
                {
                    OnWindowOpened();
                    Log.LogInfo($"[热键] 已打开窗口。{_chat.DescribeVisibility()}");
                }
                else
                {
                    Log.LogInfo("[热键] 已关闭窗口。");
                }
            }
            else if (_hotkey.KeyPressedThisFrame)
            {
                Log.LogInfo($"[热键] {_hotkey.Key} 已按下但被拒绝：{blockedBy}");
                Log.LogInfo($"[热键]   判定依据：{GameRefs.DescribeHotkeyGate()}");
            }

            // 顾问提示放在最后：刚打开的窗口会在 OnWindowOpened 里清空提示，
            // 同帧不该再弹出。
            _advisor.Tick();
        }

        /// <summary>
        /// 在 LateUpdate 再压制一次游戏输入。
        /// 游戏若在 Update 里重写 VFInput.inputing，这里能确保最终值是"正在输入"。
        /// </summary>
        private void LateUpdate()
        {
            if (_uiInitialized && _chat.WantsInputSuppressed)
                GameRefs.SetTextInputActive(true);
        }

        private void OnDestroy()
        {
            try { CancelPendingAsk("插件卸载", Protocol.CodeCancelled); } catch { /* ignore */ }
            try { CancelPendingConfirm("插件卸载"); _confirmation.Clear(); GameTether.Clear(); } catch { /* ignore */ }
            try { _advisor?.DismissAll(); } catch { /* ignore */ }
            try { AgentMenuButton.Reset(); } catch (Exception ex) { Log?.LogWarning($"清理 Game Menu 入口失败：{ex.Message}"); }
            try { _chat?.Destroy(); } catch (Exception ex) { Log?.LogWarning($"清理 UI 失败：{ex.Message}"); }
            try { _tools?.CancelAll("插件卸载"); } catch { /* ignore */ }
            try { _client?.Stop(); } catch (Exception ex) { Log?.LogWarning($"停止客户端失败：{ex.Message}"); }
            try { _launcher?.Shutdown(); } catch (Exception ex) { Log?.LogWarning($"回收 sidecar 失败：{ex.Message}"); }
            Instance = null;
        }

        // ---------------------------------------------------------------- sidecar

        private void PrepareSidecar()
        {
            if (_sidecarStartAttempted) return;
            _sidecarStartAttempted = true;

            try
            {
                Log.LogInfo("正在准备 sidecar…");
                if (!_launcher.TryPrepare(out var options, out var error))
                {
                    Post(() => ShowHint(Strings.Get("hint.disconnected") + "\n" + error));
                    Log.LogError($"sidecar 准备失败：{error}");
                    return;
                }

                var client = new SidecarClient(options);
                client.StateChanged += OnClientStateChanged;
                client.Received += OnClientMessage;
                client.Log += msg => Log.LogDebug($"[client] {msg}");
                client.ConfigChanged += OnConfigChanged;
                _client = client;
                IcarusII.UI.ModelSettingsTab.Bind(client);   // 设置窗口的"模型"页签需要读写配置
                client.Start();
            }
            catch (Exception ex)
            {
                Log.LogError($"准备 sidecar 时异常：{ex}");
                Post(() => ShowHint(Strings.Get("plugin.hint.launch_failed", ex.Message)));
            }
        }

        /// <summary>
        /// 插件开关或适配器变化后，把最新的能力/扩展列表推给 sidecar。
        /// 断线时忽略——下一条 hello 会带上最新状态。
        /// </summary>
        internal bool NotifyExtensionsChanged()
        {
            var client = _client;
            if (client is null) return false;
            try
            {
                var payload = Protocol.BuildClientState(
                    SidecarLauncher.BuildCapabilities(),
                    SidecarLauncher.BuildExtensions());
                if (client.Send(Protocol.Extensions, payload))
                {
                    _lastModsSnapshot = ModBridge.Snapshot();
                    return true;
                }
                Log?.LogDebug("[mods] sidecar 未就绪，插件列表变化留给下次 hello");
                return false;
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[mods] 推送插件列表失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 有些插件在 Start（而不是 Awake）里才 Config.Bind，hello 时配置项还是 0。
        /// 只在“即将需要工具列表”的时机比较一次：连接就绪、玩家发送。
        /// </summary>
        private void RefreshModExtensions()
        {
            var snapshot = ModBridge.Snapshot();
            if (string.Equals(snapshot, _lastModsSnapshot, StringComparison.Ordinal)) return;
            NotifyExtensionsChanged();
        }

        private void OnClientStateChanged(ConnectionState state, string detail)
        {
            Log.LogInfo($"[连接] {state} {detail}");
            Post(() =>
            {
                switch (state)
                {
                    case ConnectionState.Connecting:
                        _chat.SetStatus(Strings.Get("status.connecting"), null);
                        break;
                    case ConnectionState.Handshaking:
                        _chat.SetStatus(Strings.Get("status.handshaking"), null);
                        break;
                    case ConnectionState.Ready:
                        _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                        // 新连接上服务端对存档一无所知，必须重新绑定
                        _saveSynced = false;
                        UI.SettingsPageView.ServerVersion = ServerVersion;
                        RefreshModExtensions();
                        break;
                    case ConnectionState.Backoff:
                        ServerVersion = null;
                        UI.SettingsPageView.ServerVersion = null;
                        _tools.CancelAll("连接退避");
                        CancelPendingAsk("连接退避", Protocol.CodeToolUnavailable);
                        CancelPendingConfirm("连接退避");
                        _regenerateDeleteId.Clear();
                        CancelPendingDebug();
                        _chat.ClearToolCatalog();
                        _agentRunning = false;
                        _chat.SetBusy(false);
                        _chat.SetStatus(Strings.Get("status.backoff") + " " + detail, WarnColor);
                        break;
                    default:
                        ServerVersion = null;
                        UI.SettingsPageView.ServerVersion = null;
                        _tools.CancelAll("连接断开");
                        CancelPendingAsk("连接断开", Protocol.CodeToolUnavailable);
                        CancelPendingConfirm("连接断开");
                        _confirmation.Clear();
                        WriteActionStack.Clear();
                        GameTether.Clear();
                        _regenerateDeleteId.Clear();
                        CancelPendingDebug();
                        _chat.ClearToolCatalog();
                        _agentRunning = false;
                        _chat.SetBusy(false);
                        _chat.SetStatus(Strings.Get("status.disconnected"), null);
                        break;
                }
            });
        }

        // ---------------------------------------------------------------- 消息

        private void OnClientMessage(string type, JsonValue payload, string replyTo)
        {
            // 事件在后台线程触发，必须回到主线程再碰 Unity
            Post(() => HandleMessage(type, payload, replyTo));
        }

        /// <summary>并行会话：只处理焦点会话的运行期帧；缺 conversationId 的老 sidecar 视为焦点。</summary>
        private bool IsFocusedPayload(JsonValue payload)
        {
            var id = payload?["conversationId"].AsString(null);
            return string.IsNullOrEmpty(id) || _chat == null ||
                   string.IsNullOrEmpty(_chat.ActiveConversationId) ||
                   id == _chat.ActiveConversationId;
        }

        private void HandleMessage(string type, JsonValue payload, string replyTo)
        {
            switch (type)
            {
                case Protocol.Ready:
                {
                    var model = payload["agent"]["model"].AsString("?");
                    var session = payload["sessionId"].AsString("?");
                    ServerVersion = payload["serverVersion"].AsString(null);
                    UI.SettingsPageView.ServerVersion = ServerVersion;
                    ApplyToolCatalog(payload);
                    _streaming = false;
                    _agentRunning = false;
                    _chat.SetBusy(false);
                    _chat.AppendSystem(Strings.Get("plugin.connected", Short(session), model));
                    break;
                }

                case Protocol.Status:
                {
                    if (!IsFocusedPayload(payload)) break;
                    var state = payload["state"].AsString("");
                    // A.15：回答中（含 aborting）显示「中断」，idle 换回「发送」。
                    bool running = state != Protocol.StateIdle;
                    if (_agentRunning != running)
                    {
                        _agentRunning = running;
                        _chat.SetBusy(running);
                    }
                    if (state == Protocol.StateThinking || state == Protocol.StateTools)
                    {
                        // 这里**不**提前创建回答条目：思考块/工具块先入输出区，
                        // 回答条目等第一个 text delta 到达时再建，顺序才是 思考 → 工具 → 回答。
                        _chat.SetStatus(
                            state == Protocol.StateTools ? Strings.Get("status.tools") : Strings.Get("status.thinking"),
                            AccentColor);
                    }
                    else if (state == Protocol.StateIdle)
                    {
                        _chat.EndThinking(finalize: true);
                        if (_streaming)
                        {
                            _chat.EndStream();
                            _streaming = false;
                        }
                        _chat.FinishTurn();
                        _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                    }
                    else if (state == Protocol.StateRetrying)
                    {
                        // 重试意味着上一条助手消息作废：把它的思考块收尾，
                        // 重试产生的推理另起一块，避免两段推理混在同一个块里。
                        _chat.EndThinking();
                        _chat.SetStatus(Strings.Get("plugin.status.retrying"), WarnColor);
                    }
                    else if (state == Protocol.StateCompacting)
                    {
                        // 压缩可能要几秒到几十秒；给玩家明确反馈，避免看起来像卡死。
                        _chat.EndThinking();
                        _chat.SetStatus(Strings.Get("status.compacting"), WarnColor);
                    }
                    break;
                }

                case Protocol.SaveBound:
                    OnSaveBound(SaveBoundInfo.Parse(payload));
                    break;

                case Protocol.ConversationCreated:
                    OnConversationCreated(ConversationEventInfo.Parse(payload));
                    break;

                case Protocol.ConversationSwitched:
                    OnConversationSwitched(ConversationEventInfo.Parse(payload));
                    break;

                case Protocol.ConversationDeleted:
                    OnConversationDeleted(ConversationEventInfo.Parse(payload));
                    break;

                case Protocol.ConversationUpdated:
                    OnConversationUpdated(ConversationEventInfo.Parse(payload));
                    break;

                case Protocol.ConversationActivity:
                    OnConversationActivity(ConversationActivityInfo.Parse(payload));
                    break;

                case Protocol.ConversationRenamed:
                    OnConversationRenamed(ConversationEventInfo.Parse(payload));
                    break;

                case Protocol.ConversationPinned:
                    OnConversationPinned(ConversationEventInfo.Parse(payload));
                    break;

                case Protocol.ToolActivity:
                {
                    if (!IsFocusedPayload(payload)) break;
                    // 本地工具（kb_* / run_script）原本在聊天窗里完全不可见，
                    // 玩家会误以为模型没用计算器。这里只补 UI 卡片，不参与执行。
                    var act = ToolActivityInfo.Parse(payload);
                    if (act != null && !string.IsNullOrEmpty(act.CallId))
                    {
                        var label = ToolLabel(act.Name);
                        if (act.Phase == "start")
                            _chat.BeginToolCard(act.CallId, label, act.Args);
                        else
                            _chat.FinishToolCard(act.CallId, label, act.Ok, act.Summary, act.Detail);
                    }
                    break;
                }

                case Protocol.BlueprintCard:
                {
                    if (!IsFocusedPayload(payload)) break;
                    // sidecar 在本轮 final message 之后才发，卡片自然排在对话末尾。
                    var card = BlueprintCardInfo.Parse(payload);
                    if (card != null && (!string.IsNullOrEmpty(card.FileName) || !string.IsNullOrEmpty(card.Path)))
                        _chat.AppendBlueprintCard(card);
                    break;
                }

                case Protocol.LinkCard:
                {
                    if (!IsFocusedPayload(payload)) break;
                    var card = LinkCardInfo.Parse(payload);
                    if (card != null) _chat.AppendLinkCard(card);
                    break;
                }

                case Protocol.HistoryDeleted:
                {
                    var info = HistoryDeletedInfo.Parse(payload);

                    // 刷新按钮的「删旧回合 → 重发提问」流程。
                    string regenerateText;
                    if (!string.IsNullOrEmpty(replyTo) && _regenerateDeleteId.TryGetValue(replyTo, out regenerateText))
                    {
                        _regenerateDeleteId.Remove(replyTo);
                        if (info == null || !info.Ok)
                        {
                            var reason = info == null || string.IsNullOrEmpty(info.Error)
                                ? Strings.Get("plugin.error.unknown")
                                : info.Error;
                            _chat.AppendSystem(Strings.Get("plugin.regenerate.failed_delete", reason));
                            _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                            break;
                        }

                        _streaming = false;
                        var regenerateId = _client != null
                            ? _client.SendPrompt(regenerateText, _chat.ActiveConversationId, _chat.GetRequiredTool(regenerateText))
                            : null;
                        if (regenerateId == null)
                        {
                            _chat.AppendSystem(Strings.Get("plugin.regenerate.offline"));
                            _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                            break;
                        }
                        _promptById[regenerateId] = regenerateText;
                        _chat.SetStatus(Strings.Get("status.thinking"), AccentColor);
                        Log.LogInfo($"[刷新] 已删除旧回合，重新发送提问：{regenerateId}");
                        break;
                    }

                    if (info != null && !info.Ok)
                    {
                        var reason = string.IsNullOrEmpty(info.Error) ? Strings.Get("plugin.error.unknown") : info.Error;
                        _chat.AppendSystem(Strings.Get("plugin.delete.failed", reason));
                    }
                    break;
                }

                case Protocol.ContextCompacted:
                {
                    if (!IsFocusedPayload(payload)) break;
                    var info = ContextCompactedInfo.Parse(payload);
                    if (info != null)
                    {
                        // 压缩可能发生在流式回答中途：AppendCompaction 会收尾半截正文，
                        // 下一次 text delta 需要重新 BeginStream。
                        _streaming = false;
                        _chat.AppendCompaction(
                            info.Summary,
                            info.Timestamp,
                            info.CompactionId,
                            info.TokensBefore,
                            info.EstimatedTokensAfter);
                    }
                    break;
                }

                case Protocol.HistoryCompactionUndone:
                {
                    var info = HistoryCompactionUndoneInfo.Parse(payload);
                    if (info != null && info.Ok)
                    {
                        _chat.RemoveCompaction(info.CompactionId);
                        _chat.AppendSystem(Strings.Get("chat.compaction.undone"));
                        _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                    }
                    else
                    {
                        var reason = info == null || string.IsNullOrEmpty(info.Error)
                            ? Strings.Get("plugin.error.unknown")
                            : info.Error;
                        _chat.AppendSystem(Strings.Get("chat.compaction.failed", reason));
                        _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                    }
                    break;
                }

                case Protocol.Stream:
                {
                    if (!IsFocusedPayload(payload)) break;
                    var streamChannel = payload["channel"].AsString("text");

                    // reasoning：模型的思考增量。进入输出区的可折叠思考块（不进正文）。
                    if (streamChannel == "reasoning")
                    {
                        _chat.AppendThinkingDelta(payload["delta"].AsString(""));
                        _chat.SetStatus(Strings.Get("status.thinking"), AccentColor);
                        break;
                    }

                    // 未知 channel 忽略（前向兼容）
                    if (streamChannel != "text") break;
                    if (!_streaming)
                    {
                        // 正文开始 ⇒ 思考结束，思考块收起并显示摘要。
                        _chat.EndThinking();
                        _streaming = true;
                        _chat.BeginStream(Strings.Get("agent"));
                    }
                    _chat.AppendStreamDelta(payload["delta"].AsString(""));
                    break;
                }

                case Protocol.Message:
                {
                    if (!payload["final"].AsBool()) break;
                    var focused = IsFocusedPayload(payload);
                    var conversationId = payload["conversationId"].AsString(null);

                    // 协议规定一轮 prompt 只有一个 final 终稿。若服务端因缺陷发了
                    // 第二个，这里必须忽略——否则会走下面的"非流式追加"分支，
                    // 把同一段回答在窗口里再写一遍（实测出现过）。
                    if (!string.IsNullOrEmpty(replyTo))
                    {
                        if (replyTo == _lastFinalReplyTo) break;
                        _lastFinalReplyTo = replyTo;
                    }

                    var finalText = string.Empty;
                    foreach (var block in payload["content"].Items)
                    {
                        if (block["type"].AsString() != "text") continue;
                        var part = block["text"].AsString("");
                        if (string.IsNullOrEmpty(part)) continue;
                        finalText = string.IsNullOrEmpty(finalText) ? part : finalText + "\n" + part;
                    }

                    // A.15：玩家中断的回合。已经流出的正文保留，只加「已停止」标记，
                    // 不当错误、不回存游戏时间。
                    if (payload["interrupted"].AsBool(false))
                    {
                        if (focused)
                        {
                            _chat.EndInterrupted();
                            _streaming = false;
                            _agentRunning = false;
                            _chat.SetBusy(false);
                            _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                        }
                        if (!string.IsNullOrEmpty(replyTo)) _promptById.Remove(replyTo);
                        break;
                    }

                    // 后台终稿只用于回存游戏时间，不渲染、不改焦点会话的按钮/状态。
                    if (!focused)
                    {
                        string backgroundPrompt = null;
                        if (!string.IsNullOrEmpty(replyTo))
                            _promptById.TryGetValue(replyTo, out backgroundPrompt);
                        if (!string.IsNullOrEmpty(backgroundPrompt))
                        {
                            RecordReplyTime(
                                backgroundPrompt,
                                finalText,
                                conversationId ?? _chat.ActiveConversationId);
                            _promptById.Remove(replyTo);
                        }
                        break;
                    }

                    _chat.EndThinking(finalize: true);
                    if (_streaming)
                    {
                        // 流式已经渲染过同样内容，直接收尾即可
                        _chat.EndStream();
                        _streaming = false;
                    }
                    else
                    {
                        foreach (var block in payload["content"].Items)
                        {
                            if (block["type"].AsString() == "text")
                                _chat.AppendLine(Strings.Get("agent"), block["text"].AsString(""));
                        }
                    }

                    // 把这一轮完成时的游戏时间回存到 sidecar；history 回放时脚注显示原时间。
                    // reply 原文用于区分“继续”这类重复提问。
                    string prompt = null;
                    if (!string.IsNullOrEmpty(replyTo)) _promptById.TryGetValue(replyTo, out prompt);
                    if (!string.IsNullOrEmpty(prompt))
                    {
                        RecordReplyTime(prompt, finalText, conversationId ?? _chat.ActiveConversationId);
                        _promptById.Remove(replyTo);
                    }

                    // 窗口关着时用顾问字幕提醒「回完了」；开着就已经看见了。
                    if (!_chat.IsOpen) _advisor.NotifyReply(prompt, finalText);
                    break;
                }

                case Protocol.ConfirmRequest:
                    OnConfirmRequest(payload);
                    break;

                case Protocol.ConfirmRelease:
                    OnConfirmRelease(payload);
                    break;

                case Protocol.ToolCall:
                    OnToolCall(payload, replyTo);
                    break;

                case Protocol.ToolDebugResult:
                    OnToolDebugResult(payload, replyTo);
                    break;

                case Protocol.Error:
                {
                    if (!IsFocusedPayload(payload)) break;
                    if (!string.IsNullOrEmpty(replyTo)) _promptById.Remove(replyTo);
                    var code = payload["code"].AsString("unknown");
                    var message = payload["message"].AsString("");

                    // 调试直调的 error（debug_disabled / bad_request / internal）只回调试块，
                    // 不要走普通错误提示，也不要 EndThinking/FinishTurn。
                    if (!string.IsNullOrEmpty(replyTo)
                        && _debugById.TryGetValue(replyTo, out var debug))
                    {
                        _debugById.Remove(replyTo);
                        _chat.AppendDebugResult(Strings.Get("plugin.debug.call_failed", debug.Name, code, message));
                        _chat.SetStatus(Strings.Get("status.ready"), WarnColor);
                        break;
                    }

                    _chat.EndThinking(finalize: true);
                    _chat.FinishTurn();
                    _chat.AppendSystem(Strings.Get("plugin.error", code, message));
                    if (_streaming)
                    {
                        _chat.EndStream();
                        _streaming = false;
                    }
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- 工具

        private void OnToolCall(JsonValue payload, string replyTo)
        {
            var call = ToolCallInfo.Parse(payload, replyTo);
            if (call == null)
            {
                Log.LogWarning("[tool] 收到无法解析的 tool_call");
                _client?.SendToolResult(replyTo ?? string.Empty, false, JsonValue.Null,
                    Protocol.CodeInvalidArgs, "tool_call 缺少 name/callId", null);
                return;
            }

            if (!string.IsNullOrEmpty(call.ConversationId))
                _toolConversations[call.CallId] = call.ConversationId;
            var focused = string.IsNullOrEmpty(call.ConversationId) || _chat == null ||
                          string.IsNullOrEmpty(_chat.ActiveConversationId) ||
                          call.ConversationId == _chat.ActiveConversationId;

            // 交互问答不是游戏工具：在进入 GameToolDispatcher 前拦截。
            if (call.Name == Protocol.ToolAskUser || call.Name == Protocol.ToolUiAskUser)
            {
                if (!focused)
                {
                    SendAskError(call.CallId, Protocol.CodeNotFocused,
                        "该会话不在前台，切换后再提问");
                    return;
                }
                HandleAskCall(call);
                return;
            }

            // 沙盒门禁的 mod 侧：即使 sidecar 被替换/绕过，也不执行沙盒专属工具。
            if (!Protocol.SandboxAllows(call.Name, GameRefs.SandboxToolsEnabled))
            {
                _client?.SendToolResult(call.CallId, false, JsonValue.Null,
                    Protocol.CodeSandboxRequired, Strings.Get("sandbox.required"), JsonValue.NewObject());
                _chat?.SetStatus(Strings.Get("sandbox.required"), WarnColor);
                return;
            }

            // 写操作双重门禁的 mod 侧：没有侧车确认产生的一次性令牌，绝不执行。
            // 权限档位只决定「确认页是否自动同意」，不绕过令牌校验。
            if ((call.Risk > 0 || IsWriteToolName(call.Name)) &&
                !_confirmation.TryConsume(call, out var confirmError))
            {
                _client?.SendToolResult(call.CallId, false, JsonValue.Null,
                    confirmError, "写操作缺少有效的玩家确认令牌", JsonValue.NewObject());
                _chat?.SetStatus(Strings.Get("status.ready"), WarnColor);
                return;
            }

            if (call.Internal)
            {
                _internalCalls.Add(call.CallId);
            }
            else
            {
                if (focused)
                {
                    _chat?.SetStatus(Strings.Get("plugin.status.reading", ToolLabel(call.Name)), AccentColor);
                    if (_chat != null && _chat.Created)
                        _chat.BeginToolCard(call.CallId, ToolLabel(call.Name), ToolArgsText(call.Args));
                }
            }

            if (_tools == null)
            {
                _client?.SendToolResult(call.CallId, false, JsonValue.Null,
                    Protocol.CodeToolUnavailable, Strings.Get("plugin.tool.not_ready"), null);
                _internalCalls.Remove(call.CallId);
                if (!call.Internal && _chat != null && _chat.Created)
                    _chat.FinishToolCard(call.CallId, ToolLabel(call.Name), false, Strings.Get("plugin.tool.not_ready"));
                return;
            }

            var immediate = _tools.Begin(call.CallId, call.Name, call.Args, call.TimeoutMs);
            if (immediate != null) OnToolDone(immediate);
        }

        /// <summary>
        /// ask_user：把问题交给聊天窗的 choose 面板，等待 AskCompleted/AskCancelled。
        /// 同一时刻只允许一个 pending ask；结果仍走 tool_result，不新增协议帧。
        /// </summary>
        private void HandleAskCall(ToolCallInfo call)
        {
            if (_pendingAsk != null)
            {
                SendAskError(call.CallId, Protocol.CodeBusy, "已有一个提问正在等待玩家回答");
                return;
            }
            if (!AskArgs.TryParse(call.Args, out var questions, out var error))
            {
                SendAskError(call.CallId, Protocol.CodeInvalidArgs, error);
                return;
            }

            EnsureUi();
            if (!_uiInitialized || _chat == null || !_chat.Created)
            {
                SendAskError(call.CallId, Protocol.CodeNotInGame, "聊天窗尚未就绪");
                return;
            }

            // 窗口关着：不抢焦点开窗，先用顾问字幕提示，等玩家自己按热键进来。
            if (!_chat.IsOpen)
            {
                _pendingAskQuestions = questions;
                _pendingAsk = new PendingAsk
                {
                    CallId = call.CallId,
                    Deadline = Time.realtimeSinceStartup + call.TimeoutMs / 1000f,
                };
                _advisor.NotifyAsk(questions);
                Log.LogInfo($"[ask] 窗口关闭，已用顾问提示通知玩家（{questions.Count} 题）。");
                return;
            }

            _chat.SetOpen(true);
            if (!_chat.ShowAsk(questions))
            {
                SendAskError(call.CallId, Protocol.CodeToolUnavailable, "作答面板不可用或已有一个问题");
                return;
            }

            _pendingAsk = new PendingAsk
            {
                CallId = call.CallId,
                Deadline = Time.realtimeSinceStartup + call.TimeoutMs / 1000f,
            };
            _chat.SetStatus(Strings.Get("ask.waiting"), AccentColor);
        }

        private static bool IsWriteToolName(string name)
        {
            return Protocol.IsWriteTool(name);
        }

        /// <summary>sidecar 请求写操作确认：用 choose 的 question/explain + 红拒绝/蓝同意。</summary>
        private void OnConfirmRequest(JsonValue payload)
        {
            var request = ConfirmRequestInfo.Parse(payload);
            if (request == null)
            {
                Log.LogWarning("[confirm] confirm_request 载荷无效");
                return;
            }
            // 权限档位在 mod 侧生效：
            //   只读  → 所有写操作都要玩家确认；
            //   受限  → 只自动放行 risk 1（低风险、可逆），risk 2 仍弹确认；
            //   接管  → 全部自动同意。
            // 第三方 mod 工具用该插件自己的档位（默认受限），不跟随全局档位。
            // 自动同意也走 TryRegister/TryResolve，一次性令牌与工具白名单照常校验。
            var permission = string.IsNullOrEmpty(request.ModGuid)
                ? (_config != null ? _config.PermissionLevel : Protocol.DefaultPermission)
                : Game.Tools.ModPolicy.PermissionFor(request.ModGuid);
            if (Protocol.AutoApprovesWrite(permission, request.Risk))
            {
                if (!_confirmation.TryRegister(request, out var autoError))
                {
                    SendConfirmResult(request, false);
                    Log.LogWarning($"[confirm] 自动放行时无法登记确认请求：{autoError}");
                    return;
                }
                if (!_confirmation.TryResolve(request.RequestId, true, out var autoResolved))
                {
                    SendConfirmResult(request, false);
                    Log.LogWarning($"[confirm] 自动放行的确认请求不合法 tool={request.Tool}");
                    return;
                }
                SendConfirmResult(autoResolved, true);
                Log.LogInfo($"[confirm] 权限 {permission}：自动同意 {request.Tool} risk={request.Risk} callId={request.CallId}");
                return;
            }

            // 需要玩家点的确认只允许来自焦点会话；后台会话的 risk 1 已在上面
            // 按权限自动放行，不会走到这里。sidecar 也会在后台等待焦点，这里是兜底。
            if (!string.IsNullOrEmpty(request.ConversationId) && _chat != null &&
                !string.IsNullOrEmpty(_chat.ActiveConversationId) &&
                request.ConversationId != _chat.ActiveConversationId)
            {
                SendConfirmResult(request, false);
                Log.LogInfo($"[confirm] 非焦点会话请求，已拒绝：{request.Tool} {request.ConversationId}");
                return;
            }

            EnsureUi();
            if (!_uiInitialized || _chat == null || !_chat.Created)
            {
                SendConfirmResult(request, false);
                return;
            }
            if (!_confirmation.TryRegister(request, out var error))
            {
                SendConfirmResult(request, false);
                Log.LogWarning($"[confirm] 无法登记确认请求：{error}");
                return;
            }

            // 窗口关着：不抢焦点开窗，先用顾问字幕提示；确认页等玩家按热键进来再放。
            if (!_chat.IsOpen)
            {
                _pendingConfirm = request;
                _advisor.NotifyConfirm(request.Title, request.Body);
                Log.LogInfo($"[confirm] 窗口关闭，已用顾问提示通知玩家 {request.Tool} callId={request.CallId}");
                return;
            }

            if (!_chat.ShowConfirmation(request.Title, request.Body))
            {
                _confirmation.TryResolve(request.RequestId, false, out _);
                SendConfirmResult(request, false);
                return;
            }
            _chat.SetStatus(Strings.Get("confirm.waiting"), AccentColor);
            Log.LogInfo($"[confirm] 等待玩家确认 {request.Tool} callId={request.CallId}");
        }

        private void OnConfirmationChosen(bool accepted)
        {
            var request = _confirmation.Current;
            if (request == null) return;
            if (!_confirmation.TryResolve(request.RequestId, accepted, out var resolved))
            {
                // 确认请求本身不合法（例如脚本租约缺少 writeNames）：按拒绝回执，
                // 不能让 sidecar 干等 60 秒超时。
                Log.LogWarning($"[confirm] 拒绝不合法的确认请求 tool={request.Tool}");
                SendConfirmResult(request, false);
                _chat?.SetStatus(Strings.Get("sandbox.required"), WarnColor);
                return;
            }
            SendConfirmResult(resolved, accepted);
            ClearPendingConfirm();
            _chat?.SetStatus(Strings.Get("status.ready"), accepted ? OkColor : WarnColor);
        }

        private void OnConfirmRelease(JsonValue payload)
        {
            var token = payload["token"].AsString(null);
            if (string.IsNullOrEmpty(token)) return;
            _confirmation.Release(token);
            Log.LogInfo("[confirm] 脚本写租约已释放");
        }

        private void SendConfirmResult(ConfirmRequestInfo request, bool accepted)
        {
            if (request == null || _client == null) return;
            var payload = JsonValue.NewObject();
            payload.Set("requestId", request.RequestId);
            payload.Set("decision", accepted ? "allow" : "deny");
            payload.Set("token", request.Token);
            _client.Send(Protocol.ConfirmResult, payload);
        }

        private void CancelPendingConfirm(string reason)
        {
            var request = _confirmation.Current;
            if (request == null) return;
            _confirmation.TryResolve(request.RequestId, false, out _);
            SendConfirmResult(request, false);
            ClearPendingConfirm();
            _chat?.CancelAsk();
            _chat?.SetStatus(Strings.Get("status.ready"), WarnColor);
            Log.LogInfo($"[confirm] 取消确认：{reason}");
        }

        private void CheckConfirmTimeout()
        {
            var expired = _confirmation.Expire();
            if (expired == null) return;
            SendConfirmResult(expired, false);
            ClearPendingConfirm();
            _chat?.CancelAsk();
            _chat?.SetStatus(Strings.Get("status.ready"), WarnColor);
        }

        /// <summary>确认页收尾：清掉「开窗后再放」的暂存和还没消失的顾问字幕。</summary>
        private void ClearPendingConfirm()
        {
            _pendingConfirm = null;
            _advisor?.Dismiss(AdvisorNotifier.ConfirmTipId);
        }

        private void OnAskCompleted(List<AskAnswer> answers)
        {
            var pending = _pendingAsk;
            if (pending == null) return;
            _pendingAsk = null;
            ClearPendingAsk();
            _client?.SendToolResult(pending.CallId, true, BuildAskAnswers(answers),
                null, null, JsonValue.NewObject());
            _chat?.SetStatus(Strings.Get("status.ready"), OkColor);
        }

        private void OnAskCancelled()
        {
            var pending = _pendingAsk;
            if (pending == null) return;
            _pendingAsk = null;
            ClearPendingAsk();
            _client?.SendToolResult(pending.CallId, false, JsonValue.Null,
                Protocol.CodeCancelled, "玩家取消了提问", JsonValue.NewObject());
            _chat?.SetStatus(Strings.Get("status.ready"), OkColor);
        }

        /// <summary>断线/换档/超时：先收起 UI，再按指定错误码结束 pending 调用。</summary>
        private void CancelPendingAsk(string reason, string code)
        {
            var pending = _pendingAsk;
            if (pending == null) return;
            _pendingAsk = null;
            ClearPendingAsk();
            _chat?.CancelAsk();
            _client?.SendToolResult(pending.CallId, false, JsonValue.Null, code, reason,
                JsonValue.NewObject());
            _chat?.SetStatus(Strings.Get("status.ready"), OkColor);
        }

        /// <summary>作答收尾：清掉「开窗后再放」的暂存和还没消失的顾问字幕。</summary>
        private void ClearPendingAsk()
        {
            _pendingAskQuestions = null;
            _advisor?.Dismiss(AdvisorNotifier.AskTipId);
        }

        private void CheckAskTimeout()
        {
            if (_pendingAsk == null) return;
            if (Time.realtimeSinceStartup < _pendingAsk.Deadline) return;
            CancelPendingAsk("等待玩家回答超时", Protocol.CodeTimeout);
        }

        private void SendAskError(string callId, string code, string message)
        {
            if (string.IsNullOrEmpty(callId)) return;
            _client?.SendToolResult(callId, false, JsonValue.Null, code, message, JsonValue.NewObject());
            Log.LogWarning($"[ask] {code}: {message}");
        }

        private static JsonValue BuildAskAnswers(List<AskAnswer> answers)
        {
            var array = JsonValue.NewArray();
            if (answers != null)
            {
                foreach (var answer in answers)
                {
                    if (answer == null) continue;
                    var item = JsonValue.NewObject();
                    item.Set("index", answer.Index);
                    item.Set("question", answer.Prompt ?? string.Empty);
                    item.Set("answer", answer.Skipped ? JsonValue.Null : JsonValue.Of(answer.Answer ?? string.Empty));
                    item.Set("optionIndex", answer.OptionIndex);
                    item.Set("custom", answer.Custom);
                    item.Set("skipped", answer.Skipped);

                    var values = JsonValue.NewArray();
                    foreach (var value in answer.Answers) values.Add(value ?? string.Empty);
                    item.Set("answers", values);

                    var indexes = JsonValue.NewArray();
                    foreach (var index in answer.OptionIndexes) indexes.Add(index);
                    item.Set("optionIndexes", indexes);
                    array.Add(item);
                }
            }

            var data = JsonValue.NewObject();
            data.Set("answers", array);
            return data;
        }

        private void OnToolDone(GameToolOutcome outcome)
        {
            if (outcome == null) return;
            SendToolOutcome(outcome);

            // 内部调用（知识库导出）只回结果，不碰聊天窗。
            if (_internalCalls.Remove(outcome.CallId)) return;

            _toolConversations.TryGetValue(outcome.CallId, out var conversationId);
            _toolConversations.Remove(outcome.CallId);
            var focused = string.IsNullOrEmpty(conversationId) || _chat == null ||
                          string.IsNullOrEmpty(_chat.ActiveConversationId) ||
                          conversationId == _chat.ActiveConversationId;

            var label = ToolLabel(outcome.Name);
            if (outcome.Ok)
            {
                var summary = string.IsNullOrEmpty(outcome.Summary) ? Strings.Get("plugin.tool.done") : outcome.Summary;
                if (focused && _chat != null && _chat.Created)
                {
                    _chat.FinishToolCard(outcome.CallId, label, true, summary, outcome.Summary);
                    // 截图是本机产生的，不需要绕协议回来：直接把字节贴到工具卡上。
                    AttachToolImage(outcome);
                }
                if (focused) _chat?.SetStatus(Strings.Get("status.ready"), OkColor);
            }
            else
            {
                var message = outcome.ErrorMessage ?? outcome.ErrorCode ?? Strings.Get("plugin.error.unknown");
                if (focused && _chat != null && _chat.Created)
                    _chat.FinishToolCard(outcome.CallId, label, false, message, message);
                if (focused) _chat?.SetStatus(Strings.Get("status.ready"), WarnColor);
            }
        }

        /// <summary>工具参数的可读文本（折叠块展开后显示）。参数缺失/过大时给个占位。</summary>
        private static string ToolArgsText(JsonValue args)
        {
            if (args == null || args.IsNull) return null;
            var text = args.ToString();
            if (string.IsNullOrEmpty(text) || text == "{}") return null;
            const int max = 600;
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private void SendToolOutcome(GameToolOutcome outcome)
        {
            if (outcome == null || _client == null) return;
            var sent = _client.SendToolResult(
                outcome.CallId,
                outcome.Ok,
                outcome.Ok ? outcome.Data : JsonValue.Null,
                outcome.ErrorCode,
                outcome.ErrorMessage,
                outcome.Meta,
                outcome.Ok ? outcome.Image : null);
            if (!sent)
                Log.LogWarning($"[tool] tool_result 未能发出（连接未就绪）callId={outcome.CallId}");
        }

        /// <summary>工具结果带截图时贴到工具卡上（M4a）。</summary>
        private void AttachToolImage(GameToolOutcome outcome)
        {
            var image = outcome.Image;
            if (image == null || !image.IsObject) return;

            var data = image["data"].AsString(null);
            if (string.IsNullOrEmpty(data)) return;

            // 协议上限是解码后 2 MiB；base64 再放宽一点，超过就明确记日志而不是静默失败。
            if (data.Length > 4 * 1024 * 1024)
            {
                Log.LogWarning($"[chat] 截图过大，跳过显示（base64 {data.Length} 字符）");
                return;
            }

            try
            {
                _chat.AttachToolImage(outcome.CallId, Convert.FromBase64String(data));
            }
            catch (FormatException ex)
            {
                Log.LogWarning($"[chat] 截图 base64 解码失败：{ex.Message}");
            }
        }

        private static string ToolLabel(string name) => ToolNames.Label(name);

        // ---------------------------------------------------------------- UI

        private void EnsureUi()
        {
            if (_uiInitialized) return;
            // 开始菜单/载入中不建窗，避免主菜单看到上一局残留或误触打开。
            if (!GameRefs.IsInGame) return;

            var root = GameRefs.UiRoot;
            if (root == null) return;

            // 先捕获游戏原生样式，再建 UI。
            //
            // 顺序很重要：字号决定标题栏/输入框的高度与录音区布局，建成后再改
            // 会导致整体错位（M1 实测：捕获到的 fontSize=12 一直没被用上）。
            // 拿不到就等下一帧重试；超过 _styleWaitMs 仍拿不到才用兜底样式建，
            // 避免因为找不到参考窗口就永远不显示 UI。
            if (!_styleCaptured)
            {
                _styleCaptured = UiStyle.TryCapture(root, Log);
                if (!_styleCaptured)
                {
                    _styleWaitFrames++;
                    if (_styleWaitFrames < StyleWaitFramesBeforeFallback) return;
                    Log.LogWarning("等待游戏样式超时，将使用内置兜底配色。");
                }
            }

            Font font;
            try
            {
                font = FontProvider.Resolve();
            }
            catch (Exception ex)
            {
                if (!_uiCreateFailureLogged)
                {
                    _uiCreateFailureLogged = true;
                    Log.LogError($"获取字体失败：{ex.Message}");
                }
                return;
            }

            if (font == null)
            {
                // 每帧都会走到这里，只在首次记录，避免刷爆日志
                if (!_uiCreateFailureLogged)
                {
                    _uiCreateFailureLogged = true;
                    Log.LogError("找不到可用字体，聊天窗口无法创建。");
                }
                return;
            }

            Log.LogInfo($"[UI] 界面形态：独立窗口（热键 {_hotkey.Key}）");

            // 建窗失败绝不能每帧刷一条同样的错误，也不能让异常抛进 Unity 的 Update；
            // 记一次日志后保持 _uiInitialized=false，下一帧继续尝试（可能只是 UIRoot 还没好）。
            try
            {
                _chat.Create(root, font, _config);
            }
            catch (Exception ex)
            {
                if (!_uiCreateFailureLogged)
                {
                    _uiCreateFailureLogged = true;
                    Log.LogError($"创建聊天窗口失败：{ex}");
                }
                return;
            }

            if (_styleCaptured) _chat.ApplyStyle(UiStyle.Current);
            _uiInitialized = true;

            // UI 刚建好：立刻补上此前收到、但当时还没有控件可用的配置状态
            ApplyConfigState();
            _uiCreateFailureLogged = false;
            Log.LogInfo($"聊天窗口已创建（自建 Canvas，缩放来源 {ChatWindow.ScalerSource}，字体来源 {FontProvider.Source}）");

            var conflict = _hotkey.RunConflictScan();
            if (!string.IsNullOrEmpty(conflict)) Log.LogInfo($"[热键] {conflict}");

            if (_pendingHint != null)
            {
                _chat.AppendSystem(_pendingHint);
                _pendingHint = null;
            }

            TryApplyGameStyle();
        }

        /// <summary>
        /// 玩家在聊天窗右下角切换了「思考」档位。
        ///
        /// 1. 先落到本地 icarus-ii.cfg（断线/重启也不丢）；
        /// 2. 连接就绪就发 config.set，让 sidecar 落盘并重建 pi session；
        /// 3. 连接没就绪就记 dirty，等 ready 后补发。
        /// </summary>
        /// <summary>
        /// 玩家删除了一个问答回合：转给 sidecar 删会话文件（协议 A.7）。
        /// UI 已经先删掉了；这里失败只提示，不把行加回来（避免闪烁）。
        /// </summary>
        private void OnTurnDeleted(string text, long timestampMs)
        {
            if (string.IsNullOrEmpty(text) || _client == null) return;
            var saveKey = GameRefs.CurrentSaveKey();
            var id = _client.SendHistoryDelete(saveKey, text, timestampMs, _chat.ActiveConversationId);
            if (id == null)
            {
                _chat.AppendSystem(Strings.Get("plugin.delete.offline"));
                return;
            }
            Log.LogInfo($"[索引] 已请求 sidecar 删除回合：{id}");
        }

        /// <summary>
        /// 玩家点了压缩记录行的删除：请求 sidecar 真正删掉 compaction entry（A.11）。
        /// UI 不先删；成功后由 history.compaction_undone 回调移除并恢复灰色区间。
        /// </summary>
        private void OnCompactionUndoRequested(string compactionId)
        {
            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _chat.AppendSystem(Strings.Get("plugin.compaction_undo.offline"));
                return;
            }
            var saveKey = GameRefs.CurrentSaveKey();
            var id = _client.SendHistoryUndoCompaction(saveKey, compactionId, _chat.ActiveConversationId);
            if (id == null)
            {
                _chat.AppendSystem(Strings.Get("plugin.compaction_undo.changed"));
                return;
            }
            _chat.SetStatus(Strings.Get("status.undoing"), WarnColor);
            Log.LogInfo($"[压缩] 已请求撤销压缩：{id}（{compactionId}）");
        }

        /// <summary>
        /// 玩家点了回答 footer 的刷新：先让 sidecar 把旧回合从会话里删掉（A.7），
        /// 成功后再用同一句提问重新发 prompt，这样模型上下文里不会留旧答案。
        /// </summary>
        private void OnRegenerateRequested(string text, long timestampMs)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _chat.AppendSystem(Strings.Get("plugin.regenerate.offline"));
                return;
            }

            var saveKey = GameRefs.CurrentSaveKey();
            var id = _client.SendHistoryDelete(saveKey, text, timestampMs, _chat.ActiveConversationId);
            if (id == null)
            {
                _chat.AppendSystem(Strings.Get("plugin.regenerate.changed"));
                return;
            }
            _regenerateDeleteId[id] = text;
            _chat.SetStatus(Strings.Get("status.regenerating"), AccentColor);
            Log.LogInfo($"[刷新] 请求删除旧回合后重新推理：{id}");
        }

        private void OnThinkingLevelChanged(string level)
        {
            if (!PluginConfig.IsValidThinkingLevel(level)) return;

            _config?.SetThinkingLevel(level);

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _thinkingLevelDirty = true;
                Log.LogInfo($"[配置] 思考档位 → {level}（连接未就绪，稍后补发）");
                return;
            }

            var id = _client.SendConfigSet(thinkingLevel: level);
            _thinkingLevelDirty = id == null;
            Log.LogInfo($"[配置] 思考档位 → {level}（config.set {(id != null ? "已发送" : "发送失败")}）");
        }

        /// <summary>
        /// 玩家在聊天窗里换了权限档位。本地 cfg 先落盘（断线也能记住），
        /// 再尽力发给 sidecar；tool_call / confirm_request 的门禁都读本地值。
        /// </summary>
        /// <summary>sidecar 的 serverVersion（ready 后写入）；设置页通用页用。</summary>
        public string ServerVersion { get; private set; }

        /// <summary>sidecar 数据目录；设置页「复制路径」用。</summary>
        public string DataDir => _launcher != null ? _launcher.DataDir : SidecarLauncher.DefaultDataDir();

        /// <summary>当前权限档位；设置页打开时用它点亮卡片。</summary>
        public string CurrentPermissionLevel => _chat != null ? _chat.PermissionLevel : _config?.PermissionLevel;

        /// <summary>A.14.7：设置页读取某个按键动作的当前绑定。</summary>
        public IcarusII.Input.KeyChord CurrentKeyBinding(string action)
        {
            if (_config is null) return IcarusII.Input.KeyChord.None;
            switch (action)
            {
                case PluginConfig.KeyActionSend: return _config.SendChord;
                case PluginConfig.KeyActionNewline: return _config.NewlineChord;
                default: return _config.HotkeyChord;
            }
        }

        /// <summary>A.14.7：某个动作换成新组合会不会与另外两项冲突；返回冲突动作 id。</summary>
        public string KeyConflict(string action, IcarusII.Input.KeyChord chord)
        {
            return _config?.KeyConflict(action, chord);
        }

        /// <summary>A.14.7：恢复三项默认按键（顺序无关，配置层一次写完）。</summary>
        public void ResetKeyBindingsFromSettings()
        {
            _config?.ResetKeyBindings();
            if (_config is null) return;
            _hotkey?.SetKey(_config.HotkeyChord);
            _chat?.ApplyKeyBindings(_config.SendChord, _config.NewlineChord);
        }

        /// <summary>A.14.7：设置页保存按键绑定；重复绑定由配置层拒绝。</summary>
        public bool ApplyKeyBindingFromSettings(string action, IcarusII.Input.KeyChord chord)
        {
            if (_config is null || chord.IsNone) return false;
            if (!_config.SetKeyBinding(action, chord)) return false;

            _hotkey?.SetKey(_config.HotkeyChord);
            _chat?.ApplyKeyBindings(_config.SendChord, _config.NewlineChord);
            Log.LogInfo($"[配置] 按键 {action} → {chord.Format()}");
            return true;
        }

        /// <summary>设置页点了权限卡：走和聊天窗下拉框同一条路径（更新下拉框、落盘、发 config.set）。</summary>
        public void ApplyPermissionLevelFromSettings(string level)
        {
            if (_chat != null)
            {
                _chat.SetPermissionLevelFromSettings(level);
                return;
            }
            OnPermissionLevelChanged(level);
        }

        private void OnPermissionLevelChanged(string level)
        {
            if (!Protocol.IsValidPermissionLevel(level)) return;

            _config?.SetPermissionLevel(level);

            // 让玩家明确知道当前档位的行为差异（接管尤其重要）。
            var key = level == Protocol.PermissionReadonly
                ? "permission.changed.readonly"
                : level == Protocol.PermissionTakeover
                    ? "permission.changed.takeover"
                    : "permission.changed.limited";
            _chat?.AppendSystem(Strings.Get(key));

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _permissionLevelDirty = true;
                Log.LogInfo($"[配置] 权限 → {level}（连接未就绪，稍后补发）");
                return;
            }

            var id = _client.SendConfigSet(permissionLevel: level);
            _permissionLevelDirty = id == null;
            Log.LogInfo($"[配置] 权限 → {level}（config.set {(id != null ? "已发送" : "发送失败")}）");
        }

        /// <summary>sidecar 上报的配置状态变化：据此切换「首次引导」与正常聊天。</summary>
        private void OnConfigChanged(ConfigState config)
        {
            if (config == null) return;
            Log.LogInfo($"[配置] 状态：{config}");

            // 记下最新状态再回主线程应用。
            //
            // 关键：config 状态几乎总是**早于 UI 创建**到达（握手在插件加载后
            // 0.3 秒内完成，而 UI 要等 UIRoot 与样式捕获就绪）。如果只在这里
            // 应用一次，那时还没有可追加消息的控件，首次引导就永远不会出现
            // （实测踩到）。因此必须缓存，并在 UI 建好后由 ApplyConfigState
            // 再应用一次。
            _lastConfig = config;
            Post(ApplyConfigState);
        }

        /// <summary>把最近一次配置状态应用到 UI。UI 未就绪时留待建好后重试。</summary>
        private void ApplyConfigState()
        {
            var config = _lastConfig;
            if (config == null || _chat == null || !_uiInitialized) return;

            // 思考档位：
            //   - 断线期间玩家改过档位（dirty）→ 先把本地值补发给 sidecar，UI 也先显示本地值；
            //   - 否则跟随 sidecar 的 config.json（权威来源）；老服务端不带该字段时保持本地值。
            if (config.State == P.ConfigReady && _thinkingLevelDirty && _client != null)
            {
                var id = _client.SendConfigSet(thinkingLevel: _config.ThinkingLevel);
                _thinkingLevelDirty = id == null;
            }
            if (_thinkingLevelDirty) _chat.ApplyThinkingLevel(_config.ThinkingLevel);
            else if (!string.IsNullOrEmpty(config.ThinkingLevel)) _chat.ApplyThinkingLevel(config.ThinkingLevel);

            // 权限档位：与思考档位相同的「本地 dirty 优先，否则跟随 sidecar」。
            if (config.State == P.ConfigReady && _permissionLevelDirty && _client != null)
            {
                var id = _client.SendConfigSet(permissionLevel: _config.PermissionLevel);
                _permissionLevelDirty = id == null;
            }
            if (_permissionLevelDirty)
            {
                _chat.ApplyPermissionLevel(_config.PermissionLevel);
            }
            else if (!string.IsNullOrEmpty(config.PermissionLevel) &&
                     Protocol.IsValidPermissionLevel(config.PermissionLevel))
            {
                // sidecar 是权威来源；同时回写本地 cfg，断线/重启后仍保持一致。
                _config?.SetPermissionLevel(config.PermissionLevel);
                _chat.ApplyPermissionLevel(config.PermissionLevel);
            }

            // 只在状态真正变化时追加引导，避免切换模型/思考档位收到
            // 重复的 config.state 时刷屏。
            var previous = _lastConfigState;
            var changed = !string.Equals(previous, config.State, StringComparison.Ordinal);
            _lastConfigState = config.State;

            switch (config.State)
            {
                case P.ConfigMissingKey:
                    if (changed) _chat.AppendSystem(Strings.Get("config.missing_key"));
                    _chat.SetStatus(Strings.Get("status.needkey"), null);
                    break;

                case P.ConfigInvalidKey:
                    if (changed) _chat.AppendSystem(Strings.Get("config.invalid_key"));
                    _chat.SetStatus(Strings.Get("status.needkey"), null);
                    break;

                case P.ConfigUnreachable:
                    if (changed) _chat.AppendSystem(Strings.Get("config.unreachable"));
                    _chat.SetStatus(Strings.Get("status.needkey"), null);
                    break;

                case P.ConfigReady:
                    _chat.SetStatus(Strings.Get("status.ready"), null);
                    // 从缺 Key/失效/连不上恢复时给一次确认；启动时就已配好则不打扰。
                    if (changed && previous != null && previous != P.ConfigReady)
                        _chat.AppendSystem(Strings.Get("config.ready"));
                    break;
            }
        }

        /// <summary>
        /// 把对话串绑定到当前存档（协议附录 A.6）。
        ///
        /// 连接建立时还在主菜单，那时没有存档；玩家进入存档后 saveKey 才会出现。
        /// 因此这里每帧比对，仅在**变化时**上报（含离开存档 → saveKey=null 解绑）。
        /// 上报失败（尚未 Ready）不算数，下一帧会重试。
        /// </summary>
        private void SyncSaveBinding()
        {
            if (_client == null) return;

            var key = GameRefs.CurrentSaveKey();
            var sandbox = key != null && GameRefs.SandboxToolsEnabled;
            if (key != _saveKey)
            {
                CancelPendingAsk("存档切换", Protocol.CodeCancelled);
                CancelPendingConfirm("存档切换");
                _confirmation.Clear();
                WriteActionStack.Clear();
                GameTether.Clear();
                GameTether.SanitizeInvalidWarnings();
                _markersSanitized = false;
                _tools?.CancelAll("存档切换", OnToolDone);
                _tools?.ClearCache();
                _saveKey = key;
                _saveSynced = false;
                _sandboxValue = sandbox;
                _sandboxNoticeFor = null;
                _historyReplayedFor = null;
                _internalCalls.Clear();
                // 对话串也必须跟着换档：否则新存档里还挂着旧档的消息，
                // 即使 sidecar 已经换了会话，看起来仍然不是“新对话”。
                if (_chat != null && _chat.Created)
                {
                    _chat.ClearConversations();
                    _chat.ClearTranscript();
                }
                Log.LogInfo($"[存档] 当前存档变化：{key ?? "(无，主菜单)"}");
            }
            else if (sandbox != _sandboxValue)
            {
                // 同一存档重载后玩家可能改了“启用沙盒工具”；即使 saveKey 没变也要重报。
                _sandboxValue = sandbox;
                _saveSynced = false;
            }

            if (_saveSynced) return;
            if (_client.State != ConnectionState.Ready) return;

            var name = key != null ? GameRefs.CurrentSaveName() : null;
            JsonValue tiers = null;
            if (key != null)
            {
                var info = GameRefs.CurrentBlueprintTiers();
                if (info.Available)
                {
                    tiers = JsonValue.NewObject();
                    tiers.Set("assembler", info.Assembler);
                    tiers.Set("belt", info.Belt);
                    tiers.Set("inserter", info.Inserter);
                    tiers.Set("piler", info.Piler);
                    tiers.Set("stacking", info.Stacking);
                }
            }
            if (_client.SendSaveBind(key, name, tiers, sandbox) == null) return;

            _saveSynced = true;
            _sandboxValue = sandbox;
            Log.LogInfo($"[存档] 已上报绑定：{key ?? "(解绑)"} / {name ?? "-"} / sandbox={sandbox}");
        }

        /// <summary>连接重新就绪时，存档绑定需要重新上报（新连接上服务端一无所知）。</summary>
        private void OnSaveBound(SaveBoundInfo info)
        {
            if (info == null) return;

            Post(() =>
            {
                if (_chat == null) return;

                if (string.IsNullOrEmpty(info.SaveKey))
                {
                    _historyReplayedFor = null;
                    _sandboxNoticeFor = null;
                    _chat.ClearConversations();
                    _chat.ClearTranscript();
                    _chat.AppendSystem(Strings.Get("save.unbound"));
                    return;
                }

                var name = string.IsNullOrEmpty(info.SaveName) ? info.SaveKey : info.SaveName;
                var conversations = BuildConversations(info);
                var activeId = !string.IsNullOrEmpty(info.ActiveConversationId)
                    ? info.ActiveConversationId
                    : (conversations.Count > 0 ? conversations[0].Id : null);
                var replayKey = ReplayKey(info.SaveKey, activeId);
                var replay = info.History != null && info.History.Count > 0
                             && replayKey != _historyReplayedFor && !_streaming;

                if (replay)
                {
                    _chat.ClearTranscript();
                    _chat.ApplyConversations(conversations, activeId);

                    if (!info.Existed) _chat.AppendSystem(Strings.Get("save.new", name));
                    else if (info.MessageCount > 0)
                        _chat.AppendSystem(Strings.Get("save.bound", name, info.MessageCount));
                    else
                        _chat.AppendSystem(Strings.Get("save.bound.nocount", name));

                    // 沙盒工具会关闭该存档的成就，玩家必须知道 AI 当前拿到了这部分权限。
                    if (GameRefs.SandboxToolsEnabled && info.SaveKey != _sandboxNoticeFor)
                    {
                        _chat.AppendSystem(Strings.Get("save.sandbox"));
                        _sandboxNoticeFor = info.SaveKey;
                    }

                    if (ReplayHistory(info.History)) _historyReplayedFor = replayKey;
                }
                else
                {
                    // 重连/重复绑定：只刷新会话列表，不重复贴历史。
                    // 但如果 sidecar 的活动会话变了（例如上次退出前切过），窗口里的旧正文
                    // 已经不属于当前会话，必须清掉。
                    if (!string.IsNullOrEmpty(activeId) && activeId != _chat.ActiveConversationId)
                        _chat.ClearTranscript();
                    _chat.ApplyConversations(conversations, activeId);
                    if (!info.Existed) _chat.AppendSystem(Strings.Get("save.new", name));
                }

                _streaming = false;
                _agentRunning = false;
                _chat.SetBusy(false);
                _chat.SetStatus(Strings.Get("status.ready"), OkColor);
            });
        }

        /// <summary>
        /// 老 sidecar 没有 conversations 字段时，用 history 构造一个兼容会话，
        /// 保证左侧索引仍按新的分组 UI 显示（而不是整块空掉）。
        /// </summary>
        private static List<ConversationInfo> BuildConversations(SaveBoundInfo info)
        {
            if (info.Conversations.Count > 0) return info.Conversations;
            var conversation = ConversationInfo.Create("legacy", Strings.Get("chat.conversation.current"));
            if (info.History != null)
            {
                foreach (var message in info.History)
                {
                    if (message == null || message.Role != "user" || string.IsNullOrEmpty(message.Text))
                        continue;
                    conversation.AddRecord(ConversationRecordInfo.Create(
                        message.Text, message.Timestamp, message.GameTime));
                }
            }
            if (conversation.RecordCount > 0)
                conversation.SetTitle(ShortConversationTitle(conversation.Records[0].Text));
            var list = new List<ConversationInfo>();
            list.Add(conversation);
            return list;
        }

        private static string ShortConversationTitle(string text)
        {
            var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length == 0) return Strings.Get("chat.conversation.new");
            return value.Length <= 18 ? value : value.Substring(0, 18) + "…";
        }

        private static string ReplayKey(string saveKey, string conversationId)
        {
            return (saveKey ?? string.Empty) + "|" + (conversationId ?? string.Empty);
        }

        /// <summary>回放某会话历史；返回 false 表示有消息失败（下次 save.bound 会重试）。</summary>
        private bool ReplayHistory(List<HistoryMessageInfo> history)
        {
            if (_chat == null) return true;
            if (history == null || history.Count == 0)
            {
                _chat.FinishConversationReplay();
                return true;
            }

            // 逐条回放：单条（或某张蓝图卡片）失败只丢那一条，
            // 不能让整个回调抛异常——否则后面的历史全都不会出现。
            var replayed = 0;
            var failed = 0;
            foreach (var message in history)
            {
                if (message == null || string.IsNullOrEmpty(message.Text)) continue;
                try
                {
                    if (message.Kind == "compaction")
                    {
                        // 压缩记录：插一条特殊对话串，并把之前的历史标灰（A.11）。
                        _chat.AppendCompaction(message);
                    }
                    else if (message.Role == "user")
                    {
                        _chat.AppendHistoryUser(message.Text, message.Timestamp, message.GameTime);
                    }
                    else
                    {
                        // 回答连同它之前的思考/工具步骤一起还原（协议 A.6 steps）。
                        _chat.AppendHistoryReply(message);
                        // 蓝图卡片跟在该回答后面，与实时收到的顺序一致。
                        if (message.Cards != null)
                        {
                            foreach (var card in message.Cards)
                            {
                                if (card != null && string.IsNullOrEmpty(card.CreatedAt) && message.Timestamp > 0)
                                {
                                    card.CreatedAt = DateTimeOffset
                                        .FromUnixTimeMilliseconds(message.Timestamp)
                                        .LocalDateTime.ToString("yyyy-MM-dd");
                                }
                                _chat.AppendBlueprintCard(card);
                            }
                        }
                        if (message.LinkCards != null)
                        {
                            foreach (var link in message.LinkCards)
                                if (link != null) _chat.AppendLinkCard(link);
                        }
                    }
                    replayed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.LogWarning($"[存档] 回放历史消息失败（{message.Role}）：{ex.Message}");
                }
            }

            _chat.FinishConversationReplay();
            Log.LogInfo($"[存档] 回放历史：{replayed} 条，失败 {failed} 条");
            return failed == 0;
        }

        /// <summary>
        /// 回存一轮回答完成时的游戏时间（协议 A.10），并立刻刷新左侧所有会话的时间标记。
        /// 时间标记按“当前 gameTick − 该会话最后回复的 gameTick”计算，所以两个值来自同一次采样。
        /// </summary>
        private void RecordReplyTime(string prompt, string reply, string conversationId)
        {
            var gameTick = ChatWindow.CurrentGameTick();
            _client?.SendReplyTime(
                prompt,
                reply,
                ChatWindow.CurrentGameClock(),
                conversationId,
                gameTick);
            _chat?.MarkConversationReplied(conversationId, gameTick);
        }

        private void OnConversationActivity(ConversationActivityInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ConversationId)) return;
            Post(() =>
            {
                _chat?.SetConversationActivity(info.ConversationId, info.State, info.Reason);
            });
        }

        private void OnConversationUpdated(ConversationEventInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ConversationId) ||
                string.IsNullOrEmpty(info.Title)) return;
            Post(() =>
            {
                if (_chat == null) return;
                _chat.UpdateConversationTitle(info.ConversationId, info.Title);
            });
        }

        private void OnConversationRenameRequested(string conversationId, string title)
        {
            if (_chat == null || _client == null || _client.State != ConnectionState.Ready)
            {
                _chat?.RevertConversationTitle();
                return;
            }
            if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(title))
            {
                _chat.RevertConversationTitle();
                return;
            }
            if (_client.SendConversationRename(conversationId, title) == null)
            {
                _chat.AppendSystem(Strings.Get("chat.conversation.failed", "连接未就绪"));
                _chat.RevertConversationTitle();
            }
        }

        private void OnConversationRenamed(ConversationEventInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ConversationId)) return;
            Post(() =>
            {
                if (_chat == null) return;
                if (!info.Ok)
                {
                    _chat.AppendSystem(Strings.Get("chat.conversation.failed", info.Error ?? "unknown"));
                    _chat.RevertConversationTitle();
                    return;
                }
                _chat.UpdateConversationTitle(info.ConversationId, info.Title);
            });
        }

        /// <summary>
        /// 置顶回执：以 sidecar 的结果为准对齐（mod 之前已经乐观置顶）。
        /// 失败时把状态翻回去，玩家看到列表复原就知道没生效。
        /// </summary>
        private void OnConversationPinned(ConversationEventInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ConversationId)) return;
            Post(() =>
            {
                if (_chat == null) return;
                if (!info.Ok)
                {
                    _chat.SetConversationPinned(info.ConversationId, !info.Pinned);
                    _chat.AppendSystem(Strings.Get("chat.conversation.failed", info.Error ?? "unknown"));
                    return;
                }
                _chat.SetConversationPinned(info.ConversationId, info.Pinned);
            });
        }

        private void OnConversationCreated(ConversationEventInfo info)
        {
            if (info == null || info.Conversation == null) return;
            Post(() =>
            {
                if (_chat == null) return;
                _chat.ClearTranscript();
                _streaming = false;
                _agentRunning = false;
                _chat.SetBusy(false);
                _chat.UpsertConversation(info.Conversation, true);
                _chat.FinishConversationReplay();
                _historyReplayedFor = ReplayKey(info.SaveKey ?? _saveKey, info.Conversation.Id);
                if (!info.Ok)
                    _chat.AppendSystem(Strings.Get("chat.conversation.failed", info.Error ?? "unknown"));
                else
                    _chat.AppendSystem(Strings.Get("chat.conversation.created"));
                _chat.SetStatus(Strings.Get("status.ready"), OkColor);
            });
        }

        private void OnConversationSwitched(ConversationEventInfo info)
        {
            if (info == null || info.Conversation == null) return;
            Post(() =>
            {
                if (_chat == null) return;
                if (!info.Ok)
                {
                    _chat.CancelPendingConversationOpen();
                    _chat.AppendSystem(Strings.Get("chat.conversation.failed", info.Error ?? "unknown"));
                    _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                    return;
                }
                _chat.ClearTranscript();
                _streaming = false;
                _agentRunning = info.Running;
                _chat.SetBusy(info.Running);
                _chat.UpsertConversation(info.Conversation, true);
                // 切走时 sidecar 丢掉了那个没落盘的空会话：把它的行从左侧删掉，
                // 否则它会留在列表里，点回去只会得到 conversation_not_found。
                _chat.RemoveConversation(info.DiscardedConversationId);
                ReplayHistory(info.History);
                _historyReplayedFor = ReplayKey(info.SaveKey ?? _saveKey, info.Conversation.Id);
                if (info.Running)
                {
                    _chat.SetStatus(
                        info.State == Protocol.StateTools
                            ? Strings.Get("status.tools")
                            : Strings.Get("status.thinking"),
                        AccentColor);
                }
                else
                {
                    _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                }
            });
        }

        private void OnConversationDeleted(ConversationEventInfo info)
        {
            if (info == null) return;
            Post(() =>
            {
                if (_chat == null) return;
                if (!info.Ok)
                {
                    _chat.AppendSystem(Strings.Get("chat.conversation.failed", info.Error ?? "unknown"));
                    _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                    return;
                }
                var activeChanged = info.ActiveConversationId != _chat.ActiveConversationId;
                if (activeChanged)
                {
                    _chat.ClearTranscript();
                    _streaming = false;
                    _agentRunning = false;
                    _chat.SetBusy(false);
                    _chat.ApplyConversations(info.Conversations, info.ActiveConversationId);
                    ReplayHistory(info.History);
                    _historyReplayedFor = ReplayKey(info.SaveKey ?? _saveKey, info.ActiveConversationId);
                }
                else
                {
                    // 删的是别的会话：只更新左侧列表，不打断当前正文。
                    _chat.ApplyConversations(info.Conversations, info.ActiveConversationId);
                }
                _chat.AppendSystem(Strings.Get("chat.conversation.deleted"));
                _chat.SetStatus(Strings.Get("status.ready"), OkColor);
            });
        }

        private void OnConversationCreateRequested()
        {
            if (_chat == null || _client == null || _client.State != ConnectionState.Ready) return;
            if (_client.SendConversationCreate() == null)
                _chat.AppendSystem(Strings.Get("chat.conversation.failed", "连接未就绪"));
        }

        private void OnConversationSwitchRequested(string conversationId)
        {
            if (_chat == null || _client == null || _client.State != ConnectionState.Ready) return;
            if (string.IsNullOrEmpty(conversationId)) return;
            if (_client.SendConversationSwitch(conversationId) == null)
            {
                _chat.CancelPendingConversationOpen();
                _chat.AppendSystem(Strings.Get("chat.conversation.failed", "连接未就绪"));
            }
        }

        private void OnConversationDeleteRequested(string conversationId)
        {
            if (_chat == null || _client == null || _client.State != ConnectionState.Ready) return;
            if (string.IsNullOrEmpty(conversationId)) return;
            if (_client.SendConversationDelete(conversationId) == null)
                _chat.AppendSystem(Strings.Get("chat.conversation.failed", "连接未就绪"));
        }

        private void OnConversationPinRequested(string conversationId, bool pinned)
        {
            if (_chat == null || string.IsNullOrEmpty(conversationId)) return;
            if (_client == null || _client.State != ConnectionState.Ready)
            {
                // 乐观置顶已经生效，发不出去就翻回去，别让列表显示一个存不下来的状态。
                _chat.SetConversationPinned(conversationId, !pinned);
                _chat.AppendSystem(Strings.Get("chat.conversation.failed", "连接未就绪"));
                return;
            }
            if (_client.SendConversationPin(conversationId, pinned) == null)
            {
                _chat.SetConversationPinned(conversationId, !pinned);
                _chat.AppendSystem(Strings.Get("chat.conversation.failed", "连接未就绪"));
            }
        }

        /// <summary>
        /// 套用游戏原生面板样式。机体信息窗等预制体在主菜单可能尚未实例化，
        /// 因此这里会持续重试直到成功（或确认拿不到）。
        /// </summary>
        private void TryApplyGameStyle()
        {
            if (_styleApplied || !_uiInitialized) return;

            var root = GameRefs.UiRoot;
            if (root == null) return;

            if (!UiStyle.TryCapture(root, Log)) return;
            _styleCaptured = true;

            _chat.ApplyStyle(UiStyle.Current);
            _styleApplied = true;
            Log.LogInfo("[样式] 已套用游戏原生面板样式。");
        }

        private void OnWindowOpened()
        {
            // 顾问提示的使命就是「把玩家叫进来」，开窗即撤；后台攒下的作答/确认页在这里补上。
            _advisor.DismissAll();
            ShowDeferredPrompt();

            // 空记录时给一句欢迎语：一块什么都没有的深色面板看着像没做完，
            // 而且玩家也不知道这个窗口到底能问什么。
            if (!_welcomeShown && _chat.TranscriptEmpty)
            {
                _welcomeShown = true;
                _chat.AppendSystem(Strings.Get("welcome"));
            }

            if (_client == null)
            {
                _chat.AppendSystem(Strings.Get("hint.disconnected"));
                return;
            }

            // 首次打开时补一条提示，避免玩家不知道窗口有没有在工作
            if (_client.State == ConnectionState.Ready) return;
            _chat.SetStatus(Strings.Get("status.connecting"), null);
        }

        /// <summary>
        /// 补上窗口关着时收到的作答/确认面板。两者互斥（confirm 内部也用同一块 choose 面板），
        /// 所以先确认、后作答。
        /// </summary>
        private void ShowDeferredPrompt()
        {
            if (_pendingConfirm != null)
            {
                var request = _pendingConfirm;
                _pendingConfirm = null;
                if (_confirmation.Current == null) return;
                if (!_chat.ShowConfirmation(request.Title, request.Body))
                {
                    _confirmation.TryResolve(request.RequestId, false, out _);
                    SendConfirmResult(request, false);
                    return;
                }
                _chat.SetStatus(Strings.Get("confirm.waiting"), AccentColor);
                return;
            }

            if (_pendingAskQuestions == null) return;
            var questions = _pendingAskQuestions;
            _pendingAskQuestions = null;
            var pending = _pendingAsk;
            if (pending == null) return;
            if (!_chat.ShowAsk(questions))
            {
                _pendingAsk = null;
                SendAskError(pending.CallId, Protocol.CodeToolUnavailable, "作答面板不可用");
                return;
            }
            _chat.SetStatus(Strings.Get("ask.waiting"), AccentColor);
        }

        /// <summary>
        /// 点击 Game Menu 的聊天入口按钮。语义与热键的“打开”一致：
        /// 窗口已经开着时不做任何事（第二个入口不该把已打开的窗口关掉）。
        /// </summary>
        private void OpenChatFromMenu()
        {
            if (_chat == null || !_chat.Created || _chat.IsOpen) return;

            _chat.SetOpen(true);
            OnWindowOpened();
            Log.LogInfo($"[UI] 已从 Game Menu 打开窗口。{_chat.DescribeVisibility()}");
        }

        private void ShowHint(string hint)
        {
            if (_chat.Created) _chat.AppendSystem(hint);
            else _pendingHint = hint;
        }

        // ---------------------------------------------------------------- 交互

        /// <summary>
        /// 蓝图卡片动作：查看详情 / 进入粘贴模式。
        ///
        /// 成功时关闭聊天窗——蓝图浏览器与粘贴预览都在游戏层，
        /// 我们的窗口（sortingOrder 30000）留着会挡住它们。
        /// 失败时把原因写回聊天窗，让玩家知道是"没解锁/文件没了/当前状态不允许"。
        /// </summary>
        private void OnBlueprintCardAction(BlueprintCardInfo card, BlueprintCardAction action)
        {
            if (card == null) return;

            bool ok;
            string error;
            try
            {
                if (action == BlueprintCardAction.Inspect)
                    ok = BlueprintActions.TryInspect(card, out error);
                else
                    ok = BlueprintActions.TryPaste(card, out error);
            }
            catch (Exception ex)
            {
                // 游戏对象在切场景/读档期间可能失效；不要让异常穿透到 UI 事件。
                Log.LogWarning($"[蓝图卡] {action} 异常：{ex.Message}");
                ok = false;
                error = Strings.Get("blueprint.card.not_in_game");
            }

            if (!ok)
            {
                Log.LogInfo($"[蓝图卡] {action} 失败：{error}");
                _chat.AppendSystem(error ?? Strings.Get("blueprint.card.open_failed"));
                return;
            }

            _chat.SetOpen(false);
        }

        /// <summary>A.14.1：设定页关闭工具后，聊天窗的「/」工具列表也要同步。</summary>
        public void ApplyToolCatalogFromSettings(JsonValue payload)
        {
            ApplyToolCatalog(payload);
        }

        /// <summary>
        /// ready.payload.toolCatalog（M4l）→ 聊天窗工具列表。
        /// 老 sidecar 不带 toolCatalog 时回退到 tools 名 + 本地短名。
        /// </summary>
        private void ApplyToolCatalog(JsonValue payload)
        {
            if (_chat == null) return;

            var items = new List<ToolMenuItem>();
            var catalog = payload["toolCatalog"];
            if (catalog.IsArray)
            {
                foreach (var entry in catalog.Items)
                {
                    var name = entry["name"].AsString("");
                    if (string.IsNullOrEmpty(name)) continue;
                    // A.14.1：被设定页关掉的工具不再出现在聊天窗「/」列表，
                    // 否则玩家能强制调用一个没有注册给模型的工具。
                    if (!entry["enabled"].AsBool(true)) continue;
                    items.Add(new ToolMenuItem
                    {
                        Name = name,
                        Label = entry["label"].AsString(ToolNames.Label(name)),
                        Description = entry["description"].AsString(""),
                    });
                }
            }

            if (items.Count == 0)
            {
                var tools = payload["tools"];
                if (tools.IsArray)
                {
                    foreach (var entry in tools.Items)
                    {
                        var name = entry.AsString("");
                        if (string.IsNullOrEmpty(name)) continue;
                        items.Add(new ToolMenuItem
                        {
                            Name = name,
                            Label = ToolNames.Label(name),
                            Description = "",
                        });
                    }
                }
            }

            _chat.SetToolCatalog(items);
            Log.LogInfo($"[工具] 工具列表已更新：{items.Count} 个（{(catalog.IsArray ? "toolCatalog" : "tools 兜底")}）");
        }

        // ---------------------------------------------------------------- 调试直调（M4m）

        private sealed class DebugRequest
        {
            public string Name;
            public string Args;
        }

        /// <summary>
        /// 输入框调试命令：
        ///   !tools                 列出工具目录
        ///   !tool &lt;name&gt; &lt;json&gt;   直接执行一个工具（含写工具）
        ///
        /// 命中后完全不经过 prompt/AI，结果只作为系统调试块显示，不写会话历史。
        /// </summary>
        private bool TryHandleDebugCommand(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (!trimmed.StartsWith("!", StringComparison.Ordinal)) return false;

            if (string.Equals(trimmed, "!tools", StringComparison.OrdinalIgnoreCase))
            {
                ShowDebugTools();
                return true;
            }

            if (!trimmed.StartsWith("!tool", StringComparison.OrdinalIgnoreCase)) return false;
            if (trimmed.Length > 5 && !char.IsWhiteSpace(trimmed[5])) return false;

            var rest = trimmed.Length > 5 ? trimmed.Substring(5).Trim() : string.Empty;
            if (rest.Length == 0)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.usage"));
                return true;
            }

            string name;
            string argsText;
            var split = rest.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            if (split < 0)
            {
                name = rest;
                argsText = "{}";
            }
            else
            {
                name = rest.Substring(0, split).Trim();
                argsText = rest.Substring(split + 1).Trim();
                if (argsText.Length == 0) argsText = "{}";
            }
            if (name.StartsWith("/", StringComparison.Ordinal)) name = name.Substring(1);
            if (string.IsNullOrEmpty(name) || name.Length > 128)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.name_required"));
                return true;
            }

            JsonValue args;
            if (!JsonValue.TryParse(argsText, out args) || args == null || !args.IsObject)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.args_invalid", argsText));
                return true;
            }

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.offline"));
                return true;
            }
            if (_promptById.Count > 0 || _streaming)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.busy_stream"));
                return true;
            }
            if (_debugById.Count > 0)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.busy_request"));
                return true;
            }

            var id = _client.SendToolDebug(name, args);
            if (id == null)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.send_failed"));
                return true;
            }

            _debugById[id] = new DebugRequest { Name = name, Args = args.ToJson() };
            _chat.AppendDebugResult(Strings.Get("plugin.debug.sent", name, args.ToJson()));
            _chat.SetStatus(Strings.Get("plugin.status.debugging"), AccentColor);
            return true;
        }

        private void ShowDebugTools()
        {
            var items = _chat != null ? _chat.GetToolCatalog() : new List<ToolMenuItem>();
            var sb = new StringBuilder();
            sb.AppendLine(Strings.Get("plugin.debug.catalog", items.Count));
            if (items.Count == 0)
            {
                sb.AppendLine(Strings.Get("plugin.debug.catalog_pending"));
            }
            else
            {
                foreach (var item in items)
                {
                    sb.Append(item.Name).Append(" — ").Append(item.Label);
                    sb.AppendLine();
                }
            }
            _chat.AppendDebugResult(sb.ToString().TrimEnd());
        }

        private void OnToolDebugResult(JsonValue payload, string replyTo)
        {
            var info = ToolDebugResultInfo.Parse(payload);
            if (info == null)
            {
                _chat.AppendDebugResult(Strings.Get("plugin.debug.bad_result"));
                return;
            }

            DebugRequest request = null;
            if (!string.IsNullOrEmpty(replyTo)) _debugById.TryGetValue(replyTo, out request);
            if (!string.IsNullOrEmpty(replyTo)) _debugById.Remove(replyTo);

            var tool = string.IsNullOrEmpty(info.Tool)
                ? (request != null ? request.Name : "?")
                : info.Tool;
            var sb = new StringBuilder();
            sb.Append(Strings.Get("plugin.debug.head", tool, info.DurationMs,
                info.Ok ? Strings.Get("plugin.debug.ok") : Strings.Get("plugin.debug.fail")));
            if (!string.IsNullOrEmpty(info.Source) && info.Source != "unknown")
                sb.Append(" · ").Append(info.Source == "game" ? Strings.Get("plugin.debug.game") : "sidecar");
            sb.AppendLine();

            if (request != null && !string.IsNullOrEmpty(request.Args))
                sb.Append(Strings.Get("plugin.debug.args", Clip(request.Args, 2000))).AppendLine();

            if (!string.IsNullOrEmpty(info.ErrorCode) || !string.IsNullOrEmpty(info.ErrorMessage))
            {
                sb.Append(Strings.Get("plugin.debug.error",
                        string.IsNullOrEmpty(info.ErrorCode) ? "unknown" : info.ErrorCode,
                        Clip(info.ErrorMessage ?? string.Empty, 2000)))
                    .AppendLine();
            }

            sb.AppendLine(Strings.Get("plugin.debug.result"));
            var detailsText = info.Details != null && (info.Details.IsObject || info.Details.IsArray)
                ? PrettyJson(info.Details, 0)
                : info.DetailsText();
            sb.AppendLine(Clip(detailsText, 12000));

            if (!string.IsNullOrEmpty(info.Content))
            {
                sb.AppendLine(Strings.Get("plugin.debug.visible"));
                sb.AppendLine(Clip(info.Content, 4000));
            }

            if (info.ImageBytes > 0)
            {
                sb.Append(Strings.Get("plugin.debug.image", info.ImageMimeType ?? "image",
                    info.ImageWidth, info.ImageHeight, info.ImageBytes)).AppendLine();
            }

            if (info.Truncated) sb.AppendLine(Strings.Get("plugin.debug.truncated"));

            _chat.AppendDebugResult(sb.ToString().TrimEnd());
            _chat.SetStatus(info.Ok ? Strings.Get("status.ready") : Strings.Get("plugin.status.debug_failed"), info.Ok ? OkColor : WarnColor);
        }

        private static string Clip(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + Strings.Get("plugin.debug.clip");
        }

        /// <summary>
        /// 调试结果里的原始 JSON 缩进展示。只处理 JsonValue 已解析的对象/数组；
        /// 字符串（例如 sidecar 截断后的 details）原样返回。
        /// </summary>
        private static string PrettyJson(JsonValue value, int indent)
        {
            if (value == null || value.IsNull) return "null";

            if (value.IsObject)
            {
                if (value.Count == 0) return "{}";
                var pad = new string(' ', indent);
                var childPad = new string(' ', indent + 2);
                var sb = new StringBuilder();
                sb.Append("{\n");
                var first = true;
                foreach (var pair in value.Members)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append(childPad)
                        .Append(JsonValue.Of(pair.Key).ToJson())
                        .Append(": ")
                        .Append(PrettyJson(pair.Value, indent + 2));
                }
                sb.Append('\n').Append(pad).Append('}');
                return sb.ToString();
            }

            if (value.IsArray)
            {
                if (value.Count == 0) return "[]";
                var pad = new string(' ', indent);
                var childPad = new string(' ', indent + 2);
                var sb = new StringBuilder();
                sb.Append("[\n");
                var first = true;
                foreach (var item in value.Items)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append(childPad).Append(PrettyJson(item, indent + 2));
                }
                sb.Append('\n').Append(pad).Append(']');
                return sb.ToString();
            }

            return value.ToJson();
        }

        private void CancelPendingDebug()
        {
            if (_debugById.Count == 0) return;
            _debugById.Clear();
            _chat?.AppendDebugResult(Strings.Get("plugin.debug.cancelled"));
        }

        private void OnUserSubmitted(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // M4m：调试命令在 AppendUser 之前拦截，不进入 AI/会话历史。
            if (_config != null && _config.DebugToolConsole && TryHandleDebugCommand(text)) return;

            // 没有可用 Key 时不把提问送进会话：旧版这里会把输入当成 Key 吞掉，
            // 现在统一引导到右上角设置按钮，避免 sidecar 再回一条 config_required。
            if (_lastConfig != null && _lastConfig.NeedsKey)
            {
                _chat.AppendSystem(_lastConfig.State == P.ConfigInvalidKey
                    ? Strings.Get("config.invalid_key")
                    : Strings.Get("config.missing_key"));
                _chat.SetStatus(Strings.Get("status.needkey"), null);
                return;
            }

            // A.15：正在回答时，这句话是插话。先把流式块收尾，
            // 否则后续增量会续写进上一个块；然后按 mode=steer 投递进当前回合。
            bool steering = _agentRunning;
            if (steering && _streaming)
            {
                _chat.EndStream();
                _streaming = false;
            }

            _chat.AppendUser(text);   // 用户消息走气泡样式

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _chat.AppendSystem(Strings.Get("plugin.send.offline"));
                return;
            }

            if (!steering)
            {
                _streaming = false;
                _agentRunning = true;
                _chat.SetBusy(true);
            }
            RefreshModExtensions();
            var requiredTool = _chat.GetRequiredTool(text);
            var id = _client.SendPrompt(text, _chat.ActiveConversationId, requiredTool,
                steering ? Protocol.PromptModeSteer : null);
            if (id == null)
            {
                _chat.AppendSystem(Strings.Get("plugin.send.changed"));
                return;
            }
            if (steering)
            {
                // 插话没有自己的终稿：正文并入当前回合，不能登记成待回包提问。
                return;
            }
            _promptById[id] = text;
            _chat.SetStatus(Strings.Get("status.thinking"), AccentColor);
        }

        /// <summary>A.15：玩家点了「中断」。sidecar 收尾后回 interrupted 终稿。</summary>
        private void OnBreakRequested()
        {
            if (_chat == null) return;
            if (_client == null || _client.State != ConnectionState.Ready) return;
            if (!_agentRunning) return;
            if (!_client.SendAbort(_chat.ActiveConversationId))
                _chat.AppendSystem(Strings.Get("plugin.send.offline"));
        }

        // ---------------------------------------------------------------- 杂项

        private void Post(Action action) => _dispatcher.Post(action);

        private string ResolvePluginDir()
        {
            try
            {
                var location = Info?.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    var dir = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { /* 落到下面的默认值 */ }
            return Paths.PluginPath;
        }

        private static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "?";
            return value.Length <= 8 ? value : value.Substring(0, 8) + "…";
        }
    }
}
