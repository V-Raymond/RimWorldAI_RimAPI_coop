using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimpleMspServer.Transport;

namespace SimpleMspServer.Mcp
{
    /// <summary>MCP JSON-RPC 调度器 — 协议无关，通过 IToolProvider 注入工具</summary>
    public class McpServer
    {
        private readonly List<IToolProvider> _providers = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();

        public IReadOnlyList<IToolProvider> Providers => _providers;

        public void RegisterProvider(IToolProvider provider)
        {
            _providers.Add(provider);
            Log($"[mcp] 注册 Tool 提供者: {provider.ProviderName} ({provider.GetDefinitions().Count} tools)");
        }

        public void ClearProviders() => _providers.Clear();

        /// <summary>聚合所有提供者的 Tool 定义</summary>
        public List<ToolDefinition> GetAllDefinitions()
        {
            var all = new List<ToolDefinition>();
            foreach (var p in _providers)
                all.AddRange(p.GetDefinitions());
            return all;
        }

        /// <summary>为 /mcp 端点提供的同步处理入口</summary>
        public string ProcessRequest(string rawJson)
        {
            try
            {
                var resp = ProcessCore(rawJson).GetAwaiter().GetResult();
                return resp ?? "{\"jsonrpc\":\"2.0\",\"id\":null}";
            }
            catch (Exception ex)
            {
                Log($"ProcessRequest 异常: {ex}");
                return "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"Internal error\"}}";
            }
        }

        private async Task<string?> ProcessCore(string rawJson)
        {
            JsonRpcRequest? request;
            try { request = JsonSerializer.Deserialize<JsonRpcRequest>(rawJson, McpJson.Options); }
            catch { return BuildError(null, -32700, "Parse error: 无效的 JSON"); }

            if (request == null) return BuildError(null, -32700, "Parse error: 无法解析");
            if (request.Jsonrpc != "2.0") return BuildError(request.Id, -32600, "Invalid Request: jsonrpc 必须为 \"2.0\"");

            try
            {
                var response = await DispatchAsync(request);
                return response?.ToJson();
            }
            catch (Exception ex)
            {
                Log($"Dispatch 异常: {ex}");
                return BuildError(request.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        private async Task<JsonRpcResponse?> DispatchAsync(JsonRpcRequest request)
        {
            switch (request.Method)
            {
                case "initialize": return HandleInitialize(request.Id, request.Params);
                case "notifications/initialized": Log("MCP 初始化完成"); return null;
                case "notifications/cancelled": HandleCancelled(request.Params); return null;
                case "tools/list": return HandleToolsList(request.Id);
                case "tools/call": return await HandleToolsCallAsync(request);
                case "resources/list": return HandleResourcesList(request.Id);
                case "resources/read": return HandleResourcesRead(request.Id, request.Params);
                case "ping": return HandlePing(request.Id);
                default:
                    if (request.IsNotification) return null;
                    return JsonRpcResponse.Fail(request.Id!.Value, -32601, $"Method not found: {request.Method}");
            }
        }

        // ---- initialize ----
        private static JsonRpcResponse HandleInitialize(JsonElement? id, JsonElement? prms)
        {
            var result = new InitializeResult { ProtocolVersion = "2024-11-05" };
            return JsonRpcResponse.Success(id!.Value, result);
        }

        // ---- tools/list ----
        private JsonRpcResponse HandleToolsList(JsonElement? id)
        {
            var tools = GetAllDefinitions();
            Log($"[mcp] tools/list: 返回 {tools.Count} 个工具");
            return JsonRpcResponse.Success(id!.Value, new { tools });
        }

        // ---- tools/call ----
        private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request)
        {
            var id = request.Id!.Value;
            if (request.Params == null) return JsonRpcResponse.Fail(id, -32602, "缺少 params");

            ToolCallParams? cp;
            try { cp = JsonSerializer.Deserialize<ToolCallParams>(request.Params.Value.GetRawText(), McpJson.Options); }
            catch { return JsonRpcResponse.Fail(id, -32602, "无法解析 tool call 参数"); }

            if (cp == null || string.IsNullOrEmpty(cp.Name)) return JsonRpcResponse.Fail(id, -32602, "缺少 tool name");

            var reqId = request.Id?.GetRawText() ?? cp.Name;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            _inflight[reqId] = cts;

            try
            {
                // 按注册顺序查找 → 第一个匹配的提供者执行
                foreach (var p in _providers)
                {
                    var defs = p.GetDefinitions();
                    if (defs.Any(d => d.Name == cp.Name))
                    {
                        var result = await p.ExecuteAsync(cp.Name, cp.Arguments);
                        return JsonRpcResponse.Success(id, result);
                    }
                }
                return JsonRpcResponse.Fail(id, -32602, $"未知工具: {cp.Name}");
            }
            catch (OperationCanceledException) { return JsonRpcResponse.Fail(id, -32800, "Request cancelled"); }
            finally { _inflight.TryRemove(reqId, out _); cts.Dispose(); }
        }

        // ---- notifications/cancelled ----
        private void HandleCancelled(JsonElement? prms)
        {
            if (prms == null) return;
            try
            {
                using var doc = JsonDocument.Parse(prms.Value.GetRawText());
                if (doc.RootElement.TryGetProperty("requestId", out var rq) &&
                    _inflight.TryGetValue(rq.GetRawText(), out var cts))
                    cts.Cancel();
            }
            catch { }
        }

        // ---- resources/list ----
        private JsonRpcResponse HandleResourcesList(JsonElement? id)
        {
            var resources = _providers.SelectMany(p => p.GetResources()).ToList();
            return JsonRpcResponse.Success(id!.Value, new { resources });
        }

        // ---- resources/read ----
        private JsonRpcResponse HandleResourcesRead(JsonElement? id, JsonElement? prms)
        {
            if (prms == null || !prms.Value.TryGetProperty("uri", out var uri))
                return JsonRpcResponse.Fail(id!.Value, -32602, "缺少 uri");

            foreach (var p in _providers)
            {
                var content = p.ReadResource(uri.GetString() ?? "");
                if (content != null)
                    return JsonRpcResponse.Success(id!.Value, new { contents = new[] { new { uri = uri.GetString(), mimeType = "text/markdown", text = content } } });
            }
            return JsonRpcResponse.Fail(id!.Value, -32000, $"Resource not found: {uri}");
        }

        // ---- ping ----
        private static JsonRpcResponse HandlePing(JsonElement? id)
            => JsonRpcResponse.Success(id!.Value, new { });

        private static string? BuildError(JsonElement? id, int code, string msg)
        {
            if (id == null || id.Value.ValueKind == JsonValueKind.Null) return null;
            return JsonRpcResponse.Fail(id.Value, code, msg).ToJson();
        }

        private static void Log(string msg) => SimpleLog.Info(msg);
    }
}
