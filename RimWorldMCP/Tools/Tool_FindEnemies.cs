using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_FindEnemies : ITool
    {
        public string Name => "find_enemies";
        public string Description => "查找地图上所有敌对目标（含逃跑中状态），并列出每个已征召殖民者可攻击的敌人编号列表。攻击范围覆盖 = 武器射程 + 射速/近战。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                show_movement = new { type = "boolean", description = "是否显示敌人移动预测（攻击目标、ETA、路径）（默认 false）", @default = false }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            bool showMovement = false;
            if (args?.TryGetProperty("show_movement", out var jSm) == true && jSm.ValueKind == JsonValueKind.True)
                showMovement = true;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var playerFaction = Faction.OfPlayer;
                    if (playerFaction == null) return ToolResult.Error("无法获取玩家派系。");

                    var enemies = map.mapPawns.AllPawnsSpawned
                        .Where(p => p.HostileTo(playerFaction) && !p.Dead && !p.Destroyed && !p.Fogged())
                        .OrderBy(p => p.Position.x)
                        .ThenBy(p => p.Position.z)
                        .ThenBy(p => p.thingIDNumber)
                        .ToList();

                    if (enemies.Count == 0)
                        return ToolResult.Success("地图上没有发现敌对目标。");

                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    var colonistLookup = colonists?.ToDictionary(c => c.thingIDNumber, c => c) ?? new Dictionary<int, Pawn>();

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 战场态势");

                    // 敌人紧凑编号列表 [1] 名称 类型 (x,z) 状态
                    sb.AppendLine($"### 敌人 ({enemies.Count})");
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        var e = enemies[i];
                        string status = e.Downed ? "倒地"
                            : IsFleeing(e) ? "逃跑中"
                            : e.Drafted ? "征召"
                            : "活跃";
                        sb.AppendLine($"[{i + 1}] {e.LabelShort} | {e.KindLabel} | ({e.Position.x},{e.Position.z}) | {status} | ID:{e.thingIDNumber}");

                        // 攻击目标（始终显示）
                        var enemyTarget = e.mindState?.enemyTarget as Pawn;
                        var hasTargetLine = false;
                        if (enemyTarget != null && !e.Downed && colonistLookup.TryGetValue(enemyTarget.thingIDNumber, out var tPawn))
                        {
                            sb.Append($"    ▸ 目标: {tPawn.LabelShort}(ID:{tPawn.thingIDNumber})");
                            hasTargetLine = true;
                        }

                        // 移动预测（仅 show_movement）
                        if (showMovement && !e.Downed)
                        {
                            if (e.pather?.MovingNow == true)
                            {
                                if (e.pather.Destination.IsValid)
                                    sb.Append($" | 前往: ({e.pather.Destination.Cell.x},{e.pather.Destination.Cell.z})");
                                var path = e.pather.curPath;
                                if (path != null && path.NodesReversed != null && path.NodesReversed.Count > 1)
                                {
                                    var nodes = path.NodesReversed;
                                    int remaining = nodes.Count - 1;
                                    float eta = remaining * e.TicksPerMoveCardinal / 60f;
                                    sb.Append($" | ETA: {eta:F1}s");
                                    // 前5个路径节点
                                    int showNodes = Math.Min(5, nodes.Count);
                                    sb.Append(" | 路径: ");
                                    for (int j = nodes.Count - 1; j >= Math.Max(0, nodes.Count - showNodes); j--)
                                        sb.Append(j < nodes.Count - 1 ? "→" : "").Append($"({nodes[j].x},{nodes[j].z})");
                                }
                            }
                            sb.AppendLine();
                        }
                        else if (hasTargetLine)
                        {
                            sb.AppendLine();
                        }
                    }

                    // 已征召殖民者的攻击范围覆盖
                    var drafted = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .Where(p => p.Drafted && !p.Downed)
                        .OrderBy(p => p.thingIDNumber)
                        .ToList();

                    if (drafted.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("### 攻击覆盖");
                        foreach (var d in drafted)
                        {
                            var verb = d.CurrentEffectiveVerb;
                            bool isMelee = verb?.verbProps.IsMeleeAttack ?? true;
                            float effectiveRange = verb?.EffectiveRange ?? 1.42f;

                            string weaponLabel;
                            if (d.equipment?.Primary != null)
                                weaponLabel = d.equipment.Primary.Label;
                            else if (isMelee)
                                weaponLabel = "空手";
                            else
                                weaponLabel = "无武器";

                            string atkType = isMelee ? "近战" : "远程";
                            string rangeStr = isMelee ? "" : effectiveRange.ToString("F0");

                            // 找出可攻击的敌人编号 [1,2,3]
                            var inRangeIdx = new List<int>();
                            for (int i = 0; i < enemies.Count; i++)
                            {
                                float distSq = d.Position.DistanceToSquared(enemies[i].Position);
                                if (distSq <= effectiveRange * effectiveRange)
                                    inRangeIdx.Add(i + 1);
                            }

                            string rangeDisplay = isMelee ? "近战" : $"远程{rangeStr}";
                            string targetsDisplay = inRangeIdx.Count > 0
                                ? $"[{string.Join(",", inRangeIdx)}]"
                                : "[-]";

                            // 正在攻击谁（用编号）
                            string attackingDisplay = GetAttackingTargetIndex(d, enemies);

                            sb.AppendLine($"{d.LabelShort}({d.thingIDNumber}) {weaponLabel} {rangeDisplay} → {targetsDisplay}{attackingDisplay}");
                        }
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"查找敌人失败: {ex.Message}");
                }
            });
        }

        private static string GetAttackingTargetIndex(Pawn pawn, List<Pawn> enemies)
        {
            var job = pawn.CurJob;
            if (job == null) return "";
            bool isAttackJob = job.def == JobDefOf.AttackStatic || job.def == JobDefOf.AttackMelee;
            if (!isAttackJob) return "";

            var target = job.targetA.Thing as Pawn;
            if (target == null || target.Dead || target.Destroyed) return "";

            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].thingIDNumber == target.thingIDNumber)
                    return $" ▸{i + 1}";

            return "";
        }

        private static bool IsFleeing(Pawn pawn)
        {
            if (pawn.InMentalState)
            {
                var def = pawn.MentalStateDef;
                if (def == MentalStateDefOf.PanicFlee || def == MentalStateDefOf.PanicFleeFire)
                    return true;
            }
            if (pawn.CurJob?.def == JobDefOf.Flee)
                return true;
            var lord = pawn.GetLord();
            if (lord?.CurLordToil is LordToil_PanicFlee)
                return true;
            return false;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
