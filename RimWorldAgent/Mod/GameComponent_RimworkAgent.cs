using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimWorldAgent.Core;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using Verse;

namespace RimWorldAgent
{
    public class GameComponent_RimWorldAgent : GameComponent
    {
        private AgentEngine? _engine;
        private ScribeDbStore? _dbStore;
        private IConversationStore? _convStore;
        private bool _initialized;
        private int _lastTick;

        public GameComponent_RimWorldAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            try
            {
                ShutdownEngine();

                var settings = RimWorldAgentMod.Instance?.Settings;
                if (settings != null && !settings.AgentAutoRun)
                {
                    CoreLog.Info("[agent-mod] AgentAutoRun=false，跳过初始化");
                    return;
                }
                _initialized = true;

                ToolDispatcher.ResetTaskCount();

                var modRoot = Path.GetDirectoryName(
                    typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
                CoreLog.Info($"[agent-mod] DLL 路径 = {modRoot}");
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
                    CcbPort = 19998,
                    CcbWsUrl = "ws://127.0.0.1:19998",
                    ModelName = settings?.ModelName,
                    CcbAutoStart = true,
                    CcbAutoInstall = settings?.CcbAutoInstall ?? true,
                    CcbDir = ccbDir,
                    PlanSpeed = settings?.PlanSpeed ?? "paused",
                    TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                    ThinkingMode = settings?.ThinkingMode ?? "adaptive",
                    ThinkingEffort = settings?.ThinkingEffort ?? "high",
                };

                _engine = new AgentEngine(cfg, dbStore, gameState,
                    logInfo: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logError: msg => SafeLog.Error($"[agent-core] {msg}"),
                    logDebug: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logWarn: msg => SafeLog.Warning($"[agent-core] {msg}"));

                await _engine.InitAsync();
                CoreLog.Info($"[agent-mod] 内部工具已注册 ({InternalToolRegistry.Instance.All.Count}): {string.Join(", ", InternalToolRegistry.Instance.All.Select(t => t.Name))}");

                _dbStore = dbStore;

                if (settings?.BridgeHost != "disabled")
                {
                    var bridgeHost = settings?.BridgeHost ?? "0.0.0.0";
                    var bridgePort = settings?.BridgePort ?? 19999;
                    UIMessageBus.Start(bridgeHost, bridgePort);
                }

                if (_engine.CcbWs != null)
                {
                    _convStore = new MemoryConversationStore();
                    AgentLoop.ConversationStore = _convStore;
                    AgentLoop.WireUIMessageBus(_engine.CcbWs);
                }
                else
                {
                    SafeLog.Warning("[agent-mod] CcbWs 为 null，UI 总线未启动");
                }

                _lastTick = 0;
                SafeLog.Info("[agent-mod] Agent Runtime 初始化完成");
            }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[agent-mod] InitAgentRuntime 异常:");
                for (var e = ex; e != null; e = e.InnerException)
                    sb.AppendLine($"  [{e.GetType().Name}] {e.Message}\n{e.StackTrace}");
                SafeLog.Error(sb.ToString());
                _initialized = false;
            }
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            SafeLog.Flush();
            if (!_initialized || _engine == null) return;

            _engine.Tick();
            UIMessageBus.IsReady = _engine.CcbWs?.IsReady ?? false;

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

        public void ShutdownEngine()
        {
            try
            {
                CoreLog.Info("[agent-mod] 返回主菜单，开始关闭 Agent 和 CCB...");
                try { UIMessageBus.Stop(); }
                catch (Exception ex) { SafeLog.Warning($"[agent-mod] UIMessageBus.Stop 异常 (可忽略): {ex.GetType().Name}: {ex.Message}"); }
                _convStore = null;
                AgentLoop.ConversationStore = null;
                try
                {
                    _engine?.Dispose();
                    _engine = null;
                    _initialized = false;
                    CoreLog.Info("[agent-mod] Agent 和 CCB 已关闭");
                }
                catch (Exception ex) { CoreLog.Error($"[agent-mod] 关闭 Agent 失败: {ex.Message}"); }
                try { CcbManager.KillStaleProcesses(); }
                catch (Exception ex) { SafeLog.Warning($"[agent-mod] KillStaleProcesses 异常: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                SafeLog.Warning($"[agent-mod] ShutdownEngine 异常 (非致命): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
