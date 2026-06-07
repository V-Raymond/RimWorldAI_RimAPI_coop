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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        /// <summary>在首次 SQLite 调用前调用，将 Native\{rid}\ 追加到 DLL 搜索路径</summary>
        /// <remarks>
        /// Windows: SetDllDirectory 在进程启动目录后追加一条搜索路径，不影响系统目录 / PATH 等。
        /// Unix: 追加 LD_LIBRARY_PATH / DYLD_LIBRARY_PATH（动态链接器运行时读取）。
        /// </remarks>
        public static void Setup(string asmDir)
        {
            if (_initialized) return;
            _initialized = true;

            var plat = Environment.OSVersion.Platform;
            var nativeDir = GetNativeDir(asmDir, plat);

            if (!Directory.Exists(nativeDir))
            {
                CoreLog.Warn($"[NativeResolver] Native 目录缺失: {nativeDir}");
                return;
            }

            try
            {
                if (plat == PlatformID.Win32NT)
                {
                    if (SetDllDirectoryW(nativeDir))
                        CoreLog.Info($"[NativeResolver] SetDllDirectory → {nativeDir}");
                    else
                        CoreLog.Warn($"[NativeResolver] SetDllDirectory 失败 (err={Marshal.GetLastWin32Error()})");
                }
                else
                {
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

        private static string GetNativeDir(string asmDir, PlatformID plat)
        {
            var rid = plat == PlatformID.Win32NT
                ? (IntPtr.Size == 8 ? "win-x64" : "win-x86")
                : plat == PlatformID.MacOSX ? "osx-x64" : "linux-x64";
            return Path.GetFullPath(Path.Combine(asmDir, "Native", rid));
        }
    }
}
