using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.MapRendering;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TemperatureGridByPos : ITool
    {
        public string Name => "temperature_grid_by_pos";
        public string Description => "获取指定坐标范围的温度网格（直接坐标模式，无分块）。";

        private const int MaxGridWidth = 80;
        private const int MaxGridHeight = 60;
        private const string Legend = "█<-20°C  ▓-20~0  ░0~10  .10~21  ○21~35  ◎35~60  ●>60  ?迷雾";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上角 X 坐标" },
                pos_y = new { type = "integer", description = "左上角 Y 坐标" },
                end_x = new { type = "integer", description = "右下角 X 坐标（可选，默认=pos_x）" },
                end_y = new { type = "integer", description = "右下角 Y 坐标（可选，默认=pos_y）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;

            int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
            int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);

            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;
            if (w > MaxGridWidth || h > MaxGridHeight)
                return ToolResult.Error($"查询范围 {w}x{h} 超过上限 {MaxGridWidth}x{MaxGridHeight}，请缩小范围。");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var result = GridRenderer.RenderGrid(map, minX, minZ, maxX, maxZ, CellCharProviders.ForTemperature);

                    var sb = new StringBuilder();
                    sb.AppendLine($"## {Name}  世界({minX},{minZ})-({maxX},{maxZ})  [{w}x{h}]");
                    sb.AppendLine();

                    for (int z = 0; z < h; z++)
                    {
                        sb.Append($"z{minZ + z}: ");
                        sb.AppendLine(new string(result.Rows[z]));
                    }

                    sb.AppendLine();
                    sb.AppendLine("## 图例");
                    sb.AppendLine(Legend);

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"温度查询失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;
            return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
        }
    }
}
