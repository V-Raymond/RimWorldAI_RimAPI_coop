using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>创建新任务（替代 SDK 原生 TaskCreate，实现结构化数据拦截）</summary>
    public class Tool_TaskCreate : IInternalTool
    {
        public string Name => "task_create";
        public string Description => "创建新任务跟踪执行进度。建议在开始复杂多步骤工作时使用。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                subject = new { type = "string", description = "任务标题，简洁明了（如'修复登录 Bug'）" },
                description = new { type = "string", description = "任务详细描述，说明要做什么" },
                activeForm = new { type = "string", description = "进行中时的描述文本（如'正在修复登录 Bug'），不提供则用 subject" },
                metadata = new { type = "object", description = "自定义元数据（可选）" }
            },
            required = new[] { "subject", "description" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(("缺少参数：需要 subject, description。", false));

            var subject = args.Value.GetProperty("subject").GetString()!;
            var description = args.Value.GetProperty("description").GetString()!;
            var activeForm = args.Value.TryGetProperty("activeForm", out var af) && af.ValueKind != JsonValueKind.Null
                ? af.GetString() : null;
            Dictionary<string, JsonElement>? metadata = null;
            if (args.Value.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                metadata = new Dictionary<string, JsonElement>();
                foreach (var kv in m.EnumerateObject())
                    metadata[kv.Name] = kv.Value.Clone();
            }

            var item = TaskStore.Create(subject, description, activeForm, metadata);

            var sb = new StringBuilder();
            sb.AppendLine($"已创建任务 #{item.Id}");
            sb.AppendLine($"标题: {item.Subject}");
            sb.AppendLine($"状态: {item.Status}");
            if (!string.IsNullOrEmpty(item.Description))
                sb.AppendLine($"描述: {item.Description}");

            var pending = TaskStore.PendingCount;
            sb.AppendLine();
            sb.AppendLine($"当前共 {pending} 个未完成任务。");

            return Task.FromResult((sb.ToString(), false));
        }
    }
}
