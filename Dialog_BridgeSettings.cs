using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    public class Dialog_BridgeSettings : Window
    {
        private McpModSettings _settings;
        private string _inputText = "";
        private string _log = "";
        private Vector2 _scrollPos;

        public Dialog_BridgeSettings(McpModSettings settings)
        {
            _settings = settings;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = false;
        }

        public override Vector2 InitialSize => new Vector2(500f, 400f);

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // 状态
            var state = McpClient.State switch
            {
                ClientState.Disconnected => "未连接",
                ClientState.Connecting => "连接中...",
                ClientState.Connected => "已连接 (等待 Ready)",
                ClientState.Ready => "就绪",
                _ => "未知"
            };
            listing.Label($"状态: {state}");

            listing.Gap(6f);

            // 消息日志
            listing.Label("消息:");
            var logRect = listing.GetRect(180f);
            Widgets.DrawBox(logRect);
            _scrollPos = GUI.BeginScrollView(logRect, _scrollPos, new Rect(0, 0, logRect.width - 20, 2000));
            var logY = 0f;
            foreach (var line in _log.Split('\n'))
            {
                var h = Text.CalcHeight(line, logRect.width - 30);
                Widgets.Label(new Rect(5, logY, logRect.width - 30, h), line);
                logY += h + 2;
            }
            GUI.EndScrollView();

            listing.Gap(6f);

            // 输入
            listing.Label("发送:");
            _inputText = listing.TextEntry(_inputText);
            if (listing.ButtonText("发送") && !string.IsNullOrWhiteSpace(_inputText))
            {
                _ = McpClient.SendMessage(_inputText);
                _log += $"\n→ {_inputText}";
                _inputText = "";
            }

            listing.End();

            // 从 Incoming 队列拉取消息
            while (McpClient.Incoming.TryDequeue(out var msg))
                _log += $"\n← {msg}";
        }
    }
}
