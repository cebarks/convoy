using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Convoy
{
    public class ConvoyConfig
    {
        private readonly ConfigFile _config;

        public ConfigEntry<KeyboardShortcut>? PanelKeybind { get; private set; }

        public ConvoyConfig(ConfigFile config)
        {
            _config = config;
            PanelKeybind = _config.Bind("Convoy", "Panel Keybind",
                new KeyboardShortcut(KeyCode.F10),
                "Keybind to toggle the Convoy panel");
        }

        public List<CachedGroup> RegisterOptionalGroups(List<CatalogGroup> groups)
        {
            var cached = new List<CachedGroup>();
            foreach (var group in groups.Where(g => g.Tier == "optional"))
            {
                var modList = string.Join(", ", group.Mods.Select(m => $"{m.Name} v{m.Version}"));
                cached.Add(new CachedGroup { Slug = group.Slug, Name = group.Name, Description = modList });
            }
            return cached;
        }
    }
}
