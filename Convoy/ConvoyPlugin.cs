using BepInEx;

namespace Convoy
{
    [BepInPlugin("com.cebarks.convoy", "Convoy", "0.1.0")]
    public class ConvoyPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            var config = new ConvoyConfig(Config);
            var engine = new SyncEngine(Logger, config);
            var result = engine.Run();
            if (result == SyncResult.Failed)
                Logger.LogError("Convoy sync failed — check log above for details");
        }
    }
}
