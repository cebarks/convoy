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
    }
}
