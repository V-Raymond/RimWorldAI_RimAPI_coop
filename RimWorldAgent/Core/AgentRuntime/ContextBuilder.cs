using System.Text;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>为每个 Agent 构建 Prompt。固定段落顺序保证 Prompt Cache 命中。</summary>
    public class ContextBuilder
    {
        private readonly Mcp.McpClient _mcp;

        public ContextBuilder(Mcp.McpClient mcp) { _mcp = mcp; }

        /// <summary>构建 Agent 完整 Prompt。</summary>
        public async Task<string> BuildAsync(AgentConfig config)
        {
            var sb = new StringBuilder();

            // Layer 1: System Prompt (cached)
            sb.AppendLine(config.SystemPrompt.Trim());
            sb.AppendLine();

            // Layer 2: Memory (cached)
            var memory = MemoryManager.GetMemoryText(config.Name);
            if (!string.IsNullOrEmpty(memory)) { sb.AppendLine(memory); sb.AppendLine(); }

            // Layer 3: World Summary (via MCP get_world_summary)
            sb.AppendLine(await BuildWorldSummaryAsync());
            sb.AppendLine();

            // Layer 4: Active Alerts (from AgentOrchestrator, populated by SSE event routing)
            var alerts = AgentOrchestrator.DrainEvents(config.Name);
            sb.AppendLine(string.IsNullOrEmpty(alerts) ? "## 最近事件\n（无）\n" : alerts);
            sb.AppendLine();

            // Layer 5: TaskBoard
            sb.AppendLine(TaskBoard.ToMarkdown());
            sb.AppendLine();

            // Layer 5.5: Advice from other Agents
            var advices = AgentOrchestrator.DrainAdvices(config.Name);
            if (advices.Count > 0)
            {
                sb.AppendLine("## 来自其他 Agent 的建议");
                foreach (var a in advices) sb.AppendLine($"- {a}");
                sb.AppendLine();
            }

            // Layer 6: Runtime info
            sb.AppendLine("## 运行信息");
            sb.AppendLine($"- Load: {Scheduler.LoadScore} ({Scheduler.Mode})");
            sb.AppendLine($"- Day: {AgentOrchestrator.GameDay}");
            sb.AppendLine($"- 阶段: {AgentOrchestrator.CurrentPhase}");
            sb.AppendLine($"- 可见工具: {config.ToolCategories.Count} 类");

            // Layer 7: 当前模式指引
            var phaseHint = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "## 当前模式\n游戏已暂停，处于 **PLAN 模式**。请直接制定计划，无需调用 `enter_plan()`。\n计划完成后调用 `enter_act()` 进入 ACT 模式执行，工作全部完成后调用 `switch_agent(\"overseer\")` 回到总督。",
                GamePhase.Act => "## 当前模式\n处于 **ACT 模式**，请执行计划。完成后调用 `switch_agent(\"overseer\")` 回到总督。",
                _ => null
            };
            if (phaseHint != null)
            {
                sb.AppendLine();
                sb.AppendLine(phaseHint);
            }

            return sb.ToString().TrimEnd();
        }

        private async Task<string> BuildWorldSummaryAsync()
        {
            try
            {
                var result = await _mcp.CallTool("get_world_summary");
                return result.Length > 0 ? result : "## 殖民地状态\n（无可用地图）";
            }
            catch
            {
                return "## 殖民地状态\n（MCP 连接不可用）";
            }
        }
    }
}
