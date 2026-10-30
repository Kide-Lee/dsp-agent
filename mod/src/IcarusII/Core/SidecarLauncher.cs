using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using IcarusII.Common.Protocol;
using IcarusII.Common.Transport;
using IcarusII.Config;
using IcarusII.Game.Tools;

namespace IcarusII.Core
{
    /// <summary>
    /// 负责让 sidecar 就位：拉起进程（spawnExe）或直接连接（connectOnly），
    /// 然后从 runtime.json 读取真实的端口与 token。
    ///
    /// 之所以能统一处理原生 Windows 与 Proton：随包分发的 sidecar 是
    /// **Windows PE**（自包含，内嵌 Node 运行时），而游戏进程本身也跑在
    /// Windows 语义下（Proton/Wine），所以 Process.Start 直接可用，无需分支。
    /// </summary>
    internal sealed class SidecarLauncher
    {
        private readonly PluginConfig _config;
        private readonly ManualLogSource _log;
        private readonly string _pluginDir;

        private Process _process;

        public SidecarLauncher(PluginConfig config, ManualLogSource log, string pluginDir)
        {
            _config = config;
            _log = log;
            _pluginDir = pluginDir;
        }

        public string DataDir =>
            string.IsNullOrWhiteSpace(_config.DataDir)
                ? DefaultDataDir()
                : _config.DataDir;

        public static string DefaultDataDir()
        {
            // Windows 语义；Proton 下会落在
            // compatdata/<appid>/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Path.GetTempPath(), "icarus-ii");
            return Path.Combine(appData, "icarus-ii");
        }

        public string ResolveExePath()
        {
            if (!string.IsNullOrWhiteSpace(_config.SidecarExePath))
                return _config.SidecarExePath;
            return Path.Combine(_pluginDir, "sidecar", "icarus-ii-sidecar.exe");
        }

        /// <summary>
        /// 让 sidecar 就位并返回连接参数。
        /// 失败时返回 false，并把可读原因写入 error（调用方负责显示到 UI）。
        /// </summary>
        /// <summary>sidecar 的日志行只让 warn/error 进主日志，info 降到 debug，避免刷屏。</summary>
        private static void LogSidecarLine(ManualLogSource log, string prefix, string line)
        {
            bool Has(string level) => line.IndexOf("\"level\":\"" + level + "\"", StringComparison.Ordinal) >= 0;
            if (Has("error")) log.LogError($"{prefix} {line}");
            else if (Has("warn")) log.LogWarning($"{prefix} {line}");
            else if (line.IndexOf("\"level\":\"", StringComparison.Ordinal) >= 0) log.LogDebug($"{prefix} {line}");
            else log.LogWarning($"{prefix} {line}");
        }

        public bool TryPrepare(out SidecarOptions options, out string error)
        {
            options = null;
            error = null;

            if (!IcarusII.Common.Protocol.Protocol.IsLoopbackHost(_config.SidecarHost))
            {
                error = $"Sidecar Host={_config.SidecarHost} 不是回环地址；只允许 127.0.0.1 / ::1 / localhost。";
                _log.LogError(error);
                return false;
            }

            var dataDir = DataDir;
            try
            {
                Directory.CreateDirectory(dataDir);
            }
            catch (Exception ex)
            {
                error = $"无法创建数据目录 {dataDir}：{ex.Message}";
                return false;
            }

            var mode = (_config.LaunchMode ?? "spawnExe").Trim();
            _log.LogInfo($"sidecar 启动模式：{mode}；数据目录：{dataDir}");

            if (string.Equals(mode, "connectOnly", StringComparison.OrdinalIgnoreCase))
            {
                // 直接连：端口由配置指定，token 仍从 runtime.json 取（若存在）
                var token = string.Empty;
                if (RuntimeInfo.TryLoad(dataDir, out var existing, out var loadError))
                    token = existing.Token;
                else
                    _log.LogWarning($"connectOnly：{loadError}（将使用空 token，sidecar 可能拒绝握手）");

                options = BuildOptions(_config.SidecarHost, _config.SidecarPort, token);
                return true;
            }

            // spawnExe / spawnNode：先清掉可能残留的 runtime.json，
            // 否则会把上一次运行的 token/端口当成这次的。
            var runtimePath = RuntimeInfo.PathFor(dataDir);
            TryDelete(runtimePath);

            if (!TrySpawn(mode, dataDir, out error)) return false;

            if (!WaitForRuntime(dataDir, out var info, out error)) return false;

            options = BuildOptions(info.Host, info.Port, info.Token);
            return true;
        }

        private bool TrySpawn(string mode, string dataDir, out string error)
        {
            error = null;

            string fileName;
            string arguments;

            if (string.Equals(mode, "spawnNode", StringComparison.OrdinalIgnoreCase))
            {
                var script = Path.Combine(_pluginDir, "sidecar", "sidecar.cjs");
                if (!File.Exists(script))
                {
                    error = $"spawnNode 模式找不到 {script}";
                    return false;
                }
                fileName = "node";
                arguments = $"\"{script}\" serve --host {_config.SidecarHost} --port {_config.SidecarPort} --data-dir \"{dataDir}\"";
                if (_config.DebugToolConsole) arguments += " --debug-tools";
            }
            else
            {
                var exe = ResolveExePath();
                if (!File.Exists(exe))
                {
                    error = $"找不到 sidecar 可执行文件：{exe}\n" +
                            "请确认发布包内 BepInEx/plugins/IcarusII/sidecar/ 下有 icarus-ii-sidecar.exe，" +
                            "或在配置中改用 LaunchMode=connectOnly 自行启动 sidecar。";
                    return false;
                }
                fileName = exe;
                arguments = $"serve --host {_config.SidecarHost} --port {_config.SidecarPort} --data-dir \"{dataDir}\"";
                if (_config.DebugToolConsole) arguments += " --debug-tools";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(fileName) ?? _pluginDir,
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                // 子进程输出必须持续消费，否则管道写满会把 sidecar 卡死。
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) LogSidecarLine(_log, "[sidecar]", e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) LogSidecarLine(_log, "[sidecar:err]", e.Data);
                };
                proc.Exited += (_, __) => _log.LogWarning("[sidecar] 进程已退出");

                if (!proc.Start())
                {
                    error = "Process.Start 返回 false";
                    return false;
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                _process = proc;

                _log.LogInfo($"已拉起 sidecar：pid={proc.Id} {fileName} {arguments}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"拉起 sidecar 失败：{ex.GetType().Name} {ex.Message}";
                return false;
            }
        }

        private bool WaitForRuntime(string dataDir, out RuntimeInfo info, out string error)
        {
            info = null;
            error = null;

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, _config.SidecarStartupTimeoutMs));
            string lastError = "尚未写出 runtime.json";

            while (DateTime.UtcNow < deadline)
            {
                if (_process != null && _process.HasExited)
                {
                    error = $"sidecar 进程提前退出（exit={_process.ExitCode}）。请查看 BepInEx 日志中 [sidecar:err] 行。";
                    return false;
                }

                if (RuntimeInfo.TryLoad(dataDir, out info, out lastError))
                    return true;

                // 文件可能正在被写入（读到半截 JSON），短暂等待后重试
                Thread.Sleep(150);
            }

            error = $"等待 sidecar 启动超时（{_config.SidecarStartupTimeoutMs}ms）：{lastError}";
            return false;
        }

        private SidecarOptions BuildOptions(string host, int port, string token)
        {
            var capabilities = new List<string>
            {
                "ui.chat",
                IcarusII.Common.Protocol.Protocol.CapabilityGameRead,
                IcarusII.Common.Protocol.Protocol.CapabilityGameEntity,
                IcarusII.Common.Protocol.Protocol.CapabilityGameProgress,
                IcarusII.Common.Protocol.Protocol.CapabilityGameView,
                IcarusII.Common.Protocol.Protocol.CapabilityGameAnalyze,
                IcarusII.Common.Protocol.Protocol.CapabilityUiAsk,
                IcarusII.Common.Protocol.Protocol.CapabilityGameWrite,
                IcarusII.Common.Protocol.Protocol.CapabilityGameSandbox,
                IcarusII.Common.Protocol.Protocol.CapabilityGameDrop,
                IcarusII.Common.Protocol.Protocol.CapabilityGameBuild,
                IcarusII.Common.Protocol.Protocol.CapabilityGameStorage,
                IcarusII.Common.Protocol.Protocol.CapabilityGameTether,
                IcarusII.Common.Protocol.Protocol.CapabilityGameTodo,
                IcarusII.Common.Protocol.Protocol.CapabilityScriptWrite,
                IcarusII.Common.Protocol.Protocol.CapabilityRichTextV2,
                IcarusII.Common.Protocol.Protocol.CapabilityUiTree,
            };
            if (_config.AllowThirdPartyTools)
                capabilities.Add(IcarusII.Common.Protocol.Protocol.CapabilityModBridge);

            return new SidecarOptions
            {
                Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim(),
                Port = port,
                Token = token,
                ModVersion = Plugin.PluginVersion,
                GameVersion = Game.GameRefs.GameVersion,
                GameLanguage = Game.GameRefs.LanguageCode,
                Capabilities = capabilities.ToArray(),
                Extensions = _config.AllowThirdPartyTools
                    ? ModBridge.Extensions()
                    : new ExtensionSummary[0],
                ConnectTimeoutMs = Math.Max(500, _config.ConnectTimeoutMs),
                MinBackoffMs = 1000,
                MaxBackoffMs = 30000,
            };
        }

        /// <summary>游戏退出时回收 sidecar 进程。</summary>
        public void Shutdown()
        {
            var proc = _process;
            _process = null;
            if (proc == null) return;

            try
            {
                if (!proc.HasExited)
                {
                    _log.LogInfo($"[sidecar] 正在结束进程 pid={proc.Id}");
                    proc.Kill();
                    proc.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[sidecar] 结束进程失败：{ex.Message}");
            }
            finally
            {
                try { proc.Dispose(); } catch { /* ignore */ }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 残留文件由后续解析失败兜底，不阻断启动 */ }
        }
    }
}
