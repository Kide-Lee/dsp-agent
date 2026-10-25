using System;
using System.Collections.Generic;
using IcarusII.Common.Protocol;

namespace IcarusII.Core
{
    /// <summary>
    /// 写操作确认 + 一次性令牌。
    ///
    /// sidecar 生成 token 并放进 confirm_request；mod 收到后展示确认 UI，
    /// 玩家允许时把同一个 token 回给 sidecar。真正的 tool_call 必须带该 token，
    /// 且 callId/tool/argsHash 与确认时一致；令牌 60 秒过期、只能消费一次。
    ///
    /// 这层校验在 mod 内，sidecar 即使被替换/绕过也无法执行**未经玩家确认**的写操作。
    /// 注意：argsHash 由 sidecar 提供，mod 不持有原始 args、无法重算，
    /// 因此它只能发现 sidecar 两条消息自相矛盾，不能阻止替换后的 sidecar 换参数。
    /// 真正的边界是“玩家确认 + 一次性 token + 工具名白名单”。
    /// </summary>
    internal sealed class ConfirmationService
    {
        private sealed class Pending
        {
            public ConfirmRequestInfo Request;
            public long ExpiresAtMs;
        }

        private sealed class Allowed
        {
            public string CallId;
            public string Tool;
            public string ArgsHash;
            public long ExpiresAtMs;
        }

        /// <summary>
        /// 脚本写租约：玩家确认的是整段脚本，允许脚本在 TTL 内调用最多 N 次写工具。
        /// 只由 run_script 的确认请求创建；mod 仍要求每个写调用带同一个租约 token + 脚本哈希。
        /// </summary>
        private sealed class ScriptLease
        {
            public string ScriptHash;
            public int Remaining;
            public long ExpiresAtMs;
            public HashSet<string> WriteNames = new HashSet<string>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly Dictionary<string, Allowed> _allowed =
            new Dictionary<string, Allowed>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptLease> _leases =
            new Dictionary<string, ScriptLease>(StringComparer.Ordinal);

        public ConfirmRequestInfo Current { get; private set; }

        public bool TryRegister(ConfirmRequestInfo request, out string error)
        {
            error = null;
            if (request == null)
            {
                error = "invalid_args";
                return false;
            }
            if (Current != null || _pending.Count > 0)
            {
                error = "busy";
                return false;
            }
            var now = NowMs();
            // token 记录的有效期与请求一致；UI 等待时间不计入 60s 之外的宽限。
            var pending = new Pending
            {
                Request = request,
                ExpiresAtMs = now + Math.Max(1000, request.TimeoutMs),
            };
            _pending[request.RequestId] = pending;
            Current = request;
            return true;
        }

        public bool TryResolve(string requestId, bool allow, out ConfirmRequestInfo request)
        {
            request = null;
            if (string.IsNullOrEmpty(requestId) || !_pending.TryGetValue(requestId, out var pending))
                return false;
            _pending.Remove(requestId);
            if (ReferenceEquals(Current, pending.Request)) Current = null;
            request = pending.Request;
            if (!allow) return true;

            if (request.MaxWrites > 0)
            {
                // 租约只由 run_script 创建，且必须绑定非空脚本哈希与明确的写工具名。
                // 空 writeNames 曾经等于“放行全部写工具”，这里直接拒绝。
                if (!string.Equals(request.Tool, Protocol.ToolRunScript, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(request.ArgsHash) ||
                    request.WriteNames == null || request.WriteNames.Count == 0)
                    return false;
                foreach (var name in request.WriteNames)
                    if (!Protocol.IsWriteTool(name)) return false;

                var lease = new ScriptLease
                {
                    ScriptHash = request.ArgsHash,
                    Remaining = request.MaxWrites,
                    ExpiresAtMs = NowMs() + Protocol.ScriptLeaseMs,
                };
                foreach (var name in request.WriteNames) lease.WriteNames.Add(name);
                _leases[request.Token] = lease;
                return true;
            }

            if (!Protocol.IsWriteTool(request.Tool))
                return false;

            _allowed[request.Token] = new Allowed
            {
                CallId = request.CallId,
                Tool = request.Tool,
                ArgsHash = request.ArgsHash,
                ExpiresAtMs = NowMs() + Protocol.ConfirmTimeoutMs,
            };
            return true;
        }

        public bool TryConsume(ToolCallInfo call, out string error)
        {
            error = null;
            if (call == null) return true;
            if (string.IsNullOrEmpty(call.ConfirmToken))
            {
                error = Protocol.CodeConfirmationRequired;
                return false;
            }
            if (_leases.TryGetValue(call.ConfirmToken, out var lease))
            {
                if (NowMs() > lease.ExpiresAtMs || lease.Remaining <= 0)
                {
                    _leases.Remove(call.ConfirmToken);
                    error = Protocol.CodeConfirmationExpired;
                    return false;
                }
                if (!string.Equals(lease.ScriptHash, call.ConfirmArgsHash ?? string.Empty,
                        StringComparison.Ordinal))
                {
                    error = Protocol.CodeConfirmationInvalid;
                    return false;
                }
                if (!lease.WriteNames.Contains(call.Name))
                {
                    error = Protocol.CodeConfirmationInvalid;
                    return false;
                }
                lease.Remaining -= 1;
                // 用尽后保留到 TTL/Release，下一次调用能得到明确的 expired，而不是 invalid。
                return true;
            }
            if (!_allowed.TryGetValue(call.ConfirmToken, out var allowed))
            {
                error = Protocol.CodeConfirmationInvalid;
                return false;
            }
            _allowed.Remove(call.ConfirmToken);
            if (NowMs() > allowed.ExpiresAtMs)
            {
                error = Protocol.CodeConfirmationExpired;
                return false;
            }
            if (!string.Equals(allowed.CallId, call.CallId, StringComparison.Ordinal) ||
                !string.Equals(allowed.Tool, call.Name, StringComparison.Ordinal) ||
                !string.Equals(allowed.ArgsHash ?? string.Empty, call.ConfirmArgsHash ?? string.Empty,
                    StringComparison.Ordinal))
            {
                error = Protocol.CodeConfirmationInvalid;
                return false;
            }
            return true;
        }

        /// <summary>清理过期项；返回刚过期、且正在 UI 上展示的请求（调用方负责隐藏 UI）。</summary>
        public ConfirmRequestInfo Expire()
        {
            var now = NowMs();
            ConfirmRequestInfo expiredCurrent = null;
            foreach (var pair in new List<KeyValuePair<string, Pending>>(_pending))
            {
                if (now <= pair.Value.ExpiresAtMs) continue;
                _pending.Remove(pair.Key);
                if (ReferenceEquals(Current, pair.Value.Request))
                {
                    Current = null;
                    expiredCurrent = pair.Value.Request;
                }
            }
            foreach (var pair in new List<KeyValuePair<string, Allowed>>(_allowed))
            {
                if (now > pair.Value.ExpiresAtMs) _allowed.Remove(pair.Key);
            }
            foreach (var pair in new List<KeyValuePair<string, ScriptLease>>(_leases))
            {
                // 用尽后保留到 TTL，下一次调用能拿到明确的 expired，而不是 invalid。
                if (now > pair.Value.ExpiresAtMs) _leases.Remove(pair.Key);
            }
            return expiredCurrent;
        }

        /// <summary>脚本正常结束/失败时释放租约，缩短 token 的有效窗口。</summary>
        public void Release(string token)
        {
            if (!string.IsNullOrEmpty(token)) _leases.Remove(token);
        }

        public void Clear()
        {
            _pending.Clear();
            _allowed.Clear();
            _leases.Clear();
            Current = null;
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
