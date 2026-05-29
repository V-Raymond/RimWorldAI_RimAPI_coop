using System;
using RimWorldMCP.MapRendering;
using RimWorldMCP.Tools;

namespace RimWorldMCP
{
    public static class McpServiceManager
    {
        private static SimpleMspServer.McpServiceHost? _host;

        public static ToolRegistry? ToolRegistry { get; private set; }
        public static bool IsRunning => _host?.IsRunning ?? false;

        private const int DefaultPort = 9877;
        private const string DefaultHost = "localhost";

        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);

                SymbolDictionary.Initialize();

                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry);
                ToolRegistry = toolRegistry;

                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;

                _host = new SimpleMspServer.McpServiceHost(port, host);
                _host.RegisterProvider(toolRegistry);
                _host.Start();

                McpLog.Info($"MCP 服务已启动: http://{host}:{port}");
            }
            catch (Exception ex)
            {
                _host?.Dispose(); _host = null;
                throw new Exception($"MCP 服务启动失败: {ex.Message}", ex);
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
            foreach (var type in typeof(ToolRegistry).Assembly.GetTypes())
            {
                if (!typeof(ITool).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
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
        }
    }
}
