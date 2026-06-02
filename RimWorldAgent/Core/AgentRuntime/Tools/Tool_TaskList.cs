using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>列出所有任务及其状态（替代 SDK 原生 TaskList，实现结构化数据拦截）</summary>
    public class Tool_TaskList : IInternalTool
    {
        public string Name => "task_list";
        public string Description => "列出所有任务及其状态。用于查看当前任务进度。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var all = TaskStore.GetAll();
            if (all.Count == 0)
                return Task.FromResult(("当前没有任务。使用 task_create 创建新任务。", false));

            var sb = new StringBuilder();
            sb.AppendLine($"共 {all.Count} 个任务：");
            sb.AppendLine();

            foreach (var t in all)
            {
                var statusIcon = t.Status switch
                {
                    "pending" => "[ ]",
                    "in_progress" => "[>]",
                    "completed" => "[✓]",
                    _ => "[?]"
                };
                sb.AppendLine($"{statusIcon} #{t.Id} {t.Subject} ({t.Status})");
                if (t.Blocks.Count > 0)
                    sb.AppendLine($"    阻塞: {string.Join(", ", t.Blocks.Select(b => $"#{b}"))}");
                if (t.BlockedBy.Count > 0)
                    sb.AppendLine($"    被阻塞: {string.Join(", ", t.BlockedBy.Select(b => $"#{b}"))}");
            }

            var pending = TaskStore.PendingCount;
            if (pending > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{pending} 个未完成。完成的任务请用 task_update(taskId=\"{all[0].Id}\", status=\"completed\") 标记。");
            }

            return Task.FromResult((sb.ToString(), false));
        }
    }
}
