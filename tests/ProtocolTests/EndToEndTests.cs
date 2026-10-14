using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DSPAgent.Common.Json;
using DSPAgent.Common.Protocol;
using DSPAgent.Common.Transport;

namespace DSPAgent.Tests
{
    /// <summary>
    /// 端到端：用**与 mod 完全相同的** Shared 源码去连真实的 sidecar，
    /// 跑通 hell / ready → ping/pong → prompt/echo → 干净关闭。
    ///
    /// 这是 M0 里唯一能在没有游戏的情况下验证 C# 侧协议的自动化手段，
    /// 因此它验证的是真代码路径，而不是 mock。
    /// </summary>
    internal static class EndToEndTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static readonly ManualResetEventSlim ReadySeen = new ManualResetEventSlim(false);
        private static readonly ManualResetEventSlim PongSeen = new ManualResetEventSlim(false);
        private static readonly ManualResetEventSlim StreamDone = new ManualResetEventSlim(false);
        private static readonly ManualResetEventSlim IdleSeen = new ManualResetEventSlim(false);

        private static readonly StringBuilder Streamed = new StringBuilder();
        private static string _finalText;
        private static string _sessionId;
        private static string _errorCode;

        public static int Run(string url, string token)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine($"── 端到端：{url} ──");

            var uri = new Uri(url);
            var options = new SidecarOptions
            {
                Host = uri.Host,
                Port = uri.Port,
                Token = token,
                ModVersion = "0.1.0-test",
                GameVersion = "0.10.30.3100",
                GameLanguage = "zh-CN",
                Capabilities = new[] { "ui.chat" },
                MinBackoffMs = 200,
                MaxBackoffMs = 1000,
                ConnectTimeoutMs = 3000,
                HandshakeTimeoutMs = 5000,
            };

            using var client = new SidecarClient(options);
            client.Log += m => Console.WriteLine($"    [client] {m}");
            client.StateChanged += (s, d) =>
            {
                Console.WriteLine($"    [state] {s} {d}");
                if (s == ConnectionState.Ready) ReadySeen.Set();
            };
            client.Received += OnReceived;

            client.Start();

            if (!ReadySeen.Wait(8000))
            {
                Fail("未在 8s 内进入 Ready 状态（sidecar 未启动？token 不对？）");
                Dump();
                return 1;
            }

            // 注意：必须读 client.SessionId（权威值），不能读 Received(ready) 里缓存的字段——
            // StateChanged(Ready) 会在 Received(ready) 之前触发，存在竞态。
            Check("握手完成，拿到 sessionId", !string.IsNullOrEmpty(client.SessionId));

            // --- ping / pong
            client.Send(Protocol.Ping, Protocol.BuildPing(Protocol.NowMs()));
            Check("ping → pong", PongSeen.Wait(3000));

            // --- prompt / echo
            var sent = "你好，这是一条 M0 回声测试";
            var msgId = client.SendPrompt(sent);
            Check("prompt 已入队", !string.IsNullOrEmpty(msgId));

            var completed = StreamDone.Wait(8000);
            Check("收到 message(final)", completed);

            if (completed)
            {
                var echoed = Streamed.ToString();
                Check($"流式文本拼接非空（收到 {echoed.Length} 字符）", echoed.Length > 0);
                Check("流式文本与 message 一致", echoed == _finalText);
                Check("echo 包含原文", (echoed ?? "").Contains(sent));
                Check("回合结束后回到 idle", IdleSeen.Wait(3000));
            }

            Check("全程无服务端错误", _errorCode == null);

            client.Stop();

            Dump();
            return Failures.Count == 0 ? 0 : 1;
        }

        private static void OnReceived(string type, JsonValue payload, string replyTo)
        {
            switch (type)
            {
                case Protocol.Ready:
                    _sessionId = payload["sessionId"].AsString();
                    break;

                case Protocol.Pong:
                    PongSeen.Set();
                    break;

                case Protocol.Stream:
                    if (payload["channel"].AsString("text") == "text")
                        Streamed.Append(payload["delta"].AsString(""));
                    break;

                case Protocol.Message:
                    if (payload["final"].AsBool())
                    {
                        var sb = new StringBuilder();
                        foreach (var block in payload["content"].Items)
                            if (block["type"].AsString() == "text")
                                sb.Append(block["text"].AsString(""));
                        _finalText = sb.ToString();
                        StreamDone.Set();
                    }
                    break;

                case Protocol.Status:
                    if (payload["state"].AsString() == Protocol.StateIdle && StreamDone.IsSet)
                        IdleSeen.Set();
                    break;

                case Protocol.Error:
                    _errorCode = payload["code"].AsString("unknown");
                    Fail($"服务端错误 {_errorCode}：{payload["message"].AsString()}");
                    break;
            }
        }

        private static void Check(string name, bool ok)
        {
            if (ok) Console.WriteLine($"  ok   {name}");
            else Fail(name);
        }

        private static void Fail(string name)
        {
            Failures.Add(name);
            Console.WriteLine($"  FAIL {name}");
        }

        private static void Dump()
        {
            Console.WriteLine();
            if (Failures.Count == 0)
            {
                Console.WriteLine("✅ 端到端通过");
            }
            else
            {
                Console.WriteLine($"❌ 端到端失败 {Failures.Count} 项");
                foreach (var f in Failures) Console.WriteLine($"     - {f}");
            }
        }
    }
}
