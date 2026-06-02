using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    /// <summary>CCB (Node.js cc-companion) 子进程管理器 — JobObject+PID清理+崩溃重启（旧 BridgeLifecycle 逻辑）</summary>
    public class CcbManager : IDisposable
    {
        private Process? _process;
        private readonly string _companionDir;
        private readonly string _projectPath;
        private readonly string? _nodeExe;
        private readonly int _ccbPort;
        private readonly string? _ccbToken;
        private readonly int _mcpPort;
        private readonly int _agentMcpPort;
        private readonly string? _modelName;
        private readonly long _budgetLimit;
        private readonly string _budgetAction;
        private bool _ready;
        private IntPtr _jobHandle = IntPtr.Zero;

        public bool IsReady => _ready;
        /// <summary>TickAndRestart 重启了 companion 进程时为 true，调用方检查后应清除</summary>
        public bool WasRestarted { get; set; }

        public CcbManager(string companionDir, string projectPath, int ccbPort = 19998, int mcpPort = 9877, int agentMcpPort = 9878, string? nodeExe = null, string? ccbToken = null, string? modelName = null, long budgetLimit = 0, string budgetAction = "Block")
        {
            _companionDir = companionDir;
            _projectPath = projectPath;
            _ccbPort = ccbPort;
            _mcpPort = mcpPort;
            _agentMcpPort = agentMcpPort;
            _ccbToken = ccbToken;
            _modelName = modelName;
            _budgetLimit = budgetLimit;
            _budgetAction = budgetAction;
            _nodeExe = nodeExe ?? CompanionInstaller.FindNodeExe();
        }

        public bool Start()
        {
            if (string.IsNullOrEmpty(_nodeExe) || !Directory.Exists(_companionDir))
            {
                CoreLog.Error($"[CcbManager] node={_nodeExe ?? "(null)"}, dir={_companionDir}");
                return false;
            }

            // 进程残留清理：先按进程名扫描（最可靠），再按 PID 文件（备份）
            KillStaleProcesses();
            KillStaleByPidFile(_companionDir);
            System.Threading.Thread.Sleep(500);

            Directory.CreateDirectory(_projectPath);

            var mcpJsonPath = Path.Combine(_projectPath, ".mcp.json");
            var mcpConfig = new
            {
                mcpServers = new
                {
                    agent = new { type = "http", url = $"http://localhost:{_agentMcpPort}/mcp", timeout = 300000 }
                }
            };
            var mcpJson = System.Text.Json.JsonSerializer.Serialize(mcpConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(mcpJsonPath, mcpJson);

            var args = $"--import tsx/esm companion/companion.ts"
                + $" --idle-timeout 30000"
                + $" --project-path \"{_projectPath}\"";
            if (!string.IsNullOrEmpty(_modelName))
                args += $" --model-name \"{_modelName}\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _nodeExe,
                    Arguments = args,
                    WorkingDirectory = _companionDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.Environment["CCB_HOST"] = "0.0.0.0";
                psi.Environment["CCB_PORT"] = _ccbPort.ToString();
                psi.Environment["CCB_TOKEN_BUDGET_LIMIT"] = _budgetLimit.ToString();
                psi.Environment["CCB_TOKEN_BUDGET_ACTION"] = _budgetAction;
                if (!string.IsNullOrEmpty(_ccbToken))
                    psi.Environment["CCB_AUTH_TOKEN"] = _ccbToken;

                _ready = false;
                _process = Process.Start(psi);
                if (_process == null) { CoreLog.Error("[CcbManager] 无法启动进程"); return false; }

                // Windows JobObject：父进程退出 → OS 自动杀子进程
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (_jobHandle != IntPtr.Zero) { CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
                    AttachToJobObject(_process);
                }

                _process.EnableRaisingEvents = true;
                var ccbPid = _process.Id;
                _process.Exited += (_, _) =>
                {
                    _ready = false;
                    try
                    {
                        var code = _process?.ExitCode ?? -1;
                        if (code == -1)
                            CoreLog.Info($"[CcbManager] CCB 已终止 (PID={ccbPid})");
                        else
                            CoreLog.Error($"[CcbManager] CCB 异常退出 (PID={ccbPid}, code={code})");
                    }
                    catch (Exception ex) { CoreLog.Info($"[CcbManager] 读取退出码异常 (PID={ccbPid}): {ex.GetType().Name}: {ex.Message}"); }
                };
                _process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (e.Data.Contains("就绪")) _ready = true;
                        CoreLog.Info($"[ccb] {e.Data}");
                    }
                };
                _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) CoreLog.Error($"[ccb] {e.Data}"); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                // 写 PID 文件，供进程残留清理
                WritePidFile(_process.Id);

                CoreLog.Info($"[CcbManager] 已启动 (PID={_process.Id}, port={_ccbPort})");
                return true;
            }
            catch (Exception ex) { CoreLog.Error($"[CcbManager] 启动异常: {ex.Message}"); return false; }
        }

        public async Task<bool> WaitReadyAsync(int waitMs = 15000)
        {
            var deadline = Environment.TickCount + waitMs;
            while (Environment.TickCount < deadline)
            {
                if (_ready) return true;
                if (_process != null && _process.HasExited) return false;
                await Task.Delay(500);
            }
            return _ready;
        }

        /// <summary>每帧检测进程崩溃，自动重拉</summary>
        public bool TickAndRestart()
        {
            if (_process == null || _process.HasExited)
            {
                if (_process != null)
                {
                    var exitCode = _process.ExitCode;
                    CoreLog.Error($"[CcbManager] 进程异常退出 (code={exitCode})，重启...");
                }
                Stop();
                WasRestarted = true;
                return Start();
            }
            return true;
        }

        public void Stop()
        {
            _ready = false;
            if (_process == null) return;
            try
            {
                if (_process.HasExited)
                {
                    CoreLog.Info($"[CcbManager] 进程已退出 (PID={_process.Id})，无需关闭");
                }
                else
                {
                    var pid = _process.Id;
                    CoreLog.Info($"[CcbManager] Kill CCB (PID={pid})");
                    _process.Kill();
                    _process.WaitForExit(5000);
                    CoreLog.Info($"[CcbManager] CCB 已关闭 (PID={pid})");
                }
            }
            catch (Exception ex) { CoreLog.Info($"[CcbManager] 关闭子进程异常: {ex.Message}"); }
            finally
            {
                _process.Dispose(); _process = null;
                if (_jobHandle != IntPtr.Zero) { CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
                DeletePidFile();
            }
        }

        public void Dispose() => Stop();

        /// <summary>按进程名扫描杀所有 CCB 残留进程（public static，可被 Harmony 等外部调用）</summary>
        public static void KillStaleProcesses()
        {
            try
            {
                var procs = Process.GetProcesses();
                int killed = 0;
                foreach (var proc in procs)
                {
                    try
                    {
                        var name = proc.ProcessName.ToLowerInvariant();
                        if (name != "node" && name != "node.exe") continue;
                        var fileName = proc.MainModule?.FileName ?? "";
                        if (fileName.IndexOf("cc-companion", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            CoreLog.Info($"[CcbManager] Kill CCB 残留进程 PID={proc.Id} path={fileName}");
                            proc.Kill();
                            proc.WaitForExit(3000);
                            killed++;
                        }
                    }
                    catch (Exception ex) { CoreLog.Info($"[CcbManager] 扫描进程 {proc.Id} 失败: {ex.Message}"); }
                    finally { proc.Dispose(); }
                }
                if (killed > 0) CoreLog.Info($"[CcbManager] 进程扫描完成: 已杀 {killed} 个残留 CCB");
            }
            catch (Exception ex) { CoreLog.Info($"[CcbManager] 进程扫描异常: {ex.Message}"); }
        }

        /// <summary>按 .pid 文件杀指定 companionDir 的 CCB 进程（public static）</summary>
        public static void KillStaleByPidFile(string companionDir)
        {
            var pidFile = Path.Combine(companionDir, ".pid");
            if (!File.Exists(pidFile)) return;

            try
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                CoreLog.Info($"[CcbManager] 发现残留 PID 文件: {pidFile} (PID={pidText})");
                if (int.TryParse(pidText, out int pid))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        if (!IsNodeProcess(proc)) return;
                        CoreLog.Info($"[CcbManager] 杀死残留进程 PID={pid}");
                        proc.Kill(); proc.WaitForExit(3000);
                    }
                    catch (ArgumentException) { /* 进程已不存在 */ }
                }
            }
            catch (Exception ex) { CoreLog.Error($"[CcbManager] PID 清理失败: {ex.Message}"); }
            finally { try { File.Delete(pidFile); } catch (Exception ex) { CoreLog.Info($"[CcbManager] 删除 PID 文件失败: {ex.Message}"); } }
        }

        private void WritePidFile(int pid)
        {
            try { File.WriteAllText(Path.Combine(_companionDir, ".pid"), pid.ToString()); }
            catch (Exception ex) { CoreLog.Info($"[CcbManager] 写 PID 文件失败: {ex.Message}"); }
        }

        private void DeletePidFile()
        {
            try { File.Delete(Path.Combine(_companionDir, ".pid")); }
            catch (Exception ex) { CoreLog.Info($"[CcbManager] 删除 PID 文件失败: {ex.Message}"); }
        }

        private static bool IsNodeProcess(Process proc)
        {
            var name = proc.ProcessName.ToLowerInvariant();
            if (name != "node" && name != "node.exe") return false;
            try { return (proc.MainModule?.FileName ?? "").IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch (Exception ex) { CoreLog.Info($"[CcbManager] 无法读取进程模块信息 PID={proc.Id}: {ex.Message}"); return false; }
        }

        // ========== Windows JobObject ==========

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        private void AttachToJobObject(Process proc)
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ref info, size);
            AssignProcessToJobObject(_jobHandle, proc.Handle);
        }
    }
}
