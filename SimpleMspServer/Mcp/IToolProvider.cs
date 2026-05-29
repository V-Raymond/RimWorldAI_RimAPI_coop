using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SimpleMspServer.Mcp
{
    public interface IToolProvider
    {
        string ProviderName { get; }
        List<ToolDefinition> GetDefinitions();
        Task<ToolCallResult> ExecuteAsync(string name, JsonElement? args);
        List<ResourceDefinition> GetResources();
        string? ReadResource(string uri);
    }
}
