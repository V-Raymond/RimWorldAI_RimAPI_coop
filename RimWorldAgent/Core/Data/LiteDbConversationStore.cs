using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// LiteDB 持久化会话存储。
    /// 纯 C# 托管，零原生依赖，跨平台。
    /// </summary>
    public sealed class LiteDbConversationStore : IConversationStore, IDisposable
    {
        private readonly LiteDatabase _db;
        private readonly string _saveId;
        private readonly object _writeLock = new();
        private bool _disposed;

        /// <param name="filePath">LiteDB 文件路径，如 .../conversation.db</param>
        /// <param name="saveId">存档标识 — 所有查询/写入均按此 ID 隔离</param>
        public LiteDbConversationStore(string filePath, string saveId)
        {
            if (string.IsNullOrEmpty(saveId))
                throw new ArgumentNullException(nameof(saveId));

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _saveId = saveId;
            _db = new LiteDatabase(filePath);
            CoreLog.Info($"[LiteDbConvStore] DB: {filePath}  save_id={_saveId}");
            InitIndexes();
        }

        private void InitIndexes()
        {
            var col = _db.GetCollection<ConversationEntry>("conversation");
            col.EnsureIndex(x => x.SaveId);
            col.EnsureIndex(x => x.GameDay);
        }

        private ILiteCollection<ConversationEntry> Col => _db.GetCollection<ConversationEntry>("conversation");

        public int Count
        {
            get
            {
                if (_disposed) return 0;
                try
                {
                    return Col.Query().Where(x => x.SaveId == _saveId).Count();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[LiteDbConvStore] Count 查询失败: {ex.Message}");
                    return 0;
                }
            }
        }

        public void RecordUserMessage(string text)
            => Record(ConvRole.User, text, "", "", "");

        public void RecordAssistantMessage(string text, string thinking, string runId, string agentType)
            => Record(ConvRole.Assistant, text, thinking, runId, agentType);

        public void RecordSystemMessage(string text)
            => Record(ConvRole.System, text, "", "", "");

        public void RecordToolCall(string toolId, string name, string input)
            => Record(ConvRole.ToolCall, "", "", toolId ?? "",
                agentType: "", toolName: name ?? "", toolInput: input ?? "");

        public void RecordToolResult(string toolId, bool isError, double durationMs, string output)
            => Record(ConvRole.ToolResult, output ?? "", "", toolId ?? "",
                agentType: "", isToolError: isError, toolDurationMs: durationMs);

        private void Record(ConvRole role, string text, string thinking, string runId, string agentType,
            string toolName = "", string toolInput = "", bool isToolError = false, double toolDurationMs = 0)
        {
            if (_disposed) return;
            lock (_writeLock)
            {
                try
                {
                    var entry = new ConversationEntry
                    {
                        Role = role,
                        Text = text ?? "",
                        Thinking = thinking ?? "",
                        RunId = runId ?? "",
                        AgentType = agentType ?? "",
                        ToolName = toolName ?? "",
                        ToolInput = toolInput ?? "",
                        IsToolError = isToolError,
                        ToolDurationMs = toolDurationMs,
                        Timestamp = DateTime.UtcNow,
                        GameDay = AgentOrchestrator.GameDay,
                        SaveId = _saveId,
                    };
                    Col.Insert(entry);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[LiteDbConvStore] 写入失败: {ex.Message}");
                }
            }
        }

        public ConversationEntry? GetAt(long id)
        {
            if (_disposed) return null;
            try
            {
                return Col.Query()
                    .Where(x => x.SaveId == _saveId && x.Id == id)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[LiteDbConvStore] GetAt({id}) 失败: {ex.Message}");
                return null;
            }
        }

        public IReadOnlyList<ConversationEntry> GetRecent(int n)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                var list = Col.Query()
                    .Where(x => x.SaveId == _saveId)
                    .OrderByDescending(x => x.Id)
                    .Limit(Math.Max(1, n))
                    .ToList();
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[LiteDbConvStore] GetRecent({n}) 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        public IReadOnlyList<ConversationEntry> GetBefore(long beforeId, int n)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                var list = Col.Query()
                    .Where(x => x.SaveId == _saveId && x.Id < beforeId)
                    .OrderByDescending(x => x.Id)
                    .Limit(Math.Max(1, n))
                    .ToList();
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[LiteDbConvStore] GetBefore({beforeId}, {n}) 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        public IReadOnlyList<ConversationEntry> QueryToolCalls(
            string? toolName = null, int fromDay = 0, int toDay = int.MaxValue,
            int limit = 100, long beforeId = long.MaxValue)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                var query = Col.Query()
                    .Where(x => x.SaveId == _saveId
                        && x.Role == ConvRole.ToolCall
                        && x.Id < beforeId);

                if (toolName != null)
                    query = query.Where(x => x.ToolName == toolName);
                if (fromDay > 0)
                    query = query.Where(x => x.GameDay >= fromDay);
                if (toDay < int.MaxValue)
                    query = query.Where(x => x.GameDay <= toDay);

                var list = query.OrderByDescending(x => x.Id).Limit(Math.Max(1, limit)).ToList();
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[LiteDbConvStore] QueryToolCalls 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        public IReadOnlyList<ToolCallDailyStat> GetToolDailyStats(
            int fromDay = 0, int toDay = int.MaxValue)
        {
            if (_disposed) return Array.Empty<ToolCallDailyStat>();
            try
            {
                return Col.Query()
                    .Where(x => x.SaveId == _saveId
                        && x.Role == ConvRole.ToolCall
                        && (fromDay == 0 || x.GameDay >= fromDay)
                        && (toDay == int.MaxValue || x.GameDay <= toDay))
                    .ToList()
                    .GroupBy(e => new { e.GameDay, e.ToolName })
                    .Select(g => new ToolCallDailyStat
                    {
                        GameDay = g.Key.GameDay,
                        ToolName = g.Key.ToolName,
                        CallCount = g.Count()
                    })
                    .OrderByDescending(s => s.GameDay)
                    .ThenByDescending(s => s.CallCount)
                    .ToList();
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[LiteDbConvStore] GetToolDailyStats 失败: {ex.Message}");
                return Array.Empty<ToolCallDailyStat>();
            }
        }

        public List<string> GetKnownToolNames()
        {
            if (_disposed) return new List<string>();
            try
            {
                return Col.Query()
                    .Where(x => x.SaveId == _saveId
                        && x.Role == ConvRole.ToolCall
                        && x.ToolName != "")
                    .ToList()
                    .Select(x => x.ToolName)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[LiteDbConvStore] GetKnownToolNames 失败: {ex.Message}");
                return new List<string>();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _db?.Dispose();
        }
    }
}
