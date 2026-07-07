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

        public SyncResult Run()
        {
            try
            {
                return RunSync();
            }
            catch (Exception ex)
            {
                _log.LogError($"Convoy sync failed: {ex.Message}");
                _log.LogDebug(ex);
                return SyncResult.Failed;
            }
        }

        private SyncResult RunSync()
        {
            CleanPendingDeletes();

            var state = ConvoyState.Load();
            var serverUrl = SPT.Common.Http.RequestHandler.Host.TrimEnd('/');

            var (catalog, newEtag) = FetchCatalog(serverUrl, state.LastCatalogEtag);
            if (catalog == null)
            {
                _log.LogInfo("Catalog unchanged, skipping sync");
                return SyncResult.UpToDate;
            }

            _config.RegisterOptionalGroups(catalog.Groups);

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

            // Clean up files from old versions before downloading new ones
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
                var zipBytes = DownloadMods(serverUrl, toDownload);
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

                    if (!VerifyHashes(extractedFiles, expectedChecksums, stagingDir))
                    {
                        _log.LogError("Hash verification failed, aborting sync");
                        return SyncResult.Failed;
                    }

                    MoveToGameRoot(extractedFiles, stagingDir);
                }
                finally
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, true);
                }
            }

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
            return SyncResult.RestartRequired;
        }

        private const int CatalogTimeoutMs = 15_000;
        private const int DownloadTimeoutMs = 120_000;

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

        private byte[] DownloadMods(string serverUrl, List<int> modIds)
        {
            var request = WebRequest.CreateHttp($"{serverUrl}/quma/convoy/download");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = DownloadTimeoutMs;

            var body = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(new { mods = modIds }));
            request.ContentLength = body.Length;

            using (var s = request.GetRequestStream())
                s.Write(body, 0, body.Length);

            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream()!)
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
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
    }
}
