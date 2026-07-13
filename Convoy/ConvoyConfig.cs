using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace Convoy
{
    // Must match BepInEx.ConfigurationManager's expected class name exactly — it reflects on this
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? ReadOnly;
        public bool? HideDefaultButton;
        public bool? IsAdvanced;
        public int? Order;
    }

    public class ConvoyConfig
    {
        private readonly ConfigFile _config;
        private readonly Dictionary<string, ConfigEntry<bool>> _groupToggles =
            new Dictionary<string, ConfigEntry<bool>>();

        private ConfigEntry<string>? _convoyVersion;
        private ConfigEntry<string>? _qmVersion;
        private ConfigEntry<string>? _lastResult;
        private ConfigEntry<string>? _lastError;
        private ConfigEntry<string>? _serverUrl;
        public ConfigEntry<bool>? SyncNow { get; private set; }

        public ConvoyConfig(ConfigFile config)
        {
            _config = config;
        }

        public List<CachedGroup> RegisterOptionalGroups(List<CatalogGroup> groups)
        {
            var cached = new List<CachedGroup>();
            foreach (var group in groups.Where(g => g.Tier == "optional"))
            {
                var modList = string.Join(", ", group.Mods.Select(m => $"{m.Name} v{m.Version}"));
                var desc = $"Install mods in this group: {modList}";
                _groupToggles[group.Slug] = _config.Bind(group.Name, "Enabled", false, desc);
                cached.Add(new CachedGroup { Slug = group.Slug, Name = group.Name, Description = desc });
            }
            return cached;
        }

        public void RegisterCachedGroups(List<CachedGroup> groups)
        {
            foreach (var g in groups)
            {
                if (!_groupToggles.ContainsKey(g.Slug))
                    _groupToggles[g.Slug] = _config.Bind(g.Name, "Enabled", false, g.Description);
            }
        }

        public bool IsGroupEnabled(string slug) =>
            _groupToggles.TryGetValue(slug, out var entry) && entry.Value;

        private static ConfigDescription ReadOnlyDesc(string desc, int order) =>
            new ConfigDescription(desc, null, new ConfigurationManagerAttributes { ReadOnly = true, HideDefaultButton = true, IsAdvanced = true, Order = order });

        public void RegisterDebugEntries()
        {
            _convoyVersion = _config.Bind("Debug", "Convoy Version", VersionInfo.Version, ReadOnlyDesc("Convoy plugin version", 7));
            _qmVersion = _config.Bind("Debug", "Quartermaster Version", "unknown", ReadOnlyDesc("Quartermaster server version", 5));
            _lastResult = _config.Bind("Debug", "Last Sync Result", "pending", ReadOnlyDesc("Result of the last sync", 4));
            _lastError = _config.Bind("Debug", "Last Error", "", ReadOnlyDesc("Error from last sync (if any)", 3));
            _serverUrl = _config.Bind("Debug", "Server URL", "", ReadOnlyDesc("Quartermaster server URL", 2));
            SyncNow = _config.Bind("Debug", "Sync Now", false,
                new ConfigDescription("Toggle to trigger a manual re-sync", null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
        }

        public void UpdateDebugState(SyncOutcome outcome)
        {
            if (_qmVersion != null && !string.IsNullOrEmpty(outcome.QuartermasterVersion))
                _qmVersion.Value = outcome.QuartermasterVersion!;
            if (_lastResult != null)
            {
                switch (outcome.Result)
                {
                    case SyncResult.UpToDate: _lastResult.Value = "Up to date"; break;
                    case SyncResult.RestartRequired: _lastResult.Value = "Updated — restart required"; break;
                    case SyncResult.Failed: _lastResult.Value = "Failed"; break;
                }
            }
            if (_lastError != null)
                _lastError.Value = outcome.Error ?? "";
            if (_serverUrl != null && !string.IsNullOrEmpty(outcome.ServerUrl))
                _serverUrl.Value = outcome.ServerUrl!;
        }
    }
}
