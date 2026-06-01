using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// ConversationEntry → 前端 JSON 格式转换。
    /// 输出格式与 index.html addMsgSimple / handleMessage('history_response') 对齐。
    /// </summary>
    public static class UiHistoryFormatter
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>将条目列表格式化为 WS history_response JSON 字符串</summary>
        public static string FormatResponse(IReadOnlyList<ConversationEntry> entries)
        {
            var doc = BuildResponseDocument(entries);
            return JsonSerializer.Serialize(doc, Options);
        }

        private static object BuildResponseDocument(IReadOnlyList<ConversationEntry> entries)
        {
            var messages = new List<object>(entries.Count);
            foreach (var entry in entries)
            {
                messages.Add(FormatEntry(entry));
            }
            return new { type = "history_response", messages };
        }

        private static object FormatEntry(ConversationEntry entry)
        {
            var contentType = entry.Role switch
            {
                ConvRole.User => "user",
                ConvRole.Assistant => "assistant",
                ConvRole.System => "assistant",
                _ => "assistant"
            };

            // 构建 content 数组
            var content = new List<object>(2);
            if (!string.IsNullOrEmpty(entry.Thinking))
                content.Add(new { type = "thinking", thinking = entry.Thinking });
            if (!string.IsNullOrEmpty(entry.Text))
                content.Add(new { type = "text", text = entry.Text });

            return new
            {
                type = contentType,
                uuid = string.IsNullOrEmpty(entry.RunId) ? $"msg_{entry.Id}" : entry.RunId,
                agent_type = entry.AgentType ?? "",
                message = new { content }
            };
        }
    }
}
