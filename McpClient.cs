using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP
{
    public enum ClientState { Disconnected, Connecting, Connected, Ready }

    public static class McpClient
    {
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _cts;
        private static string _url = "";
        private static string _token = "";
        private static string _password = "";
        private static ClientState _state = ClientState.Disconnected;

        public static ClientState State => _state;
        public static bool IsConnected => _state >= ClientState.Connected;
        public static bool IsReady => _state == ClientState.Ready;

        // 收到的消息缓冲 — UI 从中读取
        public static readonly ConcurrentQueue<string> Incoming = new();

        /// <summary>连接 Gateway 并完成 auth→ready 握手</summary>
        public static async Task Connect(string wsUrl, string token, string password)
        {
            _url = wsUrl;
            _token = token;
            _password = password;
            Disconnect();

            try
            {
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                _state = ClientState.Connecting;

                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
                _state = ClientState.Connected;
                McpLog.Info($"[ws] 已连接: {wsUrl}");

                // 2. 发送 auth
                var auth = new { type = "auth", token, password };
                await SendJson(auth);

                // 3. 启动接收循环 — 等待 ready 和事件流
                _ = ReceiveLoop(_cts.Token);

                // 4. 等待 ready（最多 10 秒）
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (_state == ClientState.Connected && DateTime.UtcNow < deadline)
                    await Task.Delay(100);

                if (_state == ClientState.Ready)
                    McpLog.Info("[ws] 握手完成 — Ready");
                else
                    McpLog.Warn("[ws] 握手超时，未收到 ready");
            }
            catch (Exception ex)
            {
                _state = ClientState.Disconnected;
                McpLog.Warn($"[ws] 连接失败: {ex.Message}");
            }
        }

        /// <summary>发送文本消息</summary>
        public static async Task SendMessage(string text)
        {
            if (!IsReady) return;
            await SendJson(new { type = "message", text });
        }

        public static void Disconnect()
        {
            _cts?.Cancel();
            _state = ClientState.Disconnected;
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        private static async Task SendJson(object obj)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }

        private static async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var text = Encoding.UTF8.GetString(buf, 0, result.Count);

                    // 组装多帧消息
                    while (!result.EndOfMessage && !ct.IsCancellationRequested)
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, result.Count, buf.Length - result.Count), ct);
                        text += Encoding.UTF8.GetString(buf, 0, result.Count);
                    }

                    // 解析消息类型
                    try
                    {
                        var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var typeProp))
                        {
                            var type = typeProp.GetString();
                            if (type == "ready")
                            {
                                _state = ClientState.Ready;
                                McpLog.Info("[ws] 收到 ready");
                            }
                        }
                    }
                    catch { }

                    Incoming.Enqueue(text);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { McpLog.Warn($"[ws] 接收异常: {ex.Message}"); }

            _state = ClientState.Disconnected;
        }
    }
}
