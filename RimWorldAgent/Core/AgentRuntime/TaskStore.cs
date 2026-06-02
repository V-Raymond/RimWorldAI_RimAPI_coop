using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>任务数据模型（与 Claude Agent SDK TaskCreate/TaskUpdate 对齐）</summary>
    public class TaskItem
    {
        public string Id { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? ActiveForm { get; set; }
        public string? Owner { get; set; }
        public List<string> Blocks { get; set; } = new();
        public List<string> BlockedBy { get; set; } = new();
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    /// <summary>线程安全的任务存储，供内部 task_* 工具和 ToolDispatcher 提醒共用</summary>
    public static class TaskStore
    {
        private static readonly List<TaskItem> _tasks = new();
        private static readonly object _lock = new();
        private static int _nextId = 1;

        /// <summary>未完成任务数</summary>
        public static int PendingCount
        {
            get { lock (_lock) return _tasks.Count(t => t.Status != "completed" && t.Status != "deleted"); }
        }

        /// <summary>总任务数</summary>
        public static int Count
        {
            get { lock (_lock) return _tasks.Count; }
        }

        /// <summary>创建任务，返回分配的任务</summary>
        public static TaskItem Create(string subject, string description, string? activeForm = null,
            Dictionary<string, JsonElement>? metadata = null)
        {
            lock (_lock)
            {
                var item = new TaskItem
                {
                    Id = (_nextId++).ToString(),
                    Subject = subject,
                    Description = description,
                    ActiveForm = activeForm,
                    Metadata = metadata
                };
                _tasks.Add(item);
                return item;
            }
        }

        /// <summary>更新任务。返回 null 表示未找到。status=deleted 时移除任务。</summary>
        public static TaskItem? Update(string taskId,
            string? subject = null, string? description = null, string? activeForm = null,
            string? status = null, List<string>? addBlocks = null, List<string>? addBlockedBy = null,
            string? owner = null, Dictionary<string, JsonElement>? metadata = null)
        {
            lock (_lock)
            {
                var item = _tasks.FirstOrDefault(t => t.Id == taskId);
                if (item == null) return null;

                if (status == "deleted")
                {
                    _tasks.Remove(item);
                    return item;
                }

                if (subject != null) item.Subject = subject;
                if (description != null) item.Description = description;
                if (activeForm != null) item.ActiveForm = activeForm;
                if (status != null) item.Status = status;
                if (owner != null) item.Owner = owner;
                if (metadata != null)
                {
                    foreach (var kv in metadata)
                        item.Metadata![kv.Key] = kv.Value;
                }
                if (addBlocks != null && addBlocks.Count > 0)
                    item.Blocks.AddRange(addBlocks);
                if (addBlockedBy != null && addBlockedBy.Count > 0)
                    item.BlockedBy.AddRange(addBlockedBy);

                return item;
            }
        }

        /// <summary>获取单个任务</summary>
        public static TaskItem? Get(string taskId)
        {
            lock (_lock) return _tasks.FirstOrDefault(t => t.Id == taskId);
        }

        /// <summary>获取所有任务快照</summary>
        public static List<TaskItem> GetAll()
        {
            lock (_lock) return new List<TaskItem>(_tasks);
        }

        /// <summary>获取未完成任务快照</summary>
        public static List<TaskItem> GetPending()
        {
            lock (_lock) return _tasks.Where(t => t.Status != "completed" && t.Status != "deleted").ToList();
        }

        /// <summary>清空全部任务（新会话开始时调用）</summary>
        public static void Clear()
        {
            lock (_lock) { _tasks.Clear(); _nextId = 1; }
        }
    }
}
