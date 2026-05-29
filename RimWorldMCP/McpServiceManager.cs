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
        private const string DefaultHost = "0.0.0.0";

        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                Verse.Log.Message("[RimWorldMCP] Step 1/6: OSS 配置...");
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);

                Verse.Log.Message("[RimWorldMCP] Step 2/6: 符号字典初始化...");
                SymbolDictionary.Initialize();

                Verse.Log.Message("[RimWorldMCP] Step 3/6: 创建 ToolRegistry...");
                var toolRegistry = new ToolRegistry();

                Verse.Log.Message("[RimWorldMCP] Step 4/6: 注册全部 Tool...");
                RegisterAllTools(toolRegistry);
                ToolRegistry = toolRegistry;

                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                Verse.Log.Message($"[RimWorldMCP] Step 5/6: 创建 McpServiceHost (host={host}, port={port})...");
                _host = new SimpleMspServer.McpServiceHost(port, host);
                _host.RegisterProvider(toolRegistry);

                Verse.Log.Message("[RimWorldMCP] Step 6/6: 启动 HTTP 监听...");
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
