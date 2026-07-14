using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
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
        public Dictionary<int, CatalogMod> SkippableMods = new Dictionary<int, CatalogMod>();
        public string ServerUrl = "";
        public List<PlannedMod> Installs = new List<PlannedMod>();
        public List<PlannedMod> Updates = new List<PlannedMod>();
        public List<PlannedMod> Removals = new List<PlannedMod>();
        public List<PlannedMod> Skipped = new List<PlannedMod>();
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
            CleanPendingDeletes();

            var state = ConvoyState.Load();
            var serverUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/');

            progress?.SetPhase("Fetching catalog...");
            var catalog = FetchCatalog(serverUrl);

            state.OptionalGroups = _config.RegisterOptionalGroups(catalog.Groups);

            var catalogModGroups = new Dictionary<int, (CatalogMod mod, CatalogGroup group)>();
            foreach (var group in catalog.Groups)
                foreach (var mod in group.Mods)
                    catalogModGroups[mod.Id] = (mod, group);

            var wantedMods = new Dictionary<int, CatalogMod>();
            var skippedWanted = new Dictionary<int, CatalogMod>();
            foreach (var group in catalog.Groups)
            {
                bool isRequired = group.Tier == "required";
                if (!isRequired && !state.EnabledGroups.Contains(group.Slug))
                    continue;

                foreach (var mod in group.Mods)
                {
                    if (!isRequired && state.SkippedMods.Contains(mod.Id))
                        skippedWanted[mod.Id] = mod;
                    else
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
                else if (current.Version != mod.Version)
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
            allKeptIds.UnionWith(skippedWanted.Keys);
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

            var skippedPlan = new List<PlannedMod>();
            foreach (var kvp in skippedWanted)
            {
                var (mod, group) = catalogModGroups[kvp.Key];
                if (!currentMods.TryGetValue(kvp.Key, out var current) || current.Version != mod.Version)
                {
                    skippedPlan.Add(new PlannedMod
                    {
                        Id = mod.Id, Name = mod.Name, Version = mod.Version,
                        OldVersion = current?.Version,
                        GroupName = group.Name, GroupSlug = group.Slug,
                        IsRequired = false
                    });
                }
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
                SkippableMods = skippedWanted,
                ServerUrl = serverUrl,
                Installs = installs,
                Updates = updates,
                Removals = removals,
                Skipped = skippedPlan,
                Exclusions = new HashSet<string>(catalog.Exclusions)
            };
        }

        public SyncResult ExecuteSync(SyncPlan plan, List<int> confirmedModIds, HashSet<int> skippedModIds, SyncProgress? progress = null)
        {
            var state = plan.State;
            var currentMods = state.Mods.ToDictionary(m => m.Id);
            var confirmedSet = new HashSet<int>(confirmedModIds);

            var allEligible = new Dictionary<int, CatalogMod>(plan.WantedMods);
            foreach (var kv in plan.SkippableMods)
                allEligible[kv.Key] = kv.Value;

            foreach (var modId in confirmedModIds)
            {
                if (!currentMods.TryGetValue(modId, out var oldMod)) continue;
                if (!allEligible.TryGetValue(modId, out var newMod)) continue;
                var newFiles = new HashSet<string>(newMod.FileChecksums.Keys);
                foreach (var file in oldMod.Files.Where(f => !newFiles.Contains(f.Path)))
                {
                    if (plan.Exclusions.Contains(file.Path)) continue;
                    var fullPath = ResolvePath(file.Path);
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
            }

            if (confirmedModIds.Count > 0)
            {
                progress?.SetPhase($"Downloading {confirmedModIds.Count} mod{(confirmedModIds.Count == 1 ? "" : "s")}...");
                var zipBytes = DownloadMods(plan.ServerUrl, confirmedModIds, progress);

                progress?.SetPhase("Extracting...");
                var stagingDir = Path.Combine(Path.GetTempPath(), $"convoy-{Guid.NewGuid():N}");
                try
                {
                    var extractedFiles = ExtractZip(zipBytes, stagingDir);

                    var expectedChecksums = new Dictionary<string, string>();
                    foreach (var id in confirmedModIds)
                    {
                        if (!allEligible.TryGetValue(id, out var m)) continue;
                        foreach (var kv in m.FileChecksums)
                            expectedChecksums[kv.Key] = kv.Value;
                    }

                    var unverified = extractedFiles.Where(p => !expectedChecksums.ContainsKey(p)).ToList();
                    if (unverified.Count > 0)
                        _log.LogWarning($"ZIP contained {unverified.Count} file(s) not in catalog checksums");

                    progress?.SetPhase("Verifying hashes...");
                    if (!VerifyHashes(extractedFiles, expectedChecksums, stagingDir))
                    {
                        _log.LogError("Hash verification failed, aborting sync");
                        SendReport("failed", null, "hash verification failed");
                        return SyncResult.Failed;
                    }

                    progress?.SetPhase("Installing files...");
                    MoveToGameRoot(extractedFiles, stagingDir);
                }
                finally
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, true);
                }
            }

            var removeIds = new HashSet<int>(plan.Removals.Select(r => r.Id));
            if (removeIds.Count > 0)
            {
                progress?.SetPhase("Removing old mods...");
                foreach (var modId in removeIds)
                {
                    if (!currentMods.TryGetValue(modId, out var mod)) continue;
                    foreach (var file in mod.Files)
                    {
                        if (plan.Exclusions.Contains(file.Path)) continue;
                        var fullPath = ResolvePath(file.Path);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            _log.LogInfo($"Removed: {file.Path}");
                        }
                    }
                    CleanEmptyDirs(mod.Files
                        .Where(f => !plan.Exclusions.Contains(f.Path))
                        .Select(f => ResolvePath(f.Path)));
                }
            }

            var newMods = new List<ModState>();
            foreach (var kv in allEligible)
            {
                if (removeIds.Contains(kv.Key)) continue;

                if (skippedModIds.Contains(kv.Key))
                {
                    if (currentMods.TryGetValue(kv.Key, out var existing))
                        newMods.Add(existing);
                    continue;
                }

                newMods.Add(new ModState
                {
                    Id = kv.Value.Id,
                    Version = kv.Value.Version,
                    Files = kv.Value.FileChecksums.Select(f => new ModFileState { Path = f.Key, Hash = f.Value }).ToList()
                });
            }

            var catalogIds = new HashSet<int>(
                plan.Catalog.Groups.SelectMany(g => g.Mods).Select(m => m.Id));
            skippedModIds.IntersectWith(catalogIds);

            state.ServerUrl = plan.ServerUrl;
            state.Mods = newMods;
            state.SkippedMods = skippedModIds;
            state.Save();

            if (confirmedModIds.Count > 0 || removeIds.Count > 0)
            {
                _log.LogWarning("Convoy sync complete — restart required for changes to take effect");
                SendReport("updated", state.Mods, null);
                return SyncResult.RestartRequired;
            }

            SendReport("up_to_date", state.Mods, null);
            return SyncResult.UpToDate;
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

        private byte[] DownloadMods(string serverUrl, List<int> modIds, SyncProgress? progress = null)
        {
            var request = WebRequest.CreateHttp($"{serverUrl}/quma/convoy/download");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = DownloadConnectTimeoutMs;
            request.ReadWriteTimeout = DownloadConnectTimeoutMs;

            var body = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(new { mods = modIds }));
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

        private List<string> ExtractZip(byte[] zipBytes, string targetDir)
        {
            var normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
            var extracted = new List<string>();

            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var destPath = Path.GetFullPath(Path.Combine(targetDir,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destPath.StartsWith(normalizedTarget))
                        throw new InvalidOperationException($"ZIP path escapes target: {entry.FullName}");

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using (var src = entry.Open())
                    using (var dst = File.Create(destPath))
                        src.CopyTo(dst);

                    extracted.Add(entry.FullName);
                }
            }

            return extracted;
        }

        private bool VerifyHashes(List<string> extractedPaths, Dictionary<string, string> expected, string baseDir)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var relPath in extractedPaths)
                {
                    if (!expected.TryGetValue(relPath, out var want)) continue;
                    var fullPath = Path.Combine(baseDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                    using (var stream = File.OpenRead(fullPath))
                    {
                        var got = BitConverter.ToString(sha.ComputeHash(stream))
                            .Replace("-", "").ToLowerInvariant();
                        if (got != want.ToLowerInvariant())
                        {
                            _log.LogError($"Hash mismatch: {relPath} (expected {want}, got {got})");
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        private void MoveToGameRoot(List<string> files, string stagingDir)
        {
            foreach (var relPath in files)
            {
                var src = Path.Combine(stagingDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                var dst = ResolvePath(relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                if (File.Exists(dst))
                {
                    // ponytail: loaded DLLs can be renamed but not overwritten on Windows
                    var pending = dst + ".convoy-old";
                    try { File.Delete(pending); } catch { }
                    File.Move(dst, pending);
                }

                File.Copy(src, dst);
            }
        }

        private void CleanPendingDeletes()
        {
            try
            {
                foreach (var old in Directory.EnumerateFiles(_gameRoot, "*.convoy-old", SearchOption.AllDirectories))
                {
                    File.Delete(old);
                    _log.LogDebug($"Cleaned up {old}");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to clean pending deletes: {ex.Message}");
            }
        }

        private string ResolvePath(string relativePath) =>
            Path.Combine(_gameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private void CleanEmptyDirs(IEnumerable<string> deletedPaths)
        {
            var normalizedRoot = Path.GetFullPath(_gameRoot).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var filePath in deletedPaths)
            {
                var dir = Path.GetDirectoryName(filePath);
                while (dir != null && dir.Length > normalizedRoot.Length)
                {
                    try
                    {
                        if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
                            break;
                        Directory.Delete(dir);
                        dir = Path.GetDirectoryName(dir);
                    }
                    catch { break; }
                }
            }
        }

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
