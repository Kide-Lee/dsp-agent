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
    /// 这层校验在 mod 内，sidecar 即使被替换/绕过也无法直接执行写操作。
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

        private readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly Dictionary<string, Allowed> _allowed =
            new Dictionary<string, Allowed>(StringComparer.Ordinal);

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
            return expiredCurrent;
        }

        public void Clear()
        {
            _pending.Clear();
            _allowed.Clear();
            Current = null;
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
