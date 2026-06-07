using System;
using System.IO;
using System.Runtime.InteropServices;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core
{
    /// <summary>SQLitePCLRaw 原生 DLL 搜索路径配置</summary>
    public static class NativeResolver
    {
        private static bool _initialized;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>在首次 SQLite 调用前调用，将 Native\{rid}\ 加入 P/Invoke 搜索路径</summary>
        public static void Setup(string modulesDir)
        {
            if (_initialized) return;
            _initialized = true;

            var rid = GetRuntimeId();
            var nativeDir = Path.GetFullPath(Path.Combine(modulesDir, "Native", rid));

            if (!Directory.Exists(nativeDir))
            {
                CoreLog.Warn($"[NativeResolver] Native 目录缺失: {nativeDir}");
                return;
            }

            try
            {
                var plat = Environment.OSVersion.Platform;
                if (plat == PlatformID.Win32NT)
                {
                    if (SetDllDirectory(nativeDir))
                        CoreLog.Info($"[NativeResolver] SetDllDirectory → {nativeDir}");
                    else
                        CoreLog.Warn($"[NativeResolver] SetDllDirectory 失败 (err={Marshal.GetLastWin32Error()})");
                }
                else
                {
                    // Unix: LD_LIBRARY_PATH (Linux) / DYLD_LIBRARY_PATH (macOS)
                    var key = plat == PlatformID.MacOSX ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
                    var existing = Environment.GetEnvironmentVariable(key) ?? "";
                    Environment.SetEnvironmentVariable(key,
                        string.IsNullOrEmpty(existing) ? nativeDir : nativeDir + ":" + existing);
                    CoreLog.Info($"[NativeResolver] {key} += {nativeDir}");
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[NativeResolver] 配置失败: {ex.Message}");
            }
        }

        private static string GetRuntimeId()
        {
            var plat = Environment.OSVersion.Platform;
            if (plat == PlatformID.Win32NT)
                return IntPtr.Size == 8 ? "win-x64" : "win-x86";
            if (plat == PlatformID.MacOSX)
                return "osx-x64";
            return "linux-x64";
        }
    }
}
