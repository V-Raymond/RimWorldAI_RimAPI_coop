using System;
using System.Threading;
using SimpleMspServer.Mcp;
using SimpleMspServer.Transport;

namespace SimpleMspServer
{
    /// <summary>通用 MCP 服务管理器 — Transport + McpServer 生命周期</summary>
    public class McpServiceHost : IDisposable
    {
        private ITransport? _transport;
        private CancellationTokenSource? _cts;

        public McpServer Server { get; }
        public int Port { get; }
        public string Host { get; }
        public bool IsRunning => _transport != null;

        public McpServiceHost(int port = 9877, string host = "0.0.0.0")
        {
            Port = port;
            Host = host;
            Server = new McpServer();
        }

        public void RegisterProvider(IToolProvider provider) => Server.RegisterProvider(provider);

        public void Start()
        {
            if (IsRunning) return;

            try
            {
                var transport = new HttpTransport(Port, Host);
                transport.SetRequestHandler(rawJson => Server.ProcessRequest(rawJson));

                _cts = new CancellationTokenSource();
                transport.StartAsync(_cts.Token);
                _transport = transport;

                SimpleLog.Info($"[host] MCP 服务已启动: http://{Host}:{Port} ({Server.Providers.Count} 提供者)");
            }
            catch (Exception ex)
            {
                _cts?.Cancel(); _cts?.Dispose(); _cts = null;
                _transport = null;
                SimpleLog.Error($"[host] MCP 服务启动失败: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_transport != null)
            {
                try { _transport.StopAsync(); } catch { }
                _transport = null;
            }
            _cts?.Cancel(); _cts?.Dispose(); _cts = null;
            SimpleLog.Info("[host] MCP 服务已停止");
        }

        public void Dispose() => Stop();
    }
}
