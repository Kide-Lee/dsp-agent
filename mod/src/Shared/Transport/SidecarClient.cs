using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

namespace IcarusII.Common.Transport
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Handshaking,
        Ready,
        Backoff,
    }

    public sealed class SidecarOptions
    {
        public string Host = "127.0.0.1";
        public int Port = Protocol.Protocol.DefaultPort;
        /// <summary>来自 sidecar 的 runtime.json；用于握手鉴权。</summary>
        public string Token = string.Empty;

        public string ModVersion = "0.0.0";
        public string GameVersion = "unknown";
        public string GameLanguage = "unknown";
        public string[] Capabilities = new string[0];

        /// <summary>第三方 mod 桥的插件摘要；空数组时不写 hello.extensions。</summary>
        public Protocol.ExtensionSummary[] Extensions = new Protocol.ExtensionSummary[0];

        /// <summary>
        /// 连接时已加载的存档标识（协议 §4.1 的 saveKey）。
        /// 实际几乎总是 null——插件在加载时就连接，那时还在主菜单；
        /// 真正的绑定靠随后的 save.bind（附录 A.6）。
        /// </summary>
        public string SaveKey;

        public int ConnectTimeoutMs = 3000;
        public int HandshakeTimeoutMs = 5000;
        public int MinBackoffMs = 1000;
        public int MaxBackoffMs = 30000;
        public int PingIntervalMs = 15000;
        /// <summary>连续多少个 ping 周期没收到 pong 就判定链路已死。</summary>
        public int PingMissLimit = 2;

        public string Uri
        {
            get
            {
                var host = Host is not null && Host.Contains(":") && !Host.StartsWith("[")
                    ? $"[{Host}]" : Host;
                return $"ws://{host}:{Port}/";
            }
        }

        public SidecarOptions Clone() => (SidecarOptions)MemberwiseClone();
    }

    /// <summary>
    /// 极小的 WebSocket 客户端：连接、握手、收帧、发帧、心跳、指数退避重连。
    ///
    /// 本类**刻意不依赖 UnityEngine**：
    ///  - 所有事件都在后台线程触发，调用方（Unity 层）负责 marshal 回主线程；
    ///  - 因此同一份源码可以在普通 .NET 进程里编译运行，用于端到端测试。
    ///    （见 tests/IcarusII.ProtocolTests）
    /// </summary>
    public sealed class SidecarClient : IDisposable
    {
        private const int ReceiveBufferBytes = 16 * 1024;
        private const int MaxFrameBytes = 8 * 1024 * 1024;

        private readonly SidecarOptions _options;
        private readonly ConcurrentQueue<JsonValue> _sendQueue = new ConcurrentQueue<JsonValue>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();

        private CancellationTokenSource _cts;
        private Task _runner;
        private ClientWebSocket _ws;

        private long _messageCounter;
        private long _lastPongMs;
        private volatile ConnectionState _state = ConnectionState.Disconnected;
        private volatile string _sessionId;
        private volatile Protocol.ConfigState _config = new Protocol.ConfigState();

        private TaskCompletionSource<bool> _readySignal;

        /// <summary>状态变化（后台线程触发）。detail 供 UI 显示。</summary>
        public event Action<ConnectionState, string> StateChanged;

        /// <summary>收到应用层消息（后台线程触发）。参数：type, payload, replyTo。</summary>
        public event Action<string, JsonValue, string> Received;

        /// <summary>诊断日志（后台线程触发）。</summary>
        public event Action<string> Log;

        public SidecarClient(SidecarOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public ConnectionState State => _state;
        public string SessionId => _sessionId;

        /// <summary>
        /// 服务端配置状态（协议附录 A）。握手完成时由 ready.payload.config 初始化，
        /// 之后每次 config.set 的结果会更新它。**不含 apiKey**。
        /// </summary>
        public Protocol.ConfigState Config => _config;

        /// <summary>配置状态发生变化（首次握手、或 config.set 的结果）。</summary>
        public event Action<Protocol.ConfigState> ConfigChanged;
        public SidecarOptions Options => _options;

        // ---------------------------------------------------------------- 生命周期

        public void Start()
        {
            if (_runner is not null) return;
            _cts = new CancellationTokenSource();
            _runner = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            var cts = _cts;
            if (cts is null) return;
            try { cts.Cancel(); } catch { /* ignore */ }

            var ws = _ws;
            if (ws is not null)
            {
                // 尽力优雅关闭；失败无所谓，RunAsync 的 finally 会兜底释放。
                try
                {
                    if (ws.State == WebSocketState.Open)
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                          .Wait(500);
                }
                catch { /* ignore */ }
            }

            try { _runner?.Wait(1000); } catch { /* ignore */ }
            _runner = null;
            _cts = null;
            SetState(ConnectionState.Disconnected, "已停止");
        }

        public void Dispose() => Stop();

        // ---------------------------------------------------------------- 发送

        /// <summary>入队一条消息。连接未就绪时返回 false（消息被丢弃）。</summary>
        public bool Send(string type, JsonValue payload, string replyTo = null)
        {
            if (_state != ConnectionState.Ready) return false;
            var env = Protocol.Protocol.Envelope(type, payload, NextId(), replyTo);
            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return true;
        }

        /// <summary>发一条 prompt；返回消息 id，供 UI 关联回复。</summary>
        /// <summary>
        /// 上报配置（provider/model/apiKey/thinkingLevel）。apiKey 只在此方向传输一次；
        /// 调用方必须确保它不被写进日志或聊天记录。
        /// </summary>
        public string SendConfigSet(string provider = null, string model = null,
            string apiKey = null, string baseUrl = null, string thinkingLevel = null,
            JsonValue providerConfig = null, JsonValue removeProviders = null,
            string activeProvider = null, string activeModel = null,
            string permissionLevel = null, bool clearBaseUrl = false)
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConfigSet,
                Protocol.Protocol.BuildConfigSet(provider, model, apiKey, baseUrl, thinkingLevel,
                    providerConfig, removeProviders, activeProvider, activeModel, permissionLevel,
                    clearBaseUrl),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 列出 pi 内置目录的全部供应商。响应是 `provider.catalog`，
        /// 通过 <see cref="Received"/> 事件按 replyTo 关联。
        /// </summary>
        public string SendProviderCatalog()
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ProviderCatalog,
                Protocol.Protocol.BuildProviderCatalogRequest(),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 拉取某供应商的可用模型目录（M3）。apiKey 只用于本次探测；
        /// 响应是 `model.catalog`，通过 <see cref="Received"/> 事件按 replyTo 关联。
        /// </summary>
        public string SendModelCatalog(string provider, string baseUrl = null, string apiKey = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(provider)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ModelCatalog,
                Protocol.Protocol.BuildModelCatalogRequest(provider, baseUrl, apiKey),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 检测某供应商的 Key/端点是否可用（M3）。响应是 `provider.test`，
        /// 通过 <see cref="Received"/> 事件按 replyTo 关联；key 不会回传。
        /// </summary>
        public string SendProviderTest(string provider, string baseUrl = null, string apiKey = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(provider)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ProviderTest,
                Protocol.Protocol.BuildProviderTestRequest(provider, baseUrl, apiKey),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 把对话串绑定到某个存档（协议附录 A.6）。
        /// saveKey 为 null 表示"当前没有存档"，即解绑（不销毁线程）。
        /// </summary>
        public string SendSaveBind(string saveKey, string saveName = null,
            JsonValue blueprintTiers = null, bool? sandbox = null)
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.SaveBind,
                Protocol.Protocol.BuildSaveBind(saveKey, saveName, blueprintTiers, sandbox),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 请求删除一个问答回合（协议附录 A.7）。返回 envelope id；连接未就绪时返回 null。
        /// </summary>
        /// <summary>A.13：新建空会话并设为活动会话。</summary>
        public string SendConversationCreate()
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationCreate,
                Protocol.Protocol.BuildConversationCreate(),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>A.13：切换活动会话。</summary>
        public string SendConversationSwitch(string conversationId)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(conversationId)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationSwitch,
                Protocol.Protocol.BuildConversationTarget(conversationId),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>A.13：玩家重命名会话；saveKey 可选（A.14.4 跨存档）。</summary>
        public string SendConversationRename(string conversationId, string title, string saveKey = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(conversationId)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationRename,
                Protocol.Protocol.BuildConversationRename(conversationId, title, saveKey),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>A.13：置顶/取消置顶会话；saveKey 可选（A.14.4 跨存档）。</summary>
        public string SendConversationPin(string conversationId, bool pinned, string saveKey = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(conversationId)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationPin,
                Protocol.Protocol.BuildConversationPin(conversationId, pinned, saveKey),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>A.13：删除会话；saveKey 可选（A.14.4 跨存档）。</summary>
        public string SendConversationDelete(string conversationId, string saveKey = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(conversationId)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationDelete,
                Protocol.Protocol.BuildConversationTarget(conversationId, saveKey),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// A.14.1：保存工具开关（整份替换）。响应是 `tools.state`，
        /// 通过 <see cref="Received"/> 按 replyTo 关联。
        /// </summary>
        public string SendToolsSet(IList<string> disabled)
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ToolsSet,
                Protocol.Protocol.BuildToolsSet(disabled),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>A.14.2：拉取跨存档会话索引；响应是 `conversations.indexed`。</summary>
        public string SendConversationsIndex()
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationsIndex,
                Protocol.Protocol.BuildConversationsIndex(),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>A.14.3：导出一个会话为 Markdown；响应是 `conversations.exported`。</summary>
        public string SendConversationsExport(string saveKey, string conversationId)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(conversationId)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ConversationsExport,
                Protocol.Protocol.BuildConversationsExport(saveKey, conversationId),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        public string SendHistoryDelete(string saveKey, string text, long timestampMs,
            string conversationId = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(text)) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.HistoryDelete,
                Protocol.Protocol.BuildHistoryDelete(saveKey, text, timestampMs, conversationId),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 撤销最近一次上下文压缩（协议附录 A.11）。返回 envelope id；
        /// 连接未就绪时返回 null。`compactionId` 为空表示撤销最近一次。
        /// </summary>
        public string SendHistoryUndoCompaction(string saveKey, string compactionId,
            string conversationId = null)
        {
            if (_state != ConnectionState.Ready) return null;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.HistoryUndoCompaction,
                Protocol.Protocol.BuildHistoryUndoCompaction(saveKey, compactionId, conversationId),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 回传一次 tool_result（协议附录 B）。`replyTo` 是 sidecar 发来的 callId。
        /// 连接未就绪时返回 false；调用方无需重试（sidecar 侧会超时）。
        /// </summary>
        public bool SendToolResult(string callId, bool ok, JsonValue data,
            string errorCode, string errorMessage, JsonValue meta, JsonValue image = null)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(callId)) return false;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ToolResult,
                Protocol.Protocol.BuildToolResult(callId, ok, data, errorCode, errorMessage, meta, image),
                NextId(), callId);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return true;
        }

        private void UpdateConfig(Protocol.ConfigState next)
        {
            if (next is null) return;
            _config = next;
            try { ConfigChanged?.Invoke(next); } catch (Exception ex) { EmitLog($"配置回调异常：{ex.Message}"); }
        }

        public string SendPrompt(string text, string conversationId = null, string requiredTool = null,
            string mode = null)
        {
            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.Prompt,
                Protocol.Protocol.BuildPrompt(text, conversationId, requiredTool, mode),
                NextId(), null);

            if (_state != ConnectionState.Ready) return null;
            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// A.15：中断当前回合。没有回合在跑时 sidecar 静默忽略；
        /// 连接未就绪时返回 false（不发帧）。
        /// </summary>
        public bool SendAbort(string conversationId = null)
        {
            if (_state != ConnectionState.Ready) return false;
            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.Abort,
                Protocol.Protocol.BuildAbort(conversationId),
                NextId(), null);
            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return true;
        }

        /// <summary>
        /// 调试直调（M4m）：把一个工具名 + 参数直接发给 sidecar，不经过 AI。
        /// 返回请求 id（用于匹配 tool_debug_result）；连接未就绪时返回 null。
        /// </summary>
        public string SendToolDebug(string name, JsonValue args)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ToolDebug,
                Protocol.Protocol.BuildToolDebug(name, args),
                NextId(), null);

            if (_state != ConnectionState.Ready) return null;
            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return env["id"].AsString();
        }

        /// <summary>
        /// 回存回答完成时的游戏时间（协议附录 A.10）。不要求 sidecar 回复；
        /// 连接未就绪时返回 false，历史脚注退化为当前时间。
        /// </summary>
        public bool SendReplyTime(string prompt, string reply, string gameTime,
            string conversationId = null, long gameTick = 0)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(gameTime))
                return false;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ReplyTime,
                Protocol.Protocol.BuildReplyTime(prompt, reply, gameTime, conversationId, gameTick),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return true;
        }

        /// <summary>
        /// 回存压缩记录的游戏时间（协议附录 A.11）。不要求 sidecar 回复；
        /// 连接未就绪时返回 false，回放时该记录就不显示时间。
        /// </summary>
        public bool SendContextCompactedTime(string compactionId, string conversationId,
            string gameTime, long gameTick = 0, long timestamp = 0)
        {
            if (_state != ConnectionState.Ready || string.IsNullOrEmpty(gameTime)) return false;

            var env = Protocol.Protocol.Envelope(
                Protocol.Protocol.ContextCompactedTime,
                Protocol.Protocol.BuildContextCompactedTime(
                    compactionId, conversationId, gameTime, gameTick, timestamp),
                NextId(), null);

            _sendQueue.Enqueue(env);
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ }
            return true;
        }

        private string NextId() => "m-" + Interlocked.Increment(ref _messageCounter);

        // ---------------------------------------------------------------- 重连主循环

        private async Task RunAsync(CancellationToken ct)
        {
            int backoff = _options.MinBackoffMs;

            while (!ct.IsCancellationRequested)
            {
                var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    SetState(ConnectionState.Connecting, _options.Uri);
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token))
                    {
                        timeoutCts.CancelAfter(_options.ConnectTimeoutMs);
                        await ws.ConnectAsync(new Uri(_options.Uri), timeoutCts.Token).ConfigureAwait(false);
                    }

                    _ws = ws;
                    _readySignal = new TaskCompletionSource<bool>();
                    _lastPongMs = Protocol.Protocol.NowMs();

                    SetState(ConnectionState.Handshaking, "发送 hello");
                    var hello = Protocol.Protocol.Envelope(
                        Protocol.Protocol.Hello,
                        Protocol.Protocol.BuildHello(
                            _options.Token, _options.ModVersion, _options.GameVersion,
                            _options.GameLanguage, _options.Capabilities, _options.SaveKey,
                            _options.Extensions),
                        NextId(), null);
                    await SendRawAsync(hello, connCts.Token).ConfigureAwait(false);

                    var receiveTask = Task.Run(() => ReceiveLoopAsync(ws, connCts.Token), connCts.Token);
                    var pumpTask = Task.Run(() => SendPumpAsync(ws, connCts.Token), connCts.Token);
                    var pingTask = Task.Run(() => PingLoopAsync(ws, connCts.Token), connCts.Token);

                    // 等握手结果
                    var readyTask = _readySignal.Task;
                    var handshakeTimeout = Task.Delay(_options.HandshakeTimeoutMs, connCts.Token);
                    var done = await Task.WhenAny(readyTask, handshakeTimeout, receiveTask).ConfigureAwait(false);

                    if (done == handshakeTimeout || done == receiveTask)
                        throw new TimeoutException("握手超时或被对端关闭");

                    await readyTask.ConfigureAwait(false);

                    backoff = _options.MinBackoffMs; // 连上了，重置退避
                    EmitLog($"已连接 {_options.Uri}，会话 {_sessionId}");

                    // 主循环：任一子任务退出即视为断线
                    await Task.WhenAny(receiveTask, pumpTask, pingTask).ConfigureAwait(false);
                    throw new WebSocketException("链路结束");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        SetState(ConnectionState.Backoff, $"{Describe(ex)}；{backoff}ms 后重连");
                }
                finally
                {
                    CleanupConnection();
                    try { connCts.Cancel(); } catch { /* ignore */ }
                    connCts.Dispose();
                }

                if (ct.IsCancellationRequested) break;

                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                backoff = Math.Min(backoff * 2, _options.MaxBackoffMs);
            }

            SetState(ConnectionState.Disconnected, "已停止");
        }

        private void CleanupConnection()
        {
            var ws = _ws;
            _ws = null;
            if (ws is not null)
            {
                try { ws.Dispose(); } catch { /* ignore */ }
            }
            // 丢弃未发出的消息，避免重连后发送过期指令
            while (_sendQueue.TryDequeue(out _)) { }
            while (_sendSignal.Wait(0)) { }
            _sessionId = null;
        }

        // ---------------------------------------------------------------- 子循环

        private async Task SendPumpAsync(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await _sendSignal.WaitAsync(ct).ConfigureAwait(false);
                while (_sendQueue.TryDequeue(out var env))
                    await SendRawAsync(env, ct).ConfigureAwait(false);
            }
        }

        private async Task SendRawAsync(JsonValue env, CancellationToken ct)
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open) return;

            var bytes = Encoding.UTF8.GetBytes(env.ToJson());
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                        .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[ReceiveBufferBytes];
            var accumulated = new System.IO.MemoryStream(ReceiveBufferBytes);

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                accumulated.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        EmitLog($"对端关闭连接：{result.CloseStatus} {result.CloseStatusDescription}");
                        _readySignal?.TrySetResult(false);
                        return;
                    }

                    accumulated.Write(buffer, 0, result.Count);
                    if (accumulated.Length > MaxFrameBytes)
                    {
                        EmitLog($"收到超限帧（>{MaxFrameBytes} 字节），断开");
                        try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", ct); }
                        catch { /* ignore */ }
                        _readySignal?.TrySetResult(false);
                        return;
                    }
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(accumulated.ToArray());
                HandleFrame(text);
            }
        }

        private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(_options.PingIntervalMs, ct).ConfigureAwait(false);

                var now = Protocol.Protocol.NowMs();
                var silentFor = now - Interlocked.Read(ref _lastPongMs);
                if (silentFor > (long)_options.PingIntervalMs * _options.PingMissLimit)
                {
                    EmitLog($"心跳超时（{silentFor}ms 无 pong），断开重连");
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "ping timeout", ct); }
                    catch { /* ignore */ }
                    return;
                }

                await SendRawAsync(
                    Protocol.Protocol.Envelope(
                        Protocol.Protocol.Ping,
                        Protocol.Protocol.BuildPing(now),
                        NextId(), null),
                    ct).ConfigureAwait(false);
            }
        }

        // ---------------------------------------------------------------- 收帧处理

        private void HandleFrame(string text)
        {
            if (!JsonValue.TryParse(text, out var env) || !env.IsObject)
            {
                EmitLog("收到非法 JSON 帧，已忽略");
                return;
            }

            var type = env["type"].AsString();
            var payload = env["payload"];
            var replyTo = env["replyTo"].AsString();

            if (string.IsNullOrEmpty(type))
            {
                EmitLog("收到缺少 type 的帧，已忽略");
                return;
            }

            switch (type)
            {
                case Protocol.Protocol.Ready:
                    // 顺序很重要：StateChanged(Ready) 会在 Received("ready") **之前**触发
                    // （SetState 在上面的 switch 里，Received 在函数末尾统一派发）。
                    // 因此依赖 sessionId 的调用方必须读 SessionId 属性，
                    // 不要在 Received 处理器里缓存字段后再用——会有竞态。
                    _sessionId = payload["sessionId"].AsString();
                    // ready 会带上配置状态（附录 A.2），据此可省掉一次往返。
                    // 该字段是可选的：老服务端不带它，此时必须当作"无意见"，
                    // 不能默认成 missing_key，否则会误弹密钥引导。
                    UpdateConfig(payload.Has("config")
                        ? Protocol.ConfigState.Parse(payload["config"])
                        : Protocol.ConfigState.AssumeReady());
                    SetState(ConnectionState.Ready, _sessionId);
                    _readySignal?.TrySetResult(true);
                    break;

                case Protocol.Protocol.ConfigState:
                    UpdateConfig(Protocol.ConfigState.Parse(payload));
                    break;

                case Protocol.Protocol.Pong:
                    Interlocked.Exchange(ref _lastPongMs, Protocol.Protocol.NowMs());
                    break;

                case Protocol.Protocol.Error:
                    EmitLog($"服务端错误 {payload["code"].AsString()}：{payload["message"].AsString()}");
                    // 握手阶段的错误直接让握手失败
                    _readySignal?.TrySetResult(false);
                    break;
            }

            try
            {
                Received?.Invoke(type, payload, replyTo);
            }
            catch (Exception ex)
            {
                EmitLog($"消息处理异常（{type}）：{ex.Message}");
            }
        }

        // ---------------------------------------------------------------- 杂项

        private void SetState(ConnectionState state, string detail)
        {
            lock (_stateLock)
            {
                if (_state == state && state != ConnectionState.Ready) return;
                _state = state;
            }
            try { StateChanged?.Invoke(state, detail); } catch { /* ignore */ }
        }

        private void EmitLog(string message)
        {
            try { Log?.Invoke(message); } catch { /* ignore */ }
        }

        private static string Describe(Exception ex)
        {
            if (ex is WebSocketException wse) return $"WebSocket 错误：{wse.Message}";
            if (ex is TimeoutException) return "连接超时";
            return $"{ex.GetType().Name}：{ex.Message}";
        }
    }
}
