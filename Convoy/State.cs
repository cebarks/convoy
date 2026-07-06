using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace Convoy
{
    public class ModFileState
    {
        [JsonProperty("path")]
        public string Path { get; set; } = "";

        [JsonProperty("hash")]
        public string Hash { get; set; } = "";
    }

    public class ModState
    {
        [JsonProperty("forge_id")]
        public int ForgeId { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; } = "";

        [JsonProperty("files")]
        public List<ModFileState> Files { get; set; } = new List<ModFileState>();
    }

    public class ConvoyState
    {
        [JsonProperty("server_url")]
        public string ServerUrl { get; set; } = "";

        [JsonProperty("last_catalog_etag")]
        public string? LastCatalogEtag { get; set; }

        [JsonProperty("mods")]
        public List<ModState> Mods { get; set; } = new List<ModState>();

        private static string FilePath =>
            System.IO.Path.Combine(Paths.ConfigPath, "Convoy", "state.json");

        public static ConvoyState Load()
        {
            if (!File.Exists(FilePath))
                return new ConvoyState();
            return JsonConvert.DeserializeObject<ConvoyState>(File.ReadAllText(FilePath))
                   ?? new ConvoyState();
        }

        public void Save()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
