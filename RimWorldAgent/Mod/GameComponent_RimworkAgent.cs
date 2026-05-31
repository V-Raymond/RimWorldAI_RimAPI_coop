using System;
using System.IO;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using Verse;

namespace RimWorldAgent
{
    public class GameComponent_RimWorldAgent : GameComponent
    {
        private AgentEngine? _engine;
        private ScribeDbStore? _dbStore;
        private bool _initialized;
        private int _lastTick;

        public GameComponent_RimWorldAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            if (_initialized) return;
            var settings = RimWorldAgentMod.Instance?.Settings;
            if (settings != null && !settings.AgentAutoRun) return;
            _initialized = true;

            var modRoot = Path.GetDirectoryName(
                typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var defaultProjectPath = Path.Combine(modRoot, "claude-sessions", "rimworld-agent");
            var projectPath = !string.IsNullOrEmpty(settings?.ProjectPath)
                ? Path.Combine(modRoot, settings!.ProjectPath)
                : defaultProjectPath;

            var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                ? Path.Combine(modRoot, settings!.SkillsDir)
                : Path.GetFullPath(Path.Combine(modRoot, "Skills"));

            var asmDir = Path.GetDirectoryName(
                typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var ccbDir = Path.GetFullPath(Path.Combine(asmDir, "cc-companion"));

            var gameHost = settings?.GameMcpHost ?? "localhost";
            var gamePort = settings?.GameMcpPort ?? 9877;

            var dbStore = new ScribeDbStore();
            var gameState = new DirectGameStateProvider();

            var cfg = new AgentEngineConfig
            {
                ProjectPath = projectPath,
                SkillsDir = skillsDir,
                McpUrl = $"http://{gameHost}:{gamePort}",
                McpPort = gamePort,
                AgentMcpPort = settings?.AgentMcpPort ?? 9878,
                CcbPort = 19999,
                CcbWsUrl = "ws://127.0.0.1:19999",
                ModelName = settings?.ModelName,
                CcbAutoStart = true,
                CcbAutoInstall = settings?.CcbAutoInstall ?? true,
                CcbDir = ccbDir,
                PlanSpeed = settings?.PlanSpeed ?? "paused",
                TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                ThinkingMode = settings?.ThinkingMode ?? "default",
                ThinkingEffort = settings?.ThinkingEffort ?? "medium",
                MaxThinkingTokens = settings?.MaxThinkingTokens ?? 0,
            };

            // log 回调可能从后台线程（CCB stdout/stderr、WS ReceiveLoop、MCP HTTP）触发，
            // 直接写 Verse.Log 会和 EditWindow_Log 的渲染迭代器冲突 → Collection was modified
            _engine = new AgentEngine(cfg, dbStore, gameState,
                logInfo: msg => LongEventHandler.ExecuteWhenFinished(() => Log.Message($"[agent-core] {msg}")),
                logError: msg => LongEventHandler.ExecuteWhenFinished(() => Log.Error($"[agent-core] {msg}")),
                logDebug: msg => LongEventHandler.ExecuteWhenFinished(() => Log.Message($"[agent-core] {msg}")));

            await _engine.InitAsync();
            _dbStore = dbStore;

            // 注入 CcbWebSocket 到 CCClient（供 UI 使用）
            if (_engine.CcbWs != null)
            {
                CCClient.SetSocket(_engine.CcbWs);
                WireChatDisplayUi(_engine.CcbWs);
            }

            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        /// <summary>
        /// 将 CcbWebSocket 事件桥接到 ChatDisplayState。
        /// CcbWebSocket.ReceiveLoop 在后台线程运行，通过 EnqueueUiEvent 入队，
        /// 由 Dialog_AiChat.DoWindowContents 在 UI 线程消费。
        /// </summary>
        private static void WireChatDisplayUi(CcbWebSocket ws)
        {
            ws.OnAssistantText += text =>
                ChatDisplayState.EnqueueUiEvent(() =>
                    ChatDisplayState.OnAssistantText(text));

            ws.OnToolUse += (toolId, toolName, input) =>
                ChatDisplayState.EnqueueUiEvent(() =>
                    ChatDisplayState.AddToolCall(toolId, toolName, input));

            ws.OnResult += (subtype, _) =>
                ChatDisplayState.EnqueueUiEvent(() =>
                {
                    ChatDisplayState.FinishStreaming();
                    if (TokenUsageTracker.TotalAllTokens > 0)
                    {
                        var limit = RimWorldAgentMod.Instance?.Settings?.TokenBudgetLimit ?? 0;
                        ChatDisplayState.CurrentBudgetStatus = limit > 0
                            ? TokenUsageTracker.CheckBudget(limit)
                            : BudgetStatus.Ok;
                    }
                });

            ws.OnAborted += () =>
                ChatDisplayState.EnqueueUiEvent(() =>
                    ChatDisplayState.MarkLastAborted());
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (!_initialized || _engine == null) return;

            _engine.Tick();

            if (Find.CurrentMap == null) return;

            _lastTick++;
            if (_lastTick < 125) return; // ~2000ms @60fps
            _lastTick = 0;
            _ = AgentTickAsync();
        }

        private async Task AgentTickAsync()
        {
            if (_engine == null) return;
            await _engine.TickAsync();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            _dbStore?.ScribeExpose();
        }
    }
}
