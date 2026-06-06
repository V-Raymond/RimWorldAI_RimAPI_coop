using System;
using RimWorldAgent.Core.CcbManager;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public class RimWorldAgentMod : Mod
    {
        public static RimWorldAgentMod Instance { get; private set; } = null!;
        public AgentModSettings Settings { get; private set; }
        private Vector2 _scrollPos;

        public RimWorldAgentMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AgentModSettings>();
        }

        public override string SettingsCategory() => "RimWorld Agent";

        private static void DrawSectionHeader(Listing_Standard listing, string title)
        {
            listing.Gap(4f);
            var rect = listing.GetRect(22f);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y + 10f, rect.width, 1f),
                new Color(0.25f, 0.25f, 0.3f, 0.6f));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.45f, 0.5f, 0.6f, 1f);
            Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width, 18f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            listing.Gap(2f);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var h = 980f;
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, h);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            if (Find.CurrentMap != null)
            {
                GUI.color = Color.yellow;
                listing.Label("设置仅在主菜单生效，游戏内仅可查看。");
                GUI.color = Color.white;
                listing.Gap(8f);
            }

            // ==================== MCP 服务 ====================
            DrawSectionHeader(listing, "MCP 服务");

            listing.Label("游戏 MCP 服务地址");
            Settings.GameMcpHost = listing.TextEntry(Settings.GameMcpHost);

            listing.Label("游戏 MCP 端口");
            var gamePortStr = listing.TextEntry(Settings.GameMcpPort.ToString());
            if (int.TryParse(gamePortStr, out int gamePort) && gamePort > 0 && gamePort <= 65535)
                Settings.GameMcpPort = gamePort;

            listing.Label("Agent MCP 端口 (SDK 连接)");
            var agentPortStr = listing.TextEntry(Settings.AgentMcpPort.ToString());
            if (int.TryParse(agentPortStr, out int agentPort) && agentPort > 0 && agentPort <= 65535)
                Settings.AgentMcpPort = agentPort;

            // ==================== 模型与思考 ====================
            DrawSectionHeader(listing, "模型与思考");

            listing.Label("模型名称 (如 claude-sonnet-4-6)");
            Settings.ModelName = listing.TextEntry(Settings.ModelName);

            var modeLabels = new[] { "adaptive (引导深度)", "disabled (禁用思考)" };
            var modeValues = new[] { "adaptive", "disabled" };
            var modeIdx = Array.IndexOf(modeValues, Settings.ThinkingMode);
            if (modeIdx < 0) modeIdx = 0;
            if (listing.ButtonText($"思考模式: {modeLabels[modeIdx]}"))
            {
                modeIdx = (modeIdx + 1) % modeValues.Length;
                Settings.ThinkingMode = modeValues[modeIdx];
            }

            listing.Gap(4f);
            var effortLabels = new[] { "low (低)", "medium (中)", "high (高)", "xhigh (极高)", "max (最大)" };
            var effortValues = new[] { "low", "medium", "high", "xhigh", "max" };
            var effortIdx = Array.IndexOf(effortValues, Settings.ThinkingEffort);
            if (effortIdx < 0) effortIdx = 2; // 默认 "high"
            if (listing.ButtonText($"思考力度: {effortLabels[effortIdx]}"))
            {
                effortIdx = (effortIdx + 1) % effortValues.Length;
                Settings.ThinkingEffort = effortValues[effortIdx];
            }

            // ==================== Token 预算 ====================
            DrawSectionHeader(listing, "Token 预算");

            listing.Label("预算上限 (K, 0=不限制)");
            var limitKStr = listing.TextEntry((Settings.TokenBudgetLimit / 1000).ToString());
            if (long.TryParse(limitKStr, out long limitK) && limitK >= 0)
                Settings.TokenBudgetLimit = limitK * 1000;

            var actionLabels = new[] { "Block (阻止)", "Warn (警告)" };
            var actionValues = new[] { "Block", "Warn" };
            var actionIdx = Array.IndexOf(actionValues, Settings.TokenBudgetAction);
            if (actionIdx < 0) actionIdx = 0;
            if (listing.ButtonText($"超出行为: {actionLabels[actionIdx]}"))
            {
                actionIdx = (actionIdx + 1) % actionValues.Length;
                Settings.TokenBudgetAction = actionValues[actionIdx];
            }

            listing.Gap(4f);
            var usage = TokenUsageTracker.GetCompactDisplay(Settings.TokenBudgetLimit);
            GUI.color = new Color(0.6f, 0.65f, 0.75f, 1f);
            listing.Label($"累计: {usage}");
            GUI.color = Color.white;

            // ==================== Agent 行为 ====================
            DrawSectionHeader(listing, "Agent 行为");

            listing.CheckboxLabeled("自动运行 Agent", ref Settings.AgentAutoRun,
                "开启后加载存档时自动启动。");

            var speedLabels = new[] { "paused (暂停)", "normal (1x)", "fast (2x)", "superfast (3x)", "ultrafast (最快)" };
            var speedValues = new[] { "paused", "normal", "fast", "superfast", "ultrafast" };
            var speedIdx = Array.IndexOf(speedValues, Settings.PlanSpeed);
            if (speedIdx < 0) speedIdx = 0;
            if (listing.ButtonText($"Plan 阶段速度: {speedLabels[speedIdx]}"))
            {
                speedIdx = (speedIdx + 1) % speedValues.Length;
                Settings.PlanSpeed = speedValues[speedIdx];
            }

            listing.Label("Skills 目录 (留空用默认)");
            Settings.SkillsDir = listing.TextEntry(Settings.SkillsDir);

            listing.Label("Project 目录 (留空用默认)");
            Settings.ProjectPath = listing.TextEntry(Settings.ProjectPath);

            // ==================== UI Bridge ====================
            DrawSectionHeader(listing, "UI 桥接 (WebSocket)");

            listing.Label("监听地址");
            Settings.BridgeHost = listing.TextEntry(Settings.BridgeHost);

            listing.Label("监听端口");
            var bpStr = listing.TextEntry(Settings.BridgePort.ToString());
            if (int.TryParse(bpStr, out int bp) && bp > 0 && bp <= 65535)
                Settings.BridgePort = bp;

            // ==================== CC Companion ====================
            DrawSectionHeader(listing, "CC Companion 依赖");

            var asmDir = System.IO.Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
            var ccDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(asmDir, "cc-companion"));

            var installed = CompanionInstaller.IsInstalled(ccDir);
            var installing = CompanionInstaller.IsInstalling;
            var status = CompanionInstaller.InstallStatus;

            if (installing)
            {
                listing.Label("  状态: 安装中...");
                if (!string.IsNullOrEmpty(status)) listing.Label($"    {status}");
            }
            else if (installed)
            {
                listing.Label("  状态: 已安装 (node_modules 就绪)");
                if (listing.ButtonText("  重新安装 (npm install)"))
                    CompanionInstaller.Install(ccDir);
                if (listing.ButtonText("  卸载 (删除 node_modules)"))
                    CompanionInstaller.Uninstall(ccDir);
            }
            else
            {
                listing.Label($"  状态: 未安装{(string.IsNullOrEmpty(status) ? "" : $" ({status})")}");
                if (!installing && listing.ButtonText("  安装 (npm install)"))
                    CompanionInstaller.Install(ccDir);
            }

            listing.CheckboxLabeled("自动安装 (加载时)", ref Settings.CcbAutoInstall,
                "开启后自动检查 cc-companion/node_modules，缺失则运行 npm install。");

            DrawSectionHeader(listing, "日志");

            listing.CheckboxLabeled("☐ SDK 交互日志 (sdk-log.txt)", ref Settings.LogSdkMessages,
                "开启后 companion 将 SDK 双向通信记录写入 project 目录下的 sdk-log.txt。");
            listing.CheckboxLabeled("☐ C#↔CCB WS 日志 (ccb-ws-log.txt)", ref Settings.LogCcbWsMessages,
                "开启后 C# 将 WebSocket 收发 JSON 记录写入 project 目录下的 ccb-ws-log.txt。");

            listing.End();
            Widgets.EndScrollView();
        }
    }
}
