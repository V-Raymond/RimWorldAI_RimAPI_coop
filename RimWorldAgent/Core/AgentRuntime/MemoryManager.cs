using System.Collections.Generic;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>记忆管理器 — 数据存储委托给 Core.Data.MemoryStore。</summary>
    public static class MemoryManager
    {
        private const string Key = "commander";

        public static string GetMemoryText(string agentName)
            => MemoryStore.GetMemoryText(Key);

        public static void Append(string agentName, MemoryEntry entry)
            => MemoryStore.Append(Key, entry);

        public static void ReplaceAll(string agentName, List<MemoryEntry> entries)
            => MemoryStore.ReplaceAll(Key, entries);
    }
}
