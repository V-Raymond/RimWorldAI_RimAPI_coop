using System;
using System.Linq;
using System.Reflection;
using RimWorldMCP.MapRendering;
using RimWorldMCP.Tools;
using Verse;

namespace RimWorldMCP
{
    public static class McpServiceManager
    {
        private static SimpleMspServer.McpServiceHost? _host;

        public static ToolRegistry? ToolRegistry { get; private set; }
        public static bool IsRunning => _host?.IsRunning ?? false;

        private const int DefaultPort = 9877;
        private const string DefaultHost = "0.0.0.0";

        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                Verse.Log.Message("[RimWorldMCP] Step 1/5: OSS 配置 + 符号字典...");
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);
                SymbolDictionary.Initialize();

                Verse.Log.Message("[RimWorldMCP] Step 2/5: 创建 ToolRegistry + 注册全部 Tool...");
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry);
                ToolRegistry = toolRegistry;

                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                Verse.Log.Message($"[RimWorldMCP] Step 4/5: 创建 McpServiceHost + 注册 ToolRegistry IToolProvider (host={host}, port={port})...");
                _host = new SimpleMspServer.McpServiceHost(port, host,
                    new SimpleMspServer.DelegateMspLog(Verse.Log.Message));
                _host.RegisterProvider(toolRegistry);

                Verse.Log.Message("[RimWorldMCP] Step 5/5: 启动 HTTP 监听...");
                _host.Start();

                Verse.Log.Message($"[RimWorldMCP] MCP 服务已启动: http://{host}:{port}");
            }
            catch (Exception ex)
            {
                _host?.Dispose(); _host = null;
                Verse.Log.Error($"[RimWorldMCP] MCP 服务启动失败: {ex}");
                McpLog.Error($"MCP 服务启动失败: {ex.Message}");
            }
        }

        public static void Stop()
        {
            _host?.Dispose();
            _host = null;
            ToolRegistry = null;
        }

        public static void RefreshTools()
        {
            if (ToolRegistry == null) return;
            RegisterAllTools(ToolRegistry);
            McpLog.Info("Tool 注册表已刷新");
        }

        private static void RegisterAllTools(ToolRegistry registry)
        {
            try
            {
                var asm = typeof(ToolRegistry).Assembly;
                Verse.Log.Message($"[RimWorldMCP] 扫描程序集: {asm.FullName} ({asm.Location})");

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                    Verse.Log.Message($"[RimWorldMCP] GetTypes() 返回 {types.Length} 个类型");
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Verse.Log.Error($"[RimWorldMCP] GetTypes() 失败: {ex.Message}");
                    foreach (var le in ex.LoaderExceptions)
                    {
                        if (le != null) Verse.Log.Error($"[RimWorldMCP]   LoaderException: {le.Message}");
                    }
                    return;
                }

                foreach (var type in types)
                {
                    if (type.IsInterface || type.IsAbstract)
                        continue;
                    if (!typeof(ITool).IsAssignableFrom(type))
                        continue;

                    McpLog.Info($"注册工具: {type.Name}");
                    try
                    {
                        var tool = (ITool)Activator.CreateInstance(type);
                        if (tool != null)
                        {
                            if (tool is IHasAvailability hasAvail && !hasAvail.IsAvailable)
                            {
                                McpLog.Info($"跳过不可用工具: {type.Name}");
                                continue;
                            }
                            registry.Register(tool);
                        }
                    }
                    catch (Exception ex) { McpLog.Warn($"注册失败 {type.Name}: {ex.Message}"); }
                }

                Verse.Log.Message($"[RimWorldMCP] 共注册 {registry.AllTools.Count} 个工具");
            }
            catch (Exception ex)
            {
                Verse.Log.Error($"[RimWorldMCP] RegisterAllTools 异常: {ex}");
            }
        }
    }
}
