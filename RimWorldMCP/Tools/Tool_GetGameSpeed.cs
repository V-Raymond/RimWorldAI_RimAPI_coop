using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_GetGameSpeed : ITool, INoMapRequired
    {
        public string Name => "get_game_speed";
        public string Description => "获取当前游戏速度状态，包含强制减速的剩余时间。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        /// <summary>最近减速原因（由 Harmony Hook_Notification 写入）</summary>
        public static string LastSlowdownReason = "";
        public static int LastSlowdownTicksUntil = 0;

        /// <summary>查询真实暂停状态（供 Agent 侧同步 PaceController 用）</summary>
        public static bool IsPaused()
        {
            var tm = Find.TickManager;
            return tm != null && tm.Paused;
        }

        /// <summary>获取速度标签文本（供所有模块统一使用）</summary>
        public static string GetSpeedLabel()
        {
            var tm = Find.TickManager;
            if (tm == null) return "未知";
            if (tm.Paused) return "已暂停";
            if (tm.slower.ForcedNormalSpeed)
            {
                int remaining = LastSlowdownTicksUntil - Find.TickManager.TicksGame;
                if (remaining < 0) remaining = 0;
                float seconds = remaining / 60f;
                string reason = string.IsNullOrEmpty(LastSlowdownReason) ? "" : $"（{LastSlowdownReason}）";
                return $"1 倍速（强制减速中，剩余 ~{remaining}ticks / {seconds:F0}秒）{reason}";
            }
            return tm.CurTimeSpeed switch
            {
                TimeSpeed.Normal => "1 倍速",
                TimeSpeed.Fast => "2 倍速",
                TimeSpeed.Superfast => "3 倍速",
                TimeSpeed.Ultrafast => "最快",
                _ => tm.CurTimeSpeed.ToString()
            };
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var tm = Find.TickManager;
                var speed = GetSpeedLabel();
                var paused = IsPaused();
                var tick = tm?.TicksGame ?? 0;
                var day = tick / 60000;
                var json = JsonSerializer.Serialize(new
                {
                    paused,
                    speed,
                    tick,
                    day
                });
                return ToolResult.Success(json);
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
