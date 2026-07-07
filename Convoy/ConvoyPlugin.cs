using BepInEx;

namespace Convoy
{
    [BepInPlugin("io.cebarks.convoy", "Convoy", VersionInfo.Version)]
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
