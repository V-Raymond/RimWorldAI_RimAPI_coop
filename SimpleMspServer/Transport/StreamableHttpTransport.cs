using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMspServer.Transport
{
    /// <summary>HTTP Transport — POST /mcp 同步 JSON-RPC 请求/响应</summary>
    public class HttpTransport : ITransport
    {
        private readonly int _port;
        private readonly string _host;
        private readonly string _prefixHost;
        private HttpListener? _listener;
        private Func<string, string>? _handler;

        public string Name => "http";

        public HttpTransport(int port = 9877, string host = "localhost")
        {
            _port = port;
            _host = host;
            _prefixHost = host == "0.0.0.0" ? "+" : host;
        }

        public void SetRequestHandler(Func<string, string> handler) => _handler = handler;

        public Task StartAsync(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{_prefixHost}:{_port}/");

            try { _listener.Start(); }
            catch (HttpListenerException ex)
            {
                var diag = ex.ErrorCode switch
                {
                    5  => $"拒绝访问 — 端口 {_port} 需要管理员权限",
                    183 => $"端口 {_port} 已被占用",
                    _   => $"http.sys 错误 {ex.ErrorCode}"
                };
                SimpleLog.Error($"[http] 启动失败 [{ex.ErrorCode}]: {diag}。{ex.Message}");
                throw;
            }

            SimpleLog.Info($"[http] HTTP 服务器已启动: http://{_host}:{_port}");
            Task.Run(() => AcceptLoop(ct), ct);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            SimpleLog.Info("[http] HTTP 服务器已停止");
            return Task.CompletedTask;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(ctx), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { SimpleLog.Error($"[http] 接受连接错误: {ex.Message}"); }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // CORS
            if (req.Headers.Get("Origin") != null)
            {
                res.Headers.Add("Access-Control-Allow-Origin", "*");
                res.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            }

            try
            {
                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

                if (req.Url?.AbsolutePath == "/mcp" && req.HttpMethod == "POST")
                    await HandleMcp(ctx);
                else if (req.Url?.AbsolutePath == "/mcp" && req.HttpMethod == "DELETE")
                { res.StatusCode = 204; res.Close(); }
                else if (req.Url?.AbsolutePath == "/health" && req.HttpMethod == "GET")
                { await WriteText(res, "OK", "text/plain"); }
                else if (req.HttpMethod == "GET")
                { await WriteText(res, "{\"status\":\"ok\",\"server\":\"SimpleMspServer\",\"transport\":\"http\"}", "application/json"); }
                else
                { res.StatusCode = 404; res.Close(); }
            }
            catch (Exception ex)
            {
                SimpleLog.Error($"[http] 处理请求错误: {ex.Message}");
                try { res.StatusCode = 500; res.Close(); } catch { }
            }
        }

        private async Task HandleMcp(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();

            if (_handler == null) { res.StatusCode = 503; res.Close(); return; }

            var result = _handler(body);
            await WriteText(res, result, "application/json");
        }

        private static async Task WriteText(HttpListenerResponse res, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            res.Close();
        }
    }
}
