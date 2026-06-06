using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>EXE 模式 — tick 从 MCP 推送获取，暂停状态通过 MCP 查询</summary>
    public class RemoteGameStateProvider : GameStateBase
    {
        private readonly McpClient _mcp;
        private bool _isPaused;

        public RemoteGameStateProvider(McpClient mcp)
        {
            _mcp = mcp;
            mcp.OnGameTick += tick =>
            {
                Volatile.Write(ref _gameTick, tick);
                AgentOrchestrator.GameTick = tick;
            };
        }

        public override bool IsPaused => _isPaused;

        public override async Task SyncGameStatusAsync()
        {
            try
            {
                var json = await _mcp.CallTool("get_game_speed");
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        _isPaused = root.TryGetProperty("paused", out var p) && p.GetBoolean();
                        if (root.TryGetProperty("tick", out var t) && t.TryGetInt32(out var tick))
                        {
                            Volatile.Write(ref _gameTick, tick);
                            AgentOrchestrator.GameTick = tick;
                        }
                    }
                    catch (JsonException)
                    {
                        // 兼容旧版文本格式
                        _isPaused = json.IndexOf("已暂停", StringComparison.Ordinal) >= 0;
                    }
                }
            }
            catch (Exception ex) { CoreLog.Info($"[RemoteGameState] 查询暂停状态失败: {ex.Message}"); }
        }
    }
}
