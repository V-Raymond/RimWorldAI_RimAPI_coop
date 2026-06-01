using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// 纯内存会话存储 — MOD 模式使用。
    /// 游戏重载即清空。
    /// </summary>
    public sealed class MemoryConversationStore : IConversationStore
    {
        private readonly List<ConversationEntry> _entries = new();
        private readonly object _lock = new();
        private long _nextId = 1;

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        public void RecordUserMessage(string text)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.User,
                Text = text ?? "",
                Timestamp = DateTime.UtcNow
            };
            lock (_lock) _entries.Add(entry);
        }

        public void RecordAssistantMessage(string text, string thinking, string runId, string agentType)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.Assistant,
                Text = text ?? "",
                Thinking = thinking ?? "",
                RunId = runId ?? "",
                AgentType = agentType ?? "",
                Timestamp = DateTime.UtcNow
            };
            lock (_lock) _entries.Add(entry);
        }

        public void RecordSystemMessage(string text)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.System,
                Text = text ?? "",
                Timestamp = DateTime.UtcNow
            };
            lock (_lock) _entries.Add(entry);
        }

        public ConversationEntry? GetAt(long id)
        {
            lock (_lock)
            {
                return _entries.FirstOrDefault(e => e.Id == id);
            }
        }

        public IReadOnlyList<ConversationEntry> GetRecent(int n)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _entries.Count - n);
                return _entries.Skip(start).ToList();
            }
        }

        private long GetNextId()
        {
            lock (_lock) return _nextId++;
        }
    }
}
