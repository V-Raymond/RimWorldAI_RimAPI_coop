using System;
using System.Text.Json.Serialization;

namespace RimWorldAgent.Core.Data
{
    /// <summary>会话角色</summary>
    public enum ConvRole
    {
        User,
        Assistant,
        System
    }

    /// <summary>
    /// 单条会话记录 — 独立于 ChatDisplayState/ChatEntry，专用于持久化。
    /// </summary>
    public class ConversationEntry
    {
        /// <summary>SQLite 自增主键（0 = 未持久化）</summary>
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("role")]
        public ConvRole Role { get; set; }

        /// <summary>完整消息文本（可能为空，如纯思考消息）</summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        /// <summary>完整思考文本（无则为空）</summary>
        [JsonPropertyName("thinking")]
        public string Thinking { get; set; } = "";

        /// <summary>SDK 消息 UUID（用于前端去重）</summary>
        [JsonPropertyName("run_id")]
        public string RunId { get; set; } = "";

        /// <summary>子 Agent 类型（空串 = 主 Agent）</summary>
        [JsonPropertyName("agent_type")]
        public string AgentType { get; set; } = "";

        /// <summary>UTC 时间戳</summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
