using System;
using System.Collections.Concurrent;

namespace RimWorldMCP
{
    public static class McpLog
    {
        private static readonly ConcurrentQueue<LogEntry> _queue = new();
        private const int MaxQueueSize = 500;

        public static void Info(string msg) => Enqueue("INFO", msg);
        public static void Warn(string msg) => Enqueue("WARN", msg);
        public static void Error(string msg) => Enqueue("ERROR", msg);

        private static void Enqueue(string level, string msg)
        {
            _queue.Enqueue(new LogEntry { Level = level, Message = msg, Time = DateTime.Now });

            // 防止内存泄漏：丢弃最旧的
            while (_queue.Count > MaxQueueSize)
                _queue.TryDequeue(out _);
        }

        /// <summary>必须在主线程（GameComponentUpdate）调用</summary>
        public static void Flush()
        {
            while (_queue.TryDequeue(out var entry))
            {
                try
                {
                    var line = $"[RimWorldMCP] {entry.Time:HH:mm:ss} [{entry.Level}] {entry.Message}";
                    switch (entry.Level)
                    {
                        case "WARN": Verse.Log.Warning(line); break;
                        case "ERROR": Verse.Log.Error(line); break;
                        default: Verse.Log.Message(line); break;
                    }
                }
                catch
                {
                    Console.Error.WriteLine(
                        $"[RimWorldMCP] {entry.Time:HH:mm:ss} [{entry.Level}] {entry.Message}");
                }
            }
        }

        public static int PendingCount => _queue.Count;

        private struct LogEntry
        {
            public string Level;
            public string Message;
            public DateTime Time;
        }
    }
}
