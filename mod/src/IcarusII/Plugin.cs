using System;
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

        private string _pluginDir;
        private bool _uiInitialized;
        private bool _streaming;
        private string _pendingHint;
        private volatile bool _sidecarStartAttempted;
        private bool _uiCreateFailureLogged;
        private bool _styleApplied;
        private bool _initFailed;
        private ConfigState _lastConfig;

        /// <summary>最近一个已渲染终稿的 promptId，用于对重复的 message{final} 去重。</summary>
        private string _lastFinalReplyTo;
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
                _hotkey = new HotkeyService(_config);
                _chat = new ChatWindow();
                _chat.Submitted += OnUserSubmitted;
                _chat.KeySubmitted += OnKeySubmitted;
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
            if (_initFailed) return;

            _dispatcher.Drain(MainThreadBudgetMs, ex => Log.LogError($"主线程任务异常：{ex}"));

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
                        break;
                    case ConnectionState.Backoff:
                        _chat.SetStatus(Strings.Get("status.backoff") + " " + detail, WarnColor);
                        break;
                    default:
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
                        if (!_streaming)
                        {
                            _streaming = true;
                            _chat.BeginStream(Strings.Get("agent"));
                        }
                        _chat.SetStatus(Strings.Get("status.tools"), AccentColor);
                    }
                    else if (state == Protocol.StateIdle)
                    {
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

                case Protocol.Stream:
                {
                    var streamChannel = payload["channel"].AsString("text");

                    // reasoning：模型的思考增量。不进正文（正文只该包含答案），
                    // 但必须让玩家看到"它在想"——之前这里是静默 break，
                    // 开了思考档位就会表现为"卡住没有任何反应"。
                    if (streamChannel == "reasoning")
                    {
                        _chat.SetStatus(Strings.Get("status.thinking"), AccentColor);
                        break;
                    }

                    // 未知 channel 忽略（前向兼容）
                    if (streamChannel != "text") break;
                    if (!_streaming)
                    {
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

                case Protocol.Error:
                {
                    var code = payload["code"].AsString("unknown");
                    var message = payload["message"].AsString("");
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

        // ---------------------------------------------------------------- UI

        private void EnsureUi()
        {
            if (_uiInitialized) return;

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

            _chat.AppendLine(Strings.Get("you"), text);

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
