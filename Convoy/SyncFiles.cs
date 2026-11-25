using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;

namespace Convoy
{
    // Preflight the whole operation before granting any filesystem mutation authority.
    internal sealed class SyncFiles
    {
        private readonly string _root;
        private readonly Dictionary<string, ModFileState> _old = new Dictionary<string, ModFileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _ownership = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _observed = new Dictionary<string, string?>();
        private readonly List<string> _writes = new List<string>();
        private readonly List<string> _deletes = new List<string>();

        internal static bool Allowed(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOfAny(new[] { '\\', ':', '\0', '<', '>', '"', '|', '?', '*' }) >= 0) return false;
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.Any(char.IsControl)) return false;
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) return false;
            }
            const string managed = "EscapeFromTarkov_Data/Managed/";
            return path.StartsWith("BepInEx/", StringComparison.Ordinal)
                || (path.StartsWith(managed, StringComparison.Ordinal) && path.Substring(managed.Length).IndexOf('/') < 0 && path.EndsWith(".dll", StringComparison.Ordinal) && path.Length > managed.Length + 4)
                || path.StartsWith("SPT/user/cache/bundles/", StringComparison.Ordinal)
                || path.StartsWith("SPT_Runtime/user/cache/bundles/", StringComparison.Ordinal);
        }

        internal static string Hash(string path)
        {
            var hash = new XxHash3();
            using (var stream = File.OpenRead(path)) hash.Append(stream);
            return hash.GetCurrentHashAsUInt64().ToString("x16");
        }

        private static bool Owns(ModFileState file) => file.Owned ?? file.Path.StartsWith("BepInEx/", StringComparison.Ordinal);
        private static bool Configuration(string path) => path.StartsWith("BepInEx/config/", StringComparison.Ordinal);

        internal SyncFiles(string root, IEnumerable<ModState> current)
        {
            _root = Path.GetFullPath(root);
            foreach (var file in current.SelectMany(m => m.Files))
            {
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

        internal void Preflight(Dictionary<string, string> incoming, IEnumerable<string> retained,
            IEnumerable<ModFileState> removed)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in incoming)
            {
                var dst = Resolve(pair.Key);
                if (!names.Add(pair.Key)) throw new InvalidOperationException("Colliding sync path: " + pair.Key);
                if (Directory.Exists(dst)) throw new InvalidOperationException("Directory at file path: " + pair.Key);
                _old.TryGetValue(pair.Key, out var old);
                var exists = File.Exists(dst);
                var owned = old != null && Owns(old);
                var actual = exists ? Hash(dst) : null;
                _observed[pair.Key] = actual;
                if (exists && actual != pair.Value && !Configuration(pair.Key)
                    && (!owned || actual != old!.Hash))
                    throw new InvalidOperationException("Local file ownership conflict: " + pair.Key);
                // Existing configurations are user-controlled, even if previously tracked.
                _ownership[pair.Key] = !Configuration(pair.Key) && (!exists || (owned && actual == old!.Hash));
                if (!exists || (actual != pair.Value && !Configuration(pair.Key))) _writes.Add(pair.Key);
            }
            var keep = new HashSet<string>(retained, StringComparer.OrdinalIgnoreCase);
            foreach (var file in removed)
            {
                var path = Resolve(file.Path);
                if (!keep.Contains(file.Path) && !incoming.ContainsKey(file.Path) && Owns(file)
                    && !Configuration(file.Path) && File.Exists(path) && Hash(path) == file.Hash)
                {
                    _observed[file.Path] = file.Hash;
                    _deletes.Add(file.Path);
                }
            }
        }

        internal ModFileState Record(string path, string hash) => new ModFileState
        {
            Path = path, Hash = hash, Owned = _ownership[path]
        };

        internal void Apply(string staging, Action saveState)
        {
            var backups = new List<(string Target, string Backup)>();
            var installed = new List<string>();
            try
            {
                foreach (var path in _writes.Concat(_deletes).Distinct())
                {
                    var target = Resolve(path);
                    if ((File.Exists(target) ? Hash(target) : null) != _observed[path])
                        throw new IOException("File changed during sync preflight: " + path);
                }
                foreach (var path in _writes.Concat(_deletes).Distinct())
                {
                    var target = Resolve(path);
                    if (!File.Exists(target)) continue;
                    var backup = target + ".convoy-old-" + Guid.NewGuid().ToString("N");
                    File.Move(target, backup); // Allows replacing loaded plugin DLLs on Windows.
                    backups.Add((target, backup));
                }
                foreach (var path in _writes)
                {
                    var target = Resolve(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    installed.Add(target); // Include a partially copied file in rollback.
                    File.Copy(Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar)), target, false);
                }
                saveState();
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                foreach (var path in installed)
                    try { File.Delete(path); } catch (Exception recovery) { failures.Add(recovery); }
                foreach (var pair in backups.AsEnumerable().Reverse())
                    try { File.Move(pair.Backup, pair.Target); }
                    catch (Exception recovery) { failures.Add(new IOException("Could not restore " + pair.Target + "; backup retained at " + pair.Backup, recovery)); }
                if (failures.Count > 1) throw new AggregateException("Sync failed and rollback was incomplete", failures);
                throw;
            }
            // Loaded assemblies may hold these until process exit. Do not recursively sweep
            // arbitrary game files as a cleanup mechanism; retained backups are never owned.
            foreach (var pair in backups) try { File.Delete(pair.Backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
