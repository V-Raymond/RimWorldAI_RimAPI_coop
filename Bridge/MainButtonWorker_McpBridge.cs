using RimWorld;
using Verse;

namespace RimWorldMCP
{
    public class MainButtonWorker_McpBridge : MainButtonWorker
    {
        public override void Activate()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null) return;
            Find.WindowStack.Add(new Dialog_BridgeSettings(settings));
        }
    }
}
