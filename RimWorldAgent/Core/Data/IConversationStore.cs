using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// 会话历史存储抽象。
    /// 所有实现必须线程安全（支持并发读写）。
    /// </summary>
    public interface IConversationStore
    {
        /// <summary>已存储条目总数</summary>
        int Count { get; }

        /// <summary>记录用户消息</summary>
        void RecordUserMessage(string text);

        /// <summary>记录 AI 回复（完整 text + thinking）</summary>
        void RecordAssistantMessage(string text, string thinking, string runId, string agentType);

        /// <summary>记录系统消息（暂停提醒、错误等）</summary>
        void RecordSystemMessage(string text);

        /// <summary>按主键 ID 精确查询，不存在返回 null</summary>
        ConversationEntry? GetAt(long id);

        /// <summary>获取最近 n 条（按时间升序）</summary>
        IReadOnlyList<ConversationEntry> GetRecent(int n);
    }
}
