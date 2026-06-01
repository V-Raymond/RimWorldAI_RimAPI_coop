using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Fleck;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core
{
    /// <summary>
    /// UI 总线 — Fleck WS :19999
    /// 只负责 UiMessage 广播 + 客户端消息接收，不感知 SDK 原始格式。
    /// SDK → UiMessage 转换由 SdkMessageParser (AgentCore) 负责。
    /// </summary>
    public static class BridgeBus
    {
        private static WebSocketServer? _server;
        private static readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();

        public static bool IsRunning => _server != null;
        public static bool IsReady { get; set; }

        // ===== 上游：AgentCore → BridgeBus → UI =====

        /// <summary>推送 UiMessage 列表 — WS 广播 + 本地回调</summary>
        public static void PushUiMessages(List<string> messages)
        {
            CoreLog.Info($"[CCGUI_DEBUG] BridgeBus.PushUiMessages count={messages.Count} _clients={_clients.Count}");
            foreach (var msg in messages)
            {
                foreach (var kv in _clients)
                {
                    try { kv.Value.Send(msg); }
                    catch (Exception ex) { CoreLog.Info($"[BridgeBus] 发送失败: {ex.Message}"); _clients.TryRemove(kv.Key, out _); }
                }
                OnDisplayMessage?.Invoke(msg);
            }
        }

        /// <summary>UiMessage 本地回调 (供 ChatDisplayState)</summary>
        public static event Action<string>? OnDisplayMessage;

        /// <summary>系统事件 → 直接广播 (已是 UiMessage 格式)</summary>
        public static void PushGameEvent(string uiJson)
        {
            CoreLog.Info($"[CCGUI_DEBUG] BridgeBus.PushGameEvent _clients={_clients.Count} preview={uiJson.Substring(0, Math.Min(uiJson.Length, 120))}");
            foreach (var kv in _clients)
            {
                try { kv.Value.Send(uiJson); }
                catch (Exception ex) { CoreLog.Info($"[BridgeBus] 发送失败: {ex.Message}"); _clients.TryRemove(kv.Key, out _); }
            }
            OnDisplayMessage?.Invoke(uiJson);
        }

        // ===== 下游：客户端 → BridgeBus → AgentCore =====

        public class ChatThinking
        {
            public string Mode = "default";
            public string Effort = "medium";
            public int Tokens;
        }

        public static event Action<string, ChatThinking?>? OnChat;
        public static event Action? OnAbort;

        public static void RaiseChat(string text, ChatThinking? thinking = null) => OnChat?.Invoke(text, thinking);
        public static void RaiseAbort() => OnAbort?.Invoke();

        // ===== 生命周期 =====

        public static void Start(int port = 19999)
        {
            if (_server != null) return;
            _server = new WebSocketServer($"ws://0.0.0.0:{port}");
            _server.Start(socket =>
            {
                var id = socket.ConnectionInfo.Id;
                socket.OnOpen = () =>
                {
                    _clients[id] = socket;
                    CoreLog.Info($"[BridgeBus] 客户端已连接: {socket.ConnectionInfo.ClientIpAddress} id={id} 总数={_clients.Count}");
                };
                socket.OnClose = () =>
                {
                    _clients.TryRemove(id, out _);
                    CoreLog.Info($"[BridgeBus] 客户端已断开: {socket.ConnectionInfo.ClientIpAddress} id={id} 剩余={_clients.Count}");
                };
                socket.OnMessage = msg =>
                {
                    CoreLog.Info($"[CCGUI_DEBUG] BridgeBus 收到客户端消息 len={msg.Length} preview={msg.Substring(0, Math.Min(msg.Length, 200))}");
                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                        CoreLog.Info($"[CCGUI_DEBUG] BridgeBus 解析客户端消息 type={type}");
                        switch (type)
                        {
                            case "chat":
                                var text = root.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(text))
                                {
                                    var think = ParseThinking(root);
                                    CoreLog.Info($"[CCGUI_DEBUG] BridgeBus chat thinking mode={think?.Mode} effort={think?.Effort} tokens={think?.Tokens}");
                                    OnChat?.Invoke(text, think);
                                }
                                break;
                            case "abort":
                                OnAbort?.Invoke();
                                break;
                        }
                    }
                    catch (Exception ex) { CoreLog.Info($"[BridgeBus] 消息解析失败: {ex.Message}"); }
                };
            });
            CoreLog.Info($"[BridgeBus] 已启动 ws://0.0.0.0:{port}");
        }

        public static void Stop()
        {
            OnChat = null;
            OnAbort = null;
            OnDisplayMessage = null;
            if (_server == null) return;
            foreach (var kv in _clients) { try { kv.Value.Close(); } catch { } }
            _clients.Clear();
            _server.Dispose();
            _server = null;
            CoreLog.Info("[BridgeBus] 已停止");
        }

        private static ChatThinking? ParseThinking(JsonElement root)
        {
            if (!root.TryGetProperty("thinking", out var th)) return null;
            return new ChatThinking
            {
                Mode = th.TryGetProperty("mode", out var m) ? m.GetString() ?? "default" : "default",
                Effort = th.TryGetProperty("effort", out var e) ? e.GetString() ?? "medium" : "medium",
                Tokens = th.TryGetProperty("tokens", out var t) && t.TryGetInt32(out var n) ? n : 0,
            };
        }
    }
}
