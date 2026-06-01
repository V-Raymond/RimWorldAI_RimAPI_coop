using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// SQLite 持久化会话存储 — EXE 模式使用。
    /// WAL 模式，支持多读单写并发。可视化：sqlite3 conversation.db
    /// </summary>
    public sealed class SqliteConversationStore : IConversationStore, IDisposable
    {
        private readonly string _connectionString;
        private readonly object _writeLock = new();
        private bool _disposed;

        /// <param name="filePath">SQLite 文件路径，如 .../conversation.db</param>
        public SqliteConversationStore(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={filePath};Version=3;Journal Mode=WAL;";
            InitTable();
        }

        public int Count
        {
            get
            {
                if (_disposed) return 0;
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SQLiteCommand("SELECT COUNT(*) FROM conversation", conn);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[SqliteConvStore] Count 查询失败: {ex.Message}");
                    return 0;
                }
            }
        }

        public void RecordUserMessage(string text)
        {
            Record(ConvRole.User, text, "", "", "");
        }

        public void RecordAssistantMessage(string text, string thinking, string runId, string agentType)
        {
            Record(ConvRole.Assistant, text, thinking, runId, agentType);
        }

        public void RecordSystemMessage(string text)
        {
            Record(ConvRole.System, text, "", "", "");
        }

        private void Record(ConvRole role, string text, string thinking, string runId, string agentType)
        {
            if (_disposed) return;
            lock (_writeLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SQLiteCommand(
                        @"INSERT INTO conversation (role, text, thinking, run_id, agent_type, timestamp)
                          VALUES (@role, @text, @thinking, @runId, @agentType, @ts)", conn);
                    cmd.Parameters.AddWithValue("@role", RoleToString(role));
                    cmd.Parameters.AddWithValue("@text", text ?? "");
                    cmd.Parameters.AddWithValue("@thinking", thinking ?? "");
                    cmd.Parameters.AddWithValue("@runId", runId ?? "");
                    cmd.Parameters.AddWithValue("@agentType", agentType ?? "");
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[SqliteConvStore] 写入失败: {ex.Message}");
                }
            }
        }

        public ConversationEntry? GetAt(long id)
        {
            if (_disposed) return null;
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT id, role, text, thinking, run_id, agent_type, timestamp FROM conversation WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    return ReadEntry(reader);
                return null;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetAt({id}) 失败: {ex.Message}");
                return null;
            }
        }

        public IReadOnlyList<ConversationEntry> GetRecent(int n)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT id, role, text, thinking, run_id, agent_type, timestamp FROM conversation ORDER BY id DESC LIMIT @n", conn);
                cmd.Parameters.AddWithValue("@n", Math.Max(1, n));
                var list = new List<ConversationEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(ReadEntry(reader));
                // ORDER BY id DESC → 倒序插入 → 反转回升序
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetRecent({n}) 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        private static ConversationEntry ReadEntry(SQLiteDataReader reader)
        {
            return new ConversationEntry
            {
                Id = reader.GetInt64(0),
                Role = StringToRole(reader.GetString(1)),
                Text = reader.GetString(2),
                Thinking = reader.GetString(3),
                RunId = reader.GetString(4),
                AgentType = reader.GetString(5),
                Timestamp = DateTime.TryParse(reader.GetString(6), out var ts) ? ts : DateTime.UtcNow
            };
        }

        private void InitTable()
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    @"CREATE TABLE IF NOT EXISTS conversation (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        role        TEXT    NOT NULL,
                        text        TEXT    NOT NULL DEFAULT '',
                        thinking    TEXT    NOT NULL DEFAULT '',
                        run_id      TEXT    NOT NULL DEFAULT '',
                        agent_type  TEXT    NOT NULL DEFAULT '',
                        timestamp   TEXT    NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_timestamp ON conversation(timestamp);", conn);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[SqliteConvStore] 建表失败: {ex.Message}");
                throw;
            }
        }

        private SQLiteConnection OpenConnection()
        {
            var conn = new SQLiteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private static string RoleToString(ConvRole role) => role switch
        {
            ConvRole.User => "user",
            ConvRole.Assistant => "assistant",
            ConvRole.System => "system",
            _ => "unknown"
        };

        private static ConvRole StringToRole(string s) => s switch
        {
            "user" => ConvRole.User,
            "assistant" => ConvRole.Assistant,
            "system" => ConvRole.System,
            _ => ConvRole.System
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // SQLite 连接池由 .NET 管理，无需显式清理
        }
    }
}
