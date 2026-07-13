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

        public SyncResult Run(SyncProgress? progress = null)
        {
            Catalog? catalog = null;
            string? serverUrl = null;
            try
            {
                var result = RunSync(progress, out catalog, out serverUrl);
                progress?.Complete(result, null, catalog?.SptVersion, catalog?.QuartermasterVersion, serverUrl);
                return result;
            }
            catch (Exception ex)
            {
                _log.LogError($"Convoy sync failed: {ex.Message}");
                _log.LogDebug(ex);
                SendReport("failed", null, ex.Message);
                progress?.Complete(SyncResult.Failed, ex.Message, catalog?.SptVersion, catalog?.QuartermasterVersion, serverUrl);
                return SyncResult.Failed;
            }
        }

        private SyncResult RunSync(SyncProgress? progress, out Catalog? catalog, out string? serverUrl)
        {
            catalog = null;
            serverUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/');

            progress?.SetPhase("Cleaning up...");
            CleanPendingDeletes();

            var state = ConvoyState.Load();

            progress?.SetPhase("Fetching catalog...");
            var (fetchedCatalog, newEtag) = FetchCatalog(serverUrl, state.LastCatalogEtag);
            catalog = fetchedCatalog;
            if (catalog == null)
            {
                _log.LogInfo("Catalog unchanged, skipping sync");
                SendReport("up_to_date", state.Mods, null);
                return SyncResult.UpToDate;
            }

            state.OptionalGroups = _config.RegisterOptionalGroups(catalog.Groups);

            var wantedMods = new Dictionary<int, CatalogMod>();
            foreach (var group in catalog.Groups)
            {
                if (group.Tier != "required" && !_config.IsGroupEnabled(group.Slug))
                    continue;
                foreach (var mod in group.Mods)
                    wantedMods[mod.Id] = mod;
            }

            var currentMods = state.Mods.ToDictionary(m => m.Id);

            var toDownload = new List<int>();
            foreach (var kvp in wantedMods)
            {
                if (!currentMods.TryGetValue(kvp.Key, out var current) || current.Version != kvp.Value.Version)
                    toDownload.Add(kvp.Key);
            }

            var toRemove = currentMods.Keys.Where(id => !wantedMods.ContainsKey(id)).ToList();

            if (toDownload.Count == 0 && toRemove.Count == 0)
            {
                state.LastCatalogEtag = newEtag;
                state.Save();
                _log.LogInfo("All mods up to date");
                SendReport("up_to_date", state.Mods, null);
                return SyncResult.UpToDate;
            }

            var exclusions = new HashSet<string>(catalog.Exclusions);

            if (toDownload.Count > 0)
            {
                var names = toDownload.Select(id => wantedMods[id].Name);
                _log.LogInfo($"Installing/updating: {string.Join(", ", names)}");
            }
            if (toRemove.Count > 0)
            {
                var names = toRemove
                    .Where(id => currentMods.ContainsKey(id))
                    .Select(id => currentMods[id].Id.ToString());
                _log.LogInfo($"Removing: {string.Join(", ", names)}");
            }

            foreach (var modId in toDownload)
            {
                if (!currentMods.TryGetValue(modId, out var oldMod)) continue;
                var newFiles = new HashSet<string>(wantedMods[modId].FileChecksums.Keys);
                foreach (var file in oldMod.Files.Where(f => !newFiles.Contains(f.Path)))
                {
                    if (exclusions.Contains(file.Path)) continue;
                    var fullPath = ResolvePath(file.Path);
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
            }

            if (toDownload.Count > 0)
            {
                progress?.SetPhase($"Downloading {toDownload.Count} mod{(toDownload.Count == 1 ? "" : "s")}...");
                var zipBytes = DownloadMods(serverUrl, toDownload, progress);

                progress?.SetPhase("Extracting...");
                var stagingDir = Path.Combine(Path.GetTempPath(), $"convoy-{Guid.NewGuid():N}");
                try
                {
                    var extractedFiles = ExtractZip(zipBytes, stagingDir);

                    var expectedChecksums = wantedMods.Values
                        .Where(m => toDownload.Contains(m.Id))
                        .SelectMany(m => m.FileChecksums)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);

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

            if (toRemove.Count > 0)
            {
                progress?.SetPhase("Removing old mods...");
                foreach (var modId in toRemove)
                {
                    if (!currentMods.TryGetValue(modId, out var mod)) continue;
                    foreach (var file in mod.Files)
                    {
                        if (exclusions.Contains(file.Path)) continue;
                        var fullPath = ResolvePath(file.Path);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            _log.LogInfo($"Removed: {file.Path}");
                        }
                    }
                    CleanEmptyDirs(mod.Files
                        .Where(f => !exclusions.Contains(f.Path))
                        .Select(f => ResolvePath(f.Path)));
                }
            }

            state.ServerUrl = serverUrl;
            state.LastCatalogEtag = newEtag;
            state.Mods = wantedMods.Values.Select(m => new ModState
            {
                Id = m.Id,
                Version = m.Version,
                Files = m.FileChecksums.Select(kv => new ModFileState
                {
                    Path = kv.Key,
                    Hash = kv.Value
                }).ToList()
            }).ToList();
            state.Save();

            _log.LogWarning("Convoy sync complete — restart required for changes to take effect");
            SendReport("updated", state.Mods, null);
            return SyncResult.RestartRequired;
        }

        private const int CatalogTimeoutMs = 15_000;
        private const int DownloadConnectTimeoutMs = 30_000;
        private const int MinBitrateBytes = 10 * 1024; // 10 KB/s
        private const int StallWindowSeconds = 15;

        private (Catalog?, string?) FetchCatalog(string serverUrl, string? lastEtag)
        {
            var request = WebRequest.CreateHttp($"{serverUrl}/quma/convoy/catalog");
            request.Timeout = CatalogTimeoutMs;
            if (lastEtag != null)
                request.Headers["If-None-Match"] = lastEtag;

            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
                when (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotModified)
            {
                return (null, lastEtag);
            }

            using (response)
            using (var reader = new StreamReader(response.GetResponseStream()!))
            {
                var json = reader.ReadToEnd();
                var catalog = JsonConvert.DeserializeObject<Catalog>(json)
                              ?? throw new InvalidOperationException("Server returned invalid catalog JSON");
                return (catalog, response.Headers["ETag"]);
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

        private void SendReport(string result, List<ModState>? mods, string? error)
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
                SPT.Common.Http.RequestHandler.PostJson("/quma/convoy/report", json);
                _log.LogDebug($"Sync report sent: {result}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to send sync report: {ex.Message}");
            }
        }
    }
}
