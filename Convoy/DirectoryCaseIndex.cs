using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Convoy
{
    // Read-only scan snapshot only. Never retained across live filesystem mutations.
    internal sealed class DirectoryCaseIndex
    {
        private readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string?>>> _directories =
            new ConcurrentDictionary<string, Lazy<Dictionary<string, string?>>>(StringComparer.Ordinal);

        internal bool HasAlias(string directory, string component, CancellationToken cancel)
        {
            var names = _directories.GetOrAdd(directory, path => new Lazy<Dictionary<string, string?>>(
                () => Read(path, cancel), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            return names.TryGetValue(component, out var spelling) && spelling != component;
        }

        private static Dictionary<string, string?> Read(string directory, CancellationToken cancel)
        {
            var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(directory)) return names;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancel.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                // Null also rejects an exact spelling when a second case alias exists.
                names[name] = names.ContainsKey(name) ? null : name;
            }
            return names;
        }
    }
}
