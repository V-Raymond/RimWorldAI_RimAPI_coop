using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：内部 Tool → 本地处理，外部 Tool → 转发 MCP。</summary>
    public static class ToolDispatcher
    {
        public static async Task HandleAsync(
            CcbWebSocket ccbWs, McpClient mcp,
            string toolId, string toolName, JsonElement? input,
            Action<string> log)
        {
            var sw = Stopwatch.StartNew();

            // 内部 Tool → 直接本地处理
            if (InternalToolRegistry.Instance.IsInternal(toolName))
            {
                try
                {
                    log($"工具调用: {toolName}");
                    var (result, shouldExit) = await InternalToolRegistry.Instance.ExecuteInternalAsync(toolName, input);
                    sw.Stop();
                    log($"工具完成: {toolName} 用时 {sw.ElapsedMilliseconds}ms");
                    await ccbWs.SendToolResult(toolId, result);

                    // 原地切换：暂停游戏 + 切换角色，但不立即发送新 prompt。
                    // 新 prompt 由主循环 TickAsync 检测 PendingSessionStart 后通过 RunAgent 发送。
                    if (!string.IsNullOrEmpty(AgentOrchestrator.NextAgentRequest))
                    {
                        var target = AgentOrchestrator.NextAgentRequest;
                        AgentOrchestrator.NextAgentRequest = null;
                        await AgentOrchestrator.PauseAndSwitchRole(target);
                        Interlocked.Increment(ref AgentLoop.SwitchCount);
                        InternalToolRegistry.RequestExit();
                        log($"原地切换 → {target}，游戏已暂停，等待主循环启动新会话 (SwitchCount={AgentLoop.SwitchCount})");
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.GetType().Name}: {ex.Message}");
                    await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}", true);
                }
                return;
            }

            // 外部 Tool → 转发 MCP
            try
            {
                log($"工具调用: {toolName}");
                var args = input != null
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.Value.GetRawText())
                    : null;
                var result = await mcp.CallTool(toolName, args);
                sw.Stop();
                log($"工具完成: {toolName} 用时 {sw.ElapsedMilliseconds}ms");
                await ccbWs.SendToolResult(toolId, result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.Message}");
                await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}", true);
            }
        }
    }
}
