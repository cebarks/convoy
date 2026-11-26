using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;

namespace Convoy
{
    public class ModFileState
    {
        internal bool MatchesV2(DeliveryAsset asset) => Path == asset.Path && Sha256 == asset.Sha256
            && Length == asset.Length && Hash == asset.Xxh3_64 && Owned.HasValue;

        [JsonProperty("path")]
        public string Path { get; set; } = "";

        // Null is legacy ownership: BepInEx only, never a game assembly.
        [JsonProperty("owned")]
        public bool? Owned { get; set; }

        [JsonProperty("hash")]
        public string Hash { get; set; } = "";

        [JsonProperty("sha256", NullValueHandling = NullValueHandling.Ignore)]
        public string? Sha256 { get; set; }

        [JsonProperty("length", NullValueHandling = NullValueHandling.Ignore)]
        public long? Length { get; set; }
    }

    public class ModState
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; } = "";

        [JsonProperty("files")]
        public List<ModFileState> Files { get; set; } = new List<ModFileState>();

        [JsonProperty("manifest", NullValueHandling = NullValueHandling.Ignore)]
        public string? Manifest { get; set; }
    }

    public class CachedGroup
    {
        [JsonProperty("slug")]
        public string Slug { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("description")]
        public string Description { get; set; } = "";
    }

    public class ConvoyState
    {
        [JsonProperty("server_url")]
        public string ServerUrl { get; set; } = "";

        [JsonProperty("mods")]
        public List<ModState> Mods { get; set; } = new List<ModState>();

        [JsonProperty("optional_groups")]
        public List<CachedGroup> OptionalGroups { get; set; } = new List<CachedGroup>();

        [JsonProperty("skipped_mods")]
        public HashSet<long> SkippedMods { get; set; } = new HashSet<long>();

        [JsonProperty("enabled_groups")]
        public HashSet<string> EnabledGroups { get; set; } = new HashSet<string>();

        internal static string FilePath =>
            System.IO.Path.Combine(Paths.ConfigPath, "Convoy", "state.json");

        internal sealed class Snapshot
        {
            internal string Target { get; }
            private readonly byte[]? _bytes;

            internal Snapshot(string target)
            {
                Target = target;
                _bytes = Read(target);
            }

            private static byte[]? Read(string target)
            {
                try { return File.ReadAllBytes(target); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
            }

            internal ConvoyState Parse()
            {
                if (_bytes == null) return new ConvoyState();
                using var stream = new MemoryStream(_bytes, false);
                using var text = new StreamReader(stream);
                using var reader = new JsonTextReader(text);
                return new JsonSerializer { TypeNameHandling = TypeNameHandling.None }.Deserialize<ConvoyState>(reader)
                    ?? throw new InvalidDataException("Invalid Convoy install record");
            }

            internal void RequireCurrent(string guardedTarget)
            {
                if (guardedTarget != Target) throw new IOException("Convoy state target changed; replan before applying");
                var now = Read(guardedTarget);
                if (_bytes == null ? now != null : now == null || !_bytes.SequenceEqual(now))
                    throw new IOException("Convoy install record changed; replan before applying");
            }
        }

        public static ConvoyState Load() => new Snapshot(FilePath).Parse();
        public void Save() => Save(FilePath);

        internal void Save(string target)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            var temporary = target + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(this, Formatting.Indented));
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
