using System;
using System.Threading.Tasks;
using Verse;

namespace RimWorldAgent
{
    /// <summary>
    /// 游戏加载后自动连接 BridgeBus，维持常驻连接。
    /// Dialog_AiChat 通过静态 Bridge 属性复用此连接。
    /// </summary>
    public class MapComponent_AgentUI : MapComponent
    {
        private static BridgeClient? _bridge;
        private static WebUIHttpServer? _httpServer;
        private static bool _initialized;
        private static readonly object _lock = new();

        public static BridgeClient? Bridge => _bridge;
        public static bool IsConnected => _bridge?.IsConnected ?? false;

        public MapComponent_AgentUI(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (!_initialized)
            {
                _initialized = true;
                _ = InitAsync();
            }
        }

        private async Task InitAsync()
        {
            try
            {
                var bridgeUrl = AgentUIMod.Instance?.Settings?.BridgeWsUrl ?? "ws://127.0.0.1:19999";
                var bridgePort = new Uri(bridgeUrl).Port;
                _bridge = new BridgeClient(bridgeUrl);
                _bridge.OnMessage += msg => ChatDisplayState.ProcessMessage(msg);
                await _bridge.ConnectAsync();
                Log.Message($"[AgentUI] BridgeBus 已连接: {bridgeUrl}");

                var httpPort = AgentUIMod.Instance?.Settings?.WebUIPort ?? 19997;
                _httpServer = new WebUIHttpServer(httpPort, bridgePort);
                _httpServer.Start();
                Log.Message($"[AgentUI] WebUI: http://localhost:{httpPort}");
            }
            catch (Exception ex) { Log.Warning($"[AgentUI] 初始化失败: {ex.Message}"); }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            _bridge?.Dispose();
            _bridge = null;
            _httpServer?.Dispose();
            _httpServer = null;
            _initialized = false;
        }
    }
}
