using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Convoy
{
    // Preflight the whole operation before granting any filesystem mutation authority.
    internal sealed class SyncFiles
    {
        private readonly string _root;
        private readonly Dictionary<string, ModFileState> _old = new Dictionary<string, ModFileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _ownership = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, DeliveryAsset?> _observed = new Dictionary<string, DeliveryAsset?>();
        private readonly HashSet<string> _verified = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _writes = new List<string>();
        private readonly Dictionary<string, string> _recovery = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _verifiedRecovery = new List<string>();
        private const string RecoveryDirectory = "BepInEx/config/Convoy/recovery";
        private static readonly int LocalReadWorkers = Math.Max(1, Environment.ProcessorCount - 2);
        private readonly List<string> _deletes = new List<string>();

        internal static bool Allowed(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOfAny(new[] { '\\', ':', '\0', '<', '>', '"', '|', '?', '*' }) >= 0) return false;
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.Any(char.IsControl)) return false;
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (DeviceNames.Contains(stem)) return false;
            }
            const string managed = "EscapeFromTarkov_Data/Managed/";
            return path.StartsWith("BepInEx/", StringComparison.Ordinal)
                || (path.StartsWith(managed, StringComparison.Ordinal) && path.Substring(managed.Length).IndexOf('/') < 0 && path.EndsWith(".dll", StringComparison.Ordinal) && path.Length > managed.Length + 4)
                || Bundle(path);
        }

        internal static bool Bundle(string path) => path.StartsWith("SPT/user/cache/bundles/", StringComparison.Ordinal)
            || path.StartsWith("SPT_Runtime/user/cache/bundles/", StringComparison.Ordinal);

        internal static bool Private(string path) => path.Equals("BepInEx/config/Convoy", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("BepInEx/config/Convoy/", StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> DeviceNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static bool Owns(ModFileState file) => file.Owned ?? file.Path.StartsWith("BepInEx/", StringComparison.Ordinal);
        internal static bool Configuration(string path) => path.StartsWith("BepInEx/config/", StringComparison.Ordinal);

        internal SyncFiles(string root, IEnumerable<ModState> current)
        {
            _root = Path.GetFullPath(root);
            foreach (var file in current.SelectMany(m => m.Files))
            {
                if (Private(file.Path)) throw new InvalidOperationException("Private path in Convoy install record: " + file.Path);
                Resolve(file.Path);
                if (_old.TryGetValue(file.Path, out var prior))
                {
                    if (prior.Path != file.Path || prior.Hash != file.Hash || Owns(prior) != Owns(file))
                        throw new InvalidOperationException("Conflicting sync ownership: " + file.Path);
                }
                else _old.Add(file.Path, file);
            }
        }

        internal string Resolve(string path)
        {
            if (!Allowed(path)) throw new InvalidOperationException("Unsupported sync path: " + path);
            return ResolvePath(path);
        }

        // Parent paths are obtained only from fully validated file paths.
        private string ResolvePath(string path)
        {
            var cursor = _root;
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked game root");
            foreach (var part in path.Split('/'))
            {
                // Case aliases must also be rejected on case-sensitive hosts used for testing.
                if (Directory.Exists(cursor))
                    foreach (var name in Directory.EnumerateFileSystemEntries(cursor))
                        if (string.Equals(Path.GetFileName(name), part, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(name) != part)
                            throw new InvalidOperationException("Case alias: " + path);
                cursor = Path.Combine(cursor, part);
                if (File.Exists(cursor) && cursor != Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)))
                    throw new InvalidOperationException("File at sync directory path: " + path);
                try
                {
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Linked sync path: " + path);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return cursor;
        }

        internal Action? AdvanceEpoch { private get; set; }
        internal SyncProgress? Progress { private get; set; }
        internal CancellationToken Cancel { private get; set; }

        internal void Preflight(Dictionary<string, DeliveryAsset> incoming, IEnumerable<ModFileState> retained,
            IEnumerable<ModFileState> removed) => PreflightWithConsent(incoming, retained, removed, null, false);

        internal void PreflightWithConsent(Dictionary<string, DeliveryAsset> incoming, IEnumerable<ModFileState> retained,
            IEnumerable<ModFileState> removed, Func<DeliveryAsset, DeliveryAsset, bool>? approved, bool repair)
        {
            Progress?.SetPhase("Checking installed files");
            Progress?.Expect(incoming.Count);
            var kept = retained.ToLookup(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var keptDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in kept)
                foreach (var parent in Parents(f.Key))
                    keptDirectories[parent] = parent;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var reads = new List<(string Path, bool Cached, DeliveryAsset? Prior)>();
            void AddRead(string path)
            {
                if (indices.ContainsKey(path)) return;
                indices.Add(path, reads.Count);
                reads.Add((path, _observed.TryGetValue(path, out var prior), prior));
            }
            foreach (var path in incoming.Keys)
            {
                Cancel.ThrowIfCancellationRequested();
                if (keptDirectories.ContainsKey(path) || Parents(path).Any(p => kept.Contains(p)
                    || (keptDirectories.TryGetValue(p, out var spelling) && spelling != p)))
                    throw new InvalidOperationException("Incoming path conflicts with a retained mod: " + path);
                if (!names.Add(path)) throw new InvalidOperationException("Colliding sync path: " + path);
                AddRead(path);
            }
            var incomingCount = reads.Count;
            var retiring = removed.Where(file => !kept.Contains(file.Path) && !incoming.ContainsKey(file.Path) && Owns(file)
                && !Configuration(file.Path) && !Private(file.Path)).ToArray();
            foreach (var file in retiring) AddRead(file.Path);
            var observations = ReadBatch(reads.Count, i =>
            {
                var (path, cached, prior) = reads[i];
                if (i < incomingCount) Progress?.BeforeLocalObservation?.Invoke(path);
                var actual = cached ? prior : Read(path);
                if (i < incomingCount) Progress?.Scan();
                return actual;
            });
            DeliveryAsset? Consume(string path)
            {
                var observation = observations[indices[path]];
                observation.Error?.Throw();
                return _observed[path] = observation.Value;
            }
            foreach (var pair in incoming)
            {
                _old.TryGetValue(pair.Key, out var old);
                var owned = old != null && Owns(old);
                var actual = Consume(pair.Key);
                var exists = actual != null;
                var matches = actual?.Same(pair.Value) == true;
                var oldMatches = old != null && actual != null && StoredMatches(actual, old);
                var preserveConfig = exists && Configuration(pair.Key);
                foreach (var file in kept[pair.Key])
                    if (file.Path != pair.Key || (!preserveConfig && (!StoredMatches(pair.Value, file)
                        || (exists && !matches))))
                        throw new InvalidOperationException("Incoming asset conflicts with a retained mod: " + pair.Key);
                if (exists && !matches && !preserveConfig && (!owned || !oldMatches))
                {
                    if (kept.Contains(pair.Key) || approved?.Invoke(pair.Value, actual!) != true)
                        throw new InvalidOperationException("Local file ownership conflict: " + pair.Key);
                    _recovery.Add(pair.Key, RecoveryDirectory + "/" + Guid.NewGuid().ToString("N") + ".dll");
                }
                if (repair && !exists && old != null && !owned && !Configuration(pair.Key))
                    throw new InvalidOperationException("Missing unowned file; restore it or use normal sync: " + pair.Key);
                // Existing configurations are user-controlled, even if previously tracked.
                _ownership[pair.Key] = !Configuration(pair.Key) && (!exists ? !kept[pair.Key].Any(f => !Owns(f)) : owned && oldMatches);
                if (!preserveConfig)
                {
                    _verified.Add(pair.Key);
                    if (!matches) _writes.Add(pair.Key);
                }
            }
            foreach (var file in retiring)
                if (Consume(file.Path) is DeliveryAsset actual && StoredMatches(actual, file))
                    _deletes.Add(file.Path);
        }

        private (T Value, ExceptionDispatchInfo? Error)[] ReadBatch<T>(int count, Func<int, T> read)
        {
            Cancel.ThrowIfCancellationRequested();
            var results = new (T Value, ExceptionDispatchInfo? Error)[count];
            if (count == 1) results[0].Value = read(0);
            else if (count > 1)
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = LocalReadWorkers,
                    CancellationToken = Cancel, TaskScheduler = TaskScheduler.Default }, (i, loop) =>
                {
                    try { results[i].Value = read(i); }
                    catch (Exception ex)
                    {
                        // Finish earlier observations, then surface failures in input order on the caller.
                        results[i].Error = ExceptionDispatchInfo.Capture(ex);
                        loop.Break();
                    }
                });
            Cancel.ThrowIfCancellationRequested();
            return results;
        }

        private static IEnumerable<string> Parents(string path)
        {
            for (var end = path.LastIndexOf('/'); end > 0; end = path.LastIndexOf('/', end - 1))
                yield return path.Substring(0, end);
        }

        private static bool StoredMatches(DeliveryAsset actual, ModFileState file) => actual.Xxh3_64 == file.Hash
            && (file.Sha256 == null || actual.Sha256 == file.Sha256) && (!file.Length.HasValue || actual.Length == file.Length.Value);

        internal DeliveryAsset? Observe(string path)
        {
            if (_observed.TryGetValue(path, out var stored)) return stored;
            return _observed[path] = Read(path);
        }

        private DeliveryAsset? Read(string path)
        {
            Cancel.ThrowIfCancellationRequested();
            var target = Resolve(path);
            if (Directory.Exists(target)) throw new IOException("Directory at sync file: " + path);
            return File.Exists(target) ? FileDelivery.Measure(path, target, Cancel, Progress?.LocalHashChunk) : null;
        }

        internal ModFileState Record(DeliveryAsset asset) => new ModFileState
        {
            Path = asset.Path, Hash = _verified.Contains(asset.Path) ? asset.Xxh3_64 : "",
            Owned = _ownership[asset.Path], Sha256 = _verified.Contains(asset.Path) ? asset.Sha256 : null,
            Length = _verified.Contains(asset.Path) ? (long?)asset.Length : null
        };

        internal bool RepairEligible(string path) => _verified.Contains(path);
        internal IEnumerable<string> RecoveryPaths => _recovery.Values;
        internal long RecoveryBytes => _recovery.Keys.Sum(path => checked(_observed[path]!.Length + 4096));
        internal long PendingRecoveryBytes => _verifiedRecovery.Count == _recovery.Count ? 0 : RecoveryBytes;
        internal string? RecoveryNotice => _verifiedRecovery.Count == 0 ? null : "Original plugin backups: " + string.Join("; ", _verifiedRecovery);
        internal IEnumerable<string> Writes => _writes;
        internal IEnumerable<string> Mutations => _writes.Concat(_deletes).Distinct(StringComparer.Ordinal);
        internal int RemovalCount => _deletes.Count;
        internal bool HasChanges => _writes.Count != 0 || _deletes.Count != 0;
        internal bool NeedsRestart => _writes.Concat(_deletes).Any(path => !Bundle(path) && !Configuration(path));

        private (long Length, DateTime Modified, DateTime Created)? Identity(string path)
        {
            var target = Resolve(path);
            try
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Not a regular sync file: " + path);
                var info = new FileInfo(target);
                return (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        internal void Apply(Action<string, Stream> copyStaged, Action saveState) =>
            ApplyWithPrecommit(copyStaged, saveState, () => { }, () => { }, () => { });

        internal void ApplyWithPrecommit(Action<string, Stream> copyStaged, Action saveState,
            Action requirePublication, Action requireStateAndOrigin, Action requireFinalAdmission, Action<string, string>? placePrepared = null)
        {
            var originals = _observed.ToArray();
            var rechecked = ReadBatch(originals.Length, i =>
            {
                var observation = originals[i];
                var before = Identity(observation.Key);
                var actual = Read(observation.Key);
                var after = Identity(observation.Key);
                if (!Nullable.Equals(before, after) || (actual == null ? observation.Value != null
                    : observation.Value == null || !actual.Same(observation.Value)))
                    throw new IOException("File changed during sync preflight: " + observation.Key);
                return after;
            });
            var identities = new Dictionary<string, (long Length, DateTime Modified, DateTime Created)?>();
            for (var i = 0; i < originals.Length; i++)
            {
                rechecked[i].Error?.Throw();
                identities.Add(originals[i].Key, rechecked[i].Value);
            }
            try
            {
                if (_recovery.Count != 0) requirePublication();
                foreach (var entry in _recovery)
                {
                    requireStateAndOrigin();
                    VerifyOriginal(entry.Key);
                    CopyOriginal(entry.Key, entry.Value);
                    VerifyOriginal(entry.Key);
                }
                foreach (var entry in _recovery)
                {
                    VerifyOriginal(entry.Key);
                    var before = Identity(entry.Value);
                    if (Read(entry.Value)?.Same(_observed[entry.Key]!) != true || !Nullable.Equals(before, Identity(entry.Value)))
                        throw new IOException("Recovery copy changed: " + Resolve(entry.Value));
                    identities.Add(entry.Value, before);
                }
                requirePublication();
                requireStateAndOrigin();
                foreach (var entry in identities)
                    if (!Nullable.Equals(entry.Value, Identity(entry.Key)))
                        throw new IOException("File changed during publication check: " + entry.Key);
                requireFinalAdmission();
                if (Progress != null) Progress.BeginApply(Cancel);
                else Cancel.ThrowIfCancellationRequested();
                AdvanceEpoch?.Invoke();
                ApplyLive(copyStaged, saveState, placePrepared);
            }
            catch (Exception ex) when (RecoveryNotice != null)
            {
                ex.Data["ConvoyRecovery"] = RecoveryNotice;
                throw;
            }
        }

        private void VerifyOriginal(string path)
        {
            if (Read(path)?.Same(_observed[path]!) != true)
                throw new IOException("Approved plugin original changed: " + path);
        }

        private void CopyOriginal(string path, string recovery)
        {
            var target = Resolve(recovery);
            var created = false;
            try
            {
                Cancel.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Resolve(RecoveryDirectory));
                using (var source = File.OpenRead(Resolve(path)))
                using (var output = new FileStream(Resolve(recovery), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    var remaining = _observed[path]!.Length;
                    var buffer = new byte[64 * 1024];
                    while (remaining != 0)
                    {
                        Cancel.ThrowIfCancellationRequested();
                        var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new IOException("Approved plugin shortened during backup: " + path);
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    if (source.ReadByte() != -1) throw new IOException("Approved plugin grew during backup: " + path);
                    output.Flush(true);
                }
                if (Read(recovery)?.Same(_observed[path]!) != true)
                    throw new IOException("Recovery copy did not verify: " + target);
                _verifiedRecovery.Add(path + " → " + target);
            }
            catch (Exception error)
            {
                try { if (created) File.Delete(Resolve(recovery)); }
                catch (Exception cleanup) { throw new IOException("Backup failed at " + target + ": " + error.Message
                    + "; could not remove incomplete copy: " + cleanup.Message, new AggregateException(error, cleanup)); }
                if (error is OperationCanceledException) throw;
                throw new IOException("Could not back up " + path + " to " + target + ": " + error.Message, error);
            }
        }

        private void ApplyLive(Action<string, Stream> copyStaged, Action saveState, Action<string, string>? placePrepared)
        {
            var backups = new List<(string Target, string Backup)>();
            var installed = new List<string>();
            var directories = new List<string>();
            try
            {
                foreach (var path in Mutations)
                {
                    var target = Resolve(path);
                    if (!File.Exists(target)) continue;
                    var backup = path + ".convoy-old-" + Guid.NewGuid().ToString("N");
                    File.Move(target, ResolvePath(backup)); // Allows replacing loaded plugin DLLs on Windows.
                    backups.Add((path, backup));
                }
                foreach (var path in _writes)
                {
                    var target = Resolve(path);
                    foreach (var parent in Parents(path).Reverse())
                    {
                        var directory = ResolvePath(parent);
                        if (Directory.Exists(directory)) continue;
                        Directory.CreateDirectory(directory);
                        directories.Add(parent);
                    }
                    if (placePrepared != null)
                    {
                        placePrepared(path, target);
                        installed.Add(path);
                        continue;
                    }
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    installed.Add(path); // Include a partially copied file in rollback.
                    copyStaged(path, output);
                }
                saveState();
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                foreach (var path in installed)
                    try { File.Delete(Resolve(path)); } catch (Exception recovery) { failures.Add(new IOException("Could not remove partial install " + path + ": " + recovery.Message, recovery)); }
                foreach (var pair in backups.AsEnumerable().Reverse())
                    try { File.Move(ResolvePath(pair.Backup), Resolve(pair.Target)); }
                    catch (Exception recovery) { failures.Add(new IOException("Could not restore " + pair.Target + "; rollback backup retained at " + Path.Combine(_root, pair.Backup) + ": " + recovery.Message, recovery)); }
                foreach (var path in directories.AsEnumerable().Reverse())
                    try
                    {
                        var directory = ResolvePath(path);
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    }
                    catch (Exception recovery) { failures.Add(new IOException("Could not remove new directory " + path + ": " + recovery.Message, recovery)); }
                if (failures.Count > 1) throw new AggregateException("Sync failed and rollback was incomplete", failures);
                throw;
            }
            // Loaded assemblies may hold these until process exit. Do not recursively sweep
            // arbitrary game files as a cleanup mechanism; retained backups are never owned.
            foreach (var pair in backups) try { File.Delete(ResolvePath(pair.Backup)); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidOperationException) { }
        }
    }
}
