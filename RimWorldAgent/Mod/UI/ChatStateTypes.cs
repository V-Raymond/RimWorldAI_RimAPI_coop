using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldAgent.Core.AgentRuntime;
using Verse;

namespace RimWorldAgent
{
    public enum ChatRole { User, Assistant }
    public enum ChatState { Streaming, Done, Error }
    public enum ToolStatus { Running, Completed, Failed }

    public class ToolCallInfo
    {
        public string ItemId = "";
        public string Name = "";
        public string Title = "";
        public string Meta = "";
        public ToolStatus Status;
        public DateTime StartTime = DateTime.UtcNow;
        public double DurationMs;
    }

    public class ChatEntry
    {
        public ChatRole Role;
        public string Text = "";
        public string ThinkingText = "";
        public ChatState State;
        public string RunId = "";
        public string AgentId = "";
        public string AgentType = "";
        public bool IsContext;
        public int LastChunkLen;
        public float CachedHeight;
        public int CachedTextLen;
        public int CachedThinkingLen;
    }

    public static class ChatDisplayState
    {
        public static event Action? OnChanged;
        public static BudgetStatus CurrentBudgetStatus = BudgetStatus.Ok;
        public static float CurrentBudgetPercent;
        public static string CurrentBudgetText = "";

        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly object _lock = new();

        // 事件队列：CcbWebSocket 后台线程入队，Dialog_AiChat UI 线程消费
        private static readonly Queue<Action> _pendingEvents = new();
        private static readonly object _eventLock = new();

        /// <summary>WS 后台线程安全入队，由 Dialog_AiChat 在 UI 线程 DrainEvents</summary>
        public static void EnqueueUiEvent(Action action)
        {
            lock (_eventLock) { _pendingEvents.Enqueue(action); }
        }

        /// <summary>BridgeBus → UiMessage → EnqueueUiEvent 分发到内部方法</summary>
        public static void ProcessDisplayMessage(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                switch (type)
                {
                    case "text_delta":
                        EnqueueUiEvent(() => OnAssistantText(root.GetProperty("text").GetString() ?? ""));
                        break;
                    case "thinking_delta":
                        EnqueueUiEvent(() => OnAssistantThinking(root.GetProperty("thinking").GetString() ?? ""));
                        break;
                    case "text_block":
                        EnqueueUiEvent(() => OnAssistantText(root.GetProperty("text").GetString() ?? ""));
                        break;
                    case "tool_call":
                        EnqueueUiEvent(() => AddToolCall(root.GetProperty("id").GetString() ?? "", root.GetProperty("name").GetString() ?? "", root.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : ""));
                        break;
                    case "result":
                        EnqueueUiEvent(() => { FinishStreaming(); UpdateBudgetFromTracker(); });
                        break;
                    case "aborted":
                        EnqueueUiEvent(() => MarkLastAborted());
                        break;
                    case "system_init":
                        TokenUsageTracker.CurrentModel = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                        break;
                    case "user":
                        EnqueueUiEvent(() => OnUserMessage(root.GetProperty("text").GetString() ?? ""));
                        break;
                    case "error":
                        EnqueueUiEvent(() => AddSystemMessage(root.GetProperty("error").GetString() ?? ""));
                        break;
                    case "system":
                        EnqueueUiEvent(() => AddSystemMessage(root.GetProperty("text").GetString() ?? ""));
                        break;
                    case "budget_status":
                        break;
                }
            }
            catch (Exception ex) { CoreLog.Warn($"[ChatDisplayState] 解析显示消息失败: {ex.Message}"); }
        }

        private static void UpdateBudgetFromTracker()
        {
            if (TokenUsageTracker.TotalAllTokens > 0)
            {
                var limit = RimWorldAgentMod.Instance?.Settings?.TokenBudgetLimit ?? 0;
                CurrentBudgetStatus = limit > 0 ? TokenUsageTracker.CheckBudget(limit) : BudgetStatus.Ok;
            }
        }

        /// <summary>UI 线程调用，消费所有积压事件</summary>
        public static void DrainEvents()
        {
            List<Action> batch;
            lock (_eventLock)
            {
                if (_pendingEvents.Count == 0) return;
                batch = new List<Action>(_pendingEvents);
                _pendingEvents.Clear();
            }
            for (int i = 0; i < batch.Count; i++)
            {
                try { batch[i](); }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[ChatDisplayState] 事件处理异常 idx={i}/{batch.Count} action={batch[i].Method.Name} target={batch[i].Target?.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public static List<ChatEntry> Snapshot { get { lock (_lock) return _entries.ToList(); } }
        public static List<ToolCallInfo> ToolCallsSnapshot { get { lock (_lock) return _toolCalls.ToList(); } }

        public static void AddSystemMessage(string text) { lock (_lock) { _entries.Add(new ChatEntry { Role = ChatRole.Assistant, Text = text, State = ChatState.Done, IsContext = true }); } OnChanged?.Invoke(); }

        /// <summary>用户发送消息时记录，结束上一轮 AI 流式条目</summary>
        public static void OnUserMessage(string text)
        {
            lock (_lock)
            {
                FinalizeStreamingLocked();
                _entries.Add(new ChatEntry { Role = ChatRole.User, Text = text, State = ChatState.Done });
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        public static void MarkLastAborted()
        {
            lock (_lock)
            {
                if (_streamingEntry != null)
                {
                    _streamingEntry.State = ChatState.Done;
                    if (string.IsNullOrEmpty(_streamingEntry.Text))
                        _streamingEntry.Text = "（已中断）";
                    _streamingEntry.CachedHeight = 0f;
                    _streamingEntry = null;
                }
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        public static void Clear()
        {
            lock (_lock) { _entries.Clear(); _toolCalls.Clear(); _streamingEntry = null; }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        // ===== 从 CcbWebSocket 事件填充 =====

        // 增量累积器：stream_event delta 逐片到达，累积后用 REPLACE 语义写入当前条目
        // content_block_start 发空串信号 → 结束上一条流式，创建新条目
        private static string _deltaAccum = "";
        private static bool _deltaIsThinking;
        private static ChatEntry? _streamingEntry;

        /// <summary>流式文本 delta — 累积后替换。空串信号 = 新 text block 开始，创建新条目</summary>
        public static void OnAssistantText(string text)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(text))
                {
                    // content_block_start{text} → 结束上一条流式，新建条目
                    _deltaIsThinking = false;
                    _deltaAccum = "";
                    FinalizeStreamingLocked();
                    _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                    _entries.Add(_streamingEntry);
                }
                else
                {
                    if (_deltaIsThinking) { _deltaIsThinking = false; _deltaAccum = ""; }
                    _deltaAccum += text;
                    if (_streamingEntry == null || _streamingEntry.State != ChatState.Streaming)
                    {
                        _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                        _entries.Add(_streamingEntry);
                    }
                    _streamingEntry.Text = _deltaAccum;
                    _streamingEntry.ThinkingText = "";
                    _streamingEntry.CachedHeight = 0f;
                }
            }
            OnChanged?.Invoke();
        }

        /// <summary>流式思考 delta — 累积后替换。空串信号 = 新 thinking block 开始，创建新条目</summary>
        public static void OnAssistantThinking(string thinking)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(thinking))
                {
                    // content_block_start{thinking} → 结束上一条流式，新建条目
                    _deltaIsThinking = true;
                    _deltaAccum = "";
                    FinalizeStreamingLocked();
                    _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                    _entries.Add(_streamingEntry);
                }
                else
                {
                    if (!_deltaIsThinking) { _deltaIsThinking = true; _deltaAccum = ""; }
                    _deltaAccum += thinking;
                    if (_streamingEntry == null || _streamingEntry.State != ChatState.Streaming)
                    {
                        _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                        _entries.Add(_streamingEntry);
                    }
                    _streamingEntry.ThinkingText = _deltaAccum;
                    _streamingEntry.CachedHeight = 0f;
                }
            }
            OnChanged?.Invoke();
        }

        private static void FinalizeStreamingLocked()
        {
            if (_streamingEntry != null)
            {
                _streamingEntry.State = ChatState.Done;
                _streamingEntry.CachedHeight = 0f;
                _streamingEntry = null;
            }
        }

        /// <summary>工具开始执行</summary>
        public static void AddToolCall(string toolId, string toolName, string meta)
        {
            lock (_toolCalls)
            {
                _toolCalls.Add(new ToolCallInfo
                {
                    ItemId = toolId,
                    Name = toolName.Replace("mcp__agent__", "").Replace("mcp__rimworld__", ""),
                    Meta = meta,
                    Status = ToolStatus.Running,
                });
            }
            OnChanged?.Invoke();
        }

        /// <summary>工具执行完成（按 toolId 匹配最近一个 running）</summary>
        public static void FinishToolCall(string toolId, bool isError, double durationMs)
        {
            lock (_toolCalls)
            {
                for (int i = _toolCalls.Count - 1; i >= 0; i--)
                {
                    if (_toolCalls[i].ItemId == toolId && _toolCalls[i].Status == ToolStatus.Running)
                    {
                        _toolCalls[i].Status = isError ? ToolStatus.Failed : ToolStatus.Completed;
                        _toolCalls[i].DurationMs = durationMs;
                        break;
                    }
                }
            }
            OnChanged?.Invoke();
        }

        /// <summary>流式结束（result 消息触发），最后一条 Streaming → Done</summary>
        public static void FinishStreaming()
        {
            lock (_lock) { FinalizeStreamingLocked(); }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }
    }


    /// <summary>威胁摘要（由 Agent 通过 MCP 更新）</summary>
    public static class BridgeLifecycle
    {
        public static string DangerSummary = "";
    }

    /// <summary>殖民地概览生成（通过 MCP get_game_context 获取）</summary>
    public static class GameContextProvider
    {
        public static string BuildGameContext() => "";
        public static string BuildColonyOverview(Map map, List<Pawn> colonists, int count) => "";
    }

    /// <summary>工具显示名称转换</summary>
    public static class ToolDisplayNames
    {
        public static string GetDisplayName(string rawName) => rawName;
    }
}
