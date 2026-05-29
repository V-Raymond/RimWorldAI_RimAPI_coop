using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Mcp
{
    public class McpClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private long _nextId = 1;

        public McpClient(string baseUrl = "http://localhost:9877")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        /// <summary>tools/list — 获取可用 Tool 列表</summary>
        public async Task<List<ToolDefinition>> ListTools()
        {
            var result = await CallAsync("tools/list", null);
            var tools = JsonSerializer.Deserialize<ToolsListResult>(result.GetRawText());
            return tools?.Tools ?? new List<ToolDefinition>();
        }

        /// <summary>tools/call — 调用 MCP Tool，返回文本结果</summary>
        public async Task<string> CallTool(string name, Dictionary<string, JsonElement>? args = null)
        {
            var prms = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["arguments"] = args != null
                    ? JsonSerializer.SerializeToElement(args)
                    : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>())
            };

            var result = await CallAsync("tools/call", prms);
            var tc = JsonSerializer.Deserialize<ToolCallResult>(result.GetRawText());
            if (tc == null || tc.Content.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var c in tc.Content) sb.AppendLine(c.Text);
            return sb.ToString().TrimEnd();
        }

        private async Task<JsonElement> CallAsync(string method, Dictionary<string, JsonElement>? prms)
        {
            var request = new JsonRpcRequest
            {
                Method = method,
                Params = prms,
                Id = Interlocked.Increment(ref _nextId)
            };

            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpResp = await _http.PostAsync($"{_baseUrl}/mcp", content);
            var respJson = await httpResp.Content.ReadAsStringAsync();
            var resp = JsonSerializer.Deserialize<JsonRpcResponse>(respJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            if (resp?.Error != null)
                throw new Exception($"MCP Error [{resp.Error.Code}]: {resp.Error.Message}");
            if (resp?.Result == null)
                throw new Exception("MCP 响应无 result");

            return resp.Result.Value;
        }

        public void Dispose() { _http.Dispose(); }
    }
}
