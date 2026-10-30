using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BepInEx.Logging;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;
using IcarusII.Game;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 一次工具执行的结果，已包含协议层需要的 meta。
    /// </summary>
    internal sealed class GameToolOutcome
    {
        public string CallId;
        public string Name;
        public bool Ok;
        public JsonValue Data;
        public string ErrorCode;
        public string ErrorMessage;
        public JsonValue Meta;
        public string Summary;
        public long ElapsedMs;
        /// <summary>可选附件（协议附录 B.3 `image`）；只有截图类工具会填。</summary>
        public JsonValue Image;
    }

    /// <summary>
    /// 可分帧执行的采集任务。Step 返回 true 表示完成，之后才调用 Finish。
    /// </summary>
    internal interface IGameToolJob
    {
        bool Step(int budgetMs);
        GameToolJobResult Finish();
    }

    internal sealed class GameToolJobResult
    {
        public JsonValue Data;
        public bool Truncated;
        public int NextOffset = -1;
        /// <summary>可选附件（协议附录 B.3 `image`）。只有截图类工具会填。</summary>
        public JsonValue Image;
    }

    internal interface IGameToolHandler
    {
        string Name { get; }
        int TimeoutMs { get; }
        IGameToolJob Start(JsonValue args);
    }

    /// <summary>
    /// 只读工具执行器。
    ///
    /// - 所有 handler 只在 Unity 主线程执行；
    /// - 分帧任务每帧最多占用 <paramref name="budgetMs"/>；
    /// - 结果按 (name, args, saveKey) 缓存 5 秒；
    /// - 超时、断线、换档都会产生结构化错误。
    /// </summary>
    internal sealed class GameToolDispatcher
    {
        private const int CacheTtlMs = 5000;
        private const int MaxActiveCalls = 4;

        private sealed class CachedResult
        {
            public GameToolJobResult Result;
            public long AtMs;
            public string SaveKey;
        }

        private sealed class ActiveCall
        {
            public string CallId;
            public string Name;
            public IGameToolJob Job;
            public int TimeoutMs;
            public long DeadlineMs;
            public long StartTick;
            public long StartMs;
            public string SaveKey;
            public string CacheKey;
        }

        private readonly Dictionary<string, IGameToolHandler> _handlers =
            new Dictionary<string, IGameToolHandler>(StringComparer.Ordinal);

        private readonly Dictionary<string, CachedResult> _cache =
            new Dictionary<string, CachedResult>(StringComparer.Ordinal);

        private readonly List<ActiveCall> _active = new List<ActiveCall>();
        private readonly ManualLogSource _log;

        public GameToolDispatcher(ManualLogSource log)
        {
            _log = log;

            Register(new OverviewTool());
            Register(new PlayerTool());
            Register(new PlanetTool());
            Register(new PowerTool());
            Register(new ResearchTool());
            Register(new ProductionTool());
            Register(new ExportPrototypesTool());
            Register(new ExportTextTool());
            Register(new EntityTool());
            Register(new EntitiesTool());
            Register(new ProgressTool());
            Register(new CaptureViewTool());
            Register(new AnalyzeAreaTool());
            Register(new RenderLayoutTool());
            Register(new CopyBlueprintTool());
            Register(new TraceLineTool());
            Register(new PlanetsTool());
            Register(new LogisticsTool());
            Register(new PlaceMarkerTool());
            Register(new OpenPanelTool());
            Register(new SetResearchTool());
            Register(new DropItemTool());
            Register(new SetRecipeTool());
            Register(new SetAssemblerBatchTool());
            Register(new StorageSetFilterTool());
            Register(new StorageSetLimitTool());
            Register(new StorageMoveTool());
            Register(new StorageDepositTool());
            Register(new StorageWithdrawTool());
            Register(new FeedInputTool());
            Register(new TetherAddTool());
            Register(new TetherClearTool());
            Register(new TetherListTool());
            Register(new GameTodoTemplatesTool());
            Register(new GameTodoListTool());
            Register(new GameTodoAddTool());
            Register(new GameTodoRemoveTool());
            Register(new TakeOutputTool());
            Register(new UndoLastWriteTool());
            Register(new ModsListTool());
            Register(new ModsConfigListTool());
            Register(new ModsConfigGetTool());
            Register(new ModsConfigSetTool());
        }

        private static bool IsWriteTool(string name)
        {
            return Protocol.IsWriteTool(name);
        }

        private void Register(IGameToolHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.Name)) return;
            _handlers[handler.Name] = handler;
        }

        public int PendingCount => _active.Count;

        /// <summary>
        /// 开始一次调用。能立即完成的（缓存/错误）直接返回 outcome；
        /// 需要分帧时把任务放进队列并返回 null，由 <see cref="Tick"/> 完成。
        /// </summary>
        public GameToolOutcome Begin(string callId, string name, JsonValue args, int timeoutMs)
        {
            args = args != null && args.IsObject ? args : JsonValue.NewObject();

            if (!GameRefs.IsRunning)
            {
                return Error(callId, name, Protocol.CodeNotInGame, "当前没有进入存档（或游戏仍在加载）");
            }

            IGameToolHandler handler;
            if (!_handlers.TryGetValue(name, out handler))
            {
                return Error(callId, name, Protocol.CodeToolNotFound, $"未知工具：{name}");
            }

            if (_active.Count >= MaxActiveCalls)
            {
                return Error(callId, name, Protocol.CodeBusy, "同时执行的工具过多，请串行调用");
            }

            var saveKey = GameRefs.CurrentSaveKey() ?? string.Empty;
            var cacheKey = BuildCacheKey(name, args, saveKey);
            var now = NowMs();

            // 知识库导出是一次性快照：不走 5 秒缓存，避免换语言/换版本后拿到旧数据。
            var useCache = name != "game_export_prototypes" && name != "game_export_text"
                && !IsWriteTool(name);
            CachedResult cached;
            if (useCache && _cache.TryGetValue(cacheKey, out cached) &&
                cached.SaveKey == saveKey &&
                now - cached.AtMs <= CacheTtlMs)
            {
                return Success(callId, name, cached.Result, 0, GameMain.gameTick, GameMain.gameTick, true);
            }

            IGameToolJob job;
            try
            {
                job = handler.Start(args);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[tool] {name} 参数/初始化失败：{ex.Message}");
                return Error(callId, name, Protocol.CodeInvalidArgs, ex.Message);
            }

            if (job == null)
            {
                return Error(callId, name, Protocol.CodeInvalidArgs, "工具参数无效");
            }

            var effectiveTimeout = timeoutMs > 0 ? timeoutMs : handler.TimeoutMs;
            _active.Add(new ActiveCall
            {
                CallId = callId,
                Name = name,
                Job = job,
                TimeoutMs = effectiveTimeout,
                DeadlineMs = now + effectiveTimeout,
                StartTick = GameMain.gameTick,
                StartMs = now,
                SaveKey = saveKey,
                CacheKey = cacheKey,
            });

            _log?.LogInfo($"[tool] 开始 {name} callId={callId} timeout={effectiveTimeout}ms");
            return null;
        }

        /// <summary>
        /// 在 Plugin.Update 里调用。每帧总预算（毫秒），完成时通过 onDone 回调。
        /// </summary>
        public void Tick(int budgetMs, Action<GameToolOutcome> onDone)
        {
            if (_active.Count == 0) return;

            if (!GameRefs.IsRunning)
            {
                var pending = _active.ToArray();
                _active.Clear();
                foreach (var call in pending)
                {
                    var outcome = Error(call.CallId, call.Name, Protocol.CodeNotInGame, "游戏已退出存档");
                    SafeDone(onDone, outcome);
                }
                return;
            }

            var watch = Stopwatch.StartNew();
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var call = _active[i];
                var now = NowMs();
                if (now >= call.DeadlineMs)
                {
                    _active.RemoveAt(i);
                    SafeDone(onDone, Error(call.CallId, call.Name, Protocol.CodeTimeout,
                        $"工具 {call.Name} 在 {call.TimeoutMs}ms 内未完成"));
                    continue;
                }

                var remaining = budgetMs - (int)watch.ElapsedMilliseconds;
                if (remaining <= 0) break;

                bool done;
                try
                {
                    done = call.Job.Step(Math.Max(1, remaining));
                }
                catch (Exception ex)
                {
                    _active.RemoveAt(i);
                    _log?.LogError($"[tool] {call.Name} 执行异常：{ex}");
                    SafeDone(onDone, Error(call.CallId, call.Name, "internal", ex.Message));
                    continue;
                }

                if (!done) continue;

                _active.RemoveAt(i);
                GameToolJobResult result;
                try
                {
                    result = call.Job.Finish();
                }
                catch (Exception ex)
                {
                    _log?.LogError($"[tool] {call.Name} 组装结果异常：{ex}");
                    SafeDone(onDone, Error(call.CallId, call.Name, "internal", ex.Message));
                    continue;
                }

                var elapsed = NowMs() - call.StartMs;
                var outcome = Success(call.CallId, call.Name, result, elapsed,
                    call.StartTick, GameMain.gameTick, false);
                _log?.LogInfo($"[tool] 完成 {call.Name} callId={call.CallId} elapsed={elapsed}ms"
                    + " truncated=" + (result.Truncated ? "true" : "false"));
                if (IsWriteTool(call.Name))
                {
                    // 写操作后读缓存全部失效：否则随后的 game_get_* / tether_list
                    // 可能在 5 秒内返回写之前的状态。
                    _cache.Clear();
                }
                if (call.Name != "game_export_prototypes" && call.Name != "game_export_text"
                    && !IsWriteTool(call.Name))
                {
                    _cache[call.CacheKey] = new CachedResult
                    {
                        Result = result,
                        AtMs = NowMs(),
                        SaveKey = call.SaveKey,
                    };
                }
                SafeDone(onDone, outcome);
            }
        }

        public void CancelAll(string reason, Action<GameToolOutcome> onDone = null)
        {
            if (_active.Count == 0) return;
            var pending = _active.ToArray();
            _active.Clear();
            foreach (var call in pending)
            {
                _log?.LogInfo($"[tool] 取消 {call.Name} callId={call.CallId}：{reason}");
                if (onDone != null)
                {
                    SafeDone(onDone, Error(call.CallId, call.Name, Protocol.CodeCancelled, reason));
                }
            }
        }

        public void ClearCache()
        {
            _cache.Clear();
        }

        private void SafeDone(Action<GameToolOutcome> onDone, GameToolOutcome outcome)
        {
            try
            {
                onDone?.Invoke(outcome);
            }
            catch (Exception ex)
            {
                _log?.LogError($"[tool] 完成回调异常：{ex}");
            }
        }

        private GameToolOutcome Success(string callId, string name, GameToolJobResult result,
            long elapsedMs, long tickAtStart, long tickAtEnd, bool cached)
        {
            var data = result?.Data ?? JsonValue.NewObject();
            var summary = data["display"].AsString(string.Empty);

            var meta = BuildMeta(name, tickAtStart, tickAtEnd, result, elapsedMs);
            if (cached) meta.Set("cached", true);

            return new GameToolOutcome
            {
                CallId = callId,
                Name = name,
                Ok = true,
                Data = data,
                Meta = meta,
                Summary = summary,
                Image = result?.Image,
                ElapsedMs = elapsedMs,
            };
        }

        private GameToolOutcome Error(string callId, string name, string code, string message)
        {
            var tick = GameRefs.IsRunning ? GameMain.gameTick : 0;
            var meta = JsonValue.NewObject();
            meta.Set("gameTick", tick);
            meta.Set("saveKey", GameRefs.CurrentSaveKey() ?? string.Empty);
            meta.Set("tool", name);
            meta.Set("tickAtStart", tick);
            meta.Set("tickAtEnd", tick);
            meta.Set("truncated", false);
            meta.Set("elapsedMs", 0);

            return new GameToolOutcome
            {
                CallId = callId,
                Name = name,
                Ok = false,
                Data = JsonValue.Null,
                ErrorCode = code,
                ErrorMessage = message ?? string.Empty,
                Meta = meta,
                Summary = message ?? string.Empty,
                ElapsedMs = 0,
            };
        }

        private static JsonValue BuildMeta(string name, long tickAtStart, long tickAtEnd,
            GameToolJobResult result, long elapsedMs)
        {
            var meta = JsonValue.NewObject();
            meta.Set("tool", name);
            meta.Set("gameTick", tickAtEnd);
            meta.Set("saveKey", GameRefs.CurrentSaveKey() ?? string.Empty);
            meta.Set("tickAtStart", tickAtStart);
            meta.Set("tickAtEnd", tickAtEnd);
            meta.Set("elapsedMs", elapsedMs);
            meta.Set("truncated", result != null && result.Truncated);
            if (result != null && result.NextOffset >= 0) meta.Set("nextOffset", result.NextOffset);
            return meta;
        }

        private static string BuildCacheKey(string name, JsonValue args, string saveKey)
        {
            var sb = new StringBuilder(128);
            sb.Append(saveKey).Append('|').Append(name).Append('|');
            WriteCanonical(args, sb);
            return sb.ToString();
        }

        private static void WriteCanonical(JsonValue value, StringBuilder sb)
        {
            if (value == null || value.IsNull)
            {
                sb.Append("null");
                return;
            }

            switch (value.Kind)
            {
                case JsonKind.Object:
                {
                    var members = new List<KeyValuePair<string, JsonValue>>();
                    foreach (var m in value.Members) members.Add(m);
                    members.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    sb.Append('{');
                    for (int i = 0; i < members.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(members[i].Key).Append(':');
                        WriteCanonical(members[i].Value, sb);
                    }
                    sb.Append('}');
                    return;
                }
                case JsonKind.Array:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in value.Items)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteCanonical(item, sb);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    sb.Append(value.ToJson());
                    return;
            }
        }

        private static long NowMs()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }
    }
}
