using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace Convoy
{
    #region Catalog DTOs

    public class CatalogMod
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("forge_id")]
        public int? ForgeId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("version")]
        public string Version { get; set; } = "";

        [JsonProperty("file_checksums")]
        public Dictionary<string, string> FileChecksums { get; set; } = new Dictionary<string, string>();

        [JsonProperty("bundle_checksums")]
        public Dictionary<string, string> BundleChecksums { get; set; } = new Dictionary<string, string>();
    }

    public class CatalogGroup
    {
        [JsonProperty("slug")]
        public string Slug { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("tier")]
        public string Tier { get; set; } = "";

        [JsonProperty("mods")]
        public List<CatalogMod> Mods { get; set; } = new List<CatalogMod>();
    }

    public class Catalog
    {
        [JsonProperty("spt_version")]
        public string SptVersion { get; set; } = "";

        [JsonProperty("quartermaster_version")]
        public string QuartermasterVersion { get; set; } = "";

        [JsonProperty("groups")]
        public List<CatalogGroup> Groups { get; set; } = new List<CatalogGroup>();

        [JsonProperty("exclusions")]
        public List<string> Exclusions { get; set; } = new List<string>();
    }

    #endregion

    #region Sync Plan

    public class PlannedMod
    {
        public int Id;
        public string Name = "";
        public string Version = "";
        public string? OldVersion;
        public string GroupName = "";
        public string GroupSlug = "";
        public bool IsRequired;
    }

    public class SyncPlan
    {
        public Catalog Catalog = new Catalog();
        public ConvoyState State = new ConvoyState();
        public Dictionary<int, CatalogMod> WantedMods = new Dictionary<int, CatalogMod>();
        public string ServerUrl = "";
        public List<PlannedMod> Installs = new List<PlannedMod>();
        public List<PlannedMod> Updates = new List<PlannedMod>();
        public List<PlannedMod> Removals = new List<PlannedMod>();
        public HashSet<string> Exclusions = new HashSet<string>();
    }

    #endregion

    public enum SyncResult
    {
        UpToDate,
        RestartRequired,
        Failed
    }

    public class SyncEngine
    {
        private readonly ManualLogSource _log;
        private readonly ConvoyConfig _config;
        private readonly string _gameRoot;

        public SyncEngine(ManualLogSource log, ConvoyConfig config)
        {
            _log = log;
            _config = config;
            _gameRoot = Paths.GameRootPath;
        }

        public SyncPlan? PlanSync(SyncProgress? progress = null)
        {
            progress?.SetPhase("Cleaning up...");
            // No recursive deletion of unowned *.convoy-old files.

            var state = ConvoyState.Load();
            var serverUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/');

            progress?.SetPhase("Fetching catalog...");
            var catalog = FetchCatalog(serverUrl);

            // Reject unsupported catalogs before no-op bookkeeping or any state save.
            ValidateCatalog(catalog);
            var pathGuard = new SyncFiles(_gameRoot, state.Mods);
            foreach (var path in catalog.Groups.SelectMany(g => g.Mods).SelectMany(m => m.FileChecksums.Keys.Concat(m.BundleChecksums.Keys))) pathGuard.Resolve(path);
            pathGuard.Resolve("BepInEx/config/Convoy/state.json");
            state.OptionalGroups = _config.RegisterOptionalGroups(catalog.Groups);

            var catalogModGroups = new Dictionary<int, (CatalogMod mod, CatalogGroup group)>();
            foreach (var group in catalog.Groups)
                foreach (var mod in group.Mods)
                    catalogModGroups[mod.Id] = (mod, group);

            var wantedMods = new Dictionary<int, CatalogMod>();
            foreach (var group in catalog.Groups)
            {
                bool isRequired = group.Tier == "required";
                if (!isRequired && !state.EnabledGroups.Contains(group.Slug))
                    continue;

                foreach (var mod in group.Mods)
                {
                    if (!isRequired && state.SkippedMods.Contains(mod.Id))
                        continue;
                    wantedMods[mod.Id] = mod;
                }
            }

            var currentMods = state.Mods.ToDictionary(m => m.Id);

            var installs = new List<PlannedMod>();
            var updates = new List<PlannedMod>();
            foreach (var kvp in wantedMods)
            {
                var (mod, group) = catalogModGroups[kvp.Key];
                if (!currentMods.TryGetValue(kvp.Key, out var current))
                {
                    installs.Add(new PlannedMod
                    {
                        Id = mod.Id, Name = mod.Name, Version = mod.Version,
                        GroupName = group.Name, GroupSlug = group.Slug,
                        IsRequired = group.Tier == "required"
                    });
                }
                else if (current.Version != mod.Version
                         || !ManifestMatches(current, mod)
                         || current.Files.Any(f => !File.Exists(ResolvePath(f.Path))))
                {
                    updates.Add(new PlannedMod
                    {
                        Id = mod.Id, Name = mod.Name, Version = mod.Version,
                        OldVersion = current.Version,
                        GroupName = group.Name, GroupSlug = group.Slug,
                        IsRequired = group.Tier == "required"
                    });
                }
            }

            var allKeptIds = new HashSet<int>(wantedMods.Keys);
            var removals = new List<PlannedMod>();
            foreach (var id in currentMods.Keys.Where(id => !allKeptIds.Contains(id)))
            {
                var current = currentMods[id];
                var hasInfo = catalogModGroups.TryGetValue(id, out var info);
                removals.Add(new PlannedMod
                {
                    Id = id,
                    Name = hasInfo ? info.mod.Name : $"Mod {id}",
                    Version = current.Version,
                    GroupName = hasInfo ? info.group.Name : "",
                    GroupSlug = hasInfo ? info.group.Slug : "",
                    IsRequired = false
                });
            }

            if (installs.Count == 0 && updates.Count == 0 && removals.Count == 0)
            {
                var catalogIds = new HashSet<int>(catalogModGroups.Keys);
                state.SkippedMods.IntersectWith(catalogIds);
                state.Save();
                _log.LogInfo("All mods up to date");
                SendReport("up_to_date", state.Mods, null);
                return new SyncPlan { Catalog = catalog, ServerUrl = serverUrl, State = state };
            }

            if (installs.Count > 0 || updates.Count > 0)
            {
                var names = installs.Concat(updates).Select(m => m.Name);
                _log.LogInfo($"Installing/updating: {string.Join(", ", names)}");
            }
            if (removals.Count > 0)
            {
                var names = removals.Select(m => m.Name);
                _log.LogInfo($"Removing: {string.Join(", ", names)}");
            }

            return new SyncPlan
            {
                Catalog = catalog,
                State = state,
                WantedMods = wantedMods,
                ServerUrl = serverUrl,
                Installs = installs,
                Updates = updates,
                Removals = removals,
                Exclusions = new HashSet<string>(catalog.Exclusions)
            };
        }

        private static void ValidateCatalog(Catalog catalog)
        {
            var mods = catalog.Groups.SelectMany(g => g.Mods).ToList();
            // Validate the complete inventory before selection, deduplication or no-op returns.
            Checksums(mods);
            foreach (var group in mods.GroupBy(m => m.Id))
            {
                var first = group.First();
                foreach (var mod in group.Skip(1))
                    if (mod.Version != first.Version || mod.Name != first.Name || mod.ForgeId != first.ForgeId
                        || !SameManifest(first.FileChecksums, mod.FileChecksums)
                        || !SameManifest(first.BundleChecksums, mod.BundleChecksums))
                        throw new InvalidOperationException("Inconsistent duplicate catalog mod ID: " + mod.Id);
            }
        }

        private static bool SameManifest(Dictionary<string, string> first, Dictionary<string, string> other)
            => first.Count == other.Count && first.All(p => other.TryGetValue(p.Key, out var hash) && hash == p.Value);

        private static bool ManifestMatches(ModState current, CatalogMod mod)
        {
            var advertised = Checksums(new[] { mod });
            return current.Files.Count == advertised.Count && current.Files.All(f => advertised.TryGetValue(f.Path, out var hash) && hash == f.Hash);
        }

        private static Dictionary<string, string> Checksums(IEnumerable<CatalogMod> mods, bool bundlesOnly = false)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods)
                foreach (var pair in (bundlesOnly ? mod.BundleChecksums : mod.FileChecksums.Concat(mod.BundleChecksums)))
                {
                    if (!SyncFiles.Allowed(pair.Key) || pair.Key.StartsWith("BepInEx/config/Convoy/", StringComparison.OrdinalIgnoreCase) || (aliases.TryGetValue(pair.Key, out var name) && name != pair.Key)
                        || (result.TryGetValue(pair.Key, out var prior) && prior != pair.Value))
                        throw new InvalidOperationException("Conflicting or unsupported catalog path: " + pair.Key);
                    aliases[pair.Key] = pair.Key;
                    result[pair.Key] = pair.Value;
                }
            return result;
        }

        public SyncResult ExecuteRedownload(Catalog catalog, bool bundlesOnly, SyncProgress? progress = null)
        {
            try { ValidateCatalog(catalog); }
            catch (Exception ex) { _log.LogError("Redownload aborted: " + ex.Message); return SyncResult.Failed; }
            var state = ConvoyState.Load();
            // Redownload must not install unselected optional groups or silently adopt files.
            var ids = new HashSet<int>(state.Mods.Select(m => m.Id));
            var mods = catalog.Groups.SelectMany(g => g.Mods).Where(m => ids.Contains(m.Id)).GroupBy(m => m.Id).Select(g => g.First()).ToList();
            if (mods.Count == 0) return SyncResult.UpToDate;
            var plan = new SyncPlan { Catalog = catalog, State = state,
                ServerUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/'),
                WantedMods = mods.ToDictionary(m => m.Id) };
            if (!bundlesOnly) return ExecuteSync(plan, mods.Select(m => m.Id).ToList(), state.SkippedMods, progress);
            var staging = Path.Combine(Path.GetTempPath(), "convoy-" + Guid.NewGuid().ToString("N"));
            try
            {
                var expected = Checksums(mods, true);
                var files = StageVerifiedZip(DownloadMods(plan.ServerUrl, mods.Select(m => m.Id).ToList(), true, progress), staging, expected, progress);
                if (files == null) return SyncResult.Failed;
                var transaction = new SyncFiles(_gameRoot, state.Mods);
                transaction.Preflight(expected, Array.Empty<string>(), Array.Empty<ModFileState>());
                transaction.Apply(staging, () => { });
                return SyncResult.UpToDate;
            }
            catch (Exception ex) { _log.LogError("Redownload aborted: " + ex.Message); return SyncResult.Failed; }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        public SyncResult ExecuteSync(SyncPlan plan, List<int> confirmedModIds, HashSet<int> skippedModIds, SyncProgress? progress = null)
        {
            var staging = Path.Combine(Path.GetTempPath(), "convoy-" + Guid.NewGuid().ToString("N"));
            try
            {
                var confirmed = new HashSet<int>(confirmedModIds);
                if (confirmed.Count != confirmedModIds.Count || confirmed.Any(id => !plan.WantedMods.ContainsKey(id)))
                    throw new InvalidOperationException("Invalid confirmed mod selection");
                var remove = new HashSet<int>(plan.Removals.Select(m => m.Id));
                var selected = plan.WantedMods.Values.Where(m => !remove.Contains(m.Id)).ToList();
                // Validate even unchanged/unconfirmed selected manifests before any removal.
                var retained = Checksums(selected);
                var expected = Checksums(selected.Where(m => confirmed.Contains(m.Id)));
                var transaction = new SyncFiles(_gameRoot, plan.State.Mods);
                transaction.Resolve("BepInEx/config/Convoy/state.json");
                foreach (var path in retained.Keys) transaction.Resolve(path);
                if (confirmed.Count > 0 && StageVerifiedZip(DownloadMods(plan.ServerUrl, confirmedModIds, false, progress), staging, expected, progress) == null)
                    return SyncResult.Failed;
                var removed = plan.State.Mods.Where(m => remove.Contains(m.Id) || confirmed.Contains(m.Id))
                    .SelectMany(m => m.Files).Where(f => !plan.Exclusions.Contains(f.Path));
                transaction.Preflight(expected, retained.Keys, removed);
                var next = new List<ModState>();
                foreach (var mod in selected)
                {
                    if (confirmed.Contains(mod.Id)) next.Add(new ModState { Id = mod.Id, Version = mod.Version,
                        Files = mod.FileChecksums.Concat(mod.BundleChecksums).Select(f => transaction.Record(f.Key, f.Value)).ToList() });
                    else if (plan.State.Mods.FirstOrDefault(m => m.Id == mod.Id) is ModState existing)
                        next.Add(new ModState { Id = existing.Id, Version = existing.Version,
                            Files = existing.Files.Select(f => expected.TryGetValue(f.Path, out var hash) ? transaction.Record(f.Path, hash) : f).ToList() });
                }
                var catalogIds = new HashSet<int>(plan.Catalog.Groups.SelectMany(g => g.Mods).Select(m => m.Id));
                var skipped = new HashSet<int>(skippedModIds);
                skipped.IntersectWith(catalogIds);
                var newState = new ConvoyState { ServerUrl = plan.ServerUrl, Mods = next,
                    EnabledGroups = plan.State.EnabledGroups, OptionalGroups = plan.State.OptionalGroups, SkippedMods = skipped };
                progress?.SetPhase("Installing verified files...");
                transaction.Apply(staging, newState.Save);
                var result = confirmed.Count > 0 || remove.Count > 0 ? SyncResult.RestartRequired : SyncResult.UpToDate;
                SendReport(result == SyncResult.RestartRequired ? "updated" : "up_to_date", newState.Mods, null);
                return result;
            }
            catch (Exception ex)
            {
                _log.LogError("Sync aborted without committing state: " + ex.Message);
                SendReport("failed", null, ex.Message);
                return SyncResult.Failed;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        public Catalog FetchCatalogPublic()
        {
            var serverUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/');
            return FetchCatalog(serverUrl);
        }

        private const int CatalogTimeoutMs = 15_000;
        private const int DownloadConnectTimeoutMs = 30_000;
        private const int MinBitrateBytes = 10 * 1024; // 10 KB/s
        private const int StallWindowSeconds = 15;

        private Catalog FetchCatalog(string serverUrl)
        {
            var request = WebRequest.CreateHttp($"{serverUrl}/quma/convoy/catalog");
            request.Timeout = CatalogTimeoutMs;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()!))
            {
                var json = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<Catalog>(json)
                       ?? throw new InvalidOperationException("Server returned invalid catalog JSON");
            }
        }

        private byte[] DownloadMods(string serverUrl, List<int> modIds, bool bundlesOnly = false, SyncProgress? progress = null)
        {
            var request = WebRequest.CreateHttp($"{serverUrl}/quma/convoy/download");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = DownloadConnectTimeoutMs;
            request.ReadWriteTimeout = DownloadConnectTimeoutMs;

            var requestBody = bundlesOnly
                ? new { mods = modIds, bundles_only = true }
                : (object)new { mods = modIds };
            var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(requestBody));
            request.ContentLength = body.Length;

            using (var s = request.GetRequestStream())
                s.Write(body, 0, body.Length);

            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream()!)
            {
                var totalBytes = response.ContentLength;
                if (progress != null && totalBytes > 0)
                    progress.SetDownloadProgress(0, totalBytes);

                var capacity = totalBytes > 0 && totalBytes <= int.MaxValue ? (int)totalBytes : 4096;
                using (var ms = new MemoryStream(capacity))
                {
                    var buffer = new byte[8192];
                    long received = 0;
                    var samples = new List<(double time, long bytes)>();
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    double lastSampleTime = 0;

                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                        received += bytesRead;
                        progress?.SetDownloadProgress(received, totalBytes);

                        var elapsed = stopwatch.Elapsed.TotalSeconds;
                        if (elapsed - lastSampleTime >= 1.0)
                        {
                            samples.Add((elapsed, received));
                            lastSampleTime = elapsed;

                            while (samples.Count > 1 && elapsed - samples[0].time > StallWindowSeconds)
                                samples.RemoveAt(0);

                            if (samples.Count >= 2)
                            {
                                var window = samples[samples.Count - 1].time - samples[0].time;
                                if (window >= StallWindowSeconds)
                                {
                                    var bytesInWindow = samples[samples.Count - 1].bytes - samples[0].bytes;
                                    var bitrate = bytesInWindow / window;
                                    if (bitrate < MinBitrateBytes)
                                        throw new TimeoutException(
                                            $"Download stalled: {bitrate / 1024:F1} KB/s avg over {StallWindowSeconds}s (minimum {MinBitrateBytes / 1024} KB/s)");
                                }
                            }
                        }
                    }

                    return ms.ToArray();
                }
            }
        }

        private HashSet<string>? StageVerifiedZip(byte[] zipBytes, string targetDir,
            Dictionary<string, string> expected, SyncProgress? progress)
        {
            progress?.SetPhase("Extracting...");
            var normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
            var extracted = new HashSet<string>(StringComparer.Ordinal);
            if (expected.Keys.Any(path => !SyncFiles.Allowed(path))) return null;

            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) return null;
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) || !expected.Keys.Any(p => p.StartsWith(entry.FullName, StringComparison.Ordinal))) return null;
                        continue;
                    }
                    if (!expected.ContainsKey(entry.FullName))
                    {
                        _log.LogError("Unadvertised ZIP path: " + entry.FullName);
                        return null;
                    }
                    if (!extracted.Add(entry.FullName))
                    {
                        _log.LogError($"ZIP contained duplicate catalog path: {entry.FullName}");
                        return null;
                    }

                    var destPath = Path.GetFullPath(Path.Combine(targetDir,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destPath.StartsWith(normalizedTarget, StringComparison.Ordinal))
                        throw new InvalidOperationException($"ZIP path escapes target: {entry.FullName}");

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using (var src = entry.Open())
                    using (var dst = File.Create(destPath))
                        src.CopyTo(dst);
                }
            }

            progress?.SetPhase("Verifying hashes...");
            return VerifyHashes(extracted, expected, targetDir) ? extracted : null;
        }

        private bool VerifyHashes(HashSet<string> extractedPaths, Dictionary<string, string> expected, string baseDir)
        {
            foreach (var item in expected)
            {
                var relPath = item.Key;
                if (!extractedPaths.Contains(relPath))
                {
                    _log.LogError($"ZIP is missing catalog path: {relPath}");
                    return false;
                }
                var fullPath = Path.Combine(baseDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                var hasher = new XxHash3();
                using (var stream = File.OpenRead(fullPath))
                    hasher.Append(stream);
                var got = hasher.GetCurrentHashAsUInt64().ToString("x16");
                if (!string.Equals(got, item.Value, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError($"Hash mismatch: {relPath} (expected {item.Value}, got {got})");
                    return false;
                }
            }
            return true;
        }

        private string ResolvePath(string relativePath) => new SyncFiles(_gameRoot, Array.Empty<ModState>()).Resolve(relativePath);

        internal void SendReport(string result, List<ModState>? mods, string? error)
        {
            try
            {
                var report = new Dictionary<string, object>
                {
                    ["aid"] = SPT.Common.Http.RequestHandler.SessionId,
                    ["result"] = result,
                    ["client_version"] = VersionInfo.Version,
                };

                if (mods != null)
                    report["mods"] = mods.Select(m => new { id = m.Id, version = m.Version }).ToArray();

                if (error != null)
                    report["error"] = error;

                var json = JsonConvert.SerializeObject(report);
                var serverUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/');
                var request = WebRequest.CreateHttp($"{serverUrl}/quma/convoy/report");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 10_000;
                var body = Encoding.UTF8.GetBytes(json);
                request.ContentLength = body.Length;
                using (var s = request.GetRequestStream())
                    s.Write(body, 0, body.Length);
                using (request.GetResponse()) { }
                _log.LogDebug($"Sync report sent: {result}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to send sync report: {ex.Message}");
            }
        }
    }
}
