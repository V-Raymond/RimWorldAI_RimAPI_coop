using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimWorldMCP.Transport;

namespace RimWorldMCP
{
    public static class McpEventMonitor
    {
        private static int _nextCheckTick;
        private const int CheckIntervalTicks = 120; // 每 2 秒检查一次
        private static int _lastColonistCount = -1;
        private static int _lastIdleCount = -1;
        private static bool _lastRaidActive;
        private static bool _lastFireActive;

        /// <summary>主线程调用。检测游戏事件并通过 transport 推送给已连接客户端。</summary>
        public static void Tick(ITransport? transport)
        {
            if (transport == null) return;
            var tick = Find.TickManager?.TicksGame ?? 0;
            if (tick < _nextCheckTick) return;
            _nextCheckTick = tick + CheckIntervalTicks;

            var map = Find.CurrentMap;
            if (map == null) return;

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int colonistCount = colonists.Count;

            // 1. 袭击检测
            bool raidActive = map.attackTargetsCache?.TargetsHostileToFaction(Faction.OfPlayer)?.Any() ?? false;
            if (raidActive && !_lastRaidActive)
            {
                SendEvent(transport, "raid_started",
                    $"⚠ 袭击！{colonistCount} 名殖民者需要立即征召防御！");
            }
            _lastRaidActive = raidActive;

            // 2. 火灾检测
            bool fireActive = map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).Count > 0;
            if (fireActive && !_lastFireActive)
            {
                SendEvent(transport, "fire_started",
                    $"⚠ 火灾！地图上有火势蔓延，立即派遣灭火！");
            }
            _lastFireActive = fireActive;

            // 3. 空闲殖民者
            int idleCount = colonists.Count(c =>
                (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                && !c.Downed && !c.Deathresting);
            if (idleCount > _lastIdleCount && idleCount > 0)
            {
                var idleNames = colonists
                    .Where(c => (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                        && !c.Downed && !c.Deathresting)
                    .Take(5)
                    .Select(c => c.Name.ToStringShort);
                SendEvent(transport, "idle_colonists",
                    $"{(idleCount > 1 ? $"{idleCount} 名" : "")}殖民者空闲: {string.Join(", ", idleNames)}");
            }
            _lastIdleCount = idleCount;

            // 4. 殖民者数量变化（死亡/加入）
            if (colonistCount != _lastColonistCount && _lastColonistCount >= 0)
            {
                int diff = colonistCount - _lastColonistCount;
                var word = diff > 0 ? $"增加 {diff} 人" : $"减少 {-diff} 人";
                SendEvent(transport, "colonist_count_changed",
                    $"殖民者数量变化: {_lastColonistCount} → {colonistCount} ({word})");
            }
            _lastColonistCount = colonistCount;
        }

        private static void SendEvent(ITransport transport, string eventType, string message)
        {
            var json = $"{{\"event\":\"{eventType}\",\"message\":\"{message}\",\"ts\":\"{System.DateTime.Now:HH:mm:ss}\"}}";
            transport.SendAsync(json);
            McpLog.Info($"[event] {eventType}: {message}");
        }
    }
}
