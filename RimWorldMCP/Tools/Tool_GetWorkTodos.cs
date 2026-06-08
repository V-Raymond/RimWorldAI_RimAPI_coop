using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_GetWorkTodos : ITool
    {
        public string Name => "get_work_todos";
        public string Description => "汇总当前地图所有主要待办工作来源：建造、地图标记、工作单、医疗、囚犯、研究、搬运压力和空闲殖民者。用于判断下一步应该安排什么工作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                category = new
                {
                    type = "string",
                    description = "过滤类别，默认 all",
                    @enum = new[] { "all", "construction", "designations", "bills", "medical", "prisoners", "research", "hauling", "idle" },
                    @default = "all"
                },
                max_items = new { type = "integer", description = "每类最多显示条数，默认8，最大50", @default = 8 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var category = "all";
            var maxItems = 8;
            if (args != null)
            {
                if (args.Value.TryGetProperty("category", out var cat))
                    category = (cat.GetString() ?? "all").Trim().ToLowerInvariant();
                if (args.Value.TryGetProperty("max_items", out var max) && max.TryGetInt32(out var parsed))
                    maxItems = Math.Max(1, Math.Min(parsed, 50));
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var sections = new List<TodoSection>();
                    AddIfNeeded(sections, category, "construction", BuildConstructionSection(map, maxItems));
                    AddIfNeeded(sections, category, "designations", BuildDesignationSection(map, maxItems));
                    AddIfNeeded(sections, category, "bills", BuildBillSection(map, maxItems));
                    AddIfNeeded(sections, category, "medical", BuildMedicalSection(map, maxItems));
                    AddIfNeeded(sections, category, "prisoners", BuildPrisonerSection(map, maxItems));
                    AddIfNeeded(sections, category, "research", BuildResearchSection(map, maxItems));
                    AddIfNeeded(sections, category, "hauling", BuildHaulingSection(map, maxItems));
                    AddIfNeeded(sections, category, "idle", BuildIdleSection(map, maxItems));

                    if (sections.Count == 0)
                        return ToolResult.Error($"未知类别: {category}");

                    var sb = new StringBuilder();
                    sb.AppendLine("## 当前待办工作列表");
                    sb.AppendLine();
                    sb.AppendLine("| 类别 | 待办 | 优先级 | 摘要 |");
                    sb.AppendLine("|------|-----:|--------|------|");
                    foreach (var section in sections)
                    {
                        sb.AppendLine($"| {section.Title} | {section.Count} | {section.Priority} | {EscapeTable(section.Summary)} |");
                    }

                    foreach (var section in sections.Where(s => s.Lines.Count > 0))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"### {section.Title}");
                        foreach (var line in section.Lines)
                            sb.AppendLine($"- {line}");
                    }

                    if (sections.All(s => s.Count == 0))
                    {
                        sb.AppendLine();
                        sb.AppendLine("当前没有发现主要待办工作。建议用 check_colony 做一次原生警报检查。");
                    }

                    sb.AppendLine();
                    sb.AppendLine("说明：这是从地图状态、标记、工作单和角色状态汇总出的待办来源，不等同于 RimWorld 内部实时 Job 队列。");
                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"获取待办工作列表失败: {FormatExceptionChain(ex)}");
                }
            });
        }

        private static void AddIfNeeded(List<TodoSection> sections, string selected, string key, TodoSection section)
        {
            if (selected == "all" || selected == key)
                sections.Add(section);
        }

        private static TodoSection BuildConstructionSection(Map map, int maxItems)
        {
            var blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)?.Where(t => !t.Fogged()).ToList() ?? new List<Thing>();
            var frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)?.Where(t => !t.Fogged()).ToList() ?? new List<Thing>();
            var lines = new List<string>();

            foreach (var thing in blueprints.Concat(frames).Take(maxItems))
            {
                var pos = thing.Position;
                var label = GetBuildLabel(thing);
                var state = thing is Frame frame
                    ? $"框架 {frame.workDone:F0}/{GetBuildWorkTotal(thing):F0}"
                    : "蓝图";
                var missing = GetMissingMaterialsText(thing);
                lines.Add($"{state}: {label} ({pos.x},{pos.z}){missing}");
            }

            var count = blueprints.Count + frames.Count;
            var summary = count == 0 ? "无建造项目" : $"蓝图 {blueprints.Count}，框架 {frames.Count}";
            return new TodoSection("建造", count, count > 0 ? "高" : "-", summary, lines);
        }

        private static TodoSection BuildDesignationSection(Map map, int maxItems)
        {
            var designations = GetDesignations(map)
                .Where(d => !IsFogged(d, map))
                .OrderBy(d => d.def?.defName ?? "")
                .ThenBy(d => GetTargetLabel(d))
                .ToList();

            var grouped = designations
                .GroupBy(d => d.def?.label ?? d.def?.defName ?? "未知")
                .OrderByDescending(g => g.Count())
                .ToList();

            var lines = new List<string>();
            foreach (var group in grouped.Take(maxItems))
                lines.Add($"{group.Key}: {group.Count()} 项，示例 {FormatDesignationTarget(group.First())}");

            var summary = grouped.Count == 0
                ? "无地图标记"
                : string.Join(", ", grouped.Take(4).Select(g => $"{g.Key} {g.Count()}"));
            return new TodoSection("地图标记", designations.Count, designations.Count > 0 ? "中" : "-", summary, lines);
        }

        private static TodoSection BuildBillSection(Map map, int maxItems)
        {
            var entries = new List<string>();
            var active = 0;
            var suspended = 0;

            var tables = map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()
                .OrderBy(t => t.def?.defName ?? "")
                .ThenBy(t => t.thingIDNumber)
                .ToList();

            foreach (var table in tables)
            {
                var bills = table.billStack?.Bills;
                if (bills == null || bills.Count == 0) continue;

                foreach (var bill in bills)
                {
                    if (bill.suspended) suspended++;
                    else active++;

                    if (entries.Count >= maxItems) continue;
                    var tableLabel = table.def?.label ?? table.def?.defName ?? "工作台";
                    entries.Add($"{bill.Label} @ {tableLabel}: {FormatBillMode(bill)}");
                }
            }

            var count = active + suspended;
            var summary = count == 0 ? "无工作单" : $"运行 {active}，暂停 {suspended}";
            return new TodoSection("工作单", count, active > 0 ? "中" : "-", summary, entries);
        }

        private static TodoSection BuildMedicalSection(Map map, int maxItems)
        {
            var patients = map.mapPawns.AllPawnsSpawned
                .Where(p => IsPlayerManagedPawn(p) && !p.Dead && !p.Destroyed)
                .Select(p => new MedicalTodo(p, GetMedicalReason(p)))
                .Where(t => !string.IsNullOrEmpty(t.Reason))
                .OrderByDescending(t => t.Pawn.health?.hediffSet?.BleedRateTotal ?? 0f)
                .ThenBy(t => t.Pawn.LabelShort)
                .ToList();

            var lines = patients.Take(maxItems)
                .Select(t => $"{t.Pawn.LabelShort}(ID:{t.Pawn.thingIDNumber}): {t.Reason}")
                .ToList();

            var summary = patients.Count == 0 ? "无明显医疗待办" : $"{patients.Count} 名角色需要医疗关注";
            return new TodoSection("医疗", patients.Count, patients.Count > 0 ? "紧急" : "-", summary, lines);
        }

        private static TodoSection BuildPrisonerSection(Map map, int maxItems)
        {
            var prisoners = map.mapPawns.AllPawnsSpawned
                .Where(p => p.IsPrisonerOfColony && !p.Dead && !p.Destroyed)
                .OrderBy(p => p.LabelShort)
                .ToList();

            var lines = prisoners.Take(maxItems).Select(FormatPrisonerLine).ToList();
            var summary = prisoners.Count == 0 ? "无囚犯" : $"{prisoners.Count} 名囚犯需要看守/招募/转化";
            return new TodoSection("囚犯", prisoners.Count, prisoners.Count > 0 ? "中" : "-", summary, lines);
        }

        private static TodoSection BuildResearchSection(Map map, int maxItems)
        {
            var lines = new List<string>();
            var rm = Find.ResearchManager;
            var current = rm?.GetProject();
            if (current == null)
            {
                var available = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .Where(p => p.CanStartNow && !p.IsFinished)
                    .OrderBy(p => p.CostApparent)
                    .Take(maxItems)
                    .Select(p => $"{p.label}({p.defName}) 工作量 {p.CostApparent:F0}")
                    .ToList();
                lines.AddRange(available);
                var summary = available.Count == 0 ? "无当前研究，可研究项目也未发现" : "未设置当前研究";
                return new TodoSection("研究", available.Count, available.Count > 0 ? "高" : "-", summary, lines);
            }

            var progress = rm?.GetProgress(current) ?? 0f;
            var cost = current.Cost;
            var left = Math.Max(0f, cost - progress);
            lines.Add($"{current.label}({current.defName}): {progress:F0}/{cost:F0}，剩余 {left:F0}");

            var researchers = map.mapPawns.FreeColonistsSpawned
                .Where(p => CanDoWork(p, "Research"))
                .Select(p => $"{p.LabelShort}(优先级 {GetWorkPriority(p, "Research")})")
                .Take(maxItems)
                .ToList();
            if (researchers.Count > 0)
                lines.Add($"可研究人员: {string.Join(", ", researchers)}");

            return new TodoSection("研究", 1, "中", $"当前 {current.label}，剩余 {left:F0}", lines);
        }

        private static TodoSection BuildHaulingSection(Map map, int maxItems)
        {
            var haulables = map.listerThings.AllThings
                .Where(t => t.Spawned && !t.Destroyed && !t.Fogged() && IsHaulable(t))
                .Where(t => !t.IsForbidden(Faction.OfPlayer))
                .Where(t => !(map.zoneManager.ZoneAt(t.Position) is Zone_Stockpile))
                .OrderBy(t => t.def?.label ?? "")
                .ThenBy(t => t.Position.x)
                .ThenBy(t => t.Position.z)
                .ToList();

            var grouped = haulables
                .GroupBy(t => t.def?.label ?? t.def?.defName ?? "未知")
                .OrderByDescending(g => g.Sum(t => t.stackCount))
                .Take(maxItems)
                .Select(g => $"{g.Key}: {g.Sum(t => t.stackCount)} 个，{g.Count()} 堆")
                .ToList();

            var summary = haulables.Count == 0 ? "无明显露天/区外搬运压力" : $"{haulables.Count} 堆物品在存储区外";
            return new TodoSection("搬运", haulables.Count, haulables.Count > 0 ? "低" : "-", summary, grouped);
        }

        private static TodoSection BuildIdleSection(Map map, int maxItems)
        {
            var idle = map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState && p.CurJob == null)
                .OrderBy(p => p.LabelShort)
                .ToList();

            var lines = idle.Take(maxItems).Select(p => $"{p.LabelShort}(ID:{p.thingIDNumber}) 当前无工作").ToList();
            var summary = idle.Count == 0 ? "无空闲殖民者" : $"{idle.Count} 名殖民者空闲";
            return new TodoSection("空闲", idle.Count, idle.Count > 0 ? "中" : "-", summary, lines);
        }

        private static IEnumerable<Designation> GetDesignations(Map map)
        {
            try
            {
                return map.designationManager.AllDesignations ?? Enumerable.Empty<Designation>();
            }
            catch
            {
                return Enumerable.Empty<Designation>();
            }
        }

        private static bool IsFogged(Designation designation, Map map)
        {
            if (designation.target.HasThing)
                return designation.target.Thing?.Fogged() == true;
            return designation.target.Cell.InBounds(map) && designation.target.Cell.Fogged(map);
        }

        private static string FormatDesignationTarget(Designation designation)
        {
            if (designation.target.HasThing)
            {
                var thing = designation.target.Thing;
                if (thing == null) return "未知目标";
                return $"{thing.LabelShort}({thing.Position.x},{thing.Position.z})";
            }

            var cell = designation.target.Cell;
            return $"({cell.x},{cell.z})";
        }

        private static string GetTargetLabel(Designation designation)
        {
            if (designation.target.HasThing)
                return designation.target.Thing?.LabelShort ?? "";
            var cell = designation.target.Cell;
            return $"{cell.x},{cell.z}";
        }

        private static string GetBuildLabel(Thing thing)
        {
            var entityDef = thing.def?.entityDefToBuild;
            var label = entityDef?.label ?? entityDef?.defName ?? thing.LabelShort;
            var stuff = thing.Stuff?.label;
            return string.IsNullOrEmpty(stuff) ? label : $"{stuff}{label}";
        }

        private static float GetBuildWorkTotal(Thing thing)
        {
            var entityDef = thing.def?.entityDefToBuild;
            return entityDef?.GetStatValueAbstract(StatDefOf.WorkToBuild, thing.Stuff) ?? 0f;
        }

        private static string GetMissingMaterialsText(Thing thing)
        {
            if (!(thing is IConstructible constructible)) return "";
            var costs = constructible.TotalMaterialCost();
            if (costs == null) return "";

            var missing = new List<string>();
            foreach (var cost in costs)
            {
                var needed = constructible.ThingCountNeeded(cost.thingDef);
                if (needed > 0)
                    missing.Add($"{cost.thingDef.label}x{needed}");
            }
            return missing.Count == 0 ? "" : $"，缺 {string.Join(", ", missing)}";
        }

        private static string FormatBillMode(Bill bill)
        {
            if (bill.suspended) return "暂停";
            if (!(bill is Bill_Production production)) return "运行";
            if (production.paused) return "手动暂停";
            if (production.repeatMode == BillRepeatModeDefOf.Forever) return "永久重复";
            if (production.repeatMode == BillRepeatModeDefOf.RepeatCount) return $"重复 {production.repeatCount}/{production.targetCount}";
            if (production.repeatMode == BillRepeatModeDefOf.TargetCount) return $"做到 {production.targetCount} 件";
            return production.repeatMode?.label ?? "运行";
        }

        private static bool IsPlayerManagedPawn(Pawn pawn)
        {
            if (pawn.Faction == Faction.OfPlayer) return true;
            return pawn.IsPrisonerOfColony || pawn.RaceProps?.Animal == true && pawn.Faction == Faction.OfPlayer;
        }

        private static string GetMedicalReason(Pawn pawn)
        {
            var parts = new List<string>();
            var bleed = pawn.health?.hediffSet?.BleedRateTotal ?? 0f;
            if (bleed > 0.01f) parts.Add($"流血 {bleed * 100f:F0}%/天");
            if (pawn.Downed) parts.Add("倒地");
            try
            {
                if (HealthAIUtility.ShouldSeekMedicalRest(pawn))
                    parts.Add("应卧床休养");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WorkTodos] 判断医疗卧床失败: {ex.Message}");
            }

            var serious = pawn.health?.hediffSet?.hediffs?
                .Where(h => h.Visible && h.def.isBad && h.Severity > 0.25f)
                .Select(h => h.Label)
                .Take(3)
                .ToList() ?? new List<string>();
            if (serious.Count > 0)
                parts.Add($"严重状态: {string.Join(", ", serious)}");

            return string.Join("；", parts.Distinct());
        }

        private static string FormatPrisonerLine(Pawn pawn)
        {
            var mode = pawn.guest?.ExclusiveInteractionMode?.label ?? "无互动";
            var recruitable = pawn.guest?.Recruitable == true ? "可招募" : "不可招募";
            var resistance = pawn.guest != null ? $"抵抗 {pawn.guest.resistance:F1}" : "";
            var will = pawn.guest != null ? $"意志 {pawn.guest.will:F1}" : "";
            return $"{pawn.LabelShort}(ID:{pawn.thingIDNumber}): {mode}，{recruitable}，{resistance}，{will}";
        }

        private static bool CanDoWork(Pawn pawn, string workTypeDefName)
        {
            return GetWorkPriority(pawn, workTypeDefName) > 0;
        }

        private static int GetWorkPriority(Pawn pawn, string workTypeDefName)
        {
            var workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workTypeDefName);
            if (workType == null || pawn.workSettings == null) return 0;
            return pawn.workSettings.GetPriority(workType);
        }

        private static bool IsHaulable(Thing thing)
        {
            var def = thing.def;
            if (def == null) return false;
            if (thing is Pawn) return false;
            if (thing.stackCount <= 0) return false;
            return def.EverHaulable;
        }

        private static string EscapeTable(string value)
        {
            return (value ?? "").Replace("|", "/");
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" <- {inner.GetType().Name}: {inner.Message}";
            return message;
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;

        private sealed class TodoSection
        {
            public TodoSection(string title, int count, string priority, string summary, List<string> lines)
            {
                Title = title;
                Count = count;
                Priority = priority;
                Summary = summary;
                Lines = lines;
            }

            public string Title { get; }
            public int Count { get; }
            public string Priority { get; }
            public string Summary { get; }
            public List<string> Lines { get; }
        }

        private sealed class MedicalTodo
        {
            public MedicalTodo(Pawn pawn, string reason)
            {
                Pawn = pawn;
                Reason = reason;
            }

            public Pawn Pawn { get; }
            public string Reason { get; }
        }
    }
}
