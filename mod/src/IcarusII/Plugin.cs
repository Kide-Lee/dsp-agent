using System;
using System.Collections.Generic;
using System.IO;
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
    /// 刻意**不注册任何 Harmony 补丁**：所有接入点都是游戏暴露的公开类型/成员
    /// （UIRoot / GameMain / VFInput / PlayerAction_Build），因此游戏更新时
    /// 最坏情况是编译失败或功能降级，而不是崩溃（design.md §6.1）。
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    [BepInProcess("DSPGAME.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "icarus-ii";
        public const string PluginName = "Icarus II";
        public const string PluginVersion = "0.1.0";

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
        private string _pendingHint;
        private volatile bool _sidecarStartAttempted;
        private bool _uiCreateFailureLogged;
        private bool _styleApplied;
        private bool _initFailed;
        private ConfigState _lastConfig;

        /// <summary>思考档位在本地改过、但还没成功发给 sidecar（下次 ready 时补发）。</summary>
        private bool _thinkingLevelDirty;

        /// <summary>最近一个已渲染终稿的 promptId，用于对重复的 message{final} 去重。</summary>
        private string _lastFinalReplyTo;

        /// <summary>当前存档标识（null = 主菜单）。</summary>
        private string _saveKey;

        /// <summary>sidecar 内部工具调用（知识库导出）：不显示工具卡，只回结果。</summary>
        private readonly HashSet<string> _internalCalls = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>当前存档标识是否已经同步给服务端。</summary>
        private bool _saveSynced;

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
                _dispatcher = new MainThreadDispatcher();
                _tools = new GameToolDispatcher(Log);
                _hotkey = new HotkeyService(_config);
                _chat = new ChatWindow();
                _chat.Submitted += OnUserSubmitted;
                _chat.KeySubmitted += OnKeySubmitted;
                _chat.ThinkingLevelChanged += OnThinkingLevelChanged;
                _chat.ModelSelectionChanged += OnModelSelectionChanged;
                _chat.TurnDeleted += OnTurnDeleted;
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
            Log.LogInfo($"热键：{_config.Hotkey}（建造模式让位 = {_config.HotkeyRequireNoBuildTool}）");
            Log.LogInfo($"sidecar：{_config.LaunchMode} @ {_config.SidecarHost}:{_config.SidecarPort}");
            Log.LogInfo($"数据目录：{_launcher.DataDir}");

            // 后台准备好 sidecar，避免首次按 R 时还要等进程启动。
            // 全部失败路径都通过 dispatcher 回到主线程处理。
            Task.Run(PrepareSidecar);
        }

        private void Update()
        {
            // 设置窗口打开时自动补「模型」页签（不依赖从哪个入口进入设置）
            UI.ModelSettingsTab.Watch();

            if (_initFailed) return;

            _dispatcher.Drain(MainThreadBudgetMs, ex => Log.LogError($"主线程任务异常：{ex}"));

            // 先同步存档绑定：换档后旧工具调用必须在同帧被取消，不能带着新档的数据返回。
            SyncSaveBinding();

            // 工具采集必须在 UI 就绪之前也持续运行：模型可能在窗口关闭时仍在用工具。
            _tools.Tick(2, OnToolDone);

            // UIRoot 会在场景切换时重建；检测到旧对象被销毁就重置，等待重建
            if (_uiInitialized && !_chat.Alive)
            {
                _uiInitialized = false;
                _streaming = false;
                _chat.Reset();
                Log.LogInfo("UI 随场景销毁，将在新场景中重建。");
            }

            EnsureUi();
            if (!_uiInitialized) return;

            // 回到开始菜单/载入中：强制关窗，R 也不应把窗口留在主菜单上。
            if (!GameRefs.IsInGame)
            {
                if (_chat.IsOpen) _chat.SetOpen(false);
                return;
            }

            _chat.Tick();
            TryApplyGameStyle();

            if (_config.DebugLogGate && Time.frameCount % 60 == 0)
            {
                Log.LogInfo($"[诊断] 热键判定：{GameRefs.DescribeHotkeyGate()}");
                Log.LogInfo($"[诊断] 窗口：{_chat.DescribeVisibility()}");
                Log.LogInfo($"[诊断] 焦点：{_chat.DescribeFocus()}");
            }

            // Esc：输入框聚焦时先取消焦点（把键盘交还窗口），否则关闭窗口
            if (_chat.IsOpen && _hotkey.EscapePressedThisFrame)
            {
                if (_chat.InputFocused) _chat.BlurInput();
                else _chat.SetOpen(false);
                return;
            }

            // R：开关窗口（输入框聚焦时让位给文字输入）
            if (_hotkey.ShouldToggle(_chat.IsOpen, _chat.InputFocused, out var blockedBy))
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
                client.Log += msg => Log.LogInfo($"[client] {msg}");
                client.ConfigChanged += OnConfigChanged;
                _client = client;
                IcarusII.UI.ModelSettingsTab.Bind(client);   // 设置窗口的"模型"页签需要读写配置
                client.Start();
            }
            catch (Exception ex)
            {
                Log.LogError($"准备 sidecar 时异常：{ex}");
                Post(() => ShowHint($"准备 sidecar 失败：{ex.Message}"));
            }
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
                        break;
                    case ConnectionState.Backoff:
                        _tools.CancelAll("连接退避");
                        _chat.SetStatus(Strings.Get("status.backoff") + " " + detail, WarnColor);
                        break;
                    default:
                        _tools.CancelAll("连接断开");
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

        private void HandleMessage(string type, JsonValue payload, string replyTo)
        {
            switch (type)
            {
                case Protocol.Ready:
                {
                    var model = payload["agent"]["model"].AsString("?");
                    var session = payload["sessionId"].AsString("?");
                    _chat.AppendSystem($"已连接 sidecar（会话 {Short(session)}，模型 {model}）");
                    break;
                }

                case Protocol.Status:
                {
                    var state = payload["state"].AsString("");
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
                        _chat.EndThinking();
                        if (_streaming)
                        {
                            _chat.EndStream();
                            _streaming = false;
                        }
                        _chat.SetStatus(Strings.Get("status.ready"), OkColor);
                    }
                    else if (state == Protocol.StateRetrying)
                    {
                        _chat.SetStatus("重试中…", WarnColor);
                    }
                    break;
                }

                case Protocol.SaveBound:
                    OnSaveBound(SaveBoundInfo.Parse(payload));
                    break;

                case Protocol.ToolActivity:
                {
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

                case Protocol.HistoryDeleted:
                {
                    var info = HistoryDeletedInfo.Parse(payload);
                    if (info != null && !info.Ok)
                    {
                        var reason = string.IsNullOrEmpty(info.Error) ? "未知错误" : info.Error;
                        _chat.AppendSystem("删除未写入会话（" + reason + "）；重进存档可能仍会出现这条对话。");
                    }
                    break;
                }

                case Protocol.Stream:
                {
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

                    // 协议规定一轮 prompt 只有一个 final 终稿。若服务端因缺陷发了
                    // 第二个，这里必须忽略——否则会走下面的"非流式追加"分支，
                    // 把同一段回答在窗口里再写一遍（实测出现过）。
                    if (!string.IsNullOrEmpty(replyTo))
                    {
                        if (replyTo == _lastFinalReplyTo) break;
                        _lastFinalReplyTo = replyTo;
                    }

                    _chat.EndThinking();
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
                    break;
                }

                case Protocol.ToolCall:
                    OnToolCall(payload, replyTo);
                    break;

                case Protocol.Error:
                {
                    var code = payload["code"].AsString("unknown");
                    var message = payload["message"].AsString("");
                    _chat.EndThinking();
                    _chat.AppendSystem($"⚠ 错误 [{code}] {message}");
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

            if (call.Internal)
            {
                _internalCalls.Add(call.CallId);
            }
            else
            {
                _chat?.SetStatus("正在读取" + ToolLabel(call.Name) + "…", AccentColor);
                if (_chat != null && _chat.Created)
                    _chat.BeginToolCard(call.CallId, ToolLabel(call.Name), ToolArgsText(call.Args));
            }

            if (_tools == null)
            {
                _client?.SendToolResult(call.CallId, false, JsonValue.Null,
                    Protocol.CodeToolUnavailable, "工具执行器尚未初始化", null);
                if (!call.Internal && _chat != null && _chat.Created)
                    _chat.FinishToolCard(call.CallId, ToolLabel(call.Name), false, "工具执行器尚未初始化");
                return;
            }

            var immediate = _tools.Begin(call.CallId, call.Name, call.Args, call.TimeoutMs);
            if (immediate != null) OnToolDone(immediate);
        }

        private void OnToolDone(GameToolOutcome outcome)
        {
            if (outcome == null) return;
            SendToolOutcome(outcome);

            // 内部调用（知识库导出）只回结果，不碰聊天窗。
            if (_internalCalls.Remove(outcome.CallId)) return;

            var label = ToolLabel(outcome.Name);
            if (outcome.Ok)
            {
                var summary = string.IsNullOrEmpty(outcome.Summary) ? "完成" : outcome.Summary;
                if (_chat != null && _chat.Created)
                {
                    _chat.FinishToolCard(outcome.CallId, label, true, summary, outcome.Summary);
                    // 截图是本机产生的，不需要绕协议回来：直接把字节贴到工具卡上。
                    AttachToolImage(outcome);
                }
                _chat?.SetStatus(Strings.Get("status.ready"), OkColor);
            }
            else
            {
                var message = outcome.ErrorMessage ?? outcome.ErrorCode ?? "未知错误";
                if (_chat != null && _chat.Created)
                    _chat.FinishToolCard(outcome.CallId, label, false, message, message);
                _chat?.SetStatus(Strings.Get("status.ready"), WarnColor);
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

        private static string ToolLabel(string name)
        {
            switch (name)
            {
                case "game_get_overview": return "存档概览";
                case "game_get_player": return "玩家状态";
                case "game_get_planet": return "行星状态";
                case "game_get_power": return "电力状态";
                case "game_get_research": return "研究状态";
                case "game_get_production": return "实际产出";
                case "game_get_entity": return "建筑详情";
                case "game_get_entities": return "建筑列表";
                case "game_get_progress": return "进度阶段";
                case "game_capture_view": return "截图";
                case "game_analyze_area": return "产线分析";
                case "game_render_layout": return "布局图";
                case "game_copy_blueprint": return "存为蓝图";
                case "game_get_planets": return "星球一览";
                case "game_get_logistics": return "星际物流";
                // sidecar 本地工具（tool_activity 卡片用）
                case "kb_search": return "知识库检索";
                case "kb_get": return "知识库查询";
                case "kb_recipe_tree": return "配方树";
                case "kb_plan": return "量化计算";
                case "run_script": return "脚本";
                case "game_export_prototypes": return "建立知识库";
                default: return string.IsNullOrEmpty(name) ? "工具" : name;
            }
        }

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

            _chat.Create(root, font, _config);
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

        /// <summary>用户提交了 API Key：上报给 sidecar，然后等 config.state 回来。</summary>
        private void OnKeySubmitted(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) return;

            // 只记录长度，绝不记录内容
            Log.LogInfo($"[配置] 收到 API Key（长度 {apiKey.Length}），上报给 sidecar…");

            if (_client == null)
            {
                _chat.AppendSystem("连接未就绪，请稍后重试。");
                return;
            }

            var id = _client.SendConfigSet(apiKey: apiKey);
            if (id == null)
            {
                _chat.AppendSystem("连接未就绪，请稍后重试。");
                return;
            }

            _chat.SetStatus("校验中…", null);
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
            var id = _client.SendHistoryDelete(saveKey, text, timestampMs);
            if (id == null)
            {
                _chat.AppendSystem("删除未同步到会话（连接未就绪）；重进存档可能仍会出现这条对话。");
                return;
            }
            Log.LogInfo($"[索引] 已请求 sidecar 删除回合：{id}");
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
        /// 玩家在聊天窗里换了模型。模型列表由 sidecar 的 config.json 持久化，
        /// 这里直接发 `config.set{activeProvider,activeModel}`；下一轮 prompt 生效。
        /// </summary>
        private void OnModelSelectionChanged(string provider, string model)
        {
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return;

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _chat?.SetStatus("sidecar 未连接，模型选择未生效", WarnColor);
                Log.LogInfo($"[配置] 模型 → {provider}/{model}（连接未就绪，未发送）");
                return;
            }

            var id = _client.SendConfigSet(activeProvider: provider, activeModel: model);
            if (id == null)
            {
                _chat?.SetStatus("模型选择发送失败", WarnColor);
                Log.LogWarning($"[配置] 模型 → {provider}/{model}（config.set 发送失败）");
                return;
            }

            _chat?.SetStatus(Strings.Get("model.switched", provider + "/" + model), OkColor);
            Log.LogInfo($"[配置] 模型 → {provider}/{model}（config.set 已发送）");
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
            // 应用一次，EnterKeyEntry 会因为 _input 还是 null 而静默返回，
            // 首次引导就永远不会出现（实测踩到）。因此必须缓存，
            // 并在 UI 建好后由 ApplyConfigState 再应用一次。
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

            // 模型列表/当前模型：sidecar 的 config.state 是权威来源。
            _chat.ApplyModels(config);

            switch (config.State)
            {
                case P.ConfigMissingKey:
                    _chat.EnterKeyEntry(null);
                    break;

                case P.ConfigInvalidKey:
                    _chat.EnterKeyEntry(Strings.Get("key.invalid"));
                    break;

                case P.ConfigUnreachable:
                    _chat.AppendSystem(Strings.Get("key.unreachable"));
                    _chat.EnterKeyEntry(null);
                    break;

                case P.ConfigReady:
                    _chat.ExitKeyEntry();
                    _chat.SetStatus(Strings.Get("status.ready"), null);
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
            if (key != _saveKey)
            {
                _tools?.CancelAll("存档切换", OnToolDone);
                _tools?.ClearCache();
                _saveKey = key;
                _saveSynced = false;
                _historyReplayedFor = null;
                _internalCalls.Clear();
                // 对话串也必须跟着换档：否则新存档里还挂着旧档的消息，
                // 即使 sidecar 已经换了会话，看起来仍然不是“新对话”。
                if (_chat != null && _chat.Created) _chat.ClearTranscript();
                Log.LogInfo($"[存档] 当前存档变化：{key ?? "(无，主菜单)"}");
            }

            if (_saveSynced) return;
            if (_client.State != ConnectionState.Ready) return;

            var name = key != null ? GameRefs.CurrentSaveName() : null;
            if (_client.SendSaveBind(key, name) == null) return;

            _saveSynced = true;
            Log.LogInfo($"[存档] 已上报绑定：{key ?? "(解绑)"} / {name ?? "-"}");
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
                    _chat.AppendSystem(Strings.Get("save.unbound"));
                    return;
                }

                var name = string.IsNullOrEmpty(info.SaveName) ? info.SaveKey : info.SaveName;

                // 进入存档后把该存档上一次的对话贴回聊天窗。
                // 同一个存档重连时只贴一次；正在流式输出时不打断当前回合。
                var replay = info.History != null && info.History.Count > 0
                             && info.SaveKey != _historyReplayedFor && !_streaming;
                if (replay)
                {
                    _chat.ClearTranscript();
                    _historyReplayedFor = info.SaveKey;
                }

                if (!info.Existed) _chat.AppendSystem(Strings.Get("save.new", name));
                else if (info.MessageCount > 0)
                    _chat.AppendSystem(Strings.Get("save.bound", name, info.MessageCount));
                else
                    _chat.AppendSystem(Strings.Get("save.bound.nocount", name));

                if (replay)
                {
                    foreach (var message in info.History)
                    {
                        if (message == null || string.IsNullOrEmpty(message.Text)) continue;
                        if (message.Role == "user") _chat.AppendUser(message.Text, message.Timestamp);
                        else _chat.AppendLine(Strings.Get("agent"), message.Text);
                    }
                }

                _chat.SetStatus(Strings.Get("status.ready"), OkColor);
            });
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

        private void ShowHint(string hint)
        {
            if (_chat.Created) _chat.AppendSystem(hint);
            else _pendingHint = hint;
        }

        // ---------------------------------------------------------------- 交互

        private void OnUserSubmitted(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            _chat.AppendUser(text);   // 用户消息走气泡样式

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                _chat.AppendSystem("尚未连接到 sidecar，消息未发送。");
                return;
            }

            _streaming = false;
            var id = _client.SendPrompt(text);
            if (id == null)
            {
                _chat.AppendSystem("发送失败（连接状态已变化）。");
                return;
            }
            _chat.SetStatus(Strings.Get("status.thinking"), AccentColor);
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
