using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldMCP
{
    /// <summary>消息类别，值越大优先级越高</summary>
    public enum MessageCategory
    {
        DailyMorning = 0,
        Alert = 10,
        RaidEnd = 20,
        RaidStart = 30,
        /// <summary>存档加载后的首次会话 prompt</summary>
        SessionInit = 40,
    }

    internal struct PendingMessage
    {
        public MessageCategory Category;
        public string Text;
    }

    /// <summary>消息队列 — 同类覆盖 + 等待 agent stream 完成立即发下一条</summary>
    public static class GatewayMessageQueue
    {
        private static readonly Dictionary<MessageCategory, PendingMessage> _pending = new();
        private static bool _waitingForAgentResponse;
        private static int _lastDailyDaySent = -1;
        private static bool _sessionPromptSent;
        private static int _idleFrames; // 收到消息后等待几帧再发（给同类消息覆盖窗口）

        private const int IdleFramesBeforeSend = 30; // ~0.5s 窗口让同类消息覆盖

        /// <summary>入队消息（同类覆盖）</summary>
        public static void Enqueue(MessageCategory category, string text)
        {
            if (!GatewayClient.IsConnected) return;

            _pending[category] = new PendingMessage
            {
                Category = category,
                Text = text,
            };
            _idleFrames = IdleFramesBeforeSend;
        }

        /// <summary>每帧由 BridgeLifecycle.Tick() 调用</summary>
        public static void Tick()
        {
            // 1. 等待上次 agent RPC 的 stream 完成
            if (_waitingForAgentResponse)
            {
                if (GatewayClient.IsAgentRpcDone)
                {
                    _waitingForAgentResponse = false;
                    GatewayClient.ClearInFlightAgentRpc();
                }
                else
                {
                    return;
                }
            }

            // 2. 断开则清空
            if (!GatewayClient.IsConnected)
            {
                _pending.Clear();
                _waitingForAgentResponse = false;
                _lastDailyDaySent = -1;
                _sessionPromptSent = false;
                return;
            }

            if (!GatewayClient.IsReady) return;

            // 3. 无待发消息则跳过
            if (_pending.Count == 0) return;

            // 4. 等待短暂稳定窗口（让同类消息覆盖）
            if (_idleFrames > 0)
            {
                _idleFrames--;
                return;
            }

            // 5. 取最高优先级立即发送
            SendHighestPriority();
        }

        /// <summary>立即发送（绕过批次窗口，但仍等 inflight 完成）</summary>
        public static void SendNow(MessageCategory category, string text)
        {
            if (!GatewayClient.IsReady || _waitingForAgentResponse) return;
            DoSend(category, text);
        }

        public static void MarkDailySent(int day) => _lastDailyDaySent = day;
        public static bool WasDailySentToday(int day) => _lastDailyDaySent == day;
        public static void MarkSessionPromptSent() => _sessionPromptSent = true;
        public static bool WasSessionPromptSent => _sessionPromptSent;

        public static void Reset()
        {
            _pending.Clear();
            _waitingForAgentResponse = false;
            _lastDailyDaySent = -1;
            _sessionPromptSent = false;
        }

        private static void SendHighestPriority()
        {
            var best = _pending.Values.OrderByDescending(m => (int)m.Category).First();
            _pending.Remove(best.Category);
            DoSend(best.Category, best.Text);
        }

        private static async void DoSend(MessageCategory category, string text)
        {
            if (!GatewayClient.IsReady) return;
            _waitingForAgentResponse = true;
            try
            {
                await GatewayClient.SendMessage(text);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[queue] 发送失败 ({category}): {ex.Message}");
                _waitingForAgentResponse = false;
            }
        }
    }
}
