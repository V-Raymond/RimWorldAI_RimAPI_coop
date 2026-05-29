using System;
using Verse;

namespace RimWorldMCP.Harmony
{
    /// <summary>MCP 服务启动 Hook — 独立于游戏事件拦截</summary>
    [StaticConstructorOnStartup]
    public static class Hook_McpBootstrap
    {
        static Hook_McpBootstrap()
        {
            try
            {
                Log.Message("[RimWorldMCP] 正在启动 MCP 服务...");
                McpServiceManager.Start();
                Log.Message("[RimWorldMCP] MCP 服务启动成功");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimWorldMCP] MCP 服务启动失败: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
