using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMspServer.Transport
{
    public interface ITransport
    {
        string Name { get; }
        Task StartAsync(CancellationToken ct);
        Task StopAsync();
        /// <summary>设置同步请求处理器（JSON 入 → JSON 出）</summary>
        void SetRequestHandler(Func<string, string> handler);
    }
}
