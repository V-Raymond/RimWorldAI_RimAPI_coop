using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP
{
    public static class McpClient
    {
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _cts;
        private static string _url = "";
        private static string _token = "";
        private static string _password = "";

        public static bool IsConnected => _ws?.State == WebSocketState.Open;

        public static async Task<bool> Connect(string gatewayWsUrl, string token, string password)
        {
            _url = gatewayWsUrl;
            _token = token;
            _password = password;

            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                // 在 URL 上附加认证参数
                var uri = gatewayWsUrl;
                if (!string.IsNullOrEmpty(token))
                    uri += (uri.Contains("?") ? "&" : "?") + "token=" + Uri.EscapeDataString(token);
                if (!string.IsNullOrEmpty(password))
                    uri += (uri.Contains("?") ? "&" : "?") + "password=" + Uri.EscapeDataString(password);

                await _ws.ConnectAsync(new Uri(uri), _cts.Token);
                McpLog.Info($"[ws] 已连接到 OpenClaw Gateway: {gatewayWsUrl}");
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[ws] 连接失败: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> SendMessage(string message)
        {
            if (_ws?.State != WebSocketState.Open) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[ws] 发送失败: {ex.Message}");
                return false;
            }
        }

        public static void Disconnect()
        {
            _cts?.Cancel();
            _ws?.Dispose();
            _ws = null;
        }
    }
}
