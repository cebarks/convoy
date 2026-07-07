using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace Convoy
{
    public class ConvoyConfig
    {
        private readonly ConfigFile _config;
        private readonly Dictionary<string, ConfigEntry<bool>> _groupToggles =
            new Dictionary<string, ConfigEntry<bool>>();

        public ConvoyConfig(ConfigFile config)
        {
            _config = config;
        }

        public void RegisterOptionalGroups(List<CatalogGroup> groups)
        {
            foreach (var group in groups.Where(g => g.Tier == "optional"))
            {
                var modList = string.Join(", ", group.Mods.Select(m => $"{m.Name} v{m.Version}"));
                _groupToggles[group.Slug] = _config.Bind(
                    group.Name,
                    "Enabled",
                    false,
                    $"Install mods in this group: {modList}");
            }
        }

        public bool IsGroupEnabled(string slug) =>
            _groupToggles.TryGetValue(slug, out var entry) && entry.Value;
    }
}
