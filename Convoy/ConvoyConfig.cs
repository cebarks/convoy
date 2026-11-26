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

        private readonly ConfigEntry<long> _spoolMaxBytes;

        public ConvoyConfig(ConfigFile config)
        {
            _config = config;
            _spoolMaxBytes = config.Bind("Convoy", "Spool Max Bytes", TransferSpool.DefaultMaxBytes,
                "Maximum bytes for the private transfer cache (rollback is budgeted separately).");
            PanelKeybind = _config.Bind("Convoy", "Panel Keybind",
                new KeyboardShortcut(KeyCode.F10),
                "Keybind to toggle the Convoy panel");
        }

        public long SpoolMaxBytes { get => _spoolMaxBytes.Value; set => _spoolMaxBytes.Value = value; }

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
