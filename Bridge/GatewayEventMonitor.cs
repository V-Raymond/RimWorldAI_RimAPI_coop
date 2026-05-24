using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using RimWorld;

namespace RimWorldMCP
{
    public static class GatewayEventMonitor
    {
        private static int _nextCheckTick;
        private const int CheckIntervalTicks = 120;
        private static int _lastColonistCount = -1;
        private static int _lastIdleCount = -1;
        private static bool _lastRaidActive;
        private static bool _lastFireActive;

        public static void Tick()
        {
            if (!GatewayClient.IsConnected) return;
            var tick = Find.TickManager?.TicksGame ?? 0;
            if (tick < _nextCheckTick) return;
            _nextCheckTick = tick + CheckIntervalTicks;

            var map = Find.CurrentMap;
            if (map == null) return;

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int colonistCount = colonists.Count;

            // === 1. 袭击检测（上升沿 + 下降沿） ===
            bool raidActive = map.attackTargetsCache?.TargetsHostileToFaction(Faction.OfPlayer)?.Any() ?? false;

            if (raidActive && !_lastRaidActive)
            {
                var msg = BuildRaidStartMessage(map, colonistCount, colonists);
                GatewayMessageQueue.SendNow(MessageCategory.RaidStart, msg);
            }
            else if (!raidActive && _lastRaidActive)
            {
                var msg = BuildRaidEndMessage(map, colonistCount, colonists);
                GatewayMessageQueue.Enqueue(MessageCategory.RaidEnd, msg);
            }
            _lastRaidActive = raidActive;

            // === 2. 火灾检测（上升沿） ===
            bool fireActive = map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).Count > 0;
            if (fireActive && !_lastFireActive)
            {
                var msg = BuildFireMessage(colonistCount);
                GatewayMessageQueue.Enqueue(MessageCategory.RaidEnd, msg); // 火灾重不重要？和 RaidEnd 同级
            }
            _lastFireActive = fireActive;

            // === 3. 空闲殖民者检测 ===
            int idleCount = colonists.Count(c =>
                (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                && !c.Downed && !c.Deathresting);
            bool hasNewIdle = idleCount > _lastIdleCount && idleCount > 0;
            _lastIdleCount = idleCount;

            // === 4. 殖民者数量变化 ===
            bool countChanged = colonistCount != _lastColonistCount && _lastColonistCount >= 0;
            _lastColonistCount = colonistCount;

            // === 5. 综合警报 ===
            var alerts = BuildAlertLines(map, colonists, colonistCount);
            // 合并空闲/数量变化到警报
            if (hasNewIdle)
            {
                var names = colonists
                    .Where(c => (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                        && !c.Downed && !c.Deathresting)
                    .Take(5).Select(c => c.Name.ToStringShort);
                alerts.Add($"{(idleCount > 1 ? $"{idleCount} 名" : "")}殖民者空闲: {string.Join(", ", names)}");
            }
            if (countChanged)
            {
                int diff = colonistCount - _lastColonistCount;
                alerts.Add($"殖民者数量: {_lastColonistCount} → {colonistCount} ({(diff > 0 ? "+" : "")}{diff})");
            }

            if (alerts.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("## ⚠ 殖民地警报");
                foreach (var a in alerts)
                    sb.AppendLine($"- {a}");
                sb.Append(BuildColonySummary(map, colonists, colonistCount));
                GatewayMessageQueue.Enqueue(MessageCategory.Alert, sb.ToString().TrimEnd());
            }

            // === 6. 早报（游戏时间每天早上 6 点） ===
            int hour = GenLocalDate.HourOfDay(map);
            int day = tick / 60000;
            if (hour == 6 && !GatewayMessageQueue.WasDailySentToday(day))
            {
                GatewayMessageQueue.MarkDailySent(day);
                var msg = BuildDailyOverview(map, colonists, colonistCount, tick);
                GatewayMessageQueue.Enqueue(MessageCategory.DailyMorning, msg);
            }
        }

        // ========== 消息构建 ==========

        private static string BuildDailyOverview(Map map, List<Pawn> colonists, int colonistCount, int ticksGame)
        {
            var sb = new StringBuilder();
            int day = ticksGame / 60000;
            int year = day / 15 + 1;
            int dayOfYear = day % 15 + 1;

            var season = GenLocalDate.Season(map);
            string seasonStr = season switch
            {
                Season.Spring => "春", Season.Summer => "夏",
                Season.Fall => "秋", Season.Winter => "冬", _ => "?"
            };
            sb.AppendLine($"## 每早汇报 第{year}年 {seasonStr}季 第{dayOfYear}天");

            // 天气
            var weather = map.weatherManager?.curWeather;
            float temp = map.mapTemperature?.OutdoorTemp ?? 0f;
            sb.AppendLine($"天气: {weather?.label ?? "?"}, 室外 {temp:F0}°C");

            // 殖民者
            float avgMood = colonists.Count > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f
                : 0f;
            sb.AppendLine($"殖民者: {colonistCount} 人 | 平均心情 {avgMood:F0}%");

            // 资源
            int steel = GetCountByDefName(map, "Steel");
            int wood = GetCountByDefName(map, "WoodLog");
            int components = GetCountByDefName(map, "ComponentIndustrial");
            int silver = GetCountByDefName(map, "Silver");
            int foodDays = CalcFoodDays(map, colonistCount);
            sb.AppendLine($"资源: 钢{steel} 木{wood} 零件{components} 银{silver} | 食物约{foodDays}天");

            // 电力
            float generated = 0, used = 0, stored = 0;
            foreach (var net in map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>())
            {
                foreach (var comp in net.powerComps)
                {
                    if (!comp.PowerOn) continue;
                    float rate = comp.EnergyOutputPerTick;
                    if (rate > 0) generated += rate; else used += -rate;
                }
                stored += net.CurrentStoredEnergy();
            }
            string powerLabel = generated >= used ? "盈余" : "赤字";
            sb.AppendLine($"电力: 发{generated / 1000f:F0}kW 用{used / 1000f:F0}kW 储{stored / 1000f:F0}kWd ({powerLabel})");

            // 研究
            var rm = Find.ResearchManager;
            var curProj = rm?.GetProject();
            if (curProj != null)
                sb.AppendLine($"研究: {curProj.label} ({rm!.GetProgress(curProj) * 100f:F0}%)");
            else
                sb.AppendLine("研究: 无");

            // 财富
            float wealth = map.wealthWatcher?.WealthTotal ?? 0f;
            sb.AppendLine($"财富: {wealth:N0}");

            // 警报
            var alerts = BuildAlertLines(map, colonists, colonistCount);
            if (alerts.Count > 0)
            {
                sb.AppendLine("警报:");
                foreach (var a in alerts)
                    sb.AppendLine($"  - {a}");
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildRaidStartMessage(Map map, int colonistCount, List<Pawn> colonists)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## ⚠ 袭击开始！{colonistCount} 名殖民者需要立即征召防御！");

            int drafted = colonists.Count(c => c.Drafted);
            int withWeapon = colonists.Count(c => c.equipment?.Primary != null);
            int turrets = map.listerBuildings.AllBuildingsColonistOfClass<Building_Turret>().Count();
            int traps = map.listerBuildings.AllBuildingsColonistOfClass<Building_Trap>().Count();

            sb.Append($"防御: 已征召{drafted}/{colonistCount}");
            sb.AppendLine($" | 有武器{withWeapon} | 炮塔{turrets} | 陷阱{traps}");

            sb.Append(BuildColonySummary(map, colonists, colonistCount));
            return sb.ToString().TrimEnd();
        }

        private static string BuildRaidEndMessage(Map map, int colonistCount, List<Pawn> colonists)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 袭击结束");

            int downed = colonists.Count(c => c.Downed);
            int bleeding = colonists.Count(c => (c.health?.hediffSet?.BleedRateTotal ?? 0f) > 0.3f);
            if (downed > 0 || bleeding > 0)
            {
                sb.Append("伤亡: ");
                if (downed > 0) sb.Append($"倒地{downed} ");
                if (bleeding > 0) sb.Append($"流血{bleeding}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("无殖民者伤亡");
            }

            sb.Append(BuildColonySummary(map, colonists, colonistCount));
            return sb.ToString().TrimEnd();
        }

        private static string BuildFireMessage(int colonistCount)
        {
            return $"⚠ 火灾！{colonistCount}名殖民者需要立即灭火！";
        }

        /// <summary>殖民地概要（附加在警报/袭击消息末尾）</summary>
        private static string BuildColonySummary(Map map, List<Pawn> colonists, int colonistCount)
        {
            var sb = new StringBuilder();
            int foodDays = CalcFoodDays(map, colonistCount);
            float avgMood = colonists.Count > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f
                : 0f;

            sb.AppendLine($"---");
            sb.AppendLine($"殖民者: {colonistCount}人 | 心情: {avgMood:F0}% | 食物: {foodDays}天");

            int steel = GetCountByDefName(map, "Steel");
            int components = GetCountByDefName(map, "ComponentIndustrial");
            sb.AppendLine($"钢{steel} | 零件{components}");

            return sb.ToString();
        }

        // ========== 警报提取（check_colony 同款逻辑） ==========

        private static List<string> BuildAlertLines(Map map, List<Pawn> colonists, int colonistCount)
        {
            var alerts = new List<string>();

            // 崩溃风险
            var breakRisks = colonists
                .Where(c => (c.needs?.mood?.CurLevelPercentage ?? 1f) < 0.2f)
                .Select(c => $"崩溃风险: {c.Name.ToStringShort} 心情{(c.needs!.mood!.CurLevelPercentage * 100f):F0}%")
                .ToList();
            alerts.AddRange(breakRisks);

            // 严重流血
            var bleeders = colonists
                .Where(c => (c.health?.hediffSet?.BleedRateTotal ?? 0f) > 0.3f)
                .Select(c => $"严重流血: {c.Name.ToStringShort} 失血率{(c.health!.hediffSet.BleedRateTotal * 100f):F0}%/天")
                .ToList();
            alerts.AddRange(bleeders);

            // 食物不足
            int foodDays = CalcFoodDays(map, colonistCount);
            if (foodDays < 3 && colonistCount > 0)
                alerts.Add($"食物不足: 仅够 {foodDays} 天");

            // 无防御
            int turrets = map.listerBuildings.AllBuildingsColonistOfClass<Building_Turret>().Count();
            int traps = map.listerBuildings.AllBuildingsColonistOfClass<Building_Trap>().Count();
            float wealth = map.wealthWatcher?.WealthTotal ?? 0f;
            if (turrets == 0 && traps == 0 && wealth > 15000)
                alerts.Add($"无防御工事 (财富{wealth:N0})");

            // 缺床
            int beds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>()
                .Count(b => !b.ForPrisoners && !b.Medical);
            if (colonistCount > beds)
                alerts.Add($"缺床: {colonistCount}人仅{beds}张");

            return alerts;
        }

        // ========== 工具方法 ==========

        private static int GetCountByDefName(Map map, string defName)
        {
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources == null) return 0;
            foreach (var kv in resources)
                if (kv.Key.defName == defName)
                    return kv.Value;
            return 0;
        }

        private static int CalcFoodDays(Map map, int colonistCount)
        {
            if (colonistCount <= 0) return 999;
            float total = 0f;
            foreach (var kv in map.resourceCounter?.AllCountedAmounts ?? new Dictionary<ThingDef, int>())
            {
                var def = kv.Key;
                if (def.IsNutritionGivingIngestible && def.ingestible?.HumanEdible == true
                    && (def.ingestible?.foodType & FoodTypeFlags.Tree) == 0)
                    total += kv.Value * (def.ingestible?.CachedNutrition ?? 0f);
            }
            return (int)(total / (colonistCount * 1.6f));
        }
    }
}
