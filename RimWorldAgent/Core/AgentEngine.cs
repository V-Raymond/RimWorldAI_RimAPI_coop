using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ccb = RimWorldAgent.Core.CcbManager.CcbManager;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Agent 引擎配置 — 构造后传 InitAsync</summary>
    public class AgentEngineConfig
    {
        public string SessionDir { get; set; } = "";
        public string? SkillsDir { get; set; }
        public string McpUrl { get; set; } = "http://localhost:9877";
        public int McpPort { get; set; } = 9877;
        public int AgentMcpPort { get; set; } = 9878;
        public int CcbPort { get; set; } = 19999;
        public string CcbWsUrl { get; set; } = "ws://127.0.0.1:19999";
        public string? CcbToken { get; set; }
        public string? ModelName { get; set; }
        public bool CcbAutoStart { get; set; } = true;
        public bool CcbAutoInstall { get; set; } = true;
        public string CcbDir { get; set; } = "";
        public string PlanSpeed { get; set; } = "paused";
        public bool WaitForGame { get; set; } = false;
        public long TokenBudgetLimit { get; set; }
        public string ThinkingEffort { get; set; } = "medium";
        public int MaxThinkingTokens { get; set; }
    }

    /// <summary>Agent 引擎 — CCB 生命周期 + WS + MCP + 调度循环。EXE/MOD 共享。</summary>
    public class AgentEngine : IDisposable
    {
        private readonly AgentEngineConfig _cfg;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private Ccb? _ccb;
        private CcbWebSocket? _ccbWs;
        private McpClient? _mcp;
        private ContextBuilder? _ctx;
        private bool _initialized;
        private int _lastDialogCheckTick;
        private SimpleMspServer.McpServiceHost? _agentHost;

        public CcbWebSocket? CcbWs => _ccbWs;
        public bool IsReady => _initialized && _mcp != null;

        public AgentEngine(AgentEngineConfig cfg, Action<string>? logInfo = null, Action<string>? logError = null)
        {
            _cfg = cfg;
            _logInfo = logInfo ?? (msg => { });
            _logError = logError ?? (msg => { });
        }

        /// <summary>完整启动流程：Skills → AgentMCP → npm install → CCB spawn → WS → MCP → SSE</summary>
        public async Task<bool> InitAsync()
        {
            if (_initialized) return true;
            _initialized = true;

            CoreLog.OnInfo = _logInfo;
            CoreLog.OnError = _logError;

            // Session 目录
            Directory.CreateDirectory(_cfg.SessionDir);
            TaskBoard.SessionDir = _cfg.SessionDir;

            // Data 层 — 本地文件持久化
            TodoStore.TickProvider = () => AgentOrchestrator.GameTick;
            MemoryStore.Instance = new InMemoryMemoryStore();
            TodoStore.Instance = new InMemoryTodoStore();
            TokenStore.Instance = new LocalFileTokenStore();

            // Skills
            var skillsDir = _cfg.SkillsDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            InternalToolRegistry.Instance.LoadSkills(skillsDir);

            // Agent MCP Server
            _agentHost = new SimpleMspServer.McpServiceHost(_cfg.AgentMcpPort,
                log: new SimpleMspServer.DelegateMspLog(_logInfo));
            _agentHost.RegisterProvider(InternalToolRegistry.Instance);
            _agentHost.Start();
            _logInfo($"[AgentEngine] AgentMCP :{_cfg.AgentMcpPort}");

            // CCB 子进程 + WS（可选）
            var ccbReady = false;
            if (!string.IsNullOrEmpty(_cfg.CcbDir) && Directory.Exists(_cfg.CcbDir))
            {
                if (_cfg.CcbAutoInstall && !CompanionInstaller.IsInstalled(_cfg.CcbDir))
                {
                    _logInfo("[AgentEngine] CCB: npm install...");
                    await CompanionInstaller.InstallAsync(_cfg.CcbDir);
                }

                _ccb = new Ccb(_cfg.CcbDir, _cfg.SessionDir, _cfg.CcbPort,
                    mcpPort: _cfg.McpPort, agentMcpPort: _cfg.AgentMcpPort,
                    ccbToken: _cfg.CcbToken, modelName: _cfg.ModelName);
                if (_cfg.CcbAutoStart)
                {
                    if (_ccb.Start()) { await _ccb.WaitReadyAsync(15000); _logInfo("[AgentEngine] CCB: 就绪"); }
                }

                if (_ccb.IsReady)
                {
                    _ccbWs = new CcbWebSocket(_cfg.CcbWsUrl, _cfg.CcbToken ?? "")
                    {
                        BudgetLimit = _cfg.TokenBudgetLimit,
                        ThinkingEffort = _cfg.ThinkingEffort,
                        MaxThinkingTokens = _cfg.MaxThinkingTokens
                    };
                    if (await _ccbWs.ConnectAsync())
                    {
                        AgentLoop.WireCcbStatus(_ccbWs);
                        _logInfo("[AgentEngine] CCB WS: 已连接");
                        ccbReady = true;
                    }
                    else
                    {
                        _logError("[AgentEngine] CCB WS: 连接失败");
                        _ccbWs.Dispose();
                        _ccbWs = null;
                    }
                }
            }

            if (!ccbReady) _logInfo("[AgentEngine] CCB: 未就绪 (事件转发不可用)");

            // MCP 客户端
            _mcp = new McpClient(_cfg.McpUrl);

            // 可选：等待游戏就绪（EXE 模式需要等待 RimWorldMCP Mod 启动）
            if (_cfg.WaitForGame)
            {
                _logInfo("[AgentEngine] 等待游戏启动...");
                while (true)
                {
                    try { await _mcp.CallTool("get_world_summary"); break; }
                    catch (Exception ex) { _logInfo($"[AgentEngine] 等待游戏启动: {ex.Message}，3s 后重试..."); await Task.Delay(3000); }
                }
                _logInfo("[AgentEngine] 游戏已连接");
            }

            // 游戏事件订阅（通过 MCP notification 通道拦截，随 SDK 连接自动启动）
            AgentLoop.WireEvents(_mcp);

            GamePaceController.PlanSpeed = _cfg.PlanSpeed;
            _ctx = new ContextBuilder(_mcp);

            // TODO 变更 → 推送到 Companion
            TodoStore.OnChanged += () =>
            {
                if (_ccbWs?.IsReady == true)
                {
                    var items = TodoStore.Query(null);
                    _ = _ccbWs.SendEvent("todo-state", new
                    {
                        todoItems = items.Select(i => new
                        {
                            id = i.Id, description = i.Description, priority = i.Priority,
                            status = i.Status, createdAtTick = i.CreatedAtTick
                        }).ToArray()
                    });
                }
            };

            return ccbReady;
        }

        /// <summary>同步维护：CCB 崩溃重启 + WS 重连。MOD 每帧调用，EXE 循环中调用。</summary>
        public void Tick()
        {
            if (!_initialized) return;

            if (_ccb != null)
            {
                _ccb.TickAndRestart();
                if (_ccb.WasRestarted)
                {
                    _ccb.WasRestarted = false;
                    _logInfo("[AgentEngine] CCB 进程已重启，重连 WS...");
                    _ccbWs?.Dispose();
                    _ccbWs = new CcbWebSocket(_cfg.CcbWsUrl, _cfg.CcbToken ?? "")
                    {
                        BudgetLimit = _cfg.TokenBudgetLimit,
                        ThinkingEffort = _cfg.ThinkingEffort,
                        MaxThinkingTokens = _cfg.MaxThinkingTokens
                    };
                    _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                    {
                        if (t.Result) AgentLoop.WireCcbStatus(_ccbWs!);
                        else { _ccbWs?.Dispose(); _ccbWs = null; }
                    });
                }
            }

            // WS 被动断开自动重连（CcbWebSocket 内部 ScheduleReconnect 处理）
            if (_ccbWs != null && _ccbWs.State == CcbClientState.Disconnected)
            {
                _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                {
                    if (t.Result) AgentLoop.WireCcbStatus(_ccbWs!);
                });
            }
        }

        /// <summary>Agent 调度：Scheduler 检查 → RunAgent。原地切换由 ToolDispatcher 处理</summary>
        public async Task TickAsync()
        {
            if (_mcp == null || _ctx == null || _ccbWs == null || !_ccbWs.IsReady) return;

            var currentTick = AgentOrchestrator.GameTick;

            // 定时弹框扫描（每 2500 tick ≈ 60s 游戏时间，约 5 个主循环周期）
            if (currentTick - _lastDialogCheckTick >= 2500)
            {
                _lastDialogCheckTick = currentTick;
                try
                {
                    var dialogsResult = await _mcp.CallTool("get_open_dialogs");
                    if (!dialogsResult.Contains("没有打开") && !dialogsResult.Contains("没有可交互"))
                    {
                        AgentOrchestrator.DispatchEvent(new ColonyEvent
                        {
                            Category = "Combat",
                            Severity = "Critical",
                            Summary = $"弹框提示 — 请调用 get_open_dialogs 查看并处理",
                            Tick = currentTick
                        }, EventRoute.Combat);
                        _logInfo($"[AgentEngine] 检测到弹框，已发送 Critical 事件");
                    }
                }
                catch (Exception ex) { _logInfo($"[AgentEngine] 弹框检测失败: {ex.Message}"); }
            }

            if (AgentOrchestrator.IsSleeping("overseer")
                && (Scheduler.ShouldWake("overseer", AgentConfigs.Overseer.IntervalGameHours, currentTick)
                    || AgentOrchestrator.IsNewDay("overseer")))
            {
                await RunAgent(AgentConfigs.Overseer);
                if (AgentOrchestrator.PendingSessionStart)
                {
                    await RunPendingAgent();
                    return;
                }
            }

            if (AgentOrchestrator.IsSleeping("combat")
                && AgentOrchestrator.HasPendingEvents("combat"))
            {
                await RunAgent(AgentConfigs.Combat);
                if (AgentOrchestrator.PendingSessionStart)
                {
                    await RunPendingAgent();
                    return;
                }
            }

            if (AgentOrchestrator.IsSleeping("economy")
                && Scheduler.ShouldWake("economy", AgentConfigs.Economy.IntervalGameHours, currentTick))
            {
                await RunAgent(AgentConfigs.Economy);
                if (AgentOrchestrator.PendingSessionStart)
                {
                    await RunPendingAgent();
                    return;
                }
            }

            if (AgentOrchestrator.IsSleeping("medic")
                && (AgentOrchestrator.IsNewDay("medic")
                    || AgentOrchestrator.HasPendingEvents("medic")))
            {
                await RunAgent(AgentConfigs.Medic);
                if (AgentOrchestrator.PendingSessionStart)
                {
                    await RunPendingAgent();
                    return;
                }
            }
        }

        private async Task RunPendingAgent()
        {
            AgentOrchestrator.PendingSessionStart = false;
            var active = AgentOrchestrator.ActiveAgent;
            if (active == null) return;
            var cfg = AgentConfigs.Get(active);
            if (cfg != null)
                await RunAgent(cfg);
        }

        private async Task RunAgent(AgentConfig config)
        {
            AgentOrchestrator.PendingSessionStart = false;
            AgentOrchestrator.NextAgentRequest = null;
            AgentLoop.SwitchCount = 0;
            GamePaceController.ShouldSkipResume = null;
            AgentOrchestrator.BeginAgent(config.Name);
            _logInfo($"[AgentEngine] 唤醒 {config.Name} (Load={Scheduler.LoadScore})");

            await _ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.AgentRoleDisplay });

            var prompt = await _ctx.BuildAsync(config);
            await AgentLoop.RunSessionAsync(config, prompt, _mcp, _ccbWs);

            var endedAgent = AgentOrchestrator.ActiveAgent ?? config.Name;
            AgentOrchestrator.EndAgent(endedAgent);
            _logInfo($"[AgentEngine] {endedAgent} 休眠");
        }

        public void Dispose()
        {
            _ccbWs?.Dispose();
            _ccbWs = null;
            _ccb?.Dispose();
            _ccb = null;
            _mcp?.Dispose();
            _mcp = null;
            _agentHost?.Stop();
            _agentHost = null;
        }
    }
}
