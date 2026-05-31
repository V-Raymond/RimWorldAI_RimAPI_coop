using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_EnterAct : IInternalTool
    {
        public string Name => "enter_act";
        public string Description => "进入 Act 阶段，恢复游戏执行操作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                speed = new { type = "string", description = "游戏速度: paused, normal, fast, superfast, ultrafast" }
            },
            required = new[] { "speed" }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var speed = "superfast";
            if (args?.TryGetProperty("speed", out var speedEl) == true)
                speed = speedEl.GetString() ?? "superfast";

            AgentOrchestrator.EnterActPhase();
            var pace = AgentOrchestrator.PaceController;
            var mcp = AgentOrchestrator.SessionMcp;
            if (pace == null || mcp == null)
                return ($"进入 Act 阶段失败: {(pace == null ? "GamePaceController" : "McpClient")} 不可用，Agent 会话可能已结束", false);

            await pace.ResumeForAction(mcp, speed);
            return ($"已进入 Act 阶段，游戏速度: {speed}。", false);
        }
    }
}
