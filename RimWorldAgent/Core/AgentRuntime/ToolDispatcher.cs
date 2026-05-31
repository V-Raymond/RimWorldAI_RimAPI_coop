using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：内部 Tool → 本地处理，外部 Tool → 转发 MCP。</summary>
    public static class ToolDispatcher
    {
        private static int _toolCallCount;
        private const int TodoRemindInterval = 5;

        public static int ActPauseRemindThreshold = 5;
        private static int _actPauseCheckCount;

        public static void ResetActPauseCount() => _actPauseCheckCount = 0;

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
                    var suffix = BuildModeSuffix();
                    await ccbWs.SendToolResult(toolId, result + suffix);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.GetType().Name}: {ex.Message}");
                    await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}{BuildModeSuffix()}", true);
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
                var suffix = BuildModeSuffix();
                await ccbWs.SendToolResult(toolId, result + suffix);
            }
            catch (Exception ex)
            {
                sw.Stop();
                log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.Message}");
                await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}{BuildModeSuffix()}", true);
            }
        }

        private static string BuildModeSuffix()
        {
            var phase = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "PLAN",
                GamePhase.Act => "ACT",
                _ => "就绪"
            };

            _toolCallCount++;

            var todoRemind = "";
            if (_toolCallCount % TodoRemindInterval == 0)
            {
                todoRemind = "\n\n<system-reminder>\n请检查 TODO 列表：用 todo_query 查看当前任务，完成的用 todo_set_status 标记为 done，新任务用 todo_add 添加。不再需要的用 todo_delete 删除。\n</system-reminder>";
            }

            // ACT 阶段暂停过久提醒
            var actPauseRemind = "";
            if (AgentOrchestrator.CurrentPhase == GamePhase.Act
                && AgentOrchestrator.PaceController?.IsPaused == true)
            {
                _actPauseCheckCount++;
                if (_actPauseCheckCount > ActPauseRemindThreshold)
                {
                    actPauseRemind = "\n\n<system-reminder>\n游戏仍处于暂停状态！你在 ACT 阶段，只有恢复游戏速度后才能执行实际操作。请调用 enter_act(speed=\"superfast\") 恢复游戏。\n</system-reminder>";
                }
            }
            else { _actPauseCheckCount = 0; }

            return $"\n\n---\n当前模式: {phase}{todoRemind}{actPauseRemind}";
        }
    }
}
