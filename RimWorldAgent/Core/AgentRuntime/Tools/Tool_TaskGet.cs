using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>获取指定任务的详细信息（替代 SDK 原生 TaskGet，实现结构化数据拦截）</summary>
    public class Tool_TaskGet : IInternalTool
    {
        public string Name => "task_get";
        public string Description => "获取指定任务的详细信息，包括描述、依赖关系等。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                taskId = new { type = "string", description = "要查询的任务 ID" }
            },
            required = new[] { "taskId" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(("缺少参数：需要 taskId。", false));

            var taskId = args.Value.GetProperty("taskId").GetString()!;
            var item = TaskStore.Get(taskId);

            if (item == null)
                return Task.FromResult(($"未找到任务 #{taskId}。使用 task_list 查看所有任务。", false));

            var sb = new StringBuilder();
            sb.AppendLine($"任务 #{item.Id}");
            sb.AppendLine($"标题: {item.Subject}");
            sb.AppendLine($"状态: {item.Status}");
            sb.AppendLine($"描述: {item.Description}");
            if (!string.IsNullOrEmpty(item.ActiveForm))
                sb.AppendLine($"进行中描述: {item.ActiveForm}");
            if (!string.IsNullOrEmpty(item.Owner))
                sb.AppendLine($"负责人: {item.Owner}");
            if (item.Blocks.Count > 0)
                sb.AppendLine($"阻塞任务: {string.Join(", ", item.Blocks.Select(b => $"#{b}"))}");
            if (item.BlockedBy.Count > 0)
                sb.AppendLine($"被阻塞: {string.Join(", ", item.BlockedBy.Select(b => $"#{b}"))}");

            return Task.FromResult((sb.ToString(), false));
        }
    }
}
