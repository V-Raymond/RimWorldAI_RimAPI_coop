using System;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP
{
    /// <summary>OpenClaw Gateway 连接生命周期管理 — 独立于 MCP Server</summary>
    public static class BridgeLifecycle
    {
        private static int _connectionCheckInterval = 60; // 启动后等 1 秒再发 prompt

        public static async Task StartAsync()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null || settings.BridgeType == 0 || string.IsNullOrEmpty(settings.BridgeUrl))
                return;

            await GatewayClient.Connect(settings.BridgeUrl, settings.BridgeToken, settings.BridgePassword);
            if (GatewayClient.IsConnected)
            {
                McpLog.Info($"[bridge] 已连接到 {McpModSettings.BridgeTypeLabels[settings.BridgeType]}: {settings.BridgeUrl}");
                _connectionCheckInterval = 120; // 连接后等 2 秒
            }
        }

        public static void Tick()
        {
            GatewayMessageQueue.Tick();
            GatewayEventMonitor.Tick();

            // 连接就绪后首次会话发送 Prompt
            if (GatewayClient.IsReady
                && !GatewayMessageQueue.WasSessionPromptSent
                && --_connectionCheckInterval <= 0)
            {
                SendSessionPrompt();
            }
        }

        public static void Stop()
        {
            GatewayMessageQueue.Reset();
            GatewayClient.Disconnect();
        }

        private static void SendSessionPrompt()
        {
            GatewayMessageQueue.MarkSessionPromptSent();
            var prompt = LoadPromptFile();
            if (string.IsNullOrEmpty(prompt)) return;

            McpLog.Info("[bridge] 发送会话 Prompt");
            GatewayMessageQueue.SendNow(MessageCategory.SessionInit, prompt);
        }

        private static string LoadPromptFile()
        {
            try
            {
                var path = FindPromptPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    McpLog.Info($"[bridge] Prompt 文件不存在: {path}");
                    return "";
                }
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[bridge] 读取 Prompt 失败: {ex.Message}");
                return "";
            }
        }

        private static string FindPromptPath()
        {
            try
            {
                var asmPath = typeof(BridgeLifecycle).Assembly.Location;
                if (string.IsNullOrEmpty(asmPath)) return "";

                var asmDir = Path.GetDirectoryName(asmPath);
                if (asmDir == null) return "";

                // Assemblies/ → ../.. = mod root
                var modRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                return Path.Combine(modRoot, "Prompt.md");
            }
            catch
            {
                return "";
            }
        }
    }
}
