using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>更新任务状态或字段（替代 SDK 原生 TaskUpdate，实现结构化数据拦截）</summary>
    public class Tool_TaskUpdate : IInternalTool
    {
        public string Name => "task_update";
        public string Description => "更新任务状态或字段。支持修改标题、描述、状态、设置依赖关系等。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                taskId = new { type = "string", description = "要更新的任务 ID（来自 task_create 或 task_list）" },
                subject = new { type = "string", description = "新标题（可选）" },
                description = new { type = "string", description = "新描述（可选）" },
                activeForm = new { type = "string", description = "新的进行中描述文本（可选）" },
                status = new { type = "string", description = "新状态: pending（待处理）, in_progress（进行中）, completed（已完成）, deleted（删除）" },
                addBlocks = new { type = "array", items = new { type = "string" }, description = "此任务阻塞的其他任务 ID 列表（可选）" },
                addBlockedBy = new { type = "array", items = new { type = "string" }, description = "阻塞此任务的任务 ID 列表（可选）" },
                owner = new { type = "string", description = "任务负责人（可选）" },
                metadata = new { type = "object", description = "自定义元数据（可选，与已有元数据合并）" }
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

            // 提取可选参数
            var subject = args.Value.TryGetProperty("subject", out var s) && s.ValueKind != JsonValueKind.Null ? s.GetString() : null;
            var description = args.Value.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() : null;
            var activeForm = args.Value.TryGetProperty("activeForm", out var af) && af.ValueKind != JsonValueKind.Null ? af.GetString() : null;
            var status = args.Value.TryGetProperty("status", out var st) && st.ValueKind != JsonValueKind.Null ? st.GetString() : null;
            var owner = args.Value.TryGetProperty("owner", out var o) && o.ValueKind != JsonValueKind.Null ? o.GetString() : null;

            List<string>? addBlocks = null;
            if (args.Value.TryGetProperty("addBlocks", out var ab) && ab.ValueKind == JsonValueKind.Array)
                addBlocks = ab.EnumerateArray().Select(e => e.GetString()!).Where(x => x != null).ToList();

            List<string>? addBlockedBy = null;
            if (args.Value.TryGetProperty("addBlockedBy", out var abb) && abb.ValueKind == JsonValueKind.Array)
                addBlockedBy = abb.EnumerateArray().Select(e => e.GetString()!).Where(x => x != null).ToList();

            Dictionary<string, JsonElement>? metadata = null;
            if (args.Value.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                metadata = new Dictionary<string, JsonElement>();
                foreach (var kv in m.EnumerateObject())
                    metadata[kv.Name] = kv.Value.Clone();
            }

            var wasDeleted = status == "deleted";
            var updated = TaskStore.Update(taskId, subject, description, activeForm, status,
                addBlocks, addBlockedBy, owner, metadata);

            if (wasDeleted)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"已删除任务 #{taskId}");
                sb.AppendLine($"标题: {item.Subject}");
                sb.AppendLine($"当前共 {TaskStore.PendingCount} 个未完成任务。");
                return Task.FromResult((sb.ToString(), false));
            }

            // 构建更新摘要
            var changes = new List<string>();
            if (subject != null) changes.Add($"标题 → \"{subject}\"");
            if (description != null) changes.Add("描述已更新");
            if (status != null) changes.Add($"状态 → {status}");
            if (owner != null) changes.Add($"负责人 → {owner}");
            if (addBlocks?.Count > 0) changes.Add($"阻塞任务 +{addBlocks.Count}");
            if (addBlockedBy?.Count > 0) changes.Add($"被阻塞 +{addBlockedBy.Count}");

            var result = new StringBuilder();
            result.AppendLine($"已更新任务 #{taskId}");
            result.AppendLine($"标题: {updated!.Subject}");
            result.AppendLine($"状态: {updated.Status}");
            if (changes.Count > 0)
            {
                result.AppendLine();
                result.AppendLine("变更:");
                foreach (var c in changes) result.AppendLine($"  - {c}");
            }
            result.AppendLine();
            result.AppendLine($"当前共 {TaskStore.PendingCount} 个未完成任务。");

            return Task.FromResult((result.ToString(), false));
        }
    }
}
